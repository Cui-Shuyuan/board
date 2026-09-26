using BoardAI.Api.Models;
using BoardAI.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BoardAI.Api.Controllers;

[ApiController]
[Route("api/tts")]
public class TtsController : ControllerBase
{
    private readonly TtsService _ttsService;

    public TtsController(TtsService ttsService)
    {
        _ttsService = ttsService;
    }

    [HttpPost]
    public async Task<IActionResult> Post(
        [FromBody] TtsRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new { error = "请求体不能为空" });

        try
        {
            var bytes = await _ttsService.SynthesizeAsync(
                request.Text,
                request.Voice,
                request.Speed,
                cancellationToken);

            return File(bytes, "audio/mpeg");
        }
        catch (VoiceServiceException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(408, new { error = "TTS 请求已取消或超时" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"TTS failed: {ex.Message}" });
        }
    }
}
