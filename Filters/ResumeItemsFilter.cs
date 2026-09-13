using System;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Strips two classes of item Jellyfin's own GetResumeItems/
/// GetResumeItemsLegacy query (ItemsController.cs) never meant to
/// surface out of the Resume/Continue Watching response:
///
/// 1. Gelato's own internal per-stream duplicate items (tagged
///    GelatoManager.StreamTag). Those exist purely for
///    MediaSourceManagerDecorator's own stream resolution and are marked
///    IsVirtualItem = false so Jellyfin will actually stream them - the
///    same flag Jellyfin's own query uses to decide eligibility, so
///    nothing upstream excludes them if one ever picks up real
///    PlaybackPositionTicks. "Remove from Continue Watching" only clears
///    the canonical episode's own watch state, never a duplicate's, so a
///    stale duplicate resurfaces indefinitely.
///
/// 2. Anything that isn't actually a Movie or Episode. Real bug, found
///    live: screens/detail.js's own Play/Change Stream buttons used to
///    open the stream picker directly against a Season's own item id
///    (only Series was ever excluded from that branch, not Season too),
///    which reached the player and reported real playback progress
///    against the Season's own id - writing real
///    UserData.PlaybackPositionTicks onto a Season item, which then
///    surfaced here as a bare "Season 01"/"Specials" card. Fixed at the
///    source in Jellio now, but this filter is what actually clears the
///    display for whatever already got written before that shipped,
///    with no watched-state side effect: unlike marking the entry
///    played (which cascades to every episode in the season), simply
///    never returning it here touches nothing.
/// </summary>
public sealed class ResumeItemsFilter : IAsyncActionFilter
{
    private static readonly string[] ResumeActionNames = ["GetResumeItems", "GetResumeItemsLegacy"];
    private static readonly BaseItemKind[] ResumableKinds = [BaseItemKind.Movie, BaseItemKind.Episode];

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
                ResumableKinds.Contains(item.Type)
                && (item.Tags is null || !item.Tags.Contains(GelatoManager.StreamTag))
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
