using System;
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

        [Tooltip("可留空。编辑器下会自动尝试仓库的 content/games 目录；打包/安卓可填 persistentDataPath 或 StreamingAssets。")]
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

        [Serializable]
        private class ActiveContentDoc
        {
            public ActiveContentEntry[] games;
        }

        [Serializable]
        private class ActiveContentEntry
        {
            public string game;
            public string version;
            public string root;
        }

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
        private bool debugOverlayRequested;
        private bool debugBuildAllowed;
        private bool isPaused;
        private bool pausedBeforePlay;
        private float fallbackClock;
        private string currentSubtitle = "";
        private GUIStyle debugStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle subtitleOutlineStyle;
        private GUIStyle overlayLabelStyle;
        private Texture2D overlayPanelTexture;
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
        public bool IsPaused => isPaused;
        public int CurrentCueIndex => currentIndex;
        public int TotalCueCount => doc != null && doc.cues != null ? doc.cues.Count : 0;
        public float Volume => audioSource != null ? audioSource.volume : 1f;
        public float Position => audioSource != null && audioSource.clip != null ? audioSource.time : 0f;
        public float Duration => audioSource != null && audioSource.clip != null ? audioSource.clip.length : 0f;
        public bool UnityTouchControlsEnabled
        {
            get
            {
                var controls = GetComponent<TutorialTouchControls>();
                return controls != null && controls.enabled;
            }
        }

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

            // 调试开关打开时，OnGUI 的 cue 信息必须显示；显式修正旧场景中被
            // 序列化为 false 的 showDebugUI。
            showDebugUI = true;

#if UNITY_ANDROID && !UNITY_EDITOR
            debugBuildAllowed = false;
#else
            debugBuildAllowed = true;
#endif
            ApplyDebugOverlay();

            // UaaL 正式客户端由原生 Compose 层提供控件；Android 构建默认不再
            // 挂载旧的 Unity IMGUI 触控层，避免出现第二套控制条。
#if UNITY_ANDROID && !UNITY_EDITOR
            SetUnityTouchControlsEnabled(false);
#else
            SetUnityTouchControlsEnabled(true);
#endif
        }

        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // UaaL native home/player owns game selection.  The Android bridge
            // calls LoadGame(gameId) only after the catalog game is selected and
            // its content has been activated in active.json.
#else
            // Editor / desktop prototype keeps the original autoPlay behavior.
            if (autoPlay) LoadAndPlay();
