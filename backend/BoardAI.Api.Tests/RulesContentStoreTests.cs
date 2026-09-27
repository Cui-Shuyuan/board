using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesContentStoreTests : IDisposable
{
    private readonly string _tempDirectory;

    public RulesContentStoreTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "boardai-content-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void LoadGameConcepts_FileExists_ReturnsParsedDocument()
    {
        WriteJson(
            Path.Combine("content", "games", "testgame", "concepts.json"),
            """
            {
              "objects": [
                {
                  "id": "widget",
                  "name": { "zh": "blue widget" }
                }
              ]
            }
            """);
        using var store = new RulesContentStore(_tempDirectory);

        var document = store.LoadGameConcepts("testgame");

        Assert.NotNull(document);
        var widget = document.RootElement.GetProperty("objects")[0];
        Assert.Equal("widget", widget.GetProperty("id").GetString());
        Assert.Equal("blue widget", widget.GetProperty("name").GetProperty("zh").GetString());
    }

    [Fact]
    public void LoadGameConcepts_WhenFileDoesNotExist_ReturnsNull()
    {
        using var store = new RulesContentStore(_tempDirectory);

        var document = store.LoadGameConcepts("testgame");

        Assert.Null(document);
    }

    [Fact]
    public void OptionalFlowAndInstanceDocuments_WhenFilesDoNotExist_ReturnNull()
    {
        using var store = new RulesContentStore(_tempDirectory);

        Assert.Null(store.LoadOntologyFlow());
        Assert.Null(store.LoadGameFlow("testgame"));
        Assert.Null(store.LoadGameInstances("testgame"));
    }

    [Fact]
    public void LoadOntology_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        using var store = new RulesContentStore(_tempDirectory);

        Assert.Throws<FileNotFoundException>(() => { store.LoadOntology(); });
    }

    [Fact]
    public void DocumentChanged_OnRealChange_NotifiesOnce_OnCacheHitDoesNotNotify()
    {
        var changes = 0;
        using var store = new RulesContentStore(_tempDirectory, () => changes++);
        var relativePath = Path.Combine("content", "games", "testgame", "concepts.json");
        var path = WriteJson(relativePath, """{"value":1}""");

        store.LoadGameConcepts("testgame");
        Assert.Equal(0, changes);

        store.LoadGameConcepts("testgame");
        Assert.Equal(0, changes);

        var firstWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, """{"value":2}""");
        File.SetLastWriteTimeUtc(path, firstWriteTimeUtc.AddMinutes(1));

        store.LoadGameConcepts("testgame");
        Assert.Equal(1, changes);

        store.LoadGameConcepts("testgame");
        Assert.Equal(1, changes);
    }

    [Fact]
    public void GetGames_ReturnsOnlyDirectoriesIgnoringFiles()
    {
        var gamesDirectory = Path.Combine(_tempDirectory, "content", "games");
        Directory.CreateDirectory(Path.Combine(gamesDirectory, "alpha"));
        Directory.CreateDirectory(Path.Combine(gamesDirectory, "beta"));
        File.WriteAllText(Path.Combine(gamesDirectory, "readme.txt"), "not a game");

        using var store = new RulesContentStore(_tempDirectory);

        var games = store.GetGames();

        Assert.Equal(2, games.Count);
        Assert.Contains("alpha", games);
        Assert.Contains("beta", games);
        Assert.DoesNotContain("readme.txt", games);
    }

    [Fact]
    public void GetGames_WhenGamesDirectoryDoesNotExist_ReturnsEmpty()
    {
        using var store = new RulesContentStore(_tempDirectory);

        var games = store.GetGames();

        Assert.Empty(games);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // 测试清理失败不应掩盖真实断言结果。
        }
    }

    private string WriteJson(string relativePath, string json)
    {
        var path = Path.Combine(_tempDirectory, relativePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, json);
        return path;
    }
}
