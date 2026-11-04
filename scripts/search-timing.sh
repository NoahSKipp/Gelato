#!/usr/bin/env bash
# Times Gelato's intercepted search against a server. Read only: a search
# inserts nothing. Not run in CI. Prints timings and counts only.
#
#   GELATO_SERVER=https://jellyfin.example  GELATO_TOKEN=...  GELATO_USER_ID=...
#   scripts/search-timing.sh "Heat" "Gladiator II" ...
#
# Each term is asked twice: the second answer should come from Gelato's
# 60 second search cache. Pick terms nobody searched in the last minute.
set -euo pipefail
: "${GELATO_SERVER:?set GELATO_SERVER}"
: "${GELATO_TOKEN:?set GELATO_TOKEN}"
: "${GELATO_USER_ID:?set GELATO_USER_ID}"
[ "$#" -gt 0 ] || { echo "give one or more search terms" >&2; exit 2; }
server="${GELATO_SERVER%/}"
auth="Authorization: MediaBrowser Client=\"search-timing\", Device=\"search-timing\", DeviceId=\"search-timing\", Version=\"1\", Token=\"$GELATO_TOKEN\""
tmp="$(mktemp)"; trap 'rm -f "$tmp"' EXIT
printf '%-24s %-8s %-10s %-10s %s\n' term results first_s again_s code
for term in "$@"; do
  q=$(python3 -c 'import sys,urllib.parse; print(urllib.parse.quote(sys.argv[1]))' "$term")
  url="$server/Items?searchTerm=$q&IncludeItemTypes=Movie,Series&Recursive=true&Limit=100&userId=$GELATO_USER_ID"
  read -r code first < <(curl -s -o "$tmp" -w '%{http_code} %{time_total}\n' -H "$auth" "$url")
  n=$(python3 -c 'import json,sys; print(len(json.load(open(sys.argv[1]))["Items"]))' "$tmp" 2>/dev/null || echo -)
  read -r _ again < <(curl -s -o /dev/null -w '%{http_code} %{time_total}\n' -H "$auth" "$url")
  printf '%-24s %-8s %-10s %-10s %s\n' "$term" "$n" "$first" "$again" "$code"
done
