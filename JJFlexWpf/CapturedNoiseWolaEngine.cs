using System;
using System.Collections.Generic;
using System.Threading;

namespace JJFlexWpf
{
    /// <summary>
    /// The captured-noise subtraction, rewritten: the same arithmetic as
    /// <see cref="SpectralCaptureEngine"/> (magnitude minus the captured
    /// profile times the subtraction amount, floored, phase kept) on a
    /// 960-point frame with a 480-sample hop and the square root of the
    /// periodic Hann window on both analysis and synthesis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Built BESIDE the old engine, not instead of it (Sprint 49 Track C,
    /// ruled by Noel 2026-11-02 13:03).</b> The catalog's
    /// <c>spectral_capture</c> row still builds the old engine; this one is
    /// reachable from code and from tools/nrbench, so the two can be rendered
    /// side by side. The swap is a separate decision.
    /// </para>
    /// <para>
    /// <b>Why the window changed.</b> The old engine applies a symmetric Hann
    /// window on the way in and again on the way out, at a 50 percent hop. The
    /// two windows multiply to Hann squared, and Hann squared at a 50 percent
    /// hop does not add up to a constant: the output swings by about 6 dB
    /// every 512 samples, about 94 Hz, on everything it passes.
    /// <c>CapturedNoiseWolaTests</c> measures that. The square root of the
    /// PERIODIC Hann, applied twice, multiplies to the periodic Hann itself,
    /// and the periodic Hann at a 50 percent hop sums to exactly one, so this
    /// engine reconstructs its input exactly when it subtracts nothing.
    /// (The square root of the symmetric Hann would not quite: its sum
    /// ripples very slightly. That is why periodic.)
    /// </para>
    /// <para>
    /// <b>Its delay is 480 samples, always</b>, with a profile and without, so
    /// switching the profile on or off never moves the timeline. With no
    /// profile the input comes out unchanged, 480 samples late.
    /// </para>
    /// <para>
    /// <b>Profiles arrive in the capture's shape</b> (a 1,024-point spectrum,
    /// see <see cref="ConvertProfile"/>), whether from a live capture or a
    /// saved file, and are converted here, once, when a new one arrives. The
    /// conversion allocates; nothing per frame does.
    /// </para>
    /// </remarks>
    public sealed class CapturedNoiseWolaEngine : IDenoiseEngine
    {
        /// <summary>Analysis frame, in samples. A true 960-point transform, not a padded 1,024.</summary>
        public const int FrameSize = 960;

        /// <summary>Hop between frames, half the frame.</summary>
        public const int Hop = FrameSize / 2;

        /// <summary>Bins kept from a real frame: 0 to <see cref="Hop"/> inclusive.</summary>
        public const int Bins = FrameSize / 2 + 1;

        /// <summary>The frame size every capture to date has used, assumed when a saved file says nothing.</summary>
        public const int CaptureFftSize = 1024;

        /// <summary>
        /// The same descriptors as <see cref="SpectralCaptureEngine"/>, names
        /// and steps included, so a stored setting carries straight over.
        /// </summary>
        public static IReadOnlyList<EngineSettingDescriptor> SettingDescriptors { get; } = new[] {
            new EngineSettingDescriptor("subtraction_amount", "audio.denoise.subtraction_amount", EngineSettingKind.Number,
                new EngineSettingValue(0.7), 0, 1, 0.01),
            new EngineSettingDescriptor("spectral_floor", "audio.denoise.spectral_floor", EngineSettingKind.Number,
                new EngineSettingValue(0.02), 0, 0.2, 0.001)
        };

        /// <summary>sqrt(periodic Hann): sin(pi n / N).</summary>
        private static readonly double[] Window = MakeWindow();

        private static readonly Fft960 Transform = new();

        // Per instance, all allocated once.
        private readonly float[] _previous = new float[Hop];   // the last input block
        private readonly double[] _overlap = new double[Hop];  // second half of the last synthesised frame
        private readonly double[] _re = new double[FrameSize];
        private readonly double[] _im = new double[FrameSize];
        private readonly double[] _scratchRe = new double[FrameSize];
        private readonly double[] _scratchIm = new double[FrameSize];

