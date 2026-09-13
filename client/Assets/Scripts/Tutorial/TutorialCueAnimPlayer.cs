// BoardGameTutorial
// cue 内动画播放器：把 anim/{track}/{cue_id}.json 的 shot 时间轴演出来。
//
// 设计要点：
// 1. 时钟由外部（TutorialCuePlayer）喂入，通常是 audioSource.time。
//    动画因此天然对齐口播，暂停即冻结，重播即从 0 重新采样。
// 2. 只执行 8 个原语，全部通过 TutorialPrimitives / 本类的确定性采样完成，
//    不为任何单条动画新写协程逻辑分支。
// 3. 初始画面 = 数据里的 actors；ResetToStart() 直接重建 actors，
//    因此不维护运行端历史状态，将来可由编译器把起始画面写进同一份数据。
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BoardGameTutorial
{
    public class TutorialCueAnimPlayer : MonoBehaviour
    {
        private const float GemPpu = 100f;

        private static readonly Dictionary<string, Color> Palette = new Dictionary<string, Color>
        {
            { "gem_diamond",  new Color(0.85f, 0.90f, 0.93f) },
            { "gem_sapphire", new Color(0.26f, 0.52f, 0.96f) },
            { "gem_emerald",  new Color(0.20f, 0.66f, 0.33f) },
            { "gem_ruby",     new Color(0.82f, 0.22f, 0.26f) },
            { "gem_onyx",     new Color(0.22f, 0.23f, 0.27f) },
            { "gem_gold",     new Color(0.95f, 0.79f, 0.22f) },
            { "panel_supply", new Color(0.26f, 0.36f, 0.52f) },
            { "panel_player", new Color(0.24f, 0.42f, 0.31f) },
            { "shadow",       new Color(0f, 0f, 0f) },
            { "white",        Color.white },
        };

        // 程序化占位图形全 cue 共用，避免重复生成纹理。
        private static Sprite solidSprite;

        [Header("Cue animation")]
        [Tooltip("整条 cue 动画总开关；关掉后退回纯音频播放（便于 A/B 对比）。")]
        public bool animationEnabled = true;

        [Tooltip("动画整体速度倍率，只用于调试。")]
        public float timeScale = 1f;

        public string GameId { get; private set; }
        public string Track { get; private set; }
        public string CueId { get; private set; }
        public string Note { get; private set; }
        public bool IsLoaded => doc != null;
        public bool HasEvents => doc != null && doc.events != null && doc.events.Count > 0;
        public int AnimRootChildCount => animRoot != null ? animRoot.transform.childCount : 0;
        public float CameraGroundHalfWidth { get; private set; }
        public float CameraOrthoSize { get; private set; }

        private CueAnimDoc doc;
        private GameObject animRoot;
        private Camera animCamera;

        private readonly List<CueAnimActor> actors = new List<CueAnimActor>();
        private readonly Dictionary<string, CueAnimActor> actorsById = new Dictionary<string, CueAnimActor>();
        private readonly Dictionary<string, List<CueAnimActor>> actorsByGroup = new Dictionary<string, List<CueAnimActor>>();
        private readonly List<Coroutine> runningTweens = new List<Coroutine>();
        private readonly List<Coroutine> runningPulses = new List<Coroutine>();

        private float clock = -1f;
        private int nextIndex;

        // ── 数据加载 ──────────────────────────────────────────────────────

        /// <summary>
        /// 读取 anim/{track}/{cueId}.json。找不到文件返回 false（表示这条 cue 没有动画）。
        /// </summary>
        public bool LoadCue(string gameRoot, string track, string cueId)
        {
            GameId = null;
            Track = track;
            CueId = cueId;
            Note = null;
            doc = null;

            if (!animationEnabled || string.IsNullOrEmpty(gameRoot) || string.IsNullOrEmpty(cueId))
            {
                ClearScene();
                return false;
            }

            string path = Path.Combine(gameRoot, "tutorial", "anim", track, cueId + ".json");
            if (!File.Exists(path))
            {
                ClearScene();
                return false;
            }

            string json = File.ReadAllText(path);
            doc = JsonUtility.FromJson<CueAnimDoc>(json);
            if (doc == null || doc.scene == null || doc.events == null)
            {
                Debug.LogError($"[TutorialCueAnim] failed to parse {path}");
                doc = null;
                ClearScene();
                return false;
            }

            GameId = doc.game_id;
            Note = doc.note;
            BuildScene();   // 只搭一次景；Seek(0) 不会重建（clock 此时为 0，非 -1）。
            clock = 0f;
            nextIndex = 0;
            return true;
        }

        /// <summary>重建初始画面并把时间轴归零。</summary>
        public void ResetToStart()
        {
            StopAnimations();
            ClearActors();
            if (doc == null) return;

            BuildScene();
            clock = 0f;
            nextIndex = 0;
        }

        /// <summary>整条 cue 动画的总时长（最后一个 shot 的结束时间）。</summary>
        public float TotalDuration
        {
            get
            {
                if (doc == null || doc.events == null) return 0f;
                float end = 0f;
                foreach (var ev in doc.events)
                {
                    if (ev == null || ev.action == "wait") continue;
                    end = Mathf.Max(end, ev.at + ev.lead + Mathf.Max(0f, ev.dur));
                }
                return end;
            }
        }

        // ── 时间轴 ────────────────────────────────────────────────────────

        /// <summary>
        /// 外部时钟推进。time 通常是 audioSource.time（cue 内相对秒数）。
        /// 时间回退超过 0.25s 视为重播/拖动，重建画面后重新采样。
        /// </summary>
        public void Seek(float time)
        {
            if (doc == null) return;

            if (clock < 0f || time + 0.25f < clock)
            {
                ResetToStart();
            }

            clock = time;
            float scaled = time * Mathf.Max(0.01f, timeScale);

            // 触发所有到点的 shot。tween 协程自己按实时曲线推进。
            while (nextIndex < doc.events.Count && doc.events[nextIndex].at <= scaled + 1e-4f)
            {
                var ev = doc.events[nextIndex];
                nextIndex++;
                Trigger(ev);
            }
        }

        public void ClearScene()
        {
            StopAnimations();
            ClearActors();
            doc = null;
            clock = -1f;
            nextIndex = 0;
        }

        private void StopAnimations()
        {
            for (int i = 0; i < runningTweens.Count; i++)
                if (runningTweens[i] != null) StopCoroutine(runningTweens[i]);
            runningTweens.Clear();

            for (int i = 0; i < runningPulses.Count; i++)
                if (runningPulses[i] != null) StopCoroutine(runningPulses[i]);
            runningPulses.Clear();
        }

        private void ClearActors()
        {
            actors.Clear();
            actorsById.Clear();
            actorsByGroup.Clear();

            if (animRoot != null)
            {
                // 立即销毁：保证同一帧内重建不会出现新旧画面叠加。
                Object.DestroyImmediate(animRoot);
                animRoot = null;
            }
        }

        // ── 搭景 ──────────────────────────────────────────────────────────

        private void BuildScene()
        {
            var rootGo = new GameObject("CueAnimRoot");
            rootGo.transform.SetParent(transform, false);
            animRoot = rootGo;

            EnsureCamera();
            SetBackground();

            if (doc.scene == null || doc.scene.actors == null) return;

            foreach (var def in doc.scene.actors)
            {
                if (string.IsNullOrEmpty(def.id) || actorsById.ContainsKey(def.id)) continue;

                var actor = BuildActor(def);
                actors.Add(actor);
                actorsById[def.id] = actor;
                if (!string.IsNullOrEmpty(def.group))
                {
                    if (!actorsByGroup.TryGetValue(def.group, out var list))
                    {
                        list = new List<CueAnimActor>();
                        actorsByGroup[def.group] = list;
                    }
                    list.Add(actor);
                }
            }

            FitCamera();
        }

        private CueAnimActor BuildActor(CueAnimActorDef def)
        {
            var go = new GameObject("act:" + def.id);
            go.transform.SetParent(animRoot.transform, false);
            go.transform.localPosition = new Vector3(def.x, def.y, def.z);

            var sr = go.AddComponent<SpriteRenderer>();
            var sprite = ResolveSprite(def);
            sr.sprite = sprite;
            sr.color = ColorFor(def);
            sr.sortingOrder = def.sorting_order;

            Vector3 size = WorldSizeOf(def, sprite);
            if (def.highlight)
            {
                sr.enabled = false;
                go.transform.localScale = Vector3.zero;
            }
            else
            {
                float s = def.scale <= 0f ? 1f : def.scale;
                go.transform.localScale = new Vector3(size.x * s, size.y * s, 1f);
            }

            return new CueAnimActor(def, sprite, go, sr);
        }

        private static Sprite ResolveSprite(CueAnimActorDef def)
        {
            if (!string.IsNullOrEmpty(def.sprite))
            {
                var loaded = Resources.Load<Sprite>(def.sprite);
                if (loaded != null) return loaded;
                var tex = Resources.Load<Texture2D>(def.sprite);
                if (tex != null)
                {
                    return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), GemPpu);
                }
                Debug.LogWarning($"[TutorialCueAnim] sprite not found: {def.sprite}");
            }

            if (def.shape == "panel" || def.shape == "dot" || def.shape == "shadow")
            {
                return SharedSolidSprite();
            }

            Color c = Palette.TryGetValue(def.palette ?? "", out var p) ? p : Color.white;
            return GameSpriteFactory.Gem(c);
        }

        private static Color ColorFor(CueAnimActorDef def)
        {
            Color c = Palette.TryGetValue(def.palette ?? "", out var p) ? p : Color.white;
            c.a = Mathf.Clamp01(def.alpha);
            return c;
        }

        private static Sprite SharedSolidSprite()
        {
            if (solidSprite == null)
            {
                var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                var pixels = new Color[16];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
                tex.SetPixels(pixels);
                tex.Apply();
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                solidSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            }
            return solidSprite;
        }

        /// <summary>actor 的基础世界尺寸（不受运行时缩放影响）。</summary>
        private static Vector3 WorldSizeOf(CueAnimActorDef def, Sprite sprite)
        {
            float size = def.world_size <= 0f ? 0.26f : def.world_size;
            float aspect = 1f;
            if (sprite != null && sprite.rect.width > 0f) aspect = sprite.rect.height / sprite.rect.width;
            return new Vector3(size, size * aspect, 1f);
        }

        private Vector3 WorldSizeOf(CueAnimActor actor)
        {
            return WorldSizeOf(actor.Def, actor.Sprite);
        }

        private void SetBackground()
        {
            if (animCamera == null) return;
            Color bg = new Color(0.12f, 0.13f, 0.16f, 1f);
            if (doc.scene != null && !string.IsNullOrEmpty(doc.scene.background))
            {
                Color parsed;
                if (ColorUtility.TryParseHtmlString(doc.scene.background, out parsed)) bg = parsed;
            }
            animCamera.backgroundColor = bg;
        }

        private void EnsureCamera()
        {
            if (animCamera == null) animCamera = Camera.main;
            if (animCamera == null)
            {
                var camGo = new GameObject("TutorialCueCamera");
                camGo.tag = "MainCamera";
                animCamera = camGo.AddComponent<Camera>();
            }
            animCamera.orthographic = true;
            animCamera.clearFlags = CameraClearFlags.SolidColor;
        }

        /// <summary>按 actors 的包围盒取景，50° 固定俯角（与讲规模块一致）。</summary>
        private void FitCamera()
        {
            float pitch = doc.scene != null && doc.scene.camera_pitch > 0f ? doc.scene.camera_pitch : 50f;
            float orthoScale = doc.scene != null && doc.scene.ortho_scale > 0f ? doc.scene.ortho_scale : 1.25f;

            float minX = -1f, maxX = 1f, minZ = -1f, maxZ = 1f;
            bool any = false;
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a.Def.highlight) continue;
                Vector3 half = WorldSizeOf(a) * 0.5f * Mathf.Max(0.01f, a.Def.scale);
                float x0 = a.Def.x - half.x, x1 = a.Def.x + half.x;
                float z0 = a.Def.z - half.y, z1 = a.Def.z + half.y;
                if (!any)
                {
                    minX = x0; maxX = x1; minZ = z0; maxZ = z1; any = true;
                }
                else
                {
                    minX = Mathf.Min(minX, x0); maxX = Mathf.Max(maxX, x1);
                    minZ = Mathf.Min(minZ, z0); maxZ = Mathf.Max(maxZ, z1);
                }
            }

            if (!any)
            {
                minX = -1.5f; maxX = 1.5f; minZ = -1f; maxZ = 1.4f;
            }

            float cx = (minX + maxX) * 0.5f;
            float cz = (minZ + maxZ) * 0.5f;
            float halfW = Mathf.Max(0.5f, (maxX - minX) * 0.5f * orthoScale);
            float halfH = Mathf.Max(0.5f, (maxZ - minZ) * 0.5f * orthoScale);

            float pitchRad = pitch * Mathf.Deg2Rad;
            float sinP = Mathf.Max(0.15f, Mathf.Sin(pitchRad));
            float aspect = Mathf.Max(0.5f, (float)Screen.width / Mathf.Max(1, Screen.height));

            // 正交投影：可见世界横向半宽 = orthoSize*aspect；纵向折算到地面 ≈ orthoSize/sin(pitch)。
            float orthoSize = Mathf.Max(halfH * sinP, halfW / aspect);
            orthoSize = Mathf.Max(orthoSize, 0.4f);

            float distance = orthoSize * 3.2f;
            var focus = new Vector3(cx, 0f, cz);
            var eye = focus + new Vector3(0f, Mathf.Sin(pitchRad), -Mathf.Cos(pitchRad)) * distance;
            animCamera.transform.SetPositionAndRotation(eye, Quaternion.Euler(pitch, 0f, 0f));
            animCamera.orthographicSize = orthoSize;

            CameraOrthoSize = orthoSize;
            CameraGroundHalfWidth = orthoSize * aspect;
        }

        /// <summary>世界坐标 → 屏幕坐标（GUI 叠层标注用）。</summary>
        public bool WorldToScreen(Vector3 world, out Vector3 screen)
        {
            screen = Vector3.zero;
            if (animCamera == null) return false;
            screen = animCamera.WorldToScreenPoint(world);
            screen.y = Screen.height - screen.y;
            return screen.z > 0f;
        }

        // ── 原语执行 ──────────────────────────────────────────────────────

        private void Trigger(CueAnimEvent ev)
        {
            if (ev == null) return;

            switch (ev.action)
            {
                case "wait":
                    return;
                case "move":
                    TriggerMove(ev);
                    return;
                case "rotate":
                case "flip":
                    TriggerRotate(ev);
                    return;
                case "scale":
                    TriggerScale(ev);
                    return;
                case "fade":
                    TriggerFade(ev);
                    return;
                case "highlight":
                    TriggerHighlight(ev);
                    return;
                case "shuffle":
                    TriggerShuffle(ev);
                    return;
                default:
                    Debug.LogWarning($"[TutorialCueAnim] unknown action '{ev.action}' in cue {CueId}");
                    return;
            }
        }

        private void TriggerMove(CueAnimEvent ev)
        {
            foreach (var actor in Resolve(ev.target))
            {
                Vector3 from = actor.LivePosition;
                Vector3 to = from;
                if (ev.move != null)
                {
                    to += new Vector3(ev.move.dx, ev.move.dy, ev.move.dz);
                    if (ev.move.to_x.HasValue) to.x = ev.move.to_x.Value;
                    if (ev.move.to_z.HasValue) to.z = ev.move.to_z.Value;
                    if (!string.IsNullOrEmpty(ev.move.to_slot) &&
                        actorsById.TryGetValue(ev.move.to_slot, out var slotActor))
                    {
                        to.x = slotActor.Def.x;
                        to.z = slotActor.Def.z;
                    }
                }
                actor.LivePosition = to;
                RunTween(TweenPosition(actor, from, to, ev));
            }
        }

        private void TriggerRotate(CueAnimEvent ev)
        {
            foreach (var actor in Resolve(ev.target))
            {
                float to = ev.action == "flip" ? actor.LiveRotation + 180f : ev.angle;
                RunTween(TweenRotation(actor, actor.LiveRotation, to, ev));
                actor.LiveRotation = to;
            }
        }

        private void TriggerScale(CueAnimEvent ev)
        {
            foreach (var actor in Resolve(ev.target))
            {
                float factor = ev.scale <= 0f ? 1.25f : ev.scale;
                Vector3 baseSize = WorldSizeOf(actor);
                Vector3 from = actor.LiveScale;
                Vector3 to = ev.scale_mode == "to" ? baseSize * factor : from * factor;
                actor.LiveScale = to;
                RunTween(TweenScale(actor, from, to, ev));
            }
        }

        private void TriggerFade(CueAnimEvent ev)
        {
            foreach (var actor in Resolve(ev.target))
            {
                float to = ev.to_alpha.HasValue
                    ? Mathf.Clamp01(ev.to_alpha.Value)
                    : (actor.LiveAlpha > 0.5f ? 0f : 1f);
                float from = actor.LiveAlpha;
                actor.LiveAlpha = to;
                RunTween(TweenAlpha(actor, from, to, ev));
                if (ev.hide_at_end && to <= 0.01f && actor.Go != null) actor.Go.SetActive(false);
            }
        }

        private void TriggerHighlight(CueAnimEvent ev)
        {
            foreach (var actor in Resolve(ev.target))
            {
                if (actor.Renderer == null) continue;

                float peak = ev.peak_alpha.HasValue ? Mathf.Clamp01(ev.peak_alpha.Value) : 0.6f;
                float grow = ev.grow.HasValue && ev.grow.Value > 0f ? ev.grow.Value : 1f;
                float dur = Mathf.Max(ev.dur, 0.05f);

                Color from = actor.Renderer.color;
                from.a = 0f;
                Color to = new Color(from.r, from.g, from.b, peak);

                Vector3 baseSize = WorldSizeOf(actor);
                Vector3 fromScale = Vector3.zero;
                Vector3 toScale = baseSize * grow;

                actor.Renderer.enabled = true;
                actor.Renderer.color = from;
                actor.Go.transform.localScale = fromScale;

                var routine = StartCoroutine(PulseRoutine(actor, from, to, fromScale, toScale, dur, ev));
                runningPulses.Add(routine);
            }
        }

        private IEnumerator PulseRoutine(CueAnimActor actor, Color from, Color to,
            Vector3 fromScale, Vector3 toScale, float dur, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);

            float t = 0f;
            while (t < dur)
            {
                t = Mathf.Min(t + Time.unscaledDeltaTime, dur);
                float k = Easing.Evaluate(EasingOr(ev), t / dur);
                actor.Renderer.color = Color.LerpUnclamped(from, to, k);
                actor.Go.transform.localScale = Vector3.LerpUnclamped(fromScale, toScale, k);
                yield return null;
            }

            // 高亮是叠加装饰：结束时收起，不残留。
            actor.Renderer.color = to;
            actor.Go.transform.localScale = toScale;
            actor.Renderer.enabled = false;
            actor.Go.transform.localScale = Vector3.zero;
        }

        private void TriggerShuffle(CueAnimEvent ev)
        {
            var list = Resolve(ev.target);
            if (list.Count == 0) return;

            var targets = new Transform[list.Count];
            var positions = new Vector3[list.Count];
            const float spread = 0.16f;
            float center = (list.Count - 1) * 0.5f;
            for (int i = 0; i < list.Count; i++)
            {
                targets[i] = list[i].Go.transform;
                Vector3 from = list[i].LivePosition;
                positions[i] = from + new Vector3((i - center) * spread, 0f, (i % 2 == 0 ? 1f : -1f) * 0.05f);
                list[i].LivePosition = positions[i];
            }

            RunTween(TutorialPrimitives.TweenShuffle(targets, positions, Mathf.Max(ev.dur, 0.1f), EasingOr(ev)));
        }

        // ── 原语协程：只做「读参 → 调原语」，没有单条动画的专用逻辑 ──────

        private IEnumerator TweenPosition(CueAnimActor actor, Vector3 from, Vector3 to, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);
            yield return TutorialPrimitives.TweenPosition(actor.Go.transform, from, to, ev.dur, EasingOr(ev));
            actor.LivePosition = to;
        }

        private IEnumerator TweenRotation(CueAnimActor actor, float from, float to, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);

            float dur = Mathf.Max(ev.dur, 0f);
            if (dur <= 0f)
            {
                actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, to);
            }
            else
            {
                float t = 0f;
                while (t < dur)
                {
                    t = Mathf.Min(t + Time.deltaTime, dur);
                    float k = Easing.Evaluate(EasingOr(ev), t / dur);
                    actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.LerpUnclamped(from, to, k));
                    yield return null;
                }
                actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, to);
            }
            actor.LiveRotation = to;
        }

        private IEnumerator TweenScale(CueAnimActor actor, Vector3 from, Vector3 to, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);
            actor.Go.transform.localScale = from;
            yield return TutorialPrimitives.TweenScale(actor.Go.transform, from, to, ev.dur, EasingOr(ev));
            actor.LiveScale = to;
        }

        private IEnumerator TweenAlpha(CueAnimActor actor, float from, float to, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);
            if (actor.Renderer != null)
            {
                actor.Renderer.color = new Color(actor.Renderer.color.r, actor.Renderer.color.g, actor.Renderer.color.b, from);
                yield return TutorialPrimitives.TweenAlpha(actor.Renderer, from, to, ev.dur, EasingOr(ev));
            }
            actor.LiveAlpha = to;
        }

        private static IEnumerator WaitScaled(float seconds)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += Time.deltaTime;
                yield return null;
            }
        }

        private static string EasingOr(CueAnimEvent ev)
        {
            return string.IsNullOrEmpty(ev.easing) ? Easing.Default : ev.easing;
        }

        private void RunTween(IEnumerator routine)
        {
            if (routine == null) return;
            runningTweens.Add(StartCoroutine(routine));
        }

        private List<CueAnimActor> Resolve(string target)
        {
            var result = new List<CueAnimActor>();
            if (string.IsNullOrEmpty(target))
            {
                result.AddRange(actors);
                return result;
            }

            if (actorsById.TryGetValue(target, out var single))
            {
                result.Add(single);
                return result;
            }

            if (actorsByGroup.TryGetValue(target, out var group)) result.AddRange(group);
            return result;
        }
    }
}
