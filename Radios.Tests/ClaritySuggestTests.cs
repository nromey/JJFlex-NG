#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JJPortaudio;
using Radios.Listenback;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests;

/// <summary>
/// Sprint 49 Track J (#736), J3: the suggest pass's analysis. The long-term
/// spectrum, the reference speech spectrum, the proposal rule, the peak guard
/// and the step. Pure functions on synthetic audio and synthetic takes written
/// to a temporary folder: no radio, no audio device, no window, no settings.
/// </summary>
public sealed class ClaritySuggestTests
{
    private readonly ITestOutputHelper _out;
    public ClaritySuggestTests(ITestOutputHelper output) { _out = output; }

    private static float[] Tones(int rate, double seconds, params (double Hz, double Amp)[] tones)
    {
        int n = (int)(rate * seconds);
        var x = new float[n];
        var r = new Random(7);
        var phase = tones.Select(_ => r.NextDouble() * 2 * Math.PI).ToArray();
        for (int i = 0; i < n; i++)
        {
            double v = 0;
            for (int t = 0; t < tones.Length; t++) v += tones[t].Amp * Math.Sin(phase[t] + 2 * Math.PI * tones[t].Hz * i / rate);
            x[i] = (float)v;
        }
        return x;
    }

    private static double PowerDb(double amp) => 10 * Math.Log10(amp * amp / 2);

    private static SpectrumBand Band(LongTermSpectrum s, int nominal) => s.Bands.Single(b => b.NominalHz == nominal);

    // ── the long-term spectrum ──

    [Theory]
    [InlineData(48000)]
    [InlineData(8000)]
    public void Tones_of_known_power_measure_to_it_in_their_bands(int rate)
    {
        var tones = new[] { (LongTermSpectrumMeter.CentreHz(5), 0.1), (LongTermSpectrumMeter.CentreHz(10), 0.05), (LongTermSpectrumMeter.CentreHz(14), 0.2) };
        var s = LongTermSpectrumMeter.Measure(Tones(rate, 2, tones), rate, 100, 3000);
        Assert.True(s.Available);
        foreach (var (hz, amp) in tones)
        {
            var b = s.Bands.Single(x => Math.Abs(x.CentreHz - hz) < 1);
            _out.WriteLine($"{rate} Hz, band {b.NominalHz}: measured {b.PowerDbfs:F2}, expected {PowerDb(amp):F2} dBFS");
            Assert.InRange(b.PowerDbfs - PowerDb(amp), -0.3, 0.3);
        }
        // Shape: the inside bands' shares sum to the whole.
        double sum = s.InsideBands.Sum(b => Math.Pow(10, b.LevelDb / 10));
        Assert.InRange(sum, 0.999, 1.001);
    }

    [Fact]
    public void A_take_longer_than_one_window_is_measured_over_all_its_speech_not_the_first_window()
    {
        // At 8 kHz: half a second of 500 Hz, then two seconds of 2 kHz.
        const int rate = 8000;
        var first = Tones(rate, 0.5, (500, 0.1));
        var then = Tones(rate, 2.0, (LongTermSpectrumMeter.CentreHz(13), 0.1));
        var x = first.Concat(then).ToArray();
        var s = LongTermSpectrumMeter.Measure(x, rate, 100, 3000);
        Assert.True(s.Available);
        double twoK = Band(s, 2000).PowerDbfs, fiveHundred = Band(s, 500).PowerDbfs;
        _out.WriteLine($"2 kHz band {twoK:F1} dBFS, 500 Hz band {fiveHundred:F1} dBFS over {s.FramesUsed} frames ({s.SecondsMeasured:F2} s)");
        Assert.True(twoK > fiveHundred + 3, "the later, longer part of the take was not measured");
        Assert.InRange(twoK, PowerDb(0.1) - 2, PowerDb(0.1));
        Assert.True(s.SecondsMeasured > 2.0);
    }

    [Fact]
    public void Only_the_keyed_spans_are_read()
    {
        const int rate = 8000;
        var x = Tones(rate, 3, (1000, 0.1));
        // Loud 2.5 kHz everywhere OUTSIDE the span from 0.5 s to 2.5 s.
        var outside = Tones(rate, 3, (LongTermSpectrumMeter.CentreHz(14), 0.5));
        for (int i = 0; i < x.Length; i++) if (i < rate / 2 || i >= rate * 5 / 2) x[i] += outside[i];
        var s = LongTermSpectrumMeter.Measure(x, new[] { new TakeSpan(rate / 2, rate * 5 / 2) }, rate, 100, 3000);
        Assert.True(Band(s, 2500).PowerDbfs < Band(s, 1000).PowerDbfs - 60, "audio outside the keyed span was measured");
    }

