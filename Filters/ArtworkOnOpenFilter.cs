using Gelato.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>Opening a Gelato title without a backdrop starts fetching one, so viewed titles heal themselves.</summary>
public sealed class ArtworkOnOpenFilter(ILibraryManager libraryManager, ArtworkBackfillService backfill)
    : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        await next();
        if (
            ctx.ActionDescriptor is not ControllerActionDescriptor { ActionName: "GetItem" or "GetItemLegacy" }
            || !ctx.RouteData.Values.TryGetValue("itemId", out var raw)
            || !Guid.TryParse(raw?.ToString(), out var id)
        )
            return;
        var item = libraryManager.GetItemById(id);
        if (item is not null)
            backfill.FillOnOpen(item);
    }
}
