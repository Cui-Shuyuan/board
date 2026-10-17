using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
    private readonly string _legacySystemPromptTemplate;
    private readonly string _programSystemPromptTemplate;
    private readonly ILogger<ChatOrchestratorService> _logger;
    private readonly LLMOptions _llmOptions;
    private readonly string _orchestrationMode;
    private readonly string _configFingerprint;
    private readonly string _toolContractFingerprint;
    private readonly RuleToolResultProjector _toolResultProjector;
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
        _llmOptions = llmOptions;
        _legacySystemPromptTemplate = llmOptions.LegacySystemPrompt;
        _programSystemPromptTemplate = llmOptions.ProgramSystemPrompt;
        _orchestrationMode = (llmOptions.OrchestrationMode ?? "").Trim().ToLowerInvariant();
        if (_orchestrationMode is not ("legacy" or "program"))
            throw new InvalidOperationException(
                $"LLM:OrchestrationMode must be 'legacy' or 'program', got '{llmOptions.OrchestrationMode}'.");
        _configFingerprint = HashFingerprint(
            $"{llmOptions.Provider}|{llmOptions.BaseUrl}|{llmOptions.Model}|{llmOptions.Thinking}|"
            + $"{_orchestrationMode}|{llmOptions.MaxToolRounds}|{llmOptions.MaxToolCallsPerRound}|"
            + $"{llmOptions.MaxTotalToolCalls}|{llmOptions.MaxCandidatesPerQuestion}|"
            + $"{llmOptions.MaxEvidencePerQuestion}|{llmOptions.MaxEvidenceTotal}|"
            + $"{llmOptions.MaxEvidenceJsonChars}|{llmOptions.MaxEvidencePackChars}|"
            + $"{llmOptions.MaxSupplementQueries}|legacy:{_legacySystemPromptTemplate}|program:{_programSystemPromptTemplate}");
        _maxToolRounds = Math.Max(1, llmOptions.MaxToolRounds);
        _maxToolCallsPerRound = Math.Max(1, llmOptions.MaxToolCallsPerRound);
        _maxTotalToolCalls = Math.Max(1, llmOptions.MaxTotalToolCalls);
        _maxRequestTimeout = TimeSpan.FromSeconds(Math.Max(1, llmOptions.MaxRequestSeconds));
        _maxRepeatedFailures = Math.Max(1, llmOptions.MaxRepeatedFailures);
        _maxConversationMessages = Math.Max(2, llmOptions.MaxConversationMessages);
        _toolContractFingerprint = HashFingerprint(
            JsonSerializer.Serialize(BuildTools("template"), ToolResultOptions));
        _toolResultProjector = new RuleToolResultProjector(llmOptions.MaxEvidencePackChars);
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
        if (string.Equals(_orchestrationMode, "legacy", StringComparison.OrdinalIgnoreCase))
            return await ProcessLegacyWithEvidenceAsync(gameId, history, context, cancellationToken);
        return await ProcessProgramControlledAsync(gameId, history, context, cancellationToken);
    }

    private async Task<ChatProcessResult> ProcessProgramControlledAsync(
        string gameId,
        List<ChatMessage> history,
        ChatContext? context,
        CancellationToken cancellationToken)
    {
        using var rulesScope = _rulesService.BeginRequestScope(gameId);

        var systemPrompt = _programSystemPromptTemplate.Replace("{game_name}", gameId, StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation(
            "[Chat] program mode={Mode}, promptFingerprint={PromptFingerprint}, configFingerprint={ConfigFingerprint}, model={Model}, thinking={Thinking}",
            _orchestrationMode, PromptFingerprint(_programSystemPromptTemplate), _configFingerprint,
            _llmOptions.Model, _llmOptions.Thinking);
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new ChatMessage { Role = "system", Content = systemPrompt });
        var contextMessage = BuildContextMessage(context);
        if (!string.IsNullOrWhiteSpace(contextMessage))
            messages.Add(new ChatMessage { Role = "system", Content = contextMessage });
        messages.AddRange(TrimHistory(history, _maxConversationMessages));

        var latestUser = history.LastOrDefault(m => m.Role == "user");
        var question = latestUser?.Content ?? "";
        _logger.LogInformation("[Chat] program path: game={GameId}, question={Question}, count={Count}",
            gameId, question == "" ? "(empty)" : question, history.Count);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(_maxRequestTimeout);
        try
        {
            var orchestrator = new ProgramControlledChatOrchestrator(
                _llmService, _rulesService, _llmOptions, _logger);
            var result = await orchestrator.ProcessAsync(gameId, question, messages, linkedCts.Token);
            _logger.LogInformation(
                "[Chat] program final reply: game={GameId}, outcome={Outcome}, modelCalls={ModelCalls}, evidence={Evidence}, reply={Reply}",
                gameId, result.Trace.Outcome, result.Trace.ModelCallCount, result.Evidence.EvidenceReadCount, result.Reply);
            return new ChatProcessResult(result.Reply, result.Evidence, result.Trace);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("[Chat] program path canceled by client: game={GameId}", gameId);
            throw;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            _logger.LogInformation("[Chat] program path request timeout: game={GameId}", gameId);
            var trace = new ChatOrchestrationTrace
            {
                RequestId = "chat_" + Guid.NewGuid().ToString("N")[..12],
                Mode = "program",
                Outcome = "request_timeout"
            };
            var evidence = new AnswerEvidence
            {
                Tier = "tier3",
                OrchestrationOutcome = "request_timeout",
                IsComplete = false,
                PendingCount = 0
            };
            return new ChatProcessResult(
                "抱歉，本次规则查询已达到时间上限，请缩小问题范围或换一种更具体的问法。",
                evidence, trace);
        }
    }

    private async Task<ChatProcessResult> ProcessLegacyWithEvidenceAsync(
        string gameId,
        List<ChatMessage> history,
        ChatContext? context,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var requestTrace = new ChatOrchestrationTrace
        {
            RequestId = "chat_" + Guid.NewGuid().ToString("N")[..12],
            Mode = "legacy"
        };

        if (string.IsNullOrWhiteSpace(gameId))
        {
            throw new ArgumentException("game_id is required", nameof(gameId));
        }

        // 一轮 Chat 可能执行多个 execute_plan / 注解；整轮绑定同一规则版本。
        using var rulesScope = _rulesService.BeginRequestScope(gameId);

        var systemPrompt = _legacySystemPromptTemplate.Replace("{game_name}", gameId, StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation(
            "[Chat] legacy mode={Mode}, promptFingerprint={PromptFingerprint}, toolContractFingerprint={ToolFingerprint}, configFingerprint={ConfigFingerprint}, model={Model}, thinking={Thinking}",
            _orchestrationMode, PromptFingerprint(_legacySystemPromptTemplate), _toolContractFingerprint,
            _configFingerprint, _llmOptions.Model, _llmOptions.Thinking);

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

                var modelCallStartMs = sw.Elapsed.TotalMilliseconds;
                var modelCallStopwatch = Stopwatch.StartNew();
                var response = await _llmService.ChatWithMessagesAsync(messages, tools, effectiveToken);
                modelCallStopwatch.Stop();
                AddModelTimelineSpan(
                    requestTrace, modelCallStartMs, modelCallStopwatch.Elapsed.TotalMilliseconds,
                    "model_planning", "tool_loop", response.Usage, roundsExecuted);
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

                    sw.Stop();
                    var builtEvidence = evidence.Build();
                    LogEvidenceSummary(gameId, builtEvidence, roundsExecuted, totalToolCallsExecuted, protocolCorrections, sw.Elapsed.TotalMilliseconds);
                    _logger.LogInformation("[Chat] Game {GameId}, Round {Round} final reply ({Elapsed:F0}ms, {Tier}): {Reply}",
                        gameId, roundsExecuted, sw.Elapsed.TotalMilliseconds, evidence.GetTier(), reply);
                    return new ChatProcessResult(reply, builtEvidence, FinalizeLegacyTrace(requestTrace, sw));
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

                    var toolStartMs = sw.Elapsed.TotalMilliseconds;
                    var toolStopwatch = Stopwatch.StartNew();
                    var result = await ExecuteToolAsync(toolCall, gameId, question, effectiveToken);
                    toolStopwatch.Stop();
                    AddTimelineSpan(requestTrace, new ChatTimelineSpan
                    {
                        Name = toolCall.Function.Name,
                        Category = "tool",
                        StartMs = toolStartMs,
                        DurationMs = toolStopwatch.Elapsed.TotalMilliseconds,
                        ResultChars = result.Length
                    });
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
                totalToolCallsExecuted, protocolCorrections, sw, requestTrace, effectiveToken);
            return new ChatProcessResult(finalReply, evidence.Build(), FinalizeLegacyTrace(requestTrace, sw));
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
            return new ChatProcessResult(
                BuildIncompleteReply("request_timeout", evidence),
                evidence.Build(),
                FinalizeLegacyTrace(requestTrace, sw));
        }
        finally
        {
            sw.Stop();
        }
    }

    private static void AddModelTimelineSpan(
        ChatOrchestrationTrace trace,
        double startMs,
        double durationMs,
        string name,
        string stage,
        LLMUsage? usage,
        int sequence)
    {
        var span = new ChatTimelineSpan
        {
            Name = name,
            Category = "model",
            StartMs = startMs,
            DurationMs = durationMs,
            PromptTokens = usage?.PromptTokens,
            CompletionTokens = usage?.CompletionTokens,
            ReasoningTokens = usage?.CompletionTokenDetails?.ReasoningTokens
        };
        AddTimelineSpan(trace, span);
        trace.ModelCallCount++;
        trace.ModelCalls.Add(new ChatModelCallTrace
        {
            Sequence = trace.ModelCalls.Count + 1,
            Stage = stage,
            Reason = name,
            Succeeded = true,
            ElapsedMs = Math.Round(durationMs, 2),
            PromptTokens = span.PromptTokens,
            CompletionTokens = span.CompletionTokens,
            ReasoningTokens = span.ReasoningTokens
        });
    }

    private static void AddTimelineSpan(ChatOrchestrationTrace trace, ChatTimelineSpan span)
    {
        trace.Timeline.Add(span);
    }

    private static ChatOrchestrationTrace FinalizeLegacyTrace(
        ChatOrchestrationTrace trace, Stopwatch requestStopwatch)
    {
        var elapsed = requestStopwatch.Elapsed.TotalMilliseconds;
        trace.TimelineAccounting = ChatTimelineAccountingCalculator.Compute(trace.Timeline, elapsed);
        return trace;
    }

    private static string PromptFingerprint(string prompt) => HashFingerprint(prompt);

    private static string HashFingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
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
        ChatOrchestrationTrace requestTrace,
        CancellationToken cancellationToken)
    {
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "请基于以上工具查询结果直接给出最终回答，不要再调用工具。"
                + "直接回答客人所问，不重复询问已知信息，不添加没有依据的现场状态；必要的规则前提可以简短说明。"
                + "如果可用事实不足以完整回答，请明确说明未能完成查询，并请客人缩小问题范围或换一种问法；不要编造规则事实。"
                + "\n客人原问题：" + question
        });

        try
        {
            var summaryStartMs = sw.Elapsed.TotalMilliseconds;
            var summaryStopwatch = Stopwatch.StartNew();
            var finalResponse = await _llmService.ChatWithMessagesAsync(messages, null, cancellationToken);
            summaryStopwatch.Stop();
            AddModelTimelineSpan(
                requestTrace, summaryStartMs, summaryStopwatch.Elapsed.TotalMilliseconds,
                "model_final_answer", "summary", finalResponse.Usage, 1);
            var finalMessage = finalResponse.Choices?.FirstOrDefault()?.Message;
            var finalReply = finalMessage?.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(finalReply))
            {
                _logger.LogWarning("[Chat] Game {GameId}, final summary returned empty content", gameId);
                finalReply = BuildIncompleteReply(stopReason, evidence);
            }

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

    private static string BuildAnswerFocusReminder(string question) =>
        "回答聚焦（这是表达约束，不是新的规则事实）：\n"
        + "客人原问题：" + question + "\n"
        + "直接回答所问，只使用已确认的规则事实，不添加没有依据的现场状态，不重复询问已知信息。"
        + "用户提供的状态不等于已经通过规则校验；如与规则冲突应说明，必要前提可简短交代，无关查询结果不要写进回答。";

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
            {
                if (root.TryGetProperty("queries", out var projectedQueries)
                    && projectedQueries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in projectedQueries.EnumerateArray())
                    {
                        var queryIndex = item.TryGetProperty("query_index", out var qi) && qi.ValueKind == JsonValueKind.Number
                            ? qi.GetInt32()
                            : 0;
                        var relation = ReadFlexibleString(item, "relation", "Relation");
                        var entity = ReadFlexibleString(item, "entity", "Entity");
                        var intent = ReadFlexibleString(item, "intent", "Intent");
                        var status = ReadFlexibleString(item, "status", "Status");
                        var resolution = ReadFlexibleString(item, "resolution", "Resolution");
                        var coverage = ReadFlexibleString(item, "coverage", "Coverage");
                        var targets = ReadFlexibleStringArray(item, "resolved_targets", "ResolvedTargets");
                        var evidenceIds = ReadFlexibleStringArray(item, "evidence_ids", "EvidenceIds");
                        _logger.LogInformation(
                            "[Chat] projected plan provenance: game={GameId}, query={QueryIndex}, relation={Relation}, entity={Entity}, intent={Intent}, status={Status}, resolution={Resolution}, coverage={Coverage}, targets=[{Targets}], evidenceIds=[{EvidenceIds}]",
                            gameId, queryIndex, relation, entity, intent, status, resolution, coverage,
                            string.Join(", ", targets), string.Join(", ", evidenceIds));
                    }
                }
                return;
            }

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
                "[Chat] evidence query: game={GameId}, query={QueryIndex}, relation={Relation}, entity={Entity}, intent={Intent}, status={Status}, resolution={Resolution}, coverage={Coverage}, source={Source}, hasData={HasData}, payloadTypes=[{PayloadTypes}], omissions=[{Omissions}], resolvedBy={ResolvedBy}, confirmedCandidate={ConfirmedCandidate}, confirmedByRelation={ConfirmedRelation}",
                gameId, query.QueryIndex, query.Relation, query.Entity, query.Intent, query.Status,
                query.Resolution, query.Coverage, query.Source, query.HasData,
                string.Join(",", query.PayloadTypes), string.Join(",", query.OmissionReasons),
                query.ResolvedByQueryIndex, query.ConfirmedCandidateId,
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

                if (TryAddProjectedPlanResult(root))
                    return;

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

        private bool TryAddProjectedPlanResult(JsonElement root)
        {
            if (!root.TryGetProperty("queries", out var queries)
                || queries.ValueKind != JsonValueKind.Array)
                return false;

            string? planVersion = null;
            if (root.TryGetProperty("rules_version", out var versionProp)
                && versionProp.ValueKind == JsonValueKind.String)
            {
                planVersion = versionProp.GetString();
                RulesVersion ??= planVersion;
            }

            var evidenceById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (root.TryGetProperty("evidence_catalog", out var catalog)
                && catalog.ValueKind == JsonValueKind.Array)
            {
                foreach (var evidence in catalog.EnumerateArray())
                {
                    var evidenceId = ReadFlexibleString(evidence, "evidence_id", "EvidenceId");
                    if (!string.IsNullOrWhiteSpace(evidenceId))
                        evidenceById[evidenceId] = evidence;
                }
            }

            foreach (var item in queries.EnumerateArray())
            {
                var evidenceIds = ReadFlexibleStringArray(item, "evidence_ids", "EvidenceIds");
                var query = new QueryEvidence
                {
                    QueryIndex = item.TryGetProperty("query_index", out var qi) && qi.ValueKind == JsonValueKind.Number
                        ? qi.GetInt32()
                        : 0,
                    Relation = ReadFlexibleString(item, "relation", "Relation"),
                    Entity = ReadFlexibleString(item, "entity", "Entity"),
                    Intent = ReadFlexibleString(item, "intent", "Intent"),
                    Status = ReadFlexibleString(item, "status", "Status"),
                    Source = ReadFlexibleString(item, "source", "Source"),
                    Resolution = ReadFlexibleString(item, "resolution", "Resolution"),
                    Coverage = ReadFlexibleString(item, "coverage", "Coverage"),
                    RulesVersion = planVersion,
                    Message = ReadFlexibleNullableString(item, "message", "Message"),
                    ResolvedTargets = ReadFlexibleStringArray(item, "resolved_targets", "ResolvedTargets"),
                    EvidenceIds = evidenceIds
                };

                foreach (var evidenceId in evidenceIds)
                {
                    if (!evidenceById.TryGetValue(evidenceId, out var evidence)) continue;
                    var type = ReadFlexibleString(evidence, "type", "Type");
                    if (!string.IsNullOrWhiteSpace(type)) query.PayloadTypes.Add(type);
                    var canonical = ReadFlexibleString(evidence, "canonical_path", "CanonicalPath");
                    if (!string.IsNullOrWhiteSpace(canonical))
                        query.EvidenceCanonicalPaths.Add(canonical);
                    if (type == QueryPayloadTypes.Definition
                        && evidence.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.Object)
                    {
                        var concept = ParseEvidenceConcept(content);
                        if (concept != null && !query.Matched.Any(existing => existing.Id == concept.Id))
                            query.Matched.Add(concept);
                    }
                }

                query.Candidates = ParseProjectedCandidates(item);
                query.HasData = evidenceIds.Count > 0 || query.Matched.Count > 0;
                Queries.Add(query);
            }
            return true;
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

    internal static QueryEvidence ParseQueryEvidence(JsonElement item, string? rulesVersion)
    {
        var status = ReadFlexibleString(item, "Status", "status");
        var query = new QueryEvidence
        {
            QueryIndex = item.TryGetProperty("QueryIndex", out var qi) && qi.ValueKind == JsonValueKind.Number
                ? qi.GetInt32()
                : item.TryGetProperty("queryIndex", out var camelQi) && camelQi.ValueKind == JsonValueKind.Number
                    ? camelQi.GetInt32()
                    : 0,
            Relation = ReadFlexibleString(item, "Relation", "relation"),
            Entity = ReadFlexibleString(item, "Entity", "entity"),
            Intent = ReadFlexibleString(item, "Intent", "intent"),
            Status = status,
            Source = ReadFlexibleString(item, "Source", "source"),
            Resolution = ReadFlexibleString(item, "Resolution", "resolution"),
            Coverage = ReadFlexibleString(item, "Coverage", "coverage"),
            RulesVersion = rulesVersion,
            Message = ReadFlexibleNullableString(item, "Message", "message"),
            Matched = ParseMatchedArray(item, "Matched", "matched"),
            Candidates = ParseCandidateArray(item, "Candidates", "candidates"),
            References = ParseMatchedArray(item, "Related", "related")
        };

        var flow = ReadFlexibleArray(item, "FlowContext", "flowContext");
        if (flow.HasValue)
        {
            query.FlowContext = flow.Value.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? "")
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();
        }

        query.ResolvedTargets = ReadFlexibleStringArray(item, "ResolvedTargets", "resolvedTargets");
        var payloads = ReadFlexibleArray(item, "Payloads", "payloads");
        if (payloads.HasValue)
        {
            foreach (var payload in payloads.Value.EnumerateArray())
            {
                if (payload.ValueKind != JsonValueKind.Object) continue;
                var type = ReadFlexibleString(payload, "Type", "type");
                if (!string.IsNullOrWhiteSpace(type)) query.PayloadTypes.Add(type);
            }
        }
        var omissions = ReadFlexibleArray(item, "Omissions", "omissions");
        if (omissions.HasValue)
        {
            foreach (var omission in omissions.Value.EnumerateArray())
            {
                if (omission.ValueKind != JsonValueKind.Object) continue;
                var reason = ReadFlexibleString(omission, "Reason", "reason");
                if (!string.IsNullOrWhiteSpace(reason)) query.OmissionReasons.Add(reason);
            }
        }

        var knownPayloads = new HashSet<string>(StringComparer.Ordinal)
        {
            QueryPayloadTypes.Definition,
            QueryPayloadTypes.CatalogPage,
            QueryPayloadTypes.FlowSlice,
            QueryPayloadTypes.RelationFacts,
            QueryPayloadTypes.ComputedFact,
        QueryPayloadTypes.MemberSet
        };
        var unknownPayload = query.PayloadTypes.Any(type => !knownPayloads.Contains(type));
        var hasCatalogPayload = query.PayloadTypes.Contains(QueryPayloadTypes.CatalogPage);
        var hasCatalog = item.TryGetProperty("Catalog", out var catalog)
            && catalog.ValueKind == JsonValueKind.Object;
        var hasFormalPayload = query.PayloadTypes.Any(knownPayloads.Contains);
        var hasLegacyData = query.Matched.Count > 0 || hasCatalog;

        // 新契约存在时以 payload 为准；list 的 CatalogPage 是合法数据，不能判成空证据成功。
        query.HasData = status == "ok"
            && !unknownPayload
            && (payloads.HasValue ? hasFormalPayload : hasLegacyData)
            && (hasFormalPayload || hasLegacyData || !payloads.HasValue);
        if (unknownPayload)
        {
            query.Status = "unsupported";
            query.Resolution = QueryResolutionStatuses.Unsupported;
            query.Coverage = QueryCoverageStatuses.Unknown;
            query.HasData = false;
        }

        return query;
    }

    private static string ReadFlexibleString(JsonElement item, string pascal, string camel)
    {
        if (item.TryGetProperty(pascal, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        if (item.TryGetProperty(camel, out value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        return "";
    }

    private static string? ReadFlexibleNullableString(JsonElement item, string pascal, string camel)
    {
        if (item.TryGetProperty(pascal, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        if (item.TryGetProperty(camel, out value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }

    private static JsonElement? ReadFlexibleArray(JsonElement item, string pascal, string camel)
    {
        if (item.TryGetProperty(pascal, out var value) && value.ValueKind == JsonValueKind.Array)
            return value;
        if (item.TryGetProperty(camel, out value) && value.ValueKind == JsonValueKind.Array)
            return value;
        return null;
    }

    private static List<string> ReadFlexibleStringArray(JsonElement item, string pascal, string camel)
    {
        var array = ReadFlexibleArray(item, pascal, camel);
        if (!array.HasValue) return new List<string>();
        return array.Value.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static List<EvidenceConcept> ParseMatchedArray(JsonElement item, string pascalProperty, string? camelProperty = null)
    {
        var result = new List<EvidenceConcept>();
        var array = ReadFlexibleArray(item, pascalProperty, camelProperty ?? char.ToLowerInvariant(pascalProperty[0]) + pascalProperty[1..]);
        if (!array.HasValue)
            return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.Value.EnumerateArray())
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

    private static List<EvidenceConcept> ParseCandidateArray(JsonElement item, string pascalProperty, string? camelProperty = null)
    {
        var result = new List<EvidenceConcept>();
        var array = ReadFlexibleArray(item, pascalProperty, camelProperty ?? char.ToLowerInvariant(pascalProperty[0]) + pascalProperty[1..]);
        if (!array.HasValue)
            return result;

        foreach (var element in array.Value.EnumerateArray())
        {
            var id = element.TryGetProperty("Id", out var idProp) ? idProp.GetString() ?? ""
                : element.TryGetProperty("id", out idProp) ? idProp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id)) continue;
            var candidate = new EvidenceConcept
            {
                Id = id,
                Name = ReadFlexibleString(element, "Name", "name"),
                Type = ReadFlexibleNullableString(element, "Type", "type"),
                Description = ReadFlexibleNullableString(element, "Description", "description"),
                MatchReason = ReadFlexibleNullableString(element, "MatchReason", "matchReason")
            };
            var scoreElement = element.TryGetProperty("Score", out var scoreProp)
                ? scoreProp
                : element.TryGetProperty("score", out scoreProp) ? scoreProp : default;
            if (scoreElement.ValueKind == JsonValueKind.Number)
                candidate.Score = scoreElement.GetSingle();
            result.Add(candidate);
        }

        return result;
    }

    private static EvidenceConcept? ParseEvidenceConcept(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var id = element.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString() ?? ""
            : "";
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new EvidenceConcept
        {
            Id = id,
            Name = ExtractNameZh(element)
        };
    }

    private static List<EvidenceConcept> ParseProjectedCandidates(JsonElement item)
    {
        var result = new List<EvidenceConcept>();
        if (!item.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var candidate in candidates.EnumerateArray())
        {
            var id = ReadFlexibleString(candidate, "id", "Id");
            var path = ReadFlexibleString(candidate, "canonical_path", "CanonicalPath");
            if (string.IsNullOrWhiteSpace(id)) id = path;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var concept = new EvidenceConcept
            {
                Id = id,
                Name = ReadFlexibleString(candidate, "label", "Label"),
                Type = ReadFlexibleNullableString(candidate, "type", "Type"),
                MatchReason = ReadFlexibleNullableString(candidate, "reason", "Reason"),
                ReadStatus = ReadFlexibleNullableString(candidate, "read_status", "ReadStatus"),
                EvidenceId = ReadFlexibleNullableString(candidate, "evidence_id", "EvidenceId")
            };
            var targetResolved = candidate.TryGetProperty("target_resolved", out var tr)
                ? tr
                : candidate.TryGetProperty("TargetResolved", out tr) ? tr : default;
            if (targetResolved.ValueKind is JsonValueKind.True or JsonValueKind.False)
                concept.TargetResolved = targetResolved.GetBoolean();
            var score = candidate.TryGetProperty("score", out var sp)
                ? sp
                : candidate.TryGetProperty("Score", out sp) ? sp : default;
            if (score.ValueKind == JsonValueKind.Number)
                concept.Score = score.GetSingle();
            result.Add(concept);
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
                    Description = @"唯一工具：查询计划执行器。把客人的规则问题编译成结构化查询计划，程序会执行实体定位、完整意图检索、正式读取和结构导航。relation 选择：
- explain：X 是什么 / 怎么结算 / 怎么做 / 有什么效果 / 有几个
- condition：能不能 X / X 有什么前提 / 什么限制（读取 condition+cost+target 及声明引用）
- cost：X 要支付什么 / 费用及适用范围
- target：X 作用于谁 / 目标约束
- ordering：X 之后是什么 / 先后顺序 / 什么时候结束 / 每轮怎么轮转
- boundary：X 不会发生什么 / 满了怎么办 / 上限多少
- flow：流程查询。scope=game 返回整局结构；scope=local 必须给 entity，返回目标、直接子步骤和必要父级顺序/循环/条件
- list：分页浏览概念目录（实体解析失败需要找概念时用）
- members：枚举某个类型或显式集合的可声明成员；complete/partial 必须区分，不能把 partial 当全集
- rule_lookup：保留明确对象身份，同时用完整 intent 检索相关规则节点，程序自动正式读取有界候选；读取候选不等于已确认对象/相关性
- compute：只调用现有确定性公式/查表能力；缺参数或公式不支持时明确 partial/unsupported
- identify：客人用外观/位置描述某物但你不确定是哪个概念时，把描述原文作为 entity，程序返回候选
query 字段语义：
- entity：查谁/什么，填概念 id、准确中文名、canonical_path 或对象描述；知道准确身份时直接用，不要求凭空猜 id。
- intent：针对 entity 你要知道什么，保留用户问题里的动作、场景和疑点。必须来自用户问题及有效上下文，不能写成你已经猜出的答案，也不能包含尚未证实的规则。
- relation：需要解释、条件、顺序、边界、流程、目录、成员等哪类规则信息。
- scope：flow 必填，local 或 game；其他 relation 省略。scope=local 且无 entity 是 invalid，不会退回整局。
- offset/limit：list/members 分页参数，limit 默认 50，最大 200。
- include：flow scope=local 时可写 children/position/conditions，声明需要的局部上下文。
单一疑点默认只发一个 query；只有确实需要不同事实时才拆多个 query，每个 query 写自己的 intent。不要把每个名词机械拆成 explain + condition。
实体解析分三层：精确命中直接返回规则事实；未精确命中时程序会结合完整 intent 返回 Status=unresolved + Candidates。候选描述只用于选择目标，不能当作已确认事实；请从候选中挑真正回答 intent 的概念，用准确 id 或准确中文名、保持原 relation 和 intent 重新发起计划。没有合适的候选再用 identify 或 list。若返回 Status=no_match，说明规则库未确认到该事实——请向客人说明未查到或请其换个问法，不要基于记忆补写规则。若结果被标记为 unresolved，不要直接下结论；确认后的正式事实才可用于回答。直接回答用户所问，不重复询问已知信息，不添加没有依据的现场状态；必要规则前提可简短说明。",

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
                                    "enum": ["explain", "condition", "cost", "target", "ordering", "boundary", "flow", "list", "members", "rule_lookup", "compute", "identify"]
                                  },
                                  "entity": {
                                    "type": "string",
                                    "description": "概念 id、准确中文名或对象描述（flow/list 可省略）"
                                  },
                                  "intent": {
                                    "type": "string",
                                    "description": "针对 entity 要知道的动作、场景或疑点，来自用户问题及有效上下文；不要写猜测的答案或未证实的规则。单 query 缺省时程序可用原问句回填，多 query 时请逐项填写。"
                                  },
                                  "scope": {
                                    "type": "string",
                                    "enum": ["local", "game"],
                                    "description": "flow 必填。scope=local 表示查询目标局部结构且必须给 entity；scope=game 表示整局流程且不应给 entity。"
                                  },
                                  "offset": {
                                    "type": "integer",
                                    "minimum": 0,
                                    "description": "list/members 分页偏移，默认 0。"
                                  },
                                  "limit": {
                                    "type": "integer",
                                    "minimum": 1,
                                    "maximum": 200,
                                    "description": "list/members 单页上限，默认 50。"
                                  },
                                  "include": {
                                    "type": "array",
                                    "items": { "type": "string" },
                                    "description": "flow scope=local 时要包含的局部上下文，例如 children、position、conditions。"
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
                        var projected = _toolResultProjector.Project(gameId, planResult);
                        return Finish(_rulesService.AnnotateReferences(
                            JsonSerializer.Serialize(projected, ToolResultOptions), gameId));
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
