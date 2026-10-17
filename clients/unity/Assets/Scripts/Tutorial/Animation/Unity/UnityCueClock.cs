// Unity DSP-backed implementation of the pure controller's clock contract.
using UnityEngine;

namespace BoardGameTutorial.Animation
{
    /// <summary>
    /// The controller remains the only owner of cue-local time.  This adapter
    /// merely exposes a monotonic DSP timestamp; it never starts or completes
    /// a cue and it never reads AudioSource.time as a clock.
    /// </summary>
    public sealed class UnityDspCueClock : ICueClock
    {
        public double NowSeconds
        {
            get { return AudioSettings.dspTime; }
        }
    }
}