    [Fact]
    public void Pauses_are_left_out_so_hiss_between_words_does_not_tilt_the_shape()
    {
        const int rate = 8000;
        var speech = Tones(rate, 1, (1000, 0.2));
        // The word ends smoothly (50 ms), as speech does; an abrupt cut would splatter into every band.
        int fade = rate / 20;
        for (int i = 0; i < fade; i++) speech[speech.Length - fade + i] *= (float)(0.5 + 0.5 * Math.Cos(Math.PI * i / fade));
        // Hiss about 34 dB under the word in the passband: a pause by the gate's rule.
        var r = new Random(3);
        var hiss = Enumerable.Range(0, rate * 3).Select(_ => (float)(0.006 * (2 * r.NextDouble() - 1))).ToArray();
        var s = LongTermSpectrumMeter.Measure(speech.Concat(hiss).ToArray(), rate, 100, 3000);
        Assert.True(s.FramesUsed < s.FramesTotal / 2, $"{s.FramesUsed} of {s.FramesTotal} frames used");
        _out.WriteLine($"2.5 kHz band {Band(s, 2500).LevelDb:F1} dB of the in-passband total; {s.FramesUsed} of {s.FramesTotal} frames");
        // Averaged in, the hiss would put that band about 35 dB down.
        Assert.True(Band(s, 2500).LevelDb < -45, $"the 2.5 kHz band read {Band(s, 2500).LevelDb:F1} dB: the hiss was averaged in");
    }

    [Fact]
    public void A_band_counts_inside_the_filter_with_a_small_overhang_and_not_past_it()
    {
        int I(int nominal) => Array.IndexOf(LongTermSpectrumMeter.NominalCentresHz, nominal);
        Assert.True(LongTermSpectrumMeter.Inside(I(2500), 100, 2700));
        Assert.False(LongTermSpectrumMeter.Inside(I(3150), 100, 3000));
        Assert.False(LongTermSpectrumMeter.Inside(I(100), 100, 3000));
        Assert.True(LongTermSpectrumMeter.Inside(I(125), 100, 3000));
    }

    [Fact]
    public void It_refuses_too_little_speech_a_filter_holding_no_band_and_a_missing_filter_with_a_reason()
    {
        Assert.Equal(LongTermSpectrumRefusal.TooLittleSpeech, LongTermSpectrumMeter.Measure(Tones(8000, 0.2, (1000, 0.1)), 8000, 100, 3000).Refusal);
        Assert.Equal(LongTermSpectrumRefusal.PassbandHoldsNoBand, LongTermSpectrumMeter.Measure(Tones(8000, 1, (1000, 0.1)), 8000, 1000, 1100).Refusal);
        Assert.Equal(LongTermSpectrumRefusal.NoTransmitFilter, LongTermSpectrumMeter.Measure(Tones(8000, 1, (1000, 0.1)), 8000, 3000, 100).Refusal);
    }

    // ── the reference ──

    [Fact]
    public void The_reference_is_the_ANSI_standard_speech_spectrum_converted_to_band_levels()
    {
        Assert.Equal(18, ReferenceSpeechSpectrum.SpectrumLevelDb.Length);
        Assert.Equal(32.41, ReferenceSpeechSpectrum.SpectrumLevelDb[0]);
        Assert.Equal(25.01, ReferenceSpeechSpectrum.SpectrumLevelDb[Array.IndexOf(ReferenceSpeechSpectrum.NominalCentresHz, 1000)]);
        Assert.Equal(1.13, ReferenceSpeechSpectrum.SpectrumLevelDb[^1]);
        // 1 kHz: 25.01 dB per hertz over a 230.8 Hz band.
        Assert.InRange(ReferenceSpeechSpectrum.BandLevelDb(1000), 25.01 + 23.62, 25.01 + 23.64);
        Assert.False(ReferenceSpeechSpectrum.TryBandLevelDb(125, out _));
        var shape = ReferenceSpeechSpectrum.ShapeOver(new[] { 125, 500, 1000, 2000 });
        Assert.Equal(3, shape.Count);
        Assert.InRange(shape.Values.Sum(v => Math.Pow(10, v / 10)), 0.999, 1.001);
    }

    // ── the proposal rule ──

    private static IReadOnlyList<BandDeviation> Deviations(Func<int, double> deviation, int lowest = 160, int highest = 2500) =>
        ReferenceSpeechSpectrum.NominalCentresHz.Where(hz => hz >= lowest && hz <= highest)
            .Select(hz => new BandDeviation(hz, deviation(hz), 0)).ToList();

    [Fact]
    public void A_flat_voice_gets_nothing_and_says_so()
    {
        var p = ClarityProposalRule.Propose(Deviations(_ => 0), ClaritySettings.Default);
        Assert.True(p.IsNothing);
        Assert.Equal(ClarityJudgement.AlreadyEnough, p.Brightness);
        Assert.Equal(ClarityJudgement.AlreadyEnough, p.Fullness);
        Assert.Equal(ClaritySettings.Default, p.Proposed);
    }

