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
            currentPicture = null;
        }

        public void Sync(FrameState frame)
        {
            if (frame == null) return;

            // Stack zones have no per-card sorting_order in the data: every card
            // in a deck shares its template's value.  Without an explicit draw
            // order Unity falls back to depth and the "bottom-looking" card can
            // cover the top one.  Mirror v1: order 0 is the deck top, so it must
            // get the highest sortingOrder.  Counts are recomputed every sample
            // because a deck shrinks as cards are drawn.
            var stackTop = new Dictionary<string, int>(System.StringComparer.Ordinal);
            foreach (var item in frame.Items)
            {
                if (item == null || !item.Visible || string.IsNullOrEmpty(item.ZoneId)) continue;
                if (!StageLookup.IsStackZone(stage.Stage, item.ZoneId)) continue;
                if (!stackTop.TryGetValue(item.ZoneId, out int top) || item.Order + 1 > top)
                    stackTop[item.ZoneId] = item.Order + 1;
            }

            var alive = new HashSet<string>();
            foreach (var item in frame.Items)
            {
                if (item == null || string.IsNullOrEmpty(item.Id)) continue;
                if (!item.Visible) continue;
                alive.Add(item.Id);
                SyncOne(item, stackTop);
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

        private void SyncOne(VisualItemState item, Dictionary<string, int> stackTop)
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

            var face = sprites.LoadFace(tpl);
            var back = sprites.LoadBack(tpl);
            sr.sprite = item.Face == FaceState.Down && back != null ? back : face;
            sr.enabled = item.Alpha > 0.001f;
            int baseSortingOrder = tpl != null ? tpl.sorting_order : 0;
            if (stackTop != null && stackTop.TryGetValue(item.ZoneId ?? "", out int stackSize) && stackSize > 0)
                sr.sortingOrder = baseSortingOrder + (stackSize - item.Order);
            else
                sr.sortingOrder = baseSortingOrder;

            Color tint = sprites.HasFaceImage(tpl) ? Color.white : Palette.Resolve(item.Palette);
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
