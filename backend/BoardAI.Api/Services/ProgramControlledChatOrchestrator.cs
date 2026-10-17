using System.Diagnostics;
using System.Text.Json;
using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

/// <summary>
/// 程序控制的两阶段问答编排：
/// Understand（结构化理解）→ Retrieve（程序读取正式事实包）→ Respond（结构化回答）。
/// 模型不能自由发起工具循环；补充检索和协议修复都由本类按预算控制。
/// </summary>
internal sealed class ProgramControlledChatOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static readonly HashSet<string> AllowedRelations = new(StringComparer.Ordinal)
    {
        "explain", "condition", "ordering", "boundary", "flow", "list", "identify"
    };

    private readonly ILLMService _llmService;
    private readonly GameRulesService _rulesService;
    private readonly LLMOptions _options;
    private readonly ILogger _logger;

    public ProgramControlledChatOrchestrator(
        ILLMService llmService,
        GameRulesService rulesService,
        LLMOptions options,
        ILogger logger)
    {
        _llmService = llmService;
        _rulesService = rulesService;
        _options = options;
        _logger = logger;
    }

    public async Task<ProgramChatResult> ProcessAsync(
        string gameId,
        string question,
        List<ChatMessage> baseMessages,
        CancellationToken cancellationToken)
    {
        var requestId = "chat_" + Guid.NewGuid().ToString("N")[..12];
        var budget = new ModelCallBudget(Math.Clamp(_options.MaxModelCallsPerRequest, 2, 4));
        var trace = new ChatOrchestrationTrace
        {
            RequestId = requestId,
            Mode = "program",
            Outcome = "started"
        };
        var builder = new RuleEvidencePackBuilder(_rulesService, _options, _logger);
        var sw = Stopwatch.StartNew();

        try
        {
            var understandingMessages = WithStageInstruction(baseMessages, BuildUnderstandInstruction());

            var understanding = await SubmitUnderstandingAsync(
                gameId, question, understandingMessages, budget, trace, cancellationToken);
            if (understanding == null)
                return BuildFailure(gameId, requestId, trace, "understand_failed",
                    "抱歉，我没能正确理解这个问题，请换一种更具体的问法。");

            if (string.Equals(understanding.RequestKind, ChatOrchestrationKinds.Conversation, StringComparison.Ordinal))
            {
                trace.Outcome = "conversation";
                trace.UnderstandMs = sw.Elapsed.TotalMilliseconds;
                return BuildConversationResult(understanding.Reply ?? "", trace);
            }

            if (!string.Equals(understanding.RequestKind, ChatOrchestrationKinds.Rules, StringComparison.Ordinal))
            {
                return BuildFailure(gameId, requestId, trace, "invalid_request_kind",
                    "抱歉，我没能正确理解这个问题，请换一种更具体的问法。");
            }

            var questions = NormalizeQuestions(understanding.Questions);
            if (questions.Count == 0)
            {
                return BuildFailure(gameId, requestId, trace, "empty_question_plan",
                    "抱歉，我没能把问题拆成可查询的规则子问题，请换一种问法。");
            }

            trace.UnderstandMs = sw.Elapsed.TotalMilliseconds;
            var retrievalStarted = Stopwatch.StartNew();
            var pack = await builder.BuildAsync(gameId, questions, cancellationToken);
            retrievalStarted.Stop();
            trace.RetrievalMs = retrievalStarted.Elapsed.TotalMilliseconds;
            trace.PackId = pack.PackId;
            trace.RulesVersion = pack.RulesVersion;
            trace.EvidenceCount = pack.EvidenceCatalog.Count;
            trace.OmittedEvidenceCount = pack.OmittedEvidenceCount;
            trace.CandidateReadFailureCount = builder.CandidateReadFailureCount;
            trace.EvidencePackChars = builder.LastPackChars;
            trace.EvidenceReadAttempts = builder.LastStats.ReadAttempts;
            trace.SuccessfulEvidenceReads = builder.LastStats.SuccessfulReads;
            trace.EvidenceBindingReuses = builder.LastStats.BindingReuses;
            trace.EffectiveMaxEvidencePackChars = builder.LastStats.EffectiveMaxPackChars;
            UpdateQuestionTrace(trace, pack);

            var respondMessages = BuildRespondMessages(baseMessages, question, pack);
            AnswerResultDto? answer = null;
            var supplementCount = 0;
            var protocolFailure = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await CallStructuredAsync(
                    respondMessages,
                    BuildAnswerTool(),
                    "respond",
                    supplementCount == 0 ? "initial_answer" : "answer_after_supplement",
                    budget,
                    trace,
                    cancellationToken);
                if (response == null)
                    return BuildFailure(gameId, requestId, trace, "respond_call_failed",
                        "抱歉，生成回答时服务没有返回可用结果，请稍后重试。");

                var parse = ExtractStructuredArguments(response, "submit_answer", out var args, out var parseError);
                if (!parse)
                {
                    if (TryRepair(budget, trace, respondMessages, response, parseError))
                        continue;
                    protocolFailure = true;
                    break;
                }

                var validationSw = Stopwatch.StartNew();
                var answerValid = TryDeserializeAnswer(args, pack, questions, out var parsedAnswer, out var validationError);
                validationSw.Stop();
                trace.ValidationMs += validationSw.Elapsed.TotalMilliseconds;
                if (!answerValid)
                {
                    if (TryRepair(budget, trace, respondMessages, response, validationError))
                        continue;
                    protocolFailure = true;
                    break;
                }

                if (parsedAnswer.Kind != ChatOrchestrationKinds.NeedMore)
                {
                    answer = parsedAnswer;
                    break;
                }

                if (supplementCount >= Math.Max(0, _options.MaxSupplementQueries)
                    || !budget.CanCall)
                {
                    var resultById = parsedAnswer.QuestionResults
                        .Where(result => !string.IsNullOrWhiteSpace(result.QuestionId))
                        .GroupBy(result => result.QuestionId, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                    answer = new AnswerResultDto
                    {
                        Kind = ChatOrchestrationKinds.Unavailable,
                        Reply = "当前规则依据仍不足，无法继续补查。请缩小问题范围，或补充你指代的具体对象。",
                        QuestionResults = questions.Select(question =>
                            resultById.TryGetValue(question.QuestionId, out var result)
                                ? result
                                : new QuestionResultDto
                                {
                                    QuestionId = question.QuestionId,
                                    Status = "unavailable",
                                    Reason = "supplement_budget_exhausted"
                                }).ToList()
                    };
                    break;
                }

                if (!TryCreateSupplementQuestion(parsedAnswer, questions, pack, out var supplementQuestion, out var supplementError))
                {
                    if (TryRepair(budget, trace, respondMessages, response, supplementError))
                        continue;
                    protocolFailure = true;
                    break;
                }

                var appended = await builder.AddSupplementAsync(
                    pack, gameId, supplementQuestion, cancellationToken);
                if (!appended)
                {
                    if (TryRepair(budget, trace, respondMessages, response, "补充查询与已有查询重复，请改变查询对象或关系，或直接给出澄清/不可用回答。"))
                        continue;
                    protocolFailure = true;
                    break;
                }

                supplementCount++;
                trace.SupplementCount++;
                trace.EvidenceCount = pack.EvidenceCatalog.Count;
                trace.OmittedEvidenceCount = pack.OmittedEvidenceCount;
                trace.CandidateReadFailureCount = builder.CandidateReadFailureCount;
                trace.EvidencePackChars = builder.LastPackChars;
                trace.EvidenceReadAttempts = builder.LastStats.ReadAttempts;
                trace.SuccessfulEvidenceReads = builder.LastStats.SuccessfulReads;
                trace.EvidenceBindingReuses = builder.LastStats.BindingReuses;
                trace.EffectiveMaxEvidencePackChars = builder.LastStats.EffectiveMaxPackChars;
                UpdateQuestionTrace(trace, pack);
                respondMessages = BuildRespondMessages(baseMessages, question, pack);
            }

            if (answer == null)
            {
                var reason = protocolFailure ? "protocol_error" : "answer_missing";
                return BuildFailure(gameId, requestId, trace, reason,
                    "抱歉，我未能组织出符合规则协议的回答，请缩小问题范围或换一种问法。");
            }

            trace.Outcome = answer.Kind;
            trace.RespondMs = trace.ModelCalls
                .Where(call => string.Equals(call.Stage, "respond", StringComparison.Ordinal))
                .Sum(call => call.ElapsedMs);
            ApplyAnswerToQuestionTrace(trace, answer, pack);
            var evidence = BuildAnswerEvidence(pack, questions, answer, trace);
            trace.EvidenceCount = evidence.EvidenceReadCount;
            trace.OmittedEvidenceCount = evidence.OmittedEvidenceCount;
            trace.ModelCallCount = trace.ModelCalls.Count;
            LogTrace(trace, question);
            return new ProgramChatResult
            {
                Reply = string.IsNullOrWhiteSpace(answer.Reply) ? DefaultReplyFor(answer.Kind) : answer.Reply.Trim(),
                Evidence = evidence,
                Trace = trace
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            trace.Outcome = "canceled";
            trace.ModelCallCount = trace.ModelCalls.Count;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Orchestration] request failed: request={RequestId}, game={GameId}", requestId, gameId);
            trace.Outcome = "internal_error";
            trace.ProtocolErrors.Add(ex.Message);
            trace.ModelCallCount = trace.ModelCalls.Count;
            return BuildFailure(gameId, requestId, trace, "internal_error",
                "抱歉，本次规则查询未能完成，请换一种问法或稍后重试。");
        }
    }

    private async Task<UnderstandingDto?> SubmitUnderstandingAsync(
        string gameId,
        string question,
        List<ChatMessage> messages,
        ModelCallBudget budget,
        ChatOrchestrationTrace trace,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var response = await CallStructuredAsync(
                messages, BuildUnderstandTool(), "understand", "question_plan", budget, trace, cancellationToken);
            if (response == null)
                return null;

            var parse = ExtractStructuredArguments(response, "submit_understanding", out var args, out var error);
            if (parse && TryDeserializeUnderstanding(args, out var understanding, out error))
            {
                trace.UnderstandMs = sw.Elapsed.TotalMilliseconds;
                return understanding;
            }

            if (!TryRepair(budget, trace, messages, response, error))
                return null;
        }
    }

    private bool TryDeserializeUnderstanding(JsonElement args, out UnderstandingDto understanding, out string error)
    {
        understanding = new UnderstandingDto();
        error = "";
        try
        {
            var parsed = JsonSerializer.Deserialize<UnderstandingDto>(args.GetRawText(), JsonOptions);
            if (parsed == null)
            {
                error = "理解结果为空。";
                return false;
            }
            understanding = parsed;
            understanding.RequestKind = (understanding.RequestKind ?? "").Trim().ToLowerInvariant();
            if (understanding.RequestKind is not (ChatOrchestrationKinds.Rules or ChatOrchestrationKinds.Conversation))
            {
                error = "request_kind 必须是 rules 或 conversation。";
                return false;
            }
            if (understanding.RequestKind == ChatOrchestrationKinds.Conversation)
            {
                if (string.IsNullOrWhiteSpace(understanding.Reply))
                {
                    error = "conversation 必须提供 reply。";
                    return false;
                }
                understanding.Questions = new List<SubQuestionDto>();
                return true;
            }
            if (understanding.Questions == null || understanding.Questions.Count == 0)
            {
                error = "rules 请求至少需要一个 question。";
                return false;
            }
            if (understanding.Questions.Count > 6)
            {
                error = "单次规则问题最多拆成六个子问题。";
                return false;
            }
            for (var i = 0; i < understanding.Questions.Count; i++)
            {
                var q = understanding.Questions[i];
                q.Entity = (q.Entity ?? "").Trim();
                q.Relation = (q.Relation ?? "").Trim().ToLowerInvariant();
                q.Intent = (q.Intent ?? "").Trim();
                if (!AllowedRelations.Contains(q.Relation))
                {
                    error = $"第 {i + 1} 个问题的 relation 不受支持。";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(q.Intent))
                {
                    error = $"第 {i + 1} 个问题缺少 intent。";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(q.Entity) && q.Relation is not ("flow" or "list"))
                {
                    error = $"第 {i + 1} 个问题缺少 entity。";
                    return false;
                }
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = "理解结果不是合法 JSON：" + ex.Message;
            return false;
        }
    }

    private bool TryDeserializeAnswer(
        JsonElement args,
        RuleEvidencePack pack,
        IReadOnlyList<ProgramSubQuestion> questions,
        out AnswerResultDto answer,
        out string error)
    {
        answer = new AnswerResultDto();
        error = "";
        try
        {
            var parsed = JsonSerializer.Deserialize<AnswerResultDto>(args.GetRawText(), JsonOptions);
            if (parsed == null)
            {
                error = "回答结果为空。";
                return false;
            }
            parsed.Kind = (parsed.Kind ?? "").Trim().ToLowerInvariant();
            parsed.Reply = (parsed.Reply ?? "").Trim();
            parsed.QuestionResults ??= new List<QuestionResultDto>();
            if (parsed.Kind is not (ChatOrchestrationKinds.Answer
                or ChatOrchestrationKinds.Clarify
                or ChatOrchestrationKinds.Unavailable
                or ChatOrchestrationKinds.NeedMore))
            {
                error = "kind 必须是 answer / clarify / unavailable / need_more。";
                return false;
            }
            if (parsed.Kind == ChatOrchestrationKinds.NeedMore)
            {
                if (parsed.NeedMore == null)
                {
                    error = "need_more 必须提供 need_more 对象。";
                    return false;
                }
                var supplementRelation = (parsed.NeedMore.SupplementRelation ?? "").Trim().ToLowerInvariant();
                var supplementEntity = (parsed.NeedMore.SupplementEntity ?? "").Trim();
                if (string.IsNullOrWhiteSpace(parsed.NeedMore.MissingInformation)
                    || string.IsNullOrWhiteSpace(supplementEntity)
                    || !AllowedRelations.Contains(supplementRelation))
                {
                    error = "need_more 必须说明缺失信息、补充 entity 和合法 relation。";
                    return false;
                }
                parsed.NeedMore.SupplementRelation = supplementRelation;
                parsed.NeedMore.SupplementEntity = supplementEntity;
                parsed.NeedMore.SupplementIntent = (parsed.NeedMore.SupplementIntent ?? "").Trim();
                if (string.IsNullOrWhiteSpace(parsed.NeedMore.SupplementIntent))
                    parsed.NeedMore.SupplementIntent = parsed.NeedMore.MissingInformation.Trim();
                var needMoreResultIds = parsed.QuestionResults
                    .Select(result => result.QuestionId)
                    .ToList();
                if (needMoreResultIds.Count != needMoreResultIds.Distinct(StringComparer.Ordinal).Count())
                {
                    error = "question_results 中 question_id 重复。";
                    return false;
                }
                answer = parsed;
                return true;
            }

            if (string.IsNullOrWhiteSpace(parsed.Reply))
            {
                error = "answer/clarify/unavailable 必须提供 reply。";
                return false;
            }

            var expectedIds = questions.Select(q => q.QuestionId).ToHashSet(StringComparer.Ordinal);
            var resultIds = parsed.QuestionResults.Select(r => r.QuestionId).ToList();
            if (resultIds.Count != resultIds.Distinct(StringComparer.Ordinal).Count())
            {
                error = "question_results 中 question_id 重复。";
                return false;
            }
            foreach (var id in expectedIds)
            {
                if (!resultIds.Contains(id, StringComparer.Ordinal))
                {
                    error = $"question_results 缺少子问题 {id}。";
                    return false;
                }
            }

            var evidenceIds = pack.EvidenceCatalog
                .Select(e => e.EvidenceId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var result in parsed.QuestionResults)
            {
                result.Status = (result.Status ?? "").Trim().ToLowerInvariant();
                if (result.Status is not ("answered" or "clarify" or "unavailable" or "not_needed"))
                {
                    error = $"子问题 {result.QuestionId} 的 status 非法。";
                    return false;
                }
                result.EvidenceIds ??= new List<string>();
                if (result.EvidenceIds.Distinct(StringComparer.Ordinal).Count() != result.EvidenceIds.Count)
                {
                    error = $"子问题 {result.QuestionId} 的 evidence_ids 重复。";
                    return false;
                }
                foreach (var evidenceId in result.EvidenceIds)
                {
                    if (!evidenceIds.Contains(evidenceId))
                    {
                        error = $"引用了事实包之外的 evidence_id：{evidenceId}";
                        return false;
                    }
                }
                if (result.Status == "answered" && result.EvidenceIds.Count == 0)
                {
                    // 没有规则证据的纯闲聊应在第一阶段结束；回答阶段不能把空引用答案当已验证。
                    error = $"子问题 {result.QuestionId} 标记为 answered 但没有引用 evidence_id。";
                    return false;
                }
                if (result.Status == "not_needed" && string.IsNullOrWhiteSpace(result.Reason))
                {
                    error = $"子问题 {result.QuestionId} 标记为 not_needed 时必须说明 reason。";
                    return false;
                }
            }
            if (parsed.Kind == ChatOrchestrationKinds.Answer
                && parsed.QuestionResults.All(r => r.Status == "not_needed"))
            {
                error = "answer 不能让所有子问题都标记为 not_needed。";
                return false;
            }
            answer = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = "回答结果不是合法 JSON：" + ex.Message;
            return false;
        }
    }

    private bool TryCreateSupplementQuestion(
        AnswerResultDto answer,
        IReadOnlyList<ProgramSubQuestion> originalQuestions,
        RuleEvidencePack pack,
        out ProgramSubQuestion question,
        out string error)
    {
        question = new ProgramSubQuestion();
        error = "";
        if (answer.NeedMore == null)
        {
            error = "need_more 缺少补充信息。";
            return false;
        }
        var original = originalQuestions.FirstOrDefault(q =>
            string.Equals(q.QuestionId, answer.NeedMore.QuestionId, StringComparison.Ordinal))
            ?? originalQuestions.First();
        if (!AllowedRelations.Contains(answer.NeedMore.SupplementRelation))
        {
            error = "补充 relation 不受支持。";
            return false;
        }
        var intent = string.IsNullOrWhiteSpace(answer.NeedMore.SupplementIntent)
            ? answer.NeedMore.MissingInformation.Trim()
            : answer.NeedMore.SupplementIntent.Trim();
        if (string.IsNullOrWhiteSpace(intent))
        {
            error = "补充意图不能为空。";
            return false;
        }
        var duplicate = pack.Questions.Any(q =>
            string.Equals(q.QuestionId, original.QuestionId, StringComparison.Ordinal)
            && string.Equals(q.Entity, answer.NeedMore.SupplementEntity, StringComparison.Ordinal)
            && string.Equals(q.Relation, answer.NeedMore.SupplementRelation, StringComparison.Ordinal)
            && string.Equals(q.Intent, intent, StringComparison.Ordinal));
        if (duplicate)
        {
            error = "补充查询与已有证据读取重复。";
            return false;
        }
        question = new ProgramSubQuestion
        {
            QuestionId = original.QuestionId,
            Entity = answer.NeedMore.SupplementEntity,
            Relation = answer.NeedMore.SupplementRelation,
            Intent = intent,
            UserContext = original.UserContext
        };
        return true;
    }

    private static List<ProgramSubQuestion> NormalizeQuestions(List<SubQuestionDto> questions) =>
        questions.Select((q, index) => new ProgramSubQuestion
        {
            QuestionId = $"q{index + 1}",
            Entity = (q.Entity ?? "").Trim(),
            Relation = (q.Relation ?? "").Trim().ToLowerInvariant(),
            Intent = (q.Intent ?? "").Trim(),
            UserContext = q.UserContext
        }).ToList();

    private static List<ChatMessage> BuildRespondMessages(
        List<ChatMessage> baseMessages,
        string question,
        RuleEvidencePack pack)
    {
        var messages = WithStageInstruction(baseMessages, BuildRespondInstruction());
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "用户原问题：\n" + question
                + "\n\n程序已读取的正式规则事实包：\n"
                + JsonSerializer.Serialize(pack, RulesJsonUtils.RelaxedJsonOptions)
        });
        return messages;
    }

    private static List<ChatMessage> WithStageInstruction(
        IEnumerable<ChatMessage> baseMessages,
        string instruction)
    {
        var messages = baseMessages.ToList();
        var insertAt = 0;
        while (insertAt < messages.Count
               && string.Equals(messages[insertAt].Role, "system", StringComparison.OrdinalIgnoreCase))
            insertAt++;
        messages.Insert(insertAt, new ChatMessage
        {
            Role = "system",
            Content = instruction
        });
        return messages;
    }

    private static string BuildUnderstandInstruction() =>
        "【理解阶段】只做语言理解和查询规划，不要回答规则，不要猜测答案。"
        + "只调用 submit_understanding。判断 request_kind 是 rules 还是 conversation；"
        + "rules 时按真实子问题拆分，每个子问题给出 entity、relation、intent；"
        + "如果用户指代不清，仍保留有效的 entity/intent 供程序读取一次，不要直接编造对象。"

        + "只有真正闲聊或非规则问题才返回 conversation，并给出 reply。";

    private static string BuildRespondInstruction() =>
        "【回答阶段】只依据事实包中的正式规则内容回答。"
        + "只调用 submit_answer。answer 必须为每个子问题给出 question_results，"
        + "answered 必须引用事实包中真实存在的 evidence_id；不能引用候选摘要或包外内容。"
        + "如果用户指代或现场条件不足以判断，返回 clarify；如果规则读取不足、未找到或预算不足，返回 unavailable。"
        + "事实包包含全局 evidence_catalog；questions[].evidence_bindings 把 evidence_id 绑定到具体子问题、角色和父路径，用于定位，不是证据身份边界。可引用 evidence_catalog 中任一真实 evidence_id，优先引用该子问题绑定的条目；不能引用包外内容。"
        + "如果确实需要一次补充检索，返回 need_more，并明确缺失信息、补充 entity/relation/intent。"
        + "用户提供的信息不是已经验证的现场状态；不要把动画演示当成用户棋盘状态。"
        + "回复保持简短、TTS 友好、不使用无关扩展。";

    private async Task<StructuredResponse?> CallStructuredAsync(
        List<ChatMessage> messages,
        ToolDefinition tool,
        string stage,
        string reason,
        ModelCallBudget budget,
        ChatOrchestrationTrace trace,
        CancellationToken cancellationToken)
    {
        if (!budget.TryReserve())
        {
            trace.ProtocolErrors.Add($"model_call_budget_exhausted:{stage}");
            return null;
        }
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _llmService.ChatWithMessagesAsync(
                messages, new List<ToolDefinition> { tool }, cancellationToken);
            sw.Stop();
            RecordModelCall(trace, stage, reason, true, sw.Elapsed.TotalMilliseconds, response.Usage);
            _logger.LogInformation(
                "[Orchestration] model call: request={RequestId}, seq={Sequence}, stage={Stage}, reason={Reason}, elapsedMs={Elapsed:F0}, promptTokens={PromptTokens}, completionTokens={CompletionTokens}, reasoningTokens={ReasoningTokens}",
                trace.RequestId, trace.ModelCalls.Count, stage, reason, sw.Elapsed.TotalMilliseconds,
                response.Usage?.PromptTokens?.ToString() ?? "unknown",
                response.Usage?.CompletionTokens?.ToString() ?? "unknown",
                response.Usage?.CompletionTokenDetails?.ReasoningTokens?.ToString() ?? "unknown");
            var assistant = response.Choices?.FirstOrDefault()?.Message;
            if (assistant == null)
            {
                trace.ProtocolErrors.Add("empty_assistant_message:" + stage);
                return new StructuredResponse(response, null, sw.Elapsed.TotalMilliseconds);
            }
            return new StructuredResponse(response, assistant, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            RecordModelCall(trace, stage, reason, false, sw.Elapsed.TotalMilliseconds, null);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordModelCall(trace, stage, reason, false, sw.Elapsed.TotalMilliseconds, null);
            trace.ProtocolErrors.Add($"{stage}_call_failed:{ex.Message}");
            _logger.LogWarning(ex, "[Orchestration] model call failed: request={RequestId}, stage={Stage}", trace.RequestId, stage);
            return null;
        }
    }

    private static void RecordModelCall(
        ChatOrchestrationTrace trace,
        string stage,
        string reason,
        bool succeeded,
        double elapsedMs,
        LLMUsage? usage)
    {
        trace.ModelCalls.Add(new ChatModelCallTrace
        {
            Sequence = trace.ModelCalls.Count + 1,
            Stage = stage,
            Reason = reason,
            Succeeded = succeeded,
            ElapsedMs = Math.Round(elapsedMs, 2),
            PromptTokens = usage?.PromptTokens,
            CompletionTokens = usage?.CompletionTokens,
            ReasoningTokens = usage?.CompletionTokenDetails?.ReasoningTokens
        });
    }

    private bool TryRepair(
        ModelCallBudget budget,
        ChatOrchestrationTrace trace,
        List<ChatMessage> messages,
        StructuredResponse response,
        string error)
    {
        if (!budget.TryRepair())
        {
            trace.ProtocolErrors.Add("repair_budget_exhausted:" + error);
            return false;
        }
        trace.RepairCount++;
        trace.ProtocolErrors.Add(error);
        AppendRepairMessages(messages, response.Assistant, error);
        _logger.LogInformation(
            "[Orchestration] protocol repair: request={RequestId}, repair={Repair}, error={Error}",
            trace.RequestId, trace.RepairCount, error);
        return true;
    }

    private static void AppendRepairMessages(
        List<ChatMessage> messages,
        ChatMessage? assistant,
        string error)
    {
        if (assistant != null)
            messages.Add(assistant);
        var toolCall = assistant?.ToolCalls?.FirstOrDefault();
        if (toolCall != null)
        {
            messages.Add(new ChatMessage
            {
                Role = "tool",
                ToolCallId = toolCall.Id,
                Content = "{\"error\":" + JsonSerializer.Serialize(error) + "}"
            });
        }
        messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "协议修复：上一阶段输出未通过程序校验。错误：" + error
                + "。请只修正结构、引用或必填字段，不要编造规则事实，并重新调用指定的提交工具。"
        });
    }

    private static bool ExtractStructuredArguments(
        StructuredResponse response,
        string expectedToolName,
        out JsonElement args,
        out string error)
    {
        args = default;
        error = "";
        var calls = response.Assistant?.ToolCalls;
        if (calls == null || calls.Count == 0)
        {
            error = "没有调用指定提交工具。";
            return false;
        }
        if (calls.Count != 1)
        {
            error = "一次阶段调用只能提交一个结构化结果。";
            return false;
        }
        var call = calls[0];
        if (!string.Equals(call.Function.Name, expectedToolName, StringComparison.Ordinal))
        {
            error = $"调用了错误工具 {call.Function.Name}，应为 {expectedToolName}。";
            return false;
        }
        try
        {
            var doc = JsonDocument.Parse(call.Function.Arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "提交参数必须是 JSON 对象。";
                return false;
            }
            args = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = "提交参数不是合法 JSON：" + ex.Message;
            return false;
        }
    }

    private static void UpdateQuestionTrace(ChatOrchestrationTrace trace, RuleEvidencePack pack)
    {
        var byId = trace.Questions.ToDictionary(q => q.QuestionId, StringComparer.Ordinal);
        var catalog = pack.EvidenceCatalog.ToDictionary(e => e.EvidenceId, StringComparer.Ordinal);
        foreach (var q in pack.Questions)
        {
            var boundIds = q.EvidenceBindings
                .Select(b => b.EvidenceId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var item = new ChatQuestionTrace
            {
                QuestionId = q.QuestionId,
                Entity = q.Entity,
                Relation = q.Relation,
                Intent = q.Intent,
                RetrievalStatus = q.RetrievalStatus,
                CoverageStatus = q.CoverageStatus,
                CandidateCount = q.Candidates.Count,
                EvidenceCount = boundIds.Count,
                RetrievalCandidatePaths = q.Candidates.Select(c => string.IsNullOrWhiteSpace(c.Path) ? c.Id : c.Path).ToList(),
                EvidenceIds = boundIds,
                EvidenceCanonicalPaths = boundIds
                    .Where(catalog.ContainsKey)
                    .Select(id => catalog[id].CanonicalPath)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                OmittedEvidence = q.OmittedItems.Select(o => $"{o.CanonicalPath}:{o.Reason}").ToList()
            };
            if (byId.TryGetValue(item.QuestionId, out var old))
            {
                item.OutcomeStatus = old.OutcomeStatus;
                item.EvidenceIds = old.EvidenceIds
                    .Concat(item.EvidenceIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                item.EvidenceCanonicalPaths = old.EvidenceCanonicalPaths
                    .Concat(item.EvidenceCanonicalPaths)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var index = trace.Questions.IndexOf(old);
                if (index >= 0)
                    trace.Questions[index] = item;
                byId[item.QuestionId] = item;
            }
            else
            {
                trace.Questions.Add(item);
                byId[item.QuestionId] = item;
            }
        }
    }

    private static void ApplyAnswerToQuestionTrace(
        ChatOrchestrationTrace trace,
        AnswerResultDto answer,
        RuleEvidencePack pack)
    {
        var results = answer.QuestionResults
            .Where(result => !string.IsNullOrWhiteSpace(result.QuestionId))
            .GroupBy(result => result.QuestionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var catalog = pack.EvidenceCatalog.ToDictionary(e => e.EvidenceId, StringComparer.Ordinal);
        foreach (var question in trace.Questions)
        {
            if (!results.TryGetValue(question.QuestionId, out var result)) continue;
            question.OutcomeStatus = result.Status;
            question.CitedEvidenceIds = result.EvidenceIds.Distinct(StringComparer.Ordinal).ToList();
            question.CitedCanonicalPaths = question.CitedEvidenceIds
                .Where(catalog.ContainsKey)
                .Select(id => catalog[id].CanonicalPath)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
    }

    private static AnswerEvidence BuildAnswerEvidence(
        RuleEvidencePack pack,
        IReadOnlyList<ProgramSubQuestion> questions,
        AnswerResultDto answer,
        ChatOrchestrationTrace trace)
    {
        var allEvidence = pack.EvidenceCatalog.ToList();
        var cited = answer.QuestionResults
            .SelectMany(r => r.EvidenceIds ?? new List<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var evidence = new AnswerEvidence
        {
            RulesVersion = pack.RulesVersion,
            OrchestrationOutcome = answer.Kind,
            ModelCallCount = trace.ModelCalls.Count,
            SupplementCount = trace.SupplementCount,
            RepairCount = trace.RepairCount,
            EvidenceReadCount = allEvidence.Count,
            CitedEvidenceCount = cited.Count,
            OmittedEvidenceCount = pack.OmittedEvidenceCount,
            EvidencePackChars = trace.EvidencePackChars,
            EvidencePackId = pack.PackId,
            HasData = allEvidence.Count > 0,
            HasCandidates = pack.Questions.Any(q => q.Candidates.Count > 0),
            QueryCount = pack.Questions.Count,
            OkCount = pack.Questions.Count(q => q.EvidenceBindings.Count > 0),
            UnresolvedCount = pack.Questions.Count(q => q.RetrievalStatus == "unresolved"),
            NoMatchCount = pack.Questions.Count(q => q.RetrievalStatus == "no_match"),
            UnsupportedCount = pack.Questions.Count(q => q.RetrievalStatus is "unsupported" or "read_failed"),
            PendingCount = answer.Kind == ChatOrchestrationKinds.Answer
                ? CountUnanswered(answer, questions)
                : 0,
            Queries = BuildQueryEvidence(pack, answer)
        };
        evidence.IsComplete = answer.Kind == ChatOrchestrationKinds.Answer
            && evidence.PendingCount == 0
            && evidence.HasData;
        evidence.Tier = answer.Kind switch
        {
            ChatOrchestrationKinds.Answer when evidence.HasData => "tier1",
            ChatOrchestrationKinds.Clarify => "orchestrated",
            ChatOrchestrationKinds.Unavailable when evidence.HasData => "partial",
            ChatOrchestrationKinds.Unavailable => "tier3",
            _ => evidence.HasData ? "partial" : "tier3"
        };
        return evidence;
    }

    private static List<QueryEvidence> BuildQueryEvidence(RuleEvidencePack pack, AnswerResultDto answer)
    {
        var results = answer.QuestionResults
            .Where(result => !string.IsNullOrWhiteSpace(result.QuestionId))
            .GroupBy(result => result.QuestionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var output = new List<QueryEvidence>();
        for (var index = 0; index < pack.Questions.Count; index++)
        {
            var q = pack.Questions[index];
            results.TryGetValue(q.QuestionId, out var result);
            var bound = GetBoundEvidence(pack, q);
            var matched = bound
                .Where(item => item.Binding.Role is "target" or "candidate")
                .Select(item => new EvidenceConcept
                {
                    Id = item.Evidence.CanonicalPath,
                    Name = item.Evidence.Name,
                    Description = item.Evidence.EvidenceId
                })
                .ToList();
            var references = bound
                .Where(item => item.Binding.Role == "related")
                .Select(item => new EvidenceConcept
                {
                    Id = item.Evidence.CanonicalPath,
                    Name = item.Evidence.Name,
                    Description = item.Evidence.EvidenceId
                })
                .ToList();
            var query = new QueryEvidence
            {
                QueryIndex = index,
                Relation = q.Relation,
                Entity = q.Entity,
                Intent = q.Intent,
                Status = bound.Count > 0 ? "ok" : q.RetrievalStatus,
                Source = "program_pack",
                RulesVersion = pack.RulesVersion,
                HasData = bound.Count > 0,
                Matched = matched,
                References = references,
                Candidates = q.Candidates.Select(c => new EvidenceConcept
                {
                    Id = c.Id,
                    Name = c.Name,
                    Type = c.Type,
                    Description = c.ReadStatus,
                    Score = c.Score,
                    MatchReason = c.Reason
                }).ToList(),
                EvidenceIds = result?.EvidenceIds?.Distinct(StringComparer.Ordinal).ToList() ?? new List<string>(),
                OutcomeStatus = result?.Status
            };
            query.Message = q.Warnings.Count > 0 ? string.Join("；", q.Warnings) : null;
            output.Add(query);
        }
        return output;
    }

    private static List<(RuleEvidenceItem Evidence, RuleEvidenceBinding Binding)> GetBoundEvidence(
        RuleEvidencePack pack, EvidenceQuestionPack question)
    {
        var catalog = pack.EvidenceCatalog.ToDictionary(e => e.EvidenceId, StringComparer.Ordinal);
        return question.EvidenceBindings
            .Where(b => catalog.ContainsKey(b.EvidenceId))
            .Select(b => (catalog[b.EvidenceId], b))
            .ToList();
    }

    private static int CountUnanswered(AnswerResultDto answer, IReadOnlyList<ProgramSubQuestion> questions)
    {
        var statuses = answer.QuestionResults
            .Where(result => !string.IsNullOrWhiteSpace(result.QuestionId))
            .GroupBy(result => result.QuestionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Status, StringComparer.Ordinal);
        return questions.Count(q => !statuses.TryGetValue(q.QuestionId, out var status)
            || status is "clarify" or "unavailable");
    }

    private ProgramChatResult BuildFailure(
        string gameId,
        string requestId,
        ChatOrchestrationTrace trace,
        string outcome,
        string reply)
    {
        trace.Outcome = outcome;
        trace.ModelCallCount = trace.ModelCalls.Count;
        var evidence = new AnswerEvidence
        {
            Tier = "tier3",
            OrchestrationOutcome = outcome,
            ModelCallCount = trace.ModelCalls.Count,
            SupplementCount = trace.SupplementCount,
            RepairCount = trace.RepairCount,
            EvidenceReadCount = trace.EvidenceCount,
            OmittedEvidenceCount = trace.OmittedEvidenceCount,
            EvidencePackId = trace.PackId,
            HasData = trace.EvidenceCount > 0,
            HasCandidates = false,
            QueryCount = 0,
            PendingCount = 0
        };
        LogTrace(trace, "");
        return new ProgramChatResult
        {
            Reply = reply,
            Evidence = evidence,
            Trace = trace
        };
    }

    private static ProgramChatResult BuildConversationResult(string reply, ChatOrchestrationTrace trace)
    {
        trace.ModelCallCount = trace.ModelCalls.Count;
        return new ProgramChatResult
        {
            Reply = reply.Trim(),
            Evidence = new AnswerEvidence
            {
                Tier = "orchestrated",
                OrchestrationOutcome = ChatOrchestrationKinds.Conversation,
                ModelCallCount = trace.ModelCalls.Count,
                IsComplete = true,
                PendingCount = 0
            },
            Trace = trace
        };
    }

    private static string DefaultReplyFor(string kind) => kind switch
    {
        ChatOrchestrationKinds.Clarify => "需要补充一点具体信息才能确认这条规则，请说明你指代的对象或当前条件。",
        ChatOrchestrationKinds.Unavailable => "规则库里没有足够信息确认这条规则，请缩小问题范围或换一种问法。",
        _ => "本次规则查询未能完成，请换一种问法。"
    };

    private void LogTrace(ChatOrchestrationTrace trace, string question)
    {
        _logger.LogInformation(
            "[Orchestration] summary: request={RequestId}, outcome={Outcome}, rulesVersion={RulesVersion}, modelCalls={ModelCalls}, supplements={Supplements}, repairs={Repairs}, evidence={EvidenceCount}, omitted={Omitted}, candidatesReadFailures={CandidateReadFailures}, understandMs={UnderstandMs:F1}, retrievalMs={RetrievalMs:F1}, respondMs={RespondMs:F1}, question={Question}",
            trace.RequestId, trace.Outcome, trace.RulesVersion, trace.ModelCalls.Count, trace.SupplementCount,
            trace.RepairCount, trace.EvidenceCount, trace.OmittedEvidenceCount, trace.CandidateReadFailureCount,
            trace.UnderstandMs, trace.RetrievalMs, trace.RespondMs, question);
    }

    private sealed record StructuredResponse(LLMChatResponse Response, ChatMessage? Assistant, double ElapsedMs);

    private sealed class ModelCallBudget
    {
        private readonly int _maxCalls;
        private int _calls;
        private int _repairs;

        public ModelCallBudget(int maxCalls) => _maxCalls = maxCalls;
        public bool CanCall => _calls < _maxCalls;

        public bool TryReserve()
        {
            if (!CanCall) return false;
            _calls++;
            return true;
        }

        public bool TryRepair()
        {
            if (_repairs >= 1 || !CanCall) return false;
            _repairs++;
            return true;
        }
    }

    private static ToolDefinition BuildUnderstandTool() => new()
    {
        Function = new FunctionDefinition
        {
            Name = "submit_understanding",
            Description = "提交本轮的规则问题理解结果。只输出计划，不回答规则。",
            Parameters = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "request_kind": { "type": "string", "enum": ["rules", "conversation"] },
                "reply": { "type": "string", "description": "request_kind=conversation 时给用户的简短回复" },
                "questions": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "entity": { "type": "string", "description": "查谁/什么；flow/list 可空" },
                      "relation": { "type": "string", "enum": ["explain", "condition", "ordering", "boundary", "flow", "list", "identify"] },
                      "intent": { "type": "string", "description": "针对 entity 要知道的动作、场景或疑点" },
                      "user_context": { "type": "string", "description": "用户明确提供的条件或原文，不是已核验棋盘状态" }
                    },
                    "required": ["relation", "intent"]
                  }
                }
              },
              "required": ["request_kind", "questions"]
            }
            """).RootElement
        }
    };

    private static ToolDefinition BuildAnswerTool() => new()
    {
        Function = new FunctionDefinition
        {
            Name = "submit_answer",
            Description = "提交基于正式证据包的结构化回答。",
            Parameters = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["answer", "clarify", "unavailable", "need_more"] },
                "reply": { "type": "string" },
                "question_results": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "question_id": { "type": "string" },
                      "status": { "type": "string", "enum": ["answered", "clarify", "unavailable", "not_needed"] },
                      "evidence_ids": { "type": "array", "items": { "type": "string" } },
                      "reason": { "type": "string" }
                    },
                    "required": ["question_id", "status", "evidence_ids"]
                  }
                },
                "need_more": {
                  "type": "object",
                  "properties": {
                    "question_id": { "type": "string" },
                    "missing_information": { "type": "string" },
                    "supplement_entity": { "type": "string" },
                    "supplement_relation": { "type": "string", "enum": ["explain", "condition", "ordering", "boundary", "flow", "list", "identify"] },
                    "supplement_intent": { "type": "string" }
                  },
                  "required": ["question_id", "missing_information", "supplement_entity", "supplement_relation", "supplement_intent"]
                }
              },
              "required": ["kind", "reply", "question_results"]
            }
            """).RootElement
        }
    };
}
