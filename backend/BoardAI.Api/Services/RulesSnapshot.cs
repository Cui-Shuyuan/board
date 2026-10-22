using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// An immutable, independently owned ontology snapshot. Game snapshots share
/// this object so the ontology JSON is counted once in the memory cache budget.
/// It is never disposed on eviction because an in-flight request may still hold
/// a reference; dropping the cache reference lets GC reclaim it after requests end.
/// </summary>
public sealed class RulesOntologySnapshot
{
    private readonly ConcurrentDictionary<string, Lazy<CacheValue>> _cache = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private readonly Action<RulesOntologySnapshot, long>? _onDerivedSizeAdded;
    private long _derivedEstimatedBytes;

    internal RulesOntologySnapshot(
        string signature,
        JsonDocument ontology,
        JsonDocument? ontologyFlow,
        int maxEntries,
        Action<RulesOntologySnapshot, long>? onDerivedSizeAdded)
    {
        Signature = signature;
        Ontology = ontology;
        OntologyFlow = ontologyFlow;
        _maxEntries = maxEntries;
        _onDerivedSizeAdded = onDerivedSizeAdded;
        EstimatedRawBytes = RulesCacheSizeEstimator.EstimateDocument(ontology)
                            + RulesCacheSizeEstimator.EstimateDocument(ontologyFlow)
                            + RulesCacheSizeEstimator.EstimateString(signature);
    }

    internal string Signature { get; }
    public JsonDocument Ontology { get; }
    public JsonDocument? OntologyFlow { get; }
    internal long EstimatedRawBytes { get; }
    internal long EstimatedBytes => EstimatedRawBytes + Interlocked.Read(ref _derivedEstimatedBytes);
    internal int DerivedCacheEntryCount => _cache.Count;

    internal T GetOrAdd<T>(string key, Func<T> factory, Func<T, long>? estimator = null) where T : class
    {
        if (_cache.Count >= _maxEntries && !_cache.ContainsKey(key))
            return factory();

        var lazy = _cache.GetOrAdd(
            key,
            _ => new Lazy<CacheValue>(
                () =>
                {
                    var value = factory();
                    var estimated = estimator?.Invoke(value)
                                     ?? RulesCacheSizeEstimator.EstimateObject(value);
                    Interlocked.Add(ref _derivedEstimatedBytes, estimated);
                    _onDerivedSizeAdded?.Invoke(this, estimated);
                    return new CacheValue(value, estimated);
                },
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return (T)lazy.Value.Value;
        }
        catch
        {
            ((ICollection<KeyValuePair<string, Lazy<CacheValue>>>)_cache)
                .Remove(new KeyValuePair<string, Lazy<CacheValue>>(key, lazy));
            throw;
        }
    }

    private sealed record CacheValue(object Value, long EstimatedBytes);
}

/// <summary>
/// 一次规则请求绑定的一版不可变内容快照。快照内的 JsonDocument 是独立读取结果，
/// 生命周期由请求/结果引用和内存缓存共同持有；交换或淘汰快照只丢弃缓存引用，
/// 不 Dispose 仍可能在飞行请求中使用的旧文档。
/// 同一快照上构建出的名称/流程/类型/事实/搜索材料天然属于同一规则版本。
/// </summary>
public sealed class RulesGameSnapshot
{
    private readonly ConcurrentDictionary<string, Lazy<CacheValue>> _cache = new(StringComparer.Ordinal);
    private readonly int _maxDerivedEntries;
    private readonly Action<RulesGameSnapshot, long>? _onDerivedSizeAdded;
    private long _derivedEstimatedBytes;

    /// <summary>
    /// Compatibility constructor for callers that only have raw documents. New
    /// production code goes through RulesContentStore and shares an ontology entry.
    /// </summary>
    public RulesGameSnapshot(
        string game,
        long revision,
        string version,
        JsonDocument ontology,
        JsonDocument? gameConcepts,
        JsonDocument? gameInstances,
        JsonDocument? gameFlow,
        JsonDocument? ontologyFlow)
        : this(
            game,
            revision,
            version,
            new RulesOntologySnapshot(
                "standalone",
                ontology,
                ontologyFlow,
                32,
                onDerivedSizeAdded: null),
            gameConcepts,
            gameInstances,
            gameFlow,
            rawGameDocumentBytes: 0,
            maxDerivedEntries: 32,
            onDerivedSizeAdded: null)
    {
    }

