// Annotation resolution for the pure timeline evaluator.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    public static partial class TimelineEvaluator
    {
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

    }
}
