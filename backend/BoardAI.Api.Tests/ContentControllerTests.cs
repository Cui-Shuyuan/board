using System.Text.Json;
using BoardAI.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace BoardAI.Api.Tests;

public sealed class ContentControllerTests : IDisposable
{
    private const string Game = "testgame";
    private const string Version = "deadbeef";
    private const string FilePath = "media/source.bin";

    private readonly string _root;
    private readonly string _sourceFile;
    private readonly string _releaseFile;

    public ContentControllerTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "boardai-content-controller-tests",
            Guid.NewGuid().ToString("N"));

        _sourceFile = Path.Combine(_root, "content", "games", Game, FilePath);
        _releaseFile = Path.Combine(_root, "content", "releases", Game, Version, FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_sourceFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_releaseFile)!);

        File.WriteAllBytes(_sourceFile, "mutable-source"u8.ToArray());
        File.WriteAllBytes(_releaseFile, "immutable-release"u8.ToArray());
        WriteManifest(Version);
    }

    [Fact]
    public void GetVersionedFile_CurrentVersion_ServesReleaseWithImmutableHeader()
    {
        var controller = CreateController();

        var result = controller.GetVersionedFile(Game, Version, FilePath);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(Path.GetFullPath(_releaseFile), file.FileName);
        Assert.Equal(
            "public, max-age=31536000, immutable",
            controller.Response.Headers.CacheControl.ToString());
        Assert.Equal(
            "immutable-release",
            File.ReadAllText(_releaseFile));
    }

    [Fact]
    public void GetVersionedFile_SourceChangesButReleaseDoesNot_StillServesOldBytes()
    {
        var controller = CreateController();
        var first = Assert.IsType<PhysicalFileResult>(
            controller.GetVersionedFile(Game, Version, FilePath));

        File.WriteAllBytes(_sourceFile, "changed-source"u8.ToArray());
        var second = Assert.IsType<PhysicalFileResult>(
            controller.GetVersionedFile(Game, Version, FilePath));

        Assert.Equal(Path.GetFullPath(_releaseFile), first.FileName);
        Assert.Equal(Path.GetFullPath(_releaseFile), second.FileName);
        Assert.Equal("immutable-release", File.ReadAllText(second.FileName));
        Assert.NotEqual("changed-source", File.ReadAllText(second.FileName));
    }

    [Fact]
    public void GetVersionedFile_VersionMismatch_ReturnsConflict()
    {
        var controller = CreateController();

        var result = controller.GetVersionedFile(Game, "aaaaaaaa", FilePath);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public void GetVersionedFile_ReleaseFileMissing_ReturnsNotFound()
    {
        var controller = CreateController();

        var result = controller.GetVersionedFile(Game, Version, "media/missing.bin");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
    }

    [Fact]
    public void GetVersionedFile_ReleaseDirectoryMissing_ReturnsNotFoundWithoutSourceFallback()
    {
        Directory.Delete(Path.Combine(_root, "content", "releases", Game, Version), recursive: true);
        var controller = CreateController();

        var result = controller.GetVersionedFile(Game, Version, FilePath);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotEqual(Path.GetFullPath(_sourceFile), (result as PhysicalFileResult)?.FileName);
    }

    private ContentController CreateController()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Rules:BasePath"] = _root
            })
            .Build();

        return new ContentController(configuration)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private void WriteManifest(string version)
    {
        var manifestPath = Path.Combine(_root, "content", "manifests", $"{Game}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var manifest = new
        {
            schema = "board-content/v1",
            game = Game,
            version,
            files = Array.Empty<object>()
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
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
