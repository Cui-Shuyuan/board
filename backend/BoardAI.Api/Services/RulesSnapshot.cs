using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>
/// 一次规则请求绑定的一版不可变内容快照。快照内的 JsonDocument 是独立的深拷贝，
/// 生命周期由抢到该快照的请求/结果引用决定；交换快照不会释放仍在飞行的旧快照。
/// 同一快照上构建出的名称/流程/类型/事实缓存天然属于同一规则版本。
/// </summary>
public sealed class RulesGameSnapshot
{
    private readonly ConcurrentDictionary<string, object> _cache = new(StringComparer.Ordinal);

    public RulesGameSnapshot(
        string game,
        long revision,
        string version,
        JsonDocument ontology,
        JsonDocument? gameConcepts,
        JsonDocument? gameInstances,
        JsonDocument? gameFlow,
        JsonDocument? ontologyFlow)
    {
        Game = game;
        Revision = revision;
        Version = version;
        Ontology = ontology;
        GameConcepts = gameConcepts;
        GameInstances = gameInstances;
        GameFlow = gameFlow;
        OntologyFlow = ontologyFlow;
    }

    public string Game { get; }
    public long Revision { get; }
    public string Version { get; }
    public JsonDocument Ontology { get; }
    public JsonDocument? GameConcepts { get; }
    public JsonDocument? GameInstances { get; }
    public JsonDocument? GameFlow { get; }
    public JsonDocument? OntologyFlow { get; }

    /// <summary>在快照内一次性构建派生缓存；同一 key 并发只保留一个结果。</summary>
    public T GetOrAdd<T>(string key, Func<T> factory) where T : class
    {
        return (T)_cache.GetOrAdd(key, _ => factory()!);
    }

    /// <summary>计算并拼接影响本游戏规则语义的文件内容哈希；变更内容会产生新版本串。</summary>
    public static string ComputeVersion(
        string game,
        JsonDocument ontology,
        JsonDocument? gameConcepts,
        JsonDocument? gameInstances,
        JsonDocument? gameFlow,
        JsonDocument? ontologyFlow)
    {
        var builder = new StringBuilder();
        builder.Append("game=").Append(game).Append('\n');
        Append(builder, "ontology", ontology);
        Append(builder, "game_concepts", gameConcepts);
        Append(builder, "game_instances", gameInstances);
        Append(builder, "game_flow", gameFlow);
        Append(builder, "ontology_flow", ontologyFlow);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string name, JsonDocument? document)
    {
        builder.Append(name).Append('=');
        if (document == null)
        {
            builder.Append("<missing>");
        }
        else
        {
            builder.Append(document.RootElement.GetRawText());
        }
        builder.Append('\n');
    }
}

/// <summary>
/// AsyncLocal 请求作用域：在单次后端调用期间，让规则内容读取和派生缓存看到同一快照。
/// </summary>
public static class RulesSnapshotScope
{
    private static readonly AsyncLocal<RulesGameSnapshot?> CurrentSnapshot = new();

    public static RulesGameSnapshot? Current => CurrentSnapshot.Value;

    public static IDisposable Enter(RulesGameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var previous = CurrentSnapshot.Value;
        CurrentSnapshot.Value = snapshot;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly RulesGameSnapshot? _previous;
        private bool _disposed;

        public Scope(RulesGameSnapshot? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            CurrentSnapshot.Value = _previous;
            _disposed = true;
        }
    }
}
