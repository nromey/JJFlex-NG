using System;
using JJPortaudio;

namespace Radios.Listenback;

/// <summary>
/// What the peak guard did (Sprint 49 Track J, J3). <see cref="Proposal"/> is
/// the proposal as it may be offered: reduced, withdrawn, or unchanged. The
/// before and after figures are of the guard's last render.
/// </summary>
public sealed record ClarityGuardResult(
    ClarityProposal Proposal,
    bool Reduced,
    bool Withdrawn,
    int Steps,
    long OverFullScaleBefore,
    long OverFullScaleAfter,
    double PeakBeforeDbfs,
    double PeakAfterDbfs,
    double? CeilingDbfs);

/// <summary>
/// The suggest pass's peak guard (Sprint 49 Track J, J3; parity research idea
/// 7, "no suggestion raises peaks past the ceiling"). It renders the
/// operator's own audio twice, with Clarity as it is now ("before") and with
/// the proposal ("after"), and reduces the proposal until the after has no
/// more samples over full scale than the before and, where a limiter's
/// ceiling is given, does not raise the peak past it.
/// </summary>
/// <remarks>
/// <para><b>How it reduces, one step at a time, and only the increase</b>
/// (repair 1, the orchestrator's ruling: the loop adds, never takes away,
/// #152): every band the proposal changes has its mix's INCREASE over the
/// operator's current setting multiplied by <see cref="MixFactor"/>, and on
/// every second step its drive lowered by <see cref="DriveStepDb"/>, never
/// below the band's STORED drive, even when the band is off or at zero mix
/// (repair 2; the floor is checked before the first render too). It never
/// goes below the operator's current setting. A band whose increase falls below <see cref="MixLeastPercent"/>
/// goes back to the current setting; when no band is left changed the
/// proposal is WITHDRAWN (it is then "nothing", which the reader says, with
/// the reason), and the current settings stand.</para>
/// <para><b>The ceiling</b> (Track CL's limiter, when it is on; this track
/// knows only a number): the after may not peak above it unless the before
/// already did, and then not above the before.</para>
/// <para><b>The render</b> is a seam: by default Clarity alone
/// (<see cref="RenderClarityOnly"/>); phase 2 passes the audition twin's
/// whole-chain render, so the guard sees what the chain will send.</para>
/// </remarks>
public static class ClarityPeakGuard
{
    public const int MostSteps = 16;
    public const double MixFactor = 0.7;
    public const float DriveStepDb = 3f;
    public const float MixLeastPercent = 5f;

    /// <summary>
    /// Guard a proposal against the operator's own audio <paramref name="before"/>
    /// (mono, at <paramref name="rate"/>). <paramref name="render"/>, when given,
    /// renders that audio with a Clarity setting; null renders Clarity alone with
    /// the given passband edges.
    /// </summary>
    public static ClarityGuardResult Apply(ClarityProposal proposal, float[] before, int rate,
        float passbandLowHz, float passbandHighHz, double? limiterCeilingDbfs = null,
        Func<ClaritySettings, float[]> render = null)
    {
        if (proposal == null) throw new ArgumentNullException(nameof(proposal));
        if (before == null || before.Length == 0) throw new ArgumentException("no audio to guard against", nameof(before));
        render ??= s => RenderClarityOnly(before, rate, s, passbandLowHz, passbandHighHz);

        var current = proposal.Current;
        var baseline = render(current);
        long overBefore = CountOver(baseline);
        double peakBefore = PeakDbfs(baseline);
        if (proposal.IsNothing)
            return new ClarityGuardResult(proposal, false, false, 0, overBefore, overBefore, peakBefore, peakBefore, limiterCeilingDbfs);

        // The drive floor is checked at step zero too (repair 2): a proposal
        // that would lower a band's stored drive is lifted back to it before
        // anything is rendered, so no candidate ever goes below the floor.
        var candidate = AtLeastStoredDrive(proposal);
        int steps = 0;
        bool withdrawn = false;
        while (true)
        {
            var after = render(candidate.Proposed);
            long overAfter = CountOver(after);
            double peakAfter = PeakDbfs(after);
            bool overs = overAfter > overBefore;
            bool ceiling = limiterCeilingDbfs is double c && peakAfter > c && peakAfter > peakBefore;
            if ((!overs && !ceiling) || withdrawn || steps >= MostSteps)
                return new ClarityGuardResult(candidate, steps > 0, withdrawn, steps, overBefore, overAfter, peakBefore, peakAfter, limiterCeilingDbfs);
            steps++;
            candidate = Reduce(candidate, steps, out withdrawn);
        }
    }

