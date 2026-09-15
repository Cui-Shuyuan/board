// BoardGameTutorial
// zone 占用状态的运行时账本。
//
// 动画 = 维护组件状态；组件状态 = (zone, 槽位序号)。位置永远是算出来的，
// 不是存下来的：世界坐标 = zone.center + 槽位偏移。所以「宝石从供应堆飞到
// 持有区」在数据上只是一次 MoveTo；视觉位置由两端 zone 自动推导。
//
// 账本只保存每个 zone 里有哪几件组件、顺序如何，不缓存槽位表，
// 因此不会出现「槽位表和实际情况不一致」的状态。
using System.Collections.Generic;
using UnityEngine;

namespace BoardGameTutorial
{
    /// <summary>一个组件的运行时状态。</summary>
    public class ZoneItem
    {
        public string Id;              // 运行时实例 id，例如 gem#3
        public StageTemplate Template; // 外观
        public string PaletteName;     // 色板名（身份的一部分，别拿 Color 值比较）
        public Color BaseColor;        // 调色后的基础色（alpha 另算）

        public string ZoneId;          // 当前所在 zone
        public int Order = -1;         // zone 内顺序，决定落在哪个槽位

        /// <summary>在 offstage 时的入场方向与基准落点（用于「从盒子飞进来」的起点）。</summary>
        public string EntryFrom = "auto";
        public Vector3 EntryAnchor;

        public CueAnimActor Actor;     // 渲染实例

        /// <summary>是否已翻到另一面（正面朝上的卡牌为 true）。</summary>
        public bool Flipped;

        public Vector3 LivePosition;
        public Vector3 LiveScale;
        public float LiveRotation;
        public float LiveAlpha;
    }

    public class ZoneStore
    {
        private readonly Dictionary<string, StageZone> zones = new Dictionary<string, StageZone>();
        private readonly Dictionary<string, StageTemplate> templates = new Dictionary<string, StageTemplate>();
        private readonly Dictionary<string, ZoneItem> items = new Dictionary<string, ZoneItem>();
        private readonly Dictionary<string, List<ZoneItem>> occupancy = new Dictionary<string, List<ZoneItem>>();
        private readonly Dictionary<string, int> counters = new Dictionary<string, int>();

        /// <summary>调试：打印 PullFrom 的匹配数量。</summary>
        public bool logPull;

        public StageDoc Stage { get; private set; }
        public IEnumerable<ZoneItem> Items => items.Values;
        public IEnumerable<StageZone> Zones => zones.Values;

        public void LoadStage(StageDoc stage)
        {
            Stage = stage;
            zones.Clear();
            templates.Clear();

            if (stage == null) return;

            if (stage.zones != null)
                foreach (var zone in stage.zones)
                    if (!string.IsNullOrEmpty(zone.id)) zones[zone.id] = zone;

            if (stage.templates != null)
                foreach (var tpl in stage.templates)
                    if (!string.IsNullOrEmpty(tpl.id)) templates[tpl.id] = tpl;

            if (stage.board == null) stage.board = new StageBoard();
        }

        public StageZone GetZone(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return zones.TryGetValue(id, out var zone) ? zone : null;
        }

        public StageTemplate GetTemplate(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return templates.TryGetValue(id, out var tpl) ? tpl : null;
        }

        // ── 生命周期 ──────────────────────────────────────────────────────

        public void Reset()
        {
            occupancy.Clear();
            items.Clear();
            counters.Clear();
        }

        /// <summary>按牌桌的 initial 摆好开局状态。</summary>
        public void ApplyInitial()
        {
            Reset();
            if (Stage?.initial == null) return;
            foreach (var entry in Stage.initial)
                Spawn(entry.template, entry.palette, entry.zone, Mathf.Max(1, entry.count));
        }

