// Whole-track compatibility adapter for legacy runtime documents.
//
// This is intentionally a loader adapter, not a second playback engine.  It
// converts the old runtime cue start/duration/audio range into the same
// tutorial-cue-plan/v1 shape consumed by CuePlaybackController.  It does not
// use legacy composition fields, shared-audio handoff flags, subtitle-word tail trimming or
// title-specific pause rules.  A track either loads the shared plan file or
// this adapter produces one complete fallback plan for the entire track.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    public sealed class CueTimelineCompatibilityCueInput
    {
        public string cue_id;
        public string entry;
        public double course_start;
        public double runtime_duration;
        public string audio_asset;
        public double audio_start;
        public double compiled_visual_duration;
        public int sample_rate;
        public List<CueTimelineSubtitle> subtitles = new List<CueTimelineSubtitle>();
    }

    public static class CueTimelineCompatibilityPlanFactory
    {
        private const double Epsilon = 1e-9;

        public static bool TryBuild(CueTimelineCompatibilityCueInput input,
            out CueTimelinePlan plan, out string error)
        {
            plan = null;
            error = "";
            if (input == null)
            {
                error = "compatibility cue input is null";
                return false;
            }
            if (string.IsNullOrEmpty(input.cue_id))
            {
                error = "cue_id is empty";
                return false;
            }
            if (!IsFiniteNonNegative(input.course_start))
            {
                error = "course_start must be finite and non-negative";
                return false;
            }
            if (!IsFinitePositive(input.runtime_duration))
            {
                error = "runtime_duration must be finite and positive";
                return false;
            }

            try
            {
                double duration = input.runtime_duration;
                var tracks = new List<CueTimelineTrack>();

                if (IsFiniteNonNegative(input.compiled_visual_duration)
                    && input.compiled_visual_duration > 0.0)
                {
                    double visualEnd = Math.Min(
                        input.compiled_visual_duration, duration);
                    if (visualEnd > Epsilon)
                    {
                        tracks.Add(new CueTimelineTrack
                        {
                            id = "visual.main",
                            kind = "visual",
                            tail_policy = "hold_last_frame",
                            segments = new List<CueTimelineSegment>
                            {
                                new CueTimelineSegment
                                {
                                    id = "visual.compatibility",
                                    type = "animation",
                                    animation = input.cue_id,
                                    cue_start = 0.0,
                                    source_start = 0.0,
                                    source_end = visualEnd,
                                    after_end = "hold_last_frame",
                                },
                            },
                        });
                    }
                }

                if (!string.IsNullOrEmpty(input.audio_asset))
                {
                    if (!IsFiniteNonNegative(input.audio_start))
                        throw new ArgumentException(
                            "audio_start must be finite and non-negative");
                    double audioEnd = input.audio_start + duration;
                    tracks.Add(new CueTimelineTrack
                    {
                        id = "audio.main",
                        kind = "audio",
                        tail_policy = "silence",
                        segments = new List<CueTimelineSegment>
                        {
                            new CueTimelineSegment
                            {
                                id = "audio.compatibility",
                                type = "audio_clip",
                                asset = input.audio_asset,
                                cue_start = 0.0,
                                source_start = input.audio_start,
                                source_end = audioEnd,
                                source_start_sample = ToSample(
                                    input.audio_start, input.sample_rate),
                                source_end_sample = ToSample(
                                    audioEnd, input.sample_rate),
                            },
                        },
                    });
                }

                if (tracks.Count == 0)
                {
                    error = "compatibility cue has no playable track";
                    return false;
                }

                plan = new CueTimelinePlan
                {
                    schema = "tutorial-cue-plan/v1",
                    cue_id = input.cue_id,
                    entry = input.entry,
                    course = new CueTimelineCourse
                    {
                        start = input.course_start,
                        duration = duration,
                    },
                    tracks = tracks,
                    subtitles = ClipSubtitles(input.subtitles, duration),
                };

                string validationError;
                if (!CueTimelinePlanValidator.TryValidate(plan, out validationError))
                {
                    error = validationError;
                    plan = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                plan = null;
                return false;
            }
        }

        public static bool TryBuildSet(
            IList<CueTimelineCompatibilityCueInput> inputCues,
            string gameId,
            string track,
            out CueTimelinePlanSet set,
            out string error)
        {
            set = null;
            error = "";
            if (inputCues == null || inputCues.Count == 0)
            {
                error = "compatibility track has no cues";
                return false;
            }

            var plans = new List<CueTimelinePlan>(inputCues.Count);
            double total = 0.0;
            for (int i = 0; i < inputCues.Count; i++)
            {
                CueTimelinePlan plan;
                string cueError;
                if (!TryBuild(inputCues[i], out plan, out cueError))
                {
                    error = "cue[" + i + "]: " + cueError;
                    return false;
                }
                plans.Add(plan);
                total = Math.Max(total, plan.course.start + plan.course.duration);
            }

            set = new CueTimelinePlanSet
            {
                schema = CueTimelinePlanSetValidator.Schema,
                schema_version = CueTimelinePlanSetValidator.SchemaVersion,
                game_id = gameId ?? "",
                track = track ?? "",
                course = new CueTimelineCourse { start = 0.0, duration = total },
                cues = plans,
            };

            var expectedIds = new List<string>(inputCues.Count);
            for (int i = 0; i < inputCues.Count; i++)
                expectedIds.Add(inputCues[i].cue_id);
            if (!CueTimelinePlanSetValidator.TryValidate(
                set, gameId, track, expectedIds, out error))
            {
                set = null;
                return false;
            }
            return true;
        }

        private static List<CueTimelineSubtitle> ClipSubtitles(
            List<CueTimelineSubtitle> input, double duration)
        {
            var result = new List<CueTimelineSubtitle>();
            if (input == null) return result;
            for (int i = 0; i < input.Count; i++)
            {
                CueTimelineSubtitle source = input[i];
                if (source == null) continue;
                double start = Math.Max(0.0, source.cue_start);
                if (start >= duration - Epsilon) continue;
                double end = Math.Min(duration, source.EndCueTime);
                if (end <= start + Epsilon) continue;
                result.Add(new CueTimelineSubtitle
                {
                    id = string.IsNullOrEmpty(source.id)
                        ? "subtitle." + i : source.id,
                    cue_start = start,
                    duration = end - start,
                    text = source.text ?? "",
                });
            }
            return result;
        }

        private static long ToSample(double seconds, int sampleRate)
        {
            if (sampleRate <= 0) return 0L;
            return (long)Math.Round(
                seconds * sampleRate, MidpointRounding.AwayFromZero);
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
