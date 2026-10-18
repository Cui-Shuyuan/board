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
        private bool sceneCleared;
        private string loadedAnimationId = "";

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
            sceneCleared = false;

            CueTimelineTrack visualTrack = CueTimelineTrackResolver.FindTrack(plan, "visual");
            if (visualTrack == null || visualTrack.segments == null
                || visualTrack.segments.Count == 0)
            {
                visualPrepared = true;
                callback.OnPrepared(generation);
                return;
            }

            // Validate every compiled animation reference before declaring the
            // cue ready.  This is loader validation, not a second clock.
            var seen = new System.Collections.Generic.HashSet<string>(
                StringComparer.Ordinal);
            for (int i = 0; i < visualTrack.segments.Count; i++)
            {
                CueTimelineSegment segment = visualTrack.segments[i];
                if (segment == null || segment.type != "animation") continue;
                if (string.IsNullOrEmpty(segment.animation))
                {
                    CallbackFail(generation, "visual segment has no animation id");
                    return;
                }
                if (!seen.Add(segment.animation)) continue;
                float ignored;
                if (!animPlayer.TryGetCueDuration(segment.animation, out ignored))
                {
                    CallbackFail(generation,
                        "visual cue load failed: " + segment.animation);
                    return;
                }
            }

            CueVisualFrame initial = CueVisualTimelineResolver.Resolve(visualTrack, 0.0);
            if (!ApplyFrame(initial, generation)) return;
            visualPrepared = true;
            callback.OnPrepared(generation);
        }

        public void Seek(long generation, double cueTime)
        {
            if (!AcceptGeneration(generation)) return;
            CueTimelineTrack track = CueTimelineTrackResolver.FindTrack(plan, "visual");
            CueVisualFrame frame = CueVisualTimelineResolver.Resolve(track, cueTime);
            ApplyFrame(frame, generation);
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
            sceneCleared = false;
            loadedAnimationId = "";
            plan = null;
            callback = null;
            if (animPlayer != null) animPlayer.ClearScene();
        }

        private bool ApplyFrame(CueVisualFrame frame, long generation)
        {
            if (frame == null || frame.Kind == CueVisualFrameKind.None
                || frame.Kind == CueVisualFrameKind.Clear)
            {
                if (!sceneCleared)
                {
                    animPlayer.ClearScene();
                    sceneCleared = true;
                    loadedAnimationId = "";
                }
                return true;
            }

            sceneCleared = false;
            if (loadedAnimationId != frame.AnimationId)
            {
                if (!animPlayer.LoadCue(frame.AnimationId))
                {
                    CallbackFail(generation,
                        "visual cue load failed: " + frame.AnimationId);
                    return false;
                }
                loadedAnimationId = frame.AnimationId;
            }
            animPlayer.Seek((float)frame.SourceTime);
            return true;
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
