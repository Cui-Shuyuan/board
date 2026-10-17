// Pure adapter from the existing Unity runtime cue DTO data to a cue plan.
//
// This factory deliberately has no content-specific title/body rules.  The
// only optional composition switch is an explicit migration-sample option:
// it can split a single section-audio clip at a subtitle-window gap and append
// an authored visual hold.  Audio source ranges are never shortened by the
// subtitle word timing.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    public sealed class CueTimelineLegacyAudioPlanSegment
    {
        public string type;
        public string file;
        public double start;
        public double end;
        public double duration;
    }

    public sealed class CueTimelineLegacyCueInput
    {
        public string cue_id;
        public string entry;
        public double course_start;
        public double runtime_duration;
        public string audio_asset;
        public double audio_start;
        public bool section_audio;
        public List<CueTimelineLegacyAudioPlanSegment> audio_plan =
            new List<CueTimelineLegacyAudioPlanSegment>();
        public double compiled_visual_duration;
        public int sample_rate;
        public List<CueTimelineSubtitle> subtitles = new List<CueTimelineSubtitle>();
        public bool compose_multi_segment_audio;
        public double visual_hold_seconds;
    }

    public static class CueTimelineLegacyPlanFactory
    {
        private const double Epsilon = 1e-9;

        public static bool TryBuild(CueTimelineLegacyCueInput input,
            out CueTimelinePlan plan, out string error)
        {
            plan = null;
            error = "";
            if (input == null)
            {
                error = "legacy cue input is null";
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
                CueTimelineTrack audioTrack = BuildAudioTrack(input);
                if (input.compose_multi_segment_audio && audioTrack != null)
                    TryComposeAudioAtSubtitleGap(audioTrack, input.subtitles, input.sample_rate);

                double audioEnd = TrackContentEnd(audioTrack);
                double compiledDuration = IsFiniteNonNegative(input.compiled_visual_duration)
                    ? input.compiled_visual_duration : 0.0;
                double baseDuration = Math.Max(input.runtime_duration,
                    Math.Max(audioEnd, compiledDuration));

                double holdSeconds = IsFiniteNonNegative(input.visual_hold_seconds)
                    ? input.visual_hold_seconds : 0.0;
                double planDuration = baseDuration + holdSeconds;
                if (!IsFinitePositive(planDuration))
                {
                    error = "computed plan duration must be positive";
                    return false;
                }

                CueTimelineTrack visualTrack = null;
                if (compiledDuration > 0.0)
                {
                    visualTrack = new CueTimelineTrack
                    {
                        id = "visual.main",
                        kind = "visual",
                        tail_policy = "hold_last_frame",
                        segments = new List<CueTimelineSegment>(),
                    };
                    visualTrack.segments.Add(new CueTimelineSegment
                    {
                        id = "visual.compiled",
                        type = "animation",
                        animation = input.cue_id,
                        cue_start = 0.0,
                        source_start = 0.0,
                        source_end = compiledDuration,
                        after_end = "hold_last_frame",
                    });
                    if (planDuration > compiledDuration + Epsilon)
                    {
                        visualTrack.segments.Add(new CueTimelineSegment
                        {
                            id = "visual.hold_tail",
                            type = "hold",
                            cue_start = compiledDuration,
                            duration = planDuration - compiledDuration,
                            pose = "last_frame",
                        });
                    }
                }

                var tracks = new List<CueTimelineTrack>();
                if (visualTrack != null) tracks.Add(visualTrack);
                if (audioTrack != null) tracks.Add(audioTrack);

                plan = new CueTimelinePlan
                {
                    schema = "tutorial-cue-plan/v1",
                    cue_id = input.cue_id,
                    entry = input.entry,
                    course = new CueTimelineCourse
                    {
                        start = input.course_start,
                        duration = planDuration,
                    },
                    tracks = tracks,
                    subtitles = CopySubtitles(input.subtitles),
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

        private static CueTimelineTrack BuildAudioTrack(CueTimelineLegacyCueInput input)
        {
            var segments = new List<CueTimelineSegment>();
            if (input.audio_plan != null && input.audio_plan.Count > 0)
            {
                double cursor = 0.0;
                for (int i = 0; i < input.audio_plan.Count; i++)
                {
                    CueTimelineLegacyAudioPlanSegment legacy = input.audio_plan[i];
                    if (legacy == null) throw new ArgumentException("audio_plan[" + i + "] is null");
                    if (legacy.type == "clip")
                    {
                        string asset = !string.IsNullOrEmpty(legacy.file)
                            ? legacy.file : input.audio_asset;
                        if (string.IsNullOrEmpty(asset))
                            throw new ArgumentException("audio_plan[" + i + "].file is empty");
                        if (!IsFiniteNonNegative(legacy.start)
                            || !IsFiniteNonNegative(legacy.end)
                            || legacy.end <= legacy.start)
                            throw new ArgumentException("audio_plan[" + i + "] has invalid source range");
                        double duration = legacy.end - legacy.start;
                        segments.Add(new CueTimelineSegment
                        {
                            id = "audio.plan." + i,
                            type = "audio_clip",
                            asset = asset,
                            cue_start = cursor,
                            source_start = legacy.start,
                            source_end = legacy.end,
                            source_start_sample = ToSample(legacy.start, input.sample_rate),
                            source_end_sample = ToSample(legacy.end, input.sample_rate),
                        });
                        cursor += duration;
                    }
                    else if (legacy.type == "silence")
                    {
                        if (!IsFinitePositive(legacy.duration))
                            throw new ArgumentException("audio_plan[" + i + "].duration is invalid");
                        segments.Add(new CueTimelineSegment
                        {
                            id = "audio.plan." + i,
                            type = "silence",
                            cue_start = cursor,
                            duration = legacy.duration,
                        });
                        cursor += legacy.duration;
                    }
                    else
                    {
                        throw new ArgumentException("audio_plan[" + i + "].type is unsupported");
                    }
                }
            }
            else if (!string.IsNullOrEmpty(input.audio_asset))
            {
                if (!IsFiniteNonNegative(input.audio_start))
                    throw new ArgumentException("audio_start must be finite and non-negative");
                double sourceEnd = input.audio_start + input.runtime_duration;
                segments.Add(new CueTimelineSegment
                {
                    id = "audio.section",
                    type = "audio_clip",
                    asset = input.audio_asset,
                    cue_start = 0.0,
                    source_start = input.audio_start,
                    source_end = sourceEnd,
                    source_start_sample = ToSample(input.audio_start, input.sample_rate),
                    source_end_sample = ToSample(sourceEnd, input.sample_rate),
                });
            }

            if (segments.Count == 0) return null;
            return new CueTimelineTrack
            {
                id = "audio.main",
                kind = "audio",
                tail_policy = "silence",
                segments = segments,
            };
        }

        /// <summary>
        /// Split one audio clip at the largest gap between subtitle windows.
        /// The removed source gap is represented by an explicit silence
        /// segment, so the last audio source_end is not moved or trimmed by
        /// subtitle timing.  This is a sample-composition helper, not a title
        /// cue rule.
        /// </summary>
        private static void TryComposeAudioAtSubtitleGap(CueTimelineTrack audioTrack,
            List<CueTimelineSubtitle> subtitles, int sampleRate)
        {
            if (audioTrack == null || audioTrack.segments == null
                || audioTrack.segments.Count != 1
                || audioTrack.segments[0].type != "audio_clip"
                || subtitles == null || subtitles.Count < 2)
                return;

            var ordered = new List<CueTimelineSubtitle>(subtitles);
            ordered.Sort(delegate(CueTimelineSubtitle a, CueTimelineSubtitle b)
            {
                return a.cue_start.CompareTo(b.cue_start);
            });

            double bestStart = 0.0;
            double bestDuration = 0.0;
            for (int i = 0; i + 1 < ordered.Count; i++)
            {
                if (ordered[i] == null || ordered[i + 1] == null) continue;
                double gapStart = ordered[i].EndCueTime;
                double gapEnd = ordered[i + 1].cue_start;
                double gapDuration = gapEnd - gapStart;
                if (gapDuration > bestDuration && gapDuration >= 0.05)
                {
                    bestStart = gapStart;
                    bestDuration = gapDuration;
                }
            }
            if (bestDuration <= 0.0) return;

            CueTimelineSegment whole = audioTrack.segments[0];
            double wholeEnd = whole.EndCueTime;
            if (bestStart <= whole.cue_start + Epsilon
                || bestStart + bestDuration >= wholeEnd - Epsilon)
                return;

            double secondCueStart = bestStart + bestDuration;
            double sourceSplitOne = whole.source_start + (bestStart - whole.cue_start);
            double sourceSplitTwo = whole.source_start + (secondCueStart - whole.cue_start);
            if (sourceSplitTwo <= sourceSplitOne
                || sourceSplitTwo >= whole.source_end - Epsilon)
                return;

            var first = new CueTimelineSegment
            {
                id = "audio.composite.0",
                type = "audio_clip",
                asset = whole.asset,
                cue_start = whole.cue_start,
                source_start = whole.source_start,
                source_end = sourceSplitOne,
                source_start_sample = ToSample(whole.source_start, sampleRate),
                source_end_sample = ToSample(sourceSplitOne, sampleRate),
            };
            var silence = new CueTimelineSegment
            {
                id = "audio.composite.1",
                type = "silence",
                cue_start = bestStart,
                duration = bestDuration,
            };
            var second = new CueTimelineSegment
            {
                id = "audio.composite.2",
                type = "audio_clip",
                asset = whole.asset,
                cue_start = secondCueStart,
                source_start = sourceSplitTwo,
                source_end = whole.source_end,
                source_start_sample = ToSample(sourceSplitTwo, sampleRate),
                source_end_sample = ToSample(whole.source_end, sampleRate),
            };
            audioTrack.segments.Clear();
            audioTrack.segments.Add(first);
            audioTrack.segments.Add(silence);
            audioTrack.segments.Add(second);
        }

        private static List<CueTimelineSubtitle> CopySubtitles(List<CueTimelineSubtitle> input)
        {
            var result = new List<CueTimelineSubtitle>();
            if (input == null) return result;
            for (int i = 0; i < input.Count; i++)
            {
                CueTimelineSubtitle source = input[i];
                if (source == null) continue;
                result.Add(new CueTimelineSubtitle
                {
                    id = string.IsNullOrEmpty(source.id) ? "subtitle." + i : source.id,
                    cue_start = source.cue_start,
                    duration = source.duration,
                    text = source.text ?? "",
                });
            }
            return result;
        }

        private static double TrackContentEnd(CueTimelineTrack track)
        {
            double end = 0.0;
            if (track == null || track.segments == null) return end;
            for (int i = 0; i < track.segments.Count; i++)
            {
                CueTimelineSegment segment = track.segments[i];
                if (segment != null) end = Math.Max(end, segment.EndCueTime);
            }
            return end;
        }

        private static long ToSample(double seconds, int sampleRate)
        {
            if (sampleRate <= 0) return 0L;
            return (long)Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero);
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
