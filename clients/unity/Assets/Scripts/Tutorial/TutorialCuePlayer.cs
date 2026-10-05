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

        private sealed class MagnifierView
        {
            public GameObject Go;
            public Camera Cam;
            public RenderTexture Rt;
            public int Width;
            public int Height;
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
        private GUIStyle labelShadowStyle;
        private Texture2D overlayPanelTexture;
        private Texture2D magnifierMaskTexture;
        private Texture2D magnifierDiskTexture;
        private Texture2D magnifierFrameTexture;
        private readonly Dictionary<string, MagnifierView> magnifierViews = new Dictionary<string, MagnifierView>(StringComparer.Ordinal);
        private int magnifierRenderedFrame = -1;
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

            // OnGUI 的 cue 信息由 showDebugUI 控制；这里强制打开，避免场景里
            // 被序列化成 false。
            showDebugUI = true;

#if UNITY_ANDROID && !UNITY_EDITOR
            debugBuildAllowed = false;
#else
            debugBuildAllowed = true;
#endif
            ApplyDebugOverlay();

            // Android 上控件由原生 Compose 层提供；不挂 Unity IMGUI 触控层，
            // 避免出现第二套控制条。
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
        /// Enables or disables the Editor/dev Unity IMGUI touch controls.
        /// Android builds keep this off so only the native Compose layer is visible.
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
            // 但 Input.GetKeyDown 仍能看到它。
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

            // Render order (bottom to top):
            //   1. world/table (camera)
            //   2. mask + screen objects (DrawScreenOverlays)
            //   3. annotation markers / arrows / circles / boxes
            //   4. explanation labels
            //   5. subtitles
            //   6. debug HUD (only in debug mode)
            var frame = v2AnimPlayer != null ? v2AnimPlayer.CurrentFrame : null;
            DrawScreenOverlays(frame);
            DrawMagnifiers(frame);
            DrawAnnotations(frame);
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

        private Texture2D GetOverlayPanelTexture()
        {
            if (overlayPanelTexture != null) return overlayPanelTexture;
            overlayPanelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            overlayPanelTexture.SetPixel(0, 0, Color.white);
            overlayPanelTexture.Apply();
            return overlayPanelTexture;
        }

        private bool TryGetOverlayRects(VisualOverlayState overlay, out Rect panelRect, out Rect cardRect)
        {
            panelRect = new Rect();
            cardRect = new Rect();
            if (overlay == null) return false;

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
            panelRect = new Rect(x, y, w, h);
            cardRect = panelRect;

            var sprite = v2AnimPlayer != null ? v2AnimPlayer.LoadOverlaySprite(overlay) : null;
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
            }
            return true;
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
                if (!TryGetOverlayRects(overlay, out var panelRect, out var cardRect)) continue;

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
                if (sprite != null && sprite.texture != null)
                {
                    GUI.color = new Color(1f, 1f, 1f, overlay.Alpha);
                    GUI.DrawTexture(cardRect, sprite.texture);
                }
                GUI.color = savedColor;
            }
            GUI.color = savedColor;
        }

        private void LateUpdate()
        {
            // Keep lens cameras enabled so URP renders them as normal cameras
            // into their RenderTextures.  Calling Camera.Render() manually
            // from OnGUI nests a URP pass inside the main camera's render
            // context (UniversalCameraData already created) and throws every
            // frame; this path must stay out of any manual render callback.
            var frame = (v2AnimPlayer != null && v2AnimPlayer.IsLoaded && enableCueAnimation)
                ? v2AnimPlayer.CurrentFrame
                : null;
            PrepareMagnifiers(frame);
        }

        private void PrepareMagnifiers(FrameState frame)
        {
            var alive = new HashSet<string>(StringComparer.Ordinal);
            var mainCam = v2AnimPlayer != null ? v2AnimPlayer.Camera : null;
            if (frame != null && frame.Magnifiers != null)
            {
                foreach (var m in frame.Magnifiers)
                {
                    if (m == null || m.Alpha <= 0.001f) continue;
                    Rect lensRect = ResolveMagnifierRect(m);
                    int texW = Mathf.Clamp(Mathf.RoundToInt(lensRect.width), 64, 1024);
                    int texH = Mathf.Clamp(Mathf.RoundToInt(lensRect.height), 64, 1024);
                    var view = GetMagnifierView(m.Id, texW, texH);
                    if (view == null || view.Cam == null) continue;

                    // Disable while mutating the camera to avoid a stale
                    // transform/size being submitted on the same frame.
                    view.Cam.enabled = false;
                    view.Cam.cullingMask = mainCam != null ? mainCam.cullingMask : ~0;
                    view.Cam.clearFlags = CameraClearFlags.SolidColor;
                    bool fullMask = MagnifierMaskMode(m) == "full";
                    bool circleLens = MagnifierShape(m) == "circle";
                    bool opaqueTable = fullMask && !circleLens;
                    view.Cam.backgroundColor = opaqueTable
                        ? (mainCam != null ? mainCam.backgroundColor : Color.black)
                        : new Color(0f, 0f, 0f, 0f);
                    view.Cam.aspect = (float)view.Width / Mathf.Max(1, view.Height);
                    view.Cam.orthographic = true;
                    view.Cam.orthographicSize = Mathf.Max(0.05f, m.OrthoSize);
                    float distance = Mathf.Max(0.1f, m.OrthoSize) * 3.2f;
                    view.Cam.transform.position = new Vector3(m.CenterX, distance, m.CenterZ);
                    view.Cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    view.Cam.targetTexture = view.Rt;

                    // LateUpdate is outside URP's render callbacks, so unlike
                    // the old OnGUI path this manual offscreen render is safe.
                    // Keeping the camera disabled prevents URP from also
                    // rendering it as a normal camera in the same frame.
                    // Only the event's matched targets may enter the lens;
                    // unrelated world objects that happen to fall in the same
                    // region (e.g. the market row under the nobles) must not
                    // bleed into an otherwise opaque lens.
                    view.Cam.enabled = false;
                    if (v2AnimPlayer != null) v2AnimPlayer.BeginLensRender(m.ItemIds);
                    try
                    {
                        view.Cam.Render();
                    }
                    finally
                    {
                        if (v2AnimPlayer != null) v2AnimPlayer.EndLensRender();
                    }
                    alive.Add(m.Id);
                }
            }

            var stale = new List<string>();
            foreach (var kv in magnifierViews)
                if (!alive.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (var id in stale)
                if (magnifierViews.TryGetValue(id, out var view) && view != null && view.Cam != null)
                    view.Cam.enabled = false;
        }

        private void DrawMagnifiers(FrameState frame)
        {
            if (frame == null || frame.Magnifiers == null || frame.Magnifiers.Count == 0)
            {
                ReleaseStaleMagnifiers(new HashSet<string>(StringComparer.Ordinal));
                return;
            }
            if (v2AnimPlayer == null || !v2AnimPlayer.IsLoaded) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (magnifierRenderedFrame == Time.frameCount) return;
            magnifierRenderedFrame = Time.frameCount;

            var ordered = new List<VisualMagnifierState>(frame.Magnifiers);
            ordered.Sort((a, b) => a.Layer.CompareTo(b.Layer));
            var alive = new HashSet<string>(StringComparer.Ordinal);
            var circleMask = GetMagnifierMaskTexture();

            foreach (var m in ordered)
            {
                if (m == null || m.Alpha <= 0.001f) continue;
                if (!magnifierViews.TryGetValue(m.Id, out var view) || view == null) continue;
                Rect rect = ResolveMagnifierRect(m);

                bool fullMask = MagnifierMaskMode(m) == "full";
                bool circleLens = MagnifierShape(m) == "circle";
                var savedColor = GUI.color;

                // Full + circle: put an opaque table-coloured disc inside the
                // lens, then draw the transparent object pass on top.  This
                // keeps the magnifier showing "that piece of table" instead
                // of revealing the live screen behind a removed noble.
                if (fullMask && circleLens)
                {
                    var mainCam = v2AnimPlayer != null ? v2AnimPlayer.Camera : null;
                    Color tableColor = mainCam != null ? mainCam.backgroundColor : Color.black;
                    // OnGUI consumes GUI.color as linear, while Camera.backgroundColor
                    // was authored from the sRGB stage hex.  Match the live table clear
                    // colour by converting before drawing the disc.
                    tableColor = tableColor.linear;
                    tableColor.a *= Mathf.Clamp01(m.Alpha);
                    GUI.color = tableColor;
                    GUI.DrawTexture(rect, GetMagnifierDiskTexture(), ScaleMode.StretchToFill, true);
                }

                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(m.Alpha));
                GUI.DrawTexture(rect, view.Rt, ScaleMode.StretchToFill, !(fullMask && !circleLens));
                if (circleLens && circleMask != null)
                    GUI.DrawTexture(rect, circleMask, ScaleMode.StretchToFill, true);
                else if (!circleLens)
                    DrawMagnifierBoxFrame(rect);
                GUI.color = savedColor;
                alive.Add(m.Id);
            }

            ReleaseStaleMagnifiers(alive);
        }

        private void ReleaseStaleMagnifiers(HashSet<string> alive)
        {
            var stale = new List<string>();
            foreach (var kv in magnifierViews)
                if (!alive.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (var id in stale)
            {
                var view = magnifierViews[id];
                if (view != null)
                {
                    if (view.Cam != null)
                    {
                        view.Cam.enabled = false;
                        view.Cam.targetTexture = null;
                    }
                    if (view.Rt != null) view.Rt.Release();
                    if (view.Go != null) Destroy(view.Go);
                }
                magnifierViews.Remove(id);
            }
        }

        private static string MagnifierMaskMode(VisualMagnifierState m)
        {
            if (m == null || string.IsNullOrEmpty(m.MaskMode)) return "items";
            return m.MaskMode.Trim().ToLowerInvariant() == "full" ? "full" : "items";
        }

        private static string MagnifierShape(VisualMagnifierState m)
        {
            if (m == null || string.IsNullOrEmpty(m.Shape)) return "circle";
            string shape = m.Shape.Trim().ToLowerInvariant();
            return shape == "box" ? "box" : "circle";
        }

        private static Rect ResolveMagnifierRect(VisualMagnifierState m)
        {
            float px = m.X * Screen.width;
            float py = m.Y * Screen.height;
            float pw = Mathf.Max(24f, m.W * Screen.width);
            float ph = Mathf.Max(24f, m.H * Screen.height);
            if (MagnifierShape(m) == "circle")
            {
                // `rect` is the lens' available screen box; a circle uses the
                // largest square inside it so the mask is never stretched.
                float side = Mathf.Min(pw, ph);
                return new Rect(px + (pw - side) * 0.5f, py + (ph - side) * 0.5f, side, side);
            }
            return new Rect(px, py, pw, ph);
        }

        private MagnifierView GetMagnifierView(string id, int width, int height)
        {
            if (string.IsNullOrEmpty(id)) id = "magnifier";
            if (!magnifierViews.TryGetValue(id, out var view) || view == null)
            {
                var go = new GameObject("TutorialMagnifier_" + id);
                go.transform.SetParent(transform, false);
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.depth = -100f;
                view = new MagnifierView
                {
                    Go = go,
                    Cam = cam,
                    Rt = CreateMagnifierTexture(width, height),
                    Width = width,
                    Height = height,
                };
                cam.targetTexture = view.Rt;
                magnifierViews[id] = view;
            }
            else if (view.Width != width || view.Height != height)
            {
                if (view.Cam != null) view.Cam.enabled = false;
                if (view.Rt != null) view.Rt.Release();
                view.Rt = CreateMagnifierTexture(width, height);
                view.Width = width;
                view.Height = height;
                if (view.Cam != null) view.Cam.targetTexture = view.Rt;
            }
            return view;
        }

        private static RenderTexture CreateMagnifierTexture(int width, int height)
        {
            // URP RenderGraph imports the output texture; it must have a depth
            // buffer and be explicitly created before a camera renders into it,
            // otherwise the device logs `Fake or uninitialized surface`.
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.Create();
            return rt;
        }

        private Texture2D GetMagnifierDiskTexture()
        {
            if (magnifierDiskTexture != null) return magnifierDiskTexture;
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    Color c = d <= 0.97f ? Color.white : new Color(0f, 0f, 0f, 0f);
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            magnifierDiskTexture = tex;
            return tex;
        }

        private void DrawMagnifierBoxFrame(Rect rect)
        {
            var frame = GetMagnifierFrameTexture();
            if (frame == null) return;
            float stroke = Mathf.Max(4f, Mathf.Min(rect.width, rect.height) * 0.045f);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, stroke), frame);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - stroke, rect.width, stroke), frame);
            GUI.DrawTexture(new Rect(rect.x, rect.y, stroke, rect.height), frame);
            GUI.DrawTexture(new Rect(rect.xMax - stroke, rect.y, stroke, rect.height), frame);
        }

        private Texture2D GetMagnifierFrameTexture()
        {
            if (magnifierFrameTexture != null) return magnifierFrameTexture;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixel(0, 0, new Color(0.95f, 0.82f, 0.36f, 1f));
            tex.Apply();
            magnifierFrameTexture = tex;
            return tex;
        }

        private Texture2D GetMagnifierMaskTexture()
        {
            if (magnifierMaskTexture != null) return magnifierMaskTexture;
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            var outside = new Color(0f, 0f, 0f, 0f);
            var rim = new Color(0.95f, 0.82f, 0.36f, 1f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    Color c;
                    if (d <= 0.90f) c = new Color(0f, 0f, 0f, 0f);
                    else if (d <= 0.97f) c = rim;
                    else c = outside;
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            magnifierMaskTexture = tex;
            return tex;
        }

        private void DrawAnnotations(FrameState frame)
        {
            if (frame == null || frame.Annotations == null || frame.Annotations.Count == 0) return;
            if (v2AnimPlayer == null || !v2AnimPlayer.IsLoaded) return;
            var cam = v2AnimPlayer.Camera;
            if (cam == null) return;

            EnsureOverlayLabelStyles();

            // Shapes first, then labels, then subtitles/debug HUD.  Both
            // annotation spaces draw in this single OnGUI pass, i.e. always
            // above the mask/screen-object layer.
            foreach (var annotation in frame.Annotations)
            {
                if (annotation == null || annotation.Kind == "label") continue;
                DrawShapeAnnotation(frame, annotation, cam);
            }
            foreach (var annotation in frame.Annotations)
            {
                if (annotation == null || annotation.Kind != "label") continue;
                DrawLabelAnnotation(frame, annotation, cam);
            }
        }

        private void DrawShapeAnnotation(FrameState frame, VisualAnnotationState annotation, Camera cam)
        {
            if (annotation == null) return;
            float ppu = Screen.height / Mathf.Max(0.001f, 2f * cam.orthographicSize);
            float uiScale = UiScale();
            float strokePx = ResolvedStrokePx(annotation);
            Color color = ResolvedAnnotationColor(annotation);
            bool world = annotation.Space == "world";

            if (world)
            {
                var screen = cam.WorldToScreenPoint(new Vector3(annotation.X, 0f, annotation.Z));
                if (screen.z < 0f) return;
                float cx = screen.x + annotation.NudgeX * Screen.width;
                float cy = (Screen.height - screen.y) + annotation.NudgeY * Screen.height;
                if (annotation.Kind == "box")
                {
                    float w = Mathf.Max(8f, annotation.W * ppu);
                    float h = Mathf.Max(8f, annotation.H * ppu);
                    DrawBoxOutline(new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h), strokePx, color);
                    return;
                }
                bool worldArrow = annotation.Kind == "arrow";
                bool hasPartSize = annotation.PartW > 0f && annotation.PartH > 0f;
                float partPx = hasPartSize ? Mathf.Max(annotation.W, annotation.H) * ppu : 0f;
                float fallbackPx = annotation.Radius * 2f * ppu;
                float baseSize = partPx > 0f ? partPx : fallbackPx;
                float autoSize = worldArrow
                    ? Mathf.Max(48f, baseSize * 1.35f)
                    : Mathf.Max(24f, baseSize);
                float size = ResolvedSizePx(annotation, autoSize);
                float strokeNorm = size > 0f ? strokePx / size : 0.075f;
                if (worldArrow)
                {
                    const float tipU = 0.91f;
                    float tipX = cx - ResolvedGapPx(annotation, partPx);
                    DrawMarkerSprite(annotation.Kind,
                        new Rect(tipX - size * tipU, cy - size * 0.5f, size, size),
                        strokeNorm, color);
                }
                else
                {
                    DrawMarkerSprite(annotation.Kind,
                        new Rect(cx - size * 0.5f, cy - size * 0.5f, size, size),
                        strokeNorm, color);
                }
                return;
            }

            // Screen annotation: resolve the live overlay rect first, then the
            // compiler-baked fallback rect (for static screen anchors).
            Rect rect;
            if (!TryGetAnnotationScreenRect(frame, annotation, out rect)) return;
            if (annotation.Kind == "box")
            {
                Rect boxRect = rect;
                if (annotation.PartW > 0f && annotation.PartH > 0f)
                {
                    float bw = annotation.PartW * rect.width;
                    float bh = annotation.PartH * rect.height;
                    float bx = rect.x + rect.width * annotation.PartU + annotation.NudgeX * Screen.width;
                    float by = rect.y + rect.height * annotation.PartV + annotation.NudgeY * Screen.height;
                    boxRect = new Rect(bx - bw * 0.5f, by - bh * 0.5f, bw, bh);
                }
                DrawBoxOutline(boxRect, strokePx, color);
                return;
            }
            bool screenArrow = annotation.Kind == "arrow";
            bool screenHasPartSize = annotation.PartW > 0f && annotation.PartH > 0f;
            float screenPartPx = screenHasPartSize
                ? Mathf.Max(annotation.PartW * rect.width, annotation.PartH * rect.height)
                : 0f;
            float screenFallbackPx = Mathf.Min(rect.width, rect.height) * (screenArrow ? 0.32f : 0.20f);
            float screenBaseSize = screenPartPx > 0f ? screenPartPx : screenFallbackPx;
            float screenAutoSize = screenArrow
                ? Mathf.Max(96f, screenBaseSize * 1.35f)
                : Mathf.Max(28f, screenBaseSize);
            float screenMarkerSize = ResolvedSizePx(annotation, screenAutoSize);
            float screenStrokeNorm = screenMarkerSize > 0f ? strokePx / screenMarkerSize : 0.075f;
            float mx = rect.x + rect.width * annotation.PartU + annotation.NudgeX * Screen.width;
            float my = rect.y + rect.height * annotation.PartV + annotation.NudgeY * Screen.height;
            if (screenArrow)
            {
                const float tipU = 0.91f;
                float tipX = mx - ResolvedGapPx(annotation, screenPartPx);
                DrawMarkerSprite(annotation.Kind,
                    new Rect(tipX - screenMarkerSize * tipU, my - screenMarkerSize * 0.5f,
                             screenMarkerSize, screenMarkerSize),
                    screenStrokeNorm, color);
            }
            else
            {
                DrawMarkerSprite(annotation.Kind,
                    new Rect(mx - screenMarkerSize * 0.5f, my - screenMarkerSize * 0.5f,
                             screenMarkerSize, screenMarkerSize),
                    screenStrokeNorm, color);
            }
        }

        private static float UiScale()
        {
            // Style numbers in the source script use a 1080p reference.
            return Mathf.Max(0.2f, Screen.height / 1080f);
        }

        private static float ResolvedStrokePx(VisualAnnotationState annotation)
        {
            float basePx = annotation != null && annotation.StrokePx > 0f
                ? annotation.StrokePx : 11f;
            return basePx * UiScale();
        }

        private static float ResolvedSizePx(VisualAnnotationState annotation, float autoPx)
        {
            if (annotation != null && annotation.SizePx > 0f)
                return annotation.SizePx * UiScale();
            return autoPx;
        }

        private static float ResolvedGapPx(VisualAnnotationState annotation, float partPx)
        {
            if (annotation != null && annotation.GapPx > 0f)
                return annotation.GapPx * UiScale();
            if (partPx > 0f)
                return partPx * 0.5f + 6f * UiScale();
            return Mathf.Max(10f, Screen.height * 0.014f);
        }

        private static Color ResolvedAnnotationColor(VisualAnnotationState annotation)
        {
            string hex = annotation != null ? annotation.ColorHex : null;
            if (string.IsNullOrEmpty(hex)) return AnnotationColor;
            Color parsed;
            if (ColorUtility.TryParseHtmlString(hex, out parsed)) return parsed;
            return AnnotationColor;
        }

        private static readonly Color AnnotationColor = new Color(1f, 0.12f, 0.12f, 1f);

        private const int LabelMaxCharsPerLine = 15;

        private static string WrapLabelText(string text, int maxCharsPerLine)
        {
            if (string.IsNullOrEmpty(text) || maxCharsPerLine <= 0) return text;

            var sb = new System.Text.StringBuilder(text.Length + text.Length / maxCharsPerLine + 1);
            int lineChars = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\r') continue;
                if (ch == '\n')
                {
                    sb.Append(ch);
                    lineChars = 0;
                    continue;
                }
                if (lineChars >= maxCharsPerLine)
                {
                    sb.Append('\n');
                    lineChars = 0;
                }
                sb.Append(ch);
                lineChars++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 统一文字说明格式：所有 op:label 的文字都用这里的一把尺子。
        /// 字号随屏幕高度缩放，白色字 + 深色描边，不画背景黑框；
        /// 以后新增 cue 只要用 label + screen overlay 槽位，不要自己加底框或固定字号。
        /// </summary>
        private void EnsureOverlayLabelStyles()
        {
            int fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.046f), 28, 72);
            if (overlayLabelStyle != null && labelShadowStyle != null && overlayLabelStyle.fontSize == fontSize)
                return;

            overlayLabelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
                fontSize = fontSize,
                normal = { textColor = Color.white }
            };
            labelShadowStyle = new GUIStyle(overlayLabelStyle);
            labelShadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.92f);
        }

        private void DrawLabelAnnotation(FrameState frame, VisualAnnotationState annotation, Camera cam)
        {
            if (annotation == null || string.IsNullOrEmpty(annotation.Text)) return;
            Rect rect;
            if (annotation.Space == "world")
            {
                var screen = cam.WorldToScreenPoint(new Vector3(annotation.X, 0f, annotation.Z));
                if (screen.z < 0f) return;
                float w = annotation.LabelW > 0f ? annotation.LabelW * Screen.width : Screen.width * 0.42f;
                float h = annotation.LabelH > 0f ? annotation.LabelH * Screen.height : Screen.height * 0.12f;
                float cx = screen.x + annotation.NudgeX * Screen.width;
                float cy = (Screen.height - screen.y) + annotation.NudgeY * Screen.height;
                rect = new Rect(cx - w * 0.5f, cy - h - 8f, w, h);
            }
            else
            {
                if (!TryGetAnnotationScreenRect(frame, annotation, out var anchorRect)) return;
                rect = anchorRect;
                if (annotation.LabelW > 0f && annotation.LabelH > 0f)
                {
                    rect = new Rect(
                        rect.x + annotation.NudgeX * Screen.width,
                        rect.y + annotation.NudgeY * Screen.height,
                        annotation.LabelW * Screen.width,
                        annotation.LabelH * Screen.height);
                }
            }
            // 统一文字说明格式：深色描边 + 白色正文，不画背景框；
            // 先按每行最多 LabelMaxCharsPerLine 个字自动换行，再交给 GUI 排版。
            string wrappedText = WrapLabelText(annotation.Text, LabelMaxCharsPerLine);
            float shadowScale = Mathf.Max(1f, overlayLabelStyle.fontSize / 34f);
            for (int i = 0; i < SubtitleOutlineOffsets.Length; i++)
            {
                Vector2 offset = SubtitleOutlineOffsets[i];
                GUI.Label(
                    new Rect(
                        rect.x + offset.x * shadowScale,
                        rect.y + offset.y * shadowScale,
                        rect.width,
                        rect.height),
                    wrappedText,
                    labelShadowStyle);
            }
            GUI.Label(rect, wrappedText, overlayLabelStyle);
        }

        private bool TryGetAnnotationScreenRect(FrameState frame, VisualAnnotationState annotation, out Rect rect)
        {
            rect = new Rect();
            if (annotation == null) return false;
            if (!string.IsNullOrEmpty(annotation.OverlayId))
            {
                VisualOverlayState overlay = null;
                foreach (var candidate in frame.Overlays)
                {
                    if (candidate != null && candidate.Id == annotation.OverlayId)
                    {
                        overlay = candidate;
                        break;
                    }
                }
                if (overlay != null && TryGetOverlayRects(overlay, out _, out rect)) return true;
            }
            if (annotation.ScreenW <= 0f || annotation.ScreenH <= 0f) return false;
            rect = new Rect(
                annotation.ScreenX * Screen.width,
                annotation.ScreenY * Screen.height,
                Mathf.Max(1f, annotation.ScreenW * Screen.width),
                Mathf.Max(1f, annotation.ScreenH * Screen.height));
            return true;
        }

        private void DrawMarkerSprite(string kind, Rect rect, float strokeNorm, Color color)
        {
            var sprite = ActorBinder.GetMarkerSprite(kind, strokeNorm);
            if (sprite == null || sprite.texture == null) return;
            var savedColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, sprite.texture);
            GUI.color = savedColor;
        }

        private void DrawBoxOutline(Rect rect)
        {
            DrawBoxOutline(rect, 11f * UiScale(), AnnotationColor);
        }

        private void DrawBoxOutline(Rect rect, float thickness, Color color)
        {
            var panel = GetOverlayPanelTexture();
            var savedColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), panel);
            GUI.DrawTexture(new Rect(rect.x, rect.y + rect.height - thickness, rect.width, thickness), panel);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), panel);
            GUI.DrawTexture(new Rect(rect.x + rect.width - thickness, rect.y, thickness, rect.height), panel);
            GUI.color = savedColor;
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
