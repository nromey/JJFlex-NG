using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using JJFlexWpf.Tests.Infrastructure;
using JJPortaudio;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// The audition's offline twin of the transmit chain (#262, Sprint 49 Track Q
/// phase B, item 2): it renders exactly what the live chain would send, the
/// threshold it used and where that came from, refuses a partly dry render,
/// tears itself down whatever happens, and never touches the live chain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless and silent.</b> Every test asserts
/// <see cref="NoiseReductionPerChannelTests.AssertWindowless"/>. Takes are
/// synthetic samples; engines are fakes, the real in-process engines, the MIT
/// stub helper (<see cref="HelperTestRig"/>), or the real helper when
/// JJFLEX_WDSP_HELPER names one. Nothing is played, no microphone is opened, no
/// radio is used. The live-chain test joins the transmit statics collection and
/// puts every static back.
/// </para>
/// </remarks>
[Collection("Transmit conditioning statics")]
public sealed class ChainAuditionTwinTests
{
    private const int Rate = 48000;
    private readonly ITestOutputHelper _out;

    public ChainAuditionTwinTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    // ── Takes and setups ────────────────────────────────────────────────

    /// <summary>Speech-like bursts (0.6 s on, 0.9 s off, from 0.5 s) over steady room noise about 50 dB down.</summary>
    internal static float[] Take(double seconds, int seed = 262, double noiseDb = -50)
    {
        int n = (int)(seconds * Rate);
        var rng = new Random(seed);
        var x = new float[n];
        double noiseAmp = Math.Pow(10, noiseDb / 20.0) * Math.Sqrt(3);
        double speechAmp = Math.Pow(10, -20 / 20.0) / 0.844;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double s = (rng.NextDouble() * 2 - 1) * noiseAmp;
            double into = (t - 0.5) % 1.5;
            if (t >= 0.5 && into < 0.6)
            {
                double voice = 0;
                for (int k = 1; k <= 4; k++) voice += Math.Sin(2 * Math.PI * 140 * k * t) / k;
                s += speechAmp * (0.75 + 0.25 * Math.Sin(2 * Math.PI * 4 * t)) * voice;
            }
            x[i] = (float)s;
        }
        return x;
    }

    internal static TxNoiseSetup Setup(string? spectral = null, string? neural = null, bool gate = false, bool auto = true,
        float margin = 8f, float byHand = -40f) => new()
    {
        Spectral = new TxNoiseSlotSetup { Enabled = spectral != null, EngineId = spectral ?? "" },
        Neural = new TxNoiseSlotSetup { Enabled = neural != null, EngineId = neural ?? "" },
        Gate = new TxNoiseGateSetup { Enabled = gate, AutoThreshold = auto, ThresholdMarginDb = margin, ThresholdDb = byHand },
    };

    private static ChainAuditionTwin Twin(TxNoiseSetup setup, Func<DenoiseEngineCatalog> catalog, AuditionLiveState? live = null,
        TimeSpan? wait = null) => new(() => setup, () => live, catalog, wait ?? TimeSpan.FromSeconds(20));

    private static string LibraryPath(string file) =>
        Path.Combine(RepoPaths.RequireRoot(), "runtimes", Environment.Is64BitProcess ? "win-x64" : "win-x86", "native", file);

    /// <summary>
    /// The offline catalog with NR4 pointed at the repository's own library:
    /// the test output carries no specbleach.dll, so the app's locator finds
    /// none here. Everything else is <see cref="DenoiseEngineCatalog.ForOfflineRender"/>'s.
    /// </summary>
    private static DenoiseEngineCatalog RealCatalog(WdspHost host)
    {
        string nr4 = LibraryPath(SpecbleachEngine.LibraryFileName);
        return new DenoiseEngineCatalog(DenoiseEngineCatalog.ForOfflineRender(host).Entries.Select(e => e.Id != "nr4" ? e : e with
        {
            Factory = (initial, _) => new SpecbleachEngine(initial, nr4, estimator: SpecbleachEngine.EstimatorFrom(initial)),
            Probe = () => SpecbleachEngine.ProbeAt(nr4),
        }));
    }

    /// <summary>A catalog whose two transmit rows are one fake engine each.</summary>
    private static DenoiseEngineCatalog FakeCatalog(Func<IDenoiseEngine> spectral, Func<IDenoiseEngine> neural,
        Unavailability neuralProbe = Unavailability.None) => new(new[]
    {
        new DenoiseEngineEntry("nr4", DenoiseSide.Pc, DenoiseFamily.Spectral, "audio.denoise.nr4", new[] { DenoiseDirection.Transmit },
            (_, _) => spectral(), () => Unavailability.None),
        new DenoiseEngineEntry("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.rnnoise", new[] { DenoiseDirection.Transmit },
            (_, _) => neural(), () => neuralProbe),
    });

    /// <summary>A fake engine: halves its input, at no delay. Late from frame <c>LateAt</c> on, or throws there.</summary>
    internal sealed class Fake : IDenoiseEngine
    {
        private int _frame;
        public int LateAt = -1, ThrowAt = -1, MovesAt = -1;
        public string Id { get; init; } = "fake";
        public bool IsAvailable => true;
        public Unavailability Unavailability => Unavailability.None;
        public string UnavailabilityDetail => "";
        public int SampleRate => Rate;
        public int FrameLength => 480;
        public int LatencySamples => MovesAt >= 0 && _frame > MovesAt ? 480 : 0;
        public IReadOnlyList<EngineSettingDescriptor> Settings => Array.Empty<EngineSettingDescriptor>();
        public EngineSettingValue GetSetting(string name) => throw new ArgumentException(name);
        public bool TrySetSetting(string name, EngineSettingValue value) => false;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            int f = _frame++;
            if (f == ThrowAt) throw new InvalidOperationException("the fake engine failed");
            for (int i = 0; i < input.Length; i++) output[i] = input[i] * 0.5f;
            return LateAt < 0 || f < LateAt;
        }
        public void Reset() { }
        public void Dispose() { }
    }

    private static int Lives(HelperTestRig rig) => rig.Host.CurrentConnection?.Lives.Length ?? 0;

    // ── The positive control the Track Q brief asked for ────────────────

    /// <summary>
    /// For the in-process engines (NR4, RNNoise), both slots and the gate on,
    /// the twin's output equals, sample for sample, a conditioner built the way
    /// <see cref="TxAudioConditioning.Attach"/> builds it: one pass, the noise
    /// reducer calling <see cref="TxNoiseChain.Process"/> with the conditioner's
    /// key state, then the gate, the same gate in force and the same threshold,
    /// on the same take in the same 480-frame buffers.
    /// </summary>
    /// <remarks>
    /// <b>How the two are primed alike.</b> The live shape is keyed from its
    /// first buffer, as on the air, so its gate resets open at that key-down
    /// exactly as the twin's gate pass does. Keyed, a transmit slot adopts no
    /// on-or-off, no whole setup's values and no delivered build, so before the
    /// take the live shape is given what a receiving radio gives it between
    /// overs: one unkeyed boundary with no audio (the chain's own
    /// <c>Process</c> with a count of 0, which adopts the slots' on and off),
    /// then each slot one unkeyed call with an EMPTY buffer, which adopts the
    /// setup's values and installs any delivered build while processing no
    /// sample (a bank given no frames does nothing). The twin runs unkeyed
    /// throughout, so its first buffer does the same adoption. Neither engine
    /// sees a sample the other does not.
    /// </remarks>
    [Fact]
    public void The_twin_equals_the_live_single_pass_sample_for_sample_for_the_in_process_engines()
    {
        using var rig = new HelperTestRig();
        var catalog = RealCatalog(rig.Host);
        bool rnnoise = catalog.Find("rnnoise")!.Availability() == Unavailability.None;
        Assert.Equal(Unavailability.None, catalog.Find("nr4")!.Availability());
        if (!rnnoise)
        {
            Assert.False(Environment.Is64BitProcess, "RNNoise must load on x64: " + catalog.Find("rnnoise")!.Availability());
            _out.WriteLine("x86: RNNoise cannot load here (#739), so the control runs NR4 and the gate only.");
        }
        var setup = Setup(spectral: "nr4", neural: rnnoise ? "rnnoise" : null, gate: true, margin: 8f);
        var take = Take(8);

        var twin = Twin(setup, () => RealCatalog(rig.Host)).Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        Assert.True(twin.Available, twin.UnavailableKey);
        Assert.Equal(AuditionThresholdSource.FromThisTake, twin.ThresholdSource);
        Assert.True(twin.LatencySamples > 0);

        var live = LiveShape(setup, RealCatalog(rig.Host), take, twin.ThresholdDb!.Value);
        Assert.Equal(take.Length, twin.Processed.Length);
        for (int i = 0; i < take.Length; i++)
            if (twin.Processed[i] != live[i])
                Assert.Fail($"sample {i}: twin {twin.Processed[i]:R}, live {live[i]:R}");

        // The comparison could have failed: the chain changed the take, and a
        // gate 3 dB higher in the live shape changes the output.
        Assert.Contains(Enumerable.Range(0, take.Length), i => twin.Processed[i] != take[i]);
        var higher = LiveShape(setup, RealCatalog(rig.Host), take, twin.ThresholdDb!.Value + 3f);
        Assert.Contains(Enumerable.Range(0, take.Length), i => higher[i] != live[i]);
        _out.WriteLine($"equal over {take.Length} samples; delay {twin.LatencySamples}, threshold {twin.ThresholdDb:0.0} dB from a floor of {twin.ThresholdFloorLufs:0.0} LUFS");
    }

    /// <summary>The live path's shape: <see cref="TxAudioConditioning.Attach"/>'s wiring on a conditioner of our own, keyed.</summary>
    private static float[] LiveShape(TxNoiseSetup setup, DenoiseEngineCatalog catalog, float[] take, float threshold)
    {
        var chain = new TxNoiseChain(catalog);
        try
        {
            chain.ApplySlotsNow(setup.Spectral, setup.Neural);
            HelperTestRig.Until(() => (!setup.Spectral.Enabled || chain.SpectralSlot.RunningIsCurrent(setup.Spectral.EngineId)
                                       || chain.SpectralSlot.BuildDelivered(setup.Spectral.EngineId))
                                   && (!setup.Neural.Enabled || chain.NeuralSlot.RunningIsCurrent(setup.Neural.EngineId)
                                       || chain.NeuralSlot.BuildDelivered(setup.Neural.EngineId)), "the live shape's engines");
            // The unkeyed boundary between overs, carrying no audio (see the remarks above).
            chain.Process(new float[2], 0, Rate, keyed: false);
            chain.SpectralSlot.Process(Array.Empty<float>(), true, true, false, null);
            chain.NeuralSlot.Process(Array.Empty<float>(), true, true, false, null);

            // Through the live wiring itself (repair 1, Codex's drift caveat): the
            // same PlugNoiseStage Attach calls, and the same NoiseStage body NrStage
            // runs, so a change to either reaches this control. The one line not
            // shared is where the key state comes from: Attach's NrStage reads the
            // rig's conditioner, which is this conditioner.
            var cond = new TxAudioConditioner();
            int applied = 0;
            TxAudioConditioning.PlugNoiseStage(cond, () => true,
                (buffer, count, rate) => applied = TxAudioConditioning.NoiseStage(chain, cond.Keyed, buffer, count, rate),
                () => applied);
            cond.SetGateInForce(TxAudioConditioning.ToConditioner(setup.Gate, setup.Gate.AutoThreshold, setup.Gate.ThresholdMarginDb));
            if (setup.Gate.AutoThreshold) cond.Gate.ThresholdDb = threshold;

            var output = new float[take.Length];
            var stereo = new float[960];
            for (int at = 0; at < take.Length; at += 480)
            {
                for (int f = 0; f < 480; f++) { float s = at + f < take.Length ? take[at + f] : 0f; stereo[2 * f] = s; stereo[2 * f + 1] = s; }
                cond.Process(stereo, stereo.Length, Rate);
                for (int f = 0; f < 480 && at + f < take.Length; f++) output[at + f] = stereo[2 * f];
            }
            return output;
        }
        finally
        {
            foreach (var slot in new[] { chain.SpectralSlot, chain.NeuralSlot }) { slot.CloseAdmission(); slot.DisposeOwned(); }
        }
    }

    // ── Helper engines through the stub, and the real helper ────────────

    [Fact]
    public void A_helper_engine_offline_is_complete_late_only_by_its_pipeline_and_delays_the_take_exactly()
    {
        using var rig = new HelperTestRig();
        using var trace = new HelperTestRig.TraceCapture();
        var take = Take(6);
        var r = Twin(Setup(neural: "nnr"), () => DenoiseEngineCatalog.ForOfflineRender(rig.Host))
            .Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        Assert.True(r.Available, r.UnavailableKey + " " + string.Join(", ", r.UnavailableArgs));
        int k = WdspPipelineDepth.For("nnr", null, DenoiseDirection.Transmit);
        Assert.Equal(2448 + k * 480, r.LatencySamples);   // the stub's delay plus the pipeline
        // Late only by its start-up: k frames of pipeline, and the answers covering
        // the stub's own 2,448-sample delay, which the engine discards (6 frames).
        // The brief said "exactly its PipelineDepth"; HelperEngineTests' own offline
        // test shows the first 8 frames unanswered for k = 2, and this is why.
        int startUp = k + (2448 + 479) / 480;
        Assert.True(trace.Has(l => l.Contains("Neural nnr: engine frames") && l.Contains($"late {startUp} (expected {startUp}), fallback 0, latched 0")),
            "the twin's count line: " + string.Join(" | ", trace.Lines.Where(l => l.Contains("ChainAuditionTwin"))));
        // The stub returns its input delayed: so the render is the take, delayed by the reported latency.
        for (int i = r.LatencySamples; i < take.Length; i++)
            Assert.True(Math.Abs(r.Processed[i] - take[i - r.LatencySamples]) < 1e-6f, "sample " + i);
    }

    [Fact]
    public void The_same_render_through_the_live_catalogs_shape_is_refused_as_incomplete()
    {
        // Positive control for the completeness check: the live catalog's helper
        // never waits, so a faster-than-real-time caller gets late frames, and the
        // bank sends them dry. The check must see that and refuse the render.
        using var rig = new HelperTestRig();
        DenoiseEngineCatalog LiveShaped() => new(DenoiseEngineCatalog.ForOfflineRender(rig.Host).Entries.Select(e => e.Id != "nnr" ? e : e with
        {
            Factory = (initial, direction) => new WdspHelperEngine(rig.Host, "nnr", direction, initial, null, offlineWait: false),
        }));
        var r = Twin(Setup(neural: "nnr"), LiveShaped).Process(Take(6), Rate, CancellationToken.None, null);
        Assert.False(r.Available);
        Assert.Equal(ChainAuditionTwin.IncompleteKey, r.UnavailableKey);
        Assert.Contains(("engine", (object?)Lexicon.Get("audio.denoise.nnr")), r.UnavailableArgs);
    }

    [Fact]
    public void With_the_real_helper_one_NNR_render_is_complete()
    {
        string? helper = HelperLifecycleTests.RealHelper();
        if (helper == null) { _out.WriteLine("NOT RUN: no real helper on this machine (set JJFLEX_WDSP_HELPER)"); return; }
        using var rig = new HelperTestRig(path: helper);
        using var trace = new HelperTestRig.TraceCapture();
        var r = Twin(Setup(neural: "nnr", gate: true), () => DenoiseEngineCatalog.ForOfflineRender(rig.Host))
            .Process(Take(6), Rate, CancellationToken.None, null);
        Assert.True(r.Available, r.UnavailableKey + " " + string.Join(", ", r.UnavailableArgs));
        string line = trace.Lines.First(l => l.Contains("Neural nnr: engine frames"));
        _out.WriteLine((Environment.Is64BitProcess ? "x64: " : "x86: ") + line);
        Assert.Contains("fallback 0, latched 0", line);
        Assert.Matches(@"late (\d+) \(expected \1\)", line);
    }

    // ── The threshold ───────────────────────────────────────────────────

    [Fact]
    public void Automatic_and_long_enough_is_derived_from_the_takes_own_floor_and_the_verdict_says_so()
    {
        var setup = Setup(gate: true, margin: 8f);
        var twin = Twin(setup, () => FakeCatalog(() => new Fake(), () => new Fake()), live: Live(-30f));
        var take = Take(8);
        var r = twin.Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        Assert.Equal(AuditionThresholdSource.FromThisTake, r.ThresholdSource);
        var floor = ChainAudition.Measure(take, take.Length, Rate);
        Assert.True(floor.IsValid);
        Assert.Equal(floor.NoiseFloorLufs, r.ThresholdFloorLufs!.Value, 3);
        Assert.Equal(floor.NoiseFloorLufs + 8f, r.ThresholdDb!.Value, 3);

        var result = ChainAudition.Analyse(take, Rate, twin);
        string v = ChainAudition.Verdict(result);
        Assert.Contains(Lexicon.Get("audio.audition.verdict.threshold_from_take", ("floor", r.ThresholdFloorLufs.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))), v);
        Assert.Contains(Lexicon.Get("audio.audition.verdict.live_threshold", ("threshold", "-30.0")), v);
    }

    [Fact]
    public void The_floor_is_measured_after_the_noise_reduction_where_the_live_meter_sits()
    {
        // Both fake engines halve: the noise pass takes 12 dB off everything, so
        // the floor the threshold comes from is the raw take's less 12 dB, the
        // figure the live meter would read on an over with the gate open.
        var take = Take(8);
        var setup = Setup(spectral: "nr4", neural: "rnnoise", gate: true, margin: 8f);
        setup.Spectral.Engines.Add(new DenoiseEngineSettings { EngineId = "nr4", Direction = DenoiseDirection.Transmit, Strength = 1f });
        setup.Neural.Engines.Add(new DenoiseEngineSettings { EngineId = "rnnoise", Direction = DenoiseDirection.Transmit, Strength = 1f });
        var r = Twin(setup, () => FakeCatalog(() => new Fake(), () => new Fake()))
            .Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        var quarter = take.Select(s => s * 0.25f).ToArray();
        var expected = ChainAudition.Measure(quarter, quarter.Length, Rate).NoiseFloorLufs;
        Assert.Equal(expected, r.ThresholdFloorLufs!.Value, 2);
        Assert.NotEqual(ChainAudition.Measure(take, take.Length, Rate).NoiseFloorLufs, r.ThresholdFloorLufs!.Value, 1);
    }

    [Fact]
    public void Automatic_but_too_short_uses_the_live_figure_and_with_no_radio_the_default()
    {
        var setup = Setup(gate: true);
        var shortTake = Take(2);
        var withRadio = Twin(setup, () => FakeCatalog(() => new Fake(), () => new Fake()), live: Live(-33f))
            .Process(shortTake, Rate, CancellationToken.None, null);
        Assert.Equal(AuditionThresholdSource.LiveFigure, withRadio.ThresholdSource);
        Assert.Equal(-33f, withRadio.ThresholdDb);
        Assert.Null(withRadio.ThresholdFloorLufs);

        var noRadio = Twin(setup, () => FakeCatalog(() => new Fake(), () => new Fake()))
            .Process(shortTake, Rate, CancellationToken.None, null);
        Assert.Equal(AuditionThresholdSource.Default, noRadio.ThresholdSource);
        Assert.Equal(TxNoiseGate.DefaultThresholdDb, noRadio.ThresholdDb);
    }

    [Fact]
    public void By_hand_is_the_setups_figure_and_the_verdict_says_so()
    {
        var twin = Twin(Setup(gate: true, auto: false, byHand: -36f), () => FakeCatalog(() => new Fake(), () => new Fake()), live: Live(-30f));
        var r = twin.Process(Take(8), Rate, CancellationToken.None, null);
        Assert.Equal((AuditionThresholdSource?)AuditionThresholdSource.SetByHand, r.ThresholdSource);
        Assert.Equal(-36f, r.ThresholdDb);
        string v = ChainAudition.Verdict(ChainAudition.Analyse(Take(8), Rate, twin));
        Assert.Contains(Lexicon.Get("audio.audition.verdict.threshold_by_hand"), v);
        Assert.DoesNotContain(Lexicon.Get("audio.audition.verdict.live_threshold", ("threshold", "-30.0")), v);
    }

    [Fact]
    public void The_configuration_is_read_when_the_twin_runs_not_when_it_was_made()
    {
        var setup = Setup(gate: true, auto: false, byHand: -36f);
        var twin = new ChainAuditionTwin(() => setup, () => null, () => FakeCatalog(() => new Fake(), () => new Fake()), TimeSpan.FromSeconds(5));
        Assert.Equal(-36f, twin.Process(Take(4), Rate, CancellationToken.None, null).ThresholdDb);
        setup = Setup(gate: true, auto: false, byHand: -31f);
        Assert.Equal(-31f, twin.Process(Take(4), Rate, CancellationToken.None, null).ThresholdDb);
    }

    private static AuditionLiveState Live(float db, bool measured = true) => new(new AuditionLiveGate(true, db, measured), "USB", false);

    // ── The live side, read for the verdict (items 5 and 6) ─────────────

    [Fact]
    public void The_live_state_says_measured_only_when_the_live_rule_would_act_and_follows_the_mode_policy()
    {
        Assert.True(ChainAuditionTwin.LiveState(true, -40f, true, -55f, 8f, "USB").Gate.OverMeasured);
        Assert.False(ChainAuditionTwin.LiveState(true, -40f, false, -55f, 8f, "USB").Gate.OverMeasured);
        Assert.False(ChainAuditionTwin.LiveState(true, -40f, true, LufsMeter.Floor, 8f, "USB").Gate.OverMeasured);
        Assert.True(ChainAuditionTwin.LiveState(true, -40f, true, -55f, 8f, "CW").ChainStandsAside);
        Assert.True(ChainAuditionTwin.LiveState(true, -40f, true, -55f, 8f, "DIGU").ChainStandsAside);
        Assert.False(ChainAuditionTwin.LiveState(true, -40f, true, -55f, 8f, "LSB").ChainStandsAside);
        Assert.Null(ChainAuditionTwin.ReadLive(null));
    }

    // ── Nothing in the chain on, the rate guard, unavailable engines ────

    [Fact]
    public void With_nothing_on_nothing_is_built_or_rendered()
    {
        int built = 0;
        var r = Twin(Setup(), () => FakeCatalog(() => { built++; return new Fake(); }, () => { built++; return new Fake(); }))
            .Process(Take(4), Rate, CancellationToken.None, null);
        Assert.True(r.NothingOn);
        Assert.False(r.Available);
        Assert.Equal(0, built);
    }

    [Fact]
    public void A_take_at_another_rate_is_never_called_cleaned()
    {
        var r = Twin(Setup(neural: "rnnoise"), () => FakeCatalog(() => new Fake(), () => new Fake()))
            .Process(new float[44100], 44100, CancellationToken.None, null);
        Assert.False(r.Available);
        Assert.Equal(ChainAuditionTwin.WrongRateKey, r.UnavailableKey);
    }

    [Fact]
    public void An_engine_reported_unavailable_is_said_at_once_with_its_name_and_the_apps_own_reason()
    {
        var r = Twin(Setup(neural: "rnnoise"), () => FakeCatalog(() => new Fake(), () => new Fake(), Unavailability.ModelMissing))
            .Process(Take(4), Rate, CancellationToken.None, null);
        Assert.Equal(ChainAuditionTwin.EngineUnavailableKey, r.UnavailableKey);
        Assert.Contains(("engine", (object?)Lexicon.Get("audio.denoise.rnnoise")), r.UnavailableArgs);
        Assert.Contains(("reason", (object?)Lexicon.Get("audio.denoise.unavailable.modelmissing")), r.UnavailableArgs);
    }

    [Fact]
    public void A_build_that_fails_is_said_at_once_not_at_the_time_limit()
    {
        var clock = Stopwatch.StartNew();
        var r = Twin(Setup(neural: "rnnoise"), () => FakeCatalog(() => new Fake(), () => throw new InvalidOperationException("no engine")),
            wait: TimeSpan.FromSeconds(30)).Process(Take(4), Rate, CancellationToken.None, null);
        Assert.Equal(ChainAuditionTwin.EngineFailedKey, r.UnavailableKey);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the failure waited for the limit: " + clock.Elapsed);
    }

    // ── Incomplete renders ──────────────────────────────────────────────

    [Theory]
    [InlineData("late")]
    [InlineData("throws")]
    [InlineData("none")]
    public void An_engine_late_or_failing_mid_take_is_refused_naming_it_never_a_cleaned_render(string fault)
    {
        var r = Twin(Setup(spectral: "nr4", neural: "rnnoise"), () => FakeCatalog(() => new Fake(),
                () => new Fake { LateAt = fault == "late" ? 300 : -1, ThrowAt = fault == "throws" ? 300 : -1 }))
            .Process(Take(8), Rate, CancellationToken.None, null);
        if (fault == "none")
        {
            Assert.True(r.Available, r.UnavailableKey);   // positive control: the same chain, healthy, renders
            return;
        }
        Assert.False(r.Available);
        Assert.Equal(ChainAuditionTwin.IncompleteKey, r.UnavailableKey);
        Assert.Contains(("engine", (object?)Lexicon.Get("audio.denoise.rnnoise")), r.UnavailableArgs);
    }

    [Fact]
    public void A_delay_that_moves_part_way_is_refused_as_incomplete()
    {
        // Every frame produced, none late: only the delay jumped, which shifts the
        // rest of the take against the raw one. Never presented as the chain's.
        var r = Twin(Setup(neural: "rnnoise"), () => FakeCatalog(() => new Fake(), () => new Fake { MovesAt = 300 }))
            .Process(Take(8), Rate, CancellationToken.None, null);
        Assert.Equal(ChainAuditionTwin.IncompleteKey, r.UnavailableKey);
    }

    [Fact]
    public void A_slow_buffer_in_the_gate_pass_is_never_taken_for_a_stream_restart()
    {
        // The gate pass is one keyed over, so a pause of more than 100 ms between
        // offline buffers (a busy machine) cannot reset the gate open mid-take,
        // as a gap in an unkeyed stream would (TxNoiseGate.StreamGapMs).
        var setup = Setup(gate: true, margin: 8f);
        var take = Take(8);
        var smooth = Twin(setup, () => FakeCatalog(() => new Fake(), () => new Fake()))
            .Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        int calls = 0;
        var paused = Twin(setup, () => FakeCatalog(() => new Fake(), () => new Fake()))
            .Process((float[])take.Clone(), Rate, CancellationToken.None, p => { if (p > 0.5 && ++calls % 4 == 0) Thread.Sleep(130); });
        Assert.True(calls >= 20, "too few pauses to land in a closed stretch");
        Assert.Equal(smooth.Processed, paused.Processed);
    }

    // ── Teardown ────────────────────────────────────────────────────────

    [Fact]
    public void After_a_render_succeeds_the_helper_holds_no_channel_of_the_twins_and_held_one_during_it()
    {
        using var rig = new HelperTestRig();
        int during = 0;
        var r = Twin(Setup(neural: "nnr"), () => DenoiseEngineCatalog.ForOfflineRender(rig.Host))
            .Process(Take(4), Rate, CancellationToken.None, _ => during = Math.Max(during, Lives(rig)));
        Assert.True(r.Available, r.UnavailableKey);
        Assert.Equal(1, during);   // positive control
        Assert.Equal(0, Lives(rig));
    }

    [Fact]
    public void After_a_refused_render_the_helper_holds_no_channel()
    {
        using var rig = new HelperTestRig();
        DenoiseEngineCatalog LiveShaped() => new(DenoiseEngineCatalog.ForOfflineRender(rig.Host).Entries.Select(e => e.Id != "nnr" ? e : e with
        {
            Factory = (initial, direction) => new WdspHelperEngine(rig.Host, "nnr", direction, initial, null, offlineWait: false),
        }));
        int during = 0;
        var r = Twin(Setup(neural: "nnr"), LiveShaped).Process(Take(4), Rate, CancellationToken.None, _ => during = Math.Max(during, Lives(rig)));
        Assert.Equal(ChainAuditionTwin.IncompleteKey, r.UnavailableKey);
        Assert.Equal(1, during);
        Assert.Equal(0, Lives(rig));
    }

    [Fact]
    public void After_a_cancel_the_helper_holds_no_channel()
    {
        using var rig = new HelperTestRig();
        using var cts = new CancellationTokenSource();
        int during = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => Twin(Setup(neural: "nnr"), () => DenoiseEngineCatalog.ForOfflineRender(rig.Host))
            .Process(Take(8), Rate, cts.Token, p => { during = Math.Max(during, Lives(rig)); if (p >= 0.3) cts.Cancel(); }));
        Assert.Equal(1, during);
        Assert.Equal(0, Lives(rig));
    }

    [Fact]
    public void After_a_build_that_misses_the_limit_the_helper_holds_no_channel_once_that_build_returns()
    {
        // The stub opens NNR's channel 1.5 s late; the twin waits 300 ms. Its
        // admission closes at the limit, so the build, arriving later, disposes
        // itself (PcDenoiseSlot.BuildChoice), and the channel it opened closes.
        using var rig = new HelperTestRig(_ => new[] { "--slow-open", "nnr:1500" });
        var r = Twin(Setup(neural: "nnr"), () => DenoiseEngineCatalog.ForOfflineRender(rig.Host), wait: TimeSpan.FromMilliseconds(300))
            .Process(Take(4), Rate, CancellationToken.None, null);
        Assert.Equal(ChainAuditionTwin.EngineNotReadyKey, r.UnavailableKey);
        Assert.Contains(("engine", (object?)Lexicon.Get("audio.denoise.nnr")), r.UnavailableArgs);
        // It did open, later (so this is not passing because nothing ever opened), and was closed.
        HelperTestRig.Until(() => Interlocked.Read(ref rig.Host.CloseChannelsSent) >= 1 && Lives(rig) == 0,
            "the late build's channel closed", ms: 15000);
    }

    // ── The live chain is untouched (never auto-apply) ──────────────────

    [Fact]
    public void The_twin_changes_nothing_in_the_live_chain_and_raises_no_intent_change()
    {
        using var x = new TxGateOwnerTests.Context();
        // A live intent worth disturbing: both slots on, the gate on by hand.
        x.Chain.SetEnabled(DenoiseFamily.Spectral, true);
        x.Chain.SetEnabled(DenoiseFamily.Neural, true);
        x.Chain.SetStrength("rnnoise", 0.7f);
        TxAudioConditioning.SetGateEnabled(true);
        TxAudioConditioning.SetAutoThreshold(false);
        TxAudioConditioning.GateThresholdDb = -37f;
        x.Buffer();

        var before = TxAudioConditioning.CaptureSetup("");
        var gateBefore = x.Cond.GateInForce;
        float thresholdBefore = x.Cond.Gate.ThresholdDb;
        int chainChanges = 0, appChanges = 0;
        Action onChain = () => chainChanges++, onApp = () => appChanges++;
        x.Chain.IntentChanged += onChain;
        TxAudioConditioning.IntentChanged += onApp;
        try
        {
            var twin = new ChainAuditionTwin(() => TxAudioConditioning.CaptureSetup(""), () => ChainAuditionTwin.LiveState(false, -37f, false, 0, 8f, "USB"),
                TxNoiseChainTests.Catalog, TimeSpan.FromSeconds(10));
            var r = twin.Process(Take(8), Rate, CancellationToken.None, null);
            Assert.True(r.Available, r.UnavailableKey);
            Assert.Equal(-37f, r.ThresholdDb);   // it rendered the live intent

            Assert.True(TxAudioConditioning.CaptureSetup("").SameContentAs(before));
            Assert.Same(gateBefore, x.Cond.GateInForce);
            Assert.Equal(thresholdBefore, x.Cond.Gate.ThresholdDb);
            Assert.True(x.Chain.WantsEnabled(DenoiseFamily.Spectral) && x.Chain.WantsEnabled(DenoiseFamily.Neural));
            Assert.Equal(0.7f, x.Chain.GetStrength("rnnoise"), 4);
            Assert.False(x.Chain.HasDeferredChanges);
            Assert.Equal(0, chainChanges);
            Assert.Equal(0, appChanges);

            // Positive control: a real change through the live chain raises it.
            x.Chain.SetEnabled(DenoiseFamily.Spectral, false);
            Assert.Equal(1, chainChanges);
        }
        finally
        {
            x.Chain.IntentChanged -= onChain;
            TxAudioConditioning.IntentChanged -= onApp;
        }
    }
}
