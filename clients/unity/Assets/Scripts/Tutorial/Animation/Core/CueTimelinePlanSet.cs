// Serializable envelope for the Phase 4 shared track plan set.
//
// Android and Unity read the exact same tutorial/{track}.cue-timeline.json.
// The validator below is deliberately strict and whole-track: one bad or
// missing cue rejects the entire plan set instead of allowing a mixed timeline.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    [Serializable]
    public sealed class CueTimelinePlanSet
    {
        public string schema;
        public int schema_version;
        public string game_id;
        public string track;
        public CueTimelineCourse course;
        public List<CueTimelinePlan> cues = new List<CueTimelinePlan>();
    }

    public static class CueTimelinePlanSetValidator
    {
        public const string Schema = "tutorial-cue-plan-set/v1";
        public const int SchemaVersion = 1;
        private const double Epsilon = 1e-4;

        public static void Validate(
            CueTimelinePlanSet set,
            string expectedGameId,
            string expectedTrack,
            IList<string> expectedCueIds)
        {
            string error;
            if (!TryValidate(set, expectedGameId, expectedTrack,
                expectedCueIds, out error))
            {
                throw new ArgumentException(error, "set");
            }
        }

        public static bool TryValidate(
            CueTimelinePlanSet set,
            string expectedGameId,
            string expectedTrack,
            IList<string> expectedCueIds,
            out string error)
        {
            if (set == null)
            {
                error = "plan set is null";
                return false;
            }
            if (set.schema != Schema)
            {
                error = "plan set schema must be '" + Schema + "'";
                return false;
            }
            if (set.schema_version != SchemaVersion)
            {
                error = "plan set schema_version must be " + SchemaVersion;
                return false;
            }
            if (!string.IsNullOrEmpty(expectedGameId)
                && set.game_id != expectedGameId)
            {
                error = "plan set game_id mismatch: expected '" + expectedGameId
                    + "', got '" + set.game_id + "'";
                return false;
            }
            if (set.track != expectedTrack)
            {
                error = "plan set track mismatch: expected '" + expectedTrack
                    + "', got '" + set.track + "'";
                return false;
            }
            if (set.course == null)
            {
                error = "plan set course is missing";
                return false;
            }
            if (!IsFiniteNonNegative(set.course.start)
                || Math.Abs(set.course.start) > Epsilon)
            {
                error = "plan set course.start must be 0";
                return false;
            }
            if (!IsFinitePositive(set.course.duration))
            {
                error = "plan set course.duration must be positive";
                return false;
            }
            if (set.cues == null || set.cues.Count == 0)
            {
                error = "plan set has no cues";
                return false;
            }
            if (expectedCueIds == null || expectedCueIds.Count != set.cues.Count)
            {
                error = "plan set cue coverage mismatch: runtime="
                    + (expectedCueIds == null ? 0 : expectedCueIds.Count)
                    + " plan=" + set.cues.Count;
                return false;
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            double previousEnd = 0.0;
            bool hasPrevious = false;
            for (int i = 0; i < set.cues.Count; i++)
            {
                CueTimelinePlan plan = set.cues[i];
                string cueError;
                if (!CueTimelinePlanValidator.TryValidate(plan, out cueError))
                {
                    error = "plan set cues[" + i + "]: " + cueError;
                    return false;
                }
                if (!string.Equals(plan.cue_id, expectedCueIds[i],
                    StringComparison.Ordinal))
                {
                    error = "plan set cue order/coverage mismatch at " + i
                        + ": runtime='" + expectedCueIds[i]
                        + "', plan='" + plan.cue_id + "'";
                    return false;
                }
                if (!seenIds.Add(plan.cue_id))
                {
                    error = "plan set duplicate cue id '" + plan.cue_id + "'";
                    return false;
                }
                if (hasPrevious && plan.course.start < previousEnd - Epsilon)
                {
                    error = "plan set cue '" + plan.cue_id
                        + "' starts before the previous cue ends";
                    return false;
                }
                previousEnd = plan.course.start + plan.course.duration;
                hasPrevious = true;
            }

            if (!hasPrevious || Math.Abs(set.course.duration - previousEnd) > Epsilon)
            {
                error = "plan set course.duration mismatch: declared="
                    + set.course.duration + " cueEnd=" + previousEnd;
                return false;
            }
            error = "";
            return true;
        }

        public static CueTimelinePlan FindCue(CueTimelinePlanSet set, string cueId)
        {
            if (set == null || set.cues == null || string.IsNullOrEmpty(cueId))
                return null;
            for (int i = 0; i < set.cues.Count; i++)
            {
                CueTimelinePlan plan = set.cues[i];
                if (plan != null && plan.cue_id == cueId) return plan;
            }
            return null;
        }

        private static bool IsFiniteNonNegative(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0;
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;
        }
    }
}
