using System.Text.Json.Serialization;

namespace BoardAI.Api.Models;

public class ChatResponse
{
    public string Reply { get; set; } = string.Empty;

    /// <summary>
    /// 逐查询证据元数据；旧客户端可忽略。只用于追溯/评测，不自动证明 LLM 最终每句话为真。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnswerEvidence? Evidence { get; set; }

    /// <summary>程序控制编排的阶段、预算、事实包和模型调用轨迹；不含密钥或完整提示词。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatOrchestrationTrace? Orchestration { get; set; }
}

/// <summary>一次回答的证据汇总：按 query 记录，而不是见到任意一条 ok 就把整条回答判为完全有据。</summary>
public class AnswerEvidence
{
    /// <summary>旧客户端兼容字段；program 模式下 tier 只表示证据状态，真实控制使用 OrchestrationOutcome 等字段。</summary>
    public string Tier { get; set; } = "tier3";

    /// <summary>program 模式的最终出口：answer / clarify / unavailable / partial / protocol_error。</summary>
    public string? OrchestrationOutcome { get; set; }
    public int ModelCallCount { get; set; }
    public int SupplementCount { get; set; }
    public int RepairCount { get; set; }
    public int EvidenceReadCount { get; set; }
    public int CitedEvidenceCount { get; set; }
    public int OmittedEvidenceCount { get; set; }
    public int EvidencePackChars { get; set; }
    public string? EvidencePackId { get; set; }

    public bool HasData { get; set; }
    public bool HasCandidates { get; set; }
    public bool IsComplete { get; set; }
    public string? RulesVersion { get; set; }
    public int QueryCount { get; set; }
    public int OkCount { get; set; }
    public int UnresolvedCount { get; set; }
    public int NoMatchCount { get; set; }
    public int UnsupportedCount { get; set; }
    /// <summary>历史查询中仍未解决的数量；上述状态计数继续保留历史原貌。</summary>
    public int PendingCount { get; set; }
    public List<QueryEvidence> Queries { get; set; } = new();
}

