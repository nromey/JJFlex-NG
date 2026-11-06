using System.Numerics;

namespace NrBench;
// Sprint 49 bench CW: a spectral measure for CW. The earlier measure (key-down energy
// against gap energy, broadband percentiles) disagreed with the operator's ear on the
// one-signal takes of 2026-11-04, because a broadband level cannot tell a quiet gap
// from a gap full of hiss at the station's own pitch. This one follows the station's
// tone:
//   1. The pitch: the bin whose level swings most between its loud and quiet moments
//      across the WHOLE span (keying), not the loudest bin (a carrier or a birdie).
//   2. When the station is keyed: from the reference (the unprocessed capture) through
//      a narrow filter at the pitch, so one key-down/gap map serves every file.
//   3. Per 10 ms block of each file: the energy within 50 Hz of the pitch, and the
//      noise in the rest of the receiver's passband (more than 100 Hz from the pitch),
//      scaled to the same 100 Hz bandwidth.
// It reports how far key-down stands above the gaps at the pitch, and how far the gaps
// fell relative to the station, both at the pitch and across the rest of the passband.
// It is a measure, not a verdict: it cannot hear artefacts or a watery character.
partial class BenchApp
{
    static bool PrintSpectrum;
    const int CwHop = 480, CwWindow = 1920, CwFft = 4096;
    const double ToneHalfWidth = 50, RestGuard = 100;

    sealed record CwPitch(double Hz, double ScoreDb, double RunnerUpHz, double RunnerUpScoreDb);

