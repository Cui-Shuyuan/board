using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
    private readonly ConcurrentDictionary<string, byte> _verifiedCollections = new();

    /// <summary>概念引用正则——与 GameRulesService.ConceptRefRegex 同一模式。</summary>
    private static readonly Regex ConceptRefRegex = new(
        @"<([A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)?)>",
        RegexOptions.Compiled);

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
    /// 确保某个游戏的 collection 存在（幂等）。每个游戏独享 full + name 两个 collection。
    /// </summary>
    public async Task EnsureCollectionAsync(string gameId)
    {
        var name = CollectionName(gameId);
        var nameOnly = name + "_name";

        foreach (var collection in new[] { name, nameOnly })
        {
            if (await _client.CollectionExistsAsync(collection))
                continue;

            _logger.LogInformation("Creating Qdrant collection '{Name}' with {Dim} dimensions",
                collection, _embedder.Dimension);
            await _client.CreateCollectionAsync(collection, new VectorParams
            {
                Size = (ulong)_embedder.Dimension,
                Distance = Distance.Cosine,
            });
        }
    }

    /// <summary>
    /// 重建某一款游戏的全部概念索引（每个游戏独享 full + name 两个 collection）。
    /// 先删掉旧 collection，再创建新的并写入。
    /// </summary>
    public async Task RebuildIndexAsync(string gameId, IReadOnlyList<ConceptIndexItem> concepts)
    {
        var name = CollectionName(gameId);
        var nameOnly = name + "_name";

        // 删掉旧 collection（如果存在），重新建
        foreach (var collection in new[] { name, nameOnly })
        {
            if (await _client.CollectionExistsAsync(collection))
            {
                await _client.DeleteCollectionAsync(collection);
            }

            _logger.LogInformation("Creating Qdrant collection '{Name}' with {Dim} dimensions",
                collection, _embedder.Dimension);
            await _client.CreateCollectionAsync(collection, new VectorParams
            {
                Size = (ulong)_embedder.Dimension,
                Distance = Distance.Cosine,
            });
        }

        if (concepts.Count == 0) return;

        // 为每条 concept 生成向量并写入 Qdrant
        var points = new List<PointStruct>(concepts.Count);
        var namePoints = new List<PointStruct>(concepts.Count);
        foreach (var c in concepts)
        {
            var embedding = _embedder.Embed(StripRefs(c.SearchText));

            points.Add(new PointStruct
            {
                Id = new PointId { Uuid = GenerateUuid($"{gameId}::{c.Source}::{c.ConceptId}") },
                Vectors = embedding.ToArray(),
                Payload =
                {
                    ["concept_id"] = new Value { StringValue = c.ConceptId },
                    ["type"] = new Value { StringValue = c.Type },
                    ["name_zh"] = new Value { StringValue = c.NameZh ?? "" },
                    ["name_en"] = new Value { StringValue = c.NameEn ?? "" },
                    ["content_hash"] = new Value { StringValue = ContentHash(c, StripRefs(c.SearchText)) },
                    ["model_tag"] = new Value { StringValue = _embedder.ModelTag },
                },
            });

            // name-only 集合只存中文名（缺失时退回英文名），与 scripts/rebuild_index.py 对齐。
            var nameText = string.IsNullOrWhiteSpace(c.NameZh) ? c.NameEn ?? "" : c.NameZh;
            if (!string.IsNullOrWhiteSpace(nameText))
            {
                namePoints.Add(new PointStruct
                {
                    Id = new PointId { Uuid = GenerateUuid($"{gameId}::{c.Source}::{c.ConceptId}_name") },
                    Vectors = _embedder.Embed(nameText).ToArray(),
                    Payload =
                    {
                        ["concept_id"] = new Value { StringValue = c.ConceptId },
                        ["type"] = new Value { StringValue = c.Type },
                        ["name_zh"] = new Value { StringValue = c.NameZh ?? "" },
                        ["name_en"] = new Value { StringValue = c.NameEn ?? "" },
                        ["content_hash"] = new Value { StringValue = ContentHash(c, nameText) },
                        ["model_tag"] = new Value { StringValue = _embedder.ModelTag },
                    },
                });
            }
        }

        // 分批 upsert（每批 100 条，避免单次请求过大）
        const int batchSize = 100;
        for (int i = 0; i < points.Count; i += batchSize)
        {
            var batch = points.GetRange(i, Math.Min(batchSize, points.Count - i));
            await _client.UpsertAsync(name, batch);
        }
        for (int i = 0; i < namePoints.Count; i += batchSize)
        {
            var batch = namePoints.GetRange(i, Math.Min(batchSize, namePoints.Count - i));
            await _client.UpsertAsync(nameOnly, batch);
        }

        _logger.LogInformation("Indexed {Count} concepts for game '{GameId}' into collection '{Name}' ({NameOnlyCount} name-only points in '{NameOnly}')",
            points.Count, gameId, name, namePoints.Count, nameOnly);
    }

    /// <summary>
    /// 增量同步某一款游戏的全部概念索引。
    /// 与 Python rebuild_index.py 的增量模式保持一致：point ID 确定性生成，
    /// payload 带 content_hash + model_tag；diff 后只对新增/变化点 embedding，
    /// 删除旧集合中已不存在的点。collection 缺失或维度不匹配时回退全量重建。
    /// </summary>
    public async Task SyncIndexAsync(string gameId, IReadOnlyList<ConceptIndexItem> concepts)
    {
        var name = CollectionName(gameId);
        var nameOnly = name + "_name";

        if (!await CollectionDimensionsOkAsync(name) || !await CollectionDimensionsOkAsync(nameOnly))
        {
            _logger.LogInformation(
                "Qdrant collection missing or dimension mismatch for game '{GameId}', falling back to full rebuild.",
                gameId);
            await RebuildIndexAsync(gameId, concepts);
            return;
        }

        var existingFull = await ScrollAllPointsAsync(name);
        var existingName = await ScrollAllPointsAsync(nameOnly);

        var fullDescriptors = BuildFullPointDescriptors(gameId, concepts);
        var nameDescriptors = BuildNamePointDescriptors(gameId, concepts);

        var (fullUpsert, fullDelete) = DiffDescriptors(existingFull, fullDescriptors);
        var (nameUpsert, nameDelete) = DiffDescriptors(existingName, nameDescriptors);

        if (fullUpsert.Count > 0)
            await EmbedAndUpsertAsync(name, fullUpsert);
        if (nameUpsert.Count > 0)
            await EmbedAndUpsertAsync(nameOnly, nameUpsert);
        if (fullDelete.Count > 0)
            await DeletePointsAsync(name, fullDelete);
        if (nameDelete.Count > 0)
            await DeletePointsAsync(nameOnly, nameDelete);

        _logger.LogInformation(
            "Incremental index sync for game '{GameId}': full upserted {FullUpsert}/deleted {FullDelete}/unchanged {FullUnchanged}; name upserted {NameUpsert}/deleted {NameDelete}/unchanged {NameUnchanged}",
            gameId, fullUpsert.Count, fullDelete.Count, fullDescriptors.Count - fullUpsert.Count,
            nameUpsert.Count, nameDelete.Count, nameDescriptors.Count - nameUpsert.Count);
    }

    private async Task<bool> CollectionDimensionsOkAsync(string collection)
    {
        if (!await _client.CollectionExistsAsync(collection))
            return false;

        var info = await _client.GetCollectionInfoAsync(collection);
        return info?.Config?.Params?.VectorsConfig?.Params?.Size == (ulong)_embedder.Dimension;
    }

    private async Task<Dictionary<string, Dictionary<string, string>>> ScrollAllPointsAsync(string collection)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        PointId? offset = null;

        while (true)
        {
            var page = await _client.ScrollAsync(
                collection,
                limit: 200u,
                offset: offset,
                payloadSelector: true,
                vectorsSelector: false);

            foreach (var point in page.Result)
            {
                var key = PointIdToKey(point.Id);
                if (key == null)
                    continue;

                var payload = new Dictionary<string, string>();
                if (point.Payload != null)
                {
                    foreach (var kv in point.Payload)
                        payload[kv.Key] = kv.Value?.StringValue ?? "";
                }
                result[key] = payload;
            }

            var next = page.NextPageOffset;
            if (next == null || (!next.HasUuid && !next.HasNum))
                break;
            offset = next;
        }

        return result;
    }

    private static string? PointIdToKey(PointId? id)
    {
        if (id == null)
            return null;
        if (id.HasUuid)
            return id.Uuid;
        if (id.HasNum)
            return id.Num.ToString();
        return null;
    }

    private List<PointDescriptor> BuildFullPointDescriptors(
        string gameId, IReadOnlyList<ConceptIndexItem> concepts)
    {
        var descriptors = new List<PointDescriptor>(concepts.Count);
        foreach (var c in concepts)
        {
            var text = StripRefs(c.SearchText);
            descriptors.Add(new PointDescriptor(
                GenerateUuid($"{gameId}::{c.Source}::{c.ConceptId}"),
                text,
                BuildPayload(c, text)));
        }
        return DedupeDescriptors(descriptors);
    }

    private List<PointDescriptor> BuildNamePointDescriptors(
        string gameId, IReadOnlyList<ConceptIndexItem> concepts)
    {
        var descriptors = new List<PointDescriptor>(concepts.Count);
        foreach (var c in concepts)
        {
            var text = string.IsNullOrWhiteSpace(c.NameZh) ? c.NameEn ?? "" : c.NameZh;
            if (string.IsNullOrWhiteSpace(text))
                continue;
            descriptors.Add(new PointDescriptor(
                GenerateUuid($"{gameId}::{c.Source}::{c.ConceptId}_name"),
                text,
                BuildPayload(c, text)));
        }
        return DedupeDescriptors(descriptors);
    }

    private Dictionary<string, Value> BuildPayload(ConceptIndexItem c, string text)
    {
        return new Dictionary<string, Value>
        {
            ["concept_id"] = new Value { StringValue = c.ConceptId },
            ["type"] = new Value { StringValue = c.Type },
            ["name_zh"] = new Value { StringValue = c.NameZh ?? "" },
            ["name_en"] = new Value { StringValue = c.NameEn ?? "" },
            ["content_hash"] = new Value { StringValue = ContentHash(c, text) },
            ["model_tag"] = new Value { StringValue = _embedder.ModelTag },
        };
    }

    private static List<PointDescriptor> DedupeDescriptors(List<PointDescriptor> descriptors)
    {
        var seen = new HashSet<string>();
        var result = new List<PointDescriptor>(descriptors.Count);
        foreach (var d in descriptors)
        {
            if (seen.Add(d.Id))
                result.Add(d);
        }
        return result;
    }

    private (List<PointDescriptor> Upsert, List<string> Delete) DiffDescriptors(
        Dictionary<string, Dictionary<string, string>> existing,
        List<PointDescriptor> descriptors)
    {
        var newById = descriptors.ToDictionary(d => d.Id, d => d);
        var toUpsert = new List<PointDescriptor>();

        foreach (var d in descriptors)
        {
            if (!existing.TryGetValue(d.Id, out var oldPayload))
            {
                toUpsert.Add(d);
                continue;
            }

            var oldHash = oldPayload.TryGetValue("content_hash", out var h) ? h : null;
            var oldTag = oldPayload.TryGetValue("model_tag", out var t) ? t : null;
            var newHash = d.Payload["content_hash"].StringValue;
            if (oldHash != newHash || oldTag != _embedder.ModelTag)
                toUpsert.Add(d);
        }

        var toDelete = existing.Keys.Where(id => !newById.ContainsKey(id)).ToList();
        return (toUpsert, toDelete);
    }

    private async Task EmbedAndUpsertAsync(string collection, List<PointDescriptor> descriptors)
    {
        const int batchSize = 100;
        for (int i = 0; i < descriptors.Count; i += batchSize)
        {
            var batch = descriptors.GetRange(i, Math.Min(batchSize, descriptors.Count - i));
            var points = new List<PointStruct>(batch.Count);
            foreach (var d in batch)
            {
                var point = new PointStruct
                {
                    Id = new PointId { Uuid = d.Id },
                    Vectors = _embedder.Embed(d.Text).ToArray(),
                };
                foreach (var kv in d.Payload)
                    point.Payload[kv.Key] = kv.Value;
                points.Add(point);
            }
            await _client.UpsertAsync(collection, points);
        }
    }

    private async Task DeletePointsAsync(string collection, IReadOnlyList<string> ids)
    {
        const int batchSize = 100;
        for (int i = 0; i < ids.Count; i += batchSize)
        {
            var batch = ids.Skip(i).Take(batchSize)
                .Select(id => new PointId { Uuid = id })
                .ToList();
            await _client.DeleteAsync(collection, batch);
        }
    }

    /// <summary>
    /// 语义搜索。返回相似度 > threshold 的 topK 条结果。
    /// 每个游戏独享 collection，无需 game_id 过滤。
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string gameId, string query, int topK = 10, float threshold = 0.55f, string searchMode = "full")
    {
        var collection = searchMode == "name"
            ? CollectionName(gameId) + "_name"
            : CollectionName(gameId);
        // 查询侧不加 BGE 官方指令前缀：离线实验（scripts/_embed_gap_experiment.py）证明
        // 短概念名查询加前缀后 top1 分数整体下降约 0.3、排序变差（2026-08-16 回退）。
        var queryVec = _embedder.Embed(query);

        try
        {
            // 每个 collection 首次搜索时确认存在，缺失时给出明确日志而不是静默返回空。
            var collectionKey = $"{gameId}|{searchMode}";
            if (!_verifiedCollections.ContainsKey(collectionKey))
            {
                if (!await _client.CollectionExistsAsync(collection))
                {
                    _logger.LogWarning(
                        "Qdrant collection '{Collection}' for game '{GameId}' does not exist. Run rebuild_index to create it.",
                        collection, gameId);
                    return Array.Empty<SearchResult>();
                }
                _verifiedCollections.TryAdd(collectionKey, 0);
            }

            var results = await _client.SearchAsync(
                collection,
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
    /// 去除概念引用（&lt;&gt; 包裹的 id）——与 rebuild_index.py 的 strip_refs 一致，
    /// 引用字面不参与向量相似度计算。
    /// </summary>
    private static string StripRefs(string text)
    {
        return ConceptRefRegex.Replace(text, "");
    }

    /// <summary>
    /// payload 级内容指纹。必须与 scripts/rebuild_index.py 的 payload_hash 完全一致：
    /// SHA256(concept_id + "\n" + type + "\n" + name_zh + "\n" + name_en + "\n" + text)。
    /// 这样 Python 与 C# 两条重建路径产出的 content_hash 可以互相复用，
    /// 增量 diff 不会因重建路径不同而反复全量重算。
    /// </summary>
    private static string ContentHash(ConceptIndexItem c, string text)
    {
        var raw = string.Join("\n", c.ConceptId, c.Type, c.NameZh ?? "", c.NameEn ?? "", text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 根据输入字符串生成确定性的 UUID，用作 Qdrant PointId。
    /// 必须与 scripts/rebuild_index.py 的 make_uuid 输出完全一致：
    /// Python uuid.UUID(bytes=...) 按网络字节序解释，而 .NET new Guid(byte[])
    /// 对前 4/2/2 字节按小端解释，直接传入会产生不同的 UUID 字符串。
    /// 因此这里先按 .NET 的混合端序重排，保证两条重建路径生成相同的 point ID。
    /// </summary>
    private static string GenerateUuid(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        // UUID version 5 (name-based, SHA-1 style, but we use SHA-256 hash)
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        var pythonCompatible = new byte[16]
        {
            guidBytes[3], guidBytes[2], guidBytes[1], guidBytes[0],
            guidBytes[5], guidBytes[4],
            guidBytes[7], guidBytes[6],
            guidBytes[8], guidBytes[9], guidBytes[10], guidBytes[11],
            guidBytes[12], guidBytes[13], guidBytes[14], guidBytes[15],
        };
        return new Guid(pythonCompatible).ToString();
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
    /// <summary>来源命名空间（ontology/game/instances/game_flow/ontology_flow 等），用于区分同 id 概念。</summary>
    public string Source { get; init; } = "";
    /// <summary>用于生成向量的拼接文本（name + definition 的全文）。</summary>
    public required string SearchText { get; init; }
}

/// <summary>
/// 增量索引的 point 描述符：Id 与 payload 已确定，向量按需生成。
/// </summary>
internal record PointDescriptor(string Id, string Text, Dictionary<string, Value> Payload);

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
