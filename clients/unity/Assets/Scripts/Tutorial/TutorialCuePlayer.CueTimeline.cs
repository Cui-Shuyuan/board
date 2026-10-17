// Phase 3 feature-flagged cue timeline path.
//
// This partial keeps the old TutorialCuePlayer routines intact.  A cue only
// enters this path when useCueTimelineV1 is true, the cue id is allowlisted,
// and a plan validates.  Every other cue continues through the legacy paths.
using System;
using System.Collections;
using System.Collections.Generic;
using BoardGameTutorial.Animation;
using UnityEngine;

namespace BoardGameTutorial
{
    public partial class TutorialCuePlayer
    {
        [Header("Cue timeline v1 (Phase 3 migration)")]
        [Tooltip("默认关闭。仅 allowlist cue 且 plan 生成/校验成功时走新控制器；其它 cue 仍走旧播放路径。")]
        public bool useCueTimelineV1 = false;

        [Tooltip("Phase 3 迁移样例 allowlist。flag 关闭时不生效。")]
        public List<string> cueTimelineCueIds = new List<string>
        {
            "quick_intro/title_1",
            "quick_intro/001",
            "quick_take_gems/003",
        };

        [Tooltip("仅这些迁移样例使用多段音频+silence+visual hold 的显式合成 plan。")]
        public List<string> cueTimelineCompositeCueIds = new List<string>
        {
            "quick_take_gems/003",
        };

        [Tooltip("显式 authored visual hold 时长；不是音频 pre-roll/post-roll。")]
        public float cueTimelineCompositeHoldSeconds = 0.75f;

        private CueTimelineFeatureGate cueTimelineGate;
        private CuePlaybackController cueTimelineController;
        private UnityCueAudioExecutor cueTimelineAudioExecutor;
        private UnityCueVisualExecutor cueTimelineVisualExecutor;
        private CueTimelineSubtitleExecutor cueTimelineSubtitleExecutor;
        private UnityDspCueClock cueTimelineClock;
        private AudioSource cueTimelineAudioSource;
        private bool cueTimelineActive;
        private bool cueTimelineAdvanceRequested;
        private CueTimelinePlan cueTimelinePlan;

        private void EnsureCueTimelineController()
        {
            if (cueTimelineController != null) return;

            cueTimelineGate = new CueTimelineFeatureGate();
            cueTimelineClock = new UnityDspCueClock();
            cueTimelineAudioSource = gameObject.AddComponent<AudioSource>();
            cueTimelineAudioSource.playOnAwake = false;
            cueTimelineAudioSource.loop = false;
            cueTimelineAudioExecutor = new UnityCueAudioExecutor(
                this, cueTimelineAudioSource, gameRoot ?? "");
            cueTimelineVisualExecutor = new UnityCueVisualExecutor(v2AnimPlayer, gameRoot ?? "");
            cueTimelineSubtitleExecutor = new CueTimelineSubtitleExecutor();
            cueTimelineController = new CuePlaybackController(
                cueTimelineClock,
                cueTimelineAudioExecutor,
                cueTimelineVisualExecutor,
                cueTimelineSubtitleExecutor);
            cueTimelineController.StateChanged += OnCueTimelineStateChanged;
            cueTimelineController.Completed += OnCueTimelineCompleted;
            cueTimelineController.Failed += OnCueTimelineFailed;
        }

        private void RefreshCueTimelineRuntimePaths()
        {
            if (cueTimelineAudioExecutor != null)
                cueTimelineAudioExecutor.GameRoot = gameRoot ?? "";
        }

