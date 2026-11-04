# Search: where the time went

Measured in November 2026 on Jellyfin 12.0.0 (lscr `12.0ubu2604-ls48`),
WAL, 4 cores, with a library shaped like the live one (426k items) and a
mock addon whose search answers in 0.4 s: 48 movies and 10 series for a
one-word term, 6 and 2 for two words.

## The cost

The intercepted search inserts nothing; it turns each addon result into
an item DTO. Built with every field, four of those fields are database
lookups even for an item that is not in the library:

| Field | Per result |
| --- | --- |
| SpecialFeatureCount | about 185 ms |
| LocalTrailerCount | about 185 ms |
| MediaSources | about 40 ms |
| MediaStreams | about 40 ms |
| every other field together | under 5 ms |

So the time grew with the number of results, one after another: 0.4 s
per result here, 0.7 s on the live server with its slower disk ("Heat",
48 results, 33.4 s).

A search result is not in the library, so it has no extras, trailers,
streams or chapters. Those fields are now filled in with exactly what
Jellyfin returned for them (0, 0, one placeholder source, no streams, no
chapters); everything else is built as before, so clients that need
every field (Infuse) get the same answer. Opening a result still creates
the real item and redirects to its id; nothing about Play changed.

Also new: identical searches are answered from a 60 second cache per
user, a search the client abandons cancels its addon requests, and the
log line splits the time:

    Intercepted /Items search "Heat" types=[Movie,Series] ... results=58 in 512 ms (addon 402 ms, items 110 ms)

## Before and after (bench server)

| Search | Results | Before | After | Repeated within 60 s |
| --- | --- | --- | --- | --- |
| Heat | 58 | 25.1 s | 0.52 s | 0.013 s |
| Alien | 58 | 25.1 s | 0.52 s | |
| Gladiator II | 8 | 3.7 s | 0.43 s | |
| The Thing | 8 | 3.7 s | 0.43 s | |

0.40 s of each "after" is the mock addon; on the live server the addon's
own search time (AIOStreams, plus a TMDB lookup for terms of three
letters or more) is what remains.

Opening a result and playing it, after: detail 200 in 2.9 s (the real
item is created and its 15 streams synced), PlaybackInfo 200 in 1.6 s,
the stream answers 206.

## Repeating it on the live server

Read only; a search writes nothing.

    GELATO_SERVER=https://<server> GELATO_TOKEN=<api key> GELATO_USER_ID=<user id> \
      scripts/search-timing.sh "Heat" "Gladiator II" "The Batman"

It prints, per term, the result count, the first answer's time and the
repeat (from the cache). The server log line above has the addon and
item split.

## Searching on TMDB instead of the addon

On the live server the addon's own search was the cost: the self-hosted
tmdb-addon behind AIOStreams takes 2.1 to 4.4 s for a movie search, 3.9 s
(1.7 MB) for a series search, and anything from 0.4 to 10 s through
AIOStreams. Gelato sends the movie and the series catalog request at the
same moment (on the bench both start at +0.00 s and end at +2.50 s with
a 2.5 s addon), so a search that costs the sum of the two on the live
server is being queued behind Gelato, in AIOStreams or the addon.

Gelato now searches TMDB itself ("Search on TMDB directly", on by
default): movies (two pages) and TV (one page) in parallel, in the
server's metadata language, each hit resolved to its IMDb id (16 at a
time, kept for a week), so results are the same tt ids, the same items
and the same streams as before. Hits without an IMDb id are left out
(nothing could be streamed for them). When TMDB fails or finds nothing,
the addon's search catalog answers as before. The 60 s search cache is
unchanged.

    Search "Heat" on TMDB: movie page 1 152 ms (20), tv page 1 151 ms (20), movie page 2 152 ms (20), imdb ids 330 ms (57 of 60, 0 cached)

Bench server (addon search 2.5 s per request, TMDB mock 150 ms per
search and 80 ms per id lookup):

| Search | Before | TMDB, first time | TMDB, ids known | TMDB down |
| --- | --- | --- | --- | --- |
| Alien | 2.68 s | 0.76 s | 0.27 s | 2.66 s (addon) |
| The Batman | 2.54 s | 0.75 s | | |
| Heat | 2.65 s | 0.78 s | 0.35 s | |
| Die Verurteilten | 2.54 s | 0.81 s | | |

A TMDB result opens and plays like an addon result: the movie got its
item with 15 sources, PlaybackInfo 200 and a playing stream; the series
opened with its 8 seasons.
