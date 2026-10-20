using System;
using System.Threading;

namespace BoardGameTutorial.Animation
{
    // Main thread sends commands; only the audio thread touches ramp position.
    // No locks, allocations, or Unity APIs on the audio processing path.
    public sealed class CueAudioReleaseRamp
    {
        private long command;
        private long acknowledged;
        private long revision;
        private long appliedCommand;
        private int frames;
        private int position;

        public bool IsSilent
        {
            get
            {
                long current = Interlocked.Read(ref command);
                return (int)current > 0 && Interlocked.Read(ref acknowledged) == current;
            }
        }

        public void Reset()
        {
            Interlocked.Exchange(ref command, (++revision) << 32);
        }

        public void Release(int sampleRate)
        {
            int count = Math.Max(2, (int)Math.Round(Math.Max(1, sampleRate) * 0.004));
            Interlocked.Exchange(ref command, ((++revision) << 32) | (uint)count);
        }

        public void Process(float[] data, int channels)
        {
            if (channels <= 0) return;
            long current = Interlocked.Read(ref command);
            if (current != appliedCommand)
            {
                appliedCommand = current;
                frames = (int)current;
                position = 0;
            }
            if (frames <= 0) return; // Continuous playback is bit-identical.
            for (int offset = 0; offset < data.Length; offset += channels)
            {
                float gain = position >= frames ? 0f
                    : (float)(0.5 + 0.5 * Math.Cos(Math.PI * position / (frames - 1)));
                for (int channel = 0; channel < channels && offset + channel < data.Length; channel++)
                    data[offset + channel] *= gain;
                if (position < frames) position++;
            }
            if (position >= frames) Interlocked.Exchange(ref acknowledged, current);
        }
    }
}
