using System;

namespace JJPortaudio
{
    /// <summary>The shape of one second-order filter section (Sprint 49 Track I, #1240).</summary>
    public enum BiquadKind
    {
        /// <summary>A bell: boost or cut around the frequency, width set by Q.</summary>
        Peaking = 0,
        /// <summary>Boost or cut everything below the frequency.</summary>
        LowShelf = 1,
        /// <summary>Boost or cut everything above the frequency.</summary>
        HighShelf = 2,
        /// <summary>Pass above the frequency, roll off below (gain ignored).</summary>
        HighPass = 3,
        /// <summary>Pass below the frequency, roll off above (gain ignored).</summary>
        LowPass = 4,
        /// <summary>A deep, narrow cut at the frequency (gain ignored).</summary>
        Notch = 5,
        /// <summary>A band-pass with 0 dB at its peak (gain ignored). Used by the de-esser's split; not offered as an EQ band.</summary>
        BandPass = 6,
    }

    /// <summary>
    /// The normalised coefficients of one second-order section,
    /// y = b0 x + b1 x1 + b2 x2 - a1 y1 - a2 y2 (a0 divided out).
    /// </summary>
    /// <remarks>
    /// <para><b>Written from the published second-order formulas</b> (the
    /// widely published "audio EQ cookbook" bilinear-transform designs: the
    /// analogue prototype for each shape, pre-warped at the section's
    /// frequency), not copied from any program's source.</para>
    /// <para><b>System-only and allocation-free</b>, so the numerical harness
    /// links it and the audio thread may design a section when it has to.</para>
    /// </remarks>
    public readonly struct BiquadCoefficients
    {
        public readonly double B0, B1, B2, A1, A2;

        public BiquadCoefficients(double b0, double b1, double b2, double a1, double a2)
        {
            B0 = b0; B1 = b1; B2 = b2; A1 = a1; A2 = a2;
        }

        /// <summary>A section that passes its input unchanged.</summary>
        public static BiquadCoefficients Identity => new BiquadCoefficients(1, 0, 0, 0, 0);

        /// <summary>The lowest Q a section accepts (very wide).</summary>
        public const double MinQ = 0.1;

        /// <summary>The highest Q a section accepts (very narrow).</summary>
        public const double MaxQ = 30;

        /// <summary>
        /// Design one section. The frequency is clamped inside (1 Hz, 0.49 of
        /// the sample rate) and Q to <see cref="MinQ"/>..<see cref="MaxQ"/>, so
        /// no setting can produce an unstable section.
        /// </summary>
        public static BiquadCoefficients Design(BiquadKind kind, double frequencyHz, double gainDb, double q, double sampleRate)
        {
            if (!(sampleRate > 0)) sampleRate = 48000;
            if (!double.IsFinite(frequencyHz)) frequencyHz = 1000;
            if (!double.IsFinite(gainDb)) gainDb = 0;
            if (!double.IsFinite(q)) q = 0.7071;
            double f = Math.Clamp(frequencyHz, 1.0, 0.49 * sampleRate);
            q = Math.Clamp(q, MinQ, MaxQ);
            double w0 = 2.0 * Math.PI * f / sampleRate;
            double cos = Math.Cos(w0);
            double sin = Math.Sin(w0);
            double alpha = sin / (2.0 * q);
            double a = Math.Pow(10.0, gainDb / 40.0); // amplitude, square root of the linear gain

            double b0, b1, b2, a0, a1, a2;
            switch (kind)
            {
                case BiquadKind.LowShelf:
                {
                    double sq = 2.0 * Math.Sqrt(a) * alpha;
                    b0 = a * ((a + 1) - (a - 1) * cos + sq);
                    b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                    b2 = a * ((a + 1) - (a - 1) * cos - sq);
                    a0 = (a + 1) + (a - 1) * cos + sq;
                    a1 = -2 * ((a - 1) + (a + 1) * cos);
                    a2 = (a + 1) + (a - 1) * cos - sq;
                    break;
                }
                case BiquadKind.HighShelf:
                {
                    double sq = 2.0 * Math.Sqrt(a) * alpha;
                    b0 = a * ((a + 1) + (a - 1) * cos + sq);
                    b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                    b2 = a * ((a + 1) + (a - 1) * cos - sq);
                    a0 = (a + 1) - (a - 1) * cos + sq;
                    a1 = 2 * ((a - 1) - (a + 1) * cos);
                    a2 = (a + 1) - (a - 1) * cos - sq;
                    break;
                }
                case BiquadKind.HighPass:
                    b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2;
                    a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                    break;
                case BiquadKind.LowPass:
                    b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2;
                    a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                    break;
                case BiquadKind.Notch:
                    b0 = 1; b1 = -2 * cos; b2 = 1;
                    a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                    break;
                case BiquadKind.BandPass:
                    b0 = alpha; b1 = 0; b2 = -alpha;
                    a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                    break;
                default: // Peaking
                    b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
                    a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
                    break;
            }
            return new BiquadCoefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        /// <summary>The section's magnitude response at a frequency, in dB (for tests and readouts, never the audio thread).</summary>
        public double MagnitudeDb(double frequencyHz, double sampleRate)
        {
            double w = 2.0 * Math.PI * frequencyHz / sampleRate;
            // H(e^jw) = (b0 + b1 e^-jw + b2 e^-2jw) / (1 + a1 e^-jw + a2 e^-2jw)
            double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
            double nr = B0 + B1 * c1 + B2 * c2, ni = -(B1 * s1 + B2 * s2);
            double dr = 1 + A1 * c1 + A2 * c2, di = -(A1 * s1 + A2 * s2);
            double num = nr * nr + ni * ni, den = dr * dr + di * di;
            return 10.0 * Math.Log10(Math.Max(num, 1e-300) / Math.Max(den, 1e-300));
        }
    }

    /// <summary>
    /// The running state of one second-order section on one channel:
    /// transposed direct form II, double precision. A struct so a bank of them
    /// is one preallocated array with no per-sample allocation.
    /// </summary>
    public struct BiquadState
    {
        public double S1, S2;

        /// <summary>Run one sample through the section.</summary>
        public double Step(in BiquadCoefficients c, double x)
        {
            double y = c.B0 * x + S1;
            S1 = c.B1 * x - c.A1 * y + S2;
            S2 = c.B2 * x - c.A2 * y;
            return y;
        }

        /// <summary>
        /// Flush a state that has decayed into the subnormal range to zero, so a
        /// section left running on silence cannot stall the processor on
        /// denormal arithmetic. Called once per buffer, not per sample.
        /// </summary>
        public void FlushTiny()
        {
            if (Math.Abs(S1) < 1e-25) S1 = 0;
            if (Math.Abs(S2) < 1e-25) S2 = 0;
        }

        public void Clear() { S1 = 0; S2 = 0; }
    }
}

namespace JJPortaudio
{
    /// <summary>
    /// The expander's side-chain filter (Sprint 49 Track I, ruled 2026-11-23
    /// 11:35): a fourth-order Butterworth high-pass at the low corner and a
    /// fourth-order Butterworth low-pass at the high corner (24 dB an octave
    /// each side), per channel. ONE class for the expander's detector and the
    /// room meter that must hear exactly what the detector hears. It filters
    /// what a detector HEARS, never audio that is sent. Preallocated; Design
    /// and Step allocate nothing.
    /// </summary>
    public sealed class SideChainFilter
    {
        // Butterworth fourth order as two second-order sections.
        private const double Q1 = 0.5411961, Q2 = 1.3065630;
        private readonly BiquadCoefficients[] _co = new BiquadCoefficients[4];
        private readonly BiquadState[] _st = new BiquadState[8];
        private float _low = float.NaN, _high = float.NaN;
        private uint _rate;