        /// <summary>批量生成组件并放进指定 zone（开局摆放 / 从镜头外入场都走这里）。</summary>
        public List<ZoneItem> Spawn(string templateId, string palette, string zoneId, int count, string from = null)
        {
            var created = new List<ZoneItem>();
            var tpl = GetTemplate(templateId);
            if (tpl == null)
            {
                Debug.LogWarning($"[ZoneStore] unknown template '{templateId}'");
                return created;
            }

            var list = ListOf(zoneId);
            for (int i = 0; i < count; i++)
            {
                if (!counters.TryGetValue(templateId, out int n)) n = 0;
                counters[templateId] = n + 1;

                var colorName = string.IsNullOrEmpty(palette) ? tpl.palette : palette;
                var baseColor = Palette.TintFor(tpl.shape, colorName);
                var item = new ZoneItem
                {
                    Id = $"{templateId}#{n + 1}",
                    Template = tpl,
                    PaletteName = colorName,
                    BaseColor = baseColor,
                    ZoneId = zoneId,
                    Order = list != null ? list.Count : 0,
                    EntryFrom = string.IsNullOrEmpty(from) ? "auto" : from,
                    EntryAnchor = AnchorFor(zoneId, from),
                    LiveScale = Vector3.one,
                    LiveRotation = tpl.rotation,
                    LiveAlpha = tpl.alpha,
                };
                items[item.Id] = item;
                list?.Add(item);
                item.LivePosition = CurrentPosition(item);
                created.Add(item);
            }
            return created;
        }

        /// <summary>zone 里当前有多少件（给叠压居中用）。</summary>
        public int CountInZone(string zoneId)
        {
            var list = string.IsNullOrEmpty(zoneId) ? null : (occupancy.TryGetValue(zoneId, out var l) ? l : null);
            return list?.Count ?? 0;
        }

        private int NextOrder(string zoneId)
        {
            var list = string.IsNullOrEmpty(zoneId) ? null : (occupancy.TryGetValue(zoneId, out var l) ? l : null);
            return list == null ? 0 : list.Count;
        }

        private List<ZoneItem> ListOf(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return null;
            if (!occupancy.TryGetValue(zoneId, out var list))
            {
                list = new List<ZoneItem>();
                occupancy[zoneId] = list;
            }
            return list;
        }

        public int CountIn(string zoneId, string palette = null, string template = null)
        {
            var list = string.IsNullOrEmpty(zoneId) ? null : (occupancy.TryGetValue(zoneId, out var l) ? l : null);
            if (list == null) return 0;
            int n = 0;
            foreach (var item in list)
            {
                if (!string.IsNullOrEmpty(palette) && item.PaletteName != palette) continue;
                if (!string.IsNullOrEmpty(template) && item.Template?.id != template) continue;
                n++;
            }
            return n;
        }

        /// <summary>zone 里最靠前（顺序最前）的组件；excluded 用于一次搬多件。</summary>
        public ZoneItem FrontOf(string zoneId, List<ZoneItem> excluded = null)
        {
            var list = string.IsNullOrEmpty(zoneId) ? null : (occupancy.TryGetValue(zoneId, out var l) ? l : null);
            if (list == null) return null;

            ZoneItem best = null;
            foreach (var item in list)
            {
                if (excluded != null && excluded.Contains(item)) continue;
                if (best == null || item.Order < best.Order) best = item;
            }
            return best;
        }

        /// <summary>
        /// 从 srcZone 里挑最多 count 个匹配 (template, palette) 的组件搬到 dstZone。
        /// 返回实际搬走的数量。用于「把已经在镜头外的组件拿到桌面上」，
        /// 避免为了就位而重复生成一份。
        /// </summary>
        public int PullFrom(string srcZone, string template, string palette, string dstZone, int count)
        {
            if (string.IsNullOrEmpty(srcZone) || string.IsNullOrEmpty(dstZone)) return 0;
            var source = occupancy.TryGetValue(srcZone, out var list) ? list : null;
            if (source == null) return 0;

            var matches = new List<ZoneItem>();
            foreach (var item in source)
            {
                if (!string.IsNullOrEmpty(template) && item.Template?.id != template) continue;
                if (!string.IsNullOrEmpty(palette) && item.PaletteName != palette) continue;
                matches.Add(item);
            }
            matches.Sort((a, b) => a.Order.CompareTo(b.Order));
            if (logPull) Debug.Log($"[ZoneStore.PullFrom] {srcZone}->{dstZone} 匹配 {matches.Count} 个（要 {count} 个）");

            int moved = 0;
            foreach (var item in matches)
            {
                if (moved >= count) break;
                MoveTo(item, dstZone);
                moved++;
            }
            return moved;
        }

        /// <summary>
        /// 把组件搬到指定 zone 的指定顺序位（用于「这张牌放在第 3 个格子」）。
        /// order 之后的组件依次后移，之后重新按顺序编号。
        /// </summary>
        /// <summary>调试：打印 MoveToAt 的请求与结果。</summary>
        public bool logMoves;

