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
        public int Layer;
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

    public sealed class VisualMarkerState
    {
        public string Kind;      // forbid | circle | cross | arrow
        public float X;
        public float Z;
        public float Radius = 0.2f;
    }

    public sealed class VisualLabelState
    {
        public string Text;
        public bool ScreenSpace = true;
        public float X;
        public float Y;
        public float W;
        public float H;
    }

    public sealed class FrameState
    {
        public string Picture;
        public CompiledCameraDef Camera;
        public readonly List<VisualItemState> Items = new List<VisualItemState>();
        public readonly List<VisualMarkerState> Markers = new List<VisualMarkerState>();
        public readonly List<VisualLabelState> Labels = new List<VisualLabelState>();

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

        public static bool IsStackZone(CompiledStageDef stage, string zoneId)
        {
            var zone = Zone(stage, zoneId);
            return zone != null && string.Equals(zone.display, "stack", StringComparison.Ordinal);
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

            // 1. logical truth: start_state + all concrete state_ops <= t
            var logical = BuildLogicalState(cue.start_state, cue.state_ops, t);

            // 2. camera truth: camera_in + all camera_ops <= t
            CompiledCameraDef camera = cue.camera_in;
            if (cue.camera_ops != null)
                foreach (var op in cue.camera_ops)
                {
                    if (op == null || op.frame == null) continue;
                    if (op.at > t + 1e-6f) break;
                    camera = op.frame;
                }
            frame.Camera = camera;

            // 3. base visual items come from logical state, never from clips.
            var byId = new Dictionary<string, VisualItemState>(StringComparer.Ordinal);
            foreach (var c in logical.Values)
            {
                if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                var v = FromComponent(c, stage);
                byId[v.Id] = v;
                frame.Items.Add(v);
            }

            // 4. clips are presentation only: they move/scale/fade existing
            //    logical items.  They must not create or delete items.
            var clipsByItem = new Dictionary<string, List<CompiledClipDef>>(StringComparer.Ordinal);
            if (cue.clips != null)
                foreach (var clip in cue.clips)
                {
                    if (clip == null || string.IsNullOrEmpty(clip.item_id)) continue;
                    if (!clipsByItem.TryGetValue(clip.item_id, out var list))
                        clipsByItem[clip.item_id] = list = new List<CompiledClipDef>();
                    list.Add(clip);
                }

            foreach (var kv in clipsByItem)
            {
                if (!byId.TryGetValue(kv.Key, out var v)) continue;
                kv.Value.Sort((a, b) => (a.at + Math.Max(0f, a.lead)).CompareTo(b.at + Math.Max(0f, b.lead)));

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
                            // Logical state_ops already placed the item.  Never
                            // let a presentation clip write ZoneId/Order again:
                            // doing so would undo later compaction ops.
                            if (!string.IsNullOrEmpty(clip.to_face)) v.Face = ParseFace(clip.to_face);
                            break;
                        case "destroy":
                            // Logical state_ops already removed the item at the
                            // destroy op time; this clip is only kept for
                            // hand-written compiled fallbacks.
                            break;
                        case "move":
                        {
                            // Compiler already wrote exact from/to slots.  Do not
                            // re-resolve from the logical zone: during a transfer
                            // the item is logically in the destination, but the
                            // visual still has to fly from the source slot.
                            float fx = clip.from_x, fz = clip.from_z;
                            float tx = clip.to_x, tz = clip.to_z;
                            if (end > start && t < end)
                            {
                                v.X = Lerp(fx, tx, eased);
                                v.Z = Lerp(fz, tz, eased);
                            }
                            else
                            {
                                v.X = tx;
                                v.Z = tz;
                            }
                            if (!string.IsNullOrEmpty(clip.to_face)) v.Face = ParseFace(clip.to_face);
                            break;
                        }
                        case "face":
                            if (end <= start || t + 1e-6f >= end)
                                v.Face = ParseFace(clip.to_face);
                            break;
                        case "shuffle":
                        {
                            float bx = clip.from_x;
                            float bz = clip.from_z;
                            if (end <= start)
                            {
                                v.X = bx;
                                v.Z = bz;
                            }
                            else
                            {
                                float baseWave = Math.Max(0f, (float)Math.Sin(k * Math.PI));
                                float envelope = clip.sh_env > 0f ? (float)Math.Pow(baseWave, clip.sh_env) : baseWave;
                                float elapsed = Math.Max(0f, t - start);
                                float w = elapsed * clip.sh_freq * 2f * (float)Math.PI + clip.sh_phase;
                                float dx = (float)Math.Sin(w) * clip.sh_amp * envelope;
                                float dz = (float)Math.Sin(w * 0.73f + 1.1f) * clip.sh_zamp * envelope;
                                v.X = bx + dx;
                                v.Z = bz + dz;
                            }
                            break;
                        }
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

            // Presentation-only markers (forbid / circle / cross / arrow).
            if (cue.clips != null)
            {
                foreach (var clip in cue.clips)
                {
                    if (clip == null || clip.kind != "marker") continue;
                    float start = clip.at + Math.Max(0f, clip.lead);
                    if (t + 1e-6f < start) continue;
                    if (clip.dur > 0f && t > start + clip.dur + 1e-6f) continue;
                    frame.Markers.Add(new VisualMarkerState
                    {
                        Kind = string.IsNullOrEmpty(clip.indicator) ? "forbid" : clip.indicator,
                        X = clip.marker_x,
                        Z = clip.marker_z,
                        Radius = clip.marker_radius > 0f ? clip.marker_radius : 0.2f,
                    });
                }
            }

            // Presentation-only screen/world labels (overlay anchors).
            if (cue.clips != null)
            {
                foreach (var clip in cue.clips)
                {
                    if (clip == null || clip.kind != "label") continue;
                    float start = clip.at + Math.Max(0f, clip.lead);
                    if (t + 1e-6f < start) continue;
                    if (clip.dur > 0f && t > start + clip.dur + 1e-6f) continue;
                    frame.Labels.Add(new VisualLabelState
                    {
                        Text = clip.text ?? "",
                        ScreenSpace = clip.screen_space,
                        X = clip.label_x,
                        Y = clip.label_y,
                        W = clip.label_w,
                        H = clip.label_h,
                    });
                }
            }

            return frame;
        }

        private static Dictionary<string, ComponentState> BuildLogicalState(StateSnapshot start, List<CompiledStateOpDef> ops, float t)
        {
            var map = new Dictionary<string, ComponentState>(StringComparer.Ordinal);
            if (start?.components != null)
                foreach (var c in start.components)
                    if (c != null && !string.IsNullOrEmpty(c.Id)) map[c.Id] = c.Clone();
            if (ops != null)
                foreach (var op in ops)
                {
                    if (op == null) continue;
                    if (op.at > t + 1e-6f) break;
                    if (op.op == "put" && op.item != null && !string.IsNullOrEmpty(op.item.Id))
                        map[op.item.Id] = op.item.Clone();
                    else if (op.op == "remove" && !string.IsNullOrEmpty(op.item_id))
                        map.Remove(op.item_id);
                }
            return map;
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
                Layer = c.Layer,
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
