using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using JJPortaudio;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track I, on the app side: the automatic threshold's loop (#1252)
/// reproduced on the old rule and shown gone on the new one; "Learn the room"
/// through the owner, with the automatic threshold off writing the hand-set
/// threshold; and the audition twin rendering every in-process stage, naming
/// them, and keeping the equaliser out of "what was removed" (S8).
/// Windowless: MTA asserted; no rig is attached or connected.
/// </summary>
[Collection("Transmit conditioning statics")]
public sealed class TxSoundStagesTests
{
    private const int Rate = 48000;
    private readonly ITestOutputHelper _out;

    public TxSoundStagesTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    private static float[] Room(double seconds, double db, int seed)
    {
        // The same room the take sits in (ChainAuditionTwinTests.Take's noise: uniform, RMS at db).
        var rng = new Random(seed);
        double amp = Math.Pow(10, db / 20.0) * Math.Sqrt(3);
        var x = new float[(int)(seconds * Rate)];
        for (int i = 0; i < x.Length; i++) x[i] = (float)((rng.NextDouble() * 2 - 1) * amp);
        return x;
    }

    /// <summary>Run mono audio through a conditioner, stereo, 480 frames a buffer; returns the left channel.</summary>
    private static float[] Through(TxAudioConditioner c, float[] mono)
    {
        var output = new float[mono.Length];
        var stereo = new float[960];
        for (int at = 0; at < mono.Length; at += 480)
        {
            int n = Math.Min(480, mono.Length - at);
            for (int f = 0; f < n; f++) { stereo[2 * f] = mono[at + f]; stereo[2 * f + 1] = mono[at + f]; }
            c.Process(stereo, n * 2, Rate);
            for (int f = 0; f < n; f++) output[at + f] = stereo[2 * f];
        }
        return output;
    }

    // ── #1252: the loop, before and after ──

    [Theory]
    [InlineData(-50.0)]
    [InlineData(-40.0)]
    public void The_old_rule_alternates_from_over_to_over_and_the_new_one_settles_on_the_room_floor_plus_the_margin(double roomDb)
    {
        const float margin = 8f;
        var take = ChainAuditionTwinTests.Take(12, noiseDb: roomDb);

        // The control: the base rule, floor measured AFTER the gate (as the meter after
        // the conditioner did), reset each over; at the gate setting, as the base ran.
        var old = new List<float>();
        float threshold = TxNoiseGate.DefaultThresholdDb;
        for (int over = 0; over < 7; over++)
        {
            var c = new TxAudioConditioner { KeySource = () => true };
            c.SetGateInForce(new TxAudioConditioner.StagedGate { Enabled = true, GateSetting = true, HasThreshold = true, ThresholdDb = threshold });
            var after = Through(c, (float[])take.Clone());
            var floor = ChainAudition.Measure(after, after.Length, Rate);
            if (TxAudioConditioning.DerivedThreshold(true, false, floor.IsValid, floor.NoiseFloorLufs, margin) is float t) threshold = t;
            old.Add(threshold);
        }
        _out.WriteLine("old rule: " + string.Join(", ", old.Select(v => v.ToString("0.0"))));
        Assert.True(Math.Abs(old[5] - old[6]) > 2, "the old rule alternates (the reproduction)");
        Assert.True(Math.Abs(old[4] - old[6]) < 0.5, "with period two");

        // The new rule: ONE conditioner; the room is heard unkeyed between overs at
        // the expander's input; the rule reads that floor.
        bool keyed = false;
        var live = new TxAudioConditioner { KeySource = () => keyed };
        live.SetGateInForce(new TxAudioConditioner.StagedGate { Enabled = true, GateSetting = true, AutoThreshold = true, ThresholdMarginDb = margin });
        var news = new List<float>();
        for (int over = 0; over < 7; over++)
        {
            keyed = false;
            Through(live, Room(4, roomDb, 100 + over));
            news.Add(live.RefreshDerivedThreshold(false) ?? float.NaN);
            keyed = true;
            Through(live, (float[])take.Clone());
            Assert.Null(live.RefreshDerivedThreshold(true)); // held still while keyed (S15)
        }
        _out.WriteLine("new rule: " + string.Join(", ", news.Select(v => v.ToString("0.0"))));
        for (int i = 2; i < news.Count; i++) Assert.InRange(news[i], news[i - 1] - 0.3, news[i - 1] + 0.3);
        // The positive control: it settled on the room, not on the inert default.
        double roomSum = roomDb + 10 * Math.Log10(2); // the detector's channel-power sum of a mono room on both channels
        // #1349: the floor the rule adds the margin to is the expander's detector's
        // reading of that room, over its average; the average is still the room's.
        Assert.InRange(live.RoomFloor.MeanFloorDb, roomSum - 1, roomSum + 1);
        Assert.True(live.RoomFloor.FloorDb > live.RoomFloor.MeanFloorDb + 3);
        Assert.Equal(live.RoomFloor.FloorDb + margin, news[^1], 3);
        Assert.NotEqual(TxNoiseGate.DefaultThresholdDb, news[^1]);
    }

