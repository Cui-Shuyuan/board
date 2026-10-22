namespace BoardAI.Api.Models;

/// <summary>
/// Bounded, process-local caches for rule data and derived search materials.
/// These budgets are cache budgets, not process memory limits. In-flight
/// requests keep their own snapshot references even after cache eviction.
/// </summary>
public sealed class RulesCacheOptions
{
    public RulesMemoryCacheOptions MemoryCache { get; set; } = new();
    public RulesDiskCacheOptions DiskCache { get; set; } = new();

    public void Validate()
    {
        MemoryCache.Validate();
        DiskCache.Validate();
    }
}

public sealed class RulesMemoryCacheOptions
{
    /// <summary>When false, no snapshot or derived material survives the request scope.</summary>
    public bool Enabled { get; set; } = true;

    public long BudgetBytes { get; set; } = 256L * 1024 * 1024;
    public double HighWatermark { get; set; } = 0.80;
    public double TargetWatermark { get; set; } = 0.60;
    public int MaxDerivedEntriesPerGame { get; set; } = 32;

    public void Validate()
    {
        if (BudgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(BudgetBytes), "Rules memory cache budget must be positive.");
        if (!double.IsFinite(HighWatermark) || HighWatermark <= 0 || HighWatermark > 1)
            throw new ArgumentOutOfRangeException(nameof(HighWatermark), "Rules memory cache high watermark must be in (0, 1].");
        if (!double.IsFinite(TargetWatermark) || TargetWatermark <= 0 || TargetWatermark >= HighWatermark)
            throw new ArgumentOutOfRangeException(nameof(TargetWatermark), "Rules memory cache target watermark must be in (0, highWatermark).");
        if (MaxDerivedEntriesPerGame <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxDerivedEntriesPerGame), "Max derived cache entries per game must be positive.");
    }
}

public sealed class RulesDiskCacheOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Absolute directory, or empty for the default ignored storage directory.</summary>
    public string Directory { get; set; } = string.Empty;

    public long BudgetBytes { get; set; } = 512L * 1024 * 1024;
    public int FlushIntervalSeconds { get; set; } = 300;
    public int ShutdownFlushTimeoutMilliseconds { get; set; } = 5_000;
    public int PendingWriteLimit { get; set; } = 64;
    public long PendingBytesLimit { get; set; } = 64L * 1024 * 1024;

    public void Validate()
    {
        if (BudgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(BudgetBytes), "Rules disk cache budget must be positive.");
        if (FlushIntervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(FlushIntervalSeconds), "Disk cache flush interval must be positive.");
        if (ShutdownFlushTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(ShutdownFlushTimeoutMilliseconds), "Shutdown flush timeout must be positive.");
        if (PendingWriteLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(PendingWriteLimit), "Pending write limit must be positive.");
        if (PendingBytesLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(PendingBytesLimit), "Pending bytes limit must be positive.");
    }
}
