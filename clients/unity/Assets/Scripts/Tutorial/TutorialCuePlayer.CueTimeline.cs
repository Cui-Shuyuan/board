// Shared track-plan playback entry.
//
// The runtime cue document supplies cue order and navigation metadata only.
// Cue start/duration always come from one whole-track plan set:
//   * the shared tutorial/{track}.cue-timeline.json, or
//   * one whole-track compatibility plan built by the loader adapter for an
//     older runtime document.
// There is no per-cue mixed mode and no second playback engine.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BoardGameTutorial.Animation;
using UnityEngine;

namespace BoardGameTutorial
{
    public partial class TutorialCuePlayer
    {
        private bool cueTimelineTrackRequested;
        private bool cueTimelinePlaybackPlanReady;
        private bool cueTimelineUsingCompatibilityPlan;
        private bool cueTimelineTrackFallbackLogged;
        private string cueTimelineTrackLoadError = "";
        private CueTimelinePlanSet cueTimelineTrackPlanSet;
        private string cueTimelineLoggedSubtitle = "";

        private CuePlaybackController cueTimelineController;
        private UnityCueAudioExecutor cueTimelineAudioExecutor;
        private UnityCueVisualExecutor cueTimelineVisualExecutor;
        private CueTimelineSubtitleExecutor cueTimelineSubtitleExecutor;
        private UnityDspCueClock cueTimelineClock;
        private AudioSource cueTimelineAudioSource;
        private bool cueTimelineActive;
        private bool cueTimelineAdvanceRequested;
        private CueTimelinePlan cueTimelinePlan;

        /// <summary>
        /// Applies the Android-provided track switch before ReloadGame().  True
        /// selects the shared plan file.  False is the explicit whole-track
        /// compatibility entry point for older callers; both modes still run
        /// through CuePlaybackController.
        /// </summary>
        public void SetCueTimelineTrackEnabled(bool enabled)
        {
            cueTimelineTrackRequested = enabled;
            cueTimelinePlaybackPlanReady = false;
            cueTimelineUsingCompatibilityPlan = false;
            cueTimelineTrackFallbackLogged = false;
            cueTimelineTrackLoadError = "";
            cueTimelineTrackPlanSet = null;
            Debug.Log("[CueTimeline][Plan] track plan mode requested=" + enabled
                + " track=" + track);
        }

        public bool CueTimelineTrackModeRequested
        {
            get { return cueTimelineTrackRequested; }
        }

        public bool CueTimelineTrackPlanActive
        {
            get
            {
                return cueTimelineTrackRequested
                    && !cueTimelineUsingCompatibilityPlan
                    && cueTimelinePlaybackPlanReady
                    && cueTimelineTrackPlanSet != null
                    && cueTimelineActive
                    && cueTimelinePlan != null;
            }
        }

        public float CueTimelineCueStart
        {
            get
            {
                if (cueTimelinePlan != null)
                    return Mathf.Max(0f, (float)cueTimelinePlan.course.start);
                return CurrentCue != null ? CurrentCue.start : 0f;
            }
        }

        public float CueTimelineGlobalPosition
        {
            get { return Mathf.Max(0f, CueTimelineCueStart + Position); }
        }

        public float CueTimelineCourseDuration
        {
            get
            {
                return cueTimelinePlaybackPlanReady && cueTimelineTrackPlanSet != null
                    && cueTimelineTrackPlanSet.course != null
                    ? Mathf.Max(0f, (float)cueTimelineTrackPlanSet.course.duration)
                    : 0f;
            }
        }

