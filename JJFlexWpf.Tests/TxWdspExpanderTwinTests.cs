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
/// Sprint 49 Track I phase 2: the audition twin with the expander in WDSP's
/// mode (a helper stage, run unkeyed with the offline wait and counted for
/// completeness), its positive control (the same render forced keyed is
/// incomplete, because the check sees the latch), and the threshold mapping
/// calibrated through the real helper. Windowless: MTA asserted in every test.
/// </summary>
[Collection("Transmit conditioning statics")]
public sealed class TxWdspExpanderTwinTests
{
    private const int Rate = 48000;
    private readonly ITestOutputHelper _out;

    public TxWdspExpanderTwinTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    private static TxNoiseSetup WdspSetup() => new()
    {
        Spectral = new TxNoiseSlotSetup { Enabled = false },
        Neural = new TxNoiseSlotSetup { Enabled = false },
        Gate = new TxNoiseGateSetup { Enabled = true, AutoThreshold = false, ThresholdDb = -40f, ExpanderMode = ExpanderModes.Wdsp, WdspDepthDb = 20f },
    };

    private static ChainAuditionTwin Twin(TxNoiseSetup setup, WdspHost? expanderHost, WdspHost catalogHost) =>
        new(() => setup, () => null,
            () => new ChainAuditionTwin.RenderEngines(DenoiseEngineCatalog.ForOfflineRender(catalogHost), null, expanderHost),
            TimeSpan.FromSeconds(20));

    [Fact]
    public void A_WDSP_mode_render_through_the_stub_is_complete_and_delays_the_take_by_its_bank()
    {
        using var rig = new HelperTestRig();
        using var trace = new HelperTestRig.TraceCapture();
        var take = ChainAuditionTwinTests.Take(3);
        var r = Twin(WdspSetup(), rig.Host, rig.Host).Process(take, Rate, CancellationToken.None, null);
        _out.WriteLine(string.Join(Environment.NewLine, trace.Lines.Where(l => l.Contains("ChainAuditionTwin"))));
        Assert.True(r.Available, "unavailable: " + r.UnavailableKey);
        int delay = 2448 + WdspPipelineDepth.Dexp * ChainAuditionTwin.BufferFrames;
        Assert.Equal(delay, r.LatencySamples);
        Assert.Contains(TxAudioConditioner.StageIds.Expander, r.Stages);
        // The stub echoes: the render is the take at the bank's delay, sample for sample.
        for (int i = delay; i < take.Length; i++) Assert.Equal(take[i - delay], r.Processed[i], 6);
    }

    [Fact]
    public void The_same_render_forced_through_the_keyed_pass_is_detected_as_incomplete()
    {
        using var rig = new HelperTestRig();
        using var trace = new HelperTestRig.TraceCapture();
        var twin = Twin(WdspSetup(), rig.Host, rig.Host);
        twin.ForceKeyedExpanderPassForTest = true;
        var r = twin.Process(ChainAuditionTwinTests.Take(3), Rate, CancellationToken.None, null);
        _out.WriteLine(string.Join(Environment.NewLine, trace.Lines.Where(l => l.Contains("ChainAuditionTwin"))));
        Assert.False(r.Available);
        Assert.Equal(ChainAuditionTwin.IncompleteKey, r.UnavailableKey);
        Assert.Contains(trace.Lines, l => l.Contains("forced keyed") && !l.Contains("latched 0,"));
    }

    [Fact]
    public void With_no_helper_host_a_WDSP_mode_render_is_unavailable_never_partly_dry()
    {
        using var rig = new HelperTestRig();
        var r = Twin(WdspSetup(), null, rig.Host).Process(ChainAuditionTwinTests.Take(2), Rate, CancellationToken.None, null);
        Assert.False(r.Available);
        Assert.Equal(ChainAuditionTwin.EngineUnavailableKey, r.UnavailableKey);
    }

