using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RetrievalRequestContextTests
{
    [Fact]
    public async Task AliasSnapshot_IsSharedByConcurrentSearches_AndRefetchedForNextRequest()
    {
        var fetchEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFetch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestFetches = 0;

        using (RetrievalRequestContext.EnterIfAbsent())
        {
            var context = Assert.IsType<RetrievalRequestContext>(RetrievalRequestContext.Current);

            async Task<IReadOnlyDictionary<string, string>> Fetch(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref firstRequestFetches);
                fetchEntered.TrySetResult();
                await releaseFetch.Task.WaitAsync(cancellationToken);
                return new Dictionary<string, string>
                {
                    ["board_test__active"] = "board_test__v1",
                    ["board_test__active_name"] = "board_test__v1__name",
                };
            }

            var fullSearch = context.GetOrCreateAliasSnapshotAsync(
                "qdrant-test", Fetch, CancellationToken.None);
            await fetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // Both searches are deliberately simultaneous and share the same
            // in-flight ListAliasesAsync operation.
            var nameSearch = context.GetOrCreateAliasSnapshotAsync(
                "qdrant-test", Fetch, CancellationToken.None);

            releaseFetch.TrySetResult();
            var snapshots = await Task.WhenAll(fullSearch, nameSearch);

            Assert.Equal(1, Volatile.Read(ref firstRequestFetches));
            Assert.Equal("board_test__v1", snapshots[0]["board_test__active"]);
            Assert.Equal("board_test__v1__name", snapshots[1]["board_test__active_name"]);
        }

        var secondRequestFetches = 0;
        using (RetrievalRequestContext.EnterIfAbsent())
        {
            var context = Assert.IsType<RetrievalRequestContext>(RetrievalRequestContext.Current);
            var snapshot = await context.GetOrCreateAliasSnapshotAsync(
                "qdrant-test",
                _ =>
                {
                    Interlocked.Increment(ref secondRequestFetches);
                    return Task.FromResult<IReadOnlyDictionary<string, string>>(
                        new Dictionary<string, string>
                        {
                            ["board_test__active"] = "board_test__v2",
                        });
                },
                CancellationToken.None);

            Assert.Equal(1, Volatile.Read(ref secondRequestFetches));
            Assert.Equal("board_test__v2", snapshot["board_test__active"]);
        }
    }

    [Fact]
    public async Task FailedAliasFetch_IsEvictedAndCanBeRetriedWithinSameRequest()
    {
        using var scope = RetrievalRequestContext.EnterIfAbsent();
        var context = Assert.IsType<RetrievalRequestContext>(RetrievalRequestContext.Current);
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.GetOrCreateAliasSnapshotAsync(
                "qdrant-failure",
                _ =>
                {
                    Interlocked.Increment(ref calls);
                    throw new InvalidOperationException("temporary failure");
                },
                CancellationToken.None));

        var snapshot = await context.GetOrCreateAliasSnapshotAsync(
            "qdrant-failure",
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<IReadOnlyDictionary<string, string>>(
                    new Dictionary<string, string>
                    {
                        ["board_test__active"] = "board_test__v1",
                    });
            },
            CancellationToken.None);

        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal("board_test__v1", snapshot["board_test__active"]);
    }

    [Fact]
    public async Task OneCancelledAliasWaiter_DoesNotCancelFetchNeededByAnotherWaiter()
    {
        using var scope = RetrievalRequestContext.EnterIfAbsent();
        var context = Assert.IsType<RetrievalRequestContext>(RetrievalRequestContext.Current);
        var fetchEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFetch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<IReadOnlyDictionary<string, string>> Fetch(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            fetchEntered.TrySetResult();
            await releaseFetch.Task.WaitAsync(cancellationToken);
            return new Dictionary<string, string>
            {
                ["board_test__active"] = "board_test__v1",
            };
        }

        using var firstWaiterCancellation = new CancellationTokenSource();
        var firstWaiter = context.GetOrCreateAliasSnapshotAsync(
            "qdrant-cancel", Fetch, firstWaiterCancellation.Token);
        await fetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondWaiter = context.GetOrCreateAliasSnapshotAsync(
            "qdrant-cancel", Fetch, CancellationToken.None);

        firstWaiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstWaiter);

        releaseFetch.TrySetResult();
        var snapshot = await secondWaiter;

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal("board_test__v1", snapshot["board_test__active"]);
    }
    [Fact]
    public async Task ClosingSharedOperation_DoesNotLetLateWaiterJoinCancellationInProgress()
    {
        using var scope = RetrievalRequestContext.EnterIfAbsent();
        var context = Assert.IsType<RetrievalRequestContext>(RetrievalRequestContext.Current);
        var firstFetchStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationCallbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellationCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchCalls = 0;

        async Task<IReadOnlyDictionary<string, string>> Fetch(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref fetchCalls);
            if (call > 1)
            {
                return new Dictionary<string, string>
                {
                    ["board_test__active"] = "board_test__v2",
                };
            }

            firstFetchStarted.TrySetResult();
            using var registration = cancellationToken.Register(() =>
            {
                cancellationCallbackEntered.TrySetResult();
                releaseCancellationCallback.Task.GetAwaiter().GetResult();
            });

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        using var firstCancellation = new CancellationTokenSource();
        var first = context.GetOrCreateAliasSnapshotAsync(
            "qdrant-close-race", Fetch, firstCancellation.Token);
        await firstFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        _ = Task.Run(firstCancellation.Cancel);
        await cancellationCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // The old operation has closed waiter registration and is blocked
        // inside cancellation. This new caller must get a fresh operation.
        var second = context.GetOrCreateAliasSnapshotAsync(
            "qdrant-close-race", Fetch, CancellationToken.None);

        releaseCancellationCallback.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var snapshot = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref fetchCalls));
        Assert.Equal("board_test__v2", snapshot["board_test__active"]);
    }

}
