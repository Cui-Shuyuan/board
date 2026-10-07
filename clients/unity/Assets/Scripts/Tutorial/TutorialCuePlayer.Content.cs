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
    }
}
