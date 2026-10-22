// Unity whole-track audio transport for cue timeline playback.
//
// This class uses two scheduling voices plus two short retiring voices and
// schedules them with PlayScheduled at absolute DSP times derived from the
// course clock.  PCM decode, slicing, envelope generation and AudioClip.Create
// happen ahead of the scheduled block, never at the cue boundary itself.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace BoardGameTutorial.Animation
{
    public sealed class UnityCourseAudioTransport : ICourseAudioTransport
    {
        private const long OutputCacheBudgetBytes = 16L * 1024 * 1024;
        private const long SourcePcmBudgetBytes = 24L * 1024 * 1024;

        private readonly MonoBehaviour coroutineHost;
        private readonly AudioSource sourceA;
        private readonly AudioSource sourceB;
        private readonly List<CourseSourceSlot> slots = new List<CourseSourceSlot>(4);
        private readonly Dictionary<string, AudioClip> sourceClipCache =
            new Dictionary<string, AudioClip>(StringComparer.Ordinal);
        private readonly Dictionary<string, float[]> sourcePcmCache =
            new Dictionary<string, float[]>(StringComparer.Ordinal);
        private readonly List<string> sourcePcmOrder = new List<string>();
        private readonly Dictionary<string, AudioClip> outputClipCache =
            new Dictionary<string, AudioClip>(StringComparer.Ordinal);
        private readonly List<string> outputClipOrder = new List<string>();
        private readonly HashSet<string> preparedClipKeys = new HashSet<string>();

        private string gameRoot;
        private CueTimelinePlanSet coursePlanSet;
        private CueAudioTimelinePlan courseAudioPlan;
        private List<CueAudioRenderGroup> renderGroups;
        private int nextGroupToSchedule;
        private long outputCacheBytes;
        private long sourcePcmBytes;
        private ICourseAudioTransportCallback courseCallback;
        private long courseGeneration;
        private bool coursePrepared;
        private bool courseScheduling;
        private bool disposed;
        private Coroutine prepareRoutine;
        private const int AudioLoadConcurrency = 4;
        private readonly List<PendingAudioLoad> activeLoads = new List<PendingAudioLoad>();

        private sealed class PendingAudioLoad
        {
            public string Asset;
            public UnityWebRequest Request;
        }
        private double anchorCourseTime;
        private double anchorDspTime;
        private double playbackEndCourseTime;
        private double scheduleLookaheadSeconds = 1.5;
        private float volume = 1f;

        public UnityCourseAudioTransport(MonoBehaviour coroutineHost,
            AudioSource firstSource, AudioSource secondSource, string gameRoot)
        {
            if (coroutineHost == null) throw new ArgumentNullException("coroutineHost");
            if (firstSource == null) throw new ArgumentNullException("firstSource");
            if (secondSource == null) throw new ArgumentNullException("secondSource");
            this.coroutineHost = coroutineHost;
            this.sourceA = firstSource;
            this.sourceB = secondSource;
            this.gameRoot = gameRoot ?? "";
            ConfigureSource(sourceA);
            ConfigureSource(sourceB);
            slots.Add(new CourseSourceSlot(sourceA));
            slots.Add(new CourseSourceSlot(sourceB));
            slots.Add(new CourseSourceSlot(CreateSource(coroutineHost, "Course audio C")));
            slots.Add(new CourseSourceSlot(CreateSource(coroutineHost, "Course audio D")));
        }

        public static AudioSource CreateSource(MonoBehaviour host, string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(host.transform, false);
            var source = child.AddComponent<AudioSource>();
            ConfigureSource(source);
            child.AddComponent<CueAudioReleaseEnvelope>();
            return source;
        }

        public string GameRoot
        {
            get { return gameRoot; }
            set
            {
                string next = value ?? "";
                if (next == gameRoot) return;
                InvalidateDeviceResources();
                gameRoot = next;
            }
        }

        public float Volume
        {
            get { return volume; }
            set
            {
                volume = Mathf.Clamp01(value);
                for (int i = 0; i < slots.Count; i++) slots[i].Source.volume = volume;
            }
        }

        public double ScheduleLookaheadSeconds
        {
            get { return scheduleLookaheadSeconds; }
            set { scheduleLookaheadSeconds = Math.Max(0.1, value); }
        }

        public CueAudioTimelinePlan CourseAudioPlan
        {
            get { return courseAudioPlan; }
        }

        public void Prepare(CueTimelinePlanSet planSet, long transportGeneration,
            double startCourseTime, double playbackEnd,
            ICourseAudioTransportCallback callback)
        {
            Cancel(courseGeneration);
            ClearOutputCache();
            if (planSet == null || planSet.cues == null || planSet.cues.Count == 0)
            {
                if (callback != null)
                    callback.OnFailed(transportGeneration, "course plan set is empty");
                return;
            }

            coursePlanSet = planSet;
            courseCallback = callback;
            courseGeneration = transportGeneration;
            coursePrepared = false;
            courseScheduling = false;
            playbackEndCourseTime = playbackEnd > 0.0
                ? playbackEnd : planSet.course.duration;
            StopCourseSources();
            prepareRoutine = coroutineHost.StartCoroutine(PrepareRoutine(
                transportGeneration, startCourseTime, playbackEndCourseTime));
        }

        public double Schedule(long transportGeneration, double anchorCourse,
            double anchorDsp, bool play, double playbackEnd)
        {
            if (!AcceptGeneration(transportGeneration))
                throw new InvalidOperationException("course audio is not prepared");
            ReleaseCourseSources();
            courseGeneration = transportGeneration;
            anchorCourseTime = Math.Max(0.0, anchorCourse);
            playbackEndCourseTime = Math.Min(playbackEnd,
                courseAudioPlan != null
                    ? courseAudioPlan.CourseDuration
                    : playbackEnd);
            courseScheduling = play;
            nextGroupToSchedule = 0;
            while (nextGroupToSchedule < renderGroups.Count
                && renderGroups[nextGroupToSchedule].CourseEnd <= anchorCourseTime + 1e-7)
                nextGroupToSchedule++;
            if (!play) return AudioSettings.dspTime;

            // Preparation is intentionally before the shared epoch commit.
            // A cold seek may decode/copy a long block; that time is not playback.
            PrewarmCourseClips(anchorCourseTime, playbackEndCourseTime);
            double lead = SchedulingLeadSeconds();
            anchorDspTime = Math.Max(anchorDsp, AudioSettings.dspTime + lead);
            if (!ScheduleAhead(anchorCourseTime, true))
                throw new InvalidOperationException("initial audio appointment missed its deadline");
            preparedClipKeys.Clear();
            TrimOutputClipCache();
            Debug.Log("[CueTimeline][AudioCourse] schedule gen="
                + transportGeneration
                + " anchor_course=" + FormatTime(anchorCourseTime)
                + " anchor_dsp=" + FormatTime(anchorDspTime)
                + " end=" + FormatTime(playbackEndCourseTime));
            return anchorDspTime;
        }

        private static double SchedulingLeadSeconds()
        {
            int frames, buffers;
            AudioSettings.GetDSPBufferSize(out frames, out buffers);
            return Math.Max(0.08, (double)frames * Math.Max(2, buffers)
                / Math.Max(1, AudioSettings.outputSampleRate) + 0.02);
        }

        public void Pause(long transportGeneration)
        {
            if (!AcceptGeneration(transportGeneration)) return;
            courseScheduling = false;
            ReleaseCourseSources();
            Debug.Log("[CueTimeline][AudioCourse] pause gen="
                + transportGeneration);
        }

        public void Stop(long transportGeneration)
        {
            if (transportGeneration != 0L
                && transportGeneration != courseGeneration) return;
            courseScheduling = false;
            StopCourseSources();
        }

        public void Cancel(long transportGeneration)
        {
            if (transportGeneration != 0L
                && transportGeneration != courseGeneration) return;
            if (courseGeneration != 0L)
                Debug.Log("[CueTimeline][AudioCourse] cancel gen="
                    + transportGeneration);
            courseGeneration = 0L;
            coursePrepared = false;
            courseScheduling = false;
            if (prepareRoutine != null && coroutineHost != null)
            {
                coroutineHost.StopCoroutine(prepareRoutine);
                prepareRoutine = null;
            }
            ClearActiveLoads();
            StopCourseSources();
            coursePlanSet = null;
            courseAudioPlan = null;
            renderGroups = null;
            courseCallback = null;
            // Keep decoded source clips and Pcm caches for a subsequent
            // prepare of the same content.  Output clips are trimmed below.
            TrimOutputClipCache();
        }

        public void SyncToCourseTime(long transportGeneration, double courseTime)
        {
            if (!AcceptGeneration(transportGeneration)) return;
            if (!coursePrepared || !courseScheduling || courseAudioPlan == null)
                return;

            ReleaseFinishedSlots();

            if (nextGroupToSchedule < renderGroups.Count)
            {
                CueAudioRenderGroup group = renderGroups[nextGroupToSchedule];
                double now = AudioSettings.dspTime;
                double unheardStart = Math.Max(anchorCourseTime, group.CourseStart);
                double expectedDsp = anchorDspTime + (unheardStart - anchorCourseTime);
                if (unheardStart < playbackEndCourseTime - 1e-7 && now >= expectedDsp - 0.003)
                {
                    ReportUnderrun(unheardStart);
                    return;
                }
            }

            ScheduleAhead(courseTime, false);
        }

        private void ReportUnderrun(double unheardStart)
        {
            courseScheduling = false;
            StopCourseSources();
            string message = "missed audio appointment at course=" + FormatTime(unheardStart);
            Debug.LogWarning("[CueTimeline][AudioCourse] " + message);
            if (courseCallback != null)
                courseCallback.OnUnderrun(courseGeneration, unheardStart, message);
        }

        public void InvalidateDeviceResources()
        {
            Cancel(courseGeneration);
            ClearOutputCache();
            foreach (AudioClip clip in sourceClipCache.Values)
                if (clip != null) UnityEngine.Object.Destroy(clip);
            sourceClipCache.Clear();
            sourcePcmCache.Clear();
            sourcePcmOrder.Clear();
            sourcePcmBytes = 0;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Cancel(courseGeneration);
            foreach (AudioClip clip in sourceClipCache.Values)
                if (clip != null) UnityEngine.Object.Destroy(clip);
            foreach (AudioClip clip in outputClipCache.Values)
                if (clip != null) UnityEngine.Object.Destroy(clip);
            sourceClipCache.Clear();
            outputClipCache.Clear();
            outputClipOrder.Clear();
            sourcePcmCache.Clear();
            sourcePcmOrder.Clear();
            // The caller owns A/B; the transport owns the two spare voices.
            for (int i = 2; i < slots.Count; i++)
                if (slots[i].Source != null)
                    UnityEngine.Object.Destroy(slots[i].Source.gameObject);
        }

        private IEnumerator PrepareRoutine(long generation,
            double startCourseTime, double stopCourseTime)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            List<string> assets = CollectAssets(coursePlanSet);
            int next = 0;
            int loaded = 0;
            // Independent local files may decode concurrently. Waiting once per
            // file serialized IO/decoder latency and coroutine frame overhead.
            while (next < assets.Count || activeLoads.Count > 0)
            {
                if (generation != courseGeneration) yield break;
                while (next < assets.Count && activeLoads.Count < AudioLoadConcurrency)
                {
                    string asset = assets[next++];
                    AudioClip cached;
                    if (sourceClipCache.TryGetValue(asset, out cached) && cached != null)
                    {
                        if (!ValidateAssetRange(coursePlanSet, asset, cached, generation))
                        {
                            FailGeneration(generation, "audio source range exceeds clip: " + asset);
                            yield break;
                        }
                        continue;
                    }
                    string error = StartAudioLoad(asset);
                    if (error != null)
                    {
                        FailGeneration(generation, error);
                        yield break;
                    }
                    loaded++;
                }
                for (int i = activeLoads.Count - 1; i >= 0; i--)
                {
                    PendingAudioLoad pending = activeLoads[i];
                    if (!pending.Request.isDone) continue;
                    activeLoads.RemoveAt(i);
                    string error = CompleteAudioLoad(pending, generation);
                    if (error != null)
                    {
                        FailGeneration(generation, error);
                        yield break;
                    }
                }
                // Refill completed slots immediately; yield only when all
                // available slots are occupied or only pending work remains.
                if (activeLoads.Count > 0 &&
                    (activeLoads.Count == AudioLoadConcurrency || next == assets.Count))
                    yield return null;
            }

            if (generation != courseGeneration) yield break;
            long loadMilliseconds = timer.ElapsedMilliseconds;
            try
            {
                var formats = new CueAudioAssetFormatMap();
                foreach (KeyValuePair<string, AudioClip> pair in sourceClipCache)
                {
                    AudioClip clip = pair.Value;
                    if (clip == null) continue;
                    formats.Add(new CueAudioAssetFormat(pair.Key, clip.frequency,
                        clip.channels, 1.0));
                }
                courseAudioPlan = CueAudioTimelinePlanner.Build(
                    coursePlanSet, formats, new CueAudioPlannerOptions());
                renderGroups = CueAudioRenderGroup.Build(courseAudioPlan);
                int loggedDiagnostics = 0;
                for (int i = 0; i < courseAudioPlan.Diagnostics.Count; i++)
                {
                    CueAudioPlanDiagnostic diagnostic = courseAudioPlan.Diagnostics[i];
                    if (loggedDiagnostics < 8)
                    {
                        Debug.LogWarning("[CueTimeline][AudioCourse] "
                            + diagnostic.Code + " " + diagnostic.Message);
                        loggedDiagnostics++;
                    }
                }
                if (courseAudioPlan.Diagnostics.Count > loggedDiagnostics)
                {
                    Debug.LogWarning("[CueTimeline][AudioCourse] "
                        + (courseAudioPlan.Diagnostics.Count - loggedDiagnostics)
                        + " more planning diagnostics suppressed");
                }
                PrewarmCourseClips(startCourseTime, stopCourseTime);
            }
            catch (Exception ex)
            {
                FailGeneration(generation, ex.Message);
                yield break;
            }

            if (generation != courseGeneration) yield break;
            prepareRoutine = null;
            coursePrepared = true;
            Debug.Log("[CueTimeline][AudioCourse] prepared gen=" + generation
                + " assets=" + assets.Count
                + " loaded=" + loaded
                + " load_ms=" + loadMilliseconds
                + " plan_prewarm_ms=" + (timer.ElapsedMilliseconds - loadMilliseconds)
                + " total_ms=" + timer.ElapsedMilliseconds
                + " entries=" + courseAudioPlan.Entries.Count
                + " blocks=" + courseAudioPlan.Blocks.Count
                + " start=" + FormatTime(startCourseTime)
                + " end=" + FormatTime(stopCourseTime));
            if (courseCallback != null)
                courseCallback.OnPrepared(generation);
        }

        private string StartAudioLoad(string asset)
        {
            try
            {
                string path = ResolveAssetPath(asset);
                if (string.IsNullOrEmpty(path)) return "audio asset path is empty: " + asset;
                var request = UnityWebRequestMultimedia.GetAudioClip(
                    FilePathToUri(path), AudioTypeForPath(path));
                activeLoads.Add(new PendingAudioLoad { Asset = asset, Request = request });
                request.timeout = 60;
                request.SendWebRequest();
                return null;
            }
            catch (Exception ex) { return "audio load failed: " + asset + " " + ex.Message; }
        }

        private string CompleteAudioLoad(PendingAudioLoad pending, long generation)
        {
            try
            {
                if (pending.Request.result != UnityWebRequest.Result.Success)
                    return "audio load failed: " + pending.Asset + " " + pending.Request.error;
                AudioClip clip = DownloadHandlerAudioClip.GetContent(pending.Request);
                if (clip == null) return "audio decode returned null: " + pending.Asset;
                sourceClipCache[pending.Asset] = clip;
                return ValidateAssetRange(coursePlanSet, pending.Asset, clip, generation)
                    ? null : "audio source range exceeds clip: " + pending.Asset;
            }
            catch (Exception ex) { return "audio decode failed: " + pending.Asset + " " + ex.Message; }
            finally { SafeDisposeRequest(pending.Request); }
        }

        private void ClearActiveLoads()
        {
            foreach (PendingAudioLoad pending in activeLoads)
            {
                SafeAbortRequest(pending.Request);
                SafeDisposeRequest(pending.Request);
            }
            activeLoads.Clear();
        }

        private void PrewarmCourseClips(double startCourseTime,
            double stopCourseTime)
        {
            if (renderGroups == null) return;
            preparedClipKeys.Clear();
            double horizon = startCourseTime + scheduleLookaheadSeconds;
            int prepared = 0;
            for (int i = 0; i < renderGroups.Count; i++)
            {
                CueAudioRenderGroup block = renderGroups[i];
                if (block.CourseEnd <= startCourseTime) continue;
                if (block.CourseStart >= horizon) break;
                if (block.CourseStart >= stopCourseTime) break;
                double clipStart = Math.Max(block.CourseStart, startCourseTime);
                double clipEnd = Math.Min(block.CourseEnd, stopCourseTime);
                if (clipEnd <= clipStart) continue;
                string key;
                string error;
                GetOrCreateOutputClip(i, clipStart, clipEnd, out key, out error);
                if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                preparedClipKeys.Add(key);
                // Only two source slots can be submitted initially. Do not
                // allocate enough future clips to evict the imminent ones.
                if (++prepared == 2) break;
            }
        }

        private bool ScheduleAhead(double courseTime, bool initial)
        {
            if (renderGroups == null) return false;
            double horizon = courseTime + scheduleLookaheadSeconds;
            while (nextGroupToSchedule < renderGroups.Count)
            {
                int scheduledVoices = 0;
                for (int voice = 0; voice < slots.Count; voice++)
                    if (slots[voice].BlockIndex >= 0 && !slots[voice].Retiring
                        && slots[voice].ScheduledEndDsp > AudioSettings.dspTime)
                        scheduledVoices++;
                if (scheduledVoices >= 2) break;
                int i = nextGroupToSchedule;
                CueAudioRenderGroup group = renderGroups[i];
                if (group.CourseStart >= playbackEndCourseTime - 1e-7
                    || group.CourseStart > horizon) break;
                double start = Math.Max(group.CourseStart, anchorCourseTime);
                double end = Math.Min(group.CourseEnd, playbackEndCourseTime);
                if (end <= start) { nextGroupToSchedule++; continue; }
                double dsp = anchorDspTime + (start - anchorCourseTime);
                CourseSourceSlot slot = FindFreeSlot();
                if (slot == null) break;
                string key, error;
                AudioClip clip = GetOrCreateOutputClip(i, start, end, out key, out error);
                if (clip == null || !string.IsNullOrEmpty(error))
                    throw new InvalidOperationException(error);
                // PCM creation may have consumed the entire lead. Never submit
                // an expired appointment, even if it was valid before rendering.
                if (dsp <= AudioSettings.dspTime + 0.003)
                {
                    if (!initial) ReportUnderrun(start);
                    return false;
                }
                slot.Source.Stop();
                slot.Envelope.ResetEnvelope();
                slot.Retiring = false;
                slot.Source.clip = clip;
                slot.Source.timeSamples = 0;
                slot.Source.volume = volume;
                slot.BlockIndex = i;
                slot.ClipStartCourse = start;
                slot.ClipEndCourse = end;
                slot.ScheduledStartDsp = dsp;
                slot.ScheduledEndDsp = dsp + (double)clip.samples / clip.frequency;
                slot.Source.PlayScheduled(dsp);
                nextGroupToSchedule++;
                Debug.Log("[CueTimeline][AudioCourse] scheduled group=" + i
                    + " course=" + FormatTime(start) + ".." + FormatTime(end)
                    + " dsp=" + FormatTime(dsp) + " gen=" + courseGeneration);
            }
            return true;
        }

        private AudioClip GetOrCreateOutputClip(int index, double start, double end,
            out string key, out string error)
        {
            key = "";
            error = "";
            if (renderGroups == null || index < 0 || index >= renderGroups.Count)
            {
                error = "invalid audio render group";
                return null;
            }
            CueAudioRenderGroup group = renderGroups[index];
            long first = (long)Math.Round(start * group.SampleRate);
            long last = (long)Math.Round(end * group.SampleRate);
            // Output cache is cleared on Prepare/content/device invalidation.
            // Group index therefore belongs to exactly one immutable plan.
            key = index + "|" + first + "|" + last;
            AudioClip cached;
            if (outputClipCache.TryGetValue(key, out cached) && cached != null)
            {
                outputClipOrder.Remove(key);
                outputClipOrder.Add(key);
                return cached;
            }
            try
            {
                float[] pcm = group.Render(start, end, delegate(string asset)
                {
                    AudioClip sourceClip;
                    if (!sourceClipCache.TryGetValue(asset, out sourceClip) || sourceClip == null)
                        throw new InvalidOperationException("source audio is not prepared: " + asset);
                    return GetSourcePcm(asset, sourceClip);
                });
                int frames = pcm.Length / group.Channels;
                AudioClip output = AudioClip.Create("course-audio-" + index,
                    frames, group.Channels, group.SampleRate, false);
                if (output == null || !output.SetData(pcm, 0))
                {
                    if (output != null) UnityEngine.Object.Destroy(output);
                    throw new InvalidOperationException("AudioClip creation failed for group " + index);
                }
                outputClipCache[key] = output;
                outputClipOrder.Add(key);
                outputCacheBytes += (long)frames * group.Channels * sizeof(float);
                TrimOutputClipCache(key);
                return output;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private float[] GetSourcePcm(string asset, AudioClip clip)
        {
            float[] pcm;
            if (sourcePcmCache.TryGetValue(asset, out pcm) && pcm != null)
                return pcm;

            long expectedLong = (long)clip.samples * clip.channels;
            if (expectedLong <= 0 || expectedLong > int.MaxValue)
                return null;
            pcm = new float[(int)expectedLong];
            if (!clip.GetData(pcm, 0)) return null;
            sourcePcmCache[asset] = pcm;
            sourcePcmOrder.Add(asset);
            sourcePcmBytes += pcm.LongLength * sizeof(float);
            while (sourcePcmOrder.Count > 1 && sourcePcmBytes > SourcePcmBudgetBytes)
            {
                string oldest = sourcePcmOrder[0];
                sourcePcmOrder.RemoveAt(0);
                float[] retired;
                if (oldest != asset && sourcePcmCache.TryGetValue(oldest, out retired))
                {
                    sourcePcmBytes -= retired.LongLength * sizeof(float);
                    sourcePcmCache.Remove(oldest);
                }
            }
            return pcm;
        }

        private CourseSourceSlot FindFreeSlot()
        {
            // Do not overwrite a source that is still playing or already has a
            // future PlayScheduled appointment.  A source owned by a completed
            // block is released by ReleaseFinishedSlots() before this method is
            // called from SyncToCourseTime().
            ReleaseFinishedSlots();
            for (int i = 0; i < slots.Count; i++)
            {
                CourseSourceSlot slot = slots[i];
                if (slot.BlockIndex < 0) return slot;
            }
            return null;
        }

        public void ReleaseFinishedSlots()
        {
            double now = AudioSettings.dspTime;
            bool changed = false;
            for (int i = 0; i < slots.Count; i++)
            {
                CourseSourceSlot slot = slots[i];
                if (slot.BlockIndex >= 0 && (now >= slot.ScheduledEndDsp
                    || (slot.Retiring && slot.Envelope.IsSilent)))
                {
                    StopSlot(slot);
                    changed = true;
                }
            }
            if (changed) TrimOutputClipCache();
        }

        private void ReleaseCourseSources()
        {
            ReleaseFinishedSlots();
            double now = AudioSettings.dspTime;
            for (int i = 0; i < slots.Count; i++)
            {
                CourseSourceSlot slot = slots[i];
                if (slot.Retiring || slot.BlockIndex < 0) continue;
                if (now < slot.ScheduledStartDsp)
                    StopSlot(slot); // A future appointment has no audible tail.
                else
                {
                    slot.Retiring = true;
                    slot.Envelope.Release();
                }
            }
        }

        private static void StopSlot(CourseSourceSlot slot)
        {
            slot.Source.Stop();
            slot.Source.clip = null;
            slot.BlockIndex = -1;
            slot.Retiring = false;
            slot.ClipStartCourse = 0.0;
            slot.ClipEndCourse = 0.0;
            slot.ScheduledStartDsp = 0.0;
            slot.ScheduledEndDsp = 0.0;
        }

        private void StopCourseSources()
        {
            for (int i = 0; i < slots.Count; i++)
            {
                CourseSourceSlot slot = slots[i];
                StopSlot(slot);
            }
        }

        private void ClearOutputCache()
        {
            foreach (AudioClip clip in outputClipCache.Values)
                if (clip != null) UnityEngine.Object.Destroy(clip);
            outputClipCache.Clear();
            outputClipOrder.Clear();
            preparedClipKeys.Clear();
            outputCacheBytes = 0;
        }

        private void TrimOutputClipCache(string protectedKey = null)
        {
            int guard = outputClipOrder.Count;
            while (outputCacheBytes > OutputCacheBudgetBytes && guard-- > 0)
            {
                string oldest = outputClipOrder[0];
                outputClipOrder.RemoveAt(0);
                AudioClip clip;
                if (!outputClipCache.TryGetValue(oldest, out clip)) continue;
                if (oldest == protectedKey || preparedClipKeys.Contains(oldest) || IsOutputClipInUse(clip))
                {
                    outputClipOrder.Add(oldest);
                    continue;
                }
                outputClipCache.Remove(oldest);
                if (clip != null)
                {
                    outputCacheBytes -= (long)clip.samples * clip.channels * sizeof(float);
                    UnityEngine.Object.Destroy(clip);
                }
            }
        }

        private bool IsOutputClipInUse(AudioClip clip)
        {
            if (clip == null) return false;
            for (int i = 0; i < slots.Count; i++)
                if (slots[i].Source != null && slots[i].Source.clip == clip)
                    return true;
            return false;
        }

        private void FailGeneration(long generation, string message)
        {
            if (generation != courseGeneration) return;
            ClearActiveLoads();
            Debug.LogError("[CueTimeline][AudioCourse] " + message
                + " gen=" + generation);
            coursePrepared = false;
            courseScheduling = false;
            StopCourseSources();
            if (courseCallback != null)
                courseCallback.OnFailed(generation, message);
        }

        private bool AcceptGeneration(long generation)
        {
            if (generation == 0L || courseAudioPlan == null || !coursePrepared)
                return false;
            // The controller increments transport generation on explicit
            // discontinuities while keeping this prepared track timeline.
            // Sequential cue changes never call Schedule, so they do not
            // invalidate already scheduled blocks here.
            if (generation < courseGeneration) return false;
            courseGeneration = generation;
            return true;
        }

        private static List<string> CollectAssets(CueTimelinePlanSet planSet)
        {
            var assets = new List<string>();
            if (planSet == null || planSet.cues == null) return assets;
            for (int c = 0; c < planSet.cues.Count; c++)
            {
                CueTimelineTrack track = CueTimelineTrackResolver.FindTrack(
                    planSet.cues[c], "audio");
                if (track == null || track.segments == null) continue;
                for (int s = 0; s < track.segments.Count; s++)
                {
                    CueTimelineSegment segment = track.segments[s];
                    if (segment == null || segment.type != "audio_clip") continue;
                    string asset = (segment.asset ?? "").Trim();
                    if (asset.Length == 0) continue;
                    bool seen = false;
                    for (int a = 0; a < assets.Count; a++)
                        if (assets[a] == asset) { seen = true; break; }
                    if (!seen) assets.Add(asset);
                }
            }
            return assets;
        }

        private static bool ValidateAssetRange(CueTimelinePlanSet planSet,
            string asset, AudioClip clip, long generation)
        {
            if (planSet == null || clip == null) return false;
            for (int c = 0; c < planSet.cues.Count; c++)
            {
                CueTimelineTrack track = CueTimelineTrackResolver.FindTrack(
                    planSet.cues[c], "audio");
                if (track == null || track.segments == null) continue;
                for (int s = 0; s < track.segments.Count; s++)
                {
                    CueTimelineSegment segment = track.segments[s];
                    if (segment == null || segment.type != "audio_clip"
                        || segment.asset != asset) continue;
                    if (segment.source_end > clip.length + 0.02)
                    {
                        // This is reported through the caller's callback by
                        // returning false; the detailed path is logged here.
                        Debug.LogError("[CueTimeline][AudioCourse] source range "
                            + "exceeds clip asset=" + asset
                            + " source_end=" + segment.source_end.ToString("0.000")
                            + " clip_length=" + clip.length.ToString("0.000")
                            + " gen=" + generation);
                        return false;
                    }
                }
            }
            return true;
        }

        private string ResolveAssetPath(string asset)
        {
            if (string.IsNullOrEmpty(asset)) return "";
            if (asset.Contains("://")) return asset;
            return Path.Combine(gameRoot, asset);
        }

        private static void ConfigureSource(AudioSource source)
        {
            source.playOnAwake = false;
            source.loop = false;
        }

        private static AudioType AudioTypeForPath(string path)
        {
            string extension = Path.GetExtension(path ?? "").ToLowerInvariant();
            if (extension == ".wav") return AudioType.WAV;
            if (extension == ".ogg") return AudioType.OGGVORBIS;
            if (extension == ".aiff" || extension == ".aif") return AudioType.AIFF;
            return AudioType.MPEG;
        }

        private static string FilePathToUri(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (path.Contains("://")) return path;
            return new Uri(path).AbsoluteUri;
        }

        private static void SafeAbortRequest(UnityWebRequest request)
        {
            try { if (request != null) request.Abort(); } catch (Exception) { }
        }

        private static void SafeDisposeRequest(UnityWebRequest request)
        {
            try { if (request != null) request.Dispose(); } catch (Exception) { }
        }

        private static string FormatTime(double value)
        {
            return value.ToString("0.000",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private sealed class CourseSourceSlot
        {
            public readonly AudioSource Source;
            public readonly CueAudioReleaseEnvelope Envelope;
            public bool Retiring;
            public int BlockIndex = -1;
            public double ClipStartCourse;
            public double ClipEndCourse;
            public double ScheduledStartDsp;
            public double ScheduledEndDsp;

            public CourseSourceSlot(AudioSource source)
            {
                Source = source;
                Envelope = source.GetComponent<CueAudioReleaseEnvelope>()
                    ?? source.gameObject.AddComponent<CueAudioReleaseEnvelope>();
            }
        }
    }
}
