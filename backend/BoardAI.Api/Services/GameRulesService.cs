using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public partial class GameRulesService : IRulesConceptCatalog, IDisposable
{
    private readonly VectorSearchService? _vectorSearch;

    private readonly RulesContentStore _content;

    private readonly RulesFlowService _flowService;

    private readonly RulesConceptCatalog _catalog;

    private readonly RulesNameIndexService _nameIndex;

    private readonly RulesIndexService _indexService;

    private readonly RulesSearchService _searchService;


    /// <summary>不转义 &lt;&gt; 的序列化选项——引用提取必须看到字面 &lt;concept_id&gt;（与工具返回的 ToolResultOptions 同理）。</summary>
    private static readonly JsonSerializerOptions RelaxedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };


    public GameRulesService(IOptions<RulesOptions> options, VectorSearchService? vectorSearch = null)
    {
        // Rules:BasePath is an optional override; when empty, resolve the
        // repository root with the same portable logic used by the API host.
        var basePath = BoardPaths.ResolveBasePath(options.Value.BasePath);
        _content = new RulesContentStore(basePath, ClearDerivedCaches);
        _flowService = new RulesFlowService(_content);
        _catalog = new RulesConceptCatalog(_content);
        _nameIndex = new RulesNameIndexService(_catalog, _content);
        _vectorSearch = vectorSearch;
        _indexService = new RulesIndexService(_catalog, _content, vectorSearch);
        _searchService = new RulesSearchService(_catalog, vectorSearch);
    }


    public void Dispose() => _content.Dispose();


    public Task<SearchConceptsResult> SearchConceptsAsync(
        string game, string query, string searchMode = "full")
        => _searchService.SearchConceptsAsync(game, query, searchMode);


    public IReadOnlyList<ConceptSummary> KeywordSearch(
        string game, string query)
        => _searchService.KeywordSearch(game, query);


    public ListConceptsResult ListAllConceptIds(string game)
        => _searchService.ListAllConceptIds(game);


    public IReadOnlyList<string> GetGames() => _content.GetGames();


    public IReadOnlyList<string> GetConceptTypes(string game)
        => _catalog.GetConceptTypes(game);


    public IReadOnlyList<ConceptSummary> ListConcepts(string game, string type)
        => _catalog.ListConcepts(game, type);


    public IReadOnlyList<JsonElement> GetConcepts(string game, string id)
        => _catalog.GetConcepts(game, id);


    public JsonElement? GetConcept(string game, string id)
        => _catalog.GetConcept(game, id);


    public IReadOnlyList<JsonElement> GetActionConditions(string game, string actionId)
        => _catalog.GetActionConditions(game, actionId);


    public IReadOnlyList<ConceptIndexItem> GetIndexItems(string game)
        => _indexService.GetIndexItems(game);


    public Task BuildEmbeddingIndexAsync(string game)
        => _indexService.BuildEmbeddingIndexAsync(game);


    /// <summary>
    /// 任意被缓存规则文档变化时清空派生缓存，下次访问会从新文档重建。
    /// 这里不区分 game：保持简单，且避免遗漏任何依赖规则 JSON 的缓存。
    /// </summary>
    private void ClearDerivedCaches()
    {
        _nameIndex.Clear();
        _conceptTypeMaps.Clear();
        _flowService.Clear();
        _scoreTableGame = null;
        _scoreTable = null;
    }
}
