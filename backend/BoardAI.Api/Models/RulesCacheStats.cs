namespace BoardAI.Api.Models;

public sealed record RulesCacheStats(
    RulesMemoryCacheStats Memory,
    RulesDiskCacheStats Disk,
    long ManagedHeapBytes,
    long ProcessWorkingSetBytes);

public sealed record RulesMemoryCacheStats(
    bool Enabled,
    long BudgetBytes,
    long EstimatedManagedBytes,
    int CachedGameCount,
    int CachedOntologyCount,
    long Hits,
    long Misses,
    long OntologyHits,
    long OntologyMisses,
    long Rebuilds,
    double RebuildAverageMilliseconds,
    long Evictions,
    long OversizeEntriesRejected,
    long DerivedCacheEntries);

public sealed record RulesDiskCacheStats(
    bool Enabled,
    string Directory,
    long BudgetBytes,
    long DiskUsageBytes,
    long Hits,
    long Misses,
    long ExpiredEntries,
    long CorruptEntries,
    long ReadFailures,
    long WriteFailures,
    long WriteSuccesses,
    long CleanupDeletes,
    int PendingWrites,
    long PendingBytes,
    long OversizeEntriesRejected);
