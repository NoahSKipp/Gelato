using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Reflection;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using Gelato.Providers;
using Gelato.Services;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class MediaSourceManagerDecorator(
    IMediaSourceManager inner,
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    IHttpContextAccessor http,
    GelatoItemRepository repo,
    IDirectoryService directoryService,
    IServerConfigurationManager config,
    //Lazy<ISubtitleManager> subtitleManager,
    Lazy<GelatoManager> manager,
    Lazy<SubtitleProvider> subtitleProvider,
    IMediaSegmentManager mediaSegmentManager,
    IEnumerable<ICustomMetadataProvider<Video>> videoProbeProviders
) : IMediaSourceManager
{
    private readonly IMediaSourceManager _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ILogger<MediaSourceManagerDecorator> _log =
        log ?? throw new ArgumentNullException(nameof(log));
    private readonly IHttpContextAccessor _http =
        http ?? throw new ArgumentNullException(nameof(http));
    private readonly KeyLock _lock = new();
    private readonly IMediaSegmentManager _mediaSegmentManager =
        mediaSegmentManager ?? throw new ArgumentNullException(nameof(mediaSegmentManager));
    private readonly ILibraryManager _libraryManager =
        libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
    private readonly IServerConfigurationManager _config =
        config ?? throw new ArgumentNullException(nameof(config));
    private readonly Lazy<GelatoManager> _manager = manager;
    private readonly Lazy<SubtitleProvider> _subtitleProvider = subtitleProvider;

    //  private readonly Lazy<ISubtitleManager> _subtitleManager = subtitleManager ?? throw new ArgumentNullException(nameof(subtitleManager));
    private readonly ICustomMetadataProvider<Video>? _probeProvider =
        videoProbeProviders.FirstOrDefault(p => p.Name == "Probe Provider");

    // Item details only list the first sources unless the caller asks for
    // MediaSources explicitly (Jellio's stream picker does).
    private const int DetailSourceCap = 15;
    private static readonly TimeSpan StreamInfoTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<
        Guid,
        (long Saved, DateTime At, MediaStream[] Streams, MediaAttachment[] Attachments)
    > _streamInfo = new();

    private static readonly Func<object, object> ShallowClone =
        (Func<object, object>)
            Delegate.CreateDelegate(
                typeof(Func<object, object>),
                typeof(object).GetMethod(
                    "MemberwiseClone",
                    BindingFlags.Instance | BindingFlags.NonPublic
                )!
            );

    private sealed class SourceSlot
    {
        public MediaSourceInfo? Info;
        public Video? Stream;
        public Dictionary<string, JsonElement>? Data;
        public string? Path;
        public string Id = "";
    }

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User? user = null
    )
    {
        var sw = Stopwatch.StartNew();
        var slots = ResolveSlots(item, enablePathSubstitution, user, out var timings);
        if (slots is null)
            return _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

        var ctx = _http.HttpContext;
        var take = slots.Count;
        if (
            take > DetailSourceCap
            && ctx?.Request.Method == "GET"
            && ctx.GetActionName() is "GetItem" or "GetItemLegacy"
            && !(
                ctx.Request.Query.TryGetValue("fields", out var fields)
                && fields.ToString().Contains("MediaSources", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            take = DetailSourceCap;
        }

        var built = slots.Take(take).Select(s => Materialize(s, item, user, true)).ToList();
        built[0].Type = MediaSourceType.Default;
        built[0].Id = item.Id.ToString("N");
        LogTimings("GetStaticMediaSources", item, built.Count, slots.Count, timings, sw);
        return built;
    }

    private MediaSourceInfo? GetStaticMediaSource(
        BaseItem item,
        bool enablePathSubstitution,
        User? user,
        Guid? mediaSourceId
    )
    {
        var sw = Stopwatch.StartNew();
        var slots = ResolveSlots(item, enablePathSubstitution, user, out var timings);
        if (slots is null)
            return null;

        var index = 0;
        if (mediaSourceId is { } id && id != item.Id)
        {
            var target = id.ToString("N");
            index = Math.Max(0, slots.FindIndex(s => s.Id == target));
        }

        var selected = Materialize(slots[index], item, user, false);
        if (index == 0)
        {
            selected.Type = MediaSourceType.Default;
            selected.Id = item.Id.ToString("N");
        }
        LogTimings("GetStaticMediaSource", item, 1, slots.Count, timings, sw);
        return selected;
    }

    private void LogTimings(
        string name,
        BaseItem item,
        int built,
        int total,
        (long Sync, long Query) timings,
        Stopwatch sw
    )
    {
        var level = sw.ElapsedMilliseconds >= 250 ? LogLevel.Information : LogLevel.Trace;
        if (!_log.IsEnabled(level))
            return;
        _log.Log(
            level,
            "{Name} {Id}: {Built}/{Total} sources in {Total}ms (sync {Sync}ms, query {Query}ms, build {Build}ms)",
            name,
            item.Id,
            built,
            total,
            sw.ElapsedMilliseconds,
            timings.Sync,
            timings.Query,
            sw.ElapsedMilliseconds - timings.Sync - timings.Query
        );
    }

    // Returns null when Gelato does not handle the item. Slot 0 is the
    // default source; only the slots a caller needs get built.
    private List<SourceSlot>? ResolveSlots(
        BaseItem item,
        bool enablePathSubstitution,
        User? user,
        out (long Sync, long Query) timings
    )
    {
        timings = (0, 0);
        var manager = _manager.Value;
        var ctx = _http.HttpContext;
        Guid userId;
        if (user != null)
        {
            userId = user.Id;
        }
        else
        {
            ctx.TryGetUserId(out userId);
        }

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (
            (!cfg.EnableMixed && !IsGelatoPlaybackItem(item))
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
        )
        {
            return null;
        }

        var uri = StremioUri.FromBaseItem(item);
        var allowSync = ctx.IsInsertableAction() && userId != Guid.Empty;
        var video = item as Video;
        var cacheKey = (video?.PrimaryVersionId ?? item.Id).ToString();

        if (userId != Guid.Empty)
        {
            cacheKey = $"{userId.ToString()}:{cacheKey}";
        }

        var sw = Stopwatch.StartNew();
        var streams = LoadStreamItems(item, userId);
        var query = sw.ElapsedMilliseconds;

        if (allowSync && uri is not null && !manager.HasStreamSync(cacheKey))
        {
            // Bug in web UI that calls the detail page twice. So that's why there's a lock.
            var sync = _lock.RunSingleFlightAsync(
                item.Id,
                ct => RefreshStreamsAsync(item, uri, userId, cacheKey, ct)
            );

            if (streams.Count == 0)
            {
                sync.GetAwaiter().GetResult();
                // refresh item
                _libraryManager.GetItemById(item.Id);
                var syncedAt = sw.ElapsedMilliseconds;
                timings.Sync = syncedAt - query;
                streams = LoadStreamItems(item, userId);
                query += sw.ElapsedMilliseconds - syncedAt;
            }
            else
            {
                // Stored streams are served right away; the refresh lands
                // for the next request.
                _log.LogTrace("Serving stored streams for {Id} while refreshing", item.Id);
            }
        }

        timings.Query = query;

        var slots = _inner
            .GetStaticMediaSources(item, enablePathSubstitution, user)
            .Select(s => new SourceSlot { Info = s, Path = s.Path, Id = s.Id ?? "" })
            .ToList();
        slots.AddRange(
            streams.Select(s => new SourceSlot
            {
                Stream = s.Item,
                Data = s.Data,
                Path = s.Item.Path,
                Id = s.Item.Id.ToString("N", CultureInfo.InvariantCulture),
            })
        );

        if (slots.Count > 1)
        {
            // remove primary from list when there are streams
            slots = slots
                .Where(k =>
                    !(k.Path?.StartsWith("gelato", StringComparison.OrdinalIgnoreCase) ?? false)
                    && !(k.Path?.StartsWith("stremio", StringComparison.OrdinalIgnoreCase) ?? false)
                )
                .ToList();
        }

        // failsafe. mediasources cannot be null
        if (slots.Count == 0)
        {
            slots.Add(new SourceSlot { Info = GetVersionInfo(item, MediaSourceType.Default, user) });
        }

        return slots;
    }

    private async Task RefreshStreamsAsync(
        BaseItem item,
        StremioUri uri,
        Guid userId,
        string cacheKey,
        CancellationToken ct
    )
    {
        _log.LogDebug("GetStaticMediaSources refreshing streams for {Id}", item.Id);

        // Prewarm subtitle cache in the background if Gelato Subtitles
        // is enabled for this library.
        var libraryOptions = _libraryManager.GetLibraryOptions(item);
        var subtitlePrewarmEnabled =
            libraryOptions.SubtitleDownloadLanguages?.Length > 0
            && !libraryOptions.DisabledSubtitleFetchers.Contains(
                "Gelato Subtitles",
                StringComparer.OrdinalIgnoreCase
            );

        if (subtitlePrewarmEnabled)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _subtitleProvider
                        .Value.GetSubtitlesAsync(uri.ExternalId, uri.MediaType, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Subtitle prewarm failed for {Uri}", uri);
                }
            });
        }

        try
        {
            var count = await _manager.Value.SyncStreams(item, userId, ct).ConfigureAwait(false);
            if (count > 0)
            {
                _manager.Value.SetStreamSync(cacheKey);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to sync streams");
        }
    }

    private List<(Video Item, Dictionary<string, JsonElement>? Data)> LoadStreamItems(
        BaseItem item,
        Guid userId
    )
    {
        // we dont use jellyfins alternate versions crap. So we have to load it ourselves
        InternalItemsQuery query;
        if (item is Episode episode)
        {
            query = new InternalItemsQuery
            {
                IncludeItemTypes = [item.GetBaseItemKind()],
                ParentId = episode.SeasonId,
                Recursive = false,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
                Tags = [GelatoManager.StreamTag],
                IndexNumber = episode.IndexNumber,
            };
        }
        else
        {
            var associationUri = StremioUri.FromBaseItem(item);
            if (associationUri is null)
            {
                _log.LogTrace("No Stremio URI found for movie {ItemId}", item.Id);
                return [];
            }

            query = new InternalItemsQuery
            {
                IncludeItemTypes = [item.GetBaseItemKind()],
                HasAnyProviderId = new Dictionary<string, string>
                {
                    { "Stremio", associationUri.ExternalId },
                },
                Recursive = false,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
                Tags = [GelatoManager.StreamTag],
            };
        }

        var list = repo.GetItemList(query)
            .OfType<Video>()
            .Where(x => x.IsGelato())
            .Select(x => (Item: x, Data: x.ReadGelatoData()))
            .Where(x =>
                userId == Guid.Empty
                || (Read<List<Guid>>(x.Data, "userIds")?.Contains(userId) ?? false)
            )
            .OrderBy(x => Read<int?>(x.Data, "index") ?? int.MaxValue)
            .ToList();

        _log.LogTrace("Found {Count} streams for {Id} user {UserId}", list.Count, item.Id, userId);
        return list;
    }

    private static T? Read<T>(Dictionary<string, JsonElement>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var el))
            return default;
        try
        {
            return el.Deserialize<T>();
        }
        catch
        {
            return default;
        }
    }

    private MediaSourceInfo Materialize(SourceSlot slot, BaseItem item, User? user, bool listing)
    {
        if (slot.Info is not null)
            return slot.Info;

        var info = GetVersionInfo(slot.Stream!, MediaSourceType.Grouping, user, slot.Data, listing);
        if (user is not null)
        {
            _inner.SetDefaultAudioAndSubtitleStreamIndices(item, info, user);
        }
        return info;
    }

    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
    {
        _inner.AddParts(providers);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
    {
        return _inner.GetMediaStreams(itemId);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
    {
        return _inner.GetMediaStreams(query).ToList();
    }

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) =>
        _inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) =>
        _inner.GetMediaAttachments(query);

    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken ct
    )
    {
        if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
        {
            return await _inner
                .GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct)
                .ConfigureAwait(false);
        }

        var ctx = _http.HttpContext;

        Guid? mediaSourceId =
            ctx?.Items.TryGetValue("MediaSourceId", out var idObj) == true
            && idObj is string idStr
            && Guid.TryParse(idStr, out var fromCtx)
                ? fromCtx
                : (item.IsPrimaryVersion() ? item.Id : null);

        _log.LogTrace(
            "GetPlaybackMediaSources {ItemId} mediaSourceId={MediaSourceId}",
            item.Id,
            mediaSourceId
        );

        // Only the selected source is built; listing all of them cost one
        // stream and attachment query per stored stream.
        var selected = GetStaticMediaSource(item, enablePathSubstitution, user, mediaSourceId);
        if (selected is null)
        {
            return await _inner
                .GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct)
                .ConfigureAwait(false);
        }

        var owner = ResolveOwnerFor(selected, item);
        if (!IsGelatoPlaybackItem(owner))
        {
            return await _inner
                .GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct)
                .ConfigureAwait(false);
        }

        if (owner.IsPrimaryVersion() && owner.Id != item.Id)
        {
            selected = GetStaticMediaSource(owner, enablePathSubstitution, user, mediaSourceId);
            if (selected is null)
                return [];
        }

        if (NeedsProbe(selected))
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(owner);

            var segmentTask = _mediaSegmentManager.RunSegmentPluginProviders(
                owner,
                libraryOptions,
                false,
                ct
            );
            var metadataTask = ProbeStreamAsync((Video)owner, selected.Path, ct);
            //  var subtitleTask = DownloadSubtitles((Video)owner, ct);

            await Task.WhenAll(metadataTask, segmentTask).ConfigureAwait(false);

            await owner
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);

            _streamInfo.TryRemove(owner.Id, out _);
            selected =
                GetStaticMediaSource(item, enablePathSubstitution, user, mediaSourceId) ?? selected;
        }

        if (item.RunTimeTicks is null && selected.RunTimeTicks is not null)
        {
            item.RunTimeTicks = selected.RunTimeTicks;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);
        }

        // Stub path after probing is done so the real URL is never sent to clients.
        // Force File protocol so clients proxy through Jellyfin instead of direct-playing.
        //
        // Matched by route+method rather than the controller action's literal name:
        // that name is Jellyfin core's own internal identifier and has silently changed
        // across major versions before (see the Legacy-suffixed actions elsewhere in this
        // repo). A mismatch here means the transcoding-decision engine sees an unstubbed
        // remote HTTP source with SupportsDirectPlay=true and skips transcoding entirely -
        // exactly the "every stream serves a raw MKV to the browser" regression this
        // guarded against once already.
        var isPlaybackInfoPost =
            string.Equals(ctx?.Request.Method, "POST", StringComparison.OrdinalIgnoreCase)
            && (ctx?.Request.Path.Value?.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) ?? false);

        if (isPlaybackInfoPost != (ctx.GetActionName() == "GetPostedPlaybackInfo"))
        {
            _log.LogWarning(
                "PlaybackInfo stub gate mismatch: route-based={RouteBased} actionName-based={ActionBased} action={Action} path={Path}",
                isPlaybackInfoPost,
                ctx.GetActionName() == "GetPostedPlaybackInfo",
                ctx.GetActionName(),
                ctx?.Request.Path.Value
            );
        }

        if (isPlaybackInfoPost)
        {
            selected.Path = "/stub";
            selected.IsRemote = false;
            selected.Protocol = MediaProtocol.File;
        }

        return [selected];

        static bool NeedsProbe(MediaSourceInfo s) =>
            (s.MediaStreams?.All(ms => ms.Type != MediaStreamType.Video) ?? true)
            || (s.RunTimeTicks ?? 0) < TimeSpan.FromMinutes(2).Ticks;

        BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
            Guid.TryParse(s.ETag, out var g) ? libraryManager.GetItemById(g) ?? fallback : fallback;
    }

    private static bool IsGelatoPlaybackItem(BaseItem item) =>
        item.HasStreamTag()
        || (item.Path?.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase) ?? false);

    public Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string mediaSourceId,
        string? liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken
    ) =>
        _inner.GetMediaSource(
            item,
            mediaSourceId,
            liveStreamId,
            enablePathSubstitution,
            cancellationToken
        );

    public async Task<LiveStreamResponse> OpenLiveStream(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    ) => await _inner.OpenLiveStream(request, cancellationToken);

    public async Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    ) => await _inner.OpenLiveStreamInternal(request, cancellationToken);

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) =>
        _inner.GetLiveStream(id, cancellationToken);

    public Task<
        Tuple<MediaSourceInfo, IDirectStreamProvider>
    > GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) =>
        _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    public ILiveStream GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) =>
        _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    public async Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
        ActiveRecordingInfo info,
        CancellationToken cancellationToken
    ) => await _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);

    public async Task<MediaSourceInfo> GetLiveStreamMediaInfo(
        string id,
        CancellationToken cancellationToken
    ) => await _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    public bool SupportsDirectStream(string path, MediaProtocol protocol) =>
        _inner.SupportsDirectStream(path, protocol);

    public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);

    public void SetDefaultAudioAndSubtitleStreamIndices(
        BaseItem item,
        MediaSourceInfo source,
        User user
    ) => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    public Task AddMediaInfoWithProbe(
        MediaSourceInfo mediaSource,
        bool isAudio,
        string cacheKey,
        bool addProbeDelay,
        bool isLiveStream,
        CancellationToken cancellationToken
    ) =>
        _inner.AddMediaInfoWithProbe(
            mediaSource,
            isAudio,
            cacheKey,
            addProbeDelay,
            isLiveStream,
            cancellationToken
        );

    private MediaSourceInfo GetVersionInfo(
        BaseItem item,
        MediaSourceType type,
        User? user = null,
        Dictionary<string, JsonElement>? data = null,
        bool listing = false
    )
    {
        ArgumentNullException.ThrowIfNull(item);

        data ??= item.ReadGelatoData();
        var streamName = Read<string>(data, "name");
        var streamDesc = Read<string>(data, "description");
        var richName = !string.IsNullOrEmpty(streamDesc)
            ? $"{streamName}\n{streamDesc}"
            : streamName;

        // Only ever true when AIOStreams' PROVIDE_STREAM_DATA setting is on
        // (GelatoManager.SyncStreams only writes this GelatoData key then).
        // MediaSourceInfo has no field of its own for "this is already a
        // debrid-cached hit, no real download wait ahead" - Formats is the
        // one real field here EncodingHelper.cs never reads for an actual
        // ffmpeg/transcode decision (only ever written from real ffprobe
        // results elsewhere, MediaSourceManager.cs/LiveStreamHelper.cs, and
        // never read anywhere else in Jellyfin core), so a synthetic marker
        // riding along in it never reaches the real remote source the way
        // RequiredHttpHeaders would (that one really is forwarded to ffmpeg
        // as literal headers - confirmed directly, not guessed - and would
        // have leaked this straight to a real debrid/usenet origin).
        var isConfirmedCached = Read<bool?>(data, "cached") == true;
        var (mediaStreams, attachments) = GetStreamInfo(item, data, listing);

        var info = new MediaSourceInfo
        {
            Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
            ETag = item.Id.ToString("N", CultureInfo.InvariantCulture),
            Protocol = MediaProtocol.Http,
            MediaStreams = mediaStreams,
            MediaAttachments = attachments,
            Name = richName,
            Path = item.Path,
            RunTimeTicks = item.RunTimeTicks,
            Container = item.Container,
            Size = item.Size,
            Type = type,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            // just always say yes
            HasSegments = true,
            //HasSegments = MediaSegmentManager.HasSegments(item.Id)
            Formats = isConfirmedCached ? ["gelato-cached"] : [],
        };


        if (user is not null)
        {
            info.SupportsTranscoding = user.HasPermission(
                PermissionKind.EnableVideoPlaybackTranscoding
            );
            info.SupportsDirectStream = user.HasPermission(PermissionKind.EnablePlaybackRemuxing);
        }
        if (string.IsNullOrEmpty(info.Path))
        {
            info.Type = MediaSourceType.Placeholder;
        }

        if (item is Video video)
        {
            info.IsoType = video.IsoType;
            info.VideoType = video.VideoType;
            info.Video3DFormat = video.Video3DFormat;
            info.Timestamp = video.Timestamp;
            info.IsRemote = true;

            if (video.IsShortcut)
            {
                info.IsRemote = true;
                info.Path = video.ShortcutPath;
            }
        }

        info.Bitrate = item.TotalBitrate;
        info.InferTotalBitrate();

        return info;
    }

    private static readonly HashSet<string> _subtitleExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "vtt",
        "srt",
        "ass",
        "ssa",
        "sub",
        "idx",
        "smi",
    };

    // Jellyfin's MediaInfoResolver.GetExternalStreamsAsync bails immediately when !video.IsFileProtocol
    // (stream items have http:// paths). This means external subtitle files saved to the internal
    // metadata folder are never discovered during library refresh and never written to the DB.
    // We work around this by scanning the metadata folder ourselves at playback time and merging
    // any matching subtitle files into the DB streams on the fly.
    private (List<MediaStream> Streams, List<MediaAttachment> Attachments) GetStreamInfo(
        BaseItem item,
        Dictionary<string, JsonElement>? data,
        bool listing
    )
    {
        // Probing fills Container/Size/bitrate, so a stream row without them
        // has no stored streams yet. Listings skip the two queries for those;
        // playback always reads the database.
        var probed =
            !string.IsNullOrEmpty(item.Container) || item.Size > 0 || item.TotalBitrate > 0;
        if (listing && !probed)
        {
            return (GetMediaStreamsWithExternalSubs(item, [], data), []);
        }

        var saved = item.DateLastSaved.Ticks;
        if (
            !_streamInfo.TryGetValue(item.Id, out var cached)
            || cached.Saved != saved
            || DateTime.UtcNow - cached.At > StreamInfoTtl
        )
        {
            cached = (
                saved,
                DateTime.UtcNow,
                _inner.GetMediaStreams(item.Id).ToArray(),
                _inner.GetMediaAttachments(item.Id).ToArray()
            );
            _streamInfo[item.Id] = cached;
            if (_streamInfo.Count > 5000)
            {
                foreach (var key in _streamInfo.Where(kv => DateTime.UtcNow - kv.Value.At > StreamInfoTtl).Select(kv => kv.Key).ToList())
                    _streamInfo.TryRemove(key, out _);
            }
        }

        // Copies: playback fills per-request fields (delivery urls) on these.
        return (
            GetMediaStreamsWithExternalSubs(
                item,
                cached.Streams.Select(s => (MediaStream)ShallowClone(s)).ToList(),
                data
            ),
            cached.Attachments.Select(a => (MediaAttachment)ShallowClone(a)).ToList()
        );
    }

    private List<MediaStream> GetMediaStreamsWithExternalSubs(
        BaseItem item,
        List<MediaStream> streams,
        Dictionary<string, JsonElement>? data
    )
    {
        var gelatoFilename = Read<string>(data, "filename");
        if (string.IsNullOrEmpty(gelatoFilename))
            return streams;

        var metaPath = item.GetInternalMetadataPath();
        if (!Directory.Exists(metaPath))
            return streams;

        var baseName = Path.GetFileNameWithoutExtension(gelatoFilename);
        var existingPaths = new HashSet<string>(
            streams.Where(s => s.Path != null).Select(s => s.Path!),
            StringComparer.OrdinalIgnoreCase
        );

        var nextIndex = streams.Count > 0 ? streams.Max(s => s.Index) + 1 : 0;

        foreach (var file in Directory.EnumerateFiles(metaPath))
        {
            var fname = Path.GetFileName(file);

            // Must start with baseName + "."
            if (!fname.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                continue;

            var ext = Path.GetExtension(fname).TrimStart('.');
            if (!_subtitleExtensions.Contains(ext))
                continue;

            if (existingPaths.Contains(file))
                continue;

            // Parse language from suffix: {baseName}.{lang}.{ext} or {baseName}.{lang}.{N}.{ext}
            var suffix = fname.Substring(baseName.Length + 1); // everything after "baseName."
            var parts = Path.GetFileNameWithoutExtension(suffix).Split('.');
            var langCode = parts.Length > 0 ? parts[0] : "und";

            streams.Add(
                new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    IsExternal = true,
                    IsExternalUrl = false,
                    SupportsExternalStream = true,
                    Path = file,
                    Language = langCode,
                    Codec = ext.ToLowerInvariant(),
                    Index = nextIndex++,
                    IsDefault = false,
                    IsForced = false,
                    IsHearingImpaired = false,
                }
            );

            existingPaths.Add(file);
        }

        return streams;
    }

    private async Task ProbeStreamAsync(Video owner, string streamUrl, CancellationToken ct)
    {
        var gelatoFilename = owner.GelatoData<string>("filename");
        var strmBaseName = !string.IsNullOrEmpty(gelatoFilename)
            ? Path.GetFileNameWithoutExtension(gelatoFilename)
            : $"{owner.Id:N}";
        var tmpStrm = Path.Combine(Path.GetTempPath(), $"{strmBaseName}.strm");
        await File.WriteAllTextAsync(tmpStrm, streamUrl, ct).ConfigureAwait(false);

        var origPath = owner.Path;
        var origShortcut = owner.IsShortcut;
        owner.Path = tmpStrm;
        owner.IsShortcut = true;
        owner.DateModified = new FileInfo(tmpStrm).LastWriteTimeUtc;

        try
        {
            _log.LogInformation("Probing stream for {Id} via {Url}", owner.Id, streamUrl);

            var options = new MetadataRefreshOptions(directoryService)
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            };

            if (_probeProvider is not null)
            {
                // Call the ffprobe provider directly instead of going through
                // RefreshMetadata.
                //
                // RefreshMetadata runs the whole metadata pipeline, and with
                // FullRefresh that includes ExecuteRemoteProviders - so every
                // stream probe also re-queried OMDb/TMDb for the item. On a
                // library browsed through Gelato that is a remote metadata
                // lookup per probe, and it is where the recurring
                // "Error in The Open Movie Database" JsonException spam comes
                // from: OMDb returns malformed JSON for some season payloads
                // and the probe drags that call along every time.
                //
                // The probe provider on its own does exactly what is wanted
                // here - read the container's streams - and nothing else. The
                // caller already persists the result with
                // UpdateToRepositoryAsync and runs segment providers itself, so
                // no other part of the pipeline is needed.
                //
                // This field was already injected upstream and never used.
                await _probeProvider.FetchAsync(owner, options, ct).ConfigureAwait(false);
            }
            else
            {
                // No probe provider resolved - fall back to the old path rather
                // than silently skipping the probe.
                _log.LogDebug("No probe provider available, falling back to RefreshMetadata");
                await owner.RefreshMetadata(options, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stream probe failed for {Id}", owner.Id);
        }
        finally
        {
            owner.Path = origPath;
            owner.IsShortcut = origShortcut;
            try
            {
                File.Delete(tmpStrm);
            }
            catch
            { /* best effort */
            }
        }
    }
}
