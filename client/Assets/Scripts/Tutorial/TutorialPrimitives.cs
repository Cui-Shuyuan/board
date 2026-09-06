// 动画原语库。
//
// 所有动画都是 position / rotation / scale / alpha 的插值，不引入骨骼或逐帧动画。
// 教学动画只允许通过 tutorial.json 选择这些原语并传参，不要再为单个动画写新的协程。
using System.Collections;
using UnityEngine;

namespace BoardGameTutorial
{
    public static class TutorialPrimitives
    {
        /// <summary>
        /// 世界空间位置插值。from/to 可以是 slot 坐标，也可以是任意位置。
        /// </summary>
        public static IEnumerator TweenPosition(Transform target, Vector3 from, Vector3 to, float duration, string easing)
        {
            if (duration <= 0f)
            {
                target.position = to;
                yield break;
            }

            float t = 0f;
            while (t < duration)
            {
                t = Mathf.Min(t + Time.deltaTime, duration);
                float k = Easing.Evaluate(easing, t / duration);
                target.position = Vector3.LerpUnclamped(from, to, k);
                yield return null;
            }
            target.position = to;
        }

        /// <summary>
        /// 绕本地 Y 轴旋转（桌面上的卡牌/板块转向）。
        /// fromYaw/toYaw 为角度制。
        /// </summary>
        public static IEnumerator TweenYaw(Transform target, float fromYaw, float toYaw, float duration, string easing)
        {
            if (duration <= 0f)
            {
                ApplyYaw(target, toYaw);
                yield break;
            }

            float t = 0f;
            while (t < duration)
            {
                t = Mathf.Min(t + Time.deltaTime, duration);
                float k = Easing.Evaluate(easing, t / duration);
                ApplyYaw(target, Mathf.LerpUnclamped(fromYaw, toYaw, k));
                yield return null;
            }
            ApplyYaw(target, toYaw);
        }

        private static void ApplyYaw(Transform target, float yaw)
        {
            Vector3 e = target.localEulerAngles;
            e.y = yaw;
            target.localEulerAngles = e;
        }

        /// <summary>
        /// 翻面：绕本地 Y 轴旋转 180 度。from 传当前角度即可。
        /// </summary>
        public static IEnumerator TweenFlip(Transform target, float fromYaw, float duration, string easing)
        {
            return TweenYaw(target, fromYaw, fromYaw + 180f, duration, easing);
        }

        /// <summary>
        /// 缩放插值。fromScale/toScale 为本地缩放。
        /// </summary>
        public static IEnumerator TweenScale(Transform target, Vector3 fromScale, Vector3 toScale, float duration, string easing)
        {
            if (duration <= 0f)
            {
                target.localScale = toScale;
                yield break;
            }

            float t = 0f;
            while (t < duration)
            {
                t = Mathf.Min(t + Time.deltaTime, duration);
                float k = Easing.Evaluate(easing, t / duration);
                target.localScale = Vector3.LerpUnclamped(fromScale, toScale, k);
                yield return null;
            }
            target.localScale = toScale;
        }

        /// <summary>
        /// 透明度插值。SpriteRenderer.color 的 alpha 会被写入。
        /// </summary>
        public static IEnumerator TweenAlpha(SpriteRenderer renderer, float fromAlpha, float toAlpha, float duration, string easing)
        {
            if (renderer == null) yield break;

            if (duration <= 0f)
            {
                SetAlpha(renderer, toAlpha);
                yield break;
            }

            float t = 0f;
            while (t < duration)
            {
                t = Mathf.Min(t + Time.deltaTime, duration);
                float k = Easing.Evaluate(easing, t / duration);
                SetAlpha(renderer, Mathf.LerpUnclamped(fromAlpha, toAlpha, k));
                yield return null;
            }
            SetAlpha(renderer, toAlpha);
        }

        private static void SetAlpha(SpriteRenderer renderer, float alpha)
        {
            Color c = renderer.color;
            c.a = Mathf.Clamp01(alpha);
            renderer.color = c;
        }

        /// <summary>
        /// 单牌堆原地洗混：轻微抖动 + 左右摇摆，不改变位置。
        /// 用于 tutorial.json 里 shuffle 只给单个 sprite 的牌堆场景。
        /// </summary>
        public static IEnumerator TweenShuffleInPlace(Transform target, float duration, string easing)
        {
            if (target == null) yield break;

            Vector3 baseScale = target.localScale;
            Vector3 baseEuler = target.localEulerAngles;

            float t = 0f;
            while (t < duration)
            {
                t = Mathf.Min(t + Time.deltaTime, duration);
                float k = Easing.Evaluate(easing, t / duration);
                float wave = Mathf.Sin(k * Mathf.PI * 6f);
                target.localScale = baseScale * (1f + 0.03f * wave);
                Vector3 e = target.localEulerAngles;
                e.y = baseEuler.y + 10f * Mathf.Sin(k * Mathf.PI * 6f);
                target.localEulerAngles = e;
                yield return null;
            }

            target.localScale = baseScale;
            target.localEulerAngles = baseEuler;
        }

        /// <summary>
        /// 简易洗混：让一组 sprite 在若干 slot 之间做一次交叉换位。
        /// 真正的洗牌视觉可以后续增强，这里保证「数据驱动的 shuffle」有确定性的落位结果。
        /// </summary>
        public static IEnumerator TweenShuffle(Transform[] targets, Vector3[] toPositions, float duration, string easing)
        {
            if (targets == null || targets.Length == 0) yield break;
            if (toPositions == null || toPositions.Length != targets.Length)
            {
                Debug.LogWarning("[TutorialPrimitives] TweenShuffle: toPositions length must match targets length");
                yield break;
            }

            Vector3[] fromPositions = new Vector3[targets.Length];
            for (int i = 0; i < targets.Length; i++) fromPositions[i] = targets[i].position;

            float t = 0f;
            while (t < duration)
            {
                t = Mathf.Min(t + Time.deltaTime, duration);
                float k = Easing.Evaluate(easing, t / duration);
                for (int i = 0; i < targets.Length; i++)
                {
                    targets[i].position = Vector3.LerpUnclamped(fromPositions[i], toPositions[i], k);
                }
                yield return null;
            }

            for (int i = 0; i < targets.Length; i++) targets[i].position = toPositions[i];
        }
    }
}
