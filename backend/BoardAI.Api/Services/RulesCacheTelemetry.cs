using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

/// <summary>
/// Low-overhead counters shared by the rule cache layers. Every increment is
/// atomic; no counter path writes a log line per cache hit.
/// </summary>
internal sealed class RulesCacheTelemetry
{
    private long _memoryHits;
    private long _memoryMisses;
    private long _ontologyHits;
    private long _ontologyMisses;
    private long _rebuilds;
    private long _rebuildTicks;
    private long _evictions;
    private long _oversizeRejected;

    public void RecordMemoryHit() => Interlocked.Increment(ref _memoryHits);
    public void RecordMemoryMiss() => Interlocked.Increment(ref _memoryMisses);
    public void RecordOntologyHit() => Interlocked.Increment(ref _ontologyHits);
    public void RecordOntologyMiss() => Interlocked.Increment(ref _ontologyMisses);
    public void RecordEviction() => Interlocked.Increment(ref _evictions);
    public void RecordOversizeRejected() => Interlocked.Increment(ref _oversizeRejected);

    public void RecordRebuild(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _rebuilds);
        Interlocked.Add(ref _rebuildTicks, elapsed.Ticks);
    }

    public RulesMemoryCacheStats ToStats(
        RulesMemoryCacheOptions options,
        long estimatedManagedBytes,
        int cachedGameCount,
        int cachedOntologyCount,
        long derivedCacheEntries)
    {
        var rebuilds = Interlocked.Read(ref _rebuilds);
        var elapsedTicks = Interlocked.Read(ref _rebuildTicks);
        var averageMilliseconds = rebuilds == 0
            ? 0
            : TimeSpan.FromTicks(elapsedTicks).TotalMilliseconds / rebuilds;

        return new RulesMemoryCacheStats(
            options.Enabled,
            options.BudgetBytes,
            estimatedManagedBytes,
            cachedGameCount,
            cachedOntologyCount,
            Interlocked.Read(ref _memoryHits),
            Interlocked.Read(ref _memoryMisses),
            Interlocked.Read(ref _ontologyHits),
            Interlocked.Read(ref _ontologyMisses),
            rebuilds,
            averageMilliseconds,
            Interlocked.Read(ref _evictions),
            Interlocked.Read(ref _oversizeRejected),
            derivedCacheEntries);
    }
}