    /// <summary>One tone through our expander and through DEXP: its steady gain at the end, dB (0 open, the depth closed).</summary>
    private static (double Jj, double Wdsp) GainsAt(WdspHost host, float thresholdDb, double toneDb, bool sideFilter)
    {
        const int n = Rate * 2;
        float amp = (float)Math.Pow(10, toneDb / 20.0);
        var mono = new float[n];
        for (int i = Rate; i < n; i++) mono[i] = amp * (float)Math.Sin(2 * Math.PI * 1000 * i / Rate);   // a second of silence, then the tone

        // Ours: the gate setting's fixed depth, at the same threshold.
        var gate = new TxNoiseGate { Enabled = true, GateSetting = true, RangeDb = 20f, ThresholdDb = thresholdDb, HoldMs = 50f, ReleaseMs = 50f,
            SideChainEnabled = sideFilter, LookAheadMs = 0f };
        var jjOut = new float[n];
        var st = new float[960];
        for (int at = 0; at < n; at += 480)
        {
            for (int f = 0; f < 480; f++) { st[2 * f] = mono[at + f]; st[2 * f + 1] = mono[at + f]; }
            gate.Process(st, st.Length, Rate);
            for (int f = 0; f < 480; f++) jjOut[at + f] = st[2 * f];
        }

        // DEXP, through the real helper, with the mapped threshold.
        var setup = new TxNoiseGateSetup { WdspDepthDb = 20f, SideChainFilter = sideFilter, AttackMs = 3f, HoldMs = 50f, ReleaseMs = 50f };
        using var bank = new PerChannelDenoiserBank(() => new WdspHelperEngine(host, TxWdspExpander.EngineId, DenoiseDirection.Transmit,
            TxWdspExpander.ValuesFor(setup, thresholdDb), offlineWait: true), 1);
        Assert.True(bank.IsAvailable, "the real helper's dexp did not open");
        var wOut = (float[])mono.Clone();
        for (int at = 0; at < n; at += 480) bank.Process(wOut.AsSpan(at, 480), 1f);
        int d = bank.LatencySamples;

        double Gain(float[] y, int lag)
        {
            double num = 0, den = 0;
            for (int i = n - Rate / 4; i < n; i++) { num += (double)y[i] * y[i]; den += (double)mono[i - lag] * mono[i - lag]; }
            return 10 * Math.Log10(num / den);
        }
        return (Gain(jjOut, 0), Gain(wOut, d));
    }

    /// <summary>Whether our expander, at a threshold, opens for a steady tone of a peak level.</summary>
    private static bool OursOpens(double toneDb, float threshold, bool filter, double hz)
    {
        const int n = Rate * 2;
        float amp = (float)Math.Pow(10, toneDb / 20.0);
        var gate = new TxNoiseGate { Enabled = true, GateSetting = true, RangeDb = 20f, ThresholdDb = threshold, HoldMs = 50f, ReleaseMs = 50f,
            SideChainEnabled = filter, LookAheadMs = 0f };
        var st = new float[960];
        var copy = new float[960];
        double num = 0, den = 0;
        for (int at = 0; at < n; at += 480)
        {
            for (int f = 0; f < 480; f++) { int i = at + f; float s = i >= Rate ? amp * (float)Math.Sin(2 * Math.PI * hz * i / Rate) : 0f; st[2 * f] = s; st[2 * f + 1] = s; }
            Array.Copy(st, copy, st.Length);
            gate.Process(st, st.Length, Rate);
            if (at >= n - Rate / 4) for (int f = 0; f < 480; f++) { num += st[2 * f] * st[2 * f]; den += copy[2 * f] * copy[2 * f]; }
        }
        return num > 0.25 * den;
    }

    [Theory]
    [InlineData(300.0, false)]
    [InlineData(1000.0, false)]
    [InlineData(2000.0, false)]
    [InlineData(1000.0, true)]
    public void Our_detector_reads_a_steady_tone_the_measured_bias_above_its_peak(double hz, bool filter)
    {
        double closed = -60, open = -20;
        while (open - closed > 0.01) { double mid = (closed + open) / 2; if (OursOpens(mid, -40f, filter, hz)) open = mid; else closed = mid; }
        _out.WriteLine($"{hz} Hz, side-chain filter {(filter ? "on" : "off")}: a -40 dB threshold opens for a tone of peak {open:F2} dB");
        Assert.InRange(-40.0 - open, TxWdspExpander.OurToneBiasDb - 0.25, TxWdspExpander.OurToneBiasDb + 0.25);
    }

    [Fact]
    public void The_threshold_mapping_puts_one_tone_at_the_same_threshold_in_both_modes()
    {
        string? helper = HelperLifecycleTests.ExplicitRealHelper(out string notRun);
        if (helper == null) { _out.WriteLine("NOT RUN: " + notRun); return; }
        using var rig = new HelperTestRig(path: helper);
        const float threshold = -40f;
        // Where ours opens for a steady tone (pinned by the test above): the same
        // tone must open WDSP's at the mapped threshold, and the same tone a
        // decibel quieter must open neither.
        double opening = threshold - TxWdspExpander.OurToneBiasDb;
        foreach (bool filter in new[] { false, true })
        {
            var above = GainsAt(rig.Host, threshold, opening + 1.0, filter);
            var below = GainsAt(rig.Host, threshold, opening - 1.0, filter);
            _out.WriteLine($"side filter {(filter ? "on" : "off")}, threshold {threshold} dB: a tone of peak {opening + 1.0:F1} dB: ours {above.Jj:F2} dB, WDSP's {above.Wdsp:F2} dB; of peak {opening - 1.0:F1} dB: ours {below.Jj:F2} dB, WDSP's {below.Wdsp:F2} dB");
            Assert.True(above.Jj > -1 && above.Wdsp > -1, "1 dB above: one of the two stayed closed");
            Assert.True(below.Jj < -15 && below.Wdsp < -15, "1 dB below: one of the two opened");
        }
    }
}
