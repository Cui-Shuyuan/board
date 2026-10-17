// Pure cue-local playback state machine.  No UnityEngine types are used here.
using System;

namespace BoardGameTutorial.Animation
{
    /// <summary>
    /// Owns the only cue-local playback clock.  Audio, visual and subtitle
    /// executors are driven from this time; none of them may decide that a cue
    /// has completed.
    /// </summary>
    public sealed class CuePlaybackController
    {
        private const double Epsilon = 1e-9;

        private readonly ICueClock clock;
        private readonly IAudioCueExecutor audio;
        private readonly IVisualCueExecutor visual;
        private readonly ICueSubtitleExecutor subtitles;

        private CueTimelinePlan plan;
        private CuePlaybackState state = CuePlaybackState.Idle;
        private long generation;
        private double cueTime;
        private double lastClockSample;
        private bool audioRequired;
        private bool visualRequired;
        private bool audioPrepared;
        private bool visualPrepared;
        private long completedGeneration;
        private int completionCount;
        private int audioCompletionSignalCount;
        private CuePlaybackCompletion lastCompletion;
        private CuePlaybackFailure lastFailure;
        private CueSubtitleState currentSubtitle = CueSubtitleState.Hidden();

        public CuePlaybackController(
            ICueClock cueClock,
            IAudioCueExecutor audioExecutor,
            IVisualCueExecutor visualExecutor,
            ICueSubtitleExecutor subtitleExecutor)
        {
            if (cueClock == null) throw new ArgumentNullException("cueClock");
            clock = cueClock;
            audio = audioExecutor;
            visual = visualExecutor;
            subtitles = subtitleExecutor;
        }

        public CuePlaybackState State { get { return state; } }
        public long Generation { get { return generation; } }
        public CueTimelinePlan Plan { get { return plan; } }
        public double CueTime { get { return cueTime; } }
        public CueSubtitleState CurrentSubtitle { get { return currentSubtitle.Clone(); } }
        public CuePlaybackCompletion LastCompletion { get { return lastCompletion; } }
        public CuePlaybackFailure LastFailure { get { return lastFailure; } }
        public int CompletionCount { get { return completionCount; } }
        public int AudioCompletionSignalCount { get { return audioCompletionSignalCount; } }

        public event Action<CuePlaybackTransition> StateChanged;
        public event Action<CuePlaybackCompletion> Completed;
        public event Action<CuePlaybackFailure> Failed;

        /// <summary>
        /// Cancel any existing cue and begin preparing a new compiled plan.
        /// Preparation completion is asynchronous and generation-tagged.
        /// </summary>
        public bool Prepare(CueTimelinePlan nextPlan)
        {
            if (nextPlan == null)
            {
                Fail(generation, CueExecutorKind.Audio, CuePlaybackFailureKind.InvalidPlan,
                    "plan is null", false);
                return false;
            }

            long previousGeneration = generation;
            if (state != CuePlaybackState.Idle)
            {
                if (!CuePlaybackStateMachine.IsTerminal(state))
                    TransitionTo(CuePlaybackState.Cancelled, previousGeneration);
                generation = NextGeneration();
                StopAndCancelExecutors(previousGeneration);
            }
            else
            {
                generation = NextGeneration();
            }

            plan = nextPlan;
            cueTime = 0.0;
            lastClockSample = clock.NowSeconds;
            audioRequired = HasTrackKind("audio");
            visualRequired = HasTrackKind("visual");
            audioPrepared = !audioRequired;
            visualPrepared = !visualRequired;
            completedGeneration = 0L;
            currentSubtitle = CueSubtitleState.Hidden();

            TransitionTo(CuePlaybackState.Preparing, generation);

            string validationError;
            if (!CueTimelinePlanValidator.TryValidate(plan, out validationError))
            {
                Fail(generation, null, CuePlaybackFailureKind.InvalidPlan,
                    validationError, false);
                return false;
            }
            if (audioRequired && audio == null)
            {
                Fail(generation, CueExecutorKind.Audio, CuePlaybackFailureKind.Preparation,
                    "plan has an audio track but no audio executor", false);
                return false;
            }
            if (visualRequired && visual == null)
            {
                Fail(generation, CueExecutorKind.Visual, CuePlaybackFailureKind.Preparation,
                    "plan has a visual track but no visual executor", false);
                return false;
            }

            if (audioRequired)
            {
                try
                {
                    audio.Prepare(plan, generation,
                        new ExecutorCallback(this, CueExecutorKind.Audio));
                }
                catch (Exception ex)
                {
                    Fail(generation, CueExecutorKind.Audio,
                        CuePlaybackFailureKind.Preparation, ex.Message, false);
                    return false;
                }
            }
            if (state != CuePlaybackState.Preparing) return state == CuePlaybackState.Ready;

            if (visualRequired)
            {
                try
                {
                    visual.Prepare(plan, generation,
                        new ExecutorCallback(this, CueExecutorKind.Visual));
                }
                catch (Exception ex)
                {
                    Fail(generation, CueExecutorKind.Visual,
                        CuePlaybackFailureKind.Preparation, ex.Message, false);
                    return false;
                }
            }

            TryBecomeReady();
            return state == CuePlaybackState.Preparing || state == CuePlaybackState.Ready;
        }

