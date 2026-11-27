using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Serialization;
using JJPortaudio;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// #1349 on the app's side: WDSP's expander keeps its threshold over the
/// room's AVERAGE (its detector averages) while JJ Flexible's own is set over
/// its detector's higher reading; Learn the room with the automatic threshold
/// off sets the threshold for the mode in force; and setups operators have
/// already saved load and do what they say. Windowless: MTA asserted; the rig
/// is <see cref="TxGateOwnerTests.Context"/>'s uninitialised FlexBase holding
/// only a conditioner, never attached, never connected. WDSP's expander is a
/// holder whose engine factory records the settings it is asked for and
/// builds nothing, so no helper process starts.
/// </summary>
[Collection("Transmit conditioning statics")]
public sealed class ExpanderFloorPolicyTests
{
    private const uint Fs = 48000;
    private static readonly BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;
    private readonly ITestOutputHelper _out;

    public ExpanderFloorPolicyTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    private static float[] PinkStereo(double rmsDb, double seconds, int seed)
    {
        int n = (int)(seconds * Fs);
        var r = new Random(seed);
        var m = new double[n];
        double b0 = 0, b1 = 0, b2 = 0;
        for (int i = 0; i < n; i++)
        {
            double u1 = 1 - r.NextDouble(), u2 = r.NextDouble();
            double w = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            b0 = 0.99765 * b0 + w * 0.0990460; b1 = 0.96300 * b1 + w * 0.2965164; b2 = 0.57000 * b2 + w * 1.0526913;
            m[i] = b0 + b1 + b2 + w * 0.1848;
        }
        double rms = Math.Sqrt(m.Sum(v => v * v) / n), g = Math.Pow(10, rmsDb / 20) / rms;
        var s = new float[2 * n];
        for (int i = 0; i < n; i++) s[2 * i] = s[2 * i + 1] = (float)(m[i] * g);
        return s;
    }

    private static void Feed(TxAudioConditioner c, float[] stereo, Action? tick = null)
    {
        for (int at = 0, b = 0; at + 960 <= stereo.Length; at += 960, b++)
        {
            if (b % 50 == 0) tick?.Invoke();
            c.Process(stereo.AsSpan(at, 960).ToArray(), 960, Fs);
        }
    }

