using System;
using System.Collections;
using System.IO;
using BoardGameTutorial.Animation;
using UnityEngine;
using UnityEngine.Networking;

namespace BoardGameTutorial
{
    public partial class TutorialCuePlayer
    {
        private AudioClip sectionAudioClip;
        private string sectionAudioPath = "";
        private bool audioPlanActive;
        private float audioPlanLocal;
        private bool audioPlanAwaitingStart;
        private bool audioPlanSegmentIsClip;
        private const float SectionAudioTransitionPrerollSeconds = 0.25f;
        private int sectionAudioPreRolledCueIndex = -1;
        private bool preserveSectionAudioPreRollOnNextPlay;

        private bool IsAudioPlanCue(int index)
        {
            return doc != null
                && doc.cues != null
                && index >= 0
                && index < doc.cues.Count
                && HasAudioPlan(doc.cues[index]);
        }

        private bool IsSectionAudioCue(int index)
        {
            return doc != null
                && doc.cues != null
                && index >= 0
                && index < doc.cues.Count
                && doc.cues[index] != null
                && doc.cues[index].section_audio
                && !HasAudioPlan(doc.cues[index]);
        }

        private static bool HasAudioPlan(TutorialCue cue)
        {
            return cue != null && cue.audio_plan != null && cue.audio_plan.Count > 0;
        }

        private static bool IsAudioPlanClip(TutorialAudioPlanSegment segment)
        {
            return segment != null
                && string.Equals(segment.type, "clip", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLegacyTitleCue(TutorialCue cue)
        {
            if (cue == null) return false;
            string localId = !string.IsNullOrEmpty(cue.local_id)
                ? cue.local_id
                : (cue.id ?? "").Split('/')[^1];
            return localId.StartsWith("title", StringComparison.OrdinalIgnoreCase);
        }

        private static float AudioPlanSegmentDuration(TutorialAudioPlanSegment segment)
        {
            if (segment == null) return 0f;
            if (IsAudioPlanClip(segment))
                return Mathf.Max(0f, segment.end - segment.start);
            if (string.Equals(segment.type, "silence", StringComparison.OrdinalIgnoreCase))
                return Mathf.Max(0f, segment.duration);
            return 0f;
        }

        private static float AudioPlanDuration(TutorialCue cue)
        {
            if (!HasAudioPlan(cue)) return 0f;
            float total = 0f;
            foreach (var segment in cue.audio_plan)
                total += AudioPlanSegmentDuration(segment);
            return total;
        }

        private static int FindAudioPlanSegment(TutorialCue cue, float localTime, out float segmentStart)
        {
            segmentStart = 0f;
            if (!HasAudioPlan(cue)) return -1;
            for (int i = 0; i < cue.audio_plan.Count; i++)
            {
                float duration = AudioPlanSegmentDuration(cue.audio_plan[i]);
                if (duration <= 0f) continue;
                if (localTime < segmentStart + duration - 0.0005f
                    || i == cue.audio_plan.Count - 1)
                    return i;
                segmentStart += duration;
            }
            return -1;
        }

        private IEnumerator LoadSectionAudioClip(string path, Action<AudioClip> onLoaded)
        {
            string uri = FilePathToUri(path);
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG))
            {
                request.timeout = 60;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[TutorialCuePlayer] load section audio failed: {request.error} ({path})");
                    onLoaded?.Invoke(null);
                    yield break;
                }
                onLoaded?.Invoke(DownloadHandlerAudioClip.GetContent(request));
            }
        }

