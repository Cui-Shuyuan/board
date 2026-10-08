using System.Diagnostics;
using System.Globalization;
using System.Text;
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
    private readonly int _maxToolRounds;
    private readonly int _maxToolCallsPerRound;
    private readonly int _maxTotalToolCalls;
    private readonly TimeSpan _maxRequestTimeout;
    private readonly int _maxRepeatedFailures;
    private readonly int _maxConversationMessages;

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
        _logger = logger;

        var llmOptions = options.Value;
        _systemPromptTemplate = llmOptions.SystemPrompt;
        _maxToolRounds = Math.Max(1, llmOptions.MaxToolRounds);
        _maxToolCallsPerRound = Math.Max(1, llmOptions.MaxToolCallsPerRound);
        _maxTotalToolCalls = Math.Max(1, llmOptions.MaxTotalToolCalls);
        _maxRequestTimeout = TimeSpan.FromSeconds(Math.Max(1, llmOptions.MaxRequestSeconds));
        _maxRepeatedFailures = Math.Max(1, llmOptions.MaxRepeatedFailures);
        _maxConversationMessages = Math.Max(2, llmOptions.MaxConversationMessages);
    }

    public async Task<string> ProcessAsync(
        string gameId,
        List<ChatMessage> history,
        ChatContext? context = null,
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

        var contextMessage = BuildContextMessage(context);
        if (!string.IsNullOrWhiteSpace(contextMessage))
        {
            messages.Add(new() { Role = "system", Content = contextMessage });
        }

        var trimmedHistory = TrimHistory(history, _maxConversationMessages);
        messages.AddRange(trimmedHistory);

        var latestUser = history.LastOrDefault(m => m.Role == "user");
        var question = latestUser?.Content ?? "";
        _logger.LogInformation("[Chat] game: {GameId}, question: {Question}, count: {Count}",
            gameId,
            question == "" ? "(empty)" : question,
            history.Count);

        var tools = BuildTools(gameId);
        var evidence = new AnswerEvidence();
        var roundsExecuted = 0;
        string? stopReason = null;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(_maxRequestTimeout);
        var effectiveToken = linkedCts.Token;

        try
        {
            var consecutiveToolFailures = 0;
            var repeatedCallCount = 0;
            var repeatedFailureCount = 0;
            var totalToolCallsExecuted = 0;
            string? lastToolCallSignature = null;
            string? lastFailureSignature = null;

            for (var round = 0; round < _maxToolRounds; round++)
            {
                effectiveToken.ThrowIfCancellationRequested();
                roundsExecuted = round + 1;

                var response = await _llmService.ChatWithMessagesAsync(messages, tools, effectiveToken);
                var assistantMessage = response.Choices?.FirstOrDefault()?.Message
                    ?? throw new InvalidOperationException("LLM returned empty response.");

                if (!string.IsNullOrWhiteSpace(assistantMessage.ReasoningContent))
                {
                    _logger.LogInformation("[Chat] Game {GameId}, Round {Round} reasoning:\n{Reasoning}",
                        gameId, roundsExecuted, assistantMessage.ReasoningContent);
                }

                // If LLM returned a final answer (no tool calls)
                if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
                {
                    var reply = assistantMessage.Content ?? string.Empty;
                    sw.Stop();
                    _logger.LogInformation("[Chat] Game {GameId}, Round {Round} final reply ({Elapsed:F0}ms, {Tier}): {Reply}",
                        gameId, roundsExecuted, sw.Elapsed.TotalMilliseconds, evidence.GetTier(), reply);
                    return reply;
                }

                _logger.LogInformation("[Chat] Game {GameId}, Round {Round} tool calls:\n{ToolCalls}",
                    gameId,
                    roundsExecuted,
                    string.Join("\n", assistantMessage.ToolCalls.Select(t =>
                        $"  {t.Function.Name}({FormatForLog(t.Function.Arguments)})")));

                // Add assistant message with tool calls
                messages.Add(assistantMessage);

                for (var toolIndex = 0; toolIndex < assistantMessage.ToolCalls.Count; toolIndex++)
                {
                    if (toolIndex >= _maxToolCallsPerRound)
                    {
                        stopReason = "max_tool_calls_per_round";
                        AppendSkippedToolResults(messages, assistantMessage.ToolCalls, toolIndex, stopReason);
                        break;
                    }

                    if (totalToolCallsExecuted >= _maxTotalToolCalls)
                    {
                        stopReason = "max_total_tool_calls";
                        AppendSkippedToolResults(messages, assistantMessage.ToolCalls, toolIndex, stopReason);
                        break;
                    }

                    effectiveToken.ThrowIfCancellationRequested();

                    var toolCall = assistantMessage.ToolCalls[toolIndex];
                    var toolCallSignature = BuildToolCallSignature(toolCall);

                    if (toolCallSignature == lastToolCallSignature)
                    {
                        repeatedCallCount++;
                    }
                    else
                    {
                        repeatedCallCount = 1;
                        lastToolCallSignature = toolCallSignature;
                    }

                    var result = await ExecuteToolAsync(toolCall, gameId, question, effectiveToken);
                    totalToolCallsExecuted++;
                    effectiveToken.ThrowIfCancellationRequested();

                    _logger.LogInformation("[Chat] Game {GameId}, Tool {ToolName} result:\n{Result}",
                        gameId, toolCall.Function.Name, FormatForLog(result));
                    if (toolCall.Function.Name == "execute_plan")
                        UpdateEvidence(result, evidence);

                    if (TryGetToolError(result, out var errorText))
                    {
                        consecutiveToolFailures++;
                        var failureSignature = toolCallSignature + "\n" + errorText;
                        if (failureSignature == lastFailureSignature)
                        {
                            repeatedFailureCount++;
                        }
                        else
                        {
                            repeatedFailureCount = 1;
                            lastFailureSignature = failureSignature;
                        }
                    }
                    else
                    {
                        consecutiveToolFailures = 0;
                        repeatedFailureCount = 0;
                        lastFailureSignature = null;
                    }

                    messages.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = toolCall.Id,
                        Content = result
                    });

                    stopReason = GetToolStopReason(consecutiveToolFailures, repeatedCallCount, repeatedFailureCount);
                    if (stopReason != null)
                    {
                        AppendSkippedToolResults(messages, assistantMessage.ToolCalls, toolIndex + 1, stopReason);
                        break;
                    }
                }

                if (stopReason != null)
                {
                    _logger.LogInformation(
                        "[Chat] Game {GameId}, tool loop stopped: reason={Reason}, rounds={Rounds}, elapsed={Elapsed:F0}ms, totalToolCalls={TotalToolCalls}, consecutiveFailures={ConsecutiveFailures}, repeatedCalls={RepeatedCalls}, repeatedFailures={RepeatedFailures}",
                        gameId, stopReason, roundsExecuted, sw.Elapsed.TotalMilliseconds, totalToolCallsExecuted,
                        consecutiveToolFailures, repeatedCallCount, repeatedFailureCount);
                    break;
                }
            }

            if (stopReason == null)
                stopReason = "max_tool_rounds";

            return await RequestFinalAnswerAsync(gameId, messages, evidence, stopReason, roundsExecuted, sw, effectiveToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogInformation(
                "[Chat] Game {GameId}, canceled by client: rounds={Rounds}, elapsed={Elapsed:F0}ms",
                gameId, roundsExecuted, sw.Elapsed.TotalMilliseconds);
            throw;
        }
        catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogInformation(
                "[Chat] Game {GameId}, request timeout: rounds={Rounds}, elapsed={Elapsed:F0}ms",
                gameId, roundsExecuted, sw.Elapsed.TotalMilliseconds);
            return BuildIncompleteReply("request_timeout", evidence);
        }
        finally
        {
            sw.Stop();
        }
    }

    private static List<ChatMessage> TrimHistory(List<ChatMessage> history, int maxMessages)
    {
        if (history.Count <= maxMessages) return history;
        return history.Skip(history.Count - maxMessages).ToList();
    }

    private static string BuildToolCallSignature(ToolCall toolCall)
    {
        var arguments = (toolCall.Function.Arguments ?? string.Empty).Trim();
        return toolCall.Function.Name + "|" + arguments;
    }

    private static bool TryGetToolError(string result, out string errorText)
    {
        errorText = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(result);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error))
            {
                errorText = error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? string.Empty
                    : error.GetRawText();
                return true;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 的工具结果不按 error 处理；当前 ExecuteToolAsync 总会返回 JSON。
        }

        return false;
    }

    private string? GetToolStopReason(int consecutiveToolFailures, int repeatedCallCount, int repeatedFailureCount)
    {
        if (consecutiveToolFailures >= _maxRepeatedFailures)
            return "consecutive_tool_failures";
        if (repeatedCallCount >= _maxRepeatedFailures)
            return "repeated_tool_call";
        if (repeatedFailureCount >= _maxRepeatedFailures)
            return "repeated_failure_signature";
        return null;
    }

    private static void AppendSkippedToolResults(
        List<ChatMessage> messages,
        List<ToolCall> toolCalls,
        int startIndex,
        string stopReason)
    {
        for (var i = startIndex; i < toolCalls.Count; i++)
        {
            messages.Add(new ChatMessage
            {
                Role = "tool",
                ToolCallId = toolCalls[i].Id,
                Content = "{\"error\": \"工具调用因达到停止条件（" + stopReason + "）未执行。\"}"
            });
        }
    }

    private async Task<string> RequestFinalAnswerAsync(
        string gameId,
        List<ChatMessage> messages,
        AnswerEvidence evidence,
        string stopReason,
        int roundsExecuted,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "请基于以上工具查询结果直接给出最终回答，不要再调用工具。"
                + "如果可用事实不足以完整回答，请明确说明未能完成查询，并请客人缩小问题范围或换一种问法；不要编造规则事实。"
        });

        try
        {
            var finalResponse = await _llmService.ChatWithMessagesAsync(messages, null, cancellationToken);
            var finalMessage = finalResponse.Choices?.FirstOrDefault()?.Message;
            var finalReply = finalMessage?.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(finalReply))
            {
                _logger.LogWarning("[Chat] Game {GameId}, final summary returned empty content", gameId);
                finalReply = BuildIncompleteReply(stopReason, evidence);
            }

            sw.Stop();
            _logger.LogInformation(
                "[Chat] Game {GameId}, final reply after stop={StopReason}, rounds={Rounds}, elapsed={Elapsed:F0}ms, {Tier}: {Reply}",
                gameId, stopReason, roundsExecuted, sw.Elapsed.TotalMilliseconds, evidence.GetTier(), finalReply);
            return finalReply;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogInformation(
                "[Chat] Game {GameId}, final summary canceled: stop={StopReason}, rounds={Rounds}, elapsed={Elapsed:F0}ms",
                gameId, stopReason, roundsExecuted, sw.Elapsed.TotalMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex,
                "[Chat] Game {GameId}, final summary failed: stop={StopReason}, rounds={Rounds}, elapsed={Elapsed:F0}ms",
                gameId, stopReason, roundsExecuted, sw.Elapsed.TotalMilliseconds);
            return BuildIncompleteReply(stopReason, evidence);
        }
    }

    private static string BuildIncompleteReply(string stopReason, AnswerEvidence evidence)
    {
        var reasonText = stopReason switch
        {
            "request_timeout" => "已达到本次问答的时间上限",
            "max_tool_rounds" => "已达到本次查询的工具调用轮数上限",
            "max_tool_calls_per_round" => "单轮工具调用数量过多",
            "max_total_tool_calls" => "本次查询的工具调用总数过多",
            "consecutive_tool_failures" => "工具连续返回错误",
            "repeated_tool_call" => "工具调用重复",
            "repeated_failure_signature" => "工具连续以相同方式失败",
            _ => "本次查询未能完成"
        };

        var evidenceText = evidence.SawData
            ? "我已经查到部分规则事实，但信息还不够完整。"
            : "我还没有拿到足够的规则事实。";

        return "抱歉，" + reasonText + "，" + evidenceText + "请缩小问题范围，或者换一种更具体的问法。";
    }

    private string? BuildContextMessage(ChatContext? context)
    {
        if (context == null) return null;

        var groupPath = CleanPath(context.GroupPath);
        var recentCues = context.RecentCues?
            .Where(HasRecentCueContent)
            .Take(5)
            .ToList() ?? new List<RecentCueContext>();

        var hasContent = !string.IsNullOrWhiteSpace(context.GameName)
            || !string.IsNullOrWhiteSpace(context.CueId)
            || !string.IsNullOrWhiteSpace(context.CueText)
            || context.CueIndex.HasValue
            || context.Position.HasValue
            || groupPath.Count > 0
            || recentCues.Count > 0;

        if (!hasContent) return null;

        var builder = new StringBuilder();
        builder.AppendLine("当前上下文（仅用于理解客人提问，不要逐字复述）：");

        if (!string.IsNullOrWhiteSpace(context.GameName))
            builder.AppendLine($"游戏：{context.GameName}");

        if (groupPath.Count > 0)
            builder.AppendLine($"当前小节：{string.Join(" > ", groupPath)}");

        if (!string.IsNullOrWhiteSpace(context.CueId))
            builder.AppendLine($"当前 cue：{context.CueId}");

        if (!string.IsNullOrWhiteSpace(context.CueText))
            builder.AppendLine($"当前台词：{Truncate(context.CueText.Trim(), 300)}");

        if (context.Position.HasValue)
            builder.AppendLine(
                $"当前播放位置：{context.Position.Value.ToString("0.##", CultureInfo.InvariantCulture)} 秒");

        if (recentCues.Count > 0)
        {
            var hasActionSummaries = recentCues.Any(cue =>
                cue.Actions?.Any(action => !string.IsNullOrWhiteSpace(action)) == true);
            if (hasActionSummaries)
            {
                builder.AppendLine("以下“动画脚本动作”只用于理解当前教程画面里演示了什么。"
                    + "它们是预先编排的演示，不代表客人自己的操作记录，也不代表系统支持玩家拖动或点击组件。"
                    + "回答时请顺着客人问题的正常意图解释规则或画面，不要主动补充“这不是玩家真实操作”之类的话；"
                    + "只有客人明确把动画动作当成自己的操作或当前游戏状态时，才简短说明这是教程演示。");
            }

            builder.AppendLine();
            builder.AppendLine("最近观看内容（按时间顺序，最后一条是客人提问时的当前 cue）：");

            foreach (var cue in recentCues)
            {
                AppendRecentCue(builder, cue);
            }

            _logger.LogInformation(
                "[Chat] context injected: cues={Count}, current={CueId}",
                recentCues.Count,
                context.CueId ?? recentCues.LastOrDefault()?.Id ?? "");
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendRecentCue(StringBuilder builder, RecentCueContext cue)
    {
        var indexText = cue.Index?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var path = CleanPath(cue.GroupPath);

        var header = new StringBuilder();
        header.Append("[cue ").Append(indexText).Append(']');
        if (cue.IsCurrent) header.Append(" 当前 cue");
        if (path.Count > 0)
            header.Append(cue.IsCurrent ? "：" : " ").Append(string.Join(" > ", path));
        builder.AppendLine(header.ToString());

        if (!string.IsNullOrWhiteSpace(cue.Text))
            builder.AppendLine($"口播：{Truncate(cue.Text.Trim(), 300)}");

        var refs = cue.Refs?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToList();
        if (refs?.Count > 0)
            builder.AppendLine($"引用：{string.Join("、", refs)}");

        var actions = cue.Actions?
            .Where(action => !string.IsNullOrWhiteSpace(action))
            .Select(action => action.Trim())
            .Take(8)
            .ToList();
        if (actions?.Count > 0)
        {
            builder.AppendLine("动画脚本动作（演示，非玩家手动操作）：");
            foreach (var action in actions)
                builder.AppendLine($"- {action}");
        }

        builder.AppendLine();
    }

    private static bool HasRecentCueContent(RecentCueContext cue)
    {
        return !string.IsNullOrWhiteSpace(cue.Id)
            || cue.Index.HasValue
            || !string.IsNullOrWhiteSpace(cue.Text)
            || cue.GroupPath?.Any(part => !string.IsNullOrWhiteSpace(part)) == true
            || cue.Refs?.Any(item => !string.IsNullOrWhiteSpace(item)) == true
            || cue.Actions?.Any(action => !string.IsNullOrWhiteSpace(action)) == true
            || cue.Start.HasValue
            || cue.Duration.HasValue;
    }

    private static List<string> CleanPath(List<string>? path)
    {
        return path?
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.Trim())
            .ToList() ?? new List<string>();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (maxLength <= 0 || value.Length <= maxLength) return value;
        return value[..maxLength] + "…";
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
        // 这些检索接口只作为程序内部能力被 ExecutePlan 使用，不暴露给 LLM。
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

    private async Task<string> ExecuteToolAsync(
        ToolCall toolCall,
        string gameId,
        string question,
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
                return Finish("{\"error\":\"arguments 不是合法 JSON，请严格按函数 schema 输出 {\\\"plan\\\":{\\\"queries\\\":[{\\\"relation\\\":\\\"...\\\",\\\"entity\\\":\\\"...\\\"}]}} 形状的工具参数。\"}");
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
                            JsonSerializer.Serialize(results, ToolResultOptions), gameId));
                    }

                case "get_concept":
                    {
                        var conceptId = args.GetProperty("concept_id").GetString() ?? string.Empty;
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = _rulesService.GetConceptsWithExpansion(gameId, conceptId);
                        return Finish(result.Matched.Count > 0
                            ? _rulesService.AnnotateReferences(
                                JsonSerializer.Serialize(result, ToolResultOptions), gameId)
                            : $"{{\"error\": \"Concept '{conceptId}' not found\"}}");
                    }

                case "execute_plan":
                    {
                        if (!args.TryGetProperty("plan", out var plan))
                            return Finish("{\"error\":\"plan is required，arguments 顶层必须有 plan 键。\"}");
                        var planResult = await _rulesService.ExecutePlanAsync(
                            gameId, plan, question, cancellationToken);
                        return Finish(_rulesService.AnnotateReferences(
                            JsonSerializer.Serialize(planResult, ToolResultOptions), gameId));
                    }

                case "get_game_flow":
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var concepts = _rulesService.GetConcepts(gameId, "game");
                        return Finish(concepts.Count > 0
                            ? _rulesService.AnnotateReferences(
                                JsonSerializer.Serialize(concepts, ToolResultOptions), gameId)
                            : $"{{\"error\": \"Game flow concept 'game' not found for '{gameId}'\"}}");
                    }

                case "get_action_conditions":
                    {
                        var actionId = args.GetProperty("action_id").GetString() ?? string.Empty;
                        cancellationToken.ThrowIfCancellationRequested();
                        var conditions = _rulesService.GetActionConditions(gameId, actionId);
                        return Finish(_rulesService.AnnotateReferences(
                            JsonSerializer.Serialize(conditions, ToolResultOptions), gameId));
                    }

                case "list_concept_ids":
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = _rulesService.ListAllConceptIds(gameId);
                        return Finish(_rulesService.AnnotateReferences(
                            JsonSerializer.Serialize(result, ToolResultOptions), gameId));
                    }

                default:
                    return Finish($"{{\"error\": \"Unknown tool '{toolCall.Function.Name}'\"}}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Finish($"{{\"error\": \"{ex.Message}\"}}");
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
