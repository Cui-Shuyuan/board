using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public class ChatOrchestratorService
{
    private readonly ILLMService _llmService;
    private readonly GameRulesService _rulesService;
    private readonly string _systemPromptTemplate;
    private readonly ILogger<ChatOrchestratorService> _logger;
    private const int MaxToolRounds = int.MaxValue;

    private static readonly JsonSerializerOptions PrettyLogOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // 工具返回序列化：不转义 < > 等字符，保证概念引用 <concept_id> 以字面形式保留，
    // 供 AnnotateReferences 注解为 <id>(中文名)
    private static readonly JsonSerializerOptions ToolResultOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ChatOrchestratorService(
        ILLMService llmService,
        GameRulesService rulesService,
        IOptions<LLMOptions> options,
        ILogger<ChatOrchestratorService> logger)
    {
        _llmService = llmService;
        _rulesService = rulesService;
        _systemPromptTemplate = options.Value.SystemPrompt;
        _logger = logger;
    }

    public async Task<string> ProcessAsync(
        string gameId,
        List<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(gameId))
        {
            throw new ArgumentException("game_id is required", nameof(gameId));
        }

        var systemPrompt = _systemPromptTemplate.Replace("{game_name}", gameId, StringComparison.OrdinalIgnoreCase);

        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new() { Role = "system", Content = systemPrompt });
        }

        messages.AddRange(history);

        var latestUser = history.LastOrDefault(m => m.Role == "user");
        var question = latestUser?.Content ?? "";
        _logger.LogInformation("[Chat] game: {GameId}, question: {Question}, count: {Count}",
            gameId,
            question == "" ? "(empty)" : question,
            history.Count);

        var tools = BuildTools(gameId);
        var evidence = new AnswerEvidence();

        for (int round = 0; round < MaxToolRounds; round++)
        {
            var response = await _llmService.ChatWithMessagesAsync(messages, tools, cancellationToken);
            var assistantMessage = response.Choices!.First().Message!;

            if (!string.IsNullOrWhiteSpace(assistantMessage.ReasoningContent))
            {
                _logger.LogInformation("[Chat] Game {GameId}, Round {Round} reasoning:\n{Reasoning}", gameId, round + 1, assistantMessage.ReasoningContent);
            }

            // If LLM returned a final answer (no tool calls)
            if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
            {
                var reply = assistantMessage.Content ?? string.Empty;
                sw.Stop();
                _logger.LogInformation("[Chat] Game {GameId}, Round {Round} final reply ({Elapsed:F0}ms, {Tier}): {Reply}",
                    gameId, round + 1, sw.Elapsed.TotalMilliseconds, evidence.GetTier(), reply);
                return reply;
            }

            _logger.LogInformation("[Chat] Game {GameId}, Round {Round} tool calls:\n{ToolCalls}",
                gameId,
                round + 1,
                string.Join("\n", assistantMessage.ToolCalls.Select(t =>
                    $"  {t.Function.Name}({FormatForLog(t.Function.Arguments)})")));

            // Add assistant message with tool calls
            messages.Add(assistantMessage);

            // Execute each tool call and add results
            foreach (var toolCall in assistantMessage.ToolCalls)
            {
                var result = await ExecuteToolAsync(toolCall, gameId, question, cancellationToken);
                _logger.LogInformation("[Chat] Game {GameId}, Tool {ToolName} result:\n{Result}", gameId, toolCall.Function.Name, FormatForLog(result));
                if (toolCall.Function.Name == "execute_plan")
                    UpdateEvidence(result, evidence);
                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    Content = result
                });
            }
        }

        // Max rounds reached, force a final answer without tools
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "请基于以上工具查询结果直接给出最终回答，不要再调用工具。"
        });

        var finalResponse = await _llmService.ChatWithMessagesAsync(messages, null, cancellationToken);
        var finalMessage = finalResponse.Choices!.First().Message!;

        if (!string.IsNullOrWhiteSpace(finalMessage.ReasoningContent))
        {
            _logger.LogInformation("[Chat] Game {GameId}, Final reasoning:\n{Reasoning}", gameId, finalMessage.ReasoningContent);
        }

        var finalReply = finalMessage.Content ?? string.Empty;
        sw.Stop();
        _logger.LogInformation("[Chat] Game {GameId}, Final reply after {Rounds} rounds ({Elapsed:F0}ms, {Tier}): {Reply}",
            gameId, MaxToolRounds, sw.Elapsed.TotalMilliseconds, evidence.GetTier(), finalReply);
        return finalReply;
    }

    /// <summary>回答证据分级：tier1 = 有 ok 数据支撑；tier2 = 只有候选兜底；tier3 = 无任何数据（自行发挥）。</summary>
    private sealed class AnswerEvidence
    {
        public bool SawData;
        public bool SawCandidates;

        public string GetTier() =>
            SawData ? "tier1-数据回答" : SawCandidates ? "tier2-候选兜底" : "tier3-无数据自行发挥";
    }

    /// <summary>从 execute_plan 结果中提取证据：ok + 非空 Matched → tier1；unresolved + 非空 Candidates → tier2。
    /// no_match / 空结果不产生任何证据——最终若两者皆无则判为 tier3。</summary>
    private static void UpdateEvidence(string toolResult, AnswerEvidence evidence)
    {
        if (evidence.SawData) return;
        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            if (!doc.RootElement.TryGetProperty("Results", out var results) || results.ValueKind != JsonValueKind.Array)
                return;
            foreach (var item in results.EnumerateArray())
            {
                var status = item.TryGetProperty("Status", out var sp) ? sp.GetString() : null;
                if (status == "ok"
                    && item.TryGetProperty("Matched", out var m)
                    && m.ValueKind == JsonValueKind.Array
                    && m.GetArrayLength() > 0)
                {
                    evidence.SawData = true;
                    return;
                }
                if (status == "unresolved"
                    && item.TryGetProperty("Candidates", out var c)
                    && c.ValueKind == JsonValueKind.Array
                    && c.GetArrayLength() > 0)
                {
                    evidence.SawCandidates = true;
                }
            }
        }
        catch
        {
            // 非 plan 结果或解析失败——不参与 tier 判定
        }
    }

    private List<ToolDefinition> BuildTools(string gameId)
    {
        // 架构：LLM 只提交查询计划，规则检索全部由程序执行。
        // 其他检索接口不再对 LLM 暴露，作为程序内部能力被 ExecutePlan 使用。
        return new List<ToolDefinition>
        {
            new()
            {
                Function = new FunctionDefinition
                {
                    Name = "execute_plan",
                    Description = @"唯一工具：查询计划执行器。把客人的规则问题编译成结构化查询计划，程序执行后把所有事实摆在返回结果里（概念定义、一层引用、流程位置、顺序/条件/边界等）。relation 选择：
- explain：X 是什么 / 怎么结算 / 怎么做 / 有什么效果 / 有几个（数量参数就在概念定义里）
- condition：能不能 X / X 有什么前提 / 什么限制（返回条件谓词、费用、目标约束）
- ordering：X 之后是什么 / 先后顺序 / 什么时候结束 / 每轮怎么轮转（返回同级选项顺序、位置、循环结构）
- boundary：X 不会发生什么 / 满了怎么办 / 上限多少（返回溢出补偿、容量等边界字段；未声明溢出补偿的轨道默认无事发生）
- flow：整体游戏流程（几个阶段、怎么进行、怎么结束）
- list：浏览全部概念目录（实体解析失败需要找概念时用）
- identify：客人用外观/位置描述某物（如""黄色的六边形标记""）但你不确定是哪个概念时，把描述原文作为 entity 传入——返回带定义的候选概念，挑最吻合的再 explain
entity 填概念 id 或准确中文名（flow/list/identify 的 entity 是描述文本）。一个问题涉及多个概念时，一个 plan 里放多个 queries 一次拿全。实体解析分三层：精确命中直接返回规则事实；未精确命中时程序会先做语义检索补候选（Status=unresolved + Candidates，含相似度分），请从候选中挑确切的概念重新发起计划，没有合适的候选再用 identify（按描述找）或 list 浏览；若程序返回 Status=no_match，说明规则库查不到任何相近概念——请先用 list 浏览确认概念是否真的不存在、或用 identify 再找一次；确认规则库没有之后，该部分只能基于你自己的知识回答，请向客人说明这是规则库之外的信息，或直接反问客人确认。",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "plan": {
                          "type": "object",
                          "properties": {
                            "queries": {
                              "type": "array",
                              "items": {
                                "type": "object",
                                "properties": {
                                  "relation": {
                                    "type": "string",
                                    "enum": ["explain", "condition", "ordering", "boundary", "flow", "list", "identify"]
                                  },
                                  "entity": {
                                    "type": "string",
                                    "description": "概念 id 或准确中文名（flow/list 可省略）"
                                  }
                                },
                                "required": ["relation"]
                              }
                            }
                          },
                          "required": ["queries"]
                        }
                      },
                      "required": ["plan"]
                    }
                    """).RootElement
                }
            }
        };
    }

    private async Task<string> ExecuteToolAsync(ToolCall toolCall, string gameId, string question, CancellationToken cancellationToken)
    {
        try
        {
            var args = JsonDocument.Parse(toolCall.Function.Arguments);

            switch (toolCall.Function.Name)
            {
                case "search_concepts":
                    {
                        var query = args.RootElement.GetProperty("query").GetString() ?? string.Empty;
                        var searchMode = "full";
                        if (args.RootElement.TryGetProperty("search_mode", out var modeProp))
                            searchMode = modeProp.GetString() ?? "full";
                        var results = await _rulesService.SearchConceptsAsync(gameId, query, searchMode);
                        return _rulesService.AnnotateReferences(JsonSerializer.Serialize(results, ToolResultOptions), gameId);
                    }

                case "get_concept":
                    {
                        var conceptId = args.RootElement.GetProperty("concept_id").GetString() ?? string.Empty;
                        var result = _rulesService.GetConceptsWithExpansion(gameId, conceptId);
                        return result.Matched.Count > 0
                            ? _rulesService.AnnotateReferences(JsonSerializer.Serialize(result, ToolResultOptions), gameId)
                            : $"{{\"error\": \"Concept '{conceptId}' not found\"}}";
                    }

                case "execute_plan":
                    {
                        if (!args.RootElement.TryGetProperty("plan", out var plan))
                            return $"{{\"error\": \"plan is required\"}}";
                        var planResult = await _rulesService.ExecutePlanAsync(gameId, plan, question);
                        return _rulesService.AnnotateReferences(JsonSerializer.Serialize(planResult, ToolResultOptions), gameId);
                    }

                case "get_game_flow":
                    {
                        var concepts = _rulesService.GetConcepts(gameId, "game");
                        return concepts.Count > 0
                            ? _rulesService.AnnotateReferences(JsonSerializer.Serialize(concepts, ToolResultOptions), gameId)
                            : $"{{\"error\": \"Game flow concept 'game' not found for '{gameId}'\"}}";
                    }

                case "get_action_conditions":
                    {
                        var actionId = args.RootElement.GetProperty("action_id").GetString() ?? string.Empty;
                        var conditions = _rulesService.GetActionConditions(gameId, actionId);
                        return _rulesService.AnnotateReferences(JsonSerializer.Serialize(conditions, ToolResultOptions), gameId);
                    }

                case "list_concept_ids":
                    {
                        var result = _rulesService.ListAllConceptIds(gameId);
                        return _rulesService.AnnotateReferences(JsonSerializer.Serialize(result, ToolResultOptions), gameId);
                    }

                default:
                    return $"{{\"error\": \"Unknown tool '{toolCall.Function.Name}'\"}}";
            }
        }
        catch (Exception ex)
        {
            return $"{{\"error\": \"{ex.Message}\"}}";
        }
    }

    private static string FormatForLog(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, PrettyLogOptions);
        }
        catch
        {
            return json;
        }
    }
}
