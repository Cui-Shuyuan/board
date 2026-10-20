// Pure course-level audio planning for cue timeline playback.
//
// The planner flattens every cue-local audio track into one absolute course
// timeline, merges only source+course-contiguous ranges, and emits output
// blocks that can be rendered and scheduled ahead of time.  It deliberately
// does not know about AudioSource, DSP time or the Unity frame loop.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    public enum CueAudioEntryKind
    {
        AudioClip = 0,
        Silence = 1,
        Gap = 2,
    }

    public enum CueAudioBoundaryKind
    {
        Start = 0,
        Continuous = 1,
        SourceCut = 2,
        AssetChange = 3,
        SilenceGap = 4,
        End = 5,
    }

    /// <summary>
    /// Decoded format of one source asset.  Source sample fields in a plan are
    /// hints generated at plan-build time; playback always converts plan
    /// seconds to frames using this decoded format, never the stored hint.
    /// </summary>
    public sealed class CueAudioAssetFormat
    {
        public string Asset;
        public int SampleRate;
        public int Channels;
        public double PlaybackRate = 1.0;

        public CueAudioAssetFormat()
        {
        }

        public CueAudioAssetFormat(string asset, int sampleRate, int channels,
            double playbackRate)
        {
            Asset = asset;
            SampleRate = sampleRate;
            Channels = channels;
            PlaybackRate = playbackRate <= 0.0 ? 1.0 : playbackRate;
        }

        public long FrameAt(double seconds)
        {
            if (SampleRate <= 0) return 0L;
            return (long)Math.Round(seconds * SampleRate);
        }

        public int FrameCountForCourseDuration(double seconds)
        {
            if (SampleRate <= 0) return 0;
            double frames = seconds * SampleRate * PlaybackRate;
            if (frames <= 0.0) return 0;
            return (int)Math.Round(frames);
        }

        public double CourseDurationForFrames(long frames)
        {
            if (SampleRate <= 0 || PlaybackRate <= 0.0) return 0.0;
            return frames / (SampleRate * PlaybackRate);
        }

        public bool Matches(CueAudioAssetFormat other)
        {
            return other != null
                && string.Equals(Asset ?? "", other.Asset ?? "", StringComparison.Ordinal)
                && SampleRate == other.SampleRate
                && Channels == other.Channels
                && Math.Abs(PlaybackRate - other.PlaybackRate) <= 1e-9;
        }
    }

    public interface ICueAudioAssetFormatProvider
    {
        bool TryGetFormat(string asset, out CueAudioAssetFormat format);
    }

    public sealed class CueAudioAssetFormatMap : ICueAudioAssetFormatProvider
    {
        private readonly Dictionary<string, CueAudioAssetFormat> formats =
            new Dictionary<string, CueAudioAssetFormat>(StringComparer.Ordinal);

        public void Add(CueAudioAssetFormat format)
        {
            if (format == null || string.IsNullOrEmpty(format.Asset)) return;
            formats[format.Asset] = format;
        }

        public void Add(string asset, int sampleRate, int channels)
        {
            Add(new CueAudioAssetFormat(asset, sampleRate, channels, 1.0));
        }

        public bool TryGetFormat(string asset, out CueAudioAssetFormat format)
        {
            if (string.IsNullOrEmpty(asset))
            {
                format = null;
                return false;
            }
            return formats.TryGetValue(asset, out format);
        }
    }

    public sealed class CueAudioPlannerOptions
    {
        // Edge fades are generated in PCM sample space.  They are only applied
        // at real discontinuities and outer playback edges.
        public int EdgeFadeMilliseconds = 4;
        public int MaxFadeMilliseconds = 10;
        // Source sample fields are diagnostic.  We report a mismatch instead
        // of using them as playback coordinates because decoded asset formats
        // can legitimately differ from the plan builder's nominal rate.
        public bool ReportSourceSampleHintMismatch = true;
    }

    public sealed class CueAudioPlanDiagnostic
    {
        public string Code;
        public string Message;
        public string CueId;
        public string SegmentId;
        public string Asset;
    }

    public sealed class CueAudioCueMarker
    {
        public string CueId;
        public string SegmentId;
        public double CourseStart;
        public double CourseEnd;
    }

    public sealed class CueAudioEntry
    {
        public string Id;
        public CueAudioEntryKind Kind;
        public string CueId;
        public string SegmentId;
        public string Asset;
        public double CourseStart;
        public double CourseEnd;
        public double SourceStart;
        public double SourceEnd;
        public long SourceStartSampleHint;
        public long SourceEndSampleHint;
        public long SourceStartFrame;
        public long SourceEndFrame;
        public int SampleRate;
        public int Channels;
        public double PlaybackRate = 1.0;

        public double CourseDuration
        {
            get { return CourseEnd - CourseStart; }
        }

        public long SourceFrameCount
        {
            get { return SourceEndFrame - SourceStartFrame; }
        }
    }

    public sealed class CueAudioOutputBlock
    {
        public string Id;
        public double CourseStart;
        public double CourseEnd;
        public string Asset;
        public long SourceStartFrame;
        public long SourceEndFrame;
        public int SampleRate;
        public int Channels;
        public double PlaybackRate = 1.0;
        public int FadeInFrames;
        public int FadeOutFrames;
        public bool StartsAtCourseStart;
        public bool EndsAtCourseEnd;
        public List<CueAudioCueMarker> CueMarkers = new List<CueAudioCueMarker>();

        public long FrameCount
        {
            get { return SourceEndFrame - SourceStartFrame; }
        }

        public double CourseDuration
        {
            get { return CourseEnd - CourseStart; }
        }

        public CueAudioOutputBlock Clone()
        {
            var clone = (CueAudioOutputBlock)MemberwiseClone();
            clone.CueMarkers = new List<CueAudioCueMarker>();
            for (int i = 0; i < CueMarkers.Count; i++)
            {
                CueAudioCueMarker marker = CueMarkers[i];
                clone.CueMarkers.Add(new CueAudioCueMarker
                {
                    CueId = marker.CueId,
                    SegmentId = marker.SegmentId,
                    CourseStart = marker.CourseStart,
                    CourseEnd = marker.CourseEnd,
                });
            }
            return clone;
        }
    }

    public sealed class CueAudioBoundaryInfo
    {
        public int LeftEntryIndex;
        public int RightEntryIndex;
        public string LeftId;
        public string RightId;
        public CueAudioBoundaryKind Kind;
        public bool Merged;
        public double CourseGap;
        public int FadeOutFrames;
        public int FadeInFrames;
    }

    public sealed class CueAudioTimelinePlan
    {
        public CueTimelinePlanSet PlanSet;
        public double CourseDuration;
        public List<CueAudioEntry> Entries = new List<CueAudioEntry>();
        public List<CueAudioOutputBlock> Blocks = new List<CueAudioOutputBlock>();
        public List<CueAudioBoundaryInfo> Boundaries = new List<CueAudioBoundaryInfo>();
        public List<CueAudioPlanDiagnostic> Diagnostics =
            new List<CueAudioPlanDiagnostic>();

        public CueAudioOutputBlock FindBlockAt(double courseTime)
        {
            for (int i = 0; i < Blocks.Count; i++)
            {
                CueAudioOutputBlock block = Blocks[i];
                if (courseTime + 1e-9 >= block.CourseStart
                    && courseTime < block.CourseEnd - 1e-9)
                    return block;
            }
            return null;
        }
    }

    /// <summary>
    /// Pack dense short edits into one clip, so scheduling never needs a new
    /// AudioSource every few samples. Long continuous blocks remain untouched.
    /// Gaps inside a group are rendered as zeros; cue timing is not shortened.
    /// </summary>
    public sealed class CueAudioRenderGroup
    {
        public readonly List<CueAudioOutputBlock> Blocks = new List<CueAudioOutputBlock>();
        public double CourseStart;
        public double CourseEnd;
        public int SampleRate;
        public int Channels;

        public static List<CueAudioRenderGroup> Build(CueAudioTimelinePlan plan)
        {
            var groups = new List<CueAudioRenderGroup>();
            CueAudioRenderGroup group = null;
            foreach (CueAudioOutputBlock block in plan.Blocks)
            {
                if (group == null || group.CourseEnd - group.CourseStart >= 0.25
                    || block.CourseStart - group.CourseEnd >= 0.25)
                {
                    group = new CueAudioRenderGroup { CourseStart = block.CourseStart };
                    groups.Add(group);
                }
                group.Blocks.Add(block);
                group.CourseEnd = block.CourseEnd;
                group.SampleRate = Math.Max(group.SampleRate, block.SampleRate);
                group.Channels = Math.Max(group.Channels, block.Channels);
            }
            return groups;
        }

        public float[] Render(double start, double end, Func<string, float[]> sourcePcm)
        {
            if (SampleRate <= 0 || Channels < 1 || Channels > 2)
                throw new CueAudioPlanningException("unsupported course audio format");
            long firstFrame = (long)Math.Round(start * SampleRate);
            long lastFrame = (long)Math.Round(end * SampleRate);
            int frames = checked((int)(lastFrame - firstFrame));
            if (frames <= 0) throw new CueAudioPlanningException("empty audio output group");
            var output = new float[checked(frames * Channels)];
            foreach (CueAudioOutputBlock block in Blocks)
            {
                int from = (int)Math.Max(0, Math.Round(block.CourseStart * SampleRate) - firstFrame);
                int to = (int)Math.Min(frames, Math.Round(block.CourseEnd * SampleRate) - firstFrame);
                if (to <= from) continue;
                float[] pcm = sourcePcm(block.Asset);
                if (pcm == null || block.SourceEndFrame * block.Channels > pcm.LongLength)
                    throw new CueAudioPlanningException("source PCM output range is out of bounds: " + block.Asset);
                for (int frame = from; frame < to; frame++)
                {
                    // Absolute phase keeps resampling independent of seek and
                    // output grouping. No downsampling: group rate is the max.
                    double local = ((firstFrame + frame) / (double)SampleRate - block.CourseStart)
                        * block.SampleRate * block.PlaybackRate;
                    if (block.SampleRate == SampleRate && block.PlaybackRate == 1.0)
                        local = Math.Round(local);
                    local = Math.Max(0.0, Math.Min(block.FrameCount - 1, local));
                    long lo = (long)Math.Floor(local);
                    long hi = Math.Min(block.FrameCount - 1, lo + 1);
                    float fraction = (float)(local - lo);
                    float gain = 1f;
                    if (block.FadeInFrames > 0)
                        gain *= (float)Math.Min(1.0, local / Math.Max(1, block.FadeInFrames - 1));
                    if (block.FadeOutFrames > 0)
                        gain *= (float)Math.Min(1.0, (block.FrameCount - 1 - local)
                            / Math.Max(1, block.FadeOutFrames - 1));
                    for (int ch = 0; ch < Channels; ch++)
                    {
                        int srcChannel = block.Channels == 1 ? 0 : ch;
                        float a = pcm[(block.SourceStartFrame + lo) * block.Channels + srcChannel];
                        float b = pcm[(block.SourceStartFrame + hi) * block.Channels + srcChannel];
                        output[frame * Channels + ch] = (a + (b - a) * fraction) * gain;
                    }
                }
            }
            int fade = Math.Min((int)Math.Round(SampleRate * 0.004), frames / 2);
            CueAudioPcmRenderer.ApplyEdgeEnvelope(output, frames, Channels,
                start > CourseStart + 1e-7 ? fade : 0,
                end < CourseEnd - 1e-7 ? fade : 0);
            return output;
        }
    }

    public sealed class CueAudioPlanningException : Exception
    {
        public CueAudioPlanningException(string message) : base(message)
        {
        }
    }

    public static class CueAudioTimelinePlanner
    {
        private const double Epsilon = 1e-9;

        public static CueAudioTimelinePlan Build(
            CueTimelinePlanSet planSet,
            ICueAudioAssetFormatProvider formatProvider,
            CueAudioPlannerOptions options)
        {
            if (planSet == null) throw new ArgumentNullException("planSet");
            if (formatProvider == null) throw new ArgumentNullException("formatProvider");
            if (options == null) options = new CueAudioPlannerOptions();
            if (planSet.cues == null || planSet.cues.Count == 0)
                throw new CueAudioPlanningException("plan set has no cues");

            var plan = new CueAudioTimelinePlan
            {
                PlanSet = planSet,
                CourseDuration = planSet.course != null
                    ? planSet.course.duration
                    : 0.0,
            };

            FlattenEntries(plan, formatProvider, options);
            InsertGaps(plan);
            BuildBlocksAndBoundaries(plan, options);
            ApplyFades(plan, options);
            return plan;
        }

        /// <summary>
        /// Returns a copy of the plan truncated to a course playback limit.
        /// The original plan and its PCM sample ranges are untouched.  This is
        /// used by pauseAtCueEnd / autoAdvance-off so a continuous PCM block
        /// cannot leak samples from the next cue before the controller stops.
        /// </summary>
        public static CueAudioTimelinePlan ClampTo(
            CueAudioTimelinePlan source,
            double courseEnd,
            CueAudioPlannerOptions options)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (options == null) options = new CueAudioPlannerOptions();

            var clone = new CueAudioTimelinePlan
            {
                PlanSet = source.PlanSet,
                CourseDuration = Math.Min(source.CourseDuration, courseEnd),
            };
            for (int i = 0; i < source.Diagnostics.Count; i++)
            {
                CueAudioPlanDiagnostic d = source.Diagnostics[i];
                clone.Diagnostics.Add(new CueAudioPlanDiagnostic
                {
                    Code = d.Code,
                    Message = d.Message,
                    CueId = d.CueId,
                    SegmentId = d.SegmentId,
                    Asset = d.Asset,
                });
            }

            for (int i = 0; i < source.Blocks.Count; i++)
            {
                CueAudioOutputBlock block = source.Blocks[i];
                if (block.CourseStart >= courseEnd - Epsilon) continue;
                CueAudioOutputBlock copy = block.Clone();
                if (copy.CourseEnd > courseEnd)
                {
                    double clippedDuration = Math.Max(0.0, courseEnd - copy.CourseStart);
                    int keepFrames = (int)Math.Floor(
                        clippedDuration * copy.SampleRate * copy.PlaybackRate + 1e-6);
                    if (keepFrames <= 0) continue;
                    if (keepFrames > block.FrameCount) keepFrames = (int)block.FrameCount;
                    copy.SourceEndFrame = copy.SourceStartFrame + keepFrames;
                    copy.CourseEnd = copy.CourseStart
                        + keepFrames / (copy.SampleRate * copy.PlaybackRate);
                    copy.FadeOutFrames = Math.Min(
                        FadeFrames(options, copy.SampleRate),
                        keepFrames / 2);
                }
                copy.EndsAtCourseEnd = true;
                clone.Blocks.Add(copy);
            }
            return clone;
        }

        private static void FlattenEntries(CueAudioTimelinePlan output,
            ICueAudioAssetFormatProvider formats, CueAudioPlannerOptions options)
        {
            for (int cueIndex = 0; cueIndex < output.PlanSet.cues.Count; cueIndex++)
            {
                CueTimelinePlan cuePlan = output.PlanSet.cues[cueIndex];
                if (cuePlan == null || cuePlan.course == null) continue;
                CueTimelineTrack audioTrack =
                    CueTimelineTrackResolver.FindTrack(cuePlan, "audio");
                if (audioTrack == null || audioTrack.segments == null) continue;

                for (int segmentIndex = 0;
                    segmentIndex < audioTrack.segments.Count; segmentIndex++)
                {
                    CueTimelineSegment segment = audioTrack.segments[segmentIndex];
                    if (segment == null) continue;
                    double courseStart = cuePlan.course.start + segment.cue_start;
                    if (segment.type == "silence")
                    {
                        output.Entries.Add(new CueAudioEntry
                        {
                            Id = cueIndex + ":" + segment.id,
                            Kind = CueAudioEntryKind.Silence,
                            CueId = cuePlan.cue_id,
                            SegmentId = segment.id,
                            CourseStart = courseStart,
                            CourseEnd = courseStart + segment.duration,
                        });
                        continue;
                    }
                    if (segment.type != "audio_clip") continue;

                    CueAudioAssetFormat format;
                    if (!formats.TryGetFormat(segment.asset, out format)
                        || format == null || format.SampleRate <= 0
                        || format.Channels <= 0)
                    {
                        throw new CueAudioPlanningException(
                            "missing decoded format for audio asset '"
                            + (segment.asset ?? "") + "' cue='"
                            + (cuePlan.cue_id ?? "") + "' segment='"
                            + (segment.id ?? "") + "'");
                    }

                    long sourceStartFrame = format.FrameAt(segment.source_start);
                    long sourceEndFrame = format.FrameAt(segment.source_end);
                    if (sourceEndFrame <= sourceStartFrame)
                    {
                        throw new CueAudioPlanningException(
                            "audio source range has no frames: cue='"
                            + (cuePlan.cue_id ?? "") + "' segment='"
                            + (segment.id ?? "") + "'");
                    }
                    // Keep authored course boundaries separate from source
                    // frame quantization (e.g. 15 ms at 44.1 kHz is 661.5 frames).
                    double courseDuration = (segment.source_end - segment.source_start)
                        / format.PlaybackRate;
                    double courseEnd = courseStart + courseDuration;

                    if (options.ReportSourceSampleHintMismatch)
                    {
                        ReportHintMismatch(output, cuePlan, segment, format,
                            sourceStartFrame, sourceEndFrame);
                    }

                    output.Entries.Add(new CueAudioEntry
                    {
                        Id = cueIndex + ":" + segment.id,
                        Kind = CueAudioEntryKind.AudioClip,
                        CueId = cuePlan.cue_id,
                        SegmentId = segment.id,
                        Asset = segment.asset,
                        CourseStart = courseStart,
                        CourseEnd = courseEnd,
                        SourceStart = segment.source_start,
                        SourceEnd = segment.source_end,
                        SourceStartSampleHint = segment.source_start_sample,
                        SourceEndSampleHint = segment.source_end_sample,
                        SourceStartFrame = sourceStartFrame,
                        SourceEndFrame = sourceEndFrame,
                        SampleRate = format.SampleRate,
                        Channels = format.Channels,
                        PlaybackRate = format.PlaybackRate,
                    });
                }
            }

            output.Entries.Sort(delegate(CueAudioEntry a, CueAudioEntry b)
            {
                int byStart = a.CourseStart.CompareTo(b.CourseStart);
                if (byStart != 0) return byStart;
                return string.CompareOrdinal(a.Id ?? "", b.Id ?? "");
            });

            for (int i = 1; i < output.Entries.Count; i++)
            {
                CueAudioEntry previous = output.Entries[i - 1];
                CueAudioEntry current = output.Entries[i];
                if (current.CourseStart < previous.CourseEnd - 1e-6)
                {
                    throw new CueAudioPlanningException(
                        "audio entries overlap on course timeline: '"
                        + previous.Id + "' ends " + previous.CourseEnd.ToString("0.000000")
                        + " but '" + current.Id + "' starts "
                        + current.CourseStart.ToString("0.000000"));
                }
            }
        }

        private static void ReportHintMismatch(CueAudioTimelinePlan output,
            CueTimelinePlan cuePlan, CueTimelineSegment segment,
            CueAudioAssetFormat format, long startFrame, long endFrame)
        {
            // The sample fields are optional and may have been generated at a
            // nominal sample rate.  They are never used as playback positions.
            if (segment.source_start_sample < 0 || segment.source_end_sample < 0) return;
            if (segment.source_start_sample == 0L
                && segment.source_end_sample == 0L)
                return;
            long startDelta = Math.Abs(segment.source_start_sample - startFrame);
            long endDelta = Math.Abs(segment.source_end_sample - endFrame);
            if (startDelta <= 1 && endDelta <= 1) return;
            output.Diagnostics.Add(new CueAudioPlanDiagnostic
            {
                Code = "source_sample_hint_mismatch",
                Message = "plan sample hint differs from decoded frame position; "
                    + "using decoded seconds/format. start_hint="
                    + segment.source_start_sample + " decoded_start=" + startFrame
                    + " end_hint=" + segment.source_end_sample
                    + " decoded_end=" + endFrame,
                CueId = cuePlan.cue_id,
                SegmentId = segment.id,
                Asset = segment.asset,
            });
        }

        private static void InsertGaps(CueAudioTimelinePlan plan)
        {
            if (plan.Entries.Count == 0)
            {
                if (plan.CourseDuration > Epsilon)
                {
                    plan.Entries.Add(new CueAudioEntry
                    {
                        Id = "course.gap",
                        Kind = CueAudioEntryKind.Gap,
                        CourseStart = 0.0,
                        CourseEnd = plan.CourseDuration,
                    });
                }
                return;
            }

            var withGaps = new List<CueAudioEntry>(plan.Entries.Count * 2);
            double cursor = 0.0;
            for (int i = 0; i < plan.Entries.Count; i++)
            {
                CueAudioEntry entry = plan.Entries[i];
                if (entry.CourseStart > cursor + 1e-7)
                {
                    withGaps.Add(new CueAudioEntry
                    {
                        Id = "gap." + withGaps.Count,
                        Kind = CueAudioEntryKind.Gap,
                        CourseStart = cursor,
                        CourseEnd = entry.CourseStart,
                    });
                }
                withGaps.Add(entry);
                if (entry.CourseEnd > cursor) cursor = entry.CourseEnd;
            }
            if (plan.CourseDuration > cursor + 1e-7)
            {
                withGaps.Add(new CueAudioEntry
                {
                    Id = "gap." + withGaps.Count,
                    Kind = CueAudioEntryKind.Gap,
                    CourseStart = cursor,
                    CourseEnd = plan.CourseDuration,
                });
            }
            plan.Entries = withGaps;
        }

        private static void BuildBlocksAndBoundaries(CueAudioTimelinePlan plan,
            CueAudioPlannerOptions options)
        {
            CueAudioOutputBlock openBlock = null;
            CueAudioEntry openLastEntry = null;
            var entryToBlock = new Dictionary<string, CueAudioOutputBlock>(
                StringComparer.Ordinal);

            for (int i = 0; i < plan.Entries.Count; i++)
            {
                CueAudioEntry entry = plan.Entries[i];
                CueAudioBoundaryInfo boundary = null;
                if (i > 0)
                {
                    CueAudioEntry left = plan.Entries[i - 1];
                    boundary = ClassifyBoundary(left, entry);
                    plan.Boundaries.Add(boundary);
                }

                if (entry.Kind != CueAudioEntryKind.AudioClip)
                {
                    if (openBlock != null)
                    {
                        openBlock = null;
                        openLastEntry = null;
                    }
                    continue;
                }

                bool canMerge = openBlock != null && openLastEntry != null
                    && boundary != null
                    && boundary.Kind == CueAudioBoundaryKind.Continuous;
                if (canMerge)
                {
                    boundary.Merged = true;
                    openBlock.CourseEnd = entry.CourseEnd;
                    openBlock.SourceEndFrame = entry.SourceEndFrame;
                    openBlock.CueMarkers.Add(new CueAudioCueMarker
                    {
                        CueId = entry.CueId,
                        SegmentId = entry.SegmentId,
                        CourseStart = entry.CourseStart,
                        CourseEnd = entry.CourseEnd,
                    });
                    openLastEntry = entry;
                    entryToBlock[entry.Id] = openBlock;
                    continue;
                }

                openBlock = new CueAudioOutputBlock
                {
                    Id = "audio.block." + plan.Blocks.Count,
                    CourseStart = entry.CourseStart,
                    CourseEnd = entry.CourseEnd,
                    Asset = entry.Asset,
                    SourceStartFrame = entry.SourceStartFrame,
                    SourceEndFrame = entry.SourceEndFrame,
                    SampleRate = entry.SampleRate,
                    Channels = entry.Channels,
                    PlaybackRate = entry.PlaybackRate,
                    StartsAtCourseStart = entry.CourseStart <= Epsilon,
                    EndsAtCourseEnd = Math.Abs(entry.CourseEnd
                        - plan.CourseDuration) <= 1e-6,
                };
                openBlock.CueMarkers.Add(new CueAudioCueMarker
                {
                    CueId = entry.CueId,
                    SegmentId = entry.SegmentId,
                    CourseStart = entry.CourseStart,
                    CourseEnd = entry.CourseEnd,
                });
                plan.Blocks.Add(openBlock);
                entryToBlock[entry.Id] = openBlock;
                openLastEntry = entry;

                if (boundary != null)
                {
                    CueAudioOutputBlock previousBlock;
                    if (entryToBlock.TryGetValue(plan.Entries[i - 1].Id,
                        out previousBlock) && previousBlock != openBlock)
                    {
                        boundary.FadeOutFrames = FadeFrames(
                            options, previousBlock.SampleRate);
                    }
                    boundary.FadeInFrames = FadeFrames(
                        options, openBlock.SampleRate);
                }
            }
        }

        private static CueAudioBoundaryInfo ClassifyBoundary(
            CueAudioEntry left, CueAudioEntry right)
        {
            var boundary = new CueAudioBoundaryInfo
            {
                LeftEntryIndex = -1,
                RightEntryIndex = -1,
                LeftId = left.Id,
                RightId = right.Id,
                CourseGap = right.CourseStart - left.CourseEnd,
            };

            if (left.Kind != CueAudioEntryKind.AudioClip
                || right.Kind != CueAudioEntryKind.AudioClip)
            {
                boundary.Kind = CueAudioBoundaryKind.SilenceGap;
                return boundary;
            }

            int sampleRate = Math.Max(left.SampleRate, right.SampleRate);
            if (sampleRate <= 0) sampleRate = 24000;
            long leftEndCourseFrame = (long)Math.Round(left.CourseEnd * sampleRate);
            long rightStartCourseFrame = (long)Math.Round(right.CourseStart * sampleRate);
            if (leftEndCourseFrame != rightStartCourseFrame)
            {
                boundary.Kind = boundary.CourseGap > 0.0
                    ? CueAudioBoundaryKind.SilenceGap
                    : CueAudioBoundaryKind.SourceCut;
                return boundary;
            }

            bool sameAsset = string.Equals(left.Asset ?? "", right.Asset ?? "",
                StringComparison.Ordinal);
            bool sameFormat = left.SampleRate == right.SampleRate
                && left.Channels == right.Channels
                && Math.Abs(left.PlaybackRate - right.PlaybackRate) <= 1e-9;
            bool sameSource = sameAsset && sameFormat
                && left.SourceEndFrame == right.SourceStartFrame;
            if (sameSource)
            {
                boundary.Kind = CueAudioBoundaryKind.Continuous;
                return boundary;
            }
            boundary.Kind = sameAsset
                ? CueAudioBoundaryKind.SourceCut
                : CueAudioBoundaryKind.AssetChange;
            return boundary;
        }

        private static void ApplyFades(CueAudioTimelinePlan plan,
            CueAudioPlannerOptions options)
        {
            for (int i = 0; i < plan.Blocks.Count; i++)
            {
                CueAudioOutputBlock block = plan.Blocks[i];
                bool hasPreviousAudioBlock = i > 0;
                bool beginsAtCourseStart = block.CourseStart <= Epsilon;
                // A fade-in is needed when the block starts after silence, a
                // cut or the start of a course that is not sample zero.  The
                // very first course sample is left untouched so the first
                // phoneme is not attenuated by a player-introduced envelope.
                if (hasPreviousAudioBlock || !beginsAtCourseStart)
                    block.FadeInFrames = FadeFrames(
                        options, block.SampleRate);

                bool endsAtCourseEnd = Math.Abs(block.CourseEnd
                    - plan.CourseDuration) <= 1e-6;
                // Keep the natural outer course edge untouched.  A fade is
                // required only when playback leaves a block before a real cut
                // or planned silence.
                if (!endsAtCourseEnd)
                    block.FadeOutFrames = FadeFrames(
                        options, block.SampleRate);

                ClampFadePair(block);
            }

            for (int i = 0; i < plan.Boundaries.Count; i++)
            {
                CueAudioBoundaryInfo boundary = plan.Boundaries[i];
                if (boundary.Merged) continue;
                CueAudioOutputBlock left = FindBlockContainingEntry(plan,
                    boundary.LeftId);
                CueAudioOutputBlock right = FindBlockContainingEntry(plan,
                    boundary.RightId);
                if (left != null)
                    boundary.FadeOutFrames = left.FadeOutFrames;
                if (right != null)
                    boundary.FadeInFrames = right.FadeInFrames;
            }
        }

        private static void ClampFadePair(CueAudioOutputBlock block)
        {
            long frames = block.FrameCount;
            if (frames <= 0L)
            {
                block.FadeInFrames = 0;
                block.FadeOutFrames = 0;
                return;
            }
            int maxInOut = (int)Math.Min(frames, int.MaxValue);
            if ((long)block.FadeInFrames + block.FadeOutFrames > maxInOut)
            {
                int half = maxInOut / 2;
                if (half <= 0)
                {
                    if (block.FadeInFrames > 0 && block.FadeOutFrames > 0)
                    {
                        block.FadeInFrames = 0;
                        block.FadeOutFrames = 0;
                    }
                    else
                    {
                        block.FadeInFrames = block.FadeInFrames > 0 ? 1 : 0;
                        block.FadeOutFrames = block.FadeOutFrames > 0 ? 1 : 0;
                    }
                    return;
                }
                if (block.FadeInFrames > half) block.FadeInFrames = half;
                if (block.FadeOutFrames > half) block.FadeOutFrames = half;
                if ((long)block.FadeInFrames + block.FadeOutFrames > maxInOut)
                    block.FadeOutFrames = Math.Max(0,
                        maxInOut - block.FadeInFrames);
            }
        }

        private static CueAudioOutputBlock FindBlockContainingEntry(
            CueAudioTimelinePlan plan, string entryId)
        {
            if (string.IsNullOrEmpty(entryId)) return null;
            CueAudioEntry entry = plan.Entries.Find(e => e.Id == entryId);
            if (entry == null) return null;
            for (int i = 0; i < plan.Blocks.Count; i++)
            {
                CueAudioOutputBlock block = plan.Blocks[i];
                for (int m = 0; m < block.CueMarkers.Count; m++)
                    if (block.CueMarkers[m].SegmentId == entry.SegmentId
                        && block.CueMarkers[m].CueId == entry.CueId)
                        return block;
            }
            return null;
        }

        private static int FadeFrames(CueAudioPlannerOptions options,
            int sampleRate)
        {
            if (options == null || sampleRate <= 0) return 0;
            int milliseconds = Math.Min(options.EdgeFadeMilliseconds,
                options.MaxFadeMilliseconds);
            if (milliseconds <= 0) return 0;
            return (int)Math.Round(milliseconds * sampleRate / 1000.0);
        }
    }

    /// <summary>
    /// Copies one planned block out of decoded source PCM and applies exactly
    /// the planned sample-space edge envelope.  Frame count is never changed.
    /// </summary>
    public static class CueAudioPcmRenderer
    {
        public static float[] Render(CueAudioOutputBlock block, float[] sourcePcm,
            int sourceChannels)
        {
            if (block == null) throw new ArgumentNullException("block");
            if (sourcePcm == null) throw new ArgumentNullException("sourcePcm");
            if (sourceChannels <= 0)
                throw new ArgumentOutOfRangeException("sourceChannels");
            if (sourceChannels != block.Channels)
                throw new ArgumentException("source channel count does not match block");

            long frameCountLong = block.SourceEndFrame - block.SourceStartFrame;
            if (frameCountLong <= 0)
                throw new ArgumentException("block has no source frames");
            if (frameCountLong > int.MaxValue)
                throw new ArgumentException("block is too large for an AudioClip");
            int frameCount = (int)frameCountLong;
            long sampleOffsetLong = block.SourceStartFrame * sourceChannels;
            long sampleCountLong = (long)frameCount * sourceChannels;
            if (sampleOffsetLong < 0 || sampleCountLong <= 0
                || sampleOffsetLong + sampleCountLong > sourcePcm.Length)
                throw new ArgumentException("block source range is outside PCM data");

            var output = new float[sampleCountLong];
            Array.Copy(sourcePcm, sampleOffsetLong, output, 0L, sampleCountLong);
            ApplyEdgeEnvelope(output, frameCount, sourceChannels,
                block.FadeInFrames, block.FadeOutFrames);
            return output;
        }

        /// <summary>
        /// Applies a short linear gain envelope in place.  Every channel in a
        /// frame receives the same gain.  The envelope never changes the array
        /// length and never reads outside the supplied frame range.
        /// </summary>
        public static void ApplyEdgeEnvelope(float[] interleavedSamples,
            int frameCount, int channels, int fadeInFrames, int fadeOutFrames)
        {
            if (interleavedSamples == null)
                throw new ArgumentNullException("interleavedSamples");
            if (frameCount < 0) throw new ArgumentOutOfRangeException("frameCount");
            if (channels <= 0) throw new ArgumentOutOfRangeException("channels");
            if ((long)frameCount * channels > interleavedSamples.Length)
                throw new ArgumentException("PCM buffer is shorter than frame count");

            int fadeIn = Math.Max(0, Math.Min(fadeInFrames, frameCount));
            int fadeOut = Math.Max(0, Math.Min(fadeOutFrames, frameCount - fadeIn));

            for (int frame = 0; frame < fadeIn; frame++)
            {
                float gain = fadeIn == 1 ? 0f : (float)frame / (fadeIn - 1);
                ApplyGain(interleavedSamples, frame, channels, gain);
            }
            for (int frame = 0; frame < fadeOut; frame++)
            {
                int outputFrame = frameCount - 1 - frame;
                float gain = fadeOut == 1 ? 0f : (float)frame / (fadeOut - 1);
                ApplyGain(interleavedSamples, outputFrame, channels, gain);
            }
        }

        private static void ApplyGain(float[] samples, int frame, int channels,
            float gain)
        {
            int offset = frame * channels;
            for (int channel = 0; channel < channels; channel++)
                samples[offset + channel] *= gain;
        }
    }
}
