// Pure cue-time -> segment/source-time resolution shared by Unity executors.
using System;

namespace BoardGameTutorial.Animation
{
    public static class CueTimelineTrackResolver
    {
        private const double Epsilon = 1e-9;

        public static CueTimelineTrack FindTrack(CueTimelinePlan plan, string kind)
        {
            if (plan == null || plan.tracks == null || string.IsNullOrEmpty(kind)) return null;
            for (int i = 0; i < plan.tracks.Count; i++)
            {
                CueTimelineTrack track = plan.tracks[i];
                if (track != null && track.kind == kind) return track;
            }
            return null;
        }

        /// <summary>
        /// Segments are half-open [cue_start, end).  At an exact boundary the
        /// segment beginning at that boundary wins.
        /// </summary>
        public static CueTimelineSegment FindSegment(CueTimelineTrack track, double cueTime)
        {
            if (track == null || track.segments == null) return null;
            for (int i = 0; i < track.segments.Count; i++)
            {
                CueTimelineSegment segment = track.segments[i];
                if (segment == null) continue;
                if (cueTime + Epsilon >= segment.cue_start
                    && cueTime < segment.EndCueTime - Epsilon)
                    return segment;
            }
            return null;
        }

        public static double ClampSegmentLocalTime(CueTimelineSegment segment, double cueTime)
        {
            if (segment == null) return 0.0;
            double local = cueTime - segment.cue_start;
            if (local < 0.0) local = 0.0;
            double duration = segment.CueDuration;
            if (local > duration) local = duration;
            return local;
        }

        /// <summary>
        /// Resolve an audio clip's material time.  This is derived from the
        /// controller-owned cue time, not from an independently running player
        /// clock.
        /// </summary>
        public static double AudioSourceTime(CueTimelineSegment segment, double cueTime)
        {
            if (segment == null || segment.type != "audio_clip") return 0.0;
            return segment.source_start + ClampSegmentLocalTime(segment, cueTime);
        }

        /// <summary>
        /// Resolve an animation's material time.  A hold segment freezes at the
        /// supplied hold source time (normally the last animation's source_end).
        /// </summary>
        public static double AnimationSourceTime(CueTimelineSegment segment, double cueTime,
            double holdSourceTime)
        {
            if (segment == null) return 0.0;
            if (segment.type == "hold") return holdSourceTime;
            if (segment.type != "animation") return holdSourceTime;
            return segment.source_start + ClampSegmentLocalTime(segment, cueTime);
        }
    }
}
