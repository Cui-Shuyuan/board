// Pure cue-time subtitle selection shared by Unity and tests.
using System;

namespace BoardGameTutorial.Animation
{
    public sealed class CueTimelineSubtitleExecutor : ICueSubtitleExecutor
    {
        private const double Epsilon = 1e-9;

        public CueSubtitleState GetState(CueTimelinePlan plan, double cueTime)
        {
            if (plan == null || plan.subtitles == null) return CueSubtitleState.Hidden();
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
