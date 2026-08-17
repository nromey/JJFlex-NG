using System;
using NAudio.Wave;

namespace JJFlexWpf
{
    /// <summary>
    /// Renders a <see cref="MeterVoice"/> as a continuous, phase-continuous
    /// tone with live frequency, volume, pan and voice swapping. This is the
    /// data-driven successor to <see cref="ContinuousToneSampleProvider"/>:
    /// there is no switch over voice kinds anywhere — additive partials,
    /// brightness tilt, inharmonicity, tremolo, vibrato, gating, pitch
    /// alternation, ADS envelope and filtered noise are all just parameters
    /// read from the referenced voice every buffer.
    ///
    /// Output is STEREO at the EarconPlayer mixer rate with equal-power
    /// panning applied here, from the live <see cref="Pan"/> field — so pan
    /// changes take effect immediately (the old provider's pan was baked in
    /// at mixer registration and could never change).
    ///
    /// Thread safety: Frequency, Volume, Pan, Active and Voice are written
    /// from meter callbacks and UI threads; each is read once per buffer.
    /// Voice objects follow the MeterVoice contract: scalars may be mutated
    /// live, the Partials array is replaced wholesale, never edited in place.
    /// </summary>
    public class VoicedToneSampleProvider : ISampleProvider
    {
        private const int SampleRate = EarconPlayer.MixerSampleRate;
        private const int MaxPartials = 24;
        private const float ActivationFadeMs = 10f;   // click-free on/off
        private const float GateReleaseMs = 8f;       // click-free gate-off
        private const float FrequencyGlideMs = 25f;   // smooths 10 Hz value steps

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        /// <summary>Target tone frequency in Hz (the meter's value, mapped).
        /// Glided over ~25 ms so stepped meter updates sound continuous.</summary>
        public volatile float Frequency;

        /// <summary>Tone volume 0.0–1.0.</summary>
        public volatile float Volume;

        /// <summary>Stereo position, -1 left .. +1 right. Live.</summary>
        public volatile float Pan;

        /// <summary>Master gate. When false, fades to silence over 10 ms.</summary>
        public volatile bool Active;

        private MeterVoice _voice = MeterVoiceLibrary.Resolve(null);

        /// <summary>The voice being rendered. Swap live; takes effect on the
        /// next buffer. Never null (setting null resolves to the default).</summary>
        public MeterVoice Voice
        {
            get => _voice;
            set => _voice = value ?? MeterVoiceLibrary.Resolve(null);
        }

        // ---- render state ----
        private readonly double[] _partialPhase = new double[MaxPartials];
        private readonly float[] _partialAmp = new float[MaxPartials];
        private float _smoothedFreq;
        private float _fadeLevel;            // activation fade 0..1
        private double _tremoloPhase;        // cycles
        private double _vibratoPhase;        // cycles
        private double _alternatePhase;      // cycles
        private double _gatePositionMs;      // position inside the gate cycle
        private bool _gateWasOpen = true;

        // Envelope
        private enum EnvStage { Idle, Attack, Decay, Sustain, Release }
        private EnvStage _envStage = EnvStage.Idle;
        private float _envLevel;
        private bool _wasActive;

        // Noise: xorshift white source + RBJ bandpass biquad
        private uint _noiseSeed;
        private float _bpA0, _bpA1, _bpA2, _bpB1, _bpB2;
        private float _bpX1, _bpX2, _bpY1, _bpY2;
        private float _bpCenter = -1f, _bpBandwidth = -1f;

        // Shared sine table
        private const int SineTableSize = 4096;
        private static readonly float[] SineTable = BuildSineTable();

        private static float[] BuildSineTable()
        {
            var t = new float[SineTableSize + 1];
            for (int i = 0; i <= SineTableSize; i++)
                t[i] = (float)Math.Sin(2.0 * Math.PI * i / SineTableSize);
            return t;
        }

        private static float SineAt(double phaseCycles)
        {
            double frac = phaseCycles - Math.Floor(phaseCycles);
            double idx = frac * SineTableSize;
            int i0 = (int)idx;
            float t = (float)(idx - i0);
            return SineTable[i0] + (SineTable[i0 + 1] - SineTable[i0]) * t;
        }

