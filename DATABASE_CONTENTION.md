# SQLite write contention: what slows Jellyfin down and what to change

Measured in November 2026 against Jellyfin 12.0.0 (the lscr image
`12.0ubu2604-ls48`) with a synthetic library shaped like the live one:
425k BaseItems, 1.95M AncestorIds, 841k provider ids, 279k images, 216k
people, 493 live channels and about 8k programmes, 2.1 GB. The load in
every run is the one that hurt on the live server: Refresh Guide with the
XMLTV cache deleted, three Gelato stream syncs (40 streams each), Sync
series trees (20 continuing series, 720 new episodes), a cold Play, and a
warm title asked for PlaybackInfo and its item page every 2 seconds.

`scripts/contention-test.sh` repeats this on a copy of the live server.

## 1. Journal mode and locking behaviour (the big one)

What Jellyfin 12 sets (`SqliteDatabaseProvider`, `PragmaConnectionInterceptor`):

| Setting | Value | Where |
| --- | --- | --- |
| journal_mode | WAL, set once by the `ResetJournalMode` migration; nothing ever sets it back | stored in the database file |
| synchronous | 1 (NORMAL) | every connection, `syncmode` option |
| locking_mode | NORMAL | every connection |
| busy_timeout | not set; Microsoft.Data.Sqlite retries a busy command until its 60 s command timeout | connection string |
| temp_store | 2 (memory) | every connection |
| LockingBehavior | NoLock unless `database.xml` says otherwise | `config/database.xml` |

The live database was read as `journal_mode = delete`. Two cautions:

- Opening a WAL database with `immutable=1` (some read-only viewers do)
  also reports `delete`. The file header settles it:
  `od -An -tu1 -j18 -N2 jellyfin.db` prints `2 2` for WAL and `1 1` for a
  rollback journal; a WAL database also has `jellyfin.db-wal` and
  `jellyfin.db-shm` next to it while Jellyfin runs.
  `scripts/contention-test.sh journal` prints both, plus LockingBehavior.
- In delete mode a writer has to wait for every reader to finish before it
  can commit, and readers get `database is locked` while it commits. Every
  authenticated request updates the device's last activity, which is why a
  plain `GET /Jellio/achievements` failed with error 5.

Results with the original Gelato build (plus only the fix for the series
sync crash in section 3, so the series sync really ran), unthrottled disk:

| Journal | LockingBehavior | Refresh Guide | Stream sync (40) | PlaybackInfo p95 | Item page p95 |
| --- | --- | --- | --- | --- | --- |
| delete | NoLock | 50.1 s | 1.8 to 3.5 s | 4.59 s | 12.60 s |
| delete | Optimistic | 22.4 s | 0.5 to 0.7 s | 1.45 s | 2.13 s |
| delete | Pessimistic | **1037.5 s** | 0.5 to 0.7 s | 1.39 s | 3.08 s |
| WAL | NoLock | 15.9 s | 0.5 to 0.7 s | 0.44 s | 0.92 s |

Pessimistic turns a one minute guide refresh into 17 minutes: it makes
every read wait for every write and Jellyfin itself warns that it can
deadlock. If the live `database.xml` says `Pessimistic`, that alone
explains the 46 minutes.

The same load with the container's writes throttled to 2000 IOPS and
60 MB/s, closer to a network block volume (the guide import alone could
not finish within 15 minutes in any mode at that rate, see section 4):

| Build | Journal | Cold Play | Warm PlaybackInfo p95 / max | Item page p95 / max | Stream sync (40) | database is locked |
| --- | --- | --- | --- | --- | --- | --- |
| original | delete | 7.4 s | 0.55 s / 46.8 s | 1.10 s / 10.6 s | 1.5 to 2.9 s | 0 (8 at 300 IOPS, with a 29.6 s cold Play) |
| original | WAL | 2.3 s | 0.14 s / 8.2 s | 0.59 s / 1.2 s | 0.5 to 1.6 s | 0 |
| this build | delete | 5.7 s | 0.57 s / 55.7 s | 1.15 s / 60.2 s | 0.8 to 1.4 s | 4 |
| this build | WAL | **1.1 s** | **0.15 s** / 10.0 s | **0.59 s** / 0.8 s | **0.5 s** | **0** |

