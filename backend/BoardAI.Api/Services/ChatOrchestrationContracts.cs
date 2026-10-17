using System.Text.Json;
using System.Text.Json.Serialization;
using BoardAI.Api.Models;

namespace BoardAI.Api.Services;

internal static class ChatOrchestrationKinds
{
    internal const string Rules = "rules";
    internal const string Conversation = "conversation";
    internal const string Answer = "answer";
    internal const string Clarify = "clarify";
    internal const string Unavailable = "unavailable";
    internal const string NeedMore = "need_more";
    internal const string Answered = "answered";
    internal const string NotNeeded = "not_needed";
}

internal sealed class UnderstandingDto
{
    [JsonPropertyName("request_kind")]
    public string RequestKind { get; set; } = "";

    [JsonPropertyName("reply")]
    public string? Reply { get; set; }

    [JsonPropertyName("questions")]
    public List<SubQuestionDto> Questions { get; set; } = new();
}

internal sealed class SubQuestionDto
{
    [JsonPropertyName("entity")]
    public string Entity { get; set; } = "";

    [JsonPropertyName("relation")]
    public string Relation { get; set; } = "";

    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "";

    [JsonPropertyName("user_context")]
    public string? UserContext { get; set; }
}

internal sealed class AnswerResultDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("reply")]
    public string Reply { get; set; } = "";

    [JsonPropertyName("question_results")]
    public List<QuestionResultDto> QuestionResults { get; set; } = new();

    [JsonPropertyName("need_more")]
    public NeedMoreDto? NeedMore { get; set; }
}

internal sealed class QuestionResultDto
{
    [JsonPropertyName("question_id")]
    public string QuestionId { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("evidence_ids")]
    public List<string> EvidenceIds { get; set; } = new();

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

internal sealed class NeedMoreDto
{
    [JsonPropertyName("question_id")]
    public string QuestionId { get; set; } = "";

    [JsonPropertyName("missing_information")]
    public string MissingInformation { get; set; } = "";

    [JsonPropertyName("supplement_entity")]
    public string SupplementEntity { get; set; } = "";

    [JsonPropertyName("supplement_relation")]
    public string SupplementRelation { get; set; } = "";

    [JsonPropertyName("supplement_intent")]
    public string SupplementIntent { get; set; } = "";
}

internal sealed class ProgramSubQuestion
{
    public string QuestionId { get; init; } = "";
    public string Entity { get; init; } = "";
    public string Relation { get; init; } = "";
    public string Intent { get; init; } = "";
    public string? UserContext { get; init; }
}

internal sealed class RuleEvidencePack
{
    [JsonPropertyName("pack_id")]
    public string PackId { get; set; } = "";

    [JsonPropertyName("game")]
    public string Game { get; set; } = "";

    [JsonPropertyName("rules_version")]
    public string? RulesVersion { get; set; }

    [JsonPropertyName("evidence_catalog")]
    public List<RuleEvidenceItem> EvidenceCatalog { get; set; } = new();

    [JsonPropertyName("questions")]
    public List<EvidenceQuestionPack> Questions { get; set; } = new();

    [JsonPropertyName("omitted_evidence_count")]
    public int OmittedEvidenceCount { get; set; }

    [JsonPropertyName("omitted_items")]
    public List<OmittedEvidenceInfo> OmittedItems { get; set; } = new();

    [JsonPropertyName("stats")]
    public RuleEvidenceStats Stats { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    /// <summary>元信息单独超过字符预算时，事实包已退化为显式预算不足。</summary>
    [JsonIgnore]
    public bool MetadataBudgetExceeded { get; set; }

    /// <summary>退化清空 omitted_items 后仍保留的原始计数。</summary>
    [JsonIgnore]
    public int? RetainedOmittedCount { get; set; }
}

internal sealed class RuleEvidenceStats
{
    [JsonPropertyName("read_attempts")]
    public int ReadAttempts { get; set; }
    [JsonPropertyName("successful_reads")]
    public int SuccessfulReads { get; set; }
    [JsonPropertyName("unique_contents")]
    public int UniqueContents { get; set; }
    [JsonPropertyName("binding_reuses")]
    public int BindingReuses { get; set; }
    [JsonPropertyName("pack_items")]
    public int PackItems { get; set; }
    [JsonPropertyName("omitted")]
    public int Omitted { get; set; }
    [JsonPropertyName("effective_max_pack_chars")]
    public int EffectiveMaxPackChars { get; set; }
    [JsonPropertyName("effective_max_evidence_total")]
    public int EffectiveMaxEvidenceTotal { get; set; }
    [JsonPropertyName("effective_max_evidence_per_question")]
    public int EffectiveMaxEvidencePerQuestion { get; set; }
    [JsonPropertyName("effective_max_candidates_per_question")]
    public int EffectiveMaxCandidatesPerQuestion { get; set; }
}

internal sealed class OmittedEvidenceInfo
{
    [JsonPropertyName("question_id")]
    public string QuestionId { get; set; } = "";
    [JsonPropertyName("canonical_path")]
    public string CanonicalPath { get; set; } = "";
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
    [JsonPropertyName("dependency_impact")]
    public string DependencyImpact { get; set; } = "";
}

internal sealed class EvidenceQuestionPack
{
    [JsonPropertyName("question_id")]
    public string QuestionId { get; set; } = "";

