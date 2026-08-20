using System;
using NAudio.Wave;

namespace JJFlexWpf
{
    /// <summary>
    /// Single CW element (dit or dah) as an ISampleProvider — a tone shaped
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
    ///
    /// <para>
    /// <b>Spectrum (#145, Sprint 33 Track F).</b> The tone is no longer forced
    /// to be a sine. A <see cref="MeterVoice"/> may be supplied, in which case
    /// its PARTIALS are summed additively — the same grammar the meter engine
    /// and the alert earcons use, so the assembly still has one idea of what a
    /// square wave is made of rather than a second one written for CW.
    /// </para>
    /// <para>
    /// <b>What is deliberately NOT taken from the voice, and why.</b> Attack,
    /// decay, sustain, gating, tremolo, vibrato, pitch alternation and noise
    /// are ignored. The keying envelope is not a matter of taste: it is what
    /// stops the tone clicking, and a square or saw wave with a hard edge
    /// clicks HARDER than a sine, not less — a waveform option that
    /// reintroduced key clicks would be a regression wearing a feature's
    /// clothes. Gating is worse still: a 60 ms dit at 20 WPM is shorter than
    /// most gate cycles, so a gated voice would chop characters into fragments
    /// and the Morse would stop being Morse. Only the spectrum crosses over.
    /// </para>
    /// <para>
    /// <b>Loudness.</b> Partials are normalised equal-power, exactly as
    /// <see cref="VoicedToneSampleProvider"/> does it, so changing waveform
    /// changes the CHARACTER of the sound and not its level. That matters more
    /// than it sounds: without it a square wave would simply be louder, an
    /// operator would report it as more audible, and the app would have learned
    /// the wrong lesson about why (#115 is about camouflage, not gain).
    /// </para>
    /// </remarks>
    internal sealed class CwToneSampleProvider : ISampleProvider
    {
        /// <summary>Ceiling on how many partials are summed. Matches
        /// VoicedToneSampleProvider so neither renderer can be handed a
        /// spectrum the other would truncate differently.</summary>
        private const int MaxPartials = 24;

        private readonly WaveFormat _format;
        private readonly int _totalSamples;
        private readonly int _riseSamples;
        private readonly int _sustainSamples;
        private readonly int _fallSamples;
        private readonly float _amplitude;

        // One phase accumulator and one gain per partial. A plain sine is the
        // degenerate case: a single partial at unity, which is bit-for-bit the
        // sound this class produced before it learned about spectra.
        private readonly double[] _phaseIncrement;
        private readonly float[] _partialGain;
        private readonly double[] _phase;

        private int _position;

        public CwToneSampleProvider(int sampleRate, double frequencyHz,
                                    int durationMs, int riseFallMs, float amplitude)
            : this(sampleRate, frequencyHz, durationMs, riseFallMs, amplitude, null)
        {
        }

        public CwToneSampleProvider(int sampleRate, double frequencyHz,
                                    int durationMs, int riseFallMs, float amplitude,
                                    MeterVoice? voice)
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

            _amplitude = amplitude;

            BuildSpectrum(sampleRate, frequencyHz, voice,
                out _phaseIncrement, out _partialGain);
            _phase = new double[_phaseIncrement.Length];
        }

        /// <summary>
        /// Turn a voice's spectrum into per-partial frequencies and gains.
        ///
        /// Three things happen here and each one has a reason:
        ///
        /// Partials at or above Nyquist are DROPPED, not folded. A 700 Hz
        /// sawtooth with twelve harmonics reaches 8.4 kHz and is safe at the
        /// mixer's 44.1 kHz, but an operator who sets the sidetone to 1200 Hz
        /// and picks a rich waveform gets closer to the limit than is
        /// comfortable — and an aliased partial does not sound like a harmonic,
        /// it sounds like a fault.
        ///
        /// Inharmonicity is honoured because it is a spectrum property, and it
        /// is the one axis that makes a CW notification sound like nothing else
        /// on the band. Brightness is honoured for the same reason.
        ///
        /// Gains are normalised equal-power, so the sum of squares is 1
        /// whatever the partial count. See the loudness note in the class
        /// remarks.
        /// </summary>
        private static void BuildSpectrum(int sampleRate, double frequencyHz,
            MeterVoice? voice, out double[] increments, out float[] gains)
        {
            float[] partials = voice?.Partials is { Length: > 0 } p ? p : new[] { 1f };
            int count = Math.Min(partials.Length, MaxPartials);
            float brightness = voice?.Brightness ?? 0f;
            float inharmonicity = voice?.Inharmonicity ?? 0f;
            double nyquistGuard = sampleRate * 0.45;

            var incList = new double[count];
            var gainList = new float[count];
            int kept = 0;
            float ampSquares = 0f;

            for (int n = 0; n < count; n++)
            {
                float amp = partials[n];
                if (amp == 0f) continue;
                if (brightness != 0f)
                    amp *= (float)Math.Pow(n + 1, 1.5 * brightness);

                double mul = (n + 1) * (1.0 + inharmonicity * n);
                double f = frequencyHz * mul;
                if (f >= nyquistGuard) continue;

                incList[kept] = 2.0 * Math.PI * f / sampleRate;
                gainList[kept] = amp;
                ampSquares += amp * amp;
                kept++;
            }

            if (kept == 0)
            {
                // Every partial was silent or above Nyquist. A silent dit is
                // worse than a plain one, so fall back to the fundamental.
                increments = new[] { 2.0 * Math.PI * frequencyHz / sampleRate };
                gains = new[] { 1f };
                return;
            }

            float norm = ampSquares > 0f ? 1f / (float)Math.Sqrt(ampSquares) : 1f;
            increments = new double[kept];
            gains = new float[kept];
            for (int i = 0; i < kept; i++)
            {
                increments[i] = incList[i];
                gains[i] = gainList[i] * norm;
            }
        }

        public WaveFormat WaveFormat => _format;

        // NAudio 3.0: ISampleProvider.Read takes a Span<float>. offset/count
        // are re-declared here so the body's index arithmetic is unchanged -
        // buffer[offset + n] indexes a Span exactly as it did an array.
        public int Read(Span<float> buffer)
        {
            int offset = 0;
            int count = buffer.Length;
            int remaining = _totalSamples - _position;
            if (remaining <= 0) return 0;
            int toWrite = Math.Min(count, remaining);

            int pos = _position;
            float amp = _amplitude;
            int rise = _riseSamples;
            int fallStart = _riseSamples + _sustainSamples;
            int fallLen = _fallSamples;
            const double twoPi = 2.0 * Math.PI;
            int partials = _phase.Length;

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

                float tone = 0f;
                for (int n = 0; n < partials; n++)
                {
                    tone += _partialGain[n] * (float)Math.Sin(_phase[n]);
                    _phase[n] += _phaseIncrement[n];
                    if (_phase[n] > twoPi) _phase[n] -= twoPi;
                }

                buffer[offset + i] = env * amp * tone;
                pos++;
            }

            _position = pos;
            return toWrite;
        }
    }
}
