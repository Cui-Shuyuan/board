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
    public string? RulesVersion { get; set; }
    public string? Message { get; set; }
    public bool HasData { get; set; }

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
    public int EvidencePackChars { get; set; }
    public double UnderstandMs { get; set; }
    public double RetrievalMs { get; set; }
    public double RespondMs { get; set; }
    public double ValidationMs { get; set; }
    public List<ChatModelCallTrace> ModelCalls { get; set; } = new();
    public List<string> ProtocolErrors { get; set; } = new();
    public List<ChatQuestionTrace> Questions { get; set; } = new();
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
    public int CandidateCount { get; set; }
    public int EvidenceCount { get; set; }
    public string? OutcomeStatus { get; set; }
    public List<string> EvidenceIds { get; set; } = new();
}
