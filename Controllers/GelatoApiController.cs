#pragma warning disable SA1611, SA1591, SA1615, CS0165

using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Gelato.Decorators;
using Gelato.Filters;
using Gelato.Services;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;

namespace Gelato.Controllers;

[ApiController]
[Route("gelato")]
public sealed class GelatoApiController : ControllerBase
{
    private readonly ILogger<GelatoApiController> _log;
    private readonly GelatoManager _gelatoManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IDtoService _dtoService;
    private readonly InsertActionFilter _insertActionFilter;
    private readonly IServerApplicationHost _appHost;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly WarmTracker _warm;
    private readonly NzbdavPresence _nzbdav;
    private readonly WarmupGate _gate;
    private readonly BackgroundWriteGate _writeGate;
    private readonly string _downloadPath;

    private static readonly SlidingWindowLimiter BatchLimiter = new(
        PrefetchBatch.CallsPerMinute,
        TimeSpan.FromMinutes(1)
    );

    // user:item:probe -> when a batch last queued it, so a title is warmed
    // once per StreamTTL however often a list asks.
    private static readonly ConcurrentDictionary<string, DateTime> BatchQueued = new();

    public GelatoApiController(
        ILogger<GelatoApiController> log,
        IApplicationPaths appPaths,
        GelatoManager gelatoManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IDtoService dtoService,
        InsertActionFilter insertActionFilter,
        IServerApplicationHost appHost,
        ISubtitleEncoder subtitleEncoder,
        WarmTracker warm,
        NzbdavPresence nzbdav,
        WarmupGate gate,
        BackgroundWriteGate writeGate
    )
    {
        _warm = warm;
        _nzbdav = nzbdav;
        _gate = gate;
        _writeGate = writeGate;
        _log = log;
        _gelatoManager = gelatoManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _dtoService = dtoService;
        _insertActionFilter = insertActionFilter;
        _appHost = appHost;
        _subtitleEncoder = subtitleEncoder;
        _downloadPath = Path.Combine(appPaths.CachePath, "gelato-torrents");
        Directory.CreateDirectory(_downloadPath);
    }

    // English/German specifically: real feedback asked for these two to
    // load "instantly like Netflix", not every language Gelato Subtitles
    // might attach. Broadening this list broadens the background work
    // every prefetch now pays for, not something to do without being asked.
    private static readonly string[] PrewarmSubtitleLanguages = ["eng", "ger", "deu"];