        public void MoveToAt(ZoneItem item, string targetZoneId, int order)
        {
            MoveTo(item, targetZoneId);
            var list = string.IsNullOrEmpty(targetZoneId) ? null
                : (occupancy.TryGetValue(targetZoneId, out var l) ? l : null);
            if (list == null) return;

            list.Remove(item);
            int at = Mathf.Clamp(order, 0, list.Count);
            list.Insert(at, item);
            for (int i = 0; i < list.Count; i++) list[i].Order = i;
            if (logMoves)
                Debug.Log($"[MoveToAt] {item.Id} → {targetZoneId} 请求 {order} 实际 {item.Order} 共 {list.Count} 件");
        }

        /// <summary>把组件搬到另一个 zone，成为该 zone 的最后一件。</summary>
        public void MoveTo(ZoneItem item, string targetZoneId)
        {
            if (item == null || string.IsNullOrEmpty(targetZoneId)) return;
            if (!zones.ContainsKey(targetZoneId))
            {
                Debug.LogWarning($"[ZoneStore] unknown zone '{targetZoneId}'");
                return;
            }
            if (item.ZoneId == targetZoneId) return;

            // 从镜头外第一次落到桌面上时，记下它在桌内的落点，
            // 之后若再被移回 offstage，起点仍然对准这个位置。
            var targetZone = GetZone(targetZoneId);
            if (targetZone != null && targetZone.role != "offstage")
                item.EntryAnchor = ZonePosition(targetZoneId, NextOrder(targetZoneId));

            if (!string.IsNullOrEmpty(item.ZoneId) && occupancy.TryGetValue(item.ZoneId, out var from))
            {
                from.Remove(item);
                // 顺位前移：后面的人补上空缺，视觉上就是堆变小、往前收拢。
                for (int i = 0; i < from.Count; i++) from[i].Order = i;
            }

            var to = ListOf(targetZoneId);
            item.ZoneId = targetZoneId;
            item.Order = to.Count;
            to.Add(item);
        }

        // ── 坐标推导 ──────────────────────────────────────────────────────

        /// <summary>组件应该出现的世界坐标：由 (zone, 顺序) 推导。</summary>
        public Vector3 CurrentPosition(ZoneItem item)
        {
            if (item == null) return Vector3.zero;
            var zone = GetZone(item.ZoneId);
            if (zone == null) return Vector3.zero;

            if (zone.role == "offstage")
            {
                // 移动原语是 Vector3.Lerp，所以起点取「落点 + 入场方向 × margin」：
                // 多个组件落点不同、起点也不同，但共享同一段位移向量，飞入时保持相对位置。
                return item.EntryAnchor + EntryDirection(zone, item.EntryFrom) * zone.margin;
            }

            return ZonePosition(item.ZoneId, item.Order);
        }

