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
    private readonly Dictionary<string, FlowStructureIndex> _flowIndexesByGame = new();

    public RulesFlowService(RulesContentStore content)
    {
        _content = content;
    }

    public IReadOnlyDictionary<string, FlowPosition> GetFlowPositions(string game)
    {
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null && snapshot.Game == game)
            return snapshot.GetOrAdd("flow-positions", () => BuildFlowPositions(game));

        if (_flowPositionsByGame.TryGetValue(game, out var cached))
            return cached;

        var result = BuildFlowPositions(game);
        _flowPositionsByGame[game] = result;
        return result;
    }

    private Dictionary<string, FlowPosition> BuildFlowPositions(string game)
    {
        var result = new Dictionary<string, FlowPosition>();
        var flow = _content.LoadGameFlow(game);
        if (flow != null)
            WalkFlowPositions(flow.RootElement, new List<string>(), null, -1, null, result);

        return result;
    }

    public void Clear()
    {
        _flowPositionsByGame.Clear();
        _flowIndexesByGame.Clear();
    }

    /// <summary>返回当前请求快照的流程结构索引；同一 snapshot 只构建一次。</summary>
    public FlowStructureIndex GetFlowIndex(string game)
    {
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null && snapshot.Game == game)
            return snapshot.GetOrAdd("flow-structure-index", () => BuildFlowIndex(game, snapshot.Version));

        if (_flowIndexesByGame.TryGetValue(game, out var cached))
            return cached;

        var result = BuildFlowIndex(game, null);
        _flowIndexesByGame[game] = result;
        return result;
    }

    private FlowStructureIndex BuildFlowIndex(string game, string? rulesVersion)
    {
        var flow = _content.LoadGameFlow(game);
        if (flow == null)
        {
            return new FlowStructureIndex
            {
                Game = game,
                RulesVersion = rulesVersion ?? "",
                Root = default,
                ByPath = new Dictionary<string, FlowNodeInfo>(StringComparer.Ordinal),
                ByLocalId = new Dictionary<string, List<FlowNodeInfo>>(StringComparer.Ordinal),
                ByName = new Dictionary<string, List<FlowNodeInfo>>(StringComparer.Ordinal),
                AllNodes = new List<FlowNodeInfo>()
            };
        }

        var byPath = new Dictionary<string, FlowNodeInfo>(StringComparer.Ordinal);
        var byLocalId = new Dictionary<string, List<FlowNodeInfo>>(StringComparer.Ordinal);
        var byName = new Dictionary<string, List<FlowNodeInfo>>(StringComparer.Ordinal);
        var allNodes = new List<FlowNodeInfo>();
        var root = flow.RootElement.Clone();

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
                WalkIndexArray(property.Value, property.Name, null, new List<FlowAncestorInfo>(),
                    byPath, byLocalId, byName, allNodes);
            else if (property.Value.ValueKind == JsonValueKind.Object)
                WalkIndexObject(property.Value, property.Name, null, -1, property.Name,
                    new List<FlowAncestorInfo>(), byPath, byLocalId, byName, allNodes);
        }

        return new FlowStructureIndex
        {
            Game = game,
            RulesVersion = rulesVersion ?? "",
            Root = root,
            ByPath = byPath,
            ByLocalId = byLocalId,
            ByName = byName,
            AllNodes = allNodes
        };
    }

    private static void WalkIndexArray(
        JsonElement array,
        string container,
        string? parentPath,
        List<FlowAncestorInfo> ancestors,
        Dictionary<string, FlowNodeInfo> byPath,
        Dictionary<string, List<FlowNodeInfo>> byLocalId,
        Dictionary<string, List<FlowNodeInfo>> byName,
        List<FlowNodeInfo> allNodes)
    {
        for (var index = 0; index < array.GetArrayLength(); index++)
        {
            WalkIndexObject(array[index], $"{container}[{index}]", parentPath, index, container,
                ancestors, byPath, byLocalId, byName, allNodes);
        }
    }

    private static void WalkIndexObject(
        JsonElement node,
        string sourcePath,
        string? parentPath,
        int siblingIndex,
        string siblingContainer,
        List<FlowAncestorInfo> ancestors,
        Dictionary<string, FlowNodeInfo> byPath,
        Dictionary<string, List<FlowNodeInfo>> byLocalId,
        Dictionary<string, List<FlowNodeInfo>> byName,
        List<FlowNodeInfo> allNodes)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        string? id = null;
        string name = "";
        if (node.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            id = idProp.GetString();
        if (node.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.Object
            && nameProp.TryGetProperty("zh", out var zh) && zh.ValueKind == JsonValueKind.String)
            name = zh.GetString() ?? "";

        var hasIdentity = !string.IsNullOrWhiteSpace(id);
        var canonicalPath = hasIdentity ? parentPath : parentPath;
        var ourAncestors = ancestors;
        var ourParent = parentPath;
        var currentInfo = default(FlowNodeInfo);

        if (hasIdentity)
        {
            // Identity is built from parent canonical path + declared id. The raw
            // JSON source path is retained separately for diagnostics. This keeps
            // same-named local ids under different owners distinct and readable.
            canonicalPath = UniquePath(
                string.IsNullOrWhiteSpace(parentPath) ? id! : parentPath + "/" + id,
                byPath);
            var loop = node.TryGetProperty("loop", out var loopElement) ? loopElement.Clone() : (JsonElement?)null;
            var condition = FindCondition(node);
            var doAfter = node.TryGetProperty("do_after", out var after) ? after.Clone() : (JsonElement?)null;
            var optional = node.TryGetProperty("optional", out var optionalElement)
                && optionalElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && optionalElement.GetBoolean();

            currentInfo = new FlowNodeInfo
            {
                CanonicalPath = canonicalPath!,
                SourcePath = sourcePath,
                LocalId = id!,
                Name = name,
                Kind = InferKind(node, siblingContainer),
                ParentPath = parentPath,
                Index = siblingIndex,
                SiblingContainer = siblingContainer,
                Ancestors = new List<FlowAncestorInfo>(ancestors),
                Element = node.Clone(),
                Loop = loop,
                Condition = condition,
                Optional = optional,
                DoAfter = doAfter
            };
            byPath[currentInfo.CanonicalPath] = currentInfo;
            allNodes.Add(currentInfo);
            if (!byLocalId.TryGetValue(currentInfo.LocalId, out var idList))
                byLocalId[currentInfo.LocalId] = idList = new List<FlowNodeInfo>();
            idList.Add(currentInfo);
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (!byName.TryGetValue(name, out var nameList))
                    byName[name] = nameList = new List<FlowNodeInfo>();
                nameList.Add(currentInfo);
            }

            ourParent = currentInfo.CanonicalPath;
            ourAncestors = new List<FlowAncestorInfo>(ancestors)
            {
                new()
                {
                    CanonicalPath = currentInfo.CanonicalPath,
                    LocalId = currentInfo.LocalId,
                    Name = currentInfo.Name,
                    Kind = currentInfo.Kind,
                    Loop = currentInfo.Loop,
                    Condition = currentInfo.Condition
                }
            };
        }

        foreach (var property in node.EnumerateObject())
        {
            if (property.Name is "id" or "name" or "description" or "loop" or "do_after"
                or "optional" or "condition" or "<ontology::condition>")
                continue;

            var sourceBase = string.IsNullOrWhiteSpace(sourcePath)
                ? property.Name
                : sourcePath + "/" + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                WalkIndexArray(property.Value, sourceBase, ourParent, ourAncestors,
                    byPath, byLocalId, byName, allNodes);
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                WalkIndexObject(property.Value, sourceBase, ourParent, -1, property.Name,
                    ourAncestors, byPath, byLocalId, byName, allNodes);
            }
        }

        if (hasIdentity && currentInfo != null && ourParent != null)
        {
            // Record direct children on the nearest indexed parent after recursive walk.
            // byPath holds immutable-ish records; rebuild the parent with the accumulated child list.
            if (parentPath != null && byPath.TryGetValue(parentPath, out var parent))
            {
                if (!parent.ChildPaths.Contains(currentInfo.CanonicalPath, StringComparer.Ordinal))
                {
                    var updatedChildren = new List<string>(parent.ChildPaths) { currentInfo.CanonicalPath };
                    byPath[parentPath] = CloneWithChildren(parent, updatedChildren);
                    ReplaceNode(allNodes, parent.CanonicalPath, byPath[parentPath]);
                }
            }
        }
    }

    private static string UniquePath(string sourcePath, Dictionary<string, FlowNodeInfo> byPath)
    {
        if (!byPath.ContainsKey(sourcePath)) return sourcePath;
        var suffix = 2;
        while (byPath.ContainsKey($"{sourcePath}#{suffix}")) suffix++;
        return $"{sourcePath}#{suffix}";
    }

    private static FlowNodeInfo CloneWithChildren(FlowNodeInfo source, List<string> children) => new()
    {
        CanonicalPath = source.CanonicalPath,
        SourcePath = source.SourcePath,
        LocalId = source.LocalId,
        Name = source.Name,
        Kind = source.Kind,
        ParentPath = source.ParentPath,
        Index = source.Index,
        SiblingContainer = source.SiblingContainer,
        ChildPaths = children,
        Ancestors = source.Ancestors,
        Element = source.Element,
        Loop = source.Loop,
        Condition = source.Condition,
        Optional = source.Optional,
        DoAfter = source.DoAfter
    };

    private static void ReplaceNode(List<FlowNodeInfo> nodes, string path, FlowNodeInfo replacement)
    {
        for (var i = 0; i < nodes.Count; i++)
            if (nodes[i].CanonicalPath == path)
                nodes[i] = replacement;
    }

    private static JsonElement? FindCondition(JsonElement node)
    {
        if (node.TryGetProperty("condition", out var condition)) return condition.Clone();
        if (node.TryGetProperty("<ontology::condition>", out condition)) return condition.Clone();
        return null;
    }

    private static string InferKind(JsonElement node, string container)
    {
        if (node.TryGetProperty("specifies", out var specifiesElement)
            && specifiesElement.ValueKind == JsonValueKind.String)
        {
            var specifies = specifiesElement.GetString() ?? "";
            if (specifies.Contains("action", StringComparison.OrdinalIgnoreCase)) return "action";
            if (specifies.Contains("trigger", StringComparison.OrdinalIgnoreCase)) return "trigger";
            if (specifies.Contains("phase", StringComparison.OrdinalIgnoreCase)) return "phase";
            if (specifies.Contains("turn", StringComparison.OrdinalIgnoreCase)) return "turn";
            if (specifies.Contains("round", StringComparison.OrdinalIgnoreCase)) return "round";
            if (specifies.Contains("pipeline", StringComparison.OrdinalIgnoreCase)) return "pipeline";
        }

        return container switch
        {
            "triggers" => "trigger",
            "procedures" => "procedure",
            "options" => "pipeline_step",
            "events" => "event",
            "slots" => "slot",
            _ => "flow_node"
        };
    }

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

