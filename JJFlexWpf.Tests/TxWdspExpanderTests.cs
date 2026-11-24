using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using JJPortaudio;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track I phase 2: WDSP's downward expander (DEXP, in the helper)
/// as the transmit expander's other mode (ruled S6). Every test but the
/// threshold calibration runs against the stub helper, which offers dexp and
/// echoes it with a known delay, so no GPL code is anywhere near them; the
/// calibration needs the real helper and says NOT RUN without one. Windowless:
/// MTA asserted in every test; nothing is played, no microphone, no radio.
/// </summary>
[Collection("Transmit conditioning statics")]
public sealed class TxWdspExpanderTests
{
    private const int Rate = 48000;
    private const int Buffer = 480;
    private readonly ITestOutputHelper _out;

    public TxWdspExpanderTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    private static TxNoiseGateSetup WdspGate(float depth = 20f, bool sideFilter = true) => new()
    {
        Enabled = true,
        ExpanderMode = ExpanderModes.Wdsp,
        WdspDepthDb = depth,
        SideChainFilter = sideFilter,
    };

    /// <summary>A holder on a rig's host, its engines kept so a test can pace them.</summary>
    private sealed class Holder
    {
        public readonly List<WdspHelperEngine> Made = new();
        public readonly TxWdspExpander X;
        public int Told;
        public Holder(WdspHost host, bool offlineWait = false)
        {
            X = new TxWdspExpander(v =>
            {
                var e = new WdspHelperEngine(host, TxWdspExpander.EngineId, DenoiseDirection.Transmit, v, offlineWait: offlineWait);
                lock (Made) Made.Add(e);
                return e;
            });
            X.FellBack += () => Interlocked.Increment(ref Told);
        }
        public WdspHelperEngine Engine { get { lock (Made) return Made[^1]; } }
    }

    private static float[] Stereo(float[] mono, int at)
    {
        var b = new float[Buffer * 2];
        for (int f = 0; f < Buffer; f++) { float s = at + f < mono.Length ? mono[at + f] : 0f; b[2 * f] = s; b[2 * f + 1] = s; }
        return b;
    }

    private static float[] Noise(int n, int seed)
    {
        var r = new Random(seed);
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(r.NextDouble() * 0.2 - 0.1);
        return x;
    }

    [Fact]
    public void The_mode_switch_waits_for_the_unkey_and_only_once_its_bank_is_built()
    {
        using var rig = new HelperTestRig();
        var h = new Holder(rig.Host);
        var buf = new float[Buffer * 2];

        h.X.WantWdsp(true);
        h.X.Update(WdspGate(), -50f, keyed: false);
        // Asked, not yet built: the JJ expander keeps running (false), never a gap.
        // (A build that wins the race to here is adopted at once; nothing to see then.)
        if (!h.X.BankBuilt) Assert.False(h.X.Process(buf, buf.Length, Rate, keyed: true));
        HelperTestRig.Until(() => h.X.BankBuilt, "WDSP's expander built");

        // Keyed: the built bank waits; the JJ expander still runs.
        for (int i = 0; i < 5; i++) Assert.False(h.X.Process(buf, buf.Length, Rate, keyed: true));
        Assert.False(h.X.InForce);

        // The unkey: in force, in the expander's place, with its delay.
        Assert.True(h.X.Process(buf, buf.Length, Rate, keyed: false));
        Assert.True(h.X.InForce);
        Assert.Equal(2448 + WdspPipelineDepth.Dexp * Buffer, h.X.AppliedLatencySamples());

        // Back to JJ while keyed: still WDSP's until the unkey.
        h.X.WantWdsp(false);
        Assert.True(h.X.Process(buf, buf.Length, Rate, keyed: true));
        Assert.False(h.X.Process(buf, buf.Length, Rate, keyed: false));
        Assert.Equal(0, h.X.AppliedLatencySamples());
    }

