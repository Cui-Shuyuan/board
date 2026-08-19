namespace BoardAI.Api.Infrastructure;

/// <summary>
/// 为每个 HTTP 请求生成一个短请求 ID，注入 log scope，
/// 使并发请求的日志行可以被区分追踪。
/// </summary>
public class RequestIdMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestIdMiddleware> _logger;

    public RequestIdMiddleware(RequestDelegate next, ILogger<RequestIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 从 TraceIdentifier 取后 6 位作为短请求 ID（唯一性足够用于日志区分）
        var traceId = context.TraceIdentifier;
        var shortId = traceId.Length > 6 ? traceId[^6..] : traceId;

        // 注入 log scope：后续同一请求内的所有 ILogger 输出都会携带这个 ID
        using (_logger.BeginScope("rid:{RequestId}", shortId))
        {
            await _next(context);
        }
    }
}
