using System;
using System.Collections;
using System.Collections.Generic;
using BoardGameTutorial.Animation;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace BoardGameTutorial
{
    public partial class TutorialCuePlayer : MonoBehaviour
    {
        private float lastPosition;
        private bool playbackFinished;

        public void PlayCue(
            int index,
            bool continueState = false,
            float localTime = 0f,
            bool startPaused = false)
        {
            if (doc == null || doc.cues == null || doc.cues.Count == 0) return;
            index = Mathf.Clamp(index, 0, doc.cues.Count - 1);
            if (playbackRoutine != null)
            {
                StopCoroutine(playbackRoutine);
                playbackRoutine = null;
            }
            CancelCueTimelineForSwitch();
            lastPosition = Mathf.Max(0f, localTime);
            playbackFinished = false;
            CueTimelinePlan plan;
            if (!TryBuildCueTimelinePlan(index, out plan))
            {
                Debug.LogError("[CueTimeline] no whole-track plan for cue index "
                    + index + " track=" + track);
                return;
            }
            playbackRoutine = StartCoroutine(PlayCueTimelineRoutine(
                plan, index, Mathf.Max(0f, localTime), startPaused));
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
            Debug.LogWarning("[CueTimeline] PlayCueAt target not found: " + cueId);
            return false;
        }

        public void ReplayCurrent()
        {
            if (currentIndex >= 0) PlayCue(currentIndex);
        }

        public void Next()
        {
            if (doc == null || doc.cues == null) return;
            PlayCue(currentIndex + 1, true);
        }

        public void Previous()
        {
            if (doc == null || doc.cues == null) return;
            PlayCue(currentIndex - 1);
        }

        public void NextCue() { Next(); }
        public void PreviousCue() { Previous(); }

        public void TogglePause()
        {
            if (isPaused) Resume();
            else Pause();
        }

        public void Pause()
        {
            if (cueTimelineController != null && cueTimelineController.Pause())
                isPaused = true;
        }

        public void Resume()
        {
            if (cueTimelineController != null && cueTimelineController.Resume())
                isPaused = false;
        }

        public void SetVolume(float value)
        {
            float clamped = Mathf.Clamp01(value);
            if (cueTimelineAudioExecutor != null)
                cueTimelineAudioExecutor.Volume = clamped;
        }

        public void AdjustVolume(float delta)
        {
            SetVolume(Volume + delta);
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
            if (!cueTimelineActive || cueTimelineController == null) return;
            float target = Position + seconds;
            if (target < 0f)
            {
                if (currentIndex > 0) PlayCue(currentIndex - 1, true);
                else SeekCueTimeline(0f);
                return;
            }
            if (target > Duration)
            {
                if (currentIndex + 1 < TotalCueCount) PlayCue(currentIndex + 1, true);
                else SeekCueTimeline(Mathf.Max(0f, Duration - 0.05f));
                return;
            }
            SeekCueTimeline(target);
        }

        public void SeekTo(float localSeconds)
        {
            SeekCueTimeline(localSeconds);
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

        public void ToggleDebugJump()
        {
            if (doc == null || doc.cues == null || debugJumpCueIds == null
                || debugJumpCueIds.Count == 0) return;
            if (!inDebugJump) debugJumpReturnIndex = currentIndex;
            string want = debugJumpCueIds[debugJumpCursor % debugJumpCueIds.Count];
            debugJumpCursor = (debugJumpCursor + 1) % debugJumpCueIds.Count;
            for (int i = 0; i < doc.cues.Count; i++)
            {
                if (doc.cues[i].id != want) continue;
                inDebugJump = true;
                PlayCue(i);
                Debug.Log("[TutorialCuePlayer] debug jump -> " + want);
                return;
            }
            Debug.LogWarning("[TutorialCuePlayer] debug jump target not found: " + want);
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
            UpdateCueTimeline();

#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.spaceKey.wasPressedThisFrame) TogglePause();
            if (kb.rKey.wasPressedThisFrame) ReplayCurrent();
            if (kb.leftArrowKey.wasPressedThisFrame) Previous();
            if (kb.rightArrowKey.wasPressedThisFrame) Next();
            if (kb.aKey.wasPressedThisFrame) autoAdvance = !autoAdvance;
            if (kb.gKey.wasPressedThisFrame) ToggleCueAnimation();
            if (kb.bKey.wasPressedThisFrame && kb.shiftKey.isPressed)
            { inDebugJump = false; PlayCue(debugJumpReturnIndex); }
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
            try { return Input.GetKeyDown(KeyCode.Escape); }
            catch (System.Exception) { return false; }
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
    }
}