        private float _subtraction = 0.7f, _floor = 0.02f;
        private double[]? _sourceProfile; // the capture-shaped array last handed in, by reference
        private double[]? _profile;       // its conversion, Bins long

        public string Id => "spectral_capture";
        public int SampleRate => 48000;
        public int FrameLength => Hop;
        public int LatencySamples => Hop;
        public bool IsAvailable => true;

        public IReadOnlyList<EngineSettingDescriptor> Settings => SettingDescriptors;

        public EngineSettingValue GetSetting(string name) => name switch {
            "subtraction_amount" => new EngineSettingValue(Volatile.Read(ref _subtraction)),
            "spectral_floor" => new EngineSettingValue(Volatile.Read(ref _floor)),
            _ => throw new ArgumentException("Unknown setting", nameof(name))
        };

        public bool TrySetSetting(string name, EngineSettingValue value)
        {
            EngineSettingDescriptor? descriptor = null;
            for (int i = 0; i < SettingDescriptors.Count; i++)
                if (SettingDescriptors[i].Name == name) { descriptor = SettingDescriptors[i]; break; }
            if (descriptor == null || !descriptor.Accepts(value)) return false;
            if (name == "subtraction_amount") Volatile.Write(ref _subtraction, (float)value.Number);
            else Volatile.Write(ref _floor, (float)value.Number);
            return true;
        }

        /// <summary>True when a profile is loaded and subtraction runs.</summary>
        public bool HasProfile => Volatile.Read(ref _profile) != null;

        /// <summary>
        /// Hand over the capture's profile, in its own shape (see
        /// <see cref="ConvertProfile"/>), or null to pass audio through
        /// (480 samples late). The same array again does nothing at all: the
        /// pipeline offers the profile on every buffer, on the audio thread.
        /// A new array is converted here, which allocates once.
        /// </summary>
        internal void SetProfile(double[]? profile) => SetProfile(profile, 0);

        /// <param name="profile">Capture-shaped magnitudes, or null.</param>
        /// <param name="captureFftSize">The frame it was captured at; zero or less means <see cref="CaptureFftSize"/>.</param>
        internal void SetProfile(double[]? profile, int captureFftSize)
        {
            if (ReferenceEquals(profile, Volatile.Read(ref _sourceProfile))) return;
            double[]? converted = profile == null ? null : ConvertProfile(profile, captureFftSize);
            Volatile.Write(ref _profile, converted);
            Volatile.Write(ref _sourceProfile, profile);
        }

        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            if (input.Length != Hop || output.Length != Hop) throw new ArgumentException($"Expected {Hop} samples");
            var profile = Volatile.Read(ref _profile);
            float amount = Volatile.Read(ref _subtraction), floor = Volatile.Read(ref _floor);

            if (profile == null)
            {
                // Straight through, one hop late, without running the
                // transform: an unmodified frame's windows multiply to the
                // periodic Hann, so the overlap-add can be written directly.
                // The overlap is kept exactly as the transform would have left
                // it, so a profile arriving, or going, joins seamlessly.
                for (int n = 0; n < Hop; n++)
                {
                    output[n] = (float)(_overlap[n] + Window[n] * Window[n] * _previous[n]);
                    _overlap[n] = Window[Hop + n] * Window[Hop + n] * input[n];
                }
                input.CopyTo(_previous);
                return true;
            }

            for (int n = 0; n < Hop; n++)
            {
                _re[n] = _previous[n] * Window[n];
                _re[Hop + n] = input[n] * Window[Hop + n];
            }
            Array.Clear(_im);
            Transform.Forward(_re, _im, _scratchRe, _scratchIm);

            for (int k = 0; k < Bins; k++)
            {
                double mag = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
                if (mag <= 0) continue;
                double clean = mag - profile[k] * amount;
                double least = mag * floor;
                if (clean < least) clean = least;
                double gain = clean / mag;
                _re[k] *= gain; _im[k] *= gain;
                if (k > 0 && k < Hop) { _re[FrameSize - k] *= gain; _im[FrameSize - k] *= gain; }
            }

            Transform.Inverse(_re, _im, _scratchRe, _scratchIm);

