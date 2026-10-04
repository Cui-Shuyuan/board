// Pure timeline evaluation.  No UnityEngine types and no coroutines: every frame
// is a pure function of the compiled cue, compiled stage, and time t.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BoardGameTutorial.Animation
{
    /// <summary>
    /// Common target interface for every animated object, regardless of whether
    /// it lives in world space (entity/component) or screen space (overlay).
    /// Presentation primitives are written against this interface; the two
    /// concrete states below are the two implementations.
    /// </summary>
    public interface IAnimVisualObject
    {
        string ObjectId { get; }
        float Alpha { get; set; }
        float Scale { get; set; }
        bool Highlighted { get; set; }
        float HighlightGrow { get; set; }
        string PointPart { get; set; }
        string Indicator { get; set; }
    }

    public sealed class VisualItemState : IAnimVisualObject
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

        string IAnimVisualObject.ObjectId { get { return Id; } }
        float IAnimVisualObject.Alpha { get { return Alpha; } set { Alpha = value; } }
        float IAnimVisualObject.Scale { get { return Scale; } set { Scale = value; } }
        bool IAnimVisualObject.Highlighted { get { return Highlighted; } set { Highlighted = value; } }
        float IAnimVisualObject.HighlightGrow { get { return HighlightGrow; } set { HighlightGrow = value; } }
        string IAnimVisualObject.PointPart { get { return PointPart; } set { PointPart = value; } }
        string IAnimVisualObject.Indicator { get { return Indicator; } set { Indicator = value; } }
    }

    /// <summary>
    /// A presentation annotation.  It is fully described by two explicit
    /// answers: *space* selects the coordinate system (world follows the
    /// camera / item; screen is fixed to the viewport), and *kind* selects the
    /// drawing primitive (arrow/circle/cross/forbid/box/label).
    ///
    /// World annotations below/right of a card are resolved to their current
    /// world position while evaluating the frame; screen annotations reference
    /// an overlay (or a baked viewport rect) and never touch the camera.
    /// </summary>
    public sealed class VisualAnnotationState
    {
        public string Space;      // world | screen
        public string Kind;       // arrow | circle | cross | forbid | box | label
        public string Text;       // label only
        public string ItemId;     // world anchor (optional)
        public string OverlayId;  // screen anchor (optional)
        public string Part;
        public float X;           // current world position for world
        public float Z;
        public float Radius = 0.2f;
        public float W;           // world box width; screen fallback width
        public float H;           // world box height; screen fallback height
        public float PartU = 0.5f;
        public float PartV = 0.5f;
        public float PartW;      // semantic sub-rect width, fraction of target rect
        public float PartH;      // semantic sub-rect height, fraction of target rect
        public float NudgeX;
        public float NudgeY;
        public string ColorHex;   // annotation style, from source script
        public float StrokePx;    // 1080p-reference pixels
        public float SizePx;      // 1080p-reference pixels, 0 = auto
        public float GapPx;       // 1080p-reference pixels, 0 = auto
        public float ScreenX;     // baked viewport fallback (screen)
        public float ScreenY;
        public float ScreenW;
        public float ScreenH;
        public float LabelW;
        public float LabelH;
    }

    /// <summary>
    /// Screen-space presentation overlay.  This is NOT a ComponentState: it
    /// never owns a physical card, never enters zone/order logic and is not
    /// counted by card-identity checks.  It is a camera/viewport-fixed asset
    /// reference, optionally linked to a source item for tooling.
    /// </summary>
    public sealed class VisualOverlayState : IAnimVisualObject
    {
        public string Id;
        public string TemplateId;
        public string Palette;
        public string FaceImage;
        public string BackImage;
        public string Mask;
        public string Background;
        public float X;
        public float Y;
        public float W;
        public float H;
        public int Layer;
        public float Alpha = 1f;
        public float Scale = 1f;
        public bool Highlighted;
        public float HighlightGrow = 1f;
        public string PointPart;
        public string Indicator;
        public string SourceItemId;
        public bool PersistOnSourceMissing = true;

        string IAnimVisualObject.ObjectId { get { return Id; } }
        float IAnimVisualObject.Alpha { get { return Alpha; } set { Alpha = value; } }
        float IAnimVisualObject.Scale { get { return Scale; } set { Scale = value; } }
        bool IAnimVisualObject.Highlighted { get { return Highlighted; } set { Highlighted = value; } }
        float IAnimVisualObject.HighlightGrow { get { return HighlightGrow; } set { HighlightGrow = value; } }
        string IAnimVisualObject.PointPart { get { return PointPart; } set { PointPart = value; } }
        string IAnimVisualObject.Indicator { get { return Indicator; } set { Indicator = value; } }
    }

    /// <summary>
    /// A magnification lens: a viewport rect plus the world-space region that
    /// a dedicated camera renders into it each frame.  Because the lens shows
    /// the live world, highlighted items stay highlighted inside the lens and
    /// items flying out of the region also fly out of the lens.
    /// </summary>
    public sealed class VisualMagnifierState
    {
        public string Id;
        public string Shape = "circle";
        public float X;
        public float Y;
        public float W;
        public float H;
        public float CenterX;
        public float CenterZ;
        public float OrthoSize;
        public int Layer;
        public float Alpha = 1f;
    }

    public sealed class FrameState
    {
        public string Picture;
        public CompiledCameraDef Camera;
        public readonly List<VisualItemState> Items = new List<VisualItemState>();
        public readonly List<VisualAnnotationState> Annotations = new List<VisualAnnotationState>();
        public readonly List<VisualOverlayState> Overlays = new List<VisualOverlayState>();
        public readonly List<VisualMagnifierState> Magnifiers = new List<VisualMagnifierState>();

        public VisualItemState Find(string id)
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Id == id) return Items[i];
            return null;
        }
    }

    /// <summary>
    /// Applies the presentation primitives that are meaningful for every
    /// object implementation of <see cref="IAnimVisualObject"/>.
    /// </summary>
    public static class VisualClipPlayer
    {
        public static void Apply(IAnimVisualObject target, CompiledClipDef clip, float t)
        {
            if (target == null || clip == null) return;
            float start = clip.at + Math.Max(0f, clip.lead);
            float end = start + Math.Max(0f, clip.dur);
            if (t + 1e-6f < start) return;
            float k = end > start ? Clamp01((t - start) / (end - start)) : 1f;
            float eased = Easing.Evaluate(clip.easing, k);
            switch (clip.kind)
            {
                case "scale":
                    target.Scale = Lerp(1f, clip.to_scale, eased);
                    break;
                case "fade":
                    target.Alpha = Lerp(1f, clip.to_alpha, eased);
                    break;
                case "highlight":
                    target.Highlighted = t < end || end <= start;
                    if (target.Highlighted)
                    {
                        float half = k < 0.5f ? k * 2f : (1f - k) * 2f;
                        target.HighlightGrow = Lerp(1f, Math.Max(1f, clip.to_scale), half);
                    }
                    break;
                case "point":
                    target.PointPart = clip.part;
                    target.Indicator = clip.indicator;
                    break;
            }
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
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
                overlayClips.Sort((a, b) =>
                    (a.at + Math.Max(0f, a.lead)).CompareTo(b.at + Math.Max(0f, b.lead)));

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

            BuildAnnotations(cue, stage, frame, byId, t);
            return frame;
        }

        private sealed class AnnotationPick
        {
            public CompiledClipDef Clip;
            public float Start;
        }

        /// <summary>
        /// Convert presentation clips into drawable annotations.  Point and
        /// shape clips win per matching target; a later start replaces an
        /// earlier one.
        /// Labels and action-geometry marker clips are all emitted.
        /// </summary>
        private static void BuildAnnotations(CompiledCueDef cue, CompiledStageDef stage,
            FrameState frame, Dictionary<string, VisualItemState> byId, float t)
        {
            if (cue?.clips == null) return;
            var activeOverlays = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ov in frame.Overlays)
                if (ov != null && !string.IsNullOrEmpty(ov.Id)) activeOverlays.Add(ov.Id);

            // One annotation per (space, target): the latest-started point/shape
            // replaces the earlier one.  Without this, a box and an arrow on the
            // same target would draw on top of each other (e.g. cost rectangle
            // with the previous arrow marker still visible inside it).
            var annotationPicks = new Dictionary<string, AnnotationPick>(StringComparer.Ordinal);
            foreach (var clip in cue.clips)
            {
                if (clip == null || (clip.kind != "point" && clip.kind != "shape")) continue;
                float start = clip.at + Math.Max(0f, clip.lead);
                if (t + 1e-6f < start) continue;
                if (clip.dur > 0f && t > start + clip.dur + 1e-6f) continue;
                string space = AnnotationSpace(clip);
                string target = AnnotationTarget(clip, space);
                if (string.IsNullOrEmpty(target)) continue;
                string key = space + "|" + target;
                if (!annotationPicks.TryGetValue(key, out var existing) || start >= existing.Start)
                    annotationPicks[key] = new AnnotationPick { Clip = clip, Start = start };
            }
            foreach (var pick in annotationPicks.Values) AddAnnotation(pick.Clip, stage, frame, byId, activeOverlays);

            foreach (var clip in cue.clips)
            {
                if (clip == null || clip.kind != "marker") continue;
                float start = clip.at + Math.Max(0f, clip.lead);
                if (t + 1e-6f < start) continue;
                if (clip.dur > 0f && t > start + clip.dur + 1e-6f) continue;
                frame.Annotations.Add(new VisualAnnotationState
                {
                    Space = "world",
                    Kind = AnnotationKind(clip),
                    X = clip.marker_x,
                    Z = clip.marker_z,
                    Radius = clip.marker_radius > 0f ? clip.marker_radius : 0.2f,
                    NudgeX = clip.nudge_x,
                    NudgeY = clip.nudge_y,
                });
            }

            foreach (var clip in cue.clips)
            {
                if (clip == null || clip.kind != "label") continue;
                float start = clip.at + Math.Max(0f, clip.lead);
                if (t + 1e-6f < start) continue;
                if (clip.dur > 0f && t > start + clip.dur + 1e-6f) continue;
                AddLabelAnnotation(clip, stage, frame, byId, activeOverlays);
            }
        }

        private static void AddAnnotation(CompiledClipDef clip, CompiledStageDef stage,
            FrameState frame, Dictionary<string, VisualItemState> byId,
            HashSet<string> activeOverlays)
        {
            string space = AnnotationSpace(clip);
            ResolvePart(clip, out float u, out float v);
            if (space == "world")
            {
                if (string.IsNullOrEmpty(clip.item_id) || !byId.TryGetValue(clip.item_id, out var item)
                    || item == null || !item.Visible) return;
                GetTemplateSize(stage, item.TemplateId, out float w, out float h);
                float bw = clip.part_w > 0f ? clip.part_w * w : w;
                float bh = clip.part_h > 0f ? clip.part_h * h : h;
                frame.Annotations.Add(new VisualAnnotationState
                {
                    Space = "world",
                    Kind = AnnotationKind(clip),
                    ItemId = clip.item_id,
                    Part = clip.part,
                    X = item.X + (u - 0.5f) * w,
                    Z = item.Z + (0.5f - v) * h,
                    Radius = Math.Min(w, h) * 0.5f,
                    W = bw,
                    H = bh,
                    PartU = u,
                    PartV = v,
                    PartW = clip.part_w,
                    PartH = clip.part_h,
                    NudgeX = clip.nudge_x,
                    NudgeY = clip.nudge_y,
                    ColorHex = clip.annotation_color,
                    StrokePx = clip.annotation_stroke,
                    SizePx = clip.annotation_size,
                    GapPx = clip.annotation_gap,
                });
                return;
            }

            string id = clip.overlay ?? "";
            bool active = !string.IsNullOrEmpty(id) && activeOverlays.Contains(id);
            bool fallbackRect = clip.screen_w > 0f && clip.screen_h > 0f;
            if (!active && !fallbackRect) return;
            frame.Annotations.Add(new VisualAnnotationState
            {
                Space = "screen",
                Kind = AnnotationKind(clip),
                OverlayId = id,
                Part = clip.part,
                PartU = u,
                PartV = v,
                PartW = clip.part_w,
                PartH = clip.part_h,
                NudgeX = clip.nudge_x,
                NudgeY = clip.nudge_y,
                ColorHex = clip.annotation_color,
                StrokePx = clip.annotation_stroke,
                SizePx = clip.annotation_size,
                GapPx = clip.annotation_gap,
                ScreenX = clip.screen_x,
                ScreenY = clip.screen_y,
                ScreenW = clip.screen_w,
                ScreenH = clip.screen_h,
                LabelW = clip.label_w,
                LabelH = clip.label_h,
            });
        }

        private static void AddLabelAnnotation(CompiledClipDef clip, CompiledStageDef stage,
            FrameState frame, Dictionary<string, VisualItemState> byId,
            HashSet<string> activeOverlays)
        {
            string space = AnnotationSpace(clip);
            ResolvePart(clip, out float u, out float v);
            if (space == "world")
            {
                if (!string.IsNullOrEmpty(clip.item_id) && byId.TryGetValue(clip.item_id, out var item)
                    && item != null && item.Visible)
                {
                    GetTemplateSize(stage, item.TemplateId, out float w, out float h);
                    frame.Annotations.Add(new VisualAnnotationState
                    {
                        Space = "world",
                        Kind = "label",
                        Text = clip.text ?? "",
                        ItemId = clip.item_id,
                        Part = clip.part,
                        X = item.X + (u - 0.5f) * w,
                        Z = item.Z + (0.5f - v) * h,
                        Radius = Math.Min(w, h) * 0.5f,
                        W = w,
                        H = h,
                        PartU = u,
                        PartV = v,
                        NudgeX = clip.nudge_x,
                        NudgeY = clip.nudge_y,
                        LabelW = clip.label_w,
                        LabelH = clip.label_h,
                    });
                    return;
                }
                if (clip.world_x != 0f || clip.world_z != 0f)
                {
                    frame.Annotations.Add(new VisualAnnotationState
                    {
                        Space = "world",
                        Kind = "label",
                        Text = clip.text ?? "",
                        X = clip.world_x,
                        Z = clip.world_z,
                        NudgeX = clip.nudge_x,
                        NudgeY = clip.nudge_y,
                        LabelW = clip.label_w,
                        LabelH = clip.label_h,
                    });
                }
                return;
            }

            string id = clip.overlay ?? "";
            bool active = !string.IsNullOrEmpty(id) && activeOverlays.Contains(id);
            bool fallbackRect = (clip.screen_w > 0f && clip.screen_h > 0f)
                || (clip.label_w > 0f && clip.label_h > 0f);
            if (!active && !fallbackRect) return;
            frame.Annotations.Add(new VisualAnnotationState
            {
                Space = "screen",
                Kind = "label",
                Text = clip.text ?? "",
                OverlayId = id,
                Part = clip.part,
                PartU = u,
                PartV = v,
                NudgeX = clip.nudge_x,
                NudgeY = clip.nudge_y,
                ScreenX = clip.screen_w > 0f ? clip.screen_x : clip.label_x,
                ScreenY = clip.screen_w > 0f ? clip.screen_y : clip.label_y,
                ScreenW = clip.screen_w > 0f ? clip.screen_w : clip.label_w,
                ScreenH = clip.screen_w > 0f ? clip.screen_h : clip.label_h,
                LabelW = clip.label_w,
                LabelH = clip.label_h,
            });
        }

        private static string AnnotationSpace(CompiledClipDef clip)
        {
            string explicitSpace = (clip.annotation_space ?? "").Trim().ToLowerInvariant();
            if (explicitSpace == "world" || explicitSpace == "screen") return explicitSpace;
            if (clip.kind == "label") return clip.screen_space ? "screen" : "world";
            return clip.object_space == "screen" ? "screen" : "world";
        }

        private static string AnnotationTarget(CompiledClipDef clip, string space)
        {
            return space == "screen" ? (clip.overlay ?? "") : (clip.item_id ?? "");
        }

        private static string AnnotationKind(CompiledClipDef clip)
        {
            if (clip.kind == "label") return "label";
            string kind = (clip.indicator ?? "").Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(kind)) return kind;
            return clip.kind == "marker" ? "forbid" : "circle";
        }

        private static void ResolvePart(CompiledClipDef clip, out float u, out float v)
        {
            if (clip.has_part_uv)
            {
                u = clip.part_u;
                v = clip.part_v;
                return;
            }
            switch ((clip.part ?? "").Trim().ToLowerInvariant())
            {
                case "prestige": u = 0.126984f; v = 0.142045f; return;
                case "cost": u = 0.126984f; v = 0.744318f; return;
                case "bonus": u = 0.825397f; v = 0.130682f; return;
                case "condition": u = 0.50f; v = 0.84f; return;
                default: u = 0.5f; v = 0.5f; return;
            }
        }

        private static void GetTemplateSize(CompiledStageDef stage, string templateId,
            out float w, out float h)
        {
            w = 0.2f;
            h = 0.2f;
            var tpl = StageLookup.Template(stage, templateId);
            if (tpl == null) return;
            if (tpl.width > 0f) w = tpl.width;
            if (tpl.height > 0f) h = tpl.height;
            if (tpl.world_size > 0f && (tpl.width <= 0f || tpl.height <= 0f))
            {
                if (tpl.width <= 0f) w = tpl.world_size;
                if (tpl.height <= 0f) h = tpl.world_size;
            }
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

    /// <summary>
    /// Stateless per-cue runtime: every cue carries compiled start/end snapshots,
    /// so state resolution happens at compile time and jumping is a pure lookup.
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
            if (cue == null) return null;
            if (!string.IsNullOrEmpty(cue.stage))
                return stages.TryGetValue(cue.stage, out var cueStage) ? cueStage : null;
            if (!trees.TryGetValue(cue.tree ?? "", out var tree)) return null;
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
