#!/usr/bin/env bash
# Times the requests Gelato makes for one search, from inside the Jellyfin
# container (same network path), straight against the addon: manifest,
# each search catalog with Gelato's headers (none) and with a browser
# User-Agent, and the TMDB title lookup. Not run in CI. Prints hosts and
# timings only; the addon address (it carries your configuration) is read
# inside the container and never printed.
#
#   JELLYFIN=jellyfin scripts/addon-search-timing.sh "Alien" "The Batman"
set -euo pipefail
JELLYFIN=${JELLYFIN:-jellyfin}
[ "$#" -gt 0 ] || { echo "give one or more search terms" >&2; exit 2; }
cfg=/config/data/plugins/configurations/Gelato.xml
docker exec "$JELLYFIN" sh -c "test -f $cfg" || cfg=/config/plugins/configurations/Gelato.xml
base=$(docker exec "$JELLYFIN" sh -c "sed -n 's:.*<Url>\(.*\)</Url>.*:\1:p' $cfg | head -1" | sed 's#/manifest.json$##; s#/$##')
[ -n "$base" ] || { echo "no addon Url in Gelato's configuration" >&2; exit 2; }
host=$(echo "$base" | sed -E 's#^[a-z]+://([^/:]+).*#\1#')
fmt='%{http_code} dns %{time_namelookup} connect %{time_connect} tls %{time_appconnect} first-byte %{time_starttransfer} total %{time_total} bytes %{size_download}'

timed() { # timed <label> <url> [user agent]
  local out
  out=$(docker exec -e U="$2" -e A="${3:-}" "$JELLYFIN" sh -c 'rm -f /tmp/addon.json; if [ -n "$A" ]; then curl -s -o /tmp/addon.json -A "$A" -w "'"$fmt"'" "$U"; else curl -s -o /tmp/addon.json -H "User-Agent:" -w "'"$fmt"'" "$U"; fi' || true)
  local n
  n=$(docker exec "$JELLYFIN" sh -c 'cat /tmp/addon.json' 2>/dev/null | python3 -c 'import json,sys; d=json.load(sys.stdin); print(len(d.get("metas", [])))' 2>/dev/null || echo -)
  printf '%-34s %s results %s\n' "$1" "$out" "$n"
}

echo "addon host: $host (requests made from inside $JELLYFIN)"
manifest=$(docker exec -e U="$base/manifest.json" "$JELLYFIN" sh -c 'curl -s "$U"')
timed "manifest" "$base/manifest.json"
catalogs=$(echo "$manifest" | python3 -c '
import json, sys
m = json.load(sys.stdin)
for kind in ("movie", "series"):
    hits = [c for c in m.get("catalogs", []) if c.get("type", "").lower() == kind and any(e.get("name") == "search" for e in c.get("extra", []))]
    hits.sort(key=lambda c: "people" in c.get("id", ""))
    print(kind, len(hits), hits[0]["id"] if hits else "")
')
echo "$catalogs" | while read -r kind count id; do echo "search catalogs for $kind: $count (Gelato uses the first)"; done
ua="Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36"
for term in "$@"; do
  q=$(python3 -c 'import sys,urllib.parse; print(urllib.parse.quote(sys.argv[1]))' "$term")
  echo "== \"$term\""
  echo "$catalogs" | while read -r kind count id; do
    [ -n "$id" ] || continue
    url="$base/catalog/$kind/$(python3 -c 'import sys,urllib.parse; print(urllib.parse.quote(sys.argv[1]))' "$id")/search=$q.json"
    timed "$kind catalog, Gelato headers" "$url"
    timed "$kind catalog, browser agent" "$url" "$ua"
    timed "$kind catalog, again (cached?)" "$url"
  done
  timed "tmdb title lookup (movie)" "https://api.themoviedb.org/3/search/movie?query=$q&language=en-US&include_adult=false&api_key=4219e299c89411838049ab0dab19ebd5"
done
