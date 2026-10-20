// Pure course-level playback controller.
//
// One DSP-anchored course clock is authoritative for the whole track.  Audio
// transport appointments, current cue, subtitle state and all progress values
// are derived from it.  Sequential cue changes never prepare/cancel audio; only
// explicit seek/cancel/invalidation creates a new transport generation.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    public interface ICourseAudioTransportCallback
    {
        void OnPrepared(long transportGeneration);
        void OnFailed(long transportGeneration, string message);
        void OnUnderrun(long transportGeneration, double courseTime, string message);
    }

    /// <summary>
    /// Whole-track audio transport contract.  Playback coordinates are always
    /// absolute course seconds plus a DSP anchor; an executor must not invent a
    /// second clock from AudioSource.time or frame deltas.
    /// </summary>
    public interface ICourseAudioTransport
    {
        void Prepare(CueTimelinePlanSet planSet, long transportGeneration,
            double startCourseTime, double playbackEndCourseTime,
            ICourseAudioTransportCallback callback);
        // Return the committed DSP epoch AFTER preparing all initial PCM.
        double Schedule(long transportGeneration, double anchorCourseTime,
            double anchorDspTime, bool play, double playbackEndCourseTime);
        void Pause(long transportGeneration);
        void Stop(long transportGeneration);
        void Cancel(long transportGeneration);
        void SyncToCourseTime(long transportGeneration, double courseTime);
        void InvalidateDeviceResources();
    }

    public sealed class CueCourseCueEvent
    {
        public int CueIndex;
        public CueTimelinePlan Plan;
        public long CueGeneration;
        public long TransportGeneration;
        public double CourseTime;
        public double LocalTime;
    }

    public sealed class CueCourseCompletion
    {
        public string CueId;
        public int CueIndex;
        public long CueGeneration;
        public long TransportGeneration;
        public double CourseTime;
        public double Duration;
    }

    public sealed class CueCourseFailure
    {
        public long TransportGeneration;
        public string CueId;
        public string Message;
    }

    public sealed class CueCourseUnderrun
    {
        public long TransportGeneration;
        public double CourseTime;
        public string Message;
    }

    public sealed class CueCoursePlaybackController
    {
        private const double Epsilon = 1e-7;

        private readonly ICueClock clock;
        private readonly ICourseAudioTransport audio;
        private readonly ICueSubtitleExecutor subtitles;
        private readonly CueCoursePlaybackClock courseClock;

        private CueTimelinePlanSet planSet;
        private CuePlaybackState state = CuePlaybackState.Idle;
        private long transportGeneration;
        private long cueGeneration;
        private int currentCueIndex = -1;
        private int describedCueIndex = -1;
        private bool prepared;
        private bool startPausedPending;
        private double startCourseTimePending;
        private double playbackEndCourseTime;
        private double scheduleLeadSeconds = 0.08;
        private bool autoAdvance = true;
        private bool pauseAtCueEnd;
        private CueSubtitleState currentSubtitle = CueSubtitleState.Hidden();
        private CueCourseCompletion lastCompletion;
        private CueCourseFailure lastFailure;
        private bool terminalCompletionEmitted;
        private bool stoppedAtCueBoundary;
        private double lastObservedCourseTime;
        private double committedDspTime;
        private double retryAfterDspTime;
        private int recoveryAttempts;
        private double recoveryPosition = -1.0;

        public CueCoursePlaybackController(ICueClock cueClock,
            ICourseAudioTransport audioTransport,
            ICueSubtitleExecutor subtitleExecutor)
        {
            if (cueClock == null) throw new ArgumentNullException("cueClock");
            if (audioTransport == null) throw new ArgumentNullException("audioTransport");
            clock = cueClock;
            audio = audioTransport;
            subtitles = subtitleExecutor;
            courseClock = new CueCoursePlaybackClock(clock);
        }

        public CuePlaybackState State { get { return state; } }
        public CueTimelinePlanSet PlanSet { get { return planSet; } }
        public double CourseTime { get { return courseClock.CourseTime; } }
        public bool HasStarted { get { return state == CuePlaybackState.Playing
            && clock.NowSeconds >= committedDspTime; } }
        public int CurrentCueIndex { get { return currentCueIndex; } }
        public long TransportGeneration { get { return transportGeneration; } }
        public long CueGeneration { get { return cueGeneration; } }
        public bool IsPrepared { get { return prepared; } }
        public double PlaybackEndCourseTime { get { return playbackEndCourseTime; } }
        public CueSubtitleState CurrentSubtitle { get { return currentSubtitle.Clone(); } }
        public CueCourseCompletion LastCompletion { get { return lastCompletion; } }
        public CueCourseFailure LastFailure { get { return lastFailure; } }
        public double ScheduleLeadSeconds
        {
            get { return scheduleLeadSeconds; }
            set { scheduleLeadSeconds = Math.Max(0.0, value); }
        }

        public CueTimelinePlan CurrentPlan
        {
            get
            {
                if (planSet == null || planSet.cues == null
                    || currentCueIndex < 0 || currentCueIndex >= planSet.cues.Count)
                    return null;
                return planSet.cues[currentCueIndex];
            }
        }

        public double LocalTime
        {
            get { return CueCourseTimelineResolver.ClampToCueLocal(CurrentPlan, CourseTime); }
        }

        public bool AutoAdvance
        {
            get { return autoAdvance; }
        }

        public bool PauseAtCueEnd
        {
            get { return pauseAtCueEnd; }
        }

        public event Action<CuePlaybackState> StateChanged;
        public event Action<CueCourseCueEvent> CueChanged;
        public event Action<CueCourseCueEvent> CueCompleted;
        public event Action<CueCourseCompletion> Completed;
        public event Action<CueCourseFailure> Failed;
        public event Action<CueCourseUnderrun> Underrun;

        public void SetAdvancePolicy(bool shouldAutoAdvance, bool shouldPauseAtCueEnd)
        {
            if (autoAdvance == shouldAutoAdvance && pauseAtCueEnd == shouldPauseAtCueEnd)
                return;
            autoAdvance = shouldAutoAdvance;
            pauseAtCueEnd = shouldPauseAtCueEnd;
            stoppedAtCueBoundary = false;
            if (!prepared || planSet == null) return;
            if (state != CuePlaybackState.Playing && state != CuePlaybackState.Paused
                && state != CuePlaybackState.Buffering) return;
            RescheduleFromCurrentPosition(state == CuePlaybackState.Playing);
        }

        /// <summary>
        /// Prepare the whole course.  The requested start is committed only
        /// after all required audio blocks are ready.  Audio is scheduled at a
        /// future DSP time, and the course clock remains frozen until then.
        /// </summary>
        public bool Prepare(CueTimelinePlanSet set, double startCourseTime,
            bool startPaused)
        {
            if (set == null || set.cues == null || set.cues.Count == 0
                || set.course == null || set.course.duration <= 0.0)
                return FailCurrentOrNew("invalid plan set");
            for (int i = 0; i < set.cues.Count; i++)
            {
                string error;
                if (!CueTimelinePlanValidator.TryValidate(set.cues[i], out error))
                    return FailCurrentOrNew("plan cue[" + i + "]: " + error);
            }
            string courseError;
            if (!ValidateCourseCoverage(set, out courseError))
                return FailCurrentOrNew(courseError);

            if (prepared || transportGeneration != 0L)
            {
                try { audio.Cancel(transportGeneration); }
                catch (Exception) { }
            }

            planSet = set;
            prepared = false;
            terminalCompletionEmitted = false;
            stoppedAtCueBoundary = false;
            lastCompletion = null;
            lastFailure = null;
            startPausedPending = startPaused;
            startCourseTimePending = ClampCourseTime(startCourseTime);
            currentCueIndex = CueCourseTimelineResolver.FindCueIndex(
                planSet, startCourseTimePending);
            describedCueIndex = currentCueIndex;
            currentSubtitle = CueSubtitleState.Hidden();
            courseClock.SetMaximumCourseTime(planSet.course.duration);
            courseClock.Freeze(startCourseTimePending);
            lastObservedCourseTime = startCourseTimePending;
            recoveryAttempts = 0;
            recoveryPosition = -1.0;
            transportGeneration = NextGeneration(transportGeneration);
            cueGeneration = NextGeneration(cueGeneration);
            SetState(CuePlaybackState.Preparing);

            try
            {
                audio.Prepare(planSet, transportGeneration,
                    startCourseTimePending, ComputePlaybackEnd(currentCueIndex),
                    new TransportCallback(this));
            }
            catch (Exception ex)
            {
                Fail(transportGeneration, ex.Message);
                return false;
            }
            return state == CuePlaybackState.Preparing
                || state == CuePlaybackState.Playing
                || state == CuePlaybackState.Paused;
        }

        /// <summary>
        /// Explicit discontinuity.  Old appointments are invalidated, a new
        /// transport generation is created, and both the course clock and the
        /// audio start are re-anchored at the target.  The caller must have
        /// prepared this plan set.
        /// </summary>
        public bool SeekTo(double targetCourseTime, bool startPaused)
        {
            if (!prepared || planSet == null) return false;
            if (state == CuePlaybackState.Preparing
                || state == CuePlaybackState.Idle
                || state == CuePlaybackState.Failed
                || state == CuePlaybackState.Cancelled)
                return false;

            double target = ClampCourseTime(targetCourseTime);
            if (target >= planSet.course.duration - Epsilon)
            {
                long oldGeneration = transportGeneration;
                transportGeneration = NextGeneration(transportGeneration);
                try { audio.Stop(oldGeneration); } catch (Exception) { }
                courseClock.Freeze(planSet.course.duration);
                CompleteCourse(planSet.course.duration, transportGeneration);
                return state == CuePlaybackState.Completed;
            }

            int targetCue = CueCourseTimelineResolver.FindCueIndex(planSet, target);
            playbackEndCourseTime = ComputePlaybackEnd(targetCue);
            currentCueIndex = targetCue;
            describedCueIndex = targetCue;
            cueGeneration = NextGeneration(cueGeneration);
            transportGeneration = NextGeneration(transportGeneration);
            terminalCompletionEmitted = false;
            stoppedAtCueBoundary = false;
            courseClock.Freeze(target);

            recoveryAttempts = 0;
            recoveryPosition = -1.0;
            if (!CommitSchedule(target, !startPaused)) return false;
            RefreshPresentation();
            RaiseCueChanged(true);
            return state == CuePlaybackState.Playing
                || state == CuePlaybackState.Paused;
        }

        public void Tick()
        {
            if (!prepared || planSet == null) return;
            if (state == CuePlaybackState.Buffering)
            {
                if (clock.NowSeconds >= retryAfterDspTime)
                {
                    if (++recoveryAttempts > 3)
                        Fail(transportGeneration, "audio could not recover after three scheduling retries");
                    else Resume();
                }
                return;
            }
            if (state != CuePlaybackState.Playing) return;

            double current = courseClock.CourseTime;
            // Check output coverage BEFORE publishing completion/presentation.
            // The transport can rewind to an unheard interval after a stall.
            long tickGeneration = transportGeneration;
            try { audio.SyncToCourseTime(tickGeneration, current); }
            catch (Exception ex) { Fail(tickGeneration, ex.Message); return; }
            if (tickGeneration != transportGeneration || state != CuePlaybackState.Playing) return;
            lastObservedCourseTime = current;
            if (recoveryPosition >= 0.0 && current > recoveryPosition + 1.0)
            {
                recoveryAttempts = 0;
                recoveryPosition = -1.0;
            }
            if (current >= playbackEndCourseTime - Epsilon)
            {
                double stopAt = Math.Min(playbackEndCourseTime,
                    planSet.course.duration);
                courseClock.Freeze(stopAt);
                try { audio.Pause(transportGeneration); }
                catch (Exception ex) { Fail(transportGeneration, ex.Message); return; }

                if (stopAt >= planSet.course.duration - Epsilon)
                {
                    CompleteCourse(stopAt, transportGeneration);
                    return;
                }

                EmitCueCompleted(describedCueIndex, stopAt);
                stoppedAtCueBoundary = true;
                SetState(CuePlaybackState.Paused);
                RefreshPresentation();
                return;
            }

            int newCueIndex = CueCourseTimelineResolver.FindCueIndex(
                planSet, current);
            if (newCueIndex > describedCueIndex)
            {
                for (int i = describedCueIndex; i < newCueIndex; i++)
                    EmitCueCompleted(i, CueCourseTimelineResolver.CueEnd(
                        planSet.cues[i]));
                describedCueIndex = newCueIndex;
                currentCueIndex = newCueIndex;
                cueGeneration = NextGeneration(cueGeneration);
                RefreshPresentation();
                RaiseCueChanged(false);
            }
            else
            {
                currentCueIndex = newCueIndex;
                RefreshPresentation();
            }

        }

        public bool Pause()
        {
            if (state == CuePlaybackState.Preparing)
            {
                startPausedPending = true;
                return true;
            }
            if (state != CuePlaybackState.Playing
                && state != CuePlaybackState.Buffering)
                return false;
            double current = courseClock.CourseTime;
            courseClock.Freeze(current);
            lastObservedCourseTime = current;
            try { audio.Pause(transportGeneration); }
            catch (Exception ex) { Fail(transportGeneration, ex.Message); return false; }
            SetState(CuePlaybackState.Paused);
            return state == CuePlaybackState.Paused;
        }

        public bool Resume()
        {
            if (state == CuePlaybackState.Preparing)
            {
                startPausedPending = false;
                return true;
            }
            if (state != CuePlaybackState.Paused
                && state != CuePlaybackState.Buffering)
                return false;
            if (planSet == null || !prepared) return false;
            if (stoppedAtCueBoundary && (!autoAdvance || pauseAtCueEnd))
                return false;

            double current = courseClock.CourseTime;
            if (current >= planSet.course.duration - Epsilon)
            {
                CompleteCourse(planSet.course.duration, transportGeneration);
                return state == CuePlaybackState.Completed;
            }
            int previousCueIndex = currentCueIndex;
            currentCueIndex = CueCourseTimelineResolver.FindCueIndex(planSet, current);
            bool cueChanged = previousCueIndex != currentCueIndex;
            describedCueIndex = currentCueIndex;
            playbackEndCourseTime = ComputePlaybackEnd(currentCueIndex);
            if (current >= playbackEndCourseTime - Epsilon)
            {
                // A paused-at-cue-end state remains paused until an explicit
                // next/seek request.  Never leak the next cue to satisfy a
                // generic resume.
                SetState(CuePlaybackState.Paused);
                return false;
            }

            transportGeneration = NextGeneration(transportGeneration);
            if (!CommitSchedule(current, true)) return false;
            if (cueChanged)
            {
                cueGeneration = NextGeneration(cueGeneration);
                RefreshPresentation();
                RaiseCueChanged(true);
            }
            else
            {
                RefreshPresentation();
            }
            return true;
        }

        /// <summary>
        /// Rebuild the DSP anchor after the app returns from background or the
        /// audio device/output format changes.  Old appointments are invalid.
        /// </summary>
        public bool RebaseAfterDeviceChange()
        {
            if (planSet == null) return false;
            if (CuePlaybackStateMachine.IsTerminal(state))
            {
                // Replay must prepare fresh resources, without re-emitting a
                // completed course or restarting failed/cancelled playback.
                try { audio.InvalidateDeviceResources(); }
                catch (Exception) { }
                prepared = false;
                return false;
            }
            if (state != CuePlaybackState.Playing && state != CuePlaybackState.Paused
                && state != CuePlaybackState.Buffering && state != CuePlaybackState.Preparing)
                return false;
            bool shouldPlay = state == CuePlaybackState.Playing
                || state == CuePlaybackState.Buffering
                || (state == CuePlaybackState.Preparing && !startPausedPending);
            // dspTime may have reset already. Never subtract an old epoch
            // from the new device clock. Reload script-created AudioClips too.
            double savedPosition = lastObservedCourseTime;
            try { audio.InvalidateDeviceResources(); }
            catch (Exception ex) { Fail(transportGeneration, ex.Message); return false; }
            return Prepare(planSet, savedPosition, !shouldPlay);
        }

        public bool Cancel()
        {
            if (CuePlaybackStateMachine.IsTerminal(state)) return false;
            long oldGeneration = transportGeneration;
            transportGeneration = NextGeneration(transportGeneration);
            try { audio.Cancel(oldGeneration); } catch (Exception) { }
            courseClock.Freeze(courseClock.CourseTime);
            currentSubtitle = CueSubtitleState.Hidden();
            SetState(CuePlaybackState.Cancelled);
            return true;
        }

        private void RescheduleFromCurrentPosition(bool shouldPlay)
        {
            double current = courseClock.CourseTime;
            int previousCueIndex = currentCueIndex;
            currentCueIndex = CueCourseTimelineResolver.FindCueIndex(planSet, current);
            bool cueChanged = previousCueIndex != currentCueIndex;
            describedCueIndex = currentCueIndex;
            playbackEndCourseTime = ComputePlaybackEnd(currentCueIndex);
            transportGeneration = NextGeneration(transportGeneration);
            if (!CommitSchedule(current, shouldPlay)) return;
            if (cueChanged)
            {
                cueGeneration = NextGeneration(cueGeneration);
                RefreshPresentation();
                RaiseCueChanged(true);
            }
            else
            {
                RefreshPresentation();
            }
        }


        private static bool ValidateCourseCoverage(CueTimelinePlanSet set,
            out string error)
        {
            if (set.course == null)
            {
                error = "course is missing";
                return false;
            }
            double previousEnd = 0.0;
            for (int i = 0; i < set.cues.Count; i++)
            {
                CueTimelinePlan cue = set.cues[i];
                if (cue == null || cue.course == null)
                {
                    error = "cue[" + i + "] has no course interval";
                    return false;
                }
                if (cue.course.start < previousEnd - 1e-6)
                {
                    error = "cue[" + i + "] starts before the previous cue ends";
                    return false;
                }
                double end = cue.course.start + cue.course.duration;
                if (end > set.course.duration + 1e-5)
                {
                    error = "cue[" + i + "] ends after the declared course";
                    return false;
                }
                previousEnd = end;
            }
            if (Math.Abs(previousEnd - set.course.duration) > 1e-5)
            {
                error = "plan set cues do not cover the declared course duration";
                return false;
            }
            error = "";
            return true;
        }

        private void OnTransportPrepared(long callbackGeneration)
        {
            if (callbackGeneration != transportGeneration) return;
            if (state != CuePlaybackState.Preparing) return;
            prepared = true;
            ApplyStart(startCourseTimePending, startPausedPending);
        }

        private void ApplyStart(double targetCourseTime, bool startPaused)
        {
            double target = ClampCourseTime(targetCourseTime);
            currentCueIndex = CueCourseTimelineResolver.FindCueIndex(planSet, target);
            describedCueIndex = currentCueIndex;
            playbackEndCourseTime = ComputePlaybackEnd(currentCueIndex);
            cueGeneration = NextGeneration(cueGeneration);
            terminalCompletionEmitted = false;
            stoppedAtCueBoundary = false;

            if (target >= playbackEndCourseTime - Epsilon
                && target >= planSet.course.duration - Epsilon)
            {
                CompleteCourse(planSet.course.duration, transportGeneration);
                return;
            }

            if (!CommitSchedule(target, !startPaused)) return;
            RefreshPresentation();
            RaiseCueChanged(true);
        }

        private double ComputePlaybackEnd(int cueIndex)
        {
            if (planSet == null || planSet.course == null) return 0.0;
            if (!autoAdvance || pauseAtCueEnd)
            {
                if (cueIndex >= 0 && cueIndex < planSet.cues.Count)
                    return CueCourseTimelineResolver.CueEnd(planSet.cues[cueIndex]);
            }
            return planSet.course.duration;
        }

        private bool CommitSchedule(double target, bool play)
        {
            long committingGeneration = transportGeneration;
            courseClock.Freeze(target);
            lastObservedCourseTime = target;
            try
            {
                double requestedDsp = clock.NowSeconds + (play ? scheduleLeadSeconds : 0.0);
                double actualDsp = audio.Schedule(committingGeneration, target,
                    requestedDsp, play, playbackEndCourseTime);
                // Schedule can synchronously fail/cancel via its callback.
                if (committingGeneration != transportGeneration
                    || state == CuePlaybackState.Failed || state == CuePlaybackState.Cancelled)
                    return false;
                if (double.IsNaN(actualDsp) || double.IsInfinity(actualDsp))
                    throw new InvalidOperationException("transport returned an invalid DSP epoch");
                committedDspTime = actualDsp;
                if (play) courseClock.AnchorAt(target, actualDsp);
                SetState(play ? CuePlaybackState.Playing : CuePlaybackState.Paused);
                return state == (play ? CuePlaybackState.Playing : CuePlaybackState.Paused);
            }
            catch (Exception ex)
            {
                Fail(committingGeneration, ex.Message);
                return false;
            }
        }

        private void RefreshPresentation()
        {
            CueTimelinePlan plan = CurrentPlan;
            if (plan == null)
            {
                currentSubtitle = CueSubtitleState.Hidden();
                return;
            }
            double local = LocalTime;
            try
            {
                currentSubtitle = subtitles != null
                    ? subtitles.GetState(plan, local)
                    : CueSubtitleState.Hidden();
            }
            catch (Exception ex)
            {
                Fail(transportGeneration, ex.Message);
                return;
            }
            if (currentSubtitle == null) currentSubtitle = CueSubtitleState.Hidden();
            currentSubtitle = currentSubtitle.Clone();
        }

        private void EmitCueCompleted(int cueIndex, double courseTime)
        {
            if (planSet == null || cueIndex < 0
                || cueIndex >= planSet.cues.Count)
                return;
            CueTimelinePlan plan = planSet.cues[cueIndex];
            Action<CueCourseCueEvent> handler = CueCompleted;
            if (handler != null)
            {
                handler(new CueCourseCueEvent
                {
                    CueIndex = cueIndex,
                    Plan = plan,
                    CueGeneration = cueGeneration,
                    TransportGeneration = transportGeneration,
                    CourseTime = courseTime,
                    LocalTime = plan != null && plan.course != null
                        ? plan.course.duration : 0.0,
                });
            }
        }

        private void RaiseCueChanged(bool force)
        {
            CueTimelinePlan plan = CurrentPlan;
            if (plan == null) return;
            Action<CueCourseCueEvent> handler = CueChanged;
            if (handler != null)
            {
                handler(new CueCourseCueEvent
                {
                    CueIndex = currentCueIndex,
                    Plan = plan,
                    CueGeneration = cueGeneration,
                    TransportGeneration = transportGeneration,
                    CourseTime = CourseTime,
                    LocalTime = LocalTime,
                });
            }
        }

        private void CompleteCourse(double courseTime, long completionGeneration)
        {
            if (terminalCompletionEmitted) return;
            terminalCompletionEmitted = true;
            courseClock.Freeze(courseTime);
            try { audio.Stop(completionGeneration); } catch (Exception) { }
            for (int i = describedCueIndex; i >= 0 && i < planSet.cues.Count; i++)
            {
                CueTimelinePlan plan = planSet.cues[i];
                if (plan == null || plan.course == null) continue;
                if (plan.course.start + plan.course.duration
                    <= courseTime + Epsilon)
                {
                    EmitCueCompleted(i, plan.course.start + plan.course.duration);
                }
            }
            describedCueIndex = planSet.cues.Count - 1;
            currentCueIndex = describedCueIndex;
            RefreshPresentation();
            lastCompletion = new CueCourseCompletion
            {
                CueId = CurrentPlan != null ? CurrentPlan.cue_id : "",
                CueIndex = currentCueIndex,
                CueGeneration = cueGeneration,
                TransportGeneration = completionGeneration,
                CourseTime = courseTime,
                Duration = planSet.course.duration,
            };
            SetState(CuePlaybackState.Completed);
            Action<CueCourseCompletion> handler = Completed;
            if (handler != null) handler(lastCompletion);
        }

        private void OnTransportUnderrun(long callbackGeneration,
            double courseTime, string message)
        {
            if (callbackGeneration != transportGeneration) return;
            if (state != CuePlaybackState.Playing
                && state != CuePlaybackState.Preparing) return;
            double current = ClampCourseTime(courseTime);
            courseClock.Freeze(current);
            lastObservedCourseTime = current;
            recoveryPosition = current;
            retryAfterDspTime = clock.NowSeconds + 0.1;
            try { audio.Pause(transportGeneration); } catch (Exception) { }
            SetState(CuePlaybackState.Buffering);
            int recoveredCue = CueCourseTimelineResolver.FindCueIndex(planSet, current);
            bool changedCue = recoveredCue != currentCueIndex;
            currentCueIndex = recoveredCue;
            describedCueIndex = recoveredCue;
            RefreshPresentation();
            if (changedCue)
            {
                cueGeneration = NextGeneration(cueGeneration);
                RaiseCueChanged(true);
            }
            Action<CueCourseUnderrun> handler = Underrun;
            if (handler != null)
            {
                handler(new CueCourseUnderrun
                {
                    TransportGeneration = callbackGeneration,
                    CourseTime = current,
                    Message = message ?? "",
                });
            }
        }

        private void OnTransportFailed(long callbackGeneration, string message)
        {
            if (callbackGeneration != transportGeneration) return;
            Fail(callbackGeneration, message);
        }

        private void Fail(long failedGeneration, string message)
        {
            if (failedGeneration != transportGeneration) return;
            if (state == CuePlaybackState.Failed || state == CuePlaybackState.Completed
                || state == CuePlaybackState.Cancelled)
                return;
            try { audio.Stop(failedGeneration); } catch (Exception) { }
            courseClock.Freeze(courseClock.CourseTime);
            lastObservedCourseTime = courseClock.CourseTime;
            currentSubtitle = CueSubtitleState.Hidden();
            lastFailure = new CueCourseFailure
            {
                TransportGeneration = failedGeneration,
                CueId = CurrentPlan != null ? CurrentPlan.cue_id : "",
                Message = message ?? "",
            };
            SetState(CuePlaybackState.Failed);
            Action<CueCourseFailure> handler = Failed;
            if (handler != null) handler(lastFailure);
        }

        private bool FailCurrentOrNew(string message)
        {
            try { audio.Stop(transportGeneration); } catch (Exception) { }
            courseClock.Freeze(courseClock.CourseTime);
            SetState(CuePlaybackState.Failed);
            lastFailure = new CueCourseFailure
            {
                TransportGeneration = transportGeneration,
                CueId = CurrentPlan != null ? CurrentPlan.cue_id : "",
                Message = message ?? "",
            };
            Action<CueCourseFailure> handler = Failed;
            if (handler != null) handler(lastFailure);
            return false;
        }

        private double ClampCourseTime(double value)
        {
            if (planSet == null || planSet.course == null) return 0.0;
            if (double.IsNaN(value) || value < 0.0) return 0.0;
            if (value > planSet.course.duration) return planSet.course.duration;
            return value;
        }

        private void SetState(CuePlaybackState next)
        {
            if (state == next) return;
            state = next;
            Action<CuePlaybackState> handler = StateChanged;
            if (handler != null) handler(next);
        }

        private static long NextGeneration(long value)
        {
            value++;
            return value <= 0L ? 1L : value;
        }

        private sealed class TransportCallback : ICourseAudioTransportCallback
        {
            private readonly CueCoursePlaybackController owner;

            public TransportCallback(CueCoursePlaybackController owner)
            {
                this.owner = owner;
            }

            public void OnPrepared(long transportGeneration)
            {
                owner.OnTransportPrepared(transportGeneration);
            }

            public void OnFailed(long transportGeneration, string message)
            {
                owner.OnTransportFailed(transportGeneration, message);
            }

            public void OnUnderrun(long transportGeneration, double courseTime,
                string message)
            {
                owner.OnTransportUnderrun(transportGeneration, courseTime, message);
            }
        }
    }
}
