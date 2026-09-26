using BoardAI.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BoardAI.Api.Controllers;

[ApiController]
[Route("api/asr")]
public class AsrController : ControllerBase
{
    private const int MaxAudioBytes = 10 * 1024 * 1024;

    private readonly AsrService _asrService;

    public AsrController(AsrService asrService)
    {
        _asrService = asrService;
    }

    [HttpPost("once")]
    [RequestSizeLimit(MaxAudioBytes)]
    public async Task<IActionResult> Once(CancellationToken cancellationToken)
    {
        if (Request.ContentType is null ||
            !Request.ContentType.Contains("audio/wav", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Content-Type 必须是 audio/wav" });
        }

        if (Request.ContentLength is > MaxAudioBytes)
            return StatusCode(413, new { error = "音频文件不能超过 10MB" });

        try
        {
            await using var memory = new MemoryStream();
            await Request.Body.CopyToAsync(memory, cancellationToken);

            var bytes = memory.ToArray();
            if (bytes.Length > MaxAudioBytes)
                return StatusCode(413, new { error = "音频文件不能超过 10MB" });

            var result = await _asrService.TranscribeAsync(bytes, cancellationToken);
            return Ok(new { text = result.Text, request_id = result.RequestId });
        }
        catch (VoiceServiceException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(408, new { error = "ASR 请求已取消或超时" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"ASR failed: {ex.Message}" });
        }
    }
}