    /// <summary>A WDSP holder whose factory records each set of settings asked for, and builds nothing.</summary>
    private sealed class Recorder
    {
        public readonly List<IReadOnlyDictionary<string, EngineSettingValue>> Asked = new();
        public readonly TxWdspExpander X;
        public Recorder() => X = new TxWdspExpander(v =>
        {
            lock (Asked) Asked.Add(new Dictionary<string, EngineSettingValue>(v));
            throw new InvalidOperationException("no engine in this test");
        });
        public double LastThreshold(int atLeast)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < 5000) { lock (Asked) if (Asked.Count >= atLeast) return Asked[^1]["threshold_db"].Number; Thread.Sleep(5); }
            throw new TimeoutException("WDSP's expander was never asked for its settings");
        }
    }

    private static void SyncWdsp(TxAudioConditioner c, bool keyed) =>
        typeof(TxAudioConditioning).GetMethod("SyncWdsp", Statics, new[] { typeof(TxAudioConditioner), typeof(bool) })!.Invoke(null, new object[] { c, keyed });

    private static void SetIntent(TxNoiseGateSetup g) => typeof(TxAudioConditioning).GetField("_gateIntent", Statics)!.SetValue(null, g);

    [Fact]
    public void WDSPs_expander_is_given_the_margin_over_the_rooms_average_and_ours_the_margin_over_our_detectors_reading()
    {
        using var x = new TxGateOwnerTests.Context();
        var savedWdsp = typeof(TxAudioConditioning).GetField("_wdsp", Statics)!.GetValue(null);
        var rec = new Recorder();
        try
        {
            TxAudioConditioning.UseWdspForTest(rec.X);
            var g = new TxNoiseGateSetup { Enabled = true, AutoThreshold = true, ThresholdMarginDb = 8f, ExpanderMode = ExpanderModes.Wdsp, SideChainFilter = true };
            SetIntent(g);
            x.Cond.SetGateInForce(TxAudioConditioning.ToConditioner(g, true, 8f));
            Feed(x.Cond, PinkStereo(-50, 6, 71), () => x.Cond.RefreshDerivedThreshold(false));
            Assert.True(x.Cond.RoomFloor.HasFloor);

            x.Cond.RefreshDerivedThreshold(false);
            SyncWdsp(x.Cond, false);
            double sent = rec.LastThreshold(1);
            double average = TxWdspExpander.MapThresholdDb(x.Cond.RoomFloor.MeanFloorDb + 8f, true);
            double ours = TxWdspExpander.MapThresholdDb(x.Cond.Gate.ThresholdDb, true);
            _out.WriteLine($"sent {sent:F2} dB; over the average {average:F2}; over our reading {ours:F2}");
            Assert.Equal(Math.Round(average, 2), sent, 2);
            // Positive control: the two figures differ, so the test can tell which was sent.
            Assert.True(ours > average + 3, $"ours {ours:F1} against the average's {average:F1}");
            Assert.Equal(x.Cond.RoomFloor.FloorDb + 8f, x.Cond.Gate.ThresholdDb, 3);

            // A threshold set by hand reaches WDSP's expander as the hand set it, as before #1349.
            var hand = g.Clone(); hand.AutoThreshold = false; hand.ThresholdDb = -37f;
            SetIntent(hand);
            x.Cond.SetGateInForce(TxAudioConditioning.ToConditioner(hand, false, 8f));
            x.Buffer();
            SyncWdsp(x.Cond, false);
            Assert.Equal(Math.Round(TxWdspExpander.MapThresholdDb(-37f, true), 2), rec.LastThreshold(2), 2);
        }
        finally
        {
            rec.X.Dispose();
            typeof(TxAudioConditioning).GetField("_wdsp", Statics)!.SetValue(null, savedWdsp);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Learn_the_room_with_the_threshold_by_hand_sets_it_for_the_mode_in_force(bool wdsp)
    {
        using var x = new TxGateOwnerTests.Context();
        var g = new TxNoiseGateSetup { Enabled = true, AutoThreshold = false, ThresholdDb = -60f, ThresholdMarginDb = 8f, SideChainFilter = true, ExpanderMode = wdsp ? ExpanderModes.Wdsp : ExpanderModes.Jj };
        SetIntent(g);
        typeof(TxAudioConditioning).GetField("_autoThreshold", Statics)!.SetValue(null, false);
        typeof(TxAudioConditioning).GetField("_thresholdMarginDb", Statics)!.SetValue(null, 8f);
        x.Cond.SetGateInForce(TxAudioConditioning.ToConditioner(g, false, 8f));
        typeof(TxAudioConditioning).GetField("_learnBaseline", Statics)!.SetValue(null, x.Cond.RoomFloor.LearnsFinished);
        x.Cond.RoomFloor.Learn();
        Feed(x.Cond, PinkStereo(-50, 4, 72));
        Assert.Equal(1, x.Cond.RoomFloor.LearnsFinished);
        TxAudioConditioning.RoomLearnedResult? said = null;
        void Heard(TxAudioConditioning.RoomLearnedResult r) => said = r;
        TxAudioConditioning.RoomLearned += Heard;
        try { TxAudioConditioning.FinishLearnIfDone(x.Cond, keyed: false); }
        finally { TxAudioConditioning.RoomLearned -= Heard; }

        float expectedFloor = wdsp ? x.Cond.RoomFloor.LearnedMeanDb : x.Cond.RoomFloor.LearnedDb;
        Assert.NotNull(said);
        Assert.Equal(expectedFloor, said!.FloorDb, 3);
        Assert.Equal(expectedFloor + 8f, TxAudioConditioning.GateThresholdDb, 3);
        // Positive control: the two figures a learn could have used differ.
        Assert.True(x.Cond.RoomFloor.LearnedDb > x.Cond.RoomFloor.LearnedMeanDb + 3);
    }

    // ── setups already saved ──

    private const string VersionTwoAtTwoToOne = """
<TxNoiseSetup>
  <Version>2</Version>
  <Name>Saved before 1349</Name>
  <Spectral><Enabled>false</Enabled><EngineId /><Engines /></Spectral>
  <Neural><Enabled>false</Enabled><EngineId /><Engines /></Neural>
  <Gate>
    <Enabled>true</Enabled>
    <AutoThreshold>true</AutoThreshold>
    <ThresholdMarginDb>10</ThresholdMarginDb>
    <ThresholdDb>-60</ThresholdDb>
    <AttackMs>3</AttackMs>
    <HoldMs>150</HoldMs>
    <ReleaseMs>200</ReleaseMs>
    <RangeDb>25</RangeDb>
    <RatioToOne>2</RatioToOne>
    <GateSetting>false</GateSetting>
    <SideChainFilter>true</SideChainFilter>
    <SideChainLowCutHz>150</SideChainLowCutHz>
    <SideChainHighCutHz>4000</SideChainHighCutHz>
    <LookAheadMs>3</LookAheadMs>
  </Gate>
</TxNoiseSetup>
""";

    private const string VersionOneGateOnByHand = """
<TxNoiseSetup>
  <Version>1</Version>
  <Name>Desk mic</Name>
  <Spectral><Enabled>false</Enabled><EngineId /><Engines /></Spectral>
  <Neural><Enabled>false</Enabled><EngineId /><Engines /></Neural>
  <Gate>
    <Enabled>true</Enabled>
    <AutoThreshold>false</AutoThreshold>
    <ThresholdMarginDb>9</ThresholdMarginDb>
    <ThresholdDb>-42</ThresholdDb>
    <AttackMs>4</AttackMs>
    <HoldMs>220</HoldMs>
    <ReleaseMs>260</ReleaseMs>
    <RangeDb>30</RangeDb>
  </Gate>
</TxNoiseSetup>
""";

    private static TxNoiseSetup Load(string xml)
    {
        var s = (TxNoiseSetup)new XmlSerializer(typeof(TxNoiseSetup)).Deserialize(new StringReader(xml))!;
        s.AfterLoad();
        return s;
    }

    /// <summary>A saved setup through the live road, over 15 s of room unkeyed and 10 s keyed: how far the keyed room came down, dB.</summary>
    private static double RoomDown(TxNoiseSetup s, out TxAudioConditioner c, double roomDb = -50)
    {
        c = new TxAudioConditioner();
        bool keyed = false; c.KeySource = () => keyed;
        c.SetGateInForce(TxAudioConditioning.ToConditioner(s));
        var room = PinkStereo(roomDb, 25, 73);
        var y = new float[room.Length];
        for (int at = 0, k = 0; at + 960 <= room.Length; at += 960, k++)
        {
            keyed = at >= 2 * 15 * (int)Fs;
            if (k % 50 == 0) c.RefreshDerivedThreshold(keyed);
            var buf = room.AsSpan(at, 960).ToArray();
            c.Process(buf, 960, Fs);
            Array.Copy(buf, 0, y, at, 960);
        }
        int delay = c.AppliedDelayFrames;
        double a = 0, b = 0;
        for (int f = 17 * (int)Fs; f + delay < room.Length / 2; f++) { a += room[2 * f] * room[2 * f]; b += y[2 * (f + delay)] * y[2 * (f + delay)]; }
        return 10 * Math.Log10(a / b);
    }

    [Fact]
    public void A_setup_saved_at_two_to_one_now_turns_the_room_down_by_half_its_depth_whatever_its_margin()
    {
        var s = Load(VersionTwoAtTwoToOne);
        Assert.Equal(TxNoiseSetup.CurrentVersion, s.Version);
        Assert.False(s.Gate.GateSetting);
        double down = RoomDown(s, out var c);
        _out.WriteLine($"saved at 2:1, depth 25, margin 10: the room came down {down:F2} dB; threshold {c.Gate.ThresholdDb:F1} over a floor of {c.RoomFloor.FloorDb:F1}");
        Assert.Equal(10f, c.Gate.RoomBelowThresholdDb);
        Assert.InRange(down, 12.5 - 1.5, 12.5 + 0.5);
    }

    [Fact]
    public void A_version_one_gate_set_by_hand_keeps_its_threshold_and_its_whole_depth()
    {
        var s = Load(VersionOneGateOnByHand);
        Assert.True(s.Gate.GateSetting, "S7: a saved gate that was on is the gate setting");
        Assert.False(s.Gate.SideChainFilter);
        Assert.Equal(0f, s.Gate.LookAheadMs);
        // A room well under the hand-set threshold, on the detector's own reading of it.
        double down = RoomDown(s, out var c, roomDb: -65);
        _out.WriteLine($"version 1 gate by hand at -42 dB, depth 30: the room at -65 came down {down:F2} dB; threshold {c.Gate.ThresholdDb:F1}");
        Assert.Equal(-42f, c.Gate.ThresholdDb);
        Assert.InRange(down, 29.5, 30.5);
    }
}
