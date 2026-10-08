using BoardAI.Api.Models;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class AnswerEvidenceRecoveryTests
{
    [Fact]
    public void SingleIdentifyCandidateConfirmedLater_RetainsHistoryAndClearsPending()
    {
        var collector = Collector("identify", "explain");
        var evidence = collector.Build();
        Assert.Equal("tier1", evidence.Tier);
        Assert.True(evidence.IsComplete);
        Assert.Equal(0, evidence.PendingCount);
        Assert.Equal(1, evidence.UnresolvedCount);
        Assert.Equal("unresolved", evidence.Queries[0].Status);
        Assert.Equal(1, evidence.Queries[0].ResolvedByQueryIndex);
    }

    [Theory]
    [InlineData("explain", "explain", true)]
    [InlineData("condition", "condition", true)]
    [InlineData("condition", "explain", false)]
    [InlineData("identify", "condition", false)]
    public void RecoveryRequiresTheRequestedRelation(string first, string later, bool recovered)
    {
        var evidence = Collector(first, later).Build();
        Assert.Equal(recovered, evidence.IsComplete);
        Assert.Equal(recovered ? 0 : 1, evidence.PendingCount);
    }

    [Theory]
    [InlineData("no_match", "exact_id", "v1", "widget")]
    [InlineData("unsupported", "exact_id", "v1", "widget")]
    [InlineData("unresolved", "auto_semantic", "v1", "widget")]
    [InlineData("unresolved", "exact_id", "v2", "widget")]
    [InlineData("unresolved", "exact_id", "v1", "other")]
    public void UnrelatedOrUnconfirmedEvidenceDoesNotEraseMissing(
        string status, string source, string version, string matchedId)
    {
        var collector = Collector("identify", "explain");
        collector.Queries[0].Status = status;
        collector.Queries[1].Source = source;
        collector.Queries[1].RulesVersion = version;
        collector.Queries[1].Matched[0].Id = matchedId;
        var evidence = collector.Build();
        Assert.False(evidence.IsComplete);
        Assert.Equal("partial", evidence.Tier);
        Assert.Equal(1, evidence.PendingCount);
        Assert.Null(evidence.Queries[0].ResolvedByQueryIndex);
    }

    [Fact]
    public void MultipleCandidates_ConfirmingOneDoesNotClaimTheWholeRequirementWasCovered()
    {
        var collector = Collector("identify", "explain");
        collector.Queries[0].Candidates.Add(new EvidenceConcept { Id = "other" });
        var evidence = collector.Build();
        Assert.False(evidence.IsComplete);
        Assert.Equal(1, evidence.PendingCount);
    }

    [Fact]
    public void EarlierHitDoesNotRecoverLaterUnresolvedQuery()
    {
        var collector = Collector("identify", "explain");
        collector.Queries.Reverse();
        Assert.Equal(1, collector.Build().PendingCount);
    }

    [Fact]
    public void AddingAnUnrelatedMissingQuestionKeepsTheAnswerPartialAfterRecovery()
    {
        var collector = Collector("identify", "explain");
        collector.Queries.Add(new QueryEvidence { Relation = "explain", Entity = "unknown", Status = "no_match" });
        var evidence = collector.Build();
        Assert.Equal("partial", evidence.Tier);
        Assert.Equal(1, evidence.PendingCount);
        Assert.Equal(1, evidence.Queries[0].ResolvedByQueryIndex);
    }

    private static ChatOrchestratorService.AnswerEvidenceCollector Collector(string first, string later)
    {
        var collector = new ChatOrchestratorService.AnswerEvidenceCollector();
        collector.Queries.Add(new QueryEvidence
        {
            Relation = first, Entity = "白色组件", Status = "unresolved", RulesVersion = "v1",
            Candidates = new() { new EvidenceConcept { Id = "widget" } }
        });
        collector.Queries.Add(new QueryEvidence
        {
            Relation = later, Entity = "widget", Status = "ok", Source = "exact_id", RulesVersion = "v1",
            HasData = true, Matched = new() { new EvidenceConcept { Id = "widget" } }
        });
        return collector;
    }
}
