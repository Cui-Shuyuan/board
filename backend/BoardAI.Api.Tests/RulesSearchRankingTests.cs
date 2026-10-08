using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesSearchRankingTests
{
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

}