Our changes do not save a delete-mode database: readers and the writer
still block each other. WAL is the fix; our changes keep the writes that
remain small and in line. The occasional multi-second PlaybackInfo on WAL
is Jellyfin itself: every authenticated request saves the device's last
activity, and that write waits for SQLite's single writer while a guide
transaction holds it.

### Switching the live database to WAL

Only needed when `scripts/contention-test.sh journal` says `delete`.

1. Stop Jellyfin with time to spare. Jellyfin 12.0 vacuums the whole
   database on shutdown, which takes minutes for 3 GB on a network volume;
   the default 10 second stop kills it halfway (harmless, but wasted):
   `docker stop -t 900 jellyfin`
2. Back up while it is stopped:
   `cp -a .../data/data/jellyfin.db .../data/data/jellyfin.db.pre-wal`
   (and a `jellyfin.db-journal` if one exists; its presence means the last
   write was interrupted, so start Jellyfin once and stop it again first).
3. Switch, still stopped:
   `python3 -c "import sqlite3; print(sqlite3.connect('.../data/data/jellyfin.db').execute('pragma journal_mode=wal').fetchone())"`
   prints `('wal',)`.
4. In `config/database.xml` make sure it reads `<LockingBehavior>NoLock</LockingBehavior>`.
5. Start Jellyfin and check: `scripts/contention-test.sh journal` says
   `wal`, and `jellyfin.db-wal` exists.

Jellyfin does not reset the mode at start; the mode lives in the file.
To roll back: stop, `pragma journal_mode=delete` the same way, or put the
`.pre-wal` file back.

## 2. Our writers

| Writer | Before | Now |
| --- | --- | --- |
| Stream probe (Play, PlaybackInfo, warm-up) | The injected list of probe providers is always empty (Jellyfin builds providers by reflection), so every probe ran `RefreshMetadata` on the stream row: TMDb and OMDb lookups, the image providers (the ~600 `SaveImage intercepted` lines, four image types per row) and a full save | The probe provider comes from the provider manager; a probe reads the container and saves the streams once |
| SyncStreams | Saved orphans, then deleted them one transaction each (written=80 for 40 streams) | Only stale rows that stay are saved; orphans go in one batch delete |
| Background stream syncs (prefetch, warm-up, refresh behind stored streams) | Wrote at the same time as everything else | Queue behind the other background writers; a viewer joining a queued sync lets it go ahead |
| Sync series trees, catalog import, artwork backfill, release dates, stream purge | Two series at a time, four catalog items at a time, purge one row per transaction | One background writer at a time; these also wait while Refresh Guide or Optimize database runs (at most 30 minutes); the purge deletes 200 rows per batch |
| Play, PlaybackInfo, detail page | | Never wait on the gate |
| Addon JSON | A raw carriage return inside a string failed the whole response | Parsed once more with control characters escaped, without a new request; addon URLs are no longer logged with their configuration |

The `'0x0D' is invalid within a JSON string` errors came with the probe
fallback: each probe ran the OMDb fetcher, whose answers carry raw line
breaks for some titles, and a client retrying PlaybackInfo after a 503
probed (and fetched) again. Neither happens now.

Jellio does not write to the Jellyfin database in the background: its
achievements, playback log and live-channel health live in its own JSON
files and are written only when they change.

## 3. Separate bugs found on the way

- Running Sync series trees from the dashboard failed after 0 seconds
  (`ObjectDisposedException: HttpContext`): the task kept the finished
  request's context. Fixed.
- After the weekly Purge streams every title still counted as synced
  (the markers in `stream-sync.json` survived), so Play found no streams
  and PlaybackInfo answered 503 until they expired. Fixed: the purge clears
  them, and the first Play afterwards syncs again (200 in 1.9 s on the copy).
- This Gelato build does not load on Jellyfin 12.1 or later:
  `IItemRepository.GetTagNames` was added and `GelatoItemRepository` does
  not implement it. Stay on 12.0 until that is added.

## 4. The guide itself

