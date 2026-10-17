using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        => (await ProcessWithEvidenceAsync(gameId, history, context, cancellationToken)).Reply;

    public async Task<ChatProcessResult> ProcessWithEvidenceAsync(
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

        // 一轮 Chat 可能执行多个 execute_plan / 注解；整轮绑定同一规则版本。
        using var rulesScope = _rulesService.BeginRequestScope(gameId);

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
        var evidence = new AnswerEvidenceCollector();
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
            var protocolCorrections = 0;
            var answerFocusInjected = false;
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

                // If LLM returned a final answer (no tool calls), enforce the candidate-confirmation
                // protocol once when an unresolved candidate is still the only route to a rule fact.
                if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
                {
                    var reply = assistantMessage.Content ?? string.Empty;
                    if (ShouldRequestCandidateConfirmation(
                            evidence, protocolCorrections, round, totalToolCallsExecuted))
                    {
                        protocolCorrections++;
                        _logger.LogWarning(
                            "[Chat] Game {GameId}, Round {Round} blocked premature final answer: unresolved candidate(s) still pending; issuing one targeted confirmation correction",
                            gameId, roundsExecuted);
                        if (!string.IsNullOrWhiteSpace(reply))
                            messages.Add(new ChatMessage { Role = "assistant", Content = reply });
                        messages.Add(new ChatMessage
                        {
                            Role = "user",
                            Content = BuildCandidateConfirmationCorrection(evidence)
                        });
                        continue;
                    }

                    reply = await ApplyAnswerConstraintCorrectionAsync(
                        gameId, question, reply, messages, effectiveToken);
                    sw.Stop();
                    var builtEvidence = evidence.Build();
                    LogEvidenceSummary(gameId, builtEvidence, roundsExecuted, totalToolCallsExecuted, protocolCorrections, sw.Elapsed.TotalMilliseconds);
                    _logger.LogInformation("[Chat] Game {GameId}, Round {Round} final reply ({Elapsed:F0}ms, {Tier}): {Reply}",
                        gameId, roundsExecuted, sw.Elapsed.TotalMilliseconds, evidence.GetTier(), reply);
                    return new ChatProcessResult(reply, builtEvidence);
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

                    var toolStopwatch = Stopwatch.StartNew();
                    var result = await ExecuteToolAsync(toolCall, gameId, question, effectiveToken);
                    toolStopwatch.Stop();
                    totalToolCallsExecuted++;
                    effectiveToken.ThrowIfCancellationRequested();

                    _logger.LogInformation(
                        "[Chat] tool timing: game={GameId}, tool={ToolName}, elapsedMs={Elapsed:F0}",
                        gameId, toolCall.Function.Name, toolStopwatch.Elapsed.TotalMilliseconds);
                    _logger.LogInformation("[Chat] Game {GameId}, Tool {ToolName} result:\n{Result}",
                        gameId, toolCall.Function.Name, FormatForLog(result));
                    if (toolCall.Function.Name == "execute_plan")
                    {
                        evidence.AddPlanResult(result);
                        LogPlanProvenance(gameId, result);
                    }

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

                if (!answerFocusInjected && evidence.Queries.Count > 0)
                {
                    messages.Add(new ChatMessage
                    {
                        Role = "system",
                        Content = BuildAnswerFocusReminder(question)
                    });
                    answerFocusInjected = true;
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

            var finalReply = await RequestFinalAnswerAsync(
                gameId, question, messages, evidence, stopReason, roundsExecuted,
                totalToolCallsExecuted, protocolCorrections, sw, effectiveToken);
            return new ChatProcessResult(finalReply, evidence.Build());
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
            return new ChatProcessResult(BuildIncompleteReply("request_timeout", evidence), evidence.Build());
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
        string question,
        List<ChatMessage> messages,
        AnswerEvidenceCollector evidence,
        string stopReason,
        int roundsExecuted,
        int totalToolCallsExecuted,
        int protocolCorrections,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "请基于以上工具查询结果直接给出最终回答，不要再调用工具。"
                + "先直接回答客人原问题；客人已经说明的数量、对象和当前操作要原样承接，不要重复追问，也不要展开未被问到的其他人数配置、另一种获得方式或后续规则。"
                + "客人给出的组件数量不是玩家人数证据，不要反推或断言这是几人局。"
                + "如果可用事实不足以完整回答，请明确说明未能完成查询，并请客人缩小问题范围或换一种问法；不要编造规则事实。"
                + "\n客人原问题：" + question
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

            finalReply = await ApplyAnswerConstraintCorrectionAsync(
                gameId, question, finalReply, messages, cancellationToken);
            sw.Stop();
            var builtEvidence = evidence.Build();
            LogEvidenceSummary(
                gameId, builtEvidence, roundsExecuted, totalToolCallsExecuted,
                protocolCorrections, sw.Elapsed.TotalMilliseconds);
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

    private async Task<string> ApplyAnswerConstraintCorrectionAsync(
        string gameId,
        string question,
        string reply,
        List<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reply) || !HasNumericComponentQuantity(question))
            return reply;

        var inferredPlayerCount = AssertsPlayerCount(reply);
        var omittedComponentQuantity = !RepeatsComponentQuantity(question, reply);
        if (!inferredPlayerCount && !omittedComponentQuantity)
            return reply;

        _logger.LogWarning(
            "[Chat] Game {GameId}, answer constraint correction: inferredPlayerCount={Inferred}, omittedComponentQuantity={Omitted}; question={Question}, reply={Reply}",
            gameId, inferredPlayerCount, omittedComponentQuantity, question, reply);
        messages.Add(new ChatMessage { Role = "assistant", Content = reply });
        messages.Add(new ChatMessage
        {
            Role = "system",
            Content = "回答约束纠偏：客人给出的是当前操作中的组件数量，不是玩家人数。"
                + "第一句直接承接客人给出的组件数量，再说明规则；不要只回答人数公式。"
                + "不要说这说明是几人局，也不要用人数公式把回答转成人数判断。"
                + "请用已确认工具事实直接回答原问题。\n"
                + "客人原问题：" + question
        });

        try
        {
            var correctedResponse = await _llmService.ChatWithMessagesAsync(messages, null, cancellationToken);
            var corrected = correctedResponse.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(corrected))
                return corrected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Chat] Game {GameId}, answer constraint correction failed; keeping original reply", gameId);
        }

        return reply;
    }

    private static bool HasNumericComponentQuantity(string question) =>
        !string.IsNullOrWhiteSpace(question)
        && Regex.IsMatch(question, @"(?:[0-9]+|[零一二两三四五六七八九十百]+)\s*(?:块|张|枚|颗|片|个)\s*(?!人|玩|位|名)");

    private static bool AssertsPlayerCount(string reply) =>
        !string.IsNullOrWhiteSpace(reply)
        && Regex.IsMatch(reply, @"(?:[0-9]+|[零一二两三四五六七八九十百]+)\s*(?:个|位|名)?\s*人");

    private static bool RepeatsComponentQuantity(string question, string reply)
    {
        var match = Regex.Match(
            question,
            @"([0-9]+|[零一二两三四五六七八九十百]+)\s*(?:块|张|枚|颗|片|个)\s*(?!人|玩|位|名)");
        if (!match.Success) return true;
        var token = match.Groups[1].Value;
        var variants = token switch
        {
            "1" => new[] { "1", "一" },
            "2" => new[] { "2", "二", "两" },
            "3" => new[] { "3", "三" },
            "4" => new[] { "4", "四" },
            "5" => new[] { "5", "五" },
            "6" => new[] { "6", "六" },
            "7" => new[] { "7", "七" },
            "8" => new[] { "8", "八" },
            "9" => new[] { "9", "九" },
            "10" => new[] { "10", "十" },
            "一" => new[] { "一", "1" },
            "二" => new[] { "二", "两", "2" },
            "两" => new[] { "两", "二", "2" },
            "三" => new[] { "三", "3" },
            "四" => new[] { "四", "4" },
            "五" => new[] { "五", "5" },
            "六" => new[] { "六", "6" },
            "七" => new[] { "七", "7" },
            "八" => new[] { "八", "8" },
            "九" => new[] { "九", "9" },
            "十" => new[] { "十", "10" },
            _ => new[] { token }
        };
        return variants.Any(variant => reply.Contains(variant, StringComparison.Ordinal));
    }

    private static string BuildAnswerFocusReminder(string question) =>
        "回答聚焦（这是表达约束，不是新的规则事实）：\n"
        + "客人原问题：" + question + "\n"
        + "请先直接回答这个问题本身；如果问题里已经给出数量或对象，直接承接，不要反问客人已经说明的信息。"
        + "客人给出的组件数量不是玩家人数证据，不要反推或断言这是几人局。"
        + "只回答当前问题，只使用已确认工具事实；不展开未被问到的其他人数配置、另一种获得方式、扩展规则或后续计分；无关查询结果不要写进回答。";

    private static string BuildIncompleteReply(string stopReason, AnswerEvidenceCollector evidence)
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

    private bool ShouldRequestCandidateConfirmation(
        AnswerEvidenceCollector evidence,
        int protocolCorrections,
        int currentRound,
        int totalToolCallsExecuted)
    {
        if (protocolCorrections >= 1) return false;
        if (currentRound + 1 >= _maxToolRounds) return false;
        if (totalToolCallsExecuted >= _maxTotalToolCalls) return false;

        var built = evidence.Build();
        return built.Queries.Any(query =>
            query.Status == "unresolved"
            && query.ResolvedByQueryIndex == null
            && query.Candidates.Count > 0);
    }

    private static string BuildCandidateConfirmationCorrection(AnswerEvidenceCollector evidence)
    {
        var pending = evidence.Build().Queries
            .Where(query => query.Status == "unresolved"
                && query.ResolvedByQueryIndex == null
                && query.Candidates.Count > 0)
            .ToList();
        var lines = new List<string>
        {
            "先不要直接回答。上一轮返回的是尚未确认的候选，候选描述只能用于选择目标，不能当作正式规则事实。",
            "如果这些候选确实属于客人当前问题，请调用 execute_plan，保持原 relation 和 intent，把 entity 改成最相关候选的准确 id 或准确中文名，用本轮返回的正式事实再回答。",
            "如果某个 pending query 只是多余查询、不属于客人需要解决的问题，不必为了清空内部状态而查询或编造；但不要从未确认候选的描述里下结论。",
            "这是唯一一次协议纠偏。"
        };
        foreach (var query in pending)
        {
            var candidateText = string.Join("、", query.Candidates.Take(8)
                .Select(candidate => string.IsNullOrWhiteSpace(candidate.Name)
                    ? candidate.Id
                    : $"{candidate.Id}({candidate.Name})"));
            lines.Add($"未确认 query {query.QueryIndex}：relation={query.Relation}；entity={query.Entity}；intent={query.Intent}；候选={candidateText}");
        }
        return string.Join("\n", lines);
    }

    private void LogPlanProvenance(string gameId, string toolResult)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!TryGetProperty(root, "Results", out var results) || results.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in results.EnumerateArray())
            {
                var relation = GetString(item, "Relation");
                var entity = GetString(item, "Entity");
                var intent = GetString(item, "Intent");
                var status = GetString(item, "Status");
                var source = GetString(item, "Source");
                var queryIndex = TryGetProperty(item, "QueryIndex", out var qi) && qi.ValueKind == JsonValueKind.Number
                    ? qi.GetInt32()
                    : 0;
                var retrieval = ReadStringArray(item, "RetrievalQueries");
                var matched = ReadConceptIds(item, "Matched");
                var candidates = ReadCandidateRanking(item, "Candidates");
                _logger.LogInformation(
                    "[Chat] plan provenance: game={GameId}, query={QueryIndex}, relation={Relation}, entity={Entity}, intent={Intent}, status={Status}, source={Source}, retrieval=[{Retrieval}], matched=[{Matched}], candidates=[{Candidates}]",
                    gameId, queryIndex, relation, entity, intent, status, source,
                    string.Join(" | ", retrieval),
                    string.Join(", ", matched),
                    string.Join(" | ", candidates));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Chat] could not parse plan provenance for logging");
        }
    }

    private void LogEvidenceSummary(
        string gameId,
        AnswerEvidence evidence,
        int roundsExecuted,
        int totalToolCalls,
        int protocolCorrections,
        double elapsedMs)
    {
        foreach (var query in evidence.Queries)
        {
            _logger.LogInformation(
                "[Chat] evidence query: game={GameId}, query={QueryIndex}, relation={Relation}, entity={Entity}, intent={Intent}, status={Status}, source={Source}, hasData={HasData}, resolvedBy={ResolvedBy}, confirmedCandidate={ConfirmedCandidate}, confirmedByRelation={ConfirmedRelation}",
                gameId, query.QueryIndex, query.Relation, query.Entity, query.Intent, query.Status,
                query.Source, query.HasData, query.ResolvedByQueryIndex, query.ConfirmedCandidateId,
                query.ConfirmedByRelation);
        }
        _logger.LogInformation(
            "[Chat] evidence summary: game={GameId}, tier={Tier}, complete={Complete}, queryCount={QueryCount}, pending={Pending}, rounds={Rounds}, toolCalls={ToolCalls}, protocolCorrections={Corrections}, elapsedMs={Elapsed:F0}",
            gameId, evidence.Tier, evidence.IsComplete, evidence.QueryCount, evidence.PendingCount,
            roundsExecuted, totalToolCalls, protocolCorrections, elapsedMs);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value)) return true;
            var camel = char.ToLowerInvariant(name[0]) + name[1..];
            if (element.TryGetProperty(camel, out value)) return true;
        }
        value = default;
        return false;
    }

    private static string GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static List<string> ReadStringArray(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var array) || array.ValueKind != JsonValueKind.Array)
            return new List<string>();
        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static List<string> ReadConceptIds(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var array) || array.ValueKind != JsonValueKind.Array)
            return new List<string>();
        var ids = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var id = GetString(item, "Id");
            if (string.IsNullOrWhiteSpace(id)) id = GetString(item, "id");
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        }
        return ids;
    }

    private static List<string> ReadCandidateRanking(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var array) || array.ValueKind != JsonValueKind.Array)
            return new List<string>();
        var ranking = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var id = GetString(item, "Id");
            if (string.IsNullOrWhiteSpace(id)) id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var score = TryGetProperty(item, "Score", out var scoreProp) && scoreProp.ValueKind == JsonValueKind.Number
                ? scoreProp.GetSingle().ToString("0.####", CultureInfo.InvariantCulture)
                : "?";
            var reason = GetString(item, "MatchReason");
            ranking.Add($"{id}:{score}:{reason}");
        }
        return ranking;
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

    /// <summary>
    /// 按 query 收集证据。一次 execute_plan 的所有 query 都会记录；最终 tier
    /// 保留历史状态；仅同版本、单一候选被后续精确同关系查询（identify → explain 也允许）确认时恢复。
    /// </summary>
    internal sealed class AnswerEvidenceCollector
    {
        public List<QueryEvidence> Queries { get; } = new();
        public string? RulesVersion { get; private set; }

        public bool SawData => Queries.Any(q => q.HasData);
        public bool SawCandidates => Queries.Any(q => q.Candidates.Count > 0);

        public void AddPlanResult(string toolResult)
        {
            try
            {
                using var doc = JsonDocument.Parse(toolResult);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                string? planVersion = null;
                if (root.TryGetProperty("RulesVersion", out var versionProp)
                    && versionProp.ValueKind == JsonValueKind.String)
                {
                    planVersion = versionProp.GetString();
                    RulesVersion ??= planVersion;
                }

                if (!root.TryGetProperty("Results", out var results) || results.ValueKind != JsonValueKind.Array)
                    return;

                foreach (var item in results.EnumerateArray())
                    Queries.Add(ParseQueryEvidence(item, planVersion));
            }
            catch
            {
                // 非 plan 结果或解析失败——不参与 tier 判定
            }
        }

        public AnswerEvidence Build()
        {
            ResolveCandidateQueries();
            var evidence = new AnswerEvidence
            {
                RulesVersion = RulesVersion,
                QueryCount = Queries.Count,
                HasData = SawData,
                HasCandidates = SawCandidates,
                OkCount = Queries.Count(q => q.Status == "ok"),
                UnresolvedCount = Queries.Count(q => q.Status == "unresolved"),
                NoMatchCount = Queries.Count(q => q.Status == "no_match"),
                UnsupportedCount = Queries.Count(q => q.Status == "unsupported"),
                PendingCount = Queries.Count(q => !q.HasData && q.ResolvedByQueryIndex == null),
                Queries = Queries.ToList()
            };

            evidence.IsComplete = evidence.QueryCount > 0
                && evidence.PendingCount == 0
                && evidence.HasData;

            evidence.Tier = evidence.IsComplete
                ? "tier1"
                : evidence.HasData
                    ? "partial"
                    : evidence.HasCandidates
                        ? "tier2"
                        : "tier3";

            return evidence;
        }

        private void ResolveCandidateQueries()
        {
            foreach (var query in Queries)
            {
                query.ResolvedByQueryIndex = null;
                query.ConfirmedCandidateId = null;
                query.ConfirmedByRelation = null;
            }

            for (var i = 0; i < Queries.Count; i++)
            {
                var query = Queries[i];
                if (query.Status != "unresolved" || string.IsNullOrEmpty(query.RulesVersion)) continue;
                var candidateIds = query.Candidates
                    .Select(c => c.Id)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (candidateIds.Length == 0) continue;

                for (var j = i + 1; j < Queries.Count; j++)
                {
                    var later = Queries[j];
                    if (!CanConfirmRelation(query.Relation, later.Relation)
                        || later.RulesVersion != query.RulesVersion
                        || !later.HasData
                        || later.Status != "ok"
                        || later.Source is not ("exact_id" or "exact_name_zh"))
                        continue;

                    // 只接受唯一对象命中；名称命中多个对象或一次 ok 返回多个 matched 时不能替用户选。
                    if (later.Matched.Count != 1) continue;
                    var selectedId = later.Matched[0].Id;
                    if (string.IsNullOrEmpty(selectedId) || !candidateIds.Contains(selectedId, StringComparer.Ordinal))
                        continue;
                    // 同一候选出现在多个未解决 query 中时，单凭 id 无法证明这次 ok 对齐的是哪一问。
                    if (IsAmbiguousCandidateConfirmation(i, later, selectedId)) continue;

                    query.ResolvedByQueryIndex = j;
                    query.ConfirmedCandidateId = selectedId;
                    query.ConfirmedByRelation = later.Relation;
                    break;
                }
            }
        }

        private bool IsAmbiguousCandidateConfirmation(int priorIndex, QueryEvidence later, string candidateId)
        {
            var competing = new List<int>();
            for (var k = 0; k < Queries.Count; k++)
            {
                var other = Queries[k];
                if (other.Status != "unresolved"
                    || !string.Equals(other.RulesVersion, later.RulesVersion, StringComparison.Ordinal)
                    || !CanConfirmRelation(other.Relation, later.Relation))
                    continue;
                if (other.Candidates.Any(c => string.Equals(c.Id, candidateId, StringComparison.Ordinal)))
                    competing.Add(k);
            }

            if (competing.Count <= 1) return false;

            // 同一候选可能出现在多个宽泛 query 中。后续查询的 intent 与某一项逐字一致时，
            // 这是可审查的关联证据，只解决那一项；其余同候选 query 保持 pending。
            var laterIntent = later.Intent.Trim();
            if (!string.IsNullOrEmpty(laterIntent))
            {
                var sameIntent = competing
                    .Where(index => string.Equals(
                        Queries[index].Intent.Trim(), laterIntent, StringComparison.Ordinal))
                    .ToList();
                if (sameIntent.Count == 1)
                    return sameIntent[0] != priorIndex;
                if (sameIntent.Count > 1)
                    return true;

                // 复述 intent 常有同义词/错字（如“放几张”对“发几张”）。只有最高相似度
                // 明显领先第二项时才允许关联，避免两个相近子问题被一次 ok 一起清空。
                var ranked = competing
                    .Select(index => (Index: index, Score: IntentSimilarity(Queries[index].Intent, laterIntent)))
                    .OrderByDescending(item => item.Score)
                    .ThenBy(item => item.Index)
                    .ToList();
                var secondScore = ranked.Count > 1 ? ranked[1].Score : 0f;
                if (ranked[0].Score >= 0.70f && ranked[0].Score - secondScore >= 0.15f)
                    return ranked[0].Index != priorIndex;
            }

            // 没有足够区分证据时保持保守：不能因为 id 相同就把这次 ok 关联到任意一问。
            return true;
        }

        private static float IntentSimilarity(string left, string right)
        {
            var a = (left ?? "").Trim();
            var b = (right ?? "").Trim();
            if (a.Length == 0 || b.Length == 0) return 0f;
            if (string.Equals(a, b, StringComparison.Ordinal)) return 1f;
            if (a.Length < 2 || b.Length < 2) return 0f;

            static HashSet<string> Bigrams(string value) =>
                Enumerable.Range(0, value.Length - 1)
                    .Select(index => value.Substring(index, 2))
                    .ToHashSet(StringComparer.Ordinal);

            var leftBigrams = Bigrams(a);
            var rightBigrams = Bigrams(b);
            var intersection = leftBigrams.Count(rightBigrams.Contains);
            var union = leftBigrams.Count + rightBigrams.Count - intersection;
            return union == 0 ? 0f : (float)intersection / union;
        }

        private static bool CanConfirmRelation(string originalRelation, string laterRelation) =>
            string.Equals(originalRelation, laterRelation, StringComparison.Ordinal)
            || (originalRelation == "identify" && laterRelation == "explain");

        public string GetTier()
        {
            var tier = Build().Tier;
            return tier switch
            {
                "tier1" => "tier1-数据回答",
                "tier2" => "tier2-候选兜底",
                "partial" => "partial-部分有据",
                _ => "tier3-无数据"
            };
        }
    }

    private static QueryEvidence ParseQueryEvidence(JsonElement item, string? rulesVersion)
    {
        var status = item.TryGetProperty("Status", out var sp) ? sp.GetString() ?? "" : "";
        var query = new QueryEvidence
        {
            QueryIndex = item.TryGetProperty("QueryIndex", out var qi) && qi.ValueKind == JsonValueKind.Number
                ? qi.GetInt32()
                : 0,
            Relation = item.TryGetProperty("Relation", out var rp) ? rp.GetString() ?? "" : "",
            Entity = item.TryGetProperty("Entity", out var ep) ? ep.GetString() ?? "" : "",
            Intent = item.TryGetProperty("Intent", out var ip) ? ip.GetString() ?? "" : "",
            Status = status,
            Source = item.TryGetProperty("Source", out var src) ? src.GetString() ?? "" : "",
            RulesVersion = rulesVersion,
            Message = item.TryGetProperty("Message", out var mp) ? mp.GetString() : null,
            Matched = ParseMatchedArray(item, "Matched"),
            Candidates = ParseCandidateArray(item, "Candidates"),
            References = ParseMatchedArray(item, "Related")
        };

        if (item.TryGetProperty("FlowContext", out var flow) && flow.ValueKind == JsonValueKind.Array)
        {
            query.FlowContext = flow.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? "")
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();
        }

        var hasCatalog = item.TryGetProperty("Catalog", out var catalog)
            && catalog.ValueKind == JsonValueKind.Object;
        query.HasData = status == "ok" && (query.Matched.Count > 0 || hasCatalog);

        return query;
    }

    private static List<EvidenceConcept> ParseMatchedArray(JsonElement item, string property)
    {
        var result = new List<EvidenceConcept>();
        if (!item.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray())
        {
            var id = element.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            result.Add(new EvidenceConcept
            {
                Id = id,
                Name = ExtractNameZh(element)
            });
        }

        return result;
    }

    private static List<EvidenceConcept> ParseCandidateArray(JsonElement item, string property)
    {
        var result = new List<EvidenceConcept>();
        if (!item.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var element in array.EnumerateArray())
        {
            var id = element.TryGetProperty("Id", out var idProp) ? idProp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id)) continue;
            var candidate = new EvidenceConcept
            {
                Id = id,
                Name = element.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? "" : "",
                Type = element.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() : null,
                Description = element.TryGetProperty("Description", out var descProp) ? descProp.GetString() : null,
                MatchReason = element.TryGetProperty("MatchReason", out var reasonProp) ? reasonProp.GetString() : null
            };
            if (element.TryGetProperty("Score", out var scoreProp) && scoreProp.ValueKind == JsonValueKind.Number)
                candidate.Score = scoreProp.GetSingle();
            result.Add(candidate);
        }

        return result;
    }

    private static string ExtractNameZh(JsonElement element)
    {
        if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object)
        {
            if (name.TryGetProperty("zh", out var zh) && zh.ValueKind == JsonValueKind.String)
                return zh.GetString() ?? "";
            if (name.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
                return en.GetString() ?? "";
        }

        return "";
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
                    Description = @"唯一工具：查询计划执行器。把客人的规则问题编译成结构化查询计划，程序会执行实体定位、完整意图检索与规则导航。relation 选择：
- explain：X 是什么 / 怎么结算 / 怎么做 / 有什么效果 / 有几个
- condition：能不能 X / X 有什么前提 / 什么限制
- ordering：X 之后是什么 / 先后顺序 / 什么时候结束 / 每轮怎么轮转
- boundary：X 不会发生什么 / 满了怎么办 / 上限多少
- flow：整体游戏流程（几个阶段、怎么进行、怎么结束）
- list：浏览全部概念目录（实体解析失败需要找概念时用）
- identify：客人用外观/位置描述某物但你不确定是哪个概念时，把描述原文作为 entity，程序返回候选
query 字段语义：
- entity：查谁/什么，填概念 id、准确中文名或对象描述；知道准确 id 时直接用 id，不要求凭空猜 id。
- intent：针对 entity 你要知道什么，保留用户问题里的动作、场景和疑点。必须来自用户问题及有效上下文，不能写成你已经猜出的答案，也不能包含尚未证实的规则。
- relation：需要解释、条件、顺序、边界等哪类规则信息。
单一疑点默认只发一个 query；只有确实需要不同事实时才拆多个 query，每个 query 写自己的 intent。不要把每个名词机械拆成 explain + condition。
实体解析分三层：精确命中直接返回规则事实；未精确命中时程序会结合完整 intent 返回 Status=unresolved + Candidates。候选描述只用于选择目标，不能当作已确认事实；请从候选中挑真正回答 intent 的概念，用准确 id 或准确中文名、保持原 relation 和 intent 重新发起计划。没有合适的候选再用 identify 或 list。若返回 Status=no_match，说明规则库未确认到该事实——请向客人说明未查到或请其换个问法，不要基于记忆补写规则。若结果被标记为 unresolved，不要直接下结论；确认后的正式事实才可用于回答。客人已经说明的数量、对象和当前操作要原样承接，不得在回答里重新追问已说明的信息，也不得用数量公式反问玩家人数。客人给出的组件数量不是玩家人数证据，不要反推或断言这是几人局；只回答当前问题，不自动补充下一阶段或另一种获得方式。",

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
                                    "description": "概念 id、准确中文名或对象描述（flow/list 可省略）"
                                  },
                                  "intent": {
                                    "type": "string",
                                    "description": "针对 entity 要知道的动作、场景或疑点，来自用户问题及有效上下文；不要写猜测的答案或未证实的规则。单 query 缺省时程序可用原问句回填，多 query 时请逐项填写。"
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
