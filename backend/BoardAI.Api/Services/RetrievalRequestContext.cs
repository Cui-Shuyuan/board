using System.Collections.Concurrent;
using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 一次后端请求内的检索复用上下文。实例只通过 AsyncLocal 在请求作用域内传播，
/// 因而并行任务共享同一份缓存，不同请求之间互不影响；请求作用域结束时只丢弃
/// 对上下文的引用，不把请求数据写入任何 singleton 服务或长期规则快照。
///
/// 这里缓存可安全复用的中间计算结果：原始查询文本对应的查询向量、Qdrant
/// 别名快照，以及关键词扫描/评分的不可变快照。别名快照只在当前请求内有效，
/// 下一次问答会重新获取，以便及时看到索引别名切换。
/// </summary>
internal sealed class RetrievalRequestContext
{
    private static readonly AsyncLocal<RetrievalRequestContext?> CurrentContext = new();

    private readonly ConcurrentDictionary<QueryEmbeddingKey, SharedRequestOperation<ReadOnlyMemory<float>>> _queryEmbeddings =
        new();

    private readonly ConcurrentDictionary<AliasSnapshotKey, SharedRequestOperation<IReadOnlyDictionary<string, string>>> _aliasSnapshots =
        new();

    private readonly ConcurrentDictionary<KeywordScanKey, Lazy<Task<IReadOnlyList<KeywordScanHit>>>> _keywordScans =
        new();

    public static RetrievalRequestContext? Current => CurrentContext.Value;

    /// <summary>
    /// 若当前请求已有检索上下文则直接复用；否则建立轻量上下文。
    /// 返回的 scope 只恢复自己创建的那一层，嵌套调用不会覆盖外层上下文。
    /// </summary>
    public static IDisposable EnterIfAbsent()
    {
        var previous = CurrentContext.Value;
        if (previous != null)
            return NoopScope.Instance;

        var context = new RetrievalRequestContext();
        CurrentContext.Value = context;
        return new Scope(previous);
    }

    /// <summary>
    /// 同一原始查询文本 + 实际 embedding 配置只运行一次推理。
    /// 不进行 lower-case、去标点等规范化；查询文本按 Ordinal 精确参与键比较。
    /// 多个同时到达的等待者共享同一个实际推理任务；单个等待者取消只影响
    /// 自己，所有等待者都取消时尚未开始的推理会被取消。
    /// 返回只读内存，调用方查询 collection 时再复制得到独立数组。
    /// </summary>
    public Task<ReadOnlyMemory<float>> GetOrCreateQueryEmbeddingAsync(
        EmbeddingService embedder,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        cancellationToken.ThrowIfCancellationRequested();

        // ModelId 已包含部署模型目录名和 pooling profile；这里再把 pooling 与维度
        // 显式放进键中，保证未来即使同进程切换 embedder 也不会混用向量坐标。
        var key = new QueryEmbeddingKey(query, embedder.ModelId, embedder.Pooling, embedder.Dimension);
        return GetOrCreateSharedAsync(
            _queryEmbeddings,
            key,
            async token =>
            {
                var vector = await embedder.EmbedAsync(query, token).ConfigureAwait(false);
                return new ReadOnlyMemory<float>(vector);
            },
            cancellationToken);
    }

    /// <summary>
    /// 同一次请求内共享 Qdrant 别名列表。全文和 name 索引使用同一个 cacheKey，
    /// 因此并行搜索会合并到一次 ListAliasesAsync 调用。Qdrant 服务实例通过
    /// endpoint key 区分；成功结果只保留到当前请求结束。
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>> GetOrCreateAliasSnapshotAsync(
        string cacheKey,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> fetch,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentNullException.ThrowIfNull(fetch);
        cancellationToken.ThrowIfCancellationRequested();

        return GetOrCreateSharedAsync(
            _aliasSnapshots,
            new AliasSnapshotKey(cacheKey),
            fetch,
            cancellationToken);
    }

