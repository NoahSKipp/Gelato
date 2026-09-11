# Performance follow-ups (parked, not yet implemented)

Two AIOStreams-side levers found while chasing Nuvio-parity latency work
in Gelato/Jellio/Jellio-TV. Both require a decision on your own AIOStreams
instance before any code changes, so they're parked here rather than
implemented blind.

## 1. `provideStreamData` — structured cached/uncached signal

- **What it is**: an AIOStreams **instance-wide** admin setting
  (`packages/core/src/config/schema/api.ts`, env `PROVIDE_STREAM_DATA`).
  Default `null` auto-detects by User-Agent (only `AIOStreams/*` clients get
  it automatically). `true` turns it on for every client hitting that
  instance; an IP list scopes it to specific request IPs.
- **What it unlocks**: when on, every stream response carries a `streamData`
  block (`packages/core/src/transformers/stremio.ts`) including
  `service.cached` — a real boolean AIOStreams itself already tracks
  internally for sorting/stats, normally never exposed past display-text
  emoji (⚡/⏳).
- **Why it matters**: Gelato's own `StremioStream` C# model has no `cached`
  field at all today — even with the setting on, `System.Text.Json` silently
  drops the extra data. With it modeled, the player could shorten/skip the
  `waitForPlayableBuffer` cushion (web: `PREBUFFER_TARGET_SECONDS = 8`,
  `PREBUFFER_TIMEOUT_MS = 12000` in `screens/player.js`; Jellio-TV has the
  equivalent) **only** for a confirmed-cached source, instead of the current
  blind reactive wait that treats every source the same.
- **Why parked**: turning this on changes response payload size/shape for
  *every* request your AIOStreams instance serves, to every client, not
  just Gelato. That's your infrastructure's tradeoff to make, not something
  to flip from Gelato's side.
- **Next step if you want it**: turn on `PROVIDE_STREAM_DATA` (instance
  admin UI or env var), then get real captured JSON from your own live
  setup (a temporary Gelato-side diagnostic log, not a guess from reading
  AIOStreams' TypeScript source) before modeling `StremioStream`/
  `MediaSourceInfo`/the player logic. Verify real field shape first, build
  second — see the deep-dive conversation this note came out of.

## 2. `preloadStreams` — ping the current item's own top stream(s)

- **What it is**: a **per-user** AIOStreams config option (same tier as
  `precacheNextEpisode`, which Gelato's own `aiostreams-config.json` starter
  config already enables). Schema: `db/schemas.ts` — `enabled`, `selector`
  (default `slice(streams, 0, 2)`), `singleStream` (default true → only the
  #1 stream).
- **What it does**: `main/resources.ts`'s `pingStreamUrls()` fires a real
  HTTP request against the selected stream URL(s) the moment AIOStreams
  resolves a stream list — opens the connection, reads headers/status,
  cancels the body. This is genuine CDN/debrid-edge connection warm-up,
  exactly the kind of thing `waitForPlayableBuffer` exists to cover.
- **Why it's a bigger deal than `precacheNextEpisode`**: that setting only
  helps the *next* episode. This fires for the *current* item, every time
  — meaning it compounds directly with the prefetch-on-focus work already
  shipped: card focus → our `gelato/prefetch/{itemId}` → `SyncStreams` hits
  AIOStreams → if this is on, AIOStreams also pings the real stream URL →
  connection is warm before the reader even presses Select.
- **Why parked**: not code — just hadn't been turned on anywhere yet.
  Self-rate-limited by sane AIOStreams instance defaults (1h cooldown per
  item, max 5 concurrent pings), so risk is low, but it's still a live
  setting on your own instance to decide on, and the starter config in this
  repo (`aiostreams-config.json`) hasn't been updated to include it yet.
- **Next step if you want it**: (a) add `preloadStreams: { enabled: true }`
  to `aiostreams-config.json` in this repo so future imports pick it up,
  and (b) flip the equivalent toggle on your live AIOStreams instance now
  (the starter config only applies at initial import).

## Context

Both found while auditing `Viren070/AIOStreams` source directly (cloned at
`/home/user/aiostreams` during that session) rather than guessing from
docs, as part of the broader Nuvio-parity latency work already shipped:
speculative stream/probe prefetch on card focus (debounced), split
Movies/Series search, not-yet-inserted search-result prefetch, next-episode
prefetch, and the Jellyfin 12 migration/compatibility work.
