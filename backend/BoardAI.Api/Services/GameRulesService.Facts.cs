using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BoardAI.Api.Infrastructure;
using BoardAI.Api.Models;
using Microsoft.Extensions.Options;

namespace BoardAI.Api.Services;

public partial class GameRulesService
{

    // ---- 事实卡（广播第三站）：数量/计分题的程序化计算 ----
    // 程序算得出的数不交给 LLM 从散文里读：容量公式求值、计分表区间查表、
    // quantity.numeric 分支选择与求和。数据未结构化时静默返回 null（既有散文路径兜底）。

    /// <summary>分数词：问题在问计分/得分（含「扣1分」「+3分」这类数字嵌入形式）。</summary>
    private static readonly Regex ScoreWordRegex = new(
        @"计分|得分|扣分|加分|几分|多少分|算分|分数|\d+\s*分|VP", RegexOptions.Compiled);


    /// <summary>数量词：问题在问数量/容量/上限。</summary>
    private static readonly Regex QuantityWordRegex = new(
        @"几个|几只|几头|几块|几根|几间|几格|几多|几张|多少|上限|容量|能养|能放|拿几|放几|给几|得几",
        RegexOptions.Compiled);


    private static readonly Regex IntegerRegex = new(@"\d+", RegexOptions.Compiled);


    private const int MaxFacts = 6;


    /// <summary>
    /// 事实卡入口：数量词触发 quantity/容量事实，分数词触发计分表事实。
    /// 返回 null = 无可抽取的结构化事实（序列化时 Facts 字段省略）。
    /// </summary>
    private List<JsonElement>? ExtractFacts(string game, List<JsonElement> matched, string question)
    {
        if (string.IsNullOrWhiteSpace(question) || matched.Count == 0) return null;
        var scoreQ = ScoreWordRegex.IsMatch(question);
        var quantityQ = QuantityWordRegex.IsMatch(question);
        if (!scoreQ && !quantityQ) return null;

        var facts = new List<object>();
        if (scoreQ) ExtractScoreFacts(game, matched, question, facts);
        if (quantityQ) ExtractQuantityFacts(matched, question, facts);
        if (facts.Count == 0) return null;

        return facts.Select(f => JsonSerializer.SerializeToElement(f, RelaxedJsonOptions)).ToList();
    }


    private string? _scoreTableGame;

    private JsonElement? _scoreTable;


    /// <summary>flow 终局计分里的结构化计分表（缓存；JSON 修改后需重启 API）。</summary>
    private JsonElement? GetScoreTable(string game)
    {
        if (_scoreTableGame == game && _scoreTable.HasValue) return _scoreTable;
        var flow = LoadGameFlow(game);
        _scoreTableGame = game;
        _scoreTable = flow == null ? null : FindKeyInTree(flow.RootElement, "score_table");
        return _scoreTable;
    }


