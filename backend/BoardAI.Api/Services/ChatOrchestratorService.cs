using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    private readonly RuleToolExecutionService _toolExecutor;
    private readonly int _maxToolRounds;
    private readonly int _maxToolCallsPerRound;
    private readonly int _maxTotalToolCalls;
    private readonly TimeSpan _maxRequestTimeout;
    private readonly int _maxRepeatedFailures;
    private readonly int _maxConversationMessages;


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
            $"{llmOptions.Provider}|{llmOptions.BaseUrl}|{llmOptions.Model}|{llmOptions.Thinking}|{llmOptions.ToolChoice}|"
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
            JsonSerializer.Serialize(
                ChatToolCatalog.BuildTools("template"),
                ChatToolProtocol.ToolResultOptions));
        _toolExecutor = new RuleToolExecutionService(
            rulesService,
            llmOptions,
            new RuleToolResultProjector(llmOptions.MaxEvidencePackChars),
            logger);
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
        var contextMessage = ChatContextMessageBuilder.Build(context, _logger);
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
        using var telemetryScope = RuleRuntimeTelemetry.Begin(out var telemetryCollector);
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

        var contextMessage = ChatContextMessageBuilder.Build(context, _logger);
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

        var tools = ChatToolCatalog.BuildTools(gameId);
        var evidence = new AnswerEvidenceCollector();
        var ledger = new RuleRequestLedger();
        var continuationRegistry = new RuleContinuationRegistry();
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
            var protocolRepairPending = false;
            var answerFocusInjected = false;
            string? lastToolCallSignature = null;
            string? lastFailureSignature = null;

            for (var round = 0; round < _maxToolRounds; round++)
            {
                effectiveToken.ThrowIfCancellationRequested();
                roundsExecuted = round + 1;

                var isProtocolRepairCall = protocolRepairPending;
                protocolRepairPending = false;
                var modelCallStartMs = sw.Elapsed.TotalMilliseconds;
                var modelCallStopwatch = Stopwatch.StartNew();
                var response = await _llmService.ChatWithMessagesAsync(messages, tools, effectiveToken);
                modelCallStopwatch.Stop();
                AddModelTimelineSpan(
                    requestTrace, modelCallStartMs, modelCallStopwatch.Elapsed.TotalMilliseconds,
                    "model_planning", "tool_loop", response.Usage, roundsExecuted,
                    isProtocolRepairCall);
                var assistantMessage = response.Choices?.FirstOrDefault()?.Message
                    ?? throw new InvalidOperationException("LLM returned empty response.");
                var isSubmitAnswerCall = assistantMessage.ToolCalls?.Any(
                    call => call.Function.Name == "submit_answer") == true;
                var modelSpanName = isProtocolRepairCall
                    ? isSubmitAnswerCall
                        ? "model_protocol_repair_answer_submission"
                        : assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0
                            ? "model_protocol_repair_unstructured_final"
                            : "model_protocol_repair_planning"
                    : assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0
                        ? "model_unstructured_final"
                        : isSubmitAnswerCall
                            ? "model_answer_submission"
                            : "model_rule_planning";
                RenameLastModelTimelineSpan(requestTrace, modelSpanName);

                if (!string.IsNullOrWhiteSpace(assistantMessage.ReasoningContent))
                {
                    _logger.LogInformation("[Chat] Game {GameId}, Round {Round} reasoning:\n{Reasoning}",
                        gameId, roundsExecuted, assistantMessage.ReasoningContent);
                }

                // Plain assistant text is not a terminal protocol. It must be submitted
                // through submit_answer so rule answers can carry evidence IDs and
                // clarify/unavailable cannot be statistically counted as answers.
                // Bounded repair is program-enforced, not prompt-enforced.
                if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
                {
                    var reply = assistantMessage.Content ?? string.Empty;
                    if (protocolCorrections < _maxRepeatedFailures
                        && totalToolCallsExecuted < _maxTotalToolCalls)
                    {
                        protocolCorrections++;
                        requestTrace.ProtocolErrors.Add("plain_final_submission");
                        var correctionStart = sw.Elapsed.TotalMilliseconds;
                        if (!string.IsNullOrWhiteSpace(reply))
                            messages.Add(new ChatMessage { Role = "assistant", Content = reply });
                        messages.Add(new ChatMessage
                        {
                            Role = "user",
                            Content = BuildTerminalProtocolCorrection()
                        });
                        protocolRepairPending = true;
                        AddTimelineSpan(requestTrace, new ChatTimelineSpan
                        {
                            Name = "terminal_protocol_repair",
                            Category = "protocol",
                            StartMs = correctionStart,
                            DurationMs = 0,
                            ResultChars = reply.Length,
                            ProtocolRepair = true
                        });
                        _logger.LogWarning(
                            "[Chat] Game {GameId}, Round {Round} rejected plain final text; requiring submit_answer terminal protocol (repair {Repair}/{Max})",
                            gameId, roundsExecuted, protocolCorrections, _maxRepeatedFailures);
                        continue;
                    }

                    sw.Stop();
                    var builtEvidence = evidence.Build();
                    builtEvidence.OrchestrationOutcome = "protocol_error";
                    requestTrace.Outcome = "protocol_error";
                    requestTrace.ProtocolErrors.Add("terminal_protocol_failed");
                    const string protocolFailure =
                        "抱歉，本次回答未能按规则问答终态协议提交，无法确认规则依据。请重试或换一种更具体的问法。";
                    LogEvidenceSummary(
                        gameId, builtEvidence, roundsExecuted, totalToolCallsExecuted,
                        protocolCorrections, sw.Elapsed.TotalMilliseconds);
                    return new ChatProcessResult(
                        protocolFailure, builtEvidence,
                        FinalizeLegacyTrace(requestTrace, sw, ledger, telemetryCollector));
                }

                _logger.LogInformation("[Chat] Game {GameId}, Round {Round} tool calls:\n{ToolCalls}",
                    gameId,
                    roundsExecuted,
                    string.Join("\n", assistantMessage.ToolCalls.Select(t =>
                        $"  {t.Function.Name}({ChatToolProtocol.FormatForLog(t.Function.Arguments)})")));

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

                    if (toolCall.Function.Name == "submit_answer")
                    {
                        var terminal = TerminalSubmissionParser.Handle(toolCall, ledger);
                        if (terminal.IsValid)
                        {
                            sw.Stop();
                            var terminalEvidence = evidence.Build();
                            terminalEvidence.OrchestrationOutcome = terminal.Outcome;
                            terminalEvidence.ModelCallCount = requestTrace.ModelCallCount;
                            terminalEvidence.CitedEvidenceCount = terminal.EvidenceCount;
                            requestTrace.Outcome = terminal.Outcome;
                            LogEvidenceSummary(
                                gameId, terminalEvidence, roundsExecuted, totalToolCallsExecuted + 1,
                                protocolCorrections, sw.Elapsed.TotalMilliseconds);
                            _logger.LogInformation(
                                "[Chat] Game {GameId}, terminal submit_answer outcome={Outcome}, evidenceCount={EvidenceCount}, reply={Reply}",
                                gameId, terminal.Outcome, terminal.EvidenceCount, terminal.Text);
                            return new ChatProcessResult(
                                terminal.Text, terminalEvidence,
                                FinalizeLegacyTrace(requestTrace, sw, ledger, telemetryCollector));
                        }

                        ledger.RecordProtocolValidationError();
                        protocolCorrections++;
                        protocolRepairPending = true;
                        var validationError = ChatToolProtocol.SerializeToolError(terminal.Error);
                        messages.Add(new ChatMessage
                        {
                            Role = "tool",
                            ToolCallId = toolCall.Id,
                            Content = validationError
                        });
                        totalToolCallsExecuted++;
                        stopReason = GetToolStopReason(
                            1, repeatedCallCount, repeatedFailureCount);
                        if (protocolCorrections >= _maxRepeatedFailures)
                            stopReason = "protocol_validation_failures";
                        if (stopReason != null)
                        {
                            AppendSkippedToolResults(messages, assistantMessage.ToolCalls, toolIndex + 1, stopReason);
                            break;
                        }
                        continue;
                    }

                    var rulesVersion = RulesSnapshotScope.Current is { Game: var scopeGame } scope
                        && scopeGame == gameId
                            ? scope.Version
                            : null;
                    var ledgerKey = ledger.BuildKey(
                        toolCall.Function.Name, toolCall.Function.Arguments, rulesVersion);
                    string result;
                    var reused = ledger.TryGet(ledgerKey, out var cachedResult);
                    if (reused)
                    {
                        result = cachedResult;
                        ledger.RecordReuse(ledgerKey);
                    }
                    else
                    {
                        var toolStartMs = sw.Elapsed.TotalMilliseconds;
                        var toolStopwatch = Stopwatch.StartNew();
                        result = await _toolExecutor.ExecuteToolAsync(
                            toolCall, gameId, question, continuationRegistry, effectiveToken);
                        toolStopwatch.Stop();
                        AddTimelineSpan(requestTrace, new ChatTimelineSpan
                        {
                            Name = toolCall.Function.Name,
                            Category = "tool",
                            StartMs = toolStartMs,
                            DurationMs = toolStopwatch.Elapsed.TotalMilliseconds,
                            ResultChars = result.Length
                        });
                        if (toolCall.Function.Name == "execute_plan")
                            ledger.Store(ledgerKey, result);
                    }
                    var isToolError = ChatToolProtocol.TryNormalizeToolResult(
                        result, out var normalizedResult, out var toolErrorText);
                    result = normalizedResult;
                    totalToolCallsExecuted++;
                    effectiveToken.ThrowIfCancellationRequested();

                    _logger.LogInformation(
                        "[Chat] tool timing: game={GameId}, tool={ToolName}, reused={Reused}",
                        gameId, toolCall.Function.Name, reused);
                    _logger.LogInformation("[Chat] Game {GameId}, Tool {ToolName} result:\n{Result}",
                        gameId, toolCall.Function.Name, ChatToolProtocol.FormatForLog(result));
                    if (toolCall.Function.Name == "execute_plan")
                    {
                        // Duplicate calls are visible in the message list as a re-delivery,
                        // but the collector must not count the same query/evidence again.
                        if (!reused)
                        {
                            evidence.AddPlanResult(result);
                            ledger.ObserveToolResult(result);
                            continuationRegistry.ObserveToolResult(result);
                            LogPlanProvenance(gameId, result);
                        }
                    }

                    if (isToolError)
                    {
                        consecutiveToolFailures++;
                        var failureSignature = toolCallSignature + "\n" + toolErrorText;
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
            var finalEvidence = evidence.Build();
            finalEvidence.OrchestrationOutcome = "unavailable";
            requestTrace.Outcome = "unavailable";
            return new ChatProcessResult(
                finalReply, finalEvidence, FinalizeLegacyTrace(requestTrace, sw, ledger, telemetryCollector));
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
            requestTrace.Outcome = "request_timeout";
            var timeoutEvidence = evidence.Build();
            timeoutEvidence.OrchestrationOutcome = "unavailable";
            return new ChatProcessResult(
                BuildIncompleteReply("request_timeout", evidence),
                timeoutEvidence,
                FinalizeLegacyTrace(requestTrace, sw, ledger, telemetryCollector));
        }
        finally
        {
            sw.Stop();
        }
    }

    private static string BuildTerminalProtocolCorrection() =>
        "终态协议校验失败：普通文本不会被视为合法终态。请在下一次模型调用中调用 submit_answer，且只选择以下之一：\n"
        + "- outcome=answer：规则答案，evidence_ids 必须引用本请求 evidence_catalog 中已交付的 evidence_id；\n"
        + "- outcome=clarify：真正影响答案的指代/状态歧义，可无引用，并给 clarification.missing_kind 与 requested_information；\n"
        + "- outcome=unavailable：规则库未覆盖、身份无法解决或预算耗尽，给 unavailable_reason；\n"
        + "- outcome=conversation：仅用于闲聊或非规则回复，可无引用。\n"
        + "不要用 plain final 文本绕过校验。";

    private static void AddModelTimelineSpan(
        ChatOrchestrationTrace trace,
        double startMs,
        double durationMs,
        string name,
        string stage,
        LLMUsage? usage,
        int sequence,
        bool protocolRepair)
    {
        var span = new ChatTimelineSpan
        {
            Name = name,
            Category = "model",
            StartMs = startMs,
            DurationMs = durationMs,
            ProtocolRepair = protocolRepair,
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
            ReasoningTokens = span.ReasoningTokens,
            ProtocolRepair = protocolRepair
        });
    }

    private static void AddTimelineSpan(ChatOrchestrationTrace trace, ChatTimelineSpan span)
    {
        trace.Timeline.Add(span);
    }

    private static void RenameLastModelTimelineSpan(
        ChatOrchestrationTrace trace, string name)
    {
        for (var index = trace.Timeline.Count - 1; index >= 0; index--)
        {
            if (trace.Timeline[index].Category != "model") continue;
            trace.Timeline[index].Name = name;
            return;
        }
    }

    private static ChatOrchestrationTrace FinalizeLegacyTrace(
        ChatOrchestrationTrace trace,
        Stopwatch requestStopwatch,
        RuleRequestLedger ledger,
        RuleRuntimeTelemetry.Collector telemetryCollector)
    {
        var elapsed = requestStopwatch.Elapsed.TotalMilliseconds;
        foreach (var span in telemetryCollector.Snapshot())
        {
            trace.Timeline.Add(new ChatTimelineSpan
            {
                Name = span.Name,
                Category = span.Category,
                StartMs = span.StartMs,
                DurationMs = span.DurationMs
            });
        }
        ChatTimelineAccountingCalculator.ComputeSelfDurations(trace.Timeline);
        trace.TimelineCategories = ChatTimelineAccountingCalculator.ComputeCategoryCosts(trace.Timeline);
        var repairSpans = trace.Timeline.Where(span => span.ProtocolRepair).ToList();
        trace.ProtocolRepairModelCallCount = repairSpans.Count(span => span.Category == "model");
        trace.ProtocolRepairMs = Math.Round(repairSpans.Sum(span => span.DurationMs), 2);
        trace.ProtocolRepairSelfMs = Math.Round(repairSpans.Sum(span => span.SelfDurationMs), 2);
        trace.TimelineAccounting = ChatTimelineAccountingCalculator.Compute(trace.Timeline, elapsed);
        trace.ReusedToolCallCount = ledger.ToolCallsReused;
        trace.ExecutedToolCallCount = ledger.ToolCallsExecuted;
        trace.DeliveredEvidenceCount = ledger.DeliveredEvidenceCount;
        trace.DeliveredEvidenceChars = ledger.DeliveredEvidenceChars;
        trace.CandidateAutoReadCount = ledger.CandidateAutoReadCount;
        trace.ProtocolErrors.AddRange(
            Enumerable.Repeat("protocol_validation_error", ledger.ProtocolValidationErrors));
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

    /// <summary>
    /// Serializes tool errors through the same relaxed JSON options as tool
    /// results. Dynamic error text must never be concatenated into JSON.
    /// </summary>

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
                Content = ChatToolProtocol.SerializeToolError($"工具调用因达到停止条件（{stopReason}）未执行。")
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
                "model_final_answer", "summary", finalResponse.Usage, 1, false);
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
        if (!item.TryGetProperty(pascal, out value) || value.ValueKind != JsonValueKind.Array)
        {
            if (!item.TryGetProperty(camel, out value) || value.ValueKind != JsonValueKind.Array)
                return new List<string>();
        }

        return value.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString() ?? "")
            .Where(element => !string.IsNullOrWhiteSpace(element))
            .ToList();
    }


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




}
