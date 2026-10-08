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
        // 保留连接和请求序号；只取末六位会让不同连接都显示为 000001。
        var requestId = context.TraceIdentifier;

        // 注入 log scope：后续同一请求内的所有 ILogger 输出都会携带这个 ID
        using (_logger.BeginScope("rid:{RequestId}", requestId))
        {
            await _next(context);
        }
    }
}