        private bool TryBuildCueTimelinePlan(int index, out CueTimelinePlan plan)
        {
            plan = null;
            if (doc == null || doc.cues == null || index < 0 || index >= doc.cues.Count)
                return false;

            if (cueTimelineGate == null) cueTimelineGate = new CueTimelineFeatureGate();
            cueTimelineGate.Enabled = useCueTimelineV1;
            cueTimelineGate.SetAllowlist(cueTimelineCueIds);

            TutorialCue cue = doc.cues[index];
            string reason;
            if (!cueTimelineGate.ShouldUse(cue != null ? cue.id : "", out reason))
            {
                if (useCueTimelineV1 && cue != null)
                    Debug.Log("[CueTimelineV1] legacy path cue=" + cue.id + " reason=" + reason);
                return false;
            }

            EnsureCueTimelineController();
            RefreshCueTimelineRuntimePaths();

            float compiledDuration = 0f;
            bool hasCompiled = v2AnimPlayer != null
                && v2AnimPlayer.TryGetCueDuration(cue.id, out compiledDuration);

            var input = new CueTimelineLegacyCueInput
            {
                cue_id = cue.id,
                entry = cue.entry,
                course_start = cue.start,
                runtime_duration = cue.duration,
                audio_asset = cue.audio,
                audio_start = cue.audio_start,
                section_audio = cue.section_audio,
                compiled_visual_duration = hasCompiled ? compiledDuration : 0.0,
                sample_rate = doc.sample_rate,
                compose_multi_segment_audio = IsTimelineCompositeCue(cue.id),
                visual_hold_seconds = IsTimelineCompositeCue(cue.id)
                    ? Math.Max(0f, cueTimelineCompositeHoldSeconds) : 0.0,
            };

            if (cue.audio_plan != null)
            {
                for (int i = 0; i < cue.audio_plan.Count; i++)
                {
                    TutorialAudioPlanSegment legacy = cue.audio_plan[i];
                    if (legacy == null) continue;
                    input.audio_plan.Add(new CueTimelineLegacyAudioPlanSegment
                    {
                        type = legacy.type,
                        file = legacy.file,
                        start = legacy.start,
                        end = legacy.end,
                        duration = legacy.duration,
                    });
                }
            }
            if (cue.subtitles != null)
            {
                for (int i = 0; i < cue.subtitles.Count; i++)
                {
                    TutorialCueSubtitle subtitle = cue.subtitles[i];
                    if (subtitle == null) continue;
                    input.subtitles.Add(new CueTimelineSubtitle
                    {
                        id = "subtitle." + i,
                        cue_start = subtitle.t,
                        duration = Math.Max(0.0, subtitle.end - subtitle.t),
                        text = subtitle.text ?? "",
                    });
                }
            }

            string error;
            if (!CueTimelineLegacyPlanFactory.TryBuild(input, out plan, out error))
            {
                Debug.LogWarning("[CueTimelineV1] plan generation failed cue=" + cue.id
                    + " fallback=legacy reason=" + error);
                return false;
            }

            Debug.Log("[CueTimelineV1] plan ready cue=" + cue.id
                + " duration=" + plan.course.duration.ToString("0.000")
                + " tracks=" + plan.tracks.Count);
            return true;
        }

        private bool IsTimelineCompositeCue(string cueId)
        {
            if (string.IsNullOrEmpty(cueId) || cueTimelineCompositeCueIds == null) return false;
            for (int i = 0; i < cueTimelineCompositeCueIds.Count; i++)
                if (cueTimelineCompositeCueIds[i] == cueId) return true;
            return false;
        }

        private IEnumerator PlayCueTimelineRoutine(
            CueTimelinePlan plan,
            int index,
            float localTime,
            bool startPaused)
        {
            if (plan == null) yield break;

            // The legacy source is a separate playback instance and must not
            // remain audible when the new path takes ownership.
            if (audioSource != null)
            {
                audioSource.Stop();
                audioSource.clip = null;
                audioSource.time = 0f;
            }
            audioPlanActive = false;
            audioPlanAwaitingStart = false;
            audioPlanSegmentIsClip = false;
            playbackFinished = false;
            animationHoldActive = false;
            animationHoldLocal = 0f;
            currentIndex = index;
            currentSubtitle = "";
            cueTimelineActive = true;
            cueTimelineAdvanceRequested = false;
            cueTimelinePlan = plan;
            isPaused = startPaused;
            pausedBeforePlay = startPaused;
            lastPosition = Mathf.Max(0f, localTime);

            if (cueTimelineController.State != CuePlaybackState.Idle
                && !CuePlaybackStateMachine.IsTerminal(cueTimelineController.State))
            {
                cueTimelineController.Cancel();
            }

            cueTimelineController.Prepare(plan);
            while (cueTimelineActive
                && cueTimelineController.State == CuePlaybackState.Preparing)
            {
                yield return null;
            }

            if (!cueTimelineActive) yield break;
            if (cueTimelineController.State != CuePlaybackState.Ready)
            {
                // Failure is terminal; do not auto-advance and do not fall
                // back to the legacy player after resources started loading.
                Debug.LogError("[CueTimelineV1] prepare did not become ready cue=" + plan.cue_id
                    + " state=" + cueTimelineController.State
                    + (cueTimelineController.LastFailure != null
                        ? " error=" + cueTimelineController.LastFailure.Message : ""));
                yield break;
            }

            bool started = cueTimelineController.PlayAt(
                Mathf.Max(0f, localTime), startPaused);
            if (!started)
            {
                Debug.LogError("[CueTimelineV1] PlayAt failed cue=" + plan.cue_id
                    + " state=" + cueTimelineController.State);
                yield break;
            }

            Debug.Log("[CueTimelineV1] started cue=" + plan.cue_id
                + " generation=" + cueTimelineController.Generation
                + " local=" + localTime.ToString("0.000")
                + " paused=" + startPaused);
        }

