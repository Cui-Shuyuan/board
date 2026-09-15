// BoardGameTutorial
// 语义 → 视觉 绑定层。
//
// 分工：
//   concepts.json / flow.json  定义「源、目的地、对象、数量」这些语义事实；
//   stage 的 visual 段          只回答「gem_supply 这块语义区域，画在屏幕哪里」；
//   cue 数据                    只回答「第几秒发生」。
//
// 所以「拿取三枚不同色宝石」不需要在动画数据里重写 gem_supply → player_holding，
// 而是按语义 id 去 flow / concepts 里查到这次 transfer 的 source / destination /
// quantity，再用本类把语义 id 解析成画面上的 zone 和具体组件。
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BoardGameTutorial
{
    public class SemanticMap
    {
        private readonly Dictionary<string, VisualZone> zonesByConcept = new Dictionary<string, VisualZone>();
        private readonly Dictionary<string, Dictionary<string, string>> zoneByColor =
            new Dictionary<string, Dictionary<string, string>>();

        public IEnumerable<VisualZone> Zones => zonesByConcept.Values;

        public void Load(StageVisual visual)
        {
            zonesByConcept.Clear();
            zoneByColor.Clear();
            if (visual?.zones == null) return;

            foreach (var zone in visual.zones)
            {
                if (string.IsNullOrEmpty(zone.concept)) continue;
                zonesByConcept[zone.concept] = zone;

                if (zone.colors != null && zone.colors.Count > 0)
                {
                    var map = new Dictionary<string, string>();
                    foreach (var entry in zone.colors)
                        if (!string.IsNullOrEmpty(entry.color) && !string.IsNullOrEmpty(entry.zone))
                            map[entry.color] = entry.zone;
                    zoneByColor[zone.concept] = map;
                }
            }
        }

        public VisualZone Resolve(string conceptId)
        {
            if (string.IsNullOrEmpty(conceptId)) return null;

            string key = Normalize(conceptId);
            if (zonesByConcept.TryGetValue(key, out var zone)) return zone;
            // 也允许直接写 zone id（调试 / 特例）
            if (zonesByConcept.TryGetValue(conceptId, out zone)) return zone;
            return null;
        }

        /// <summary>语义区域 + 颜色 → 画面 zone。例如 gem_supply + diamond → gem_supply_diamond。</summary>
        public string ResolveZone(string conceptId, string color)
        {
            var visual = Resolve(conceptId);
            if (visual == null) return null;

            if (!string.IsNullOrEmpty(color) &&
                zoneByColor.TryGetValue(Normalize(conceptId), out var byColor) &&
                byColor.TryGetValue(color, out var zoneId))
            {
                return zoneId;
            }
            return visual.zone;
        }

        private static string Normalize(string conceptId)
        {
            if (string.IsNullOrEmpty(conceptId)) return conceptId;
            return conceptId.Trim().TrimStart('<').TrimEnd('>')
                .Replace("ontology::", "");
        }
    }

    // ── JSON 模型（stage.visual）────────────────────────────────────────

    [System.Serializable]
    public class StageVisual
    {
        public List<VisualZone> zones;
    }

    [System.Serializable]
    public class VisualZone
    {
        /// <summary>语义 id，例如 gem_supply / ontology::player_holding。</summary>
        public string concept;

        /// <summary>画面上的 zone id。</summary>
        public string zone;

        /// <summary>同一语义区域在画面上按颜色分成多堆时（宝石供应堆）。</summary>
        public List<VisualColorZone> colors;
    }

    [System.Serializable]
    public class VisualColorZone
    {
        public string color;
        public string zone;
    }

    // ── 语义事实（flow / concepts 侧查到的结果）─────────────────────────

    /// <summary>flow 或 concepts 里定义的一次语义操作。</summary>
    public class SemanticOp
    {
        public string NodeId;
        public string Kind;          // ontology::transfer / shuffle / random_draw / top_draw / play / state_change
        public string Source;
        public string Destination;
        public string ObjectType;
        public string Color;

        /// <summary>quantity 的原始写法（数字、"all"、表达式或说明性文本）。</summary>
        public string QuantityRaw;
        public int Quantity;

        public string Describe() =>
            $"{Kind} {ObjectType} {Source} -> {Destination} x{Quantity} ({QuantityRaw})";
    }

    /// <summary>
    /// 从 flow.json / concepts.json 里按节点 id 查语义事实。
    /// 只读、确定性；运行时不改数据。
    /// </summary>
    public static class SemanticFlow
    {
        private static readonly string[] OpKeys =
        {
            "transfer", "shuffle", "random_draw", "top_draw", "play", "state_change",
        };

        private static string cacheRoot;
        private static readonly Dictionary<string, Dictionary<string, object>> cache =
            new Dictionary<string, Dictionary<string, object>>();

        /// <summary>加载一个游戏目录下的 flow.json 与 concepts.json 并建立 id 索引。</summary>
        public static void Load(string gameRoot)
        {
            if (cacheRoot == gameRoot && cache.ContainsKey(gameRoot)) return;

            var index = new Dictionary<string, object>();
            IndexFile(Path.Combine(gameRoot, "flow.json"), index);
            IndexFile(Path.Combine(gameRoot, "concepts.json"), index);
            cache[gameRoot] = index;
            cacheRoot = gameRoot;
        }

        public static int IndexedCount(string gameRoot)
        {
            return cache.TryGetValue(gameRoot, out var index) ? index.Count : 0;
        }

        /// <summary>查一个语义节点；找不到返回 null。</summary>
        public static SemanticOp Find(string gameRoot, string nodeId, string color = null)
        {
            if (string.IsNullOrEmpty(nodeId)) return null;
            if (!cache.TryGetValue(gameRoot, out var index)) return null;

            // flow 里可能直接有同名节点；否则去 concepts 的 action 定义里找内层 transfer。
            if (index.TryGetValue(nodeId, out var node) && node is Dictionary<string, object> dict)
            {
                var op = Extract(nodeId, dict, color);
                if (op != null) return op;
            }

            // 节点存在但自身不是操作（例如 concepts 里的 action）：往下找工作核。
            if (index.TryGetValue(nodeId, out node) && node is Dictionary<string, object> outer)
            {
                var found = FindOpDeep(outer, color);
                if (found != null)
                {
                    found.NodeId = nodeId;
                    return found;
                }
            }
            return null;
        }

        private static void IndexFile(string path, Dictionary<string, object> index)
        {
            if (!File.Exists(path)) return;
            var parsed = MiniJson.Parse(File.ReadAllText(path));
            IndexNode(parsed, index);
        }

        private static void IndexNode(object node, Dictionary<string, object> index)
        {
            if (node is Dictionary<string, object> dict)
            {
                if (dict.TryGetValue("id", out var idObj) && idObj is string id && !string.IsNullOrEmpty(id))
                {
                    if (!index.ContainsKey(id)) index[id] = dict;
                }
                foreach (var value in dict.Values) IndexNode(value, index);
            }
            else if (node is List<object> list)
            {
                foreach (var item in list) IndexNode(item, index);
            }
        }

        /// <summary>flow / concepts 里的键名带 &lt;ontology::&gt; 前缀，两种写法都要认。</summary>
        private static string OpKeyOf(Dictionary<string, object> node, string key)
        {
            if (node == null) return null;
            if (node.TryGetValue(key, out var plain) && plain is Dictionary<string, object>) return key;
            string namespaced = "<ontology::" + key + ">";
            if (node.TryGetValue(namespaced, out var ns) && ns is Dictionary<string, object>) return namespaced;
            namespaced = "<" + key + ">";
            if (node.TryGetValue(namespaced, out var shortNs) && shortNs is Dictionary<string, object>) return namespaced;
            return null;
        }

        private static SemanticOp Extract(string nodeId, Dictionary<string, object> node, string color)
        {
            foreach (var key in OpKeys)
            {
                string actualKey = OpKeyOf(node, key);
                if (actualKey == null) continue;
                var op = (Dictionary<string, object>)node[actualKey];

                var result = BuildOp(node, op, key);
                if (result == null) continue;
                result.NodeId = nodeId;
                result.Color = color ?? FirstColor(op);
                return result;
            }
            return null;
        }

        private static SemanticOp FindOpDeep(object node, string color)
        {
            if (node is Dictionary<string, object> dict)
            {
                var direct = Extract(null, dict, color);
                if (direct != null) return direct;

                foreach (var value in dict.Values)
                {
                    var found = FindOpDeep(value, color);
                    if (found != null) return found;
                }
            }
            else if (node is List<object> list)
            {
                foreach (var item in list)
                {
                    var found = FindOpDeep(item, color);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private static SemanticOp BuildOp(Dictionary<string, object> node, Dictionary<string, object> op, string kind)
        {
            var result = new SemanticOp { Kind = kind };

            result.Source = GetString(op, "source") ?? GetString(node, "source");
            result.Destination = GetString(op, "destination") ?? GetString(node, "destination");
            result.ObjectType = GetString(op, "<ontology::object>") ?? GetString(op, "object")
                                ?? GetString(node, "<ontology::object>") ?? GetString(node, "object");

            object quantity = null;
            if (op.TryGetValue("quantity", out var q)) quantity = q;
            else if (node.TryGetValue("quantity", out var q2)) quantity = q2;
            if (quantity != null)
            {
                result.QuantityRaw = quantity.ToString();
                result.Quantity = ParseQuantity(quantity);
            }

            if (string.IsNullOrEmpty(result.Source) && string.IsNullOrEmpty(result.Destination))
            {
                // shuffle / state_change 这类没有 source/destination，只有 target
                result.Source = null;
            }
            return result;
        }

        private static string FirstColor(Dictionary<string, object> op)
        {
            if (op.TryGetValue("color", out var c) && c is string s) return s;
            return null;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            if (dict != null && dict.TryGetValue(key, out var value) && value is string s) return s;
            return null;
        }

        private static int ParseQuantity(object quantity)
        {
            if (quantity is double d) return Mathf.RoundToInt((float)d);
            if (quantity is long l) return (int)l;
            if (quantity is int i) return i;
            if (quantity is string s && int.TryParse(s, out int parsed)) return parsed;
            return 1;
        }
    }
}
