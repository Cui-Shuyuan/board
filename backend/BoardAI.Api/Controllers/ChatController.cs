using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BoardAI.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly ChatOrchestratorService _orchestrator;

    public ChatController(ChatOrchestratorService orchestrator)
    {
        _orchestrator = orchestrator;
    }

    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Post([FromBody] ChatRequest request)
    {
        try
        {
            var reply = await _orchestrator.ProcessAsync(request.GameId, request.Messages);
            return Ok(new ChatResponse { Reply = reply });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Chat failed: {ex.Message}" });
        }
    }
}
