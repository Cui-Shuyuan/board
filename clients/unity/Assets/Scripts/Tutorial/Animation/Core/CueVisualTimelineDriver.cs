// Pure cue-local visual timeline resolution.
//
// A visual track may contain any number of animation and hold segments.  The
// authoritative cue clock decides which segment is active; this helper never
// starts, stops or observes an audio source.
using System;

namespace BoardGameTutorial.Animation
{
    public enum CueVisualFrameKind
    {
        None = 0,
        Animation = 1,
        Hold = 2,
        Clear = 3,
    }

    public sealed class CueVisualFrame
    {
        public CueVisualFrameKind Kind;
        public string SegmentId;
        public string AnimationId;
        public double SourceTime;

        public static CueVisualFrame None()
        {
            return new CueVisualFrame { Kind = CueVisualFrameKind.None };
        }

        public static CueVisualFrame Clear(string segmentId)
        {
            return new CueVisualFrame
            {
                Kind = CueVisualFrameKind.Clear,
                SegmentId = segmentId ?? "",
            };
        }
    }

    /// <summary>
    /// Resolves cue-local time to a deterministic visual operation.
    ///
    /// Rules:
    /// * an active animation segment seeks its own source range;
    /// * an active hold segment freezes at the immediately preceding
    ///   animation's source_end, or clears if no animation preceded it;
    /// * a gap after an animation follows that animation's after_end policy;
    /// * a gap after a hold follows the visual track tail_policy;
    /// * no preceding visual material means blank/clear, never a stale frame.
    ///
    /// Multiple animation segments may reference the same compiled cue with
    /// different source ranges, or different compiled cues.  Different compiled
    /// cues each own their compiled start_state; switching between them is an
    /// explicit state reset and does not attempt to inherit runtime state.
    /// </summary>
    public static class CueVisualTimelineResolver
    {
        private const double Epsilon = 1e-9;

        public static CueVisualFrame Resolve(CueTimelineTrack track, double cueTime)
        {
            if (track == null || track.segments == null || track.segments.Count == 0)
                return CueVisualFrame.None();

            CueTimelineSegment active = null;
            for (int i = 0; i < track.segments.Count; i++)
            {
                CueTimelineSegment segment = track.segments[i];
                if (segment == null) continue;
                if (cueTime + Epsilon >= segment.cue_start
                    && cueTime < segment.EndCueTime - Epsilon)
                {
                    active = segment;
                    break;
                }
            }

            if (active != null)
            {
                if (active.type == "animation")
                {
                    double local = ClampLocal(active, cueTime);
                    return new CueVisualFrame
                    {
                        Kind = CueVisualFrameKind.Animation,
                        SegmentId = active.id,
                        AnimationId = active.animation,
                        SourceTime = active.source_start + local,
                    };
                }
                if (active.type == "state")
                {
                    return new CueVisualFrame
                    {
                        Kind = CueVisualFrameKind.Animation,
                        SegmentId = active.id,
                        AnimationId = active.animation,
                        SourceTime = 0.0,
                    };
                }
                if (active.type == "hold")
                {
                    CueTimelineSegment previous = PreviousAnimation(track, active);
                    if (previous == null) return CueVisualFrame.Clear(active.id);
                    return new CueVisualFrame
                    {
                        Kind = CueVisualFrameKind.Hold,
                        SegmentId = active.id,
                        AnimationId = previous.animation,
                        SourceTime = previous.type == "state"
                            ? 0.0 : previous.source_end,
                    };
                }
                return CueVisualFrame.Clear(active.id);
            }

            CueTimelineSegment latest = null;
            for (int i = 0; i < track.segments.Count; i++)
            {
                CueTimelineSegment segment = track.segments[i];
                if (segment == null) continue;
                if (segment.EndCueTime <= cueTime + Epsilon)
                {
                    if (latest == null || segment.EndCueTime >= latest.EndCueTime)
                        latest = segment;
                }
            }

            if (latest == null) return CueVisualFrame.Clear("");

            if (latest.type == "animation" || latest.type == "state")
            {
                if (latest.after_end == "hold_last_frame")
                {
                    return new CueVisualFrame
                    {
                        Kind = CueVisualFrameKind.Hold,
                        SegmentId = latest.id,
                        AnimationId = latest.animation,
                        SourceTime = latest.type == "state"
                            ? 0.0 : latest.source_end,
                    };
                }
                return CueVisualFrame.Clear(latest.id);
            }

            if (latest.type == "hold")
            {
                CueTimelineSegment previous = PreviousAnimation(track, latest);
                if (previous != null && track.tail_policy == "hold_last_frame")
                {
                    return new CueVisualFrame
                    {
                        Kind = CueVisualFrameKind.Hold,
                        SegmentId = latest.id,
                        AnimationId = previous.animation,
                        SourceTime = previous.type == "state"
                            ? 0.0 : previous.source_end,
                    };
                }
                return CueVisualFrame.Clear(latest.id);
            }

            return CueVisualFrame.Clear(latest.id);
        }

        private static CueTimelineSegment PreviousAnimation(
            CueTimelineTrack track, CueTimelineSegment current)
        {
            CueTimelineSegment previous = null;
            for (int i = 0; i < track.segments.Count; i++)
            {
                CueTimelineSegment candidate = track.segments[i];
                if (candidate == null
                    || (candidate.type != "animation" && candidate.type != "state"))
                    continue;
                if (candidate.EndCueTime > current.cue_start + Epsilon) continue;
                bool adjacent = candidate.EndCueTime >= current.cue_start - Epsilon;
                if (!adjacent && candidate.after_end != "hold_last_frame") continue;
                if (previous == null || candidate.EndCueTime >= previous.EndCueTime)
                    previous = candidate;
            }
            return previous;
        }

        private static double ClampLocal(CueTimelineSegment segment, double cueTime)
        {
            double local = cueTime - segment.cue_start;
            if (local < 0.0) return 0.0;
            double duration = segment.CueDuration;
            return local > duration ? duration : local;
        }
    }
}
