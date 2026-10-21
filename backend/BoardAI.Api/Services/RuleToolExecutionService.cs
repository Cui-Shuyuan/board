using System.Text.Json;
using System.Text.Json.Nodes;
using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

/// <summary>
/// legacy 工具执行协作类：解析参数、兼容解包、展开 continuation、分发到 rules
/// facade，执行最终证据投影/引用注解/预算收敛并统一错误序列化。
/// 可变 continuation/ledger/evidence 状态均由调用方按请求传入或持有。
/// </summary>
internal sealed class RuleToolExecutionService
{
    private readonly GameRulesService _rulesService;
    private readonly LLMOptions _llmOptions;
    private readonly RuleToolResultProjector _toolResultProjector;
    private readonly ILogger _logger;

    public RuleToolExecutionService(
        GameRulesService rulesService,
        LLMOptions llmOptions,
        RuleToolResultProjector toolResultProjector,
        ILogger logger)
    {
        _rulesService = rulesService;
        _llmOptions = llmOptions;
        _toolResultProjector = toolResultProjector;
        _logger = logger;
    }

    private static (string Json, string? Error) ExpandContinuationQueries(
        JsonElement args,
        RuleContinuationRegistry registry,
        string? currentRulesVersion)
    {
        var raw = args.GetRawText();
        if (!raw.Contains("continuation_handle", StringComparison.Ordinal))
            return (raw, null);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return (raw, "续读参数不是合法 JSON。");
        }
        if (node is not JsonObject root
            || root["plan"] is not JsonObject plan
            || plan["queries"] is not JsonArray queries)
            return (raw, "续读必须放在 plan.queries[] 中。");

