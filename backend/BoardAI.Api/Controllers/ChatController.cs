using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BoardAI.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly ILLMService _llmService;

    public ChatController(ILLMService llmService)
    {
        _llmService = llmService;
    }

    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Post([FromBody] ChatRequest request)
    {
        try
        {
            var reply = await _llmService.ChatAsync(request.Message);
            return Ok(new ChatResponse { Reply = reply });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"LLM request failed: {ex.Message}" });
        }
    }
}
