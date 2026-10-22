using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BoardAI.Api.Models;
using Microsoft.Extensions.Logging;

namespace BoardAI.Api.Services;

/// <summary>
/// Rule content facade. It owns two separately tracked memory cache layers:
/// one shared ontology snapshot and one LRU set of per-game snapshots. A game
/// snapshot references the shared ontology object but never accounts its bytes
/// again. Eviction only drops cache references; in-flight requests keep their
/// snapshot and therefore can never observe a partially removed/Disposed graph.
///
/// Freshness is based on file metadata (existence, length, mtime) and, for the
/// final content identity, on a SHA-256 over the exact bytes that were parsed.
/// Disk cache identity uses that content hash rather than game name or mtime.
/// </summary>
public sealed class RulesContentStore : IDisposable
{
    private const int ReadRetryCount = 80;
    private const int ReadRetryDelayMilliseconds = 2;
    private const int MaxSeenDocumentSignatures = 256;
    private const int StripedLoadLockCount = 64;

    private readonly string _basePath;
    private readonly RulesMemoryCacheOptions _cacheOptions;
    private readonly RulesCacheTelemetry _telemetry = new();
    private readonly ILogger? _logger;
    private readonly Action? _onDocumentChanged;
    private readonly object _gate = new();
    private readonly object _ontologyLoadLock = new();
    private readonly object[] _gameLoadLocks = Enumerable.Range(0, StripedLoadLockCount)
        .Select(_ => new object())
        .ToArray();
    private readonly Dictionary<string, GameCacheEntry> _games = new(StringComparer.Ordinal);
    private readonly LinkedList<GameCacheEntry> _gameLru = new();
    private readonly Dictionary<string, string> _syntheticDocuments = new(StringComparer.Ordinal);
    private readonly object _syntheticGate = new();
    private readonly Dictionary<string, DocumentSignature> _seenDirectSignatures = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenDirectSignatureOrder = new();
    private readonly object _seenDirectSignaturesGate = new();

    private OntologyCacheEntry? _ontology;
    private long _estimatedManagedBytes;
    private long _revision;
    private long _syntheticEpoch;
    private bool _disposed;

    public RulesContentStore(string basePath, Action? onDocumentChanged = null)
        : this(
            basePath,
            new RulesOptions { BasePath = basePath },
            onDocumentChanged,
            logger: null,
            timeProvider: null)
    {
    }

    internal RulesContentStore(
        string basePath,
        RulesOptions options,
        Action? onDocumentChanged = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.MemoryCache.Validate();
        _basePath = Path.GetFullPath(basePath);
        _cacheOptions = options.MemoryCache;
        _onDocumentChanged = onDocumentChanged;
        _logger = logger;
        _ = timeProvider ?? TimeProvider.System;
    }

    /// <summary>当前内容代次；用于诊断与测试。</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>
    /// 返回当前游戏规则快照。命中时只检查文件签名；冷加载或规则变化时读取所有
    /// 相关文档并验证读取前后签名一致，然后原子替换缓存中的快照引用。
    /// </summary>
    public RulesGameSnapshot GetSnapshot(string game)
    {
        RulesPathSafety.ValidateGameId(game);
        ThrowIfDisposed();

        var quickSignature = CaptureCombinedSignature(game);
        lock (_gate)
        {
            if (_games.TryGetValue(game, out var hit) && hit.Signature == quickSignature)
            {
                TouchGameLocked(hit);
                _telemetry.RecordMemoryHit();
                return hit.Snapshot;
            }
        }

        var loadLock = _gameLoadLocks[(StringComparer.Ordinal.GetHashCode(game) & int.MaxValue) % StripedLoadLockCount];
        lock (loadLock)
        {
            // Double-check after waiting for a concurrent cold load. This is the
            // coalescing point: only one builder per game/content signature wins.
            var signature = CaptureCombinedSignature(game);
            lock (_gate)
            {
                if (_games.TryGetValue(game, out var hit) && hit.Signature == signature)
                {
                    TouchGameLocked(hit);
                    _telemetry.RecordMemoryHit();
                    return hit.Snapshot;
                }
            }

            _telemetry.RecordMemoryMiss();
            var started = Stopwatch.GetTimestamp();
            var (snapshot, builtSignature) = BuildGameSnapshot(game);
            var elapsed = Stopwatch.GetElapsedTime(started);
            _telemetry.RecordRebuild(elapsed);

            if (!_cacheOptions.Enabled)
                return snapshot;

            lock (_gate)
            {
                ThrowIfDisposed();
                CacheGameSnapshotLocked(game, snapshot, builtSignature);
            }

            return snapshot;
        }
    }