    // Card-open today blocks on SyncStreams (a network round trip to the
    // Stremio addon) the first time an item is opened, since
    // MediaSourceManagerDecorator.GetStaticMediaSources needs the stream
    // list before it can answer. This endpoint lets the frontend fire that
    // same sync speculatively (e.g. when a poster gains focus, well before
    // the user actually opens the detail page), so by the time the item is
    // opened for real the sync is already cached and GetStaticMediaSources
    // just skips straight to reading it back from the DB.
    //
    // Reuses the exact same cache key shape and HasStreamSync/SetStreamSync
    // guard MediaSourceManagerDecorator uses, so a real card-open racing a
    // prefetch never double-syncs.
    [HttpPost("prefetch/{itemId:guid}")]
    [Authorize]
    public ActionResult PrefetchStreams([FromRoute, Required] Guid itemId)
    {
        if (!HttpContext.TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return PrefetchInsert(itemId, userId);
        }

        if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
        {
            return BadRequest("Prefetch only supports movies and episodes.");
        }

        // PrimaryVersionId is Guid? as of Jellyfin 12 (was string), matching
        // MediaSourceManagerDecorator.cs's own real cache-key construction.
        // A repeat prefetch still probes and warms subtitles even when the
        // stream list is fresh.
        // The stream list does not queue behind other warm-ups (two at a time,
        // for nzbdav); only the probe does.
        RunDetached(async () =>
        {
            try
            {
                await SyncIfNeededAsync(item, userId, background: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Stream sync for {ItemId} failed", itemId);
            }
            await WarmLoggedAsync(itemId, true, () => WarmItemAsync(item, userId, true, background: false))
                .ConfigureAwait(false);
        });
        return Accepted();
    }

    public sealed class PrefetchBatchRequest
    {
        public List<Guid> ItemIds { get; set; } = [];
        public bool Probe { get; set; }
    }

    // Syncs the stream list of up to five titles one after another, and
    // probes the first one too when asked. Answers at once with what was
    // queued and what was already warm.
    [HttpPost("prefetch-batch")]
    [Authorize]
    public ActionResult PrefetchBatchStreams([FromBody] PrefetchBatchRequest body)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized();
        var user = _userManager.GetUserById(userId);
        if (user is null)
            return Unauthorized();
        if (!BatchLimiter.TryAcquire(userId))
            return StatusCode(StatusCodes.Status429TooManyRequests);

        var ttl = TimeSpan.FromSeconds(GelatoPlugin.Instance!.Configuration.StreamTTL);
        var now = DateTime.UtcNow;
        foreach (var stale in BatchQueued.Where(p => now - p.Value > ttl).Select(p => p.Key).ToList())
            BatchQueued.TryRemove(stale, out _);

        var (steps, alreadyWarm) = PrefetchBatch.Plan(
            body.ItemIds ?? [],
            body.Probe,
            (id, probe) => IsAlreadyWarm(id, userId, user, probe, now, ttl)
        );
        foreach (var step in steps)
            BatchQueued[$"{userId:N}:{step.ItemId:N}:{step.Probe}"] = now;

        RunDetached(async () =>
        {
            for (var i = 0; i < steps.Count; i++)
            {
                if (i > 0)
                    await Task.Delay(PrefetchBatch.Gap).ConfigureAwait(false);
                var step = steps[i];
                await WarmLoggedAsync(step.ItemId, step.Probe, () => WarmBatchItemAsync(step, userId, user))
                    .ConfigureAwait(false);
            }
        });

        return Accepted(new { Queued = steps.Select(s => s.ItemId), AlreadyWarm = alreadyWarm });
    }

    // { sourceId: warmedAtUtc } for this title's sources that start fast now,
    // under the source ids clients are given.
    [HttpGet("warm/{itemId:guid}")]
    [Authorize]
    public async Task<ActionResult<Dictionary<string, DateTime>>> GetWarmSources(
        [FromRoute, Required] Guid itemId,
        CancellationToken ct
    )
    {
        if (!HttpContext.TryGetUserId(out var userId) || _userManager.GetUserById(userId) is not { } user)
            return Unauthorized();
        var present = await _nzbdav.SnapshotAsync(ct).ConfigureAwait(false);
        return Ok(WarmSourcesOf(itemId, user, present));
    }

    // The same for a list view: { itemId: { sourceId: warmedAtUtc } }.
    [HttpGet("warm")]
    [Authorize]
    public async Task<ActionResult<Dictionary<string, Dictionary<string, DateTime>>>> GetWarmItems(
        [FromQuery] string? ids,
        CancellationToken ct
    )
    {
        if (!HttpContext.TryGetUserId(out var userId) || _userManager.GetUserById(userId) is not { } user)
            return Unauthorized();
        var present = await _nzbdav.SnapshotAsync(ct).ConfigureAwait(false);
        var result = new Dictionary<string, Dictionary<string, DateTime>>();
        foreach (var raw in (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(50))
        {
            if (!Guid.TryParse(raw, out var id))
                continue;
            var warm = WarmSourcesOf(id, user, present);
            if (warm.Count > 0)
                result[id.ToString("N")] = warm;
        }
        return Ok(result);
    }

    // { sourceId: qualityTier }, e.g. "2160p-dv", "1080p-sdr", or "unknown".
    [HttpGet("tiers/{itemId:guid}")]
    [Authorize]
    public ActionResult<Dictionary<string, string>> GetQualityTiers([FromRoute, Required] Guid itemId)
    {
        if (!HttpContext.TryGetUserId(out var userId) || _userManager.GetUserById(userId) is not { } user)
            return Unauthorized();
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || _mediaSourceManager is not MediaSourceManagerDecorator sources)
            return Ok(new Dictionary<string, string>());
        var tiers = new Dictionary<string, string>();
        foreach (var row in sources.ListSourceRows(item, user))
            tiers.TryAdd(row.ClientId, row.Tier);
        return Ok(tiers);
    }

