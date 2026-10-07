using System;
// BoardGameTutorial
// 音频/字幕/导航外壳（v2-only）。
//
// 动画本身由 BoardGameTutorial.Animation.TutorialAnimPlayer 提供：
// 运行时只读 {track}.compiled.json，按 audioSource.time 调 Seek(t)。
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BoardGameTutorial.Animation;
using UnityEngine;
using UnityEngine.Networking;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace BoardGameTutorial
{
    public partial class TutorialCuePlayer : MonoBehaviour
    {
        private void DrawAnnotations(FrameState frame)
        {
            if (frame == null || frame.Annotations == null || frame.Annotations.Count == 0) return;
            if (v2AnimPlayer == null || !v2AnimPlayer.IsLoaded) return;
            var cam = v2AnimPlayer.Camera;
            if (cam == null) return;

            EnsureOverlayLabelStyles();

            // Shapes first, then labels, then subtitles/debug HUD.  Both
            // annotation spaces draw in this single OnGUI pass, i.e. always
            // above the mask/screen-object layer.
            foreach (var annotation in frame.Annotations)
            {
                if (annotation == null || annotation.Kind == "label") continue;
                DrawShapeAnnotation(frame, annotation, cam);
            }
            foreach (var annotation in frame.Annotations)
            {
                if (annotation == null || annotation.Kind != "label") continue;
                DrawLabelAnnotation(frame, annotation, cam);
            }
        }

        private void DrawShapeAnnotation(FrameState frame, VisualAnnotationState annotation, Camera cam)
        {
            if (annotation == null) return;
            float ppu = Screen.height / Mathf.Max(0.001f, 2f * cam.orthographicSize);
            float uiScale = UiScale();
            float strokePx = ResolvedStrokePx(annotation);
            Color color = ResolvedAnnotationColor(annotation);
            bool world = annotation.Space == "world";

            if (world)
            {
                var screen = cam.WorldToScreenPoint(new Vector3(annotation.X, 0f, annotation.Z));
                if (screen.z < 0f) return;
                float cx = screen.x + annotation.NudgeX * Screen.width;
                float cy = (Screen.height - screen.y) + annotation.NudgeY * Screen.height;
                if (annotation.Kind == "box")
                {
                    float w = Mathf.Max(8f, annotation.W * ppu);
                    float h = Mathf.Max(8f, annotation.H * ppu);
                    DrawBoxOutline(new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h), strokePx, color);
                    return;
                }
                bool worldArrow = annotation.Kind == "arrow";
                bool hasPartSize = annotation.PartW > 0f && annotation.PartH > 0f;
                float partPx = hasPartSize ? Mathf.Max(annotation.W, annotation.H) * ppu : 0f;
                float fallbackPx = annotation.Radius * 2f * ppu;
                float baseSize = partPx > 0f ? partPx : fallbackPx;
                float autoSize = worldArrow
                    ? Mathf.Max(48f, baseSize * 1.35f)
                    : Mathf.Max(24f, baseSize);
                float size = ResolvedSizePx(annotation, autoSize);
                float strokeNorm = size > 0f ? strokePx / size : 0.075f;
                if (worldArrow)
                {
                    const float tipU = 0.91f;
                    float tipX = cx - ResolvedGapPx(annotation, partPx);
                    DrawMarkerSprite(annotation.Kind,
                        new Rect(tipX - size * tipU, cy - size * 0.5f, size, size),
                        strokeNorm, color);
                }
                else
                {
                    DrawMarkerSprite(annotation.Kind,
                        new Rect(cx - size * 0.5f, cy - size * 0.5f, size, size),
                        strokeNorm, color);
                }
                return;
            }

            // Screen annotation: resolve the live overlay rect first, then the
            // compiler-baked fallback rect (for static screen anchors).
            Rect rect;
            if (!TryGetAnnotationScreenRect(frame, annotation, out rect)) return;
            if (annotation.Kind == "box")
            {
                Rect boxRect = rect;
                if (annotation.PartW > 0f && annotation.PartH > 0f)
                {
                    float bw = annotation.PartW * rect.width;
                    float bh = annotation.PartH * rect.height;
                    float bx = rect.x + rect.width * annotation.PartU + annotation.NudgeX * Screen.width;
                    float by = rect.y + rect.height * annotation.PartV + annotation.NudgeY * Screen.height;
                    boxRect = new Rect(bx - bw * 0.5f, by - bh * 0.5f, bw, bh);
                }
                DrawBoxOutline(boxRect, strokePx, color);
                return;
            }
            bool screenArrow = annotation.Kind == "arrow";
            bool screenHasPartSize = annotation.PartW > 0f && annotation.PartH > 0f;
            float screenPartPx = screenHasPartSize
                ? Mathf.Max(annotation.PartW * rect.width, annotation.PartH * rect.height)
                : 0f;
            float screenFallbackPx = Mathf.Min(rect.width, rect.height) * (screenArrow ? 0.32f : 0.20f);
            float screenBaseSize = screenPartPx > 0f ? screenPartPx : screenFallbackPx;
            float screenAutoSize = screenArrow
                ? Mathf.Max(96f, screenBaseSize * 1.35f)
                : Mathf.Max(28f, screenBaseSize);
            float screenMarkerSize = ResolvedSizePx(annotation, screenAutoSize);
            float screenStrokeNorm = screenMarkerSize > 0f ? strokePx / screenMarkerSize : 0.075f;
            float mx = rect.x + rect.width * annotation.PartU + annotation.NudgeX * Screen.width;
            float my = rect.y + rect.height * annotation.PartV + annotation.NudgeY * Screen.height;
            if (screenArrow)
            {
                const float tipU = 0.91f;
                float tipX = mx - ResolvedGapPx(annotation, screenPartPx);
                DrawMarkerSprite(annotation.Kind,
                    new Rect(tipX - screenMarkerSize * tipU, my - screenMarkerSize * 0.5f,
                             screenMarkerSize, screenMarkerSize),
                    screenStrokeNorm, color);
            }
            else
            {
                DrawMarkerSprite(annotation.Kind,
                    new Rect(mx - screenMarkerSize * 0.5f, my - screenMarkerSize * 0.5f,
                             screenMarkerSize, screenMarkerSize),
                    screenStrokeNorm, color);
            }
        }

        private static float UiScale()
        {
            // Style numbers in the source script use a 1080p reference.
            return Mathf.Max(0.2f, Screen.height / 1080f);
        }

        private static float ResolvedStrokePx(VisualAnnotationState annotation)
        {
            float basePx = annotation != null && annotation.StrokePx > 0f
                ? annotation.StrokePx : 11f;
            return basePx * UiScale();
        }

        private static float ResolvedSizePx(VisualAnnotationState annotation, float autoPx)
        {
            if (annotation != null && annotation.SizePx > 0f)
                return annotation.SizePx * UiScale();
            return autoPx;
        }

        private static float ResolvedGapPx(VisualAnnotationState annotation, float partPx)
        {
            if (annotation != null && annotation.GapPx > 0f)
                return annotation.GapPx * UiScale();
            if (partPx > 0f)
                return partPx * 0.5f + 6f * UiScale();
            return Mathf.Max(10f, Screen.height * 0.014f);
        }

        private static Color ResolvedAnnotationColor(VisualAnnotationState annotation)
        {
            string hex = annotation != null ? annotation.ColorHex : null;
            if (string.IsNullOrEmpty(hex)) return AnnotationColor;
            Color parsed;
            if (ColorUtility.TryParseHtmlString(hex, out parsed)) return parsed;
            return AnnotationColor;
        }

        private static readonly Color AnnotationColor = new Color(1f, 0.12f, 0.12f, 1f);

        private const int LabelMaxCharsPerLine = 15;

        private static string WrapLabelText(string text, int maxCharsPerLine)
        {
            if (string.IsNullOrEmpty(text) || maxCharsPerLine <= 0) return text;

            var sb = new System.Text.StringBuilder(text.Length + text.Length / maxCharsPerLine + 1);
            int lineChars = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\r') continue;
                if (ch == '\n')
                {
                    sb.Append(ch);
                    lineChars = 0;
                    continue;
                }
                if (lineChars >= maxCharsPerLine)
                {
                    sb.Append('\n');
                    lineChars = 0;
                }
                sb.Append(ch);
                lineChars++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 统一文字说明格式：所有 op:label 的文字都用这里的一把尺子。
        /// 字号随屏幕高度缩放，白色字 + 深色描边，不画背景黑框；
        /// 以后新增 cue 只要用 label + screen overlay 槽位，不要自己加底框或固定字号。
        /// </summary>
        private void EnsureOverlayLabelStyles()
        {
            int fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.046f), 28, 72);
            if (overlayLabelStyle != null && labelShadowStyle != null && overlayLabelStyle.fontSize == fontSize)
                return;

            overlayLabelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
                fontSize = fontSize,
                normal = { textColor = Color.white }
            };
            labelShadowStyle = new GUIStyle(overlayLabelStyle);
            labelShadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.92f);
        }

        private void DrawLabelAnnotation(FrameState frame, VisualAnnotationState annotation, Camera cam)
        {
            if (annotation == null || string.IsNullOrEmpty(annotation.Text)) return;
            Rect rect;
            if (annotation.Space == "world")
            {
                var screen = cam.WorldToScreenPoint(new Vector3(annotation.X, 0f, annotation.Z));
                if (screen.z < 0f) return;
                float w = annotation.LabelW > 0f ? annotation.LabelW * Screen.width : Screen.width * 0.42f;
                float h = annotation.LabelH > 0f ? annotation.LabelH * Screen.height : Screen.height * 0.12f;
                float cx = screen.x + annotation.NudgeX * Screen.width;
                float cy = (Screen.height - screen.y) + annotation.NudgeY * Screen.height;
                rect = new Rect(cx - w * 0.5f, cy - h - 8f, w, h);
            }
            else
            {
                if (!TryGetAnnotationScreenRect(frame, annotation, out var anchorRect)) return;
                rect = anchorRect;
                if (annotation.LabelW > 0f && annotation.LabelH > 0f)
                {
                    rect = new Rect(
                        rect.x + annotation.NudgeX * Screen.width,
                        rect.y + annotation.NudgeY * Screen.height,
                        annotation.LabelW * Screen.width,
                        annotation.LabelH * Screen.height);
                }
            }
            // 统一文字说明格式：深色描边 + 白色正文，不画背景框；
            // 先按每行最多 LabelMaxCharsPerLine 个字自动换行，再交给 GUI 排版。
            string wrappedText = WrapLabelText(annotation.Text, LabelMaxCharsPerLine);
            float shadowScale = Mathf.Max(1f, overlayLabelStyle.fontSize / 34f);
            for (int i = 0; i < SubtitleOutlineOffsets.Length; i++)
            {
                Vector2 offset = SubtitleOutlineOffsets[i];
                GUI.Label(
                    new Rect(
                        rect.x + offset.x * shadowScale,
                        rect.y + offset.y * shadowScale,
                        rect.width,
                        rect.height),
                    wrappedText,
                    labelShadowStyle);
            }
            GUI.Label(rect, wrappedText, overlayLabelStyle);
        }

        private bool TryGetAnnotationScreenRect(FrameState frame, VisualAnnotationState annotation, out Rect rect)
        {
            rect = new Rect();
            if (annotation == null) return false;
            if (!string.IsNullOrEmpty(annotation.OverlayId))
            {
                VisualOverlayState overlay = null;
                foreach (var candidate in frame.Overlays)
                {
                    if (candidate != null && candidate.Id == annotation.OverlayId)
                    {
                        overlay = candidate;
                        break;
                    }
                }
                if (overlay != null && TryGetOverlayRects(overlay, out _, out rect)) return true;
            }
            if (annotation.ScreenW <= 0f || annotation.ScreenH <= 0f) return false;
            rect = new Rect(
                annotation.ScreenX * Screen.width,
                annotation.ScreenY * Screen.height,
                Mathf.Max(1f, annotation.ScreenW * Screen.width),
                Mathf.Max(1f, annotation.ScreenH * Screen.height));
            return true;
        }

        private void DrawMarkerSprite(string kind, Rect rect, float strokeNorm, Color color)
        {
            var sprite = ActorBinder.GetMarkerSprite(kind, strokeNorm);
            if (sprite == null || sprite.texture == null) return;
            var savedColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, sprite.texture);
            GUI.color = savedColor;
        }

        private void DrawBoxOutline(Rect rect)
        {
            DrawBoxOutline(rect, 11f * UiScale(), AnnotationColor);
        }

        private void DrawBoxOutline(Rect rect, float thickness, Color color)
        {
            var panel = GetOverlayPanelTexture();
            var savedColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), panel);
            GUI.DrawTexture(new Rect(rect.x, rect.y + rect.height - thickness, rect.width, thickness), panel);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), panel);
            GUI.DrawTexture(new Rect(rect.x + rect.width - thickness, rect.y, thickness, rect.height), panel);
            GUI.color = savedColor;
        }
    }
}