    [Fact]
    public void A_bright_voice_gets_nothing()
    {
        var p = ClarityProposalRule.Propose(Deviations(hz => hz >= 1600 ? 4 : 0), ClaritySettings.Default);
        Assert.Equal(ClarityJudgement.AlreadyEnough, p.Brightness);
        Assert.True(p.PresenceShortfallDb < 0);
        Assert.True(p.IsNothing);
    }

    [Fact]
    public void A_voice_short_of_presence_gets_a_brightness_proposal_scaled_by_the_shortfall()
    {
        var off = ClaritySettings.Default.WithHigh(ClaritySettings.Default.High.WithEnabled(false));
        var p = ClarityProposalRule.Propose(Deviations(hz => hz >= 1600 ? -6 : 0), off);
        Assert.Equal(ClarityJudgement.Proposed, p.Brightness);
        Assert.Equal(ClarityJudgement.AlreadyEnough, p.Fullness);
        Assert.InRange(p.PresenceShortfallDb, 5.99, 6.01);
        Assert.True(p.Proposed.High.Enabled);
        Assert.Equal(1600f, p.Proposed.High.TuneHz);
        Assert.Equal(ClaritySettings.HighDriveDefaultDb, p.Proposed.High.DriveDb);
        Assert.Equal(24f, p.Proposed.High.MixPercent, 3);
        Assert.Equal(ClarityHarmonics.Even, p.Proposed.High.Harmonics);
        Assert.Equal(off.Low, p.Proposed.Low);
    }

    [Fact]
    public void A_take_made_with_Clarity_on_asks_for_more_only_while_short_and_stops_at_the_rules_most()
    {
        var on = ClaritySettings.Default.WithHigh(new ClarityBand(true, 2000, 9, 20, ClarityHarmonics.Both));
        var p = ClarityProposalRule.Propose(Deviations(hz => hz >= 1600 ? -4 : 0), on);
        Assert.Equal(ClarityJudgement.Proposed, p.Brightness);
        Assert.Equal(36f, p.Proposed.High.MixPercent, 3);
        Assert.Equal(2000f, p.Proposed.High.TuneHz);   // kept
        Assert.Equal(9f, p.Proposed.High.DriveDb);      // kept
        Assert.Equal(ClarityHarmonics.Both, p.Proposed.High.Harmonics);
        var most = on.WithHigh(on.High.WithMix(ClarityProposalRule.BrightnessMixMostPercent));
        Assert.Equal(ClarityJudgement.AtTheRulesMost, ClarityProposalRule.Propose(Deviations(hz => hz >= 1600 ? -5 : 0), most).Brightness);
    }

    [Theory]
    [InlineData(60f, 50f)]   // exactly at each cap
    [InlineData(60.5f, 50.5f)]
    [InlineData(90f, 80f)]   // well above each cap
    [InlineData(100f, 100f)]
    public void A_band_at_or_above_the_rules_most_is_left_exactly_as_the_operator_set_it(float highMix, float lowMix)
    {
        // Repair 1, B1: the cap limits what the rule offers; it never lowers a stronger choice.
        var current = new ClaritySettings(new ClarityBand(true, 2000, 9, highMix, ClarityHarmonics.Odd),
            new ClarityBand(true, 120, 6, lowMix, ClarityHarmonics.Even));
        var p = ClarityProposalRule.Propose(Deviations(hz => hz >= 1600 || hz <= 315 ? -6 : 0), current);
        Assert.Equal(ClarityJudgement.AtTheRulesMost, p.Brightness);
        Assert.Equal(ClarityJudgement.AtTheRulesMost, p.Fullness);
        Assert.True(p.IsNothing);
        Assert.Equal(current, p.Proposed);
    }

    [Fact]
    public void A_voice_short_of_low_body_gets_a_fullness_proposal()
    {
        var p = ClarityProposalRule.Propose(Deviations(hz => hz <= 315 ? -6 : 0), ClaritySettings.Default);
        Assert.Equal(ClarityJudgement.Proposed, p.Fullness);
        Assert.Equal(ClarityJudgement.AlreadyEnough, p.Brightness);
        Assert.True(p.Proposed.Low.Enabled);
        Assert.Equal(ClaritySettings.LowTuneDefaultHz, p.Proposed.Low.TuneHz);
        Assert.Equal(18f, p.Proposed.Low.MixPercent, 3);
    }

    [Fact]
    public void One_near_empty_band_does_not_drag_its_region_and_hide_a_shortfall()
    {
        // The 630 Hz band fell between harmonics; presence is 5 dB short.
        var p = ClarityProposalRule.Propose(Deviations(hz => hz == 630 ? -70 : hz >= 1600 ? -5 : 0), ClaritySettings.Default);
        _out.WriteLine($"middle {p.MiddleDeviationDb:F2} dB, presence shortfall {p.PresenceShortfallDb:F2} dB");
        Assert.InRange(p.MiddleDeviationDb, -1.0, -0.9);   // four bands at 0 dB and one empty: 10 log(4/5)
        Assert.Equal(ClarityJudgement.Proposed, p.Brightness);
    }

