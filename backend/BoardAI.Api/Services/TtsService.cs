using System.Globalization;
using System.Text;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public sealed class TtsService
{
    private const int MaxTextLength = 500;

    private readonly VoiceProcessRunner _runner;
    private readonly VoiceOptions _options;

    public TtsService(VoiceProcessRunner runner, IOptions<VoiceOptions> options)
    {
        _runner = runner;
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
            ? "zh_female_vv_uranus_bigtts"
            : voice.Trim();
        var normalizedSpeed = speed ?? 1.0;
        if (normalizedSpeed is < 0.5 or > 2.0)
            throw new VoiceServiceException("speed 必须在 0.5 到 2.0 之间", 400);

        var tempTextPath = Path.Combine(
            Path.GetTempPath(),
            $"boardai-tts-{Guid.NewGuid():N}.txt");
        var tempAudioPath = Path.Combine(
            Path.GetTempPath(),
            $"boardai-tts-{Guid.NewGuid():N}.mp3");

        try
        {
            await File.WriteAllTextAsync(
                tempTextPath,
                normalizedText,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            await _runner.RunJsonAsync(
                _options.TtsScript,
                new[]
                {
                    "--text-file", tempTextPath,
                    "--out-file", tempAudioPath,
                    "--voice", normalizedVoice,
                    "--speed", normalizedSpeed.ToString("0.###", CultureInfo.InvariantCulture),
                },
                _options.TtsTimeoutSeconds,
                "TTS",
                cancellationToken);

            if (!File.Exists(tempAudioPath))
                throw new VoiceServiceException("TTS 没有生成 mp3 文件", 502);

            var bytes = await File.ReadAllBytesAsync(tempAudioPath, cancellationToken);
            if (bytes.Length == 0)
                throw new VoiceServiceException("TTS 生成的 mp3 为空", 502);

            return bytes;
        }
        finally
        {
            TryDelete(tempTextPath);
            TryDelete(tempAudioPath);
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