        private bool LoadCueTimelineTrackPlan()
        {
            cueTimelineTrackPlanSet = null;
            cueTimelinePlaybackPlanReady = false;
            cueTimelineUsingCompatibilityPlan = false;
            cueTimelineTrackLoadError = "";
            cueTimelineTrackFallbackLogged = false;

            if (doc == null || doc.cues == null || doc.cues.Count == 0)
            {
                cueTimelineTrackLoadError = "runtime has no cues";
                LogCueTimelineTrackFallback();
                return false;
            }
            if (string.IsNullOrEmpty(gameRoot) || string.IsNullOrEmpty(track))
            {
                cueTimelineTrackLoadError = "gameRoot or track is empty";
                LogCueTimelineTrackFallback();
                return false;
            }

            var expectedCueIds = new List<string>(doc.cues.Count);
            for (int i = 0; i < doc.cues.Count; i++)
            {
                TutorialCue cue = doc.cues[i];
                if (cue == null || string.IsNullOrEmpty(cue.id))
                {
                    cueTimelineTrackLoadError =
                        "runtime cue id is empty at index " + i;
                    LogCueTimelineTrackFallback();
                    return false;
                }
                expectedCueIds.Add(cue.id);
            }

            if (cueTimelineTrackRequested)
            {
                string path = Path.Combine(
                    gameRoot, "tutorial", track + ".cue-timeline.json");
                try
                {
                    if (!File.Exists(path))
                        throw new FileNotFoundException("missing plan file", path);
                    CueTimelinePlanSet parsed = JsonUtility.FromJson<CueTimelinePlanSet>(
                        File.ReadAllText(path));
                    string error;
                    if (!CueTimelinePlanSetValidator.TryValidate(
                        parsed, gameId, track, expectedCueIds, out error))
                        throw new ArgumentException(error);
                    ValidateCueTimelinePlanAudioFiles(parsed);
                    cueTimelineTrackPlanSet = parsed;
                    cueTimelinePlaybackPlanReady = true;
                    Debug.Log("[CueTimeline][Plan] loaded track=" + track
                        + " cues=" + parsed.cues.Count
                        + " duration=" + parsed.course.duration.ToString("0.000")
                        + " path=" + path);
                    return true;
                }
                catch (Exception ex)
                {
                    cueTimelineTrackLoadError = ex.Message;
                    LogCueTimelineTrackFallback();
                }
            }

            // Whole-track compatibility fallback.  This adapter has no
            // legacy composition fields or title rules; it only copies runtime
            // start/duration and the declared audio source range into the same
            // plan schema used by the controller.
            try
            {
                var inputs = new List<CueTimelineCompatibilityCueInput>(doc.cues.Count);
                for (int i = 0; i < doc.cues.Count; i++)
                {
                    TutorialCue cue = doc.cues[i];
                    float compiledDuration = 0f;
                    bool hasCompiled = v2AnimPlayer != null
                        && v2AnimPlayer.TryGetCueDuration(cue.id, out compiledDuration);
                    var input = new CueTimelineCompatibilityCueInput
                    {
                        cue_id = cue.id,
                        entry = cue.entry,
                        course_start = cue.start,
                        runtime_duration = cue.duration,
                        audio_asset = cue.audio,
                        audio_start = cue.audio_start,
                        compiled_visual_duration = hasCompiled ? compiledDuration : 0.0,
                        sample_rate = doc.sample_rate,
                    };
                    if (cue.subtitles != null)
                    {
                        for (int subtitleIndex = 0;
                            subtitleIndex < cue.subtitles.Count; subtitleIndex++)
                        {
                            TutorialCueSubtitle subtitle = cue.subtitles[subtitleIndex];
                            if (subtitle == null) continue;
                            input.subtitles.Add(new CueTimelineSubtitle
                            {
                                id = "subtitle." + subtitleIndex,
                                cue_start = subtitle.t,
                                duration = Math.Max(0f, subtitle.end - subtitle.t),
                                text = subtitle.text ?? "",
                            });
                        }
                    }
                    inputs.Add(input);
                }

                CueTimelinePlanSet compatibilitySet;
                string compatibilityError;
                if (!CueTimelineCompatibilityPlanFactory.TryBuildSet(
                    inputs, gameId, track, out compatibilitySet,
                    out compatibilityError))
                {
                    cueTimelineTrackLoadError = compatibilityError;
                    LogCueTimelineTrackFallback();
                    return false;
                }
                cueTimelineTrackPlanSet = compatibilitySet;
                cueTimelinePlaybackPlanReady = true;
                cueTimelineUsingCompatibilityPlan = true;
                Debug.LogWarning("[CueTimeline][Compatibility] loaded whole-track "
                    + "runtime fallback track=" + track
                    + " cues=" + compatibilitySet.cues.Count
                    + " duration=" + compatibilitySet.course.duration.ToString("0.000")
                    + (string.IsNullOrEmpty(cueTimelineTrackLoadError)
                        ? "" : " reason=" + cueTimelineTrackLoadError));
                return true;
            }
            catch (Exception ex)
            {
                cueTimelineTrackLoadError = ex.Message;
                LogCueTimelineTrackFallback();
                return false;
            }
        }

