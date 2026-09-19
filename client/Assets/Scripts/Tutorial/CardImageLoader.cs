// BoardGameTutorial
// 扫描图加载器：JPG/PNG → Sprite，按**形状**处理背景。
//
// 三类素材，三种处理：
//   卡牌/贵族（矩形）  只做「白底转透明」——扫描件的白边在画面里无所谓；
//   圆形 token（宝石） 除了白底，还要切掉四角：实物是圆片而扫描件是方图，
//                     不切就会在圆片外露出一圈白。
//
// 世界尺寸不在这里硬编码：由 stage 模板的 width/height/world_size 决定，
// 所以「宝石实物到底多大」是数据问题，靠截图调，不用改代码。
//
// 扫描件不进 Git，Unity 运行时从 games/{game}/media/ 直接读文件。
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BoardGameTutorial
{
    public static class CardImageLoader
    {
        /// <summary>亮度 ≥ 该值的像素视为卡片外部的白底。</summary>
        private const float WhiteCutoff = 0.93f;

        /// <summary>亮度 ≤ 该值的像素视为完全不透明。</summary>
        private const float OpaqueCutoff = 0.78f;

        /// <summary>圆形 token 的白底阈值：token 外圈常是略灰的白，用更松的阈值。</summary>
        private const float TokenWhiteCutoff = 0.86f;

        /// <summary>圆形遮罩外再留一点余量，避免边缘出现一圈透明缝。</summary>
        private const float CircleMargin = 1.02f;

        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        public static Sprite Load(string absolutePath, string shape = "card")
        {
            if (string.IsNullOrEmpty(absolutePath)) return null;

            string key = absolutePath + "|" + shape;
            if (Cache.TryGetValue(key, out var cached)) return cached;
            if (!File.Exists(absolutePath)) return null;

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(absolutePath);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[CardImageLoader] 读取失败 {absolutePath}: {e.Message}");
                return null;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes))
            {
                Object.Destroy(tex);
                return null;
            }

            // **已经抠好的图不再二次处理**：否则运行时的启发式会把烘焙好的 alpha 覆盖掉
            //（它在这批扫描件上本来就不可靠 —— 背景亮度 0.85 落在半透明带里，四角 alpha 会留 1.0，
            //  用户看到的就是"方形白边"）。识别约定：文件名以 `_cutout.png` 结尾。
            bool preCut = absolutePath.EndsWith("_cutout.png", System.StringComparison.OrdinalIgnoreCase);
            if (preCut)
            {
                // alpha 已是成品：什么都不做
            }
            else if (shape == "gem")
            {
                ApplyTokenMask(tex);
            }
            else
            {
                ApplyWhiteKey(tex);
            }

            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            Cache[key] = sprite;
            return sprite;
        }

        // ── 矩形件：白底转透明 ────────────────────────────────────────────

        private static void ApplyWhiteKey(Texture2D tex)
        {
            var pixels = tex.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                c.a = AlphaFor(Luma(c), WhiteCutoff);
                pixels[i] = c;
            }
            tex.SetPixels(pixels);
            tex.Apply();
            ErodeTransparentFringe(tex);
        }

        // ── 圆形 token：白底 + 圆形遮罩 ──────────────────────────────────

        /// <summary>
        /// 宝石/黄金是圆片，扫描件是方图。先按「与背景的差异」找出圆心与半径，
        /// 再把圆外一律设为透明；圆内仍按亮度处理（含外圈的白色环）。
        /// </summary>
        /// <summary>
        /// 圆形 token 的白底 + 圆遮罩处理。**公开**是为了让抠图工具（Editor）复用同一套算法：
        /// 工具把这里的结果烘焙成 PNG，两边就不会漂移（改一处两边一起变）。
        /// </summary>
        public static void ApplyTokenMask(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            var pixels = tex.GetPixels();

            var circle = DetectCircle(pixels, w, h);
            bool hasCircle = circle.z > 0f;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    var c = pixels[i];
                    float a = AlphaFor(Luma(c), TokenWhiteCutoff);

                    if (hasCircle)
                    {
                        float dx = x - circle.x;
                        float dy = y - circle.y;
                        if (Mathf.Sqrt(dx * dx + dy * dy) > circle.z * CircleMargin) a = 0f;
                    }

                    c.a = a;
                    pixels[i] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            ErodeTransparentFringe(tex);
        }

        /// <summary>
        /// 找圆片：逐行取「非背景」像素的最左最右，最宽的一行给出直径，该行中点是圆心。
        /// 背景色取四角中位色，避免用固定阈值猜白。
        /// 返回 (cx, cy, radius)；找不到时 radius = 0。
        /// </summary>
        public static Vector3 DetectCircle(Color[] px, int w, int h)
        {
            var corners = new List<Color>();
            int pad = Mathf.Clamp(Mathf.Min(w, h) / 12, 4, 16);
            for (int y = 0; y < pad; y++)
                for (int x = 0; x < pad; x++)
                {
                    corners.Add(px[y * w + x]);
                    corners.Add(px[y * w + (w - 1 - x)]);
                    corners.Add(px[(h - 1 - y) * w + x]);
                    corners.Add(px[(h - 1 - y) * w + (w - 1 - x)]);
                }
            corners.Sort((a, b) => (a.r + a.g + a.b).CompareTo(b.r + b.g + b.b));
            var bg = corners[corners.Count / 2];

            const float dist = 0.10f;
            float best = 0f, bestCy = 0f, bestCx = 0f;
            for (int y = 0; y < h; y++)
            {
                int left = -1, right = -1;
                for (int x = 0; x < w; x++)
                {
                    var c = px[y * w + x];
                    float dr = c.r - bg.r, dg = c.g - bg.g, db = c.b - bg.b;
                    if (Mathf.Sqrt(dr * dr + dg * dg + db * db) <= dist) continue;
                    if (left < 0) left = x;
                    right = x;
                }
                if (left < 0) continue;
                float span = right - left + 1;
                if (span > best)
                {
                    best = span;
                    bestCy = y;
                    bestCx = (left + right) * 0.5f;
                }
            }

            return new Vector3(bestCx, bestCy, best * 0.5f);
        }

        // ── 公共 ──────────────────────────────────────────────────────────

        private static float Luma(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;

        private static float AlphaFor(float luma, float whiteCutoff)
        {
            if (luma >= whiteCutoff) return 0f;
            if (luma <= OpaqueCutoff) return 1f;
            return Mathf.InverseLerp(whiteCutoff, OpaqueCutoff, luma);
        }

        /// <summary>圆角外残留的半透明白噪点并进本体，避免边缘一圈毛刺。</summary>
        private static void ErodeTransparentFringe(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            var src = tex.GetPixels();
            var dst = (Color[])src.Clone();
            bool changed = false;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (src[i].a <= 0f || src[i].a >= 0.6f) continue;

                    bool transparentNeighbour =
                        (x > 0 && src[i - 1].a <= 0f) ||
                        (x < w - 1 && src[i + 1].a <= 0f) ||
                        (y > 0 && src[i - w].a <= 0f) ||
                        (y < h - 1 && src[i + w].a <= 0f);

                    if (!transparentNeighbour) continue;
                    var c = dst[i];
                    c.a = 0f;
                    dst[i] = c;
                    changed = true;
                }
            }

            if (!changed) return;
            tex.SetPixels(dst);
            tex.Apply();
        }
    }
}
