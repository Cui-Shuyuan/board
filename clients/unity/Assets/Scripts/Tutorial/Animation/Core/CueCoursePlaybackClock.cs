// Pure DSP-epoch course clock.  This is the sole time authority for the
// course-level transport: cue-local time, subtitles, visuals and progress are
// derived from CourseTime, never accumulated independently.
using System;

namespace BoardGameTutorial.Animation
{
    public sealed class CueCoursePlaybackClock
    {
        private readonly ICueClock clock;
        private double anchorCourseTime;
        private double anchorDspTime;
        private double frozenCourseTime;
        private bool anchored;
        private double maximumCourseTime = double.PositiveInfinity;

        public CueCoursePlaybackClock(ICueClock cueClock)
        {
            if (cueClock == null) throw new ArgumentNullException("cueClock");
            clock = cueClock;
        }

        public void SetMaximumCourseTime(double maximum)
        {
            if (double.IsNaN(maximum) || maximum < 0.0)
                throw new ArgumentOutOfRangeException("maximum");
            maximumCourseTime = maximum;
        }

        public void AnchorAt(double courseTime, double dspTime)
        {
            if (double.IsNaN(courseTime) || double.IsInfinity(courseTime)
                || courseTime < 0.0)
                throw new ArgumentOutOfRangeException("courseTime");
            if (double.IsNaN(dspTime) || double.IsInfinity(dspTime))
                throw new ArgumentOutOfRangeException("dspTime");
            anchorCourseTime = courseTime;
            anchorDspTime = dspTime;
            frozenCourseTime = courseTime;
            anchored = true;
        }

        public void Freeze(double courseTime)
        {
            if (double.IsNaN(courseTime) || double.IsInfinity(courseTime)
                || courseTime < 0.0)
                throw new ArgumentOutOfRangeException("courseTime");
            frozenCourseTime = courseTime;
            anchorCourseTime = courseTime;
            anchored = false;
        }

        public void Clear()
        {
            anchored = false;
            frozenCourseTime = 0.0;
            anchorCourseTime = 0.0;
            anchorDspTime = 0.0;
        }

        public bool IsAnchored
        {
            get { return anchored; }
        }

        public double CourseTime
        {
            get
            {
                if (!anchored) return Clamp(frozenCourseTime);
                double now = clock.NowSeconds;
                double elapsed = now - anchorDspTime;
                // Before a scheduled start the course stays frozen.  This is
                // what prevents subtitles/visuals from advancing while the
                // first audio samples have not reached the device yet.
                if (elapsed < 0.0) return Clamp(anchorCourseTime);
                return Clamp(anchorCourseTime + elapsed);
            }
        }

        public double DspTimeForCourseTime(double courseTime)
        {
            if (!anchored) return clock.NowSeconds;
            return anchorDspTime + (courseTime - anchorCourseTime);
        }

        private double Clamp(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > maximumCourseTime) return maximumCourseTime;
            return value;
        }
    }

    public static class CueCourseTimelineResolver
    {
        private const double Epsilon = 1e-9;

        /// <summary>
        /// Finds the cue containing course time.  The interval is half-open
        /// [cue.start, cue.end); an exact cue boundary selects the next cue.
        /// At/after the course end the final cue is returned for completion
        /// bookkeeping.
        /// </summary>
        public static int FindCueIndex(CueTimelinePlanSet planSet, double courseTime)
        {
            if (planSet == null || planSet.cues == null || planSet.cues.Count == 0)
                return -1;
            if (courseTime <= 0.0) return 0;

            int low = 0;
            int high = planSet.cues.Count - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                CueTimelinePlan plan = planSet.cues[mid];
                if (plan != null && plan.course != null
                    && plan.course.start <= courseTime + Epsilon)
                    low = mid;
                else
                    high = mid - 1;
            }
            return low;
        }

        public static double CueEnd(CueTimelinePlan plan)
        {
            if (plan == null || plan.course == null) return 0.0;
            return plan.course.start + plan.course.duration;
        }

        public static double ClampToCueLocal(CueTimelinePlan plan, double courseTime)
        {
            if (plan == null || plan.course == null) return 0.0;
            double local = courseTime - plan.course.start;
            if (local < 0.0) return 0.0;
            if (local > plan.course.duration) return plan.course.duration;
            return local;
        }
    }
}
