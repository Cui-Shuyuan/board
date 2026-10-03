using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 规则 JSON 文档缓存：以绝对路径为 key，按文件长度与最后写入时间自动失效。
/// 被替换的文档进入 retired 列表延迟释放，避免并发请求仍持有其 JsonElement。
/// </summary>
public sealed class RulesDocumentStore : IDisposable
{
    private readonly Action? _onDocumentChanged;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _documents = new(StringComparer.Ordinal);
    private readonly List<JsonDocument> _retired = new();
    private bool _disposed;

    public RulesDocumentStore(Action? onDocumentChanged = null)
    {
        _onDocumentChanged = onDocumentChanged;
    }

    /// <summary>
    /// 返回指定绝对路径的 JsonDocument；文件 mtime 或 size 变化时重新读取并解析。
    /// </summary>
    public JsonDocument GetDocument(string absolutePath)
    {
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RulesDocumentStore));

            var info = new FileInfo(absolutePath);
            if (!info.Exists)
                throw new FileNotFoundException($"Could not find file '{absolutePath}'.", absolutePath);

            if (_documents.TryGetValue(absolutePath, out var cached)
                && info.Length == cached.Length
                && info.LastWriteTimeUtc == cached.LastWriteTimeUtc)
            {
                return cached.Document;
            }

            var json = File.ReadAllText(absolutePath);
            var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true
            });

            if (_documents.TryGetValue(absolutePath, out var old))
            {
                // 不立即释放被替换的文档：已构建的派生缓存仍可能引用其 JsonElement。
                _retired.Add(old.Document);
                _onDocumentChanged?.Invoke();
            }

            _documents[absolutePath] = new Entry
            {
                Path = absolutePath,
                Length = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                Document = document
            };

            return document;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            foreach (var entry in _documents.Values)
                entry.Document.Dispose();
            foreach (var document in _retired)
                document.Dispose();

            _documents.Clear();
            _retired.Clear();
            _disposed = true;
        }
    }

    private sealed class Entry
    {
        public string Path = string.Empty;
        public long Length;
        public DateTime LastWriteTimeUtc;
        public JsonDocument Document = null!;
    }
}
