using System;
using System.Collections.Generic;
using System.Linq;
using JJPortaudio;

namespace Radios.Listenback;

/// <summary>Why a brightness offer is predicted to close less than the shortfall it was made for. No words here: the reader chooses the sentence.</summary>
public enum ClarityLiftLimit
{
    /// <summary>The offer is predicted to close the whole shortfall.</summary>
    None = 0,
    /// <summary>Even with drive and mix at the tops of their ranges the high band cannot lift the region that far: the offer is the most it can lift.</summary>
    Ranges,
    /// <summary>The peak guard turned the offer down: what it left is predicted to close only part of the shortfall.</summary>
    PeakGuard,
}

/// <summary>
/// What the suggest pass expects its brightness offer to do (#736, ruled
/// 2026-11-26): the lift, in decibels, of the region it measured short
/// against the middle it measured that against, predicted from renders of
/// the operator's own recording. Facts only; the reader says them.
/// </summary>
/// <param name="ShortfallDb">The presence shortfall the offer set out to close.</param>
/// <param name="PredictedLiftDb">What the offer as it may be kept (after the peak guard) is predicted to lift the region by, relative to the middle, against the settings in force now. Zero when the guard withdrew it.</param>
/// <param name="SolvedLiftDb">What the solved setting was predicted to lift, before the guard.</param>
/// <param name="MostLiftDb">The most the high band can lift the region within its ranges (drive and mix at most, its tune and harmonics as offered).</param>
/// <param name="Limit">Why the prediction falls short of the shortfall, or <see cref="ClarityLiftLimit.None"/>.</param>
/// <param name="RegionLowHz">The lowest nominal third-octave band of the region (Hz).</param>
/// <param name="RegionHighHz">The highest nominal third-octave band of the region (Hz).</param>
public sealed record ClarityLiftPrediction(
    double ShortfallDb,
    double PredictedLiftDb,
    double SolvedLiftDb,
    double MostLiftDb,
    ClarityLiftLimit Limit,
    int RegionLowHz,
    int RegionHighHz)
{
    /// <summary>True when the offer is predicted to close the whole shortfall.</summary>
    public bool ClosesTheShortfall => Limit == ClarityLiftLimit.None;
}

/// <summary>
/// The suggest pass's lift model (#736, 2026-11-26): from two renders of the
/// operator's own recording, the long-term lift any high-band weight would
/// give in any set of third-octave bands. Built because the level fix made
/// the high band's added path EXACTLY proportional to one number.
/// </summary>
/// <remarks>
/// <para><b>Why two renders predict every setting.</b> Since the level fix
/// the high band adds k times a fixed signal a, where k is the band's
/// <see cref="Weight"/> (mix times <see cref="ClarityExciter.HarmonicRatio"/>
/// of drive) and a depends only on the voice, the tune, the harmonics, the
/// passband and the rate (drive 24 and mix 100 is k = 1). Every filter and
/// the mix are linear and the shaper's output is weighted by the ratio after
/// it is made, so this holds to rounding. So the render at the settings in
/// force (weight k0) and the render with the band at k = 1 give the dry
/// signal x and a; and in each analysis frame and frequency bin the power at
/// any k is |X + kA|^2 = |X|^2 + 2k Re(X A*) + k^2 |A|^2, exactly. Summing
/// those three terms over the bins of each third-octave band and over the
/// frames of speech gives each band's long-term power at any k.</para>
/// <para><b>The analysis</b> is the suggest pass's own: the frames, window and
/// third-octave bands of <see cref="LongTermSpectrumMeter"/>, the frames of
/// speech chosen by the same 30 dB activity gate on the render at the
/// settings in force (so every k is averaged over the same frames).</para>
/// <para><b>The lift</b> of a region is its long-term power at k over its
/// power at k0, in decibels, minus the same for the middle it is judged
/// against: exactly how much the presence shortfall (the middle's deviation
/// minus the region's, <see cref="ClarityProposalRule"/>) would fall, since
/// the reference and the normalisation cancel.</para>
/// <para><b>What it cannot know:</b> the shortfall is measured on an off-air
/// take and the lift on the operator's microphone recording, so the two
/// meet only as far as the recording is like what was said on the air; and
/// an exciter lifts in proportion to the band it feeds from, so the same
/// setting lifts a brighter or duller stretch of speech by different
/// amounts. Stages after Clarity (the compressor) are not in the render the
/// model is built from; the guard and before and after still render them.</para>
/// </remarks>
public sealed class ClarityLiftModel
{
    private readonly double[] _x, _cross, _a;
    private readonly int _bandCount;

