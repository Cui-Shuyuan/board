using System.Collections.Concurrent;
using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 一次后端请求内的检索复用上下文。实例只通过 AsyncLocal 在请求作用域内传播，
/// 因而并行任务共享同一份缓存，不同请求之间互不影响；请求作用域结束时只丢弃
/// 对上下文的引用，不把请求数据写入任何 singleton 服务或长期规则快照。
///
/// 这里不缓存 Qdrant 查询结果，只缓存可安全复用的中间计算结果：
/// 原始查询文本对应的查询向量，以及关键词扫描/评分的不可变快照。
/// </summary>
internal sealed class RetrievalRequestContext
{
    private static readonly AsyncLocal<RetrievalRequestContext?> CurrentContext = new();

    private readonly ConcurrentDictionary<QueryEmbeddingKey, Lazy<Task<ReadOnlyMemory<float>>>> _queryEmbeddings =
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
    /// 返回只读内存，调用方查询 collection 时再复制得到独立数组。
    /// </summary>
    public async Task<ReadOnlyMemory<float>> GetOrCreateQueryEmbeddingAsync(
        EmbeddingService embedder,
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // ModelId 已包含部署模型目录名和 pooling profile；这里再把 pooling 与维度
        // 显式放进键中，保证未来即使同进程切换 embedder 也不会混用向量坐标。
        var key = new QueryEmbeddingKey(query, embedder.ModelId, embedder.Pooling, embedder.Dimension);
        var lazy = _queryEmbeddings.GetOrAdd(
            key,
            _ => new Lazy<Task<ReadOnlyMemory<float>>>(
                () => ExecuteAsync(() => Task.FromResult(new ReadOnlyMemory<float>(embedder.Embed(query)))),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return await AwaitSharedAsync(_queryEmbeddings, key, lazy, cancellationToken).ConfigureAwait(false);
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

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> factory)
    {
        // async 状态机会把 factory 在首个 await 前抛出的异常捕获为 faulted task。
        return await factory().ConfigureAwait(false);
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

    private readonly record struct KeywordScanKey(
        string Game,
        string RulesVersion,
        string Query);

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
