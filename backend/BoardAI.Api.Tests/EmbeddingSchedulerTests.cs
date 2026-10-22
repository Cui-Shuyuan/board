using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class EmbeddingSchedulerTests
{
    [Fact]
    public async Task PendingQueueIsBounded_AndCancellationPreventsQueuedWorkFromStarting()
    {
        using var scheduler = new BoundedEmbeddingScheduler(
            maxConcurrent: 1,
            maxPending: 1);

        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = scheduler.RunAsync(
            () =>
            {
                firstStarted.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
                return 1;
            },
            inputLength: 1,
            CancellationToken.None);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var queuedWorkStarted = 0;
        using var queuedCancellation = new CancellationTokenSource();
        var second = scheduler.RunAsync(
            () =>
            {
                Interlocked.Increment(ref queuedWorkStarted);
                return 2;
            },
            inputLength: 2,
            queuedCancellation.Token);

        // Give the second call time to reserve the only pending slot before
        // the third call attempts admission.
        await Task.Delay(50);

        await Assert.ThrowsAsync<EmbeddingQueueFullException>(() =>
            scheduler.RunAsync(
                () => 3,
                inputLength: 3,
                CancellationToken.None));

        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        releaseFirst.TrySetResult();
        Assert.Equal(1, await first);
        Assert.Equal(0, Volatile.Read(ref queuedWorkStarted));
    }

    [Fact]
    public async Task RunningInferenceIsNotAbortedByWaiterCancellation_AndSlotIsReleased()
    {
        using var scheduler = new BoundedEmbeddingScheduler(
            maxConcurrent: 1,
            maxPending: 1);

        var inferenceStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFinish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var first = scheduler.RunAsync(
            () =>
            {
                inferenceStarted.TrySetResult();
                allowFinish.Task.GetAwaiter().GetResult();
                return 42;
            },
            inputLength: 1,
            cancellation.Token);

        await inferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        allowFinish.TrySetResult();

        // The started synchronous inference cannot be cancelled, but it must
        // complete and release a concurrency slot for the next queued query.
        Assert.Equal(42, await first);
        Assert.Equal(7, await scheduler.RunAsync(
            () => 7,
            inputLength: 1,
            CancellationToken.None));
    }
    [Fact]
    public async Task ZeroPendingQueue_AllowsImmediateExecutionAndRejectsOnlyWhenBusy()
    {
        using var scheduler = new BoundedEmbeddingScheduler(
            maxConcurrent: 1,
            maxPending: 0);

        Assert.Equal(1, await scheduler.RunAsync(
            () => 1,
            inputLength: 1,
            CancellationToken.None));

        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = scheduler.RunAsync(
            () =>
            {
                started.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return 2;
            },
            inputLength: 2,
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<EmbeddingQueueFullException>(() =>
            scheduler.RunAsync(
                () => 3,
                inputLength: 3,
                CancellationToken.None));

        release.TrySetResult();
        Assert.Equal(2, await running);
        Assert.Equal(4, await scheduler.RunAsync(
            () => 4,
            inputLength: 4,
            CancellationToken.None));
    }

}
