// BoardGameTutorial
// 纯音频 cue 播放器 v0。
//
// 数据：games/{game}/tutorial/{track}.runtime.json
// 行为：播放 mp3、显示字幕、上一段/下一段、跳转、暂停、重播当前 cue。
// 动画暂不参与，后续按 cue.animation 挂独立时间轴。
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

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

        // 当 cue 播放器启用时，旧的动画 TutorialDirector 不再自动搭景。
        // 需要回到旧动画原型时，把它设为 false 或手动把 TutorialDirector 挂到场景。
        public static bool DisableLegacyBootstrap = true;

        private TutorialCueDoc doc;
        private string gameRoot;
        private AudioSource audioSource;
        private Coroutine playbackRoutine;
        private int currentIndex = -1;
        private bool isPaused;
        private string currentSubtitle = "";
        private GUIStyle debugStyle;

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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
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
                if (cue.group_path == null) cue.group_path = new System.Collections.Generic.List<string>();
                if (cue.refs == null) cue.refs = new System.Collections.Generic.List<string>();
                if (cue.subtitles == null) cue.subtitles = new System.Collections.Generic.List<TutorialCueSubtitle>();
                foreach (var subtitle in cue.subtitles)
                {
                    if (subtitle.words == null) subtitle.words = new System.Collections.Generic.List<TutorialCueWord>();
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
            if (doc == null || doc.cues == null) return false;
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
            if (Input.GetKeyDown(KeyCode.Space)) TogglePause();
            if (Input.GetKeyDown(KeyCode.R)) ReplayCurrent();
            if (Input.GetKeyDown(KeyCode.LeftArrow)) Previous();
            if (Input.GetKeyDown(KeyCode.RightArrow)) Next();
            if (Input.GetKeyDown(KeyCode.A)) autoAdvance = !autoAdvance;
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

        private static string FilePathToUri(string path)
        {
            if (path.Contains("://")) return path;
            return new System.Uri(path).AbsoluteUri;
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
            GUI.Label(new Rect(24, 168, Screen.width - 48, 24),
                "Space 暂停/继续  R 重播  ← 上一段  → 下一段  A 自动播放", debugStyle);
        }
    }
}
