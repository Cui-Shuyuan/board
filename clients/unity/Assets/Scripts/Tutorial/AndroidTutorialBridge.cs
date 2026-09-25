// Explicit bridge object for native Android -> Unity control.
//
// Keep GameObjectName stable: the native Activity targets this GameObject with
// UnityPlayer.UnitySendMessage(GameObjectName, "TogglePause", "").
using UnityEngine;

namespace BoardGameTutorial
{
    public class AndroidTutorialBridge : MonoBehaviour
    {
        public const string GameObjectName = "AndroidTutorialBridge";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindFirstObjectByType<AndroidTutorialBridge>() != null) return;
            var go = new GameObject(GameObjectName);
            go.AddComponent<AndroidTutorialBridge>();
        }

        public void TogglePause()
        {
            var player = FindFirstObjectByType<TutorialCuePlayer>();
            if (player == null)
            {
                Debug.LogWarning("[AndroidTutorialBridge] TutorialCuePlayer not found; TogglePause ignored.");
                return;
            }

            player.TogglePause();
            Debug.Log("[AndroidTutorialBridge] TogglePause forwarded. IsPaused=" + player.IsPaused);
        }
    }
}