        private IEnumerator PlaySectionCueRoutine(int index, float localTime, bool startPaused)
        {
            if (doc == null || doc.cues == null || index < 0 || index >= doc.cues.Count)
                yield break;

            var cue = doc.cues[index];
            bool usePreRoll = localTime <= 0.0001f
                && !startPaused
                && sectionAudioPreRolledCueIndex == index
                && audioSource != null
                && audioSource.clip != null
                && audioSource.isPlaying
                && sectionAudioClip != null
                && sectionAudioPath == cue.audio;
            sectionAudioPreRolledCueIndex = -1;

            if (!usePreRoll && audioSource.isPlaying) audioSource.Stop();
            currentIndex = index;
            isPaused = startPaused;
            pausedBeforePlay = startPaused;
            currentSubtitle = "";
            fallbackClock = 0f;
            lastPosition = Mathf.Max(0f, localTime);
            playbackFinished = false;

            if (!v2AnimPlayer.LoadCue(cue.id))
                Debug.LogError($"[TutorialCuePlayer] v2 LoadCue failed: {cue.id}");
            v2AnimPlayer.animationEnabled = enableCueAnimation;

            if (usePreRoll)
            {
                // The previous audio-plan cue started this section clip during
                // its trailing silence.  Continue from the already-running
                // source instead of seeking exactly to the first word, which
                // loses the MP3 decoder's initial frames on Android.
                audioSource.clip = sectionAudioClip;
                float preRolledLocal = Mathf.Clamp(
                    audioSource.time - cue.audio_start,
                    0f,
                    Mathf.Max(0f, cue.duration - 0.001f));
                Debug.Log(
                    $"[TutorialCuePlayer] preroll handoff cue={cue.id} "
                    + $"source={audioSource.time:0.000} audio_start={cue.audio_start:0.000} "
                    + $"local={preRolledLocal:0.000} isPlaying={audioSource.isPlaying}");
                lastPosition = preRolledLocal;
                v2AnimPlayer.Seek(preRolledLocal);
                isPaused = false;
                pausedBeforePlay = false;
            }
            else
            {
                string audioPath = Path.Combine(gameRoot, cue.audio);
                if (sectionAudioClip == null || sectionAudioPath != cue.audio)
                {
                    if (audioSource != null) audioSource.clip = null;
                    if (sectionAudioClip != null)
                    {
                        UnityEngine.Object.Destroy(sectionAudioClip);
                        sectionAudioClip = null;
                    }
                    sectionAudioPath = cue.audio;
                    yield return LoadSectionAudioClip(audioPath, clip => sectionAudioClip = clip);
                    if (sectionAudioClip == null) yield break;
                }
                audioSource.clip = sectionAudioClip;

                float clipLength = Mathf.Max(0f, sectionAudioClip.length);
                float seekTime = Mathf.Clamp(
                    cue.audio_start + Mathf.Max(0f, localTime),
                    0f,
                    Mathf.Max(0f, clipLength - 0.02f));
                audioSource.time = seekTime;
                Debug.Log(
                    $"[TutorialCuePlayer] section seek cue={cue.id} source={seekTime:0.000} "
                    + $"audio_start={cue.audio_start:0.000} local={localTime:0.000}");
                v2AnimPlayer.Seek(Mathf.Max(0f, localTime));

                if (startPaused)
                {
                    isPaused = true;
                    pausedBeforePlay = true;
                }
                else
                {
                    isPaused = false;
                    pausedBeforePlay = false;
                    audioSource.Play();
                }
            }

            UpdateSubtitle();
            bool speechPauseDone = false;
            while (audioSource != null && (isPaused || audioSource.isPlaying))
            {
                if (!isPaused)
                {
                    UpdateSubtitle();

                    int nextIndex = currentIndex + 1;
                    if (nextIndex < doc.cues.Count)
                    {
                        var next = doc.cues[nextIndex];
                        if (next != null && next.section_audio && next.audio == cue.audio)
                        {
                            // Chapter-title cards need a natural gap after the
                            // spoken title, not a pause at a waveform boundary
                            // that may land inside the next word.  Pause after
                            // the last subtitle word, hold for the authored
                            // duration, then jump over the TTS's trailing
                            // silence to the next cue's audio start.
                            float speechPause = v2AnimPlayer != null
                                ? v2AnimPlayer.CurrentCuePauseAfterSpeechSeconds
                                : 0f;
                            // This compatibility path exists only for legacy
                            // section-audio title cues.  New title cues use
                            // audio_plan; body cues must never pause/jump here,
                            // otherwise the tail of the narration is cut off.
                            if (!speechPauseDone && speechPause > 0f && IsLegacyTitleCue(cue))
                            {
                                float speechEndLocal = LastSpokenWordEnd(cue);
                                if (speechEndLocal > 0f
                                    && audioSource.time >= cue.audio_start + speechEndLocal - 0.02f)
                                {
                                    speechPauseDone = true;
                                    audioSource.Pause();
                                    animationHoldActive = true;
                                    animationHoldLocal = speechEndLocal;
                                    if (v2AnimPlayer != null) v2AnimPlayer.Seek(speechEndLocal);

                                    float remaining = speechPause;
                                    while (remaining > 0f)
                                    {
                                        if (isPaused)
                                        {
                                            yield return null;
                                            continue;
                                        }
                                        remaining -= Time.deltaTime;
                                        animationHoldLocal = speechEndLocal
                                            + Mathf.Clamp(speechPause - remaining, 0f, speechPause);
                                        if (v2AnimPlayer != null)
                                            v2AnimPlayer.Seek(animationHoldLocal);
                                        yield return null;
                                    }

                                    animationHoldActive = false;
                                    if (audioSource.clip != null)
                                    {
                                        audioSource.time = Mathf.Min(
                                            next.audio_start,
                                            Mathf.Max(0f, audioSource.clip.length - 0.02f));
                                        if (!isPaused) audioSource.UnPause();
                                    }
                                }
                            }

                            if (audioSource.time >= next.audio_start - 0.02f)
                            {
                                // If this cue explicitly asks to finish its animation
                                // before the next narration starts, pause the section
                                // audio at the boundary and let the animation catch up.
                                if (v2AnimPlayer != null && v2AnimPlayer.CurrentCueHoldAfterAudio)
                                {
                                    float holdLocal = Mathf.Max(0f, audioSource.time - cue.audio_start);
                                    if (v2AnimPlayer.TotalDuration > holdLocal + 0.02f)
                                    {
                                        bool wasPlaying = audioSource.isPlaying;
                                        animationHoldActive = true;
                                        animationHoldLocal = holdLocal;
                                        audioSource.Pause();
                                        yield return HoldAnimationToEnd(holdLocal);
                                        animationHoldActive = false;
                                        if (wasPlaying && !isPaused && audioSource != null) audioSource.UnPause();
                                    }
                                }
                                currentIndex = nextIndex;
                                cue = next;
                                v2AnimPlayer.LoadCue(cue.id);
                                lastPosition = Mathf.Max(0f, audioSource.time - cue.audio_start);
                                v2AnimPlayer.Seek(lastPosition);
                                UpdateSubtitle();
                            }
                        }
                    }
                }
                yield return null;
            }

            currentSubtitle = "";
            previousIndex = currentIndex;
            playbackFinished = true;
            if (pauseAtCueEnd) yield break;
            if (autoAdvance && currentIndex + 1 < doc.cues.Count)
            {
                if (v2AnimPlayer != null && v2AnimPlayer.CurrentCueHoldAfterAudio
                    && audioSource != null && audioSource.clip != null)
                {
                    float currentLocal = Mathf.Max(0f, audioSource.clip.length - cue.audio_start);
                    animationHoldActive = true;
                    animationHoldLocal = currentLocal;
                    yield return HoldAnimationToEnd(currentLocal);
                    animationHoldActive = false;
                }
                v2AnimPlayer.Complete();
                PlayCue(currentIndex + 1, true);
            }
        }

