using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public sealed class TtsService
{
    private const int MaxTextLength = 500;

    private readonly VolcengineTtsClient _client;
    private readonly VoiceOptions _options;

    public TtsService(VolcengineTtsClient client, IOptions<VoiceOptions> options)
    {
        _client = client;
        _options = options.Value;
    }

    public async Task<byte[]> SynthesizeAsync(
        string? text,
        string? voice,
        double? speed,
        CancellationToken cancellationToken)
    {
        var normalizedText = text?.Trim() ?? string.Empty;
        if (normalizedText.Length == 0)
            throw new VoiceServiceException("text 不能为空", 400);
        if (normalizedText.Length > MaxTextLength)
            throw new VoiceServiceException($"text 不能超过 {MaxTextLength} 字", 400);

        var normalizedVoice = string.IsNullOrWhiteSpace(voice)
            ? _options.DefaultTtsVoice
            : voice.Trim();
        var normalizedSpeed = speed ?? 1.0;
        if (normalizedSpeed is < 0.5 or > 2.0)
            throw new VoiceServiceException("speed 必须在 0.5 到 2.0 之间", 400);

        // Complete audio, in memory: no bridge temp text/MP3 files and no
        // streaming response to the Android client.
        return await _client.SynthesizeAsync(
            normalizedText,
            normalizedVoice,
            normalizedSpeed,
            cancellationToken);
    }
}