public class QueryEvidence
{
    public int QueryIndex { get; set; }
    public string Relation { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    /// <summary>当前子问题要知道什么；缺失时仅单 query 回填原问句。</summary>
    public string Intent { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public string Coverage { get; set; } = string.Empty;
    public string? RulesVersion { get; set; }
    public string? Message { get; set; }
    public bool HasData { get; set; }
    public List<string> PayloadTypes { get; set; } = new();
    public List<string> ResolvedTargets { get; set; } = new();
    public List<string> OmissionReasons { get; set; } = new();

    /// <summary>候选被后续精确查询确认时，指向 Queries 中该查询的零基索引；不覆盖原 Status。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ResolvedByQueryIndex { get; set; }

    /// <summary>被后续查询明确选中的候选 id；仅当后续精确命中唯一对象且确在原候选中时写入。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfirmedCandidateId { get; set; }

    /// <summary>完成确认的后续查询 relation；用于区分同一候选在不同关系下的误关联。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfirmedByRelation { get; set; }

    /// <summary>已命中概念（explain/condition/ordering/boundary/flow 的 Matched）。</summary>
    public List<EvidenceConcept> Matched { get; set; } = new();

    /// <summary>程序无法解析实体时给出的候选概念（unresolved/identify）。</summary>
    public List<EvidenceConcept> Candidates { get; set; } = new();

    /// <summary>命中概念直接引用的一层概念，供客户端定位上下文；不递归展开。</summary>
    public List<EvidenceConcept> References { get; set; } = new();

    public List<string> FlowContext { get; set; } = new();

    /// <summary>program 模式中该子问题装入的正式证据 id。</summary>
    public List<string> EvidenceIds { get; set; } = new();

    /// <summary>program 模式中该子问题的最终处理状态：answered / clarify / unavailable / not_needed。</summary>
    public string? OutcomeStatus { get; set; }
}

public class EvidenceConcept
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? Description { get; set; }
    public float? Score { get; set; }
    /// <summary>候选分数来源：intent / entity / intent+entity / exact_identity / question_hint。</summary>
    public string? MatchReason { get; set; }
}


/// <summary>聊天服务的内部返回值：回复与证据一起交给 Controller；旧 ProcessAsync 仍只取 Reply。</summary>
public sealed record ChatProcessResult(
    string Reply,
    AnswerEvidence Evidence,
    ChatOrchestrationTrace? Orchestration = null);


/// <summary>程序控制编排的结构化轨迹，用于日志、评测和客户端分层展示。</summary>
public class ChatOrchestrationTrace
{
    public string RequestId { get; set; } = string.Empty;
    public string Mode { get; set; } = "program";
    public string Outcome { get; set; } = string.Empty;
    public string? RulesVersion { get; set; }
    public string? PackId { get; set; }
    public int ModelCallCount { get; set; }
    public int SupplementCount { get; set; }
    public int RepairCount { get; set; }
    public int EvidenceCount { get; set; }
    public int OmittedEvidenceCount { get; set; }
    public int CandidateReadFailureCount { get; set; }
    public int EvidenceReadAttempts { get; set; }
    public int SuccessfulEvidenceReads { get; set; }
    public int EvidenceBindingReuses { get; set; }
    public int EvidencePackChars { get; set; }
    public int EffectiveMaxEvidencePackChars { get; set; }
    public double UnderstandMs { get; set; }
    public double RetrievalMs { get; set; }
    public double RespondMs { get; set; }
    public double ValidationMs { get; set; }
    public List<ChatModelCallTrace> ModelCalls { get; set; } = new();
    public List<string> ProtocolErrors { get; set; } = new();
    public List<ChatQuestionTrace> Questions { get; set; } = new();
    /// <summary>本次请求的单调时钟时间线；模型/tool/搜索/装配 span 都相对请求起点。</summary>
    public List<ChatTimelineSpan> Timeline { get; set; } = new();
    public ChatTimelineAccounting? TimelineAccounting { get; set; }
}

/// <summary>一个可对账的请求内时间 span。并行 span 共用并发组，不能简单相加当总耗时。</summary>
public class ChatTimelineSpan
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    /// <summary>相对请求开始时刻的单调时钟偏移。</summary>
    public double StartMs { get; set; }
    public double DurationMs { get; set; }
    public string? ConcurrencyGroup { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? ReasoningTokens { get; set; }
    public int? ResultChars { get; set; }
}

/// <summary>span 覆盖对账。CoveredMs 是区间并集，OverlapMs 是并行/重叠量，不是耗时相加。</summary>
public class ChatTimelineAccounting
{
    public double ElapsedMs { get; set; }
    public double CoveredMs { get; set; }
    public double UnaccountedMs { get; set; }
    public double OverlapMs { get; set; }
    public bool IsReconciled { get; set; }
    public int SpanCount { get; set; }
    public List<string> ParallelGroups { get; set; } = new();
}

internal static class ChatTimelineAccountingCalculator
{
    public static ChatTimelineAccounting Compute(
        IEnumerable<ChatTimelineSpan> spans,
        double elapsedMs,
        double toleranceMs = 1.0)
    {
        var ordered = spans
            .Where(span => span.DurationMs >= 0 && span.StartMs >= 0)
            .OrderBy(span => span.StartMs)
            .ThenBy(span => span.StartMs + span.DurationMs)
            .ToList();
        var union = 0d;
        var overlap = 0d;
        var currentStart = 0d;
        var currentEnd = 0d;
        var hasInterval = false;
        foreach (var span in ordered)
        {
            var start = span.StartMs;
            var end = span.StartMs + span.DurationMs;
            if (!hasInterval)
            {
                hasInterval = true;
                currentStart = start;
                currentEnd = end;
                continue;
            }

            if (start <= currentEnd + toleranceMs)
            {
                overlap += Math.Max(0, currentEnd - start);
                currentEnd = Math.Max(currentEnd, end);
            }
            else
            {
                union += currentEnd - currentStart;
                currentStart = start;
                currentEnd = end;
            }
        }
        if (hasInterval) union += currentEnd - currentStart;

        var feasible = ordered.All(span => span.StartMs + span.DurationMs <= elapsedMs + toleranceMs);
        var unaccounted = Math.Max(0, elapsedMs - union);
        return new ChatTimelineAccounting
        {
            ElapsedMs = Math.Round(elapsedMs, 2),
            CoveredMs = Math.Round(union, 2),
            UnaccountedMs = Math.Round(unaccounted, 2),
            OverlapMs = Math.Round(overlap, 2),
            IsReconciled = feasible && unaccounted >= -toleranceMs,
            SpanCount = ordered.Count,
            ParallelGroups = ordered
                .Select(span => span.ConcurrencyGroup)
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Distinct(StringComparer.Ordinal)
                .ToList()!
        };
    }
}

public class ChatModelCallTrace
{
    public int Sequence { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public double ElapsedMs { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? ReasoningTokens { get; set; }
}

public class ChatQuestionTrace
{
    public string QuestionId { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public string Relation { get; set; } = string.Empty;
    public string Intent { get; set; } = string.Empty;
    public string RetrievalStatus { get; set; } = string.Empty;
    public string CoverageStatus { get; set; } = string.Empty;
    public int CandidateCount { get; set; }
    public int EvidenceCount { get; set; }
    public string? OutcomeStatus { get; set; }
    /// <summary>Raw retrieval candidate canonical paths in rank order.</summary>
    public List<string> RetrievalCandidatePaths { get; set; } = new();
    /// <summary>Formal evidence IDs bound to this question.</summary>
    public List<string> EvidenceIds { get; set; } = new();
    /// <summary>Formal evidence canonical paths bound to this question.</summary>
    public List<string> EvidenceCanonicalPaths { get; set; } = new();
    /// <summary>Evidence IDs actually cited by the answer.</summary>
    public List<string> CitedEvidenceIds { get; set; } = new();
    /// <summary>Canonical paths for cited evidence when it exists in this pack.</summary>
    public List<string> CitedCanonicalPaths { get; set; } = new();
    public List<string> OmittedEvidence { get; set; } = new();
}