        private IEnumerator PlayAudioPlanCueRoutine(int index, float localTime, bool startPaused)
        {
            if (doc == null || doc.cues == null || index < 0 || index >= doc.cues.Count)
                yield break;

            if (audioSource.isPlaying) audioSource.Stop();
            audioSource.clip = null;
            currentIndex = index;
            isPaused = startPaused;
            pausedBeforePlay = startPaused;
            currentSubtitle = "";
            fallbackClock = 0f;
            playbackFinished = false;
            audioPlanActive = true;
            audioPlanAwaitingStart = false;
            audioPlanSegmentIsClip = false;

            var cue = doc.cues[index];
            float planDuration = AudioPlanDuration(cue);
            int preRollNextIndex = -1;
            bool nextSectionPreRollStarted = false;
            if (autoAdvance && !pauseAtCueEnd && index + 1 < doc.cues.Count)
            {
                var following = doc.cues[index + 1];
                if (following != null
                    && following.section_audio
                    && !HasAudioPlan(following)
                    && following.audio == cue.audio)
                    preRollNextIndex = index + 1;
            }
            if (planDuration <= 0f)
            {
                Debug.LogError($"[TutorialCuePlayer] cue has an empty audio_plan: {cue.id}");
                audioPlanActive = false;
                playbackFinished = true;
                lastPosition = Mathf.Max(0f, cue.duration);
                yield break;
            }

            float startLocal = Mathf.Clamp(localTime, 0f, planDuration);
            if (startLocal >= planDuration && planDuration > 0f)
                startLocal = Mathf.Max(0f, planDuration - 0.0005f);
            audioPlanLocal = startLocal;
            lastPosition = startLocal;

            if (!v2AnimPlayer.LoadCue(cue.id))
                Debug.LogError($"[TutorialCuePlayer] v2 LoadCue failed: {cue.id}");
            v2AnimPlayer.animationEnabled = enableCueAnimation;
            v2AnimPlayer.Seek(startLocal);
            UpdateSubtitle();

            float segmentStartLocal = 0f;
            int segmentIndex = FindAudioPlanSegment(cue, audioPlanLocal, out segmentStartLocal);
            while (segmentIndex >= 0 && segmentIndex < cue.audio_plan.Count && audioSource != null)
            {
                var segment = cue.audio_plan[segmentIndex];
                float segmentDuration = AudioPlanSegmentDuration(segment);
                if (segmentDuration <= 0f)
                {
                    segmentIndex++;
                    continue;
                }

                float segmentEndLocal = segmentStartLocal + segmentDuration;
                bool isClip = IsAudioPlanClip(segment);
                audioPlanSegmentIsClip = isClip;
                audioPlanAwaitingStart = false;

                if (isClip)
                {
                    float localInSegment = Mathf.Clamp(
                        audioPlanLocal - segmentStartLocal, 0f, segmentDuration);
                    bool clipReady = false;
                    yield return PrepareAudioPlanClip(segment, localInSegment, ready => clipReady = ready);
                    if (!clipReady)
                    {
                        Debug.LogError($"[TutorialCuePlayer] audio_plan clip failed: {cue.id}");
                        break;
                    }

                    if (isPaused)
                    {
                        audioPlanAwaitingStart = true;
                    }
                    else
                    {
                        audioSource.Play();
                    }
                }
                else if (!nextSectionPreRollStarted && audioSource.isPlaying)
                {
                    audioSource.Stop();
                }

                while (audioSource != null && audioPlanLocal < segmentEndLocal - 0.0005f)
                {
                    if (isPaused)
                    {
                        yield return null;
                        continue;
                    }

                    if (isClip)
                    {
                        if (audioPlanAwaitingStart)
                        {
                            audioPlanAwaitingStart = false;
                            audioSource.Play();
                        }
                        if (!audioSource.isPlaying)
                        {
                            audioPlanLocal = segmentEndLocal;
                            break;
                        }

                        float sourceLocal = audioSource.time - segment.start;
                        if (sourceLocal >= segmentDuration - 0.005f)
                        {
                            audioPlanLocal = segmentEndLocal;
                            break;
                        }
                        audioPlanLocal = Mathf.Clamp(
                            segmentStartLocal + sourceLocal,
                            segmentStartLocal,
                            segmentEndLocal);
                    }
                    else
                    {
                        audioPlanLocal = Mathf.Min(
                            segmentEndLocal,
                            audioPlanLocal + Time.deltaTime);

                        if (!nextSectionPreRollStarted
                            && preRollNextIndex >= 0
                            && planDuration - audioPlanLocal <= SectionAudioTransitionPrerollSeconds
                            && audioSource != null
                            && sectionAudioClip != null)
                        {
                            var following = doc.cues[preRollNextIndex];
                            if (following.audio == sectionAudioPath)
                            {
                                // Start the next section clip under the tail of
                                // this plan's silence.  By the cue boundary the
                                // decoder is already past its MP3 priming
                                // frames, so the next cue's first word is not
                                // clipped when the SectionPlayback routine
                                // takes over without another seek.
                                float remaining = Mathf.Clamp(
                                    planDuration - audioPlanLocal,
                                    0.001f,
                                    SectionAudioTransitionPrerollSeconds);
                                float preRollSource = Mathf.Clamp(
                                    following.audio_start - remaining,
                                    0f,
                                    Mathf.Max(0f, sectionAudioClip.length - 0.02f));
                                audioSource.clip = sectionAudioClip;
                                audioSource.time = preRollSource;
                                audioSource.Play();
                                audioPlanSegmentIsClip = true;
                                nextSectionPreRollStarted = true;
                                Debug.Log(
                                    $"[TutorialCuePlayer] preroll start cue={cue.id} "
                                    + $"next={following.id} plan_local={audioPlanLocal:0.000} "
                                    + $"remaining={remaining:0.000} source={preRollSource:0.000} "
                                    + $"audio_start={following.audio_start:0.000}");
                            }
                        }
                    }

                    lastPosition = audioPlanLocal;
                    if (v2AnimPlayer != null) v2AnimPlayer.Seek(audioPlanLocal);
                    UpdateSubtitle();
                    yield return null;
                }

                audioPlanLocal = segmentEndLocal;
                lastPosition = audioPlanLocal;
                if (!nextSectionPreRollStarted && audioSource.isPlaying)
                    audioSource.Stop();
                segmentStartLocal = segmentEndLocal;
                segmentIndex++;
            }

            audioPlanActive = false;
            audioPlanAwaitingStart = false;
            audioPlanSegmentIsClip = false;
            audioPlanLocal = planDuration;
            lastPosition = planDuration;
            bool handOffSectionAudio = nextSectionPreRollStarted
                && autoAdvance
                && !pauseAtCueEnd
                && preRollNextIndex >= 0;
            if (!handOffSectionAudio && audioSource != null && audioSource.isPlaying)
                audioSource.Stop();
            currentSubtitle = "";
            previousIndex = currentIndex;
            playbackFinished = true;
            if (v2AnimPlayer != null) v2AnimPlayer.Seek(planDuration);

            if (pauseAtCueEnd) yield break;
            if (autoAdvance && currentIndex + 1 < doc.cues.Count)
            {
                if (v2AnimPlayer != null) v2AnimPlayer.Complete();
                if (handOffSectionAudio)
                {
                    sectionAudioPreRolledCueIndex = preRollNextIndex;
                    preserveSectionAudioPreRollOnNextPlay = true;
                }
                PlayCue(currentIndex + 1, true);
            }
        }