    private static JsonElement? FindKeyInTree(JsonElement node, string key)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty(key, out var v)) return v;
            foreach (var prop in node.EnumerateObject())
            {
                var r = FindKeyInTree(prop.Value, key);
                if (r.HasValue) return r;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                var r = FindKeyInTree(item, key);
                if (r.HasValue) return r;
            }
        }
        return null;
    }


    /// <summary>
    /// 计分事实：从计分表选问题涉及的类别（查询命中的概念 / 问题提到类别名或别名 / 表级别名），
    /// 问题含唯一整数时程序直接查表给数。一类别都没选中或超上限时整表返回。
    /// </summary>
    private void ExtractScoreFacts(string game, List<JsonElement> matched, string question, List<object> facts)
    {
        var table = GetScoreTable(game);
        if (!table.HasValue || table.Value.ValueKind != JsonValueKind.Object) return;

        // 命中概念的本地 id 集合——查询目标本身就是计分类别（如 field_tile）时直接选中该类别
        var matchedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in matched)
        {
            var id = GetElementId(m);
            if (id.Contains("::")) id = id[(id.IndexOf("::", StringComparison.Ordinal) + 2)..];
            matchedIds.Add(id);
        }

        // 全部类别条目按 what 引用索引（<field_tile> → 条目）
        var byRef = new Dictionary<string, (string Group, JsonElement Entry)>(StringComparer.Ordinal);
        foreach (var group in new[] { "by_count", "per_unit", "by_material", "by_card" })
        {
            if (!table.Value.TryGetProperty(group, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var entry in arr.EnumerateArray())
            {
                var r = EntryRef(entry);
                if (r != null) byRef.TryAdd(r, (group, entry));
            }
        }

        var selected = new Dictionary<string, (string Group, JsonElement Entry)>(StringComparer.Ordinal);
        foreach (var (r, ge) in byRef)
        {
            if (matchedIds.Contains(Unwrap(r)) || CategoryMatchesQuestion(ge.Entry, question))
                selected.TryAdd(r, ge);
        }

        // 表级别名（动物→羊/野猪/牛；作物→谷物/蔬菜）
        if (table.Value.TryGetProperty("aliases", out var ta) && ta.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in ta.EnumerateArray())
            {
                var az = a.TryGetProperty("zh", out var z) && z.ValueKind == JsonValueKind.String
                    ? z.GetString() ?? ""
                    : "";
                if (az.Length < 2 || !question.Contains(az, StringComparison.Ordinal)) continue;
                if (!a.TryGetProperty("whats", out var whats) || whats.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var w in whats.EnumerateArray())
                {
                    var wid = w.GetString();
                    if (wid != null && byRef.TryGetValue(wid, out var ge)) selected.TryAdd(wid, ge);
                }
            }
        }

        if (selected.Count == 0 || selected.Count > MaxFacts)
        {
            facts.Add(new Dictionary<string, object?> { ["kind"] = "score_table", ["table"] = table.Value });
            return;
        }

        // 问题中的唯一整数 → 程序查表/乘法直接给数（「3 个空农场格」→ -3；「6 头牛」→ +4）
        var ints = new List<int>();
        foreach (Match m in IntegerRegex.Matches(question)) ints.Add(int.Parse(m.Value));
        var n = ints.Count == 1 ? ints[0] : (int?)null;

        foreach (var (_, ge) in selected)
        {
            var (group, entry) = ge;
            var fact = new Dictionary<string, object?>
            {
                ["kind"] = GroupToKind(group),
                ["subject"] = EntryRef(entry),
                ["zh"] = EntryZh(entry)
            };
            switch (group)
            {
                case "by_count":
                    fact["rows"] = entry.GetProperty("rows");
                    if (entry.TryGetProperty("note", out var note)) fact["note"] = note;
                    if (n.HasValue)
                        foreach (var row in entry.GetProperty("rows").EnumerateArray())
                        {
                            var min = row.GetProperty("min").GetInt32();
                            var max = row.TryGetProperty("max", out var mx) && mx.ValueKind == JsonValueKind.Number
                                ? (int?)mx.GetInt32()
                                : null;
                            if (n >= min && (max == null || n <= max))
                                fact["computed"] = new { count = n.Value, vp = row.GetProperty("vp").GetInt32() };
                        }
                    break;
                case "per_unit":
                    fact["vp"] = entry.GetProperty("vp").GetInt32();
                    if (entry.TryGetProperty("unit", out var unit)) fact["unit"] = unit;
                    var d = entry.TryGetProperty("divisor", out var dv) && dv.ValueKind == JsonValueKind.Number
                        ? dv.GetInt32()
                        : 1;
                    if (d != 1) fact["divisor"] = d;
                    if (n.HasValue)
                        fact["computed"] = new { count = n.Value, vp = entry.GetProperty("vp").GetInt32() * n.Value / d };
                    break;
                case "by_material":
                    fact["rows"] = entry.GetProperty("rows");
                    break;
                default: // by_card
                    if (entry.TryGetProperty("rule", out var rule)) fact["rule"] = rule;
                    break;
            }
            facts.Add(fact);
        }
    }


    private static string? GroupToKind(string group) => group switch
    {
        "by_count" => "score_rows",
        "per_unit" => "score_per_unit",
        "by_material" => "score_by_material",
        _ => "score_by_card"
    };


    /// <summary>类别条目的 what 引用（&lt;field_tile&gt;）；无则 null。</summary>
    private static string? EntryRef(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object
        && entry.TryGetProperty("what", out var w) && w.ValueKind == JsonValueKind.String
            ? w.GetString()
            : null;


    private static string Unwrap(string r) =>
        r.Contains("::") ? r[(r.IndexOf("::", StringComparison.Ordinal) + 2)..] : r.Trim('<', '>');


    private static string? EntryZh(JsonElement entry) =>
        entry.TryGetProperty("zh", out var z) && z.ValueKind == JsonValueKind.String
            ? z.GetString()
            : null;


    /// <summary>问题提到类别 zh 名或别名即选中（别名允许单字——「田」「牛」）。</summary>
    private static bool CategoryMatchesQuestion(JsonElement entry, string question)
    {
        var zh = EntryZh(entry);
        if (!string.IsNullOrEmpty(zh) && question.Contains(zh, StringComparison.Ordinal)) return true;
        if (entry.TryGetProperty("aliases", out var als) && als.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in als.EnumerateArray())
            {
                var s = a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                if (!string.IsNullOrEmpty(s) && question.Contains(s, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }


    /// <summary>
    /// 数量事实：容量公式（代入示例绑定程序求值）+ quantity.numeric 分支（按问题关键词选分支）。
    /// 选中分支是全部分支的真子集时求和（播种谷物：自己 1 + 供应 2 = 3）；
    /// 无分支匹配时全部摆出不求和（互斥选项，如起始玩家 2 / 其他玩家 3）。
    /// </summary>
    private void ExtractQuantityFacts(List<JsonElement> matched, string question, List<object> facts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in matched)
        {
            var id = GetElementId(el);
            if (string.IsNullOrEmpty(id) || !seen.Add(id) || el.ValueKind != JsonValueKind.Object)
                continue;
            var subject = $"<{id}>";

            // 容量公式：程序代入示例绑定求值（牧场容量 = 2 × 格数 × 2^马厩数 → 16）
            if (el.TryGetProperty("capacity", out var cap) && cap.ValueKind == JsonValueKind.Object
                && cap.TryGetProperty("formula", out var formula))
            {
                var fact = new Dictionary<string, object?>
                {
                    ["kind"] = "capacity_formula",
                    ["subject"] = subject,
                    ["formula"] = formula
                };
                if (cap.TryGetProperty("examples", out var exs) && exs.ValueKind == JsonValueKind.Array)
                {
                    var computed = new List<object>();
                    foreach (var ex in exs.EnumerateArray())
                    {
                        if (ex.ValueKind != JsonValueKind.Object
                            || !ex.TryGetProperty("bindings", out var b))
                            continue;
                        var r = EvalFormula(formula, b);
                        if (!r.HasValue) continue;
                        var zh = ex.TryGetProperty("zh", out var z) && z.ValueKind == JsonValueKind.String
                            ? z.GetString()
                            : null;
                        computed.Add(new { zh, result = FormatNumber(r.Value) });
                    }
                    if (computed.Count > 0) fact["computed"] = computed;
                }
                facts.Add(fact);
            }

            // quantity.numeric：收集子树里所有转移数量分支
            var branches = new List<JsonElement>();
            WalkNumericQuantities(el, branches);
            if (branches.Count == 0) continue;

            var selectedBranches = new List<JsonElement>();
            foreach (var b in branches)
            {
                if (BranchMatchesQuestion(b, question)) selectedBranches.Add(b);
            }
            if (selectedBranches.Count == 0) selectedBranches = branches;

            var display = new List<object>();
            string? unit = null;
            foreach (var b in selectedBranches)
            {
                var item = new Dictionary<string, object?> { ["value"] = b.GetProperty("value").GetInt32() };
                if (b.TryGetProperty("when", out var when) && when.ValueKind == JsonValueKind.Object
                    && when.TryGetProperty("zh", out var wz) && wz.ValueKind == JsonValueKind.String)
                    item["when"] = wz.GetString();
                if (b.TryGetProperty("per", out var per) && per.ValueKind == JsonValueKind.Object
                    && per.TryGetProperty("zh", out var pz) && pz.ValueKind == JsonValueKind.String)
                    item["per"] = pz.GetString();
                if (b.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object
                    && from.TryGetProperty("zh", out var fz) && fz.ValueKind == JsonValueKind.String)
                    item["from"] = fz.GetString();
                if (unit == null && b.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String)
                    unit = u.GetString();
                display.Add(item);
            }

            var qfact = new Dictionary<string, object?>
            {
                ["kind"] = "quantity_numeric",
                ["subject"] = subject,
                ["branches"] = display
            };
            if (unit != null) qfact["unit"] = unit;
            if (selectedBranches.Count > 0 && selectedBranches.Count < branches.Count)
                qfact["total"] = selectedBranches.Sum(b => b.GetProperty("value").GetInt32());
            facts.Add(qfact);
        }
    }


    /// <summary>收集子树中所有 quantity.numeric 的分支数组。</summary>
    private static void WalkNumericQuantities(JsonElement node, List<JsonElement> branches)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("numeric", out var num) && num.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in num.EnumerateArray())
                {
                    if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("value", out var v)
                        && v.ValueKind == JsonValueKind.Number)
                        branches.Add(b);
                }
            }
            foreach (var prop in node.EnumerateObject())
                WalkNumericQuantities(prop.Value, branches);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                WalkNumericQuantities(item, branches);
        }
    }


    /// <summary>分支选择：无条件分支恒选；when.zh 的任一词（、，/空格分隔）出现在问题中即选中。</summary>
    private static bool BranchMatchesQuestion(JsonElement branch, string question)
    {
        if (!branch.TryGetProperty("when", out var when)) return true;
        if (when.ValueKind != JsonValueKind.Object || !when.TryGetProperty("zh", out var zh)
            || zh.ValueKind != JsonValueKind.String)
            return true;
        foreach (var seg in zh.GetString()!.Split('、', '，', ',', '/', ' '))
        {
            if (seg.Length > 0 && question.Contains(seg, StringComparison.Ordinal)) return true;
        }
        return false;
    }


    /// <summary>求值小公式：op ∈ {*, +, ^}；operand 为数字 / 绑定变量（{"var": name}）/ 嵌套公式。</summary>
    private static double? EvalFormula(JsonElement node, JsonElement bindings)
    {
        if (node.ValueKind == JsonValueKind.Number) return node.GetDouble();
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("var", out var v) && v.ValueKind == JsonValueKind.String
                && bindings.ValueKind == JsonValueKind.Object
                && bindings.TryGetProperty(v.GetString()!, out var bv))
                return bv.ValueKind == JsonValueKind.Number ? bv.GetDouble() : null;

            if (node.TryGetProperty("op", out var op) && node.TryGetProperty("operands", out var ops)
                && ops.ValueKind == JsonValueKind.Array)
            {
                var vals = new List<double>();
                foreach (var o in ops.EnumerateArray())
                {
                    var r = EvalFormula(o, bindings);
                    if (!r.HasValue) return null;
                    vals.Add(r.Value);
                }
                if (vals.Count == 0) return null;
                return op.GetString() switch
                {
                    "*" => vals.Aggregate(1.0, (a, b) => a * b),
                    "+" => vals.Sum(),
                    "^" => vals.Skip(1).Aggregate(vals[0], (a, b) => Math.Pow(a, b)),
                    _ => null
                };
            }
        }
        return null;
    }


    /// <summary>整数则去掉小数点（16.0 → 16），否则保留原值。</summary>
    private static object FormatNumber(double d)
    {
        var rounded = Math.Round(d);
        return Math.Abs(d - rounded) < 1e-9 ? (object)(long)rounded : d;
    }


    /// <summary>提取概念顶层的指定字段（无则 null）。</summary>
    private static JsonElement? ExtractTopField(JsonElement element, string key)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var v)
            ? v
            : null;
    }
}
