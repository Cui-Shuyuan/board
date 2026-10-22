using System.Diagnostics;
using System.Text.Json;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public partial class GameRulesService : IRulesConceptCatalog, IDisposable
{
    private readonly RulesContentStore _content;
    private readonly RulesDerivedDiskCache? _diskCache;

    private readonly RulesFactService _factService;

    private readonly RulesFlowService _flowService;

    private readonly RulesConceptCatalog _catalog;

    private readonly RulesNameIndexService _nameIndex;

    private readonly RulesReferenceService _referenceService;

    private readonly RulesIndexService _indexService;

    private readonly RulesSearchService _searchService;

    private readonly RulesPlanService _planService;


    public GameRulesService(
        IOptions<RulesOptions> options,
        VectorSearchService? vectorSearch = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Value.Validate();

        // Rules:BasePath is an optional override; when empty, resolve the
        // repository root with the same portable logic used by the API host.
        var basePath = BoardPaths.ResolveBasePath(options.Value.BasePath);
        _content = new RulesContentStore(
            basePath,
            options.Value,
            logger: loggerFactory?.CreateLogger<RulesContentStore>());
        _diskCache = options.Value.DiskCache.Enabled
            ? new RulesDerivedDiskCache(
                basePath,
                options.Value.DiskCache,
                loggerFactory?.CreateLogger<RulesDerivedDiskCache>())
            : null;
        _factService = new RulesFactService(_content);
        _flowService = new RulesFlowService(_content);
        _catalog = new RulesConceptCatalog(_content);
        _nameIndex = new RulesNameIndexService(_catalog, _content);
        _referenceService = new RulesReferenceService(_catalog, _nameIndex);
        _indexService = new RulesIndexService(_catalog, _content, vectorSearch);
        _searchService = new RulesSearchService(_catalog, vectorSearch, _diskCache);
        _planService = new RulesPlanService(
            _catalog,
            _searchService,
            _nameIndex,
            _flowService,
            _referenceService,
            _factService,
            vectorSearch);
    }


    public RulesCacheStats GetCacheStats()
    {
        using var process = Process.GetCurrentProcess();
        return new RulesCacheStats(
            _content.GetMemoryStats(),
            _diskCache?.GetStats() ?? new RulesDiskCacheStats(
                Enabled: false,
                Directory: string.Empty,
                BudgetBytes: 0,
                DiskUsageBytes: 0,
                Hits: 0,
                Misses: 0,
                ExpiredEntries: 0,
                CorruptEntries: 0,
                ReadFailures: 0,
                WriteFailures: 0,
                WriteSuccesses: 0,
                CleanupDeletes: 0,
                PendingWrites: 0,
                PendingBytes: 0,
                OversizeEntriesRejected: 0),
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64);
    }

    public void Dispose()
    {
        _content.Dispose();
        _diskCache?.Dispose();
    }


    public string AnnotateReferences(string text, string game)
    {
        using var scope = UseSnapshot(game);
        return _referenceService.AnnotateReferences(text, game);
    }


    public GetConceptResult GetConceptsWithExpansion(string game, string id)
    {
        using var scope = UseSnapshot(game);
        return _referenceService.GetConceptsWithExpansion(game, id);
    }


    /// <summary>带规范路径的正式概念读取，供程序事实包构建使用。</summary>
    public ConceptReadResult ReadConceptWithExpansionWithPaths(string game, string id)
    {
        using var scope = UseSnapshot(game);
        return _referenceService.ReadConceptWithExpansionWithPaths(game, id);
    }


    public async Task<SearchConceptsResult> SearchConceptsAsync(
        string game, string query, string searchMode = "full", CancellationToken cancellationToken = default)
    {
        using var scope = UseSnapshot(game);
        using var retrievalScope = RetrievalRequestContext.EnterIfAbsent();
        return await _searchService.SearchConceptsAsync(game, query, searchMode, cancellationToken);
    }


    public IReadOnlyList<ConceptSummary> KeywordSearch(
        string game, string query)
    {
        using var scope = UseSnapshot(game);
        return _searchService.KeywordSearch(game, query);
    }


    public ListConceptsResult ListAllConceptIds(string game, int offset = 0, int limit = 50)
    {
        using var scope = UseSnapshot(game);
        return _searchService.ListAllConceptIds(game, offset, limit);
    }


    public IReadOnlyList<string> GetGames() => _content.GetGames();


    public IReadOnlyList<string> GetConceptTypes(string game)
    {
        using var scope = UseSnapshot(game);
        return _catalog.GetConceptTypes(game);
    }


    public IReadOnlyList<ConceptSummary> ListConcepts(string game, string type)
    {
        using var scope = UseSnapshot(game);
        return _catalog.ListConcepts(game, type);
    }


    public IReadOnlyList<JsonElement> GetConcepts(string game, string id)
    {
        using var scope = UseSnapshot(game);
        return _catalog.GetConcepts(game, id);
    }


    public JsonElement? GetConcept(string game, string id)
    {
        using var scope = UseSnapshot(game);
        return _catalog.GetConcept(game, id);
    }


    public IReadOnlyList<JsonElement> GetActionConditions(string game, string actionId)
    {
        using var scope = UseSnapshot(game);
        return _catalog.GetActionConditions(game, actionId);
    }


    public async Task<PlanExecutionResult> ExecutePlanAsync(
        string game, JsonElement plan, string question = "", CancellationToken cancellationToken = default)
    {
        using var scope = UseSnapshot(game);
        using var retrievalScope = RetrievalRequestContext.EnterIfAbsent();
        return await _planService.ExecutePlanAsync(game, plan, question, cancellationToken);
    }


    public IReadOnlyList<ConceptIndexItem> GetIndexItems(string game)
    {
        using var scope = UseSnapshot(game);
        return _indexService.GetIndexItems(game);
    }


    public async Task<IndexBuildResult?> BuildEmbeddingIndexAsync(string game)
    {
        using var scope = UseSnapshot(game);
        return await _indexService.BuildEmbeddingIndexAsync(game);
    }


    /// <summary>
    /// 将一次上层请求（如一轮 Chat 或直接 search/execute-plan API 调用）绑定到同一规则
    /// 版本和同一检索复用上下文；内部嵌套同游戏调用会复用外层 scope。请求结束时释放
    /// 对 snapshot 与检索缓存的引用。
    /// </summary>
    public IDisposable BeginRequestScope(string game)
    {
        var snapshotScope = UseSnapshot(game);
        var retrievalScope = RetrievalRequestContext.EnterIfAbsent();
        return new CompositeScope(retrievalScope, snapshotScope);
    }


    /// <summary>测试注入：将规则文档放入 content store 的内存快照源。</summary>
    internal void SetDocumentForTesting(string relativePath, string json)
        => _content.SetDocumentForTesting(relativePath, json);


    /// <summary>进入指定游戏当前规则版本；嵌套同游戏调用复用外层 scope。</summary>
    private IDisposable UseSnapshot(string game)
    {
        var current = RulesSnapshotScope.Current;
        if (current != null && current.Game == game)
            return NoopDisposable.Instance;

        return RulesSnapshotScope.Enter(_content.GetSnapshot(game));
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }


    /// <summary>按进入的逆序释放，确保检索上下文先结束，再恢复外层规则快照。</summary>
    private sealed class CompositeScope : IDisposable
    {
        private readonly IDisposable _retrievalScope;
        private readonly IDisposable _snapshotScope;
        private bool _disposed;

        public CompositeScope(IDisposable retrievalScope, IDisposable snapshotScope)
        {
            _retrievalScope = retrievalScope;
            _snapshotScope = snapshotScope;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _retrievalScope.Dispose();
            _snapshotScope.Dispose();
            _disposed = true;
        }
    }
}