        public bool LoadCue(CueTimelinePlan nextPlan)
        {
            return Prepare(nextPlan);
        }

        /// <summary>
        /// Enter Playing only after both required executors have reported
        /// readiness.  The playback generation advances for every play request.
        /// </summary>
        public bool Play()
        {
            if (state != CuePlaybackState.Ready) return false;

            generation = NextGeneration();
            completedGeneration = 0L;
            lastClockSample = clock.NowSeconds;
            TransitionTo(CuePlaybackState.Playing, generation);

            if (audioRequired)
            {
                try
                {
                    audio.Play(generation, cueTime);
                }
                catch (Exception ex)
                {
                    Fail(generation, CueExecutorKind.Audio,
                        CuePlaybackFailureKind.AudioExecution, ex.Message, false);
                    return false;
                }
                if (state != CuePlaybackState.Playing) return false;
            }
            if (!UpdatePresentation(generation)) return false;
            if (state != CuePlaybackState.Playing) return false;
            if (visualRequired)
            {
                try
                {
                    visual.Resume(generation);
                }
                catch (Exception ex)
                {
                    Fail(generation, CueExecutorKind.Visual,
                        CuePlaybackFailureKind.VisualExecution, ex.Message, false);
                    return false;
                }
            }
            return state == CuePlaybackState.Playing;
        }

        /// <summary>Advance cue-local time from the injected clock.</summary>
        public void Tick()
        {
            if (state != CuePlaybackState.Playing) return;

            double now = clock.NowSeconds;
            double delta = now - lastClockSample;
            lastClockSample = now;
            if (delta <= 0.0) return;

            double next = cueTime + delta;
            if (next >= plan.course.duration - Epsilon)
                cueTime = plan.course.duration;
            else
                cueTime = next;

            if (!UpdatePresentation(generation)) return;
            if (cueTime >= plan.course.duration - Epsilon)
                Complete();
        }

        public bool Pause()
        {
            if (state != CuePlaybackState.Playing) return false;

            Tick();
            if (state != CuePlaybackState.Playing) return false;

            TransitionTo(CuePlaybackState.Paused, generation);
            try
            {
                if (audioRequired) audio.Pause(generation);
                if (state != CuePlaybackState.Paused) return false;
                if (visualRequired) visual.Pause(generation);
            }
            catch (Exception ex)
            {
                CueExecutorKind kind = audioRequired
                    ? CueExecutorKind.Audio : CueExecutorKind.Visual;
                CuePlaybackFailureKind failureKind = audioRequired
                    ? CuePlaybackFailureKind.AudioExecution
                    : CuePlaybackFailureKind.VisualExecution;
                Fail(generation, kind, failureKind, ex.Message, false);
                return false;
            }
            return state == CuePlaybackState.Paused;
        }

