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
        return new List<ToolDefinition>
        {
            new()
            {
                Function = new FunctionDefinition
                {
                    Name = "search_concepts",
                    Description = $"通过关键词搜索当前游戏《{gameId}》中的概念、行动、条件、触发器等。支持 ontology 命名空间查询，例如 'ontology::resource' 只搜索 ontology 中的 resource 概念；不带命名空间时同时搜索当前游戏和 ontology。",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "query": {
                          "type": "string",
                          "description": "搜索关键词，可以是中文或英文；支持 'ontology::concept_id' 格式限定只查 ontology"
                        }
                      },
                      "required": ["query"]
                    }
                    """).RootElement
                }
            },
            new()
            {
                Function = new FunctionDefinition
                {
                    Name = "get_concept",
                    Description = $"通过概念 ID 获取当前游戏《{gameId}》中的详细信息，包括定义、条件、触发效果等。支持 'ontology::concept_id' 格式只查询 ontology；不带命名空间时同时查询当前游戏和 ontology，返回所有匹配结果。",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "concept_id": {
                          "type": "string",
                          "description": "概念 ID；可带 'ontology::' 前缀限定 ontology，如 'ontology::resource'"
                        }
                      },
                      "required": ["concept_id"]
                    }
                    """).RootElement
                }
            },
            new()
            {
                Function = new FunctionDefinition
                {
                    Name = "get_action_conditions",
                    Description = "获取某个行动的所有前置条件和触发条件。",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "action_id": {
                          "type": "string",
                          "description": "行动 ID"
                        }
                      },
                      "required": ["action_id"]
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
                        var results = await _rulesService.SearchConceptsAsync(gameId, query);
                        return JsonSerializer.Serialize(results);
                    }

                case "get_concept":
                    {
                        var conceptId = args.RootElement.GetProperty("concept_id").GetString() ?? string.Empty;
                        var concepts = _rulesService.GetConcepts(gameId, conceptId);
                        return concepts.Count > 0
                            ? JsonSerializer.Serialize(concepts)
                            : $"{{\"error\": \"Concept '{conceptId}' not found\"}}";
                    }

                case "get_action_conditions":
                    {
                        var actionId = args.RootElement.GetProperty("action_id").GetString() ?? string.Empty;
                        var conditions = _rulesService.GetActionConditions(gameId, actionId);
                        return JsonSerializer.Serialize(conditions);
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
