using System.Text.Json;
using System.Text.RegularExpressions;

namespace BoardAI.Api.Services;

public sealed record ExactNameMatch(string Id, string Kind);

/// <summary>
/// 规则名称索引协作类：从概念目录与规则 JSON 构建 id→zh 名映射，以及
/// 「名词直呼」精确查找表（zh/en 名、aliases、基名）。按 game 缓存，
/// 规则文档变化时由 <see cref="Clear"/> 清空重建。
/// </summary>
public sealed class RulesNameIndexService
{
    private readonly IRulesConceptCatalog _catalog;
    private readonly RulesContentStore _content;

    private readonly Dictionary<string, Dictionary<string, string>> _nameMaps = new();
    private readonly Dictionary<string, Dictionary<string, IReadOnlyList<ExactNameMatch>>> _exactLookups = new();

    public RulesNameIndexService(IRulesConceptCatalog catalog, RulesContentStore content)
    {
        _catalog = catalog;
        _content = content;
    }

    /// <summary>游戏概念 id → zh 名（概念目录 + 游戏流 + 本体流）。</summary>
    public IReadOnlyDictionary<string, string> GetNameMap(string game)
    {
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null && snapshot.Game == game)
            return snapshot.GetOrAdd("name-map", () => BuildNameMap(game));

