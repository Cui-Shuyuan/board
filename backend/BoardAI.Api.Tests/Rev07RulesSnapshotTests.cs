using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Tests;

public sealed class Rev07RulesSnapshotTests : IDisposable
{
    private readonly string _tempRoot;

    public Rev07RulesSnapshotTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "boardai-rev07-snapshot-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void DocumentStore_ManyUpdates_BoundsRetiredDocuments()
    {
        using var store = new RulesDocumentStore();
        var path = Path.Combine(_tempRoot, "rules.json");
        File.WriteAllText(path, """{"value":0}""");
        store.GetDocument(path);

        for (var i = 1; i <= 200; i++)
        {
            File.WriteAllText(path, $$"""{"value":{{i}}}""");
            store.GetDocument(path);
        }

        Assert.True(store.RetiredDocumentCount <= 16,
            $"retired JsonDocument count grew to {store.RetiredDocumentCount}");
    }

    [Fact]
    public async Task AddedAndDeletedGameConcepts_RefreshSnapshot()
    {
        var ontologyPath = Path.Combine(_tempRoot, "content", "ontology", "concepts.json");
        Directory.CreateDirectory(Path.GetDirectoryName(ontologyPath)!);
        File.WriteAllText(ontologyPath, """
            {
              "concepts": [
                { "id": "ontology_widget", "name": { "zh": "本体小装置", "en": "Ontology Widget" }, "abstract": true }
              ]
            }
            """);

        using var service = new GameRulesService(
            Options.Create(new RulesOptions { BasePath = _tempRoot }),
            vectorSearch: null);

        var beforeTypes = service.GetConceptTypes("testgame");
        Assert.DoesNotContain("objects", beforeTypes);

        var gameDirectory = Path.Combine(_tempRoot, "content", "games", "testgame");
        Directory.CreateDirectory(gameDirectory);
        File.WriteAllText(Path.Combine(gameDirectory, "concepts.json"), BuildConceptsJson(7));

        using var plan = JsonDocument.Parse(
            """
            { "queries": [ { "relation": "explain", "entity": "widget" } ] }
            """);
        var added = await service.ExecutePlanAsync("testgame", plan.RootElement);
        Assert.Equal("ok", Assert.Single(added.Results).Status);
        Assert.Equal("小装置7", AddedName(added.Results[0]));

        File.Delete(Path.Combine(gameDirectory, "concepts.json"));

        var deletedTypes = service.GetConceptTypes("testgame");
        Assert.DoesNotContain("objects", deletedTypes);
        var deleted = await service.ExecutePlanAsync("testgame", plan.RootElement);
        Assert.NotEqual("ok", Assert.Single(deleted.Results).Status);
    }

    [Fact]
    public async Task ConcurrentRequestsAndHotUpdates_BindSingleRuleVersion()
    {
        using var fixture = new RulesFixture();
        using var service = fixture.CreateService();
        SeedInMemory(service, fixture);

        var writerDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exceptions = new ConcurrentBag<Exception>();
        var sawVersions = new ConcurrentDictionary<int, byte>();

        var writer = Task.Run(() =>
        {
            try
            {
                for (var version = 1; version <= 500; version++)
                {
                    service.SetDocumentForTesting(
                        "content/games/testgame/concepts.json",
                        BuildConceptsJson(version));
                    if (version % 10 == 0)
                        Thread.Yield();
                }
            }
            finally
            {
                writerDone.TrySetResult();
            }
        });

        var readers = Enumerable.Range(0, Math.Max(4, Environment.ProcessorCount))
            .Select(_ => Task.Run(async () =>
            {
                while (!writerDone.Task.IsCompleted)
                {
                    try
                    {
                        var version = await ExecutePlanAndReadVersion(service);
                        if (version.HasValue)
                            sawVersions.TryAdd(version.Value, 0);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            }))
            .ToArray();

        await Task.WhenAll(readers.Append(writer));

        Assert.True(exceptions.IsEmpty, string.Join("\n", exceptions.Take(5).Select(e => e.ToString())));
        Assert.NotEmpty(sawVersions);
    }

    [Fact]
    public async Task CacheHitPath_AfterInMemoryUpdate_RebuildsDerivedCaches()
    {
        using var fixture = new RulesFixture();
        using var service = fixture.CreateService();
        SeedInMemory(service, fixture, version: 1);

        var first = await ExecutePlanAndReadVersion(service);
        Assert.Equal(1, first);

        service.SetDocumentForTesting(
            "content/games/testgame/concepts.json",
            BuildConceptsJson(2));

        var second = await ExecutePlanAndReadVersion(service);
        Assert.Equal(2, second);
    }

    private static async Task<int?> ExecutePlanAndReadVersion(GameRulesService service)
    {
        using var plan = JsonDocument.Parse(
            """
            {
              "queries": [
                { "relation": "identify", "entity": "小装置" },
                { "relation": "explain", "entity": "widget" },
                { "relation": "explain", "entity": "use_widget" }
              ]
            }
            """);

        var result = await service.ExecutePlanAsync("testgame", plan.RootElement);
        var versions = new List<int>();

        foreach (var item in result.Results)
        {
            if (item.Candidates is { Count: > 0 })
                versions.AddRange(item.Candidates.Select(c => ParseVersion(c.Name)).Where(v => v.HasValue).Select(v => v!.Value));

            foreach (var matched in item.Matched)
            {
                var name = matched.TryGetProperty("name", out var nameElement)
                    && nameElement.TryGetProperty("zh", out var zh)
                    ? zh.GetString() ?? ""
                    : "";
                var version = ParseVersion(name);
                if (version.HasValue) versions.Add(version.Value);
            }
        }

        if (versions.Distinct().Count() > 1)
            throw new InvalidOperationException(
                $"single request mixed rule versions: {string.Join(", ", versions)}");

        if (versions.Count == 0)
            throw new InvalidOperationException("no versions found: " + JsonSerializer.Serialize(result));
        return versions[0];
    }

    private static void SeedInMemory(GameRulesService service, RulesFixture fixture, int version = 0)
    {
        service.SetDocumentForTesting(
            "content/ontology/concepts.json",
            File.ReadAllText(fixture.OntologyConceptsPath));
        service.SetDocumentForTesting(
            "content/games/testgame/flow.json",
            File.ReadAllText(fixture.GameFlowPath));
        service.SetDocumentForTesting(
            "content/games/testgame/concepts.json",
            BuildConceptsJson(version));
    }

    private static string AddedName(PlanItemResult item)
    {
        var matched = Assert.Single(item.Matched);
        return matched.GetProperty("name").GetProperty("zh").GetString() ?? "";
    }

    private static int? ParseVersion(string text)
    {
        var match = Regex.Match(text, @"(\d+)$");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static string BuildConceptsJson(int version)
        => $$"""
        {
          "objects": [
            {
              "id": "widget",
              "name": { "zh": "小装置{{version}}", "en": "Widget" },
              "description": { "zh": "测试对象 {{version}}" }
            }
          ],
          "actions": [
            {
              "id": "use_widget",
              "name": { "zh": "使用小装置{{version}}", "en": "Use Widget" },
              "<ontology::condition>": { "id": "widget_ready" },
              "<ontology::cost>": { "id": "widget_cost" },
              "target": { "id": "widget_target" },
              "<ontology::content>": { "id": "widget_content" }
            }
          ],
          "triggers": [],
          "conditions": []
        }
        """;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // ignore cleanup failures
        }
    }
}
