// BoardGameTutorial
// cue 内动画播放器（状态版）。
//
// 模型：动画 = 维护一组组件的状态。组件状态 = 它在哪个 zone、以什么姿态；
// 位置由 zone 的布局规则推导。因此本播放器做两件事：
//   1. 渲染层：把 ZoneStore 的每个组件实例成 sprite，位置由 (zone, slot) 算出；
//   2. 时间轴层：按相对秒数触发 cue 数据里的原语，move 的语义就是 zone → zone。
//
// 时钟由 TutorialCuePlayer 喂入（audioSource.time）：动画天然对齐口播。
// 重播当前 cue 恢复到这条 cue 的入口状态；顺序播放接着上一条的终态继续。
// 将来编译器可以离线复算每个 cue 的入口状态写进 runtime，用于任意跳转。
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BoardGameTutorial
{
    public class TutorialCueAnimPlayer : MonoBehaviour
    {
        private const float GemPpu = 100f;

        private static Sprite solidSprite;

        [Header("Cue animation")]
        [Tooltip("整条 cue 动画总开关；关掉后退回纯音频（便于 A/B 对比）。")]
        public bool animationEnabled = true;

        [Tooltip("动画整体速度倍率，只用于调试。")]
        public float timeScale = 1f;

        public string CueId { get; private set; }
        public string Note { get; private set; }
        public bool IsLoaded => cueDoc != null;
        public bool HasEvents => cueDoc != null && cueDoc.events != null && cueDoc.events.Count > 0;
        public float CameraGroundHalfWidth { get; private set; }
        public float CameraOrthoSize { get; private set; }
        public ZoneStore Store { get; private set; } = new ZoneStore();

        private StageDoc stage;
        private CueAnimDoc cueDoc;
        private string gameRootPath;
        private GameObject animRoot;
        private Camera animCamera;

        private readonly Dictionary<string, CueAnimActor> actors = new Dictionary<string, CueAnimActor>();
        /// <summary>区域底板：zone id → 代表它的装饰件 id。zone 级 highlight 打到这里。</summary>
        private readonly Dictionary<string, List<string>> zonePanels = new Dictionary<string, List<string>>();

        private struct PendingScale
        {
            public float At;
            public CueAnimActor Actor;
            public float Factor;
            public CueAnimEvent Event;
        }

        /// <summary>错峰触发的缩放（scale + stagger）。</summary>
        private readonly List<PendingScale> pendingScales = new List<PendingScale>();
        private readonly List<Coroutine> running = new List<Coroutine>();

        private float clock = -1f;
        private int nextIndex;
        private ZoneSnapshot entrySnapshot;

        // ── 加载 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 载入牌桌与这一条 cue，并把画面摆到「本条 cue 开始播放时」的状态。
        /// continueState=true 接着上一条的终态；false 从牌桌 initial + 本条 start 起。
        /// </summary>
        public bool LoadCue(string gameRoot, string track, string cueId, bool continueState)
        {
            CueId = cueId;
            Note = null;
            cueDoc = null;
            StopAnimations();
            ClearActors();

            if (!animationEnabled || string.IsNullOrEmpty(gameRoot) || string.IsNullOrEmpty(cueId))
            {
                ClearScene();
                return false;
            }

            string path = Path.Combine(gameRoot, "tutorial", "anim", track, cueId + ".json");
            if (!File.Exists(path))
            {
                // 这条 cue 还没做动画：保留上一张牌桌画面，不要清空，
                // 否则播到没做动画的 cue 时整张桌子会突然消失。
                CueId = null;
                clock = -1f;
                nextIndex = 0;
                return false;
            }

            cueDoc = JsonUtility.FromJson<CueAnimDoc>(File.ReadAllText(path));
            if (cueDoc == null || cueDoc.events == null)
            {
                Debug.LogError($"[TutorialCueAnim] failed to parse {path}");
                cueDoc = null;
                return false;
            }
            Note = cueDoc.note;

            LoadStage(gameRoot, cueDoc.stage);

            if (!continueState) Store.ApplyInitial();
            ApplyCueStart();

            BuildActorObjects();
            SyncActorsToStore();

            EnsureCamera();
            SetBackground();
            FitCamera();

            CaptureEntry();
            clock = 0f;
            nextIndex = 0;
            return true;
        }

        private void LoadStage(string gameRoot, string stageRel)
        {
            string rel = string.IsNullOrEmpty(stageRel) ? "_stage/splendor.table" : stageRel;
            string path = Path.Combine(gameRoot, "tutorial", "anim", rel + ".json");
            if (!File.Exists(path))
            {
                Debug.LogError($"[TutorialCueAnim] missing stage file: {path}");
                stage = null;
                Store.LoadStage(null);
                return;
            }

            stage = JsonUtility.FromJson<StageDoc>(File.ReadAllText(path));
            Store.LoadStage(stage);
            gameRootPath = gameRoot;
        }

        /// <summary>本条 cue 播放前对状态做的准备（清空 / 预置 / 临时组件）。</summary>
        private void ApplyCueStart()
        {
            var start = cueDoc.start;
            if (start == null) return;

            if (start.clear) Store.Reset();

            if (start.set != null)
            {
                foreach (var seed in start.set)
                {
                    int count = Mathf.Max(1, seed.count);
                    if (seed.expand_to.HasValue)
                    {
                        int have = Store.CountIn(seed.zone, seed.palette, seed.template);
                        count = Mathf.Max(0, seed.expand_to.Value - have);
                    }
                    if (count > 0) Store.Spawn(seed.template, seed.palette, seed.zone, count);
                }
            }

        }

        // ── 渲染层 ────────────────────────────────────────────────────────

        private void BuildActorObjects()
        {
            var rootGo = new GameObject("CueAnimRoot");
            rootGo.transform.SetParent(transform, false);
            animRoot = rootGo;

            if (stage?.anchors != null)
            {
                foreach (var anchor in stage.anchors)
                {
                    if (string.IsNullOrEmpty(anchor.id)) continue;
                    var tpl = Store.GetTemplate(anchor.template);
                    if (tpl == null)
                    {
                        Debug.LogWarning($"[TutorialCueAnim] anchor '{anchor.id}' uses unknown template '{anchor.template}'");
                        continue;
                    }

                    var color = Palette.TintFor(tpl.shape, tpl.palette);
                    var go = CreateSpriteObject("anchor:" + anchor.id, tpl, color);
                    go.transform.localPosition = new Vector3(anchor.x, anchor.y, anchor.z);

                    var sr = go.GetComponent<SpriteRenderer>();
                    var item = new ZoneItem
                    {
                        Id = anchor.id,
                        Template = tpl,
                        BaseColor = color,
                        ZoneId = null,
                        LiveAlpha = tpl.alpha,
                        LivePosition = new Vector3(anchor.x, anchor.y, anchor.z),
                        LiveScale = go.transform.localScale,
                        LiveRotation = tpl.rotation,
                    };
                    actors[anchor.id] = new CueAnimActor(item, sr.sprite, go, sr, go.transform.localScale);
                    if (anchor.zones != null)
                    {
                        foreach (var zoneId in anchor.zones)
                        {
                            if (string.IsNullOrEmpty(zoneId)) continue;
                            if (!zonePanels.TryGetValue(zoneId, out var list))
                            {
                                list = new List<string>();
                                zonePanels[zoneId] = list;
                            }
                            list.Add(anchor.id);
                        }
                    }
                }
            }

            foreach (var item in Store.Items)
            {
                if (actors.ContainsKey(item.Id)) continue;
                var go = CreateSpriteObject("item:" + item.Id, item.Template, item.BaseColor);
                var sr = go.GetComponent<SpriteRenderer>();
                var actor = new CueAnimActor(item, sr.sprite, go, sr, go.transform.localScale);
                item.Actor = actor;
                actors[item.Id] = actor;
            }
        }

        private GameObject CreateSpriteObject(string name, StageTemplate tpl, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(animRoot.transform, false);

            var sr = go.AddComponent<SpriteRenderer>();
            var sprite = ResolveSprite(tpl);
            sr.sprite = sprite;
            sr.sortingOrder = tpl.sorting_order;
            color.a = Mathf.Clamp01(tpl.alpha);
            sr.color = color;

            var size = WorldSizeOf(tpl, sprite);
            if (tpl.highlight)
            {
                sr.enabled = false;
                go.transform.localScale = Vector3.zero;
            }
            else
            {
                go.transform.localScale = new Vector3(size.x, size.y, 1f);
            }
            return go;
        }

        private Sprite ResolveSprite(StageTemplate tpl)
        {
            var image = ResolveImagePath(tpl);
            if (image != null)
            {
                var loaded = CardImageLoader.Load(image);
                if (loaded != null) return loaded;
                Debug.LogWarning($"[TutorialCueAnim] card image failed: {image}");
            }

            if (!string.IsNullOrEmpty(tpl.sprite))
            {
                var loaded = Resources.Load<Sprite>(tpl.sprite);
                if (loaded != null) return loaded;
                var tex = Resources.Load<Texture2D>(tpl.sprite);
                if (tex != null)
                    return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), GemPpu);
                Debug.LogWarning($"[TutorialCueAnim] sprite not found: {tpl.sprite}");
            }

            if (tpl.shape == "panel" || tpl.shape == "dot" || tpl.shape == "shadow")
                return SharedSolidSprite();

            var color = Palette.Resolve(tpl.palette);
            if (tpl.shape == "card")
            {
                // 贵族面向下用 noble 色板的方块；发展卡用等级色卡背。
                return tpl.palette == "noble" ? GameSpriteFactory.NobleTile(color) : GameSpriteFactory.CardBack(color);
            }

            return GameSpriteFactory.Gem(color);
        }

        /// <summary>
        /// 找到模板对应的扫描图。优先 face_image；否则按 card/level 约定查找
        /// media/card/{level}_back.{jpg,png} 或 media/card/{level}_{color}.{jpg,png}。
        /// </summary>
        private string ResolveImagePath(StageTemplate tpl)
        {
            if (string.IsNullOrEmpty(gameRootPath)) return null;

            foreach (var relative in ImageCandidates(tpl))
            {
                var candidate = Path.Combine(gameRootPath, relative);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>按顺序尝试的扫描图路径：face_image 优先，其次 media/card/ 的命名约定。</summary>
        private static IEnumerable<string> ImageCandidates(StageTemplate tpl)
        {
            if (!string.IsNullOrEmpty(tpl.face_image)) yield return tpl.face_image;

            if (tpl.shape != "card" || string.IsNullOrEmpty(tpl.palette)) yield break;
            if (!tpl.palette.StartsWith("card_level_")) yield break;

            string level = "level_" + tpl.palette.Substring("card_level_".Length);
            yield return "media/card/" + level + "_back.jpg";
            yield return "media/card/" + level + "_back.png";
        }

        private static Vector3 WorldSizeOf(StageTemplate tpl, Sprite sprite)
        {
            if (tpl.width > 0f || tpl.height > 0f)
            {
                float w = tpl.width > 0f ? tpl.width : (tpl.height > 0f ? tpl.height : 0.5f);
                float h = tpl.height > 0f ? tpl.height : w;
                return new Vector3(w, h, 1f);
            }

            float size = tpl.world_size <= 0f ? 0.10f : tpl.world_size;
            float aspect = 1f;
            if (sprite != null && sprite.rect.width > 0f) aspect = sprite.rect.height / sprite.rect.width;
            return new Vector3(size, size * aspect, 1f);
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

        /// <summary>把每个组件瞬间摆到它当前 (zone, slot) 的位置 —— 这是状态的可视化。</summary>
        private void SyncActorsToStore()
        {
            foreach (var item in Store.Items)
            {
                if (item.Actor == null) continue;
                var pos = Store.CurrentPosition(item);
                item.LivePosition = pos;
                item.Actor.LivePosition = pos;
                item.Actor.Go.transform.localPosition = pos;

                item.Actor.LiveScale = item.Actor.BaseScale;
                item.Actor.LiveAlpha = item.Template.alpha;
                item.Actor.LiveColor = item.BaseColor;
                item.Actor.LiveRotation = item.Template.rotation;
                item.Actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, item.Template.rotation);
                item.Actor.ApplyColor();
            }
        }

        // ── 时间轴 ────────────────────────────────────────────────────────

        public float TotalDuration
        {
            get
            {
                if (cueDoc?.events == null) return 0f;
                float end = 0f;
                foreach (var ev in cueDoc.events)
                {
                    if (ev == null || ev.action == "wait") continue;
                    end = Mathf.Max(end, ev.at + ev.lead + Mathf.Max(0f, ev.dur));
                }
                return end;
            }
        }

        public void Seek(float time)
        {
            if (cueDoc == null) return;

            if (clock < 0f || time + 0.25f < clock) ResetToStart();

            clock = time;
            float scaled = time * Mathf.Max(0.01f, timeScale);

            while (nextIndex < cueDoc.events.Count && cueDoc.events[nextIndex].at <= scaled + 1e-4f)
            {
                var ev = cueDoc.events[nextIndex];
                nextIndex++;
                Trigger(ev);
            }

            for (int i = pendingScales.Count - 1; i >= 0; i--)
            {
                if (pendingScales[i].At > scaled + 1e-4f) continue;
                var entry = pendingScales[i];
                pendingScales.RemoveAt(i);
                ApplyScale(entry.Actor, entry.Factor, entry.Event);
            }
        }

        /// <summary>把这条 cue 直接推到结束（顺序播放进入下一条之前用）。</summary>
        public void Complete()
        {
            if (cueDoc?.events == null) return;
            while (nextIndex < cueDoc.events.Count)
            {
                var ev = cueDoc.events[nextIndex];
                nextIndex++;
                TriggerFinal(ev);
            }
            clock = Mathf.Max(clock, TotalDuration);
        }

        /// <summary>恢复到本条 cue 的入口状态（重播 / 向后拖动时用）。</summary>
        public void ResetToStart()
        {
            StopAnimations();
            pendingScales.Clear();
            RestoreSnapshot(entrySnapshot);
            clock = 0f;
            nextIndex = 0;
        }

        private void CaptureEntry()
        {
            entrySnapshot = ZoneSnapshot.Capture(Store);
        }

        private void RestoreSnapshot(ZoneSnapshot snapshot)
        {
            if (snapshot == null) return;

            foreach (var item in Store.Items)
            {
                if (item.Actor == null) continue;
                if (!snapshot.TryGet(item.Id, out string zoneId, out int order)) continue;
                item.ZoneId = zoneId;
                item.Order = order;

                var pos = Store.CurrentPosition(item);
                item.LivePosition = pos;
                item.Actor.LivePosition = pos;
                item.Actor.Go.transform.localPosition = pos;

                item.Actor.LiveScale = item.Actor.BaseScale;
                item.Actor.LiveAlpha = item.Template.alpha;
                item.Actor.LiveColor = item.BaseColor;
                item.Actor.LiveRotation = item.Template.rotation;
                item.Actor.Go.transform.localScale = item.Actor.BaseScale;
                item.Actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, item.Template.rotation);
                item.Actor.ApplyColor();
            }
        }

        public void ClearScene()
        {
            StopAnimations();
            ClearActors();
            cueDoc = null;
            clock = -1f;
            nextIndex = 0;
            pendingScales.Clear();
            Store.Reset();
        }

        private void StopAnimations()
        {
            for (int i = 0; i < running.Count; i++)
                if (running[i] != null) StopCoroutine(running[i]);
            running.Clear();
        }

        private void ClearActors()
        {
            actors.Clear();
            zonePanels.Clear();
            if (animRoot != null)
            {
                Object.DestroyImmediate(animRoot);
                animRoot = null;
            }
        }

        // ── 原语 ──────────────────────────────────────────────────────────

        private void Trigger(CueAnimEvent ev)
        {
            if (ev == null) return;

            switch (ev.action)
            {
                case "wait": return;
                case "move": TriggerMove(ev); return;
                case "rotate":
                case "flip": TriggerRotate(ev); return;
                case "scale": TriggerScale(ev); return;
                case "fade": TriggerFade(ev); return;
                case "highlight": TriggerHighlight(ev); return;
                case "shuffle": TriggerShuffle(ev); return;
                default:
                    Debug.LogWarning($"[TutorialCueAnim] unknown action '{ev.action}' in cue {CueId}");
                    return;
            }
        }

        /// <summary>把事件推到终态且不播协程，用于顺序播放时推进状态。</summary>
        private void TriggerFinal(CueAnimEvent ev)
        {
            if (ev == null || ev.action == "wait") return;

            if (ev.action == "move")
            {
                foreach (var step in PlanMove(ev))
                {
                    Store.MoveTo(step.Item, step.Destination);
                    if (step.Item.Actor != null) ApplyCurrentPlacement(step.Item, step.Item.Actor);
                }
                return;
            }

            // 其余原语只影响外观：进入下一条前复原到基准。
            foreach (var actor in Resolve(ev))
            {
                if (actor?.Item == null) continue;
                actor.LiveScale = actor.BaseScale;
                actor.LiveAlpha = actor.BaseAlpha;
                actor.LiveColor = actor.BaseColor;
                actor.LiveRotation = actor.Item.Template.rotation;
                actor.Go.transform.localScale = actor.BaseScale;
                actor.Go.transform.localRotation = Quaternion.Euler(0f, 0f, actor.LiveRotation);
                actor.Go.transform.localPosition = Store.CurrentPosition(actor.Item);
                actor.ApplyColor();
            }
        }

        private void ApplyCurrentPlacement(ZoneItem item, CueAnimActor actor)
        {
            var pos = Store.CurrentPosition(item);
            item.LivePosition = pos;
            actor.LivePosition = pos;
            actor.Go.transform.localPosition = pos;
            actor.LiveScale = actor.BaseScale;
            actor.Go.transform.localScale = actor.BaseScale;
        }

        private struct MovePlan
        {
            public ZoneItem Item;
            public string Destination;
        }

        /// <summary>
        /// move 的语义：把组件从 source zone 搬到 destination zone。
        /// 数据里只有两端 zone（+ 可选 take），落点位置由 zone 推导。
        /// 注意：这里只算「搬哪几件、搬到哪」，不改账本；改账本由调用方决定
        /// （播放时立即落账，位置动画负责补上视觉；推进终态时瞬间落账）。
        /// </summary>
        private List<MovePlan> PlanMove(CueAnimEvent ev)
        {
            var plan = new List<MovePlan>();

            if (!string.IsNullOrEmpty(ev.target))
            {
                var actor = FindActor(ev.target);
                if (actor?.Item != null)
                    plan.Add(new MovePlan { Item = actor.Item, Destination = ev.zone });
                return plan;
            }

            if (string.IsNullOrEmpty(ev.from) || Store.GetZone(ev.from) == null)
            {
                Debug.LogWarning($"[TutorialCueAnim] move in cue {CueId} has no resolvable from zone ('{ev.from}')");
                return plan;
            }

            if (string.IsNullOrEmpty(ev.zone) || Store.GetZone(ev.zone) == null)
            {
                Debug.LogWarning($"[TutorialCueAnim] move in cue {CueId} has no resolvable destination ('{ev.zone}')");
                return plan;
            }

            int take = ev.take.HasValue && ev.take.Value > 0 ? ev.take.Value : 1;
            var picked = new List<ZoneItem>();
            for (int i = 0; i < take; i++)
            {
                var item = PickFront(ev.from, picked);
                if (item == null) break;
                picked.Add(item);
                plan.Add(new MovePlan { Item = item, Destination = ev.zone });
            }
            return plan;
        }

        /// <summary>取 zone 里最靠前、且不在 excluded 中的组件。</summary>
        private ZoneItem PickFront(string zoneId, List<ZoneItem> excluded)
        {
            ZoneItem best = null;
            foreach (var item in Store.Items)
            {
                if (item.ZoneId != zoneId) continue;
                if (excluded != null && excluded.Contains(item)) continue;
                if (best == null || item.Order < best.Order) best = item;
            }
            return best;
        }

        private void TriggerMove(CueAnimEvent ev)
        {
            var moved = new List<ZoneItem>();
            foreach (var step in PlanMove(ev))
            {
                var actor = step.Item.Actor;
                Vector3 from = actor != null ? actor.LivePosition : step.Item.LivePosition;
                Store.MoveTo(step.Item, step.Destination);
                moved.Add(step.Item);

                Vector3 to = Store.CurrentPosition(step.Item);
                step.Item.LivePosition = to;
                if (actor == null) continue;
                RunTween(TweenPosition(actor, step.Item, from, to, ev));
            }

            CloseGaps(moved, ev);
        }

        /// <summary>
        /// 一件组件被拿走之后，同一 zone 里排在它后面的组件顺位前移。
        /// 这样「从供应堆拿走宝石」会看到堆真的少了一枚，而不是留一个空位。
        /// </summary>
        private void CloseGaps(List<ZoneItem> moved, CueAnimEvent ev)
        {
            if (moved == null || moved.Count == 0) return;

            var touched = new HashSet<string>();
            foreach (var item in moved)
            {
                // 源 zone 需要重排；目标 zone 不需要（新来的排在最后）。
                touched.Add(item.Id);
            }

            foreach (var item in Store.Items)
            {
                if (touched.Contains(item.Id) || item.Actor == null) continue;

                var target = Store.CurrentPosition(item);
                if ((target - item.Actor.LivePosition).sqrMagnitude < 1e-6f) continue;

                Vector3 from = item.Actor.LivePosition;
                item.LivePosition = target;
                RunTween(TweenPosition(item.Actor, item, from, target, ev));
            }
        }

        private void TriggerRotate(CueAnimEvent ev)
        {
            foreach (var actor in Resolve(ev))
            {
                float to = ev.action == "flip" ? actor.LiveRotation + 180f : ev.angle;
                RunTween(TweenRotation(actor, actor.LiveRotation, to, ev));
                actor.LiveRotation = to;
            }
        }

        private void TriggerScale(CueAnimEvent ev)
        {
            float factor = ev.scale <= 0f ? 1.25f : ev.scale;
            float step = ev.stagger > 0f ? ev.stagger : 0f;
            float scaledAt = clock * Mathf.Max(0.01f, timeScale);
            int scheduled = 0;

            foreach (var actor in Resolve(ev))
            {
                if (step <= 0f)
                {
                    ApplyScale(actor, factor, ev);
                }
                else
                {
                    // stagger：同一组组件错开缩放，视觉上是「一枚一枚被强调」。
                    pendingScales.Add(new PendingScale
                    {
                        At = scaledAt + scheduled * step,
                        Actor = actor,
                        Factor = factor,
                        Event = ev,
                    });
                }
                scheduled++;
            }
        }

        private void ApplyScale(CueAnimActor actor, float factor, CueAnimEvent ev)
        {
            Vector3 from = actor.LiveScale;
            Vector3 to = ev.scale_mode == "to" ? actor.BaseScale * factor : from * factor;
            actor.LiveScale = to;
            RunTween(TweenScale(actor, from, to, ev));
        }

        private void TriggerFade(CueAnimEvent ev)
        {
            foreach (var actor in Resolve(ev))
            {
                float to = ev.to_alpha.HasValue
                    ? Mathf.Clamp01(ev.to_alpha.Value)
                    : (actor.LiveAlpha > 0.5f ? 0f : 1f);
                float from = actor.LiveAlpha;
                actor.LiveAlpha = to;
                RunTween(TweenAlpha(actor, from, to, ev));
            }
        }

        /// <summary>
        /// highlight：目标可以是某件组件，也可以是整个 zone。
        /// zone 的情况只脉冲该区域的装饰底板（panel/dot），不脉冲里面每一枚宝石，
        /// 否则「高亮供应区」会变成整堆宝石一起闪。
        /// </summary>
        private void TriggerHighlight(CueAnimEvent ev)
        {
            foreach (var actor in ResolveHighlight(ev))
            {
                if (actor.Renderer == null) continue;

                float peak = ev.peak_alpha.HasValue ? Mathf.Clamp01(ev.peak_alpha.Value) : 0.6f;
                float grow = ev.grow.HasValue && ev.grow.Value > 0f ? ev.grow.Value : 1f;
                float dur = Mathf.Max(ev.dur, 0.05f);

                Color from = actor.LiveColor;
                from.a = 0f;
                var to = new Color(from.r, from.g, from.b, peak);
                Vector3 toScale = actor.BaseScale * grow;

                actor.Renderer.enabled = true;
                actor.Renderer.color = from;
                actor.Go.transform.localScale = Vector3.zero;

                RunTween(PulseRoutine(actor, from, to, Vector3.zero, toScale, dur, ev));
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

            // 装饰底板的高亮是「提亮后回落」，不是消失；普通件的高亮则收起。
            if (actor.Item != null && actor.Item.Template != null && IsDecoration(actor.Item.Template))
            {
                actor.Renderer.color = actor.BaseColor;
                actor.Go.transform.localScale = actor.BaseScale;
            }
            else
            {
                actor.Renderer.enabled = false;
                actor.Go.transform.localScale = Vector3.zero;
            }
        }

        private static bool IsDecoration(StageTemplate tpl)
        {
            return tpl.shape == "panel" || tpl.shape == "dot";
        }

        private void TriggerShuffle(CueAnimEvent ev)
        {
            var list = Resolve(ev);
            if (list.Count == 0) return;

            var targets = new Transform[list.Count];
            var positions = new Vector3[list.Count];
            const float spread = 0.12f;
            float center = (list.Count - 1) * 0.5f;
            for (int i = 0; i < list.Count; i++)
            {
                targets[i] = list[i].Go.transform;
                Vector3 from = list[i].LivePosition;
                positions[i] = from + new Vector3((i - center) * spread, 0f, (i % 2 == 0 ? 1f : -1f) * 0.04f);
                list[i].LivePosition = positions[i];
            }
            RunTween(TutorialPrimitives.TweenShuffle(targets, positions, Mathf.Max(ev.dur, 0.1f), EasingOr(ev)));
        }

        // ── 协程 ──────────────────────────────────────────────────────────

        private IEnumerator TweenPosition(CueAnimActor actor, ZoneItem item, Vector3 from, Vector3 to, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);
            yield return TutorialPrimitives.TweenPosition(actor.Go.transform, from, to, ev.dur, EasingOr(ev));
            actor.LivePosition = to;
            item.LivePosition = to;
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
                var c = actor.LiveColor;
                c.a = from;
                actor.Renderer.color = c;
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
            running.Add(StartCoroutine(routine));
        }

        // ── 解析 ──────────────────────────────────────────────────────────

        private CueAnimActor FindActor(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return actors.TryGetValue(id, out var actor) ? actor : null;
        }

        /// <summary>highlight 的目标解析：zone → 该区域的装饰底板；显式 target → 那一件。</summary>
        private List<CueAnimActor> ResolveHighlight(CueAnimEvent ev)
        {
            var result = new List<CueAnimActor>();
            if (ev == null) return result;

            if (!string.IsNullOrEmpty(ev.target))
            {
                var actor = FindActor(ev.target);
                if (actor != null) result.Add(actor);
                return result;
            }

            // zone 高亮：打到代表该 zone 的装饰底板，而不是区域里的每一件组件。
            if (!string.IsNullOrEmpty(ev.zone) && zonePanels.TryGetValue(ev.zone, out var panelIds))
            {
                foreach (var panelId in panelIds)
                    if (actors.TryGetValue(panelId, out var panel)) result.Add(panel);
                if (result.Count > 0) return result;
            }

            return Resolve(ev);
        }

        private List<CueAnimActor> Resolve(CueAnimEvent ev)
        {
            var result = new List<CueAnimActor>();
            if (ev == null) return result;

            if (!string.IsNullOrEmpty(ev.zone))
            {
                foreach (var item in Store.Items)
                    if (item.ZoneId == ev.zone && item.Actor != null) result.Add(item.Actor);
                return result;
            }

            if (!string.IsNullOrEmpty(ev.target))
            {
                var actor = FindActor(ev.target);
                if (actor != null) result.Add(actor);
                return result;
            }

            foreach (var item in Store.Items)
                if (item.Actor != null) result.Add(item.Actor);
            return result;
        }

        // ── 相机与背景 ────────────────────────────────────────────────────

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

        private void SetBackground()
        {
            if (animCamera == null) return;
            var bg = new Color(0.12f, 0.13f, 0.16f, 1f);
            if (stage?.board != null && !string.IsNullOrEmpty(stage.board.background))
            {
                Color parsed;
                if (Palette.TryResolveRgb(stage.board.background, out parsed)) bg = parsed;
            }
            animCamera.backgroundColor = bg;
        }

        /// <summary>按牌桌的 zone 范围取景，50° 固定俯角。</summary>
        private void FitCamera()
        {
            float pitch = stage?.board != null && stage.board.camera_pitch > 0f ? stage.board.camera_pitch : 50f;
            float orthoScale = stage?.board != null && stage.board.ortho_scale > 0f ? stage.board.ortho_scale : 1.18f;

            float minX = -1.5f, maxX = 1.5f, minZ = -1f, maxZ = 1.4f;
            bool any = false;

            foreach (var zone in Store.Zones)
            {
                if (zone.role == "offstage") continue;
                var layout = zone.layout ?? new StageLayout();
                int capacity = zone.capacity > 0 ? zone.capacity : 1;
                int cols = Mathf.Max(1, layout.cols);
                int rows = Mathf.Max(1, Mathf.CeilToInt(capacity / (float)cols));
                float halfW = Mathf.Max(0.05f, (cols - 1) * 0.5f * layout.x_step);
                float halfH = Mathf.Max(0.05f, (rows - 1) * 0.5f * layout.z_step);
                const float pad = 0.26f;

                float x0 = zone.center.x - halfW - pad, x1 = zone.center.x + halfW + pad;
                float z0 = zone.center.z - halfH - pad, z1 = zone.center.z + halfH + pad;
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

            float cx = (minX + maxX) * 0.5f;
            float cz = (minZ + maxZ) * 0.5f;
            float halfW2 = Mathf.Max(0.5f, (maxX - minX) * 0.5f * orthoScale);
            float halfH2 = Mathf.Max(0.5f, (maxZ - minZ) * 0.5f * orthoScale);

            float pitchRad = pitch * Mathf.Deg2Rad;
            float sinP = Mathf.Max(0.15f, Mathf.Sin(pitchRad));
            float aspect = Mathf.Max(0.5f, (float)Screen.width / Mathf.Max(1, Screen.height));

            float orthoSize = Mathf.Max(halfH2 * sinP, halfW2 / aspect);
            orthoSize = Mathf.Max(orthoSize, 0.6f);

            float distance = orthoSize * 3.2f;
            var focus = new Vector3(cx, 0f, cz);
            var eye = focus + new Vector3(0f, Mathf.Sin(pitchRad), -Mathf.Cos(pitchRad)) * distance;
            animCamera.transform.SetPositionAndRotation(eye, Quaternion.Euler(pitch, 0f, 0f));
            animCamera.orthographicSize = orthoSize;

            CameraOrthoSize = orthoSize;
            CameraGroundHalfWidth = orthoSize * aspect;
        }

        public bool WorldToScreen(Vector3 world, out Vector3 screen)
        {
            screen = Vector3.zero;
            if (animCamera == null) return false;
            screen = animCamera.WorldToScreenPoint(world);
            screen.y = Screen.height - screen.y;
            return screen.z > 0f;
        }
    }

    /// <summary>cue 入口状态的轻量快照，用于重播时恢复（不重建对象）。</summary>
    public class ZoneSnapshot
    {
        private struct State
        {
            public string ZoneId;
            public int Order;
        }

        private readonly Dictionary<string, State> states = new Dictionary<string, State>();

        public static ZoneSnapshot Capture(ZoneStore store)
        {
            var snapshot = new ZoneSnapshot();
            foreach (var item in store.Items)
            {
                snapshot.states[item.Id] = new State
                {
                    ZoneId = item.ZoneId,
                    Order = item.Order,
                };
            }
            return snapshot;
        }

        public bool TryGet(string id, out string zoneId, out int order)
        {
            zoneId = null;
            order = -1;
            if (!states.TryGetValue(id, out var state)) return false;
            zoneId = state.ZoneId;
            order = state.Order;
            return true;
        }
    }
}
