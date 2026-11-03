#!/usr/bin/env bash
# Checks that Play right after a warm-up gets a playable stream, against a
# real server. Not run in CI.
#
#   GELATO_SERVER=https://jellyfin.example  GELATO_TOKEN=...  GELATO_USER_ID=...
#   scripts/verify-probe-race.sh <movie id for 0 s> <for 2 s> <for 4 s> <for 6 s> <for 9 s>
#
# Every id must be a movie that has never been opened, since a run warms it.
# Prints timings only; never the token or any stream address.
set -euo pipefail

: "${GELATO_SERVER:?set GELATO_SERVER}"
: "${GELATO_TOKEN:?set GELATO_TOKEN}"
: "${GELATO_USER_ID:?set GELATO_USER_ID}"
command -v jq >/dev/null || { echo "jq is needed" >&2; exit 2; }

waits=(0 2 4 6 9)
if [ "$#" -ne "${#waits[@]}" ]; then
  echo "give one never-opened movie id per wait (${waits[*]} s)" >&2
  exit 2
fi

auth=(-H "X-Emby-Token: ${GELATO_TOKEN}")
server="${GELATO_SERVER%/}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
failed=0

printf '%-6s %-10s %-10s %-10s %-8s %-10s %-10s %s\n' wait prefetch info_s info_code container stream_s stream_code bytes
for i in "${!waits[@]}"; do
  wait="${waits[$i]}"
  id="$1"; shift

  pre=$(curl -s -o /dev/null -w '%{http_code}' -X POST "${auth[@]}" "$server/gelato/prefetch/$id")
  sleep "$wait"

  info_code=503
  info_s=0
  for attempt in 1 2 3 4 5; do
    read -r info_code info_t < <(curl -s -o "$tmp/info.json" -w '%{http_code} %{time_total}\n' -X POST "${auth[@]}" \
      -H 'Content-Type: application/json' \
      -d "{\"UserId\":\"$GELATO_USER_ID\",\"StartTimeTicks\":0}" \
      "$server/Items/$id/PlaybackInfo")
    info_s=$(awk -v a="$info_s" -v b="$info_t" 'BEGIN { print a + b }')
    [ "$info_code" = 503 ] || break
    sleep 2
  done

  container=$(jq -r '.MediaSources[0].Container // ""' "$tmp/info.json" 2>/dev/null || true)
  source_id=$(jq -r '.MediaSources[0].Id // ""' "$tmp/info.json" 2>/dev/null || true)
  stream_code=-
  stream_s=-
  bytes=0
  if [ "$info_code" = 200 ] && [ -n "$container" ] && [ -n "$source_id" ]; then
    read -r stream_code stream_s bytes < <(curl -s -o /dev/null -r 0-2097151 "${auth[@]}" \
      -w '%{http_code} %{time_total} %{size_download}\n' \
      "$server/Videos/$id/stream.${container%%,*}?Static=true&MediaSourceId=$source_id")
  fi

  printf '%-6s %-10s %-10s %-10s %-8s %-10s %-10s %s\n' "${wait}s" "$pre" "$info_s" "$info_code" "${container:-none}" "$stream_s" "$stream_code" "$bytes"
  case "$stream_code" in
    200|206) [ "$bytes" -gt 0 ] || failed=1 ;;
    *) failed=1 ;;
  esac
done

[ "$failed" = 0 ] && echo "all runs played" || { echo "at least one run failed"; exit 1; }