/// <summary>规范流程节点身份；路径来自实际 JSON 结构，唯一且与游戏/规则版本绑定。</summary>
public sealed class FlowNodeInfo
{
    public string CanonicalPath { get; init; } = "";
    /// <summary>原始 JSON 来源路径，仅用于诊断；身份以 CanonicalPath 为准。</summary>
    public string SourcePath { get; init; } = "";
    public string LocalId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "flow_node";
    public string? ParentPath { get; init; }
    public int Index { get; init; } = -1;
    public string SiblingContainer { get; init; } = "";
    public List<string> ChildPaths { get; init; } = new();
    public List<FlowAncestorInfo> Ancestors { get; init; } = new();
    public JsonElement Element { get; init; }
    public JsonElement? Loop { get; init; }
    public JsonElement? Condition { get; init; }
    public bool Optional { get; init; }
    public JsonElement? DoAfter { get; init; }
}

public sealed class FlowAncestorInfo
{
    public string CanonicalPath { get; init; } = "";
    /// <summary>原始 JSON 来源路径，仅用于诊断；身份以 CanonicalPath 为准。</summary>
    public string SourcePath { get; init; } = "";
    public string LocalId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "flow_node";
    public JsonElement? Loop { get; init; }
    public JsonElement? Condition { get; init; }
}