    private static ClarityProposal Reduce(ClarityProposal p, int step, out bool withdrawn)
    {
        var settings = p.Proposed;
        var brightness = p.Brightness;
        var fullness = p.Fullness;
        if (p.ProposesBrightness)
        {
            if (!TrySmaller(settings.High, p.Current.High, step, out var b)) { settings = settings.WithHigh(p.Current.High); brightness = ClarityJudgement.WithdrawnByPeakGuard; }
            else settings = settings.WithHigh(b);
        }
        if (p.ProposesFullness)
        {
            if (!TrySmaller(settings.Low, p.Current.Low, step, out var b)) { settings = settings.WithLow(p.Current.Low); fullness = ClarityJudgement.WithdrawnByPeakGuard; }
            else settings = settings.WithLow(b);
        }
        var next = p with { Proposed = settings, Brightness = brightness, Fullness = fullness };
        withdrawn = next.IsNothing;
        return next;
    }

    /// <summary>
    /// Shrink only the INCREASE a proposed band makes over the operator's
    /// current band (repair 1; the orchestrator's ruling, #152: the loop adds,
    /// never takes away). The mix's increase is multiplied by
    /// <see cref="MixFactor"/>; on every second step the drive comes down
    /// <see cref="DriveStepDb"/>, never below the current band's STORED drive,
    /// on, off or at zero mix (repair 2). False when the increase left is under
    /// <see cref="MixLeastPercent"/>: the band is then withdrawn and the
    /// current setting stands.
    /// </summary>
    private static bool TrySmaller(ClarityBand proposed, ClarityBand current, int step, out ClarityBand smaller)
    {
        bool currentOn = !current.AddsNothing;
        float baseMix = currentOn ? current.MixPercent : 0f;
        float increase = (float)((proposed.MixPercent - baseMix) * MixFactor);
        float driveFloor = current.DriveDb;   // the stored drive, whether the band is on or off (repair 2)
        float drive = step % 2 == 0 ? Math.Max(driveFloor, proposed.DriveDb - DriveStepDb) : proposed.DriveDb;
        smaller = proposed.WithMix(baseMix + increase).WithDrive(Math.Max(driveFloor, drive));
        return increase >= MixLeastPercent;
    }

    /// <summary>A proposal whose changed bands each keep at least the operator's stored drive.</summary>
    private static ClarityProposal AtLeastStoredDrive(ClarityProposal p)
    {
        var s = p.Proposed;
        if (p.ProposesBrightness && s.High.DriveDb < p.Current.High.DriveDb) s = s.WithHigh(s.High.WithDrive(p.Current.High.DriveDb));
        if (p.ProposesFullness && s.Low.DriveDb < p.Current.Low.DriveDb) s = s.WithLow(s.Low.WithDrive(p.Current.Low.DriveDb));
        return ReferenceEquals(s, p.Proposed) ? p : p with { Proposed = s };
    }

    /// <summary>
    /// Render mono audio through JJ Clarity alone, at its own rate, in the
    /// transmit chain's 10 ms buffers. Offline; allocates freely.
    /// </summary>
    public static float[] RenderClarityOnly(float[] mono, int rate, ClaritySettings settings, float passbandLowHz, float passbandHighHz)
    {
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        var exciter = new ClarityExciter { Enabled = settings != null && !settings.AddsNothing };
        exciter.Use(ClarityDesign.Create(settings ?? ClaritySettings.Default, passbandLowHz, passbandHighHz, (uint)rate));
        int block = Math.Max(1, rate / 100);
        var stereo = new float[block * 2];
        var result = new float[mono.Length];
        for (int start = 0; start < mono.Length; start += block)
        {
            int n = Math.Min(block, mono.Length - start);
            for (int i = 0; i < n; i++) stereo[2 * i] = stereo[2 * i + 1] = mono[start + i];
            exciter.Process(stereo, n * 2, (uint)rate, countStats: false);
            for (int i = 0; i < n; i++) result[start + i] = stereo[2 * i];
        }
        return result;
    }

    /// <summary>Samples past full scale.</summary>
    public static long CountOver(float[] audio)
    {
        long n = 0;
        foreach (float v in audio) if (v > 1f || v < -1f) n++;
        return n;
    }

    /// <summary>The largest sample, dBFS.</summary>
    public static double PeakDbfs(float[] audio)
    {
        double peak = 0;
        foreach (float v in audio) peak = Math.Max(peak, Math.Abs(v));
        return 20.0 * Math.Log10(Math.Max(1e-10, peak));
    }
}