        /// <summary>Design for these corners and rate when any changed; true when it did (the state is cleared then).</summary>
        public bool Design(float lowHz, float highHz, uint sampleRate)
        {
            if (lowHz == _low && highHz == _high && sampleRate == _rate) return false;
            double high = Math.Max(highHz, lowHz * 1.5f);
            _co[0] = BiquadCoefficients.Design(BiquadKind.HighPass, lowHz, 0, Q1, sampleRate);
            _co[1] = BiquadCoefficients.Design(BiquadKind.HighPass, lowHz, 0, Q2, sampleRate);
            _co[2] = BiquadCoefficients.Design(BiquadKind.LowPass, high, 0, Q1, sampleRate);
            _co[3] = BiquadCoefficients.Design(BiquadKind.LowPass, high, 0, Q2, sampleRate);
            _low = lowHz; _high = highHz; _rate = sampleRate;
            Clear();
            return true;
        }

        /// <summary>One sample of channel 0 or 1 through all four sections.</summary>
        public double Step(int channel, double x)
        {
            int s = channel * 4;
            x = _st[s].Step(in _co[0], x);
            x = _st[s + 1].Step(in _co[1], x);
            x = _st[s + 2].Step(in _co[2], x);
            return _st[s + 3].Step(in _co[3], x);
        }

        public void Clear() { for (int i = 0; i < _st.Length; i++) _st[i].Clear(); }

        public void FlushTiny() { for (int i = 0; i < _st.Length; i++) _st[i].FlushTiny(); }
    }
}
