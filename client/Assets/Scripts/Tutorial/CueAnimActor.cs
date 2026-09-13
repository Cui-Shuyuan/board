// BoardGameTutorial
// cue 动画里的单个可见对象（sprite 实例）。
//
// 它只保存数据里的初始状态 + 运行时的当前状态；所有变换都由
// TutorialCueAnimPlayer 调原语写入，actor 本身不含动画逻辑。
using UnityEngine;

namespace BoardGameTutorial
{
    public class CueAnimActor
    {
        public readonly CueAnimActorDef Def;
        public readonly Sprite Sprite;
        public readonly GameObject Go;
        public readonly SpriteRenderer Renderer;

        /// <summary>运行时当前状态，事件参考它计算 from / to。</summary>
        public Vector3 LivePosition;
        public Vector3 LiveScale;
        public float LiveRotation;
        public float LiveAlpha;

        public CueAnimActor(CueAnimActorDef def, Sprite sprite, GameObject go, SpriteRenderer renderer)
        {
            Def = def;
            Sprite = sprite;
            Go = go;
            Renderer = renderer;

            LivePosition = new Vector3(def.x, def.y, def.z);
            LiveScale = go.transform.localScale;
            LiveRotation = def.rotation;
            LiveAlpha = Mathf.Clamp01(def.alpha);

            if (Mathf.Abs(def.rotation) > 0.001f)
            {
                go.transform.localRotation = Quaternion.Euler(0f, 0f, def.rotation);
            }
        }
    }
}
