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

        private bool unityReady;
        private bool readyLogged;
        private bool startupStatusPosted;
        private static bool statusCallbackWarned;

        // Low-cost state-change detection.  Update checks every frame, but the
        // JNI callback is only invoked when one of these values changes.
        private bool hasStatusSnapshot;
        private bool lastIsPlaying;
        private bool lastIsPaused;
        private int lastCueIndex = -1;
        private string lastCueId = "";
        private float lastVolume = 1f;

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
            PostStartupStatusIfPossible();
        }

        private void Update()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!unityReady)
            {
                MarkUnityReady();
            }

            // If Start ran before TutorialCuePlayer existed, keep trying once
            // per frame until the first ready status has been posted.
            if (!startupStatusPosted)
            {
                PostStartupStatusIfPossible();
            }

            // Fixed-interval polling is gone.  The per-frame check remains
            // cheap and only crosses JNI when player state actually changes;
            // SeekTo positions between events are interpolated natively.
            PostStatusIfChanged();
#endif
        }

        /// <summary>
        /// Immediately posts the current player snapshot.  Native code calls
        /// this after Unity is ready, when returning to the foreground, after
        /// ReloadGame, and whenever an authoritative refresh is useful.
        /// </summary>
        public void RequestStatus()
        {
            Debug.Log("[AndroidTutorialBridge] RequestStatus forwarded.");
            PostStatus();
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
        /// Payload format is "cueId|localSeconds|paused", e.g.
        /// "setup.cards.001.1|3.25|1".  The third field is optional (0/1);
        /// it preserves the native paused state across cross-cue seeks.
        /// </summary>
        public void PlayCueAt(string payload)
        {
            var player = FindPlayer();
            if (player == null) return;

            if (!TryParseCueAt(
                    payload,
                    out string cueId,
                    out float localSeconds,
                    out bool startPaused))
            {
                Debug.LogWarning("[AndroidTutorialBridge] PlayCueAt received invalid payload: " + payload);
                return;
            }

            bool found = player.PlayCueAt(cueId, localSeconds, startPaused);
            Debug.Log(
                $"[AndroidTutorialBridge] PlayCueAt({cueId}|{localSeconds:0.###}|paused={startPaused}) found={found}.");
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
        /// Selects and loads one catalog game.  The native side has already
        /// switched active.json to the downloaded version; clearing
        /// tutorialRoot makes ResolveGameRoot read that active pointer.
        /// </summary>
        public void LoadGame(string gameId)
        {
            var player = FindPlayer();
            if (player == null) return;

            player.gameId = string.IsNullOrWhiteSpace(gameId) ? "" : gameId.Trim();
            player.tutorialRoot = "";
            player.ReloadGame();
            Debug.Log("[AndroidTutorialBridge] LoadGame(" + player.gameId + ") forwarded.");
            PostStatus();
        }

        /// <summary>
        /// Selects a game and supplies the absolute version directory directly.
        /// Payload format: "{gameId}|{versionRoot}".
        ///
        /// The version root is the directory that contains
        /// {versionRoot}/{gameId}/tutorial/... .  Native code uses this for the
        /// app-private content repository, avoiding any dependency on
        /// Application.persistentDataPath/active.json.
        /// </summary>
        public void LoadGameWithRoot(string payload)
        {
            var player = FindPlayer();
            if (player == null) return;

            if (string.IsNullOrWhiteSpace(payload))
            {
                Debug.LogWarning("[AndroidTutorialBridge] LoadGameWithRoot received empty payload.");
                return;
            }

            int separator = payload.IndexOf('|');
            if (separator <= 0 || separator >= payload.Length - 1)
            {
                Debug.LogWarning("[AndroidTutorialBridge] LoadGameWithRoot invalid payload: " + payload);
                return;
            }

            string gameId = payload.Substring(0, separator).Trim();
            string versionRoot = payload.Substring(separator + 1).Trim();
            if (gameId.Length == 0 || versionRoot.Length == 0)
            {
                Debug.LogWarning("[AndroidTutorialBridge] LoadGameWithRoot missing game/root: " + payload);
                return;
            }

            player.gameId = gameId;
            player.tutorialRoot = versionRoot;
            player.ReloadGame();
            Debug.Log(
                "[AndroidTutorialBridge] LoadGameWithRoot(game=" + gameId +
                ", root=" + versionRoot + ") forwarded.");
            PostStatus();
        }

        /// <summary>
        /// Stops playback, clears the current audio and animation scene, and
        /// leaves the runtime idle until the next LoadGame call.
        /// </summary>
        public void UnloadGame(string ignored)
        {
            var player = FindPlayer();
            if (player == null) return;

            player.StopAndClear();
            Debug.Log("[AndroidTutorialBridge] UnloadGame forwarded.");
            PostStatus();
        }

        /// <summary>
        /// Called by TutorialCuePlayer when Android back is observed in the
        /// Unity input loop.  The native Activity owns the actual navigation.
        /// </summary>
        public static void NotifyNativeBack()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var callback = new AndroidJavaClass("com.boardai.tutorial.uaal.UnityBridgeCallback"))
                {
                    callback.CallStatic("postBackPressed");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AndroidTutorialBridge] native back callback failed: " + ex.Message);
            }
#endif
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
            out float localSeconds,
            out bool startPaused)
        {
            cueId = "";
            localSeconds = 0f;
            startPaused = false;
            if (string.IsNullOrEmpty(payload)) return false;

            string[] parts = payload.Split('|');
            string idPart = parts.Length > 0 ? parts[0].Trim() : "";
            string secondsPart = parts.Length > 1 ? parts[1].Trim() : "0";
            string pausedPart = parts.Length > 2 ? parts[2].Trim() : "0";

            if (idPart.Length == 0) return false;
            if (!TryParseFloat(secondsPart, out localSeconds)) return false;
            if (localSeconds < 0f) localSeconds = 0f;
            startPaused = ParseBool(pausedPart, false);
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

        private void PostStatusIfChanged()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var player = FindFirstObjectByType<TutorialCuePlayer>();
            if (player == null) return;

            string currentCueId = player.CurrentCueId ?? "";
            if (hasStatusSnapshot &&
                lastIsPlaying == player.IsPlaying &&
                lastIsPaused == player.IsPaused &&
                lastCueIndex == player.CurrentCueIndex &&
                string.Equals(lastCueId, currentCueId, StringComparison.Ordinal) &&
                Mathf.Abs(lastVolume - player.Volume) <= 0.0001f)
            {
                return;
            }

            PostStatus();
#endif
        }

        private void CaptureStatusSnapshot(TutorialCuePlayer player)
        {
            hasStatusSnapshot = true;
            lastIsPlaying = player.IsPlaying;
            lastIsPaused = player.IsPaused;
            lastCueIndex = player.CurrentCueIndex;
            lastCueId = player.CurrentCueId ?? "";
            lastVolume = player.Volume;
        }

        private void PostStatus()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var player = FindFirstObjectByType<TutorialCuePlayer>();
            if (player == null) return;

            CaptureStatusSnapshot(player);

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