#endif
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

        public void ReloadGame()
        {
            StopAndClear();

            if (!LoadAndPlay())
                Debug.LogWarning("[TutorialCuePlayer] ReloadGame could not load the new content root.");
        }

        /// <summary>
        /// Stops the current coroutine/audio, clears animation state, and drops
        /// the parsed runtime document.  Native UnloadGame uses this when the
        /// user returns from the tutorial player to the home screen.
        /// </summary>
        public void StopAndClear()
        {
            if (playbackRoutine != null)
            {
                StopCoroutine(playbackRoutine);
                playbackRoutine = null;
            }
            if (audioSource != null)
            {
                audioSource.Stop();
                audioSource.clip = null;
                audioSource.time = 0f;
            }
            if (v2AnimPlayer != null)
            {
                v2AnimPlayer.ClearScene();
            }

            doc = null;
            gameRoot = null;
            currentIndex = -1;
            previousIndex = -1;
            debugJumpReturnIndex = -1;
            debugJumpCursor = 0;
            inDebugJump = false;
            isPaused = false;
            pausedBeforePlay = false;
            fallbackClock = 0f;
            currentSubtitle = "";
        }

        public bool LoadRuntime()
        {
            gameRoot = ResolveGameRoot();
            if (string.IsNullOrEmpty(gameRoot))
            {
                Debug.LogError("[TutorialCuePlayer] cannot find game root. Set tutorialRoot or put files in content/games/{game}.");
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
            ApplyDebugOverlay();
            return true;
        }

        private string ResolveGameRoot()
        {
            // 1) Explicit Inspector/bridge override.  SetContentRoot passes the
            //    version directory; gameId is still appended by this method.
            if (!string.IsNullOrEmpty(tutorialRoot))
            {
                string custom = Path.Combine(tutorialRoot, gameId);
                if (Directory.Exists(custom)) return custom;
                Debug.LogWarning($"[TutorialCuePlayer] tutorialRoot does not contain {gameId}: {tutorialRoot}");
            }

            // 2) Native content repository active pointer.
            string active = ResolveActiveGameRoot();
            if (!string.IsNullOrEmpty(active)) return active;

            // 3) Legacy manual adb push target: persistentDataPath/{gameId}.
            string persistent = Path.Combine(Application.persistentDataPath, gameId);
            if (Directory.Exists(persistent)) return persistent;

            // 4) StreamingAssets / repository fallback, useful in Editor Play.
            string streaming = Path.Combine(Application.streamingAssetsPath, gameId);
            if (Directory.Exists(streaming)) return streaming;

            string repoGames = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "content", "games"));
            string repoGame = Path.Combine(repoGames, gameId);
            if (Directory.Exists(repoGame)) return repoGame;

            return null;
        }

        private string ResolveActiveGameRoot()
        {
            try
            {
                string activePath = Path.Combine(Application.persistentDataPath, "board-content", "active.json");
                if (!File.Exists(activePath)) return null;

                var document = JsonUtility.FromJson<ActiveContentDoc>(File.ReadAllText(activePath));
                if (document == null || document.games == null) return null;

                foreach (var entry in document.games)
                {
                    if (entry == null || entry.game != gameId) continue;
                    if (string.IsNullOrEmpty(entry.root)) continue;
                    if (Directory.Exists(entry.root)) return entry.root;
                    Debug.LogWarning($"[TutorialCuePlayer] active root does not exist: {entry.root}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TutorialCuePlayer] failed to read active.json: " + ex.Message);
            }

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

        public void PlayCue(
            int index,
            bool continueState = false,
            float localTime = 0f,
            bool startPaused = false)
        {
            if (doc == null || doc.cues == null || doc.cues.Count == 0) return;
            index = Mathf.Clamp(index, 0, doc.cues.Count - 1);
            if (playbackRoutine != null) StopCoroutine(playbackRoutine);
            playbackRoutine = StartCoroutine(
                PlayCueRoutine(
                    index,
                    continueState,
                    Mathf.Max(0f, localTime),
                    startPaused));
        }

        /// <summary>
        /// Jumps to a specific cue and then seeks to a cue-local second offset.
        /// This is the route used by the native Android seek bar for cross-cue
        /// scrubbing and chapter jumps.
        /// </summary>
        public bool PlayCueAt(
            string cueId,
            float localSeconds,
            bool startPaused = false)
        {
            if (doc == null || doc.cues == null) return false;
            for (int i = 0; i < doc.cues.Count; i++)
            {
                if (doc.cues[i].id != cueId) continue;
                PlayCue(i, false, localSeconds, startPaused);
                return true;
            }
            Debug.LogWarning($"[TutorialCuePlayer] PlayCueAt target not found: {cueId}");
            return false;
        }

        private IEnumerator PlayCueRoutine(
            int index,
            bool continueState,
            float localTime,
            bool startPaused)
        {
            if (audioSource.isPlaying) audioSource.Stop();
            audioSource.clip = null;
            currentIndex = index;
            isPaused = startPaused;
            pausedBeforePlay = startPaused;
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
                float seekTime = ClampCueLocalTime(localTime, clip.length);
                audioSource.time = seekTime;
                if (v2AnimPlayer != null) v2AnimPlayer.Seek(seekTime);

                if (startPaused)
                {
                    // Keep the clip positioned but do not start playback.  The
                    // next Resume() uses Play() rather than UnPause().
                    isPaused = true;
                    pausedBeforePlay = true;
                }
                else
                {
                    isPaused = false;
                    pausedBeforePlay = false;
                    audioSource.Play();
                }
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

        /// <summary>Bridge-friendly alias for the native next-section button.</summary>
        public void NextCue()
        {
            Next();
        }

        /// <summary>Bridge-friendly alias for the native previous-section button.</summary>
        public void PreviousCue()
        {
            Previous();
        }

        public void TogglePause()
        {
            if (isPaused) Resume();
            else Pause();
        }

        public void Pause()
        {
            if (audioSource == null || audioSource.clip == null || isPaused) return;
            if (!audioSource.isPlaying) return;

            audioSource.Pause();
            isPaused = true;
            UpdateSubtitle();
        }

        public void Resume()
        {
            if (audioSource == null || audioSource.clip == null || !isPaused) return;

            if (pausedBeforePlay)
            {
                pausedBeforePlay = false;
                isPaused = false;
                audioSource.Play();
            }
            else
            {
                audioSource.UnPause();
                isPaused = false;
            }
        }

        public void SetVolume(float value)
        {
            if (audioSource == null) return;
            audioSource.volume = Mathf.Clamp01(value);
        }

        public void AdjustVolume(float delta)
        {
            if (audioSource == null) return;
            SetVolume(audioSource.volume + delta);
        }

        public void SetDebugBuildAllowed(bool allowed)
        {
            debugBuildAllowed = allowed;
            ApplyDebugOverlay();
        }

        public void SetDebugOverlay(bool enabled)
        {
            debugOverlayRequested = enabled;
            ApplyDebugOverlay();
        }

        private void ApplyDebugOverlay()
        {
            if (v2AnimPlayer == null) return;
            v2AnimPlayer.debugToggleEnabled = debugBuildAllowed;
            if (debugOverlayRequested && debugBuildAllowed) showDebugUI = true;
            v2AnimPlayer.SetDebugOverlay(debugOverlayRequested && debugBuildAllowed);
        }

        /// <summary>
        /// Enables or disables the legacy Unity IMGUI touch controls. Android
        /// builds start with this off so only the native Compose layer is visible.
        /// </summary>
        public void SetUnityTouchControlsEnabled(bool enabled)
        {
            var controls = GetComponent<TutorialTouchControls>();
            if (controls == null && enabled)
                controls = gameObject.AddComponent<TutorialTouchControls>();

            if (controls == null) return;

            controls.Bind(this);
            controls.enabled = enabled;
        }

        public void SeekRelative(float seconds)
        {
            if (audioSource == null || audioSource.clip == null) return;

            float duration = audioSource.clip.length;
            if (duration <= 0f && CurrentCue != null) duration = CurrentCue.duration;
            float target = audioSource.time + seconds;

            if (target < 0f)
            {
                // 负方向越过 cue 开头：优先跳上一条；第一条则停在 0。
                if (doc != null && doc.cues != null && currentIndex > 0)
                    PlayCue(currentIndex - 1, true);
                else
                    SeekToCurrentTime(0f);
                return;
            }

            if (duration > 0f && target > duration)
            {
                // 正方向越过 cue 末尾：优先跳下一条；最后一条则停在末尾附近。
                if (doc != null && doc.cues != null && currentIndex + 1 < doc.cues.Count)
                    PlayCue(currentIndex + 1, true);
                else
                    SeekToCurrentTime(Mathf.Max(0f, duration - 0.05f));
                return;
            }

            SeekToCurrentTime(Mathf.Clamp(target, 0f, duration));
        }

        /// <summary>
        /// Absolute cue-local seek used while scrubbing within the current cue.
        /// If the clip has not loaded yet, restart the cue and position it after
        /// loading instead of silently dropping the seek.
        /// </summary>
        public void SeekTo(float localSeconds)
        {
            if (audioSource == null || audioSource.clip == null)
            {
                if (CurrentCue != null)
                    PlayCueAt(CurrentCue.id, localSeconds, isPaused);
                return;
            }

            SeekToCurrentTime(localSeconds);
        }

        private void SeekToCurrentTime(float time)
        {
            if (audioSource == null || audioSource.clip == null) return;
            float duration = Mathf.Max(0f, audioSource.clip.length);
            float clamped = ClampCueLocalTime(time, duration);
            audioSource.time = clamped;
            if (v2AnimPlayer != null) v2AnimPlayer.Seek(clamped);
            UpdateSubtitle();
        }

        private static float ClampCueLocalTime(float localSeconds, float clipLength)
        {
            float duration = Mathf.Max(0f, clipLength);
            if (duration <= 0f) return 0f;
            // Unity audio playback cannot reliably start exactly at clip.length.
            // Keep end-seeks just inside the clip; the normal auto-advance then
            // carries playback to the following cue.
            return Mathf.Clamp(localSeconds, 0f, Mathf.Max(0f, duration - 0.02f));
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
#if UNITY_ANDROID
            if (AndroidBackPressed())
            {
                Debug.Log("[TutorialCuePlayer] Android back -> native home request");
                AndroidTutorialBridge.NotifyNativeBack();
                return;
            }
#endif

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

#if UNITY_ANDROID
        private static bool AndroidBackPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                return true;
#endif
            // Android KEYCODE_BACK 在部分 Unity / Input System 组合下不会走 Keyboard.escapeKey，
            // 但旧 Input 通道仍能看到它。
            try
            {
                return Input.GetKeyDown(KeyCode.Escape);
            }
            catch (System.Exception)
            {
                return false;
            }
        }
#endif

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
            var frame = v2AnimPlayer != null ? v2AnimPlayer.CurrentFrame : null;
            DrawOverlayLabels(frame);
            DrawScreenOverlays(frame);

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

        private Texture2D GetOverlayPanelTexture()
        {
            if (overlayPanelTexture != null) return overlayPanelTexture;
            overlayPanelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            overlayPanelTexture.SetPixel(0, 0, Color.white);
            overlayPanelTexture.Apply();
            return overlayPanelTexture;
        }

        private void DrawScreenOverlays(FrameState frame)
        {
            if (frame == null || frame.Overlays == null || frame.Overlays.Count == 0) return;
            if (v2AnimPlayer == null || !v2AnimPlayer.IsLoaded) return;

            var ordered = new List<VisualOverlayState>(frame.Overlays);
            ordered.Sort((a, b) => a.Layer.CompareTo(b.Layer));

            var panel = GetOverlayPanelTexture();
            var savedColor = GUI.color;
            foreach (var overlay in ordered)
            {
                if (overlay == null || overlay.Alpha <= 0.001f) continue;

                float baseX = overlay.X * Screen.width;
                float baseY = overlay.Y * Screen.height;
                float baseW = Mathf.Max(1f, overlay.W * Screen.width);
                float baseH = Mathf.Max(1f, overlay.H * Screen.height);

                // scale/highlight are part of the common IAnimVisualObject
                // interface; screen overlays apply them around their rect center.
                float zoom = Mathf.Max(0.05f, overlay.Scale);
                if (overlay.Highlighted) zoom *= Mathf.Max(1f, overlay.HighlightGrow);
                float w = Mathf.Max(1f, baseW * zoom);
                float h = Mathf.Max(1f, baseH * zoom);
                float x = baseX + (baseW - w) * 0.5f;
                float y = baseY + (baseH - h) * 0.5f;
                var panelRect = new Rect(x, y, w, h);

                // A panel is drawn only when the data explicitly asks for a
                // background.  The default filled panel made every card carry
                // a visible rectangle; a screen-space card should be just the
                // cutout sprite.
                if (!string.IsNullOrEmpty(overlay.Background)
                    && Palette.TryResolveRgb(overlay.Background, out var parsed))
                {
                    // Project is Linear; OnGUI consumes this color as a linear
                    // value while the authored hex is sRGB.  Use .linear so
                    // scene_backdrop renders with exactly the stage/table
                    // color instead of a lighter conversion.
                    Color panelColor = parsed.linear;
                    panelColor.a = overlay.Alpha;
                    GUI.color = panelColor;
                    GUI.DrawTexture(panelRect, panel);
                }

                var sprite = v2AnimPlayer.LoadOverlaySprite(overlay);
                var cardRect = panelRect;
                if (sprite != null && sprite.texture != null)
                {
                    float inset = Mathf.Min(w, h) * 0.06f;
                    float availW = Mathf.Max(1f, w - inset * 2f);
                    float availH = Mathf.Max(1f, h - inset * 2f);
                    float srcW = Mathf.Max(1f, sprite.rect.width);
                    float srcH = Mathf.Max(1f, sprite.rect.height);
                    float k = Mathf.Min(availW / srcW, availH / srcH);
                    float cardW = srcW * k;
                    float cardH = srcH * k;
                    cardRect = new Rect(
                        x + (w - cardW) * 0.5f,
                        y + (h - cardH) * 0.5f,
                        cardW,
                        cardH);

                    GUI.color = new Color(1f, 1f, 1f, overlay.Alpha);
                    GUI.DrawTexture(cardRect, sprite.texture);
                }

                // Highlight is the same grow/breath animation as the entity
                // implementation (VisualClipPlayer), already folded into zoom
                // above.  Do not draw a border here: a rectangle outline around
                // the overlay rect reads as an extra object following the card.
                if (!string.IsNullOrEmpty(overlay.Indicator) || !string.IsNullOrEmpty(overlay.PointPart))
                {
                    float u = 0.5f;
                    float v = 0.5f;
                    switch (overlay.PointPart)
                    {
                        case "prestige": u = 0.16f; v = 0.16f; break;
                        case "cost": u = 0.16f; v = 0.84f; break;
                        case "bonus": u = 0.84f; v = 0.16f; break;
                        case "condition": u = 0.50f; v = 0.84f; break;
                    }
                    float markerSize = Mathf.Max(28f, Mathf.Min(cardRect.width, cardRect.height) * 0.20f);
                    var markerRect = new Rect(
                        cardRect.x + cardRect.width * u - markerSize * 0.5f,
                        cardRect.y + cardRect.height * v - markerSize * 0.5f,
                        markerSize,
                        markerSize);
                    string markerKind = string.IsNullOrEmpty(overlay.Indicator)
                        ? "circle" : overlay.Indicator;
                    var markerSprite = ActorBinder.GetMarkerSprite(markerKind);
                    if (markerSprite != null && markerSprite.texture != null)
                    {
                        GUI.color = new Color(0.92f, 0.24f, 0.20f, overlay.Alpha);
                        GUI.DrawTexture(markerRect, markerSprite.texture);
                    }
                }
                GUI.color = savedColor;
            }
            GUI.color = savedColor;
        }

        private void DrawOverlayLabels(FrameState frame)
        {
            if (frame == null || frame.Labels == null || frame.Labels.Count == 0) return;
            if (overlayLabelStyle == null)
            {
                overlayLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = true,
                    fontSize = 34,
                    normal = { textColor = Color.white }
                };
            }
            foreach (var label in frame.Labels)
            {
                if (label == null || !label.ScreenSpace || string.IsNullOrEmpty(label.Text)) continue;
                var rect = new Rect(
                    label.X * Screen.width,
                    label.Y * Screen.height,
                    Mathf.Max(40f, label.W * Screen.width),
                    Mathf.Max(28f, label.H * Screen.height));
                GUI.Box(rect, GUIContent.none);
                GUI.Label(rect, label.Text, overlayLabelStyle);
            }
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
                    fontSize = 34
                };
                subtitleStyle.normal.textColor = Color.white;

                // 不要从 subtitleStyle 复制后再改色，避免 GUIStyleState 被共享。
                subtitleOutlineStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    fontSize = 34
                };
                subtitleOutlineStyle.normal.textColor = Color.black;
            }

            float width = Mathf.Min(1100f, Screen.width - 64f);
            float height = 120f;
            float x = (Screen.width - width) * 0.5f;
            float bottomInset = 28f;
            var touchControls = GetComponent<TutorialTouchControls>();
            if (touchControls != null && touchControls.enabled)
                bottomInset += touchControls.PanelHeightPixels;
            float y = Screen.height - height - bottomInset;

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
