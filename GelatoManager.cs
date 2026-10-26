using System.Diagnostics;
using System.Globalization;
using Gelato.Config;
using Gelato.Decorators;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gelato;

public sealed class GelatoManager(
    ILoggerFactory loggerFactory,
    IProviderManager provider,
    GelatoItemRepository repo,
    IItemPersistenceService persistence,
    IFileSystem fileSystem,
    IMemoryCache memoryCache,
    IServerConfigurationManager serverConfig,
    ILibraryManager libraryManager,
    IDirectoryService directoryService,
    IApplicationPaths appPaths
)
{
    public const string StreamTag = "gelato-stream";
    private const int MaxStaleDeletesPerSync = 50;
    public const string TreeSyncedTag = "gelato-tree-synced";

    /// <summary>
    /// Marks a movie or series as anime, so clients can keep anime out of
    /// non-anime rows with one tag query instead of guessing.
    /// </summary>
    public const string AnimeTag = "anime";

    private static readonly string[] AnimeProviders = ["AniDB", "Kitsu", "Mal", "Anilist"];

    /// <summary>Name of the file Gelato seeds into its library folders.</summary>
    public const string SeedFileName = "stub.txt";

    /// <summary>
    /// Content of the seed file. Unchanged since the file was introduced, so a stub written by
    /// any earlier Gelato build is recognised as ours.
    /// </summary>
    public const string SeedFileContent =
        "This is a seed file created by Gelato so that library scans are triggered. Do not remove.";

    private readonly ILogger<GelatoManager> _log = loggerFactory.CreateLogger<GelatoManager>();

    private int GetHttpPort()
    {
        var networkConfig = serverConfig.GetNetworkConfiguration();
        return networkConfig.InternalHttpPort;
    }

    public void SetStremioSubtitlesCache(Guid guid, List<StremioSubtitle> subs)
    {
        memoryCache.Set($"subs:{guid}", subs, TimeSpan.FromMinutes(3600));
    }

    public List<StremioSubtitle>? GetStremioSubtitlesCache(Guid guid)
    {
        return memoryCache.Get<List<StremioSubtitle>>($"subs:{guid}");
    }

    public void SetStreamSync(string guid)
    {
        memoryCache.Set(
            $"streamsync:{guid}",
            guid,
            TimeSpan.FromSeconds(GelatoPlugin.Instance!.Configuration.StreamTTL)
        );
    }

    public bool HasStreamSync(string guid)
    {
        return memoryCache.TryGetValue($"streamsync:{guid}", out _);
    }

    public void SaveStremioMeta(Guid guid, StremioMeta meta)
    {
        memoryCache.Set($"meta:{guid}", meta, TimeSpan.FromMinutes(360));
    }

    public StremioMeta? GetStremioMeta(Guid guid)
    {
        return memoryCache.TryGetValue($"meta:{guid}", out var value) ? value as StremioMeta : null;
    }

    public void RemoveStremioMeta(Guid guid)
    {
        memoryCache.Remove($"meta:{guid}");
    }

    public void ClearCache()
    {
        if (memoryCache is MemoryCache cache)
        {
            cache.Compact(1.0);
        }

        _log.LogDebug("Cache cleared");
    }

    private static void SeedFolder(string path)
    {
        Directory.CreateDirectory(path);
        var seed = Path.Combine(path, SeedFileName);
        if (!File.Exists(seed))
        {
            File.WriteAllText(seed, SeedFileContent);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="filePath"/> is a seed file Gelato wrote: same name and
    /// same content. A "stub.txt" holding anything else belongs to someone else.
    /// </summary>
    public static bool IsSeedFile(string filePath)
    {
        if (
            !string.Equals(
                Path.GetFileName(filePath),
                SeedFileName,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return false;
        }

        try
        {
            var info = new FileInfo(filePath);
            // Never read a large file just because it shares the name.
            if (!info.Exists || info.Length > 1024)
                return false;

            return string.Equals(
                File.ReadAllText(filePath).Trim(),
                SeedFileContent,
                StringComparison.Ordinal
            );
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the seed file from <paramref name="folder"/> if it is the one Gelato wrote and
    /// leaves any other "stub.txt" alone. Returns true when the folder holds no seed file
    /// afterwards.
    /// </summary>
    public static bool RemoveSeedFile(string folder, ILogger log)
    {
        var seed = Path.Combine(folder, SeedFileName);
        if (!File.Exists(seed))
            return true;

        if (!IsSeedFile(seed))
        {
            log.LogWarning(
                "Gelato: {Seed} is not the seed file Gelato wrote, leaving it in place; a Jellyfin 12 upgrade may prune Gelato items.",
                seed
            );
            return false;
        }

        File.Delete(seed);
        log.LogInformation(
            "Gelato: removed seed file {Seed} so a Jellyfin 12 upgrade cannot prune the library.",
            seed
        );
        return true;
    }

    public Folder? TryGetMovieFolder(Guid userId)
    {
        return TryGetFolder(
            GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(userId).MoviePath
        );
    }

    public Folder? TryGetSeriesFolder(Guid userId)
    {
        return TryGetFolder(
            GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(userId).SeriesPath
        );
    }

    public Folder? TryGetMovieFolder(PluginConfiguration cfg)
    {
        return TryGetFolder(cfg.MoviePath);
    }

    public Folder? TryGetSeriesFolder(PluginConfiguration cfg)
    {
        return TryGetFolder(cfg.SeriesPath);
    }

    private Folder? TryGetFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        SeedFolder(path);
        return repo.GetItemList(new InternalItemsQuery { IsDeadPerson = true, Path = path })
            .OfType<Folder>()
            .FirstOrDefault();
    }

    private BaseItem? Exist(StremioMeta meta, User? user = null)
    {
        var item = IntoBaseItem(meta);
        if (item?.ProviderIds is { Count: > 0 })
            return FindExistingItem(item, user);
        _log.LogWarning("Gelato: Missing provider ids, skipping");
        return null;
    }

    public BaseItem? FindExistingItem(BaseItem item, User? user = null)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [item.GetBaseItemKind()],
            HasAnyProviderId = item.ProviderIds,
            Recursive = true,
            ExcludeTags = [StreamTag],
            User = user,
            IsDeadPerson = true, // skip filter marker
        };

        return libraryManager
            .GetItemList(query)
            .FirstOrDefault(x =>
            {
                return x switch
                {
                    null => false,
                    Video v => !v.IsStream(),
                    _ => true,
                };
            });
    }

    /// <summary>
    /// Inserts metadata into the library. Skip if it already exists.
    /// </summary>
    public async Task<(BaseItem? Item, bool Created)> InsertMeta(
        Folder parent,
        StremioMeta meta,
        User? user,
        bool allowRemoteRefresh,
        bool refreshItem,
        bool queueRefreshItem,
        CancellationToken ct
    )
    {
        var mediaType = meta.Type;
        BaseItem? existing;

        if (mediaType is not (StremioMediaType.Movie or StremioMediaType.Series))
        {
            _log.LogWarning("type {Type} is not valid, skipping", mediaType);
            return (null, false);
        }
        _log.LogDebug("inserting  {Name}", meta.Name);
        var baseItemKind = mediaType.ToBaseItem();
        var cfg = GelatoPlugin.Instance!.GetConfig(user?.Id ?? Guid.Empty);

        // load in full metadata if needed.
        if (
            allowRemoteRefresh
            && (
                meta.ImdbId is null
                || (
                    baseItemKind == BaseItemKind.Series
                    && (meta.Videos is null || meta.Videos.Count == 0)
                )
            )
        )
        {
            // do a precheck as loading metadata is expensive
            existing = Exist(meta, user);

            if (existing is not null)
            {
                _log.LogDebug(
                    "found existing {Kind}: {Id} for {Name}",
                    existing.GetBaseItemKind(),
                    existing.Id,
                    existing.Name
                );
                if (allowRemoteRefresh)
                    await RefreshReturningSeriesAsync(existing, meta, cfg, ct).ConfigureAwait(false);
                return (existing, false);
            }

            var lookupId = meta.ImdbId ?? meta.Id;
            meta = await cfg.Stremio!.GetMetaAsync(lookupId, mediaType).ConfigureAwait(false);

            if (meta is null)
            {
                _log.LogWarning(
                    "InsertMeta: no aio meta found for {Id} {Type}, maybe try aiometadata as meta addon.",
                    lookupId,
                    mediaType
                );
                return (null, false);
            }

            mediaType = meta.Type;
        }

        if (!meta.IsValid())
        {
            _log.LogWarning(
                "meta for {Id} is not valid {Name} , skipping",
                meta.Id,
                meta.GetName()
            );
            return (null, false);
        }

        if (mediaType is not (StremioMediaType.Movie or StremioMediaType.Series))
        {
            _log.LogWarning("type {Type} is not valid after refresh, skipping", mediaType);
            return (null, false);
        }

        existing = Exist(meta, user);

        if (existing is not null)
        {
            _log.LogDebug(
                "found existing {Kind}: {Id} for {Name}",
                existing.GetBaseItemKind(),
                existing.Id,
                existing.Name
            );
            return (existing, false);
        }

        await EnrichMetaAsync(meta, ct).ConfigureAwait(false);

        if (IntoBaseItem(meta) is not { } baseItem)
        {
            _log.LogWarning("failed to convert meta into base item for {Name}", meta.Name);
            return (null, false);
        }

        if (mediaType == StremioMediaType.Movie)
        {
            baseItem = SaveItem(baseItem, parent);
            if (baseItem is null)
            {
                _log.LogWarning("InsertMeta: failed to create baseItem");
                return (null, false);
            }

            await baseItem
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
        }
        else
        {
            baseItem = await SyncSeriesTreesAsync(cfg, meta, ct).ConfigureAwait(false);
        }

        if (baseItem is null)
        {
            _log.LogWarning("InsertMeta: failed to create {Type} for {Name}", mediaType, meta.Name);
            return (null, false);
        }

        if (refreshItem)
        {
            var options = new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = false,
                ReplaceAllMetadata = false,
                ForceSave = true,
            };

            if (queueRefreshItem)
            {
                provider.QueueRefresh(baseItem.Id, options, RefreshPriority.High);
            }
            else
            {
                _ = provider.RefreshFullItem(baseItem, options, ct);
            }
        }
        _log.LogDebug("inserted new {Kind}: {Name}", baseItem.GetBaseItemKind(), baseItem.Name);
        return (baseItem, true);
    }

    private IEnumerable<BaseItem> FindByProviderIds(
        Dictionary<string, string> providerIds,
        BaseItemKind kind,
        Folder parent
    )
    {
        var q = new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            Recursive = true,
            ParentId = parent.Id,
            HasAnyProviderId = providerIds
                .Where(kvp =>
                    kvp.Key is nameof(MetadataProvider.Tmdb) or nameof(MetadataProvider.Tvdb)
                    || kvp.Key == nameof(MetadataProvider.TvRage)
                    || kvp.Key == "Stremio"
                    || kvp.Key == nameof(MetadataProvider.Imdb)
                )
                .ToDictionary(),
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false,
            // skip filter marker
            IsDeadPerson = true,
        };

        foreach (var item in libraryManager.GetItemList(q))
        {
            yield return item;
        }
    }

    private BaseItem? GetByProviderIds(
        Dictionary<string, string> providerIds,
        BaseItemKind kind,
        Folder parent
    )
    {
        return FindByProviderIds(providerIds, kind, parent).FirstOrDefault();
    }

    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    // Small batches with a pause in between, one writer at a time, so short
    // writes (sessions, user data, Jellio state) get the lock between them.
    public async Task SaveInBatchesAsync<T>(IReadOnlyList<T> items, CancellationToken ct)
        where T : BaseItem
    {
        const int batchSize = 50;
        for (var start = 0; start < items.Count; start += batchSize)
        {
            var batch = items.Skip(start).Take(batchSize).Cast<BaseItem>().ToList();
            await WriteGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                persistence.SaveItems(batch, ct);
            }
            finally
            {
                WriteGate.Release();
            }

            if (start + batchSize < items.Count)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }

    private static string StreamRowState(BaseItem item) =>
        string.Join(
            '\u001f',
            item.ExternalId,
            item.Path,
            item.Name,
            item.ParentId,
            item.RunTimeTicks,
            item.PremiereDate?.Ticks,
            item.IsVirtualItem,
            string.Join(',', item.Tags ?? []),
            string.Join(',', item.ProviderIds.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)),
            (item as Video)?.PrimaryVersionId,
            string.Join(',', item.LockedFields ?? []),
            (item as Video)?.LinkedAlternateVersions?.Length ?? 0
        );

    /// <summary>
    /// Load streams and inserts them into the database keeping original
    /// sorting. We make sure to keep a one stable version based on primaryversionid
    /// </summary>
    /// <returns></returns>
    public async Task<int> SyncStreams(BaseItem item, Guid userId, CancellationToken ct)
    {
        _log.LogDebug($"SyncStreams for {item.Id}");
        var stopwatch = Stopwatch.StartNew();
        if (item is not Video video)
        {
            _log.LogWarning(
                "SyncStreams: item is not a Video type, itemType={ItemType}",
                item.GetType().Name
            );
            return 0;
        }

        if (video.IsStream())
        {
            _log.LogWarning("SyncStreams: item is a stream, skipping");
            return 0;
        }

        var isEpisode = video is Episode;
        var parent = isEpisode ? video.GetParent() as Folder : TryGetMovieFolder(userId);
        if (parent is null)
        {
            _log.LogWarning("SyncStreams: no parent, skipping");
            return 0;
        }

        var uri = StremioUri.FromBaseItem(video);
        if (uri is null)
        {
            _log.LogError($"Unable to build Stremio URI for {video.Name}");
            return 0;
        }

        var streamProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (providerId, value) in video.ProviderIds)
        {
            streamProviderIds[providerId] = value;
        }
        streamProviderIds["Stremio"] = uri.ExternalId;

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var stremio = cfg.Stremio;
        var streams = await stremio.GetStreamsAsync(uri).ConfigureAwait(false);
        var httpPort = GetHttpPort();

        // Filter valid streams
        var acceptable = streams
            .Select(s =>
            {
                if (!s.IsValid())
                {
                    _log.LogWarning("Invalid stream, skipping {StreamName}", s.Name);
                    return null;
                }

                if (!cfg.P2PEnabled && s.IsTorrent())
                {
                    _log.LogDebug($"P2P stream, skipping {s.Name}");
                    return null;
                }

                return s;
            })
            .Where(s => s is not null)
            .ToList();

        // Get existing streams. Same shape as the stream listing in
        // MediaSourceManagerDecorator.GetStaticMediaSources: match on the
        // Stremio id plus the stream tag in SQL, with grouping off. On
        // Jellyfin 12 the old query (all provider ids, filtered in memory
        // through IsStream(), which reads item.Tags) came back without any of
        // the existing stream rows, so stale rows never lost this user and
        // every sync piled another full set on top (300+ per episode, most
        // of them dead links).
        var query =
            video is Episode episodeVideo
                ? new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    ParentId = episodeVideo.SeasonId,
                    Recursive = false,
                    GroupByPresentationUniqueKey = false,
                    GroupBySeriesPresentationUniqueKey = false,
                    CollapseBoxSetItems = false,
                    IsDeadPerson = true,
                    Tags = [StreamTag],
                    IndexNumber = episodeVideo.IndexNumber,
                }
                : new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie],
                    HasAnyProviderId = new Dictionary<string, string>
                    {
                        { "Stremio", uri.ExternalId },
                    },
                    Recursive = false,
                    GroupByPresentationUniqueKey = false,
                    GroupBySeriesPresentationUniqueKey = false,
                    CollapseBoxSetItems = false,
                    IsDeadPerson = true,
                    Tags = [StreamTag],
                };

        var existingStreamItems = repo.GetItemList(query)
            .OfType<Video>()
            .Where(v =>
                v.Id != video.Id
                && string.Equals(
                    v.GetProviderId("Stremio"),
                    uri.ExternalId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

        // Match stream rows by persisted Gelato guid, not by volatile playback URL/path.
        var existingByGuid = new Dictionary<Guid, Video>();
        foreach (var existingItem in existingStreamItems)
        {
            var existingGuid = existingItem.GelatoData<Guid?>("guid");
            if (existingGuid is null || existingGuid == Guid.Empty)
            {
                // Strict guid matching: ignore rows without a persisted guid.
                continue;
            }

            if (!existingByGuid.TryAdd(existingGuid.Value, existingItem))
            {
                // Guard against bad historical data; don't fail sync on collisions.
                _log.LogWarning(
                    "Duplicate stream guid found during sync: {Guid}. Keeping first item id={FirstId}, ignoring item id={SecondId}",
                    existingGuid.Value,
                    existingByGuid[existingGuid.Value].Id,
                    existingItem.Id
                );
            }
        }

        var upsertedStreams = new List<Video>();
        var changedStreams = new List<Video>();

        for (var i = 0; i < acceptable.Count; i++)
        {
            var s = acceptable[i];
            var index = i + 1;
            var path = s.IsFile()
                ? s.Url
                : $"http://127.0.0.1:{httpPort}/gelato/stream?ih={s.InfoHash}"
                    + (s.FileIdx is not null ? $"&idx={s.FileIdx}" : "")
                    + (
                        s.Sources is { Count: > 0 }
                            ? $"&trackers={Uri.EscapeDataString(string.Join(',', s.Sources))}"
                            : ""
                    );

            var streamGuid = s.GetGuid();
            var isNewStreamItem = !existingByGuid.TryGetValue(streamGuid, out var streamItem);
            var before = isNewStreamItem ? null : StreamRowState(streamItem!);

            if (isNewStreamItem)
            {
                streamItem =
                    isEpisode && video is Episode e
                        ? new Episode
                        {
                            //Id = libraryManager.GetNewItemId(path, typeof(Episode)),
                            SeriesId = e.SeriesId,
                            SeriesName = e.SeriesName,
                            SeasonId = e.SeasonId,
                            SeasonName = e.SeasonName,
                            IndexNumber = e.IndexNumber,
                            ParentIndexNumber = e.ParentIndexNumber,
                            PremiereDate = e.PremiereDate,
                        }
                        : new Movie
                        {
                            //Id = libraryManager.GetNewItemId(path, typeof(Movie))
                        };
                streamItem.Path = path;
                streamItem.Id = libraryManager.GetNewItemId(streamItem.Path, streamItem.GetType());
            }

            streamItem.Name = video.Name;
            streamItem.Tags = [StreamTag];

            var locked = streamItem.LockedFields?.ToList() ?? [];
            if (!locked.Contains(MetadataField.Tags))
                locked.Add(MetadataField.Tags);
            streamItem.LockedFields = locked.ToArray();

            streamItem.ProviderIds = streamProviderIds;
            streamItem.RunTimeTicks = video.RunTimeTicks ?? video.RunTimeTicks;
            streamItem.LinkedAlternateVersions = [];
            streamItem.SetPrimaryVersionId(null);
            streamItem.PremiereDate = video.PremiereDate;
            streamItem.Path = path;
            streamItem.IsVirtualItem = false;
            streamItem.SetParent(parent);

            var users = streamItem.GelatoData<List<Guid>>("userIds") ?? [];
            if (!users.Contains(userId))
            {
                users.Add(userId);
                streamItem.SetGelatoData("userIds", users);
            }

            streamItem.SetGelatoData("name", s.Name);
            streamItem.SetGelatoData("description", s.Description);
            if (!string.IsNullOrEmpty(s.BehaviorHints?.BingeGroup))
            {
                streamItem.SetGelatoData("bingeGroup", s.BehaviorHints.BingeGroup);
            }
            if (!string.IsNullOrEmpty(s.BehaviorHints?.Filename))
            {
                streamItem.SetGelatoData("filename", s.BehaviorHints.Filename);
            }
            // Only present with AIOStreams' PROVIDE_STREAM_DATA on; absent
            // (Service null) otherwise, same as every field above it.
            if (s.StreamData?.Service is { } service)
            {
                streamItem.SetGelatoData("cached", service.Cached);
            }
            streamItem.SetGelatoData("index", index);
            streamItem.SetGelatoData("guid", streamGuid);
            // Keep map current so stale detection below uses the final upserted set.
            existingByGuid[streamGuid] = streamItem;

            upsertedStreams.Add(streamItem);
            if (before is null || before != StreamRowState(streamItem))
            {
                changedStreams.Add(streamItem);
            }
        }

        // Unchanged rows are not rewritten: a resync of a 60 stream title used
        // to update every row and hold the write lock for all of them.
        await SaveInBatchesAsync(changedStreams, ct).ConfigureAwait(false);

        var newIds = new HashSet<Guid>(upsertedStreams.Select(x => x.Id));
        var stale = existingByGuid
            .Values.Where(m =>
                !newIds.Contains(m.Id)
                && (m.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false)
            )
            .ToList();

        foreach (var _item in stale)
        {
            var users = _item.GelatoData<List<Guid>>("userIds") ?? [];
            users.Remove(userId);
            _item.SetGelatoData("userIds", users);
        }

        var toDelete = stale
            .Where(item => item.GelatoData<List<Guid>>("userIds") is { Count: 0 })
            .ToList();

        // Persist the userId removal for every stale row, orphans included.
        // This used to save only the rows other users still held, while the
        // orphans' delete was commented out, so the removal never reached the
        // database: a stale row kept this user and stayed in the stream list
        // for good. Saving all of them hides them from the list right away.
        await SaveInBatchesAsync(stale, ct).ConfigureAwait(false);

        // Then drop orphans, a bounded batch per sync so a title with a large
        // backlog doesn't stall the sync the reader is waiting on.
        foreach (var staleItem in toDelete.Take(MaxStaleDeletesPerSync))
        {
            try
            {
                libraryManager.DeleteItem(
                    staleItem,
                    new DeleteOptions { DeleteFileLocation = false },
                    false
                );
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete stale stream {ItemId}", staleItem.Id);
            }
        }

        _log.LogDebug(
            "SyncStreams stale rows GelatoId={Id}: existing={Existing} stale={Stale} orphaned={Orphaned}",
            uri.ExternalId,
            existingStreamItems.Count,
            stale.Count,
            toDelete.Count
        );
        upsertedStreams.Add(video);

        stopwatch.Stop();

        _log.LogInformation(
            $"SyncStreams finished GelatoId={uri.ExternalId} userId={userId} duration={Math.Round(stopwatch.Elapsed.TotalSeconds, 1)}s streams={upsertedStreams.Count} written={changedStreams.Count + stale.Count}"
        );

        return acceptable.Count;
    }

    /// <summary>
    /// We only check permissions cause jellyfin excludes remote items by default
    /// </summary>
    /// <param name="item"></param>
    /// <param name="user"></param>
    /// <returns></returns>
    public bool CanDelete(BaseItem item, User user)
    {
        var allCollectionFolders = libraryManager
            .GetUserRootFolder()
            .Children.OfType<Folder>()
            .ToList();

        return item.IsAuthorizedToDelete(user, allCollectionFolders);
    }

    public bool IsStremio(BaseItem item)
    {
        return item.IsGelato();
    }

    private static SeriesStatus? ToSeriesStatus(StremioStatus? status) =>
        status switch
        {
            StremioStatus.Continuing => SeriesStatus.Continuing,
            StremioStatus.Ended => SeriesStatus.Ended,
            StremioStatus.Upcoming => SeriesStatus.Unreleased,
            _ => null,
        };

    /// <summary>
    /// A series stored as not airing whose catalog entry says it is airing
    /// again: its tree is synced now, which also updates the stored status.
    /// </summary>
    private async Task RefreshReturningSeriesAsync(
        BaseItem existing,
        StremioMeta meta,
        PluginConfiguration cfg,
        CancellationToken ct
    )
    {
        if (
            existing is not Series series
            || series.Status == SeriesStatus.Continuing
            || ToSeriesStatus(meta.GetStatus()) != SeriesStatus.Continuing
            || cfg.Stremio is null
        )
            return;
        try
        {
            var full = await cfg.Stremio.GetMetaAsync(series).ConfigureAwait(false);
            if (full is not null)
            {
                _log.LogInformation("{Name} is airing again, syncing its seasons", series.Name);
                await SyncSeriesTreesAsync(cfg, full, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not sync returning series {Name}", series.Name);
        }
    }

    public async Task<BaseItem?> SyncSeriesTreesAsync(
        PluginConfiguration cfg,
        StremioMeta seriesMeta,
        CancellationToken ct,
        Series? existingSeries = null
    )
    {
        var seriesRootFolder = cfg.SeriesFolder;

        Series series;

        if (existingSeries is not null)
        {
            // Local (non-gelato) series — use as-is, no creation needed
            series = existingSeries;
        }
        else
        {
            // Gelato series — create or find under the virtual folder
            if (seriesRootFolder is null || string.IsNullOrWhiteSpace(seriesRootFolder.Path))
            {
                _log.LogWarning("seriesRootFolder null or empty for {SeriesId}", seriesMeta.Id);
                return null;
            }

            if (IntoBaseItem(seriesMeta) is not Series tmpSeries)
                return null;

            if (tmpSeries.ProviderIds.Count == 0)
            {
                _log.LogWarning(
                    "No providers found for {SeriesId} {SeriesName}, skipping creation",
                    seriesMeta.Id,
                    seriesMeta.Name
                );
                return null;
            }

            if (
                GetByProviderIds(
                    tmpSeries.ProviderIds,
                    tmpSeries.GetBaseItemKind(),
                    seriesRootFolder
                )
                is not Series found
            )
            {
                tmpSeries.Id = tmpSeries.Id == Guid.Empty ? Guid.NewGuid() : tmpSeries.Id;

                var options = new MetadataRefreshOptions(directoryService)
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllImages = false,
                    ReplaceAllMetadata = true,
                    ForceSave = true,
                };

                tmpSeries.ParentId = seriesRootFolder.Id;
                await tmpSeries.RefreshMetadata(options, ct).ConfigureAwait(false);
                seriesRootFolder.AddChild(tmpSeries);
                await tmpSeries.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, ct);
                series = tmpSeries;
            }
            else
            {
                series = found;
                var freshStatus = ToSeriesStatus(seriesMeta.GetStatus());
                if (freshStatus is not null && series.Status != freshStatus)
                {
                    _log.LogInformation(
                        "{Name}: status {Old} -> {New}",
                        series.Name,
                        series.Status,
                        freshStatus
                    );
                    series.Status = freshStatus;
                    await series
                        .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                        .ConfigureAwait(false);
                }
            }
        }

        var stopwatch = Stopwatch.StartNew();

        // Group episodes by season
        var seasonGroups = (seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
            .Where(e => e.Season.HasValue && (e.Episode.HasValue || e.Number.HasValue))
            .OrderBy(e => e.Season)
            .ThenBy(e => e.Episode ?? e.Number)
            .GroupBy(e => e.Season!.Value)
            .ToList();

        if (seasonGroups.Count == 0)
        {
            _log.LogWarning("No valid episodes found for {SeriesId}", seriesMeta.Id);
            return null;
        }

        var existingSeasonsDict = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    ParentId = series.Id,
                    IncludeItemTypes = [BaseItemKind.Season],
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .OfType<Season>()
            .Where(s => s.IndexNumber.HasValue)
            .GroupBy(s => s.IndexNumber!.Value)
            .Select(g =>
            {
                if (g.Count() > 1)
                {
                    _log.LogWarning(
                        "Duplicate seasons found for series {SeriesName} ({SeriesId})! Season {SeasonNum} exists {Count} times. IDs: {Ids}",
                        series.Name,
                        series.Id,
                        g.Key,
                        g.Count(),
                        string.Join(", ", g.Select(s => s.Id))
                    );
                }
                return g;
            })
            .ToDictionary(g => g.Key, g => g.First());

        // Fetch all existing episodes for this series in one query, grouped by season
        var existingEpisodesBySeason = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    AncestorIds = [series.Id],
                    IncludeItemTypes = [BaseItemKind.Episode],
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .OfType<Episode>()
            .Where(x => !x.IsStream() && x.IndexNumber.HasValue && x.ParentIndexNumber.HasValue)
            .GroupBy(e => e.ParentIndexNumber!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(e => e.IndexNumber!.Value).ToDictionary(x => x.Key, x => x.First())
            );

        var seasonsInserted = 0;
        var episodesInserted = 0;
        var episodesUpdated = 0;

        var newSeasons = new List<Season>();
        var allNewEpisodes = new List<Episode>();
        var updatedEpisodes = new List<Episode>();

        var seriesStremioId = series.GetProviderId("Stremio");
        var seriesPresentationKey = series.GetPresentationUniqueKey();

        foreach (var seasonGroup in seasonGroups)
        {
            ct.ThrowIfCancellationRequested();

            var seasonIndex = seasonGroup.Key;
            var seasonPath = $"{series.Path}:{seasonIndex}";

            if (!existingSeasonsDict.TryGetValue(seasonIndex, out var season))
            {
                _log.LogTrace(
                    "Creating series {SeriesName} season {SeasonIndex:D2}",
                    series.Name,
                    seasonIndex
                );
                var epMeta = seasonGroup.First();
                epMeta.Type = StremioMediaType.Episode;
                if (IntoBaseItem(epMeta) is not Episode episode)
                {
                    _log.LogWarning(
                        "Could not load base item as episode for: {EpisodeName}, skipping",
                        epMeta.GetName()
                    );
                    continue;
                }

                season = new Season
                {
                    Id = libraryManager.GetNewItemId(seasonPath, typeof(Season)),
                    Name = $"Season {seasonIndex:D2}",
                    IndexNumber = seasonIndex,
                    SeriesId = series.Id,
                    SeriesName = series.Name,
                    Path = seasonPath,
                    DateLastRefreshed = DateTime.UtcNow,
                    SeriesPresentationUniqueKey = seriesPresentationKey,
                    DateModified = DateTime.UtcNow,
                    DateLastSaved = DateTime.UtcNow,
                    PremiereDate = episode.PremiereDate,
                    EndDate =
                        episode.PremiereDate ?? new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ParentId = series.Id,
                };

                var primary = seriesMeta.App_Extras?.SeasonPosters?.ElementAtOrDefault(seasonIndex);
                if (!string.IsNullOrWhiteSpace(primary))
                {
                    ProviderManagerDecorator.SetRemoteImage(
                        appPaths,
                        season,
                        ImageType.Primary,
                        null,
                        primary
                    );
                }

                season.SetProviderId("Stremio", $"{seriesStremioId}:{seasonIndex}");
                season.PresentationUniqueKey = season.CreatePresentationUniqueKey();
                newSeasons.Add(season);
                seasonsInserted++;
            }

            // Look up existing episodes for this season from the pre-fetched dict
            var existingEpisodes = existingEpisodesBySeason.TryGetValue(seasonIndex, out var eps)
                ? eps
                : [];
            foreach (var epMeta in seasonGroup)
            {
                ct.ThrowIfCancellationRequested();

                var index = epMeta.Episode ?? epMeta.Number;

                // This should never happen due to earlier filtering, but kept for safety
                if (!index.HasValue)
                {
                    _log.LogWarning(
                        "Episode number missing for: {EpisodeName}, skipping",
                        epMeta.GetName()
                    );
                    continue;
                }

                if (existingEpisodes.TryGetValue(index.Value, out var existingEpisode))
                {
                    if (RefreshExistingEpisode(existingEpisode, epMeta))
                    {
                        updatedEpisodes.Add(existingEpisode);
                        episodesUpdated++;
                    }
                    continue;
                }

                _log.LogTrace(
                    "Processing episode {EpisodeName} with index {Index} for {SeriesName} season {SeasonIndex}",
                    epMeta.GetName(),
                    index,
                    series.Name,
                    season.IndexNumber
                );

                epMeta.Type = StremioMediaType.Episode;
                if (IntoBaseItem(epMeta) is not Episode episode)
                {
                    _log.LogWarning(
                        "Could not load base item as episode for: {EpisodeName}, skipping",
                        epMeta.GetName()
                    );
                    continue;
                }

                episode.IndexNumber = index;
                episode.ParentIndexNumber = season.IndexNumber;
                episode.SeasonId = season.Id;
                episode.SeriesId = series.Id;
                episode.SeriesName = series.Name;
                episode.SeasonName = season.Name;
                episode.ParentId = season.Id;
                episode.SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey;
                episode.PresentationUniqueKey = episode.GetPresentationUniqueKey();

                allNewEpisodes.Add(episode);
                episodesInserted++;
                _log.LogTrace("Created episode {EpisodeName}", epMeta.GetName());
            }
        }

        await SaveInBatchesAsync(newSeasons, ct).ConfigureAwait(false);
        await SaveInBatchesAsync(allNewEpisodes, ct).ConfigureAwait(false);
        await SaveInBatchesAsync(updatedEpisodes, ct).ConfigureAwait(false);

        stopwatch.Stop();

        _log.LogDebug(
            "Sync completed for {SeriesName}: {SeasonsInserted} season(s), {EpisodesInserted} new and {EpisodesUpdated} refreshed episode(s) in {Dur}",
            series.Name,
            seasonsInserted,
            episodesInserted,
            episodesUpdated,
            stopwatch.Elapsed.TotalSeconds
        );

        return series;
    }

    /// <summary>
    /// Pass 1: fixes EndDate on all gelato media items (movies get TMDB digital release date,
    /// series/seasons/episodes get PremiereDate as EndDate).
    /// </summary>
    public async Task SyncReleaseDates(
        Guid userId,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null
    )
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var stremio = cfg.Stremio;
        if (stremio is null)
            return;

        var sentinel = new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = DateTime.UtcNow;
        const int chunkSize = 400;
        const int maxDegreeOfParallelism = 4;

        var needsEndDate = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes =
                    [
                        BaseItemKind.Movie,
                        BaseItemKind.Series,
                        BaseItemKind.Season,
                        BaseItemKind.Episode,
                    ]
                }
            )
            .Where(m => m.EndDate is null || m.EndDate >= sentinel || m.EndDate > now)
            .ToList();

        var total = needsEndDate.Count;
        var processed = 0;
        var totalSaved = 0;

        for (var chunkStart = 0; chunkStart < total; chunkStart += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = needsEndDate.Skip(chunkStart).Take(chunkSize).ToList();
            var chunkResults = new System.Collections.Concurrent.ConcurrentBag<BaseItem>();

            await Parallel
                .ForEachAsync(
                    chunk,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxDegreeOfParallelism,
                        CancellationToken = cancellationToken,
                    },
                    async (item, ct) =>
                    {
                        try
                        {
                            switch (item)
                            {
                                case Movie movie:
                                    {
                                        var meta = await stremio
                                            .GetMetaAsync(movie)
                                            .ConfigureAwait(false);
                                        if (meta is null)
                                            break;
                                        await EnrichMetaAsync(meta, ct).ConfigureAwait(false);
                                        var digital = meta.GetDigitalReleaseDate();
                                        var oneYearAgo = DateTime.UtcNow.AddYears(-1);
                                        movie.EndDate =
                                            digital
                                            ?? (
                                                movie.PremiereDate.HasValue
                                                && movie.PremiereDate.Value < oneYearAgo
                                                    ? movie.PremiereDate.Value
                                                    : sentinel
                                            );
                                        chunkResults.Add(movie);
                                        _log.LogDebug(
                                            "SyncReleaseDates: movie {Name} EndDate → {Date}",
                                            movie.Name,
                                            movie.EndDate?.ToString(
                                                "yyyy-MM-dd",
                                                CultureInfo.InvariantCulture
                                            )
                                        );
                                        break;
                                    }

                                case BaseItem other when other is Series or Season or Episode:
                                    other.EndDate = other.PremiereDate ?? sentinel;
                                    chunkResults.Add(other);
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(
                                ex,
                                "SyncReleaseDates: failed for {Name} ({Id})",
                                item.Name,
                                item.Id
                            );
                        }
                        finally
                        {
                            var current = Interlocked.Increment(ref processed);
                            if (total > 0)
                                progress?.Report(100.0 * current / total);
                        }
                    }
                )
                .ConfigureAwait(false);

            if (!chunkResults.IsEmpty)
            {
                var toSave = chunkResults.ToList();
                persistence.SaveItems(toSave, cancellationToken);
                totalSaved += toSave.Count;
            }
        }

        _log.LogInformation(
            "SyncReleaseDates completed. EndDate fixed for {Count} item(s).",
            totalSaved
        );
    }

    /// <summary>
    /// Syncs series trees: fetches new episodes for all continuing series (gelato + local),
    /// and extends local series trees for the first time if ExtendLocalSeriesTrees is enabled.
    /// </summary>
    public async Task SyncSeriesTrees(
        Guid userId,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null,
        bool includeEnded = false
    )
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var stremio = cfg.Stremio;
        if (stremio is null)
            return;

        var gelatoProviders = new Dictionary<string, string>
        {
            { "Stremio", string.Empty },
            { "stremio", string.Empty },
        };

        var continuingGelatoSeries = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Series],
                    SeriesStatuses = includeEnded
                        ? [SeriesStatus.Continuing, SeriesStatus.Ended]
                        : [SeriesStatus.Continuing],
                    HasAnyProviderId = gelatoProviders,
                }
            )
            .OfType<Series>()
            .ToList();

        var continuingLocalSeries = cfg.ExtendLocalSeriesTrees
            ? libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Series],
                        SeriesStatuses = [SeriesStatus.Continuing],
                    }
                )
                .OfType<Series>()
                .Where(s =>
                    !s.IsGelato()
                    && (
                        !string.IsNullOrWhiteSpace(s.GetProviderId("Imdb"))
                        || !string.IsNullOrWhiteSpace(s.GetProviderId("Tmdb"))
                    )
                )
                .ToList()
            : [];

        var continuingSeries = continuingGelatoSeries.Concat(continuingLocalSeries).ToList();

        var total = continuingSeries.Count;
        var i = 0;

        await Parallel.ForEachAsync(
            continuingSeries,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken = cancellationToken,
            },
            async (series, ct) =>
            {
                try
                {
                    var meta = await stremio.GetMetaAsync(series).ConfigureAwait(false);
                    if (meta is not null)
                    {
                        var isLocal = !series.IsGelato();
                        await SyncSeriesTreesAsync(
                                cfg,
                                meta,
                                ct,
                                existingSeries: isLocal ? series : null
                            )
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(
                        ex,
                        "SyncSeriesTrees: tree sync failed for {Name} ({Id})",
                        series.Name,
                        series.Id
                    );
                }
                finally
                {
                    if (total > 0)
                        progress?.Report(100.0 * Interlocked.Increment(ref i) / total);
                }
            }
        );

        _log.LogInformation(
            "SyncSeriesTrees: continuing series synced: {SeriesCount}.",
            continuingSeries.Count
        );

        if (cfg.ExtendLocalSeriesTrees)
        {
            await SyncLocalSeriesTreesAsync(cfg, stremio, cancellationToken, progress, i, total)
                .ConfigureAwait(false);
        }
        else
        {
            CleanVirtualTreeItems(cancellationToken);
        }
    }

    private async Task SyncLocalSeriesTreesAsync(
        PluginConfiguration cfg,
        GelatoStremioProvider stremio,
        CancellationToken ct,
        IProgress<double>? progress,
        int progressOffset,
        int progressTotal
    )
    {
        var localSeries = libraryManager
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series] })
            .OfType<Series>()
            .Where(s =>
                !s.IsGelato()
                && s.Status != SeriesStatus.Continuing // continuing handled in pass 2
                && (
                    !string.IsNullOrWhiteSpace(s.GetProviderId("Imdb"))
                    || !string.IsNullOrWhiteSpace(s.GetProviderId("Tmdb"))
                )
                && !(s.Tags?.Contains(TreeSyncedTag, StringComparer.OrdinalIgnoreCase) ?? false)
            )
            .ToList();

        _log.LogInformation(
            "SyncSeriesTrees: {Count} local (non-gelato, non-continuing) series to extend for the first time.",
            localSeries.Count
        );

        var total = progressTotal + localSeries.Count;
        var i = progressOffset;

        foreach (var series in localSeries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var meta = await stremio.GetMetaAsync(series).ConfigureAwait(false);
                if (meta is not null)
                {
                    await SyncSeriesTreesAsync(cfg, meta, ct, existingSeries: series)
                        .ConfigureAwait(false);

                    // Mark as synced so we skip on future runs
                    series.Tags = [.. (series.Tags ?? []), TreeSyncedTag];
                    persistence.SaveItems([series], ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "SyncSeriesTrees: virtual tree sync failed for {Name} ({Id})",
                    series.Name,
                    series.Id
                );
            }
            finally
            {
                if (total > 0)
                    progress?.Report(100.0 * ++i / total);
            }
        }
    }

    public void CleanVirtualTreeItem(Series series, CancellationToken ct)
    {
        var allEpisodes = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    AncestorIds = [series.Id],
                }
            )
            .OfType<Episode>()
            .ToList();

        var virtualEpisodes = allEpisodes.Where(ep => ep.IsGelato()).ToList();

        if (virtualEpisodes.Count == 0)
            return;

        var virtualEpIds = virtualEpisodes.Select(e => e.Id).ToHashSet();
        var seasonsWithRemainingEpisodes = allEpisodes
            .Where(ep => !virtualEpIds.Contains(ep.Id))
            .Select(ep => ep.SeasonId)
            .ToHashSet();

        _log.LogDebug(
            "CleanVirtualTreeItem: removing {EpCount} virtual episodes from {SeriesName}.",
            virtualEpisodes.Count,
            series.Name
        );

        foreach (var ep in virtualEpisodes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                libraryManager.DeleteItem(ep, new DeleteOptions { DeleteFileLocation = false });
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "CleanVirtualTreeItem: failed to delete episode {Name} ({Id})",
                    ep.Name,
                    ep.Id
                );
            }
        }

        var allSeasons = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Season],
                    ParentId = series.Id,
                }
            )
            .OfType<Season>()
            .ToList();

        foreach (var season in allSeasons)
        {
            ct.ThrowIfCancellationRequested();
            if (seasonsWithRemainingEpisodes.Contains(season.Id))
                continue;

            try
            {
                libraryManager.DeleteItem(season, new DeleteOptions { DeleteFileLocation = false });
                _log.LogDebug(
                    "CleanVirtualTreeItem: deleted empty season {Name} ({Id})",
                    season.Name,
                    season.Id
                );
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "CleanVirtualTreeItem: failed to delete season {Name} ({Id})",
                    season.Name,
                    season.Id
                );
            }
        }

        series.Tags = series
            .Tags?.Where(t => !t.Equals(TreeSyncedTag, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        persistence.SaveItems([series], ct);
    }

    private void CleanVirtualTreeItems(CancellationToken ct)
    {
        var localSeries = libraryManager
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series] })
            .OfType<Series>()
            .Where(s => string.IsNullOrEmpty(s.GetProviderId("Stremio")))
            .ToList();

        foreach (var series in localSeries)
        {
            ct.ThrowIfCancellationRequested();
            CleanVirtualTreeItem(series, ct);
        }
    }

    private BaseItem? SaveItem(BaseItem item, Folder parent)
    {
        return SaveItems([item], parent).FirstOrDefault();
    }

    private List<BaseItem> SaveItems(IEnumerable<BaseItem> items, Folder parent)
    {
        var baseItems = items.ToList();
        foreach (var item in baseItems)
        {
            var now = DateTime.UtcNow;
            item.DateModified = now;
            item.DateLastRefreshed = now;
            item.DateLastSaved = now;

            item.Id = libraryManager.GetNewItemId(item.Path, item.GetType());
            item.PresentationUniqueKey = item.CreatePresentationUniqueKey();

            parent.AddChild(item);
        }

        persistence.SaveItems(baseItems, CancellationToken.None);
        return baseItems;
    }

    private static readonly System.Text.RegularExpressions.Regex GenericEpisodeName = new(
        @"^\s*(episode|folge|ep\.?|e)\s*\d+\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled
    );

    /// <summary>
    /// Brings an already-created Gelato episode in line with the series meta.
    /// The tree sync used to skip existing episodes entirely, so an episode
    /// first created before the metadata source had its title, description
    /// or thumbnail (common for new shows) stayed blank or stale for good.
    /// Only Gelato stub episodes are touched, never local files, and locked
    /// fields are left alone. Returns true when something changed.
    /// </summary>
    private bool RefreshExistingEpisode(Episode episode, StremioMeta meta)
    {
        if (!(episode.Path?.StartsWith("gelato", StringComparison.OrdinalIgnoreCase) ?? false))
            return false;

        var locked = episode.LockedFields ?? [];
        var changed = false;

        var name = meta.GetName();
        if (
            !locked.Contains(MetadataField.Name)
            && !string.IsNullOrWhiteSpace(name)
            && !GenericEpisodeName.IsMatch(name)
            && !string.Equals(episode.Name, name, StringComparison.Ordinal)
        )
        {
            episode.Name = name;
            changed = true;
        }

        var overview = meta.Description ?? meta.Overview;
        if (
            !locked.Contains(MetadataField.Overview)
            && !string.IsNullOrWhiteSpace(overview)
            && !string.Equals(episode.Overview, overview, StringComparison.Ordinal)
        )
        {
            episode.Overview = overview;
            changed = true;
        }

        var premiere = meta.GetPremiereDate();
        // Compare by day: stored and parsed values can differ in DateTimeKind only.
        if (premiere.HasValue && episode.PremiereDate?.Date != premiere.Value.Date)
        {
            episode.PremiereDate = premiere;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(meta.Runtime) && !locked.Contains(MetadataField.Runtime))
        {
            var ticks = Utils.ParseToTicks(meta.Runtime);
            if (ticks > 0 && episode.RunTimeTicks != ticks)
            {
                episode.RunTimeTicks = ticks;
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(meta.Thumbnail))
        {
            var thumbChanged = !string.Equals(
                episode.GetProviderId("StremioThumb"),
                meta.Thumbnail,
                StringComparison.Ordinal
            );
            if (thumbChanged || !episode.HasImage(ImageType.Primary))
            {
                episode.SetProviderId("StremioThumb", meta.Thumbnail);
                ProviderManagerDecorator.SetRemoteImage(
                    appPaths,
                    episode,
                    ImageType.Primary,
                    null,
                    meta.Thumbnail
                );
                changed = true;
            }
        }

        if (changed)
        {
            episode.DateModified = DateTime.UtcNow;
            episode.DateLastSaved = DateTime.UtcNow;
        }

        return changed;
    }

    /// <summary>
    /// Anime: an id from an anime database (only anime addons use those)
    /// unless the title is known to be made outside Japan, an "Anime" genre,
    /// or animation made in Japan alone (co-productions like The Legend of
    /// Korra or The Super Mario Bros. Movie list Japan among others).
    /// </summary>
    public static bool IsAnime(BaseItem item)
    {
        var locations = (item.ProductionLocations ?? [])
            .Where(location => !string.IsNullOrWhiteSpace(location))
            .ToArray();
        static bool IsJapan(string location) =>
            location.Contains("Japan", StringComparison.OrdinalIgnoreCase);

        var genres = item.Genres ?? [];
        if (genres.Contains("Anime", StringComparer.OrdinalIgnoreCase))
            return true;

        if (
            item.ProviderIds is { } ids
            && ids.Any(pair =>
                !string.IsNullOrWhiteSpace(pair.Value)
                && AnimeProviders.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)
            )
            && (locations.Length == 0 || locations.Any(IsJapan))
        )
            return true;

        return genres.Contains("Animation", StringComparer.OrdinalIgnoreCase)
            && locations.Length > 0
            && locations.All(IsJapan);
    }

    /// <summary>
    /// Tags every library movie and series that looks like anime but isn't
    /// tagged yet: titles imported before the tag existed, and ones whose
    /// tags a metadata refresh replaced.
    /// </summary>
    public int TagAnime(CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        var untagged = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                    Recursive = true,
                }
            )
            .Where(item =>
                !(item.Tags?.Contains(AnimeTag, StringComparer.OrdinalIgnoreCase) ?? false)
                && IsAnime(item)
            )
            .ToList();

        const int chunkSize = 400;
        for (var start = 0; start < untagged.Count; start += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = untagged.Skip(start).Take(chunkSize).ToList();
            foreach (var item in chunk)
                item.Tags = [.. item.Tags ?? [], AnimeTag];
            persistence.SaveItems(chunk, cancellationToken);
            progress?.Report(100.0 * Math.Min(start + chunkSize, untagged.Count) / untagged.Count);
        }

        _log.LogInformation("TagAnime: tagged {Count} titles as anime", untagged.Count);
        return untagged.Count;
    }

    public BaseItem? IntoBaseItem(StremioMeta meta)
    {
        BaseItem item;

        var id = meta.Id;

        switch (meta.Type)
        {
            case StremioMediaType.Series:
                item = new Series { };
                break;

            case StremioMediaType.Movie:
                item = new Movie { };
                break;

            case StremioMediaType.Episode:
                item = new Episode { };
                break;
            default:
                _log.LogWarning("unsupported type {type}", meta.Type);
                return null;
        }

        item.Name = meta.GetName();

        item.PremiereDate = meta.GetPremiereDate();

        // Always set EndDate so it's never NULL — NULL breaks MaxEndDate filtering (SQL NULL semantics).
        // Movies: use digital release date (TMDB type-4); sentinel 9999 means no digital date yet.
        // Exception: if PremiereDate is older than 1 year, use it as EndDate so old media without a
        // digital release date is never hidden by the unreleased filter.
        // Series/Season/Episode: use premiere/firstAired date; sentinel 9999 means not yet known.
        {
            var sentinel = new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var oneYearAgo = DateTime.UtcNow.AddYears(-1);
            item.EndDate =
                meta.Type == StremioMediaType.Movie
                    ? meta.GetDigitalReleaseDate()
                        ?? (
                            item.PremiereDate.HasValue && item.PremiereDate.Value < oneYearAgo
                                ? item.PremiereDate.Value
                                : sentinel
                        )
                    : meta.GetPremiereDate() ?? sentinel;
        }

        item.ProductionYear = meta.GetYear();
        item.Path = $"gelato://stub/{id}";

        // Provider IDs — skip for episodes since the parent series IMDB id is used there
        if (meta.Type is not StremioMediaType.Episode && !string.IsNullOrWhiteSpace(id))
        {
            var providerMappings = new (string Prefix, string Provider, bool StripPrefix)[]
            {
                ("tmdb:", nameof(MetadataProvider.Tmdb), true),
                ("tt", nameof(MetadataProvider.Imdb), false),
                ("anidb:", "AniDB", true),
                ("kitsu:", "Kitsu", true),
                ("mal:", "Mal", true),
                ("anilist:", "Anilist", true),
                ("tvdb:", nameof(MetadataProvider.Tvdb), true),
                ("tvmaze:", nameof(MetadataProvider.TvMaze), true),
            };

            foreach (var (prefix, prov, stripPrefix) in providerMappings)
            {
                if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var providerId = stripPrefix ? id[prefix.Length..] : id;
                item.SetProviderId(prov, providerId);
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(meta.ImdbId))
            item.SetProviderId(MetadataProvider.Imdb, meta.ImdbId);

        // Fall back to the catalog id when ImdbId is absent OR blank. Addons
        // commonly emit "imdb_id": "" rather than omitting the field, and `??`
        // does not treat an empty string as missing - so a meta carrying a
        // perfectly good id (e.g. "tmdb:456173") reached StremioUri with "".
        var externalId = !string.IsNullOrWhiteSpace(meta.ImdbId) ? meta.ImdbId : id;

        // Every caller of IntoBaseItem already skips a null return, so honour
        // that contract instead of throwing: an exception here aborts the whole
        // request and takes every other result with it.
        if (string.IsNullOrWhiteSpace(externalId))
        {
            _log.LogWarning(
                "Skipping meta with no usable external id: {Name} ({Type})",
                meta.GetName(),
                meta.Type
            );
            return null;
        }

        var stremioUri = new StremioUri(meta.Type, externalId);
        item.SetProviderId("Stremio", stremioUri.ExternalId);

        item.Overview = meta.Description ?? meta.Overview;

        if (meta.ImdbRating.HasValue)
            item.CommunityRating = meta.ImdbRating;

        if (!string.IsNullOrWhiteSpace(meta.Country))
            item.ProductionLocations =
            [
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(meta.Country.ToLowerInvariant()),
            ];

        if (!string.IsNullOrWhiteSpace(meta.App_Extras?.Certification))
            item.OfficialRating = meta.App_Extras.Certification;

        if (meta.Type is StremioMediaType.Movie or StremioMediaType.Series)
        {
            if (!string.IsNullOrWhiteSpace(meta.Runtime))
                item.RunTimeTicks = Utils.ParseToTicks(meta.Runtime);

            var genres = (meta.Genres ?? meta.Genre) ?? [];
            item.Genres = genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();

            if (IsAnime(item))
                item.Tags = [.. item.Tags ?? [], AnimeTag];
        }

        if (item is Episode ep)
        {
            ep.IndexNumber = meta.Episode ?? meta.Number;
            ep.ParentIndexNumber = meta.Season;
            if (!string.IsNullOrWhiteSpace(meta.Runtime))
                ep.RunTimeTicks = Utils.ParseToTicks(meta.Runtime);
            if (!string.IsNullOrWhiteSpace(meta.Thumbnail))
                ep.SetProviderId("StremioThumb", meta.Thumbnail);
            var tvdbId = meta.TvdbEpisodeId();
            if (tvdbId is not null)
                ep.SetProviderId(MetadataProvider.Tvdb, tvdbId);
        }

        if (item is Series series)
        {
            series.Status = ToSeriesStatus(meta.GetStatus());
        }

        item.IsVirtualItem = false;
        item.DateModified = DateTime.UtcNow;
        item.DateLastSaved = DateTime.UtcNow;
        item.DateCreated = DateTime.UtcNow;
        item.Id = libraryManager.GetNewItemId(item.Path, item.GetType());
        item.PresentationUniqueKey = item.CreatePresentationUniqueKey();

        var primaryImage = meta.Poster ?? meta.Thumbnail;
        if (!string.IsNullOrWhiteSpace(primaryImage))
            ProviderManagerDecorator.SetRemoteImage(
                appPaths,
                item,
                ImageType.Primary,
                null,
                primaryImage
            );

        // Backdrop, logo and landscape art from the start too, the same way as
        // the poster: they used to appear only once the queued metadata refresh
        // got to the item, which on a large import can take hours. An episode's
        // meta only carries the series' art, so it is left to its thumbnail.
        if (item is not Episode)
        {
            SetRemoteImageIfAny(item, ImageType.Backdrop, meta.Background);
            SetRemoteImageIfAny(item, ImageType.Logo, meta.Logo);
            SetRemoteImageIfAny(item, ImageType.Thumb, meta.LandscapePoster);
        }

        return item;
    }

    private void SetRemoteImageIfAny(BaseItem item, ImageType type, string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
            ProviderManagerDecorator.SetRemoteImage(appPaths, item, type, null, url);
    }

    /// <summary>
    /// Enriches <paramref name="meta"/> with digital release dates from TMDB when
    /// the meta is a movie and <c>App_Extras.ReleaseDates</c> is not yet populated.
    /// </summary>
    public async Task EnrichMetaAsync(StremioMeta meta, CancellationToken ct)
    {
        if (meta.Type != StremioMediaType.Movie)
            return;

        if (meta.App_Extras?.ReleaseDates is not null)
            return;

        var stremio = GelatoPlugin.Instance?.Configuration.Stremio;
        if (stremio is null)
            return;

        await stremio.EnrichDigitalReleaseDateAsync(meta, ct).ConfigureAwait(false);
    }
}
