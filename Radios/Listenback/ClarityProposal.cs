using System;
using System.Collections.Generic;
using System.Linq;
using JJPortaudio;

namespace Radios.Listenback;

/// <summary>One band's place against the reference: the voice's share and the reference's share of the in-passband power, dB, and the difference.</summary>
public sealed record BandDeviation(int NominalHz, double MeasuredDb, double ReferenceDb)
{
    /// <summary>Positive: the voice has more of this band than the reference does.</summary>
    public double DeviationDb => MeasuredDb - ReferenceDb;
}

/// <summary>What the rule concluded about one band of Clarity. No words here: the reader chooses the sentence.</summary>
public enum ClarityJudgement
{
    /// <summary>The region is short of the reference by at least the threshold: a setting is proposed.</summary>
    Proposed = 0,
    /// <summary>The region is within the threshold of the reference, or above it: nothing is needed. Saying so is a result.</summary>
    AlreadyEnough,
    /// <summary>The take's transmit filter holds too little of the region (or of the middle it is judged against) to judge.</summary>
    RegionOutsidePassband,
    /// <summary>A setting would be proposed, but the band is already at the rule's most.</summary>
    AtTheRulesMost,
    /// <summary>A setting was proposed, and the peak guard took it back: even reduced, it put more samples over full scale (or past the limiter's ceiling) than the operator's audio has now.</summary>
    WithdrawnByPeakGuard,
}

/// <summary>
/// The suggest pass's proposal (Sprint 49 Track J, J3): the Clarity settings
/// it proposes, whether it proposes anything, and every fact it rests on. It
/// is a PROPOSAL: nothing here applies it (never auto-apply, #262).
/// </summary>
public sealed record ClarityProposal(
    ClaritySettings Current,
    ClaritySettings Proposed,
    ClarityJudgement Brightness,
    ClarityJudgement Fullness,
    double PresenceShortfallDb,
    double FullnessShortfallDb,
    double MiddleDeviationDb,
    IReadOnlyList<BandDeviation> Deviations)
{
    public bool ProposesBrightness => Brightness == ClarityJudgement.Proposed;
    public bool ProposesFullness => Fullness == ClarityJudgement.Proposed;

    /// <summary>True when the proposal changes nothing: "your voice needs no Clarity here" is a result.</summary>
    public bool IsNothing => !ProposesBrightness && !ProposesFullness;
}

/// <summary>
/// The proposal rule (Sprint 49 Track J, J3, #736): from where a voice's
/// long-term spectrum sits against <see cref="ReferenceSpeechSpectrum"/>
/// inside the transmit filter, propose JJ Clarity settings. Deterministic, a
/// pure function. It proposes ONLY Clarity settings (the default of question
/// 2; #152: "Leave TX EQ alone").
/// </summary>
/// <remarks>
/// <para><b>The comparison.</b> The bands both spectra cover inside the
/// passband are taken; each spectrum is normalised over exactly those bands;
/// the deviation of each band is the voice's level minus the reference's.
/// Three regions are read: the MIDDLE (500 Hz to 1.25 kHz), the PRESENCE
/// region (1.6 to 3.15 kHz, where #736's plan and the per-take analysis place
/// presence), and the FULLNESS region (160 to 315 Hz).</para>
/// <para><b>Brightness</b> (the high band): the presence shortfall is the
/// middle's deviation minus the presence region's, in dB, each region's
/// deviation taken in power (<see cref="RegionDeviationDb"/>). At or above
/// <see cref="PresenceThresholdDb"/> the high band is proposed on; its tune
/// is the centre of the lowest presence band that is short by the threshold;
/// its mix is the current mix (zero when the band is off) plus
/// <see cref="BrightnessMixPerDb"/> per dB of shortfall, between
/// <see cref="MixFloorPercent"/> and <see cref="BrightnessMixMostPercent"/>.
/// The drive and harmonic choice are kept when the band is already on, else
/// the defaults (12 dB, even). Adding to the current mix is what makes the
/// rule a convergence step (#152): a take made WITH Clarity already measures
/// it, and a remaining shortfall asks for more, never less.</para>
/// <para><b>Fullness</b> (the low band), the same with
/// <see cref="FullnessThresholdDb"/> and <see cref="FullnessMixPerDb"/>,
/// capped at <see cref="FullnessMixMostPercent"/>; tune 150 Hz unless the
/// band is already on.</para>
/// <para>Every number here is Track J's judgement (in the report, for Noel),
/// meant to be settled by the harness walk and his ears; the rule's SHAPE (a
/// shortfall against a cited reference, proposing more only when short) is
/// what the tests pin.</para>
/// </remarks>
public static class ClarityProposalRule
{
    public const int MiddleLowHz = 500, MiddleHighHz = 1250;
    public const int PresenceLowHz = 1600, PresenceHighHz = 3150;
    public const int FullnessLowHz = 160, FullnessHighHz = 315;

    public const double PresenceThresholdDb = 3.0;
    public const double FullnessThresholdDb = 4.0;
    public const double BrightnessMixPerDb = 4.0, FullnessMixPerDb = 3.0;
    public const float MixFloorPercent = 10f, BrightnessMixMostPercent = 60f, FullnessMixMostPercent = 50f;

