// Shared whole-track cue-plan playback entry.
//
// The audio transport owns one DSP-anchored course clock.  The current cue,
// subtitles, visual localTime and Android progress all derive from that same
// course position.  Sequential cue changes never Stop/Play/Seek audio.
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

        private CueCoursePlaybackController cueTimelineController;
        private UnityCourseAudioTransport cueTimelineAudioTransport;
        private UnityCueVisualExecutor cueTimelineVisualExecutor;
        private CueTimelineSubtitleExecutor cueTimelineSubtitleExecutor;
        private UnityDspCueClock cueTimelineClock;
        private AudioSource cueTimelineAudioSourceA;
        private AudioSource cueTimelineAudioSourceB;
        private bool cueTimelineActive;
        private CueTimelinePlan cueTimelinePlan;
        private bool resumeAfterApplicationPause;
        private int lastAudioOutputSampleRate;
        private bool audioConfigurationDirty;

        /// <summary>
        /// Applies the Android-provided track switch before ReloadGame().  Both
        /// modes run through the same whole-track controller.
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
                if (cueTimelinePlan != null && cueTimelinePlan.course != null)
                    return Mathf.Max(0f, (float)cueTimelinePlan.course.start);
                return CurrentCue != null ? CurrentCue.start : 0f;
            }
        }

        public float CueTimelineGlobalPosition
        {
            get
            {
                if (cueTimelineActive && cueTimelineController != null)
                    return Mathf.Max(0f, (float)cueTimelineController.CourseTime);
                return Mathf.Max(0f, CueTimelineCueStart + Position);
            }
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
            cueTimelineAudioSourceA = UnityCourseAudioTransport.CreateSource(this, "Course audio A");
            cueTimelineAudioSourceB = UnityCourseAudioTransport.CreateSource(this, "Course audio B");
            cueTimelineAudioTransport = new UnityCourseAudioTransport(
                this, cueTimelineAudioSourceA, cueTimelineAudioSourceB,
                gameRoot ?? "");
            cueTimelineVisualExecutor = new UnityCueVisualExecutor(
                v2AnimPlayer, gameRoot ?? "");
            cueTimelineSubtitleExecutor = new CueTimelineSubtitleExecutor();
            cueTimelineController = new CueCoursePlaybackController(
                cueTimelineClock,
                cueTimelineAudioTransport,
                cueTimelineSubtitleExecutor);
            cueTimelineController.StateChanged += OnCueTimelineStateChanged;
            cueTimelineController.CueChanged += OnCueTimelineCueChanged;
            cueTimelineController.CueCompleted += OnCueTimelineCueCompleted;
            cueTimelineController.Completed += OnCueTimelineCompleted;
            cueTimelineController.Failed += OnCueTimelineFailed;
            cueTimelineController.Underrun += OnCueTimelineUnderrun;
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
        }

        private void RefreshCueTimelineRuntimePaths()
        {
            if (cueTimelineAudioTransport != null)
                cueTimelineAudioTransport.GameRoot = gameRoot ?? "";
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
            CueTimelinePlan targetPlan,
            int index,
            float localTime,
            bool startPaused)
        {
            if (targetPlan == null || targetPlan.course == null) yield break;

            currentIndex = index;
            currentSubtitle = "";
            cueTimelineActive = true;
            cueTimelinePlan = targetPlan;
            isPaused = startPaused;
            pausedBeforePlay = startPaused;
            playbackFinished = false;
            lastPosition = Mathf.Max(0f, localTime);

            cueTimelineController.SetAdvancePolicy(autoAdvance, pauseAtCueEnd);
            double courseTime = targetPlan.course.start
                + Mathf.Max(0f, localTime);

            if (!cueTimelineController.Prepare(
                cueTimelineTrackPlanSet, courseTime, startPaused))
            {
                Debug.LogError("[CueTimeline] course prepare rejected cue="
                    + targetPlan.cue_id + " state=" + cueTimelineController.State
                    + (cueTimelineController.LastFailure != null
                        ? " error=" + cueTimelineController.LastFailure.Message : ""));
                yield break;
            }

            while (cueTimelineActive
                && cueTimelineController.State == CuePlaybackState.Preparing)
                yield return null;

            if (!cueTimelineActive) yield break;
            if (cueTimelineController.State == CuePlaybackState.Failed)
            {
                Debug.LogError("[CueTimeline] course prepare failed cue="
                    + targetPlan.cue_id
                    + (cueTimelineController.LastFailure != null
                        ? " error=" + cueTimelineController.LastFailure.Message : ""));
                yield break;
            }

            Debug.Log("[CueTimeline] started course cue=" + targetPlan.cue_id
                + " transport_gen=" + cueTimelineController.TransportGeneration
                + " cue_gen=" + cueTimelineController.CueGeneration
                + " global=" + courseTime.ToString("0.000")
                + " local=" + localTime.ToString("0.000")
                + " paused=" + startPaused);
        }

        private void UpdateCueTimeline()
        {
            if (cueTimelineAudioTransport != null)
                cueTimelineAudioTransport.ReleaseFinishedSlots();
            if (!cueTimelineActive || cueTimelineController == null) return;

            int outputRate = AudioSettings.outputSampleRate;
            if (audioConfigurationDirty || (lastAudioOutputSampleRate != 0
                && outputRate != lastAudioOutputSampleRate))
            {
                audioConfigurationDirty = false;
                Debug.LogWarning("[CueTimeline][Audio] output sample rate changed "
                    + lastAudioOutputSampleRate + " -> " + outputRate
                    + "; rebuilding the course DSP anchor");
                cueTimelineController.RebaseAfterDeviceChange();
            }
            lastAudioOutputSampleRate = outputRate;
            cueTimelineController.Tick();

            currentIndex = cueTimelineController.CurrentCueIndex;
            cueTimelinePlan = cueTimelineController.CurrentPlan;
            if (cueTimelineVisualExecutor != null && cueTimelinePlan != null)
            {
                cueTimelineVisualExecutor.Seek(
                    cueTimelineController.CueGeneration,
                    cueTimelineController.LocalTime);
            }
            CueSubtitleState subtitle = cueTimelineController.CurrentSubtitle;
            string nextSubtitle = subtitle != null && subtitle.Visible
                ? subtitle.Text : "";
            if (nextSubtitle != cueTimelineLoggedSubtitle)
            {
                cueTimelineLoggedSubtitle = nextSubtitle;
                Debug.Log("[CueTimeline][Subtitle] text=" + nextSubtitle);
            }
            currentSubtitle = nextSubtitle;
            lastPosition = Mathf.Max(0f, (float)cueTimelineController.LocalTime);

            CuePlaybackState state = cueTimelineController.State;
            if (state == CuePlaybackState.Completed)
            {
                playbackFinished = true;
            }
            else if (state == CuePlaybackState.Failed
                || state == CuePlaybackState.Cancelled)
            {
                playbackFinished = false;
            }
        }


        private void OnApplicationPause(bool paused)
        {
            if (!cueTimelineActive || cueTimelineController == null) return;
            if (paused)
            {
                resumeAfterApplicationPause =
                    cueTimelineController.State == CuePlaybackState.Playing
                    || (cueTimelineController.State == CuePlaybackState.Preparing && !isPaused);
                if (resumeAfterApplicationPause)
                    cueTimelineController.Pause();
                return;
            }

            cueTimelineController.RebaseAfterDeviceChange();
            if (resumeAfterApplicationPause)
            {
                resumeAfterApplicationPause = false;
                cueTimelineController.Resume();
            }
        }

        private void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            if (!cueTimelineActive || cueTimelineController == null) return;
            // Handle on Update, including device switches at the same sample
            // rate. Coalesce the notification with output-rate polling.
            audioConfigurationDirty = true;
        }

        private void CancelCueTimelineForSwitch()
        {
            if (cueTimelineController != null
                && !CuePlaybackStateMachine.IsTerminal(cueTimelineController.State))
                cueTimelineController.Cancel();
            cueTimelineActive = false;
            cueTimelinePlan = null;
            currentSubtitle = "";
            cueTimelineLoggedSubtitle = "";
            resumeAfterApplicationPause = false;
            lastAudioOutputSampleRate = 0;
        }

        private void DisposeCueTimeline()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
            audioConfigurationDirty = false;
            if (cueTimelineController != null
                && !CuePlaybackStateMachine.IsTerminal(cueTimelineController.State))
                cueTimelineController.Cancel();
            cueTimelineActive = false;
            cueTimelinePlan = null;
            resumeAfterApplicationPause = false;
            lastAudioOutputSampleRate = 0;
            cueTimelineTrackPlanSet = null;
            cueTimelinePlaybackPlanReady = false;
            cueTimelineUsingCompatibilityPlan = false;
            cueTimelineTrackFallbackLogged = false;
            if (cueTimelineAudioTransport != null) cueTimelineAudioTransport.Dispose();
            if (cueTimelineVisualExecutor != null) cueTimelineVisualExecutor.Dispose();
            if (cueTimelineAudioSourceA != null)
            {
                cueTimelineAudioSourceA.Stop();
                cueTimelineAudioSourceA.clip = null;
                UnityEngine.Object.Destroy(cueTimelineAudioSourceA.gameObject);
            }
            if (cueTimelineAudioSourceB != null)
            {
                cueTimelineAudioSourceB.Stop();
                cueTimelineAudioSourceB.clip = null;
                UnityEngine.Object.Destroy(cueTimelineAudioSourceB.gameObject);
            }
            cueTimelineAudioTransport = null;
            cueTimelineAudioSourceA = null;
            cueTimelineAudioSourceB = null;
            cueTimelineController = null;
            cueTimelineVisualExecutor = null;
            cueTimelineClock = null;
            cueTimelineSubtitleExecutor = null;
        }

        private void ApplyCueTimelineAdvancePolicy()
        {
            if (cueTimelineController == null) return;
            cueTimelineController.SetAdvancePolicy(autoAdvance, pauseAtCueEnd);
        }

        private void OnCueTimelineStateChanged(CuePlaybackState state)
        {
            if (state == CuePlaybackState.Paused
                || state == CuePlaybackState.Buffering)
                isPaused = true;
            else if (state == CuePlaybackState.Playing)
                isPaused = false;
            Debug.Log("[CueTimeline][State] " + state
                + " transport_gen="
                + (cueTimelineController != null
                    ? cueTimelineController.TransportGeneration : 0L));
        }

        private void OnCueTimelineCueChanged(CueCourseCueEvent cueEvent)
        {
            if (cueEvent == null || cueEvent.Plan == null) return;
            currentIndex = cueEvent.CueIndex;
            cueTimelinePlan = cueEvent.Plan;
            currentSubtitle = "";
            Debug.Log("[CueTimeline][Cue] enter index=" + cueEvent.CueIndex
                + " cue=" + cueEvent.Plan.cue_id
                + " cue_gen=" + cueEvent.CueGeneration
                + " global=" + cueEvent.CourseTime.ToString("0.000")
                + " local=" + cueEvent.LocalTime.ToString("0.000"));

            if (cueTimelineVisualExecutor != null)
            {
                cueTimelineVisualExecutor.Prepare(cueEvent.Plan,
                    cueEvent.CueGeneration, new CourseVisualCallback(this));
                cueTimelineVisualExecutor.Seek(cueEvent.CueGeneration,
                    cueEvent.LocalTime);
            }
        }

        private void OnCueTimelineCueCompleted(CueCourseCueEvent cueEvent)
        {
            if (cueEvent == null || cueEvent.Plan == null) return;
            Debug.Log("[CueTimeline][CueComplete] index=" + cueEvent.CueIndex
                + " cue=" + cueEvent.Plan.cue_id
                + " global=" + cueEvent.CourseTime.ToString("0.000"));
        }

        private void OnCueTimelineCompleted(CueCourseCompletion completion)
        {
            if (completion == null) return;
            playbackFinished = true;
            lastPosition = cueTimelineController != null
                ? Mathf.Max(0f, (float)cueTimelineController.LocalTime) : 0f;
            currentSubtitle = "";
            Debug.Log("[CueTimeline][Complete] cue=" + completion.CueId
                + " transport_gen=" + completion.TransportGeneration
                + " cue_gen=" + completion.CueGeneration
                + " global=" + completion.CourseTime.ToString("0.000"));
        }

        private void OnCueTimelineFailed(CueCourseFailure failure)
        {
            playbackFinished = false;
            currentSubtitle = "";
            if (failure == null) return;
            Debug.LogError("[CueTimeline][Failed] cue=" + failure.CueId
                + " transport_gen=" + failure.TransportGeneration
                + " message=" + failure.Message);
        }

        private void OnCueTimelineUnderrun(CueCourseUnderrun underrun)
        {
            if (underrun == null) return;
            isPaused = true;
            Debug.LogError("[CueTimeline][Underrun] global="
                + underrun.CourseTime.ToString("0.000")
                + " transport_gen=" + underrun.TransportGeneration
                + " message=" + underrun.Message);
        }

        private void OnCourseVisualFailed(long generation, string message)
        {
            Debug.LogError("[CueTimeline][Visual] gen=" + generation
                + " " + message);
            if (cueTimelineController != null
                && cueTimelineController.State == CuePlaybackState.Playing)
                cueTimelineController.Pause();
        }

        private bool SeekCueTimeline(float localSeconds)
        {
            if (!cueTimelineActive || cueTimelineController == null) return false;
            CueTimelinePlan plan = cueTimelineController.CurrentPlan;
            if (plan == null || plan.course == null) return false;
            double target = plan.course.start + Mathf.Max(0f, localSeconds);
            bool stayPaused = cueTimelineController.State == CuePlaybackState.Paused
                || cueTimelineController.State == CuePlaybackState.Buffering;
            if (!cueTimelineController.SeekTo(target, stayPaused)) return false;
            lastPosition = Mathf.Max(0f, (float)cueTimelineController.LocalTime);
            isPaused = cueTimelineController.State == CuePlaybackState.Paused
                || cueTimelineController.State == CuePlaybackState.Buffering;
            CueSubtitleState subtitle = cueTimelineController.CurrentSubtitle;
            currentSubtitle = subtitle != null && subtitle.Visible
                ? subtitle.Text : "";
            return true;
        }

        private bool SeekCueTimelineGlobal(float globalSeconds, bool startPaused)
        {
            if (!cueTimelineActive || cueTimelineController == null) return false;
            if (!cueTimelineController.SeekTo(
                Mathf.Max(0f, globalSeconds), startPaused))
                return false;
            lastPosition = Mathf.Max(0f, (float)cueTimelineController.LocalTime);
            isPaused = cueTimelineController.State == CuePlaybackState.Paused
                || cueTimelineController.State == CuePlaybackState.Buffering;
            return true;
        }

        private sealed class CourseVisualCallback : ICueExecutorCallback
        {
            private readonly TutorialCuePlayer owner;

            public CourseVisualCallback(TutorialCuePlayer owner)
            {
                this.owner = owner;
            }

            public void OnPrepared(long generation)
            {
            }

            public void OnCompleted(long generation)
            {
            }

            public void OnFailed(long generation, string message)
            {
                owner.OnCourseVisualFailed(generation, message);
            }
        }
    }
}
