using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class IndexContractTests
{
    [Fact]
    public void ComputePointId_MatchesPythonBigEndianUuidContract()
    {
        Assert.Equal(
            "075604e4-754d-925c-ab1a-67e18dce79fa",
            IndexContract.ComputePointId("testgame", "game", "widget", nameOnly: false));
        Assert.Equal(
            "d10710a1-1b07-e054-9317-a678d9971639",
            IndexContract.ComputePointId("testgame", "game", "widget", nameOnly: true));
        Assert.Equal(
            "aa8417aa-e7a3-7459-a2ec-f30252081529",
            IndexContract.ComputePointId("splendor", "ontology", "resource", nameOnly: false));
    }

    [Fact]
    public void ComputePointId_KeepsDifferentSourcesDistinct()
    {
        var ontology = IndexContract.ComputePointId("testgame", "ontology", "widget", false);
        var game = IndexContract.ComputePointId("testgame", "game", "widget", false);

        Assert.NotEqual(ontology, game);
    }

    [Fact]
    public void ComputeIndexVersion_IsDeterministicAndChangesWithContent()
    {
        var first = IndexContract.ComputeIndexVersion("testgame", "bge-base-zh-v1.5-fp32", 768, new[]
        {
            new ConceptIndexItem
            {
                ConceptId = "widget",
                Type = "objects",
                Source = "game",
                NameZh = "小装置",
                NameText = "小装置",
                SearchText = "widget 小装置 使用资源"
            }
        });
        var second = IndexContract.ComputeIndexVersion("testgame", "bge-base-zh-v1.5-fp32", 768, new[]
        {
            new ConceptIndexItem
            {
                ConceptId = "widget",
                Type = "objects",
                Source = "game",
                NameZh = "小装置",
                NameText = "小装置",
                SearchText = "widget 小装置 使用资源"
            }
        });
        var changed = IndexContract.ComputeIndexVersion("testgame", "bge-base-zh-v1.5-fp32", 768, new[]
        {
            new ConceptIndexItem
            {
                ConceptId = "widget",
                Type = "objects",
                Source = "game",
                NameZh = "小装置",
                NameText = "小装置",
                SearchText = "widget 小装置 使用资源并抽牌"
            }
        });

        Assert.Equal("15364129b9e78411", first);
        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
    }
}
