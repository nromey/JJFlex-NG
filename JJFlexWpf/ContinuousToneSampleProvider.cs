using System;
using NAudio.Wave;

namespace JJFlexWpf
{
    /// <summary>
    /// Waveform shape for continuous tone generation.
    /// Sprint 22: Pulled forward from Phase 9 for ATU progress earcon.
    /// </summary>
    public enum WaveformType
    {
        /// <summary>Smooth pure tone (default).</summary>
        Sine,
        /// <summary>Buzzier, more distinct — odd harmonics only.</summary>
        Square,
        /// <summary>Brighter, full harmonic series.</summary>
        Sawtooth,
        /// <summary>Sine on 300ms, off 300ms — gentle pulsing.</summary>
        SlowPulse,
        /// <summary>Sine on 100ms, off 100ms — urgent pulsing (ATU progress).</summary>
        FastPulse,
        /// <summary>Alternates between Frequency and AltFrequency at 100ms intervals.</summary>
        Alternating
    }

    /// <summary>
    /// A persistent ISampleProvider that generates a phase-continuous waveform
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

        /// <summary>Waveform shape. Default is Sine (backwards compatible).</summary>
        public volatile WaveformType Waveform;

        /// <summary>Alternate frequency for WaveformType.Alternating.</summary>
        public volatile float AltFrequency;

        private double _phase;

        // Fade state: 0.0 = silent, 1.0 = full volume
        private float _fadeLevel;

        // Pulse/alternating state tracking (sample counter)
        private int _pulseCounter;

        public ContinuousToneSampleProvider(float initialFrequency = 440f, float initialVolume = 0.5f)
        {
            Frequency = initialFrequency;
            Volume = initialVolume;
            AltFrequency = initialFrequency * 1.25f; // Default alt = major third above
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // Capture volatile values once per buffer for consistency
            float targetFreq = Frequency;
            float targetVol = Volume;
            bool active = Active;
            var waveform = Waveform;
            float altFreq = AltFrequency;

            // If completely silent and not active, fast-fill zeros
            if (!active && _fadeLevel <= 0f)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            float fadeTarget = active ? 1.0f : 0.0f;
            float fadeStep = (fadeTarget - _fadeLevel) / Math.Max(count, 1);
            // Clamp fade step to achieve ~10ms ramp regardless of buffer size
            float maxFadePerSample = 1.0f / FadeSamples;
            if (Math.Abs(fadeStep) > maxFadePerSample)
                fadeStep = fadeTarget > _fadeLevel ? maxFadePerSample : -maxFadePerSample;

            // Pulse timing constants (in samples)
            const int slowPulseOn = (int)(SampleRate * 0.3); // 300ms
            const int slowPulseCycle = (int)(SampleRate * 0.6); // 600ms total
            const int fastPulseOn = (int)(SampleRate * 0.05); // 50ms
            const int fastPulseCycle = (int)(SampleRate * 0.1); // 100ms total
            const int altSwitch = (int)(SampleRate * 0.1); // 100ms per tone
            const int altCycle = (int)(SampleRate * 0.2); // 200ms total

            for (int i = 0; i < count; i++)
            {
                // Update fade
                if (fadeStep > 0 && _fadeLevel < 1.0f)
                    _fadeLevel = Math.Min(_fadeLevel + fadeStep, 1.0f);
                else if (fadeStep < 0 && _fadeLevel > 0f)
                    _fadeLevel = Math.Max(_fadeLevel + fadeStep, 0f);

                float currentVol = targetVol * _fadeLevel;

                // Determine effective frequency and pulse gating
                float effectiveFreq = targetFreq;
                bool pulseGate = true; // true = sound on, false = silence

                switch (waveform)
                {
                    case WaveformType.SlowPulse:
                        pulseGate = (_pulseCounter % slowPulseCycle) < slowPulseOn;
                        break;
                    case WaveformType.FastPulse:
                        pulseGate = (_pulseCounter % fastPulseCycle) < fastPulseOn;
                        break;
                    case WaveformType.Alternating:
                        effectiveFreq = (_pulseCounter % altCycle) < altSwitch ? targetFreq : altFreq;
                        break;
                }

                _pulseCounter++;

                // Generate waveform sample
                float sample;
                if (!pulseGate)
                {
                    sample = 0f;
                }
                else
                {
                    switch (waveform)
                    {
                        case WaveformType.Square:
                            sample = _phase < 0.5 ? 1.0f : -1.0f;
                            break;
                        case WaveformType.Sawtooth:
                            sample = (float)(2.0 * _phase - 1.0);
                            break;
                        default: // Sine, SlowPulse, FastPulse, Alternating all use sine base
                            sample = (float)Math.Sin(2.0 * Math.PI * _phase);
                            break;
                    }
                }

                buffer[offset + i] = sample * currentVol;

                // Advance phase using effective frequency
                _phase += effectiveFreq / SampleRate;
                if (_phase >= 1.0)
                    _phase -= Math.Floor(_phase);
            }

            return count;
        }
    }
}
