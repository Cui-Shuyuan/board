// BoardGameTutorial
// 卡牌扫描图加载器：JPG/PNG → Sprite，并把白底与白边转成透明。
//
// 为什么需要：手里的发展卡是实物扫描（白底 + 圆角），直接当 sprite 会画出一块白方块。
// 这里不引入任何图像库，只是读出像素、按亮度做一次 alpha key，再修掉边缘残留的白点。
//
// 结果按路径缓存，同一张图只处理一次。素材不进 Git，Unity 在运行时从
// games/{game}/media/card/ 直接读文件。
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

        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        public static Sprite Load(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return null;
            if (Cache.TryGetValue(absolutePath, out var cached)) return cached;
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

            ApplyWhiteKey(tex);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            Cache[absolutePath] = sprite;
            return sprite;
        }

        /// <summary>把白底变透明。alpha = 1 - 亮度，并在白底阈值处截止，避免卡片边缘留一圈灰。</summary>
        private static void ApplyWhiteKey(Texture2D tex)
        {
            var pixels = tex.GetPixels();
            int width = tex.width;
            int height = tex.height;

            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                // 卡片外部是白底（含 jpeg 噪点），按亮度做一个软阈值。
                float luma = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;

                float a;
                if (luma >= WhiteCutoff) a = 0f;
                else if (luma <= OpaqueCutoff) a = 1f;
                else a = Mathf.InverseLerp(WhiteCutoff, OpaqueCutoff, luma);

                c.a = a;
                pixels[i] = c;
            }

            tex.SetPixels(pixels);
            tex.Apply();

            // 圆角外的白像素带 jpeg 噪点，逐像素阈值化后会留下孤立的半透明白点。
            // 用一次收缩把这些白点并入卡片本体，避免画面出现一圈毛刺。
            ErodeTransparentFringe(tex, width, height);
        }

        private static void ErodeTransparentFringe(Texture2D tex, int width, int height)
        {
            var src = tex.GetPixels();
            var dst = (Color[])src.Clone();
            bool changed = false;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (src[i].a <= 0f) continue;

                    // 四周有完全透明的邻居 → 该像素属于边缘白噪点，收掉。
                    bool transparentNeighbour =
                        (x > 0 && src[i - 1].a <= 0f) ||
                        (x < width - 1 && src[i + 1].a <= 0f) ||
                        (y > 0 && src[i - width].a <= 0f) ||
                        (y < height - 1 && src[i + width].a <= 0f);

                    if (transparentNeighbour && src[i].a < 0.6f)
                    {
                        var c = dst[i];
                        c.a = 0f;
                        dst[i] = c;
                        changed = true;
                    }
                }
            }

            if (!changed) return;
            tex.SetPixels(dst);
            tex.Apply();
        }
    }
}
