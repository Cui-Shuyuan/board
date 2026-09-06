// 缓动函数库。
// 输入 t 在 [0,1] 内，输出通常也在 [0,1]（Back 系列会轻微越界，这是预期效果）。
using System;

namespace BoardGameTutorial
{
    public static class Easing
    {
        public const string Default = "easeOutCubic";

        public static float Evaluate(string name, float t)
        {
            if (string.IsNullOrEmpty(name)) name = Default;
            // 先夹紧，避免越界时间造成奇怪插值。
            t = Math.Max(0f, Math.Min(1f, t));

            switch (name)
            {
                case "linear": return Linear(t);
                case "easeInQuad": return EaseInQuad(t);
                case "easeOutQuad": return EaseOutQuad(t);
                case "easeInOutQuad": return EaseInOutQuad(t);
                case "easeInCubic": return EaseInCubic(t);
                case "easeOutCubic": return EaseOutCubic(t);
                case "easeInOutCubic": return EaseInOutCubic(t);
                case "easeInBack": return EaseInBack(t);
                case "easeOutBack": return EaseOutBack(t);
                case "easeInOutBack": return EaseInOutBack(t);
                default: return EaseOutCubic(t);
            }
        }

        public static float Linear(float t) => t;

        public static float EaseInQuad(float t) => t * t;

        public static float EaseOutQuad(float t) => t * (2f - t);

        public static float EaseInOutQuad(float t) => t < 0.5f ? 2f * t * t : -1f + (4f - 2f * t) * t;

        public static float EaseInCubic(float t) => t * t * t;

        public static float EaseOutCubic(float t)
        {
            float u = t - 1f;
            return u * u * u + 1f;
        }

        public static float EaseInOutCubic(float t) => t < 0.5f ? 4f * t * t * t : 1f + (t - 1f) * (t - 1f) * (t - 1f) * 4f;

        public static float EaseInBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            return c3 * t * t * t - c1 * t * t;
        }

        public static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        public static float EaseInOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c2 = c1 * 1.525f;
            float u = t - 1f;
            return t < 0.5f
                ? ((2f * t) * (2f * t) * ((c2 + 1f) * 2f * t - c2)) / 2f
                : ((2f * u) * (2f * u) * ((c2 + 1f) * 2f * u + c2) + 2f) / 2f;
        }
    }
}
