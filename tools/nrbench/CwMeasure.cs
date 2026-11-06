using System.Numerics;

namespace NrBench;
// Sprint 49 bench CW: a spectral measure for CW. The earlier measure (key-down energy
// against gap energy, broadband percentiles) disagreed with the operator's ear on the
// one-signal takes of 2026-11-04, because a broadband level cannot tell a quiet gap
// from a gap full of hiss at the station's own pitch. This one follows the station's
// tone:
//   1. The pitch: the bin whose level ALTERNATES most within short stretches (keying),
//      not the loudest bin (a carrier), not the bin whose level merely changed once over
//      the span (a filter widened part way through), and not what moves with the whole
//      band (a fast AGC pumping, a static crash).
//   2. When the station is keyed: from the reference (the unprocessed capture) at the
//      pitch, against a threshold that follows the station's own level through fades.
//      Where the local keying is too faint to tell from noise, blocks are UNCERTAIN and
//      count for nothing, rather than being called gaps.
//   3. Per 5 ms block of each file: the energy within 50 Hz of the pitch (a 100 Hz
//      linear-phase band-pass), and the noise in the rest of the receiver's passband
//      (more than 100 Hz from the pitch and from every other station being tracked),
//      scaled to the same 100 Hz bandwidth.
//   4. Per element (each key-down of the reference): the station's signal, noise
//      subtracted, against the same element in the reference. Deleting a weak element
//      therefore reads as a loss, never as quieter gaps.
// It is a measure, not a verdict: it cannot hear artefacts or a watery character.
//
// Repaired 2026-11-06 after the Codex check: the key-down threshold was one global Otsu
// split, so a fade put real key-down below it and called it gap, and deleting a faded
// station then IMPROVED every figure. The block was 10 ms with a 40 ms window and a
// 20 ms edge guard, which erased 30 ms dits. The pitch score was a whole-span level
// swing, which a filter change wins.
partial class BenchApp
{
    static bool PrintSpectrum;
    // 5 ms hop, 10 ms power window: a 30 ms dit keeps blocks clear of its edges.
    const int CwHop = 240, CwWindow = 480;
    const double ToneHalfWidth = 50, RestGuard = 100;
    /// <summary>Blocks dropped either side of a key edge: the band-pass's rise (about 10 ms) plus the window.</summary>
    static int EdgeGuardBlocks => Mutation == "cw-edge-guard" ? 4 : 2;
    /// <summary>The local stretch, either side, over which the station's own keying sets the threshold.</summary>
    const double LocalSeconds = 1.5;
    /// <summary>How far a stretch's level spread must exceed noise alone's before keying there counts as seen.</summary>
    const double CertainMarginDb = 3;
    /// <summary>
    /// A gap must sit near the local floor: within this share of white noise's spread, or within the
    /// recording's own spread of its quieter blocks (their 90th percentile above the floor), whichever
    /// is larger. A block above that and below the key-down threshold is too loud for noise and too
    /// quiet for key-down here, which is what a faded element beside strong ones looks like, so it
    /// is neither. The recording's own figure matters on air: static, a fast AGC recovering, and a
    /// station's own decay between elements all spread real gaps wider than white noise does.
    /// </summary>
    const double GapCeilingShare = .75;

    sealed record CwPitch(double Hz, double ScoreDb, double RunnerUpHz, double RunnerUpScoreDb, IReadOnlyList<(double Hz, double ScoreDb)> Candidates);

