using BoardAI.Api.Services;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BoardAI.Api.Tests;

public sealed class EmbeddingProfileTests
{
    [Fact]
    public void ClsUsesOnlyTheFirstTokenAndNormalizesIt()
    {
        var hidden = new DenseTensor<float>(new[] { 1, 3, 2 });
        hidden[0, 0, 0] = 3; hidden[0, 0, 1] = 4;
        hidden[0, 1, 0] = -100; hidden[0, 1, 1] = 100;
        hidden[0, 2, 0] = 999;
        var mask = new DenseTensor<long>(new long[] { 1, 1, 0 }, new[] { 1, 3 });
        var cls = EmbeddingService.PoolAndNormalize(hidden, mask, "cls");
        Assert.Equal(0.6f, cls[0], 5);
        Assert.Equal(0.8f, cls[1], 5);
        Assert.NotEqual(cls, EmbeddingService.PoolAndNormalize(hidden, mask, "mean"));
    }

    [Fact]
    public void LegacyMeanIgnoresPaddingAndNormalizesValidTokens()
    {
        var hidden = new DenseTensor<float>(new[] { 1, 3, 2 });
        hidden[0, 0, 0] = 2; hidden[0, 1, 1] = 2; hidden[0, 2, 0] = 999;
        var mask = new DenseTensor<long>(new long[] { 1, 1, 0 }, new[] { 1, 3 });
        var mean = EmbeddingService.PoolAndNormalize(hidden, mask, "mean");
        Assert.Equal(MathF.Sqrt(0.5f), mean[0], 5);
        Assert.Equal(MathF.Sqrt(0.5f), mean[1], 5);
    }

    [Theory]
    [InlineData("cls")]
    [InlineData("mean")]
    public void NoValidTokensReturnsZeroWithoutNaN(string pooling)
    {
        Assert.Equal(new float[] { 0, 0 }, EmbeddingService.PoolAndNormalize(
            new DenseTensor<float>(new[] { 1, 2, 2 }), new DenseTensor<long>(new[] { 1, 2 }), pooling));
    }

    [Fact]
    public void DifferentPoolingCannotReuseAnIndexVersion()
    {
        var mean = EmbeddingProfile.ModelId("model", "mean");
        var cls = EmbeddingProfile.ModelId("model", "cls");
        Assert.Equal("model", mean);
        Assert.Equal("model/cls-v1", cls);
        Assert.NotEqual(IndexContract.ComputeIndexVersion("test", mean, 768, Array.Empty<ConceptIndexItem>()),
            IndexContract.ComputeIndexVersion("test", cls, 768, Array.Empty<ConceptIndexItem>()));
    }

    [Theory]
    [InlineData(null, "mean", true)]
    [InlineData(null, "cls", false)]
    [InlineData("model", "cls", false)]
    [InlineData("model/cls-v1", "mean", false)]
    [InlineData("model/cls-v1", "cls", true)]
    public void ProfileGuardRejectsMixingCoordinates(string? stored, string pooling, bool expected)
        => Assert.Equal(expected, EmbeddingProfile.IsCompatible(stored, "model", pooling));

    [Fact]
    public void UnknownPoolingFailsBeforeModelOrIndexLoading()
        => Assert.Throws<ArgumentException>(() => EmbeddingProfile.NormalizePooling("typo"));
}