    private ClarityLiftModel(double[] x, double[] cross, double[] a, int bandCount, double baselineWeight, int framesUsed)
    {
        _x = x; _cross = cross; _a = a; _bandCount = bandCount;
        BaselineWeight = baselineWeight; FramesUsed = framesUsed;
    }

    /// <summary>The weight of the high band in the render the model was built from (the settings in force now).</summary>
    public double BaselineWeight { get; }

    /// <summary>How many analysis frames were judged speech and summed.</summary>
    public int FramesUsed { get; }

    /// <summary>
    /// A band's weight, k: zero when it adds nothing, otherwise mix (as a
    /// fraction) times <see cref="ClarityExciter.HarmonicRatio"/> of its
    /// drive. The added path is k times the band's added path at drive 24 and
    /// mix 100, for the same tune and harmonics.
    /// </summary>
    public static double Weight(ClarityBand band) =>
        band.AddsNothing ? 0.0 : band.MixPercent / 100.0 * ClarityExciter.HarmonicRatio(band.DriveDb);

    /// <summary>
    /// Build the model from the render at the settings in force
    /// (<paramref name="baseline"/>, its high band at weight
    /// <paramref name="baselineWeight"/>) and the render with the high band at
    /// weight one (<paramref name="full"/>), the same tune and harmonics and
    /// everything else alike. Null when there is too little audio, no speech,
    /// or the baseline is already at weight one (nothing is left to predict).
    /// </summary>
    public static ClarityLiftModel Build(float[] baseline, float[] full, double baselineWeight, int rate, double filterLow, double filterHigh)
    {
        if (baseline == null || full == null || rate <= 0) return null;
        int length = Math.Min(baseline.Length, full.Length);
        if (!(baselineWeight < 1.0 - 1e-9) || !(baselineWeight >= 0)) return null;
        int n = LongTermSpectrumMeter.FrameLength(rate);
        while (n > 512 && n > length) n >>= 1;
        if (n > length) return null;

        int bandCount = 0;
        while (bandCount < LongTermSpectrumMeter.NominalCentresHz.Length && LongTermSpectrumMeter.UpperHz(bandCount) < rate / 2.0) bandCount++;
        var inside = new bool[bandCount];
        for (int b = 0; b < bandCount; b++) inside[b] = filterHigh > filterLow && LongTermSpectrumMeter.Inside(b, filterLow, filterHigh);
        if (!inside.Any(v => v)) for (int b = 0; b < bandCount; b++) inside[b] = true;   // no usable passband: gate on everything

        var binBand = new int[n / 2];
        for (int k = 1; k < n / 2; k++)
        {
            double hz = k * (double)rate / n;
            binBand[k] = -1;
            for (int b = 0; b < bandCount; b++)
                if (hz >= LongTermSpectrumMeter.LowerHz(b) && hz < LongTermSpectrumMeter.UpperHz(b)) { binBand[k] = b; break; }
        }
        var window = new double[n];
        double windowPower = 0;
        for (int i = 0; i < n; i++) { window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n); windowPower += window[i] * window[i]; }
        double scale = 2.0 / (n * windowPower);

