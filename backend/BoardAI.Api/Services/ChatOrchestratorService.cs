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
        _logger.LogInformation("[Chat] game: {GameId}, question: {Question}, count: {Count}",
            gameId,
            latestUser?.Content ?? "(empty)",
            history.Count);

        var tools = BuildTools(gameId);

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
                _logger.LogInformation("[Chat] Game {GameId}, Round {Round} final reply ({Elapsed:F0}ms): {Reply}", gameId, round + 1, sw.Elapsed.TotalMilliseconds, reply);
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
                var result = await ExecuteToolAsync(toolCall, gameId, cancellationToken);
                _logger.LogInformation("[Chat] Game {GameId}, Tool {ToolName} result:\n{Result}", gameId, toolCall.Function.Name, FormatForLog(result));
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
        _logger.LogInformation("[Chat] Game {GameId}, Final reply after {Rounds} rounds ({Elapsed:F0}ms): {Reply}", gameId, MaxToolRounds, sw.Elapsed.TotalMilliseconds, finalReply);
        return finalReply;
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
entity 填概念 id 或准确中文名（flow/list/identify 的 entity 是描述文本）。一个问题涉及多个概念时，一个 plan 里放多个 queries 一次拿全。实体解析失败会返回候选列表：从候选中挑确切的名字重新发起计划；没有候选用 identify（按描述找）或 list 浏览。",
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

    private async Task<string> ExecuteToolAsync(ToolCall toolCall, string gameId, CancellationToken cancellationToken)
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
                        var planResult = await _rulesService.ExecutePlanAsync(gameId, plan);
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
