// Pure .NET types and executor contracts for the cue timeline playback layer.
//
// This file deliberately has no UnityEngine dependency.  The controller owns
// cue-local playback time; executors only perform work for a generation and
// report generation-tagged lifecycle events.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    public enum CuePlaybackState
    {
        Idle = 0,
        Preparing = 1,
        Ready = 2,
        Playing = 3,
        Paused = 4,
        Seeking = 5,
        Completed = 6,
        Cancelled = 7,
        Failed = 8,
    }

    public static class CuePlaybackStateMachine
    {
        public static bool IsTerminal(CuePlaybackState state)
        {
            return state == CuePlaybackState.Completed
                || state == CuePlaybackState.Cancelled
                || state == CuePlaybackState.Failed;
        }

        /// <summary>
        /// Explicit state graph for the controller.  A new cue may start from a
        /// terminal state, and any non-terminal state may be cancelled or fail.
        /// </summary>
        public static bool CanTransition(CuePlaybackState from, CuePlaybackState to)
        {
            if (from == to) return false;
            if (to == CuePlaybackState.Failed) return true;
            if (to == CuePlaybackState.Cancelled) return !IsTerminal(from);

            switch (from)
            {
                case CuePlaybackState.Idle:
                    return to == CuePlaybackState.Preparing;
                case CuePlaybackState.Preparing:
                    return to == CuePlaybackState.Ready;
                case CuePlaybackState.Ready:
                    return to == CuePlaybackState.Playing;
                case CuePlaybackState.Playing:
                    return to == CuePlaybackState.Paused
                        || to == CuePlaybackState.Seeking
                        || to == CuePlaybackState.Completed;
                case CuePlaybackState.Paused:
                    return to == CuePlaybackState.Playing
                        || to == CuePlaybackState.Seeking;
                case CuePlaybackState.Seeking:
                    return to == CuePlaybackState.Playing
                        || to == CuePlaybackState.Paused
                        || to == CuePlaybackState.Completed;
                case CuePlaybackState.Completed:
                    return to == CuePlaybackState.Seeking
                        || to == CuePlaybackState.Preparing;
                case CuePlaybackState.Cancelled:
                case CuePlaybackState.Failed:
                    return to == CuePlaybackState.Preparing;
                default:
                    return false;
            }
        }
    }

    public enum CueExecutorKind
    {
        Audio = 0,
        Visual = 1,
        Subtitle = 2,
    }

    public enum CuePlaybackFailureKind
    {
        InvalidPlan = 0,
        Preparation = 1,
        AudioExecution = 2,
        VisualExecution = 3,
        SubtitleExecution = 4,
        Execution = 5,
    }

    public sealed class CuePlaybackTransition
    {
        public CuePlaybackState From;
        public CuePlaybackState To;
        public long Generation;
    }

    public sealed class CuePlaybackCompletion
    {
        public long Generation;
        public string CueId;
        public double CueTime;
        public double Duration;
    }

    public sealed class CuePlaybackFailure
    {
        public long Generation;
        public string CueId;
        public CueExecutorKind? Executor;
        public CuePlaybackFailureKind Kind;
        public string Message;
    }

    [Serializable]
    public sealed class CueTimelinePlan
    {
        public string schema;
        public string cue_id;
        public string entry;
        public CueTimelineCourse course;
        public List<CueTimelineTrack> tracks = new List<CueTimelineTrack>();
        // Subtitle timing is presentation data only.  It never controls audio
        // scheduling or cue completion.
        public List<CueTimelineSubtitle> subtitles = new List<CueTimelineSubtitle>();
        // Navigation/presentation metadata carried by the shared plan.  These
        // fields never participate in timing or playback completion.
        public string text;
        public List<string> group_path = new List<string>();
        public List<string> refs = new List<string>();
    }

    [Serializable]
    public sealed class CueTimelineSubtitle
    {
        public string id;
        public double cue_start;
        public double duration;
        public string text;

        public double EndCueTime
        {
            get { return cue_start + duration; }
        }
    }

    [Serializable]
    public sealed class CueTimelineCourse
    {
        public double start;
        public double duration;
    }

    [Serializable]
    public sealed class CueTimelineTrack
    {
        public string id;
        public string kind;
        public string tail_policy;
        public List<CueTimelineSegment> segments = new List<CueTimelineSegment>();
    }

    [Serializable]
    public sealed class CueTimelineSegment
    {
        public string id;
        public string type;
        public double cue_start;
        public double duration;
        public double source_start;
        public double source_end;
        public long source_start_sample;
        public long source_end_sample;
        public string asset;
        public string animation;
        public string pose;
        public string after_end;
        public string text;

        public double CueDuration
        {
            get
            {
                if (type == "hold" || type == "silence" || type == "subtitle")
                    return duration;
                return source_end - source_start;
            }
        }

        public double EndCueTime
        {
            get { return cue_start + CueDuration; }
        }
    }

    public sealed class CueSubtitleState
    {
        public bool Visible;
        public string Text;
        public string SegmentId;

        public static CueSubtitleState Hidden()
        {
            return new CueSubtitleState { Visible = false, Text = "", SegmentId = "" };
        }

        public CueSubtitleState Clone()
        {
            return new CueSubtitleState
            {
                Visible = Visible,
                Text = Text,
                SegmentId = SegmentId,
            };
        }
    }

    /// <summary>
    /// Authoritative wall/dsp clock source.  The controller converts this
    /// monotonic source into its own cue-local time and freezes that time when
    /// paused.  Phase 3 can provide a DSP-backed implementation.
    /// </summary>
    public interface ICueClock
    {
        double NowSeconds { get; }
    }

    /// <summary>Deterministic clock for tests and non-Unity hosts.</summary>
    public sealed class ManualCueClock : ICueClock
    {
        public double NowSeconds { get; private set; }

        public void Advance(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
                throw new ArgumentOutOfRangeException("seconds");
            NowSeconds += seconds;
        }

        public void Reset()
        {
            NowSeconds = 0.0;
        }
    }

    /// <summary>
    /// Generation-tagged lifecycle callback shared by audio and visual
    /// executors.  A completion is diagnostic; only the controller's cue clock
    /// can complete a cue.
    /// </summary>
    public interface ICueExecutorCallback
    {
        void OnPrepared(long generation);
        void OnCompleted(long generation);
        void OnFailed(long generation, string message);
    }

    public interface IAudioCueExecutor
    {
        void Prepare(CueTimelinePlan plan, long generation, ICueExecutorCallback callback);
        void Play(long generation, double cueTime);
        void Pause(long generation);
        void Resume(long generation, double cueTime);
        void Seek(long generation, double cueTime);
        void Stop(long generation);
        void Cancel(long generation);
    }

    /// <summary>
    /// Optional per-tick synchronization hook.  The controller owns cue time;
    /// an audio executor may use this hook to start/stop the segment selected
    /// by the authoritative cue time without introducing a second clock.
    /// </summary>
    public interface IAudioCueExecutorTimeline
    {
        void SyncToCueTime(long generation, double cueTime);
    }

    public interface IVisualCueExecutor
    {
        void Prepare(CueTimelinePlan plan, long generation, ICueExecutorCallback callback);
        void Seek(long generation, double cueTime);
        void Pause(long generation);
        void Resume(long generation);
        void Stop(long generation);
        void Cancel(long generation);
    }

    public interface ICueSubtitleExecutor
    {
        CueSubtitleState GetState(CueTimelinePlan plan, double cueTime);
    }

    /// <summary>Minimal compiled-plan validator for the pure controller.</summary>
    public static class CueTimelinePlanValidator
    {
        private const double Epsilon = 1e-9;

        public static void Validate(CueTimelinePlan plan)
        {
            string error;
            if (!TryValidate(plan, out error))
                throw new ArgumentException(error, "plan");
        }

        public static bool TryValidate(CueTimelinePlan plan, out string error)
        {
            if (plan == null)
            {
                error = "plan is null";
                return false;
            }
            if (plan.schema != "tutorial-cue-plan/v1")
            {
                error = "schema must be 'tutorial-cue-plan/v1'";
                return false;
            }
            if (string.IsNullOrEmpty(plan.cue_id))
            {
                error = "cue_id must be non-empty";
                return false;
            }
            if (plan.course == null)
            {
                error = "course is missing";
                return false;
            }
            if (!IsFiniteNonNegative(plan.course.start))
            {
                error = "course.start must be a finite non-negative number";
                return false;
            }
            if (!IsFinitePositive(plan.course.duration))
            {
                error = "course.duration must be a finite positive number";
                return false;
            }
            if (plan.tracks == null)
            {
                error = "tracks must not be null";
                return false;
            }

            var trackIds = new HashSet<string>(StringComparer.Ordinal);
            var segmentIds = new HashSet<string>(StringComparer.Ordinal);
            for (int ti = 0; ti < plan.tracks.Count; ti++)
            {
                CueTimelineTrack track = plan.tracks[ti];
                if (track == null)
                {
                    error = "tracks[" + ti + "] is null";
                    return false;
                }
                if (string.IsNullOrEmpty(track.id))
                {
                    error = "tracks[" + ti + "].id must be non-empty";
                    return false;
                }
                if (!trackIds.Add(track.id))
                {
                    error = "duplicate track id '" + track.id + "'";
                    return false;
                }
                if (track.kind != "audio" && track.kind != "visual"
                    && track.kind != "subtitle")
                {
                    error = "tracks[" + ti + "].kind must be audio, visual or subtitle";
                    return false;
                }
                if (track.segments == null)
                {
                    error = "tracks[" + ti + "].segments must not be null";
                    return false;
                }

                for (int si = 0; si < track.segments.Count; si++)
                {
                    CueTimelineSegment segment = track.segments[si];
                    string path = "tracks[" + ti + "].segments[" + si + "]";
                    if (segment == null)
                    {
                        error = path + " is null";
                        return false;
                    }
                    if (string.IsNullOrEmpty(segment.id))
                    {
                        error = path + ".id must be non-empty";
                        return false;
                    }
                    if (!segmentIds.Add(segment.id))
                    {
                        error = "duplicate segment id '" + segment.id + "'";
                        return false;
                    }
                    if (!IsFiniteNonNegative(segment.cue_start))
                    {
                        error = path + ".cue_start must be finite and non-negative";
                        return false;
                    }

                    string expectedKind = SegmentKind(segment.type);
                    if (expectedKind == null || expectedKind != track.kind)
                    {
                        error = path + ".type is invalid for track kind " + track.kind;
                        return false;
                    }
                    if ((segment.type == "hold" || segment.type == "silence"
                         || segment.type == "subtitle")
                        && !IsFinitePositive(segment.duration))
                    {
                        error = path + ".duration must be finite and positive";
                        return false;
                    }
                    if ((segment.type == "animation" || segment.type == "audio_clip")
                        && (!IsFiniteNonNegative(segment.source_start)
                            || !IsFiniteNonNegative(segment.source_end)
                            || segment.source_end <= segment.source_start))
                    {
                        error = path + " source range must be finite and increasing";
                        return false;
                    }
                    if (segment.type == "audio_clip" && string.IsNullOrEmpty(segment.asset))
                    {
                        error = path + ".asset must be non-empty";
                        return false;
                    }
                    if (segment.type == "animation")
                    {
                        if (string.IsNullOrEmpty(segment.animation))
                        {
                            error = path + ".animation must be non-empty";
                            return false;
                        }
                        if (segment.after_end != "hold_last_frame"
                            && segment.after_end != "clear")
                        {
                            error = path + ".after_end is invalid";
                            return false;
                        }
                    }
                    if (segment.type == "subtitle" && segment.text == null)
                    {
                        error = path + ".text must not be null";
                        return false;
                    }
                    if (segment.EndCueTime > plan.course.duration + Epsilon)
                    {
                        error = path + " ends after course.duration";
                        return false;
                    }
                }

                for (int a = 0; a < track.segments.Count; a++)
                {
                    CueTimelineSegment left = track.segments[a];
                    for (int b = a + 1; b < track.segments.Count; b++)
                    {
                        CueTimelineSegment right = track.segments[b];
                        if (left.cue_start < right.EndCueTime - Epsilon
                            && right.cue_start < left.EndCueTime - Epsilon)
                        {
                            error = "track '" + track.id + "' has overlapping segments";
                            return false;
                        }
                    }
                }
            }

            error = "";
            return true;
        }

        private static string SegmentKind(string type)
        {
            if (type == "audio_clip" || type == "silence") return "audio";
            if (type == "animation" || type == "hold") return "visual";
            if (type == "subtitle") return "subtitle";
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