        private IEnumerator PrepareAudioPlanClip(
            TutorialAudioPlanSegment segment,
            float localInSegment,
            Action<bool> onReady)
        {
            string audioRel = string.IsNullOrEmpty(segment.file)
                ? (CurrentCue != null ? CurrentCue.audio : "")
                : segment.file;
            string audioPath = Path.Combine(gameRoot, audioRel);
            if (sectionAudioClip == null || sectionAudioPath != audioRel)
            {
                if (audioSource != null)
                {
                    audioSource.Stop();
                    audioSource.clip = null;
                }
                if (sectionAudioClip != null)
                {
                    UnityEngine.Object.Destroy(sectionAudioClip);
                    sectionAudioClip = null;
                }
                sectionAudioPath = audioRel;
                AudioClip loaded = null;
                yield return LoadSectionAudioClip(audioPath, clip => loaded = clip);
                sectionAudioClip = loaded;
            }

            if (sectionAudioClip == null)
            {
                onReady?.Invoke(false);
                yield break;
            }

            float clipLength = Mathf.Max(0f, sectionAudioClip.length);
            float segmentDuration = AudioPlanSegmentDuration(segment);
            float sourceStart = Mathf.Clamp(segment.start, 0f, Mathf.Max(0f, clipLength - 0.02f));
            float sourceTime = sourceStart + Mathf.Clamp(localInSegment, 0f, segmentDuration);
            sourceTime = Mathf.Clamp(sourceTime, 0f, Mathf.Max(0f, clipLength - 0.02f));

            audioSource.clip = sectionAudioClip;
            audioSource.time = sourceTime;
            audioPlanSegmentIsClip = true;
            onReady?.Invoke(true);
        }

        private static float LastSpokenWordEnd(TutorialCue cue)
        {
            if (cue == null || cue.subtitles == null) return -1f;
            float lastEnd = -1f;
            foreach (var subtitle in cue.subtitles)
            {
                if (subtitle == null || subtitle.words == null) continue;
                foreach (var word in subtitle.words)
                {
                    if (word != null)
                        lastEnd = Mathf.Max(lastEnd, word.end);
                }
            }
            return lastEnd;
        }

        private System.Collections.IEnumerator HoldAnimationToEnd(float startLocal)
        {
            if (v2AnimPlayer == null) yield break;
            float animDuration = v2AnimPlayer.TotalDuration;
            float holdLocal = Mathf.Max(0f, startLocal);
            animationHoldLocal = holdLocal;
            while (holdLocal < animDuration - 0.02f)
            {
                if (isPaused)
                {
                    yield return null;
                    continue;
                }
                holdLocal += Time.deltaTime;
                animationHoldLocal = Mathf.Min(holdLocal, animDuration);
                v2AnimPlayer.Seek(animationHoldLocal);
                UpdateSubtitle();
                yield return null;
            }
            v2AnimPlayer.Seek(animDuration);
        }
    }
}