        public VoicedToneSampleProvider(float initialFrequency = 440f, float initialVolume = 0.5f)
        {
            Frequency = initialFrequency;
            Volume = initialVolume;
            _smoothedFreq = initialFrequency;
            _noiseSeed = (uint)Environment.TickCount | 1u;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // count is in floats; stereo → frames
            int frames = count / 2;

            var voice = _voice;
            float targetFreq = Math.Clamp(Frequency, 20f, SampleRate * 0.45f);
            float volume = Volume;
            float pan = Math.Clamp(Pan, -1f, 1f);
            bool active = Active;

            // Rising edge: (re)trigger the envelope so attack character is
            // audible on every activation, not only the first.
            if (active && !_wasActive)
            {
                _envStage = EnvStage.Attack;
                _gatePositionMs = 0;
                _gateWasOpen = true;
            }
            _wasActive = active;

            // Fast path: fully silent and inactive.
            if (!active && _fadeLevel <= 0f)
            {
                Array.Clear(buffer, offset, count);
                _envStage = EnvStage.Idle;
                return count;
            }

            // ---- per-buffer voice parameter snapshot ----
            float[] partials = voice.Partials is { Length: > 0 } p ? p : new[] { 1f };
            int partialCount = Math.Min(partials.Length, MaxPartials);
            float brightness = voice.Brightness;
            float inharmonicity = voice.Inharmonicity;

            // Brightness tilt + equal-power normalisation, computed once per
            // buffer (parameters change far slower than the sample clock).
            float ampSquares = 0f;
            for (int n = 0; n < partialCount; n++)
            {
                float amp = partials[n];
                if (brightness != 0f)
                    amp *= (float)Math.Pow(n + 1, 1.5 * brightness);
                _partialAmp[n] = amp;
                ampSquares += amp * amp;
            }
            float norm = ampSquares > 0f ? 1f / (float)Math.Sqrt(ampSquares) : 0f;

            float tremRate = voice.TremoloRateHz;
            float tremDepth = Math.Clamp(voice.TremoloDepth, 0f, 1f);
            float vibRate = voice.VibratoRateHz;
            float vibSemis = voice.VibratoDepthSemitones;
            float altSemis = voice.AlternateIntervalSemitones;
            float altRate = voice.AlternateRateHz;
            float altMul = altSemis != 0f ? (float)Math.Pow(2.0, altSemis / 12.0) : 1f;
            float gateOnMs = Math.Max(voice.GateOnMs, 0f);
            float gateOffMs = Math.Max(voice.GateOffMs, 0f);
            bool gated = gateOnMs > 0f && gateOffMs > 0f;
            float gateCycleMs = gateOnMs + gateOffMs;

            float attackStep = 1f / (Math.Max(voice.AttackMs, 0.5f) * SampleRate / 1000f);
            float sustain = Math.Clamp(voice.SustainLevel, 0f, 1f);
            float decayStep = voice.DecayMs > 0.5f
                ? (1f - sustain) / (voice.DecayMs * SampleRate / 1000f)
                : float.MaxValue; // no decay phase: jump straight to sustain
            float releaseStep = 1f / (GateReleaseMs * SampleRate / 1000f);

            float noiseLevel = Math.Clamp(voice.NoiseLevel, 0f, 1f);
            if (noiseLevel > 0f)
            {
                float center = voice.NoiseTracksPitch ? targetFreq
                    : Math.Clamp(voice.NoiseCenterHz, 50f, SampleRate * 0.45f);
                float bw = Math.Clamp(voice.NoiseBandwidthHz, 20f, SampleRate * 0.4f);
                UpdateBandpass(center, bw);
            }

            float fadeStepPerSample = 1f / (ActivationFadeMs * SampleRate / 1000f);
            float glideCoeff = 1f - (float)Math.Exp(-1.0 /
                (FrequencyGlideMs * SampleRate / 1000.0));

            float msPerSample = 1000f / SampleRate;
            // Post-mix scale keeps tone+noise in comparable loudness territory.
            float mixScale = 1f / (1f + noiseLevel * 0.5f);

            // Equal-power pan gains — pan is sampled once per buffer.
            float panAngle = (pan + 1f) * 0.25f * (float)Math.PI;
            float panL = (float)Math.Cos(panAngle);
            float panR = (float)Math.Sin(panAngle);

            for (int i = 0; i < frames; i++)
            {
                // Activation fade
                if (active)
                    _fadeLevel = Math.Min(_fadeLevel + fadeStepPerSample, 1f);
                else
                    _fadeLevel = Math.Max(_fadeLevel - fadeStepPerSample, 0f);

                // Frequency glide
                _smoothedFreq += (targetFreq - _smoothedFreq) * glideCoeff;
                float f = _smoothedFreq;

                // Vibrato (small-x 2^x approximation; x ≤ ~0.1)
                if (vibRate > 0f && vibSemis != 0f)
                {
                    float x = vibSemis * SineAt(_vibratoPhase) / 12f * 0.6931472f;
                    f *= 1f + x + 0.5f * x * x;
                    _vibratoPhase += vibRate / SampleRate;
                }

                // Pitch alternation: square LFO between base and base+interval
                if (altSemis != 0f && altRate > 0f)
                {
                    double altFrac = _alternatePhase - Math.Floor(_alternatePhase);
                    if (altFrac >= 0.5) f *= altMul;
                    _alternatePhase += altRate / SampleRate;
                }

                // Gate pattern with envelope retrigger on the on-edge
                bool gateOpen = true;
                if (gated)
                {
                    gateOpen = _gatePositionMs < gateOnMs;
                    if (gateOpen && !_gateWasOpen)
                        _envStage = EnvStage.Attack; // retrigger: repeating strikes
                    if (!gateOpen && _gateWasOpen)
                        _envStage = EnvStage.Release;
                    _gateWasOpen = gateOpen;
                    _gatePositionMs += msPerSample;
                    if (_gatePositionMs >= gateCycleMs) _gatePositionMs -= gateCycleMs;
                }

                // Envelope
                switch (_envStage)
                {
                    case EnvStage.Attack:
                        _envLevel += attackStep;
                        if (_envLevel >= 1f) { _envLevel = 1f; _envStage = EnvStage.Decay; }
                        break;
                    case EnvStage.Decay:
                        if (decayStep == float.MaxValue) { _envLevel = sustain; _envStage = EnvStage.Sustain; }
                        else
                        {
                            _envLevel -= decayStep;
                            if (_envLevel <= sustain) { _envLevel = sustain; _envStage = EnvStage.Sustain; }
                        }
                        break;
                    case EnvStage.Sustain:
                        _envLevel = sustain;
                        break;
                    case EnvStage.Release:
                        _envLevel = Math.Max(_envLevel - releaseStep, 0f);
                        break;
                    case EnvStage.Idle:
                        _envLevel = 0f;
                        break;
                }

                // Additive partial sum
                float tone = 0f;
                double phaseInc = f / SampleRate;
                for (int n = 0; n < partialCount; n++)
                {
                    float amp = _partialAmp[n];
                    double mul = (n + 1) * (1.0 + inharmonicity * n);
                    double pf = phaseInc * mul;
                    if (pf < 0.45) // skip partials at/above Nyquist territory
                        tone += amp * SineAt(_partialPhase[n]);
                    _partialPhase[n] += pf;
                    if (_partialPhase[n] >= 1024.0) _partialPhase[n] -= 1024.0;
                }
                tone *= norm;

                // Filtered noise, sharing the envelope and modulation chain
                if (noiseLevel > 0f)
                {
                    tone += Bandpass(NextWhite()) * noiseLevel * 2.5f;
                    tone *= mixScale;
                }

                // Tremolo
                if (tremRate > 0f && tremDepth > 0f)
                {
                    tone *= 1f - tremDepth * (0.5f + 0.5f * SineAt(_tremoloPhase));
                    _tremoloPhase += tremRate / SampleRate;
                }

                float sample = tone * _envLevel * _fadeLevel * volume;

                buffer[offset + i * 2] = sample * panL;
                buffer[offset + i * 2 + 1] = sample * panR;
            }

            return count;
        }

