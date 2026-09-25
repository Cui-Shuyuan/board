// Explicit bridge object for native Android -> Unity control.
//
// Keep GameObjectName stable: the native Activity targets this GameObject with
// UnityPlayer.UnitySendMessage(GameObjectName, methodName, stringArg).
//
// All public methods take a string because UnitySendMessage can only pass a
// string. Numeric bridge methods parse with the invariant culture and then call
// TutorialCuePlayer.
using System;
using System.Globalization;
using UnityEngine;

namespace BoardGameTutorial
{
    public class AndroidTutorialBridge : MonoBehaviour
    {
        public const string GameObjectName = "AndroidTutorialBridge";

        private const float StatusIntervalSeconds = 0.5f;
        private float nextStatusAt;
        private bool unityReady;
        private bool readyLogged;
        private bool startupStatusPosted;
        private static bool statusCallbackWarned;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindFirstObjectByType<AndroidTutorialBridge>() != null) return;
            var go = new GameObject(GameObjectName);
            go.AddComponent<AndroidTutorialBridge>();
        }

        private void Start()
        {
            MarkUnityReady();
            nextStatusAt = 0f;
            PostStartupStatusIfPossible();
        }

        private void Update()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!unityReady)
            {
                MarkUnityReady();
                nextStatusAt = 0f;
            }

            // If Start ran before TutorialCuePlayer existed, keep trying once
            // per frame until the first ready status has been posted.
            if (!startupStatusPosted)
            {
                PostStartupStatusIfPossible();
            }

            if (Time.unscaledTime < nextStatusAt) return;
            nextStatusAt = Time.unscaledTime + StatusIntervalSeconds;
            PostStatus();