    [Fact]
    public void A_filter_too_narrow_to_hold_a_region_cannot_be_judged_there()
    {
        var p = ClarityProposalRule.Propose(Deviations(_ => -10, 400, 1250), ClaritySettings.Default);
        Assert.Equal(ClarityJudgement.RegionOutsidePassband, p.Brightness);
        Assert.Equal(ClarityJudgement.RegionOutsidePassband, p.Fullness);
        Assert.True(p.IsNothing);
    }

    /// <summary>A synthetic voice: one tone per band at the reference's band level, with a region moved.</summary>
    private static float[] ReferenceShapedVoice(int rate, Func<int, double> moveDb)
    {
        var tones = new List<(double, double)>();
        foreach (int hz in ReferenceSpeechSpectrum.NominalCentresHz.Where(h => h <= 2500))
        {
            int idx = Array.IndexOf(LongTermSpectrumMeter.NominalCentresHz, hz);
            double power = Math.Pow(10, (ReferenceSpeechSpectrum.BandLevelDb(hz) - 90 + moveDb(hz)) / 10);
            tones.Add((LongTermSpectrumMeter.CentreHz(idx), Math.Sqrt(2 * power)));
        }
        return Tones(rate, 3, tones.ToArray());
    }

    [Fact]
    public void End_to_end_a_measured_voice_shaped_like_the_reference_gets_nothing_and_one_with_presence_cut_gets_brightness()
    {
        const int rate = 16000;
        var flat = ClarityProposalRule.Propose(LongTermSpectrumMeter.Measure(ReferenceShapedVoice(rate, _ => 0), rate, 100, 2900), ClaritySettings.Default);
        _out.WriteLine($"reference-shaped: presence shortfall {flat.PresenceShortfallDb:F2} dB, fullness {flat.FullnessShortfallDb:F2} dB");
        Assert.True(flat.IsNothing);
        Assert.InRange(flat.PresenceShortfallDb, -0.5, 0.5);
        var dull = ClarityProposalRule.Propose(LongTermSpectrumMeter.Measure(ReferenceShapedVoice(rate, hz => hz >= 1600 ? -8 : 0), rate, 100, 2900), ClaritySettings.Default);
        _out.WriteLine($"presence cut 8 dB: shortfall {dull.PresenceShortfallDb:F2} dB, proposes {dull.Proposed.High}");
        Assert.Equal(ClarityJudgement.Proposed, dull.Brightness);
        Assert.InRange(dull.PresenceShortfallDb, 7.5, 8.5);
    }

    // ── the peak guard ──

    private static ClarityProposal Hot(ClaritySettings current) => ClarityProposalRule.Propose(
        Deviations(hz => hz >= 1600 ? -12 : 0),
        current.WithHigh(new ClarityBand(true, 2000, 24, 50, ClarityHarmonics.Even))) is var p && p.ProposesBrightness
        ? p with { Proposed = p.Proposed.WithHigh(p.Proposed.High.WithDrive(24).WithMix(60)) }
        : throw new InvalidOperationException("the fixture did not propose");

    [Fact]
    public void A_proposal_that_would_push_the_operators_audio_over_full_scale_is_reduced_until_it_does_not()
    {
        const int rate = 48000;
        var off = ClaritySettings.Default.WithHigh(ClaritySettings.Default.High.WithEnabled(false));
        var before = Tones(rate, 1, (1200, 0.6), (700, 0.35));
        var proposal = Hot(off) with { Current = off };
        // The positive control: unguarded, the proposal does push the audio over.
        long unguarded = ClarityPeakGuard.CountOver(ClarityPeakGuard.RenderClarityOnly(before, rate, proposal.Proposed, 100, 2900));
        Assert.True(unguarded > 0, "the fixture never went over full scale");
        var g = ClarityPeakGuard.Apply(proposal, before, rate, 100, 2900);
        _out.WriteLine($"unguarded {unguarded} over; guarded after {g.Steps} steps: {g.OverFullScaleAfter} over, {g.Proposal.Proposed.High}");
        Assert.True(g.Reduced);
        Assert.Equal(0, g.OverFullScaleBefore);
        Assert.True(g.OverFullScaleAfter <= g.OverFullScaleBefore);
        Assert.Equal(0, ClarityPeakGuard.CountOver(ClarityPeakGuard.RenderClarityOnly(before, rate, g.Proposal.Proposed, 100, 2900)));
        Assert.True(g.Proposal.Proposed.High.MixPercent < proposal.Proposed.High.MixPercent);
    }

