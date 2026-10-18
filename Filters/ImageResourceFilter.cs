using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Gelato.Decorators;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// Sends image requests that would otherwise wait on a download straight to the
/// image host (TMDB, Metahub, ...), sized close to what the client asked for:
/// search results, which were proxied in full on every request, and library
/// images still pointing at their zero-byte placeholder (every imported poster,
/// episode still and season poster starts out that way). For the latter the
/// real file is downloaded in the background, so later requests are served
/// locally as before.
/// </summary>
public sealed partial class ImageResourceFilter(
    GelatoManager manager,
    ILibraryManager libraryManager,
    IApplicationPaths appPaths,
    Lazy<ProviderManagerDecorator> providerManager,
    ILogger<ImageResourceFilter> log
) : IAsyncResourceFilter
{
    private static readonly ConcurrentDictionary<string, byte> Downloading = new();
    private static readonly SemaphoreSlim DownloadSlots = new(4);

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext ctx,
        ResourceExecutionDelegate next
    )
    {
        if (
            ctx.ActionDescriptor
            is not ControllerActionDescriptor
            {
                ActionName: "GetItemImage" or "GetItemImageByIndex" or "GetItemImage2"
            }
        )
        {
            await next();
            return;
        }

        var routeValues = ctx.RouteData.Values;
        if (
            !routeValues.TryGetValue("itemId", out var guidString)
            || !Guid.TryParse(guidString?.ToString(), out var guid)
        )
        {
            await next();
            return;
        }

        var type =
            routeValues.TryGetValue("imageType", out var typeValue)
            && Enum.TryParse<ImageType>(typeValue?.ToString(), true, out var parsed)
                ? parsed
                : ImageType.Primary;
        int? index =
            routeValues.TryGetValue("imageIndex", out var indexValue)
            && int.TryParse(indexValue?.ToString(), out var parsedIndex)
                ? parsedIndex
                : null;
        var width = RequestedWidth(ctx);

        var url = SearchResultUrl(guid, type) ?? PlaceholderUrl(guid, type, index);
        if (url is null)
        {
            await next();
            return;
        }

        var target = SizedUrl(url, type, width);
        log.LogDebug(
            "ImageFilter: redirecting item={ItemId} type={Type} to {Url}",
            guid,
            type,
            target
        );
        ctx.HttpContext.Response.Headers.CacheControl = "no-store";
        ctx.Result = new RedirectResult(target, permanent: false);
    }

    // Search results only live in the meta cache, never in the library.
    private string? SearchResultUrl(Guid guid, ImageType type)
    {
        var meta = manager.GetStremioMeta(guid);
        if (meta is null)
            return null;
        return type switch
        {
            ImageType.Primary => meta.Poster,
            ImageType.Backdrop => meta.Background,
            ImageType.Logo => meta.Logo,
            ImageType.Thumb => meta.LandscapePoster ?? meta.Background,
            _ => null,
        };
    }

    // A library image still on its zero-byte placeholder: its remote URL, and
    // a background download of the real file.
    private string? PlaceholderUrl(Guid guid, ImageType type, int? index)
    {
        var item = libraryManager.GetItemById(guid);
        var info = item?.GetImageInfo(type, index ?? 0);
        if (item is null || info?.Path is null)
            return null;

        var file = new FileInfo(info.Path);
        if (file.Exists && file.Length > 0)
            return null;

        var urlFile = ResolveUrlFile(info.Path, item.Id, type, index);
        if (urlFile is null)
            return null;

        var url = File.ReadAllText(urlFile).Trim();
        if (!url.IsUrl())
            return null;

        DownloadInBackground(item, url, type, index);
        return url;
    }

    private string? ResolveUrlFile(string imagePath, Guid itemId, ImageType type, int? index)
    {
        var direct = imagePath + ".url";
        if (File.Exists(direct))
            return direct;
        var fileName = index > 0 ? $"{type}_{index}.jpg" : $"{type}.jpg";
        var fallback =
            Path.Combine(appPaths.DataPath, "gelato", "images", itemId.ToString("N"), fileName)
            + ".url";
        return File.Exists(fallback) ? fallback : null;
    }

    private void DownloadInBackground(BaseItem item, string url, ImageType type, int? index)
    {
        var key = $"{item.Id:N}:{type}:{index ?? 0}";
        if (!Downloading.TryAdd(key, 0))
            return;

        _ = Task.Run(async () =>
        {
            await DownloadSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                await providerManager
                    .Value.SaveImageDirect(item, url, type, index, CancellationToken.None)
                    .ConfigureAwait(false);
                await libraryManager.UpdateImagesAsync(item).ConfigureAwait(false);
                log.LogDebug("ImageFilter: stored {Type} for {Id} from {Url}", type, item.Id, url);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "ImageFilter: background download failed for {Url}", url);
            }
            finally
            {
                DownloadSlots.Release();
                Downloading.TryRemove(key, out _);
            }
        });
    }

    private static int? RequestedWidth(ResourceExecutingContext ctx)
    {
        var query = ctx.HttpContext.Request.Query;
        foreach (var name in new[] { "maxWidth", "fillWidth", "width" })
        {
            if (
                query.TryGetValue(name, out var value)
                && int.TryParse(value.ToString(), out var width)
                && width > 0
            )
                return width;
        }
        return null;
    }

    // TMDB serves fixed sizes: the smallest one at least as wide as asked
    // for, or the original past the largest.
    internal static string SizedUrl(string url, ImageType type, int? width)
    {
        if (width is null)
            return url;
        var match = TmdbImage().Match(url);
        if (!match.Success)
            return url;
        int[] sizes = type switch
        {
            ImageType.Backdrop or ImageType.Thumb or ImageType.Banner or ImageType.Art => [
                300,
                780,
                1280,
            ],
            ImageType.Logo => [92, 154, 185, 300, 500],
            _ => [92, 154, 185, 342, 500, 780],
        };
        var pick = sizes.FirstOrDefault(size => size >= width.Value);
        var size = pick == 0 ? "original" : $"w{pick}";
        return match.Groups["base"].Value + size + match.Groups["file"].Value;
    }

    [GeneratedRegex(
        @"^(?<base>https?://image\.tmdb\.org/t/p/)[^/]+(?<file>/.+)$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex TmdbImage();
}
