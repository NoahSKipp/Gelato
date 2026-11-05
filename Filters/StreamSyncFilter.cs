using Gelato.Decorators;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

// Fetches a title's streams before an action that lists or plays them runs.
// Jellyfin asks for sources synchronously, so a sync started there blocked a
// request thread until the addon answered and the rows were written; with a
// few titles opened at once that starved the thread pool and every request
// on the server waited. The detail page does not wait at all.
public sealed class StreamSyncFilter(
    IMediaSourceManager mediaSources,
    ILibraryManager libraryManager,
    ILogger<StreamSyncFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    public int Order => 4;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (
            mediaSources is MediaSourceManagerDecorator decorator
            && ctx.IsInsertableAction()
            && !ctx.HttpContext.IsDetailWithoutSources()
            && ctx.TryGetRouteGuid(out var guid)
            && ctx.TryGetUserId(out var userId)
            && libraryManager.GetItemById(guid) is { } item
        )
        {
            try
            {
                var waited = await decorator
                    .EnsureStreamsAsync(item, userId, ctx.HttpContext.RequestAborted)
                    .ConfigureAwait(false);
                if (waited > 0)
                {
                    log.Log(
                        waited >= 1000 ? LogLevel.Information : LogLevel.Debug,
                        "{Action} for {Id} waited {Ms} ms for its streams",
                        ctx.GetActionName(),
                        guid,
                        waited
                    );
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Fetching the streams of {Id} ahead of {Action} failed", guid, ctx.GetActionName());
            }
        }
        await next();
    }
}