    // ── Learn the room through the owner ──

    private static readonly BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;

    private static IDisposable SaveStageIntents()
    {
        var fields = new[] { "_eqIntent", "_deEsserIntent", "_orderIntent", "_learnBaseline" }
            .Select(n => typeof(TxAudioConditioning).GetField(n, Statics)!).ToArray();
        var values = fields.Select(f => f.GetValue(null)).ToArray();
        return new Restore(() => { for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, values[i]); });
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_learn_finishes_and_with_the_automatic_threshold_off_it_writes_the_hand_set_threshold_which_then_holds(bool auto)
    {
        using var x = new TxGateOwnerTests.Context();
        using var restore = SaveStageIntents();
        // The side-chain filter off, so the room's level reads plainly (with it on,
        // white noise reads about 8 dB lower: the detector hears 150 Hz to 4 kHz).
        TxAudioConditioning.ApplySetup(new TxNoiseSetup { Gate = new TxNoiseGateSetup { Enabled = true, AutoThreshold = auto, ThresholdDb = -70, ThresholdMarginDb = 9, SideChainFilter = false } });
        Through(x.Cond, Room(0.1, -60, 1)); // audio is reaching the chain (and the setup lands)
        TxAudioConditioning.RoomLearnedResult? said = null;
        Action<TxAudioConditioning.RoomLearnedResult> listen = r => said = r;
        TxAudioConditioning.RoomLearned += listen;
        try
        {
            Assert.Equal(TxAudioConditioning.LearnRoomOutcome.Started, TxAudioConditioning.LearnRoom());
            Through(x.Cond, Room(3.6, -45, 2));
            TxAudioConditioning.FinishLearnIfDone(x.Cond, keyed: false);
            Assert.NotNull(said);
            float floor = x.Cond.RoomFloor.LearnedDb;
            // -45 per channel, -42 as a channel-power sum: the room's average. The floor a
            // learn sets is the expander's detector's reading of it, higher (#1349).
            Assert.InRange(x.Cond.RoomFloor.LearnedMeanDb, -42.6, -41.4);
            Assert.True(floor > x.Cond.RoomFloor.LearnedMeanDb + 3);
            Assert.Equal(floor + 9, said!.ThresholdDb, 1);
            Assert.Equal(floor + 9, x.Cond.Gate.ThresholdDb, 1);
            Assert.Equal(auto, said.AutomaticThreshold);
            if (!auto)
            {
                // Written by hand: it holds while the room changes, until the next learn
                // or a change by hand, which is kept.
                Assert.Equal(floor + 9, TxAudioConditioning.GateThresholdDb, 1);
                Through(x.Cond, Room(6, -30, 3));
                x.Tick();
                Assert.Equal(floor + 9, x.Cond.Gate.ThresholdDb, 1);
                TxAudioConditioning.GateThresholdDb = -33;
                x.Tick();
                Assert.Equal(-33f, x.Cond.Gate.ThresholdDb);
            }
            else
            {
                // Automatic: the tracking carries on from the learn.
                Through(x.Cond, Room(6, -55, 4));
                x.Tick();
                Assert.True(x.Cond.Gate.ThresholdDb < floor + 9 - 5, "the automatic threshold followed the quieter room down");
            }
        }
        finally { TxAudioConditioning.RoomLearned -= listen; }
    }

