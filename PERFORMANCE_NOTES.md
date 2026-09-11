# Performance follow-ups — action guide (nothing here is Gelato code)

Three AIOStreams-side settings found while chasing Nuvio-parity latency in
Gelato/Jellio/Jellio-TV. All three live on **your AIOStreams instance**,
not in this repo — nothing to merge, just settings to flip and verify.
Ordered by how much each one is likely to matter.

For each: where the setting lives, the exact value, and how to actually
confirm it's doing something (real log lines, traced from AIOStreams'
own source, not guessed) — useful if you point your other Claude Code
agent's Portainer log access at the AIOStreams container after making a
change.

---

## 1. `cacheAndPlay` — play an uncached result while it downloads

**Likely the biggest of the three.** The other two only make an
already-fast (cached) result faster; this decides whether an *uncached*
torrent/usenet result — the majority of anything less than top-tier
popular content — is playable at all without manually waiting for it to
finish downloading first.

- **Where**: AIOStreams user config (same config screen as your other
  addon/service settings, not an instance-admin-only setting).
- **What to set**: `cacheAndPlay: { enabled: true, streamTypes: ["torrent"] }`
  — start with `torrent` only; add `"usenet"` too once you've confirmed it
  behaves the way you want. This is absent entirely from Gelato's own
  starter `aiostreams-config.json`, so it's off unless you've set it
  yourself.
- **Also check**: `excludeUncachedFromServices` / `excludeUncachedFromStreamTypes`
  in your config (both empty in the starter file) — if either lists your
  actual debrid service/stream type, uncached results are being filtered
  out before `cacheAndPlay` ever gets a chance to matter.
- **How to verify it's real**: open a title you're confident has no cached
  result on your debrid service right now, hit play on the uncached
  stream, and confirm it actually starts (progressively) instead of
  erroring or hanging. This one doesn't have a clean dedicated log line —
  it's a behavior flag threaded straight into the debrid resolve call
  (`packages/core/src/main/serviceWrapper.ts` → `debrid/base.ts`), so the
  real test is trying it, not grepping.

## 2. `preloadStreams` — ping the current item's own top stream(s)

- **Where**: AIOStreams user config, same tier as `precacheNextEpisode`.
- **What to set**: `preloadStreams: { enabled: true }` (defaults:
  `selector: "slice(streams, 0, 2)"`, `singleStream: true` → only the #1
  stream gets pinged, which is the sane starting point).
- **Why it compounds with what's already shipped**: fires the moment
  AIOStreams resolves a stream list — which is exactly when our
  `gelato/prefetch/{itemId}` → `SyncStreams` call reaches it (card focus,
  next-episode-known, search-result prefetch). So enabling this means
  every one of those prefetch triggers now also warms the actual
  CDN/debrid-edge connection, not just Gelato's own DB row.
- **How to verify it's real**: AIOStreams needs `LOG_LEVEL=debug` (its
  default is almost certainly quieter than that) to show this at all.
  With debug logging on, focus a poster in Jellio/Jellio-TV (or open a
  title) and grep the AIOStreams container logs for:
  ```
  pinging stream urls
  ```
  That line fires from `pingStreamUrls()`, shared by both this setting
  and `precacheNextEpisode` below.
- **Rate limits already in place** (instance-wide, admin-only, probably
  fine to leave as-is): 1 hour cooldown per item per user
  (`PRELOAD_MIN_INTERVAL`), max 5 concurrent pings
  (`PRELOAD_STREAMS_CONCURRENCY`).
- **Repo follow-up once confirmed working**: tell me and I'll add
  `preloadStreams: { enabled: true }` to `aiostreams-config.json` in this
  repo so future setups pick it up automatically, matching how
  `precacheNextEpisode` is already in there.

## 3. `provideStreamData` — structured cached/uncached signal

**The one with real follow-on Gelato work if you turn it on** — don't
flip this one lightly, it changes response shape for every client your
instance serves, not just Gelato.

- **Where**: AIOStreams **instance-admin** settings (not per-user) — env
  var `PROVIDE_STREAM_DATA`, or the equivalent admin UI toggle if your
  deployment exposes one.
- **What to set**: `true` to enable for everyone hitting the instance, or
  (safer, if supported) an IP allowlist scoped to just your Jellyfin
  server's address, via the same field.
- **How to verify it's real**: after enabling, have your other agent curl
  the same stream endpoint URL Gelato itself calls (visible in AIOStreams
  request logs, or reconstructable from your addon manifest URL —
  `{base}/stream/{type}/{id}.json`) and check the raw JSON for a
  `streamData` object per stream, with a nested `service.cached` boolean.
  If it's there, the setting worked.
- **What happens after you confirm it**: this is where I pick back up —
  model the real (verified, not guessed) field shape into Gelato's
  `StremioStream` C# class, thread `cached` through `SyncStreams` into
  `GelatoData` (same mechanism already carrying name/description/
  bingeGroup/filename), surface it on `MediaSourceInfo`, then have the
  player (`waitForPlayableBuffer` in `screens/player.js`,
  `PREBUFFER_TARGET_SECONDS = 8` / `PREBUFFER_TIMEOUT_MS = 12000`, and its
  Jellio-TV equivalent) shorten or skip that buffer cushion **only** for a
  confirmed-cached source instead of the current blind reactive wait that
  treats every source identically.
- **Don't do this step yourself** — come back and tell me it's on and
  verified, and I'll do the Gelato/Jellio side.

---

## Optional, smaller, Jellio-only (no AIOStreams involved)

**Home hero backdrop image priority.** `heroCarousel.js`'s
`crossfadeBackdrop()` sets the hero image via CSS `background-image` on a
`<div>`, not an `<img>` tag — a browser's HTML preload scanner can't
discover a JS-set background-image the way it can an `<img src>` or
`<link rel=preload>`, so the fetch only starts once the hero-candidate API
call has already resolved and the JS has run. Can't be fully preloaded
(the URL is dynamic, decided by API response), but switching to a real
`<img>` with `fetchPriority="high"` set the moment the URL is known would
give the browser a stronger priority signal relative to the row poster
images loading concurrently around it. Small, real, but modest compared
to the three above — say the word if you want this done, otherwise
leaving it noted.

---

## Context

All three AIOStreams settings found auditing `Viren070/AIOStreams` source
directly (cloned at `/home/user/aiostreams` during the session that wrote
this) rather than guessing from docs, as part of the broader Nuvio-parity
latency work already shipped this round: speculative stream/probe
prefetch on card focus (debounced), split Movies/Series search,
not-yet-inserted search-result prefetch, next-episode prefetch, and the
Jellyfin 12 migration/compatibility work.
