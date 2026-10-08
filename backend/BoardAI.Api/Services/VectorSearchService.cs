using Grpc.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace BoardAI.Api.Services;

/// <summary>
/// 封装 Qdrant 向量数据库，负责概念向量的存储与语义搜索。
///
/// 索引契约（与 tools/indexing/rebuild_index.py 共用）：
/// 查询使用稳定别名 board_{game}__active / board_{game}__active_name；
/// 每次重建先写入带版本号的新 collection，校验数量后再用 UpdateAliases
/// 原子切换。构建失败时旧别名仍指向旧 collection，查询持续可用。
/// </summary>
public class VectorSearchService : IDisposable
{
    private readonly QdrantClient _client;
    private readonly EmbeddingService _embedder;
    private readonly ILogger<VectorSearchService> _logger;
    private const int BatchSize = 100;

    public VectorSearchService(
        string host, int port,
        EmbeddingService embedder,
        ILogger<VectorSearchService> logger)
    {
        _client = new QdrantClient(host, port);
        _embedder = embedder;
        _logger = logger;
    }

    /// <summary>
    /// 兼容旧调用：确保某游戏至少有一个可查询 collection。当前实现不再
    /// 创建无版本的空白 collection，只在旧 collection 不存在时创建它。
    /// </summary>
    public async Task EnsureCollectionAsync(string gameId)
    {
        var alias = IndexContract.ActiveAlias(gameId, nameOnly: false);
        if (await AliasExistsAsync(alias, CancellationToken.None))
            return;

        var legacy = IndexContract.LegacyCollectionName(gameId, nameOnly: false);
        if (await _client.CollectionExistsAsync(legacy))
            return;

        await CreateCollectionAsync(legacy, CancellationToken.None);
    }

