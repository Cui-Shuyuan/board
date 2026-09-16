// BoardGameTutorial
// 纯音频 cue 播放器 v1：音频 + 字幕 + cue 内动画。
//
// 数据：
//   games/{game}/tutorial/{track}.runtime.json        —— cue 顺序、音频、字幕、导航
//   games/{game}/tutorial/anim/{track}/{cue_id}.json  —— 该 cue 的画面与动作时间轴
//
// 三层时钟关系：音频是主，动画时钟直接取 audioSource.time，
// 因此动画天然对齐口播、暂停即冻结、重播即从头。
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace BoardGameTutorial
{
    public class TutorialCuePlayer : MonoBehaviour
    {
        [Header("Data")]
        public string gameId = "splendor";
        public string track = "full";

        [Tooltip("可留空。编辑器下会自动尝试仓库的 games 目录；打包/安卓可填 persistentDataPath 或 StreamingAssets。")]
        public string tutorialRoot = "";

        [Header("Playback")]
        public bool autoPlay = true;
        public bool autoAdvance = true;
        public bool showDebugUI = true;

        [Header("Cue animation")]
        [Tooltip("是否播放 cue 内动画。关掉退回纯音频（便于 A/B 对比）。")]
        public bool enableCueAnimation = true;

        [Tooltip("调试：播放动画到 cue 结尾后停住不自动进入下一条。")]
        public bool pauseAtCueEnd;

        [Tooltip("调试：按 B 直接跳到当前正在制作的动画（默认是设置段第一条），再按一次回到原来的位置。")]
        public string debugJumpCueId = "setup.cards.001.1";

        [Tooltip("调试叠层：在画面上标注供应区/持有区的位置。仅用于标定，默认关闭——它会在画面中间画出色块和文字。")]
        public bool showZoneLabels;

        // 纯音频 cue 模式开关。
        // true：自动启动 cue 播放器，禁用旧的 TeachingPlayer 自动动画。
        // false：恢复旧的 TeachingPlayer 自动动画，cue 播放器不自动启动。
        public static bool CueModeEnabled = true;

        private TutorialCueDoc doc;
        private string gameRoot;
        private AudioSource audioSource;
        private TutorialCueAnimPlayer animPlayer;
        private Coroutine playbackRoutine;
        private int currentIndex = -1;
        private int previousIndex = -1;
        private int debugJumpReturnIndex = -1;
        private bool inDebugJump;
        private bool isPaused;
        private float fallbackClock;
        private string currentSubtitle = "";
        private GUIStyle debugStyle;
        private Texture2D swatchTexture;

        private struct ZoneLabel
        {
            public string text;
            public string colorHex;
            public Vector3 world;
        }

        private readonly List<ZoneLabel> zoneLabels = new List<ZoneLabel>();

        public TutorialCue CurrentCue
        {
            get
            {
                if (doc == null || doc.cues == null) return null;
                if (currentIndex < 0 || currentIndex >= doc.cues.Count) return null;
                return doc.cues[currentIndex];
            }
        }

        public string CurrentCueId => CurrentCue != null ? CurrentCue.id : "";
        public string CurrentCueText => CurrentCue != null ? CurrentCue.text : "";
        public string CurrentCueGroupPath => CurrentCue != null && CurrentCue.group_path != null
            ? string.Join(" > ", CurrentCue.group_path)
            : "";
        public bool IsPlaying => audioSource != null && audioSource.isPlaying;
        public TutorialCueAnimPlayer AnimPlayer => animPlayer;

        /// <summary>当前载入的 runtime 文档（出帧/离线工具用）。</summary>
        public TutorialCueDoc Document => doc;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!CueModeEnabled) return;
            if (UnityEngine.Object.FindFirstObjectByType<TutorialCuePlayer>() != null) return;
            var go = new GameObject("TutorialCuePlayer");
            go.AddComponent<TutorialCuePlayer>();
        }

        private void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
            }
            audioSource.playOnAwake = false;
            audioSource.loop = false;

            animPlayer = GetComponent<TutorialCueAnimPlayer>();
            if (animPlayer == null)
            {
                animPlayer = gameObject.AddComponent<TutorialCueAnimPlayer>();
            }
            animPlayer.animationEnabled = enableCueAnimation;
        }

        private void Start()
        {
            if (autoPlay)
            {
                LoadAndPlay();
            }
        }

        public bool LoadAndPlay()
        {
            if (!LoadRuntime()) return false;
            if (doc.cues == null || doc.cues.Count == 0)
            {
                Debug.LogError("[TutorialCuePlayer] runtime has no cues");
                return false;
            }
            PlayCue(0);
            return true;
        }

        public bool LoadRuntime()
        {
            gameRoot = ResolveGameRoot();
            if (string.IsNullOrEmpty(gameRoot))
            {
                Debug.LogError("[TutorialCuePlayer] cannot find game root. Set tutorialRoot or put files in games/{game}.");
                return false;
            }

            string runtimePath = Path.Combine(gameRoot, "tutorial", track + ".runtime.json");
            if (!File.Exists(runtimePath))
            {
                Debug.LogError($"[TutorialCuePlayer] missing runtime file: {runtimePath}");
                return false;
            }

            string json = File.ReadAllText(runtimePath);
            doc = JsonUtility.FromJson<TutorialCueDoc>(json);
            if (doc == null || doc.cues == null || doc.cues.Count == 0)
            {
                Debug.LogError($"[TutorialCuePlayer] failed to parse {runtimePath}");
                return false;
            }

            NormalizeDoc();
            return true;
        }

        private string ResolveGameRoot()
        {
            if (!string.IsNullOrEmpty(tutorialRoot))
            {
                string custom = Path.Combine(tutorialRoot, gameId);
                if (Directory.Exists(custom)) return custom;
                Debug.LogWarning($"[TutorialCuePlayer] tutorialRoot does not contain {gameId}: {tutorialRoot}");
            }

            // 编辑器/Windows：client/Assets -> repo root -> games/{game}
            string repoGames = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "games"));
            string repoGame = Path.Combine(repoGames, gameId);
            if (Directory.Exists(repoGame)) return repoGame;

            string persistent = Path.Combine(Application.persistentDataPath, gameId);
            if (Directory.Exists(persistent)) return persistent;

            string streaming = Path.Combine(Application.streamingAssetsPath, gameId);
            if (Directory.Exists(streaming)) return streaming;

            return null;
        }

        private void NormalizeDoc()
        {
            for (int i = 0; i < doc.cues.Count; i++)
            {
                var cue = doc.cues[i];
                if (cue.group_path == null) cue.group_path = new List<string>();
                if (cue.refs == null) cue.refs = new List<string>();
                if (cue.subtitles == null) cue.subtitles = new List<TutorialCueSubtitle>();
                foreach (var subtitle in cue.subtitles)
                {
                    if (subtitle.words == null) subtitle.words = new List<TutorialCueWord>();
                }
            }
        }

        /// <summary>
        /// continueState = true 表示「接着上一条 cue 的终态继续」（顺序播放）；
        /// false 表示重新起局（首条 cue / 跳转 / 手动回到某条）。
        /// 动画是牌桌状态的变化，所以这个区分决定了画面从哪种局面开始。
        /// </summary>
        public void PlayCue(int index, bool continueState = false)
        {
            if (doc == null || doc.cues == null || doc.cues.Count == 0) return;
            index = Mathf.Clamp(index, 0, doc.cues.Count - 1);
            if (playbackRoutine != null) StopCoroutine(playbackRoutine);
            playbackRoutine = StartCoroutine(PlayCueRoutine(index, continueState));
        }

        /// <summary>
        /// 从第一条开始，把目标 cue 之前所有带动画数据的 cue 静默推到终态，重建牌桌。
        /// 这样任意跳转都能看到正确的当前局面，而不依赖播放历史。
        /// 将来编译器会把每个 cue 的入口状态直接写进 runtime，这一步即可省掉。
        /// </summary>
        /// <summary>
        /// 解出第 targetIndex 条 cue 的**入口状态由哪条 cue 的终态提供**。
        ///
        ///   显式写了 entry  → 用它（"initial" 表示牌桌初始态，否则是某条 cue 的 id）
        ///   没写            → 继承「上一条有动画数据的 cue」的终态；再往上同理
        ///
        /// 返回 null 表示"牌桌初始态"。
        /// </summary>
        private string ResolveEntryCueId(int targetIndex)
        {
            if (doc?.cues == null || targetIndex <= 0 || targetIndex >= doc.cues.Count) return null;

            var me = doc.cues[targetIndex];
            string declared = string.IsNullOrWhiteSpace(me.entry) ? null : me.entry.Trim();
            if (!string.IsNullOrEmpty(declared))
                return declared == "initial" ? null : declared;

            // 未声明：继承上一条「有终态可言」的 cue。
            // 没有动画数据的 cue 不改牌局，跳过它继续往上找。
            for (int i = targetIndex - 1; i >= 0; i--)
                if (HasAnimation(doc.cues[i].id)) return doc.cues[i].id;
            return null;
        }

        private bool HasAnimation(string cueId)
        {
            if (string.IsNullOrEmpty(cueId) || string.IsNullOrEmpty(gameRoot)) return false;
            return File.Exists(Path.Combine(gameRoot, "tutorial", "anim", track, cueId + ".json"));
        }

        /// <summary>
        /// 把「入口状态」算出来并交给正式播放器。
        ///
        /// 做法：拿一个**独立的**播放器实例当草稿纸，从 stage.initial 起，
        /// 沿入口链把每条祖先 cue 推到终态，再把结果整体交给主播放器。
        /// 草稿纸与主播放器完全隔离，所以不会污染主播放器的时钟和片段列表。
        /// </summary>
        private void ApplyEntryState(TutorialCueAnimPlayer anim, string entryCueId)
        {
            var scratchHost = new GameObject("EntryStateHost");
            scratchHost.transform.SetParent(transform, false);
            var scratch = scratchHost.AddComponent<TutorialCueAnimPlayer>();
            scratch.animationEnabled = true;

            try
            {
                if (string.IsNullOrEmpty(entryCueId))
                {
                    // 入口 = 牌桌初始态
                    scratch.LoadInitialOnly(gameRoot);
                }
                else
                {
                    // 沿「祖先链」收集：entry → 它的 entry → …… → initial
                    var chain = new List<string>();
                    var guard = 0;
                    for (string id = entryCueId; !string.IsNullOrEmpty(id) && guard++ < 256; )
                    {
                        chain.Insert(0, id);
                        int idx = IndexOfCue(id);
                        if (idx < 0) { Debug.LogWarning($"[TutorialCuePlayer] entry 指向未知 cue '{id}'"); break; }
                        string declared = string.IsNullOrWhiteSpace(doc.cues[idx].entry) ? null : doc.cues[idx].entry.Trim();
                        if (!string.IsNullOrEmpty(declared))
                            id = declared == "initial" ? null : declared;
                        else
                        {
                            id = null;
                            for (int i = idx - 1; i >= 0; i--)
                                if (HasAnimation(doc.cues[i].id)) { id = doc.cues[i].id; break; }
                        }
                    }

                    bool first = true;
                    foreach (var id in chain)
                    {
                        bool applied;
                        try { applied = scratch.LoadCue(gameRoot, track, id, !first); }
                        catch (System.Exception e)
                        {
                            Debug.LogError($"[TutorialCuePlayer] 解入口状态时异常 cue={id}: {e}");
                            applied = false;
                        }
                        if (!applied) continue;
                        // 推到终态：SnapTo 会先恢复入口快照，累积不起来，所以用 Seek。
                        scratch.Seek(scratch.TotalDuration + 1f);
                        first = false;
                    }
                }

                // 交接后，盒面状态由草稿的结果决定（草稿从根开始重放了整条链）。
                // 顺序很重要：AdoptStateFrom 会 ClearActors 并重建画面，
                // 若在它之前应用根画面，盒面会被这次重建销毁。
                bool boxVisible = scratch.BoxVisibleForTest;
                string boxPic = scratch.BoxPictureForTest;
                anim.AdoptStateFrom(scratch, gameRoot);
                anim.ApplyPictureForTest(boxVisible, boxPic);

                // 入口状态诊断：市场/三个牌堆的数量与「可见张数」。
                // 「数量对但看不见」和「数量就不对」是两类完全不同的 bug，
                // 必须分开看，否则只能靠肉眼猜。
                int visibleMarket = 0;
                foreach (var it in anim.Store.Items)
                    if (it.ZoneId == "card_market" && it.Actor != null &&
                        it.Actor.Renderer != null && it.Actor.Renderer.enabled && it.Actor.LiveAlpha > 0.05f)
                        visibleMarket++;
                Debug.Log($"[Entry] 入口={(entryCueId ?? "初始态")} " +
                          $"market={anim.Store.CountInZone("card_market")}(可见 {visibleMarket}) " +
                          $"deck1={anim.Store.CountInZone("deck_level_1")} " +
                          $"deck2={anim.Store.CountInZone("deck_level_2")} " +
                          $"deck3={anim.Store.CountInZone("deck_level_3")} " +
                          $"总={anim.ActorCount}");
            }
            finally
            {
                Object.DestroyImmediate(scratchHost);
            }
        }

        private int IndexOfCue(string cueId)
        {
            if (doc?.cues == null) return -1;
            for (int i = 0; i < doc.cues.Count; i++) if (doc.cues[i].id == cueId) return i;
            return -1;
        }

        private IEnumerator PlayCueRoutine(int index, bool continueState)
        {
            if (audioSource.isPlaying) audioSource.Stop();
            audioSource.clip = null;
            currentIndex = index;
            isPaused = false;
            currentSubtitle = "";

            var cue = doc.cues[index];
            int previous = previousIndex;   // 先记住上一条：下面的「是否顺序播放」要用它判断

            // 动画在音频加载前就复位：重播/切 cue 时画面从头开始。
            if (animPlayer != null)
            {
                animPlayer.animationEnabled = enableCueAnimation;
                fallbackClock = 0f;   // 每条 cue 重置降级时钟，避免把它累积成「已经播完」
                // 入口状态**总是**按 cue 声明的 entry 解出来，不再有「顺序播放就沿用当前画面」
                // 的快捷路径。那条捷径会让两条路得到不同画面：按右跳转到某条 cue 时从根解
                // （树根画面/盒面会出现或消失），而顺序播到同一条却保留了上一条的画面。
                // 用户就是这么发现的：按右盒面消失、往回两次又出现。
                //
                // 入口状态从根开始解，正是「可以抽一张 / 不可以抽两张」能做兄弟的原因 ——
                // 两条 cue 声明同一个 entry，各自从同一张桌子出发。
                if (animPlayer != null)
                {
                    ApplyEntryState(animPlayer, ResolveEntryCueId(index));
                }
                bool continueFromPrevious = continueState && index == previous + 1;

                // 注意：LoadCue 必须无条件调用。曾经写成 `if (showZoneLabels && LoadCue(...))`，
                // 而 showZoneLabels 默认 false —— 短路导致动画永远不载入：
                // 音频照常播放、画面全空、Console 一条日志都没有。
                bool loaded;
                try
                {
                    loaded = animPlayer.LoadCue(gameRoot, track, cue.id, continueFromPrevious);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[TutorialCuePlayer] 载入动画异常 cue={cue.id}: {e}");
                    loaded = false;
                }

                if (loaded && showZoneLabels) RefreshZoneLabels();
                else if (!loaded) zoneLabels.Clear();
            }

            string audioPath = Path.Combine(gameRoot, cue.audio);
            string uri = FilePathToUri(audioPath);

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG))
            {
                request.timeout = 60;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[TutorialCuePlayer] load audio failed: {cue.id} {request.error} ({audioPath})");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    Debug.LogError($"[TutorialCuePlayer] empty audio clip: {cue.id}");
                    yield break;
                }

                audioSource.clip = clip;
                audioSource.time = 0f;
                audioSource.Play();
            }

            UpdateSubtitle();
            while (audioSource != null && (isPaused || audioSource.isPlaying))
            {
                if (!isPaused) UpdateSubtitle();
                yield return null;
            }

            currentSubtitle = "";

            // 本条播完，才把它记为「上一条」——下一条据此判断能否沿用当前画面。
            previousIndex = index;

            if (pauseAtCueEnd) yield break;

            if (autoAdvance && index + 1 < doc.cues.Count)
            {
                // 先把本条动画推到终态，下一条才能在正确的牌桌状态上接续。
                if (animPlayer != null && animPlayer.IsLoaded) animPlayer.Complete();
                PlayCue(index + 1, true);
            }
        }

        public void ReplayCurrent()
        {
            if (currentIndex >= 0) PlayCue(currentIndex);
        }

        public void Next()
        {
            if (doc == null || doc.cues == null) return;
            if (animPlayer != null && animPlayer.IsLoaded) animPlayer.Complete();
            PlayCue(currentIndex + 1, true);
        }

        public void Previous()
        {
            if (doc == null || doc.cues == null) return;
            PlayCue(currentIndex - 1);
        }

        public void TogglePause()
        {
            if (audioSource == null || audioSource.clip == null) return;
            if (isPaused)
            {
                audioSource.UnPause();
                isPaused = false;
                TutorialPrimitives.Paused = false;
            }
            else if (audioSource.isPlaying)
            {
                audioSource.Pause();
                isPaused = true;
                TutorialPrimitives.Paused = true;   // 动画一起冻结
                UpdateSubtitle();
            }
        }

        public bool JumpToCue(string cueId)
        {
            for (int i = 0; i < doc.cues.Count; i++)
            {
                if (doc.cues[i].id == cueId)
                {
                    PlayCue(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 调试用：一键在当前 cue 与 debugJumpCueId 之间来回跳，
        /// 免得每次都要等前面 100 多条 cue 播完才能看到目标动画。
        /// </summary>
        public void ToggleDebugJump()
        {
            if (doc == null || doc.cues == null || string.IsNullOrEmpty(debugJumpCueId)) return;

            if (inDebugJump)
            {
                inDebugJump = false;
                PlayCue(debugJumpReturnIndex);
                return;
            }

            for (int i = 0; i < doc.cues.Count; i++)
            {
                if (doc.cues[i].id == debugJumpCueId)
                {
                    debugJumpReturnIndex = currentIndex;
                    inDebugJump = true;
                    PlayCue(i);
                    return;
                }
            }
            Debug.LogWarning($"[TutorialCuePlayer] debugJumpCueId not found: {debugJumpCueId}");
        }

        private void Update()
        {
            // 动画时钟 = 音频时间。暂停时音频时间不再前进，动画自动冻结。
            // 音频尚未就绪（clip 为空）时退回本地计时：否则动画会永远停在 0，
            // 表现为「音频在放、画面什么都没有」——这正是之前排查很久的现象。
            if (animPlayer != null && animPlayer.IsLoaded)
            {
                if (audioSource != null && audioSource.clip != null)
                {
                    animPlayer.Seek(audioSource.time);
                }
                else if (!isPaused)
                {
                    fallbackClock += Time.deltaTime;
                    animPlayer.Seek(fallbackClock);
                }
            }

#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.spaceKey.wasPressedThisFrame) TogglePause();
            if (kb.rKey.wasPressedThisFrame) ReplayCurrent();
            if (kb.leftArrowKey.wasPressedThisFrame) Previous();
            if (kb.rightArrowKey.wasPressedThisFrame) Next();
            if (kb.aKey.wasPressedThisFrame) autoAdvance = !autoAdvance;
            if (kb.gKey.wasPressedThisFrame) ToggleCueAnimation();
            if (kb.bKey.wasPressedThisFrame) ToggleDebugJump();
#else
            if (Input.GetKeyDown(KeyCode.Space)) TogglePause();
            if (Input.GetKeyDown(KeyCode.R)) ReplayCurrent();
            if (Input.GetKeyDown(KeyCode.LeftArrow)) Previous();
            if (Input.GetKeyDown(KeyCode.RightArrow)) Next();
            if (Input.GetKeyDown(KeyCode.A)) autoAdvance = !autoAdvance;
            if (Input.GetKeyDown(KeyCode.G)) ToggleCueAnimation();
            if (Input.GetKeyDown(KeyCode.B)) ToggleDebugJump();
#endif
        }

        public void ToggleCueAnimation()
        {
            enableCueAnimation = !enableCueAnimation;
            if (animPlayer != null)
            {
                animPlayer.animationEnabled = enableCueAnimation;
                if (!enableCueAnimation) animPlayer.ClearScene();
            }
            ReplayCurrent();
        }

        private void UpdateSubtitle()
        {
            currentSubtitle = "";
            var cue = CurrentCue;
            if (cue == null || audioSource == null || cue.subtitles == null) return;

            float time = audioSource.time;
            foreach (var subtitle in cue.subtitles)
            {
                if (time >= subtitle.t && time <= subtitle.end)
                {
                    currentSubtitle = subtitle.text;
                    return;
                }
            }
        }

        /// <summary>把当前 cue 的可见分区映射成画面上的小标注（纯调试，便于截图定位）。</summary>
        private void RefreshZoneLabels()
        {
            zoneLabels.Clear();
            if (animPlayer == null || !animPlayer.IsLoaded) return;

            foreach (var zone in ZoneDefinitions)
            {
                zoneLabels.Add(new ZoneLabel
                {
                    text = zone.label,
                    colorHex = zone.colorHex,
                    world = new Vector3(zone.x, 0f, zone.z),
                });
            }
        }

        private struct ZoneDef
        {
            public string zone;
            public string label;
            public string colorHex;
            public float x;
            public float z;
        }

        private static readonly ZoneDef[] ZoneDefinitions =
        {
            new ZoneDef { zone = "supply",  label = "SUPPLY 供应区",  colorHex = "#4E6E96", x = -0.95f, z = 1.30f },
            new ZoneDef { zone = "holding", label = "PLAYER 持有区", colorHex = "#3E7A56", x = 0.95f,  z = -0.60f },
        };

        private static string FilePathToUri(string path)
        {
            if (path.Contains("://")) return path;
            return new System.Uri(path).AbsoluteUri;
        }

        private Texture2D SwatchTexture()
        {
            if (swatchTexture == null)
            {
                swatchTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                swatchTexture.SetPixel(0, 0, Color.white);
                swatchTexture.Apply();
            }
            return swatchTexture;
        }

        private void OnGUI()
        {
            if (!showDebugUI || doc == null) return;
            if (debugStyle == null)
            {
                debugStyle = new GUIStyle(GUI.skin.label);
                debugStyle.wordWrap = true;
                debugStyle.fontSize = 16;
                debugStyle.normal.textColor = Color.white;
            }

            GUI.Box(new Rect(10, 10, Screen.width - 20, 214), doc.title ?? "Tutorial");
            GUI.Label(new Rect(24, 28, Screen.width - 48, 24), $"cue {currentIndex + 1}/{doc.cues.Count}  {CurrentCueId}", debugStyle);
            GUI.Label(new Rect(24, 52, Screen.width - 48, 24), CurrentCueGroupPath, debugStyle);
            GUI.Label(new Rect(24, 76, Screen.width - 48, 28), currentSubtitle, debugStyle);
            GUI.Label(new Rect(24, 108, Screen.width - 48, 56), CurrentCueText, debugStyle);

            string animInfo = "anim: -";
            if (animPlayer != null)
            {
                // 没有 clip 时访问 AudioSource.time 会在 Console 刷警告并永远返回 0，必须先判。
                float t = (audioSource != null && audioSource.clip != null) ? audioSource.time : 0f;
                animInfo = animPlayer.IsLoaded
                    ? $"anim: ON  {animPlayer.CueId}  t={t:0.00}s  动画总长 {animPlayer.TotalDuration:0.00}s"
                    : $"anim: none  (t={t:0.00}s)";
                if (inDebugJump) animInfo += "   [B 返回]";
            }
            GUI.Label(new Rect(24, 150, Screen.width - 48, 24), animInfo, debugStyle);

            // 动画开关状态必须一眼可见：关掉时画面会完全空白，容易被误认为坏掉。
            string animSwitch = enableCueAnimation ? "动画开关: 开" : "动画开关: 关 —— 按 G 打开（现在画面是空的）";
            var switchStyle = new GUIStyle(debugStyle);
            switchStyle.normal.textColor = enableCueAnimation ? Color.white : new Color(1f, 0.5f, 0.4f);
            GUI.Label(new Rect(24, 172, Screen.width - 48, 24), animSwitch, switchStyle);

            GUI.Label(new Rect(24, 194, Screen.width - 48, 24),
                "Space 暂停/继续  R 重播  ← 上一段  → 下一段  A 自动播放  G 动画开关  B 跳到动画切片", debugStyle);

            DrawZoneLabels();
        }

        private void DrawZoneLabels()
        {
            if (!showZoneLabels || zoneLabels.Count == 0 || animPlayer == null) return;
            if (debugStyle == null) return;

            var small = new GUIStyle(debugStyle) { fontSize = 14 };

            foreach (var zone in zoneLabels)
            {
                Vector3 screen;
                if (!animPlayer.WorldToScreen(zone.world, out screen)) continue;

                const float w = 150f;
                const float h = 26f;
                var box = new Rect(screen.x - w * 0.5f, screen.y - h * 0.5f, w, h);

                Color bg;
                var previous = GUI.backgroundColor;
                if (ColorUtility.TryParseHtmlString(zone.colorHex, out bg)) GUI.backgroundColor = bg;
                GUI.Box(box, GUIContent.none);
                GUI.backgroundColor = previous;
                GUI.Label(box, zone.text, small);
            }
        }
    }
}
