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
    public partial class TutorialCuePlayer : MonoBehaviour
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
    }
}