        /// <summary>
        /// 组件落点。摆放方式由 display.mode 与 layout.type 共同决定：
        ///
        ///   display.mode = "stack"  → 一层层盖住，每层只错开一点点（40/30/20 张的牌堆）
        ///   display.mode = "count"  → 一件件都看得见，按 layout.type 摆：
        ///       "row"   单行等距排开（玩家持有区、贵族行、一排 7 枚的宝石堆）
        ///       "grid"  居中紧凑块（发展卡市场 4 列）
        /// 超出容量的部分压在最后一格并向外扩，避免整块突然移位。
        /// </summary>
        public Vector3 ZonePosition(string zoneId, int order)
        {
            var zone = GetZone(zoneId);
            if (zone == null) return Vector3.zero;
            if (zone.role == "offstage") return OffstagePosition(zone);

            var layout = zone.layout ?? new StageLayout();
            var display = zone.display;
            int capacity = zone.capacity > 0 ? zone.capacity : 12;

            float x = zone.center.x;
            float z = zone.center.z;

            int slot = order;
            int overflow = 0;
            if (slot >= capacity)
            {
                slot = capacity - 1;
                overflow = order - capacity + 1;
            }

            if (display != null && display.mode == "stack")
            {
                // 叠放显示：**超过 max_visible 个就只显示 max_visible 个**，多出来的压在最后一层。
                // （没有「一堆」这个独立概念，就是一条显示规则。）
                // 每层只错开一点点，整摞按可见层数居中：越深的层越往左上偏，最上面一件落在 zone 正中心。
                int total = Mathf.Max(1, CountInZone(zoneId));
                int visible = Mathf.Clamp(total, 1, display.max_visible > 0 ? display.max_visible : 8);
                int layer = Mathf.Min(slot, visible - 1);
                float depth = (visible - 1) * 0.5f;
                x += layer * display.dx - depth * display.dx;
                z += layer * display.dz - depth * display.dz;
                return new Vector3(x, 0f, z);
            }

            // 摆放方式：
            //   row   单行、以 zone 中心对称展开（持有区、贵族行）
            //   grid  固定列数的居中块（发展卡市场）
            //   block 按「整齐的 2x2 块」摆（宝石堆：7 枚 = 4+3 两行，紧凑且数得清）
            if (layout.type == "row")
            {
                x += (slot - (capacity - 1) * 0.5f) * layout.x_step;
            }
            else if (layout.type == "block")
            {
                int cols = Mathf.Max(1, Mathf.CeilToInt(capacity * 0.5f)); // 目标是两行
                int row = slot / cols;
                int col = slot % cols;
                int inRow = Mathf.Min(cols, capacity - row * cols);
                float cx = (inRow - 1) * 0.5f;
                x += (col - cx) * layout.x_step;
                z += (row - 0.5f) * layout.z_step;
            }
            else
            {
                int cols = Mathf.Max(1, layout.cols);
                int row = slot / cols;
                int col = slot % cols;
                float cx = (cols - 1) * 0.5f;
                x += (col - cx) * layout.x_step;
                z += row * layout.z_step;
            }

            if (overflow > 0)
            {
                x += overflow * layout.x_step * 0.10f;
                z += overflow * layout.z_step * 0.10f;
            }

            return new Vector3(x, 0f, z);
        }

        /// <summary>
        /// 镜头外的入场点。方向由 from 决定（top/bottom/left/right），
        /// auto 时从桌心朝外推。距离取 zone.margin，必须足够远，投影到屏幕后才真的在画面外。
        /// </summary>
        public Vector3 OffstagePosition(StageZone zone, string from = null)
        {
            var center = new Vector3(zone.center.x, 0f, zone.center.z);
            return center + EntryDirection(zone, from) * zone.margin;
        }

        /// <summary>入场方向（单位向量）：top/bottom/left/right；auto 时从桌心朝外推。</summary>
        public static Vector3 EntryDirection(StageZone zone, string from)
        {
            switch (from)
            {
                case "top": return new Vector3(0f, 0f, 1f);
                case "bottom": return new Vector3(0f, 0f, -1f);
                case "left": return new Vector3(-1f, 0f, 0f);
                case "right": return new Vector3(1f, 0f, 0f);
                default:
                    var dir = new Vector3(zone.center.x, 0f, zone.center.z - 0.85f);
                    if (dir.sqrMagnitude < 1e-4f) dir = new Vector3(0f, 0f, 1f);
                    return dir.normalized;
            }
        }

        /// <summary>入场方向对应的桌内基准点（组件真正的落点附近）。</summary>
        private Vector3 AnchorFor(string zoneId, string from)
        {
            var zone = GetZone(zoneId);
            if (zone == null) return Vector3.zero;
            return new Vector3(zone.center.x, 0f, zone.center.z);
        }

        /// <summary>
        /// 某件组件「从镜头外飞进来」的起点：对准它的落点，再沿桌心方向外推。
        /// 设置阶段把宝石放进供应堆时用这个。
        /// </summary>
        public Vector3 EntryFrom(ZoneItem item, string offstageZoneId)
        {
            var zone = GetZone(offstageZoneId);
            if (zone == null) return CurrentPosition(item);

            var target = CurrentPosition(item);
            var outward = new Vector3(target.x, 0f, target.z - 0.85f);
            if (outward.sqrMagnitude < 1e-4f) outward = new Vector3(0f, 0f, 1f);
            outward.Normalize();
            return target + outward * zone.margin;
        }

        /// <summary>zone 的近似世界中心（调试标注 / GUI 用）。</summary>
        public Vector3 ZoneCenter(string zoneId)
        {
            var zone = GetZone(zoneId);
            if (zone == null) return Vector3.zero;
            return new Vector3(zone.center.x, 0f, zone.center.z);
        }
    }
}
