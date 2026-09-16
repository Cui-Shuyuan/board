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

        /// <summary>正面/背面贴图。翻面时切换，配合绕本地 Y 轴旋转。</summary>
        public Sprite FaceSprite;
        public Sprite BackSprite;

        /// <summary>实例自己的有效模板（含从实例带过来的色板），翻面/换色时用它。</summary>
        public StageTemplate EffectiveTemplate;

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
            // 染色（item.Tint）必须一起带上：它是"同级但不同类的东西一眼可分"的手段，
            // 只在渲染器上设一次会被后续复位冲掉。
            LiveColor = item.BaseColor * item.Tint;
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