    [Fact]
    public void A_proposal_is_not_let_raise_the_peak_past_the_limiters_ceiling()
    {
        const int rate = 48000;
        var off = ClaritySettings.Default.WithHigh(ClaritySettings.Default.High.WithEnabled(false));
        var before = Tones(rate, 1, (1200, 0.3), (700, 0.2));
        var proposal = Hot(off) with { Current = off };
        var g = ClarityPeakGuard.Apply(proposal, before, rate, 100, 2900, limiterCeilingDbfs: -5);
        _out.WriteLine($"before peak {g.PeakBeforeDbfs:F1}, after {g.PeakAfterDbfs:F1} dBFS in {g.Steps} steps");
        Assert.True(g.PeakBeforeDbfs < -5);
        Assert.True(g.Reduced);
        Assert.True(g.PeakAfterDbfs <= -5);
        // The positive control: unguarded, the proposal does raise the peak past -5 dBFS.
        Assert.True(ClarityPeakGuard.PeakDbfs(ClarityPeakGuard.RenderClarityOnly(before, rate, proposal.Proposed, 100, 2900)) > -5);
        // Without a ceiling the same proposal is not reduced (nothing goes over full scale).
        Assert.False(ClarityPeakGuard.Apply(proposal, before, rate, 100, 2900).Reduced);
    }

    [Fact]
    public void The_guard_shrinks_only_the_increase_and_never_goes_below_the_operators_current_setting()
    {
        // Codex's case (2026-11-24): current high band 10 %, drive 24 dB; a 4 dB shortfall proposes 26 %.
        const int rate = 48000;
        var current = ClaritySettings.Default.WithHigh(new ClarityBand(true, 2000, 24, 10, ClarityHarmonics.Even));
        var proposal = ClarityProposalRule.Propose(Deviations(hz => hz >= 1600 ? -4 : 0), current);
        Assert.Equal(26f, proposal.Proposed.High.MixPercent, 3);
        var before = Tones(rate, 1, (1200, 0.63), (700, 0.3675));
        var g = ClarityPeakGuard.Apply(proposal, before, rate, 100, 2900);
        _out.WriteLine($"after {g.Steps} steps: withdrawn {g.Withdrawn}, {g.Proposal.Proposed.High}; overs {g.OverFullScaleBefore} before, {g.OverFullScaleAfter} after");
        Assert.True(g.Reduced);
        Assert.True(g.Proposal.Proposed.High.MixPercent >= current.High.MixPercent, "the guard went below the operator's mix");
        Assert.True(g.Proposal.Proposed.High.DriveDb >= current.High.DriveDb, "the guard went below the operator's drive");
        if (g.Withdrawn)
        {
            Assert.Equal(current, g.Proposal.Proposed);
            Assert.Equal(ClarityJudgement.WithdrawnByPeakGuard, g.Proposal.Brightness);
        }
        else Assert.True(g.OverFullScaleAfter <= g.OverFullScaleBefore);
    }

    // ── repair 2: an off or zero-mix band's stored drive and harmonics are the operator's ──

    /// <summary>The four inactive starting points: high or low band, switched off or on at zero mix, each holding a stored drive and harmonics unlike the rule's defaults.</summary>
    public static TheoryData<bool, bool> InactiveBands() => new() { { true, false }, { true, true }, { false, false }, { false, true } };

    private const float StoredDrive = 24f;

    private static ClaritySettings Inactive(bool high, bool enabledAtZero)
    {
        var d = ClaritySettings.Default;
        return high
            ? new ClaritySettings(new ClarityBand(enabledAtZero, 2000, StoredDrive, enabledAtZero ? 0 : 25, ClarityHarmonics.Odd), d.Low.WithEnabled(false))
            : new ClaritySettings(d.High.WithEnabled(false), new ClarityBand(enabledAtZero, 120, StoredDrive, enabledAtZero ? 0 : 25, ClarityHarmonics.Even));
    }

    private static ClarityProposal ProposeFor(bool high, ClaritySettings current) =>
        ClarityProposalRule.Propose(Deviations(hz => (high ? hz >= 1600 : hz <= 315) ? -10 : 0), current);

    /// <summary>
    /// A controlled renderer (the guard's seam): any band adding more than
    /// <paramref name="mostMix"/> percent makes five samples over full scale.
    /// It records the lowest drive it was ever asked to render, per band.
    /// </summary>
    private sealed class Renderer
    {
        private readonly float _mostMix;
        public float LowestHighDrive = float.MaxValue, LowestLowDrive = float.MaxValue;
        public Renderer(float mostMix) { _mostMix = mostMix; }
        public float[] Render(ClaritySettings s)
        {
            if (!s.High.AddsNothing) LowestHighDrive = Math.Min(LowestHighDrive, s.High.DriveDb);
            if (!s.Low.AddsNothing) LowestLowDrive = Math.Min(LowestLowDrive, s.Low.DriveDb);
            float mix = Math.Max(s.High.AddsNothing ? 0 : s.High.MixPercent, s.Low.AddsNothing ? 0 : s.Low.MixPercent);
            var x = new float[1000];
            if (mix > _mostMix) for (int i = 0; i < 5; i++) x[i] = 1.5f;
            return x;
        }
    }

