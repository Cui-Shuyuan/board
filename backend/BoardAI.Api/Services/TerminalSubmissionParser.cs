using System.Text.Json;
using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

/// <summary>
/// submit_answer 的纯参数解析/校验。协议修复次数、预算消耗、连续失败和终态转换
/// 仍由 ChatOrchestratorService 的单一控制循环决定。
/// </summary>
internal static class TerminalSubmissionParser
{
    internal static TerminalSubmission Handle(ToolCall toolCall, RuleRequestLedger ledger)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolCall.Function.Arguments);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return TerminalSubmission.Invalid("submit_answer 参数必须是 JSON object。");

            var outcome = ReadFlexibleString(root, "outcome", "Outcome").Trim().ToLowerInvariant();
            var text = ReadFlexibleString(root, "text", "Text").Trim();
            if (outcome is not ("answer" or "clarify" or "unavailable" or "conversation"))
                return TerminalSubmission.Invalid("outcome 必须是 answer、clarify、unavailable 或 conversation。");
            if (string.IsNullOrWhiteSpace(text))
                return TerminalSubmission.Invalid("text 不能为空。");

            var evidenceIds = ReadFlexibleStringArray(root, "evidence_ids", "EvidenceIds")
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (outcome == "answer")
            {
                if (evidenceIds.Count == 0)
                    return TerminalSubmission.Invalid(
                        "outcome=answer 时必须引用本请求已交付的 evidence_ids；没有正式依据请改用 clarify 或 unavailable。");
                var missing = evidenceIds
                    .Where(id => !ledger.IsDeliveredEvidenceId(id))
                    .ToList();
                if (missing.Count > 0)
                    return TerminalSubmission.Invalid(
                        "以下 evidence_id 未在本请求交付或不存在：" + string.Join("、", missing)
                        + "。只能引用工具结果 evidence_catalog 中的 evidence_id。");
            }
            else if (outcome == "clarify")
            {
                if (root.TryGetProperty("clarification", out var clarification)
                    && clarification.ValueKind == JsonValueKind.Object)
                {
                    var missingKind = ReadFlexibleString(clarification, "missing_kind", "MissingKind");
                    var requested = ReadFlexibleString(clarification, "requested_information", "RequestedInformation");
                    if (string.IsNullOrWhiteSpace(missingKind) || string.IsNullOrWhiteSpace(requested))
                        return TerminalSubmission.Invalid(
                            "clarify 的 clarification.missing_kind 与 requested_information 都不能为空。");
                }
            }
            else if (outcome == "unavailable")
            {
                var reason = ReadFlexibleString(root, "unavailable_reason", "UnavailableReason");
                if (reason is not ("no_rule_coverage" or "unresolved_identity" or "budget_exhausted"))
                    return TerminalSubmission.Invalid(
                        "unavailable 必须给出 unavailable_reason=no_rule_coverage、unresolved_identity 或 budget_exhausted。");
            }
            // conversation intentionally needs neither evidence nor clarification.

            return TerminalSubmission.Valid(outcome, text, evidenceIds.Count);
        }
        catch (JsonException)
        {
            return TerminalSubmission.Invalid("submit_answer 参数不是合法 JSON。");
        }
    }

    private static string ReadFlexibleString(JsonElement item, string pascal, string camel)
    {
        if (item.TryGetProperty(pascal, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        if (item.TryGetProperty(camel, out value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        return "";
    }

    private static List<string> ReadFlexibleStringArray(JsonElement item, string pascal, string camel)
    {
        JsonElement value;
        if (item.TryGetProperty(pascal, out value) && value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString() ?? "")
                .Where(element => !string.IsNullOrWhiteSpace(element))
                .ToList();
        if (item.TryGetProperty(camel, out value) && value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString() ?? "")
                .Where(element => !string.IsNullOrWhiteSpace(element))
                .ToList();
        return new List<string>();
    }
}

internal sealed record TerminalSubmission(
    bool IsValid, string Outcome, string Text, int EvidenceCount, string Error)
{
    public static TerminalSubmission Valid(string outcome, string text, int evidenceCount)
        => new(true, outcome, text, evidenceCount, "");
    public static TerminalSubmission Invalid(string error)
        => new(false, "", "", 0, error);
}
