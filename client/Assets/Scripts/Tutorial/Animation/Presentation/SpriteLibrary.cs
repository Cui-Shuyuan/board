using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BoardGameTutorial.Animation
{
    /// <summary>
    /// Loads the sprite for a compiled template.  Image paths are baked into the
    /// compiled asset; this class only resolves them against the game root.
    /// </summary>
    public sealed class SpriteLibrary
    {
        private readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();
        private string gameRoot;
        private Sprite whiteSprite;

        public void Init(string root)
        {
            gameRoot = root;
            cache.Clear();
        }

        public bool HasFaceImage(CompiledTemplateDef tpl, string palette = null)
        {
            return !string.IsNullOrEmpty(ResolveFacePath(tpl, palette));
        }

        public Sprite LoadFace(CompiledTemplateDef tpl, string palette = null)
        {
            if (tpl == null) return White();
            return LoadFile(ResolveFacePath(tpl, palette), tpl.shape) ?? White();
        }

        public Sprite LoadRelative(string relative, string shape = "card")
        {
            return LoadFile(relative, shape);
        }

        public Sprite LoadBack(CompiledTemplateDef tpl, string palette = null)
        {
            string path = ResolveBackPath(tpl, palette);
            return string.IsNullOrEmpty(path) ? null : LoadFile(path, tpl.shape);
        }

        private static string ResolveFacePath(CompiledTemplateDef tpl, string palette)
        {
            if (tpl == null) return null;
            if (!string.IsNullOrEmpty(palette) && tpl.face_image_by_palette != null)
                foreach (var m in tpl.face_image_by_palette)
                    if (m != null && m.palette == palette && !string.IsNullOrEmpty(m.face_image))
                        return m.face_image;
            return tpl.face_image;
        }

        private static string ResolveBackPath(CompiledTemplateDef tpl, string palette)
        {
            if (tpl == null) return null;
            if (!string.IsNullOrEmpty(palette) && tpl.face_image_by_palette != null)
                foreach (var m in tpl.face_image_by_palette)
                    if (m != null && m.palette == palette && !string.IsNullOrEmpty(m.back_image))
                        return m.back_image;
            return tpl.back_image;
        }

        private Sprite LoadFile(string relative, string shape)
        {
            if (string.IsNullOrEmpty(relative) || string.IsNullOrEmpty(gameRoot)) return null;
            string key = relative + "|" + (shape ?? "");
            if (cache.TryGetValue(key, out var cached)) return cached;

            string path = Path.IsPathRooted(relative) ? relative : Path.Combine(gameRoot, relative);
            var sprite = File.Exists(path) ? CardImageLoader.Load(path, string.IsNullOrEmpty(shape) ? "card" : shape) : null;
            cache[key] = sprite;
            return sprite;
        }

        private Sprite White()
        {
            if (whiteSprite != null) return whiteSprite;
            var tex = new Texture2D(2, 2);
            var pixels = new Color[4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
            tex.SetPixels(pixels);
            tex.Apply();
            whiteSprite = Sprite.Create(tex, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f), 100f);
            return whiteSprite;
        }
    }
}
