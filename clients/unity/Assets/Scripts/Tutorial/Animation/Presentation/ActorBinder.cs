using System.Collections.Generic;
using UnityEngine;

namespace BoardGameTutorial.Animation
{
    /// <summary>
    /// Unity-side materializer.  It reads a pure FrameState and makes the diff
    /// on GameObjects; it owns no logical state and no selection logic.
    /// </summary>
    public sealed class ActorBinder
    {
        private readonly Dictionary<string, GameObject> actors = new Dictionary<string, GameObject>();
        private Transform root;
        private StageRuntime stage;
        private SpriteLibrary sprites;
        private Camera camera;
        private SpriteRenderer pictureRenderer;
        private string currentPicture;
        private readonly Dictionary<string, bool> lensFilterSaved = new Dictionary<string, bool>();
        private bool lensFilterActive;
        private bool lensFilterPictureSaved;
        private bool lensFilterPictureWasEnabled;
        private static readonly Dictionary<string, Sprite> MarkerSprites = new Dictionary<string, Sprite>();

        public void Init(Transform root, StageRuntime stage, SpriteLibrary sprites, Camera camera)
        {
            this.root = root;
            this.stage = stage;
            this.sprites = sprites;
            this.camera = camera;
        }

        public void Clear()
        {
            lensFilterActive = false;
            lensFilterSaved.Clear();
            lensFilterPictureSaved = false;
            foreach (var kv in actors)
                if (kv.Value != null) Object.Destroy(kv.Value);
            actors.Clear();
            if (pictureRenderer != null)
            {
                Object.Destroy(pictureRenderer.gameObject);
                pictureRenderer = null;
            }
            currentPicture = null;
        }

        /// <summary>
        /// Temporarily hide every actor that is not an explicit magnifier target
        /// so an offscreen lens camera cannot render unrelated world objects
        /// that happen to fall inside the lens region.  Call
        /// <see cref="EndLensRender"/> after the lens camera has been rendered.
        /// </summary>
        public void BeginLensRender(string[] targetItemIds)
        {
            if (lensFilterActive) return;
            if (targetItemIds == null || targetItemIds.Length == 0) return;

            var keep = new HashSet<string>(targetItemIds);
            lensFilterActive = true;
            lensFilterSaved.Clear();
            foreach (var kv in actors)
            {
                if (kv.Value == null) continue;
                var sr = kv.Value.GetComponent<SpriteRenderer>();
                if (sr == null) continue;
                lensFilterSaved[kv.Key] = sr.enabled;
                if (!keep.Contains(kv.Key)) sr.enabled = false;
            }

            if (pictureRenderer != null)
            {
                lensFilterPictureSaved = true;
                lensFilterPictureWasEnabled = pictureRenderer.enabled;
                pictureRenderer.enabled = false;
            }
        }

        public void EndLensRender()
        {
            if (!lensFilterActive) return;
            lensFilterActive = false;
            foreach (var kv in lensFilterSaved)
            {
                if (actors.TryGetValue(kv.Key, out var go) && go != null)
                {
                    var sr = go.GetComponent<SpriteRenderer>();
                    if (sr != null) sr.enabled = kv.Value;
                }
            }
            lensFilterSaved.Clear();

            if (lensFilterPictureSaved)
            {
                if (pictureRenderer != null) pictureRenderer.enabled = lensFilterPictureWasEnabled;
                lensFilterPictureSaved = false;
            }
        }

        public void Sync(FrameState frame)
        {
            if (frame == null) return;

            var alive = new HashSet<string>();
            foreach (var item in frame.Items)
            {
                if (item == null || string.IsNullOrEmpty(item.Id)) continue;
                if (!item.Visible) continue;
                alive.Add(item.Id);
                SyncOne(item);
            }

            var remove = new List<string>();
            foreach (var kv in actors)
                if (!alive.Contains(kv.Key)) remove.Add(kv.Key);
            foreach (var id in remove)
            {
                if (actors.TryGetValue(id, out var go) && go != null) Object.Destroy(go);
                actors.Remove(id);
            }

            SyncPicture(frame.Picture);
        }

