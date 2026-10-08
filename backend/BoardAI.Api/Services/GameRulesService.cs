using System.Text.Json;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public partial class GameRulesService : IRulesConceptCatalog, IDisposable
{
    private readonly RulesContentStore _content;

    private readonly RulesFactService _factService;

    private readonly RulesFlowService _flowService;

    private readonly RulesConceptCatalog _catalog;

    private readonly RulesNameIndexService _nameIndex;

    private readonly RulesReferenceService _referenceService;

    private readonly RulesIndexService _indexService;

    private readonly RulesSearchService _searchService;

    private readonly RulesPlanService _planService;


    public GameRulesService(IOptions<RulesOptions> options, VectorSearchService? vectorSearch = null)
    {
        // Rules:BasePath is an optional override; when empty, resolve the
        // repository root with the same portable logic used by the API host.
        var basePath = BoardPaths.ResolveBasePath(options.Value.BasePath);
        _content = new RulesContentStore(basePath);
        _factService = new RulesFactService(_content);
        _flowService = new RulesFlowService(_content);
        _catalog = new RulesConceptCatalog(_content);
        _nameIndex = new RulesNameIndexService(_catalog, _content);
        _referenceService = new RulesReferenceService(_catalog, _nameIndex);
        _indexService = new RulesIndexService(_catalog, _content, vectorSearch);
        _searchService = new RulesSearchService(_catalog, vectorSearch);
        _planService = new RulesPlanService(
            _catalog,
            _searchService,
            _nameIndex,
            _flowService,
            _referenceService,
            _factService,
            vectorSearch);
    }


    public void Dispose() => _content.Dispose();


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


    public async Task<SearchConceptsResult> SearchConceptsAsync(
        string game, string query, string searchMode = "full", CancellationToken cancellationToken = default)
    {
        using var scope = UseSnapshot(game);
        return await _searchService.SearchConceptsAsync(game, query, searchMode, cancellationToken);
    }


    public IReadOnlyList<ConceptSummary> KeywordSearch(
        string game, string query)
    {
        using var scope = UseSnapshot(game);
        return _searchService.KeywordSearch(game, query);
    }


    public ListConceptsResult ListAllConceptIds(string game)
    {
        using var scope = UseSnapshot(game);
        return _searchService.ListAllConceptIds(game);
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
}
