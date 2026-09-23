// BoardGameTutorial.Animation v2 -- pure state model.
//
// This file deliberately has no UnityEngine dependency.  The runtime core owns
// exactly one truth: a component is (zone, order, face).  Visual coordinates and
// tweens are compiled assets, never part of this model.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BoardGameTutorial.Animation
{
    public enum FaceState
    {
        Hidden = 0,
        Down = 1,
        Up = 2,
    }

    public enum SelectMode
    {
        Front = 0,
        Back = 1,
    }

    [Serializable]
    public sealed class PartRef
    {
        public string key;
        public string value;

        public PartRef() { }

        public PartRef(string key, string value)
        {
            this.key = key;
            this.value = value;
        }

        public PartRef Clone() => new PartRef(key, value);

        public override string ToString() => key + "=" + value;
    }

    [Serializable]
    public sealed class ComponentSelector
    {
        public string template;
        public string palette;
        public string concept;
        public List<PartRef> parts = new List<PartRef>();

        public bool IsEmpty =>
            string.IsNullOrEmpty(template) &&
            string.IsNullOrEmpty(palette) &&
            string.IsNullOrEmpty(concept) &&
            (parts == null || parts.Count == 0);

        public ComponentSelector Clone()
        {
            var c = new ComponentSelector
            {
                template = template,
                palette = palette,
                concept = concept,
            };
            if (parts != null)
                foreach (var p in parts) c.parts.Add(p.Clone());
            return c;
        }

        public bool Matches(ComponentState item)
        {
            if (item == null) return false;
            if (!string.IsNullOrEmpty(template) && !Eq(template, item.TemplateId)) return false;
            if (!string.IsNullOrEmpty(palette) && !Eq(palette, item.Palette)) return false;
            if (!string.IsNullOrEmpty(concept) && !Eq(concept, item.Concept)) return false;
            if (parts == null || parts.Count == 0) return true;
            foreach (var want in parts)
            {
                if (want == null) continue;
                bool hit = false;
                if (item.parts != null)
                    foreach (var have in item.parts)
                        if (have != null && Eq(want.key, have.key) && Eq(want.value, have.value))
                        {
                            hit = true;
                            break;
                        }
                if (!hit) return false;
            }
            return true;
        }

        private static bool Eq(string a, string b)
        {
            if (a == null) return b == null;
            return string.Equals(a.Trim(), (b ?? "").Trim(), StringComparison.Ordinal);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(concept)) sb.Append(concept);
            if (!string.IsNullOrEmpty(template)) sb.Append(" template=").Append(template);
            if (!string.IsNullOrEmpty(palette)) sb.Append(" palette=").Append(palette);
            if (parts != null)
                foreach (var p in parts)
                    if (p != null) sb.Append(" ").Append(p);
            return sb.Length == 0 ? "<empty-selector>" : sb.ToString();
        }
    }

    [Serializable]
    public sealed class ComponentState
    {
        public string Id;
        public string TemplateId;
        public string Palette;
        public string Concept;
        public List<PartRef> parts = new List<PartRef>();
        public string ZoneId;
        public int Order;
        public int Layer;
        public FaceState Face = FaceState.Up;

        public ComponentState Clone()
        {
            var c = new ComponentState
            {
                Id = Id,
                TemplateId = TemplateId,
                Palette = Palette,
                Concept = Concept,
                ZoneId = ZoneId,
                Order = Order,
                Layer = Layer,
                Face = Face,
            };
            if (parts != null)
                foreach (var p in parts) c.parts.Add(p.Clone());
            return c;
        }

        public string IdentityKey =>
            (TemplateId ?? "") + "|" + (Palette ?? "");

    }

    public sealed class StateException : Exception
    {
        public StateException(string message) : base(message) { }
    }

    [Serializable]
    public sealed class SequenceEntry
    {
        public string key;
        public int value;

        public SequenceEntry() { }

        public SequenceEntry(string key, int value)
        {
            this.key = key;
            this.value = value;
        }
    }

    /// <summary>Full world snapshot.  Compiled cues use snapshots for jump/replay.</summary>
    [Serializable]
    public sealed class StateSnapshot
    {
        public List<ComponentState> components = new List<ComponentState>();
        // List instead of Dictionary so the compiled asset stays JsonUtility-friendly.
        public List<SequenceEntry> nextSeq = new List<SequenceEntry>();

        public StateSnapshot Clone()
        {
            var s = new StateSnapshot();
            foreach (var c in components) s.components.Add(c.Clone());
            foreach (var kv in nextSeq)
                if (kv != null) s.nextSeq.Add(new SequenceEntry(kv.key, kv.value));
            return s;
        }

        public WorldState ToWorld()
        {
            var w = new WorldState();
            foreach (var c in components) w.Add(c.Clone());
            return w;
        }

        public Dictionary<string, int> SeqMap()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in nextSeq)
                if (kv != null && kv.key != null) map[kv.key] = kv.value;
            return map;
        }

        public static StateSnapshot FromWorld(WorldState world, Dictionary<string, int> seq)
        {
            var s = new StateSnapshot();
            foreach (var c in world.Components) s.components.Add(c.Clone());
            if (seq != null)
                foreach (var kv in seq) s.nextSeq.Add(new SequenceEntry(kv.Key, kv.Value));
            return s;
        }
    }

    /// <summary>
    /// Runtime world state.  Zone order is derived from ComponentState.Order;
    /// there is no second occupancy table that can drift away.
    /// </summary>
    [Serializable]
    public sealed class WorldState
    {
        private readonly Dictionary<string, ComponentState> byId =
            new Dictionary<string, ComponentState>(StringComparer.Ordinal);

        public IEnumerable<ComponentState> Components => byId.Values;

        public int CountAll => byId.Count;

        public bool TryGet(string id, out ComponentState item) => byId.TryGetValue(id ?? "", out item);

        public ComponentState Get(string id)
        {
            if (!TryGet(id, out var item)) throw new StateException("unknown component id: " + id);
            return item;
        }

        public void Add(ComponentState item)
        {
            if (item == null) throw new StateException("cannot add null component");
            if (string.IsNullOrEmpty(item.Id)) throw new StateException("component id is required");
            if (byId.ContainsKey(item.Id)) throw new StateException("duplicate component id: " + item.Id);
            byId.Add(item.Id, item);
        }

        public bool Remove(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            return byId.Remove(id);
        }

        public List<ComponentState> InZone(string zoneId)
        {
            var list = new List<ComponentState>();
            if (string.IsNullOrEmpty(zoneId)) return list;
            foreach (var c in byId.Values)
                if (string.Equals(c.ZoneId, zoneId, StringComparison.Ordinal))
                    list.Add(c);
            list.Sort(CompareByOrderThenId);
            return list;
        }

        public List<ComponentState> Matching(string zoneId, ComponentSelector selector)
        {
            var list = new List<ComponentState>();
            if (string.IsNullOrEmpty(zoneId)) return list;
            foreach (var c in byId.Values)
            {
                if (!string.Equals(c.ZoneId, zoneId, StringComparison.Ordinal)) continue;
                if (selector != null && !selector.Matches(c)) continue;
                list.Add(c);
            }
            list.Sort(CompareByOrderThenId);
            return list;
        }

        public ComponentState Front(string zoneId, ComponentSelector selector)
        {
            var list = Matching(zoneId, selector);
            return list.Count > 0 ? list[0] : null;
        }

        public ComponentState Back(string zoneId, ComponentSelector selector)
        {
            var list = Matching(zoneId, selector);
            return list.Count > 0 ? list[list.Count - 1] : null;
        }

        public int Count(string zoneId, ComponentSelector selector = null)
        {
            int n = 0;
            if (string.IsNullOrEmpty(zoneId)) return 0;
            foreach (var c in byId.Values)
                if (string.Equals(c.ZoneId, zoneId, StringComparison.Ordinal) &&
                    (selector == null || selector.Matches(c))) n++;
            return n;
        }

        public int NextOrder(string zoneId) => Count(zoneId);

        public void NormalizeZone(string zoneId)
        {
            var list = InZone(zoneId);
            for (int i = 0; i < list.Count; i++) list[i].Order = i;
        }

        public void NormalizeAll()
        {
            foreach (var zone in byId.Values.Select(c => c.ZoneId).Distinct(StringComparer.Ordinal))
                NormalizeZone(zone);
        }

        public static int CompareByOrderThenId(ComponentState a, ComponentState b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            int byOrder = a.Order.CompareTo(b.Order);
            if (byOrder != 0) return byOrder;
            return string.CompareOrdinal(a.Id ?? "", b.Id ?? "");
        }
    }

    /// <summary>
    /// Pure state mutation.  The source-level selector is resolved here; coordinate
    /// calculations and visual clips intentionally live in the compiler, not here.
    /// </summary>
    public sealed class StateStore
    {
        private readonly Dictionary<string, int> nextSeq =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public WorldState World { get; private set; } = new WorldState();

        public StateStore() { }

        public StateStore(StateSnapshot snapshot)
        {
            Restore(snapshot);
        }

        public void Reset()
        {
            World = new WorldState();
            nextSeq.Clear();
        }

        public StateSnapshot Snapshot()
        {
            return StateSnapshot.FromWorld(World, nextSeq);
        }

        public void Restore(StateSnapshot snapshot)
        {
            World = new WorldState();
            nextSeq.Clear();
            if (snapshot == null) return;
            if (snapshot.components != null)
                foreach (var c in snapshot.components)
                    if (c != null) World.Add(c.Clone());
            if (snapshot.nextSeq != null)
                foreach (var kv in snapshot.nextSeq)
                    if (kv != null && kv.key != null) nextSeq[kv.key] = kv.value;
        }

        public List<ComponentState> Spawn(
            string templateId, string palette, string concept, string zoneId,
            int count, FaceState face = FaceState.Up)
        {
            if (count < 0) throw new StateException("spawn count must be >= 0");
            if (count == 0) return new List<ComponentState>();
            if (string.IsNullOrEmpty(zoneId)) throw new StateException("spawn zone is required");
            var added = new List<ComponentState>();
            for (int i = 0; i < count; i++)
            {
                string key = (templateId ?? "") + "|" + (palette ?? "");
                nextSeq.TryGetValue(key, out int seq);
                seq++;
                nextSeq[key] = seq;
                var item = new ComponentState
                {
                    Id = key + "#" + seq,
                    TemplateId = templateId,
                    Palette = palette,
                    Concept = concept,
                    ZoneId = zoneId,
                    Order = World.NextOrder(zoneId),
                    Face = face,
                };
                World.Add(item);
                added.Add(item);
            }
            World.NormalizeZone(zoneId);
            return added;
        }

        /// <summary>
        /// Fill up to at least <paramref name="count"/> matching items in a zone.
        /// Used by cue start/ensure, where replay must never double-spawn.
        /// </summary>
        public List<ComponentState> EnsureAtLeast(
            string templateId, string palette, string concept, string zoneId,
            int count, FaceState face = FaceState.Up)
        {
            var selector = new ComponentSelector { template = templateId, palette = palette, concept = concept };
            int have = World.Count(zoneId, selector);
            int need = Math.Max(0, count - have);
            return need == 0 ? new List<ComponentState>() : Spawn(templateId, palette, concept, zoneId, need, face);
        }

        public List<ComponentState> Destroy(ComponentSelector selector, string zoneId, int count, SelectMode mode = SelectMode.Front)
        {
            if (count < 0) throw new StateException("destroy count must be >= 0");
            if (count == 0) return new List<ComponentState>();
            var matches = World.Matching(zoneId, selector);
            if (matches.Count < count)
                throw new StateException($"destroy needs {count} in zone '{zoneId}' selector {selector}, have {matches.Count}");
            var victims = mode == SelectMode.Back
                ? matches.Skip(Math.Max(0, matches.Count - count)).ToList()
                : matches.Take(count).ToList();
            foreach (var v in victims) World.Remove(v.Id);
            World.NormalizeZone(zoneId);
            return victims;
        }

        public List<ComponentState> Transfer(
            ComponentSelector selector,
            string sourceZone,
            string destinationZone,
            int quantity,
            FaceState? toFace = null,
            int order = -1)
        {
            if (quantity < 0) throw new StateException("transfer quantity must be >= 0");
            if (quantity == 0) return new List<ComponentState>();
            if (string.IsNullOrEmpty(sourceZone) || string.IsNullOrEmpty(destinationZone))
                throw new StateException("transfer source/destination are required");
            var matches = World.Matching(sourceZone, selector);
            if (matches.Count < quantity)
                throw new StateException($"transfer needs {quantity} from '{sourceZone}' selector {selector}, have {matches.Count}");

            var moved = matches.Take(quantity).ToList();
            foreach (var item in moved) World.Remove(item.Id);
            World.NormalizeZone(sourceZone);

            foreach (var item in moved)
            {
                item.ZoneId = destinationZone;
                if (toFace.HasValue) item.Face = toFace.Value;
                World.Add(item);
            }

            if (order >= 0) PlaceAtOrder(moved, destinationZone, order);
            else World.NormalizeZone(destinationZone);
            return moved;
        }

        public void MoveOrder(string itemId, string zoneId, int order)
        {
            var item = World.Get(itemId);
            if (!string.Equals(item.ZoneId, zoneId, StringComparison.Ordinal))
                throw new StateException($"item {itemId} is not in zone {zoneId}");
            item.Order = order;
            World.NormalizeZone(zoneId);
        }

        public void SetFace(ComponentSelector selector, string zoneId, FaceState face)
        {
            foreach (var item in World.Matching(zoneId, selector)) item.Face = face;
        }

        /// <summary>
        /// Deterministic shuffle: a permutation derived from (item id, seed), so
        /// replay and compiled snapshots agree without a random source.
        /// </summary>
        public void Shuffle(string zoneId, int seed)
        {
            var list = World.InZone(zoneId);
            if (list.Count <= 1) return;
            var ranked = list
                .Select((item, index) => new { item, index, hash = Hash32(item.Id, seed) })
                .OrderBy(x => x.hash)
                .ThenBy(x => x.item.Id, StringComparer.Ordinal)
                .ToList();
            for (int i = 0; i < ranked.Count; i++) ranked[i].item.Order = i;
            World.NormalizeZone(zoneId);
        }

        private void PlaceAtOrder(List<ComponentState> moved, string zoneId, int order)
        {
            if (moved == null || moved.Count == 0) return;
            var dest = World.InZone(zoneId);
            // remove moved from dest so insertion index is computed against the rest
            dest.RemoveAll(c => moved.Any(m => m.Id == c.Id));
            int at = Math.Max(0, Math.Min(order, dest.Count));
            dest.InsertRange(at, moved);
            for (int i = 0; i < dest.Count; i++) dest[i].Order = i;
        }

        public static uint Hash32(string s, int seed)
        {
            unchecked
            {
                uint h = 2166136261u ^ (uint)seed;
                if (s != null)
                    for (int i = 0; i < s.Length; i++)
                    {
                        h ^= s[i];
                        h *= 16777619u;
                    }
                return h;
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("StateStore components=").Append(World.CountAll);
            foreach (var zone in World.Components.Select(c => c.ZoneId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(" ").Append(zone).Append("=").Append(World.Count(zone));
            return sb.ToString();
        }
    }
}
