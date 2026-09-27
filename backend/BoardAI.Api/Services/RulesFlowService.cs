using System.Text.Json;

namespace BoardAI.Api.Services;

/// <summary>flow 节点 id → 流程位置（祖先链 + 同级选项顺序 + 位置 + 最近 loop）。</summary>
public sealed class FlowPosition
{
    /// <summary>同级 options 的 id/引用列表（按序）——回答「之后是什么」。</summary>
    public List<string> Siblings { get; set; } = new();
    /// <summary>在同级 options 中的下标。</summary>
    public int Index { get; set; } = -1;
    /// <summary>祖先链 zh 名（不含自己）。</summary>
    public List<string> Ancestors { get; set; } = new();
    /// <summary>最近的 loop 结构（round/phase 的 count/until）——回答「何时结束」。</summary>
    public JsonElement? Loop { get; set; }
}

/// <summary>
/// 规则流程位置协作类：从 game flow.json 构建节点 id → FlowPosition 映射。
/// 按 game 分别缓存；缺少 flow 文件时缓存空字典。
/// </summary>
public sealed class RulesFlowService
{
    private readonly RulesContentStore _content;
    private readonly Dictionary<string, Dictionary<string, FlowPosition>> _flowPositionsByGame = new();

    public RulesFlowService(RulesContentStore content)
    {
        _content = content;
    }

    public IReadOnlyDictionary<string, FlowPosition> GetFlowPositions(string game)
    {
        if (_flowPositionsByGame.TryGetValue(game, out var cached))
            return cached;

        var result = new Dictionary<string, FlowPosition>();
        var flow = _content.LoadGameFlow(game);
        if (flow != null)
            WalkFlowPositions(flow.RootElement, new List<string>(), null, -1, null, result);

        _flowPositionsByGame[game] = result;
        return result;
    }

    public void Clear() => _flowPositionsByGame.Clear();

    private static void WalkFlowPositions(
        JsonElement node, List<string> ancestors,
        List<string>? siblingIds, int siblingIndex,
        JsonElement? currentLoop,
        Dictionary<string, FlowPosition> result)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        var hasId = node.TryGetProperty("id", out var idProp)
            && idProp.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(idProp.GetString());
        string? id = hasId ? idProp.GetString() : null;
        JsonElement zhp = default;
        var hasName = node.TryGetProperty("name", out var nm)
            && nm.ValueKind == JsonValueKind.Object
            && nm.TryGetProperty("zh", out zhp)
            && zhp.ValueKind == JsonValueKind.String;

        // loop 沿树向下传递：最近的 procedure 祖先的 loop 是子树的循环边界
        var loopHere = node.TryGetProperty("loop", out var lp) ? lp : currentLoop;

        if (!string.IsNullOrEmpty(id))
        {
            result[id] = new FlowPosition
            {
                Siblings = siblingIds != null ? new List<string>(siblingIds) : new List<string>(),
                Index = siblingIndex,
                Ancestors = new List<string>(ancestors),
                Loop = loopHere
            };
            if (hasName && !string.IsNullOrEmpty(zhp.GetString()))
                ancestors.Add(zhp.GetString()!);
        }

        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Name == "id" || prop.Name == "name" || prop.Name == "description" || prop.Name == "loop") continue;

            if (prop.Name == "options" && prop.Value.ValueKind == JsonValueKind.Array)
            {
                // 收集同级 id 列表，然后逐个下钻（带新上下文）
                var sibIds = new List<string>();
                foreach (var opt in prop.Value.EnumerateArray())
                {
                    if (opt.ValueKind == JsonValueKind.String)
                        sibIds.Add(opt.GetString()!);
                    else if (opt.ValueKind == JsonValueKind.Object
                        && opt.TryGetProperty("id", out var oid) && oid.ValueKind == JsonValueKind.String)
                        sibIds.Add(oid.GetString()!);
                }
                for (var i = 0; i < prop.Value.GetArrayLength(); i++)
                    WalkFlowPositions(prop.Value[i], ancestors, sibIds, i, loopHere, result);
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.Object)
                WalkFlowPositions(prop.Value, ancestors, siblingIds, siblingIndex, loopHere, result);
            else if (prop.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in prop.Value.EnumerateArray())
                    WalkFlowPositions(item, ancestors, siblingIds, siblingIndex, loopHere, result);
        }

        if (!string.IsNullOrEmpty(id) && hasName && !string.IsNullOrEmpty(zhp.GetString()))
            ancestors.RemoveAt(ancestors.Count - 1);
    }
}
