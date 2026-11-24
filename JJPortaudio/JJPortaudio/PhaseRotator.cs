using System;

namespace JJPortaudio
{
    /// <summary>
    /// The phase rotator ahead of the transmit compressor (Sprint 49 Track CL,
    /// #1278): up to six second-order all-pass sections in series. An all-pass
    /// changes no frequency's level, only its phase, so a voice whose peaks are
    /// lopsided (taller on one side of zero than the other) comes out more
    /// symmetric: the same sound and the same average power under a lower
    /// peak, which lets the compressor after it run hotter at the same peak.
    /// It is the broadcast technique for "more talk power without more peaks".
    /// </summary>
    /// <remarks>
    /// <para><b>Written from the published all-pass formula</b> (the widely
    /// published bilinear-transform second-order all-pass: numerator
    /// coefficients the denominator's in reverse order, so the magnitude is
    /// exactly one at every frequency), per sample rate. The staggered centres
    /// (300, 700, 1500 and 2500 Hz, then 1000 and 2000 Hz) and the wide
    /// section Q of 0.5 are the common broadcast pattern, read as numbers from
    /// a reference design's description; no code was copied.</para>
    /// <para><b>Every centre is below Nyquist at every transmit rate:</b> the
    /// highest is 2500 Hz and the lowest rate 8 kHz, whose Nyquist is 4 kHz.
    /// A test proves it for all five rates.</para>
    /// <para><b>Not a pure delay.</b> An all-pass has group delay that depends
    /// on frequency (largest near each centre), which is exactly how it moves
    /// the harmonics against each other. Its applied delay, as the chain
    /// counts delay, is zero: <see cref="GroupDelaySeconds"/> gives the real
    /// figure per frequency for the record.</para>
    /// <para>Zero stages is the input, untouched. No lock and no allocation on
    /// the audio thread. System-only.</para>
    /// </remarks>
    public sealed class PhaseRotator
    {
        /// <summary>The most sections that can run.</summary>
        public const int MaxStages = 6;

        /// <summary>The count broadcast processors usually run, and what the presets that use the rotator choose.</summary>
        public const int BroadcastStages = 4;

        /// <summary>Each section's Q: wide, so the phase turns gently across the voice band.</summary>
        public const double SectionQ = 0.5;

        private static readonly double[] Centres = { 300, 700, 1500, 2500, 1000, 2000 };

        /// <summary>The centre of section <paramref name="index"/> (0 to 5), Hz. The first four are the broadcast set; the last two fill the gaps between them.</summary>
        public static double CentreHz(int index) => Centres[Math.Clamp(index, 0, MaxStages - 1)];

        private readonly BiquadCoefficients[] _co = new BiquadCoefficients[MaxStages];
        private readonly BiquadState[] _st = new BiquadState[MaxStages * 2];
        private uint _rate;

        /// <summary>
        /// One second-order all-pass section: unity gain at every frequency,
        /// phase turning through 180 degrees at <paramref name="frequencyHz"/>.
        /// </summary>
        public static BiquadCoefficients AllPass(double frequencyHz, double q, double sampleRate)
        {
            if (!(sampleRate > 0)) sampleRate = 48000;
            double f = Math.Clamp(frequencyHz, 1.0, 0.49 * sampleRate);
            q = Math.Clamp(q, BiquadCoefficients.MinQ, BiquadCoefficients.MaxQ);
            double w0 = 2.0 * Math.PI * f / sampleRate;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * Math.Cos(w0) / a0;
            double a2 = (1.0 - alpha) / a0;
            // H(z) = (a2 + a1 z^-1 + z^-2) / (1 + a1 z^-1 + a2 z^-2)
            return new BiquadCoefficients(a2, a1, 1.0, a1, a2);
        }

        /// <summary>Start again from silence (a change of stage count, or the stage switched on).</summary>
        public void Clear()
        {
            for (int i = 0; i < _st.Length; i++) _st[i].Clear();
        }

        /// <summary>
        /// One stereo frame through the first <paramref name="stages"/>
        /// sections at <paramref name="sampleRate"/>. The caller checks
        /// <paramref name="stages"/> is above zero.
        /// </summary>
        public void Step(int stages, uint sampleRate, ref double left, ref double right)
        {
            if (sampleRate != _rate) Design(sampleRate);
            for (int s = 0; s < stages; s++)
            {
                left = _st[2 * s].Step(in _co[s], left);
                right = _st[2 * s + 1].Step(in _co[s], right);
            }
        }

        private void Design(uint sampleRate)
        {
            for (int s = 0; s < MaxStages; s++) _co[s] = AllPass(Centres[s], SectionQ, sampleRate);
            _rate = sampleRate;
            Clear();
        }

        /// <summary>Once per buffer: keep a state that decayed into the subnormal range from stalling the processor.</summary>
        public void FlushTiny()
        {
            for (int i = 0; i < _st.Length; i++) _st[i].FlushTiny();
        }

        /// <summary>The cascade's level at a frequency, dB: zero, to rounding, for any stage count. For tests and the record, never the audio thread.</summary>
        public static double MagnitudeDb(int stages, double frequencyHz, double sampleRate)
        {
            double db = 0;
            for (int s = 0; s < Math.Clamp(stages, 0, MaxStages); s++)
                db += AllPass(Centres[s], SectionQ, sampleRate).MagnitudeDb(frequencyHz, sampleRate);
            return db;
        }

        /// <summary>
        /// The cascade's group delay at a frequency, seconds: the slope of its
        /// phase, taken numerically. For the record and tests, never the audio
        /// thread.
        /// </summary>
        public static double GroupDelaySeconds(int stages, double frequencyHz, double sampleRate)
        {
            double step = Math.Max(0.01, frequencyHz * 1e-4);
            double before = Phase(stages, frequencyHz - step, sampleRate);
            double after = Phase(stages, frequencyHz + step, sampleRate);
            return -(after - before) / (2.0 * Math.PI * 2.0 * step);
        }

        /// <summary>The cascade's unwrapped phase at a frequency, radians (each section's own phase runs from 0 down to minus two pi, so the sum never wraps).</summary>
        private static double Phase(int stages, double frequencyHz, double sampleRate)
        {
            double total = 0;
            double w = 2.0 * Math.PI * frequencyHz / sampleRate;
            double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
            for (int s = 0; s < Math.Clamp(stages, 0, MaxStages); s++)
            {
                var co = AllPass(Centres[s], SectionQ, sampleRate);
                double nr = co.B0 + co.B1 * c1 + co.B2 * c2, ni = -(co.B1 * s1 + co.B2 * s2);
                double dr = 1 + co.A1 * c1 + co.A2 * c2, di = -(co.A1 * s1 + co.A2 * s2);
                double phase = Math.Atan2(ni, nr) - Math.Atan2(di, dr);
                // One section's phase lies in (-2 pi, 0]: bring the difference of two arctangents back into it.
                while (phase > 1e-12) phase -= 2.0 * Math.PI;
                while (phase <= -2.0 * Math.PI) phase += 2.0 * Math.PI;
                total += phase;
            }
            return total;
        }
    }
}
