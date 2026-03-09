using System;
using NAudio.Wave;

namespace JJFlexWpf
{
    /// <summary>
    /// A persistent ISampleProvider that generates a phase-continuous sine wave
    /// with dynamically updating frequency and volume. Designed to live in the
    /// EarconPlayer mixer permanently — generates silence when inactive.
    ///
    /// Thread safety: Frequency, Volume, and Active are volatile floats/bools
    /// updated from meter callbacks on arbitrary threads.
    /// </summary>
    public class ContinuousToneSampleProvider : ISampleProvider
    {
        private const int SampleRate = 44100;
        private const int FadeSamples = 441; // 10ms fade for click-free on/off

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1); // Mono

        /// <summary>Current tone frequency in Hz. Updated by MeterToneEngine.</summary>
        public volatile float Frequency;

        /// <summary>Tone volume 0.0–1.0. Updated by MeterToneEngine.</summary>
        public volatile float Volume;

        /// <summary>Master gate. When false, tone fades to silence over 10ms.</summary>
        public volatile bool Active;

        private double _phase;

        // Fade state: 0.0 = silent, 1.0 = full volume
        private float _fadeLevel;

        public ContinuousToneSampleProvider(float initialFrequency = 440f, float initialVolume = 0.5f)
        {
            Frequency = initialFrequency;
            Volume = initialVolume;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // Capture volatile values once per buffer for consistency
            float targetFreq = Frequency;
            float targetVol = Volume;
            bool active = Active;

            // If completely silent and not active, fast-fill zeros
            if (!active && _fadeLevel <= 0f)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            // Capture starting frequency for interpolation across the buffer
            // (smooth frequency transitions prevent clicks)
            float startFreq = targetFreq; // Will refine with per-sample lerp

            float fadeTarget = active ? 1.0f : 0.0f;
            float fadeStep = (fadeTarget - _fadeLevel) / Math.Max(count, 1);
            // Clamp fade step to achieve ~10ms ramp regardless of buffer size
            float maxFadePerSample = 1.0f / FadeSamples;
            if (Math.Abs(fadeStep) > maxFadePerSample)
                fadeStep = fadeTarget > _fadeLevel ? maxFadePerSample : -maxFadePerSample;

            for (int i = 0; i < count; i++)
            {
                // Update fade
                if (fadeStep > 0 && _fadeLevel < 1.0f)
                    _fadeLevel = Math.Min(_fadeLevel + fadeStep, 1.0f);
                else if (fadeStep < 0 && _fadeLevel > 0f)
                    _fadeLevel = Math.Max(_fadeLevel + fadeStep, 0f);

                float currentVol = targetVol * _fadeLevel;

                // Generate sine sample
                buffer[offset + i] = (float)(Math.Sin(2.0 * Math.PI * _phase) * currentVol);

                // Advance phase using current frequency
                _phase += targetFreq / SampleRate;
                if (_phase >= 1.0)
                    _phase -= Math.Floor(_phase);
            }

            return count;
        }
    }
}
