using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BoardAI.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace BoardAI.Api.Controllers;

/// <summary>
/// Read-only game catalog endpoint used by the native Android home screen.
///
/// The catalog intentionally lives outside content/games/{game}/ and therefore
/// does not participate in the versioned content manifest.  Toggling a game's
/// released flag must never invalidate downloaded tutorial content.
/// </summary>
[ApiController]
[Route("api/catalog")]
public class CatalogController : ControllerBase
{
    private static readonly Regex SafeGameId = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex SafeVersion = new("^[0-9a-fA-F]{8,64}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _contentRoot;
    private readonly ILogger<CatalogController> _logger;

    public CatalogController(IConfiguration configuration, ILogger<CatalogController> logger)
    {
        _contentRoot = BoardPaths.ResolveBasePath(
            configuration.GetValue<string>("Rules:BasePath"));
        _logger = logger;
    }

    /// <summary>
    /// GET /api/catalog/games
    ///
    /// Returns every catalog entry, including released=false.  Filtering is a
    /// client-side build-type concern (debug sees all, release sees released
    /// only) so release builds can still refresh and keep the full local cache.
    /// </summary>
    [HttpGet("games")]
    [Produces("application/json")]
    public IActionResult GetGames()
    {
        Response.Headers.CacheControl = "no-cache";

        var catalogDirectory = Path.Combine(_contentRoot, "content", "catalog");
        if (!Directory.Exists(catalogDirectory))
            return Ok(Array.Empty<CatalogGame>());

        var games = new List<CatalogGame>();
        foreach (var file in Directory.EnumerateFiles(catalogDirectory, "*.json")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                var game = document.RootElement.Deserialize<CatalogGame>(JsonOptions);
                if (game == null || string.IsNullOrWhiteSpace(game.Id) || !SafeGameId.IsMatch(game.Id))
                {
                    _logger.LogWarning("Skipping invalid catalog entry: {CatalogFile}", file);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(game.NameZh))
                    game.NameZh = game.Id;

                game.Aliases ??= new List<string>();
                game.SearchKeys ??= new List<string>();
                NormalizeCapabilities(game);
                PopulateContentInfo(game);
                games.Add(game);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping malformed catalog entry: {CatalogFile}", file);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable catalog entry: {CatalogFile}", file);
            }
        }

        var ordered = games
            .OrderBy(game => game.NameZh, StringComparer.OrdinalIgnoreCase)
            .ThenBy(game => game.NameEn, StringComparer.OrdinalIgnoreCase)
            .ThenBy(game => game.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Ok(ordered);
    }

    /// <summary>
    /// Normalizes the rules/tutorial capability declaration.
    ///
    /// New catalog entries carry explicit rules_ready/tutorial_ready/
    /// tutorial_tracks.  Legacy entries that only have tutorial_track are
    /// treated as tutorial-ready so existing Splendor caches keep working.
    /// A rules-only game must not inherit tutorial_track="full".
    /// </summary>
    private void NormalizeCapabilities(CatalogGame game)
    {
        var rawTracks = new List<string>();
        if (game.TutorialTracks != null)
            rawTracks.AddRange(game.TutorialTracks);
        if (!string.IsNullOrWhiteSpace(game.TutorialTrack))
            rawTracks.Add(game.TutorialTrack);

        var tracks = rawTracks
            .Select(track => track.Trim())
            .Where(track => !string.IsNullOrWhiteSpace(track) && SafeGameId.IsMatch(track))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Explicit false wins.  Legacy entries without tutorial_ready infer
        // readiness from a non-empty tutorial_track.
        game.TutorialReady ??= tracks.Count > 0;
        if (game.TutorialReady != true)
        {
            game.TutorialReady = false;
            tracks.Clear();
        }
        else if (tracks.Count == 0)
        {
            game.TutorialReady = false;
        }

        // Do not default rules_ready to true: a catalog entry without runtime
        // rule data must not expose a rules-only QA entry.  Explicit catalog
        // declarations still win; omissions are inferred from the actual
        // content/games/{id}/concepts.json runtime file.
        game.RulesReady ??= HasRulePackage(game.Id);

        game.TutorialTracks = tracks;
        game.TutorialTrack = game.TutorialReady == true
            ? tracks.FirstOrDefault()
            : null;
    }

