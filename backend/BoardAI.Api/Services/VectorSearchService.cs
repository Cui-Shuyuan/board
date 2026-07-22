using System.Security.Cryptography;
using System.Text;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace BoardAI.Api.Services;

/// <summary>
/// 封装 Qdrant 向量数据库，负责概念向量的存储与语义搜索。
/// </summary>
public class VectorSearchService : IDisposable
{
    private readonly QdrantClient _client;
    private readonly EmbeddingService _embedder;
    private readonly ILogger<VectorSearchService> _logger;

    private static string CollectionName(string gameId) => $"board_{gameId}";

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
    /// 确保某个游戏的 collection 存在（幂等）。每个游戏独享一个 collection。
    /// </summary>
    public async Task EnsureCollectionAsync(string gameId)
    {
        var name = CollectionName(gameId);
        if (await _client.CollectionExistsAsync(name))
            return;

        _logger.LogInformation("Creating Qdrant collection '{Name}' with {Dim} dimensions",
            name, _embedder.Dimension);
        await _client.CreateCollectionAsync(name, new VectorParams
        {
            Size = (ulong)_embedder.Dimension,
            Distance = Distance.Cosine,
        });
    }

    /// <summary>
    /// 重建某一款游戏的全部概念索引（每个游戏独享一个 collection）。
    /// 先删掉整个旧 collection，再创建新的并写入。
    /// </summary>
    public async Task RebuildIndexAsync(string gameId, IReadOnlyList<ConceptIndexItem> concepts)
    {
        var name = CollectionName(gameId);

        // 删掉旧 collection（如果存在），重新建
        if (await _client.CollectionExistsAsync(name))
        {
            await _client.DeleteCollectionAsync(name);
        }

        _logger.LogInformation("Creating Qdrant collection '{Name}' with {Dim} dimensions",
            name, _embedder.Dimension);
        await _client.CreateCollectionAsync(name, new VectorParams
        {
            Size = (ulong)_embedder.Dimension,
            Distance = Distance.Cosine,
        });

        if (concepts.Count == 0) return;

        // 为每条 concept 生成向量并写入 Qdrant
        var points = new List<PointStruct>(concepts.Count);
        foreach (var c in concepts)
        {
            var embedding = _embedder.Embed(c.SearchText);

            points.Add(new PointStruct
            {
                Id = new PointId { Uuid = GenerateUuid($"{gameId}::{c.ConceptId}") },
                Vectors = embedding.ToArray(),
                Payload =
                {
                    ["concept_id"] = new Value { StringValue = c.ConceptId },
                    ["type"] = new Value { StringValue = c.Type },
                    ["name_zh"] = new Value { StringValue = c.NameZh ?? "" },
                    ["name_en"] = new Value { StringValue = c.NameEn ?? "" },
                },
            });
        }

        // 分批 upsert（每批 100 条，避免单次请求过大）
        const int batchSize = 100;
        for (int i = 0; i < points.Count; i += batchSize)
        {
            var batch = points.GetRange(i, Math.Min(batchSize, points.Count - i));
            await _client.UpsertAsync(name, batch);
        }

        _logger.LogInformation("Indexed {Count} concepts for game '{GameId}' into collection '{Name}'",
            points.Count, gameId, name);
    }

    /// <summary>
    /// 语义搜索。返回相似度 > threshold 的 topK 条结果。
    /// 每个游戏独享 collection，无需 game_id 过滤。
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string gameId, string query, int topK = 10, float threshold = 0.55f)
    {
        var name = CollectionName(gameId);
        var queryVec = _embedder.Embed(query);

        try
        {
            var results = await _client.SearchAsync(
                name,
                queryVec.ToArray(),
                limit: (ulong)topK,
                scoreThreshold: threshold);

            return results.Select(r => new SearchResult
            {
                ConceptId = r.Payload.TryGetValue("concept_id", out var idVal)
                    ? idVal.StringValue : "",
                Type = r.Payload.TryGetValue("type", out var typeVal)
                    ? typeVal.StringValue : "",
                NameZh = r.Payload.TryGetValue("name_zh", out var nameVal)
                    ? nameVal.StringValue : "",
                Score = r.Score,
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qdrant search failed for game '{GameId}', query '{Query}'",
                gameId, query);
            return Array.Empty<SearchResult>();
        }
    }

    /// <summary>
    /// 根据输入字符串生成确定性的 UUID，用作 Qdrant PointId。
    /// </summary>
    private static string GenerateUuid(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        // UUID version 5 (name-based, SHA-1 style, but we use SHA-256 hash)
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes).ToString();
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
    public required string Type { get; init; }
    public string? NameZh { get; init; }
    public string? NameEn { get; init; }
    /// <summary>用于生成向量的拼接文本（name + definition 的全文）。</summary>
    public required string SearchText { get; init; }
}

/// <summary>
/// 语义搜索结果。
/// </summary>
public record SearchResult
{
    public required string ConceptId { get; init; }
    public required string Type { get; init; }
    public required string NameZh { get; init; }
    public float Score { get; init; }
}
