using System;
using NAudio.Wave;

namespace JJFlexWpf
{
    /// <summary>
    /// Single CW element (dit or dah) as an ISampleProvider — sine wave shaped
    /// by a raised-cosine (half-cosine) attack and release envelope. Produces
    /// click-free keying suitable for prosign notifications, code practice,
    /// and on-air CW synthesis.
    /// </summary>
    /// <remarks>
    /// Raised-cosine is the amateur-radio community-standard minimum-click
    /// envelope for CW keying. The attack ramp follows 0.5·(1 − cos(πt/rise))
    /// and the release follows 0.5·(1 + cos(πt/fall)); between them the
    /// amplitude holds at 1.0. See QRP Labs' RC1 Raised Cosine Keyer and the
    /// ARRL's "Key-clicks and CW Waveform shaping" reference.
    ///
    /// Rise/fall time default 5 ms follows ARRL's 10%–90% recommendation for
    /// keying speeds up to 30 WPM. Shorter (~3 ms) preserves crisp feel at
    /// the expense of more clickiness; longer (~10 ms) is cleaner spectrally
    /// but can sound mushy for speed work. Expose this as a tunable later if
    /// operator preference diverges.
    ///
    /// Timing is sample-accurate — the audio engine, not a Task.Delay, drives
    /// element boundaries. Sequencing multiple elements through
    /// ConcatenatingSampleProvider preserves precise PARIS timing even under
    /// UI-thread load.
    /// </remarks>
    internal sealed class CwToneSampleProvider : ISampleProvider
    {
        private readonly WaveFormat _format;
        private readonly int _totalSamples;
        private readonly int _riseSamples;
        private readonly int _sustainSamples;
        private readonly int _fallSamples;
        private readonly double _phaseIncrement;
        private readonly float _amplitude;

        private int _position;
        private double _phase;

        public CwToneSampleProvider(int sampleRate, double frequencyHz,
                                    int durationMs, int riseFallMs, float amplitude)
        {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (durationMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(durationMs));

            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
            _totalSamples = (int)((long)sampleRate * durationMs / 1000);

            int requestedRise = (int)((long)sampleRate * Math.Max(1, riseFallMs) / 1000);
            // Cap rise+fall at the total so a very short element with a long
            // rise/fall setting still produces *some* sustained level.
            _riseSamples = Math.Max(1, Math.Min(requestedRise, _totalSamples / 2));
            _fallSamples = _riseSamples;
            _sustainSamples = Math.Max(0, _totalSamples - _riseSamples - _fallSamples);

            _phaseIncrement = 2.0 * Math.PI * frequencyHz / sampleRate;
            _amplitude = amplitude;
        }

        public WaveFormat WaveFormat => _format;

        public int Read(float[] buffer, int offset, int count)
        {
            int remaining = _totalSamples - _position;
            if (remaining <= 0) return 0;
            int toWrite = Math.Min(count, remaining);

            int pos = _position;
            double phase = _phase;
            double inc = _phaseIncrement;
            float amp = _amplitude;
            int rise = _riseSamples;
            int fallStart = _riseSamples + _sustainSamples;
            int fallLen = _fallSamples;
            const double twoPi = 2.0 * Math.PI;

            for (int i = 0; i < toWrite; i++)
            {
                float env;
                if (pos < rise)
                {
                    // Raised-cosine attack: 0.5 · (1 − cos(π · t / rise))
                    double t = (double)pos / rise;
                    env = (float)(0.5 * (1.0 - Math.Cos(Math.PI * t)));
                }
                else if (pos >= fallStart)
                {
                    // Raised-cosine release: 0.5 · (1 + cos(π · t / fall))
                    int fallPos = pos - fallStart;
                    double t = (double)fallPos / fallLen;
                    env = (float)(0.5 * (1.0 + Math.Cos(Math.PI * t)));
                }
                else
                {
                    env = 1.0f;
                }

                buffer[offset + i] = env * amp * (float)Math.Sin(phase);
                phase += inc;
                if (phase > twoPi) phase -= twoPi;
                pos++;
            }

            _position = pos;
            _phase = phase;
            return toWrite;
        }
    }
}
