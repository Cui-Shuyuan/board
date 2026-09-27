using System.Text.Json;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesFactServiceTests : IDisposable
{
    private readonly string _root;

    public RulesFactServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "boardai-rules-fact-tests",
            Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void ExtractFacts_NoQuestionOrNoMatchedOrNoKeyword_ReturnsNull()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesFactService(content);
        var matched = new List<JsonElement> { Element("""{ "id": "field_tile" }""") };

        Assert.Null(service.ExtractFacts("testgame", matched, ""));
        Assert.Null(service.ExtractFacts("testgame", new List<JsonElement>(), "能放几只"));
        Assert.Null(service.ExtractFacts("testgame", matched, "这是什么"));
    }

    [Fact]
    public void ExtractFacts_CapacityFormula_EvaluatesExamples()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesFactService(content);
        var matched = new List<JsonElement>
        {
            Element(
                """
                {
                  "id": "pasture",
                  "capacity": {
                    "formula": {
                      "op": "*",
                      "operands": [
                        2,
                        { "var": "cells" },
                        { "op": "^", "operands": [2, { "var": "stables" }] }
                      ]
                    },
                    "examples": [
                      {
                        "zh": "1×2牧场加2马厩",
                        "bindings": { "cells": 2, "stables": 2 }
                      }
                    ]
                  }
                }
                """)
        };

        var facts = service.ExtractFacts("agricola", matched, "1×2牧场加2马厩能放几只动物");

        Assert.NotNull(facts);
        var fact = Assert.Single(facts);
        Assert.Equal("capacity_formula", fact.GetProperty("kind").GetString());
        Assert.Equal("<pasture>", fact.GetProperty("subject").GetString());
        var computed = Assert.Single(fact.GetProperty("computed").EnumerateArray());
        Assert.Equal("1×2牧场加2马厩", computed.GetProperty("zh").GetString());
        Assert.Equal(16L, computed.GetProperty("result").GetInt64());
    }

    [Fact]
    public void ExtractFacts_QuantityNumeric_SelectsMatchingBranchesAndComputesTotal()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesFactService(content);
        var matched = new List<JsonElement>
        {
            Element(
                """
                {
                  "id": "sow",
                  "quantity": {
                    "numeric": [
                      { "value": 1, "when": { "zh": "自己" } },
                      { "value": 2, "when": { "zh": "供应" } }
                    ]
                  }
                }
                """)
        };

        var matchingFacts = service.ExtractFacts("testgame", matched, "自己播种能得几个");
        Assert.NotNull(matchingFacts);
        var matching = Assert.Single(matchingFacts);
        Assert.Equal("quantity_numeric", matching.GetProperty("kind").GetString());
        Assert.Equal("<sow>", matching.GetProperty("subject").GetString());
        var selected = Assert.Single(matching.GetProperty("branches").EnumerateArray());
        Assert.Equal(1, selected.GetProperty("value").GetInt32());
        Assert.Equal("自己", selected.GetProperty("when").GetString());
        Assert.Equal(1, matching.GetProperty("total").GetInt32());

        var allFacts = service.ExtractFacts("testgame", matched, "播种能得多少");
        Assert.NotNull(allFacts);
        var all = Assert.Single(allFacts);
        Assert.Equal(2, all.GetProperty("branches").GetArrayLength());
        Assert.False(all.TryGetProperty("total", out _));
    }

    [Fact]
    public void ExtractFacts_ScoreTable_ByCount_ComputesLookupWithSingleInteger()
    {
        WriteFlow(
            "testgame",
            """
            {
              "score_table": {
                "by_count": [
                  {
                    "what": "<field_tile>",
                    "zh": "农田",
                    "rows": [
                      { "min": 1, "max": 2, "vp": -1 },
                      { "min": 3, "max": 3, "vp": -3 }
                    ]
                  }
                ]
              }
            }
            """);

        using var content = new RulesContentStore(_root);
        var service = new RulesFactService(content);
        var matched = new List<JsonElement> { Element("""{ "id": "field_tile" }""") };

        var facts = service.ExtractFacts("testgame", matched, "3 个空农场格得几分");

        Assert.NotNull(facts);
        var fact = Assert.Single(facts);
        Assert.Equal("score_rows", fact.GetProperty("kind").GetString());
        var computed = fact.GetProperty("computed");
        Assert.Equal(3, computed.GetProperty("count").GetInt32());
        Assert.Equal(-3, computed.GetProperty("vp").GetInt32());
    }

    [Fact]
    public void ExtractFacts_ScoreTable_PerUnit_Computes()
    {
        WriteFlow(
            "testgame",
            """
            {
              "score_table": {
                "per_unit": [
                  {
                    "what": "<animal>",
                    "zh": "动物",
                    "vp": 1,
                    "unit": { "zh": "只" },
                    "divisor": 2
                  }
                ]
              }
            }
            """);

        using var content = new RulesContentStore(_root);
        var service = new RulesFactService(content);
        var matched = new List<JsonElement> { Element("""{ "id": "animal" }""") };

        var facts = service.ExtractFacts("testgame", matched, "6 只动物得几分");

        Assert.NotNull(facts);
        var fact = Assert.Single(facts);
        Assert.Equal("score_per_unit", fact.GetProperty("kind").GetString());
        Assert.Equal(1, fact.GetProperty("vp").GetInt32());
        Assert.Equal(2, fact.GetProperty("divisor").GetInt32());
        Assert.Equal("只", fact.GetProperty("unit").GetProperty("zh").GetString());
        var computed = fact.GetProperty("computed");
        Assert.Equal(6, computed.GetProperty("count").GetInt32());
        Assert.Equal(3, computed.GetProperty("vp").GetInt32());
    }

    [Fact]
    public void Clear_RebuildsScoreTableCache()
    {
        WriteFlow(
            "testgame",
            """{ "score_table": { "per_unit": [ { "what": "<animal>", "vp": 1 } ] } }""");

        using var content = new RulesContentStore(_root);
        var service = new RulesFactService(content);
        var matched = new List<JsonElement> { Element("""{ "id": "animal" }""") };

        var before = service.ExtractFacts("testgame", matched, "6 只动物得几分");
        Assert.NotNull(before);
        Assert.Equal(6, Assert.Single(before).GetProperty("computed").GetProperty("vp").GetInt32());

        WriteFlow(
            "testgame",
            """
            {
              "score_table": {
                "per_unit": [
                  { "what": "<animal>", "zh": "动物", "vp": 2 }
                ]
              }
            }
            """);

        var cached = service.ExtractFacts("testgame", matched, "6 只动物得几分");
        Assert.NotNull(cached);
        Assert.Equal(6, Assert.Single(cached).GetProperty("computed").GetProperty("vp").GetInt32());

        service.Clear();

        var rebuilt = service.ExtractFacts("testgame", matched, "6 只动物得几分");
        Assert.NotNull(rebuilt);
        Assert.Equal(12, Assert.Single(rebuilt).GetProperty("computed").GetProperty("vp").GetInt32());
    }

    [Fact]
    public void ExtractFacts_ScoreWordsWithoutStructuredTable_ReturnsNull()
    {
        using var content = new RulesContentStore(_root);
        var service = new RulesFactService(content);
        var matched = new List<JsonElement> { Element("""{ "id": "field_tile" }""") };

        var facts = service.ExtractFacts("testgame", matched, "3 个空农场格得几分");

        Assert.Null(facts);
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

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private void WriteFlow(string game, string json)
    {
        var path = Path.Combine(_root, "content", "games", game, "flow.json");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, json);
    }
}