        if (_nameMaps.TryGetValue(game, out var cached)) return cached;
        var map = BuildNameMap(game);
        _nameMaps[game] = map;
        return map;
    }

    private Dictionary<string, string> BuildNameMap(string game)
    {
        var map = new Dictionary<string, string>();

        foreach (var type in _catalog.GetConceptTypes(game))
        {
            foreach (var summary in _catalog.ListConcepts(game, type))
            {
                if (!string.IsNullOrEmpty(summary.Id) && !string.IsNullOrEmpty(summary.Name) && summary.Id != summary.Name)
                    map[summary.Id] = summary.Name;
            }
        }

        // flow.json 递归节点（procedures/triggers 内嵌的 id + name.zh）
        var flow = _content.LoadGameFlow(game);
        if (flow != null) WalkFlowForNames(flow.RootElement, map);
        var ontologyFlow = _content.LoadOntologyFlow();
        if (ontologyFlow != null) WalkFlowForNames(ontologyFlow.RootElement, map);

        return map;
    }

    private static void WalkFlowForNames(JsonElement node, Dictionary<string, string> map)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString() ?? "";
                if (!string.IsNullOrEmpty(id)
                    && node.TryGetProperty("name", out var name)
                    && name.TryGetProperty("zh", out var zh))
                {
                    var zhName = zh.GetString() ?? "";
                    if (!string.IsNullOrEmpty(zhName) && !map.ContainsKey(id))
                        map[id] = zhName;
                }
            }
            foreach (var prop in node.EnumerateObject())
                WalkFlowForNames(prop.Value, map);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                WalkFlowForNames(item, map);
        }
    }

    /// <summary>
    /// 「名词直呼」精确查找表：zh 名 / en 名（大小写不敏感）/ aliases / 基名（括号注解剥除）
    /// → 概念 id 列表 + 匹配类别。一个键可以映射多个概念（如基名「家庭成长」→ 需空房间与
    /// 无需房间两个行动；别名「随时转换效果」→ 烹饪与生吃），多命中全部返回由 LLM 读数据取舍。
    /// 与 GetNameMap 同源（概念 + 实例 + flow + 本体）。首次访问时构建并缓存；
    /// 规则 JSON 变化时由 RulesDocumentStore 回调自动清空重建。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ExactNameMatch>> GetExactLookup(string game)
    {
        var snapshot = RulesSnapshotScope.Current;
        if (snapshot != null && snapshot.Game == game)
            return snapshot.GetOrAdd("exact-lookup", () => BuildExactLookup(game));

        if (_exactLookups.TryGetValue(game, out var cached)) return cached;
        var lookup = BuildExactLookup(game);
        _exactLookups[game] = lookup;
        return lookup;
    }

    private Dictionary<string, IReadOnlyList<ExactNameMatch>> BuildExactLookup(string game)
    {
        var map = new Dictionary<string, List<ExactNameMatch>>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string id, string kind)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<ExactNameMatch>();
                map[key] = list;
            }
            if (!list.Any(e => e.Id == id)) list.Add(new ExactNameMatch(id, kind));
        }

        // 1) 既有来源的 zh 名（与 GetNameMap 同源，保持原行为）
        foreach (var type in _catalog.GetConceptTypes(game))
            foreach (var summary in _catalog.ListConcepts(game, type))
                Add(summary.Name, summary.Id, "exact_name_zh");

        // 2) 游戏概念 + 实例的 zh/en 名与 aliases（只有原始 JSON 才有这些字段）
        AddRawNames(_content.LoadGameConcepts(game), RulesConceptTypes.ConceptArrayTypes, Add);
        AddRawNames(_content.LoadGameInstances(game), RulesConceptTypes.InstanceArrayTypes, Add);

        // 3) 本体概念 en 名（zh 名太通用——行动/转移/对象——不进直呼表，避免噪声）
        AddRawNames(_content.LoadOntology(), new[] { "concepts" }, Add);

        // 4) flow 节点 zh/en 名
        var flow = _content.LoadGameFlow(game);
        if (flow != null) WalkFlowNames(flow.RootElement, Add);
        var ontologyFlow = _content.LoadOntologyFlow();
        if (ontologyFlow != null) WalkFlowNames(ontologyFlow.RootElement, Add);

        // 5) 基名：把 zh 名里的括号注解剥掉（「家庭成长（需空房间）」→「家庭成长」），
        //    让客人/LLM 只说名字主体也能直呼命中——2026-08-16 QA 显示 C 类题几乎全是
        //    转述与全名不一致导致的实体解析失败
        var bases = new List<(string Key, string Id)>();
        foreach (var (key, list) in map)
            foreach (var match in list)
                if (match.Kind == "exact_name_zh")
                {
                    var b = StripAnnotations(key);
                    if (b != null && !string.Equals(b, key, StringComparison.Ordinal))
                        bases.Add((b, match.Id));
                }
        foreach (var (key, id) in bases)
            Add(key, id, "exact_base");

        var frozen = map.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<ExactNameMatch>)kv.Value.AsReadOnly(),
            StringComparer.OrdinalIgnoreCase);

        return frozen;
    }

    /// <summary>清空名称映射与精确查找缓存（规则文档变化时调用）。</summary>
    public void Clear()
    {
        _nameMaps.Clear();
        _exactLookups.Clear();
    }

    /// <summary>剥除中文名里的括号注解（全角/半角均可），剥后不足 2 字返回 null。</summary>
    private static string? StripAnnotations(string name)
    {
        var s = ParentheticalRegex.Replace(name, "");
        s = s.Trim();
        return s.Length >= 2 ? s : null;
    }

    private static readonly Regex ParentheticalRegex = new(@"[（(][^（）()]*[）)]");

    private static void AddRawNames(JsonDocument? doc, string[] arrays,
        Action<string, string, string> add)
    {
        if (doc == null) return;
        foreach (var type in arrays)
        {
            if (!doc.RootElement.TryGetProperty(type, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var el in arr.EnumerateArray())
            {
                var id = RulesTextUtils.GetElementId(el);
                if (string.IsNullOrEmpty(id)) continue;
                if (el.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object)
                {
                    if (name.TryGetProperty("zh", out var zh))
                    {
                        var zhName = zh.GetString() ?? "";
                        if (!string.IsNullOrEmpty(zhName)) add(zhName, id, "exact_name_zh");
                    }
                    if (name.TryGetProperty("en", out var en))
                    {
                        var enName = en.GetString() ?? "";
                        if (!string.IsNullOrEmpty(enName)) add(enName, id, "exact_name_en");
                    }
                }
                if (el.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Object)
                {
                    foreach (var lang in new[] { "zh", "en" })
                    {
                        if (al.TryGetProperty(lang, out var list) && list.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var a in list.EnumerateArray())
                            {
                                var s = a.GetString() ?? "";
                                if (!string.IsNullOrEmpty(s)) add(s, id, "exact_alias");
                            }
                        }
                    }
                }
            }
        }
    }

    private static void WalkFlowNames(JsonElement node, Action<string, string, string> add)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString() ?? "";
                if (!string.IsNullOrEmpty(id))
                {
                    if (node.TryGetProperty("name", out var name)
                        && name.ValueKind == JsonValueKind.Object)
                    {
                        if (name.TryGetProperty("zh", out var zh))
                        {
                            var zhName = zh.GetString() ?? "";
                            if (!string.IsNullOrEmpty(zhName)) add(zhName, id, "exact_name_zh");
                        }
                        if (name.TryGetProperty("en", out var en))
                        {
                            var enName = en.GetString() ?? "";
                            if (!string.IsNullOrEmpty(enName)) add(enName, id, "exact_name_en");
                        }
                    }
                    // flow 节点别名（与概念同形）——如 give_starting_food 别名「开局食物」
                    if (node.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var lang in new[] { "zh", "en" })
                        {
                            if (al.TryGetProperty(lang, out var list) && list.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var a in list.EnumerateArray())
                                {
                                    var s = a.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(s)) add(s, id, "exact_alias");
                                }
                            }
                        }
                    }
                }
            }
            foreach (var prop in node.EnumerateObject())
                WalkFlowNames(prop.Value, add);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                WalkFlowNames(item, add);
        }
    }
}
