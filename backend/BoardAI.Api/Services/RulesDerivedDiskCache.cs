using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoardAI.Api.Models;
using Microsoft.Extensions.Logging;

namespace BoardAI.Api.Services;

/// <summary>
/// Explicit, versioned on-disk cache for deterministic derived rule material.
/// Only allow-listed DTOs are serialized (currently the keyword corpus); service
/// objects, locks, Lazy/Task values, user questions and answers are never stored.
///
/// Cache files are keyed by a SHA-256 identity over game, format version,
/// algorithm/config version and the complete rule content hash. Payloads carry
/// their own hash so a valid-JSON but stale/corrupted body is rejected.
/// </summary>
public sealed class RulesDerivedDiskCache : IDisposable
{
    internal const string KeywordCorpusAlgorithmVersion = "keyword-corpus/2026-10-22/v1";
    internal const string KeywordCorpusConfigFingerprint =
        "types=ontology,objects,actions,triggers,conditions,top_level_refs,effects,modules,cards,continent_tiles,sites,chips,slots,flow;" +
        "id-lookup=summary-id;text=recursive-json-strings;media=raw-json";

    private const int CacheFormatVersion = 1;
    private const string KeywordCorpusKind = "keyword-corpus";
    private const long MinimumEntryBudget = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly RulesDiskCacheOptions _options;
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _root;
    private readonly string _keywordCorpusDirectory;
    private readonly object _pendingGate = new();
    private readonly Dictionary<string, PendingWrite> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly ITimer? _timer;

    private long _pendingBytes;
    private long _pendingSequence;
    private long _hits;
    private long _misses;
    private long _expired;
    private long _corrupt;
    private long _readFailures;
    private long _writeFailures;
    private long _writeSuccesses;
    private long _cleanupDeletes;
    private long _oversizeRejected;
    private long _diskUsageBytes;
    private long _readFailureLogs;
    private long _writeFailureLogs;
    private long _corruptLogs;
    private int _disposed;

    public RulesDerivedDiskCache(
        string basePath,
        RulesDiskCacheOptions options,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var configured = string.IsNullOrWhiteSpace(options.Directory)
            ? Path.Combine(basePath, "backend", "BoardAI.Api", "storage", "rules-derived-cache")
            : options.Directory;
        _root = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(basePath, configured));
        _keywordCorpusDirectory = RulesPathSafety.ResolveWithinRoot(
            _root,
            Path.Combine(_root, "keyword-corpus"));

