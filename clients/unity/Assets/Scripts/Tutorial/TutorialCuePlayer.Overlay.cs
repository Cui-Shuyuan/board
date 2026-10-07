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
        private void OnGUI()
        {
            if (doc == null) return;

            // Render order (bottom to top):
            //   1. world/table (camera)
            //   2. mask + screen objects (DrawScreenOverlays)
            //   3. annotation markers / arrows / circles / boxes
            //   4. explanation labels
            //   5. subtitles
            //   6. debug HUD (only in debug mode)
            var frame = v2AnimPlayer != null ? v2AnimPlayer.CurrentFrame : null;
            DrawScreenOverlays(frame);
            DrawMagnifiers(frame);
            DrawAnnotations(frame);
            DrawSubtitle();

            // 左上角信息只在 zone debug 模式下显示；正常播放时屏幕底部只有字幕。
            bool zoneDebugVisible = v2AnimPlayer != null && v2AnimPlayer.debugZones;
            if (!showDebugUI || !zoneDebugVisible) return;

            if (debugStyle == null)
            {
                debugStyle = new GUIStyle(GUI.skin.label)
                {
                    wordWrap = true,
                    fontSize = 16,
                    normal = { textColor = Color.white }
                };
            }

            GUI.Box(new Rect(10, 10, Screen.width - 20, 202), doc.title ?? "Tutorial");
            GUI.Label(new Rect(24, 28, Screen.width - 48, 24), $"cue {currentIndex + 1}/{doc.cues.Count}  {CurrentCueId}", debugStyle);
            GUI.Label(new Rect(24, 52, Screen.width - 48, 24), CurrentCueGroupPath, debugStyle);
            GUI.Label(new Rect(24, 78, Screen.width - 48, 56), CurrentCueText, debugStyle);

            float t = (audioSource != null && audioSource.clip != null) ? audioSource.time : 0f;
            string animInfo = v2AnimPlayer != null && v2AnimPlayer.IsLoaded
                ? $"anim: ON  {v2AnimPlayer.CueId}  t={t:0.00}s  动画总长 {v2AnimPlayer.TotalDuration:0.00}s"
                : $"anim: none  (t={t:0.00}s)";
            if (inDebugJump) animInfo += "   [B 返回]";
            GUI.Label(new Rect(24, 138, Screen.width - 48, 24), animInfo, debugStyle);

            string animSwitch = enableCueAnimation ? "动画开关: 开" : "动画开关: 关 —— 按 G 打开（现在画面是空的）";
            var switchStyle = new GUIStyle(debugStyle);
            switchStyle.normal.textColor = enableCueAnimation ? Color.white : new Color(1f, 0.5f, 0.4f);
            GUI.Label(new Rect(24, 160, Screen.width - 48, 24), animSwitch, switchStyle);
            GUI.Label(new Rect(24, 182, Screen.width - 48, 24),
                "Space 暂停/继续  R 重播  ← 上一段  → 下一段  A 自动播放  G 动画开关  B 跳到动画切片  Z 调试模式", debugStyle);
        }

        private Texture2D GetOverlayPanelTexture()
        {
            if (overlayPanelTexture != null) return overlayPanelTexture;
            overlayPanelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            overlayPanelTexture.SetPixel(0, 0, Color.white);
            overlayPanelTexture.Apply();
            return overlayPanelTexture;
        }

        private bool TryGetOverlayRects(VisualOverlayState overlay, out Rect panelRect, out Rect cardRect)
        {
            panelRect = new Rect();
            cardRect = new Rect();
            if (overlay == null) return false;

            float baseX = overlay.X * Screen.width;
            float baseY = overlay.Y * Screen.height;
            float baseW = Mathf.Max(1f, overlay.W * Screen.width);
            float baseH = Mathf.Max(1f, overlay.H * Screen.height);

            // scale/highlight are part of the common IAnimVisualObject
            // interface; screen overlays apply them around their rect center.
            float zoom = Mathf.Max(0.05f, overlay.Scale);
            if (overlay.Highlighted) zoom *= Mathf.Max(1f, overlay.HighlightGrow);
            float w = Mathf.Max(1f, baseW * zoom);
            float h = Mathf.Max(1f, baseH * zoom);
            float x = baseX + (baseW - w) * 0.5f;
            float y = baseY + (baseH - h) * 0.5f;
            panelRect = new Rect(x, y, w, h);
            cardRect = panelRect;

            var sprite = v2AnimPlayer != null ? v2AnimPlayer.LoadOverlaySprite(overlay) : null;
            if (sprite != null && sprite.texture != null)
            {
                float inset = Mathf.Min(w, h) * 0.06f;
                float availW = Mathf.Max(1f, w - inset * 2f);
                float availH = Mathf.Max(1f, h - inset * 2f);
                float srcW = Mathf.Max(1f, sprite.rect.width);
                float srcH = Mathf.Max(1f, sprite.rect.height);
                float k = Mathf.Min(availW / srcW, availH / srcH);
                float cardW = srcW * k;
                float cardH = srcH * k;
                cardRect = new Rect(
                    x + (w - cardW) * 0.5f,
                    y + (h - cardH) * 0.5f,
                    cardW,
                    cardH);
            }
            return true;
        }

        private void DrawScreenOverlays(FrameState frame)
        {
            if (frame == null || frame.Overlays == null || frame.Overlays.Count == 0) return;
            if (v2AnimPlayer == null || !v2AnimPlayer.IsLoaded) return;

            var ordered = new List<VisualOverlayState>(frame.Overlays);
            ordered.Sort((a, b) => a.Layer.CompareTo(b.Layer));

            var panel = GetOverlayPanelTexture();
            var savedColor = GUI.color;
            foreach (var overlay in ordered)
            {
                if (overlay == null || overlay.Alpha <= 0.001f) continue;
                if (!TryGetOverlayRects(overlay, out var panelRect, out var cardRect)) continue;

                // A panel is drawn only when the data explicitly asks for a
                // background.  The default filled panel made every card carry
                // a visible rectangle; a screen-space card should be just the
                // cutout sprite.
                if (!string.IsNullOrEmpty(overlay.Background)
                    && Palette.TryResolveRgb(overlay.Background, out var parsed))
                {
                    // Project is Linear; OnGUI consumes this color as a linear
                    // value while the authored hex is sRGB.  Use .linear so
                    // scene_backdrop renders with exactly the stage/table
                    // color instead of a lighter conversion.
                    Color panelColor = parsed.linear;
                    panelColor.a = overlay.Alpha;
                    GUI.color = panelColor;
                    GUI.DrawTexture(panelRect, panel);
                }

                var sprite = v2AnimPlayer.LoadOverlaySprite(overlay);
                if (sprite != null && sprite.texture != null)
                {
                    GUI.color = new Color(1f, 1f, 1f, overlay.Alpha);
                    GUI.DrawTexture(cardRect, sprite.texture);
                }
                GUI.color = savedColor;
            }
            GUI.color = savedColor;
        }
    }
}
