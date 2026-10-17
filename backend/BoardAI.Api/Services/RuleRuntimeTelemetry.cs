using System.Collections.Concurrent;
using System.Diagnostics;

namespace BoardAI.Api.Services;

/// <summary>
/// 请求内轻量 telemetry scope。内部服务用异步安全的作用域记录 search/read/
/// navigation/assembly/serialization 等 span；最终由 orchestrator 合并到 trace。
/// </summary>
internal static class RuleRuntimeTelemetry
{
    private static readonly AsyncLocal<Collector?> CurrentCollector = new();

    public static IDisposable Begin(out Collector collector)
    {
        collector = new Collector();
        var previous = CurrentCollector.Value;
        CurrentCollector.Value = collector;
        return new Scope(previous);
    }

    public static IDisposable Measure(string category, string name)
    {
        var collector = CurrentCollector.Value;
        if (collector == null) return NoopScope.Instance;
        return new MeasureScope(collector, category, name);
    }

    internal sealed class Collector
    {
        private readonly long _startTimestamp = Stopwatch.GetTimestamp();
        private readonly ConcurrentQueue<TelemetrySpan> _spans = new();

        public IReadOnlyList<TelemetrySpan> Snapshot() => _spans.ToArray();

        public void Add(string category, string name, long startTimestamp, long endTimestamp)
        {
            var frequency = (double)Stopwatch.Frequency;
            _spans.Enqueue(new TelemetrySpan
            {
                Category = category,
                Name = name,
                StartMs = (startTimestamp - _startTimestamp) * 1000d / frequency,
                DurationMs = Math.Max(0, endTimestamp - startTimestamp) * 1000d / frequency
            });
        }
    }

    internal sealed class TelemetrySpan
    {
        public string Category { get; init; } = "";
        public string Name { get; init; } = "";
        public double StartMs { get; init; }
        public double DurationMs { get; init; }
    }

    private sealed class MeasureScope : IDisposable
    {
        private readonly Collector _collector;
        private readonly string _category;
        private readonly string _name;
        private readonly long _startTimestamp;
        private bool _disposed;

        public MeasureScope(Collector collector, string category, string name)
        {
            _collector = collector;
            _category = category;
            _name = name;
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _collector.Add(_category, _name, _startTimestamp, Stopwatch.GetTimestamp());
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Collector? _previous;
        private bool _disposed;

        public Scope(Collector? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentCollector.Value = _previous;
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