        // ---- noise helpers ----

        private float NextWhite()
        {
            // xorshift32 → -1..1
            _noiseSeed ^= _noiseSeed << 13;
            _noiseSeed ^= _noiseSeed >> 17;
            _noiseSeed ^= _noiseSeed << 5;
            return (_noiseSeed / (float)uint.MaxValue) * 2f - 1f;
        }

        private void UpdateBandpass(float center, float bandwidth)
        {
            // Recompute only on meaningful movement — coefficients are stable
            // within a buffer anyway.
            if (_bpCenter > 0 &&
                Math.Abs(center - _bpCenter) < _bpCenter * 0.02f &&
                Math.Abs(bandwidth - _bpBandwidth) < _bpBandwidth * 0.05f)
                return;

            _bpCenter = center;
            _bpBandwidth = bandwidth;
            float q = Math.Clamp(center / bandwidth, 0.3f, 30f);
            double w0 = 2.0 * Math.PI * center / SampleRate;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            // RBJ constant-peak-gain bandpass
            _bpA0 = (float)(alpha / a0);
            _bpA1 = 0f;
            _bpA2 = (float)(-alpha / a0);
            _bpB1 = (float)(-2.0 * cosw0 / a0);
            _bpB2 = (float)((1.0 - alpha) / a0);
        }

        private float Bandpass(float x)
        {
            float y = _bpA0 * x + _bpA1 * _bpX1 + _bpA2 * _bpX2
                      - _bpB1 * _bpY1 - _bpB2 * _bpY2;
            _bpX2 = _bpX1; _bpX1 = x;
            _bpY2 = _bpY1; _bpY1 = y;
            return y;
        }

        /// <summary>
        /// Offline one-shot render of a voice — the earcon path (Track H) and
        /// audition previews use this. Returns MONO samples at the mixer rate,
        /// including a clean fade-out tail so the clip never clicks.
        /// </summary>
        public static float[] RenderMono(MeterVoice voice, float frequencyHz,
            int durationMs, float volume = 0.5f)
        {
            var p = new VoicedToneSampleProvider(frequencyHz, volume)
            {
                Voice = voice,
                Pan = -1f, // full left = unity gain on the left channel
                Active = true,
            };
            int bodyFrames = Math.Max(SampleRate * durationMs / 1000, 1);
            int tailFrames = SampleRate * 15 / 1000;
            var stereo = new float[(bodyFrames + tailFrames) * 2];
            p.Read(stereo, 0, bodyFrames * 2);
            p.Active = false; // let the activation fade finish the clip
            p.Read(stereo, bodyFrames * 2, tailFrames * 2);

            var mono = new float[bodyFrames + tailFrames];
            for (int i = 0; i < mono.Length; i++)
                mono[i] = stereo[i * 2];
            return mono;
        }
    }
}