    /// <summary>The keyed station's pitch, from activity over the whole span.</summary>
    static CwPitch FindPitch(float[] x, double lowHz = 200, double highHz = 3000)
    {
        const int N = 8192, hop = 2048;
        double bin = (double)Rate / N;
        int lo = (int)Math.Ceiling(lowHz / bin), hi = (int)Math.Floor(highHz / bin);
        var window = Hann(N);
        int frames = Math.Max(0, (x.Length - N) / hop + 1);
        if (frames < 20) throw new InvalidOperationException("Too little audio to find a pitch");
        var levels = new float[hi - lo + 1][];
        for (int k = 0; k <= hi - lo; k++) levels[k] = new float[frames];
        var buf = new Complex[N];
        var power = new double[N / 2 + 1];
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < N; i++) buf[i] = x[f * hop + i] * window[i];
            Fft(buf, false);
            for (int k = 0; k <= N / 2; k++) power[k] = buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary;
            // Five bins (about 29 Hz) together: a keyed note's own spread, and a steadier noise estimate.
            for (int k = lo; k <= hi; k++)
            {
                double s = 0;
                for (int j = -2; j <= 2; j++) s += power[k + j];
                levels[k - lo][f] = (float)(10 * Math.Log10(s + 1e-30));
            }
        }
        var score = new double[hi - lo + 1];
        for (int k = 0; k < score.Length; k++)
        {
            var sorted = levels[k].Order().ToArray();
            double p90 = sorted[(int)(sorted.Length * .9)], p30 = sorted[(int)(sorted.Length * .3)];
            // cw-pitch: the control that the activity rule is load-bearing; the loudest bin instead.
            score[k] = Mutation == "cw-pitch" ? sorted.Average(v => (double)v) : p90 - p30;
        }
        int best = Array.IndexOf(score, score.Max());
        // The activity score picks the station; its exact pitch comes from the note itself: the
        // power centroid, within three bins (about 18 Hz), of the frames where that band is
        // loudest (its top tenth), when the station is keyed.
        var at = levels[best];
        float loud = at.Order().ElementAt((int)(at.Length * .9));
        var sum = new double[7];
        for (int f = 0; f < frames; f++)
        {
            if (at[f] < loud) continue;
            for (int i = 0; i < N; i++) buf[i] = x[f * hop + i] * window[i];
            Fft(buf, false);
            for (int j = -3; j <= 3; j++) { var v = buf[lo + best + j]; sum[j + 3] += v.Real * v.Real + v.Imaginary * v.Imaginary; }
        }
        double frac = Enumerable.Range(0, 7).Sum(j => (j - 3) * sum[j]) / Math.Max(1e-30, sum.Sum());
        // The runner-up more than 60 Hz away, so the report shows how clear the choice was.
        int second = -1;
        for (int k = 0; k < score.Length; k++)
            if (Math.Abs(k - best) * bin > 60 && (second < 0 || score[k] > score[second])) second = k;
        return new((lo + best + frac) * bin, score[best], second < 0 ? double.NaN : (lo + second) * bin, second < 0 ? double.NaN : score[second]);
    }

    /// <summary>The receiver's passband around the pitch: where the long-term noise floor (20th percentile) stays within 8 dB of its plateau.</summary>
    static (double Lo, double Hi) FindPassband(float[] x, double pitch)
    {
        const int N = 4096, hop = 4096;
        double bin = (double)Rate / N;
        var window = Hann(N);
        int frames = Math.Max(1, (x.Length - N) / hop + 1);
        int maxBin = (int)(4000 / bin);
        var levels = new float[maxBin + 1][];
        for (int k = 0; k <= maxBin; k++) levels[k] = new float[frames];
        var buf = new Complex[N];
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < N; i++) buf[i] = f * hop + i < x.Length ? x[f * hop + i] * window[i] : 0;
            Fft(buf, false);
            for (int k = 0; k <= maxBin; k++) levels[k][f] = (float)(10 * Math.Log10(buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary + 1e-30));
        }
        // The 20th percentile over time is the noise floor the receiver lets through, with the
        // stations mostly out of it (a median still holds a busy pileup); smoothed over about 50 Hz.
        var median = levels.Select(l => (double)l.Order().ElementAt(l.Length / 5)).ToArray();
        var smooth = new double[median.Length];
        for (int k = 0; k < median.Length; k++)
        {
            int a = Math.Max(0, k - 2), b = Math.Min(median.Length - 1, k + 2);
            smooth[k] = Enumerable.Range(a, b - a + 1).Average(j => median[j]);
        }
        int lowest = (int)(100 / bin);
        double top = smooth.Skip(lowest).Max();
        if (PrintSpectrum)
            Console.WriteLine("Long-term floor (20th percentile) by 100 Hz band, dB below the peak, 100 Hz upward: " + string.Join(", ",
                Enumerable.Range(1, 38).Select(band => (top - Enumerable.Range((int)(band * 100 / bin), (int)(100 / bin)).Where(k => k <= maxBin).Average(k => smooth[k])).ToString("0"))));
        // The edges are where the floor falls 8 dB below its own plateau: the median floor over the
        // bins within 30 dB of the peak. A plateau, not the peak, because a busy pileup or the
        // receiver's own shaping can raise a hump of 15 dB around the pitch inside a flat passband.
        var near = Enumerable.Range(lowest, maxBin - lowest + 1).Where(k => smooth[k] >= top - 30).Select(k => smooth[k]).Order().ToArray();
        double edge = near[near.Length / 2] - 8;
        int c = Math.Clamp((int)Math.Round(pitch / bin), lowest, maxBin);
        int l = c, h = c;
        while (l > lowest && smooth[l - 1] >= edge) l--;
        while (h < maxBin && smooth[h + 1] >= edge) h++;
        return (l * bin, h * bin);
    }

    static double[] Hann(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / n);
        return w;
    }

    /// <summary>Per 10 ms block: energy within 50 Hz of the pitch, and the rest of the passband scaled to 100 Hz (NaN when the passband leaves no room).</summary>
    sealed record CwBlocks(double[] Tone, double[] Rest);

    static CwBlocks Blocks(float[] x, double pitch, double passLo, double passHi)
    {
        double bin = (double)Rate / CwFft;
        var window = Hann(CwWindow);
        int n = Math.Max(0, (x.Length - CwWindow) / CwHop + 1);
        var tone = new double[n];
        var rest = new double[n];
        var toneBins = new List<int>();
        var restBins = new List<int>();
        for (int k = (int)Math.Ceiling(passLo / bin); k <= (int)Math.Floor(passHi / bin); k++)
        {
            double d = Math.Abs(k * bin - pitch);
            if (d <= ToneHalfWidth) toneBins.Add(k);
            else if (d > RestGuard) restBins.Add(k);
        }
        // Bins outside the passband but near the pitch still count as tone (a passband narrower than 100 Hz).
        for (int k = (int)Math.Ceiling((pitch - ToneHalfWidth) / bin); k <= (int)Math.Floor((pitch + ToneHalfWidth) / bin); k++)
            if (!toneBins.Contains(k)) toneBins.Add(k);
        double restScale = restBins.Count >= 4 ? (2 * ToneHalfWidth / bin) / restBins.Count : double.NaN;
        var buf = new Complex[CwFft];
        for (int b = 0; b < n; b++)
        {
            Array.Clear(buf);
            for (int i = 0; i < CwWindow; i++) buf[i] = x[b * CwHop + i] * window[i];
            Fft(buf, false);
            double t = 0, r = 0;
            foreach (int k in toneBins) t += buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary;
            foreach (int k in restBins) r += buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary;
            tone[b] = 10 * Math.Log10(t + 1e-30);
            rest[b] = double.IsNaN(restScale) ? double.NaN : 10 * Math.Log10(r * restScale + 1e-30);
        }
        return new(tone, rest);
    }

    /// <summary>Key-down (true), gap (false) or neither (null: within two blocks of an edge), from the reference's tone through the narrow filter.</summary>
    sealed record CwMask(bool?[] Key, double ThresholdDb, double[] ReferenceTone);

    static CwMask MakeMask(float[] reference, double pitch)
    {
        var narrow = NarrowBand(reference, pitch, 250);
        var tone = Blocks(narrow, pitch, pitch - 125, pitch + 125).Tone;
        var smooth = new double[tone.Length];
        for (int i = 0; i < tone.Length; i++)
        {
            var w = new[] { tone[Math.Max(0, i - 1)], tone[i], tone[Math.Min(tone.Length - 1, i + 1)] };
            Array.Sort(w); smooth[i] = w[1];
        }
        double threshold = Otsu(smooth);
        var raw = smooth.Select(v => v > threshold).ToArray();
        var key = new bool?[raw.Length];
        // The 40 ms analysis window spans four blocks, so drop two blocks either side of every edge.
        for (int i = 0; i < raw.Length; i++)
        {
            bool same = true;
            for (int j = Math.Max(0, i - 2); j <= Math.Min(raw.Length - 1, i + 2); j++) same &= raw[j] == raw[i];
            key[i] = same ? raw[i] : null;
        }
        return new(key, threshold, smooth);
    }

    static double Otsu(double[] v)
    {
        double lo = v.Min(), hi = v.Max();
        if (hi - lo < 1e-9) return lo;
        const int bins = 200;
        var hist = new double[bins];
        foreach (double x in v) hist[Math.Min(bins - 1, (int)((x - lo) / (hi - lo) * bins))]++;
        double total = v.Length, sumAll = 0;
        for (int i = 0; i < bins; i++) sumAll += i * hist[i];
        double wB = 0, sumB = 0, best = -1; int at = 0;
        for (int i = 0; i < bins; i++)
        {
            wB += hist[i]; if (wB == 0) continue;
            double wF = total - wB; if (wF == 0) break;
            sumB += i * hist[i];
            double mB = sumB / wB, mF = (sumAll - sumB) / wF, between = wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; at = i; }
        }
        return lo + (at + 1) * (hi - lo) / bins;
    }

    /// <summary>How many blocks the file lags the reference, from the tone envelopes, over -0.2 to +0.6 s.</summary>
    static int BlockLag(double[] reference, double[] file)
    {
        if (Mutation == "cw-align") return 0;
        double mr = reference.Average(), mf = file.Average();
        int best = 0; double bestV = double.MinValue;
        for (int lag = -20; lag <= 60; lag++)
        {
            double s = 0; int c = 0;
            for (int i = 0; i < reference.Length; i++)
            {
                int j = i + lag;
                if (j < 0 || j >= file.Length) continue;
                s += (reference[i] - mr) * (file[j] - mf); c++;
            }
            if (c > 0 && s / c > bestV) { bestV = s / c; best = lag; }
        }
        return best;
    }

    sealed record CwFigures(double KeyDown, double Gap, double WeakKeyDown, double RestKey, double RestGap, int Lag, int KeyBlocks, int GapBlocks)
    {
        public double Separation => KeyDown - Gap;
        public double WeakSeparation => WeakKeyDown - Gap;
        public double RestBelowStation => KeyDown - RestGap;
    }

    static CwFigures Measure(float[] file, CwMask mask, double pitch, double passLo, double passHi)
    {
        var blocks = Blocks(file, pitch, passLo, passHi);
        int lag = BlockLag(mask.ReferenceTone, Blocks(NarrowBand(file, pitch, 250), pitch, pitch - 125, pitch + 125).Tone);
        var key = new List<double>(); var gap = new List<double>(); var restKey = new List<double>(); var restGap = new List<double>();
        var keyRef = new List<(double Ref, double File)>();
        for (int i = 0; i < mask.Key.Length; i++)
        {
            int j = i + lag;
            if (j < 0 || j >= blocks.Tone.Length || mask.Key[i] == null) continue;
            // cw-separation: the control that the key-down/gap split is load-bearing; every block in both.
            if (Mutation == "cw-separation") { key.Add(blocks.Tone[j]); gap.Add(blocks.Tone[j]); continue; }
            if (mask.Key[i] == true) { key.Add(blocks.Tone[j]); restKey.Add(blocks.Rest[j]); keyRef.Add((mask.ReferenceTone[i], blocks.Tone[j])); }
            else { gap.Add(blocks.Tone[j]); restGap.Add(blocks.Rest[j]); }
        }
        if (key.Count < 20 || gap.Count < 20) throw new InvalidOperationException($"Too few key-down ({key.Count}) or gap ({gap.Count}) blocks to measure");
        // The weakest third of the station's elements, by the reference's own level: fades and QSB.
        var weak = keyRef.OrderBy(k => k.Ref).Take(Math.Max(1, keyRef.Count / 3)).Select(k => k.File).ToList();
        return new(Median(key), Median(gap), weak.Count > 0 ? Median(weak) : double.NaN,
            Median(restKey), Median(restGap), lag, key.Count, gap.Count);
    }

    static double Median(List<double> v)
    {
        var f = v.Where(x => !double.IsNaN(x)).Order().ToArray();
        return f.Length == 0 ? double.NaN : f[f.Length / 2];
    }

    static string CwLine(string name, CwFigures f, CwFigures reference)
    {
        string rest = double.IsNaN(f.RestGap) ? "the passband leaves no room beside the tone"
            : $"the rest of the passband between elements {f.RestBelowStation:0.0} dB below the station ({(f.RestBelowStation - reference.RestBelowStation):+0.0;-0.0;0.0} dB against the capture)";
        return $"{name}: key-down {f.Separation:0.0} dB above the gaps at the pitch; the gaps at the pitch fell " +
               $"{(f.Separation - reference.Separation):+0.0;-0.0;0.0} dB against the capture, relative to the station; " +
               $"weakest third of elements {f.WeakSeparation:0.0} dB above the gaps; {rest}; " +
               $"station level {(f.KeyDown - reference.KeyDown):+0.0;-0.0;0.0} dB against the capture" +
               (f.Lag != 0 ? $"; aligned {f.Lag * 10} ms late" : "") + ".";
    }

    // ------------------------------------------------------------------
    // The narrow filter after the engine, built the way WDSP builds its receive
    // band-pass (fir.c, fir_bandpass): a windowed sinc, linear phase, the 4-term
    // Blackman-Harris window of WDSP's always-running notched band-pass. 4,095 taps
    // at 48 kHz rather than WDSP's minimum of 2,048, for steeper skirts at 250 Hz;
    // the delay, 2,047 samples, is removed, so the output lines up with the input.
    // ------------------------------------------------------------------
    const int NarrowTaps = 4095;

    static double[] NarrowKernel(double centre, double width)
    {
        double lo = centre - width / 2, hi = centre + width / 2;
        double ft = (hi - lo) / (2.0 * Rate), wOsc = Math.PI * (hi + lo) / Rate, m = .5 * (NarrowTaps - 1);
        var h = new double[NarrowTaps];
        for (int i = 0; i < NarrowTaps; i++)
        {
            double pos = i - m;
            double sinc = pos == 0 ? 2 * ft : Math.Sin(2 * Math.PI * ft * pos) / (Math.PI * pos);
            double c = Math.Cos(2 * Math.PI * i / (NarrowTaps - 1));
            double window = .21747 + c * (-.45325 + c * (.28256 + c * -.04672));
            h[i] = sinc * window * Math.Cos(pos * wOsc);
        }
        // Unity at the centre, exactly.
        double re = 0, im = 0;
        for (int i = 0; i < NarrowTaps; i++) { double a = 2 * Math.PI * centre * i / Rate; re += h[i] * Math.Cos(a); im -= h[i] * Math.Sin(a); }
        double g = Math.Sqrt(re * re + im * im);
        for (int i = 0; i < NarrowTaps; i++) h[i] /= g;
        return h;
    }

    static float[] NarrowBand(float[] x, double centre, double width)
    {
        var h = NarrowKernel(centre, width);
        const int size = 16384;
        int block = size - NarrowTaps + 1, delay = Mutation == "narrow-filter" ? 0 : (NarrowTaps - 1) / 2;
        var H = new Complex[size];
        for (int i = 0; i < NarrowTaps; i++) H[i] = h[i];
        Fft(H, false);
        var full = new double[x.Length + NarrowTaps];
        var buf = new Complex[size];
        for (int start = 0; start < x.Length; start += block)
        {
            Array.Clear(buf);
            int n = Math.Min(block, x.Length - start);
            for (int i = 0; i < n; i++) buf[i] = x[start + i];
            Fft(buf, false);
            for (int i = 0; i < size; i++) buf[i] *= H[i];
            Fft(buf, true);
            for (int i = 0; i < size && start + i < full.Length; i++) full[start + i] += buf[i].Real;
        }
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = (float)full[i + delay];
        return y;
    }

    // ------------------------------------------------------------------
    // A synthetic CW signal for the self-checks: a keyed tone with 5 ms raised-cosine
    // edges, a fixed pseudo-random element pattern, over band noise.
    // ------------------------------------------------------------------
    sealed record SynthCw(float[] Tone, float[] Noise, bool[] Keyed)
    {
        public float[] Mix(double noiseGain = 1) => Tone.Select((t, i) => (float)(t + Noise[i] * noiseGain)).ToArray();
    }

    static SynthCw MakeSynthCw(double seconds, double pitch, double toneOverNoiseDb, int seed, double carrierHz = 0, double carrierOverNoiseDb = 0)
    {
        int n = (int)(seconds * Rate);
        var rng = new Random(seed);
        var noise = new float[n];
        for (int i = 0; i < n; i++) noise[i] = (float)Gaussian(rng);
        BandPass(noise, 200, 3000);
        double noiseRms = Math.Sqrt(noise.Average(v => (double)v * v));
        double target = .02;
        for (int i = 0; i < n; i++) noise[i] = (float)(noise[i] * target / noiseRms);
        // Elements of one, three and gaps of one or three 60 ms units: a 20 wpm hand.
        var keyed = new bool[n];
        int unit = (int)(.06 * Rate), at = Rate / 2;
        while (at < n - Rate / 2)
        {
            int on = (rng.Next(3) == 0 ? 3 : 1) * unit, off = (rng.Next(4) == 0 ? 3 : 1) * unit;
            for (int i = at; i < Math.Min(n, at + on); i++) keyed[i] = true;
            at += on + off;
        }
        double amp = target * Math.Pow(10, toneOverNoiseDb / 20) * Math.Sqrt(2);
        var tone = new float[n];
        int ramp = (int)(.005 * Rate);
        double env = 0;
        for (int i = 0; i < n; i++)
        {
            env = Math.Clamp(env + (keyed[i] ? 1.0 / ramp : -1.0 / ramp), 0, 1);
            double shaped = .5 - .5 * Math.Cos(Math.PI * env);
            tone[i] = (float)(amp * shaped * Math.Sin(2 * Math.PI * pitch * i / Rate));
            if (carrierHz > 0) tone[i] += (float)(target * Math.Pow(10, carrierOverNoiseDb / 20) * Math.Sqrt(2) * Math.Sin(2 * Math.PI * carrierHz * i / Rate));
        }
        return new(tone, noise, keyed);
    }
}
