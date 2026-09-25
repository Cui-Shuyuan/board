using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace BoardAI.Api.Controllers;

/// <summary>
/// Read-only content endpoints for the Android local content repository.
/// The controller deliberately bypasses Qdrant/embedding services: content
/// synchronisation is a plain file-delivery concern.
/// </summary>
[ApiController]
[Route("api/content/games/{game}")]
public class ContentController : ControllerBase
{
    private static readonly Regex SafeGameId = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly FileExtensionContentTypeProvider MimeTypes = new();

    private readonly string _contentRoot;

    public ContentController(IConfiguration configuration, IWebHostEnvironment environment)
    {
        // Keep the same root resolution as Program.cs: Rules:BasePath is the
        // repository root in normal local development.  Falling back to the
        // ASP.NET content root lets a self-contained publish work too.
        var configuredBase = configuration.GetValue<string>("Rules:BasePath");
        _contentRoot = string.IsNullOrWhiteSpace(configuredBase)
            ? environment.ContentRootPath
            : configuredBase;
    }

    [HttpGet("manifest")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetManifest([FromRoute] string game)
    {
        if (!TryValidateGame(game, out var gameError))
            return BadRequest(new { message = gameError });

        var manifestPath = Path.Combine(_contentRoot, "content", "manifests", $"{game}.json");
        if (!System.IO.File.Exists(manifestPath))
        {
            return NotFound(new
            {
                message = $"Content manifest for game '{game}' was not found. " +
                          $"Run: python3 tools/content/build_content_manifest.py --game {game}"
            });
        }

        // Read fresh file metadata on every request.  No in-memory cache means a
        // regenerated manifest is picked up immediately without restarting API.
        var info = new FileInfo(manifestPath);
        var etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "no-cache, must-revalidate";

        if (Request.Headers.IfNoneMatch.Count > 0 &&
            Request.Headers.IfNoneMatch.ToString().Split(',').Any(tag =>
                string.Equals(tag.Trim(), etag, StringComparison.Ordinal) || tag.Trim() == "*"))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return PhysicalFile(
            manifestPath,
            "application/json; charset=utf-8",
            enableRangeProcessing: true);
    }

    [HttpGet("files/{**filePath}")]
    public IActionResult GetFile([FromRoute] string game, [FromRoute] string? filePath)
    {
        if (!TryValidateGame(game, out var gameError))
            return BadRequest(new { message = gameError });

        if (string.IsNullOrWhiteSpace(filePath))
            return BadRequest(new { message = "file path is required" });

        if (!TryResolveGameFile(game, filePath!, out var fullPath, out var pathError))
            return BadRequest(new { message = pathError });

        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { message = $"Content file was not found: {filePath}" });

        return PhysicalFile(
            fullPath,
            ResolveContentType(fullPath),
            enableRangeProcessing: true);
    }

    private bool TryValidateGame(string? game, out string error)
    {
        if (string.IsNullOrWhiteSpace(game) || !SafeGameId.IsMatch(game))
        {
            error = "invalid game id; only letters, digits, '_' and '-' are allowed";
            return false;
        }

        error = "";
        return true;
    }

    private bool TryResolveGameFile(
        string game,
        string filePath,
        out string fullPath,
        out string error)
    {
        fullPath = "";
        error = "";

        // ASP.NET already URL-decodes route values once.  Reject decoded
        // traversal markers here, then do a second line of defence with a
        // canonical-prefix check below.
        var normalized = filePath.Replace('\\', '/');
        if (normalized.IndexOf('\0') >= 0)
        {
            error = "invalid file path";
            return false;
        }

        // Some proxies/routers leave encoded separators or dots intact.  Treat
        // any traversal-looking sequence as invalid instead of silently trying
        // to open a literal file with that name.
        if (normalized.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            error = "path traversal is not allowed";
            return false;
        }

        if (Path.IsPathRooted(filePath) || normalized.StartsWith('/'))
        {
            error = "absolute file paths are not allowed";
            return false;
        }

        var segments = normalized.Split('/');
        if (segments.Length == 0)
        {
            error = "invalid file path";
            return false;
        }

        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment) || segment == "." || segment == "..")
            {
                error = "path traversal is not allowed";
                return false;
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "invalid file path segment";
                return false;
            }
        }

        var gameRoot = Path.GetFullPath(Path.Combine(_contentRoot, "content", "games", game));
        var candidate = Path.GetFullPath(Path.Combine(gameRoot, Path.Combine(segments)));
        var rootPrefix = gameRoot.EndsWith(Path.DirectorySeparatorChar)
            ? gameRoot
            : gameRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "resolved path escapes the game content directory";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static string ResolveContentType(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return "application/json; charset=utf-8";
        if (extension.Equals(".lrc", StringComparison.OrdinalIgnoreCase))
            return "text/plain; charset=utf-8";
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            return "text/markdown; charset=utf-8";
        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return "text/plain; charset=utf-8";

        if (MimeTypes.TryGetContentType(path, out var contentType))
            return contentType;

        return "application/octet-stream";
    }
}