        public bool Resume()
        {
            if (state != CuePlaybackState.Paused) return false;

            generation = NextGeneration();
            completedGeneration = 0L;
            lastClockSample = clock.NowSeconds;
            TransitionTo(CuePlaybackState.Playing, generation);

            if (audioRequired)
            {
                try
                {
                    audio.Resume(generation, cueTime);
                }
                catch (Exception ex)
                {
                    Fail(generation, CueExecutorKind.Audio,
                        CuePlaybackFailureKind.AudioExecution, ex.Message, false);
                    return false;
                }
                if (state != CuePlaybackState.Playing) return false;
            }
            if (!UpdatePresentation(generation)) return false;
            if (state != CuePlaybackState.Playing) return false;
            if (visualRequired)
            {
                try
                {
                    visual.Resume(generation);
                }
                catch (Exception ex)
                {
                    Fail(generation, CueExecutorKind.Visual,
                        CuePlaybackFailureKind.VisualExecution, ex.Message, false);
                    return false;
                }
            }
            return state == CuePlaybackState.Playing;
        }

        /// <summary>
        /// Seek to an absolute cue-local time.  Scheduled audio is cancelled
        /// before the new generation is scheduled.  A playing cue resumes at
        /// the target; a paused cue stays paused.  A completed cue seeks to a
        /// paused state.
        /// </summary>
        public bool SeekTo(double targetCueTime)
        {
            if (plan == null) return false;
            if (double.IsNaN(targetCueTime) || double.IsInfinity(targetCueTime)) return false;
            if (state != CuePlaybackState.Playing
                && state != CuePlaybackState.Paused
                && state != CuePlaybackState.Completed)
                return false;

            bool resumeAfterSeek = state == CuePlaybackState.Playing;
            double target = targetCueTime;
            if (target < 0.0) target = 0.0;
            if (target > plan.course.duration) target = plan.course.duration;

            long oldGeneration = generation;
            TransitionTo(CuePlaybackState.Seeking, oldGeneration);
            try
            {
                if (audioRequired) audio.Cancel(oldGeneration);
                if (state != CuePlaybackState.Seeking) return false;
                if (visualRequired) visual.Cancel(oldGeneration);
            }
            catch (Exception ex)
            {
                Fail(oldGeneration, audioRequired ? CueExecutorKind.Audio : CueExecutorKind.Visual,
                    audioRequired ? CuePlaybackFailureKind.AudioExecution
                        : CuePlaybackFailureKind.VisualExecution,
                    ex.Message, false);
                return false;
            }
            if (state != CuePlaybackState.Seeking) return false;

            generation = NextGeneration();
            completedGeneration = 0L;
            cueTime = target;
            lastClockSample = clock.NowSeconds;

            if (audioRequired)
            {
                try
                {
                    audio.Seek(generation, cueTime);
                }
                catch (Exception ex)
                {
                    Fail(generation, CueExecutorKind.Audio,
                        CuePlaybackFailureKind.AudioExecution, ex.Message, false);
                    return false;
                }
                if (state != CuePlaybackState.Seeking) return false;
            }
            if (!UpdatePresentation(generation)) return false;
            if (state != CuePlaybackState.Seeking) return false;

            if (cueTime >= plan.course.duration - Epsilon)
            {
                Complete();
                return state == CuePlaybackState.Completed;
            }

            if (resumeAfterSeek)
            {
                TransitionTo(CuePlaybackState.Playing, generation);
                if (audioRequired)
                {
                    try
                    {
                        audio.Play(generation, cueTime);
                    }
                    catch (Exception ex)
                    {
                        Fail(generation, CueExecutorKind.Audio,
                            CuePlaybackFailureKind.AudioExecution, ex.Message, false);
                        return false;
                    }
                    if (state != CuePlaybackState.Playing) return false;
                }
                if (visualRequired)
                {
                    try
                    {
                        visual.Resume(generation);
                    }
                    catch (Exception ex)
                    {
                        Fail(generation, CueExecutorKind.Visual,
                            CuePlaybackFailureKind.VisualExecution, ex.Message, false);
                        return false;
                    }
                }
            }
            else
            {
                TransitionTo(CuePlaybackState.Paused, generation);
            }
            return state == CuePlaybackState.Playing || state == CuePlaybackState.Paused;
        }

