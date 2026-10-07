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
    }
}