    [Theory]
    [MemberData(nameof(InactiveBands))]
    public void An_inactive_band_is_proposed_on_with_its_stored_drive_and_harmonics(bool high, bool enabledAtZero)
    {
        var current = Inactive(high, enabledAtZero);
        var p = ProposeFor(high, current);
        var was = high ? current.High : current.Low;
        var band = high ? p.Proposed.High : p.Proposed.Low;
        Assert.True(high ? p.ProposesBrightness : p.ProposesFullness);
        Assert.True(band.Enabled);
        Assert.Equal(StoredDrive, band.DriveDb);
        Assert.Equal(was.Harmonics, band.Harmonics);
        Assert.Equal(high ? 40f : 30f, band.MixPercent, 3);   // a rise from an effective zero
    }

    [Theory]
    [MemberData(nameof(InactiveBands))]
    public void An_inactive_band_accepted_at_once_keeps_its_stored_drive(bool high, bool enabledAtZero)
    {
        var current = Inactive(high, enabledAtZero);
        var r = new Renderer(mostMix: 100);
        var g = ClarityPeakGuard.Apply(ProposeFor(high, current), new float[1000], 48000, 100, 2900, render: r.Render);
        Assert.Equal(0, g.Steps);
        Assert.False(g.Reduced);
        Assert.Equal(StoredDrive, (high ? g.Proposal.Proposed.High : g.Proposal.Proposed.Low).DriveDb);
        Assert.Equal(StoredDrive, high ? r.LowestHighDrive : r.LowestLowDrive);
    }

    [Theory]
    [MemberData(nameof(InactiveBands))]
    public void An_inactive_band_reduced_to_pass_keeps_its_stored_drive_and_shrinks_only_its_mix(bool high, bool enabledAtZero)
    {
        var current = Inactive(high, enabledAtZero);
        var r = new Renderer(mostMix: 20);    // 40 or 30 percent goes over, and so does the first step (28, 21): two steps, the second a drive step
        var g = ClarityPeakGuard.Apply(ProposeFor(high, current), new float[1000], 48000, 100, 2900, render: r.Render);
        var band = high ? g.Proposal.Proposed.High : g.Proposal.Proposed.Low;
        Assert.True(g.Reduced);
        Assert.False(g.Withdrawn);
        Assert.True(band.Enabled);
        Assert.Equal(StoredDrive, band.DriveDb);
        Assert.True(g.Steps >= 2, "the reduction never reached a drive step");
        Assert.True(band.MixPercent <= 20 && band.MixPercent > 0);
        Assert.Equal(StoredDrive, high ? r.LowestHighDrive : r.LowestLowDrive);
    }

    [Theory]
    [MemberData(nameof(InactiveBands))]
    public void A_proposal_below_the_stored_drive_is_lifted_to_it_before_the_first_render(bool high, bool enabledAtZero)
    {
        // The step-zero check: a proposal made the way the rule used to make it (the default drive, 12 or 6 dB).
        var current = Inactive(high, enabledAtZero);
        var p = ProposeFor(high, current);
        p = p with { Proposed = high ? p.Proposed.WithHigh(p.Proposed.High.WithDrive(12)) : p.Proposed.WithLow(p.Proposed.Low.WithDrive(6)) };
        var r = new Renderer(mostMix: 100);
        var g = ClarityPeakGuard.Apply(p, new float[1000], 48000, 100, 2900, render: r.Render);
        Assert.Equal(0, g.Steps);
        Assert.Equal(StoredDrive, (high ? g.Proposal.Proposed.High : g.Proposal.Proposed.Low).DriveDb);
        Assert.Equal(StoredDrive, high ? r.LowestHighDrive : r.LowestLowDrive);
    }

    [Theory]
    [MemberData(nameof(InactiveBands))]
    public void An_inactive_band_no_reduction_can_pass_is_withdrawn_and_its_settings_restored_exactly(bool high, bool enabledAtZero)
    {
        var current = Inactive(high, enabledAtZero);
        var r = new Renderer(mostMix: 0);     // anything added goes over
        var g = ClarityPeakGuard.Apply(ProposeFor(high, current), new float[1000], 48000, 100, 2900, render: r.Render);
        Assert.True(g.Withdrawn);
        Assert.Equal(current, g.Proposal.Proposed);
        Assert.Equal(StoredDrive, high ? r.LowestHighDrive : r.LowestLowDrive);
    }