            for (int n = 0; n < Hop; n++)
            {
                output[n] = (float)(_overlap[n] + _re[n] * Window[n]);
                _overlap[n] = _re[Hop + n] * Window[Hop + n];
            }
            input.CopyTo(_previous);
            return true;
        }

        public void Reset()
        {
            Array.Clear(_previous);
            Array.Clear(_overlap);
        }

        public void Dispose() { }

        /// <summary>
        /// Convert a profile from the capture's shape to this engine's 481
        /// bins, by frequency and with a window correction.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The shape it accepts.</b> The capture
        /// (<see cref="SpectralSubtractionProvider"/>) stores the magnitude of
        /// every element <c>FftSharp.FFT.Forward</c> returns for a 1,024-point
        /// frame: the FULL, symmetric spectrum, 1,024 values (measured in
        /// <c>CapturedNoiseWolaTests</c>). A half spectrum of
        /// <c>fftSize / 2 + 1</c> values is accepted too.
        /// </para>
        /// <para>
        /// <b>By frequency:</b> capture bin k sits at k x 48000 / fftSize Hz,
        /// this engine's bin j at j x 50 Hz; the magnitudes are interpolated
        /// linearly onto the new bins.
        /// </para>
        /// <para>
        /// <b>The window correction.</b> For steady noise, a bin's mean
        /// magnitude through a window w is proportional to the square root of
        /// the window's sum of squares (the bin's expected power is the noise
        /// density times that sum, and the mean of a Rayleigh-distributed
        /// magnitude is a fixed multiple of the root of its mean power). The
        /// capture's window is FftSharp's symmetric Hann; this engine's is the
        /// square root of the periodic Hann, whose sum of squares is exactly
        /// 480. Both sums are computed here from the windows themselves, never
        /// typed in; the factor comes to about 1.118 for a 1,024-point capture.
        /// </para>
        /// </remarks>
        public static double[] ConvertProfile(double[] capture, int captureFftSize)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            int fft = captureFftSize > 0 ? captureFftSize : CaptureFftSize;
            int half = fft / 2;
            // A full spectrum has fft values; a half spectrum half + 1. Anything
            // else names its own size by its length.
            if (capture.Length != fft && capture.Length != half + 1)
            {
                fft = capture.Length % 2 == 0 ? capture.Length : 2 * (capture.Length - 1);
                half = fft / 2;
            }
            double correction = WindowCorrection(fft);
            var result = new double[Bins];
            for (int j = 0; j < Bins; j++)
            {
                double pos = (double)j * fft / FrameSize; // capture bin at this frequency
                int lo = Math.Min((int)Math.Floor(pos), half);
                int hi = Math.Min(lo + 1, half);
                double frac = pos - lo;
                if (lo == half) frac = 0;
                result[j] = (capture[lo] * (1 - frac) + capture[hi] * frac) * correction;
            }
            return result;
        }

        /// <summary>sqrt(sum of squares of this engine's window / that of the capture's window at <paramref name="captureFftSize"/>).</summary>
        public static double WindowCorrection(int captureFftSize)
        {
            double ours = 0;
            foreach (double w in Window) ours += w * w;
            double theirs = 0;
            foreach (double w in new FftSharp.Windows.Hanning().Create(captureFftSize)) theirs += w * w;
            return Math.Sqrt(ours / theirs);
        }

        /// <summary>
        /// The mean magnitude per bin that this engine's own window and
        /// transform measure over a signal, frame by frame at its hop: what a
        /// profile captured natively at 960 points would hold. For tests and the bench.
        /// </summary>
        public static double[] MeasureMeanMagnitudes(ReadOnlySpan<float> signal)
        {
            var re = new double[FrameSize]; var im = new double[FrameSize];
            var sr = new double[FrameSize]; var si = new double[FrameSize];
            var sum = new double[Bins];
            int frames = 0;
            for (int start = 0; start + FrameSize <= signal.Length; start += Hop, frames++)
            {
                for (int n = 0; n < FrameSize; n++) { re[n] = signal[start + n] * Window[n]; im[n] = 0; }
                Transform.Forward(re, im, sr, si);
                for (int k = 0; k < Bins; k++) sum[k] += Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            }
            for (int k = 0; k < Bins; k++) sum[k] /= Math.Max(frames, 1);
            return sum;
        }

        /// <summary>Sum over n of window(n) squared plus window(n + hop) squared, for each n: one for an exact overlap-add.</summary>
        internal static double OverlapSum(int n) => Window[n] * Window[n] + Window[Hop + n] * Window[Hop + n];

        private static double[] MakeWindow()
        {
            var w = new double[FrameSize];
            for (int n = 0; n < FrameSize; n++) w[n] = Math.Sin(Math.PI * n / FrameSize);
            return w;
        }

        /// <summary>
        /// An exact 960-point complex FFT, planned once: 960 = 4 x 4 x 4 x 3 x 5,
        /// one Stockham (self-sorting) stage per factor, ping-ponging between the
        /// caller's arrays and two scratch arrays, so nothing is allocated per
        /// call and the result comes out in natural order. Forward is
        /// unnormalised, like <c>FftSharp.FFT.Forward</c>, so magnitudes are in
        /// the same units as the capture's; inverse divides by 960.
        /// </summary>
        internal sealed class Fft960
        {
            private static readonly int[] Factors = { 4, 4, 4, 3, 5 };
            private readonly double[] _cos = new double[FrameSize];
            private readonly double[] _sin = new double[FrameSize];

            public Fft960()
            {
                for (int k = 0; k < FrameSize; k++)
                {
                    double a = -2 * Math.PI * k / FrameSize;
                    _cos[k] = Math.Cos(a); _sin[k] = Math.Sin(a);
                }
            }

            /// <summary>In place (result back in re/im). Thread-safe: the plan is read-only.</summary>
            public void Forward(double[] re, double[] im, double[] scratchRe, double[] scratchIm)
                => Run(re, im, scratchRe, scratchIm, +1);

            /// <summary>In place, divided by 960.</summary>
            public void Inverse(double[] re, double[] im, double[] scratchRe, double[] scratchIm)
            {
                Run(re, im, scratchRe, scratchIm, -1);
                const double scale = 1.0 / FrameSize;
                for (int i = 0; i < FrameSize; i++) { re[i] *= scale; im[i] *= scale; }
            }

            // sign +1: exp(-2 pi i ...); sign -1: exp(+2 pi i ...).
            private void Run(double[] re, double[] im, double[] scratchRe, double[] scratchIm, int sign)
            {
                double[] xr = re, xi = im, yr = scratchRe, yi = scratchIm;
                int n = FrameSize, s = 1;
                Span<double> ar = stackalloc double[5];
                Span<double> ai = stackalloc double[5];
                foreach (int r in Factors)
                {
                    int m = n / r;
                    int nStep = FrameSize / n; // W_n^x = W_N^(x * N/n)
                    int rStep = FrameSize / r; // W_r^x = W_N^(x * N/r)
                    for (int p = 0; p < m; p++)
                    {
                        for (int q = 0; q < s; q++)
                        {
                            for (int k = 0; k < r; k++)
                            {
                                int idx = q + s * (p + k * m);
                                ar[k] = xr[idx]; ai[k] = xi[idx];
                            }
                            for (int t = 0; t < r; t++)
                            {
                                double sr = 0, si = 0;
                                for (int k = 0; k < r; k++)
                                {
                                    int w = (k * t * rStep) % FrameSize;
                                    double c = _cos[w], sn = sign * _sin[w];
                                    sr += ar[k] * c - ai[k] * sn;
                                    si += ar[k] * sn + ai[k] * c;
                                }
                                int tw = (p * t * nStep) % FrameSize;
                                double tc = _cos[tw], ts = sign * _sin[tw];
                                int o = q + s * (r * p + t);
                                yr[o] = sr * tc - si * ts;
                                yi[o] = sr * ts + si * tc;
                            }
                        }
                    }
                    (xr, yr) = (yr, xr);
                    (xi, yi) = (yi, xi);
                    n = m; s *= r;
                }
                // Five stages: an odd count leaves the result in the scratch arrays.
                if (!ReferenceEquals(xr, re))
                {
                    Array.Copy(xr, re, FrameSize);
                    Array.Copy(xi, im, FrameSize);
                }
            }
        }
    }
}