    [Fact]
    public void Through_the_conditioner_the_mode_replaces_the_expander_and_its_delay_counts()
    {
        using var rig = new HelperTestRig();
        var h = new Holder(rig.Host);
        bool keyed = false;
        var c = new TxAudioConditioner { KeySource = () => keyed };
        c.SetGateInForce(new TxAudioConditioner.StagedGate { Enabled = true, GateSetting = false, LookAheadMs = 3f });
        TxAudioConditioning.PlugExpanderMode(c, h.X);
        var buf = new float[Buffer * 2];
        c.Process(buf, buf.Length, Rate);
        Assert.Equal(TxNoiseGate.LookAheadFramesFor(3f, Rate), c.AppliedDelayFrames);   // JJ's: its look-ahead

        h.X.WantWdsp(true);
        h.X.Update(WdspGate(), -50f, keyed: false);
        HelperTestRig.Until(() => h.X.BankBuilt, "built");
        keyed = true;
        c.Process(buf, buf.Length, Rate);
        Assert.Equal(TxNoiseGate.LookAheadFramesFor(3f, Rate), c.AppliedDelayFrames);   // keyed: not yet
        keyed = false;
        c.Process(buf, buf.Length, Rate);
        Assert.Equal(2448 + WdspPipelineDepth.Dexp * Buffer, c.AppliedDelayFrames);      // WDSP's in its place

        // In its place, not as well: an expander set to shut this audio hard
        // (40 dB, opening only above -10 dB) leaves it whole, because only the
        // stub (an echo) ran. The JJ expander running too would take 40 dB off.
        c.SetGateInForce(new TxAudioConditioner.StagedGate { Enabled = true, GateSetting = true, RangeDb = 40f, HasThreshold = true, ThresholdDb = -10f, HoldMs = 50f, ReleaseMs = 50f });
        var input = Noise(Buffer * 40, 3);
        var output = new float[input.Length];
        for (int b = 0; b < 40; b++)
        {
            var s = Stereo(input, b * Buffer);
            HelperTestRig.Pace(h.Engine);
            c.Process(s, s.Length, Rate);
            for (int f = 0; f < Buffer; f++) output[b * Buffer + f] = s[2 * f];
        }
        int d = c.AppliedDelayFrames;
        for (int i = d + Buffer * 2; i < input.Length; i++) Assert.Equal(input[i - d], output[i], 6);

        // The expander off: the mode is not asked at all.
        c.SetGateInForce(new TxAudioConditioner.StagedGate { Enabled = false });
        c.Process(buf, buf.Length, Rate);
        Assert.Equal(0, c.AppliedDelayFrames);
    }

