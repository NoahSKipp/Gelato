#nullable disable
#pragma warning disable CS1591

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Gelato.Decorators;

public class GelatoItemRepository(IItemRepository inner, IHttpContextAccessor http)
    : IItemRepository
{
    private static readonly BaseItemKind[] ListScopeMediaKinds =
    [
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Episode,
    ];

    private static readonly BaseItemKind[] PremiereFilterMediaKinds =
    [
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Season,
        BaseItemKind.Episode,
    ];

    private readonly IHttpContextAccessor _http =
        http ?? throw new ArgumentNullException(nameof(http));

    public BaseItem RetrieveItem(Guid id) => inner.RetrieveItem(id);

    public QueryResult<BaseItem> GetItems(InternalItemsQuery filter)
    {
        return inner.GetItems(ApplyFilters(filter));
    }

    public IReadOnlyList<Guid> GetItemIdsList(InternalItemsQuery filter) =>
        inner.GetItemIdsList(ApplyFilters(filter));

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery filter)
    {
        if (IsExtrasOfGelatoItems(filter))
            return [];
        return inner.GetItemList(ApplyFilters(filter));
    }

    // Gelato titles and stream rows never own extras (trailers, specials),
    // but Jellyfin looks them up on every item page: on a large library that
    // query scans for a few hundred ms each time.
    private static bool IsExtrasOfGelatoItems(InternalItemsQuery filter)
    {
        if (filter.OwnerIds is not { Length: > 0 } owners || BaseItem.LibraryManager is not { } library)
            return false;
        foreach (var id in owners)
        {
            // Only Gelato's own stubs and stream rows: a local file that
            // Gelato knows can have extras next to it.
            if (
                library.GetItemById(id) is not { } owner
                || !owner.IsGelato()
                || !(
                    owner.Path is null
                    || owner.Path.StartsWith("gelato", StringComparison.OrdinalIgnoreCase)
                    || owner.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                )
            )
                return false;
        }
        return true;
    }

    private InternalItemsQuery ApplyFilters(InternalItemsQuery filter)
    {
        var includeTypes = filter.IncludeItemTypes;
        var includesPerson = includeTypes.Contains(BaseItemKind.Person);
        // Internal Gelato/library lookups should never be reshaped by listing filters.
        // Path-based queries are commonly used to resolve configured root folders.
        if (filter.IsDeadPerson == true || !string.IsNullOrWhiteSpace(filter.Path))
        {
            if (!includesPerson)
            {
                filter.IsDeadPerson = null;
            }
            return filter;
        }

        var ctx = _http.HttpContext;
        bool isListingIntent;
        try
        {
            isListingIntent =
                ctx is not null && (ctx.IsApiListing() || ctx.IsHomeScreenSectionListing());
        }
        catch (ObjectDisposedException)
        {
            // Work started by a request (a task run from the dashboard) keeps
            // its context after the request has finished.
            isListingIntent = false;
        }
        if (!isListingIntent)
            return filter;

        var filterUnreleased = GelatoPlugin.Instance!.Configuration.FilterUnreleased;
        var bufferDays = GelatoPlugin.Instance.Configuration.FilterUnreleasedBufferDays;
        var hasIncludeTypes = includeTypes.Length != 0;
        var isStreamTagQuery = filter.Tags.Contains(
            GelatoManager.StreamTag,
            StringComparer.OrdinalIgnoreCase
        );
        var isTargetedLookup =
            filter.ItemIds.Length > 0 || (ctx is not null && ctx.IsSingleItemList());

        // Targeted ItemIds lookups are generally internal existence/permission checks.
        // Keep those untouched so the caller gets strict results from the underlying query.
        if (isTargetedLookup)
            return filter;

        if (!includesPerson)
            filter.IsDeadPerson = null;

        // Query-shape based media list detection: empty IncludeItemTypes is broad-list scope,
        // otherwise only media kinds we manage are considered for stream-row exclusion.
        var isMediaListQuery =
            !hasIncludeTypes || includeTypes.Intersect(ListScopeMediaKinds).Any();
        if (!isMediaListQuery)
            return filter;

        // Do not override queries that explicitly target stream-tagged rows.
        if (!isStreamTagQuery && filter.ExcludeTags.Length == 0)
            filter.ExcludeTags = [GelatoManager.StreamTag];

        if (filter.MaxPremiereDate is not null || !filterUnreleased)
            return filter;

        var isPremiereFilteredQuery =
            !hasIncludeTypes || includeTypes.Intersect(PremiereFilterMediaKinds).Any();
        if (!isPremiereFilteredQuery)
            return filter;

        // All media types use EndDate (digital for movies, premiere for series/episodes).
        // sentinel 9999 = no release date known → excluded by MaxEndDate <= today + bufferDays.
        if (filter.MaxEndDate is null)
            filter.MaxEndDate = DateTime.Today.AddDays(bufferDays);

        return filter;
    }

    public IReadOnlyList<BaseItem> GetLatestItemList(
        InternalItemsQuery filter,
        CollectionType collectionType
    ) => inner.GetLatestItemList(filter, collectionType);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetGenres(
        InternalItemsQuery filter
    ) => inner.GetGenres(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetMusicGenres(
        InternalItemsQuery filter
    ) => inner.GetMusicGenres(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetStudios(
        InternalItemsQuery filter
    ) => inner.GetStudios(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetArtists(
        InternalItemsQuery filter
    ) => inner.GetArtists(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAlbumArtists(
        InternalItemsQuery filter
    ) => inner.GetAlbumArtists(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAllArtists(
        InternalItemsQuery filter
    ) => inner.GetAllArtists(filter);

    public IReadOnlyList<string> GetMusicGenreNames() => inner.GetMusicGenreNames();

    public IReadOnlyList<string> GetStudioNames() => inner.GetStudioNames();

    public IReadOnlyList<string> GetGenreNames() => inner.GetGenreNames();

    // Jellyfin 12.1 added IItemRepository.GetTagNames. This build compiles
    // against 12.0, where it does not exist: a public method with its
    // signature still implements it on 12.1 and later (interface methods
    // are matched when the type loads), and the call into the inner
    // repository is bound once here when the running Jellyfin has it. It has
    // to be virtual: the runtime only maps interface methods onto virtual
    // ones, and the compiler only makes it virtual by itself when it knows
    // the interface member (which is why the class is not sealed).
    private static readonly System.Reflection.MethodInfo? InnerGetTagNames = typeof(IItemRepository).GetMethod(
        "GetTagNames",
        [typeof(InternalItemsQuery)]
    );

    private readonly Func<InternalItemsQuery, IReadOnlyList<string>>? _getTagNames =
        InnerGetTagNames?.CreateDelegate<Func<InternalItemsQuery, IReadOnlyList<string>>>(inner);

    // Gelato's own markers on its rows are not tags anyone filters by.
    private static readonly string[] InternalTags = [GelatoManager.StreamTag, GelatoManager.TreeSyncedTag];

    public virtual IReadOnlyList<string> GetTagNames(InternalItemsQuery filter) =>
        _getTagNames?.Invoke(filter).Where(t => !InternalTags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList()
        ?? [];

    public IReadOnlyList<string> GetMediaStreamLanguages(
        InternalItemsQuery filter,
        MediaBrowser.Model.Entities.MediaStreamType mediaStreamType
    ) => inner.GetMediaStreamLanguages(filter, mediaStreamType);

    public QueryFiltersLegacy GetQueryFiltersLegacy(InternalItemsQuery filter) =>
        inner.GetQueryFiltersLegacy(filter);

    public IReadOnlyList<string> GetAllArtistNames() => inner.GetAllArtistNames();

    public Task<bool> ItemExistsAsync(Guid id) => inner.ItemExistsAsync(id);

    public bool GetIsPlayed(User user, Guid id, bool recursive) =>
        inner.GetIsPlayed(user, id, recursive);

}