    [Theory]
    [InlineData(12f)]
    [InlineData(24f)]
    public void Codex_case_through_the_public_road_a_disabled_high_band_keeps_its_stored_drive(float stored)
    {
        // Codex's recheck (2026-11-24): a disabled high band holding tune 2 kHz, mix 25 percent, drive 12 or 24 dB;
        // a reference-shaped voice with presence 10 dB down; retained 1.2 kHz at 0.6 and 700 Hz at 0.35.
        const int rate = 48000;
        var current = ClaritySettings.Default.WithHigh(new ClarityBand(false, 2000, stored, 25, ClarityHarmonics.Even));
        var retained = Tones(rate, 1, (1200, 0.6), (700, 0.35));
        var r = ClaritySuggest.FromAudio(ReferenceShapedVoice(rate, hz => hz >= 1600 ? -10 : 0), null, rate, 100, 2900,
            ClaritySuggestSource.RadioRecorderStandIn, null, new ClaritySuggestInput(current, retained, rate));
        Assert.True(r.GuardRan);
        Assert.Equal(stored, r.Proposal.Proposed.High.DriveDb);
        var g = r.Guard!;
        _out.WriteLine($"stored {stored} dB: {g.Steps} steps, withdrawn {g.Withdrawn}, offered {g.Proposal.Proposed.High}");
        Assert.True(g.Proposal.Proposed.High.DriveDb >= stored, "the guard went below the stored drive");
        if (g.Withdrawn) Assert.Equal(current, g.Proposal.Proposed);
        else Assert.True(g.OverFullScaleAfter <= g.OverFullScaleBefore);
        if (r.TryGetGuardedOffer(out var offer)) Assert.True(offer!.Proposed.High.DriveDb >= stored);
    }

    [Fact]
    public void A_proposal_no_reduction_can_save_is_withdrawn_and_says_why()
    {
        const int rate = 48000;
        var off = ClaritySettings.Default.WithHigh(ClaritySettings.Default.High.WithEnabled(false));
        // Already at full scale, with flat tops: anything added, of either sign, goes over somewhere on them.
        var before = Tones(rate, 1, (1200, 3.0)).Select(v => Math.Clamp(v, -0.9999f, 0.9999f)).ToArray();
        var proposal = Hot(off) with { Current = off };
        var g = ClarityPeakGuard.Apply(proposal, before, rate, 100, 2900);
        Assert.True(g.Withdrawn);
        Assert.True(g.Proposal.IsNothing);
        Assert.Equal(ClarityJudgement.WithdrawnByPeakGuard, g.Proposal.Brightness);
        Assert.Equal(off, g.Proposal.Proposed);
    }

    // ── the step ──

    private const int TakeRate = 8000;

    /// <summary>A synthetic off-air take, the way the listen-back tests write one: a USB voice of tones, keyed throughout, with one gap.</summary>
    private static string Take(string root, string mode = "USB", double presenceDb = 0)
    {
        string directory = Path.Combine(root, "take");
        Directory.CreateDirectory(directory);
        int samples = TakeRate * 3;
        var hz = new[] { 200.0, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500 };
        string iq = Path.Combine(directory, "rf.iq");
        using (var writer = new BinaryWriter(File.Create(iq)))
            for (int i = 0; i < samples; i++)
            {
                double re = 0, im = 0;
                foreach (double nominal in hz)
                {
                    // Shaped like the reference speech spectrum, then the presence region moved.
                    int band = Array.IndexOf(LongTermSpectrumMeter.NominalCentresHz, (int)nominal);
                    double f = LongTermSpectrumMeter.CentreHz(band);
                    double a = 0.05 * Math.Pow(10, (ReferenceSpeechSpectrum.BandLevelDb((int)nominal) - ReferenceSpeechSpectrum.BandLevelDb(500)) / 20)
                        * (nominal >= 1600 ? Math.Pow(10, presenceDb / 20) : 1);
                    re += a * Math.Cos(2 * Math.PI * f * i / TakeRate);
                    im += a * Math.Sin(2 * Math.PI * f * i / TakeRate);
                }
                writer.Write((float)(32768 * re)); writer.Write((float)(32768 * im));
            }
        using (var header = new TakeHeader(Path.Combine(directory, "take.txt")))
        {
            var facts = new Dictionary<string, string> {
                ["serial"] = "test-radio", ["mode"] = mode, ["rate-echoed"] = TakeRate.ToString(),
                ["receive-antenna"] = "XVT-B", ["rf-gain"] = "32", ["tx-antenna"] = "XVT-A", ["rf-power"] = "1",
                ["tx-frequency-mhz"] = "14.2", ["pan-centre-mhz"] = "14.2", ["tx-filter-low"] = "100", ["tx-filter-high"] = "2900",
                ["dummy-load"] = "True", ["mic-input"] = "PC", ["mic-profile"] = "Default", ["reference-audio"] = "True",
                ["processor"] = "True", ["processor-level"] = "50", ["compander"] = "False", ["compander-level"] = "30", ["mic-gain"] = "42",
                ["render-scale"] = "1/32768; fixed; no normalization; no AGC", ["classifier"] = "noise-like",
            };
            foreach (var fact in facts) header.Write(fact.Key, fact.Value);
            header.Event("keying", new { Source = "radio-TransmitChange", Keyed = true, AtPair = 0 });
            header.Event("gap", new { AtPair = 12000, FillPairs = 800 });
            header.Event("keying", new { Source = "radio-TransmitChange", Keyed = false, AtPair = samples });
            header.Write("pairs-written", samples);
            header.Write("sha256", TakeHeader.Checksum(iq));
            header.Write("closed", "clean");
        }
        return directory;
    }

