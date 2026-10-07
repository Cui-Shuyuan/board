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
        private void LateUpdate()
        {
            // Keep lens cameras enabled so URP renders them as normal cameras
            // into their RenderTextures.  Calling Camera.Render() manually
            // from OnGUI nests a URP pass inside the main camera's render
            // context (UniversalCameraData already created) and throws every
            // frame; this path must stay out of any manual render callback.
            var frame = (v2AnimPlayer != null && v2AnimPlayer.IsLoaded && enableCueAnimation)
                ? v2AnimPlayer.CurrentFrame
                : null;
            PrepareMagnifiers(frame);
        }

        private void PrepareMagnifiers(FrameState frame)
        {
            var alive = new HashSet<string>(StringComparer.Ordinal);
            var mainCam = v2AnimPlayer != null ? v2AnimPlayer.Camera : null;
            if (frame != null && frame.Magnifiers != null)
            {
                foreach (var m in frame.Magnifiers)
                {
                    if (m == null || m.Alpha <= 0.001f) continue;
                    Rect lensRect = ResolveMagnifierRect(m);
                    int texW = Mathf.Clamp(Mathf.RoundToInt(lensRect.width), 64, 1024);
                    int texH = Mathf.Clamp(Mathf.RoundToInt(lensRect.height), 64, 1024);
                    var view = GetMagnifierView(m.Id, texW, texH);
                    if (view == null || view.Cam == null) continue;

                    // Disable while mutating the camera to avoid a stale
                    // transform/size being submitted on the same frame.
                    view.Cam.enabled = false;
                    view.Cam.cullingMask = mainCam != null ? mainCam.cullingMask : ~0;
                    view.Cam.clearFlags = CameraClearFlags.SolidColor;
                    bool fullMask = MagnifierMaskMode(m) == "full";
                    bool circleLens = MagnifierShape(m) == "circle";
                    bool opaqueTable = fullMask && !circleLens;
                    view.Cam.backgroundColor = opaqueTable
                        ? (mainCam != null ? mainCam.backgroundColor : Color.black)
                        : new Color(0f, 0f, 0f, 0f);
                    view.Cam.aspect = (float)view.Width / Mathf.Max(1, view.Height);
                    view.Cam.orthographic = true;
                    view.Cam.orthographicSize = Mathf.Max(0.05f, m.OrthoSize);
                    float distance = Mathf.Max(0.1f, m.OrthoSize) * 3.2f;
                    view.Cam.transform.position = new Vector3(m.CenterX, distance, m.CenterZ);
                    view.Cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    view.Cam.targetTexture = view.Rt;

                    // LateUpdate is outside URP's render callbacks, so unlike
                    // the old OnGUI path this manual offscreen render is safe.
                    // Keeping the camera disabled prevents URP from also
                    // rendering it as a normal camera in the same frame.
                    // Only the event's matched targets may enter the lens;
                    // unrelated world objects that happen to fall in the same
                    // region (e.g. the market row under the nobles) must not
                    // bleed into an otherwise opaque lens.
                    view.Cam.enabled = false;
                    if (v2AnimPlayer != null) v2AnimPlayer.BeginLensRender(m.ItemIds);
                    try
                    {
                        view.Cam.Render();
                    }
                    finally
                    {
                        if (v2AnimPlayer != null) v2AnimPlayer.EndLensRender();
                    }
                    alive.Add(m.Id);
                }
            }

            var stale = new List<string>();
            foreach (var kv in magnifierViews)
                if (!alive.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (var id in stale)
                if (magnifierViews.TryGetValue(id, out var view) && view != null && view.Cam != null)
                    view.Cam.enabled = false;
        }

        private void DrawMagnifiers(FrameState frame)
        {
            if (frame == null || frame.Magnifiers == null || frame.Magnifiers.Count == 0)
            {
                ReleaseStaleMagnifiers(new HashSet<string>(StringComparer.Ordinal));
                return;
            }
            if (v2AnimPlayer == null || !v2AnimPlayer.IsLoaded) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (magnifierRenderedFrame == Time.frameCount) return;
            magnifierRenderedFrame = Time.frameCount;

            var ordered = new List<VisualMagnifierState>(frame.Magnifiers);
            ordered.Sort((a, b) => a.Layer.CompareTo(b.Layer));
            var alive = new HashSet<string>(StringComparer.Ordinal);
            var circleMask = GetMagnifierMaskTexture();

            foreach (var m in ordered)
            {
                if (m == null || m.Alpha <= 0.001f) continue;
                if (!magnifierViews.TryGetValue(m.Id, out var view) || view == null) continue;
                Rect rect = ResolveMagnifierRect(m);

                bool fullMask = MagnifierMaskMode(m) == "full";
                bool circleLens = MagnifierShape(m) == "circle";
                var savedColor = GUI.color;

                // Full + circle: put an opaque table-coloured disc inside the
                // lens, then draw the transparent object pass on top.  This
                // keeps the magnifier showing "that piece of table" instead
                // of revealing the live screen behind a removed noble.
                if (fullMask && circleLens)
                {
                    var mainCam = v2AnimPlayer != null ? v2AnimPlayer.Camera : null;
                    Color tableColor = mainCam != null ? mainCam.backgroundColor : Color.black;
                    // OnGUI consumes GUI.color as linear, while Camera.backgroundColor
                    // was authored from the sRGB stage hex.  Match the live table clear
                    // colour by converting before drawing the disc.
                    tableColor = tableColor.linear;
                    tableColor.a *= Mathf.Clamp01(m.Alpha);
                    GUI.color = tableColor;
                    GUI.DrawTexture(rect, GetMagnifierDiskTexture(), ScaleMode.StretchToFill, true);
                }

                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(m.Alpha));
                GUI.DrawTexture(rect, view.Rt, ScaleMode.StretchToFill, !(fullMask && !circleLens));
                if (circleLens && circleMask != null)
                    GUI.DrawTexture(rect, circleMask, ScaleMode.StretchToFill, true);
                else if (!circleLens)
                    DrawMagnifierBoxFrame(rect);
                GUI.color = savedColor;
                alive.Add(m.Id);
            }

            ReleaseStaleMagnifiers(alive);
        }

        private void ReleaseStaleMagnifiers(HashSet<string> alive)
        {
            var stale = new List<string>();
            foreach (var kv in magnifierViews)
                if (!alive.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (var id in stale)
            {
                var view = magnifierViews[id];
                if (view != null)
                {
                    if (view.Cam != null)
                    {
                        view.Cam.enabled = false;
                        view.Cam.targetTexture = null;
                    }
                    if (view.Rt != null) view.Rt.Release();
                    if (view.Go != null) Destroy(view.Go);
                }
                magnifierViews.Remove(id);
            }
        }

        private static string MagnifierMaskMode(VisualMagnifierState m)
        {
            if (m == null || string.IsNullOrEmpty(m.MaskMode)) return "items";
            return m.MaskMode.Trim().ToLowerInvariant() == "full" ? "full" : "items";
        }

        private static string MagnifierShape(VisualMagnifierState m)
        {
            if (m == null || string.IsNullOrEmpty(m.Shape)) return "circle";
            string shape = m.Shape.Trim().ToLowerInvariant();
            return shape == "box" ? "box" : "circle";
        }

        private static Rect ResolveMagnifierRect(VisualMagnifierState m)
        {
            float px = m.X * Screen.width;
            float py = m.Y * Screen.height;
            float pw = Mathf.Max(24f, m.W * Screen.width);
            float ph = Mathf.Max(24f, m.H * Screen.height);
            if (MagnifierShape(m) == "circle")
            {
                // `rect` is the lens' available screen box; a circle uses the
                // largest square inside it so the mask is never stretched.
                float side = Mathf.Min(pw, ph);
                return new Rect(px + (pw - side) * 0.5f, py + (ph - side) * 0.5f, side, side);
            }
            return new Rect(px, py, pw, ph);
        }

        private MagnifierView GetMagnifierView(string id, int width, int height)
        {
            if (string.IsNullOrEmpty(id)) id = "magnifier";
            if (!magnifierViews.TryGetValue(id, out var view) || view == null)
            {
                var go = new GameObject("TutorialMagnifier_" + id);
                go.transform.SetParent(transform, false);
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.depth = -100f;
                view = new MagnifierView
                {
                    Go = go,
                    Cam = cam,
                    Rt = CreateMagnifierTexture(width, height),
                    Width = width,
                    Height = height,
                };
                cam.targetTexture = view.Rt;
                magnifierViews[id] = view;
            }
            else if (view.Width != width || view.Height != height)
            {
                if (view.Cam != null) view.Cam.enabled = false;
                if (view.Rt != null) view.Rt.Release();
                view.Rt = CreateMagnifierTexture(width, height);
                view.Width = width;
                view.Height = height;
                if (view.Cam != null) view.Cam.targetTexture = view.Rt;
            }
            return view;
        }

        private static RenderTexture CreateMagnifierTexture(int width, int height)
        {
            // URP RenderGraph imports the output texture; it must have a depth
            // buffer and be explicitly created before a camera renders into it,
            // otherwise the device logs `Fake or uninitialized surface`.
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.Create();
            return rt;
        }

        private Texture2D GetMagnifierDiskTexture()
        {
            if (magnifierDiskTexture != null) return magnifierDiskTexture;
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    Color c = d <= 0.97f ? Color.white : new Color(0f, 0f, 0f, 0f);
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            magnifierDiskTexture = tex;
            return tex;
        }

        private void DrawMagnifierBoxFrame(Rect rect)
        {
            var frame = GetMagnifierFrameTexture();
            if (frame == null) return;
            float stroke = Mathf.Max(4f, Mathf.Min(rect.width, rect.height) * 0.045f);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, stroke), frame);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - stroke, rect.width, stroke), frame);
            GUI.DrawTexture(new Rect(rect.x, rect.y, stroke, rect.height), frame);
            GUI.DrawTexture(new Rect(rect.xMax - stroke, rect.y, stroke, rect.height), frame);
        }

        private Texture2D GetMagnifierFrameTexture()
        {
            if (magnifierFrameTexture != null) return magnifierFrameTexture;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixel(0, 0, new Color(0.95f, 0.82f, 0.36f, 1f));
            tex.Apply();
            magnifierFrameTexture = tex;
            return tex;
        }

        private Texture2D GetMagnifierMaskTexture()
        {
            if (magnifierMaskTexture != null) return magnifierMaskTexture;
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            var outside = new Color(0f, 0f, 0f, 0f);
            var rim = new Color(0.95f, 0.82f, 0.36f, 1f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    Color c;
                    if (d <= 0.90f) c = new Color(0f, 0f, 0f, 0f);
                    else if (d <= 0.97f) c = rim;
                    else c = outside;
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            magnifierMaskTexture = tex;
            return tex;
        }
    }
}
