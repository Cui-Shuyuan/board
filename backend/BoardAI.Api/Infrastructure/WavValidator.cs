using System.Buffers.Binary;
using System.Text;
using BoardAI.Api.Services;

namespace BoardAI.Api.Infrastructure;

public sealed record WavInfo(
    int Channels,
    int SampleRate,
    int BitsPerSample,
    int ByteRate,
    int DataSize,
    double DurationSeconds);

/// <summary>
/// Minimal RIFF/WAVE header validation.  It intentionally does not decode
/// audio or inspect every chunk; it just ensures the bytes are a plausible
/// PCM WAV and enforces the one-shot duration/size limits before Python sees
/// the file.
/// </summary>
public static class WavValidator
{
    private static readonly byte[] Riff = Encoding.ASCII.GetBytes("RIFF");
    private static readonly byte[] Wave = Encoding.ASCII.GetBytes("WAVE");
    private static readonly byte[] Fmt = Encoding.ASCII.GetBytes("fmt ");
    private static readonly byte[] Data = Encoding.ASCII.GetBytes("data");

    public static WavInfo Validate(byte[] bytes)
    {
        if (bytes.Length < 44)
            throw new VoiceServiceException("WAV 文件不完整", 400);

        if (!bytes.AsSpan(0, 4).SequenceEqual(Riff) ||
            !bytes.AsSpan(8, 4).SequenceEqual(Wave))
            throw new VoiceServiceException("不是有效的 WAV 文件", 400);

        int? channels = null;
        int? sampleRate = null;
        int? bitsPerSample = null;
        int? byteRate = null;
        int? dataSize = null;

        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var chunkId = bytes.AsSpan(offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkSize < 0)
                throw new VoiceServiceException("WAV chunk 长度无效", 400);

            var dataOffset = offset + 8;
            if (dataOffset > bytes.Length || chunkSize > bytes.Length - dataOffset)
                throw new VoiceServiceException("WAV chunk 越过文件边界", 400);

            if (chunkId.SequenceEqual(Fmt))
            {
                if (chunkSize < 16)
                    throw new VoiceServiceException("WAV fmt chunk 过短", 400);

                var fmt = bytes.AsSpan(dataOffset, 16);
                var audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmt[0..2]);
                if (audioFormat != 1)
                    throw new VoiceServiceException("仅支持 PCM WAV", 400);

                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..4]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..8]);
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[8..12]);
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..16]);
            }
            else if (chunkId.SequenceEqual(Data))
            {
                dataSize = chunkSize;
            }

            // RIFF chunks are word-aligned.
            offset = dataOffset + chunkSize + (chunkSize & 1);
        }

        if (channels is null || channels.Value <= 0 ||
            sampleRate is null || sampleRate.Value <= 0 ||
            bitsPerSample is null || bitsPerSample.Value <= 0 ||
            byteRate is null || byteRate.Value <= 0 ||
            dataSize is null || dataSize.Value <= 0)
        {
            throw new VoiceServiceException("WAV 缺少有效的 fmt/data 信息", 400);
        }

        var duration = dataSize.Value / (double)byteRate.Value;
        if (duration > 60.5)
            throw new VoiceServiceException("音频时长不能超过 60 秒", 400);

        return new WavInfo(
            channels.Value,
            sampleRate.Value,
            bitsPerSample.Value,
            byteRate.Value,
            dataSize.Value,
            duration);
    }
}