    /// <summary>Propose from a measured spectrum. The spectrum must be available.</summary>
    public static ClarityProposal Propose(LongTermSpectrum spectrum, ClaritySettings current)
    {
        if (spectrum == null || !spectrum.Available) throw new ArgumentException("the spectrum is not available", nameof(spectrum));
        current ??= ClaritySettings.Default;

        var inside = spectrum.InsideBands.Where(b => ReferenceSpeechSpectrum.TryBandLevelDb(b.NominalHz, out _)).ToList();
        var reference = ReferenceSpeechSpectrum.ShapeOver(inside.Select(b => b.NominalHz));
        double total = inside.Sum(b => Math.Pow(10.0, b.PowerDbfs / 10.0));
        var deviations = inside.Select(b => new BandDeviation(b.NominalHz,
            b.PowerDbfs - 10.0 * Math.Log10(Math.Max(1e-30, total)), reference[b.NominalHz])).ToList();
        return Propose(deviations, current);
    }

    /// <summary>Propose from deviations already computed (the rule itself; also how the tests drive it with synthetic spectra).</summary>
    public static ClarityProposal Propose(IReadOnlyList<BandDeviation> deviations, ClaritySettings current)
    {
        current ??= ClaritySettings.Default;
        deviations ??= Array.Empty<BandDeviation>();
        var middle = deviations.Where(d => d.NominalHz >= MiddleLowHz && d.NominalHz <= MiddleHighHz).ToList();
        var presence = deviations.Where(d => d.NominalHz >= PresenceLowHz && d.NominalHz <= PresenceHighHz).OrderBy(d => d.NominalHz).ToList();
        var fullnessBands = deviations.Where(d => d.NominalHz >= FullnessLowHz && d.NominalHz <= FullnessHighHz).ToList();

        double middleMean = middle.Count > 0 ? RegionDeviationDb(middle) : double.NaN;
        var proposed = current;

        // Brightness.
        ClarityJudgement brightness;
        double presenceShortfall = double.NaN;
        if (middle.Count < 2 || presence.Count < 1) brightness = ClarityJudgement.RegionOutsidePassband;
        else
        {
            presenceShortfall = middleMean - RegionDeviationDb(presence);
            if (presenceShortfall < PresenceThresholdDb) brightness = ClarityJudgement.AlreadyEnough;
            else
            {
                var high = current.High;
                bool wasOn = !high.AddsNothing;
                var firstShort = presence.FirstOrDefault(d => middleMean - d.DeviationDb >= PresenceThresholdDb) ?? presence[0];
                float mix = (float)Math.Clamp((wasOn ? high.MixPercent : 0f) + presenceShortfall * BrightnessMixPerDb,
                    MixFloorPercent, BrightnessMixMostPercent);
                var band = new ClarityBand(true,
                    wasOn ? high.TuneHz : firstShort.NominalHz,
                    wasOn ? high.DriveDb : ClaritySettings.HighDriveDefaultDb,
                    mix,
                    wasOn ? high.Harmonics : ClarityHarmonics.Even);
                band = ClaritySettings.Normalize(band, isHigh: true);
                if (wasOn && band.Equals(high)) brightness = ClarityJudgement.AtTheRulesMost;
                else { brightness = ClarityJudgement.Proposed; proposed = proposed.WithHigh(band); }
            }
        }

        // Fullness.
        ClarityJudgement fullness;
        double fullnessShortfall = double.NaN;
        if (middle.Count < 2 || fullnessBands.Count < 1) fullness = ClarityJudgement.RegionOutsidePassband;
        else
        {
            fullnessShortfall = middleMean - RegionDeviationDb(fullnessBands);
            if (fullnessShortfall < FullnessThresholdDb) fullness = ClarityJudgement.AlreadyEnough;
            else
            {
                var low = current.Low;
                bool wasOn = !low.AddsNothing;
                float mix = (float)Math.Clamp((wasOn ? low.MixPercent : 0f) + fullnessShortfall * FullnessMixPerDb,
                    MixFloorPercent, FullnessMixMostPercent);
                var band = new ClarityBand(true,
                    wasOn ? low.TuneHz : ClaritySettings.LowTuneDefaultHz,
                    wasOn ? low.DriveDb : ClaritySettings.LowDriveDefaultDb,
                    mix,
                    wasOn ? low.Harmonics : ClarityHarmonics.Both);
                band = ClaritySettings.Normalize(band, isHigh: false);
                if (wasOn && band.Equals(low)) fullness = ClarityJudgement.AtTheRulesMost;
                else { fullness = ClarityJudgement.Proposed; proposed = proposed.WithLow(band); }
            }
        }

        return new ClarityProposal(current, proposed, brightness, fullness, presenceShortfall, fullnessShortfall,
            middleMean, deviations);
    }

    /// <summary>
    /// A region's deviation from the reference, dB: the voice's power in the
    /// region over the reference's, summed in POWER, not averaged in dB, so
    /// one near-empty band cannot drag a region tens of dB (a voice has gaps
    /// between its harmonics that a narrow band can fall into).
    /// </summary>
    public static double RegionDeviationDb(IReadOnlyCollection<BandDeviation> region)
    {
        double measured = 0, reference = 0;
        foreach (var d in region) { measured += Math.Pow(10.0, d.MeasuredDb / 10.0); reference += Math.Pow(10.0, d.ReferenceDb / 10.0); }
        return 10.0 * Math.Log10(Math.Max(1e-30, measured) / Math.Max(1e-30, reference));
    }
}
