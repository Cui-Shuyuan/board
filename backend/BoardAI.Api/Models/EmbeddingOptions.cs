namespace BoardAI.Api.Models;

/// <summary>
/// Local ONNX embedding runtime configuration.
///
/// ONNX Runtime exposes two independent thread pools: intra-op parallelism
/// inside one operator and inter-op parallelism between independent operators.
/// This deployment is request/latency oriented. Small representative-query
/// measurements on the target host showed a knee at six intra-op threads;
/// keeping at most two inferences in flight caps the expected ONNX worker
/// count while avoiding unbounded thread contention.
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>Worker threads ONNX Runtime may use inside one inference.</summary>
    public int IntraOpThreads { get; set; } = 6;

    /// <summary>Worker threads for independent graph operators in one inference.</summary>
    public int InterOpThreads { get; set; } = 1;

    /// <summary>Maximum number of query embeddings that may run at the same time.</summary>
    public int MaxConcurrentInference { get; set; } = 2;

    /// <summary>
    /// Maximum number of query embeddings allowed to wait for an inference
    /// slot. Zero means no queuing: run immediately when a slot is free,
    /// otherwise fail with an explicit queue-full error.
    /// </summary>
    public int MaxPendingInference { get; set; } = 16;

    public void Validate()
    {
        if (IntraOpThreads is < 1 or > 128)
            throw new ArgumentOutOfRangeException(
                nameof(IntraOpThreads), IntraOpThreads,
                "Embedding:IntraOpThreads must be between 1 and 128.");
        if (InterOpThreads is < 1 or > 128)
            throw new ArgumentOutOfRangeException(
                nameof(InterOpThreads), InterOpThreads,
                "Embedding:InterOpThreads must be between 1 and 128.");
        if (MaxConcurrentInference is < 1 or > 64)
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentInference), MaxConcurrentInference,
                "Embedding:MaxConcurrentInference must be between 1 and 64.");
        if (MaxPendingInference is < 0 or > 1024)
            throw new ArgumentOutOfRangeException(
                nameof(MaxPendingInference), MaxPendingInference,
                "Embedding:MaxPendingInference must be between 0 and 1024.");
    }
}
