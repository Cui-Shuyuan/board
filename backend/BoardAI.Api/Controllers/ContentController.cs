using System.Text.Json;
using System.Text.RegularExpressions;
using BoardAI.Api.Infrastructure;
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
    private static readonly Regex SafeVersion = new("^[0-9a-fA-F]{8,64}$", RegexOptions.Compiled);
    private static readonly FileExtensionContentTypeProvider MimeTypes = new();

    private readonly string _contentRoot;

    public ContentController(IConfiguration configuration)
    {
        // Use the exact same portable resolution as Program.cs and
        // GameRulesService.  Rules:BasePath remains an optional override.
        _contentRoot = BoardPaths.ResolveBasePath(
            configuration.GetValue<string>("Rules:BasePath"));
    }

    [HttpGet("manifest")]
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
        var etag = BuildEtag(info);
        ApplyRevalidatedCacheHeaders(etag);

        if (IsNotModified(etag))
            return StatusCode(StatusCodes.Status304NotModified);

        return PhysicalFile(
            manifestPath,
            "application/json; charset=utf-8",
            enableRangeProcessing: true);
    }

    /// <summary>
    /// Versioned content URL:
    ///   GET /api/content/games/{game}/files/{version}/{**filePath}
    ///
    /// The URL version must match the current manifest version.  If it does not,
    /// the client receives 409 Conflict and must fetch a fresh manifest before
    /// retrying.  A successful response is safe for long-lived immutable
    /// caching.
    /// </summary>
    [HttpGet("files/{version:regex(^[[0-9a-fA-F]]{{8,64}}$)}/{**filePath}")]
    public IActionResult GetVersionedFile(
        [FromRoute] string game,
        [FromRoute] string version,
        [FromRoute] string? filePath)
    {
        if (!TryValidateGame(game, out var gameError))
            return BadRequest(new { message = gameError });

        if (string.IsNullOrWhiteSpace(version) || !SafeVersion.IsMatch(version))
            return BadRequest(new { message = "invalid content version" });

        if (string.IsNullOrWhiteSpace(filePath))
            return BadRequest(new { message = "file path is required" });

        if (!TryReadManifestVersion(
                game,
                out var currentVersion,
                out var manifestError,
                out var manifestMissing))
        {
            return manifestMissing
                ? NotFound(new { message = manifestError })
                : StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new { message = manifestError });
        }

        if (!string.Equals(currentVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new
            {
                message = $"Content version '{version}' is no longer current for game " +
                          $"'{game}'. Reload the manifest and retry."
            });
        }

        if (!TryResolveGameFile(game, filePath!, out var fullPath, out var pathError))
            return BadRequest(new { message = pathError });

        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { message = $"Content file was not found: {filePath}" });

        var info = new FileInfo(fullPath);
        var etag = BuildEtag(info);
        ApplyImmutableCacheHeaders(etag);

        if (IsNotModified(etag))
            return StatusCode(StatusCodes.Status304NotModified);

        return PhysicalFile(
            fullPath,
            ResolveContentType(fullPath),
            enableRangeProcessing: true);
    }

    // Legacy compatibility route.  New clients should use the versioned
    // /files/{version}/{**filePath} route above.  Kept so old app builds keep
    // working; it intentionally uses revalidation instead of immutable caching
    // and can be removed once no supported client depends on it.
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

        var info = new FileInfo(fullPath);
        var etag = BuildEtag(info);
        ApplyRevalidatedCacheHeaders(etag);

        if (IsNotModified(etag))
            return StatusCode(StatusCodes.Status304NotModified);

        return PhysicalFile(
            fullPath,
            ResolveContentType(fullPath),
            enableRangeProcessing: true);
    }

    private static string BuildEtag(FileInfo info) =>
        $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";

    private void ApplyImmutableCacheHeaders(string etag)
    {
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    }

    private void ApplyRevalidatedCacheHeaders(string etag)
    {
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "no-cache, must-revalidate";
    }

    private bool IsNotModified(string etag)
    {
        if (Request.Headers.IfNoneMatch.Count == 0)
            return false;

        foreach (var rawValue in Request.Headers.IfNoneMatch)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                continue;

            foreach (var rawTag in rawValue.Split(','))
            {
                var tag = rawTag.Trim();
                if (tag == "*")
                    return true;

                // If-None-Match uses the weak comparison algorithm, so W/"..."
                // and "..." are equivalent for this endpoint's purpose.
                if (tag.StartsWith("W/", StringComparison.Ordinal))
                    tag = tag[2..].Trim();

                if (string.Equals(tag, etag, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private bool TryReadManifestVersion(
        string game,
        out string version,
        out string error,
        out bool manifestMissing)
    {
        version = "";
        error = "";
        manifestMissing = false;

        var manifestPath = Path.Combine(_contentRoot, "content", "manifests", $"{game}.json");
        if (!System.IO.File.Exists(manifestPath))
        {
            manifestMissing = true;
            error = $"Content manifest for game '{game}' was not found. " +
                    $"Run: python3 tools/content/build_content_manifest.py --game {game}";
            return false;
        }

        try
        {
            using var stream = System.IO.File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                error = $"Content manifest for game '{game}' has no valid version field.";
                return false;
            }

            var value = versionElement.GetString()?.Trim() ?? "";
            if (!SafeVersion.IsMatch(value))
            {
                error = $"Content manifest for game '{game}' has an invalid version: '{value}'.";
                return false;
            }

            version = value;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Content manifest for game '{game}' is not valid JSON: {ex.Message}";
            return false;
        }
        catch (IOException ex)
        {
            error = $"Content manifest for game '{game}' could not be read: {ex.Message}";
            return false;
        }
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