    /// <summary>
    /// 重建某一款游戏的全量 / 名称两套索引，并原子切换查询别名。
    /// </summary>
    public async Task<IndexBuildResult> RebuildIndexAsync(
        string gameId,
        IReadOnlyList<ConceptIndexItem> concepts,
        CancellationToken cancellationToken = default)
    {
        var version = IndexContract.ComputeIndexVersion(gameId, _embedder.ModelId, _embedder.Dimension, concepts);
        var fullCollection = IndexContract.VersionedCollectionName(gameId, version, nameOnly: false);
        var nameCollection = IndexContract.VersionedCollectionName(gameId, version, nameOnly: true);
        var fullAlias = IndexContract.ActiveAlias(gameId, nameOnly: false);
        var nameAlias = IndexContract.ActiveAlias(gameId, nameOnly: true);

        var fullPoints = BuildPoints(gameId, concepts, nameOnly: false);
        var namePoints = BuildPoints(gameId, concepts, nameOnly: true);
        var createdCollections = new List<string>();
        var reused = false;

        try
        {
            reused |= await PrepareCollectionAsync(
                fullCollection, fullPoints, createdCollections, cancellationToken);
            reused |= await PrepareCollectionAsync(
                nameCollection, namePoints, createdCollections, cancellationToken);

            await VerifyCountAsync(fullCollection, fullPoints.Count, cancellationToken);
            await VerifyCountAsync(nameCollection, namePoints.Count, cancellationToken);

            await SwitchAliasesAsync(
                fullAlias, fullCollection,
                nameAlias, nameCollection,
                cancellationToken);

            _logger.LogInformation(
                "Atomically switched index aliases for game '{GameId}': version={Version}, full={FullCollection} ({FullCount}), name={NameCollection} ({NameCount})",
                gameId, version, fullCollection, fullPoints.Count, nameCollection, namePoints.Count);

            return new IndexBuildResult
            {
                GameId = gameId,
                ModelId = _embedder.ModelId,
                Version = version,
                FullCollection = fullCollection,
                NameCollection = nameCollection,
                FullAlias = fullAlias,
                NameAlias = nameAlias,
                FullCount = fullPoints.Count,
                NameCount = namePoints.Count,
                ReusedExistingCollections = reused
            };
        }
        catch (Exception ex)
        {
            // 索引切换之前失败：清掉本次新建的临时 collection，旧别名不动。
            foreach (var collection in createdCollections)
            {
                try
                {
                    // The caller token may already be cancelled; cleanup must
                    // still make a best effort to remove unswitched temp data.
                    if (await _client.CollectionExistsAsync(collection, CancellationToken.None))
                        await _client.DeleteCollectionAsync(collection, cancellationToken: CancellationToken.None);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx,
                        "Failed to clean up temporary index collection '{Collection}'", collection);
                }
            }

            if (ex is RpcException rpc && rpc.StatusCode == StatusCode.Cancelled
                && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Index rebuild cancelled while building temporary collections.", ex, cancellationToken);
            }
            throw;
        }
    }

    /// <summary>
    /// 语义搜索。返回相似度 > threshold 的 topK 条结果。
    /// 优先查询版本化 collection 的稳定别名；尚未重建过的旧主机回退到
    /// 历史 collection 名称，保证升级期间查询不断。
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string gameId, string query, int topK = 10, float threshold = 0.55f, string searchMode = "full",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var collection = await ResolveCollectionNameAsync(
            gameId, searchMode == "name", cancellationToken);

        var queryVec = _embedder.Embed(query);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var results = await _client.SearchAsync(
                collection,
                queryVec.ToArray(),
                limit: (ulong)topK,
                scoreThreshold: threshold,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            return results.Select(r => new SearchResult
            {
                ConceptId = r.Payload.TryGetValue("concept_id", out var idVal)
                    ? idVal.StringValue : "",
                Path = r.Payload.TryGetValue("path", out var pathVal)
                    && !string.IsNullOrEmpty(pathVal.StringValue)
                    ? pathVal.StringValue
                    : r.Payload.TryGetValue("concept_id", out var fallbackId)
                        ? fallbackId.StringValue
                        : "",
                OwnerPath = r.Payload.TryGetValue("owner_path", out var ownerVal)
                    ? ownerVal.StringValue : "",
                Type = r.Payload.TryGetValue("type", out var typeVal)
                    ? typeVal.StringValue : "",
                NameZh = r.Payload.TryGetValue("name_zh", out var nameVal)
                    ? nameVal.StringValue : "",
                Score = r.Score,
            }).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled
                                     && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException($"Qdrant search cancelled for game '{gameId}'.", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qdrant search failed for game '{GameId}', query '{Query}', collection '{Collection}'",
                gameId, query, collection);
            return Array.Empty<SearchResult>();
        }
    }

    private async Task<bool> PrepareCollectionAsync(
        string collection,
        List<PointStruct> points,
        List<string> createdCollections,
        CancellationToken cancellationToken)
    {
        if (await _client.CollectionExistsAsync(collection, cancellationToken))
        {
            var existingCount = await CountAsync(collection, cancellationToken);
            if (existingCount == (ulong)points.Count)
            {
                _logger.LogInformation(
                    "Reusing existing index collection '{Collection}' with {Count} points",
                    collection, points.Count);
                return true;
            }

            _logger.LogWarning(
                "Removing stale index collection '{Collection}' ({ActualCount} points, expected {ExpectedCount})",
                collection, existingCount, points.Count);
            await _client.DeleteCollectionAsync(collection, cancellationToken: cancellationToken);
        }

        await CreateCollectionAsync(collection, cancellationToken);
        createdCollections.Add(collection);

        for (var i = 0; i < points.Count; i += BatchSize)
        {
            var batch = points.GetRange(i, Math.Min(BatchSize, points.Count - i));
            await _client.UpsertAsync(collection, batch, cancellationToken: cancellationToken);
        }

        return false;
    }

    private List<PointStruct> BuildPoints(
        string gameId,
        IReadOnlyList<ConceptIndexItem> concepts,
        bool nameOnly)
    {
        var points = new List<PointStruct>(nameOnly ? concepts.Count : concepts.Count);
        foreach (var c in concepts)
        {
            if (nameOnly && string.IsNullOrWhiteSpace(c.NameText))
                continue;

            var text = nameOnly ? c.NameText! : c.SearchText;
            var identity = IndexContract.IdentityOf(c);
            var embedding = _embedder.Embed(text);
            points.Add(new PointStruct
            {
                Id = new PointId
                {
                    Uuid = IndexContract.ComputePointId(gameId, c.Source, identity, nameOnly)
                },
                Vectors = embedding,
                Payload =
                {
                    ["concept_id"] = new Value { StringValue = c.ConceptId },
                    ["path"] = new Value { StringValue = identity },
                    ["owner_path"] = new Value { StringValue = c.OwnerPath ?? "" },
                    ["type"] = new Value { StringValue = c.Type },
                    ["name_zh"] = new Value { StringValue = c.NameZh ?? "" },
                    ["name_en"] = new Value { StringValue = c.NameEn ?? "" },
                },
            });
        }
        return points;
    }

    private async Task<ulong> CountAsync(string collection, CancellationToken cancellationToken)
    {
        return await _client.CountAsync(collection, exact: true, cancellationToken: cancellationToken);
    }

    private async Task VerifyCountAsync(
        string collection,
        int expected,
        CancellationToken cancellationToken)
    {
        var actual = await CountAsync(collection, cancellationToken);
        if (actual != (ulong)expected)
        {
            throw new InvalidOperationException(
                $"Index collection '{collection}' count mismatch: expected {expected}, got {actual}");
        }
    }

    private async Task CreateCollectionAsync(string collection, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Creating Qdrant collection '{Collection}' with {Dim} dimensions",
            collection, _embedder.Dimension);
        await _client.CreateCollectionAsync(collection, new VectorParams
        {
            Size = (ulong)_embedder.Dimension,
            Distance = Distance.Cosine,
        }, cancellationToken: cancellationToken);
    }

    private async Task<bool> AliasExistsAsync(string alias, CancellationToken cancellationToken)
    {
        var aliases = await _client.ListAliasesAsync(cancellationToken);
        foreach (var entry in aliases)
        {
            if (string.Equals(entry.AliasName, alias, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private async Task<string> ResolveCollectionNameAsync(
        string gameId,
        bool nameOnly,
        CancellationToken cancellationToken)
    {
        var alias = IndexContract.ActiveAlias(gameId, nameOnly);
        try
        {
            if (await AliasExistsAsync(alias, cancellationToken))
                return alias;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled
                                     && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException($"Index alias resolution cancelled for game '{gameId}'.", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not resolve index alias '{Alias}; falling back to legacy collection", alias);
        }

        return IndexContract.LegacyCollectionName(gameId, nameOnly);
    }

    private async Task SwitchAliasesAsync(
        string fullAlias,
        string fullCollection,
        string nameAlias,
        string nameCollection,
        CancellationToken cancellationToken)
    {
        var actions = new List<AliasOperations>
        {
            new() { DeleteAlias = new DeleteAlias { AliasName = fullAlias } },
            new() { CreateAlias = new CreateAlias { AliasName = fullAlias, CollectionName = fullCollection } },
            new() { DeleteAlias = new DeleteAlias { AliasName = nameAlias } },
            new() { CreateAlias = new CreateAlias { AliasName = nameAlias, CollectionName = nameCollection } },
        };

        await _client.UpdateAliasesAsync(actions, cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

/// <summary>
/// 索引条目：一条待存入 Qdrant 的概念。
/// </summary>
public record ConceptIndexItem
{
    public required string ConceptId { get; init; }
    /// <summary>
    /// 稳定寻址路径：普通概念等于 ConceptId；局部槽位为
    /// &lt;owner_concept_path&gt;.&lt;slot_id&gt;。Point ID 使用该路径，
    /// 因此同名全局概念与局部槽位不会互相覆盖。
    /// </summary>
    public string? Path { get; init; }
    /// <summary>局部槽位的所属概念路径；普通概念为空。</summary>
    public string? OwnerPath { get; init; }
    public required string Type { get; init; }
    /// <summary>来源：ontology / ontology_flow / game / instances / game_flow。</summary>
    public string Source { get; init; } = "game";
    public string? NameZh { get; init; }
    public string? NameEn { get; init; }
    /// <summary>name-only 集合的嵌入文本；空值不进入 name 集合。</summary>
    public string? NameText { get; init; }
    /// <summary>用于生成向量的拼接文本（name + definition 的全文）。</summary>
    public required string SearchText { get; init; }
}

/// <summary>
/// 语义搜索结果。
/// </summary>
public record SearchResult
{
    public required string ConceptId { get; init; }
    /// <summary>稳定寻址路径；旧 collection 无该 payload 时回退为 ConceptId。</summary>
    public string Path { get; init; } = "";
    /// <summary>局部槽位的所属概念路径；普通概念为空。</summary>
    public string OwnerPath { get; init; } = "";
    public required string Type { get; init; }
    public required string NameZh { get; init; }
    public float Score { get; init; }
}