        // The dry signal and the added path at weight one: baseline = x + k0 a, full = x + a.
        double k0 = baselineWeight, inv = 1.0 / (1.0 - k0);
        var frames = new List<(double[] X, double[] C, double[] A, double Gate)>();
        var xr = new double[n]; var xi = new double[n]; var ar = new double[n]; var ai = new double[n];
        for (long start = 0; start + n <= length; start += n / 2)
        {
            for (int i = 0; i < n; i++)
            {
                double a = (full[start + i] - (double)baseline[start + i]) * inv;
                double x = baseline[start + i] - k0 * a;
                xr[i] = x * window[i]; xi[i] = 0;
                ar[i] = a * window[i]; ai[i] = 0;
            }
            Radix2Fft.Forward(xr, xi);
            Radix2Fft.Forward(ar, ai);
            var px = new double[bandCount]; var pc = new double[bandCount]; var pa = new double[bandCount];
            for (int k = 1; k < n / 2; k++)
            {
                int b = binBand[k];
                if (b < 0) continue;
                px[b] += (xr[k] * xr[k] + xi[k] * xi[k]) * scale;
                pc[b] += 2.0 * (xr[k] * ar[k] + xi[k] * ai[k]) * scale;
                pa[b] += (ar[k] * ar[k] + ai[k] * ai[k]) * scale;
            }
            // The gate reads the render at the settings in force, as the meter would.
            double gate = 0;
            for (int b = 0; b < bandCount; b++) if (inside[b]) gate += px[b] + k0 * pc[b] + k0 * k0 * pa[b];
            frames.Add((px, pc, pa, gate));
        }
        if (frames.Count == 0) return null;
        var totals = frames.Select(f => f.Gate).OrderBy(t => t).ToArray();
        double p90 = totals[Math.Min(totals.Length - 1, (int)Math.Floor(0.9 * (totals.Length - 1)))];
        if (!(p90 > 0)) return null;
        double floor = p90 * Math.Pow(10.0, -LongTermSpectrumMeter.ActivityGateDb / 10.0);
        var sx = new double[bandCount]; var sc = new double[bandCount]; var sa = new double[bandCount];
        int used = 0;
        foreach (var f in frames)
        {
            if (!(f.Gate >= floor)) continue;
            for (int b = 0; b < bandCount; b++) { sx[b] += f.X[b]; sc[b] += f.C[b]; sa[b] += f.A[b]; }
            used++;
        }
        return used == 0 ? null : new ClarityLiftModel(sx, sc, sa, bandCount, k0, used);
    }

    /// <summary>The long-term power of the given bands (nominal Hz) at weight <paramref name="k"/>, summed over the frames of speech (not averaged: only ratios are read).</summary>
    public double RegionPower(IEnumerable<int> nominalsHz, double k)
    {
        double p = 0;
        foreach (int hz in nominalsHz)
        {
            int b = Array.IndexOf(LongTermSpectrumMeter.NominalCentresHz, hz);
            if (b < 0 || b >= _bandCount) continue;
            p += _x[b] + k * _cross[b] + k * k * _a[b];
        }
        return p;
    }

    /// <summary>How much the given bands' power changes, dB, going from the baseline weight to <paramref name="k"/>.</summary>
    public double ChangeDb(IReadOnlyCollection<int> nominalsHz, double k)
    {
        double now = RegionPower(nominalsHz, BaselineWeight), then = RegionPower(nominalsHz, k);
        return 10.0 * Math.Log10(Math.Max(1e-30, then) / Math.Max(1e-30, now));
    }

    /// <summary>
    /// The lift of <paramref name="region"/> relative to <paramref name="middle"/>
    /// at weight <paramref name="k"/>, dB, against the settings in force: how
    /// much the presence shortfall would fall.
    /// </summary>
    public double LiftDb(IReadOnlyCollection<int> region, IReadOnlyCollection<int> middle, double k) =>
        ChangeDb(region, k) - ChangeDb(middle, k);
}

/// <summary>
/// The suggest pass's brightness solve (#736, ruled 2026-11-26): the high
/// band's drive and mix that bring the measured region's predicted lift to
/// the presence shortfall, within the ranges, from a <see cref="ClarityLiftModel"/>.
/// </summary>
/// <remarks>
/// <para><b>Weight first, then the levers.</b> The added path depends on drive
/// and mix only through their product (<see cref="ClarityLiftModel.Weight"/>),
/// so the solve finds the least weight whose predicted lift reaches the
/// shortfall, searching from the weight in force up to one (drive 24, mix 100),
/// on a fine grid refined by bisection. When no weight reaches it, it takes
/// the weight that lifts the most: the most the band can do.</para>
/// <para><b>Mix first, then drive.</b> The weight is turned into levers by
/// holding the operator's STORED drive (repair 2's ruling: his drive is his
/// choice, and the guard never goes below it) and solving for mix; only when
/// mix would have to pass 100 is the drive raised, to the least whole decibel
/// that reaches the weight, and the mix solved again at that drive. Two
/// settings with the same weight sound identical, so this choice changes
/// nothing heard; it keeps the offer to the lever the pass has always
/// proposed, and touches drive only when mix alone cannot do it. Mix is
/// rounded UP to a whole percent and drive up to a whole decibel (the
/// levers' own steps), so a reachable shortfall is predicted closed, never
/// just short of it; and a mix never goes below the one in force.</para>
/// </remarks>
public static class ClarityBrightnessSolve
{
    /// <summary>Grid points between the weight in force and one; each interval is then refined by bisection.</summary>
    public const int GridPoints = 400;

