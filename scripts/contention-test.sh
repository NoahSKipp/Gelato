#!/usr/bin/env bash
# Database contention test on a copy of the live Jellyfin, never the live one.
# Not run in CI. Every step prints timings and counts only; never a token or
# a stream address.
#
#   LIVE=jellyfin                     name of the live container (read only)
#   LIVE_CONFIG=/home/opc/jf-stack/jellyfin-config
#   COPY_DIR=/home/opc/jf-contention  where the copy goes (about 5 GB)
#   JF_TOKEN=...                      an API key of the live server (works on the copy)
#   JF_USER_ID=...                    optional, the user to play as (default: the first user)
#
#   scripts/contention-test.sh journal            journal mode of the live DB, read from the file header
#   scripts/contention-test.sh copy               copy config and a consistent DB snapshot
#   scripts/contention-test.sh up [plugin dir]    start the copy, optionally with a Gelato build swapped in
#   scripts/contention-test.sh mode wal|delete [NoLock|Optimistic|Pessimistic]
#   scripts/contention-test.sh run <label> <warm movie id> <id> <id> <id>
#   scripts/contention-test.sh soak <minutes> <warm movie id> <id> <id> <id>
#   scripts/contention-test.sh down               remove the copy container and directory
set -euo pipefail

LIVE=${LIVE:-jellyfin}
LIVE_CONFIG=${LIVE_CONFIG:-/home/opc/jf-stack/jellyfin-config}
COPY_DIR=${COPY_DIR:-/home/opc/jf-contention}
COPY=${COPY:-jf-contention}
OUT=${OUT:-$COPY_DIR-results}

db_path() { local d; for d in "$1/data/data/jellyfin.db" "$1/data/jellyfin.db"; do [ -f "$d" ] && { echo "$d"; return; }; done; echo "no jellyfin.db under $1" >&2; exit 2; }
header_mode() { case "$(od -An -tu1 -j18 -N2 "$1" | tr -s ' ')" in " 1 1") echo delete;; " 2 2") echo wal;; *) echo unknown;; esac; }

api() { # api METHOD PATH [json body]: curl inside the copy, prints the body
  docker exec -e T="$JF_TOKEN" "$COPY" sh -c 'curl -s -X "$0" -H "Authorization: MediaBrowser Client=\"contention-test\", Device=\"contention-test\", DeviceId=\"contention-test\", Version=\"1\", Token=\"$T\"" -H "Content-Type: application/json" ${2:+-d "$2"} "http://localhost:8096$1"' "$1" "$2" "${3:-}"
}
timed() { # timed METHOD PATH [body]: prints "code seconds"
  docker exec -e T="$JF_TOKEN" "$COPY" sh -c 'curl -s -o /dev/null -w "%{http_code} %{time_total}" -X "$0" -H "Authorization: MediaBrowser Client=\"contention-test\", Device=\"contention-test\", DeviceId=\"contention-test\", Version=\"1\", Token=\"$T\"" -H "Content-Type: application/json" ${2:+-d "$2"} "http://localhost:8096$1"' "$1" "$2" "${3:-}"
}
task_id() { api GET /ScheduledTasks | python3 -c "import json,sys; print([t['Id'] for t in json.load(sys.stdin) if t['Key']=='$1'][0])"; }
task_start() { api POST "/ScheduledTasks/Running/$(task_id "$1")" >/dev/null; }
task_wait() {
  local id; id=$(task_id "$1"); sleep 2
  while [ "$(api GET "/ScheduledTasks/$id" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("State"))')" != Idle ]; do sleep 3; done
  api GET "/ScheduledTasks/$id" | python3 -c '
import json,sys
from datetime import datetime
r=json.load(sys.stdin)["LastExecutionResult"]
def p(s):
    s=s.rstrip("Z").split("+")[0]; base,_,frac=s.partition(".")
    return datetime.strptime(base,"%Y-%m-%dT%H:%M:%S").timestamp()+float("0."+(frac or "0"))
print(round(p(r["EndTimeUtc"])-p(r["StartTimeUtc"]),1), r["Status"])'
}
wait_up() { for _ in $(seq 1 120); do [ "$(timed GET /System/Info/Public 2>/dev/null | cut -d' ' -f1)" = 200 ] && return; sleep 3; done; echo "copy did not come up" >&2; exit 1; }

