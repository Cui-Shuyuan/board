using System.Text.Json;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public sealed record AsrOnceResult(string Text, string RequestId);

public sealed class AsrService
{
    private readonly VoiceProcessRunner _runner;
    private readonly VoiceOptions _options;
    private readonly ILogger<AsrService> _logger;

    public AsrService(
        VoiceProcessRunner runner,
        IOptions<VoiceOptions> options,
        ILogger<AsrService> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AsrOnceResult> TranscribeAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        WavValidator.Validate(wavBytes);

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"boardai-asr-{Guid.NewGuid():N}.wav");

        try
        {
            await File.WriteAllBytesAsync(tempPath, wavBytes, cancellationToken);

            var stdout = await _runner.RunJsonAsync(
                _options.AsrScript,
                new[] { "--input", tempPath },
                _options.AsrTimeoutSeconds,
                "ASR",
                cancellationToken);

            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;

            var text = root.TryGetProperty("text", out var textElement)
                ? textElement.GetString()?.Trim()
                : null;
            var requestId = root.TryGetProperty("request_id", out var idElement)
                ? idElement.GetString()?.Trim()
                : null;

            if (string.IsNullOrWhiteSpace(text))
                throw new VoiceServiceException("ASR 未识别出文本", 422);

            _logger.LogInformation(
                "ASR recognized {CharCount} chars, request_id={RequestId}",
                text.Length,
                requestId ?? "-");

            return new AsrOnceResult(text, requestId ?? string.Empty);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // temp cleanup is best-effort
        }
    }
}
