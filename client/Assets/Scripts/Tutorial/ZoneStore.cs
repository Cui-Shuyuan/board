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
        public Color BaseColor;        // 调色后的基础色（alpha 另算）

        public string ZoneId;          // 当前所在 zone
        public int Order = -1;         // zone 内顺序，决定落在哪个槽位

        public CueAnimActor Actor;     // 渲染实例

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
        public List<ZoneItem> Spawn(string templateId, string palette, string zoneId, int count)
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

                var baseColor = Palette.Resolve(string.IsNullOrEmpty(palette) ? tpl.palette : palette);
                var item = new ZoneItem
                {
                    Id = $"{templateId}#{n + 1}",
                    Template = tpl,
                    BaseColor = baseColor,
                    ZoneId = zoneId,
                    Order = list != null ? list.Count : 0,
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
                if (!string.IsNullOrEmpty(palette) && item.BaseColor != Palette.Resolve(palette)) continue;
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
            return ZonePosition(item.ZoneId, item.Order);
        }

        public Vector3 ZonePosition(string zoneId, int order)
        {
            var zone = GetZone(zoneId);
            if (zone == null) return Vector3.zero;
            if (zone.role == "offstage") return OffstagePosition(zone);

            var layout = zone.layout ?? new StageLayout();
            int cols = Mathf.Max(1, layout.cols);
            int capacity = zone.capacity > 0 ? zone.capacity : 12;

            float x = zone.center.x;
            float z = zone.center.z;

            // 超出容量的部分压在最后一格并略微外扩，避免整堆突然移位。
            int slot = order;
            int overflow = 0;
            if (slot >= capacity)
            {
                slot = capacity - 1;
                overflow = order - capacity + 1;
            }

            if (slot > 0)
            {
                if (layout.type == "row")
                {
                    x += slot * layout.x_step;
                }
                else
                {
                    int row = slot / cols;
                    int col = slot % cols;
                    float cx = (cols - 1) * 0.5f;
                    x += (col - cx) * layout.x_step;
                    z += row * layout.z_step;
                }
            }

            if (overflow > 0)
            {
                x += overflow * layout.x_step * 0.12f;
                z += overflow * layout.z_step * 0.12f;
            }

            return new Vector3(x, 0f, z);
        }

        /// <summary>镜头外的入场点：从桌心朝该 zone 方向往外推。</summary>
        public Vector3 OffstagePosition(StageZone zone)
        {
            var forward = new Vector3(zone.center.x, 0f, zone.center.z - 0.85f);
            if (forward.sqrMagnitude < 1e-4f) forward = new Vector3(0f, 0f, 1f);
            forward.Normalize();
            return new Vector3(zone.center.x, 0f, zone.center.z) + forward * zone.margin;
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
