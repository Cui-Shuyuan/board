using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BoardAI.Api.Services;

/// <summary>
/// Bounded asynchronous admission for CPU-bound query embedding inference.
///
/// The caller waits asynchronously for a concurrency slot. Only the first
/// <see cref="MaxConcurrent"/> callers get to run inference; at most
/// <see cref="MaxPending"/> additional callers may wait. With MaxPending == 0
/// there is no wait queue: a free slot still runs immediately, while a busy
/// slot fails with <see cref="EmbeddingQueueFullException"/>. Once a waiting caller
/// is cancelled it never starts inference. Cancellation cannot stop a
/// synchronous ONNX call that has already started.
/// </summary>
internal sealed class BoundedEmbeddingScheduler : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim? _pendingSlots;
    private readonly ILogger? _logger;
    private readonly int _maxConcurrent;
    private readonly int _maxPending;
    private int _active;
    private bool _disposed;

    public BoundedEmbeddingScheduler(
        int maxConcurrent,
        int maxPending,
        ILogger? logger = null)
    {
        if (maxConcurrent < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
        if (maxPending < 0)
            throw new ArgumentOutOfRangeException(nameof(maxPending));

        _maxConcurrent = maxConcurrent;
        _maxPending = maxPending;
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        // A zero-sized SemaphoreSlim is invalid. maxPending == 0 means
        // "execute immediately when a slot is free, otherwise fail", so no
        // pending semaphore is needed in that mode.
        _pendingSlots = maxPending > 0
            ? new SemaphoreSlim(maxPending, maxPending)
            : null;
        _logger = logger;
    }

    public int ActiveCount => Volatile.Read(ref _active);

    public async Task<T> RunAsync<T>(
        Func<T> inference,
        int inputLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inference);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var queueStarted = Stopwatch.StartNew();

        // Fast path: use an execution slot immediately without consuming a
        // queue position. This is required for maxPending == 0, where calls
        // must still run whenever capacity is available.
        var slotAcquired = _slots.Wait(0);
        if (!slotAcquired)
        {
            var pendingSlots = _pendingSlots;
            if (pendingSlots is null || !pendingSlots.Wait(0))
            {
                throw new EmbeddingQueueFullException(_maxPending, _maxConcurrent);
            }

            try
            {
                // Only calls that actually need to wait consume the bounded
                // pending-queue capacity.
                await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                pendingSlots.Release();
                throw;
            }

            pendingSlots.Release();
        }

        queueStarted.Stop();

        var active = 0;
        var inferenceStarted = new Stopwatch();
        try
        {
            // Re-check after admission: if every waiter cancelled while this
            // task was queued, it must not start ONNX work just because a slot
            // happened to become available.
            cancellationToken.ThrowIfCancellationRequested();

            active = Interlocked.Increment(ref _active);
            inferenceStarted.Start();

            // ONNX Runtime's Run is synchronous CPU work. Run it off the
            // request thread only after bounded admission, and do not pass the
            // request token as Task.Run's cancellation token: once started the
            // native inference cannot be cancelled.
            var result = await Task.Run(inference).ConfigureAwait(false);
            inferenceStarted.Stop();
            _logger?.LogInformation(
                "Embedding inference completed: chars={Chars}, queue_ms={QueueMs:F1}, inference_ms={InferenceMs:F1}, max_concurrent={MaxConcurrent}, active={Active}",
                inputLength,
                queueStarted.Elapsed.TotalMilliseconds,
                inferenceStarted.Elapsed.TotalMilliseconds,
                _maxConcurrent,
                active);
            return result;
        }
        finally
        {
            if (active > 0)
                Interlocked.Decrement(ref _active);
            _slots.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _slots.Dispose();
        _pendingSlots?.Dispose();
    }
}

/// <summary>
/// Raised when every bounded embedding queue slot is occupied. Callers should
/// surface/retry this explicitly; it must never be converted into a made-up
/// vector or a silent drop.
/// </summary>
public sealed class EmbeddingQueueFullException : InvalidOperationException
{
    public EmbeddingQueueFullException(int maxPending, int maxConcurrent)
        : base(
            $"Embedding inference queue is full (pending limit {maxPending}, max concurrent {maxConcurrent}).")
    {
        MaxPending = maxPending;
        MaxConcurrent = maxConcurrent;
    }

    public int MaxPending { get; }
    public int MaxConcurrent { get; }
}
