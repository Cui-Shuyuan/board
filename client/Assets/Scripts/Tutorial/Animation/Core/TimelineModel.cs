// Pure timeline evaluation.  No UnityEngine types and no coroutines: every frame
// is a pure function of the compiled cue, compiled stage, and time t.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BoardGameTutorial.Animation
{
    public sealed class VisualItemState
    {
        public string Id;
        public string TemplateId;
        public string Palette;
        public string ZoneId;
        public int Order;
        public float X;
        public float Z;
        public float Scale = 1f;
        public float Alpha = 1f;
        public float Rotation;
        public FaceState Face = FaceState.Up;
        public bool Visible = true;

        // Presentation-only modifiers, resolved by the binder.
        public bool Highlighted;
        public float HighlightGrow = 1f;
        public string PointPart;
        public string Indicator;
    }

    public sealed class FrameState
    {
        public string Picture;
        public readonly List<VisualItemState> Items = new List<VisualItemState>();

        public VisualItemState Find(string id)
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Id == id) return Items[i];
            return null;
        }
    }

    public static class StageLookup
    {
        public static CompiledZoneDef Zone(CompiledStageDef stage, string zoneId)
        {
            if (stage?.zones == null || string.IsNullOrEmpty(zoneId)) return null;
            foreach (var z in stage.zones)
                if (z != null && z.zone == zoneId) return z;
            return null;
        }

        public static bool TrySlot(CompiledStageDef stage, string zoneId, int order, out float x, out float z)
        {
            x = z = 0f;
            var zone = Zone(stage, zoneId);
            if (zone?.slots == null) return false;
            foreach (var s in zone.slots)
                if (s != null && s.order == order)
                {
                    x = s.x;
                    z = s.z;
                    return true;
                }
            return false;
        }

        public static CompiledTemplateDef Template(CompiledStageDef stage, string templateId)
        {
            if (stage?.templates == null || string.IsNullOrEmpty(templateId)) return null;
            foreach (var t in stage.templates)
                if (t != null && t.id == templateId) return t;
            return null;
        }
    }

    public static class TimelineEvaluator
    {
        public static FrameState Evaluate(CompiledCueDef cue, CompiledStageDef stage, float t)
        {
            var frame = new FrameState();
            if (cue == null) return frame;

            var byId = new Dictionary<string, VisualItemState>(StringComparer.Ordinal);
            if (cue.start_state?.components != null)
                foreach (var c in cue.start_state.components)
                {
                    if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                    var v = FromComponent(c, stage);
                    byId[v.Id] = v;
                    frame.Items.Add(v);
                }

            // Compiler should put all created items in start_state via zero-time
            // clips; this fallback keeps hand-written compiled examples expressive.
            var clipsByItem = new Dictionary<string, List<CompiledClipDef>>(StringComparer.Ordinal);
            if (cue.clips != null)
                foreach (var clip in cue.clips)
                {
                    if (clip == null || string.IsNullOrEmpty(clip.item_id)) continue;
                    if (!clipsByItem.TryGetValue(clip.item_id, out var list))
                        clipsByItem[clip.item_id] = list = new List<CompiledClipDef>();
                    list.Add(clip);
                    if (!byId.ContainsKey(clip.item_id))
                    {
                        var v = new VisualItemState
                        {
                            Id = clip.item_id,
                            TemplateId = clip.template,
                            Palette = clip.palette,
                            Visible = false,
                        };
                        byId[v.Id] = v;
                        frame.Items.Add(v);
                    }
                }

            foreach (var kv in clipsByItem)
            {
                var v = byId[kv.Key];
                kv.Value.Sort((a, b) => (a.at + Math.Max(0f, a.lead)).CompareTo(b.at + Math.Max(0f, b.lead)));
                bool exists = v.Visible;

                foreach (var clip in kv.Value)
                {
                    float start = clip.at + Math.Max(0f, clip.lead);
                    float end = start + Math.Max(0f, clip.dur);
                    if (t + 1e-6f < start)
                    {
                        // Future clip must not override the current value.
                        break;
                    }
                    float k = end > start
                        ? Clamp01((t - start) / (end - start))
                        : 1f;
                    float eased = Easing.Evaluate(clip.easing, k);

                    switch (clip.kind)
                    {
                        case "spawn":
                            exists = true;
                            ApplyPlacement(v, clip.to_zone, clip.to_order, clip.to_x, clip.to_z, stage, v.X, v.Z);
                            if (!string.IsNullOrEmpty(clip.to_face)) v.Face = ParseFace(clip.to_face);
                            break;
                        case "destroy":
                            if (end <= start || t + 1e-6f >= end) exists = false;
                            break;
                        case "move":
                        {
                            float fx = v.X, fz = v.Z;
                            string fzId = v.ZoneId;
                            int fo = v.Order;
                            ResolveFrom(clip, stage, ref fx, ref fz, ref fzId, ref fo, v.X, v.Z);
                            float tx = fx, tz = fz;
                            string tzId = fzId;
                            int to = fo;
                            ResolveTo(clip, stage, ref tx, ref tz, ref tzId, ref to, fx, fz);
                            if (end > start && t < end)
                            {
                                v.X = Lerp(fx, tx, eased);
                                v.Z = Lerp(fz, tz, eased);
                            }
                            else
                            {
                                v.X = tx;
                                v.Z = tz;
                                v.ZoneId = tzId;
                                v.Order = to;
                            }
                            if (!string.IsNullOrEmpty(clip.to_face)) v.Face = ParseFace(clip.to_face);
                            break;
                        }
                        case "face":
                            if (end <= start || t + 1e-6f >= end)
                                v.Face = ParseFace(clip.to_face);
                            break;
                        case "scale":
                            v.Scale = Lerp(clip.from_scale, clip.to_scale, eased);
                            break;
                        case "fade":
                            v.Alpha = Lerp(clip.from_alpha, clip.to_alpha, eased);
                            break;
                        case "highlight":
                            v.Highlighted = t < end || end <= start;
                            if (v.Highlighted)
                            {
                                float half = k < 0.5f ? k * 2f : (1f - k) * 2f;
                                v.HighlightGrow = Lerp(1f, Math.Max(1f, clip.to_scale), half);
                            }
                            break;
                        case "point":
                            v.PointPart = clip.part;
                            v.Indicator = clip.indicator;
                            break;
                    }
                }

                v.Visible = exists;
                if (!exists)
                {
                    v.Alpha = 0f;
                }
            }

            // Whole-picture state (box cover, etc.).
            if (cue.clips != null)
            {
                string picture = null;
                float pictureAt = float.NegativeInfinity;
                foreach (var clip in cue.clips)
                {
                    if (clip == null) continue;
                    if (clip.kind != "picture" && clip.kind != "show") continue;
                    float start = clip.at + Math.Max(0f, clip.lead);
                    if (start <= t + 1e-6f && start >= pictureAt)
                    {
                        picture = clip.picture_on ? clip.picture : null;
                        pictureAt = start;
                    }
                }
                frame.Picture = picture;
            }

            return frame;
        }

        private static VisualItemState FromComponent(ComponentState c, CompiledStageDef stage)
        {
            var v = new VisualItemState
            {
                Id = c.Id,
                TemplateId = c.TemplateId,
                Palette = c.Palette,
                ZoneId = c.ZoneId,
                Order = c.Order,
                Face = c.Face,
                Visible = true,
                Alpha = 1f,
                Scale = 1f,
            };
            if (StageLookup.TrySlot(stage, c.ZoneId, c.Order, out float x, out float z))
            {
                v.X = x;
                v.Z = z;
            }
            return v;
        }

        private static void ApplyPlacement(VisualItemState v, string zoneId, int order, float x, float z,
            CompiledStageDef stage, float defaultX, float defaultZ)
        {
            v.ZoneId = string.IsNullOrEmpty(zoneId) ? v.ZoneId : zoneId;
            v.Order = order >= 0 ? order : v.Order;
            if (stage != null && StageLookup.TrySlot(stage, v.ZoneId, v.Order, out float sx, out float sz))
            {
                v.X = sx;
                v.Z = sz;
            }
            else
            {
                v.X = x != 0f || z != 0f ? x : defaultX;
                v.Z = z != 0f || x != 0f ? z : defaultZ;
            }
        }

        private static void ResolveFrom(CompiledClipDef clip, CompiledStageDef stage,
            ref float x, ref float z, ref string zone, ref int order, float fallbackX, float fallbackZ)
        {
            if (!string.IsNullOrEmpty(clip.from_zone))
            {
                zone = clip.from_zone;
                order = clip.from_order >= 0 ? clip.from_order : order;
                if (StageLookup.TrySlot(stage, zone, order, out float sx, out float sz))
                {
                    x = sx; z = sz;
                    return;
                }
            }
            x = clip.from_x != 0f || clip.from_z != 0f ? clip.from_x : fallbackX;
            z = clip.from_x != 0f || clip.from_z != 0f ? clip.from_z : fallbackZ;
        }

        private static void ResolveTo(CompiledClipDef clip, CompiledStageDef stage,
            ref float x, ref float z, ref string zone, ref int order, float fallbackX, float fallbackZ)
        {
            if (!string.IsNullOrEmpty(clip.to_zone))
            {
                zone = clip.to_zone;
                order = clip.to_order >= 0 ? clip.to_order : order;
                if (StageLookup.TrySlot(stage, zone, order, out float sx, out float sz))
                {
                    x = sx; z = sz;
                    return;
                }
            }
            x = clip.to_x != 0f || clip.to_z != 0f ? clip.to_x : fallbackX;
            z = clip.to_x != 0f || clip.to_z != 0f ? clip.to_z : fallbackZ;
        }

        private static FaceState ParseFace(string s)
        {
            if (string.IsNullOrEmpty(s)) return FaceState.Up;
            if (s == "face_down" || s == "down") return FaceState.Down;
            if (s == "hidden") return FaceState.Hidden;
            return FaceState.Up;
        }

        private static float Lerp(float a, float b, float k) => a + (b - a) * k;
        private static float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);
    }

    /// <summary>
    /// Stateless per-cue runtime: every cue carries compiled start/end snapshots,
    /// so jumping is a pure lookup.  This replaces the old replay-entry-chain code.
    /// </summary>
    public sealed class WorldRuntime
    {
        private CompiledTrackDef track;
        private readonly Dictionary<string, CompiledCueDef> cues = new Dictionary<string, CompiledCueDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, CompiledStageDef> stages = new Dictionary<string, CompiledStageDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, TreeDef> trees = new Dictionary<string, TreeDef>(StringComparer.Ordinal);

        public CompiledTrackDef Track => track;

        public void Load(CompiledTrackDef compiled)
        {
            track = compiled;
            cues.Clear(); stages.Clear(); trees.Clear();
            if (compiled?.cues != null)
                foreach (var c in compiled.cues)
                    if (c != null && !string.IsNullOrEmpty(c.id)) cues[c.id] = c;
            if (compiled?.stages != null)
                foreach (var s in compiled.stages)
                    if (s != null && !string.IsNullOrEmpty(s.stage)) stages[s.stage] = s;
            if (compiled?.trees != null)
                foreach (var t in compiled.trees)
                    if (t != null && !string.IsNullOrEmpty(t.id)) trees[t.id] = t;
        }

        public bool TryCue(string cueId, out CompiledCueDef cue) => cues.TryGetValue(cueId ?? "", out cue);

        public bool TryTree(string treeId, out TreeDef tree) => trees.TryGetValue(treeId ?? "", out tree);

        public CompiledStageDef StageForCue(CompiledCueDef cue)
        {
            if (cue == null || !trees.TryGetValue(cue.tree ?? "", out var tree)) return null;
            return stages.TryGetValue(tree.stage ?? "", out var stage) ? stage : null;
        }

        public FrameState Evaluate(string cueId, float t)
        {
            if (!TryCue(cueId, out var cue)) return new FrameState();
            var stage = StageForCue(cue);
            return TimelineEvaluator.Evaluate(cue, stage, t);
        }

        public float DurationOf(string cueId)
        {
            return TryCue(cueId, out var cue) ? cue.duration : 0f;
        }
    }
}
