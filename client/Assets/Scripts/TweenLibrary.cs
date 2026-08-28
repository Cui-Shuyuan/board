using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 通用补间系统：把 teaching.json 里的 shot 指令解析为可等待的协程。
/// 纯程序确定性，不依赖任何资产/第三方库。
/// 支持的指令 type：move / rotate / scale / flip / appear / group / tell
/// </summary>
public static class TweenLibrary
{
    static readonly Dictionary<string, Func<float, float>> Easing = new Dictionary<string, Func<float, float>>()
    {
        { "linear", t => t },
        { "easeInQuad", t => t * t },
        { "easeOutQuad", t => t * (2f - t) },
        { "easeInOutQuad", t => t < 0.5f ? 2f * t * t : -1f + (4f - 2f * t) * t },
        { "easeInCubic", t => t * t * t },
        { "easeOutCubic", t => 1f - Mathf.Pow(1f - t, 3f) },
        { "easeInOutCubic", t => t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f },
        { "easeInSine", t => 1f - Mathf.Cos(t * Mathf.PI * 0.5f) },
        { "easeOutSine", t => Mathf.Sin(t * Mathf.PI * 0.5f) },
        { "easeInOutSine", t => -(Mathf.Cos(Mathf.PI * t) - 1f) / 2f },
    };

    public static float Ease(float t, string name)
    {
        if (Easing.TryGetValue(name, out var f)) return f(Mathf.Clamp01(t));
        return t; // 未知缓动默认 linear
    }

    /// <summary>执行一个 shot（顺序 or 并行组）。返回一个等待其完成的协程。</summary>
    public static IEnumerator Run(Transform root, TeachingShot shot)
    {
        Factory f = new Factory(root);
        yield return f.Build(shot);
    }
}

/// <summary>从每个 shot 构建协程的小型工厂。</summary>
internal class Factory
{
    readonly Transform root;

    public Factory(Transform root) { this.root = root; }

    public IEnumerator Build(TeachingShot shot)
    {
        if (shot.delay > 0) yield return new WaitForSeconds(shot.delay);

        switch (shot.type)
        {
            case "group":
                // 并行组：所有子 shot 同时启动，等待最长完成
                var runners = new List<IEnumerator>();
                foreach (var sub in shot.items)
                {
                    var it = new Factory(root).Build(sub);
                    runners.Add(it);
                }
                // 无法真正并行时用轮询等待全部完成（简化：顺序执行 each 到完成即整组完成）
                // 说明：为保持确定性且避免协程并发复杂度，这里对 group 内子 shot 采用"整体运行直到所有完成"。
                foreach (var it in runners) yield return it;
                break;

            case "tell":
                yield return new WaitForSeconds(shot.duration);
                break;

            case "move":
                yield return Move(shot);
                break;
            case "rotate":
                yield return Rotate(shot);
                break;
            case "scale":
                yield return Scale(shot);
                break;
            case "flip":
                yield return Flip(shot);
                break;
            case "appear":
                yield return Appear(shot);
                break;
            default:
                Debug.LogWarning("[Tween] 未知指令: " + shot.type);
                yield break;
        }

        if (shot.hold > 0) yield return new WaitForSeconds(shot.hold);
    }

    Transform Resolve(string target)
    {
        var t = root.Find(target);
        if (t == null) t = root.Find(target.TrimStart('/'));
        return t;
    }

    IEnumerator Move(TeachingShot shot)
    {
        var t = Resolve(shot.target);
        if (t == null) { yield break; }
        Vector3 from = shot.From.HasValue ? shot.From.Value : t.localPosition;
        Vector3 to = shot.To.HasValue ? shot.To.Value : t.localPosition;
        float dur = Mathf.Max(0.001f, shot.duration);
        float start = Time.time;
        while (Time.time - start < dur)
        {
            float k = TweenLibrary.Ease((Time.time - start) / dur, shot.easing);
            t.localPosition = Vector3.LerpUnclamped(from, to, k);
            yield return null;
        }
        t.localPosition = to;
    }

    IEnumerator Rotate(TeachingShot shot)
    {
        var t = Resolve(shot.target);
        if (t == null) { yield break; }
        Quaternion from = t.localRotation;
        Quaternion to = shot.Angle.HasValue ? Quaternion.Euler(shot.Angle.Value) : from;
        float dur = Mathf.Max(0.001f, shot.duration);
        float start = Time.time;
        while (Time.time - start < dur)
        {
            float k = TweenLibrary.Ease((Time.time - start) / dur, shot.easing);
            t.localRotation = Quaternion.SlerpUnclamped(from, to, k);
            yield return null;
        }
        t.localRotation = to;
    }

    IEnumerator Scale(TeachingShot shot)
    {
        var t = Resolve(shot.target);
        if (t == null) { yield break; }
        Vector3 from = t.localScale;
        Vector3 to = shot.To.HasValue ? shot.To.Value : from;
        float dur = Mathf.Max(0.001f, shot.duration);
        float start = Time.time;
        while (Time.time - start < dur)
        {
            float k = TweenLibrary.Ease((Time.time - start) / dur, shot.easing);
            t.localScale = Vector3.LerpUnclamped(from, to, k);
            yield return null;
        }
        t.localScale = to;
    }

    IEnumerator Flip(TeachingShot shot)
    {
        var t = Resolve(shot.target);
        if (t == null) { yield break; }
        Quaternion from = t.localRotation;
        Quaternion to = shot.Angle.HasValue ? Quaternion.Euler(shot.Angle.Value) : Quaternion.Euler(0, 180, 0);
        float dur = Mathf.Max(0.001f, shot.duration);
        float start = Time.time;
        while (Time.time - start < dur)
        {
            float k = TweenLibrary.Ease((Time.time - start) / dur, shot.easing);
            t.localRotation = Quaternion.SlerpUnclamped(from, to, k);
            yield return null;
        }
        t.localRotation = to;
    }

    IEnumerator Appear(TeachingShot shot)
    {
        var t = Resolve(shot.target);
        if (t == null) { yield break; }
        var sr = t.GetComponent<SpriteRenderer>();
        if (sr == null) { yield break; }
        float dur = Mathf.Max(0.001f, shot.duration);
        float start = Time.time;
        Color from = new Color(1, 1, 1, 0);
        float forFrom = 0f, to = 1f;
        var baseScale = t.localScale;
        while (Time.time - start < dur)
        {
            float k = TweenLibrary.Ease((Time.time - start) / dur, shot.easing);
            float a = Mathf.LerpUnclamped(forFrom, to, k);
            sr.color = new Color(sr.color.r, sr.color.g, sr.color.b, a);
            t.localScale = baseScale * Mathf.LerpUnclamped(0.6f, 1f, k);
            yield return null;
        }
        sr.color = new Color(sr.color.r, sr.color.g, sr.color.b, 1);
        t.localScale = baseScale;
    }
}