public sealed class FlowTargetCandidate
{
    public string CanonicalPath { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
}

public sealed class FlowTargetResolution
{
    public List<FlowNodeInfo> Exact { get; init; } = new();
    public List<FlowTargetCandidate> Candidates { get; init; } = new();
}

/// <summary>
/// 对同一规则 snapshot 构建的流程结构索引。键为 game + rules_version；
/// 路径唯一，同名局部 id 只作为解析输入，不作为身份。
/// </summary>
public sealed class FlowStructureIndex
{
    public string Game { get; init; } = "";
    public string RulesVersion { get; init; } = "";
    public JsonElement Root { get; init; }
    public Dictionary<string, FlowNodeInfo> ByPath { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<FlowNodeInfo>> ByLocalId { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<FlowNodeInfo>> ByName { get; init; } = new(StringComparer.Ordinal);
    public List<FlowNodeInfo> AllNodes { get; init; } = new();

    public FlowTargetResolution Resolve(string entity)
    {
        entity = (entity ?? "").Trim();
        if (string.IsNullOrEmpty(entity))
            return new FlowTargetResolution();

        if (ByPath.TryGetValue(entity, out var pathNode))
            return new FlowTargetResolution { Exact = new List<FlowNodeInfo> { pathNode } };

        // Accept a game-scoped path as a convenience, but never strip arbitrary
        // prefixes to turn a local id into a unique identity.
        var gamePrefix = Game + "/";
        if (entity.StartsWith(gamePrefix, StringComparison.Ordinal)
            && ByPath.TryGetValue(entity[gamePrefix.Length..], out var prefixedNode))
            return new FlowTargetResolution { Exact = new List<FlowNodeInfo> { prefixedNode } };

        if (ByLocalId.TryGetValue(entity, out var byId) && byId.Count == 1)
            return new FlowTargetResolution { Exact = new List<FlowNodeInfo> { Current(byId[0]) } };

        if (ByName.TryGetValue(entity, out var byName) && byName.Count == 1)
            return new FlowTargetResolution { Exact = new List<FlowNodeInfo> { Current(byName[0]) } };

        var candidates = new List<FlowNodeInfo>();
        if (byId != null) candidates.AddRange(byId);
        if (byName != null) candidates.AddRange(byName);
        if (candidates.Count == 0)
        {
            candidates.AddRange(AllNodes
                .Where(node => !string.IsNullOrEmpty(node.Name)
                    && (node.Name.Contains(entity, StringComparison.OrdinalIgnoreCase)
                        || node.LocalId.Contains(entity, StringComparison.OrdinalIgnoreCase)))
                .Take(12));
        }

        return new FlowTargetResolution
        {
            Candidates = candidates
                .DistinctBy(node => node.CanonicalPath, StringComparer.Ordinal)
                .Select(Current)
                .Select(node => new FlowTargetCandidate
                {
                    CanonicalPath = node.CanonicalPath,
                    Id = node.LocalId,
                    Name = node.Name,
                    Kind = node.Kind
                })
                .ToList()
        };
    }

    /// <summary>
    /// Earlier nodes captured in byLocalId/byName can carry stale child lists after
    /// recursive updates; resolve through byPath at read time.
    /// </summary>
    private FlowNodeInfo Current(FlowNodeInfo node)
        => ByPath.TryGetValue(node.CanonicalPath, out var current) ? current : node;
}