        private void ValidateCueTimelinePlanAudioFiles(CueTimelinePlanSet planSet)
        {
            if (planSet == null || planSet.cues == null) return;
            for (int cueIndex = 0; cueIndex < planSet.cues.Count; cueIndex++)
            {
                CueTimelinePlan plan = planSet.cues[cueIndex];
                if (plan == null || plan.tracks == null) continue;
                for (int trackIndex = 0; trackIndex < plan.tracks.Count; trackIndex++)
                {
                    CueTimelineTrack planTrack = plan.tracks[trackIndex];
                    if (planTrack == null || planTrack.kind != "audio"
                        || planTrack.segments == null) continue;
                    for (int segmentIndex = 0; segmentIndex < planTrack.segments.Count;
                        segmentIndex++)
                    {
                        CueTimelineSegment segment = planTrack.segments[segmentIndex];
                        if (segment == null || segment.type != "audio_clip") continue;
                        string asset = (segment.asset ?? "").Trim();
                        if (asset.Length == 0 || Path.IsPathRooted(asset))
                            throw new ArgumentException(
                                "audio segment asset must be game-relative: '"
                                + asset + "'");
                        string localPath = Path.Combine(
                            gameRoot, asset.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(localPath))
                            throw new FileNotFoundException(
                                "plan references missing audio file", localPath);
                    }
                }
            }
        }

        private void LogCueTimelineTrackFallback()
        {
            if (cueTimelineTrackFallbackLogged) return;
            cueTimelineTrackFallbackLogged = true;
            Debug.LogWarning("[CueTimeline][Plan] fallback=whole-track-compatibility "
                + "track=" + track + " reason=" + cueTimelineTrackLoadError);
        }

        private void EnsureCueTimelineController()
        {
            if (cueTimelineController != null) return;

            cueTimelineClock = new UnityDspCueClock();
            cueTimelineAudioSource = gameObject.AddComponent<AudioSource>();
            cueTimelineAudioSource.playOnAwake = false;
            cueTimelineAudioSource.loop = false;
            cueTimelineAudioExecutor = new UnityCueAudioExecutor(
                this, cueTimelineAudioSource, gameRoot ?? "");
            cueTimelineVisualExecutor = new UnityCueVisualExecutor(
                v2AnimPlayer, gameRoot ?? "");
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
            if (!cueTimelinePlaybackPlanReady || cueTimelineTrackPlanSet == null)
            {
                LogCueTimelineTrackFallback();
                return false;
            }

            TutorialCue trackCue = doc.cues[index];
            CueTimelinePlan trackPlan = CueTimelinePlanSetValidator.FindCue(
                cueTimelineTrackPlanSet, trackCue != null ? trackCue.id : "");
            if (trackPlan == null)
            {
                cueTimelineTrackLoadError =
                    "plan set has no cue at runtime index " + index;
                cueTimelinePlaybackPlanReady = false;
                LogCueTimelineTrackFallback();
                return false;
            }

            EnsureCueTimelineController();
            RefreshCueTimelineRuntimePaths();
            plan = trackPlan;
            return true;
        }

        private IEnumerator PlayCueTimelineRoutine(
            CueTimelinePlan plan,
            int index,
            float localTime,
            bool startPaused)
        {
            if (plan == null) yield break;

            currentIndex = index;
            currentSubtitle = "";
            cueTimelineActive = true;
            cueTimelineAdvanceRequested = false;
            cueTimelinePlan = plan;
            isPaused = startPaused;
            pausedBeforePlay = startPaused;
            playbackFinished = false;
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
                Debug.LogError("[CueTimeline] prepare did not become ready cue="
                    + plan.cue_id + " state=" + cueTimelineController.State
                    + (cueTimelineController.LastFailure != null
                        ? " error=" + cueTimelineController.LastFailure.Message : ""));
                yield break;
            }

            if (!cueTimelineController.PlayAt(
                Mathf.Max(0f, localTime), startPaused))
            {
                Debug.LogError("[CueTimeline] PlayAt failed cue=" + plan.cue_id
                    + " state=" + cueTimelineController.State);
                yield break;
            }

            Debug.Log("[CueTimeline] started cue=" + plan.cue_id
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
            if (currentSubtitle != cueTimelineLoggedSubtitle)
            {
                cueTimelineLoggedSubtitle = currentSubtitle;
                Debug.Log("[CueTimeline][Subtitle] text=" + currentSubtitle);
            }

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
                cueTimelineAudioExecutor.Stop(0L);
        }

        private void DisposeCueTimeline()
        {
            if (cueTimelineController != null
                && !CuePlaybackStateMachine.IsTerminal(cueTimelineController.State))
                cueTimelineController.Cancel();
            cueTimelineActive = false;
            cueTimelineAdvanceRequested = false;
            cueTimelinePlan = null;
            cueTimelineTrackPlanSet = null;
            cueTimelinePlaybackPlanReady = false;
            cueTimelineUsingCompatibilityPlan = false;
            cueTimelineTrackFallbackLogged = false;
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
            Debug.Log("[CueTimeline][State] " + transition.From + " -> " + transition.To
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
            Debug.Log("[CueTimeline][Complete] cue=" + completion.CueId
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
            Debug.LogError("[CueTimeline][Failed] cue=" + failure.CueId
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
            return true;
        }
    }
}