    internal RulesGameSnapshot(
        string game,
        long revision,
        string version,
        RulesOntologySnapshot ontology,
        JsonDocument? gameConcepts,
        JsonDocument? gameInstances,
        JsonDocument? gameFlow,
        long rawGameDocumentBytes,
        int maxDerivedEntries,
        Action<RulesGameSnapshot, long>? onDerivedSizeAdded)
    {
        Game = game;
        Revision = revision;
        Version = version;
        OntologySnapshot = ontology;
        GameConcepts = gameConcepts;
        GameInstances = gameInstances;
        GameFlow = gameFlow;
        _maxDerivedEntries = Math.Max(1, maxDerivedEntries);
        _onDerivedSizeAdded = onDerivedSizeAdded;
        EstimatedRawBytes = rawGameDocumentBytes
                            + RulesCacheSizeEstimator.EstimateString(version)
                            + RulesCacheSizeEstimator.EstimateString(game);
    }

    public string Game { get; }
    public long Revision { get; }
    public string Version { get; }
    public JsonDocument Ontology => OntologySnapshot.Ontology;
    public JsonDocument? OntologyFlow => OntologySnapshot.OntologyFlow;
    public JsonDocument? GameConcepts { get; }
    public JsonDocument? GameInstances { get; }
    public JsonDocument? GameFlow { get; }

    internal RulesOntologySnapshot OntologySnapshot { get; }
    internal long EstimatedRawBytes { get; }
    internal long EstimatedBytes => EstimatedRawBytes + Interlocked.Read(ref _derivedEstimatedBytes);
    internal int DerivedCacheEntryCount => _cache.Count;

    /// <summary>在快照内一次性构建派生缓存；同一 key 并发只保留一个结果。</summary>
    public T GetOrAdd<T>(string key, Func<T> factory) where T : class
        => GetOrAdd(key, factory, estimator: null);

    internal T GetOrAdd<T>(string key, Func<T> factory, Func<T, long>? estimator) where T : class
    {
        if (_cache.Count >= _maxDerivedEntries && !_cache.ContainsKey(key))
            return factory();

        var lazy = _cache.GetOrAdd(
            key,
            _ => new Lazy<CacheValue>(
                () =>
                {
                    var value = factory();
                    var estimated = estimator?.Invoke(value)
                                     ?? RulesCacheSizeEstimator.EstimateObject(value);
                    Interlocked.Add(ref _derivedEstimatedBytes, estimated);
                    _onDerivedSizeAdded?.Invoke(this, estimated);
                    return new CacheValue(value, estimated);
                },
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return (T)lazy.Value.Value;
        }
        catch
        {
            // A failed build must not poison this snapshot's cache: a later request
            // with the same rules version is allowed to retry the computation.
            ((ICollection<KeyValuePair<string, Lazy<CacheValue>>>)_cache)
                .Remove(new KeyValuePair<string, Lazy<CacheValue>>(key, lazy));
            throw;
        }
    }

    /// <summary>计算并拼接影响本游戏规则语义的文件内容哈希；变更内容会产生新版本串。</summary>
    public static string ComputeVersion(
        string game,
        JsonDocument ontology,
        JsonDocument? gameConcepts,
        JsonDocument? gameInstances,
        JsonDocument? gameFlow,
        JsonDocument? ontologyFlow)
    {
        var builder = new StringBuilder();
        builder.Append("game=").Append(game).Append('\n');
        Append(builder, "ontology", ontology);
        Append(builder, "game_concepts", gameConcepts);
        Append(builder, "game_instances", gameInstances);
        Append(builder, "game_flow", gameFlow);
        Append(builder, "ontology_flow", ontologyFlow);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string name, JsonDocument? document)
    {
        builder.Append(name).Append('=');
        if (document == null)
        {
            builder.Append("<missing>");
        }
        else
        {
            builder.Append(document.RootElement.GetRawText());
        }
        builder.Append('\n');
    }

    private sealed record CacheValue(object Value, long EstimatedBytes);
}

/// <summary>
/// AsyncLocal 请求作用域：在单次后端调用期间，让规则内容读取和派生缓存看到同一快照。
/// </summary>
public static class RulesSnapshotScope
{
    private static readonly AsyncLocal<RulesGameSnapshot?> CurrentSnapshot = new();

    public static RulesGameSnapshot? Current => CurrentSnapshot.Value;

    public static IDisposable Enter(RulesGameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var previous = CurrentSnapshot.Value;
        CurrentSnapshot.Value = snapshot;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly RulesGameSnapshot? _previous;
        private bool _disposed;

        public Scope(RulesGameSnapshot? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            CurrentSnapshot.Value = _previous;
            _disposed = true;
        }
    }
}
