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
    public partial class TutorialCueAnimPlayer : MonoBehaviour
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
        /// <summary>区域底板：zone id → 代表它的装饰件 id（静态底板，不带高亮）。</summary>
        private readonly Dictionary<string, List<string>> zonePanels = new Dictionary<string, List<string>>();

        /// <summary>底板 id → 它代表的 zone 列表；这些 zone 里没有可见组件时底板不显示。</summary>
        private readonly Dictionary<string, List<string>> panelZones = new Dictionary<string, List<string>>();

        /// <summary>zone id → 该 zone 专属的高亮底板（每个 zone 一块，绝不共用）。</summary>
        private readonly Dictionary<string, CueAnimActor> zoneGlow = new Dictionary<string, CueAnimActor>();

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

            // 续接（顺序播放）：接着上一条的终态。
            // 不续接（跳转 / 重播 / 按 B 预览）：退回牌桌初始态，再按本条 cue 的 start 布置。
            // 必须 Reset+ApplyInitial，否则跳到后面的 cue 会带着上一轮留下的组件。
            if (!continueState)
            {
                Store.Reset();
                Store.ApplyInitial();
            }
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

        private string OffstageZoneId
        {
            get
            {
                if (offstageZoneId == null)
                {
                    offstageZoneId = "";
                    foreach (var zone in Store.Zones)
                        if (zone.role == "offstage") { offstageZoneId = zone.id; break; }
                }
                return offstageZoneId;
            }
        }

        private string offstageZoneId;

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
            offstageZoneId = null;
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
                    int want = seed.expand_to > 0 ? seed.expand_to : Mathf.Max(1, seed.count);
                    int have = Store.CountIn(seed.zone, seed.palette, seed.template);
                    int need = Mathf.Max(0, want - have);
                    if (need == 0) continue;

                    // 必须「先搬后生」：stage.initial 常把这一份放在 offstage（表示还在盒子里），
                    // 直接 Spawn 会多造一份，出现在画面外的重复件。
                    // 每一次都重新数一遍现有数量，因此重播/重复载入也不会翻倍。
                    int guard = 0;
                    while (need > 0 && guard++ < 64)
                    {
                        int moved = Store.PullFrom(OffstageZoneId, seed.template, seed.palette, seed.zone, need);
                        need -= moved;
                        if (moved == 0) break;
                    }
                    if (need > 0) Store.Spawn(seed.template, seed.palette, seed.zone, need);
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
                        panelZones[anchor.id] = new List<string>(anchor.zones);
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

                // 正反两面都准备好：翻面时只换贴图，不重建对象。
                actor.FaceSprite = sr.sprite;
                var backPath = ResolveBackImagePath(item.Template);
                if (backPath != null)
                {
                    var back = CardImageLoader.Load(backPath, item.Template.shape);
                    if (back != null) actor.BackSprite = back;
                }

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

            var scale = LocalScaleFor(tpl, sprite);
            if (tpl.highlight)
            {
                sr.enabled = false;
                go.transform.localScale = Vector3.zero;
            }
            else
            {
                go.transform.localScale = scale;
            }
            return go;
        }

        private Sprite ResolveSprite(StageTemplate tpl)
        {
            var image = ResolveImagePath(tpl);
            if (image != null)
            {
                var loaded = CardImageLoader.Load(image, tpl.shape);
                if (loaded != null)
                {
                    if (logImages) Debug.Log($"[TutorialCueAnim] 扫描图: {tpl.id} ({tpl.palette}) ← {Path.GetFileName(image)}");
                    return loaded;
                }
                Debug.LogWarning($"[TutorialCueAnim] 扫描图加载失败: {image}");
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
        /// 色板名 → 扫描图文件名。宝石六色共用一个 gem 模板，靠 palette 区分实物图。
        /// 键是 stage 里用的 palette 名；值是 media/card 下的文件名。
        /// </summary>
        private static readonly Dictionary<string, string> PaletteImages = new Dictionary<string, string>
        {
            { "gem_diamond",  "白宝石.jpg" },
            { "gem_sapphire", "蓝宝石.jpg" },
            { "gem_emerald",  "绿宝石.jpg" },
            { "gem_ruby",     "红宝石.jpg" },
            { "gem_onyx",     "黑宝石.jpg" },
            { "gem_gold",     "黄金.jpg" },
        };

        /// <summary>
        /// 找到模板对应的扫描图。优先 face_image；其次按色板名的实物图；最后按等级卡背约定。
        /// </summary>
        /// <summary>翻面用的另一面。模板写了 back_image 才有；否则不翻转。</summary>
        private string ResolveBackImagePath(StageTemplate tpl)
        {
            if (tpl == null || string.IsNullOrEmpty(tpl.back_image) || string.IsNullOrEmpty(gameRootPath))
                return null;
            var path = Path.Combine(gameRootPath, tpl.back_image);
            if (File.Exists(path)) return path;
            Debug.LogWarning($"[TutorialCueAnim] back_image 不存在: {path}");
            return null;
        }

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

            // 按色板名找实物图（宝石六色、黄金）
            if (!string.IsNullOrEmpty(tpl.palette) && PaletteImages.TryGetValue(tpl.palette, out var image))
            {
                yield return "media/card/" + image;
                yield return "media/card/" + Path.GetFileNameWithoutExtension(image) + ".png";
            }

            // 贵族板块
            if (tpl.palette == "noble")
            {
                yield return "media/card/贵族_0001.jpg";
                yield return "media/card/贵族_0001.png";
            }

            if (tpl.shape != "card" || string.IsNullOrEmpty(tpl.palette)) yield break;
            if (!tpl.palette.StartsWith("card_level_")) yield break;

            string level = "level_" + tpl.palette.Substring("card_level_".Length);
            yield return "media/card/" + level + "_back.jpg";
            yield return "media/card/" + level + "_back.png";
        }

        /// <summary>
        /// 计算 SpriteRenderer 需要的 localScale。
        ///
        /// 注意：sprite 的原生尺寸 = 像素 / pixelsPerUnit。扫描图是 712x1012 @100PPU，
        /// 原生就已经是 7.12x10.12 世界单位，所以绝不能把「目标世界尺寸」直接当缩放用，
        /// 否则卡牌会放大到占满屏幕。缩放 = 目标世界尺寸 / 原生世界尺寸。
        /// </summary>
        private static Vector3 LocalScaleFor(StageTemplate tpl, Sprite sprite)
        {
            float nativeW = 1f, nativeH = 1f;
            if (sprite != null && sprite.rect.width > 0f && sprite.rect.height > 0f)
            {
                float ppu = sprite.pixelsPerUnit > 0f ? sprite.pixelsPerUnit : GemPpu;
                nativeW = sprite.rect.width / ppu;
                nativeH = sprite.rect.height / ppu;
            }

            if (tpl.width > 0f || tpl.height > 0f)
            {
                // 显式宽高：按比例取较小的那个缩放，保证整体装进给定尺寸（不拉伸卡面）。
                float wantW = tpl.width > 0f ? tpl.width : nativeW;
                float wantH = tpl.height > 0f ? tpl.height : nativeH;
                float scale = Mathf.Min(wantW / nativeW, wantH / nativeH);
                return new Vector3(scale, scale, 1f);
            }

            float target = tpl.world_size <= 0f ? 0.10f : tpl.world_size;
            float uniform = target / Mathf.Max(nativeW, nativeH);
            return new Vector3(uniform, uniform, 1f);
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

        /// <summary>
        /// 区域底板随内容出现：它代表的 zone 里一件可见组件都没有时，底板不画。
        /// 否则「宝石还在盒子里」时供应区底板就已经亮在那里了。
        /// </summary>
        private void UpdatePanelVisibility()
        {
            if (panelZones.Count == 0) return;

            foreach (var pair in panelZones)
            {
                if (!actors.TryGetValue(pair.Key, out var panel) || panel.Renderer == null) continue;

                bool occupied = false;
                foreach (var zoneId in pair.Value)
                {
                    if (string.IsNullOrEmpty(zoneId)) continue;
                    foreach (var item in Store.Items)
                    {
                        if (item.ZoneId != zoneId) continue;
                        // 还在 offstage 的组件不算「在场」
                        var zone = Store.GetZone(item.ZoneId);
                        if (zone != null && zone.role == "offstage") continue;
                        if (item.Actor != null && item.Actor.Go != null && item.Actor.Go.activeSelf)
                        {
                            occupied = true;
                            break;
                        }
                    }
                    if (occupied) break;
                }

                panel.Renderer.enabled = occupied;
            }
        }

        /// <summary>按「是否已翻开」刷新贴图。有背面贴图且未翻开时显示背面。</summary>
        private static void RefreshFace(CueAnimActor actor)
        {
            if (actor?.Renderer == null || actor.BackSprite == null) return;
            actor.Renderer.sprite = actor.Item != null && actor.Item.Flipped
                ? actor.BackSprite
                : actor.FaceSprite;
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
                RefreshFace(item.Actor);
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

        /// <summary>
        /// 外部直接给定动画时间（离线出帧 / 帧截图用，不依赖音频）。
        /// 与 Seek 的区别只是语义：Seek 由音频时钟驱动，这里由调用方驱动。
        /// </summary>
        public void DriveAnimation(float time)
        {
            Seek(time);
        }

        /// <summary>
        /// 把这条 cue 推进到指定时刻的**终态**，不播协程、不等音频。
        ///
        /// 两个用途：
        ///   1. 离线出帧（batchmode 里没有音频设备，不能靠音频时钟）；
        ///   2. 将来编译器离线计算「某个时刻的完整画面」。
        /// 与 Seek 的区别：Seek 触发协程做插值（表现为动画），这里直接落位（表现为状态）。
        /// </summary>
        public void SnapTo(float time)
        {
            if (cueDoc?.events == null) return;

            var target = new List<CueAnimEvent>();
            foreach (var ev in cueDoc.events)
                if (ev != null && ev.at <= time + 1e-4f) target.Add(ev);

            StopAnimations();
            RestoreSnapshot(entrySnapshot);

            // 需要按原始顺序逐条落位，因为 move 依赖前一条 move 之后的顺序（take 的取件顺序）。
            foreach (var ev in target) TriggerFinal(ev);

            clock = time;
            nextIndex = cueDoc.events.Count;
            pendingScales.Clear();
            UpdatePanelVisibility();
        }

        public void Seek(float time)
        {
            if (cueDoc == null) return;

            if (clock < 0f || time + 0.25f < clock) ResetToStart();

            clock = time;
            float scaled = time * Mathf.Max(0.01f, timeScale);

            UpdatePanelVisibility();

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
            UpdatePanelVisibility();
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
                RefreshFace(item.Actor);
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
            panelZones.Clear();
            zoneGlow.Clear();
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
                    if (step.Order >= 0) Store.MoveToAt(step.Item, step.Destination, step.Order);
                    else Store.MoveTo(step.Item, step.Destination);
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
            public int Order;      // -1 = 追加到末尾
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
                    plan.Add(new MovePlan { Item = actor.Item, Destination = ev.zone, Order = ev.order });
                return plan;
            }

            if (string.IsNullOrEmpty(ev.zone) || Store.GetZone(ev.zone) == null)
            {
                Debug.LogWarning($"[TutorialCueAnim] move in cue {CueId} has no resolvable destination ('{ev.zone}')");
                return plan;
            }

            var sources = ev.from;
            if (sources == null || sources.Count == 0)
            {
                Debug.LogWarning($"[TutorialCueAnim] move in cue {CueId} has no from zone");
                return plan;
            }

            int take = ev.take > 0 ? ev.take : 1;
            var picked = new List<ZoneItem>();

            // from 写多个 zone = 每个 zone 各取 take 件（三种宝石各一枚）。
            foreach (var source in sources)
            {
                if (Store.GetZone(source) == null)
                {
                    Debug.LogWarning($"[TutorialCueAnim] move in cue {CueId}: 未知 from zone '{source}'");
                    continue;
                }
                for (int i = 0; i < take; i++)
                {
                    var item = PickFront(source, picked);
                    if (item == null) break;
                    picked.Add(item);
                    plan.Add(new MovePlan { Item = item, Destination = ev.zone, Order = ev.order });
                }
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
                if (step.Order >= 0) Store.MoveToAt(step.Item, step.Destination, step.Order);
                else Store.MoveTo(step.Item, step.Destination);
                moved.Add(step.Item);
                if (logImages && step.Order >= 0)
                    Debug.Log($"[Move] {step.Item.Id} → {step.Destination} order={step.Item.Order} " +
                              $"pos=({Store.CurrentPosition(step.Item).x:0.00},{Store.CurrentPosition(step.Item).z:0.00}) " +
                              $"zoneCount={Store.CountInZone(step.Destination)}");

                Vector3 to = Store.CurrentPosition(step.Item);
                step.Item.LivePosition = to;
                if (actor == null) continue;
                RunTween(TweenPosition(actor, step.Item, from, to, ev));

                // 边移动边翻转：到终点恰好转到另一面。
                if (ev.flip) RunTween(TweenFlip(actor, step.Item, ev));
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
                float to = ev.to_alpha >= 0f
                    ? Mathf.Clamp01(ev.to_alpha)
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
        /// <summary>
        /// shuffle：洗混是**原地**表现，不改变任何组件的位置。
        ///
        /// 以前这里把每张牌搬到新位置（「交叉换位」），作用于整摞 40 张时会把牌堆
        /// 摊成一条横跨画面的长龙 —— 洗牌不该把牌洗到桌面上。所以改成原地抖动：
        /// 轻微摇晃 + 微小缩放起伏，位置始终不变。
        /// </summary>
        private void TriggerShuffle(CueAnimEvent ev)
        {
            foreach (var actor in Resolve(ev))
            {
                if (actor?.Go == null) continue;
                RunTween(TutorialPrimitives.TweenShuffleInPlace(actor.Go.transform,
                    Mathf.Max(ev.dur, 0.1f), EasingOr(ev)));
            }
        }

        // ── 协程 ──────────────────────────────────────────────────────────

        private IEnumerator TweenPosition(CueAnimActor actor, ZoneItem item, Vector3 from, Vector3 to, CueAnimEvent ev)
        {
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);
            yield return TutorialPrimitives.TweenPosition(actor.Go.transform, from, to, ev.dur, EasingOr(ev));
            actor.LivePosition = to;
            item.LivePosition = to;
        }

        /// <summary>
        /// 边移动边翻转：与位移并行，到终点恰好转到另一面。
        ///
        /// 轴向取 actor 的**本地 Y 轴**——actor 已绕 X 转 90° 平躺，本地 Y 正是屏幕竖直方向，
        /// 绕它转 180° 就是牌面水平翻过去（途中 90° 时收成一条线）。
        /// 用 Mathf.Cos 判断当前朝哪边，决定显示正面还是背面贴图。
        /// </summary>
        private IEnumerator TweenFlip(CueAnimActor actor, ZoneItem item, CueAnimEvent ev)
        {
            if (actor?.Go == null || actor.BackSprite == null) yield break;
            if (ev.lead > 0f) yield return WaitScaled(ev.lead);

            float dur = Mathf.Max(ev.dur, 0.01f);
            float fromYaw = item.Flipped ? 180f : 0f;
            float toYaw = fromYaw + 180f;

            float t = 0f;
            while (t < dur)
            {
                t = Mathf.Min(t + Time.deltaTime, dur);
                float k = Easing.Evaluate(EasingOr(ev), t / dur);
                float yaw = Mathf.LerpUnclamped(fromYaw, toYaw, k);
                actor.Go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

                // 以 90°/270° 为界切贴图：前半看到原面，后半看到另一面。
                bool showingBack = Mathf.Cos(yaw * Mathf.Deg2Rad) < 0f;
                actor.Renderer.sprite = showingBack ? actor.BackSprite : actor.FaceSprite;
                yield return null;
            }

            actor.Go.transform.localRotation = Quaternion.identity;
            item.Flipped = !item.Flipped;
            actor.Renderer.sprite = item.Flipped ? actor.BackSprite : actor.FaceSprite;
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

        /// <summary>装饰件（区域底板/高亮层）不参与原地缩放脉冲。</summary>
        private static bool IsDecoration(StageTemplate tpl)
        {
            return tpl != null && (tpl.shape == "panel" || tpl.shape == "dot");
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

            // extent 是取景的唯一依据；没有时才退回按 zone 布局推算（容易因 cols 写错而失准）。
            var extent = stage?.board?.extent;
            if (extent != null && extent.max_x > extent.min_x && extent.max_z > extent.min_z)
            {
                minX = extent.min_x; maxX = extent.max_x;
                minZ = extent.min_z; maxZ = extent.max_z;
                any = true;
            }

            foreach (var zone in Store.Zones)
            {
                if (any || zone.role == "offstage") continue;
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
            float aspect = cameraAspectOverride > 0f
                ? cameraAspectOverride
                : (stage?.board != null && stage.board.aspect > 0f
                    ? stage.board.aspect
                    : Mathf.Max(0.5f, (float)Screen.width / Mathf.Max(1, Screen.height)));

            float orthoSize = Mathf.Max(halfH2 * sinP, halfW2 / aspect);
            orthoSize = Mathf.Max(orthoSize, 0.6f);

            // 横向上界兜底：相机实际 aspect 若比取景时窄，内容会从左右溢出。
            float safeAspect = Mathf.Max(0.5f, aspect);
            if (orthoSize * safeAspect < halfW2) orthoSize = halfW2 / safeAspect;

            float distance = orthoSize * 3.2f;
            var focus = new Vector3(cx, 0f, cz);
            var eye = focus + new Vector3(0f, Mathf.Sin(pitchRad), -Mathf.Cos(pitchRad)) * distance;
            animCamera.transform.SetPositionAndRotation(eye, Quaternion.Euler(pitch, 0f, 0f));
            animCamera.orthographicSize = orthoSize;

            CameraOrthoSize = orthoSize;
            CameraGroundHalfWidth = orthoSize * aspect;

            if (logCameraFit)
            {
                Debug.Log($"[CueAnim.FitCamera] bounds x[{minX:0.00},{maxX:0.00}] z[{minZ:0.00},{maxZ:0.00}] " +
                          $"halfW={halfW2:0.00} halfH={halfH2:0.00} orthoSize={orthoSize:0.00} " +
                          $"aspect={aspect:0.00} pitch={pitch:0} eye=({eye.x:0.00},{eye.y:0.00},{eye.z:0.00})");
            }
        }

        /// <summary>调试开关：打印取景计算过程（离线出帧诊断用）。</summary>
        public bool logCameraFit;

        /// <summary>调试：打印扫锚图解析结果。</summary>
        public bool logImages;

        /// <summary>
        /// 取景使用的宽高比。&lt;=0 表示用当前屏幕（正常运行）。
        /// 离线出帧必须显式指定，否则 batchmode 的 4:3 GameView 会和 16:9 渲染目标不一致，
        /// 导致画面被裁掉右边和下边。
        /// </summary>
        public float cameraAspectOverride;

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