        /// <summary>
        /// Stop scheduled audio/visual work and invalidate every in-flight
        /// generation.  Cancellation never emits a completion event.
        /// </summary>
        public bool Cancel()
        {
            if (CuePlaybackStateMachine.IsTerminal(state)) return false;

            long oldGeneration = generation;
            TransitionTo(CuePlaybackState.Cancelled, oldGeneration);
            generation = NextGeneration();
            StopAndCancelExecutors(oldGeneration);
            audioPrepared = false;
            visualPrepared = false;
            completedGeneration = 0L;
            cueTime = 0.0;
            currentSubtitle = CueSubtitleState.Hidden();
            return true;
        }

        private void TryBecomeReady()
        {
            if (state != CuePlaybackState.Preparing) return;
            if (audioPrepared && visualPrepared)
                TransitionTo(CuePlaybackState.Ready, generation);
        }

        private bool UpdatePresentation(long activeGeneration)
        {
            if (activeGeneration != generation) return false;
            if (visualRequired)
            {
                try
                {
                    visual.Seek(activeGeneration, cueTime);
                }
                catch (Exception ex)
                {
                    Fail(activeGeneration, CueExecutorKind.Visual,
                        CuePlaybackFailureKind.VisualExecution, ex.Message, false);
                    return false;
                }
            }

            try
            {
                currentSubtitle = subtitles != null
                    ? subtitles.GetState(plan, cueTime)
                    : CueSubtitleState.Hidden();
                if (currentSubtitle == null) currentSubtitle = CueSubtitleState.Hidden();
                currentSubtitle = currentSubtitle.Clone();
            }
            catch (Exception ex)
            {
                Fail(activeGeneration, CueExecutorKind.Subtitle,
                    CuePlaybackFailureKind.SubtitleExecution, ex.Message, false);
                return false;
            }
            return true;
        }

        private void Complete()
        {
            if (state != CuePlaybackState.Playing && state != CuePlaybackState.Seeking)
                return;
            if (completedGeneration == generation) return;

            cueTime = plan.course.duration;
            if (!UpdatePresentation(generation)) return;
            try
            {
                if (audioRequired) audio.Stop(generation);
            }
            catch (Exception ex)
            {
                Fail(generation, CueExecutorKind.Audio,
                    CuePlaybackFailureKind.AudioExecution, ex.Message, false);
                return;
            }
            if (state != CuePlaybackState.Playing && state != CuePlaybackState.Seeking)
                return;

            completedGeneration = generation;
            TransitionTo(CuePlaybackState.Completed, generation);
            lastCompletion = new CuePlaybackCompletion
            {
                Generation = generation,
                CueId = plan.cue_id,
                CueTime = cueTime,
                Duration = plan.course.duration,
            };
            completionCount++;
            Action<CuePlaybackCompletion> handler = Completed;
            if (handler != null) handler(lastCompletion);
        }

        private void OnExecutorPrepared(CueExecutorKind kind, long callbackGeneration)
        {
            if (callbackGeneration != generation) return;
            if (state != CuePlaybackState.Preparing) return;

            if (kind == CueExecutorKind.Audio) audioPrepared = true;
            else if (kind == CueExecutorKind.Visual) visualPrepared = true;
            else return;

            TryBecomeReady();
        }

        private void OnExecutorCompleted(CueExecutorKind kind, long callbackGeneration)
        {
            if (callbackGeneration != generation) return;
            if (CuePlaybackStateMachine.IsTerminal(state)) return;

            // This is diagnostic only.  Audio isPlaying=false or an executor
            // completion callback must never complete the cue early.
            if (kind == CueExecutorKind.Audio) audioCompletionSignalCount++;
        }

