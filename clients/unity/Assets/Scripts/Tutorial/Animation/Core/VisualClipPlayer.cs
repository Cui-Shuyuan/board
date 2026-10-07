// Shared presentation-primitive application for animated visual objects.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    /// <summary>
    /// Applies the presentation primitives that are meaningful for every
    /// object implementation of <see cref="IAnimVisualObject"/>.
    /// </summary>
    public static class VisualClipPlayer
    {
        public static void Apply(IAnimVisualObject target, CompiledClipDef clip, float t)
        {
            if (target == null || clip == null) return;
            float start = clip.at + Math.Max(0f, clip.lead);
            float end = start + Math.Max(0f, clip.dur);
            if (t + 1e-6f < start) return;
            float k = end > start ? Clamp01((t - start) / (end - start)) : 1f;
            float eased = Easing.Evaluate(clip.easing, k);
            switch (clip.kind)
            {
                case "scale":
                    target.Scale = Lerp(1f, clip.to_scale, eased);
                    break;
                case "fade":
                    target.Alpha = Lerp(1f, clip.to_alpha, eased);
                    break;
                case "highlight":
                    target.Highlighted = t < end || end <= start;
                    if (target.Highlighted)
                    {
                        float half = k < 0.5f ? k * 2f : (1f - k) * 2f;
                        target.HighlightGrow = Lerp(1f, Math.Max(1f, clip.to_scale), half);
                    }
                    break;
                case "point":
                    target.PointPart = clip.part;
                    target.Indicator = clip.indicator;
                    break;
            }
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }
    }
}
