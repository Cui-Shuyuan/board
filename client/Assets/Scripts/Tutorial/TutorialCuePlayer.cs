// BoardGameTutorial
// 音频/字幕/导航外壳（v2-only）。
//
// 动画本身由 BoardGameTutorial.Animation.TutorialAnimPlayer 提供：
// 运行时只读 {track}.compiled.json，按 audioSource.time 调 Seek(t)。
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BoardGameTutorial.Animation;
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
        public bool enableCueAnimation = true;
        public bool pauseAtCueEnd;

        [Tooltip("调试：按 B 依次跳转的 cue（循环）；Shift+B 回到原位。")]
        public List<string> debugJumpCueIds = new List<string>
        {
            "setup.gems.001.1",
            "setup.gems.003.1",
            "setup.gems.003.2",
            "setup.gems.004",
            "setup.nobles.001.2",
            "setup.cards.001.1",
            "setup.cards.002.1",
            "setup.starting_player.001.3",
            "action.cards.cost.001.1",
            "action.cards.discount.001",
            "action.purchase_reserved.002.1",
            "action.reserve.limit_hand.001.2",
            "action.nobles.source.001",
            "endgame.trigger.001",
        };

        public static bool CueModeEnabled = true;

        private TutorialCueDoc doc;
        private string gameRoot;
        private AudioSource audioSource;
        private TutorialAnimPlayer v2AnimPlayer;
        private Coroutine playbackRoutine;
        private int currentIndex = -1;
        private int previousIndex = -1;
        private int debugJumpReturnIndex = -1;
        private int debugJumpCursor;
        private bool inDebugJump;
        private bool isPaused;
        private float fallbackClock;
        private string currentSubtitle = "";
        private GUIStyle debugStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle subtitleOutlineStyle;
        private static readonly Vector2[] SubtitleOutlineOffsets =
        {
            new Vector2(-2f, -2f),
            new Vector2( 0f, -2f),
            new Vector2( 2f, -2f),
            new Vector2(-2f,  0f),
            new Vector2( 2f,  0f),
            new Vector2(-2f,  2f),
            new Vector2( 0f,  2f),
            new Vector2( 2f,  2f),
        };

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
            ? string.Join(" > ", CurrentCue.group_path) : "";
        public bool IsPlaying => audioSource != null && audioSource.isPlaying;
        public TutorialCueDoc Document => doc;
        public TutorialAnimPlayer AnimPlayer => v2AnimPlayer;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!CueModeEnabled) return;
            if (FindFirstObjectByType<TutorialCuePlayer>() != null) return;
            var go = new GameObject("TutorialCuePlayer");
            go.AddComponent<TutorialCuePlayer>();
        }

        private void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = false;

            v2AnimPlayer = GetComponent<TutorialAnimPlayer>();
            if (v2AnimPlayer == null) v2AnimPlayer = gameObject.AddComponent<TutorialAnimPlayer>();
        }

        private void Start()
        {
            if (autoPlay) LoadAndPlay();
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

            doc = JsonUtility.FromJson<TutorialCueDoc>(File.ReadAllText(runtimePath));
            if (doc == null || doc.cues == null || doc.cues.Count == 0)
            {
                Debug.LogError($"[TutorialCuePlayer] failed to parse {runtimePath}");
                return false;
            }
            NormalizeDoc();

            if (!v2AnimPlayer.LoadTrack(gameRoot, track))
            {
                Debug.LogError($"[TutorialCuePlayer] missing compiled animation for track={track}");
                return false;
            }
            v2AnimPlayer.animationEnabled = enableCueAnimation;
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
                    if (subtitle.words == null) subtitle.words = new List<TutorialCueWord>();
            }
        }

        public void PlayCue(int index, bool continueState = false)
        {
            if (doc == null || doc.cues == null || doc.cues.Count == 0) return;
            index = Mathf.Clamp(index, 0, doc.cues.Count - 1);
            if (playbackRoutine != null) StopCoroutine(playbackRoutine);
            playbackRoutine = StartCoroutine(PlayCueRoutine(index, continueState));
        }

        private IEnumerator PlayCueRoutine(int index, bool continueState)
        {
            if (audioSource.isPlaying) audioSource.Stop();
            audioSource.clip = null;
            currentIndex = index;
            isPaused = false;
            currentSubtitle = "";
            fallbackClock = 0f;

            var cue = doc.cues[index];
            if (!v2AnimPlayer.LoadCue(cue.id))
                Debug.LogError($"[TutorialCuePlayer] v2 LoadCue failed: {cue.id}");
            v2AnimPlayer.animationEnabled = enableCueAnimation;

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
            previousIndex = index;

            if (pauseAtCueEnd) yield break;
            if (autoAdvance && index + 1 < doc.cues.Count)
            {
                v2AnimPlayer.Complete();
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
            v2AnimPlayer.Complete();
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
            if (doc?.cues == null) return false;
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

        public void ToggleDebugJump()
        {
            if (doc == null || doc.cues == null || debugJumpCueIds == null || debugJumpCueIds.Count == 0) return;
            if (!inDebugJump) debugJumpReturnIndex = currentIndex;
            string want = debugJumpCueIds[debugJumpCursor % debugJumpCueIds.Count];
            debugJumpCursor = (debugJumpCursor + 1) % debugJumpCueIds.Count;
            for (int i = 0; i < doc.cues.Count; i++)
            {
                if (doc.cues[i].id == want)
                {
                    inDebugJump = true;
                    PlayCue(i);
                    Debug.Log($"[TutorialCuePlayer] 调试跳转 → {want}（再按 B 看下一条，Shift+B 回原位）");
                    return;
                }
            }
            Debug.LogWarning($"[TutorialCuePlayer] 调试跳转目标不在轨道里: {want}");
        }

        private void Update()
        {
            if (v2AnimPlayer != null && v2AnimPlayer.IsLoaded)
            {
                float t = audioSource != null && audioSource.clip != null ? audioSource.time : fallbackClock;
                if (!isPaused && (audioSource == null || audioSource.clip == null))
                    fallbackClock += Time.deltaTime;
                v2AnimPlayer.Seek(t);
            }

            // 音频播放期间每帧刷新字幕；不依赖协程 while 的 isPlaying 时序。
            if (audioSource != null && audioSource.clip != null && audioSource.isPlaying && !isPaused)
                UpdateSubtitle();

#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.spaceKey.wasPressedThisFrame) TogglePause();
            if (kb.rKey.wasPressedThisFrame) ReplayCurrent();
            if (kb.leftArrowKey.wasPressedThisFrame) Previous();
            if (kb.rightArrowKey.wasPressedThisFrame) Next();
            if (kb.aKey.wasPressedThisFrame) autoAdvance = !autoAdvance;
            if (kb.gKey.wasPressedThisFrame) ToggleCueAnimation();
            if (kb.bKey.wasPressedThisFrame && kb.shiftKey.isPressed) { inDebugJump = false; PlayCue(debugJumpReturnIndex); }
            else if (kb.bKey.wasPressedThisFrame) ToggleDebugJump();
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
            if (v2AnimPlayer != null)
            {
                v2AnimPlayer.animationEnabled = enableCueAnimation;
                if (!enableCueAnimation) v2AnimPlayer.ClearScene();
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
                if (time >= subtitle.t && time <= subtitle.end) { currentSubtitle = subtitle.text; return; }
        }

        private static string FilePathToUri(string path)
        {
            if (path.Contains("://")) return path;
            return new System.Uri(path).AbsoluteUri;
        }

        private void OnGUI()
        {
            if (doc == null) return;

            DrawSubtitle();

            // 左上角信息只在 zone debug 模式下显示；正常播放时屏幕底部只有字幕。
            bool zoneDebugVisible = v2AnimPlayer != null && v2AnimPlayer.debugZones;
            if (!showDebugUI || !zoneDebugVisible) return;

            if (debugStyle == null)
            {
                debugStyle = new GUIStyle(GUI.skin.label)
                {
                    wordWrap = true,
                    fontSize = 16,
                    normal = { textColor = Color.white }
                };
            }

            GUI.Box(new Rect(10, 10, Screen.width - 20, 202), doc.title ?? "Tutorial");
            GUI.Label(new Rect(24, 28, Screen.width - 48, 24), $"cue {currentIndex + 1}/{doc.cues.Count}  {CurrentCueId}", debugStyle);
            GUI.Label(new Rect(24, 52, Screen.width - 48, 24), CurrentCueGroupPath, debugStyle);
            GUI.Label(new Rect(24, 78, Screen.width - 48, 56), CurrentCueText, debugStyle);

            float t = (audioSource != null && audioSource.clip != null) ? audioSource.time : 0f;
            string animInfo = v2AnimPlayer != null && v2AnimPlayer.IsLoaded
                ? $"anim: ON  {v2AnimPlayer.CueId}  t={t:0.00}s  动画总长 {v2AnimPlayer.TotalDuration:0.00}s"
                : $"anim: none  (t={t:0.00}s)";
            if (inDebugJump) animInfo += "   [B 返回]";
            GUI.Label(new Rect(24, 138, Screen.width - 48, 24), animInfo, debugStyle);

            string animSwitch = enableCueAnimation ? "动画开关: 开" : "动画开关: 关 —— 按 G 打开（现在画面是空的）";
            var switchStyle = new GUIStyle(debugStyle);
            switchStyle.normal.textColor = enableCueAnimation ? Color.white : new Color(1f, 0.5f, 0.4f);
            GUI.Label(new Rect(24, 160, Screen.width - 48, 24), animSwitch, switchStyle);
            GUI.Label(new Rect(24, 182, Screen.width - 48, 24),
                "Space 暂停/继续  R 重播  ← 上一段  → 下一段  A 自动播放  G 动画开关  B 跳到动画切片  Z 调试模式", debugStyle);
        }

        private void DrawSubtitle()
        {
            string text = SubtitleText();
            if (string.IsNullOrEmpty(text)) return;

            if (subtitleStyle == null)
            {
                subtitleStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    fontSize = 28
                };
                subtitleStyle.normal.textColor = Color.white;

                // 不要从 subtitleStyle 复制后再改色，避免 GUIStyleState 被共享。
                subtitleOutlineStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    fontSize = 28
                };
                subtitleOutlineStyle.normal.textColor = Color.black;
            }

            float width = Mathf.Min(1100f, Screen.width - 64f);
            float height = 96f;
            float x = (Screen.width - width) * 0.5f;
            float y = Screen.height - height - 28f;

            foreach (var offset in SubtitleOutlineOffsets)
                GUI.Label(new Rect(x + offset.x, y + offset.y, width, height), text, subtitleOutlineStyle);
            GUI.Label(new Rect(x, y, width, height), text, subtitleStyle);
        }

        private string SubtitleText()
        {
            if (!string.IsNullOrEmpty(currentSubtitle)) return currentSubtitle;
            var cue = CurrentCue;
            if (cue == null) return "";
            if (cue.subtitles == null || cue.subtitles.Count == 0) return cue.text ?? "";
            return "";
        }

    }
}