    /// <summary>读取必需的本体 concepts 文档；不存在时由加载器抛 FileNotFoundException。</summary>
    public JsonDocument LoadOntology()
    {
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null) return snapshot.Ontology;
        return LoadDirect(OntologyPath, required: true)!;
    }

    /// <summary>读取游戏 concepts 文档；不存在时返回 null。</summary>
    public JsonDocument? LoadGameConcepts(string game)
    {
        RulesPathSafety.ValidateGameId(game);
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null && snapshot.Game == game) return snapshot.GameConcepts;
        return LoadDirect(GameConceptsPath(game), required: false);
    }

    /// <summary>读取本体 flow 文档；不存在时返回 null。</summary>
    public JsonDocument? LoadOntologyFlow()
    {
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null) return snapshot.OntologyFlow;
        return LoadDirect(OntologyFlowPath, required: false);
    }

    /// <summary>读取游戏 flow 文档；不存在时返回 null。</summary>
    public JsonDocument? LoadGameFlow(string game)
    {
        RulesPathSafety.ValidateGameId(game);
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null && snapshot.Game == game) return snapshot.GameFlow;
        return LoadDirect(GameFlowPath(game), required: false);
    }

    /// <summary>读取游戏 instances 文档；不存在时返回 null。</summary>
    public JsonDocument? LoadGameInstances(string game)
    {
        RulesPathSafety.ValidateGameId(game);
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null && snapshot.Game == game) return snapshot.GameInstances;
        return LoadDirect(GameInstancesPath(game), required: false);
    }

    /// <summary>测试注入：将相对路径文档写入 store 内存，不访问磁盘。</summary>
    internal void SetDocumentForTesting(string relativePath, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var path = NormalizePath(relativePath);
        lock (_syntheticGate)
        {
            _syntheticDocuments[path] = json;
            _syntheticEpoch++;
        }
        Interlocked.Increment(ref _revision);
        _onDocumentChanged?.Invoke();
    }

    /// <summary>返回当前内容代次；顺带探测当前游戏的文档签名用于诊断。</summary>
    public long GetRevision(string game)
    {
        RulesPathSafety.ValidateGameId(game);
        _ = CaptureCombinedSignature(game);
        return Revision;
    }

    /// <summary>列出 content/games 下的游戏目录，忽略普通文件。</summary>
    public IReadOnlyList<string> GetGames()
    {
        var gamesDir = Path.Combine(_basePath, "content", "games");
        if (!Directory.Exists(gamesDir)) return Array.Empty<string>();
        return Directory.GetDirectories(gamesDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n) && IsSafeGameId(n!))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList()!;
    }

    public RulesMemoryCacheStats GetMemoryStats()
    {
        lock (_gate)
        {
            _estimatedManagedBytes = RecalculateManagedBytesLocked();
            var derived = _games.Values.Sum(g => g.Snapshot.DerivedCacheEntryCount);
            if (_ontology != null)
                derived += _ontology.Snapshot.DerivedCacheEntryCount;

            return _telemetry.ToStats(
                _cacheOptions,
                _estimatedManagedBytes,
                _games.Count,
                _ontology == null ? 0 : 1,
                derived);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Do not Dispose cached JsonDocuments here. A concurrently finishing
            // request may still hold the snapshot; dropping references is safe and
            // lets GC reclaim after the last request/result reference disappears.
            _games.Clear();
            _gameLru.Clear();
            _ontology = null;
            _estimatedManagedBytes = 0;
        }
    }

    private (RulesGameSnapshot Snapshot, string Signature) BuildGameSnapshot(string game)
    {
        var attempts = 0;
        while (true)
        {
            attempts++;
            var ontology = GetOntologySnapshot();
            var ontologySignature = ontology.Signature;

            var specs = new[]
            {
                new DocumentSpec(GameConceptsPath(game), Required: false),
                new DocumentSpec(GameInstancesPath(game), Required: false),
                new DocumentSpec(GameFlowPath(game), Required: false)
            };

            JsonDocument?[] docs;
            string gameSignature;
            try
            {
                (docs, gameSignature) = ReadDocumentsAtomically(specs);
            }
            catch (Exception ex) when ((ex is IOException or JsonException) && attempts < ReadRetryCount)
            {
                Thread.Sleep(ReadRetryDelayMilliseconds);
                continue;
            }

            var ontologyAfter = CaptureOntologySignature();
            if (!string.Equals(ontologySignature, ontologyAfter, StringComparison.Ordinal))
            {
                foreach (var doc in docs) doc?.Dispose();
                ontology = GetOntologySnapshot();
                if (attempts >= ReadRetryCount)
                    throw new IOException($"Rules changed repeatedly while building snapshot for '{game}'.");
                continue;
            }

            var version = RulesGameSnapshot.ComputeVersion(
                game,
                ontology.Ontology,
                docs[0],
                docs[1],
                docs[2],
                ontology.OntologyFlow);

            var rawBytes = RulesCacheSizeEstimator.EstimateDocument(docs[0])
                           + RulesCacheSizeEstimator.EstimateDocument(docs[1])
                           + RulesCacheSizeEstimator.EstimateDocument(docs[2]);

            var combinedSignature = ontology.Signature + "||" + gameSignature;
            var snapshot = new RulesGameSnapshot(
                game,
                Revision,
                version,
                ontology,
                docs[0],
                docs[1],
                docs[2],
                rawBytes,
                _cacheOptions.MaxDerivedEntriesPerGame,
                (source, _) => OnDerivedSizeAdded(source));
            return (snapshot, combinedSignature);
        }
    }

    private RulesOntologySnapshot GetOntologySnapshot()
    {
        var quickSignature = CaptureOntologySignature();
        lock (_gate)
        {
            if (_ontology != null && _ontology.Signature == quickSignature)
            {
                _telemetry.RecordOntologyHit();
                return _ontology.Snapshot;
            }
        }

        lock (_ontologyLoadLock)
        {
            var signature = CaptureOntologySignature();
            lock (_gate)
            {
                if (_ontology != null && _ontology.Signature == signature)
                {
                    _telemetry.RecordOntologyHit();
                    return _ontology.Snapshot;
                }
            }

            _telemetry.RecordOntologyMiss();
            var specs = new[]
            {
                new DocumentSpec(OntologyPath, Required: true),
                new DocumentSpec(OntologyFlowPath, Required: false)
            };
            var (docs, builtSignature) = ReadDocumentsAtomically(specs);
            var snapshot = new RulesOntologySnapshot(
                builtSignature,
                docs[0]!,
                docs[1],
                _cacheOptions.MaxDerivedEntriesPerGame,
                (source, _) => OnOntologyDerivedSizeAdded(source));

            if (!_cacheOptions.Enabled)
                return snapshot;

            lock (_gate)
            {
                ThrowIfDisposed();
                var old = _ontology;
                if (old != null && !ReferenceEquals(old.Snapshot, snapshot))
                    EvictGamesForOntologyChangeLocked(old.Snapshot);

                _ontology = new OntologyCacheEntry(snapshot, builtSignature);

                if (snapshot.EstimatedBytes > _cacheOptions.BudgetBytes)
                {
                    _telemetry.RecordOversizeRejected();
                    _ontology = null;
                    _estimatedManagedBytes = RecalculateManagedBytesLocked();
                }
                else
                {
                    _estimatedManagedBytes = RecalculateManagedBytesLocked();
                    EnforceMemoryBudgetLocked();
                }
            }

            return snapshot;
        }
    }

    private void CacheGameSnapshotLocked(string game, RulesGameSnapshot snapshot, string signature)
    {
        if (snapshot.EstimatedBytes > _cacheOptions.BudgetBytes
            || _ontology == null
            || !ReferenceEquals(_ontology.Snapshot, snapshot.OntologySnapshot))
        {
            // A game snapshot whose shared ontology is not itself admitted would
            // keep an unaccounted ontology graph alive. Compute it for this request
            // but do not retain it as a long-lived cache entry.
            _telemetry.RecordOversizeRejected();
            return;
        }

        if (_games.TryGetValue(game, out var old))
        {
            if (old.Node.List != null)
                _gameLru.Remove(old.Node);
        }

        var entry = new GameCacheEntry(snapshot, signature);
        entry.Node = _gameLru.AddLast(entry);
        _games[game] = entry;
        _estimatedManagedBytes = RecalculateManagedBytesLocked();
        EnforceMemoryBudgetLocked();
    }

    /// <summary>
    /// Derived-cache growth must be attributed to the exact snapshot that grew.
    /// An evicted/in-flight old snapshot may still be used by a request, but its
    /// late growth must not alter the accounting of a newer snapshot for the
    /// same game (or trigger eviction of unrelated work).
    /// </summary>
    private void OnDerivedSizeAdded(RulesGameSnapshot source)
    {
        if (!_cacheOptions.Enabled) return;
        lock (_gate)
        {
            if (!_games.TryGetValue(source.Game, out var entry)
                || !ReferenceEquals(entry.Snapshot, source))
            {
                return;
            }

            _estimatedManagedBytes = RecalculateManagedBytesLocked();
            EnforceMemoryBudgetLocked();
        }
    }

    private void OnOntologyDerivedSizeAdded(RulesOntologySnapshot source)
    {
        if (!_cacheOptions.Enabled) return;
        lock (_gate)
        {
            if (_ontology == null || !ReferenceEquals(_ontology.Snapshot, source))
                return;

            _estimatedManagedBytes = RecalculateManagedBytesLocked();
            EnforceMemoryBudgetLocked();
        }
    }

    /// <summary>
    /// Recomputes the managed-cache estimate from the objects actually retained
    /// by the cache. This makes derived-cache growth and eviction use the same
    /// authoritative snapshot sizes instead of independently accumulated deltas.
    /// </summary>
    private long RecalculateManagedBytesLocked()
    {
        var total = _ontology?.Snapshot.EstimatedBytes ?? 0;
        foreach (var entry in _games.Values)
            total += entry.Snapshot.EstimatedBytes;
        return total;
    }

    private void EvictGamesForOntologyChangeLocked(RulesOntologySnapshot oldOntology)
    {
        var staleGames = _games.Values
            .Where(entry => ReferenceEquals(entry.Snapshot.OntologySnapshot, oldOntology))
            .Select(entry => entry.Snapshot.Game)
            .ToArray();

        foreach (var game in staleGames)
        {
            if (!_games.Remove(game, out var entry)) continue;
            if (entry.Node.List != null)
                _gameLru.Remove(entry.Node);
            _telemetry.RecordEviction();
        }

        _estimatedManagedBytes = RecalculateManagedBytesLocked();
    }

    /// <summary>
    /// Caller must hold _gate. Evicts least-recently-used games first. The shared
    /// ontology is evicted only after no game can be evicted; old snapshots remain
    /// valid in any request that already holds them.
    /// </summary>
    private void EnforceMemoryBudgetLocked()
    {
        var options = _cacheOptions;
        if (!options.Enabled) return;

        _estimatedManagedBytes = RecalculateManagedBytesLocked();
        var high = options.BudgetBytes * options.HighWatermark;
        if (_estimatedManagedBytes <= high) return;
        var target = options.BudgetBytes * options.TargetWatermark;

        while (_estimatedManagedBytes > target && _gameLru.First != null)
        {
            var oldest = _gameLru.First.Value;
            _gameLru.RemoveFirst();
            oldest.Node = null!;
            if (_games.Remove(oldest.Snapshot.Game))
            {
                _telemetry.RecordEviction();
            }
            _estimatedManagedBytes = RecalculateManagedBytesLocked();
        }

        if (_estimatedManagedBytes > target && _ontology != null)
        {
            _ontology = null;
            _telemetry.RecordEviction();
            _estimatedManagedBytes = RecalculateManagedBytesLocked();
        }
    }

    private void TouchGameLocked(GameCacheEntry entry)
    {
        if (entry.Node.List == null) return;
        _gameLru.Remove(entry.Node);
        _gameLru.AddLast(entry.Node);
    }

    private JsonDocument? LoadDirect(string absolutePath, bool required)
    {
        ThrowIfDisposed();
        var path = NormalizePath(absolutePath);
        var signature = CaptureDocumentSignature(path, required);
        var (hadPrevious, changed) = RecordDirectSignature(path, signature);

        var document = ReadDocumentWithRetry(path, required);
        if (!hadPrevious || changed)
            Interlocked.Increment(ref _revision);
        if (changed)
            NotifyExternalDocumentChange(path);
        return document;
    }

    private (bool HadPrevious, bool Changed) RecordDirectSignature(string path, DocumentSignature signature)
    {
        lock (_seenDirectSignaturesGate)
        {
            var hadPrevious = _seenDirectSignatures.TryGetValue(path, out var previous);
            _seenDirectSignatures[path] = signature;
            _seenDirectSignatureOrder.Enqueue(path);
            TrimSeenSignaturesLocked();
            return (hadPrevious, hadPrevious && !previous.Equals(signature));
        }
    }

    private void TrimSeenSignaturesLocked()
    {
        while (_seenDirectSignatureOrder.Count > MaxSeenDocumentSignatures)
        {
            var oldest = _seenDirectSignatureOrder.Dequeue();
            if (_seenDirectSignatureOrder.Contains(oldest)) continue;
            _seenDirectSignatures.Remove(oldest);
        }
    }

    private void NotifyExternalDocumentChange(string path)
    {
        Interlocked.Increment(ref _revision);
        _logger?.LogInformation("Rule document changed: {RuleDocument}", path);
        _onDocumentChanged?.Invoke();
    }

    private (JsonDocument?[] Documents, string Signature) ReadDocumentsAtomically(
        IReadOnlyList<DocumentSpec> specs)
    {
        for (var attempt = 0; attempt <= ReadRetryCount; attempt++)
        {
            var before = specs.Select(s => CaptureDocumentSignature(s.Path, s.Required)).ToArray();
            var signatureBefore = BuildSignature(specs, before);
            var documents = new JsonDocument?[specs.Count];
            try
            {
                for (var i = 0; i < specs.Count; i++)
                    documents[i] = ReadDocumentWithRetry(specs[i].Path, specs[i].Required);

                var after = specs.Select(s => CaptureDocumentSignature(s.Path, s.Required)).ToArray();
                var signatureAfter = BuildSignature(specs, after);
                if (string.Equals(signatureBefore, signatureAfter, StringComparison.Ordinal))
                    return (documents, signatureAfter);

                foreach (var document in documents)
                    document?.Dispose();
            }
            catch (Exception ex) when (IsRetryableDocumentError(ex) && attempt < ReadRetryCount)
            {
                foreach (var document in documents)
                    document?.Dispose();
                Thread.Sleep(ReadRetryDelayMilliseconds);
            }
            catch
            {
                foreach (var document in documents)
                    document?.Dispose();
                throw;
            }

            Thread.Sleep(ReadRetryDelayMilliseconds);
        }

        throw new IOException("Rules changed repeatedly while reading a consistent document set.");
    }

    private JsonDocument? ReadDocumentWithRetry(string absolutePath, bool required)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= ReadRetryCount; attempt++)
        {
            try
            {
                lock (_syntheticGate)
                {
                    if (_syntheticDocuments.TryGetValue(absolutePath, out var syntheticJson))
                        return JsonDocument.Parse(syntheticJson, new JsonDocumentOptions { AllowTrailingCommas = true });
                }

                if (!File.Exists(absolutePath))
                {
                    if (required)
                        throw new FileNotFoundException($"Could not find file '{absolutePath}'.", absolutePath);
                    return null;
                }

                using var stream = new FileStream(
                    absolutePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var json = reader.ReadToEnd();
                return JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            }
            catch (Exception ex) when (IsRetryableDocumentError(ex) && attempt < ReadRetryCount)
            {
                last = ex;
                Thread.Sleep(ReadRetryDelayMilliseconds);
            }
        }

        throw last ?? new IOException($"Could not read rule document '{absolutePath}'.");
    }

    private DocumentSignature CaptureDocumentSignature(string absolutePath, bool required)
    {
        lock (_syntheticGate)
        {
            if (_syntheticDocuments.TryGetValue(absolutePath, out var json))
                return new DocumentSignature(true, json.Length, _syntheticEpoch);
        }

        var info = new FileInfo(absolutePath);
        if (!info.Exists)
        {
            if (required)
                throw new FileNotFoundException($"Could not find file '{absolutePath}'.", absolutePath);
            return new DocumentSignature(false, 0, 0);
        }

        return new DocumentSignature(true, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private string CaptureOntologySignature()
    {
        var specs = new[]
        {
            new DocumentSpec(OntologyPath, Required: true),
            new DocumentSpec(OntologyFlowPath, Required: false)
        };
        var signatures = specs.Select(s => CaptureDocumentSignature(s.Path, s.Required)).ToArray();
        return BuildSignature(specs, signatures);
    }

    private string CaptureCombinedSignature(string game)
    {
        var ontologySignature = CaptureOntologySignature();
        var specs = new[]
        {
            new DocumentSpec(GameConceptsPath(game), Required: false),
            new DocumentSpec(GameInstancesPath(game), Required: false),
            new DocumentSpec(GameFlowPath(game), Required: false)
        };
        var signatures = specs.Select(s => CaptureDocumentSignature(s.Path, s.Required)).ToArray();
        return ontologySignature + "||" + BuildSignature(specs, signatures);
    }

    private static string BuildSignature(IReadOnlyList<DocumentSpec> specs, IReadOnlyList<DocumentSignature> signatures)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < specs.Count; i++)
        {
            builder.Append(specs[i].Path).Append('=')
                .Append(signatures[i].Exists ? '1' : '0').Append(':')
                .Append(signatures[i].Length).Append(':')
                .Append(signatures[i].LastWriteTicks).Append('\n');
        }
        return builder.ToString();
    }

    private static bool IsRetryableDocumentError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or JsonException;

    private static bool IsSafeGameId(string game)
    {
        try
        {
            RulesPathSafety.ValidateGameId(game);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RulesContentStore));
    }

    private string OntologyPath => Path.Combine(_basePath, "content", "ontology", "concepts.json");
    private string OntologyFlowPath => Path.Combine(_basePath, "content", "ontology", "flow.json");
    private string GameConceptsPath(string game) => Path.Combine(_basePath, "content", "games", game, "concepts.json");
    private string GameInstancesPath(string game) => Path.Combine(_basePath, "content", "games", game, "instances.json");
    private string GameFlowPath(string game) => Path.Combine(_basePath, "content", "games", game, "flow.json");

    private string NormalizePath(string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_basePath, path));

    private readonly record struct DocumentSpec(string Path, bool Required);
    private readonly record struct DocumentSignature(bool Exists, long Length, long LastWriteTicks);

    private sealed class GameCacheEntry
    {
        public GameCacheEntry(RulesGameSnapshot snapshot, string signature)
        {
            Snapshot = snapshot;
            Signature = signature;
        }

        public RulesGameSnapshot Snapshot { get; }
        public string Signature { get; }
        public LinkedListNode<GameCacheEntry> Node { get; set; } = null!;
    }

    private sealed record OntologyCacheEntry(RulesOntologySnapshot Snapshot, string Signature);
}
