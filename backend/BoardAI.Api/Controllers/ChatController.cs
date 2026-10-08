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
            var reply = await _orchestrator.ProcessAsync(
                request.GameId,
                request.Messages,
                request.Context,
                HttpContext.RequestAborted);
            return Ok(new ChatResponse { Reply = reply });
        }
        catch (OperationCanceledException)
        {
            // 客户端断开或请求预算到点时，保持 ASP.NET Core 的取消语义，不在控制器里包装成 500。
            throw;
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Chat failed: {ex.Message}" });
        }
    }
}
