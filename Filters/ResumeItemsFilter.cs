using System;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Strips Gelato's own internal per-stream duplicate items (tagged
/// GelatoManager.StreamTag) out of the Resume/Continue Watching response.
/// Those items exist purely for MediaSourceManagerDecorator's own stream
/// resolution and are marked IsVirtualItem = false so Jellyfin will
/// actually stream them - the same flag Jellyfin's own GetResumeItems
/// query (ItemsController.cs) uses to decide eligibility, so nothing
/// upstream excludes them if one ever picks up real PlaybackPositionTicks.
/// Real bug this fixes: "Remove from Continue Watching" only clears the
/// canonical episode's own watch state, never a duplicate's, so a stale
/// duplicate resurfaces indefinitely, rendered as a bare title/season card
/// since it never carries real episode metadata of its own.
/// </summary>
public sealed class ResumeItemsFilter : IAsyncActionFilter
{
    private static readonly string[] ResumeActionNames = ["GetResumeItems", "GetResumeItemsLegacy"];

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        var actionName = ctx.GetActionName();
        if (actionName is null || !ResumeActionNames.Contains(actionName))
        {
            await next();
            return;
        }

        var executed = await next();

        if (executed.Result is not ObjectResult { Value: QueryResult<BaseItemDto> result })
        {
            return;
        }

        var filtered = result
            .Items.Where(item =>
                item.Tags is null || !item.Tags.Contains(GelatoManager.StreamTag)
            )
            .ToList();

        var removed = result.Items.Count - filtered.Count;
        if (removed > 0)
        {
            result.Items = filtered;
            result.TotalRecordCount = Math.Max(0, result.TotalRecordCount - removed);
        }
    }
}
