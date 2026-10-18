// Unity AudioSource executor for compiled cue plans.
//
// Generation-tagged loading, cancellation, and cue-time selection live here.
// The executor never completes a cue and never uses AudioSource.isPlaying as a
// completion signal.  Cue-local time always comes from CuePlaybackController.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace BoardGameTutorial.Animation
{
    public sealed class UnityCueAudioExecutor : IAudioCueExecutor, IAudioCueExecutorTimeline
    {
        private readonly MonoBehaviour coroutineHost;
        private readonly AudioSource source;
        private string gameRoot;
        private readonly Dictionary<string, AudioClip> clipCache =
            new Dictionary<string, AudioClip>(StringComparer.Ordinal);
        // Decoded PCM and segment clips start at sample 0 so compressed-audio
        // decoder seek/priming frames can never eat the first phoneme of a cue.
        private readonly Dictionary<string, float[]> pcmCache =
            new Dictionary<string, float[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, AudioClip> segmentClipCache =
            new Dictionary<string, AudioClip>(StringComparer.Ordinal);

        private CueTimelinePlan plan;
        private ICueExecutorCallback callback;
        private long activeGeneration;
        private Coroutine prepareRoutine;
        private UnityWebRequest activeRequest;
        private bool paused;
        private bool audioPrepared;
        private string currentSegmentId = "";
        private string lastCompletedSegmentId = "";
        private double lastCueTime;
        private float volume = 1f;

        public UnityCueAudioExecutor(MonoBehaviour coroutineHost, AudioSource source, string gameRoot)
        {
            if (coroutineHost == null) throw new ArgumentNullException("coroutineHost");
            if (source == null) throw new ArgumentNullException("source");
            this.coroutineHost = coroutineHost;
            this.source = source;
            this.gameRoot = gameRoot ?? "";
            this.source.playOnAwake = false;
            this.source.loop = false;
        }

        public string GameRoot
        {
            get { return gameRoot; }
            set { gameRoot = value ?? ""; }
        }

        public float Volume
        {
            get { return volume; }
            set
            {
                volume = Mathf.Clamp01(value);
                source.volume = volume;
            }
        }

        public string CurrentSegmentId { get { return currentSegmentId; } }

        public void Prepare(CueTimelinePlan nextPlan, long generation, ICueExecutorCallback executorCallback)
        {
            Cancel(activeGeneration);
            plan = nextPlan;
            callback = executorCallback;
            activeGeneration = generation;
            audioPrepared = false;
            paused = true;
            currentSegmentId = "";
            lastCompletedSegmentId = "";
            source.Stop();
            source.clip = null;

            CueTimelineTrack audioTrack = CueTimelineTrackResolver.FindTrack(plan, "audio");
            if (audioTrack == null || audioTrack.segments == null || audioTrack.segments.Count == 0)
            {
                audioPrepared = true;
                callback.OnPrepared(generation);
                return;
            }

            var assets = new List<string>();
            for (int i = 0; i < audioTrack.segments.Count; i++)
            {
                CueTimelineSegment segment = audioTrack.segments[i];
                if (segment == null || segment.type != "audio_clip") continue;
                if (string.IsNullOrEmpty(segment.asset)) continue;
                bool seen = false;
                for (int a = 0; a < assets.Count; a++)
                    if (assets[a] == segment.asset) { seen = true; break; }
                if (!seen) assets.Add(segment.asset);
            }

            prepareRoutine = coroutineHost.StartCoroutine(PrepareRoutine(generation, assets));
        }

        private IEnumerator PrepareRoutine(long generation, List<string> assets)
        {
            for (int i = 0; i < assets.Count; i++)
            {
                string asset = assets[i];
                if (generation != activeGeneration) yield break;

                AudioClip clip;
                if (clipCache.TryGetValue(asset, out clip) && clip != null)
                {
                    if (!ValidateTrackRanges(plan, asset, clip, generation)) yield break;
                    continue;
                }

                string path = ResolveAssetPath(asset);
                if (string.IsNullOrEmpty(path))
                {
                    FailGeneration(generation, "audio asset path is empty: " + asset);
                    yield break;
                }
                UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(FilePathToUri(path), AudioType.MPEG);
                request.timeout = 60;
                activeRequest = request;
                yield return request.SendWebRequest();

                if (generation != activeGeneration)
                {
                    SafeDisposeRequest(request);
                    activeRequest = null;
                    yield break;
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    string message = "audio load failed: " + asset + " " + request.error;
                    SafeDisposeRequest(request);
                    activeRequest = null;
                    FailGeneration(generation, message);
                    yield break;
                }
                clip = DownloadHandlerAudioClip.GetContent(request);
                SafeDisposeRequest(request);
                activeRequest = null;
                if (clip == null)
                {
                    FailGeneration(generation, "audio decode returned null: " + asset);
                    yield break;
                }
                clipCache[asset] = clip;
                if (!ValidateTrackRanges(plan, asset, clip, generation)) yield break;
            }

            if (generation != activeGeneration) yield break;
            prepareRoutine = null;
            audioPrepared = true;
            callback.OnPrepared(generation);
        }

        private bool ValidateTrackRanges(CueTimelinePlan activePlan, string asset,
            AudioClip clip, long generation)
        {
            if (activePlan == null || activePlan.tracks == null) return true;
            float clipLength = clip.length;
            for (int ti = 0; ti < activePlan.tracks.Count; ti++)
            {
                CueTimelineTrack track = activePlan.tracks[ti];
                if (track == null || track.kind != "audio" || track.segments == null) continue;
                for (int si = 0; si < track.segments.Count; si++)
                {
                    CueTimelineSegment segment = track.segments[si];
                    if (segment == null || segment.type != "audio_clip" || segment.asset != asset) continue;
                    if (segment.source_end > clipLength + 0.02)
                    {
                        FailGeneration(generation,
                            "audio source range exceeds clip: " + asset
                            + " source_end=" + segment.source_end.ToString("0.000")
                            + " clip_length=" + clipLength.ToString("0.000"));
                        return false;
                    }
                }
            }
            return true;
        }

        public void Play(long generation, double cueTime)
        {
            if (!AcceptGeneration(generation)) return;
            paused = false;
            ApplyCueTime(generation, cueTime, true);
        }

        public void Pause(long generation)
        {
            if (!AcceptGeneration(generation)) return;
            paused = true;
            if (source.isPlaying) source.Pause();
            Debug.Log("[CueTimelineV1][Audio] pause gen=" + generation
                + " cue_time=" + FormatTime(lastCueTime));
        }

        public void Resume(long generation, double cueTime)
        {
            if (!AcceptGeneration(generation)) return;
            paused = false;
            ApplyCueTime(generation, cueTime, true);
        }

        public void Seek(long generation, double cueTime)
        {
            if (!AcceptGeneration(generation)) return;
            paused = true;
            source.Stop();
            currentSegmentId = "";
            ApplyCueTime(generation, cueTime, false);
        }

        public void Stop(long generation)
        {
            if (generation != activeGeneration && generation != 0L) return;
            source.Stop();
            currentSegmentId = "";
            paused = true;
        }

        public void Cancel(long generation)
        {
            if (generation != 0L && generation != activeGeneration) return;
            activeGeneration = 0L;
            paused = true;
            if (prepareRoutine != null && coroutineHost != null)
            {
                coroutineHost.StopCoroutine(prepareRoutine);
                prepareRoutine = null;
            }
            if (activeRequest != null)
            {
                SafeAbortRequest(activeRequest);
                SafeDisposeRequest(activeRequest);
                activeRequest = null;
            }
            source.Stop();
            source.clip = null;
            currentSegmentId = "";
            // Keep the prepared plan alive: CuePlaybackController.SeekTo()
            // calls Cancel(oldGeneration) followed by Seek(newGeneration)
            // without another Prepare.  A subsequent Prepare replaces it.
        }

        public void SyncToCueTime(long generation, double cueTime)
        {
            if (!AcceptGeneration(generation) || paused) return;
            ApplyCueTime(generation, cueTime, true);
        }

        private void ApplyCueTime(long generation, double cueTime, bool shouldPlay)
        {
            if (!AcceptGeneration(generation)) return;
            lastCueTime = cueTime;
            CueTimelineTrack track = CueTimelineTrackResolver.FindTrack(plan, "audio");
            CueTimelineSegment segment = CueTimelineTrackResolver.FindSegment(track, cueTime);

            if (segment == null || segment.type == "silence")
            {
                if (source.isPlaying) source.Stop();
                if (!string.IsNullOrEmpty(currentSegmentId))
                {
                    lastCompletedSegmentId = currentSegmentId;
                    currentSegmentId = "";
                    if (callback != null) callback.OnCompleted(generation);
                }
                return;
            }
            if (segment.type != "audio_clip") return;

            bool sameSegment = currentSegmentId == segment.id
                && source.clip != null
                && source.isPlaying
                && !paused;
            if (sameSegment) return;

            AudioClip clip;
            if (!clipCache.TryGetValue(segment.asset ?? "", out clip) || clip == null)
            {
                if (callback != null)
                    callback.OnFailed(generation, "audio clip is not prepared: " + segment.asset);
                return;
            }

            double sourceTime = CueTimelineTrackResolver.AudioSourceTime(segment, cueTime);
            AudioClip playbackClip = GetOrCreateSegmentClip(
                segment, clip, generation);
            if (playbackClip == null) return;

            double segmentLocal = cueTime - segment.cue_start;
            if (segmentLocal < 0.0) segmentLocal = 0.0;
            int segmentOffsetSamples = TimeToFrame(
                playbackClip, segmentLocal, allowEnd: true);

            source.Stop();
            source.clip = playbackClip;
            source.timeSamples = segmentOffsetSamples;
            currentSegmentId = segment.id;
            if (shouldPlay)
            {
                source.Play();
                Debug.Log("[CueTimelineV1][Audio] play gen=" + generation
                    + " segment=" + segment.id
                    + " cue_time=" + FormatTime(cueTime)
                    + " source_time=" + FormatTime(sourceTime)
                    + " source_sample=" + source.timeSamples
                    + " source_clip_mode=pcm_slice"
                    + " planned_start_sample=" + segment.source_start_sample);
            }
            else
            {
                Debug.Log("[CueTimelineV1][Audio] seek-position gen=" + generation
                    + " segment=" + segment.id
                    + " cue_time=" + FormatTime(cueTime)
                    + " source_time=" + FormatTime(sourceTime)
                    + " source_sample=" + source.timeSamples
                    + " source_clip_mode=pcm_slice");
            }
        }

        /// <summary>
        /// Creates a PCM clip containing exactly [source_start, source_end).
        /// Starting every segment at sample 0 avoids MP3 decoder seek/priming
        /// imprecision, which previously dropped the first syllable when a cue
        /// began part-way through a shared section-audio file.
        /// </summary>
        private AudioClip GetOrCreateSegmentClip(
            CueTimelineSegment segment, AudioClip sourceClip, long generation)
        {
            int startFrame = TimeToFrame(sourceClip, segment.source_start, allowEnd: true);
            int endFrame = TimeToFrame(sourceClip, segment.source_end, allowEnd: true);
            if (endFrame <= startFrame) endFrame = Math.Min(sourceClip.samples, startFrame + 1);
            if (startFrame < 0) startFrame = 0;
            if (endFrame <= startFrame)
            {
                FailGeneration(generation,
                    "audio segment has no samples: " + segment.id);
                return null;
            }

            string key = (segment.asset ?? "") + "|" + startFrame.ToString()
                + "|" + endFrame.ToString();
            AudioClip cached;
            if (segmentClipCache.TryGetValue(key, out cached) && cached != null)
                return cached;

            float[] pcm;
            if (!pcmCache.TryGetValue(segment.asset ?? "", out pcm) || pcm == null)
            {
                int expected = sourceClip.samples * sourceClip.channels;
                pcm = new float[expected];
                if (!sourceClip.GetData(pcm, 0))
                {
                    FailGeneration(generation,
                        "audio PCM decode unavailable: " + segment.asset);
                    return null;
                }
                pcmCache[segment.asset ?? ""] = pcm;
            }

            int channels = sourceClip.channels;
            if (channels <= 0)
            {
                FailGeneration(generation,
                    "audio clip has invalid channel count: " + segment.asset);
                return null;
            }
            int frameCount = endFrame - startFrame;
            int sampleOffset = startFrame * channels;
            int sampleCount = frameCount * channels;
            if (sampleOffset < 0 || sampleCount <= 0
                || sampleOffset + sampleCount > pcm.Length)
            {
                FailGeneration(generation,
                    "audio PCM slice out of range: " + segment.id);
                return null;
            }

            var slice = new float[sampleCount];
            Array.Copy(pcm, sampleOffset, slice, 0, sampleCount);
            AudioClip segmentClip = AudioClip.Create(
                "cue-segment-" + segment.id,
                frameCount,
                channels,
                sourceClip.frequency,
                false);
            if (segmentClip == null || !segmentClip.SetData(slice, 0))
            {
                FailGeneration(generation,
                    "audio PCM clip creation failed: " + segment.id);
                return null;
            }
            segmentClipCache[key] = segmentClip;
            return segmentClip;
        }

        private static int TimeToFrame(
            AudioClip clip, double seconds, bool allowEnd)
        {
            if (clip == null) return 0;
            int samples = clip.samples;
            int frequency = clip.frequency;
            if (samples <= 0 || frequency <= 0) return 0;
            double frame = Math.Round(seconds * frequency);
            if (frame < 0.0) frame = 0.0;
            int maximum = allowEnd ? samples : samples - 1;
            if (maximum < 0) maximum = 0;
            if (frame > maximum) frame = maximum;
            return (int)frame;
        }

        private bool AcceptGeneration(long generation)
        {
            if (generation == 0L || plan == null || !audioPrepared) return false;
            // Play/Pause/Resume/Seek each advance the controller generation.
            // Once preparation is complete, the executor follows that
            // generation; stale async callbacks are still rejected against
            // the active generation by FailGeneration/PrepareRoutine.
            activeGeneration = generation;
            return true;
        }

        private bool IsActive(long generation)
        {
            return generation != 0L && generation == activeGeneration && plan != null;
        }

        private void FailGeneration(long generation, string message)
        {
            if (generation != activeGeneration) return;
            Debug.LogError("[CueTimelineV1][Audio] " + message + " gen=" + generation);
            if (callback != null) callback.OnFailed(generation, message);
        }

        private string ResolveAssetPath(string asset)
        {
            if (string.IsNullOrEmpty(asset)) return "";
            if (asset.Contains("://")) return asset;
            return Path.Combine(gameRoot, asset);
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
            return value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            Cancel(activeGeneration);
            plan = null;
            callback = null;
            audioPrepared = false;
            foreach (AudioClip clip in clipCache.Values)
            {
                if (clip != null) UnityEngine.Object.Destroy(clip);
            }
            foreach (AudioClip clip in segmentClipCache.Values)
            {
                if (clip != null) UnityEngine.Object.Destroy(clip);
            }
            clipCache.Clear();
            segmentClipCache.Clear();
            pcmCache.Clear();
        }
    }
}
