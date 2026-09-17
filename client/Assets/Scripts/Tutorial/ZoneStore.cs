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

        /// <summary>
        /// 画面上**实际**显示的是哪一面（由渲染器当前贴图与两面的引用比较得出）。
        ///
        /// 为什么不复用 Flipped：Flipped 是**逻辑意图**（"要不要显示卡背"），
        /// 而它可能和实际显示不一致 —— 那正是画面出问题时最难查的地方。
        /// 导出状态时用这个，才能直接看出"市场里的牌全都朝下"这类异常。
        /// </summary>
        public string Showing
        {
            get
            {
                var sr = Actor?.Renderer;
                if (sr == null || sr.sprite == null || !sr.enabled) return "hidden";
                if (Actor.BackSprite != null && ReferenceEquals(sr.sprite, Actor.BackSprite)) return "back";
                if (Actor.FaceSprite != null && ReferenceEquals(sr.sprite, Actor.FaceSprite))
                {
                    // 没有独立背图的模板（牌堆里的牌）：它的"正面"就是卡背扫描图，
                    // 所以显示 FaceSprite 实际看到的是**卡背**。
                    return Actor.BackSprite == null ? "back" : "face";
                }
                return "other";
            }
        }

        /// <summary>
        /// 身份键："模板|色板"。契约里按它统计"哪个 zone 里各有什么"。
        /// 用色板而不是只看模板名，是因为"宝石是哪种颜色"记在色板上（gem_emerald）。
        /// </summary>
        public string KindKey => (Template != null ? Template.id : "?") + "|" + (PaletteName ?? "");

        /// <summary>
        /// 是否**已经出场过**（被动画带出来过）。
        ///
        /// 现在的"还没出场"主要靠**对象根本还没被创建**（见 create 原语）来表达；
        /// 这个标记用于"预先存在、之后才被动画带出来"的那些（例如从头构建场景时的组件）。
        /// 曾经模板上有一个静态的 hide_until_animated，用它判断每帧可见性 ——
        /// 场景一重建就把已出场的牌重新隐藏（用户报的"发完12张牌后消失"）。
        /// </summary>
        public bool Shown;

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
        /// <summary>
        /// zone → (格位号 → 组件)。**格位号是固定地址**：一件东西占上第 5 格就一直待在第 5 格，
        /// 后来的东西不会让它重新编号。这是「第一张发到 (1,1)，第二张发到 (1,2)，然后都不再动」的模型。
        /// （旧模型用 List，每次插入都重排所有 Order，导致已落位的牌被改地址、又飞一次。）
        /// </summary>
        private readonly Dictionary<string, Dictionary<int, ZoneItem>> occupancy = new Dictionary<string, Dictionary<int, ZoneItem>>();
        private readonly Dictionary<string, int> counters = new Dictionary<string, int>();

        /// <summary>调试：打印 PullFrom 的匹配数量。</summary>
        public bool logPull;

        public StageDoc Stage { get; private set; }
        public IEnumerable<ZoneItem> Items => items.Values;

        /// <summary>牌桌上组件总数（O(1)，用于快速判断牌桌是否已搭好）。</summary>
        public int ActorCountHint() => items.Count;
        public IEnumerable<StageZone> Zones => zones.Values;

        // ── 格位表 ────────────────────────────────────────────────────────
        //
        // 所有 zone 的每个格位坐标，在 stage 载入时一次性算好并缓存。
        // 好处：移动/落位只需要「目标 zone + 第几格」，坐标不再是散落在各处的算术，
        // 底板贴合、断言、离屏出图都读同一张表，不可能再出现「牌和底板差一行」这类错位。

        private readonly Dictionary<string, List<Vector3>> slots = new Dictionary<string, List<Vector3>>();
        private int occupancyVersion;
        private int slotsVersion = -1;

        /// <summary>占用变化后让格位表失效（stack 显示依赖总件数）。</summary>
        private void InvalidateSlots() { occupancyVersion++; }

        /// <summary>重建全部 zone 的格位表。</summary>
        public void BuildSlotTable()
        {
            slots.Clear();
            if (Stage?.zones == null) return;
            foreach (var zone in Stage.zones)
            {
                if (zone == null || string.IsNullOrEmpty(zone.id)) continue;
                if (zone.role == "offstage") continue;
                int capacity = zone.capacity > 0 ? zone.capacity : 1;
                // stack 显示只依赖可见层数，多留一点余量便于越界时仍返回合理值
                if (zone.display != null && zone.display.mode == "stack") capacity = Mathf.Max(capacity, 16);
                var list = new List<Vector3>(capacity);
                for (int i = 0; i < capacity; i++) list.Add(ComputeZonePosition(zone.id, i));
                slots[zone.id] = list;
            }
            slotsVersion = occupancyVersion;
        }

        /// <summary>
        /// 把一件组件落到「某 zone 的第 slot 格」。这是搬运的统一入口：
        /// 坐标全部来自格位表，调用方只关心 zone 和格号。
        /// slot &lt; 0 表示追加到末尾。
        /// </summary>
        public void MoveToSlot(ZoneItem item, string zoneId, int slot)
        {
            if (item == null || string.IsNullOrEmpty(zoneId)) return;
            if (slot < 0) { MoveTo(item, zoneId); return; }
            MoveToAt(item, zoneId, slot);
        }

        /// <summary>格位坐标。表未建或已失效时自动重建。</summary>
        public Vector3 SlotAt(string zoneId, int index)
        {
            if (string.IsNullOrEmpty(zoneId)) return Vector3.zero;
            if (slotsVersion != occupancyVersion) BuildSlotTable();
            if (slots.TryGetValue(zoneId, out var list) && list.Count > 0)
            {
                if (index < 0) index = 0;
                if (index >= list.Count) index = list.Count - 1;
                return list[index];
            }
            return ComputeZonePosition(zoneId, index);
        }

        /// <summary>该 zone 有多少个格位（表未建时按容量）。</summary>
        public int SlotCount(string zoneId)
        {
            if (slotsVersion != occupancyVersion) BuildSlotTable();
            if (slots.TryGetValue(zoneId, out var list)) return list.Count;
            var zone = GetZone(zoneId);
            return zone?.capacity > 0 ? zone.capacity : 0;
        }

        public void LoadStage(StageDoc stage)
        {
            BuildSlotTable();
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

        /// <summary>移除一个组件（对象在数据上真的不存在了，而不只是看不见）。</summary>
        public bool RemoveItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId) || !items.TryGetValue(itemId, out var item) || item == null)
                return false;
            if (SlotsOf(item.ZoneId).TryGetValue(item.Order, out var at) && at == item)
                SlotsOf(item.ZoneId).Remove(item.Order);
            items.Remove(itemId);
            InvalidateSlots();
            return true;
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
                    Order = NextFreeSlot(zoneId),
                    EntryFrom = string.IsNullOrEmpty(from) ? "auto" : from,
                    EntryAnchor = AnchorFor(zoneId, from, tpl),
                    LiveScale = Vector3.one,
                    LiveRotation = tpl.rotation,
                    LiveAlpha = tpl.alpha,
                };
                items[item.Id] = item;
                SlotsOf(zoneId)[item.Order] = item;
                InvalidateSlots();
                item.LivePosition = CurrentPosition(item);
                created.Add(item);
            }
            return created;
        }

        /// <summary>zone 里当前有多少件（给叠压居中用）。</summary>
        /// <summary>某 zone 内某模板的件数（用于 create 的幂等补齐）。</summary>
        public int CountInZone(string zoneId, string templateId)
        {
            int n = 0;
            foreach (var it in items.Values)
                if (it.ZoneId == zoneId && it.Template != null && it.Template.id == templateId) n++;
            return n;
        }

        public int CountInZone(string zoneId)
        {
            var map = string.IsNullOrEmpty(zoneId) ? null : (occupancy.TryGetValue(zoneId, out var m) ? m : null);
            return map?.Count ?? 0;
        }

        private int NextOrder(string zoneId) => NextFreeSlot(zoneId);

        /// <summary>取（必要时创建）该 zone 的格位表。</summary>
        private Dictionary<int, ZoneItem> SlotsOf(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return null;
            if (!occupancy.TryGetValue(zoneId, out var map))
            {
                map = new Dictionary<int, ZoneItem>();
                occupancy[zoneId] = map;
            }
            return map;
        }

        /// <summary>下一个空着的格位号（不改变任何已有格位）。</summary>
        private int NextFreeSlot(string zoneId)
        {
            var map = string.IsNullOrEmpty(zoneId) ? null : (occupancy.TryGetValue(zoneId, out var m) ? m : null);
            if (map == null) return 0;
            for (int i = 0; ; i++) if (!map.ContainsKey(i)) return i;
        }

        /// <summary>
        /// 从另一个 store 把「组件本身」搬过来（模板/色板/归属/格位/正反面）。
        /// 用于「重建实例算好牌桌，再交给正式播放器」。
        /// </summary>
        public void AdoptItemsFrom(ZoneStore other)
        {
            if (other == null) return;
            Reset();
            foreach (var src in other.Items)
            {
                if (src?.Template == null) continue;
                var copy = new ZoneItem
                {
                    Id = src.Id,
                    Template = src.Template,
                    PaletteName = src.PaletteName,
                    BaseColor = src.BaseColor,
                    ZoneId = src.ZoneId,
                    Order = src.Order,
                    EntryFrom = src.EntryFrom,
                    EntryAnchor = src.EntryAnchor,
                    Flipped = src.Flipped,
                    // 注意：新增的每件状态都要加进这份复制清单，否则交接后就丢了。
                    // 曾经漏掉它：重建后已出场的牌会重新隐藏。
                    Shown = src.Shown,
                    LiveScale = src.LiveScale,
                    LiveRotation = src.LiveRotation,
                    LiveAlpha = src.LiveAlpha,
                    LivePosition = src.LivePosition,
                };
                items[copy.Id] = copy;
                var map = SlotsOf(copy.ZoneId);
                if (map != null && !map.ContainsKey(copy.Order)) map[copy.Order] = copy;
                else InvalidateSlots();
                // 让后续 Spawn 不会重复用同一个编号
                var parts = copy.Id.Split('#');
                if (parts.Length == 2 && int.TryParse(parts[1], out int n))
                    if (!counters.TryGetValue(parts[0], out int cur) || n > cur)
                        counters[parts[0]] = n;
            }
            InvalidateSlots();
        }

        /// <summary>按 id 取组件。</summary>
        public bool TryGetItem(string id, out ZoneItem item)
        {
            item = null;
            return !string.IsNullOrEmpty(id) && items.TryGetValue(id, out item);
        }

        /// <summary>把组件按它当前的 ZoneId/Order 重新登记进占用表（用于重建后对接状态）。</summary>
        public void SetActiveItem(ZoneItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.ZoneId)) return;
            var map = SlotsOf(item.ZoneId);
            int slot = Mathf.Max(0, item.Order);
            if (map.TryGetValue(slot, out var other) && !ReferenceEquals(other, item))
            {
                int free = NextFreeSlot(item.ZoneId);
                map.Remove(slot);
                map[free] = other;
                other.Order = free;
            }
            map[slot] = item;
            item.Order = slot;
            InvalidateSlots();
        }

        /// <summary>某格位的组件（没有则 null）。</summary>
        public ZoneItem AtSlot(string zoneId, int slot)
            => !string.IsNullOrEmpty(zoneId) && occupancy.TryGetValue(zoneId, out var m) && m.TryGetValue(slot, out var it) ? it : null;

        public int CountIn(string zoneId, string palette = null, string template = null)
        {
            var map = string.IsNullOrEmpty(zoneId) ? null : (occupancy.TryGetValue(zoneId, out var m) ? m : null);
            if (map == null) return 0;
            int n = 0;
            foreach (var item in map.Values)
            {
                // 两个条件都是**必须匹配**才算数。
                // 曾经只判 palette，而市场牌与牌堆共用同一个色板（card_level_1），
                // 于是 12 张市场牌被当成 12 张牌堆卡，牌堆数量核对彻底失真 ——
                // 表现为「三个牌堆没被创建」「发牌方向反过来」。
                if (!string.IsNullOrEmpty(template) && item.Template?.id != template) continue;
                if (!string.IsNullOrEmpty(palette) && item.PaletteName != palette) continue;
                n++;
            }
            return n;
        }

        /// <summary>zone 里最靠前（顺序最前）的组件；excluded 用于一次搬多件。</summary>
        public ZoneItem FrontOf(string zoneId, List<ZoneItem> excluded = null)
        {
            var map = string.IsNullOrEmpty(zoneId) ? null : (occupancy.TryGetValue(zoneId, out var m) ? m : null);
            if (map == null) return null;

            ZoneItem best = null;
            foreach (var item in map.Values)
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
            var source = occupancy.TryGetValue(srcZone, out var srcMap) ? srcMap : null;
            if (source == null) return 0;

            var matches = new List<ZoneItem>();
            foreach (var item in source.Values)
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
            if (item == null || string.IsNullOrEmpty(targetZoneId)) return;
            if (!zones.ContainsKey(targetZoneId)) return;

            var map = SlotsOf(targetZoneId);

            // 从原 zone 释放（按格位号删除，其他人的格号不受影响）
            if (!string.IsNullOrEmpty(item.ZoneId) && occupancy.TryGetValue(item.ZoneId, out var fromMap)
                && item.Order >= 0 && fromMap.TryGetValue(item.Order, out var occupant) && ReferenceEquals(occupant, item))
                fromMap.Remove(item.Order);

            int slot = Mathf.Max(0, order);

            // 目标格位已被别人占：把占用者挪到下一个空位（它的格号会变，但它本来就是「没被指定过」的）。
            if (map.TryGetValue(slot, out var existing) && !ReferenceEquals(existing, item))
            {
                int free = NextFreeSlot(targetZoneId);
                map.Remove(slot);
                map[free] = existing;
                existing.Order = free;
            }

            map[slot] = item;
            item.ZoneId = targetZoneId;
            item.Order = slot;
            InvalidateSlots();
            if (logMoves)
                Debug.Log($"[MoveToAt] {item.Id} → {targetZoneId} 第 {slot} 格（该 zone 共 {map.Count} 件）");
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

            // 落位时更新「桌内落点」锚点。但模板声明了 from_zone 的组件不更新：
            // 它的出场点是那个 zone（例如「市场牌从对应牌堆飞出」），
            // 一旦被落位坐标覆盖，牌就会一落位就直接出现在目标格、补间从原地起步。
            var targetZone = GetZone(targetZoneId);
            bool hasFixedOrigin = item.Template != null && !string.IsNullOrEmpty(item.Template.from_zone);
            if (!hasFixedOrigin && targetZone != null && targetZone.role != "offstage")
                item.EntryAnchor = ZonePosition(targetZoneId, NextOrder(targetZoneId));

            // 从原 zone 释放自己那一格。**不重排其他格位**：别人的地址保持不变。
            if (!string.IsNullOrEmpty(item.ZoneId) && occupancy.TryGetValue(item.ZoneId, out var from)
                && item.Order >= 0 && from.TryGetValue(item.Order, out var occ) && ReferenceEquals(occ, item))
                from.Remove(item.Order);

            var to = SlotsOf(targetZoneId);
            item.ZoneId = targetZoneId;
            item.Order = NextFreeSlot(targetZoneId);   // 占一个空位，不动别人的格号
            to[item.Order] = item;
            InvalidateSlots();
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
                // 模板声明了 from_zone：起点就是那个 zone 的位置，不再外推（否则会飞到画面外）。
                if (item.Template != null && !string.IsNullOrEmpty(item.Template.from_zone))
                    return item.EntryAnchor;
                // 否则起点取「落点 + 入场方向 × margin」：多个组件落点不同、起点也不同，
                // 但共享同一段位移向量，飞入时保持相对位置。
                return item.EntryAnchor + EntryDirection(zone, item.EntryFrom) * zone.margin;
            }

            return ZonePosition(item.ZoneId, item.Order);
        }

        /// <summary>
        /// 格位坐标（走预计算的格位表；表会随占用变化自动重建）。
        /// </summary>
        public Vector3 ZonePosition(string zoneId, int order) => SlotAt(zoneId, order);

        /// <summary>
        /// 格位坐标的纯计算。摆放方式由 display.mode 与 layout.type 共同决定：
        ///
        ///   display.mode = "stack"  → 一层层盖住，每层只错开一点点（40/30/20 张的牌堆）
        ///   display.mode = "count"  → 一件件都看得见，按 layout.type 摆：
        ///       "row"   单行等距排开（玩家持有区、贵族行、一排 7 枚的宝石堆）
        ///       "grid"  居中紧凑块（发展卡市场 4 列）
        /// 超出容量的部分压在最后一格并向外扩，避免整块突然移位。
        /// </summary>
        private Vector3 ComputeZonePosition(string zoneId, int order)
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
                // 叠放显示 = **固定的一叠**，外形与"当前还剩几张"无关。
                //
                // 用户的原话：「发出的那4张牌，其实是连带着另外一部分牌是重叠在一起的。
                // 只有最底部那8张牌是错开的，这样发出4张牌，牌堆的样子也不会改变。
                // 现在这个牌堆的话，如果发出7张牌，牌堆看上去就只会剩下一张，
                // 然后这一张特别耐发，这是不对的。」
                //
                // 关键：错开量用 **capacity** 算，不用当前张数。
                //   lift = min(capacity - 1 - order, maxVisible - 1)
                // 这样"第几格错开多少"是**固定的**，与还剩几张无关：
                //   靠近顶的那些格（lift 小）永远重合在一起；
                //   越往下的格错开越多，到第 maxVisible 格封顶；
                //   更下面的格全部落在同一个位置。
                // 于是从顶上发牌时，可见外形**完全不变** —— 发出的那几张本来就在重合块里。
                int maxVisible = display.max_visible > 0 ? display.max_visible : 8;
                int deckCapacity = zone.capacity > 0 ? zone.capacity : Mathf.Max(1, CountInZone(zoneId));
                int fromTop = Mathf.Max(0, deckCapacity - 1 - slot);
                float lift = Mathf.Min(fromTop, maxVisible - 1);
                x += lift * display.dx;
                z += lift * display.dz;
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

            // NaN 防护：无效坐标一旦流进 Transform 就会每帧报错且很难定位。
            // 这里直接指出是哪个 zone、哪个格位、以及可疑的输入，便于一次查清。
            if (float.IsNaN(x) || float.IsNaN(z) || float.IsInfinity(x) || float.IsInfinity(z))
            {
                var c = zone.center;
                Debug.LogError($"[ZoneStore] 格位坐标无效 zone={zoneId} order={order} " +
                               $"center=({(c != null ? c.x.ToString() : "null")},{(c != null ? c.z.ToString() : "null")}) " +
                               $"capacity={capacity} slot={slot} visible={((display != null && display.mode == "stack") ? Mathf.Clamp(CountInZone(zoneId), 1, display.max_visible > 0 ? display.max_visible : 8).ToString() : "-")} " +
                               $"x_step={layout.x_step} z_step={layout.z_step} cols={layout.cols} " +
                               $"dx={((display != null) ? display.dx.ToString() : "null")} dz={((display != null) ? display.dz.ToString() : "null")} → ({x},{z})");
                return new Vector3(c != null ? c.x : 0f, 0f, c != null ? c.z : 0f);
            }

            // 该格位上的组件若已带无效坐标，一并指出（无效坐标流进 Transform 会每帧报错）
            if (SlotsOf(zoneId).TryGetValue(order, out var liveItem) && liveItem != null
                && (float.IsNaN(liveItem.LivePosition.x) || float.IsNaN(liveItem.LivePosition.z)))
                Debug.LogError($"[ZoneStore] 组件坐标已失效 {liveItem.Id} zone={zoneId} order={order} " +
                               $"LivePosition=({liveItem.LivePosition.x},{liveItem.LivePosition.z})");

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
        private Vector3 AnchorFor(string zoneId, string from, StageTemplate tpl = null)
        {
            // 模板指定了 from_zone：入场起点对准那个 zone（例如「市场牌从对应牌堆飞出」）。
            if (tpl != null && !string.IsNullOrEmpty(tpl.from_zone))
            {
                var src = GetZone(tpl.from_zone);
                if (src != null) return new Vector3(src.center.x, 0f, src.center.z);
            }
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
