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
    /// 直接执行一个 execute_plan，用于检索/实体解析评测。
    /// Body: { "question": "客人原话", "plan": { "queries": [{ "relation": "...", "entity": "..." }] } }
    /// </summary>
    [HttpPost("games/{game}/execute-plan")]
    public async Task<IActionResult> ExecutePlan(string game, [FromBody] JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("plan", out var plan) ||
            plan.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(new { error = "body.plan object is required" });
        }
        var question = body.TryGetProperty("question", out var qp) && qp.ValueKind == JsonValueKind.String
            ? qp.GetString() ?? ""
            : "";
        return Ok(await _rulesService.ExecutePlanAsync(game, plan, question));
    }

    /// <summary>
    /// 重建指定游戏的向量索引（改了规则文件后调用）。
    /// </summary>
    [HttpPost("admin/rebuild-index/{game}")]
    public async Task<IActionResult> RebuildIndex(string game)
    {
        await _rulesService.BuildEmbeddingIndexAsync(game);
        return Ok(new { message = $"Index rebuilt for '{game}'" });
    }

    /// <summary>
    /// 重建全部游戏的向量索引。
    /// </summary>
    [HttpPost("admin/rebuild-all")]
    public async Task<IActionResult> RebuildAll()
    {
        var games = _rulesService.GetGames();
        foreach (var game in games)
        {
            await _rulesService.BuildEmbeddingIndexAsync(game);
        }
        return Ok(new { rebuilt = games, message = $"Rebuilt {games.Count} game(s)" });
    }
}