case "${1:-}" in
journal)
  db=$(db_path "$LIVE_CONFIG")
  echo "live journal mode (file header): $(header_mode "$db")"
  ls -la "$(dirname "$db")" | grep -E 'jellyfin\.db(-wal|-shm|-journal)?$' || true
  grep -h "LockingBehavior" "$LIVE_CONFIG/config/database.xml" "$LIVE_CONFIG/database.xml" 2>/dev/null || true
  ;;
copy)
  [ -e "$COPY_DIR" ] && { echo "$COPY_DIR exists, run down first" >&2; exit 2; }
  mkdir -p "$COPY_DIR"
  tar -C "$LIVE_CONFIG" --exclude './data/metadata' --exclude './cache' --exclude './transcodes' \
    --exclude './log' --exclude 'jellyfin.db' --exclude 'jellyfin.db-*' -cf - . | tar -C "$COPY_DIR" -xf -
  src=$(db_path "$LIVE_CONFIG"); dst="$COPY_DIR${src#"$LIVE_CONFIG"}"
  mode=$(header_mode "$src")
  # SQLite's backup API gives a consistent copy. In WAL mode it holds a read
  # snapshot only and never blocks the live writers; in delete mode its read
  # lock holds off live writes while it copies (about a minute for 3 GB).
  echo "copying the database ($mode mode)"
  start=$(date +%s)
  python3 - "$src" "$dst" <<'PY'
import sqlite3, sys
src = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
dst = sqlite3.connect(sys.argv[2])
src.backup(dst)
dst.close(); src.close()
PY
  echo "copied in $(( $(date +%s) - start )) s, $(du -sh "$COPY_DIR" | cut -f1) in total"
  ;;