        private void OnExecutorFailed(CueExecutorKind kind, long callbackGeneration,
            string message)
        {
            if (callbackGeneration != generation) return;
            if (CuePlaybackStateMachine.IsTerminal(state)) return;
            CuePlaybackFailureKind failureKind;
            if (state == CuePlaybackState.Preparing)
            {
                failureKind = CuePlaybackFailureKind.Preparation;
            }
            else if (kind == CueExecutorKind.Audio)
            {
                failureKind = CuePlaybackFailureKind.AudioExecution;
            }
            else if (kind == CueExecutorKind.Visual)
            {
                failureKind = CuePlaybackFailureKind.VisualExecution;
            }
            else
            {
                failureKind = CuePlaybackFailureKind.Execution;
            }
            Fail(callbackGeneration, kind, failureKind, message, false);
        }

        private void Fail(long failedGeneration, CueExecutorKind? executor,
            CuePlaybackFailureKind kind, string message, bool invalidateGeneration)
        {
            if (failedGeneration != generation) return;
            if (state == CuePlaybackState.Failed || state == CuePlaybackState.Completed
                || state == CuePlaybackState.Cancelled)
                return;

            TransitionTo(CuePlaybackState.Failed, failedGeneration);
            if (invalidateGeneration) generation = NextGeneration();
            StopAndCancelExecutors(failedGeneration);
            currentSubtitle = CueSubtitleState.Hidden();
            lastFailure = new CuePlaybackFailure
            {
                Generation = failedGeneration,
                CueId = plan != null ? plan.cue_id : "",
                Executor = executor,
                Kind = kind,
                Message = message ?? "",
            };
            Action<CuePlaybackFailure> handler = Failed;
            if (handler != null) handler(lastFailure);
        }

        private void StopAndCancelExecutors(long activeGeneration)
        {
            if (audioRequired && audio != null)
            {
                TryAudioStop(activeGeneration);
                TryAudioCancel(activeGeneration);
            }
            if (visualRequired && visual != null)
            {
                TryVisualStop(activeGeneration);
                TryVisualCancel(activeGeneration);
            }
        }

        private void TryAudioStop(long activeGeneration)
        {
            try { audio.Stop(activeGeneration); } catch (Exception) { }
        }

        private void TryAudioCancel(long activeGeneration)
        {
            try { audio.Cancel(activeGeneration); } catch (Exception) { }
        }

        private void TryVisualStop(long activeGeneration)
        {
            try { visual.Stop(activeGeneration); } catch (Exception) { }
        }

        private void TryVisualCancel(long activeGeneration)
        {
            try { visual.Cancel(activeGeneration); } catch (Exception) { }
        }

        private bool HasTrackKind(string kind)
        {
            if (plan == null || plan.tracks == null) return false;
            for (int i = 0; i < plan.tracks.Count; i++)
            {
                if (plan.tracks[i] != null && plan.tracks[i].kind == kind) return true;
            }
            return false;
        }

        private long NextGeneration()
        {
            generation++;
            if (generation <= 0L) generation = 1L;
            return generation;
        }

        private void TransitionTo(CuePlaybackState nextState, long transitionGeneration)
        {
            if (!CuePlaybackStateMachine.CanTransition(state, nextState))
            {
                throw new InvalidOperationException(
                    "illegal cue playback transition " + state + " -> " + nextState);
            }
            CuePlaybackState previous = state;
            state = nextState;
            Action<CuePlaybackTransition> handler = StateChanged;
            if (handler != null)
            {
                handler(new CuePlaybackTransition
                {
                    From = previous,
                    To = nextState,
                    Generation = transitionGeneration,
                });
            }
        }

        private sealed class ExecutorCallback : ICueExecutorCallback
        {
            private readonly CuePlaybackController owner;
            private readonly CueExecutorKind kind;

            public ExecutorCallback(CuePlaybackController owner, CueExecutorKind kind)
            {
                this.owner = owner;
                this.kind = kind;
            }

            public void OnPrepared(long callbackGeneration)
            {
                owner.OnExecutorPrepared(kind, callbackGeneration);
            }

            public void OnCompleted(long callbackGeneration)
            {
                owner.OnExecutorCompleted(kind, callbackGeneration);
            }

            public void OnFailed(long callbackGeneration, string message)
            {
                owner.OnExecutorFailed(kind, callbackGeneration, message);
            }
        }
    }
}
