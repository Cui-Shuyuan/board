// Pure frame evaluator; no UnityEngine types and no coroutines.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    public static partial class TimelineEvaluator
    {
        public static FrameState Evaluate(CompiledCueDef cue, CompiledStageDef stage, float t)
        {
            var frame = new FrameState();
            if (cue == null) return frame;

            // 1. logical truth: start_state + all concrete state_ops <= t
            var logical = EvaluateLogicalState(cue.start_state, cue.state_ops, t);

            // 2. camera truth: camera_in + all camera_ops <= t
            frame.Camera = EvaluateCamera(cue, t);

            // 3. base visual items come from logical state, never from clips.
            var byId = BuildVisualItems(frame, stage, logical);

            // 4. clips are presentation only: they move/scale/fade existing
            //    logical items.  They must not create or delete items.
            EvaluateEntityClips(cue, byId, t);

            // 5. whole-frame / screen-space / annotation layers.
            EvaluatePicture(cue, frame, t);
            EvaluateOverlays(cue, frame, t);
            EvaluateMagnifiers(cue, frame, t);
            BuildAnnotations(cue, stage, frame, byId, t);
            return frame;
        }

        private static float ClipStart(CompiledClipDef clip)
        {
            return clip.at + Math.Max(0f, clip.lead);
        }

        private static Dictionary<CompiledClipDef, int> ClipOrderMap(CompiledCueDef cue)
        {
            var map = new Dictionary<CompiledClipDef, int>();
            if (cue?.clips == null) return map;
            for (int i = 0; i < cue.clips.Count; i++)
                if (cue.clips[i] != null) map[cue.clips[i]] = i;
            return map;
        }

        private static CompiledCameraDef EvaluateCamera(CompiledCueDef cue, float t)
        {
            // 2. camera truth: camera_in + all camera_ops <= t
            CompiledCameraDef camera = cue.camera_in;
            if (cue.camera_ops != null)
                foreach (var op in cue.camera_ops)
                {
                    if (op == null || op.frame == null) continue;
                    if (op.at > t + 1e-6f) break;
                    camera = op.frame;
                }
            return camera;
        }

        private static Dictionary<string, VisualItemState> BuildVisualItems(
            FrameState frame, CompiledStageDef stage, Dictionary<string, ComponentState> logical)
        {
            // 3. base visual items come from logical state, never from clips.
            var byId = new Dictionary<string, VisualItemState>(StringComparer.Ordinal);
            foreach (var c in logical.Values)
            {
                if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                var v = FromComponent(c, stage);
                byId[v.Id] = v;
                frame.Items.Add(v);
            }
            return byId;
        }

        private static void EvaluateEntityClips(CompiledCueDef cue, Dictionary<string, VisualItemState> byId, float t)
        {
            // 4. clips are presentation only: they move/scale/fade existing
            //    logical items.  They must not create or delete items.
            var clipOrder = ClipOrderMap(cue);
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
                kv.Value.Sort((a, b) =>
                {
                    int byStart = ClipStart(a).CompareTo(ClipStart(b));
                    return byStart != 0 ? byStart : clipOrder[a].CompareTo(clipOrder[b]);
                });

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
                            // Logical state_ops removed the item at the destroy
                            // op time; this clip is the visual fallback for
                            // hand-authored compiled assets.
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
                        case "flip":
                        {
                            bool flipShort = string.Equals(clip.flip_axis, "short", StringComparison.Ordinal);
                            bool flipEdge = string.Equals(clip.flip_mode, "edge", StringComparison.Ordinal)
                                            && clip.flip_span > 0f;
                            // Compiled assets from before flip_mode existed carry no
                            // position fields; keep their in-place behaviour.
                            bool legacyFlip = string.IsNullOrEmpty(clip.flip_mode);
                            v.FlipAxis = flipShort ? "short" : "long";
                            if (end > start && t < end)
                            {
                                // Project a 180-degree turn about the hinge:
                                // scale = |cos(pi*p)| is 0 exactly at the
                                // midpoint, so neither face is visible there.
                                float p = eased;
                                v.Flip = Math.Abs((float)Math.Cos(Math.PI * p));

                                FaceState fromFace = ParseFace(clip.from_face);
                                if (string.IsNullOrEmpty(clip.from_face) && !string.IsNullOrEmpty(clip.to_face))
                                {
                                    // Old compiled assets only carried to_face;
                                    // the only sensible from-face is the opposite.
                                    fromFace = ParseFace(clip.to_face) == FaceState.Up
                                        ? FaceState.Down : FaceState.Up;
                                }
                                v.Face = p < 0.5f ? fromFace : ParseFace(clip.to_face);

                                if (flipEdge)
                                {
                                    // U is the folding axis: world X for a long-edge
                                    // hinge, world Z for a short-edge hinge.  The
                                    // hinge sits on the starting card edge selected
                                    // by direction.  First half moves the centre to
                                    // the hinge while the card collapses; second half
                                    // unfolds from the hinge into the destination slot.
                                    float su = flipShort ? clip.from_z : clip.from_x;
                                    float sv = flipShort ? clip.from_x : clip.from_z;
                                    float tu = flipShort ? clip.to_z : clip.to_x;
                                    float tv = flipShort ? clip.to_x : clip.to_z;
                                    float hinge = su + clip.flip_side * 0.5f * clip.flip_span;
                                    float u, w;
                                    if (p <= 0.5f)
                                    {
                                        // 0..0.5s: q sweeps 0..1; cos(pi*p)
                                        // goes 1 -> 0, so the centre reaches the
                                        // hinge exactly at the midpoint.
                                        float q = p * 2f;
                                        u = hinge + (su - hinge) * (float)Math.Cos(0.5f * Math.PI * q);
                                        w = sv;
                                    }
                                    else
                                    {
                                        float q = (p - 0.5f) * 2f;
                                        float s = (float)Math.Sin(0.5f * Math.PI * q);
                                        u = hinge + (tu - hinge) * s;
                                        w = sv + (tv - sv) * s;
                                    }
                                    if (flipShort)
                                    {
                                        v.Z = u;
                                        v.X = w;
                                    }
                                    else
                                    {
                                        v.X = u;
                                        v.Z = w;
                                    }
                                    // While the card is turning, keep the source
                                    // render layer so it draws above the deck it
                                    // came from; the destination layer is applied
                                    // once the clip ends.  Without this the card
                                    // sinks under the deck and reads as flipping
                                    // through/below the table.
                                    v.Layer = clip.from_layer;
                                }
                                else if (!legacyFlip)
                                {
                                    // New centred flip: explicit source slot.
                                    v.X = clip.from_x;
                                    v.Z = clip.from_z;
                                }
                            }
                            else
                            {
                                v.Flip = 1f;
                                if (!legacyFlip)
                                {
                                    v.X = clip.to_x;
                                    v.Z = clip.to_z;
                                }
                                if (flipEdge) v.Layer = clip.to_layer;
                                if (!string.IsNullOrEmpty(clip.to_face)) v.Face = ParseFace(clip.to_face);
                            }
                            break;
                        }
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
                        case "fade":
                        case "highlight":
                        case "point":
                            VisualClipPlayer.Apply(v, clip, t);
                            break;
                    }
                }
            }
        }

        private static void EvaluatePicture(CompiledCueDef cue, FrameState frame, float t)
        {
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
        }

        private static void EvaluateOverlays(CompiledCueDef cue, FrameState frame, float t)
        {
            // Screen-space presentation overlays.  They are intentionally
            // outside the ComponentState list: a real card may be shown here
            // while it still lives in card_market / player_development, and the
            // overlay persists independently of world camera/rotation/destroy.
            // Screen-targeted presentation clips use the same IAnimVisualObject
            // interface as entity clips, so highlight/point/fade/scale are not
            // duplicated per object kind.
            if (cue.clips != null)
            {
                var overlayClips = new List<CompiledClipDef>();
                foreach (var clip in cue.clips)
                {
                    if (clip == null) continue;
                    // Only overlay lifecycle and the three object modifiers
                    // actually mutate VisualOverlayState.  Annotation clips
                    // (point/shape/label) are rendered by DrawAnnotations and
                    // must not be folded into the overlay state as well.
                    if (clip.kind == "overlay_show" || clip.kind == "overlay_hide"
                        || (clip.object_space == "screen"
                            && (clip.kind == "fade" || clip.kind == "scale" || clip.kind == "highlight")))
                        overlayClips.Add(clip);
                }
                var overlayOrder = ClipOrderMap(cue);
                overlayClips.Sort((a, b) =>
                {
                    int byStart = ClipStart(a).CompareTo(ClipStart(b));
                    return byStart != 0 ? byStart : overlayOrder[a].CompareTo(overlayOrder[b]);
                });

                var active = new Dictionary<string, VisualOverlayState>(StringComparer.Ordinal);
                foreach (var clip in overlayClips)
                {
                    float start = clip.at + Math.Max(0f, clip.lead);
                    if (t + 1e-6f < start) continue;
                    string id = clip.overlay ?? "";
                    if (string.IsNullOrEmpty(id)) continue;

                    if (clip.kind == "overlay_hide")
                    {
                        active.Remove(id);
                        continue;
                    }
                    if (clip.kind == "overlay_show")
                    {
                        float alpha = 1f;
                        if (clip.dur > 0f)
                            alpha = Clamp01((t - start) / clip.dur);
                        active[id] = new VisualOverlayState
                        {
                            Id = id,
                            TemplateId = clip.template,
                            Palette = clip.palette,
                            FaceImage = clip.face_image,
                            BackImage = clip.back_image,
                            Mask = clip.mask,
                            Background = clip.background,
                            X = clip.label_x,
                            Y = clip.label_y,
                            W = clip.label_w,
                            H = clip.label_h,
                            Layer = clip.layer,
                            Alpha = alpha,
                            SourceItemId = clip.source_item_id,
                            PersistOnSourceMissing = clip.persist_on_source_missing,
                        };
                        continue;
                    }

                }

                // Apply the common presentation primitives in a second pass.
                // This keeps source-event ordering irrelevant: a highlight or
                // point may appear before its overlay_show in the file and the
                // interface still resolves against the object active at t.
                foreach (var clip in overlayClips)
                {
                    if (clip == null || clip.kind == "overlay_show" || clip.kind == "overlay_hide")
                        continue;
                    if (clip.object_space != "screen") continue;
                    float start = clip.at + Math.Max(0f, clip.lead);
                    if (t + 1e-6f < start) continue;
                    string id = clip.overlay ?? "";
                    if (string.IsNullOrEmpty(id)) continue;
                    if (active.TryGetValue(id, out var existing) && existing != null)
                        VisualClipPlayer.Apply(existing, clip, t);
                }
                foreach (var ov in active.Values) frame.Overlays.Add(ov);
            }
        }

        private static void EvaluateMagnifiers(CompiledCueDef cue, FrameState frame, float t)
        {
            // Magnifier lenses are independent screen-space viewports.  They
            // persist from their show clip until an optional hide clip.
            if (cue.clips != null)
            {
                var activeMagnifiers = new Dictionary<string, VisualMagnifierState>(StringComparer.Ordinal);
                foreach (var clip in cue.clips)
                {
                    if (clip == null) continue;
                    if (clip.kind != "magnifier_show" && clip.kind != "magnifier_hide") continue;
                    float start = clip.at + Math.Max(0f, clip.lead);
                    if (t + 1e-6f < start) continue;
                    string id = clip.overlay ?? "";
                    if (string.IsNullOrEmpty(id)) continue;
                    if (clip.kind == "magnifier_hide")
                    {
                        activeMagnifiers.Remove(id);
                        continue;
                    }
                    float alpha = 1f;
                    if (clip.dur > 0f) alpha = Clamp01((t - start) / clip.dur);
                    activeMagnifiers[id] = new VisualMagnifierState
                    {
                        Id = id,
                        Shape = string.IsNullOrEmpty(clip.mag_shape) ? "circle" : clip.mag_shape,
                        MaskMode = string.IsNullOrEmpty(clip.mag_mask) ? "items" : clip.mag_mask,
                        ItemIds = clip.mag_item_ids,
                        X = clip.mag_x,
                        Y = clip.mag_y,
                        W = clip.mag_w,
                        H = clip.mag_h,
                        CenterX = clip.mag_center_x,
                        CenterZ = clip.mag_center_z,
                        OrthoSize = clip.mag_ortho_size,
                        Layer = clip.layer,
                        Alpha = alpha,
                    };
                }
                foreach (var m in activeMagnifiers.Values) frame.Magnifiers.Add(m);
            }

        }

        private static Dictionary<string, ComponentState> EvaluateLogicalState(StateSnapshot start, List<CompiledStateOpDef> ops, float t)
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
            var zone = StageLookup.Zone(stage, c.ZoneId);
            var v = new VisualItemState
            {
                Id = c.Id,
                TemplateId = c.TemplateId,
                Palette = c.Palette,
                ZoneId = c.ZoneId,
                Order = c.Order,
                Layer = c.Layer,
                Face = c.Face,
                // Tree/stage decides what is visible.  A component whose zone
                // is absent from the current stage (for example table state
                // while a card-intro tree is playing) stays in the logical
                // state for inheritance, but is not drawn.
                Visible = zone != null
                    && !string.Equals(zone.role, "offstage", StringComparison.Ordinal),
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
}
