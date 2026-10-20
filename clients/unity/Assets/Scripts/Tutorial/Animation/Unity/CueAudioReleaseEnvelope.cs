using UnityEngine;

namespace BoardGameTutorial.Animation
{
    // Each course AudioSource has its own GameObject and filter. Applying a
    // filter to their shared parent would fade unrelated/newly scheduled audio.
    public sealed class CueAudioReleaseEnvelope : MonoBehaviour
    {
        private readonly CueAudioReleaseRamp ramp = new CueAudioReleaseRamp();

        public bool IsSilent { get { return ramp.IsSilent; } }
        public void ResetEnvelope() { ramp.Reset(); }
        public void Release() { ramp.Release(AudioSettings.outputSampleRate); }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            ramp.Process(data, channels);
        }
    }
}
