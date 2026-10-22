using System.Text.Json;
using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Tests;

public sealed class RulesCacheArchitectureTests
{
    [Fact]
    public async Task QaSearch_SecondCall_UsesMemoryCache_AndReturnsSameResults()
    {
        using var fixture = new RulesFixture();
        using var service = new GameRulesService(
            Options.Create(TestOptions(fixture.Root, memoryEnabled: true, diskEnabled: false)),
            vectorSearch: null);

        var cold = await service.SearchConceptsAsync("testgame", "widget");
        var hot = await service.SearchConceptsAsync("testgame", "widget");

        Assert.Equal(Serialize(cold), Serialize(hot));
        var stats = service.GetCacheStats();
        Assert.Equal(1, stats.Memory.Misses);
        Assert.True(stats.Memory.Hits >= 1);
        Assert.Equal(1, stats.Memory.CachedGameCount);
        Assert.True(stats.Memory.EstimatedManagedBytes > 0);
    }

    [Fact]
    public async Task QaSearch_WithMemoryAndDiskDisabled_StillReturnsSameResultsWithoutRetainedGames()
    {
        using var fixture = new RulesFixture();
        using var service = new GameRulesService(
            Options.Create(TestOptions(fixture.Root, memoryEnabled: false, diskEnabled: false)),
            vectorSearch: null);

        var first = await service.SearchConceptsAsync("testgame", "widget");
        var second = await service.SearchConceptsAsync("testgame", "widget");

        Assert.Equal(Serialize(first), Serialize(second));
        var stats = service.GetCacheStats();
        Assert.Equal(0, stats.Memory.CachedGameCount);
        Assert.Equal(0, stats.Memory.EstimatedManagedBytes);
        Assert.False(stats.Disk.Enabled);
    }

    [Fact]
    public void SmallMemoryBudget_EvictsLeastRecentlyUsedGame_AndKeepsRecentGamesHot()
    {
        using var root = new MultiGameFixture();
        var probeOptions = TestOptions(root.Path, memoryEnabled: true, diskEnabled: false);
        probeOptions.Cache.MemoryCache.BudgetBytes = 1024L * 1024 * 1024;
        long ontologyBytes;
        long gameBytes;

        using (var probe = new RulesContentStore(root.Path, probeOptions))
        {
            var probeSnapshot = probe.GetSnapshot("a");
            ontologyBytes = probeSnapshot.OntologySnapshot.EstimatedBytes;
            gameBytes = probeSnapshot.EstimatedBytes;
        }

        // Target holds exactly two games; adding the third must evict the oldest.
        var budget = (long)Math.Ceiling((ontologyBytes + 2.0 * gameBytes) / 0.80);
        var options = TestOptions(root.Path, memoryEnabled: true, diskEnabled: false);
        options.Cache.MemoryCache.BudgetBytes = budget;
        options.Cache.MemoryCache.HighWatermark = 0.90;
        options.Cache.MemoryCache.TargetWatermark = 0.80;

        using var content = new RulesContentStore(root.Path, options);
        content.GetSnapshot("a");
        content.GetSnapshot("b");
        content.GetSnapshot("c");

        var afterInsert = content.GetMemoryStats();
        Assert.Equal(2, afterInsert.CachedGameCount);
        Assert.True(afterInsert.Evictions >= 1);

        var missesBeforeHotReads = afterInsert.Misses;
        content.GetSnapshot("b");
        content.GetSnapshot("c");
        var afterHotReads = content.GetMemoryStats();
        Assert.Equal(2, afterHotReads.Hits);
        Assert.Equal(missesBeforeHotReads, afterHotReads.Misses);

        content.GetSnapshot("a");
        var afterColdRebuild = content.GetMemoryStats();
        Assert.Equal(missesBeforeHotReads + 1, afterColdRebuild.Misses);
        Assert.Equal(2, afterColdRebuild.CachedGameCount);
    }

    [Fact]
    public async Task NewProcess_RestoresKeywordSearchMaterialFromDisk()
    {
        using var fixture = new RulesFixture();
        var options = TestOptions(fixture.Root, memoryEnabled: true, diskEnabled: true);
        options.Cache.DiskCache.Directory = Path.Combine(fixture.Root, "rules-cache");
        options.Cache.DiskCache.FlushIntervalSeconds = 3600;
        options.Cache.DiskCache.ShutdownFlushTimeoutMilliseconds = 5_000;

        SearchConceptsResult first;
        using (var firstProcess = new GameRulesService(Options.Create(options), vectorSearch: null))
        {
            first = await firstProcess.SearchConceptsAsync("testgame", "widget");
        }

        var writtenFiles = Directory.GetFiles(
            options.Cache.DiskCache.Directory,
            "*.json",
            SearchOption.AllDirectories);
        Assert.NotEmpty(writtenFiles);

        SearchConceptsResult second;
        using (var secondProcess = new GameRulesService(Options.Create(options), vectorSearch: null))
        {
            second = await secondProcess.SearchConceptsAsync("testgame", "widget");
            var stats = secondProcess.GetCacheStats();
            Assert.True(stats.Disk.Hits >= 1, "The second process should restore the corpus from disk.");
        }

        Assert.Equal(Serialize(first), Serialize(second));
    }

    private static RulesOptions TestOptions(string basePath, bool memoryEnabled, bool diskEnabled)
    {
        return new RulesOptions
        {
            BasePath = basePath,
            Cache = new RulesCacheOptions
            {
                MemoryCache = new RulesMemoryCacheOptions
                {
                    Enabled = memoryEnabled,
                    BudgetBytes = 256L * 1024 * 1024,
                    HighWatermark = 0.80,
                    TargetWatermark = 0.60
                },
                DiskCache = new RulesDiskCacheOptions
                {
                    Enabled = diskEnabled,
                    FlushIntervalSeconds = 3600,
                    ShutdownFlushTimeoutMilliseconds = 2_000
                }
            }
        };
    }

    private static string Serialize(SearchConceptsResult result)
        => JsonSerializer.Serialize(result);

    private sealed class MultiGameFixture : IDisposable
    {
        public MultiGameFixture()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "boardai-rules-cache-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "content", "ontology"));
            File.WriteAllText(
                System.IO.Path.Combine(Path, "content", "ontology", "concepts.json"),
                """{"concepts":[]}""");

            var padding = new string('x', 24_000);
            foreach (var game in new[] { "a", "b", "c" })
            {
                var directory = System.IO.Path.Combine(Path, "content", "games", game);
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    System.IO.Path.Combine(directory, "concepts.json"),
                    $$"""
                    {"objects":[{"id":"widget_{{game}}","name":{"zh":"小装置{{game}}"},
                      "description":{"zh":"alpha"},"padding":"{{padding}}"}]}
                    """);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup must not mask assertions.
            }
        }
    }
}