        if (options.Enabled)
        {
            var interval = TimeSpan.FromSeconds(options.FlushIntervalSeconds);
            _timer = _timeProvider.CreateTimer(
                _ => _ = FlushSafelyAsync(),
                state: null,
                dueTime: interval,
                period: interval);
        }
    }

    public string DirectoryPath => _root;

    internal bool TryGetKeywordCorpus(
        string game,
        string rulesVersion,
        string? nameSpace,
        out RulesKeywordCorpusCacheDto? corpus)
    {
        corpus = null;
        if (!_options.Enabled) return false;
        RulesPathSafety.ValidateGameId(game);

        var normalizedNs = NormalizeNamespace(nameSpace);
        var identity = BuildKeywordCorpusIdentity(game, rulesVersion, normalizedNs);
        var path = GetKeywordCorpusPath(identity.Hash);
        if (!File.Exists(path))
        {
            Interlocked.Increment(ref _misses);
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > GetMaxEntryBytes())
            {
                Interlocked.Increment(ref _expired);
                Interlocked.Increment(ref _oversizeRejected);
                return false;
            }

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var envelope = JsonSerializer.Deserialize<RulesCacheEnvelope<RulesKeywordCorpusCacheDto>>(
                stream, SerializerOptions);
            if (envelope == null || envelope.Payload == null)
            {
                RejectCorrupt(path, "empty envelope");
                return false;
            }

            if (envelope.FormatVersion != CacheFormatVersion
                || !string.Equals(envelope.Kind, KeywordCorpusKind, StringComparison.Ordinal)
                || !string.Equals(envelope.Game, game, StringComparison.Ordinal)
                || !string.Equals(envelope.AlgorithmVersion, KeywordCorpusAlgorithmVersion, StringComparison.Ordinal)
                || !string.Equals(envelope.ConfigFingerprint, KeywordCorpusConfigFingerprint, StringComparison.Ordinal)
                || !string.Equals(envelope.RulesVersion, rulesVersion, StringComparison.Ordinal)
                || !string.Equals(envelope.Namespace, normalizedNs, StringComparison.Ordinal)
                || !string.Equals(envelope.IdentityHash, identity.Hash, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _expired);
                return false;
            }

            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, SerializerOptions);
            var payloadHash = HashHex(payloadBytes);
            if (!string.Equals(payloadHash, envelope.PayloadHash, StringComparison.Ordinal))
            {
                RejectCorrupt(path, "payload hash mismatch");
                return false;
            }

            corpus = envelope.Payload;
            Interlocked.Increment(ref _hits);
            TryTouchAccessTime(path);
            return true;
        }
        catch (JsonException ex)
        {
            RejectCorrupt(path, ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _readFailures);
            if (Interlocked.Increment(ref _readFailureLogs) <= 10)
            {
                _logger?.LogWarning(ex,
                    "Rule disk cache read failed; falling back to rule documents. CacheFile={CacheFile}",
                    path);
            }
            return false;
        }
    }

    internal void QueueKeywordCorpus(
        string game,
        string rulesVersion,
        string? nameSpace,
        RulesKeywordCorpusCacheDto corpus)
    {
        if (!_options.Enabled) return;
        RulesPathSafety.ValidateGameId(game);
        ArgumentNullException.ThrowIfNull(corpus);

        var normalizedNs = NormalizeNamespace(nameSpace);
        var identity = BuildKeywordCorpusIdentity(game, rulesVersion, normalizedNs);
        var estimated = EstimateDtoBytes(corpus);
        var maxEntryBytes = GetMaxEntryBytes();
        if (estimated > maxEntryBytes)
        {
            Interlocked.Increment(ref _oversizeRejected);
            return;
        }

        var pending = new PendingWrite(
            identity.Hash,
            GetKeywordCorpusPath(identity.Hash),
            game,
            rulesVersion,
            normalizedNs,
            identity.Hash,
            corpus,
            estimated,
            Interlocked.Increment(ref _pendingSequence));

        lock (_pendingGate)
        {
            if (_pending.TryGetValue(pending.Key, out var replaced))
                _pendingBytes -= replaced.EstimatedBytes;
            _pending[pending.Key] = pending;
            _pendingBytes += pending.EstimatedBytes;
            TrimPendingLocked();
        }
    }

    /// <summary>
    /// Best-effort, non-blocking for request threads. The timer calls this; tests
    /// also call it directly instead of sleeping for the periodic interval.
    /// </summary>
    internal async Task FlushPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;
        if (!await _flushGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            List<PendingWrite> batch;
            lock (_pendingGate)
            {
                batch = _pending.Values.OrderBy(p => p.Sequence).ToList();
                _pending.Clear();
                _pendingBytes = 0;
            }

            foreach (var pending in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await WritePendingAsync(pending, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _writeSuccesses);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    RequeuePending(pending);
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _writeFailures);
                    RequeuePending(pending);
                    if (Interlocked.Increment(ref _writeFailureLogs) <= 10)
                    {
                        _logger?.LogWarning(ex,
                            "Rule disk cache write failed; retained pending entry for a later retry. CacheFile={CacheFile}",
                            pending.FinalPath);
                    }
                }
            }

            // Capacity cleanup is intentionally part of the background flush, never
            // the request path. Reading a cold cache file only touches metadata.
            await CleanupDiskAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public RulesDiskCacheStats GetStats()
    {
        int pendingCount;
        long pendingBytes;
        lock (_pendingGate)
        {
            pendingCount = _pending.Count;
            pendingBytes = _pendingBytes;
        }

        return new RulesDiskCacheStats(
            _options.Enabled,
            _root,
            _options.BudgetBytes,
            Interlocked.Read(ref _diskUsageBytes),
            Interlocked.Read(ref _hits),
            Interlocked.Read(ref _misses),
            Interlocked.Read(ref _expired),
            Interlocked.Read(ref _corrupt),
            Interlocked.Read(ref _readFailures),
            Interlocked.Read(ref _writeFailures),
            Interlocked.Read(ref _writeSuccesses),
            Interlocked.Read(ref _cleanupDeletes),
            pendingCount,
            pendingBytes,
            Interlocked.Read(ref _oversizeRejected));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();

        if (_options.Enabled && HasPendingWrites())
        {
            try
            {
                var flush = Task.Run(() => FlushPendingAsync());
                flush.Wait(TimeSpan.FromMilliseconds(_options.ShutdownFlushTimeoutMilliseconds));
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _writeFailureLogs) <= 10)
                {
                    _logger?.LogWarning(ex,
                        "Rule disk cache shutdown flush did not complete within the configured timeout.");
                }
            }
        }

        // Do not dispose the semaphore: an already-scheduled timer callback may
        // still call WaitAsync. The object is tiny and will be reclaimed with the cache.
    }

    private async Task FlushSafelyAsync()
    {
        try
        {
            await FlushPendingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _writeFailureLogs) <= 10)
            {
                _logger?.LogWarning(ex,
                    "Rule disk cache background flush failed; pending writes remain bounded for retry.");
            }
        }
    }

    private async Task WritePendingAsync(PendingWrite pending, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_keywordCorpusDirectory);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(pending.Corpus, SerializerOptions);
        if (payloadBytes.LongLength > GetMaxEntryBytes())
        {
            Interlocked.Increment(ref _oversizeRejected);
            return;
        }

        var envelope = new RulesCacheEnvelope<RulesKeywordCorpusCacheDto>
        {
            FormatVersion = CacheFormatVersion,
            Kind = KeywordCorpusKind,
            Game = pending.Game,
            AlgorithmVersion = KeywordCorpusAlgorithmVersion,
            ConfigFingerprint = KeywordCorpusConfigFingerprint,
            RulesVersion = pending.RulesVersion,
            Namespace = pending.Namespace,
            IdentityHash = pending.IdentityHash,
            PayloadHash = HashHex(payloadBytes),
            CreatedUtc = _timeProvider.GetUtcNow(),
            Payload = pending.Corpus
        };
        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);

        var finalPath = RulesPathSafety.ResolveWithinRoot(_root, pending.FinalPath);
        var tempPath = RulesPathSafety.ResolveWithinRoot(
            _root,
            finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true))
            {
                await stream.WriteAsync(envelopeBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            // Same-directory replacement is atomic on the local filesystems used by
            // the API. The final name includes the full identity hash, so a late
            // writer for an old rule version cannot overwrite a newer version.
            File.Move(tempPath, finalPath, overwrite: true);
            Interlocked.Exchange(ref _diskUsageBytes, new FileInfo(finalPath).Length);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // Best effort only; stale temp files are ignored by readers.
            }
        }
    }

    private Task CleanupDiskAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_keywordCorpusDirectory)) return Task.CompletedTask;

        var files = new List<FileInfo>();
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(_keywordCorpusDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                files.Add(info);
                total += info.Length;
            }
            catch (IOException)
            {
                // A concurrent atomic replacement can briefly make metadata unavailable.
            }
        }

        Interlocked.Exchange(ref _diskUsageBytes, total);
        if (total <= _options.BudgetBytes) return Task.CompletedTask;

        var target = (long)(_options.BudgetBytes * 0.80);
        foreach (var file in files
                     .OrderBy(f => SafeLastAccessUtc(f))
                     .ThenBy(f => f.LastWriteTimeUtc)
                     .ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            if (total <= target) break;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var length = file.Length;
                file.Delete();
                total -= length;
                Interlocked.Increment(ref _cleanupDeletes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Interlocked.Increment(ref _writeFailureLogs) <= 10)
                    _logger?.LogWarning(ex, "Rule disk cache cleanup could not delete {CacheFile}", file.FullName);
            }
        }

        Interlocked.Exchange(ref _diskUsageBytes, total);
        return Task.CompletedTask;
    }

    private void RequeuePending(PendingWrite pending)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_pendingGate)
        {
            if (_pending.ContainsKey(pending.Key)) return;
            _pending[pending.Key] = pending with { Sequence = Interlocked.Increment(ref _pendingSequence) };
            _pendingBytes += pending.EstimatedBytes;
            TrimPendingLocked();
        }
    }

    private void TrimPendingLocked()
    {
        while (_pending.Count > _options.PendingWriteLimit || _pendingBytes > _options.PendingBytesLimit)
        {
            var oldest = _pending.Values.OrderBy(p => p.Sequence).FirstOrDefault();
            if (oldest == null) break;
            _pending.Remove(oldest.Key);
            _pendingBytes -= oldest.EstimatedBytes;
        }
    }

    private bool HasPendingWrites()
    {
        lock (_pendingGate) return _pending.Count > 0;
    }

    private void RejectCorrupt(string path, string reason)
    {
        Interlocked.Increment(ref _corrupt);
        if (Interlocked.Increment(ref _corruptLogs) <= 10)
            _logger?.LogWarning("Rule disk cache entry rejected as corrupt ({Reason}): {CacheFile}", reason, path);
    }

    private void TryTouchAccessTime(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, _timeProvider.GetUtcNow().UtcDateTime);
        }
        catch
        {
            // Recency metadata is an optimization only; a read remains valid.
        }
    }

    private long GetMaxEntryBytes()
        => Math.Max(MinimumEntryBudget, Math.Min(_options.BudgetBytes / 4, 128L * 1024 * 1024));

    private string GetKeywordCorpusPath(string identityHash)
        => RulesPathSafety.ResolveWithinRoot(
            _root,
            Path.Combine(_keywordCorpusDirectory, identityHash + ".json"));

    private static string NormalizeNamespace(string? nameSpace)
        => string.IsNullOrWhiteSpace(nameSpace) ? "<all>" : nameSpace;

    private static KeywordCorpusIdentity BuildKeywordCorpusIdentity(
        string game,
        string rulesVersion,
        string nameSpace)
    {
        var canonical = string.Join('\n', new[]
        {
            "format=" + CacheFormatVersion,
            "kind=" + KeywordCorpusKind,
            "game=" + game,
            "algorithm=" + KeywordCorpusAlgorithmVersion,
            "config=" + KeywordCorpusConfigFingerprint,
            "rules=" + rulesVersion,
            "namespace=" + nameSpace
        });
        return new KeywordCorpusIdentity(HashHex(Encoding.UTF8.GetBytes(canonical)));
    }

    private static long EstimateDtoBytes(RulesKeywordCorpusCacheDto corpus)
    {
        long total = 256;
        foreach (var entry in corpus.Entries)
        {
            total += RulesCacheSizeEstimator.EstimateString(entry.Id)
                     + RulesCacheSizeEstimator.EstimateString(entry.Path)
                     + RulesCacheSizeEstimator.EstimateString(entry.Name)
                     + RulesCacheSizeEstimator.EstimateString(entry.Type)
                     + RulesCacheSizeEstimator.EstimateString(entry.Description);
            if (entry.Media != null)
            {
                foreach (var (key, value) in entry.Media)
                    total += RulesCacheSizeEstimator.EstimateString(key)
                             + RulesCacheSizeEstimator.EstimateObject(value);
            }
            foreach (var text in entry.Text)
                total += RulesCacheSizeEstimator.EstimateString(text);
        }
        return total;
    }

    private static DateTime SafeLastAccessUtc(FileInfo file)
    {
        try { return file.LastAccessTimeUtc; }
        catch { return file.LastWriteTimeUtc; }
    }

    private static string HashHex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private readonly record struct KeywordCorpusIdentity(string Hash);

    private sealed record PendingWrite(
        string Key,
        string FinalPath,
        string Game,
        string RulesVersion,
        string Namespace,
        string IdentityHash,
        RulesKeywordCorpusCacheDto Corpus,
        long EstimatedBytes,
        long Sequence);
}

internal sealed class RulesCacheEnvelope<T>
{
    public int FormatVersion { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Game { get; set; } = string.Empty;
    public string AlgorithmVersion { get; set; } = string.Empty;
    public string ConfigFingerprint { get; set; } = string.Empty;
    public string RulesVersion { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string IdentityHash { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public T? Payload { get; set; }
}

internal sealed class RulesKeywordCorpusCacheDto
{
    public List<RulesKeywordCorpusEntryDto> Entries { get; set; } = new();
}

internal sealed class RulesKeywordCorpusEntryDto
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Dictionary<string, JsonElement>? Media { get; set; }
    public List<string> Text { get; set; } = new();
}

internal sealed record KeywordCorpusEntry(
    string Id,
    string Path,
    string Name,
    string Type,
    string? Description,
    IReadOnlyDictionary<string, JsonElement>? Media,
    string[] Text);
