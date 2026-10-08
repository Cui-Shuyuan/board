using System.Text;
using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 规则 JSON 文档缓存：以绝对路径为 key，按文件长度与最后写入时间自动失效。
/// 每次实际加载/替换/删除都会推进 Revision，供上层构建不可变规则快照。
/// 被替换的原始文档只保留有限个用于紧急兜底；快照使用深拷贝，不依赖
/// retired 文档，因此长期热更新不会把旧文档积压到 Dispose。
/// </summary>
public sealed class RulesDocumentStore : IDisposable
{
    private const int MaxRetiredDocuments = 16;
    private const int ReadRetryCount = 50;
    private const int ReadRetryDelayMilliseconds = 10;

    private readonly Action? _onDocumentChanged;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _documents = new(StringComparer.Ordinal);
    private readonly Queue<JsonDocument> _retired = new();
    private readonly HashSet<string> _syntheticPaths = new(StringComparer.Ordinal);
    private long _revision;
    private bool _disposed;

    public RulesDocumentStore(Action? onDocumentChanged = null)
    {
        _onDocumentChanged = onDocumentChanged;
    }

    /// <summary>当前内容代次；任何已缓存文档的首次加载/替换/删除都会推进。</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>当前保留的 retired 原始文档数，仅用于测试/诊断。</summary>
    public int RetiredDocumentCount
    {
        get
        {
            lock (_gate)
            {
                return _retired.Count;
            }
        }
    }

    /// <summary>
    /// 在 store 锁内执行一段需要读取多份文档的一致性操作。回调内可再次调用
    /// <see cref="GetDocument"/> / <see cref="GetDocumentIfExists"/>（同一线程锁可重入）。
    /// </summary>
    public T ReadConsistent<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            ThrowIfDisposed();
            return action();
        }
    }

    /// <summary>
    /// 返回指定绝对路径的 JsonDocument；文件 mtime 或 size 变化时重新读取并解析。
    /// </summary>
    public JsonDocument GetDocument(string absolutePath)
    {
        absolutePath = NormalizePath(absolutePath);
        lock (_gate)
        {
            ThrowIfDisposed();
            return GetDocumentCore(absolutePath);
        }
    }

    /// <summary>
    /// 可选文档读取：文件不存在时返回 null；如果之前有缓存而文件被删除，会移除缓存并推进 Revision。
    /// </summary>
    public JsonDocument? GetDocumentIfExists(string absolutePath)
    {
        absolutePath = NormalizePath(absolutePath);
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_syntheticPaths.Contains(absolutePath) && _documents.TryGetValue(absolutePath, out var synthetic))
                return synthetic.Document;

            var info = new FileInfo(absolutePath);
            if (!info.Exists)
            {
                if (_documents.Remove(absolutePath, out var removed))
                {
                    Retire(removed.Document);
                    _revision++;
                    _onDocumentChanged?.Invoke();
                }
                return null;
            }

            return GetDocumentCore(absolutePath, info);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            foreach (var entry in _documents.Values)
                entry.Document.Dispose();
            while (_retired.Count > 0)
                _retired.Dequeue().Dispose();

            _documents.Clear();
            _syntheticPaths.Clear();
            _disposed = true;
        }
    }

    /// <summary>测试注入：不访问磁盘，直接用 JSON 文本替换指定路径的文档并推进 Revision。</summary>
    internal void SetDocumentForTesting(string absolutePath, string json)
    {
        absolutePath = NormalizePath(absolutePath);
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = ParseDocument(json);
            if (_documents.TryGetValue(absolutePath, out var old))
                Retire(old.Document);

            _documents[absolutePath] = new Entry
            {
                Path = absolutePath,
                Length = System.Text.Encoding.UTF8.GetByteCount(json),
                LastWriteTimeUtc = DateTime.UtcNow,
                Document = document
            };
            _syntheticPaths.Add(absolutePath);
            _revision++;
            _onDocumentChanged?.Invoke();
        }
    }

    private JsonDocument GetDocumentCore(string absolutePath)
    {
        if (_syntheticPaths.Contains(absolutePath) && _documents.TryGetValue(absolutePath, out var synthetic))
            return synthetic.Document;

        var info = new FileInfo(absolutePath);
        if (!info.Exists)
            throw new FileNotFoundException($"Could not find file '{absolutePath}'.", absolutePath);

        return GetDocumentCore(absolutePath, info);
    }

    private JsonDocument GetDocumentCore(string absolutePath, FileInfo info)
    {
        if (_syntheticPaths.Contains(absolutePath) && _documents.TryGetValue(absolutePath, out var synthetic))
            return synthetic.Document;

        if (_documents.TryGetValue(absolutePath, out var cached)
            && info.Length == cached.Length
            && info.LastWriteTimeUtc == cached.LastWriteTimeUtc)
        {
            return cached.Document;
        }

        var loaded = ReadDocumentWithRetry(absolutePath);

        var replaced = _documents.TryGetValue(absolutePath, out var old);
        if (replaced)
            Retire(old!.Document);

        _documents[absolutePath] = new Entry
        {
            Path = absolutePath,
            Length = loaded.Length,
            LastWriteTimeUtc = loaded.LastWriteTimeUtc,
            Document = loaded.Document
        };
        _revision++;
        if (replaced)
            _onDocumentChanged?.Invoke();

        return loaded.Document;
    }

    private static JsonDocument ParseDocument(string json)
        => JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true
        });

    private sealed record LoadedDocument(long Length, DateTime LastWriteTimeUtc, JsonDocument Document);

    private static LoadedDocument ReadDocumentWithRetry(string absolutePath)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= ReadRetryCount; attempt++)
        {
            try
            {
                var before = new FileInfo(absolutePath);
                if (!before.Exists)
                    throw new FileNotFoundException($"Could not find file '{absolutePath}'.", absolutePath);

                string json;
                // Open with read/write sharing so an external editor or publisher can replace the
                // file while requests are in flight. The parser retry below handles the truncate /
                // write window on Windows, where a reader may otherwise observe an empty or partial file.
                using (var stream = new FileStream(
                    absolutePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    json = reader.ReadToEnd();
                }

                var after = new FileInfo(absolutePath);
                if (!after.Exists)
                    throw new FileNotFoundException($"Could not find file '{absolutePath}'.", absolutePath);

                var document = ParseDocument(json);

                // If the file changed during the read, keep the pre-read timestamp/size so the next
                // freshness probe reloads instead of trusting a document whose metadata may already
                // describe a newer write. A successful JSON parse still gives this request a coherent
                // snapshot; a partial/truncated write is retried by the JsonException path below.
                var length = after.Length == before.Length && after.LastWriteTimeUtc == before.LastWriteTimeUtc
                    ? after.Length
                    : before.Length;
                var lastWriteTimeUtc = after.LastWriteTimeUtc == before.LastWriteTimeUtc
                    ? after.LastWriteTimeUtc
                    : before.LastWriteTimeUtc;

                return new LoadedDocument(length, lastWriteTimeUtc, document);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                last = ex;
                if (attempt == ReadRetryCount)
                    break;
                Thread.Sleep(ReadRetryDelayMilliseconds);
            }
        }

        throw last!;
    }

    private void Retire(JsonDocument document)
    {
        _retired.Enqueue(document);
        while (_retired.Count > MaxRetiredDocuments)
            _retired.Dequeue().Dispose();
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RulesDocumentStore));
    }

    private sealed class Entry
    {
        public string Path = string.Empty;
        public long Length;
        public DateTime LastWriteTimeUtc;
        public JsonDocument Document = null!;
    }
}