    [Fact]
    public void A_learn_refuses_in_cw_and_digital_and_when_no_computer_audio_reaches_the_chain()
    {
        using var x = new TxGateOwnerTests.Context();
        using var restore = SaveStageIntents();
        Assert.Equal(TxAudioConditioning.LearnRoomOutcome.NoComputerAudio, TxAudioConditioning.LearnRoom());
        Through(x.Cond, Room(0.1, -60, 5));
        x.Cond.BypassAll = true;
        Assert.Equal(TxAudioConditioning.LearnRoomOutcome.NotInThisMode, TxAudioConditioning.LearnRoom());
    }

    // ── The audition twin (item 7) ──

    private static ChainAuditionTwin Twin(TxNoiseSetup setup) =>
        new(() => setup, () => null, () => new DenoiseEngineCatalog(), TimeSpan.FromSeconds(5));

    private static TxNoiseSetup Stages(bool expander, bool eq, bool deEss) => new()
    {
        Gate = new TxNoiseGateSetup { Enabled = expander, AutoThreshold = false, ThresholdDb = -40, RatioToOne = 2 },
        Eq = new ParametricEqSetup { Enabled = eq, Bands = { new() { Kind = ParametricEqKinds.Peaking, FrequencyHz = 280, GainDb = 6, Q = 1 } } },
        DeEsser = new TxDeEsserSetup { Enabled = deEss, FrequencyHz = 2000, AmountDb = 6, SensitivityDb = -40 },
    };

    [Fact]
    public void With_only_the_equaliser_on_the_audition_renders_it_names_it_and_what_was_removed_is_silent()
    {
        var take = ChainAuditionTwinTests.Take(6);
        var r = Twin(Stages(false, true, false)).Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        Assert.True(r.Available, r.UnavailableKey);
        Assert.False(r.NothingOn);
        Assert.Equal(new[] { TxAudioConditioner.StageIds.Equaliser }, r.Stages);
        Assert.Contains(Enumerable.Range(0, take.Length), i => r.Processed[i] != take[i]); // the boost is heard in what goes out
        var audition = ChainAudition.Analyse((float[])take.Clone(), Rate, Twin(Stages(false, true, false)));
        var removed = audition.Render(AuditionRender.Removed);
        Assert.NotNull(removed);
        Assert.All(removed!, s => Assert.Equal(0f, s)); // and is never "removed" (S8)
        Assert.Contains("eq", audition.RenderedStages);
    }

    [Fact]
    public void The_twin_equals_a_live_shaped_conditioner_sample_for_sample_with_the_expander_at_two_to_one_the_equaliser_and_the_de_esser()
    {
        var setup = Stages(true, true, true);
        var take = ChainAuditionTwinTests.Take(6);
        var r = Twin(setup).Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        Assert.True(r.Available, r.UnavailableKey);
        Assert.Equal(new[] { "expander", "eq", "deesser" }, r.Stages);

        // The live shape: a conditioner of our own with the setup put in force by
        // the ONE wiring function the live chain uses, keyed.
        float[] LiveShape(float threshold)
        {
            var c = new TxAudioConditioner { KeySource = () => true };
            var record = TxAudioConditioning.ToConditioner(setup);
            record.ThresholdDb = threshold;
            c.SetGateInForce(record);
            return Through(c, (float[])take.Clone());
        }
        var live = LiveShape(-40);
        for (int i = 0; i < take.Length; i++)
            if (r.Processed[i] != live[i]) Assert.Fail($"sample {i}: twin {r.Processed[i]:R}, live {live[i]:R}");
        // The comparison can see a difference.
        var higher = LiveShape(-37);
        Assert.Contains(Enumerable.Range(0, take.Length), i => higher[i] != live[i]);
        // The look-ahead is in the alignment the audition uses.
        Assert.Equal(TxNoiseGate.LookAheadFramesFor(setup.Gate.LookAheadMs, Rate), r.LatencySamples);
    }

    [Fact]
    public void Rendering_changes_nothing_live_or_saved()
    {
        using var x = new TxGateOwnerTests.Context();
        using var restore = SaveStageIntents();
        var before = TxAudioConditioning.CaptureSetup("");
        var inForce = x.Cond.GateInForce;
        var r = Twin(Stages(true, true, true)).Process(ChainAuditionTwinTests.Take(4), Rate, CancellationToken.None, null);
        Assert.True(r.Available);
        Assert.True(TxAudioConditioning.CaptureSetup("").SameContentAs(before));
        Assert.Same(inForce, x.Cond.GateInForce);
    }
}
