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
        private readonly List<GameObject> markerObjects = new List<GameObject>();
        private Transform markerRoot;
        private static readonly Dictionary<string, Sprite> MarkerSprites = new Dictionary<string, Sprite>();
        private static readonly Color MarkerColor = new Color(0.92f, 0.24f, 0.20f, 1f);

        public void Init(Transform root, StageRuntime stage, SpriteLibrary sprites, Camera camera)
        {
            this.root = root;
            this.stage = stage;
            this.sprites = sprites;
            this.camera = camera;
        }

        public void Clear()
        {
            foreach (var kv in actors)
                if (kv.Value != null) Object.Destroy(kv.Value);
            actors.Clear();
            if (pictureRenderer != null)
            {
                Object.Destroy(pictureRenderer.gameObject);
                pictureRenderer = null;
            }
            foreach (var go in markerObjects)
                if (go != null) Object.Destroy(go);
            markerObjects.Clear();
            if (markerRoot != null)
            {
                Object.Destroy(markerRoot.gameObject);
                markerRoot = null;
            }
            currentPicture = null;
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
            SyncMarkers(frame.Markers);
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
            go.transform.localScale = baseScale * (item.Scale * grow);
            go.transform.localPosition = new Vector3(item.X, 0f, item.Z);
            go.transform.localRotation = stage.SpriteRotation(item.Rotation);

            if (!string.IsNullOrEmpty(item.PointPart))
            {
                // Point/part is intentionally carried in FrameState.  The final
                // marker animation is a presentation primitive and may be swapped
                // without changing the pure timeline model.
            }
        }

        private void SyncMarkers(List<VisualMarkerState> markers)
        {
            int count = markers != null ? markers.Count : 0;
            if (count == 0)
            {
                foreach (var go in markerObjects)
                    if (go != null) go.SetActive(false);
                return;
            }
            if (markerRoot == null)
            {
                var rootGo = new GameObject("v2:Markers");
                if (root != null) rootGo.transform.SetParent(root, false);
                markerRoot = rootGo.transform;
            }
            while (markerObjects.Count < count)
            {
                var go = new GameObject("v2:marker");
                go.transform.SetParent(markerRoot, false);
                go.AddComponent<SpriteRenderer>();
                markerObjects.Add(go);
            }
            for (int i = 0; i < markerObjects.Count; i++)
            {
                var go = markerObjects[i];
                if (go == null) continue;
                if (i >= count)
                {
                    go.SetActive(false);
                    continue;
                }
                var m = markers[i];
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr == null) sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = MarkerSprite(m != null ? m.Kind : "forbid");
                sr.color = MarkerColor;
                sr.sortingOrder = 1000;
                sr.enabled = true;
                float radius = m != null && m.Radius > 0f ? m.Radius : 0.2f;
                float d = Mathf.Max(0.05f, radius * 2f);
                go.transform.localScale = new Vector3(d, d, 1f);
                if (m != null) go.transform.localPosition = new Vector3(m.X, 0f, m.Z);
                if (stage != null && stage.Stage != null) go.transform.localRotation = stage.SpriteRotation(0f);
                go.SetActive(true);
            }
        }

        private static Sprite MarkerSprite(string kind)
        {
            string key = string.IsNullOrEmpty(kind) ? "forbid" : kind;
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
                            acc += MarkerCoverage(key, u, v);
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

        private static float MarkerCoverage(string kind, float u, float v)
        {
            float r = Mathf.Sqrt(u * u + v * v);
            switch (kind)
            {
                case "circle":
                    return r <= 0.98f && r >= 0.78f ? 1f : 0f;
                case "forbid":
                    if (r <= 0.98f && r >= 0.78f) return 1f;
                    if (Mathf.Abs(u - v) <= 0.075f && r <= 0.95f) return 1f;
                    return 0f;
                case "cross":
                    if (Mathf.Abs(u - v) <= 0.10f && r <= 0.72f) return 1f;
                    if (Mathf.Abs(u + v) <= 0.10f && r <= 0.72f) return 1f;
                    return 0f;
                default:
                    float a = (u + 0.1f) - (v - 0.1f);
                    if (Mathf.Abs(a) <= 0.055f && r <= 0.80f) return 1f;
                    Vector2 tip = new Vector2(-0.72f, 0.72f);
                    Vector2 p = new Vector2(u, v);
                    Vector2 dir = new Vector2(1f, -1f).normalized;
                    Vector2 perp = new Vector2(1f, 1f).normalized;
                    Vector2 d = p - tip;
                    float along = Vector2.Dot(d, dir);
                    float side = Mathf.Abs(Vector2.Dot(d, perp));
                    return along >= 0f && along <= 0.34f && side <= along * 0.85f ? 1f : 0f;
            }
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