up)
  image=$(docker inspect "$LIVE" --format '{{.Image}}')
  net=$(docker inspect "$LIVE" --format '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}' | awk '{print $1}')
  envs=$(docker inspect "$LIVE" --format '{{range .Config.Env}}{{println .}}{{end}}' | grep -E '^(PUID|PGID|TZ)=' | sed 's/^/-e /' | tr '\n' ' ')
  mounts=$(docker inspect "$LIVE" --format '{{range .Mounts}}{{if ne .Destination "/config"}}-v {{.Source}}:{{.Destination}}:ro {{end}}{{end}}')
  if [ -n "${2:-}" ]; then
    gel=$(ls -d "$COPY_DIR"/data/plugins/Gelato* 2>/dev/null | head -1)
    [ -n "$gel" ] || { echo "no Gelato plugin folder in the copy" >&2; exit 2; }
    cp "$2"/*.dll "$gel/"
    echo "swapped in the build from $2"
  fi
  docker rm -f "$COPY" >/dev/null 2>&1 || true
  # No published ports; media mounted read only.
  # shellcheck disable=SC2086
  docker run -d --name "$COPY" --network "$net" $envs $mounts -v "$COPY_DIR:/config" "$image" >/dev/null
  wait_up; echo "copy is up on network $net (image ${image:7:12})"
  ;;
mode)
  jm=${2:?wal or delete}; lock=${3:-NoLock}
  docker stop "$COPY" >/dev/null
  db=$(db_path "$COPY_DIR")
  python3 -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); print('journal_mode', c.execute('pragma journal_mode=' + sys.argv[2]).fetchone()[0])" "$db" "$jm"
  for x in "$COPY_DIR/config/database.xml" "$COPY_DIR/database.xml"; do
    [ -f "$x" ] && sed -i "s#<LockingBehavior>.*</LockingBehavior>#<LockingBehavior>$lock</LockingBehavior>#" "$x" && grep -h LockingBehavior "$x"
  done
  docker start "$COPY" >/dev/null; wait_up
  ;;
run|soak)
  if [ "$1" = run ]; then label=$2; minutes=0; else minutes=$2; label="soak-${minutes}m"; fi
  warm=$3; shift 3
  dir="$OUT/$label"; mkdir -p "$dir"
  user=${JF_USER_ID:-$(api GET /Users | python3 -c 'import json,sys; print(json.load(sys.stdin)[0]["Id"])')}
  info_body="{\"UserId\":\"$user\"}"
  api POST "/gelato/prefetch/$warm" >/dev/null; sleep 20
  for _ in 1 2 3; do timed POST "/Items/$warm/PlaybackInfo" "$info_body" >/dev/null; done
  timed POST "/Items/$warm/PlaybackInfo" "$info_body" > "$dir/info_idle.txt"
  since=$(date -u +%Y-%m-%dT%H:%M:%S); start=$(date +%s)
  ( while :; do
      t=$(( $(date +%s) - start ))
      echo "$t $(timed POST "/Items/$warm/PlaybackInfo" "$info_body") $(timed GET "/Users/$user/Items/$warm")" >> "$dir/latency.txt"
      sleep 2
    done ) & probe=$!
  round=0
  while :; do
    round=$((round + 1))
    docker exec "$COPY" sh -c 'rm -f /config/cache/xmltv/*.xml'
    task_start RefreshGuide
    for id in "$@"; do api POST "/gelato/prefetch/$id" >/dev/null; done
    task_start SyncSeriesTrees
    echo "round $round guide $(task_wait RefreshGuide) series $(task_wait SyncSeriesTrees)" | tee -a "$dir/rounds.txt"
    [ $(( $(date +%s) - start )) -ge $(( minutes * 60 )) ] && break
  done
  sleep 10; kill $probe 2>/dev/null; wait $probe 2>/dev/null || true
  docker logs --since "$since" "$COPY" > "$dir/server.log" 2>&1
  {
    echo "label=$label rounds=$round wall=$(( $(date +%s) - start ))s journal=$(header_mode "$(db_path "$COPY_DIR")")"
    cat "$dir/rounds.txt"
    echo "database_is_locked=$(grep -c 'database is locked' "$dir/server.log")"
    echo "save_errors=$(grep -c 'Error trying to save changes' "$dir/server.log")"
    echo "syncstreams: $(grep -o 'SyncStreams finished.*' "$dir/server.log" | grep -o 'duration=[0-9.]*s\|written=[0-9]*' | paste -sd' ' -)"
    python3 - "$dir/latency.txt" "$dir/info_idle.txt" <<'PY'
import sys
rows = [l.split() for l in open(sys.argv[1]) if l.strip()]
def stats(v):
    v = sorted(v); n = len(v)
    return f"n={n} p50={v[n//2]:.2f}s p95={v[min(n-1, int(n*.95))]:.2f}s max={v[-1]:.2f}s" if n else "n=0"
print("playbackinfo_idle", open(sys.argv[2]).read().strip())
print("playbackinfo_under_load", stats([float(r[2]) for r in rows]), "non200", sum(r[1] != "200" for r in rows))
print("item_under_load", stats([float(r[4]) for r in rows]), "non200", sum(r[3] != "200" for r in rows))
PY
  } | tee "$dir/summary.txt"
  ;;
down)
  docker rm -f "$COPY" >/dev/null 2>&1 || true
  case "$COPY_DIR" in "$LIVE_CONFIG"|/|"") echo "refusing to remove $COPY_DIR" >&2; exit 2;; esac
  rm -rf "$COPY_DIR"; echo "removed the copy (results stay in $OUT)"
  ;;
*) sed -n 2,20p "$0"; exit 2;;
esac