#endif
        }

        private void MarkUnityReady()
        {
            unityReady = true;
            if (readyLogged) return;
            readyLogged = true;
            Debug.Log("[AndroidTutorialBridge] unityReady");
        }

        private void PostStartupStatusIfPossible()
        {
            if (startupStatusPosted) return;
            if (FindFirstObjectByType<TutorialCuePlayer>() == null) return;
            startupStatusPosted = true;
            PostStatus();
        }

        public void TogglePause()
        {
            var player = FindPlayer();
            if (player == null) return;

            player.TogglePause();
            Debug.Log("[AndroidTutorialBridge] TogglePause forwarded. IsPaused=" + player.IsPaused);
            PostStatus();
        }

        public void Pause()
        {
            var player = FindPlayer();
            if (player == null) return;

            player.Pause();
            Debug.Log("[AndroidTutorialBridge] Pause forwarded. IsPaused=" + player.IsPaused);
            PostStatus();
        }

        public void Resume()
        {
            var player = FindPlayer();
            if (player == null) return;

            player.Resume();
            Debug.Log("[AndroidTutorialBridge] Resume forwarded. IsPaused=" + player.IsPaused);
            PostStatus();
        }

        /// <summary>
        /// Native absolute seek to a cue-local second offset.
        /// </summary>
        public void SeekTo(string seconds)
        {
            var player = FindPlayer();
            if (player == null) return;

            if (!TryParseFloat(seconds, out float value))
            {
                Debug.LogWarning("[AndroidTutorialBridge] SeekTo received invalid seconds: " + seconds);
                return;
            }

            player.SeekTo(value);
            Debug.Log($"[AndroidTutorialBridge] SeekTo({value:0.###}) forwarded.");
            PostStatus();
        }

        /// <summary>
        /// Payload format is "cueId|localSeconds", e.g.
        /// "setup.cards.001.1|3.25".  This is the cross-cue route used by
        /// the native seek bar and chapter menu.
        /// </summary>
        public void PlayCueAt(string payload)
        {
            var player = FindPlayer();
            if (player == null) return;

            if (!TryParseCueAt(payload, out string cueId, out float localSeconds))
            {
                Debug.LogWarning("[AndroidTutorialBridge] PlayCueAt received invalid payload: " + payload);
                return;
            }

            bool found = player.PlayCueAt(cueId, localSeconds);
            Debug.Log($"[AndroidTutorialBridge] PlayCueAt({cueId}|{localSeconds:0.###}) found={found}.");
            PostStatus();
        }

        public void NextCue(string ignored)
        {
            var player = FindPlayer();
            if (player == null) return;

            player.NextCue();
            Debug.Log("[AndroidTutorialBridge] NextCue forwarded.");
            PostStatus();
        }

        public void PreviousCue(string ignored)
        {
            var player = FindPlayer();
            if (player == null) return;

            player.PreviousCue();
            Debug.Log("[AndroidTutorialBridge] PreviousCue forwarded.");
            PostStatus();
        }

        public void SeekRelative(string seconds)
        {
            var player = FindPlayer();
            if (player == null) return;

            if (!TryParseFloat(seconds, out float value))
            {
                Debug.LogWarning("[AndroidTutorialBridge] SeekRelative received invalid seconds: " + seconds);
                return;
            }

            player.SeekRelative(value);
            Debug.Log($"[AndroidTutorialBridge] SeekRelative({value}) forwarded.");
            PostStatus();
        }

        public void AdjustVolume(string delta)
        {
            var player = FindPlayer();
            if (player == null) return;

            if (!TryParseFloat(delta, out float value))
            {
                Debug.LogWarning("[AndroidTutorialBridge] AdjustVolume received invalid delta: " + delta);
                return;
            }

            player.AdjustVolume(value);
            Debug.Log($"[AndroidTutorialBridge] AdjustVolume({value}) forwarded. Volume={player.Volume:0.00}");
            PostStatus();
        }

        public void SetVolume(string value)
        {
            var player = FindPlayer();
            if (player == null) return;

            if (!TryParseFloat(value, out float volume))
            {
                Debug.LogWarning("[AndroidTutorialBridge] SetVolume received invalid value: " + value);
                return;
            }

            player.SetVolume(volume);
            Debug.Log($"[AndroidTutorialBridge] SetVolume({volume:0.00}) forwarded.");
            PostStatus();
        }

        /// <summary>
        /// Stores the content version directory for TutorialCuePlayer.
        /// TutorialCuePlayer still appends the configured game id, so native
        /// code passes board-content/versions/{version}/ (not .../splendor).
        /// Call ReloadGame() afterwards to apply it.
        /// </summary>
        public void SetContentRoot(string path)
        {
            var player = FindPlayer();
            if (player == null) return;

            player.tutorialRoot = path ?? "";
            Debug.Log("[AndroidTutorialBridge] SetContentRoot stored (call ReloadGame to apply): " + player.tutorialRoot);
        }

        public void ReloadGame()
        {
            var player = FindPlayer();
            if (player == null) return;

            player.ReloadGame();
            Debug.Log("[AndroidTutorialBridge] ReloadGame forwarded.");
            PostStatus();
        }

        /// <summary>
        /// Content updates are driven by the native Compose layer.  This entry
        /// point exists for protocol completeness; Unity itself has no direct
        /// reverse call into the Activity.
        /// </summary>
        public void CheckContentUpdate()
        {
            Debug.Log("[AndroidTutorialBridge] CheckContentUpdate is native-driven; use the Compose button.");
        }

        public void SetUnityTouchControlsEnabled(string enabled)
        {
            var player = FindPlayer();
            if (player == null) return;

            bool value = ParseBool(enabled, false);
            player.SetUnityTouchControlsEnabled(value);
            Debug.Log("[AndroidTutorialBridge] SetUnityTouchControlsEnabled(" + value + ") forwarded.");
            PostStatus();
        }

        private static TutorialCuePlayer FindPlayer()
        {
            var player = FindFirstObjectByType<TutorialCuePlayer>();
            if (player == null)
                Debug.LogWarning("[AndroidTutorialBridge] TutorialCuePlayer not found; command ignored.");
            return player;
        }

        private static bool TryParseFloat(string value, out float result)
        {
            return float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result);
        }

        private static bool TryParseCueAt(
            string payload,
            out string cueId,
            out float localSeconds)
        {
            cueId = "";
            localSeconds = 0f;
            if (string.IsNullOrEmpty(payload)) return false;

            int separator = payload.IndexOf('|');
            string idPart;
            string secondsPart;
            if (separator < 0)
            {
                idPart = payload.Trim();
                secondsPart = "0";
            }
            else
            {
                idPart = payload.Substring(0, separator).Trim();
                secondsPart = payload.Substring(separator + 1).Trim();
            }

            if (idPart.Length == 0) return false;
            if (!TryParseFloat(secondsPart, out localSeconds)) return false;
            if (localSeconds < 0f) localSeconds = 0f;
            cueId = idPart;
            return true;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            if (bool.TryParse(value, out bool result)) return result;
            if (value == "1") return true;
            if (value == "0") return false;
            return fallback;
        }

        private void PostStatus()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var player = FindFirstObjectByType<TutorialCuePlayer>();
            if (player == null) return;

            var status = new BridgeStatus
            {
                unityReady = unityReady,
                isPlaying = player.IsPlaying,
                isPaused = player.IsPaused,
                volume = player.Volume,
                cueId = player.CurrentCueId ?? "",
                cueText = player.CurrentCueText ?? "",
                cueIndex = player.CurrentCueIndex,
                cueTotal = player.TotalCueCount,
                position = player.Position,
                duration = player.Duration,
                touchControlsEnabled = player.UnityTouchControlsEnabled,
            };

            string json = JsonUtility.ToJson(status);
            try
            {
                using (var callback = new AndroidJavaClass("com.boardai.tutorial.uaal.UnityBridgeCallback"))
                {
                    callback.CallStatic("postStatus", json);
                }
            }
            catch (Exception ex)
            {
                if (!statusCallbackWarned)
                {
                    statusCallbackWarned = true;
                    Debug.LogWarning("[AndroidTutorialBridge] status callback failed: " + ex.Message);
                }
            }
#endif
        }

        [Serializable]
        private sealed class BridgeStatus
        {
            public bool unityReady;
            public bool isPlaying;
            public bool isPaused;
            public float volume;
            public string cueId = "";
            public string cueText = "";
            public int cueIndex = -1;
            public int cueTotal;
            public float position;
            public float duration;
            public bool touchControlsEnabled;
        }
    }
}