    [JsonPropertyName("entity")]
    public string Entity { get; set; } = "";

    [JsonPropertyName("relation")]
    public string Relation { get; set; } = "";

    [JsonPropertyName("intent")]
    public string Intent { get; set; } = "";

    [JsonPropertyName("user_context")]
    public string? UserContext { get; set; }

    [JsonPropertyName("retrieval_status")]
    public string RetrievalStatus { get; set; } = "";

    [JsonPropertyName("retrieval_source")]
    public string RetrievalSource { get; set; } = "";

    [JsonPropertyName("candidates")]
    public List<CandidateSummary> Candidates { get; set; } = new();

    [JsonPropertyName("evidence_bindings")]
    public List<RuleEvidenceBinding> EvidenceBindings { get; set; } = new();

    [JsonPropertyName("coverage_status")]
    public string CoverageStatus { get; set; } = "unknown";

    [JsonPropertyName("omitted_evidence_count")]
    public int OmittedEvidenceCount { get; set; }

    [JsonPropertyName("omitted_items")]
    public List<OmittedEvidenceInfo> OmittedItems { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();
}

internal sealed class RuleEvidenceBinding
{
    [JsonPropertyName("evidence_id")]
    public string EvidenceId { get; set; } = "";
    [JsonPropertyName("role")]
    public string Role { get; set; } = "candidate";
    [JsonPropertyName("parent_path")]
    public string? ParentPath { get; set; }
    [JsonPropertyName("rank")]
    public int Rank { get; set; }
    [JsonPropertyName("added_in_round")]
    public string AddedInRound { get; set; } = "initial";
}

internal sealed class CandidateSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("score")]
    public float? Score { get; set; }

    [JsonPropertyName("read_status")]
    public string ReadStatus { get; set; } = "not_read";

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

internal sealed class RuleEvidenceItem
{
    [JsonPropertyName("evidence_id")]
    public string EvidenceId { get; set; } = "";

    [JsonPropertyName("canonical_path")]
    public string CanonicalPath { get; set; } = "";

    [JsonPropertyName("source_ref")]
    public string SourceRef { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("read_status")]
    public string ReadStatus { get; set; } = "ok";

    [JsonPropertyName("content_hash")]
    public string ContentHash { get; set; } = "";

    [JsonPropertyName("projection")]
    public string? Projection { get; set; }

    [JsonPropertyName("projection_complete")]
    public bool ProjectionComplete { get; set; } = true;

    [JsonPropertyName("source")]
    public JsonElement? Source { get; set; }

    [JsonPropertyName("condition")]
    public JsonElement? Condition { get; set; }

    [JsonPropertyName("cost")]
    public JsonElement? Cost { get; set; }

    [JsonPropertyName("target")]
    public JsonElement? Target { get; set; }

    [JsonPropertyName("boundary")]
    public JsonElement? Boundary { get; set; }

    [JsonPropertyName("siblings")]
    public List<string>? Siblings { get; set; }

    [JsonPropertyName("position_index")]
    public int? PositionIndex { get; set; }

    [JsonPropertyName("loop")]
    public JsonElement? Loop { get; set; }

    [JsonPropertyName("flow_context")]
    public List<string>? FlowContext { get; set; }

    [JsonPropertyName("facts")]
    public List<JsonElement>? Facts { get; set; }
}

internal sealed class ProgramChatResult
{
    public required string Reply { get; init; }
    public required AnswerEvidence Evidence { get; init; }
    public required ChatOrchestrationTrace Trace { get; init; }
}

internal sealed class ModelCallOutcome
{
    public string Stage { get; init; } = "";
    public string Reason { get; init; } = "";
    public int Sequence { get; init; }
    public bool Succeeded { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public double ElapsedMs { get; init; }
}