    private bool HasRulePackage(string gameId)
    {
        var conceptsPath = Path.Combine(
            _contentRoot, "content", "games", gameId, "concepts.json");
        try
        {
            var file = new FileInfo(conceptsPath);
            return file.Exists && file.Length > 0;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                ex,
                "Could not inspect rules content for catalog game {GameId}: {ConceptsPath}",
                gameId,
                conceptsPath);
            return false;
        }
    }

    /// <summary>
    /// Reads content/manifests/{gameId}.json on every request and exposes the
    /// version, total byte size and file count.  A missing/invalid manifest
    /// leaves the three fields null and never fails the whole catalog.
    ///
    /// Content metadata is only exposed for tutorial-capable packages; a
    /// rules-only game may have no runtime manifest at all, and adding an
    /// unrelated/empty manifest must not create a downloadable tutorial.
    /// </summary>
    private void PopulateContentInfo(CatalogGame game)
    {
        if (game.TutorialReady != true)
        {
            return;
        }

        var manifestPath = Path.Combine(
            _contentRoot, "content", "manifests", $"{game.Id}.json");

        if (!System.IO.File.Exists(manifestPath))
        {
            _logger.LogWarning(
                "Content manifest not found for catalog game {GameId}: {ManifestPath}",
                game.Id,
                manifestPath);
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(
                System.IO.File.ReadAllText(manifestPath));
            var root = document.RootElement;

            if (!root.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                _logger.LogWarning(
                    "Content manifest has no valid version for catalog game {GameId}: {ManifestPath}",
                    game.Id,
                    manifestPath);
                return;
            }

            var version = versionElement.GetString()?.Trim() ?? "";
            if (!SafeVersion.IsMatch(version))
            {
                _logger.LogWarning(
                    "Content manifest has invalid version '{Version}' for catalog game {GameId}: {ManifestPath}",
                    version,
                    game.Id,
                    manifestPath);
                return;
            }

            if (!root.TryGetProperty("files", out var filesElement) ||
                filesElement.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning(
                    "Content manifest has no valid files array for catalog game {GameId}: {ManifestPath}",
                    game.Id,
                    manifestPath);
                return;
            }

            long totalBytes = 0;
            var fileCount = filesElement.GetArrayLength();
            foreach (var fileElement in filesElement.EnumerateArray())
            {
                if (fileElement.ValueKind != JsonValueKind.Object ||
                    !fileElement.TryGetProperty("size", out var sizeElement) ||
                    sizeElement.ValueKind != JsonValueKind.Number ||
                    !sizeElement.TryGetInt64(out var size) ||
                    size < 0)
                {
                    _logger.LogWarning(
                        "Content manifest has an invalid file size for catalog game {GameId}: {ManifestPath}",
                        game.Id,
                        manifestPath);
                    return;
                }

                totalBytes = checked(totalBytes + size);
            }

            if (game.TutorialReady != true || fileCount == 0)
            {
                return;
            }

            game.ContentVersion = version.ToLowerInvariant();
            game.ContentSizeBytes = totalBytes;
            game.ContentFileCount = fileCount;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Content manifest is malformed for catalog game {GameId}: {ManifestPath}",
                game.Id,
                manifestPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(
                ex,
                "Content manifest could not be read for catalog game {GameId}: {ManifestPath}",
                game.Id,
                manifestPath);
        }
        catch (OverflowException ex)
        {
            _logger.LogWarning(
                ex,
                "Content manifest size overflow for catalog game {GameId}: {ManifestPath}",
                game.Id,
                manifestPath);
        }
    }

    private sealed class CatalogGame
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("released")]
        public bool Released { get; set; }

        [JsonPropertyName("name_zh")]
        public string NameZh { get; set; } = "";

        [JsonPropertyName("name_en")]
        public string NameEn { get; set; } = "";

        [JsonPropertyName("aliases")]
        public List<string>? Aliases { get; set; }

        [JsonPropertyName("search_keys")]
        public List<string>? SearchKeys { get; set; }

        [JsonPropertyName("min_players")]
        public int MinPlayers { get; set; }

        [JsonPropertyName("max_players")]
        public int MaxPlayers { get; set; }

        [JsonPropertyName("rules_ready")]
        public bool? RulesReady { get; set; }

        [JsonPropertyName("tutorial_ready")]
        public bool? TutorialReady { get; set; }

        [JsonPropertyName("tutorial_tracks")]
        public List<string>? TutorialTracks { get; set; }

        [JsonPropertyName("tutorial_track")]
        public string? TutorialTrack { get; set; }

        [JsonPropertyName("content_version")]
        public string? ContentVersion { get; set; }

        [JsonPropertyName("content_size_bytes")]
        public long? ContentSizeBytes { get; set; }

        [JsonPropertyName("content_file_count")]
        public int? ContentFileCount { get; set; }
    }
}
