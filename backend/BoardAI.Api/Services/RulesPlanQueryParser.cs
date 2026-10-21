using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 一次 execute_plan 中单个 query 在进入执行层前的值类型快照。
/// 所有字段都在解析阶段完成默认值、范围限制和展示/检索 intent 分离，
/// 下游处理器不再读取调用方传入的 JsonElement。
/// </summary>
internal sealed record ParsedPlanQuery(
    string Relation,
    string Entity,
    string ExplicitIntent,
    string DisplayIntent,
    string Scope,
    IReadOnlyList<string> Include,
    int Offset,
    int Limit,
    bool HasExplicitLimit);

/// <summary>
/// execute_plan 参数解析器。只负责把外部 JSON 转成稳定的内部查询描述，
/// 不执行实体解析、检索或结果构造。
/// </summary>
internal static class RulesPlanQueryParser
{
    public static bool TryParse(
        JsonElement plan,
        string question,
        out List<ParsedPlanQuery> queries,
        out string error)
    {
        queries = new List<ParsedPlanQuery>();
        error = "";

        if (!plan.TryGetProperty("queries", out var queriesElement)
            || queriesElement.ValueKind != JsonValueKind.Array)
        {
            error = "plan 缺少 queries 数组。";
            return false;
        }

        var queryItems = queriesElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .ToList();

        for (var queryIndex = 0; queryIndex < queryItems.Count; queryIndex++)
        {
            var q = queryItems[queryIndex];
            var relation = q.TryGetProperty("relation", out var rp) ? rp.GetString() ?? "" : "";
            var entity = q.TryGetProperty("entity", out var ep) ? ep.GetString() ?? "" : "";
            var explicitIntent = q.TryGetProperty("intent", out var ip) ? ip.GetString() ?? "" : "";
            explicitIntent = explicitIntent.Trim();
            var scope = q.TryGetProperty("scope", out var scopeProp) ? scopeProp.GetString() ?? "" : "";
            var offset = q.TryGetProperty("offset", out var offsetProp)
                && offsetProp.ValueKind == JsonValueKind.Number
                && offsetProp.TryGetInt32(out var parsedOffset)
                    ? Math.Max(0, parsedOffset)
                    : 0;
            var parsedLimit = 50;
            var hasExplicitLimit = q.TryGetProperty("limit", out var limitProp)
                && limitProp.ValueKind == JsonValueKind.Number
                && limitProp.TryGetInt32(out parsedLimit);
            var limit = hasExplicitLimit ? Math.Clamp(parsedLimit, 1, 200) : 50;
            var include = q.TryGetProperty("include", out var includeProp)
                && includeProp.ValueKind == JsonValueKind.Array
                    ? includeProp.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString() ?? "")
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToList()
                    : new List<string>();

            // 展示/日志用 intent：单一子问题缺失时回填客人原问句，方便观察；
            // 但检索只使用模型显式提供的 intent。缺失项保守回到原有 entity 路径，
            // 多子问题更不能把整段问题不加区分地套到每个实体上。
            var displayIntent = explicitIntent;
            if (string.IsNullOrWhiteSpace(displayIntent)
                && queryItems.Count == 1
                && !string.IsNullOrWhiteSpace(question))
                displayIntent = question.Trim();

            queries.Add(new ParsedPlanQuery(
                relation, entity, explicitIntent, displayIntent, scope, include,
                offset, limit, hasExplicitLimit));
        }

        return true;
    }
}