    private Dictionary<string, DateTime> WarmSourcesOf(Guid itemId, User user, Func<string, bool?> present)
    {
        var warm = new Dictionary<string, DateTime>();
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || _mediaSourceManager is not MediaSourceManagerDecorator sources)
            return warm;
        foreach (var row in sources.ListSourceRows(item, user))
        {
            if (_warm.WarmedAt(row.RowId, present) is { } at)
                warm.TryAdd(row.ClientId, DateTime.SpecifyKind(at, DateTimeKind.Utc));
        }
        return warm;
    }

    private bool IsAlreadyWarm(Guid itemId, Guid userId, User user, bool probe, DateTime now, TimeSpan ttl)
    {
        if (BatchQueued.TryGetValue($"{userId:N}:{itemId:N}:{probe}", out var queuedAt) && now - queuedAt < ttl)
            return true;
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
            return false;
        var cacheKey = $"{userId}:{(item as Video)?.PrimaryVersionId ?? item.Id}";
        if (!_gelatoManager.HasStreamSync(cacheKey))
            return false;
        if (!probe)
            return true;
        if (_mediaSourceManager is not MediaSourceManagerDecorator sources)
            return false;
        var first = sources.ListSourceRows(item, user).FirstOrDefault();
        return first is not null && _warm.WarmedAt(first.RowId) is not null;
    }

    private async Task WarmBatchItemAsync(PrefetchStep step, Guid userId, User user)
    {
        var item = _libraryManager.GetItemById(step.ItemId);
        if (item is null)
        {
            // A search result not in the library yet: inserting it is the
            // slow part of its first open.
            if (_gelatoManager.GetStremioMeta(step.ItemId) is { } meta)
                item = await InsertForPrefetchAsync(step.ItemId, userId, meta, user).ConfigureAwait(false);
            if (item is null)
                return;
        }
        if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
            return;
        await WarmItemAsync(item, userId, step.Probe).ConfigureAwait(false);
    }

    // background: nobody has the title open (a batch from a result list), so
    // its writes queue behind the other background writers. The title page's
    // own prefetch has a viewer and does not.
    private async Task WarmItemAsync(BaseItem item, Guid userId, bool probe, bool background = true)
    {
        await SyncIfNeededAsync(item, userId, background).ConfigureAwait(false);
        // The probe is the same call Play makes, so Play reuses its result.
        if (probe)
            await WarmPlaybackDataAsync(item, userId, item.Id).ConfigureAwait(false);
    }

    private async Task SyncIfNeededAsync(BaseItem item, Guid userId, bool background)
    {
        var cacheKey = $"{userId}:{(item as Video)?.PrimaryVersionId ?? item.Id}";
        if (_gelatoManager.HasStreamSync(cacheKey))
            return;
        var count = await _gelatoManager
            .SyncStreams(item, userId, CancellationToken.None, background)
            .ConfigureAwait(false);
        if (count > 0)
            _gelatoManager.SetStreamSync(cacheKey);
    }

    // One line per warm-up; no addresses.
    private Task WarmLoggedAsync(Guid itemId, bool probe, Func<Task> work) =>
        _gate.RunAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            var ok = false;
            try
            {
                await work().ConfigureAwait(false);
                ok = true;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Warm-up failed for {ItemId}", itemId);
            }
            finally
            {
                _log.LogInformation(
                    "Warm-up {ItemId}: {Ms} ms, ok={Ok}, probe={Probe}",
                    itemId,
                    sw.ElapsedMilliseconds,
                    ok,
                    probe
                );
            }
        });

    // Work that outlives the request must not carry its HttpContext along:
    // ASP.NET Core reuses that object for the next request, and the source
    // decorator reads the chosen source id from it.
    private static void RunDetached(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(work);
        }
    }

    private async Task WarmPlaybackDataAsync(BaseItem item, Guid userId, Guid itemId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return;
        }

        var sources = await _mediaSourceManager
            .GetPlaybackMediaSources(item, user, true, false, CancellationToken.None)
            .ConfigureAwait(false);

        // Same real bottleneck class as the probe above: a real Play
        // still hits SubtitleController's own Stream.vtt endpoint the
        // moment a reader picks a track, which blocks on
        // ISubtitleEncoder.GetSubtitles actually extracting/converting
        // that stream from a remote debrid/torrent source (or the
        // source's own embedded PGS/ASS track) the first time it is asked
        // for - confirmed directly against SubtitleEncoder.cs's own
        // on-disk cache (GetSubtitleCachePath + File.Exists), not
        // guessed. Warming English/German here means a reader who picks
        // either loads from that same cache instead of paying for the
        // real conversion at the moment they are actually waiting on it.
        foreach (var source in sources)
        {
            var subtitleStreams = (source.MediaStreams ?? [])
                .Where(s =>
                    s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle
                    && s.IsTextSubtitleStream
                    && !string.IsNullOrEmpty(s.Language)
                    && PrewarmSubtitleLanguages.Contains(s.Language, StringComparer.OrdinalIgnoreCase)
                )
                .ToList();

            foreach (var subtitleStream in subtitleStreams)
            {
                try
                {
                    await using var stream = await _subtitleEncoder
                        .GetSubtitles(
                            item,
                            source.Id,
                            subtitleStream.Index,
                            "vtt",
                            0,
                            0,
                            false,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Prefetch subtitle warm failed for {ItemId} stream {Index}",
                        itemId,
                        subtitleStream.Index
                    );
                }
            }
        }
    }

    // A search result's own item id (gelato/search/movie|series's own
    // stremioUri.ToGuid(), Filters/SearchActionFilter.cs's real
    // counterpart) never exists in the library at all until it is opened
    // for the first time - InsertActionFilter.cs normally does exactly
    // this insert synchronously, blocking the very first detail-page
    // open on a real Stremio meta fetch (GetMetaAsync) plus TMDb digital-
    // release-date enrichment and an image download, all before the page
    // can render anything at all. Same real bottleneck class as the
    // stream-sync half of this endpoint above, just one step earlier in
    // the pipeline: a poster's own focus can warm this too, well before
    // Select ever needs it.
    //
    // Reuses InsertActionFilter.InsertMetaAsync directly (it is a plain
    // injectable singleton, not something only the MVC filter pipeline
    // can reach) rather than duplicating its logic. The resulting real
    // item's Id will not equal itemId (Jellyfin's own
    // GetNewItemId(path, type) decides that, not the search result's own
    // synthetic guid - InsertActionFilter.cs's own meta.Guid assignment
    // is dead code, confirmed nothing reads it), but that does not
    // matter: a real open still routes through InsertActionFilter again,
    // and its own FindExistingItem provider-id lookup finds this
    // already-inserted item and redirects to it without re-fetching
    // anything, the same fast path a duplicate real open already takes
    // today.
    private ActionResult PrefetchInsert(Guid itemId, Guid userId)
    {
        var stremioMeta = _gelatoManager.GetStremioMeta(itemId);
        if (stremioMeta is null)
        {
            return NotFound();
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Unauthorized();
        }

        RunDetached(() =>
            WarmLoggedAsync(itemId, false, () => InsertForPrefetchAsync(itemId, userId, stremioMeta, user))
        );

        return Accepted();
    }

    private async Task<BaseItem?> InsertForPrefetchAsync(
        Guid itemId,
        Guid userId,
        StremioMeta stremioMeta,
        User user
    )
    {
        try
        {
            var isSeries = stremioMeta.Type == StremioMediaType.Series;
            var root = isSeries
                ? _gelatoManager.TryGetSeriesFolder(userId)
                : _gelatoManager.TryGetMovieFolder(userId);
            if (root is null)
            {
                return null;
            }

            if (
                _gelatoManager.IntoBaseItem(stremioMeta) is { } candidate
                && _gelatoManager.FindExistingItem(candidate, user) is { } existing
            )
            {
                // Something else (a real open, a duplicate prefetch) already
                // inserted this between the card rendering and this firing.
                return existing;
            }

            var cfg = GelatoPlugin.Instance!.GetConfig(userId);
            var meta = await cfg
                .Stremio.GetMetaAsync(stremioMeta.ImdbId ?? stremioMeta.Id, stremioMeta.Type)
                .ConfigureAwait(false);
            if (meta is null)
            {
                return null;
            }

            // Nobody waits on a prefetch: its insert queues behind the other
            // background writers (a series builds its whole episode tree).
            BaseItem? inserted;
            using (
                await _writeGate
                    .EnterAsync("Prefetch insert", CancellationToken.None, heavy: isSeries)
                    .ConfigureAwait(false)
            )
            {
                inserted = await _insertActionFilter
                    .InsertMetaAsync(itemId, root, meta, user)
                    .ConfigureAwait(false);
            }
            if (inserted is not null)
            {
                _gelatoManager.RemoveStremioMeta(itemId);
            }
            return inserted;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Prefetch insert failed for {ItemId}", itemId);
            return null;
        }
    }

    // Real bottleneck: SearchActionFilter.cs intercepts the native
    // GetItems search action and waits on Task.WhenAll(movie search,
    // series search) before answering at all - a reader typing a query
    // that matches both sections waits for whichever of the two takes
    // longer, even though search.js's own results screen already renders
    // Movies and Series as two independent sections once the combined
    // answer lands. These two endpoints expose that exact same per-type
    // search (same GelatoStremioProvider.SearchAsync call, same unreleased
    // filter, same meta-to-BaseItemDto conversion SearchActionFilter.cs's
    // own ConvertMetasToDtos already does) as two requests that resolve
    // independently, so a client can fire both in parallel and paint
    // whichever section's real addon round trip finishes first the
    // moment it lands, matching Nuvio's own incremental-by-source search
    // UI rather than Gelato's own previous all-or-nothing wait.
    //
    // Deliberately does not touch SearchActionFilter.cs or the native
    // /Items endpoint it intercepts: other real Jellyfin clients still
    // calling that endpoint directly keep getting the exact same combined
    // answer they always have, this only adds a second, opt-in way in for
    // callers (Jellio's own search screen) that want the two halves apart.
    [HttpGet("search/movie")]
    [Authorize]
    public Task<ActionResult<IReadOnlyList<BaseItemDto>>> SearchMovies([FromQuery] string q) =>
        SearchByTypeAsync(q, StremioMediaType.Movie);

    [HttpGet("search/series")]
    [Authorize]
    public Task<ActionResult<IReadOnlyList<BaseItemDto>>> SearchSeries([FromQuery] string q) =>
        SearchByTypeAsync(q, StremioMediaType.Series);

    private async Task<ActionResult<IReadOnlyList<BaseItemDto>>> SearchByTypeAsync(
        string q,
        StremioMediaType mediaType
    )
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Ok(Array.Empty<BaseItemDto>());
        }

        if (!HttpContext.TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (cfg.DisableSearch || !await cfg.Stremio.IsReady().ConfigureAwait(false))
        {
            return Ok(Array.Empty<BaseItemDto>());
        }

        var folder =
            mediaType == StremioMediaType.Movie
                ? cfg.MovieFolder ?? _gelatoManager.TryGetMovieFolder(userId)
                : cfg.SeriesFolder ?? _gelatoManager.TryGetSeriesFolder(userId);
        if (folder is null)
        {
            _log.LogWarning(
                "SearchByType: no {MediaType} folder found, please add your gelato path to a library and rescan.",
                mediaType
            );
            return Ok(Array.Empty<BaseItemDto>());
        }

        var metas = (await cfg.Stremio.SearchAsync(q, mediaType).ConfigureAwait(false)).ToList();

        if (cfg.FilterUnreleased)
        {
            metas = metas.Where(m => m.IsReleased(cfg.FilterUnreleasedBufferDays)).ToList();
        }

        var options = new DtoOptions { EnableImages = true, EnableUserData = false };
        var dtos = new List<BaseItemDto>(metas.Count);
        foreach (var meta in metas)
        {
            var baseItem = _gelatoManager.IntoBaseItem(meta);
            if (baseItem is null)
            {
                continue;
            }

            var dto = _dtoService.GetBaseItemDto(baseItem, options);
            var stremioUri = StremioUri.FromBaseItem(baseItem);
            if (stremioUri is null)
            {
                continue;
            }

            dto.Id = stremioUri.ToGuid();
            dtos.Add(dto);
            _gelatoManager.SaveStremioMeta(dto.Id, meta);
        }

        _log.LogInformation(
            "SearchByType \"{Query}\" type={MediaType} results={Results}",
            q,
            mediaType,
            dtos.Count
        );

        return Ok(dtos);
    }

    [HttpGet("meta/{stremioMetaType}/{Id}")]
    [Authorize]
    public async Task<ActionResult<StremioMeta>> GelatoMeta(
        [FromRoute, Required] StremioMediaType stremioMetaType,
        [FromRoute, Required] string id
    )
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
        var meta = await cfg.Stremio.GetMetaAsync(id, stremioMetaType);
        if (meta is null)
        {
            return NotFound();
        }
        return meta;
    }

    // [HttpGet("catalogs")]
    // Moved to CatalogController

    /// <summary>
    /// Reports whether this install can survive an upgrade to Jellyfin 12.
    /// </summary>
    /// <remarks>
    /// Jellyfin 12's MigrateLinkedChildren migration deletes every non-folder item whose path
    /// is not under a library location. Gelato addresses its items by gelato:// and https://
    /// URLs, so all of them qualify. The migration skips that cleanup when any library location
    /// is missing or empty, and Gelato empties its own folders on shutdown to trigger that — but
    /// only the seed stub is removed, so anything else in the folder (a NAS "@eaDir", a
    /// "Thumbs.db", or real media sharing the folder) keeps it non-empty and the library is
    /// destroyed on the first Jellyfin 12 start.
    /// </remarks>
    private const int BlockerSampleSize = 5;

    /// <summary>The Jellyfin major version whose first start prunes URL-backed items.</summary>
    private const int JellyfinMajorThatPrunes = 12;

    [HttpGet("upgrade-readiness")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<UpgradeReadiness> GetUpgradeReadiness()
    {
        // Once the server is on Jellyfin 12 the migration has already run, so there is nothing to
        // be ready for. The same code ships in the Jellyfin 12 build, where the banner would
        // otherwise keep announcing readiness for an upgrade that has already happened.
        if (_appHost.ApplicationVersion.Major >= JellyfinMajorThatPrunes)
        {
            return new UpgradeReadiness { Applies = false, Ready = true };
        }

        var cfg = GelatoPlugin.Instance!.Configuration;
        var folders = new List<UpgradeReadinessFolder>();

        foreach (var library in cfg.GetLibraryPaths())
        {
            var path = library.Path;
            var folder = new UpgradeReadinessFolder { Label = library.Label, Path = path };

            try
            {
                if (!Directory.Exists(path))
                {
                    // A missing folder already counts as inaccessible to the migration.
                    folder.WillBeEmpty = true;
                }
                else
                {
                    // Only ever look at a handful of entries: this folder may be shared with a
                    // real media library holding thousands of files, and naming them all would
                    // be useless in the UI and slow to enumerate over a network mount.
                    // Only the stub Gelato wrote is removed on shutdown; a "stub.txt" with other
                    // content stays behind and blocks like any other file.
                    var sample = Directory
                        .EnumerateFileSystemEntries(path)
                        .Where(entry => !GelatoManager.IsSeedFile(entry))
                        .Select(entry => Path.GetFileName(entry))
                        .Where(name => !string.IsNullOrEmpty(name))
                        .Take(BlockerSampleSize + 1)
                        .ToList();

                    folder.WillBeEmpty = sample.Count == 0;
                    folder.HasMoreBlockers = sample.Count > BlockerSampleSize;
                    folder.Blockers = sample
                        .Take(BlockerSampleSize)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                // Detail stays in the log; the response carries no exception text.
                _log.LogWarning(ex, "Could not inspect {Path} for upgrade readiness", path);
                folder.Error = "could not be read, check the Jellyfin log";
            }

            folders.Add(folder);
        }

        // One empty library location is enough: the migration's guard is global.
        return new UpgradeReadiness
        {
            Applies = true,
            Ready = folders.Exists(f => f.WillBeEmpty),
            Folders = folders,
        };
    }


    [HttpGet("subtitles/{itemId:guid}")]
    public ActionResult<IEnumerable<StremioSubtitle>> GetSubtitles(
        [FromRoute, Required] Guid itemId
    )
    {
        var subs = _gelatoManager.GetStremioSubtitlesCache(itemId);
        return Ok(subs ?? new List<StremioSubtitle>());
    }

    [HttpGet("stream")]
    public async Task<IActionResult> TorrentStream(
        [FromQuery] string ih,
        [FromQuery] int? idx,
        [FromQuery] string? filename,
        [FromQuery] string? trackers
    )
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (
            remoteIp == null
            || !(
                IPAddress.IsLoopback(remoteIp)
                || remoteIp.Equals(HttpContext.Connection.LocalIpAddress)
            )
        )
            return Forbid();

        if (string.IsNullOrWhiteSpace(ih))
            return BadRequest("Missing ?ih=<infohash or magnet>");

        var ct = HttpContext.RequestAborted;

        var settings = new EngineSettingsBuilder
        {
            MaximumConnections = 40,
            MaximumDownloadRate = GelatoPlugin.Instance!.Configuration.P2PDLSpeed,
            MaximumUploadRate = GelatoPlugin.Instance.Configuration.P2PULSpeed,
        }.ToSettings();

        var engine = new ClientEngine(settings);

        var infoHashes =
            TryParseInfoHashes(ih)
            ?? throw new ArgumentException("Invalid infohash or magnet.", nameof(ih));
        var announce = ParseTrackers(trackers) ?? DefaultTrackers();
        var magnet = new MagnetLink(infoHashes, name: null, announceUrls: announce);

        var manager = await engine.AddStreamingAsync(magnet, _downloadPath);
        await manager.StartAsync();

        if (!manager.HasMetadata)
        {
            while (!manager.HasMetadata && !ct.IsCancellationRequested)
                await Task.Delay(100, ct);

            if (!manager.HasMetadata)
                return StatusCode(503, "Metadata not yet available.");
        }

        var selected =
            idx is { } i and >= 0 && i < manager.Files.Count
                ? manager.Files[i]
                : (
                    !string.IsNullOrWhiteSpace(filename)
                        ? manager.Files.FirstOrDefault(x =>
                            x.Path.EndsWith(filename, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(
                                Path.GetFileName(x.Path),
                                filename,
                                StringComparison.OrdinalIgnoreCase
                            )
                        ) ?? PickHeuristic(manager)
                        : PickHeuristic(manager)
                );

        var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = new Timer(
            _ =>
            {
                _log.LogDebug(
                    "file: {File}, progress: {Progress:0.00}%, dl: {DL}/s, ul: {UL}/s, peers: {Peers}, seeds: {Seeds}, leechers: {Leechs}, bytes: {Bytes}",
                    selected.Path,
                    manager.Progress,
                    manager.Monitor.DownloadRate,
                    manager.Monitor.UploadRate,
                    manager.Peers.Available,
                    manager.Peers.Seeds,
                    manager.Peers.Leechs,
                    manager.Monitor.DataBytesReceived
                );
            },
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(10)
        );

        _log.LogInformation($"starting torrent stream for {selected.Path}");
        var stream = await manager.StreamProvider.CreateStreamAsync(selected, ct);

        // Register cleanup for both normal completion and cancellation
        ct.Register(() =>
        {
            _log.LogInformation("Client disconnected. Cleaning up resources...");
            try
            {
                timerCts.Cancel();
            }
            catch
            {
                // ignored
            }

            try
            {
                timer.Dispose();
            }
            catch
            {
                // ignored
            }

            try
            {
                manager.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // ignored
            }

            try
            {
                engine.Dispose();
            }
            catch
            {
                // ignored
            }
        });

        Response.Headers.AcceptRanges = "bytes";
        return File(stream, GuessContentType(selected.Path), enableRangeProcessing: true);
    }

    private static ITorrentManagerFile PickHeuristic(TorrentManager manager)
    {
        return manager.Files.OrderByDescending(LikelyVideo).ThenByDescending(f => f.Length).First();

        static bool LikelyVideo(ITorrentManagerFile f)
        {
            var name = Path.GetFileName(f.Path);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (name.Contains("sample", StringComparison.OrdinalIgnoreCase))
                return false;
            if (
                ext
                is ".srt"
                    or ".ass"
                    or ".ssa"
                    or ".sub"
                    or ".idx"
                    or ".nfo"
                    or ".txt"
                    or ".jpg"
                    or ".jpeg"
                    or ".png"
                    or ".gif"
            )
                return false;
            return ext
                is ".mkv"
                    or ".mp4"
                    or ".m4v"
                    or ".avi"
                    or ".mov"
                    or ".wmv"
                    or ".ts"
                    or ".m2ts";
        }
    }

    private static InfoHashes? TryParseInfoHashes(string s)
    {
        s = s.Trim();

        if (Regex.IsMatch(s, "^[A-Fa-f0-9]{40}$"))
            return InfoHashes.FromInfoHash(InfoHash.FromHex(s));

        if (Regex.IsMatch(s, "^[A-Z2-7=]+$", RegexOptions.IgnoreCase))
            return InfoHashes.FromInfoHash(InfoHash.FromBase32(s));

        if (Regex.IsMatch(s, "^[A-Fa-f0-9]{64}$"))
            return InfoHashes.FromInfoHash(InfoHash.FromHex(s));

        if (MagnetLink.TryParse(s, out var m))
            return m.InfoHashes;

        return null;
    }

    private static string[]? ParseTrackers(string? trackers) =>
        string.IsNullOrWhiteSpace(trackers)
            ? null
            : Uri.UnescapeDataString(trackers)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] DefaultTrackers() =>
        [
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://explodie.org:6969/announce",
            "udp://tracker.openbittorrent.com:6969/announce",
        ];

    private static string GuessContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".ts" or ".m2ts" => "video/mp2t",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            _ => "application/octet-stream",
        };
    }
}

public sealed class UpgradeReadiness
{
    /// <summary>Gets or sets a value indicating whether the check applies at all; false once the server is on Jellyfin 12.</summary>
    public bool Applies { get; set; }

    public bool Ready { get; set; }

    public IReadOnlyList<UpgradeReadinessFolder> Folders { get; set; } = [];
}

public sealed class UpgradeReadinessFolder
{
    public string Label { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool WillBeEmpty { get; set; }

    /// <summary>Gets or sets a short sample of blocking entries, never the whole folder.</summary>
    public IReadOnlyList<string> Blockers { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether more blockers exist than are listed.</summary>
    public bool HasMoreBlockers { get; set; }

    public string? Error { get; set; }
}
