using System.Reflection;
using Gelato.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Captures media source id for playback request and save it for later reuse.
/// Looks for both "MediaSourceId" and "RouteMediaSourceId", stores as "MediaSourceId".
/// </summary>
public sealed class PlaybackInfoFilter : IAsyncActionFilter, IOrderedFilter
{
    public int Order { get; init; } = 3;

    private const string ItemsKey = "MediaSourceId";
    private static readonly string[] InputKeys = ["MediaSourceId", "RouteMediaSourceId"];

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (ctx.ActionDescriptor is ControllerActionDescriptor cad)
            ctx.HttpContext.Items["actionName"] = cad.ActionName;

        if (!ctx.HttpContext.Items.ContainsKey(ItemsKey))
        {
            if (
                TryFromArgs(ctx.ActionArguments, out var id)
                || TryFromRoute(ctx, out id)
                || TryFromQuery(ctx.HttpContext.Request, out id)
            )
            {
                if (!string.IsNullOrWhiteSpace(id))
                    ctx.HttpContext.Items[ItemsKey] = id;
            }
        }

        var executed = await next();
        HandleProbeUnavailable(executed);
    }

    // No playable source yet (its probe failed or is still running): 503 with
    // Retry-After so the client asks again instead of starting a stream that
    // would fail.
    public static bool HandleProbeUnavailable(ActionExecutedContext executed)
    {
        var ex = executed.Exception;
        while (ex is not null and not ProbeUnavailableException)
            ex = ex.InnerException;
        if (ex is not ProbeUnavailableException unavailable || executed.ExceptionHandled)
            return false;
        executed.HttpContext.Response.Headers["Retry-After"] =
            ProbeUnavailableException.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        executed.Result = new ObjectResult(new { Error = "source_not_ready", Reason = unavailable.Message })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
        };
        executed.ExceptionHandled = true;
        return true;
    }

    private static bool TryFromArgs(IDictionary<string, object?> args, out string? id)
    {
        foreach (var kv in args)
        {
            if (kv.Value is null)
                continue;

            foreach (var key in InputKeys)
            {
                if (
                    kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                    && kv.Value is string s
                    && !string.IsNullOrWhiteSpace(s)
                )
                {
                    id = s;
                    return true;
                }
            }
        }

        foreach (var kv in args)
        {
            var v = kv.Value;
            if (v is null)
                continue;

            var type = v.GetType();
            foreach (var key in InputKeys)
            {
                var prop = type.GetProperty(
                    key,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
                );
                if (prop?.GetValue(v) is string s && !string.IsNullOrWhiteSpace(s))
                {
                    id = s;
                    return true;
                }
            }
        }

        id = null;
        return false;
    }

    private static bool TryFromRoute(ActionExecutingContext ctx, out string? id)
    {
        foreach (var key in InputKeys)
        {
            if (
                ctx.RouteData.Values.TryGetValue(key, out var val)
                && val is string s
                && !string.IsNullOrWhiteSpace(s)
            )
            {
                id = s;
                return true;
            }
        }

        id = null;
        return false;
    }

    private static bool TryFromQuery(HttpRequest req, out string? id)
    {
        foreach (var key in InputKeys)
        {
            var val = req.Query[key];
            if (!string.IsNullOrWhiteSpace(val))
            {
                id = val.ToString();
                return true;
            }
        }

        id = null;
        return false;
    }
}