        private void SyncPicture(string picture)
        {
            if (string.IsNullOrEmpty(picture))
            {
                if (pictureRenderer != null) pictureRenderer.enabled = false;
                currentPicture = null;
                return;
            }
            if (picture == currentPicture && pictureRenderer != null && pictureRenderer.sprite != null) return;

            var sprite = sprites.LoadRelative(picture, "card");
            if (sprite == null) return;
            if (pictureRenderer == null)
            {
                var go = new GameObject("v2:BoxArt");
                if (root != null) go.transform.SetParent(root, false);
                pictureRenderer = go.AddComponent<SpriteRenderer>();
                pictureRenderer.sortingOrder = -900;
            }
            pictureRenderer.sprite = sprite;
            pictureRenderer.enabled = true;
            currentPicture = picture;

            if (camera == null) return;
            float aspect = camera.aspect > 0.01f ? camera.aspect : 1.7778f;
            float ortho = camera.orthographicSize > 0.01f ? camera.orthographicSize : 2.8f;
            float viewH = 2f * ortho;
            float viewW = viewH * aspect;
            float ppu = sprite.pixelsPerUnit > 0f ? sprite.pixelsPerUnit : 100f;
            float nativeW = sprite.rect.width / ppu;
            float nativeH = sprite.rect.height / ppu;
            float k = Mathf.Min(viewH * 0.92f / Mathf.Max(0.001f, nativeH),
                                viewW * 0.92f / Mathf.Max(0.001f, nativeW));
            pictureRenderer.transform.localScale = new Vector3(k, k, 1f);
            pictureRenderer.transform.rotation = camera.transform.rotation;
            pictureRenderer.transform.position = camera.transform.position + camera.transform.forward * ortho;
        }

        private void SyncOne(VisualItemState item)
        {
            if (!actors.TryGetValue(item.Id, out var go) || go == null)
            {
                go = new GameObject("v2:" + item.Id);
                if (root != null) go.transform.SetParent(root, false);
                go.AddComponent<SpriteRenderer>();
                actors[item.Id] = go;
            }

            stage.TryTemplate(item.TemplateId, out var tpl);
            var sr = go.GetComponent<SpriteRenderer>();
            if (sr == null) sr = go.AddComponent<SpriteRenderer>();

            var face = sprites.LoadFace(tpl, item.Palette);
            var back = sprites.LoadBack(tpl, item.Palette);
            sr.sprite = item.Face == FaceState.Down && back != null ? back : face;
            sr.enabled = item.Alpha > 0.001f;
            int baseSortingOrder = tpl != null ? tpl.sorting_order : 0;
            // Layer is canonical cover order: larger layer = closer to the top.
            sr.sortingOrder = baseSortingOrder + item.Layer;

            Color tint = sprites.HasFaceImage(tpl, item.Palette) ? Color.white : Palette.Resolve(item.Palette);
            tint.a = Mathf.Clamp01(item.Alpha);
            sr.color = tint;

            var baseScale = BaseScale(tpl, sr.sprite);
            float grow = item.Highlighted ? item.HighlightGrow : 1f;
            var finalScale = baseScale * (item.Scale * grow);
            finalScale.x *= Mathf.Max(0f, item.Flip);
            go.transform.localScale = finalScale;
            go.transform.localPosition = new Vector3(item.X, 0f, item.Z);
            go.transform.localRotation = stage.SpriteRotation(item.Rotation);

            if (!string.IsNullOrEmpty(item.PointPart))
            {
                // Point/part is intentionally carried in FrameState.  The final
                // marker animation is a presentation primitive and may be swapped
                // without changing the pure timeline model.
            }
        }

        public static Sprite GetMarkerSprite(string kind, float strokeNorm)
        {
            return MarkerSprite(kind, strokeNorm);
        }

