using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public partial class GameRulesService
{

    /// <summary>
    /// 把文本中的概念引用 <concept_id> / <ontology::concept_id> 注解为 <id>(中文名)，
    /// 让 LLM 无需自行翻译英文 id（如 <idea_marker>(创意标记)）。
    /// 查不到映射的引用（枚举值、未知 id）保持原样。
    /// </summary>
    public string AnnotateReferences(string text, string game)
    {
        var map = _nameIndex.GetNameMap(game);
        if (map.Count == 0) return text;
        return ConceptRefRegex.Replace(
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
    /// 排除自身与 matched 集合；related 内部的引用不再递归扩展。
    /// 按首现顺序最多扩展 MaxRelatedConcepts 个，超出截断并在 Note 提示。
    /// </summary>
    private const int MaxRelatedConcepts = 10;


    public GetConceptResult GetConceptsWithExpansion(string game, string id)
    {
        var matched = GetConcepts(game, id).ToList();
        if (matched.Count == 0)
            return new GetConceptResult();

        var (_, localId) = RulesTextUtils.ParseNamespace(id);

        // 排除集合：查询概念自身 + 已命中的概念（避免 related 里出现 matched 副本）
        var excluded = new HashSet<string>(StringComparer.Ordinal) { localId };
        foreach (var element in matched)
        {
            var matchedId = RulesTextUtils.GetElementId(element);
            if (!string.IsNullOrEmpty(matchedId))
                excluded.Add(matchedId);
        }

        var related = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // 已扩展概念 id——不同引用串（如 <score_track> 与 <ontology::score_track>）可能解析到同一概念，按解析结果去重
        var appendedIds = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        foreach (var element in matched)
        {
            var text = JsonSerializer.Serialize(element, RelaxedJsonOptions);
            foreach (Match m in ConceptRefRegex.Matches(text))
            {
                var raw = m.Groups[1].Value;
                if (!seen.Add(raw)) continue;

                var local = raw.Contains("::")
                    ? raw[(raw.IndexOf("::", StringComparison.Ordinal) + 2)..]
                    : raw;
                if (excluded.Contains(local)) continue;

                foreach (var found in GetConcepts(game, raw))
                {
                    var foundId = RulesTextUtils.GetElementId(found);
                    if (!string.IsNullOrEmpty(foundId) && !appendedIds.Add(foundId))
                        continue; // 已扩展过该概念
                    if (related.Count >= MaxRelatedConcepts)
                    {
                        truncated = true;
                        break;
                    }
                    related.Add(found);
                }
                if (truncated) break;
            }
            if (truncated) break;
        }

        var result = new GetConceptResult { Matched = matched, Related = related };
        if (truncated)
        {
            result.Note +=
                $"本次直接引用较多，related 仅返回首现顺序的前 {MaxRelatedConcepts} 个；其余引用请用具体 ID 继续调用 get_concept。";
        }
        return result;
    }




    /// <summary>
    /// ok 结果的引用扩展：所有命中概念直接引用的概念的并集（去重、按首现顺序、
    /// 上限 MaxRelatedConcepts）。light=true（问题级直呼）时跳过本体引用
    /// （&lt;ontology::x&gt;）——程序已拍板目标概念，只给游戏概念引用，减少 LLM 噪声。
    /// </summary>
    private List<JsonElement> ExpandRelated(string game, List<JsonElement> matched, bool light)
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
            var text = JsonSerializer.Serialize(el, RelaxedJsonOptions);
            foreach (Match m in ConceptRefRegex.Matches(text))
            {
                var raw = m.Groups[1].Value;
                if (!seen.Add(raw)) continue;
                if (light && raw.Contains("::")) continue; // 本体概念在问题级直呼时不展开

                var local = raw.Contains("::") ? raw[(raw.IndexOf("::", StringComparison.Ordinal) + 2)..] : raw;
                if (excluded.Contains(local)) continue;

                foreach (var found in GetConcepts(game, raw))
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
