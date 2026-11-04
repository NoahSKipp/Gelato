using System.Diagnostics;
using Gelato.Config;
using Gelato.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public class SearchActionFilter(
    IDtoService dtoService,
    GelatoManager manager,
    ILogger<SearchActionFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    public int Order => 1;

    private static readonly SearchCache<List<BaseItemDto>> Cache = new(TimeSpan.FromSeconds(60));

    // A search result is not in the library yet, so it has no extras,
    // trailers, streams or chapters; Jellyfin still asked the database for
    // each (about 0.4 s per result on a large library). Every other field
    // is built as before, and these are filled in with what they would be.
    private static readonly ItemFields[] SkippedFields =
    [
        ItemFields.SpecialFeatureCount,
        ItemFields.LocalTrailerCount,
        ItemFields.MediaSources,
        ItemFields.MediaStreams,
        ItemFields.Chapters,
    ];

    private static readonly ItemFields[] ResultFields = Enum.GetValues<ItemFields>()
        .Except(SkippedFields)
        .ToArray();

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        ctx.TryGetUserId(out var userId);
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (
            cfg.DisableSearch
            || !ctx.IsApiSearchAction()
            || !ctx.TryGetActionArgument<string>("searchTerm", out var searchTerm)
            || !await cfg.Stremio.IsReady()
        )
        {
            await next();
            return;
        }

        // Strip "local:" prefix if present and pass through to default handler
        if (searchTerm.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            ctx.ActionArguments["searchTerm"] = searchTerm[6..].Trim();
            await next();
            return;
        }

        // Handle Stremio search
        var requestedTypes = GetRequestedItemTypes(ctx);
        if (requestedTypes.Count == 0)
        {
            await next();
            return;
        }

        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 25);

        var ct = ctx.HttpContext.RequestAborted;
        var sw = Stopwatch.StartNew();
        var key = SearchCache<List<BaseItemDto>>.Key(
            userId,
            searchTerm,
            requestedTypes
                .Select(t => t.ToString())
                .Append(cfg.FilterUnreleased ? $"unreleased:{cfg.FilterUnreleasedBufferDays}" : "all")
        );

        long addonMs = 0;
        var cached = Cache.TryGet(key, out var dtos);
        if (!cached)
        {
            List<StremioMeta> metas;
            try
            {
                metas = await SearchMetasAsync(searchTerm, requestedTypes, cfg, userId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                log.LogDebug("Search \"{Query}\" abandoned by the client", searchTerm);
                ctx.Result = new EmptyResult();
                return;
            }
            addonMs = sw.ElapsedMilliseconds;
            dtos = ConvertMetasToDtos(metas, ct);
            if (ct.IsCancellationRequested)
            {
                ctx.Result = new EmptyResult();
                return;
            }
            Cache.Set(key, dtos);
        }

        log.LogInformation(
            "Intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit} results={Results} in {Ms} ms (addon {AddonMs} ms, items {ItemsMs} ms{Cached})",
            searchTerm,
            string.Join(",", requestedTypes),
            start,
            limit,
            dtos.Count,
            sw.ElapsedMilliseconds,
            addonMs,
            sw.ElapsedMilliseconds - addonMs,
            cached ? ", cached" : ""
        );

        var paged = dtos.Skip(start).Take(limit).ToArray();

        ctx.Result = new OkObjectResult(
            new QueryResult<BaseItemDto> { Items = paged, TotalRecordCount = dtos.Count }
        );
    }

    private HashSet<BaseItemKind> GetRequestedItemTypes(ActionExecutingContext ctx)
    {
        var requested = new HashSet<BaseItemKind>([BaseItemKind.Movie, BaseItemKind.Series]);

        // Already parsed as BaseItemKind[] by model binder
        if (
            ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var includeTypes)
            && includeTypes is { Length: > 0 }
        )
        {
            requested = new HashSet<BaseItemKind>(includeTypes);
            // Only keep Movie and Series
            requested.IntersectWith([BaseItemKind.Movie, BaseItemKind.Series]);
        }

        // Remove excluded types
        if (
            ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var excludeTypes)
            && excludeTypes is { Length: > 0 }
        )
        {
            requested.ExceptWith(excludeTypes);
        }

        // If mediaTypes=Video, exclude Series
        if (
            ctx.TryGetActionArgument<MediaType[]>("mediaTypes", out var mediaTypes)
            && mediaTypes.Contains(MediaType.Video)
        )
        {
            requested.Remove(BaseItemKind.Series);
        }

        return requested;
    }

    private async Task<List<StremioMeta>> SearchMetasAsync(
        string searchTerm,
        HashSet<BaseItemKind> requestedTypes,
        PluginConfiguration cfg,
        Guid userId,
        CancellationToken ct
    )
    {
        var tasks = new List<Task<IReadOnlyList<StremioMeta>>>();
        var movieFolder = cfg.MovieFolder ?? manager.TryGetMovieFolder(userId);
        var seriesFolder = cfg.SeriesFolder ?? manager.TryGetSeriesFolder(userId);

        // Keep hot config in sync for subsequent searches in this request window.
        cfg.MovieFolder = movieFolder;
        cfg.SeriesFolder = seriesFolder;

        if (requestedTypes.Contains(BaseItemKind.Movie) && movieFolder is not null)
        {
            tasks.Add(SearchAnyLanguageAsync(cfg.Stremio!, searchTerm, StremioMediaType.Movie, ct));
        }
        else if (requestedTypes.Contains(BaseItemKind.Movie))
        {
            log.LogWarning(
                "No movie folder found, please add your gelato path to a library and rescan. skipping search"
            );
        }

        if (requestedTypes.Contains(BaseItemKind.Series) && seriesFolder is not null)
        {
            tasks.Add(SearchAnyLanguageAsync(cfg.Stremio!, searchTerm, StremioMediaType.Series, ct));
        }
        else if (requestedTypes.Contains(BaseItemKind.Series))
        {
            log.LogWarning(
                "No series folder found, please add your gelato path to a library and rescan. skipping search"
            );
        }

        var results = (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();

        var filterUnreleased = cfg.FilterUnreleased;
        var bufferDays = cfg.FilterUnreleasedBufferDays;

        if (filterUnreleased)
        {
            results = results.Where(x => x.IsReleased(bufferDays)).ToList();
        }

        return results;
    }

    // The addon's search only knows one title per entry (usually English).
    // A term in another language is also looked up on TMDB, and the titles
    // it names are searched too; the term's own hits come first.
    private static async Task<IReadOnlyList<StremioMeta>> SearchAnyLanguageAsync(
        GelatoStremioProvider stremio,
        string searchTerm,
        StremioMediaType mediaType,
        CancellationToken ct
    )
    {
        var direct = stremio.SearchAsync(searchTerm, mediaType, ct: ct);
        if (searchTerm.Trim().Length < 3)
            return await direct;

        var titles = await stremio.GetSearchTitlesAsync(searchTerm, mediaType, ct);
        var translated = titles
            .Take(2)
            .Select(title => stremio.SearchAsync(title, mediaType, ct: ct))
            .ToList();

        var merged = new List<StremioMeta>(await direct);
        var seen = new HashSet<string>(merged.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var batch in await Task.WhenAll(translated))
        {
            foreach (var meta in batch.Take(5))
            {
                if (seen.Add(meta.Id))
                    merged.Add(meta);
            }
        }

        return merged;
    }

    private List<BaseItemDto> ConvertMetasToDtos(List<StremioMeta> metas, CancellationToken ct)
    {
        // Infuse breaks on a small subset of fields, so every field is still
        // sent; only the ones that need the database are filled in here.
        var options = new DtoOptions(false)
        {
            Fields = ResultFields,
            EnableImages = true,
            EnableUserData = false,
        };

        var dtos = new List<BaseItemDto>(metas.Count);

        foreach (var meta in metas)
        {
            if (ct.IsCancellationRequested)
                break;
            var baseItem = manager.IntoBaseItem(meta);
            if (baseItem is null)
                continue;

            var dto = dtoService.GetBaseItemDto(baseItem, options);
            FillSkippedFields(dto, baseItem);
            var stremioUri = StremioUri.FromBaseItem(baseItem);
            dto.Id = stremioUri.ToGuid();
            dtos.Add(dto);

            manager.SaveStremioMeta(dto.Id, meta);
        }

        return dtos;
    }

    // What Jellyfin returns for these fields on an item that is not saved.
    public static void FillSkippedFields(BaseItemDto dto, BaseItem item)
    {
        dto.SpecialFeatureCount = 0;
        dto.LocalTrailerCount = 0;
        dto.Chapters = [];
        if (item is Video)
        {
            dto.MediaStreams = [];
            dto.MediaSources =
            [
                new MediaSourceInfo
                {
                    Protocol = MediaProtocol.Http,
                    Id = item.Id.ToString("N"),
                    Path = item.Path,
                    Type = MediaSourceType.Default,
                    Name = item.Name,
                    VideoType = VideoType.VideoFile,
                    IsRemote = false,
                    MediaStreams = [],
                },
            ];
        }
    }
}
