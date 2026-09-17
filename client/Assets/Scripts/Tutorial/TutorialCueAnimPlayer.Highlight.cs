// BoardGameTutorial
// 高亮：介绍到谁，谁就高亮。
//
// 选择器规则与全项目统一（2026-09 修订）：
//   target    = 一件（原地呼吸）
//   container = 任意一组（整组缩放，锚点用组重心或容器自带中心）
//   zone      = 该区域全部（整组缩放）
//   同时写多个 → 报警告，按 target > container > zone 取优先级最高的。
//
// 历史：早期高亮只能打单件，牌堆有几十张叠着，点一张只能让其中一张变大；
// 后来改为按 zone 整组；再后来 zone 之外还需要「点名的一组」（例如卡 + 压在上面的宝石），
// 于是有了 container。三者是同一套选择器的不同粒度，不是三套机制。
//
// 视觉手法：
//   Pulse / GroupScale = 放大再回落（呼吸，不改变最终状态）
//   Glow               = 底板透明度升到 peak 再回落（按需开启，默认不叠）
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BoardGameTutorial
{
    public partial class TutorialCueAnimPlayer
    {
        private void TriggerHighlight(CueAnimEvent ev)
        {
            if (string.IsNullOrEmpty(ev.target) && string.IsNullOrEmpty(ev.zone)
                && string.IsNullOrEmpty(ev.container))
            {
                Debug.LogWarning($"[TutorialCueAnim] highlight 未指定 target/container/zone（cue {CueId}），已忽略");
                return;
            }

            float peak = ev.peak_alpha >= 0f ? Mathf.Clamp01(ev.peak_alpha) : 0.55f;
            float grow = ev.grow > 0f ? ev.grow : 1.14f;
            float dur = Mathf.Max(ev.dur, 0.05f);
            float lead = Mathf.Max(0f, ev.lead);
            string easing = EasingOr(ev);

            if (!string.IsNullOrEmpty(ev.target))
            {
                var actor = FindActor(ev.target);
                if (actor == null)
                {
                    Debug.LogWarning($"[TutorialCueAnim] highlight target '{ev.target}' 不存在（cue {CueId}）");
                    return;
                }
                PulseActor(actor, grow, dur, lead, easing);
                return;
            }

            // 容器：任意一组，整组缩放
            if (PickSelector(ev) == Selector.Container)
            {
                var items = ResolveContainer(ev.container);
                var actors = new List<CueAnimActor>();
                foreach (var it in items) if (it?.Actor != null) actors.Add(it.Actor);
                GroupScaleActors(actors, ContainerCenter(ev.container, items), grow, dur, lead, easing);
                return;
            }

            var glowZone = Store.GetZone(ev.zone);
            if (glowZone == null)
            {
                Debug.LogWarning($"[TutorialCueAnim] highlight zone '{ev.zone}' 不存在（cue {CueId}）");
                return;
            }

            // 区域高亮 = **整组**一起放大再回落。这是「选中这一堆」的表达。
            // 不要对组内单张做缩放：牌堆有几十张叠着，单张缩放只会让其中一张变大
            // （用户看到的「只有堆底那张大了一圈」）。
            //
            // 不再默认叠一层区域光晕：那是一块覆盖整个 zone 的光斑，会盖住牌本身、
            // 也让「变大」这件事看不清。需要时由 cue 数据显式开启（peak_alpha）。
            GroupScaleZone(ev.zone, grow, dur, lead, easing);
            if (ev.peak_alpha > 0f)
            {
                var glow = GlowFor(ev.zone);
                if (glow != null) PulseGlow(glow, ev.zone, peak, dur, lead, easing);
            }
        }

        /// <summary>原地强调一个组件：缩放到 grow 再回到基准，不改变它的最终状态。</summary>
        /// <summary>
        /// 单件高亮 = **原地**放大再回落（呼吸），锚点取它自己 → 位置不动。
        ///
        /// 必须走**时间采样**（clip），不能用协程：
        /// 协程靠 `Time.unscaledDeltaTime` 自己往前跑，与 `Seek` 的时钟无关 ——
        /// 于是**暂停时它照样放完**（用户报的问题）；而洗混用 `HasShuffle` 片段，
        /// 采样是 `Seek(t)` 的纯函数，暂停就停住。
        ///
        /// 实现上直接复用 `HasGroupScale`（它本来就是"以某点为锚点整组缩放、去程+回程"），
        /// 把锚点设为这张牌自己的位置，等效于原地呼吸 —— 不必再造一种脉冲机制。
        /// </summary>
        private void PulseActor(CueAnimActor actor, float grow, float duration, float lead, string easing)
        {
            if (actor?.Go == null || actor.Renderer == null) return;
            if (actor.Item != null && actor.Item.Template != null && IsDecoration(actor.Item.Template)) return;

            var synthetic = new CueAnimEvent { at = currentEventAt, dur = duration, lead = lead, easing = easing };
            var clip = ClipAt(actor.Item, actor, synthetic);
            clip.HasGroupScale = true;
            clip.GroupCenter = actor.LivePosition;   // 锚点 = 它自己 → 原地放大
            clip.GroupGrow = grow;
        }

        private IEnumerator ScalePulseRoutine(CueAnimActor actor, Vector3 baseScale, float grow, float duration, float lead, string easing)
        {
            if (lead > 0f) yield return WaitScaled(lead);

            float t = 0f;
            while (t < duration)
            {
                t = Mathf.Min(t + Time.unscaledDeltaTime, duration);
                // 0 → 1 → 0：先放大再回到原位，峰值在中点。
                float envelope = Mathf.Sin(Easing.Evaluate(easing, t / duration) * Mathf.PI);
                actor.Go.transform.localScale = baseScale * (1f + (grow - 1f) * envelope);
                yield return null;
            }

            actor.Go.transform.localScale = baseScale;
            actor.LiveScale = baseScale;
        }

        /// <summary>强调一整片区域：只对该 zone 专属的 glow 底板做透明度呼吸。</summary>
        private void PulseGlow(CueAnimActor glow, string zoneId, float peak, float duration, float lead, string easing)
        {
            if (glow?.Renderer == null || glow.Go == null) return;
            RunTween(GlowPulseRoutine(glow, peak, duration, lead, easing));
        }

        private IEnumerator GlowPulseRoutine(CueAnimActor glow, float peak, float duration, float lead, string easing)
        {
            if (lead > 0f) yield return WaitScaled(lead);

            var baseColor = glow.BaseColor;
            float baseAlpha = glow.BaseAlpha;
            float baseScale = glow.Go.transform.localScale.x;

            glow.Renderer.enabled = true;
            glow.Go.transform.localScale = new Vector3(baseScale * 1.06f, baseScale * 1.06f, 1f);

            float t = 0f;
            while (t < duration)
            {
                t = Mathf.Min(t + Time.unscaledDeltaTime, duration);
                float envelope = Mathf.Sin(Easing.Evaluate(easing, t / duration) * Mathf.PI);
                var c = baseColor;
                c.a = Mathf.Lerp(baseAlpha, peak, envelope);
                glow.Renderer.color = c;
                yield return null;
            }

            var restored = baseColor;
            restored.a = baseAlpha;
            glow.Renderer.color = restored;
            glow.Go.transform.localScale = new Vector3(baseScale, baseScale, 1f);
            glow.Renderer.enabled = baseAlpha > 0.01f;
        }

        /// <summary>
        /// 取该 zone 专属的高亮底板：优先 stage 里显式声明的 highlight 模板锚点；
        /// 没声明就现场建一块只属于这个 zone 的（覆盖该 zone 的范围），
        /// **绝不**复用到别处，避免一个事件点亮一片。
        /// </summary>
        private CueAnimActor GlowFor(string zoneId)
        {
            if (zoneGlow.TryGetValue(zoneId, out var existing)) return existing;

            var zone = Store.GetZone(zoneId);
            if (zone == null) return null;

            var tpl = Store.GetTemplate("glow_zone");
            if (tpl == null)
            {
                Debug.LogWarning("[TutorialCueAnim] stage 缺少 glow_zone 模板，无法高亮区域");
                return null;
            }

            // 尺寸按该 zone 的实际跨度算，保证高亮只覆盖这一片。
            // row 是单行：长度只由 capacity × x_step 决定（cols 对 row 无效）。
            float width, height;
            var layout = zone.layout ?? new StageLayout();
            if (layout.type == "row")
            {
                width = Mathf.Max(0.30f, (Mathf.Max(1, zone.capacity) - 1) * layout.x_step + 0.40f);
                height = 0.40f;
            }
            else
            {
                int cols = Mathf.Max(1, layout.cols);
                int rows = Mathf.CeilToInt(Mathf.Max(1, zone.capacity) / (float)cols);
                width = Mathf.Max(0.30f, (cols - 1) * layout.x_step + 0.40f);
                height = Mathf.Max(0.30f, (rows - 1) * layout.z_step + 0.40f);
            }

            var local = new StageTemplate
            {
                id = "glow_zone",
                shape = tpl.shape,
                palette = string.IsNullOrEmpty(zone.palette) ? tpl.palette : zone.palette,
                width = width,
                height = height,
                alpha = 0.0f,
                sorting_order = -19,
                highlight = true,
            };

            var go = CreateSpriteObject("glow:" + zoneId, local, Palette.Resolve(local.palette));
            go.transform.localPosition = new Vector3(zone.center.x, 0.004f, zone.center.z);

            var sr = go.GetComponent<SpriteRenderer>();
            var item = new ZoneItem
            {
                Id = "glow:" + zoneId,
                Template = local,
                BaseColor = Palette.Resolve(local.palette),
                ZoneId = zoneId,
                LiveAlpha = 0f,
                LivePosition = go.transform.localPosition,
                LiveScale = go.transform.localScale,
            };
            var actor = new CueAnimActor(item, sr.sprite, go, sr, go.transform.localScale);
            actor.Renderer.enabled = false;
            zoneGlow[zoneId] = actor;
            return actor;
        }
    }
}