Jellyfin keys a programme by channel and start time and skips unchanged
ones by their ETag. A programme whose start moves between two downloads is
a new programme: inserted with all of BaseItems' indexes, while the old one
is deleted one transaction at a time. Each programme row costs roughly
one programme insert or delete; deleting is the slow half, about 0.2 seconds a programme on an unthrottled disk with a library this size (one transaction each, cascading through AncestorIds and the other tables) of page writes.

Guide refresh one hour after the previous one, unthrottled, nothing else
running, 493 channels of which 60 have a real schedule:

| Filler on the other 433 channels | Programmes | Refresh an hour later | Written |
| --- | --- | --- | --- |
| 3 h blocks counted from "now" (they move every hour) | 8,076 | **980.6 s** | 1,355 MB |
| 3 h blocks on fixed clock boundaries (00:00, 03:00, ...) | 7,836 | 14.4 s | 255 MB |
| none | 2,640 | 25.1 s | 177 MB |

Switching from moving to fixed blocks, or dropping them, costs one slow
refresh (the old blocks are deleted: 1,089 s here) and is fast after that.

What to change in the XMLTV proxy that adds the "Live: <channel>" filler:
start every block on a fixed boundary (a multiple of 3 hours since
midnight UTC) so a block keeps its start time, and with it its identity,
from one download to the next. Longer blocks (6 or 12 hours) cut the rows
further. Keep the channel's real programmes as they are.

The daily Refresh Guide trigger at 05:30 needs nothing from our side
anymore: Gelato's heavy background writers wait while it runs.

## 5. Sixty minutes of mixed load

`scripts/contention-test.sh soak 60` on a copy of the bench server (this
build, WAL, NoLock, filler on fixed boundaries): 69 rounds of guide
refresh plus three stream syncs plus Sync series trees, PlaybackInfo and
the item page every 2 seconds.

| | Result |
| --- | --- |
| Refresh Guide per round | 10.5 to 11.5 s (the first round, which replaced the old programmes, 131.8 s) |
| Sync series trees per round | 40 to 55 s (waits for the guide by design) |
| Stream syncs (40 streams) | 0.5 to 0.7 s; one 6 s while Optimize database ran |
| PlaybackInfo, warm title | p50 0.10 s, p95 0.15 s, 1,259 calls |
| Item page | p50 0.51 s, p95 0.62 s, max 1.98 s |
| database is locked | 1, see below |

The one lock, and the one 57 s PlaybackInfo, came 59 minutes in, when
Jellyfin's own daily tasks came due on the copy: Optimize database vacuums
the whole file (2 min 16 s here) and Refresh People's cleanup query timed
out behind it after 60 s. Gelato's purge and series sync waited for it as
intended. On the live server, put Optimize database at a time when nothing
else runs (not near the 05:30 guide refresh).

## 6. Repeating the test on a copy of the live server

Nothing here writes to the live database or stops the live container. The
copy runs from the live image, on the live network, with no published
ports and the media mounted read only. It calls the same addon and tuner
as the live server while it runs.

```sh
export JF_TOKEN=<an API key>        # never printed
scripts/contention-test.sh journal   # journal mode and LockingBehavior of the live DB
scripts/contention-test.sh copy      # about 5 GB to /home/opc/jf-contention
scripts/contention-test.sh up                       # current build
# or: scripts/contention-test.sh up /path/to/new/gelato/build
scripts/contention-test.sh run before-delete <warm movie id> <movie id> <movie id> <movie id>
scripts/contention-test.sh mode wal NoLock          # also: delete, Optimistic, Pessimistic
scripts/contention-test.sh run after-wal <warm movie id> <movie id> <movie id> <movie id>
scripts/contention-test.sh soak 60 <warm movie id> <movie id> <movie id> <movie id>
scripts/contention-test.sh down
```

The warm movie should be one that has played before; the other three
should not have been opened recently, so their stream lists get synced.
Results go to `/home/opc/jf-contention-results/<label>/summary.txt`:
guide and series sync durations per round, `database is locked` and save
error counts, every SyncStreams line's fetch, wait and write time, and
PlaybackInfo and item page latency (p50, p95, max) while the load runs.