        for (var index = 0; index < queries.Count; index++)
        {
            if (queries[index] is not JsonObject query) continue;
            if (query["continuation_handle"] is not JsonValue handleValue
                || !handleValue.TryGetValue<string>(out var handle)
                || string.IsNullOrWhiteSpace(handle))
                continue;
            if (!registry.TryGet(handle, out var registration))
                return (raw, "continuation_handle 未登记或不属于本请求；请使用工具返回的 handle。");
            if (!string.Equals(registration.RulesVersion, currentRulesVersion, StringComparison.Ordinal))
                return (raw, "continuation_handle 已因规则版本变化失效；请重新发起原查询。");

            var rewritten = new JsonObject
            {
                ["relation"] = registration.Operation switch
                {
                    "members" => "members",
                    "flow" => "flow",
                    "rule_lookup" => "rule_lookup",
                    "read_evidence" => registration.Scope switch
                    {
                        QueryPayloadTypes.CatalogPage => "list",
                        QueryPayloadTypes.MemberSet => "members",
                        QueryPayloadTypes.FlowSlice => "flow",
                        _ => "explain"
                    },
                    _ => "explain"
                },
                ["entity"] = registration.Entity ?? ""
            };
            if (registration.Operation == "rule_lookup"
                && !string.IsNullOrWhiteSpace(registration.Intent))
                rewritten["intent"] = registration.Intent;
            else if (query["intent"] is JsonNode intent)
                rewritten["intent"] = intent.DeepClone();
            if (registration.Operation == "read_evidence"
                && registration.Scope == QueryPayloadTypes.FlowSlice)
                rewritten["scope"] = registration.Scope == QueryPayloadTypes.FlowSlice ? "local" : null;
            else if (registration.Operation == "flow")
                rewritten["scope"] = string.IsNullOrWhiteSpace(registration.Scope)
                    ? "local"
                    : registration.Scope;
            else if (query["scope"] is JsonNode scope)
                rewritten["scope"] = scope.DeepClone();
            if (registration.Offset.HasValue || registration.Limit.HasValue)
            {
                if (registration.Offset.HasValue) rewritten["offset"] = registration.Offset.Value;
                if (registration.Limit.HasValue) rewritten["limit"] = registration.Limit.Value;
            }
            if (registration.Include.Count > 0)
                rewritten["include"] = new JsonArray(registration.Include
                    .Select(item => JsonValue.Create(item)).ToArray());
            queries[index] = rewritten;
        }
        return (root.ToJsonString(), null);
    }


    internal async Task<string> ExecuteToolAsync(
        ToolCall toolCall,
        string gameId,
        string question,
        RuleContinuationRegistry continuationRegistry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string Finish(string value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return value;
        }

        try
        {
            JsonElement args;
            try
            {
                args = JsonDocument.Parse(toolCall.Function.Arguments).RootElement;
            }
            catch (JsonException)
            {
                return Finish(ChatToolProtocol.SerializeToolError(
                    "arguments 不是合法 JSON，请严格按函数 schema 输出 {\"plan\":{\"queries\":[{\"relation\":\"...\",\"entity\":\"...\"}]}} 形状的工具参数。"));
            }

            // 自愈：模型偶尔把参数包成 {"arguments": "<json 字符串>"} 的 OpenAI 风格外壳
            // （2026-08-16 实测：一次调用报错后模型陷入该模式 29 轮，全部得到 plan is required）。
            // 检测到外壳时解包内层 JSON 字符串再执行。
            if (!args.TryGetProperty("plan", out _)
                && args.TryGetProperty("arguments", out var wrapped)
                && wrapped.ValueKind == JsonValueKind.String)
            {
                try
                {
                    args = JsonDocument.Parse(wrapped.GetString()!).RootElement;
                }
                catch (JsonException)
                {
                    // 解包失败保持原样，走下方原有错误路径
                }
            }

            switch (toolCall.Function.Name)
            {
                case "search_concepts":
                    {
                        var query = args.GetProperty("query").GetString() ?? string.Empty;
                        var searchMode = "full";
                        if (args.TryGetProperty("search_mode", out var modeProp))
                            searchMode = modeProp.GetString() ?? "full";
                        var results = await _rulesService.SearchConceptsAsync(
                            gameId, query, searchMode, cancellationToken);
                        return Finish(_rulesService.AnnotateReferences(
                            JsonSerializer.Serialize(results, ChatToolProtocol.ToolResultOptions), gameId));
                    }

                case "get_concept":
                    {
                        var conceptId = args.GetProperty("concept_id").GetString() ?? string.Empty;
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = _rulesService.GetConceptsWithExpansion(gameId, conceptId);
                        return Finish(result.Matched.Count > 0
                            ? _rulesService.AnnotateReferences(
                                JsonSerializer.Serialize(result, ChatToolProtocol.ToolResultOptions), gameId)
                            : ChatToolProtocol.SerializeToolError($"Concept '{conceptId}' not found"));
                    }

                case "execute_plan":
                    {
                        if (!args.TryGetProperty("plan", out _))
                            return Finish(ChatToolProtocol.SerializeToolError("plan is required，arguments 顶层必须有 plan 键。"));
                        var currentRulesVersion = RulesSnapshotScope.Current is { Game: var scopeGame } scope
                            && scopeGame == gameId
                                ? scope.Version
                                : null;
                        var (expandedJson, continuationError) = ExpandContinuationQueries(
                            args, continuationRegistry, currentRulesVersion);
                        if (continuationError != null)
                            return Finish(ChatToolProtocol.SerializeToolError(continuationError));

                        using var expandedDoc = JsonDocument.Parse(expandedJson);
                        if (!expandedDoc.RootElement.TryGetProperty("plan", out var expandedPlan))
                            return Finish(ChatToolProtocol.SerializeToolError("续读展开后缺少 plan。"));
                        var planResult = await _rulesService.ExecutePlanAsync(
                            gameId, expandedPlan, question, cancellationToken);
                        var maxPackChars = Math.Max(1000, _llmOptions.MaxEvidencePackChars);
                        var budget = maxPackChars;
                        for (var attempt = 0; attempt < 4; attempt++)
                        {
                            ProjectedRuleQueryResult projected;
                            using (RuleRuntimeTelemetry.Measure("assembly", "evidence_projection"))
                                projected = _toolResultProjector.Project(gameId, planResult, budget);
                            string serialized;
                            using (RuleRuntimeTelemetry.Measure("serialization", "tool_result_serialization"))
                                serialized = _rulesService.AnnotateReferences(
                                    JsonSerializer.Serialize(projected, ChatToolProtocol.ToolResultOptions), gameId);
                            if (serialized.Length <= maxPackChars)
                                return Finish(serialized);
                            var overflow = serialized.Length - maxPackChars;
                            budget = Math.Max(1000, budget - overflow - 128);
                            _logger.LogWarning(
                                "[Chat] projected tool result exceeded budget after annotation: game={GameId}, attempt={Attempt}, chars={Chars}, max={Max}; retrying with budget={Budget}",
                                gameId, attempt + 1, serialized.Length, maxPackChars, budget);
                        }

                        var fallback = JsonSerializer.Serialize(new
                        {
                            request_id = planResult.RequestId,
                            game = gameId,
                            rules_version = planResult.RulesVersion,
                            evidence_catalog = Array.Empty<object>(),
                            queries = Array.Empty<object>(),
                            note = "evidence_budget_exceeded",
                            omitted_evidence_count = planResult.Results.Sum(result => result.Payloads.Count)
                        }, ChatToolProtocol.ToolResultOptions);
                        if (fallback.Length > maxPackChars)
                            fallback = "{\"note\":\"budget_exceeded\"}";
                        return Finish(fallback);
                    }

                case "get_game_flow":
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var concepts = _rulesService.GetConcepts(gameId, "game");
                        return Finish(concepts.Count > 0
                            ? _rulesService.AnnotateReferences(
                                JsonSerializer.Serialize(concepts, ChatToolProtocol.ToolResultOptions), gameId)
                            : ChatToolProtocol.SerializeToolError($"Game flow concept 'game' not found for '{gameId}'"));
                    }

                case "get_action_conditions":
                    {
                        var actionId = args.GetProperty("action_id").GetString() ?? string.Empty;
                        cancellationToken.ThrowIfCancellationRequested();
                        var conditions = _rulesService.GetActionConditions(gameId, actionId);
                        return Finish(_rulesService.AnnotateReferences(
                            JsonSerializer.Serialize(conditions, ChatToolProtocol.ToolResultOptions), gameId));
                    }

                case "list_concept_ids":
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = _rulesService.ListAllConceptIds(gameId);
                        return Finish(_rulesService.AnnotateReferences(
                            JsonSerializer.Serialize(result, ChatToolProtocol.ToolResultOptions), gameId));
                    }

                default:
                    return Finish(ChatToolProtocol.SerializeToolError($"Unknown tool '{toolCall.Function.Name}'"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Finish(ChatToolProtocol.SerializeToolError(ex.Message));
        }
    }
}
