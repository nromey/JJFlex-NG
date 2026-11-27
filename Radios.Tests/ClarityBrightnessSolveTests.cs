#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using JJPortaudio;
using Radios.Listenback;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests;

/// <summary>
/// The suggest pass's brightness solve (#736, ruled 2026-11-26): the lift
/// model built from two renders of the operator's own recording, the solve
/// for drive and mix, the partial offer, the peak guard over it, and the
/// predicted lift checked against a REAL render measured by the pass's own
/// meter. Pure functions on synthetic audio: no radio, no device, no window.
/// </summary>
public sealed class ClarityBrightnessSolveTests
{
    private readonly ITestOutputHelper _out;
    public ClarityBrightnessSolveTests(ITestOutputHelper output) { _out = output; }

    private const int Rate = 48000;
    private const float Lo = 100, Hi = 2900;
    private static readonly int[] Region = { 1600, 2000, 2500 };
    private static readonly int[] Middle = { 500, 630, 800, 1000, 1250 };

    /// <summary>The largest gap allowed between the predicted lift and a real render's, dB, each render measured and gated on its own.</summary>
    private const double PredictionToleranceDb = 0.1;

    /// <summary>
    /// A voice-like signal: the harmonics of a fundamental gliding around
    /// <paramref name="f0"/>, falling 6 dB an octave above 500 Hz, the region
    /// above 1.5 kHz a further <paramref name="presenceDb"/> down, under a
    /// syllable envelope (four a second, with short pauses).
    /// </summary>
    private static float[] Voice(double f0 = 140, double presenceDb = -8, double peak = 0.3, double seconds = 4, int seed = 5)
    {
        int n = (int)(Rate * seconds);
        var x = new float[n];
        var r = new Random(seed);
        int harmonics = (int)(3400 / f0);
        var phase = Enumerable.Range(0, harmonics + 1).Select(_ => r.NextDouble() * 2 * Math.PI).ToArray();
        double theta = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            double f = f0 * (1 + 0.06 * Math.Sin(2 * Math.PI * 0.7 * t));
            theta += 2 * Math.PI * f / Rate;
            double syllable = Math.Max(0, Math.Sin(Math.PI * ((t * 4) % 1)));
            syllable = (t * 4) % 1 > 0.85 ? 0 : Math.Pow(syllable, 0.5);
            double v = 0;
            for (int h = 1; h <= harmonics; h++)
            {
                double hz = h * f0;
                double a = hz <= 500 ? 1 : 500 / hz;
                if (hz >= 1500) a *= Math.Pow(10, presenceDb / 20);
                v += a * Math.Sin(h * theta + phase[h]);
            }
            x[i] = (float)(v * syllable);
        }
        double most = x.Max(v => Math.Abs(v));
        for (int i = 0; i < n; i++) x[i] = (float)(x[i] * peak / most);
        return x;
    }

    private static float[] Render(float[] x, ClaritySettings s) => ClarityPeakGuard.RenderClarityOnly(x, Rate, s, Lo, Hi);

    /// <summary>The lift as the suggest pass's meter sees it: each render measured and gated on its own, the region's change minus the middle's.</summary>
    private static double MeterLift(float[] before, float[] after)
    {
        var a = LongTermSpectrumMeter.Measure(before, Rate, Lo, Hi);
        var b = LongTermSpectrumMeter.Measure(after, Rate, Lo, Hi);
        double R(LongTermSpectrum s, int[] set) => s.Bands.Where(x => set.Contains(x.NominalHz)).Sum(x => Math.Pow(10, x.PowerDbfs / 10));
        return 10 * Math.Log10(R(b, Region) / R(a, Region)) - 10 * Math.Log10(R(b, Middle) / R(a, Middle));
    }

    private static readonly ClaritySettings Off = ClaritySettings.Default.WithHigh(ClaritySettings.Default.High.WithEnabled(false));

    private static ClarityLiftModel Model(float[] x, ClaritySettings current, ClarityBand shape)
    {
        var full = current.WithHigh(new ClarityBand(true, shape.TuneHz, 24, 100, shape.Harmonics));
        return ClarityLiftModel.Build(Render(x, current), Render(x, full), ClarityLiftModel.Weight(current.High), Rate, Lo, Hi)
            ?? throw new InvalidOperationException("no model");
    }

    // ── the model ──

    public static TheoryData<string> Voices() => new() { "voice", "dull voice", "two tones", "low voice" };

    private static float[] VoiceNamed(string name) => name switch
    {
        "voice" => Voice(),
        "dull voice" => Voice(presenceDb: -16),
        "low voice" => Voice(f0: 95, presenceDb: -4),
        // The surface tests' retained take: 1.2 kHz and 700 Hz, half a second on and off.
        _ => Enumerable.Range(0, Rate * 6).Select(i => (float)(((i / (Rate / 2)) % 2 == 0 ? 0.3 : 0.0009)
            * (0.6 * Math.Sin(2 * Math.PI * 1200 * i / (double)Rate) + 0.35 * Math.Sin(2 * Math.PI * 700 * i / (double)Rate)))).ToArray(),
    };

    [Theory]
    [MemberData(nameof(Voices))]
    public void The_lift_one_pair_of_renders_predicts_matches_a_real_render_of_each_setting(string voice)
    {
        var x = VoiceNamed(voice);
        foreach (var harmonics in new[] { ClarityHarmonics.Even, ClarityHarmonics.Odd, ClarityHarmonics.Both })
        {
            var shape = new ClarityBand(true, 1600, 20, 0, harmonics);
            var model = Model(x, Off, shape);
            var baseline = Render(x, Off);
            foreach (var (drive, mix) in new[] { (6f, 20f), (20f, 50f), (12f, 100f), (24f, 60f), (24f, 100f) })
            {
                var band = new ClarityBand(true, 1600, drive, mix, harmonics);
                double predicted = model.LiftDb(Region, Middle, ClarityLiftModel.Weight(band));
                double real = MeterLift(baseline, Render(x, Off.WithHigh(band)));
                _out.WriteLine($"{voice}, {harmonics}, drive {drive} mix {mix}: predicted {predicted:F3} dB, real render {real:F3} dB, error {real - predicted:+0.000;-0.000;0.000}");
                Assert.InRange(real - predicted, -PredictionToleranceDb, PredictionToleranceDb);
            }
        }
    }

    [Fact]
    public void With_a_band_already_in_force_the_model_separates_it_and_predicts_from_there()
    {
        var x = Voice();
        var current = ClaritySettings.Default;   // the high band on at drive 20, mix 50
        var model = Model(x, current, current.High);
        Assert.Equal(ClarityLiftModel.Weight(current.High), model.BaselineWeight, 9);
        Assert.Equal(0.0, model.LiftDb(Region, Middle, model.BaselineWeight), 9);   // no change is no lift
        var baseline = Render(x, current);
        foreach (float mix in new[] { 70f, 100f })
        {
            var band = current.High.WithMix(mix).WithDrive(24);
            double predicted = model.LiftDb(Region, Middle, ClarityLiftModel.Weight(band));
            double real = MeterLift(baseline, Render(x, current.WithHigh(band)));
            _out.WriteLine($"from drive 20 mix 50 to drive 24 mix {mix}: predicted {predicted:F3}, real {real:F3}");
            Assert.True(predicted > 0.2);
            Assert.InRange(real - predicted, -PredictionToleranceDb, PredictionToleranceDb);
        }
        // Already at the most: nothing left to predict.
        var most = current.WithHigh(current.High.WithDrive(24).WithMix(100));
        Assert.Null(ClarityLiftModel.Build(Render(x, most), Render(x, most), 1.0, Rate, Lo, Hi));
    }

    // ── the solve ──

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(5.0)]
    public void The_solve_reaches_a_reachable_shortfall_and_a_real_render_of_its_setting_agrees(double target)
    {
        var x = Voice();
        var shape = new ClarityBand(true, 1600, ClaritySettings.HighDriveDefaultDb, 0, ClarityHarmonics.Even);
        var model = Model(x, Off, shape);
        var sol = ClarityBrightnessSolve.Solve(model, Region, Middle, Off.High, shape, target);
        double real = MeterLift(Render(x, Off), Render(x, Off.WithHigh(sol.Band)));
        _out.WriteLine($"target {target}: {sol.Band}; predicted {sol.LiftDb:F3} (most {sol.MostLiftDb:F2}); real render {real:F3}");
        Assert.True(sol.ReachesShortfall);
        // Rounded up to the levers' steps: never short, and over by no more than one percent of mix gives.
        Assert.InRange(sol.LiftDb, target - ClarityBrightnessSolve.ClosedWithinDb, target + 0.25);
        Assert.InRange(real, target - PredictionToleranceDb, target + 0.25 + PredictionToleranceDb);
        Assert.InRange(sol.Band.MixPercent, 1f, 100f);
        Assert.True(sol.Band.DriveDb >= Off.High.DriveDb);
    }

    [Fact]
    public void A_shortfall_the_ranges_cannot_reach_gets_the_most_the_band_can_lift_and_says_so()
    {
        var x = Voice();
        var shape = new ClarityBand(true, 1600, ClaritySettings.HighDriveDefaultDb, 0, ClarityHarmonics.Even);
        var model = Model(x, Off, shape);
        var sol = ClarityBrightnessSolve.Solve(model, Region, Middle, Off.High, shape, 40);
        _out.WriteLine($"{sol.Band}; predicted {sol.LiftDb:F2}, most {sol.MostLiftDb:F2}");
        Assert.False(sol.ReachesShortfall);
        Assert.Equal((24f, 100f), (sol.Band.DriveDb, sol.Band.MixPercent));   // monotonic here: the most is at the top of both ranges
        Assert.Equal(sol.MostLiftDb, sol.LiftDb, 6);
        Assert.InRange(MeterLift(Render(x, Off), Render(x, Off.WithHigh(sol.Band))) - sol.LiftDb, -PredictionToleranceDb, PredictionToleranceDb);

        // Through the pass: the prediction carries the shortfall, the lift and why it falls short.
        var r = ClaritySuggest.FromAudio(MeasuredShort(40), null, Rate, Lo, Hi, ClaritySuggestSource.RadioRecorderStandIn, null,
            new ClaritySuggestInput(Off, x, Rate));
        Assert.NotNull(r.Prediction);
        Assert.Equal(ClarityLiftLimit.Ranges, r.Prediction!.Limit);
        Assert.False(r.Prediction.ClosesTheShortfall);
        Assert.True(r.Prediction.PredictedLiftDb < r.Prediction.ShortfallDb);
        Assert.Equal((1600, 2500), (r.Prediction.RegionLowHz, r.Prediction.RegionHighHz));
    }

    [Theory]
    [InlineData(20f, 0.30, 20f, 48f)]    // mix alone reaches it at the stored drive
    [InlineData(20f, 0.63, 20f, 100f)]   // just: 0.63 / 0.631 rounds up to 100
    [InlineData(20f, 0.90, 24f, 90f)]    // mix cannot: drive rises to the least whole decibel that can (23.1 to 24), then mix again
    [InlineData(6f, 0.50, 18f, 100f)]    // an old low stored drive: 18 dB is the least whole decibel that reaches 0.5 (17.98), at mix 99.8, rounded up
    [InlineData(24f, 0.25, 24f, 25f)]    // a stored drive of 24 is never raised
    public void Mix_is_solved_first_at_the_stored_drive_and_drive_rises_only_when_mix_alone_cannot(float storedDrive, double weight, float drive, float mix)
    {
        var current = new ClarityBand(false, 2000, storedDrive, 10, ClarityHarmonics.Even);
        var band = ClarityBrightnessSolve.Levers(current, current, weight);
        Assert.Equal((true, drive, mix), (band.Enabled, band.DriveDb, band.MixPercent));
        Assert.True(ClarityLiftModel.Weight(band) >= weight - 1e-9, "rounded down: the weight fell short");
    }

    [Fact]
    public void A_mix_in_force_is_never_lowered_even_when_drive_rises()
    {
        var current = new ClarityBand(true, 2000, 20, 97, ClarityHarmonics.Even);
        var band = ClarityBrightnessSolve.Levers(current, current, 0.95);
        Assert.True(band.DriveDb > 20f);
        Assert.True(band.MixPercent >= 97f);
    }

    // ── the pass ──

    /// <summary>A measured recording short of presence by about <paramref name="shortDb"/>: the reference-shaped tones, the presence region moved down.</summary>
    private static float[] MeasuredShort(double shortDb)
    {
        var tones = new List<(double Hz, double Amp)>();
        foreach (int hz in ReferenceSpeechSpectrum.NominalCentresHz.Where(h => h <= 2500))
        {
            int idx = Array.IndexOf(LongTermSpectrumMeter.NominalCentresHz, hz);
            double power = Math.Pow(10, (ReferenceSpeechSpectrum.BandLevelDb(hz) - 90 + (hz >= 1600 ? -shortDb : 0)) / 10);
            tones.Add((LongTermSpectrumMeter.CentreHz(idx), Math.Sqrt(2 * power)));
        }
        var r = new Random(7);
        var phase = tones.Select(_ => r.NextDouble() * 2 * Math.PI).ToArray();
        var x = new float[Rate * 3];
        for (int i = 0; i < x.Length; i++)
        {
            double v = 0;
            for (int t = 0; t < tones.Count; t++) v += tones[t].Amp * Math.Sin(phase[t] + 2 * Math.PI * tones[t].Hz * i / Rate);
            x[i] = (float)v;
        }
        return x;
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(6.0)]
    public void The_pass_offers_the_solved_setting_and_its_predicted_lift_is_what_a_real_render_of_the_offer_gives(double shortDb)
    {
        var retained = Voice();
        var r = ClaritySuggest.FromAudio(MeasuredShort(shortDb), null, Rate, Lo, Hi, ClaritySuggestSource.RadioRecorderStandIn, null,
            new ClaritySuggestInput(Off, retained, Rate));
        Assert.True(r.TryGetGuardedOffer(out var offer));
        var e = r.Prediction!;
        double real = MeterLift(Render(retained, Off), Render(retained, offer!.Proposed));
        _out.WriteLine($"shortfall {e.ShortfallDb:F2}: offer {offer.Proposed.High}; predicted {e.PredictedLiftDb:F3} (limit {e.Limit}); real render {real:F3}");
        Assert.Equal(ClarityJudgement.Proposed, r.Solved!.Brightness);
        Assert.Equal(ClarityLiftLimit.None, e.Limit);
        Assert.True(e.ClosesTheShortfall);
        Assert.InRange(e.PredictedLiftDb, e.ShortfallDb - ClarityBrightnessSolve.ClosedWithinDb, e.ShortfallDb + 0.25);
        Assert.InRange(real - e.PredictedLiftDb, -PredictionToleranceDb, PredictionToleranceDb);
        // The rule's raw proposal is still there for analysis, and is not what is offered.
        Assert.NotEqual(r.Proposal.Proposed.High, offer.Proposed.High);
        // The fullness band is the rule's.
        Assert.Equal(r.Proposal.Proposed.Low, offer.Proposed.Low);
    }

    [Fact]
    public void The_peak_guard_still_bounds_the_solved_offer_and_the_prediction_says_the_guard_is_why()
    {
        // A limiter's ceiling half a decibel above the voice's own peak: the solved setting raises the peak past it.
        var retained = Voice();
        double ceiling = ClarityPeakGuard.PeakDbfs(Render(retained, Off)) + 0.5;
        var r = ClaritySuggest.FromAudio(MeasuredShort(8), null, Rate, Lo, Hi, ClaritySuggestSource.RadioRecorderStandIn, null,
            new ClaritySuggestInput(Off, retained, Rate, LimiterCeilingDbfs: ceiling));
        var solved = r.Solved!;
        // The positive control: unguarded, the solved setting does raise the peak past the ceiling.
        Assert.True(ClarityPeakGuard.PeakDbfs(Render(retained, solved.Proposed)) > ceiling);
        var g = r.Guard!;
        _out.WriteLine($"solved {solved.Proposed.High}; guard {g.Steps} steps, withdrawn {g.Withdrawn}; offer {g.Proposal.Proposed.High}; peak {g.PeakAfterDbfs:F2} against {ceiling:F2}; predicted {r.Prediction!.PredictedLiftDb:F2} of {r.Prediction.ShortfallDb:F2} (solved {r.Prediction.SolvedLiftDb:F2})");
        Assert.True(g.Reduced);
        Assert.True(g.PeakAfterDbfs <= ceiling || g.Withdrawn);
        if (r.TryGetGuardedOffer(out var offer))
            Assert.True(ClarityPeakGuard.PeakDbfs(Render(retained, offer!.Proposed)) <= ceiling);
        Assert.Equal(ClarityLiftLimit.PeakGuard, r.Prediction.Limit);
        Assert.True(r.Prediction.PredictedLiftDb < r.Prediction.SolvedLiftDb);
    }

    [Fact]
    public void A_voice_with_nothing_for_the_band_to_work_on_is_told_brightness_cannot_lift_it_and_nothing_is_kept()
    {
        // Only a 300 Hz tone: the high band's input (800 Hz up) is empty, so it can add nothing.
        var retained = Enumerable.Range(0, Rate * 3).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * 300 * i / (double)Rate))).ToArray();
        var r = ClaritySuggest.FromAudio(MeasuredShort(6), null, Rate, Lo, Hi, ClaritySuggestSource.RadioRecorderStandIn, null,
            new ClaritySuggestInput(Off, retained, Rate));
        _out.WriteLine($"most {r.Prediction?.MostLiftDb:F3} dB");
        Assert.Equal(ClarityJudgement.CannotLiftItMeasurably, r.Solved!.Brightness);
        Assert.True(r.Prediction!.MostLiftDb < ClarityBrightnessSolve.LeastUsefulLiftDb);
        Assert.False(r.CanKeep);
        Assert.Equal(Off, r.Guard!.Proposal.Proposed);
    }

    [Fact]
    public void A_band_at_the_old_rules_most_is_solved_again_because_the_ranges_hold_more()
    {
        // Mix 60 was the rule's cap: the rule alone says "at its most" and changes nothing.
        var current = Off.WithHigh(new ClarityBand(true, 1600, 20, 60, ClarityHarmonics.Even));
        var measured = MeasuredShort(6);
        var rule = ClarityProposalRule.Propose(LongTermSpectrumMeter.Measure(measured, Rate, Lo, Hi), current);
        Assert.Equal(ClarityJudgement.AtTheRulesMost, rule.Brightness);
        var r = ClaritySuggest.FromAudio(measured, null, Rate, Lo, Hi, ClaritySuggestSource.RadioRecorderStandIn, null,
            new ClaritySuggestInput(current, Voice(), Rate));
        Assert.Equal(ClarityJudgement.Proposed, r.Solved!.Brightness);
        Assert.True(ClarityLiftModel.Weight(r.Solved.Proposed.High) > ClarityLiftModel.Weight(current.High));
        Assert.Equal(1600f, r.Solved.Proposed.High.TuneHz);   // a band in force keeps its tune
        // And a band at the top of both ranges is at the most, and left exactly as set.
        var top = Off.WithHigh(new ClarityBand(true, 1600, 24, 100, ClarityHarmonics.Even));
        var t = ClaritySuggest.FromAudio(measured, null, Rate, Lo, Hi, ClaritySuggestSource.RadioRecorderStandIn, null,
            new ClaritySuggestInput(top, Voice(), Rate));
        Assert.Equal(ClarityJudgement.AtTheRulesMost, t.Solved!.Brightness);
        Assert.Equal(top, t.Solved.Proposed);
        Assert.Null(t.Prediction);
    }

    [Fact]
    public void The_solve_renders_through_the_solve_seam_when_one_is_given()
    {
        var retained = Voice();
        int solveCalls = 0, guardCalls = 0;
        var input = new ClaritySuggestInput(Off, retained, Rate, Render: s => { guardCalls++; return Render(retained, s); })
        {
            SolveRender = s => { solveCalls++; return Render(retained, s); },
        };
        var r = ClaritySuggest.FromAudio(MeasuredShort(5), null, Rate, Lo, Hi, ClaritySuggestSource.RadioRecorderStandIn, null, input);
        Assert.Equal(2, solveCalls);       // the settings in force and the band at full weight: two renders predict every setting
        Assert.True(guardCalls >= 2);      // the guard's own renders are its seam's
        Assert.NotNull(r.Prediction);
    }
}