        private static Sprite MarkerSprite(string kind, float strokeNorm)
        {
            string baseKey = string.IsNullOrEmpty(kind) ? "forbid" : kind;
            float safeStroke = Mathf.Clamp(strokeNorm, 0.015f, 0.22f);
            string key = baseKey + "|" + safeStroke.ToString("F3");
            if (MarkerSprites.TryGetValue(key, out var cached)) return cached;

            const int N = 128;
            const int S = 2;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float acc = 0f;
                    for (int sy = 0; sy < S; sy++)
                        for (int sx = 0; sx < S; sx++)
                        {
                            float u = ((x + (sx + 0.5f) / S) / N) * 2f - 1f;
                            float v = ((y + (sy + 0.5f) / S) / N) * 2f - 1f;
                            acc += MarkerCoverage(baseKey, u, v, safeStroke);
                        }
                    px[y * N + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(acc / (S * S)));
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            var sprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N);
            MarkerSprites[key] = sprite;
            return sprite;
        }

        private static float MarkerCoverage(string kind, float u, float v, float strokeNorm)
        {
            float r = Mathf.Sqrt(u * u + v * v);
            switch (kind)
            {
                case "circle":
                {
                    float inner = Mathf.Max(0.40f, 0.98f - 2f * strokeNorm);
                    return r <= 0.98f && r >= inner ? 1f : 0f;
                }
                case "forbid":
                {
                    float inner = Mathf.Max(0.40f, 0.98f - 2f * strokeNorm);
                    if (r <= 0.98f && r >= inner) return 1f;
                    if (Mathf.Abs(u - v) <= strokeNorm && r <= 0.95f) return 1f;
                    return 0f;
                }
                case "cross":
                    if (Mathf.Abs(u - v) <= strokeNorm * 1.4f && r <= 0.72f) return 1f;
                    if (Mathf.Abs(u + v) <= strokeNorm * 1.4f && r <= 0.72f) return 1f;
                    return 0f;
                default:
                {
                    // Big right-pointing arrow (→), drawn with a shaft and two
                    // straight head strokes.
                    float thickness = strokeNorm;
                    if (DistanceToSegment(u, v, -0.92f, 0f, 0.82f, 0f) <= thickness)
                        return 1f;
                    if (DistanceToSegment(u, v, 0.82f, 0f, -0.02f, 0.52f) <= thickness)
                        return 1f;
                    if (DistanceToSegment(u, v, 0.82f, 0f, -0.02f, -0.52f) <= thickness)
                        return 1f;
                    return 0f;
                }
            }
        }

        private static float DistanceToSegment(float px, float py,
                                                float ax, float ay,
                                                float bx, float by)
        {
            float dx = bx - ax;
            float dy = by - ay;
            float lenSq = dx * dx + dy * dy;
            if (lenSq <= 0.000001f)
                return Mathf.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
            float t = Mathf.Clamp01(((px - ax) * dx + (py - ay) * dy) / lenSq);
            float qx = ax + t * dx;
            float qy = ay + t * dy;
            return Mathf.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
        }

        private static Vector3 BaseScale(CompiledTemplateDef tpl, Sprite sprite)
        {
            float worldSize = tpl != null && tpl.world_size > 0f ? tpl.world_size : 0.2f;
            if (tpl != null && tpl.width > 0f && tpl.height > 0f && sprite != null)
            {
                float sx = Mathf.Max(0.0001f, sprite.bounds.size.x);
                float sy = Mathf.Max(0.0001f, sprite.bounds.size.y);
                return new Vector3(tpl.width / sx, tpl.height / sy, 1f);
            }
            if (sprite != null)
            {
                float s = Mathf.Max(0.0001f, Mathf.Max(sprite.bounds.size.x, sprite.bounds.size.y));
                return Vector3.one * (worldSize / s);
            }
            return Vector3.one * worldSize;
        }
    }
}
