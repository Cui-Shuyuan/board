using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 规则文件内容读取协作类：统一负责本体 / 游戏 concepts / flow / instances 的路径解析与读取，
/// 并把实际缓存与失效语义委托给内部 <see cref="RulesDocumentStore"/>。
/// 生产请求通过 <see cref="GetSnapshot"/> 获取一版不可变快照，并用
/// <see cref="RulesSnapshotScope"/> 让整次调用读取同一规则版本。
/// </summary>
public sealed class RulesContentStore : IDisposable
{
    private readonly string _basePath;
    private readonly RulesDocumentStore _documentStore;
    private readonly object _snapshotsGate = new();
    private readonly Dictionary<string, RulesGameSnapshot> _snapshots = new(StringComparer.Ordinal);

    public RulesContentStore(string basePath, Action? onDocumentChanged = null)
    {
        _basePath = basePath;
        _documentStore = new RulesDocumentStore(onDocumentChanged);
    }

    /// <summary>当前内容代次；用于诊断与测试。</summary>
    public long Revision => _documentStore.Revision;

    /// <summary>读取必需的本体 concepts 文档；不存在时由 RulesDocumentStore 抛 FileNotFoundException。</summary>
    public JsonDocument LoadOntology()
    {
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null) return snapshot.Ontology;
        return LoadRequired(Path.Combine("content", "ontology", "concepts.json"));
    }

    /// <summary>读取游戏 concepts 文档；不存在时返回 null。</summary>
    public JsonDocument? LoadGameConcepts(string game)
    {
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null && snapshot.Game == game) return snapshot.GameConcepts;
        return LoadOptional(Path.Combine("content", "games", game, "concepts.json"));
    }

    /// <summary>读取本体 flow 文档；不存在时返回 null。</summary>
    public JsonDocument? LoadOntologyFlow()
    {
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null) return snapshot.OntologyFlow;
        return LoadOptional(Path.Combine("content", "ontology", "flow.json"));
    }

    /// <summary>读取游戏 flow 文档；不存在时返回 null。</summary>
    public JsonDocument? LoadGameFlow(string game)
    {
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null && snapshot.Game == game) return snapshot.GameFlow;
        return LoadOptional(Path.Combine("content", "games", game, "flow.json"));
    }

    /// <summary>读取游戏 instances 文档；不存在时返回 null。</summary>
    public JsonDocument? LoadGameInstances(string game)
    {
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null && snapshot.Game == game) return snapshot.GameInstances;
        return LoadOptional(Path.Combine("content", "games", game, "instances.json"));
    }

    /// <summary>
    /// 获取当前游戏规则快照。命中缓存时只做轻量 freshness 探测；文件变化时才重新深拷贝构建。
    /// </summary>
    public RulesGameSnapshot GetSnapshot(string game)
    {
        lock (_snapshotsGate)
        {
            var revision = GetRevision(game);
            if (_snapshots.TryGetValue(game, out var cached) && cached.Revision == revision)
                return cached;

            var snapshot = CaptureSnapshot(game);
            _snapshots[game] = snapshot;
            return snapshot;
        }
    }

    /// <summary>测试注入：将相对路径文档写入 store 内存，不访问磁盘。</summary>
    internal void SetDocumentForTesting(string relativePath, string json)
        => _documentStore.SetDocumentForTesting(Path.Combine(_basePath, relativePath), json);

    /// <summary>返回当前内容代次：顺带探测必需/可选文件的增删改，保证缓存命中路径也会刷新。</summary>
    public long GetRevision(string game)
    {
        return _documentStore.ReadConsistent(() =>
        {
            _documentStore.GetDocument(OntologyPath);
            _documentStore.GetDocumentIfExists(GameConceptsPath(game));
            _documentStore.GetDocumentIfExists(GameInstancesPath(game));
            _documentStore.GetDocumentIfExists(GameFlowPath(game));
            _documentStore.GetDocumentIfExists(OntologyFlowPath);
            return _documentStore.Revision;
        });
    }

    public void Dispose() => _documentStore.Dispose();

    private RulesGameSnapshot CaptureSnapshot(string game)
    {
        return _documentStore.ReadConsistent(() =>
        {
            var ontology = CopyDocument(_documentStore.GetDocument(OntologyPath));
            var gameConcepts = CopyDocument(_documentStore.GetDocumentIfExists(GameConceptsPath(game)));
            var gameInstances = CopyDocument(_documentStore.GetDocumentIfExists(GameInstancesPath(game)));
            var gameFlow = CopyDocument(_documentStore.GetDocumentIfExists(GameFlowPath(game)));
            var ontologyFlow = CopyDocument(_documentStore.GetDocumentIfExists(OntologyFlowPath));
            var revision = _documentStore.Revision;
            var version = RulesGameSnapshot.ComputeVersion(
                game, ontology, gameConcepts, gameInstances, gameFlow, ontologyFlow);

            return new RulesGameSnapshot(
                game,
                revision,
                version,
                ontology,
                gameConcepts,
                gameInstances,
                gameFlow,
                ontologyFlow);
        });
    }

    private string OntologyPath => Path.Combine(_basePath, "content", "ontology", "concepts.json");
    private string OntologyFlowPath => Path.Combine(_basePath, "content", "ontology", "flow.json");

    private string GameConceptsPath(string game)
        => Path.Combine(_basePath, "content", "games", game, "concepts.json");

    private string GameInstancesPath(string game)
        => Path.Combine(_basePath, "content", "games", game, "instances.json");

    private string GameFlowPath(string game)
        => Path.Combine(_basePath, "content", "games", game, "flow.json");

    /// <summary>
    /// 从 store 当前文档深拷贝出一份独立 JsonDocument，使快照不依赖 store 的 retired 生命周期。
    /// </summary>
    private static JsonDocument CopyDocument(JsonDocument? source)
    {
        if (source == null) return null!;
        return JsonDocument.Parse(source.RootElement.GetRawText(), new JsonDocumentOptions
        {
            AllowTrailingCommas = true
        });
    }

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

    private JsonDocument LoadRequired(string relativePath)
        => _documentStore.GetDocument(Path.Combine(_basePath, relativePath));

    private JsonDocument? LoadOptional(string relativePath)
        => _documentStore.GetDocumentIfExists(Path.Combine(_basePath, relativePath));
}
