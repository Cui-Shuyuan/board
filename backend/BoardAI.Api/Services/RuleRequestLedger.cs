using System.Text;
using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 单次聊天请求内的轻量请求账本。只复用完全相同、同一规则版本下的工具计划；
/// 不按语义相似或相同局部 id 合并。正式证据 id 与候选读取状态从投影结果中读取，
/// 只作为程序侧校验依据，不把 evidence id 当成答案正确证明。
/// </summary>
internal sealed class RuleRequestLedger
{
    private readonly Dictionary<string, string> _toolResults = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deliveredEvidenceIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _queryReuseCounts = new(StringComparer.Ordinal);

    public int ToolCallsReused { get; private set; }
    public int ToolCallsExecuted { get; private set; }
    public int DeliveredEvidenceCount => _deliveredEvidenceIds.Count;
    public int DeliveredEvidenceChars { get; private set; }
    public int CandidateAutoReadCount { get; private set; }
    public int ProtocolValidationErrors { get; private set; }
    public IReadOnlyCollection<string> DeliveredEvidenceIds => _deliveredEvidenceIds;
    public IReadOnlyDictionary<string, int> QueryReuseCounts => _queryReuseCounts;

    public string BuildKey(string toolName, string arguments, string? rulesVersion)
    {
        var normalized = toolName == "execute_plan"
            ? NormalizeExecutePlanArguments(arguments)
            : (arguments ?? "").Trim();
        return (rulesVersion ?? "") + "\u0000" + toolName + "\u0000" + normalized;
    }

    public bool TryGet(string key, out string result)
        => _toolResults.TryGetValue(key, out result!);

    public void Store(string key, string result)
    {
        _toolResults[key] = result;
        ToolCallsExecuted++;
    }

    public void RecordReuse(string key)
    {
        ToolCallsReused++;
        _queryReuseCounts[key] = _queryReuseCounts.GetValueOrDefault(key) + 1;
    }

    public void ObserveToolResult(string toolResult)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (root.TryGetProperty("evidence_catalog", out var catalog)
                && catalog.ValueKind == JsonValueKind.Array)
            {
                foreach (var evidence in catalog.EnumerateArray())
                {
                    if (evidence.TryGetProperty("evidence_id", out var idProp)
                        && idProp.ValueKind == JsonValueKind.String)
                    {
                        var id = idProp.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(id) && _deliveredEvidenceIds.Add(id))
                            DeliveredEvidenceChars += evidence.GetRawText().Length;
                    }
                }
            }

            if (root.TryGetProperty("queries", out var queries)
                && queries.ValueKind == JsonValueKind.Array)
            {
                foreach (var query in queries.EnumerateArray())
                {
                    if (query.TryGetProperty("omissions", out var omissions)
                        && omissions.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var omission in omissions.EnumerateArray())
                        {
                            if (omission.TryGetProperty("reason", out var reason)
                                && reason.ValueKind == JsonValueKind.String
                                && reason.GetString() == QueryOmissionReasons.Oversized)
                                ProtocolValidationErrors += 0; // explicit marker; not a protocol failure
                        }
                    }
                }
            }

            if (root.TryGetProperty("evidence_catalog", out var cat))
            {
                foreach (var evidence in cat.EnumerateArray())
                {
                    if (!evidence.TryGetProperty("data", out var data)
                        || data.ValueKind != JsonValueKind.Object)
                        continue;
                    if (data.TryGetProperty("role", out var role)
                        && role.ValueKind == JsonValueKind.String
                        && role.GetString() == "rule_candidate")
                        CandidateAutoReadCount++;
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON tool result is not a delivery observation.
        }
    }

    public void RecordProtocolValidationError() => ProtocolValidationErrors++;

    public bool IsDeliveredEvidenceId(string evidenceId)
        => !string.IsNullOrWhiteSpace(evidenceId) && _deliveredEvidenceIds.Contains(evidenceId);

    private static string NormalizeExecutePlanArguments(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var root = doc.RootElement;
            if (root.TryGetProperty("plan", out var plan))
                return Canonicalize(plan);
        }
        catch (JsonException)
        {
            // Fall through to raw-string identity; malformed arguments are not
            // equivalent to any valid plan.
        }
        return (arguments ?? "").Trim();
    }

    private static string Canonicalize(JsonElement element)
    {
        var builder = new StringBuilder();
        Canonicalize(element, builder);
        return builder.ToString();
    }

    private static void Canonicalize(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    Canonicalize(property.Value, builder);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem) builder.Append(',');
                    firstItem = false;
                    Canonicalize(item, builder);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;
            case JsonValueKind.Number:
                builder.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
        }
    }
}
