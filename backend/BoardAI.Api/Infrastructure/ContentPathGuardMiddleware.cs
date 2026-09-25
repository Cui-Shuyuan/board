using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace BoardAI.Api.Infrastructure;

/// <summary>
/// Rejects traversal-looking content download URLs before endpoint routing.
///
/// Kestrel may normalise a literal "/../" path before the controller sees it,
/// so route-value validation alone can turn a traversal attempt into an empty
/// 404.  This middleware checks the original request target instead and returns
/// a clear 400 for content API paths.
/// </summary>
public class ContentPathGuardMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RequestDelegate _next;

    public ContentPathGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path.Value ?? "";
        var queryIndex = rawTarget.IndexOf('?');
        var rawPath = queryIndex >= 0 ? rawTarget[..queryIndex] : rawTarget;

        if (rawPath.StartsWith("/api/content/", StringComparison.OrdinalIgnoreCase) &&
            HasTraversalToken(rawPath))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { message = "path traversal is not allowed" }, JsonOptions));
            return;
        }

        await _next(context);
    }

    private static bool HasTraversalToken(string rawPath)
    {
        foreach (var segment in rawPath.Split('/'))
        {
            if (segment.Length == 0) continue;
            if (segment == "." || segment == "..") return true;
            if (segment.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
                segment.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
                segment.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