        private void UpdateCueTimeline()
        {
            if (!cueTimelineActive || cueTimelineController == null) return;
            cueTimelineController.Tick();

            CueSubtitleState subtitle = cueTimelineController.CurrentSubtitle;
            currentSubtitle = subtitle != null && subtitle.Visible ? subtitle.Text : "";

            CuePlaybackState state = cueTimelineController.State;
            if (state == CuePlaybackState.Completed || state == CuePlaybackState.Failed
                || state == CuePlaybackState.Cancelled)
            {
                playbackFinished = state == CuePlaybackState.Completed;
                lastPosition = Mathf.Max(0f, (float)cueTimelineController.CueTime);
            }
            if (cueTimelineAdvanceRequested && cueTimelineActive)
            {
                cueTimelineAdvanceRequested = false;
                if (doc != null && doc.cues != null
                    && currentIndex + 1 < doc.cues.Count)
                {
                    v2AnimPlayer.Complete();
                    PlayCue(currentIndex + 1, true);
                }
            }
        }

        private void CancelCueTimelineForSwitch()
        {
            if (cueTimelineController != null
                && !CuePlaybackStateMachine.IsTerminal(cueTimelineController.State))
                cueTimelineController.Cancel();
            cueTimelineActive = false;
            cueTimelineAdvanceRequested = false;
            cueTimelinePlan = null;
            currentSubtitle = "";
            if (cueTimelineAudioExecutor != null)
            {
                // Cancel() above already stopped generation work.  This also
                // clears any clip reference while preserving the read-only
                // decoded clip cache.
                cueTimelineAudioExecutor.Stop(0L);
            }
        }

        private void DisposeCueTimeline()
        {
            if (cueTimelineController != null
                && !CuePlaybackStateMachine.IsTerminal(cueTimelineController.State))
                cueTimelineController.Cancel();
            cueTimelineActive = false;
            cueTimelineAdvanceRequested = false;
            cueTimelinePlan = null;
            if (cueTimelineAudioExecutor != null) cueTimelineAudioExecutor.Dispose();
            if (cueTimelineVisualExecutor != null) cueTimelineVisualExecutor.Dispose();
            if (cueTimelineAudioSource != null)
            {
                cueTimelineAudioSource.Stop();
                cueTimelineAudioSource.clip = null;
            }
        }

        private void OnCueTimelineStateChanged(CuePlaybackTransition transition)
        {
            if (transition == null) return;
            if (transition.To == CuePlaybackState.Paused) isPaused = true;
            else if (transition.To == CuePlaybackState.Playing) isPaused = false;
            Debug.Log("[CueTimelineV1][State] " + transition.From + " -> " + transition.To
                + " gen=" + transition.Generation);
        }

        private void OnCueTimelineCompleted(CuePlaybackCompletion completion)
        {
            if (completion == null || cueTimelinePlan == null
                || completion.CueId != cueTimelinePlan.cue_id)
                return;
            playbackFinished = true;
            lastPosition = Mathf.Max(0f, (float)completion.Duration);
            currentSubtitle = "";
            Debug.Log("[CueTimelineV1][Complete] cue=" + completion.CueId
                + " gen=" + completion.Generation
                + " cue_time=" + completion.CueTime.ToString("0.000"));
            if (pauseAtCueEnd) return;
            if (autoAdvance && doc != null && doc.cues != null
                && currentIndex + 1 < doc.cues.Count)
            {
                cueTimelineAdvanceRequested = true;
            }
        }

        private void OnCueTimelineFailed(CuePlaybackFailure failure)
        {
            playbackFinished = false;
            cueTimelineAdvanceRequested = false;
            currentSubtitle = "";
            if (failure == null) return;
            Debug.LogError("[CueTimelineV1][Failed] cue=" + failure.CueId
                + " gen=" + failure.Generation
                + " executor=" + failure.Executor
                + " kind=" + failure.Kind
                + " message=" + failure.Message);
        }

        private bool SeekCueTimeline(float localSeconds)
        {
            if (!cueTimelineActive || cueTimelineController == null) return false;
            bool ok = cueTimelineController.SeekTo(localSeconds);
            if (!ok) return false;
            lastPosition = Mathf.Max(0f, (float)cueTimelineController.CueTime);
            isPaused = cueTimelineController.State == CuePlaybackState.Paused;
            currentSubtitle = cueTimelineController.CurrentSubtitle != null
                && cueTimelineController.CurrentSubtitle.Visible
                ? cueTimelineController.CurrentSubtitle.Text : "";
            Debug.Log("[CueTimelineV1][Seek] cue=" + CurrentCueId
                + " local=" + localSeconds.ToString("0.000")
                + " state=" + cueTimelineController.State
                + " gen=" + cueTimelineController.Generation);
            return true;
        }
    }
}
