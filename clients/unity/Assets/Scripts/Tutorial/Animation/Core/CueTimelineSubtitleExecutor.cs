// Pure cue-time subtitle selection shared by Unity and tests.
using System;

namespace BoardGameTutorial.Animation
{
    public sealed class CueTimelineSubtitleExecutor : ICueSubtitleExecutor
    {
        private const double Epsilon = 1e-9;

        public CueSubtitleState GetState(CueTimelinePlan plan, double cueTime)
        {
            if (plan == null) return CueSubtitleState.Hidden();
            // New plans carry subtitles as a real track so all track kinds use
            // the same cue-local segment scheduling model.
            if (plan.tracks != null)
            {
                for (int ti = 0; ti < plan.tracks.Count; ti++)
                {
                    CueTimelineTrack track = plan.tracks[ti];
                    if (track == null || track.kind != "subtitle"
                        || track.segments == null)
                        continue;
                    for (int si = 0; si < track.segments.Count; si++)
                    {
                        CueTimelineSegment segment = track.segments[si];
                        if (segment == null || segment.type != "subtitle")
                            continue;
                        if (cueTime + Epsilon >= segment.cue_start
                            && cueTime < segment.EndCueTime - Epsilon)
                        {
                            return new CueSubtitleState
                            {
                                Visible = true,
                                Text = segment.text ?? "",
                                SegmentId = segment.id ?? ("subtitle." + si),
                            };
                        }
                    }
                }
            }
            // Backward compatibility with older plan files.
            if (plan.subtitles == null) return CueSubtitleState.Hidden();
            for (int i = 0; i < plan.subtitles.Count; i++)
            {
                CueTimelineSubtitle subtitle = plan.subtitles[i];
                if (subtitle == null) continue;
                if (cueTime + Epsilon >= subtitle.cue_start
                    && cueTime < subtitle.EndCueTime - Epsilon)
                {
                    return new CueSubtitleState
                    {
                        Visible = true,
                        Text = subtitle.text ?? "",
                        SegmentId = subtitle.id ?? ("subtitle." + i),
                    };
                }
            }
            return CueSubtitleState.Hidden();
        }
    }
}