    [Fact]
    public void A_helper_crash_mid_over_latches_dry_at_the_same_delay_and_says_so_once()
    {
        // The stub dies after 40 frames, and stays down for the test (the restart
        // is a minute away), so a second over meets the same failure.
        using var rig = new HelperTestRig(n => n == 1 ? new[] { "--crash-after", "40:9" } : Array.Empty<string>(),
            HelperTestRig.Fast with { RestartDelays = new[] { TimeSpan.FromSeconds(60) } });
        var h = new Holder(rig.Host);
        h.X.WantWdsp(true);
        h.X.Update(WdspGate(), -50f, keyed: false);
        HelperTestRig.Until(() => h.X.BankBuilt, "built");

        var input = Noise(Buffer * 120, 7);
        var output = new float[input.Length];
        int b = 0;
        void Run(bool keyed)
        {
            var s = Stereo(input, b * Buffer);
            HelperTestRig.Pace(h.Engine);
            Assert.True(h.X.Process(s, s.Length, Rate, keyed) || b == 0);
            for (int f = 0; f < Buffer; f++) output[b * Buffer + f] = s[2 * f];
            b++;
        }
        // Receiving: the pipeline fills, as it does while the microphone streams unkeyed (#565).
        for (int i = 0; i < 20; i++) Run(false);
        int delay = h.X.AppliedLatencySamples();
        Assert.Equal(2448 + WdspPipelineDepth.Dexp * Buffer, delay);
        // The over: the helper dies partway through.
        while (b < 100) Run(true);
        var bank = h.X.RunningBank!;
        Assert.True(bank.Latched);
        Assert.Equal(delay, h.X.AppliedLatencySamples());          // no jump in the timeline
        HelperTestRig.Until(() => Volatile.Read(ref h.Told) >= 1, "the notice");
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref h.Told));                // said once
        // Every sample, processed or latched: the input at the same delay (the stub
        // echoes, so processed and dry agree, and a jump would show as a mismatch).
        for (int i = delay + Buffer * 25; i < Buffer * 100; i++) Assert.Equal(input[i - delay], output[i], 6);
        _out.WriteLine($"latched after {bank.Latches} latch, {bank.LatchedFrames} latched frames, delay {delay}");

        // More of the same over: still once.
        while (b < 110) Run(true);
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref h.Told));
        // The unkey releases the latch; the bank is asked again.
        Run(false);
        Assert.False(bank.Latched);
        // A second over with the helper still down: it latches again, at the same
        // delay, and the operator is NOT told again: one failure, one notice.
        while (b < 118) Run(true);
        Assert.True(bank.Latched);
        Assert.Equal(delay, h.X.AppliedLatencySamples());
        Thread.Sleep(150);
        Assert.Equal(1, Volatile.Read(ref h.Told));
    }

    [Fact]
    public void The_one_lever_means_depth_in_WDSP_mode_and_each_mode_keeps_its_own_setting()
    {
        // The JJ mode's strength is its ratio; WDSP's is its depth: two fields, so
        // switching back restores the other, and WDSP's reaches the helper as its depth.
        var g = new TxNoiseGateSetup { RatioToOne = 4f, WdspDepthDb = 30f, ExpanderMode = ExpanderModes.Wdsp };
        Assert.Equal(30.0, TxWdspExpander.ValuesFor(g, -50f)["depth_db"].Number);
        g.ExpanderMode = ExpanderModes.Jj;
        Assert.Equal(4f, g.RatioToOne);
        Assert.Equal(30f, g.WdspDepthDb);

        using var f = new NoiseFixture();
        var fake = new ModeGate();
        f.Noise.TransmitGate = fake;
        var strength = f.Noise.GateLever(TxGateLever.Strength)!;
        var jjWords = f.Noise.GateSteps(strength).Select(s => s.Text).ToArray();
        fake.Mode = ExpanderModes.Wdsp;
        var wdspWords = f.Noise.GateSteps(strength).Select(s => s.Text).ToArray();
        Assert.Equal(jjWords.Length, wdspWords.Length);                     // one item, the same range
        Assert.Empty(jjWords.Intersect(wdspWords));                         // the words say what it now does
        Assert.All(wdspWords, w => Assert.Contains("deep", w));
        f.Noise.SetGate(strength, 5, "home");                               // the sixth stop: 30 dB
        Assert.Equal(30f, fake.Depth);
        Assert.Equal(2f, fake.Ratio);                                       // the ratio untouched
    }

    [Fact]
    public void WDSPs_mode_unavailable_is_listed_with_why_and_kept_as_the_choice()
    {
        using var f = new NoiseFixture();
        var fake = new ModeGate { Availability = Unavailability.HelperMissing };
        f.Noise.TransmitGate = fake;
        var mode = f.Noise.GateLever(TxGateLever.Mode)!;
        Assert.Contains(f.Noise.GateLevers(includeAdvanced: true), l => l.Key == TxGateLever.Mode);
        var steps = f.Noise.GateSteps(mode);
        Assert.Equal(2, steps.Count);                                        // listed, never hidden
        string reason = Lexicon.Get(DenoiseEngineCatalog.UnavailabilityKey(Unavailability.HelperMissing));
        Assert.Contains(reason, steps[1].Text);                              // what to do
        f.Noise.SetGate(mode, 1, "home");
        Assert.Equal(ExpanderModes.Wdsp, fake.Mode);                          // the choice is kept
        Assert.Contains(reason, f.Spoken.Last);
        // Available: no reason said.
        fake.Availability = Unavailability.None;
        Assert.DoesNotContain(reason, f.Noise.GateSteps(mode)[1].Text);
    }

    /// <summary>A gate holding the two modes' settings, as the app's does.</summary>
    private sealed class ModeGate : ITransmitGate
    {
        public string Mode = ExpanderModes.Jj;
        public float Depth = 20f, Ratio = 2f;
        public Unavailability Availability = Unavailability.None;
        public bool Enabled => true;
        public TxChangeResult SetEnabled(bool on) => TxChangeResult.Now;
        public bool AutoThreshold => true;
        public float MarginDb => 8;
        public float ThresholdDb => -60;
        public float AttackMs => 3;
        public float HoldMs => 150;
        public float ReleaseMs => 200;
        public float RangeDb => 25;
        public float RatioToOne => Ratio;
        public string ExpanderMode => Mode;
        public float WdspDepthDb => Depth;
        public Unavailability WdspAvailability => Availability;
        public TxChangeResult SetExpanderMode(string mode) { Mode = mode; return TxChangeResult.Now; }
        public TxChangeResult SetWdspDepth(float db) { Depth = db; return TxChangeResult.Now; }
        public TxChangeResult SetStrength(float ratio, bool gate) { Ratio = ratio; return TxChangeResult.Now; }
        public TxChangeResult SetAutoThreshold(bool on) => TxChangeResult.Now;
        public TxChangeResult SetLevel(string key, float value) => TxChangeResult.Now;
        public bool MonitorAvailable => false;
        public int MonitorMode { get; set; }
        public float MonitorVolume { get; set; } = 1;
        public TxNoiseGateSetup Capture() => new() { ExpanderMode = Mode, WdspDepthDb = Depth, RatioToOne = Ratio };
        public void ApplyNow(TxNoiseGateSetup gate) { Mode = gate.ExpanderMode; Depth = gate.WdspDepthDb; Ratio = gate.RatioToOne; }
    }
}
