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

            var face = sprites.LoadFace(tpl);
            var back = sprites.LoadBack(tpl);
            sr.sprite = item.Face == FaceState.Down && back != null ? back : face;
            sr.enabled = item.Alpha > 0.001f;
            sr.sortingOrder = tpl != null ? tpl.sorting_order : 0;

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
