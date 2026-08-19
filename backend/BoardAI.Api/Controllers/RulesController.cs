using System.Text.Json;
using BoardAI.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BoardAI.Api.Controllers;

[ApiController]
[Route("api/rules")]
public class RulesController : ControllerBase
{
    private readonly GameRulesService _rulesService;

    public RulesController(GameRulesService rulesService)
    {
        _rulesService = rulesService;
    }

    [HttpGet("games")]
    public IActionResult GetGames()
    {
        return Ok(_rulesService.GetGames());
    }

    [HttpGet("games/{game}/types")]
    public IActionResult GetTypes(string game)
    {
        return Ok(_rulesService.GetConceptTypes(game));
    }

    [HttpGet("games/{game}/concepts")]
    public IActionResult ListConcepts(string game, [FromQuery] string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return BadRequest(new { error = "type parameter is required" });
        }
        return Ok(_rulesService.ListConcepts(game, type));
    }

    [HttpGet("games/{game}/concepts/{id}")]
    public IActionResult GetConcept(string game, string id)
    {
        var concepts = _rulesService.GetConcepts(game, id);
        if (concepts.Count == 0)
        {
            return NotFound(new { error = $"Concept '{id}' not found" });
        }
        return Ok(concepts);
    }

    [HttpGet("games/{game}/actions/{actionId}/conditions")]
    public IActionResult GetActionConditions(string game, string actionId)
    {
        return Ok(_rulesService.GetActionConditions(game, actionId));
    }

    [HttpGet("games/{game}/search")]
    public async Task<IActionResult> Search(string game, [FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest(new { error = "q parameter is required" });
        }
        return Ok(await _rulesService.SearchConceptsAsync(game, q));
    }

    /// <summary>
    /// 同步指定游戏的向量索引（默认增量；full=true 时删除并重建 collection）。
    /// </summary>
    [HttpPost("admin/rebuild-index/{game}")]
    public async Task<IActionResult> RebuildIndex(string game, [FromQuery] bool full = false)
    {
        if (full)
        {
            await _rulesService.BuildEmbeddingIndexAsync(game);
            return Ok(new { game, mode = "full", message = $"Index rebuilt for '{game}'" });
        }

        await _rulesService.SyncEmbeddingIndexAsync(game);
        return Ok(new { game, mode = "incremental", message = $"Index synced for '{game}'" });
    }

    /// <summary>
    /// 同步全部游戏的向量索引（默认增量；full=true 时删除并重建 collection）。
    /// </summary>
    [HttpPost("admin/rebuild-all")]
    public async Task<IActionResult> RebuildAll([FromQuery] bool full = false)
    {
        var games = _rulesService.GetGames();
        foreach (var game in games)
        {
            if (full)
                await _rulesService.BuildEmbeddingIndexAsync(game);
            else
                await _rulesService.SyncEmbeddingIndexAsync(game);
        }
        return Ok(new { games, mode = full ? "full" : "incremental", message = $"Synced {games.Count} game(s)" });
    }
}