    /// <summary>
    /// The least lift worth offering, dB: below this the band is judged unable
    /// to lift the region (<see cref="ClarityJudgement.CannotLiftItMeasurably"/>).
    /// Judgement (2026-11-26): last night's measurements put lifts of a few
    /// tenths of a decibel at the edge of hearing.
    /// </summary>
    public const double LeastUsefulLiftDb = 0.5;

    /// <summary>A predicted lift this close under the shortfall still counts as closing it (rounding).</summary>
    public const double ClosedWithinDb = 0.05;

    /// <summary>The result of a solve: the band to offer and what it, and the most, are predicted to lift.</summary>
    public readonly record struct Solved(ClarityBand Band, double Weight, double LiftDb, double MostLiftDb, bool ReachesShortfall);

    /// <summary>
    /// Solve for the high band. <paramref name="current"/> is the band in force
    /// (its weight must be the model's baseline); <paramref name="shape"/> gives
    /// the tune and harmonics to offer (the rule's); the stored drive is
    /// <paramref name="current"/>'s.
    /// </summary>
    public static Solved Solve(ClarityLiftModel model, IReadOnlyCollection<int> region, IReadOnlyCollection<int> middle,
        ClarityBand current, ClarityBand shape, double shortfallDb)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        double k0 = model.BaselineWeight;
        double Lift(double k) => model.LiftDb(region, middle, k);

        var ks = new double[GridPoints + 1];
        var lifts = new double[GridPoints + 1];
        int best = 0;
        for (int i = 0; i <= GridPoints; i++)
        {
            ks[i] = k0 + (1.0 - k0) * i / GridPoints;
            lifts[i] = Lift(ks[i]);
            if (lifts[i] > lifts[best] + 1e-12) best = i;
        }
        double most = lifts[best];
        double kStar;
        bool reaches = most >= shortfallDb;
        if (!reaches) kStar = ks[best];
        else
        {
            int i = 1;
            while (i <= GridPoints && lifts[i] < shortfallDb) i++;
            double lo = ks[i - 1], hi = ks[Math.Min(i, GridPoints)];
            for (int it = 0; it < 40; it++)
            {
                double mid = 0.5 * (lo + hi);
                if (Lift(mid) >= shortfallDb) hi = mid; else lo = mid;
            }
            kStar = hi;
        }

        var band = Levers(current, shape, kStar);
        double weight = ClarityLiftModel.Weight(band);
        return new Solved(band, weight, Lift(weight), most, reaches);
    }

    /// <summary>
    /// The levers for a weight: the stored drive, mix solved and rounded up;
    /// when mix would pass 100, drive raised to the least whole decibel that
    /// reaches the weight (at most 24) and mix solved again. On, with the
    /// shape's tune and harmonics.
    /// </summary>
    public static ClarityBand Levers(ClarityBand current, ClarityBand shape, double weight)
    {
        float drive = Math.Clamp(current.DriveDb, ClaritySettings.DriveMinDb, ClaritySettings.DriveMaxDb);
        double mix = 100.0 * weight / ClarityExciter.HarmonicRatio(drive);
        if (mix > ClaritySettings.MixMaxPercent + 1e-9)
        {
            double needed = ClarityExciter.DriveAtBandLevelDb + 20.0 * Math.Log10(Math.Max(1e-12, weight));
            drive = (float)Math.Clamp(Math.Ceiling(needed - 1e-9), drive, ClaritySettings.DriveMaxDb);
            mix = 100.0 * weight / ClarityExciter.HarmonicRatio(drive);
        }
        float rounded = (float)Math.Clamp(Math.Ceiling(mix - 1e-9), ClaritySettings.MixMinPercent, ClaritySettings.MixMaxPercent);
        if (!current.AddsNothing) rounded = Math.Max(rounded, current.MixPercent);   // never below the mix in force
        return ClaritySettings.Normalize(new ClarityBand(true, shape.TuneHz, drive, rounded, shape.Harmonics), isHigh: true);
    }
}