    private static string Root() => Path.Combine(Path.GetTempPath(), "jjf-clarity-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_step_measures_an_off_air_take_says_what_the_radio_was_doing_and_proposes()
    {
        string root = Root();
        try
        {
            var r = ClaritySuggest.FromTake(Take(root, presenceDb: -10), new ClaritySuggestInput(ClaritySettings.Default.WithHigh(ClaritySettings.Default.High.WithEnabled(false))));
            Assert.True(r.Available);
            foreach (var d in r.Proposal.Deviations) _out.WriteLine($"band {d.NominalHz}: {d.DeviationDb:F1} dB");
            _out.WriteLine($"presence shortfall {r.Proposal.PresenceShortfallDb:F1} dB");
            Assert.Equal(ClaritySuggestSource.OffAirTake, r.Source);
            Assert.Equal("True", r.Radio.Processor);
            Assert.Equal("50", r.Radio.ProcessorLevel);
            Assert.Equal("False", r.Radio.Compander);
            Assert.Equal("30", r.Radio.CompanderLevel);
            Assert.Equal("42", r.Radio.MicGain);
            Assert.Equal(100, r.Spectrum.PassbandLowHz);
            Assert.Equal(2900, r.Spectrum.PassbandHighHz);
            Assert.Equal(ClarityJudgement.Proposed, r.Proposal.Brightness);
            Assert.False(r.GuardRan);           // no retained audio: the guard did not run, and the result says so
            // Nothing keepable without the guard (repair 1, B2): the raw proposal is for analysis only.
            Assert.False(r.CanKeep);
            Assert.False(r.TryGetGuardedOffer(out var offer));
            Assert.Null(offer);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Given_the_operators_retained_audio_the_step_guards_its_proposal()
    {
        string root = Root();
        try
        {
            var before = Tones(48000, 1, (1200, 0.3), (700, 0.2));
            var off = ClaritySettings.Default.WithHigh(ClaritySettings.Default.High.WithEnabled(false));
            var r = ClaritySuggest.FromTake(Take(root, presenceDb: -20), new ClaritySuggestInput(off, before, 48000));
            Assert.True(r.GuardRan);
            // With the guard run, there is a keepable offer, and it is the guarded proposal.
            Assert.True(r.CanKeep);
            Assert.True(r.TryGetGuardedOffer(out var offer));
            Assert.Same(r.Guard!.Proposal, offer);
            Assert.Equal(0, ClarityPeakGuard.CountOver(ClarityPeakGuard.RenderClarityOnly(before, 48000, offer!.Proposed, 100, 2900)));
            // The render is a seam: a supplied renderer is the one used.
            int calls = 0;
            var seam = ClaritySuggest.FromTake(Path.Combine(root, "take"), new ClaritySuggestInput(off, before, 48000,
                Render: s => { calls++; return before; }), reuseRender: true);
            Assert.True(calls >= 2);
            Assert.True(seam.GuardRan);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void The_step_refuses_with_its_reason_for_no_take_and_for_a_mode_with_no_speech_render()
    {
        string root = Root();
        try
        {
            Assert.Equal(ClaritySuggestRefusal.NoTake, ClaritySuggest.FromTake(Path.Combine(root, "nothing-here"), new ClaritySuggestInput(ClaritySettings.Default)).Refusal);
            var am = ClaritySuggest.FromTake(Take(root, mode: "AM"), new ClaritySuggestInput(ClaritySettings.Default));
            Assert.False(am.Available);
            Assert.Equal(ClaritySuggestRefusal.NoSpeechRender, am.Refusal);
            Assert.Null(am.Proposal);
            Assert.Equal("True", am.Radio.Processor); // what the radio was doing is still known
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void The_radios_own_recorder_stands_in_labelled_as_such()
    {
        const int rate = 24000;
        var r = ClaritySuggest.FromAudio(ReferenceShapedVoice(rate, hz => hz >= 1600 ? -8 : 0), null, rate, 100, 2900,
            ClaritySuggestSource.RadioRecorderStandIn, null, new ClaritySuggestInput(ClaritySettings.Default));
        Assert.True(r.Available);
        Assert.Equal(ClaritySuggestSource.RadioRecorderStandIn, r.Source);
        Assert.Same(RadioProcessingFacts.Unknown, r.Radio);
        Assert.Equal(ClaritySuggestRefusal.NoTake, ClaritySuggest.FromAudio(Array.Empty<float>(), null, rate, 100, 2900,
            ClaritySuggestSource.RadioRecorderStandIn, null, new ClaritySuggestInput(ClaritySettings.Default)).Refusal);
    }
}
