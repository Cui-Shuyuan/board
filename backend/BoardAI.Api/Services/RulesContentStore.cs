using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 规则文件内容读取协作类：统一负责本体 / 游戏 concepts / flow / instances 的路径解析与读取，
/// 并把实际缓存与失效语义委托给内部 <see cref="RulesDocumentStore"/>。
/// </summary>
public sealed class RulesContentStore : IDisposable
{
    private readonly string _basePath;
    private readonly RulesDocumentStore _documentStore;

    public RulesContentStore(string basePath, Action? onDocumentChanged = null)
    {
        _basePath = basePath;
        _documentStore = new RulesDocumentStore(onDocumentChanged);
    }

    /// <summary>读取必需的本体 concepts 文档；不存在时由 RulesDocumentStore 抛 FileNotFoundException。</summary>
    public JsonDocument LoadOntology()
        => LoadRequired(Path.Combine("content", "ontology", "concepts.json"));

    /// <summary>读取游戏 concepts 文档；不存在时返回 null。</summary>
    public JsonDocument? LoadGameConcepts(string game)
        => LoadOptional(Path.Combine("content", "games", game, "concepts.json"));

    /// <summary>读取本体 flow 文档；不存在时返回 null。</summary>
    public JsonDocument? LoadOntologyFlow()
        => LoadOptional(Path.Combine("content", "ontology", "flow.json"));

    /// <summary>读取游戏 flow 文档；不存在时返回 null。</summary>
    public JsonDocument? LoadGameFlow(string game)
        => LoadOptional(Path.Combine("content", "games", game, "flow.json"));

    /// <summary>读取游戏 instances 文档；不存在时返回 null。</summary>
    public JsonDocument? LoadGameInstances(string game)
        => LoadOptional(Path.Combine("content", "games", game, "instances.json"));

    /// <summary>列出 content/games 下的游戏目录，忽略普通文件。</summary>
    public IReadOnlyList<string> GetGames()
    {
        var gamesDir = Path.Combine(_basePath, "content", "games");
        if (!Directory.Exists(gamesDir)) return Array.Empty<string>();
        return Directory.GetDirectories(gamesDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList()!;
    }

    public void Dispose() => _documentStore.Dispose();

    private JsonDocument LoadRequired(string relativePath)
        => _documentStore.GetDocument(Path.Combine(_basePath, relativePath));

    private JsonDocument? LoadOptional(string relativePath)
    {
        var path = Path.Combine(_basePath, relativePath);
        return File.Exists(path) ? _documentStore.GetDocument(path) : null;
    }
}
