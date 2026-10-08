using System.Text.Json;
using BoardAI.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoardAI.Api.Tests;

public sealed class CatalogControllerTests : IDisposable
{
    private readonly string _root;

    public CatalogControllerTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "boardai-catalog-controller-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "content", "catalog"));
        Directory.CreateDirectory(Path.Combine(_root, "content", "manifests"));
        Directory.CreateDirectory(Path.Combine(_root, "content", "games"));
    }

    [Fact]
    public void GetGames_RulesOnlyEntry_DoesNotExposeTutorialDownloadMetadata()
    {
        WriteCatalog("rulesonly", """
        {
          "id": "rulesonly",
          "released": true,
          "name_zh": "规则游戏",
          "name_en": "Rules Only",
          "aliases": [],
          "search_keys": [],
          "min_players": 2,
          "max_players": 4,
          "rules_ready": true,
          "tutorial_ready": false,
          "tutorial_tracks": []
        }
        """);
        WriteManifest("rulesonly", "deadbeef", fileCount: 1, fileSize: 4096);
        var controller = CreateController();

        var game = ReadSingleGame(controller);

        Assert.True(game.GetProperty("rules_ready").GetBoolean());
        Assert.False(game.GetProperty("tutorial_ready").GetBoolean());
        Assert.Empty(game.GetProperty("tutorial_tracks").EnumerateArray());
        Assert.False(
            game.TryGetProperty("content_version", out var version) &&
            version.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public void GetGames_LegacyTutorialTrackEntry_InfersTutorialReadyAndExposesContent()
    {
        WriteCatalog("legacy", """
        {
          "id": "legacy",
          "released": true,
          "name_zh": "旧目录",
          "name_en": "Legacy",
          "aliases": [],
          "search_keys": [],
          "min_players": 2,
          "max_players": 4,
          "tutorial_track": "full"
        }
        """);
        WriteConcepts("legacy");
        WriteManifest("legacy", "deadbeef", fileCount: 1, fileSize: 4096);
        var controller = CreateController();

        var game = ReadSingleGame(controller);

        Assert.True(game.GetProperty("rules_ready").GetBoolean());
        Assert.True(game.GetProperty("tutorial_ready").GetBoolean());
        Assert.Equal("full", game.GetProperty("tutorial_tracks")[0].GetString());
        Assert.Equal("deadbeef", game.GetProperty("content_version").GetString());
        Assert.Equal(1, game.GetProperty("content_file_count").GetInt32());
        Assert.Equal(4096, game.GetProperty("content_size_bytes").GetInt64());
    }

    [Fact]
    public void GetGames_OmittedRulesReadyWithoutConceptsFile_DefaultsToFalse()
    {
        WriteCatalog("notready", """
        {
          "id": "notready",
          "released": true,
          "name_zh": "未就绪",
          "name_en": "Not Ready",
          "aliases": [],
          "search_keys": [],
          "min_players": 2,
          "max_players": 4
        }
        """);
        var controller = CreateController();

        var game = ReadSingleGame(controller);

        Assert.False(game.GetProperty("rules_ready").GetBoolean());
        Assert.False(game.GetProperty("tutorial_ready").GetBoolean());
    }

    [Fact]
    public void GetGames_OmittedRulesReadyWithConceptsFile_InfersTrue()
    {
        WriteCatalog("ready", """
        {
          "id": "ready",
          "released": true,
          "name_zh": "规则就绪",
          "name_en": "Ready",
          "aliases": [],
          "search_keys": [],
          "min_players": 2,
          "max_players": 4,
          "rules_ready": null
        }
        """);
        WriteConcepts("ready");
        var controller = CreateController();

        var game = ReadSingleGame(controller);

        Assert.True(game.GetProperty("rules_ready").GetBoolean());
        Assert.False(game.GetProperty("tutorial_ready").GetBoolean());
    }

    private JsonElement ReadSingleGame(CatalogController controller)
    {
        var result = Assert.IsType<OkObjectResult>(controller.GetGames());
        var json = JsonSerializer.Serialize(result.Value);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Single().Clone();
    }

    private CatalogController CreateController()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Rules:BasePath"] = _root
            })
            .Build();
        return new CatalogController(configuration, NullLogger<CatalogController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private void WriteConcepts(string game)
    {
        var gameDirectory = Path.Combine(_root, "content", "games", game);
        Directory.CreateDirectory(gameDirectory);
        File.WriteAllText(
            Path.Combine(gameDirectory, "concepts.json"),
            """{"game":"...","concepts":[]}""");
    }

    private void WriteCatalog(string game, string json)
    {
        File.WriteAllText(
            Path.Combine(_root, "content", "catalog", $"{game}.json"),
            json);
    }

    private void WriteManifest(string game, string version, int fileCount, long fileSize)
    {
        var files = Enumerable.Range(0, fileCount)
            .Select(index => new
            {
                path = $"file-{index}.bin",
                size = fileSize,
                sha256 = new string('a', 64),
                url = $"/api/content/games/{game}/files/{version}/file-{index}.bin"
            })
            .ToArray();
        var manifest = new
        {
            schema = "board-content/v1",
            game,
            version,
            files
        };
        File.WriteAllText(
            Path.Combine(_root, "content", "manifests", $"{game}.json"),
            JsonSerializer.Serialize(manifest));
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
            // Cleanup failure must not hide assertion results.
        }
    }
}
