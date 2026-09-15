// BoardGameTutorial
// 一个组件实例的渲染壳。
//
// 它只知道「我长什么样」和「我现在被摆在哪」；位置全部来自 ZoneStore 的
// (zone, slot) 推导，actor 自己不保存逻辑状态。
using UnityEngine;

namespace BoardGameTutorial
{
    public class CueAnimActor
    {
        public readonly ZoneItem Item;
        public readonly GameObject Go;
        public readonly SpriteRenderer Renderer;
        public readonly Sprite Sprite;

        public readonly Vector3 BaseScale;
        public readonly float BaseAlpha;
        public readonly Color BaseColor;

        public Vector3 LivePosition;
        public Vector3 LiveScale;
        public float LiveRotation;
        public float LiveAlpha;
        public Color LiveColor;

        public CueAnimActor(ZoneItem item, Sprite sprite, GameObject go, SpriteRenderer renderer, Vector3 baseScale)
        {
            Item = item;
            Sprite = sprite;
            Go = go;
            Renderer = renderer;
            BaseScale = baseScale;
            BaseAlpha = item.LiveAlpha;
            BaseColor = item.BaseColor;

            LivePosition = item.LivePosition;
            LiveScale = baseScale;
            LiveRotation = item.LiveRotation;
            LiveAlpha = item.LiveAlpha;
            LiveColor = item.BaseColor;
        }

        public void ApplyColor()
        {
            if (Renderer == null) return;
            var c = LiveColor;
            c.a = Mathf.Clamp01(LiveAlpha);
            Renderer.color = c;
        }
    }
}
