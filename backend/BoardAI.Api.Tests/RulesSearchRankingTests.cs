using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesSearchRankingTests
{
    [Fact]
    public void ExactObjectKeepsItsIntentScoreWhenDefinitionIsRelevant()
    {
        var definition = Concept("widget"); definition.Score = .85f;
        var action = Concept("use_widget"); action.Score = .65f;
        var ranked = RulesSearchRanking.MergeEntityAndIntentCandidates(
            Array.Empty<ConceptSummary>(), new[] { definition, action },
            new[] { Concept("widget") }, new[] { "widget" });
        Assert.Equal("widget", ranked[0].Id);
        Assert.Equal(.85f, ranked[0].Score);
    }

    private static ConceptSummary Concept(string id, string? path = null) => new() { Id = id, Path = path ?? id, Name = id };
    private static RulesSearchRanking.Batch Batch(string q, bool vector, params (ConceptSummary, float)[] items) => new(q, vector, items.ToList());

    [Fact]
    public void FullPhraseBeatsPartialTerm_EvenWhenPartialHasExtraChannels()
    {
        var test = Concept("favor_test");
        var marker = Concept("favor_marker");
        var ranked = RulesSearchRanking.Rank("Favor 测试", new[] { "Favor", "测试" }, new[]
        {
            Batch("Favor 测试", true, (test, .580f), (marker, .552f)),
            Batch("Favor", true, (marker, .558f)),
            Batch("Favor", false, (test, .05f), (marker, .05f))
        });
        Assert.Equal("favor_test", ranked[0].Id);
        Assert.False(RulesSearchRanking.CanAutoResolve(ranked));
    }

    [Fact]
    public void GenericCardTermDoesNotDisplaceCompleteBonusPhrase()
    {
        var bonus = Concept("point_bonus", "board.point_bonus");
        var aid = Concept("player_aid");
        var ranked = RulesSearchRanking.Rank("point bonus 卡", new[] { "point", "bonus", "卡" }, new[]
        {
            Batch("point bonus 卡", true, (bonus, .578f)),
            Batch("卡", true, (aid, .62f)),
            Batch("卡", false, (aid, .30f), (aid, .30f))
        });
        Assert.Equal("point_bonus", ranked[0].Id);
        Assert.Equal(.30f, ranked[1].TermScores!["卡"].Keyword);
        Assert.False(RulesSearchRanking.CanAutoResolve(ranked));
    }

    [Fact]
    public void DuplicateDefinitionsDoNotIncreaseKeywordScore()
    {
        var ranked = RulesSearchRanking.Rank("卡", new[] { "卡" }, new[]
        {
            Batch("卡", false, (Concept("player_aid"), .30f), (Concept("player_aid"), .30f))
        });
        Assert.Equal(.30f, Assert.Single(ranked).Score);
    }

    [Fact]
    public void LocalSlotsWithSameIdRemainSeparate()
    {
        var ranked = RulesSearchRanking.Rank("槽位", new[] { "槽位" }, new[]
        {
            Batch("槽位", true, (Concept("slot", "board.slot"), .80f), (Concept("slot", "console.slot"), .70f))
        });
        Assert.Equal(2, ranked.Count);
        Assert.Equal("board.slot", ranked[0].Path);
        Assert.Equal("console.slot", ranked[1].Path);
    }

    [Theory]
    [InlineData(.61f, .05f, false)]
    [InlineData(.90f, .00f, false)]
    [InlineData(.85f, .30f, true)]
    public void AutoResolutionNeedsStrongPhraseAndNameEvidence(float vector, float keyword, bool expected)
    {
        var candidate = Concept("candidate");
        candidate.Score = vector + keyword;
        candidate.PhraseScore = new ChannelScores { Vector = vector, Keyword = keyword };
        Assert.Equal(expected, RulesSearchRanking.CanAutoResolve(new[] { candidate }));
    }

    [Fact]
    public void NameRecallPreservesFeedingCandidateMissingFromFullText()
    {
        var harvest = Concept("harvest"); harvest.Score = .60f;
        var food = Concept("starting_food"); food.Score = .56f;
        var feeding = Concept("harvest_feeding"); feeding.Score = .85f;
        var result = RulesSearchRanking.SupplementNames(new[] { harvest, food }, new[] { feeding });
        Assert.Contains(result.Take(3), x => x.Id == "harvest_feeding");
        Assert.Equal(.85f, result[2].NameMatchScore);
        Assert.Null(result[2].PhraseScore);
    }

    [Fact]
    public void GenericNameMatchDoesNotDisplaceWhiteDiceFullDescription()
    {
        var activation = Concept("activation_die"); activation.Score = .5536f;
        var generic = Concept("dice"); generic.Score = .80f;
        var result = RulesSearchRanking.SupplementNames(new[] { activation }, new[] { generic });
        Assert.Equal("activation_die", result[0].Id);
        Assert.False(RulesSearchRanking.CanAutoResolve(result));
    }


    [Fact]
    public void WeakKeywordDuplicateDoesNotBlockStrongNameRecall()
    {
        var weak = Concept("module"); weak.Score = .05f;
        var name = Concept("module"); name.Score = .83f;
        var result = RulesSearchRanking.SupplementNames(new[] { weak }, new[] { name });
        var candidate = Assert.Single(result);
        Assert.Equal(.55f, candidate.Score);
        Assert.Equal(.83f, candidate.NameMatchScore);
        Assert.False(RulesSearchRanking.CanAutoResolve(result));
    }

    [Fact]
    public void ClsRecallFloorKeepsFullPhraseAndCapsNameOnlyCandidates()
    {
        var full = Concept("component"); full.Score = .52f;
        full.PhraseScore = new ChannelScores { Vector = .52f };
        var duplicate = Concept("component"); duplicate.Score = .85f;
        var generic = Concept("generic"); generic.Score = .90f;
        var result = RulesSearchRanking.SupplementNames(new[] { full }, new[] { generic, duplicate }, .50f);
        Assert.Equal("component", result[0].Id);
        Assert.Same(full, result[0]);
        Assert.Equal(.50f, result[1].Score);
        Assert.Equal(.90f, result[1].NameMatchScore);
        Assert.False(RulesSearchRanking.CanAutoResolve(result));
    }


    [Fact]
    public void IntentSceneEvidenceLeadsEntityNameRecall_ForSetupSelectionQuestion()
    {
        var deal = Concept("deal_nobles"); deal.Score = .7026f;
        deal.PhraseScore = new ChannelScores { Vector = .7026f, Keyword = 0f };
        var attract = Concept("attract_noble"); attract.Score = .5587f;
        var dealEntity = Concept("deal_nobles"); dealEntity.Score = .9599f;
        var attractEntity = Concept("attract_noble"); attractEntity.Score = 1.0796f;
        var exact = Concept("noble");

        var ranked = RulesSearchRanking.MergeEntityAndIntentCandidates(
            new[] { attractEntity, dealEntity },
            new[] { deal, attract },
            new[] { exact },
            new[] { "noble" });

        Assert.Equal("deal_nobles", ranked[0].Id);
        Assert.Equal("intent+entity", ranked[0].MatchReason);
        Assert.Contains(ranked, candidate => candidate.Id == "noble" && candidate.MatchReason == "exact_identity");
    }

    [Fact]
    public void IntentCandidateWeightsEntityRecallWithoutLettingGenericNameAccumulationWin()
    {
        var dealIntent = Concept("deal_nobles"); dealIntent.Score = .6004f;
        var attractIntent = Concept("attract_noble"); attractIntent.Score = .5925f;
        var dealEntity = Concept("deal_nobles"); dealEntity.Score = .9599f;
        var attractEntity = Concept("attract_noble"); attractEntity.Score = 1.0796f;

        var ranked = RulesSearchRanking.MergeEntityAndIntentCandidates(
            new[] { attractEntity, dealEntity },
            new[] { dealIntent, attractIntent },
            Array.Empty<ConceptSummary>(),
            Array.Empty<string>());

        Assert.Equal("attract_noble", ranked[0].Id);
        Assert.Equal(.6899f, ranked[0].Score, 3);
    }

}
