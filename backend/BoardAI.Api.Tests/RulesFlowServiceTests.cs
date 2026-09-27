using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesFlowServiceTests : IDisposable
{
    private readonly string _root;

    public RulesFlowServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "boardai-rules-flow-tests",
            Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void GetFlowPositions_BuildsSiblingsAndIndex()
    {
        WriteFlow("testgame", """
        {
          "procedures": [
            {
              "id": "root",
              "name": { "zh": "根" },
              "options": [
                { "id": "a", "name": { "zh": "甲" } },
                { "id": "b", "name": { "zh": "乙" } },
                { "id": "c", "name": { "zh": "丙" } }
              ]
            }
          ]
        }
        """);

        using var content = new RulesContentStore(_root);
        var service = new RulesFlowService(content);

        var positions = service.GetFlowPositions("testgame");

        Assert.Equal(new[] { "a", "b", "c" }, positions["a"].Siblings);
        Assert.Equal(0, positions["a"].Index);
        Assert.Equal(1, positions["b"].Index);
        Assert.Equal(2, positions["c"].Index);
    }

    [Fact]
    public void GetFlowPositions_TracksAncestorNames_WithoutLeakingAcrossSiblings()
    {
        WriteFlow("testgame", """
        {
          "procedures": [
            {
              "id": "root",
              "name": { "zh": "根" },
              "options": [
                {
                  "id": "branch_a",
                  "name": { "zh": "分支甲" },
                  "options": [
                    { "id": "leaf", "name": { "zh": "叶" } }
                  ]
                },
                { "id": "branch_b", "name": { "zh": "分支乙" } }
              ]
            }
          ]
        }
        """);

        using var content = new RulesContentStore(_root);
        var service = new RulesFlowService(content);

        var positions = service.GetFlowPositions("testgame");

        Assert.Empty(positions["root"].Ancestors);
        Assert.Equal(new[] { "根" }, positions["branch_a"].Ancestors);
        Assert.Equal(new[] { "根", "分支甲" }, positions["leaf"].Ancestors);
        Assert.Equal(new[] { "根" }, positions["branch_b"].Ancestors);
    }

    [Fact]
    public void GetFlowPositions_TracksNearestLoop()
    {
        WriteFlow("testgame", """
        {
          "procedures": [
            {
              "id": "round_loop",
              "name": { "zh": "回合循环" },
              "loop": { "count": 3, "until": "deck_empty" },
              "options": [
                { "id": "inside", "name": { "zh": "循环内" } },
                {
                  "id": "own_loop",
                  "name": { "zh": "自己的循环" },
                  "loop": { "count": 2 }
                }
              ]
            }
          ]
        }
        """);

        using var content = new RulesContentStore(_root);
        var service = new RulesFlowService(content);

        var positions = service.GetFlowPositions("testgame");

        Assert.True(positions["inside"].Loop.HasValue);
        Assert.Equal(3, positions["inside"].Loop!.Value.GetProperty("count").GetInt32());
        Assert.True(positions["own_loop"].Loop.HasValue);
        Assert.Equal(2, positions["own_loop"].Loop!.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public void GetFlowPositions_CachesPerGame_DifferentGamesDoNotCollide()
    {
        WriteFlow("game_a", """
        {
          "procedures": [
            { "id": "only_a", "name": { "zh": "游戏甲节点" } }
          ]
        }
        """);
        WriteFlow("game_b", """
        {
          "procedures": [
            { "id": "only_b", "name": { "zh": "游戏乙节点" } }
          ]
        }
        """);

        using var content = new RulesContentStore(_root);
        var service = new RulesFlowService(content);

        var gameA = service.GetFlowPositions("game_a");
        var gameB = service.GetFlowPositions("game_b");

        Assert.True(gameA.ContainsKey("only_a"));
        Assert.False(gameA.ContainsKey("only_b"));
        Assert.True(gameB.ContainsKey("only_b"));
        Assert.False(gameB.ContainsKey("only_a"));
        Assert.NotSame(gameA, gameB);
    }

    [Fact]
    public void Clear_RebuildsCache()
    {
        WriteFlow("testgame", """
        { "procedures": [ { "id": "first", "name": { "zh": "第一" } } ] }
        """);

        using var content = new RulesContentStore(_root);
        var service = new RulesFlowService(content);

        var first = service.GetFlowPositions("testgame");
        var second = service.GetFlowPositions("testgame");
        Assert.Same(first, second);
        Assert.True(first.ContainsKey("first"));

        WriteFlow("testgame", """
        {
          "procedures": [
            {
              "id": "second",
              "name": { "zh": "第二" },
              "description": { "zh": "内容已更新" }
            }
          ]
        }
        """);

        service.Clear();

        var rebuilt = service.GetFlowPositions("testgame");
        Assert.NotSame(first, rebuilt);
        Assert.True(rebuilt.ContainsKey("second"));
        Assert.False(rebuilt.ContainsKey("first"));
    }

    [Fact]
    public void GetFlowPositions_MissingFlowFile_ReturnsEmpty()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesFlowService(content);

        var positions = service.GetFlowPositions("missing_game");
        var again = service.GetFlowPositions("missing_game");

        Assert.Empty(positions);
        Assert.Same(positions, again);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 测试清理失败不应掩盖真实断言结果。
        }
    }

    private void WriteFlow(string game, string json)
        => WriteJson(Path.Combine("content", "games", game, "flow.json"), json);

    private void WriteJson(string relativePath, string json)
    {
        var path = Path.Combine(_root, relativePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, json);
    }
}
