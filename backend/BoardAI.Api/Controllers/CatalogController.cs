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

        [JsonPropertyName("tutorial_track")]
        public string TutorialTrack { get; set; } = "full";
    }
}
