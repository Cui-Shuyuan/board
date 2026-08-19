using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Infrastructure;

/// <summary>
/// 自定义 Console Formatter，在每行日志中嵌入请求 ID，
/// 格式：HH:mm:ss.fff [rid:abc123] INFO Category message
/// </summary>
public sealed class RequestIdConsoleFormatter : ConsoleFormatter, IDisposable
{
    private readonly IDisposable? _optionsReloadToken;
    private SimpleConsoleFormatterOptions _options;

    private static readonly ConsoleColor[] LevelColors =
    [
        ConsoleColor.DarkGray,   // Trace
        ConsoleColor.DarkGray,   // Debug
        ConsoleColor.DarkGreen,  // Information
        ConsoleColor.DarkYellow, // Warning
        ConsoleColor.DarkRed,    // Error
        ConsoleColor.Red,        // Critical
        ConsoleColor.White,      // None
    ];

    public RequestIdConsoleFormatter(IOptionsMonitor<SimpleConsoleFormatterOptions> options)
        : base("requestId")
    {
        _optionsReloadToken = options.OnChange(o => _options = o);
        _options = options.CurrentValue;
    }

    public void Dispose() => _optionsReloadToken?.Dispose();

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var useColor = _options.ColorBehavior == LoggerColorBehavior.Enabled
                       || _options.ColorBehavior == LoggerColorBehavior.Default;

        // 1. 时间戳
        if (_options.TimestampFormat != null)
        {
            var ts = _options.UseUtcTimestamp
                ? DateTime.UtcNow.ToString(_options.TimestampFormat)
                : DateTime.Now.ToString(_options.TimestampFormat);
            textWriter.Write(ts);
        }

        // 2. 请求 ID（从 log scope 中提取）
        var rid = new Box<string>("-");
        scopeProvider?.ForEachScope((scope, box) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object>> dict)
            {
                foreach (var kv in dict)
                {
                    if (kv.Key == "RequestId" && kv.Value is string s && s.Length > 0)
                    {
                        box.Value = s;
                        return;
                    }
                }
            }
        }, rid);
        var requestId = rid.Value;

        textWriter.Write($"[{requestId}] ");

        // 4. 日志级别
        var level = logEntry.LogLevel;
        var levelStr = level switch
        {
            LogLevel.Trace => "TRCE",
            LogLevel.Debug => "DBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "EROR", // 4 字符对齐，EROR 比 ERROR 短但可辨识
            LogLevel.Critical => "CRIT",
            _ => "NONE"
        };

        if (useColor)
        {
            var fg = Console.ForegroundColor;
            Console.ForegroundColor = LevelColors[(int)level];
            textWriter.Write(levelStr);
            Console.ForegroundColor = fg;
        }
        else
        {
            textWriter.Write(levelStr);
        }

        textWriter.Write(' ');

        // 5. 类别
        textWriter.Write(logEntry.Category);
        textWriter.Write(' ');

        // 6. 消息
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        textWriter.WriteLine(message);

        // 7. 异常
        if (logEntry.Exception != null)
        {
            if (useColor)
            {
                var fg = Console.ForegroundColor;
                Console.ForegroundColor = LevelColors[(int)LogLevel.Error];
                textWriter.WriteLine(logEntry.Exception.ToString());
                Console.ForegroundColor = fg;
            }
            else
            {
                textWriter.WriteLine(logEntry.Exception.ToString());
            }
        }
    }

    /// <summary>可变的字符串包装器，供闭包回传值。</summary>
    private sealed class Box<T> where T : notnull
    {
        public T Value;
        public Box(T value) => Value = value;
    }
}
