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
    public void MultipleCandidates_ExplicitlyConfirmingOneCandidateResolvesThatQueryWithTrace()
    {
        var collector = Collector("identify", "explain");
        collector.Queries[0].Candidates.Add(new EvidenceConcept { Id = "other" });
        var evidence = collector.Build();
        Assert.True(evidence.IsComplete);
        Assert.Equal(0, evidence.PendingCount);
        Assert.Equal("widget", evidence.Queries[0].ConfirmedCandidateId);
        Assert.Equal("explain", evidence.Queries[0].ConfirmedByRelation);
        Assert.Equal(1, evidence.Queries[0].ResolvedByQueryIndex);
    }

    [Fact]
    public void SameCandidateAcrossTwoPendingQueries_OneExactQueryDoesNotDisambiguateEither()
    {
        var collector = Collector("identify", "explain");
        collector.Queries[0].Candidates.Add(new EvidenceConcept { Id = "other" });
        collector.Queries.Insert(1, new QueryEvidence
        {
            Relation = "identify", Entity = "另一个白色组件", Status = "unresolved", RulesVersion = "v1",
            Candidates = new() { new EvidenceConcept { Id = "widget" } }
        });
        var evidence = collector.Build();
        Assert.Equal(2, evidence.PendingCount);
        Assert.All(evidence.Queries.Take(2), query =>
        {
            Assert.Null(query.ResolvedByQueryIndex);
            Assert.Null(query.ConfirmedCandidateId);
        });
    }

    [Fact]
    public void SameCandidateWithMatchingIntent_ResolvesOnlyTheMatchingPendingQuery()
    {
        var collector = new ChatOrchestratorService.AnswerEvidenceCollector();
        collector.Queries.Add(new QueryEvidence
        {
            Relation = "explain", Entity = "贵族", Intent = "设置时如何选择",
            Status = "unresolved", RulesVersion = "v1",
            Candidates = new() { new EvidenceConcept { Id = "deal_nobles" } }
        });
        collector.Queries.Add(new QueryEvidence
        {
            Relation = "explain", Entity = "游戏设置", Intent = "开局怎么摆放",
            Status = "unresolved", RulesVersion = "v1",
            Candidates = new() { new EvidenceConcept { Id = "deal_nobles" } }
        });
        collector.Queries.Add(new QueryEvidence
        {
            Relation = "explain", Entity = "deal_nobles", Intent = "设置时如何选择",
            Status = "ok", Source = "exact_id", RulesVersion = "v1", HasData = true,
            Matched = new() { new EvidenceConcept { Id = "deal_nobles" } }
        });

        var evidence = collector.Build();

        Assert.Equal(1, evidence.PendingCount);
        Assert.Equal(2, evidence.Queries[0].ResolvedByQueryIndex);
        Assert.Null(evidence.Queries[1].ResolvedByQueryIndex);
    }

    [Fact]
    public void NearIdenticalIntentWithClearMargin_AssociatesExactConfirmation()
    {
        var collector = new ChatOrchestratorService.AnswerEvidenceCollector();
        collector.Queries.Add(new QueryEvidence
        {
            Relation = "explain", Entity = "贵族", Intent = "开局设置时贵族怎么选、放几张",
            Status = "unresolved", RulesVersion = "v1",
            Candidates = new() { new EvidenceConcept { Id = "deal_nobles" } }
        });
        collector.Queries.Add(new QueryEvidence
        {
            Relation = "explain", Entity = "贵族", Intent = "游戏开始后怎么获得贵族",
            Status = "unresolved", RulesVersion = "v1",
            Candidates = new() { new EvidenceConcept { Id = "deal_nobles" } }
        });
        collector.Queries.Add(new QueryEvidence
        {
            Relation = "explain", Entity = "deal_nobles", Intent = "开局设置时贵族怎么选、发几张",
            Status = "ok", Source = "exact_id", RulesVersion = "v1", HasData = true,
            Matched = new() { new EvidenceConcept { Id = "deal_nobles" } }
        });

        var evidence = collector.Build();

        Assert.Equal(1, evidence.PendingCount);
        Assert.Equal(2, evidence.Queries[0].ResolvedByQueryIndex);
        Assert.Null(evidence.Queries[1].ResolvedByQueryIndex);
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