    /// <summary>The keyed station's pitch, from keying activity, with the other keyed pitches ranked behind it.</summary>
    static CwPitch FindPitch(float[] x, double lowHz = 200, double highHz = 3000)
    {
        const int N = 4096, hop = 1024, perWindow = 94; // 85 ms frames, 21 ms apart; 2 s stretches
        double bin = (double)Rate / N;
        // Only inside the receiver's passband, found from the capture itself: behind a narrow radio
        // filter almost every bin is the floor outside it, and that floor can move with the keying.
        bool wideBand = true;
        if (Mutation != "cw-pitch-wholeband")
        {
            var (pLo, pHi) = FindPassbandAuto(x);
            if (pHi - pLo >= 5 * bin) { lowHz = Math.Max(lowHz, pLo); highHz = Math.Min(highHz, pHi); }
            wideBand = pHi - pLo >= CommonModeMinWidthHz;
        }
        int lo = (int)Math.Ceiling(lowHz / bin), hi = (int)Math.Floor(highHz / bin);
        var window = Hann(N);
        int frames = Math.Max(0, (x.Length - N) / hop + 1);
        if (frames < 40) throw new InvalidOperationException("Too little audio to find a pitch");
        var levels = new float[hi - lo + 1][];
        for (int k = 0; k <= hi - lo; k++) levels[k] = new float[frames];
        var buf = new Complex[N];
        var power = new double[N / 2 + 1];
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < N; i++) buf[i] = x[f * hop + i] * window[i];
            Fft(buf, false);
            for (int k = 0; k <= N / 2; k++) power[k] = buf[k].Real * buf[k].Real + buf[k].Imaginary * buf[k].Imaginary;
            // Five bins (about 59 Hz) together: a keyed note's own spread, and a steadier noise estimate.
            for (int k = lo; k <= hi; k++)
            {
                double s = 0;
                for (int j = -2; j <= 2; j++) s += power[k + j];
                levels[k - lo][f] = (float)(10 * Math.Log10(s + 1e-30));
            }
        }
        // What moves the whole band at once is not keying: a fast AGC pumping on a strong signal,
        // or a static crash. Take each frame's median level across the passband out of every bin,
        // so only what one pitch does on its own is scored. Only from a passband many stations
        // wide: in a narrow one the median is largely the station itself, and taking it out would
        // take out the keying.
        if (Mutation != "cw-pitch-common-mode" && wideBand)
        {
            var column = new double[hi - lo + 1];
            for (int f = 0; f < frames; f++)
            {
                for (int k = 0; k < column.Length; k++) column[k] = levels[k][f];
                Array.Sort(column);
                float m = (float)column[column.Length / 2];
                for (int k = 0; k < column.Length; k++) levels[k][f] -= m;
            }
        }
        var score = new double[hi - lo + 1];
        int stretches = Math.Max(1, frames / perWindow);
        var range = new double[stretches];
        for (int k = 0; k < score.Length; k++)
        {
            if (Mutation == "cw-pitch") { score[k] = levels[k].Average(v => (double)v); continue; }
            if (Mutation == "cw-pitch-longspan")
            {
                var sorted = levels[k].Order().ToArray();
                score[k] = sorted[(int)(sorted.Length * .9)] - sorted[(int)(sorted.Length * .3)];
                continue;
            }
            // Keying alternates within seconds; a carrier does not move, and a filter change
            // moves once. So: the level's spread (90th less 20th percentile) within each 2 s
            // stretch, and the score is the spread reached in a quarter of the stretches.
            for (int w = 0; w < stretches; w++)
            {
                int a = w * perWindow, b = w == stretches - 1 ? frames : a + perWindow;
                var s = levels[k][a..b].Order().ToArray();
                range[w] = s[(int)(s.Length * .9)] - s[(int)(s.Length * .2)];
            }
            score[k] = range.Order().ElementAt((int)(stretches * .75));
        }
        // Candidates: best first, each more than 60 Hz from every better one.
        var candidates = new List<int>();
        foreach (int k in Enumerable.Range(0, score.Length).OrderByDescending(k => score[k]))
        {
            if (candidates.All(c => Math.Abs(c - k) * bin > 60)) candidates.Add(k);
            if (candidates.Count == 8) break;
        }
        int best = candidates[0];
        // The score picks the station; its exact pitch comes from the note itself: the power
        // centroid, within three bins (about 35 Hz), of the frames where that band is loudest.
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
        var ranked = candidates.Select(k => ((lo + k) * bin, score[k])).ToList();
        ranked[0] = ((lo + best + frac) * bin, score[best]);
        return new(ranked[0].Item1, score[best], ranked.Count > 1 ? ranked[1].Item1 : double.NaN, ranked.Count > 1 ? ranked[1].Item2 : double.NaN, ranked);
    }

    /// <summary>The common movement is only estimated from a passband at least this wide (about ten CW signals).</summary>
    const double CommonModeMinWidthHz = 600;

    /// <summary>
    /// The receiver's passband without a pitch: in each 5 s stretch, the widest run where that
    /// stretch's floor stays within 8 dB of its plateau; then everything any stretch let through.
    /// Per stretch, because an operator may widen the filter part way through, and then the
    /// whole take's floor makes the new, louder band the plateau and hides the old one.
    /// </summary>
    static (double Lo, double Hi) FindPassbandAuto(float[] x)
    {
        int stretch = 5 * Rate;
        // Under two stretches long: the whole span, once.
        // cw-passband-whole: the control that the per-stretch reading is load-bearing; the whole span at once.
        var parts = x.Length < 2 * stretch || Mutation == "cw-passband-whole" ? new List<float[]> { x }
            : Enumerable.Range(0, x.Length / stretch).Select(i => x.AsSpan(i * stretch, stretch).ToArray()).ToList();
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var part in parts)
        {
            var (smooth, edge, bin, lowest) = LongTermFloor(part);
            int bestLo = lowest, bestHi = lowest, k = lowest;
            while (k < smooth.Length)
            {
                if (smooth[k] < edge) { k++; continue; }
                int a = k;
                while (k + 1 < smooth.Length && smooth[k + 1] >= edge) k++;
                if (k - a > bestHi - bestLo) { bestLo = a; bestHi = k; }
                k++;
            }
            lo = Math.Min(lo, bestLo * bin); hi = Math.Max(hi, bestHi * bin);
        }
        return (lo, hi);
    }

    /// <summary>The receiver's passband around the pitch: where the long-term noise floor (20th percentile) stays within 8 dB of its plateau.</summary>
    static (double Lo, double Hi) FindPassband(float[] x, double pitch)
    {
        var (smooth, edge, bin, lowest) = LongTermFloor(x);
        int maxBin = smooth.Length - 1;
        int c = Math.Clamp((int)Math.Round(pitch / bin), lowest, maxBin);
        int l = c, h = c;
        while (l > lowest && smooth[l - 1] >= edge) l--;
        while (h < maxBin && smooth[h + 1] >= edge) h++;
        return (l * bin, h * bin);
    }

    /// <summary>The long-term noise floor by bin (20th percentile, smoothed over about 50 Hz) to 4 kHz, and the level 8 dB under its plateau.</summary>
    static (double[] Smooth, double Edge, double Bin, int Lowest) LongTermFloor(float[] x)
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
        return (smooth, edge, bin, lowest);
    }

    static double[] Hann(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / n);
        return w;
    }

    /// <summary>Mean power per 5 ms block over a 10 ms window, in dB; block b is centred on sample b * CwHop + CwWindow / 2.</summary>
    static double[] BlockPowerDb(float[] y)
    {
        int n = Math.Max(0, (y.Length - CwWindow) / CwHop + 1);
        var p = new double[n];
        for (int b = 0; b < n; b++)
        {
            double s = 0;
            for (int i = b * CwHop; i < b * CwHop + CwWindow; i++) s += (double)y[i] * y[i];
            p[b] = 10 * Math.Log10(s / CwWindow + 1e-30);
        }
        return p;
    }

    /// <summary>The tone band: 100 Hz centred on the pitch, linear phase, delay removed.</summary>
    static double[] ToneBlocks(float[] x, double pitch) => BlockPowerDb(NarrowBand(x, pitch, 2 * ToneHalfWidth));

    /// <summary>Per 5 ms block: energy within 50 Hz of the pitch, and the rest of the passband scaled to 100 Hz (NaN when the passband leaves no room).</summary>
    sealed record CwBlocks(double[] Tone, double[] Rest);

    static CwBlocks Blocks(float[] x, double pitch, double passLo, double passHi, IReadOnlyList<double>? others = null)
    {
        var tone = ToneBlocks(x, pitch);
        // The rest of the passband: the passband less 100 Hz either side of the pitch and of every
        // other station being tracked, so deleting a station never reads as quieter noise.
        var centres = new List<double> { pitch };
        if (others != null && Mutation != "cw-rest-includes-stations") centres.AddRange(others);
        var cuts = new List<(double Lo, double Hi)>();
        foreach (double c in centres.Order())
        {
            double a = Math.Max(passLo, c - RestGuard), b = Math.Min(passHi, c + RestGuard);
            if (b <= a) continue;
            if (cuts.Count > 0 && a <= cuts[^1].Hi) cuts[^1] = (cuts[^1].Lo, Math.Max(cuts[^1].Hi, b));
            else cuts.Add((a, b));
        }
        double width = passHi - passLo - cuts.Sum(c => c.Hi - c.Lo);
        if (width < 2 * ToneHalfWidth) return new(tone, Enumerable.Repeat(double.NaN, tone.Length).ToArray());
        var rest = NarrowBand(x, (passLo + passHi) / 2, passHi - passLo);
        foreach (var (a, b) in cuts)
        {
            var band = NarrowBand(x, (a + b) / 2, b - a);
            for (int i = 0; i < rest.Length; i++) rest[i] -= band[i];
        }
        double scale = 10 * Math.Log10(2 * ToneHalfWidth / width);
        return new(tone, BlockPowerDb(rest).Select(v => v + scale).ToArray());
    }

    /// <summary>One key-down of the reference: blocks [Start, End) above the threshold.</summary>
    sealed record CwElement(int Start, int End);

    /// <summary>
    /// Key-down (true), gap (false) or neither (null: within the edge guard, outside the span, or
    /// where the keying is too faint to tell from noise), from the reference's tone at the pitch.
    /// </summary>
    sealed record CwMask(bool?[] Key, double ThresholdDb, double[] ReferenceTone, CwElement[] Elements, int UncertainBlocks, int ShortElements, double MinContrastDb);

    static double Percentile(double[] sorted, double p) => sorted[Math.Clamp((int)(sorted.Length * p), 0, sorted.Length - 1)];

    static double[] Median3(double[] v)
    {
        var s = new double[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            double a = v[Math.Max(0, i - 1)], b = v[i], c = v[Math.Min(v.Length - 1, i + 1)];
            s[i] = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        }
        return s;
    }

    /// <summary>The local floor (20th percentile) and peak (90th) of a block series, over the stretch either side.</summary>
    static (double[] Floor, double[] Peak) LocalSpread(double[] v)
    {
        int half = (int)(LocalSeconds * Rate / CwHop), step = 10, n = v.Length;
        var floor = new double[n]; var peak = new double[n];
        var anchors = new List<int>();
        for (int a = 0; a < n; a += step) anchors.Add(a);
        if (anchors[^1] != n - 1) anchors.Add(n - 1);
        var f = new double[anchors.Count]; var p = new double[anchors.Count];
        for (int j = 0; j < anchors.Count; j++)
        {
            int a = Math.Max(0, anchors[j] - half), b = Math.Min(n, anchors[j] + half + 1);
            var s = v[a..b].Order().ToArray();
            f[j] = Percentile(s, .2); p[j] = Percentile(s, .9);
        }
        for (int j = 0; j + 1 < anchors.Count; j++)
            for (int i = anchors[j]; i <= anchors[j + 1]; i++)
            {
                double t = (double)(i - anchors[j]) / Math.Max(1, anchors[j + 1] - anchors[j]);
                floor[i] = f[j] + t * (f[j + 1] - f[j]); peak[i] = p[j] + t * (p[j + 1] - p[j]);
            }
        return (floor, peak);
    }

    static double? noiseSpreadDb;
    /// <summary>
    /// The local spread (90th less 20th percentile) that noise ALONE shows through this tone band
    /// and block, measured once on white noise. Keying is seen only where a stretch spreads
    /// CertainMarginDb more than this.
    /// </summary>
    static double NoiseSpreadDb()
    {
        if (noiseSpreadDb != null) return noiseSpreadDb.Value;
        var rng = new Random(4242);
        var noise = Enumerable.Range(0, Rate * 20).Select(_ => (float)Gaussian(rng)).ToArray();
        var tone = Median3(ToneBlocks(noise, 700));
        var (floor, peak) = LocalSpread(tone);
        var spread = peak.Zip(floor, (p, f) => p - f).Skip(400).SkipLast(400).Order().ToArray();
        noiseSpreadDb = Percentile(spread, .99);
        return noiseSpreadDb.Value;
    }

    /// <summary>A gap counts only with the station clearly keyed within this long on BOTH sides of it.</summary>
    const double GapAnchorSeconds = .75;

    static double[]? toneResponse;
    /// <summary>The tone band's response, in dB, at an offset from its centre (1 Hz steps to 400 Hz; beyond, its floor).</summary>
    static double ToneBandResponseDb(double offsetHz)
    {
        if (toneResponse == null)
        {
            var h = NarrowKernel(1000, 2 * ToneHalfWidth);
            var r = new double[401];
            for (int d = 0; d <= 400; d++)
            {
                double re = 0, im = 0, f = 1000 + d;
                for (int i = 0; i < h.Length; i++) { double a = 2 * Math.PI * f * i / Rate; re += h[i] * Math.Cos(a); im -= h[i] * Math.Sin(a); }
                r[d] = 10 * Math.Log10(re * re + im * im + 1e-30);
            }
            toneResponse = r;
        }
        return toneResponse[Math.Clamp((int)Math.Round(Math.Abs(offsetHz)), 0, 400)];
    }

    /// <summary>A named station closer than this (the tone band less than 13 dB down at its pitch) cannot be told apart from the station measured.</summary>
    const double InseparableResponseDb = -13;

    static CwMask MakeMask(float[] reference, double pitch, double spanStart = 0, double spanEnd = double.PositiveInfinity, IReadOnlyList<CwStationSpec>? others = null)
    {
        var tone = ToneBlocks(reference, pitch);
        var smooth = Median3(tone);
        int n = smooth.Length;
        var raw = new bool?[n];
        var (floor, peak) = LocalSpread(smooth);
        double Seconds(int i) => (i * CwHop + CwWindow / 2) / (double)Rate;
        // A named station contaminates this one only where its own level, through this station's tone
        // band at their separation (taking the worst case within 10 Hz, for an imprecise pitch), comes
        // within 10 dB of what this band measures and 3 dB clear of its floor. Only there, and only
        // within the station's own span, is the block contaminated and left unscored. Closer than the
        // band can separate, wherever the named station keys, this one cannot be scored at all.
        // Simultaneous keying that is NOT contaminated is kept.
        var contaminated = new bool[n];
        if (others != null && Mutation != "cw-no-station-veto")
        {
            foreach (var o in others)
            {
                var other = Median3(ToneBlocks(reference, o.Hz));
                double response = ToneBandResponseDb(Math.Max(0, Math.Abs(o.Hz - pitch) - 10));
                bool inseparable = response >= InseparableResponseDb;
                var (of, op) = LocalSpread(other);
                for (int i = 0; i < n && i < other.Length; i++)
                {
                    double t = Seconds(i);
                    if (t < o.From || t >= o.To) continue;
                    if (Mutation == "cw-veto-any-louder")
                    {
                        // The control: d03deb33's rule, any keyed station louder in its own band.
                        contaminated[i] |= op[i] - of[i] >= NoiseSpreadDb() + CertainMarginDb && other[i] > of[i] + (op[i] - of[i]) / 2 && other[i] > smooth[i];
                        continue;
                    }
                    if (inseparable)
                        contaminated[i] |= op[i] - of[i] >= NoiseSpreadDb() + CertainMarginDb && other[i] > of[i] + (op[i] - of[i]) / 2;
                    else
                    {
                        double leak = other[i] + response;
                        contaminated[i] |= leak >= smooth[i] - 10 && leak >= floor[i] + 3;
                    }
                }
            }
            // Widened by the edge guard, so the neighbour's element edges go with it.
            var wide = new bool[n];
            for (int i = 0; i < n; i++)
                if (contaminated[i]) for (int j = Math.Max(0, i - EdgeGuardBlocks); j <= Math.Min(n - 1, i + EdgeGuardBlocks); j++) wide[j] = true;
            contaminated = wide;
        }
        double minContrast = NoiseSpreadDb() + CertainMarginDb, reported;
        if (Mutation == "cw-global-threshold")
        {
            // The control: the old classifier, one Otsu split over the whole span.
            reported = Otsu(smooth);
            for (int i = 0; i < n; i++) raw[i] = smooth[i] > reported;
        }
        else
        {
            // The threshold follows the station: halfway, in dB, between the local floor and the
            // local key-down level, so a fade lowers it with the signal. Where the stretch spreads
            // no more than noise would, nothing is classified.
            var quiet = new List<double>();
            for (int i = 0; i < n; i++)
            {
                double contrast = peak[i] - floor[i];
                if (contrast >= minContrast && smooth[i] <= floor[i] + contrast / 2) quiet.Add(smooth[i] - floor[i]);
            }
            double ceiling = Math.Max(GapCeilingShare * (minContrast - CertainMarginDb), quiet.Count > 0 ? Percentile(quiet.Order().ToArray(), .9) : 0);
            var thresholds = new List<double>();
            for (int i = 0; i < n; i++)
            {
                double contrast = peak[i] - floor[i];
                if (contrast < minContrast) { raw[i] = null; continue; }
                double t = floor[i] + contrast / 2;
                thresholds.Add(t);
                raw[i] = smooth[i] > t ? true : smooth[i] <= Math.Min(t, floor[i] + ceiling) ? false : null;
            }
            reported = thresholds.Count > 0 ? Percentile(thresholds.Order().ToArray(), .5) : double.NaN;
            // A gap is a gap only where the station is demonstrably strong on both sides of it, close
            // by. In a fade, or a long pause, nothing proves the station is not still keying below the
            // noise, so a quiet block there could be its faint key-down: it is left unscored, never
            // called a gap. Otherwise deleting a faded station would make its "gaps" quieter.
            if (Mutation != "cw-gap-unanchored")
            {
                int reach = (int)(GapAnchorSeconds * Rate / CwHop);
                // Clearly keyed: three blocks running (15 ms) at least the certainty contrast over the
                // local floor. One block can get there on a noise peak; three in a row is keying.
                var strong = new bool[n];
                for (int i = 0; i < n; i++) strong[i] = raw[i] == true && smooth[i] >= floor[i] + minContrast;
                var anchored = new bool[n];
                for (int i = 0; i + 2 < n; i++)
                    if (strong[i] && strong[i + 1] && strong[i + 2]) anchored[i] = anchored[i + 1] = anchored[i + 2] = true;
                strong = anchored;
                int last = -1;
                var before = new int[n];
                for (int i = 0; i < n; i++) { if (strong[i]) last = i; before[i] = last; }
                int next = -1;
                var after = new int[n];
                for (int i = n - 1; i >= 0; i--) { if (strong[i]) next = i; after[i] = next; }
                for (int i = 0; i < n; i++)
                    if (raw[i] == false && (before[i] < 0 || i - before[i] > reach || after[i] < 0 || after[i] - i > reach)) raw[i] = null;
            }
        }
        for (int i = 0; i < n; i++)
        {
            double t = (i * CwHop + CwWindow / 2) / (double)Rate;
            if (t < spanStart || t >= spanEnd || contaminated[i]) raw[i] = null;
        }
        var key = new bool?[n];
        int g = EdgeGuardBlocks;
        for (int i = 0; i < n; i++)
        {
            if (raw[i] == null) continue;
            // Dropped only near the OPPOSITE class: a block beside an unclassified one is still what it is.
            bool clear = true;
            for (int j = Math.Max(0, i - g); j <= Math.Min(n - 1, i + g); j++) clear &= raw[j] == null || raw[j] == raw[i];
            key[i] = clear ? raw[i] : null;
        }
        var elements = new List<CwElement>();
        int shortOnes = 0;
        for (int i = 0; i < n;)
        {
            if (raw[i] != true) { i++; continue; }
            int s = i;
            while (i < n && raw[i] == true) i++;
            bool measured = false;
            for (int j = s; j < i; j++) measured |= key[j] == true;
            if (measured) elements.Add(new(s, i)); else shortOnes++;
        }
        return new(key, reported, smooth, elements.ToArray(), raw.Count(r => r == null), shortOnes, minContrast);
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
        int lo = -(int)(.2 * Rate / CwHop), hi = (int)(.6 * Rate / CwHop);
        int best = 0; double bestV = double.MinValue;
        for (int lag = lo; lag <= hi; lag++)
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

    static int LagMs(int blocks) => blocks * CwHop * 1000 / Rate;

    /// <summary>
    /// The figures for one file. ElementSignal is each reference element's signal power in this file
    /// (key-down less the nearby gaps, linear; NaN where it could not be measured), so two files'
    /// figures compare element by element (see Survival).
    /// </summary>
    sealed record CwFigures(double KeyDown, double Gap, double WeakKeyDown, double RestKey, double RestGap, int Lag, int KeyBlocks, int GapBlocks,
        double[] ElementSignal, double[] ElementCentreSeconds, double UnscoredShare)
    {
        public double Separation => KeyDown - Gap;
        public double WeakSeparation => WeakKeyDown - Gap;
        public double RestBelowStation => KeyDown - RestGap;
    }

    static CwFigures Measure(float[] file, CwMask mask, double pitch, double passLo, double passHi, IReadOnlyList<double>? others = null, int? knownLag = null)
    {
        var blocks = Blocks(file, pitch, passLo, passHi, others);
        int lag = knownLag ?? BlockLag(mask.ReferenceTone, blocks.Tone);
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
        // Key-down is the MEAN of its blocks in dB, not their median: a median cannot see a third of
        // the elements vanish, so deleting faded elements would leave it unmoved (or nudge it up).
        // The weakest third of the station's key-down blocks, by the reference's own level: fades and QSB.
        var weak = keyRef.OrderBy(k => k.Ref).Take(Math.Max(1, keyRef.Count / 3)).Select(k => k.File).ToList();
        // Each element's signal: its key-down power less the gaps within a second of it, so the
        // noise an engine removes is not counted as signal lost.
        int near = (int)(1.0 * Rate / CwHop);
        double Lin(double db) => Math.Pow(10, db / 10);
        double fallbackGap = Lin(Median(gap));
        var signal = new double[mask.Elements.Length];
        var centres = new double[mask.Elements.Length];
        for (int e = 0; e < mask.Elements.Length; e++)
        {
            var el = mask.Elements[e];
            centres[e] = ((el.Start + el.End) / 2 * CwHop + CwWindow / 2) / (double)Rate;
            double p = 0; int pc = 0;
            for (int i = el.Start; i < el.End; i++)
            {
                int j = i + lag;
                if (mask.Key[i] == true && j >= 0 && j < blocks.Tone.Length) { p += Lin(blocks.Tone[j]); pc++; }
            }
            if (pc == 0) { signal[e] = double.NaN; continue; }
            double g = 0, g2 = 0; int gc = 0;
            for (int i = Math.Max(0, el.Start - near); i < Math.Min(mask.Key.Length, el.End + near); i++)
            {
                int j = i + lag;
                if (mask.Key[i] == false && j >= 0 && j < blocks.Tone.Length) { double v = Lin(blocks.Tone[j]); g += v; g2 += v * v; gc++; }
            }
            double gapMean = gc > 0 ? g / gc : fallbackGap;
            // Detected only if it stands out from the nearby gaps by more than twice their own
            // scatter over as many blocks; otherwise this element is not there in this file.
            double scatter = gc > 1 ? Math.Sqrt(Math.Max(0, g2 / gc - gapMean * gapMean)) : gapMean;
            double s = p / pc - gapMean;
            signal[e] = s > 2 * scatter / Math.Sqrt(pc) ? s : 0;
        }
        return new(key.Average(), Median(gap), weak.Count > 0 ? Median(weak) : double.NaN,
            Median(restKey), Median(restGap), lag, key.Count, gap.Count, signal, centres,
            // What the reference cannot classify (too faint, a gap with no strong keying either side,
            // contaminated by a named station, or outside the span). Not the edge guard around every
            // element, which is excluded by design and is not a hole in the judgement.
            mask.UncertainBlocks / (double)Math.Max(1, mask.Key.Length));
    }

    /// <summary>
    /// Element by element, a file against the reference: the station's level change (the median over
    /// its strongest half), how much more its weakest third lost than that, and how many elements
    /// survived: still detected, and within 10 dB of the anchor. The anchor is the station's own
    /// change, or, for a second station, the MAIN station's change (pass it), so that a station
    /// deleted outright cannot count its own noise as kept. A loss is floored at 30 dB.
    /// </summary>
    sealed record CwSurvival(double StationChangeDb, double WeakAgainstStrongDb, int Kept, int Measured, double[] SegmentChangeDb);

    static CwSurvival Survival(CwFigures file, CwFigures reference, double? anchorDb = null, double segmentSeconds = 20)
    {
        var rows = new List<(double Ref, double Change, double Centre)>();
        for (int e = 0; e < reference.ElementSignal.Length; e++)
        {
            double r = reference.ElementSignal[e], f = file.ElementSignal[e];
            if (!(r > 0) || double.IsNaN(f)) continue;
            rows.Add((r, 10 * Math.Log10((Math.Max(0, f) + r * 1e-3) / r), reference.ElementCentreSeconds[e]));
        }
        if (rows.Count < 6) return new(double.NaN, double.NaN, 0, rows.Count, Array.Empty<double>());
        var byLevel = rows.OrderBy(r => r.Ref).ToList();
        double station = Median(byLevel.Skip(byLevel.Count / 2).Select(r => r.Change).ToList());
        double weakThird = Median(byLevel.Take(Math.Max(1, byLevel.Count / 3)).Select(r => r.Change).ToList());
        double anchor = anchorDb ?? station;
        int kept = rows.Count(r => r.Change > -29.9 && r.Change - anchor >= -10);
        int segments = (int)Math.Ceiling(rows.Max(r => r.Centre) / segmentSeconds);
        var seg = Enumerable.Range(0, segments).Select(s =>
        {
            var c = rows.Where(r => r.Centre >= s * segmentSeconds && r.Centre < (s + 1) * segmentSeconds).Select(r => r.Change).ToList();
            return c.Count >= 5 ? Median(c) : double.NaN;
        }).ToArray();
        return new(station, weakThird - station, kept, rows.Count, seg);
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
        var s = Survival(f, reference);
        string elements = double.IsNaN(s.StationChangeDb) ? "too few elements to follow one by one"
            : $"elements kept {s.Kept} of the {s.Measured} it could score; the weakest third of those {s.WeakAgainstStrongDb:+0.0;-0.0;0.0} dB against the strongest half";
        return $"{name}: {100 * f.UnscoredShare:0} percent of the span unscorable, and no figure here covers it; key-down {f.Separation:0.0} dB above the gaps at the pitch; the gaps at the pitch fell " +
               $"{(f.Separation - reference.Separation):+0.0;-0.0;0.0} dB against the capture, relative to the station; " +
               $"weakest third of key-down {f.WeakSeparation:0.0} dB above the gaps; {elements}; {rest}; " +
               $"station level {(f.KeyDown - reference.KeyDown):+0.0;-0.0;0.0} dB against the capture" +
               (f.Lag != 0 ? $"; aligned {LagMs(f.Lag)} ms late" : "") + ".";
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
