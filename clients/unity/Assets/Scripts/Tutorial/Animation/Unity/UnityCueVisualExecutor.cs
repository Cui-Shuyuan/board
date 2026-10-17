// Visual executor for the cue timeline.  It only reads cue-local time and
// drives TutorialAnimPlayer; it never touches audio or declares completion.
using System;
using UnityEngine;

namespace BoardGameTutorial.Animation
{
    public sealed class UnityCueVisualExecutor : IVisualCueExecutor
    {
        private readonly TutorialAnimPlayer animPlayer;
        private readonly string gameRoot;
        private CueTimelinePlan plan;
        private ICueExecutorCallback callback;
        private long activeGeneration;
        private bool visualPrepared;
        private string loadedAnimationId = "";
        private double lastAnimationSourceEnd;

        public UnityCueVisualExecutor(TutorialAnimPlayer animPlayer, string gameRoot)
        {
            if (animPlayer == null) throw new ArgumentNullException("animPlayer");
            this.animPlayer = animPlayer;
            this.gameRoot = gameRoot ?? "";
        }

        public void Prepare(CueTimelinePlan nextPlan, long generation, ICueExecutorCallback executorCallback)
        {
            plan = nextPlan;
            callback = executorCallback;
            activeGeneration = generation;
            visualPrepared = false;
            loadedAnimationId = "";
            lastAnimationSourceEnd = 0.0;

            CueTimelineTrack visualTrack = CueTimelineTrackResolver.FindTrack(plan, "visual");
            if (visualTrack == null || visualTrack.segments == null)
            {
                visualPrepared = true;
                callback.OnPrepared(generation);
                return;
            }

            int animationCount = 0;
            string firstAnimation = "";
            for (int i = 0; i < visualTrack.segments.Count; i++)
            {
                CueTimelineSegment segment = visualTrack.segments[i];
                if (segment == null || segment.type != "animation") continue;
                animationCount++;
                if (string.IsNullOrEmpty(firstAnimation)) firstAnimation = segment.animation;
                lastAnimationSourceEnd = Math.Max(lastAnimationSourceEnd, segment.source_end);
            }

            if (animationCount > 1)
            {
                // The current compiled track keys visual material by cue id and
                // this phase only wires one independent visual reference plus
                // holds.  Fail explicitly instead of pretending multi-clip
                // visual support exists.
                CallbackFail(generation,
                    "multiple independent visual animation segments are not supported yet; compiler blocker documented");
                return;
            }

            if (!string.IsNullOrEmpty(firstAnimation))
            {
                if (!animPlayer.LoadCue(firstAnimation))
                {
                    CallbackFail(generation, "visual cue load failed: " + firstAnimation);
                    return;
                }
                loadedAnimationId = firstAnimation;
            }
            visualPrepared = true;
            callback.OnPrepared(generation);
        }

        public void Seek(long generation, double cueTime)
        {
            if (!AcceptGeneration(generation)) return;
            CueTimelineTrack track = CueTimelineTrackResolver.FindTrack(plan, "visual");
            CueTimelineSegment segment = CueTimelineTrackResolver.FindSegment(track, cueTime);
            if (segment == null)
            {
                if (lastAnimationSourceEnd > 0.0)
                    animPlayer.Seek((float)lastAnimationSourceEnd);
                return;
            }

            if (segment.type == "animation")
            {
                if (loadedAnimationId != segment.animation)
                {
                    if (!animPlayer.LoadCue(segment.animation))
                    {
                        CallbackFail(generation, "visual cue load failed: " + segment.animation);
                        return;
                    }
                    loadedAnimationId = segment.animation;
                }
                lastAnimationSourceEnd = Math.Max(lastAnimationSourceEnd, segment.source_end);
                double sourceTime = CueTimelineTrackResolver.AnimationSourceTime(
                    segment, cueTime, lastAnimationSourceEnd);
                animPlayer.Seek((float)sourceTime);
            }
            else if (segment.type == "hold")
            {
                animPlayer.Seek((float)lastAnimationSourceEnd);
            }
        }

        public void Pause(long generation)
        {
            // The controller freezes cue time.  There is no separate visual
            // clock to pause.
            AcceptGeneration(generation);
        }

        public void Resume(long generation)
        {
            // Next controller Tick seeks to the authoritative cue time.
            AcceptGeneration(generation);
        }

        public void Stop(long generation)
        {
            AcceptGeneration(generation);
        }

        public void Cancel(long generation)
        {
            if (generation != 0L && generation != activeGeneration) return;
            // Preserve the loaded plan/visual reference for the immediate
            // Seek(newGeneration) sequence used by controller seek.  A real
            // cue switch follows with Prepare(newPlan), which replaces it.
            activeGeneration = 0L;
        }

        public void Dispose()
        {
            activeGeneration = 0L;
            visualPrepared = false;
            plan = null;
            callback = null;
            if (animPlayer != null) animPlayer.ClearScene();
        }

        private bool AcceptGeneration(long generation)
        {
            if (generation == 0L || plan == null || !visualPrepared) return false;
            // Visual has no asynchronous playback callbacks.  Every controller
            // play/resume/seek advances generation, so follow the active one.
            activeGeneration = generation;
            return true;
        }

        private void CallbackFail(long generation, string message)
        {
            Debug.LogError("[CueTimelineV1][Visual] " + message + " gen=" + generation);
            if (callback != null) callback.OnFailed(generation, message);
        }
    }
}
