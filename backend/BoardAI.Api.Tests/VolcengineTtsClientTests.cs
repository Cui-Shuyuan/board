using System.Text;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class VolcengineTtsClientTests
{
    [Fact]
    public void StandardTtsChunking_KeepsEveryChunkUnderV1TextLimit()
    {
        var text = string.Join("。", Enumerable.Repeat(
            "购买卡牌后需要补充市场中的卡牌并检查贵族是否满足条件", 40)) + "。";

        var chunks = VolcengineTtsClient.SplitTextChunks(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk =>
        {
            Assert.False(string.IsNullOrWhiteSpace(chunk));
            Assert.True(Encoding.UTF8.GetByteCount(chunk) <= 900);
        });
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void StandardTtsChunking_LeavesShortTextAsSingleRequest()
    {
        const string text = "贵族怎么获得？";
        var chunks = VolcengineTtsClient.SplitTextChunks(text);
        Assert.Single(chunks);
        Assert.Equal(text, chunks[0]);
    }
}

public sealed class VolcengineTtsProtocolTests
{
    [Fact]
    public void V3EventFrame_RoundTripsEventSessionAndPayload()
    {
        var payload = Encoding.UTF8.GetBytes("{\"event\":100}");
        var frame = VolcengineTtsClient.BuildV3ClientEvent(
            eventId: 100,
            sessionId: "session-123",
            payload: payload);

        var parsed = VolcengineTtsClient.ParseV3Message(frame);

        Assert.Equal(100, parsed.Event);
        Assert.Equal("session-123", parsed.SessionId);
        Assert.Equal(payload, parsed.Payload);
    }
}
