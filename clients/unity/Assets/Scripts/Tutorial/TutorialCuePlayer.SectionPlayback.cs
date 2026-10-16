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

        private bool IsSectionAudioCue(int index)
        {
            return doc != null
                && doc.cues != null
                && index >= 0
                && index < doc.cues.Count
                && doc.cues[index] != null
                && doc.cues[index].section_audio;
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

            if (audioSource.isPlaying) audioSource.Stop();
            currentIndex = index;
            isPaused = startPaused;
            pausedBeforePlay = startPaused;
            currentSubtitle = "";
            fallbackClock = 0f;
            lastPosition = Mathf.Max(0f, localTime);
            playbackFinished = false;

            var cue = doc.cues[index];
            if (!v2AnimPlayer.LoadCue(cue.id))
                Debug.LogError($"[TutorialCuePlayer] v2 LoadCue failed: {cue.id}");
            v2AnimPlayer.animationEnabled = enableCueAnimation;

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

            UpdateSubtitle();
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
