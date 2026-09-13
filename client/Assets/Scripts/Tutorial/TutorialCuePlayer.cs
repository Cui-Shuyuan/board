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
        private bool isPaused;
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

        public void PlayCue(int index)
        {
            if (doc == null || doc.cues == null || doc.cues.Count == 0) return;
            index = Mathf.Clamp(index, 0, doc.cues.Count - 1);
            if (playbackRoutine != null) StopCoroutine(playbackRoutine);
            playbackRoutine = StartCoroutine(PlayCueRoutine(index));
        }

        private IEnumerator PlayCueRoutine(int index)
        {
            if (audioSource.isPlaying) audioSource.Stop();
            audioSource.clip = null;
            currentIndex = index;
            isPaused = false;
            currentSubtitle = "";

            var cue = doc.cues[index];

            // 动画在音频加载前就复位：重播/切 cue 时画面从头开始。
            if (animPlayer != null)
            {
                animPlayer.animationEnabled = enableCueAnimation;
                if (animPlayer.LoadCue(gameRoot, track, cue.id))
                {
                    RefreshZoneLabels();
                }
                else
                {
                    zoneLabels.Clear();
                }
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
            if (pauseAtCueEnd) yield break;

            if (autoAdvance && index + 1 < doc.cues.Count)
            {
                PlayCue(index + 1);
            }
        }

        public void ReplayCurrent()
        {
            if (currentIndex >= 0) PlayCue(currentIndex);
        }

        public void Next()
        {
            if (doc == null || doc.cues == null) return;
            PlayCue(currentIndex + 1);
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
            }
            else if (audioSource.isPlaying)
            {
                audioSource.Pause();
                isPaused = true;
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

        private void Update()
        {
            // 动画时钟 = 音频时间。暂停时音频时间不再前进，动画自动冻结。
            if (animPlayer != null && animPlayer.IsLoaded && audioSource != null && audioSource.clip != null)
            {
                animPlayer.Seek(audioSource.time);
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
#else
            if (Input.GetKeyDown(KeyCode.Space)) TogglePause();
            if (Input.GetKeyDown(KeyCode.R)) ReplayCurrent();
            if (Input.GetKeyDown(KeyCode.LeftArrow)) Previous();
            if (Input.GetKeyDown(KeyCode.RightArrow)) Next();
            if (Input.GetKeyDown(KeyCode.A)) autoAdvance = !autoAdvance;
            if (Input.GetKeyDown(KeyCode.G)) ToggleCueAnimation();
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

            GUI.Box(new Rect(10, 10, Screen.width - 20, 190), doc.title ?? "Tutorial");
            GUI.Label(new Rect(24, 28, Screen.width - 48, 24), $"cue {currentIndex + 1}/{doc.cues.Count}  {CurrentCueId}", debugStyle);
            GUI.Label(new Rect(24, 52, Screen.width - 48, 24), CurrentCueGroupPath, debugStyle);
            GUI.Label(new Rect(24, 76, Screen.width - 48, 28), currentSubtitle, debugStyle);
            GUI.Label(new Rect(24, 108, Screen.width - 48, 56), CurrentCueText, debugStyle);

            string animInfo = "anim: -";
            if (animPlayer != null)
            {
                float t = audioSource != null ? audioSource.time : 0f;
                animInfo = animPlayer.IsLoaded
                    ? $"anim: ON  {animPlayer.CueId}  t={t:0.00}s  动画总长 {animPlayer.TotalDuration:0.00}s"
                    : $"anim: none  (t={t:0.00}s)";
            }
            GUI.Label(new Rect(24, 150, Screen.width - 48, 24), animInfo, debugStyle);
            GUI.Label(new Rect(24, 172, Screen.width - 48, 24),
                "Space 暂停/继续  R 重播  ← 上一段  → 下一段  A 自动播放  G 动画开关", debugStyle);

            DrawZoneLabels();
        }

        private void DrawZoneLabels()
        {
            if (zoneLabels.Count == 0 || animPlayer == null) return;
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