    /// <summary>
    /// 同一游戏、同一规则快照版本、同一原始查询文本的关键词扫描/评分只计算一次。
    /// 缓存值是只读快照；每次调用方都会重新物化为独立的 ConceptSummary。
    /// </summary>
    public async Task<IReadOnlyList<KeywordScanHit>> GetOrCreateKeywordScanAsync(
        string game,
        string rulesVersion,
        string query,
        Func<Task<IReadOnlyList<KeywordScanHit>>> factory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = new KeywordScanKey(game, rulesVersion, query);
        var lazy = _keywordScans.GetOrAdd(
            key,
            _ => new Lazy<Task<IReadOnlyList<KeywordScanHit>>>(
                () => ExecuteAsync(factory),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return await AwaitSharedAsync(_keywordScans, key, lazy, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GetOrCreateSharedAsync<TKey, T>(
        ConcurrentDictionary<TKey, SharedRequestOperation<T>> cache,
        TKey key,
        Func<CancellationToken, Task<T>> start,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (cache.TryGetValue(key, out var existing))
            {
                if (existing.TryAddWaiter())
                    return await AwaitOperationAsync(cache, key, existing, cancellationToken).ConfigureAwait(false);

                // A faulted/cancelled operation is never kept as a reusable
                // result. Remove it and let this caller attempt one fresh
                // start, still inside the same request.
                RemoveIfSame(cache, key, existing);
                continue;
            }

            var candidate = new SharedRequestOperation<T>(start);
            if (!cache.TryAdd(key, candidate))
            {
                // Another caller won the creation race. Its waiter is already
                // responsible for starting the operation.
                continue;
            }

            if (!candidate.TryAddWaiter())
            {
                RemoveIfSame(cache, key, candidate);
                continue;
            }

            // If a failed/cancelled shared operation is abandoned after its
            // last waiter leaves, remove it from the request cache as soon as
            // it reaches a terminal non-success state.
            _ = candidate.Task.ContinueWith(
                completed =>
                {
                    if (!completed.IsCompletedSuccessfully)
                        RemoveIfSame(cache, key, candidate);
                    if (completed.IsFaulted)
                        _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return await AwaitOperationAsync(cache, key, candidate, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<T> AwaitOperationAsync<TKey, T>(
        ConcurrentDictionary<TKey, SharedRequestOperation<T>> cache,
        TKey key,
        SharedRequestOperation<T> operation,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        try
        {
            var result = await operation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            // 命中已完成缓存时也必须遵守调用方当前取消状态。
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            // Do not evict a still-running shared inference merely because
            // this one waiter was cancelled; other waiters may still need it.
            if (operation.Task.IsCompleted && !operation.Task.IsCompletedSuccessfully)
                RemoveIfSame(cache, key, operation);
            throw;
        }
        finally
        {
            // This only cancels the shared operation when this was the last
            // interested waiter and the inference/fetch has not completed.
            operation.ReleaseWaiter();
        }
    }

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> factory)
    {
        // async 状态机会把 factory 在首个 await 前抛出的异常捕获为 faulted task。
        return await factory().ConfigureAwait(false);
    }

    private static async Task<T> AwaitSharedAsync<TKey, T>(
        ConcurrentDictionary<TKey, Lazy<Task<T>>> cache,
        TKey key,
        Lazy<Task<T>> lazy,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Lazy 的 value factory 只返回 Task；其中捕获的同步异常也会成为 faulted task，
        // 不会让 Lazy.Value 永久缓存同步异常。
        var task = lazy.Value;

        // 失败/取消的共享计算不能作为成功结果留在本请求缓存中，否则后续重试会命中
        // 错误。这里独立登记清理，避免首个等待者已被调用方取消时漏删。
        _ = task.ContinueWith(
            completed =>
            {
                if (!completed.IsFaulted && !completed.IsCanceled)
                    return;

                // Observe the exception so an abandoned computation does not become
                // an unobserved task exception.
                if (completed.IsFaulted)
                    _ = completed.Exception;

                RemoveIfSame(cache, key, lazy);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            var result = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            // 命中已完成缓存时也必须遵守调用方当前取消状态。
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // If the shared computation itself was canceled, evict it. A caller-only
            // cancellation must not throw away an otherwise valid in-flight result.
            if (task.IsCanceled)
                RemoveIfSame(cache, key, lazy);
            throw;
        }
        catch
        {
            RemoveIfSame(cache, key, lazy);
            throw;
        }
    }

    private static void RemoveIfSame<TKey, T>(
        ConcurrentDictionary<TKey, SharedRequestOperation<T>> cache,
        TKey key,
        SharedRequestOperation<T> operation)
        where TKey : notnull
    {
        ((ICollection<KeyValuePair<TKey, SharedRequestOperation<T>>>)cache)
            .Remove(new KeyValuePair<TKey, SharedRequestOperation<T>>(key, operation));
    }

    private static void RemoveIfSame<TKey, T>(
        ConcurrentDictionary<TKey, Lazy<Task<T>>> cache,
        TKey key,
        Lazy<Task<T>> lazy)
        where TKey : notnull
    {
        // 只删除 (key, lazy) 都仍然匹配的条目。先 TryGetValue 再 TryRemove(key)
        // 存在交错：另一清理者可能已删除旧值并让重试写入了新 lazy，随后按 key
        // 删除会误删新任务。ConcurrentDictionary 的 ICollection.Remove 对键值对
        // 匹配与删除是原子的。
        ((ICollection<KeyValuePair<TKey, Lazy<Task<T>>>>)cache)
            .Remove(new KeyValuePair<TKey, Lazy<Task<T>>>(key, lazy));
    }

    private readonly record struct QueryEmbeddingKey(
        string Query,
        string ModelId,
        string Pooling,
        int Dimension);

    private readonly record struct AliasSnapshotKey(string SourceKey);

    private readonly record struct KeywordScanKey(
        string Game,
        string RulesVersion,
        string Query);

    /// <summary>
    /// Shared one-request operation with reference-counted cancellation. The
    /// first waiter starts it; a caller cancellation only removes that waiter.
    /// When no waiter remains before completion the underlying operation token
    /// is cancelled. Once a synchronous ONNX inference has started, however,
    /// it cannot be stopped; its eventual result is simply no longer awaited.
    /// </summary>
    private sealed class SharedRequestOperation<T>
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<CancellationToken, Task<T>> _start;
        private int _waiters;
        private bool _started;
        private bool _closed;

        public SharedRequestOperation(Func<CancellationToken, Task<T>> start)
        {
            _start = start;

            // Observe a faulted task when every waiter has already gone away.
            _ = _completion.Task.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted)
                        _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public Task<T> Task => _completion.Task;

        public bool TryAddWaiter()
        {
            var shouldStart = false;
            lock (_gate)
            {
                // Closing is atomic with waiter registration. Once the last
                // waiter decides to cancel, no new waiter may join this
                // operation between that decision and the actual Cancel call.
                if (_closed)
                    return false;

                // Successfully completed results may be reused by later calls
                // in the same request. Faulted/cancelled tasks are evicted by
                // the caller and replaced with a fresh attempt.
                if (_completion.Task.IsCompleted && !_completion.Task.IsCompletedSuccessfully)
                    return false;
                if (_cancellation.IsCancellationRequested)
                    return false;

                _waiters++;
                if (!_started)
                {
                    _started = true;
                    shouldStart = true;
                }
            }

            if (shouldStart)
                _ = RunAsync();

            return true;
        }

        public void ReleaseWaiter()
        {
            var cancel = false;
            lock (_gate)
            {
                if (_waiters > 0)
                    _waiters--;

                if (_waiters == 0 && !_completion.Task.IsCompleted)
                {
                    // Mark closed while still holding the lock, then cancel
                    // outside it. A caller arriving after this point must
                    // create a fresh shared operation instead of joining a
                    // task whose cancellation is already in progress.
                    _closed = true;
                    cancel = true;
                }
            }

            if (!cancel) return;

            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The operation has already completed and can no longer need
                // waiter-based cancellation.
            }
        }

        private async Task RunAsync()
        {
            try
            {
                var result = await _start(_cancellation.Token).ConfigureAwait(false);
                _completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellation.Token);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }

    private sealed class Scope : IDisposable
    {
        private readonly RetrievalRequestContext? _previous;
        private bool _disposed;

        public Scope(RetrievalRequestContext? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            CurrentContext.Value = _previous;
            _disposed = true;
        }
    }
}

/// <summary>
/// 关键词扫描结果的不可变值快照。ConceptSummary 在交付调用方时重新构造，
/// 避免一条检索路径补充 description/score 时污染另一条路径的缓存对象。
/// </summary>
internal sealed record KeywordScanHit(
    string Id,
    string Path,
    string Name,
    string Type,
    string? Description,
    IReadOnlyDictionary<string, JsonElement>? Media,
    float Score);
