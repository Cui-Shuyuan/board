using System.Text.Json;
using System.Text.RegularExpressions;

namespace BoardAI.Api.Services;

/// <summary>
/// 规则引用协作类：概念引用注解、一层 related 扩展，以及 Plan ok 结果的引用扩展。
/// 自身不缓存。
/// </summary>
public sealed class RulesReferenceService
{
    private readonly RulesConceptCatalog _catalog;
    private readonly RulesNameIndexService _nameIndex;

    public RulesReferenceService(
        RulesConceptCatalog catalog,
        RulesNameIndexService nameIndex)
    {
        _catalog = catalog;
        _nameIndex = nameIndex;
    }

    /// <summary>
    /// 把文本中的概念引用 <concept_id> / <ontology::concept_id> 注解为 <id>(中文名)，
    /// 让 LLM 无需自行翻译英文 id（如 <idea_marker>(创意标记)）。
    /// 查不到映射的引用（枚举值、未知 id）保持原样。
    /// </summary>
    public string AnnotateReferences(string text, string game)
    {
        var map = _nameIndex.GetNameMap(game);
        if (map.Count == 0) return text;
        return RulesJsonUtils.ConceptRefRegex.Replace(
            text,
            m =>
            {
                var raw = m.Groups[1].Value;
                var localId = raw.Contains("::") ? raw[(raw.IndexOf("::", StringComparison.Ordinal) + 2)..] : raw;
                if (map.TryGetValue(localId, out var name) && !string.IsNullOrEmpty(name))
                    return $"<{raw}>({name})";
                return m.Value;
            });
    }


    /// <summary>
    /// get_concept 的一层引用扩展：matched 概念直接引用的概念一并返回为 related。
    /// 引用提取与注解共用同一个正则；能解析（GetConcepts 查得到）的才扩展，
    /// 排除自身与 matched 集合；related 内部的引用不递归扩展。
    /// 按首现顺序最多扩展 MaxRelatedConcepts 个，超出截断并在 Note 提示。
    /// </summary>
    private const int MaxRelatedConcepts = 10;


    public GetConceptResult GetConceptsWithExpansion(string game, string id)
    {
        var read = ReadConceptWithExpansionWithPaths(game, id);
        return new GetConceptResult
        {
            Matched = read.Matched.Select(hit => hit.Element).ToList(),
            Related = read.Related.Select(hit => hit.Element).ToList(),
            Note = read.Note
        };
    }


    /// <summary>
    /// 带规范路径的一层引用扩展。正文按 canonical path 去重，但关联边按
    /// (父路径, 子路径) 保留；每个 related 携带自己的真实路径与引用它的父路径，
    /// 不继承某个父节点的路径。
    /// </summary>
    public ConceptReadResult ReadConceptWithExpansionWithPaths(string game, string id)
    {
        var read = new ConceptReadResult();
        var matched = _catalog.GetConceptsWithPaths(game, id).ToList();
        if (matched.Count == 0)
            return read;

        read.Matched = matched;
        var (_, localId) = RulesTextUtils.ParseNamespace(id);
        var excluded = new HashSet<string>(StringComparer.Ordinal) { localId };
        foreach (var hit in matched)
            excluded.Add(hit.CanonicalPath);

        var seenRefs = new HashSet<string>(StringComparer.Ordinal);
        var appendedEdges = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        foreach (var hit in matched)
        {
            var text = JsonSerializer.Serialize(hit.Element, RulesJsonUtils.RelaxedJsonOptions);
            foreach (Match match in RulesJsonUtils.ConceptRefRegex.Matches(text))
            {
                var raw = match.Groups[1].Value;
                if (!seenRefs.Add(hit.CanonicalPath + "\u0000" + raw)) continue;

                var local = raw.Contains("::")
                    ? raw[(raw.IndexOf("::", StringComparison.Ordinal) + 2)..]
                    : raw;
                if (excluded.Contains(local)) continue;

                foreach (var found in _catalog.GetConceptsWithPaths(game, raw))
                {
                    var edgeKey = hit.CanonicalPath + "\u0000" + found.CanonicalPath;
                    if (excluded.Contains(found.CanonicalPath) || !appendedEdges.Add(edgeKey))
                        continue;
                    if (read.Related.Count >= MaxRelatedConcepts)
                    {
                        truncated = true;
                        break;
                    }
                    found.ParentPath = hit.CanonicalPath;
                    read.Related.Add(found);
                }
                if (truncated) break;
            }
            if (truncated) break;
        }

        if (truncated)
        {
            read.Note +=
                $"本次直接引用较多，related 仅返回首现顺序的前 {MaxRelatedConcepts} 个；其余引用请用具体 ID 继续调用 get_concept。";
        }
        return read;
    }


    /// <summary>
    /// ok 结果的引用扩展：所有命中概念直接引用的概念的并集（去重、按首现顺序、
    /// 上限 MaxRelatedConcepts）。light=true（问题级直呼）时跳过本体引用
    /// （&lt;ontology::x&gt;）——程序已拍板目标概念，只给游戏概念引用，减少 LLM 噪声。
    /// </summary>
    public List<JsonElement> ExpandRelated(string game, List<JsonElement> matched, bool light)
    {
        var related = new List<JsonElement>();
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in matched)
        {
            var mid = RulesTextUtils.GetElementId(el);
            if (!string.IsNullOrEmpty(mid)) excluded.Add(mid);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var appendedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in matched)
        {
            var text = JsonSerializer.Serialize(el, RulesJsonUtils.RelaxedJsonOptions);
            foreach (Match m in RulesJsonUtils.ConceptRefRegex.Matches(text))
            {
                var raw = m.Groups[1].Value;
                if (!seen.Add(raw)) continue;
                if (light && raw.Contains("::")) continue; // 本体概念在问题级直呼时不展开

                var local = raw.Contains("::") ? raw[(raw.IndexOf("::", StringComparison.Ordinal) + 2)..] : raw;
                if (excluded.Contains(local)) continue;

                foreach (var found in _catalog.GetConcepts(game, raw))
                {
                    var foundId = RulesTextUtils.GetElementId(found);
                    if (!string.IsNullOrEmpty(foundId) && !appendedIds.Add(foundId))
                        continue;
                    if (related.Count >= MaxRelatedConcepts) return related;
                    related.Add(found);
                }
            }
        }
        return related;
    }
}
