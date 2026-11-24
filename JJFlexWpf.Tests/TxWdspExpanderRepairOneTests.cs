using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using JJFlexWpf.Controls;
using JJPortaudio;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track I phase 2, repair round 1 (Codex's check of 41c50191):
/// B1, the controller's one gate comparison sees the expander's mode and
/// WDSP's depth (and every field added later), so presets, named setups and
/// the layer's Escape carry them; B2, Home's gate fields re-word themselves
/// when their meaning changes; B3, a lever WDSP's expander shares says "at
/// the unkey" while keyed in WDSP's mode; N1, the holder hands work off the
/// audio thread without allocating; N2, its notice is owed again only after
/// the engine has really worked. Windowless: MTA asserted in every test; the
/// one control test builds its fields unhosted on a private STA thread.
/// No helper process: WDSP's expander runs on an in-process fake engine.
/// </summary>
[Collection("Transmit conditioning statics")]
public sealed class TxWdspExpanderRepairOneTests
{
    private const string Layer = NoiseController.LayerSource;
    private const string Home = "home";
    private const int Rate = 48000;

    public TxWdspExpanderRepairOneTests() => NoiseReductionPerChannelTests.AssertWindowless();

    /// <summary>An in-process engine with dexp's frame: it echoes at a known delay, or is late, or fails, as told.</summary>
    internal sealed class FakeExpanderEngine : IDenoiseEngine
    {
        public volatile bool Late, Fail, Available = true;
        private readonly float[] _line = new float[480 + 96];
        public string Id => TxWdspExpander.EngineId;
        public int SampleRate => Rate;
        public int FrameLength => 480;
        public bool IsAvailable => Available;
        public int LatencySamples => 96;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            if (Fail) throw new InvalidOperationException("told to fail");
            if (Late) return false;
            input.CopyTo(output);
            return true;
        }
        public void Reset() { }
        public void Dispose() { }
    }

    /// <summary>The owner's statics and the controller bridge, with WDSP's expander on a fake engine; every static put back.</summary>
    private sealed class Bridge : IDisposable
    {
        private static readonly FieldInfo WdspField = typeof(TxAudioConditioning).GetField("_wdsp", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? _savedWdsp = WdspField.GetValue(null);
        public readonly TxGateOwnerTests.Context X = new();
        public readonly NoiseFixture F = new();
        public readonly TxWdspExpander Wdsp = new(_ => new FakeExpanderEngine());
        public Bridge()
        {
            TxAudioConditioning.UseWdspForTest(Wdsp);
            typeof(NoiseFixture).GetField("_transmit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(F, X.Chain);
            F.Noise.TransmitGate = new AppTransmitGate();
            F.Noise.PresetStore = new MemoryPresets();
            TxAudioConditioning.ApplySetup(new TxNoiseSetup());
            X.Buffer();
        }
        public void Dispose()
        {
            F.Dispose();
            X.Dispose();
            Wdsp.Dispose();
            WdspField.SetValue(null, _savedWdsp);
        }
    }

    private sealed class MemoryPresets : INoisePresetStore
    {
        public AudioChainPresets Store = new() { BalancedCaptureOffered = true };
        public AudioChainPresets Load() => Store;
        public bool Save(AudioChainPresets store) { Store = store; return true; }
    }

    // ── B1 ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_gate_comparison_sees_every_field_of_the_record_so_a_field_added_later_is_compared_too()
    {
        // The class, not the instance: every public setting of the record, changed
        // alone, makes two gates differ. (The hand threshold with automatic off, as
        // the comparison counts it only then.)
        foreach (var p in typeof(TxNoiseGateSetup).GetProperties().Where(p => p.CanWrite && p.GetCustomAttribute<System.Xml.Serialization.XmlIgnoreAttribute>() == null))
        {
            var a = new TxNoiseGateSetup { AutoThreshold = false };
            var b = a.Clone();
            object? now = p.GetValue(b);
            object changed = p.PropertyType == typeof(bool) ? !(bool)now!
                : p.PropertyType == typeof(float) ? (float)now! + 1f
                : p.PropertyType == typeof(string) ? (string)now! + "_other"
                : throw new InvalidOperationException("a gate field of a kind this test does not change: " + p.Name);
            p.SetValue(b, changed);
            Assert.False(NoiseController.SameGate(a, b), "the gate comparison does not see " + p.Name);
        }
        // And the one deliberate exception: the hand threshold while automatic.
        var x = new TxNoiseGateSetup { AutoThreshold = true };
        var y = x.Clone(); y.ThresholdDb -= 5;
        Assert.True(NoiseController.SameGate(x, y));
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("depth")]
    public void A_preset_differing_only_in_the_expanders_mode_or_WDSPs_depth_applies_and_says_so(string field)
    {
        using var b = new Bridge();
        if (field == "depth") TxAudioConditioning.SetExpanderMode(ExpanderModes.Wdsp);
        b.F.Noise.SavePreset("P", false, false, true, PresetCollision.Replace, "test");
        if (field == "mode") TxAudioConditioning.SetExpanderMode(ExpanderModes.Wdsp);
        else TxAudioConditioning.SetWdspDepth(30f);
        b.F.Spoken.Clear();
        b.F.Noise.ApplyPreset("P", "test", DenoiseDirection.Transmit);
        if (field == "mode") Assert.Equal(ExpanderModes.Jj, TxAudioConditioning.ExpanderMode);
        else Assert.Equal(ExpanderModes.DefaultWdspDepthDb, TxAudioConditioning.WdspDepthDb);
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.phrase_gate"), b.F.Spoken.Last);
        Assert.DoesNotContain(Lexicon.Get("audio.noise_surface.preset_nothing_changed"), b.F.Spoken.Last);
    }

    [Fact]
    public void The_layers_Escape_puts_back_the_mode_and_the_depth()
    {
        using var b = new Bridge();
        var snap = b.F.Noise.TakeSnapshot();
        b.F.Noise.SetExpanderModeWdsp(true, Layer);
        Assert.Equal(ExpanderModes.Wdsp, TxAudioConditioning.ExpanderMode);
        Assert.True(b.F.Noise.RestoreSnapshot(snap, Layer));
        Assert.Equal(ExpanderModes.Jj, TxAudioConditioning.ExpanderMode);

        TxAudioConditioning.SetExpanderMode(ExpanderModes.Wdsp);           // from elsewhere, before the layer
        snap = b.F.Noise.TakeSnapshot();
        b.F.Noise.SetGate(b.F.Noise.GateLever(TxGateLever.Strength)!, 5, Layer);   // 30 dB deep
        Assert.Equal(30f, TxAudioConditioning.WdspDepthDb);
        Assert.True(b.F.Noise.RestoreSnapshot(snap, Layer));
        Assert.Equal(ExpanderModes.DefaultWdspDepthDb, TxAudioConditioning.WdspDepthDb);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("depth")]
    public void An_outside_change_to_the_mode_or_depth_is_kept_while_Escape_puts_another_gate_field_back(string field)
    {
        using var b = new Bridge();
        if (field == "depth") TxAudioConditioning.SetExpanderMode(ExpanderModes.Wdsp);
        var snap = b.F.Noise.TakeSnapshot();
        float attackBefore = TxAudioConditioning.GateAttackMs;
        // Home changes the mode (or the depth); then the layer changes the attack.
        if (field == "mode") b.F.Noise.SetExpanderModeWdsp(true, Home);
        else b.F.Noise.SetGate(b.F.Noise.GateLever(TxGateLever.Strength)!, 5, Home);
        b.F.Noise.SetGate(b.F.Noise.GateLever(TxGateLever.Attack)!, 12, Layer);
        Assert.True(b.F.Noise.RestoreSnapshot(snap, Layer));
        Assert.Equal(attackBefore, TxAudioConditioning.GateAttackMs);           // the layer's goes back
        if (field == "mode") Assert.Equal(ExpanderModes.Wdsp, TxAudioConditioning.ExpanderMode);   // Home's stays
        else Assert.Equal(30f, TxAudioConditioning.WdspDepthDb);
    }

    // ── B3 ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("threshold")]
    [InlineData("attack")]
    [InlineData("hold")]
    [InlineData("release")]
    [InlineData("sidechain_low")]
    [InlineData("sidechain_high")]
    [InlineData("margin")]
    [InlineData("auto")]
    public void In_WDSPs_mode_a_shared_lever_changed_while_keyed_says_it_waits_for_the_unkey(string key)
    {
        using var b = new Bridge();
        TxChangeResult Write(float v) => key == "auto" ? TxAudioConditioning.SetAutoThreshold(v > 0) : TxAudioConditioning.SetGateLevel(key, v);
        float value = key switch { "threshold" => -45, "sidechain_low" => 200, "sidechain_high" => 3500, "margin" => 9, _ => 20 };
        b.X.Keyed = true;
        Assert.Equal(TxChangeTiming.Now, Write(value).Timing);                    // JJ's mode: live, as ruled (S15)
        b.X.Keyed = false;
        TxAudioConditioning.SetExpanderMode(ExpanderModes.Wdsp);
        b.X.Keyed = true;
        Assert.Equal(TxChangeTiming.AtUnkey, Write(value + 1).Timing);           // WDSP's: held, and says so
        b.X.Keyed = false;
        Assert.Equal(TxChangeTiming.Now, Write(value).Timing);                    // unkeyed: now
    }

    [Fact]
    public void The_controller_says_at_the_unkey_for_a_shared_lever_in_WDSPs_mode_and_not_for_its_own_depth_lever_in_JJs()
    {
        using var b = new Bridge();
        TxAudioConditioning.SetExpanderMode(ExpanderModes.Wdsp);
        b.X.Keyed = true;
        b.F.Spoken.Clear();
        b.F.Noise.SetGate(b.F.Noise.GateLever(TxGateLever.Attack)!, 9, Home);
        Assert.Contains(b.F.Spoken.Said, s => s.Text.Contains(Lexicon.Get("audio.denoise.tx.at_unkey")));
        b.F.Spoken.Clear();
        b.F.Noise.SetGate(b.F.Noise.GateLever(TxGateLever.Depth)!, 30, Home);    // the JJ gate setting's depth: not WDSP's
        Assert.DoesNotContain(b.F.Spoken.Said, s => s.Text.Contains(Lexicon.Get("audio.denoise.tx.at_unkey")));
    }

    // ── B2 ──────────────────────────────────────────────────────────────

    /// <summary>A gate that holds the two modes' settings and records what was written, and whose WDSP availability a test turns.</summary>
    private sealed class ModeGate : ITransmitGate
    {
        public string Mode = ExpanderModes.Jj;
        public float Depth = 20f, Ratio = 2f;
        public bool Gate;
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
        public bool GateSetting => Gate;
        public string ExpanderMode => Mode;
        public float WdspDepthDb => Depth;
        public Unavailability WdspAvailability => Availability;
        public TxChangeResult SetExpanderMode(string mode) { Mode = mode; return TxChangeResult.Now; }
        public TxChangeResult SetWdspDepth(float db) { Depth = db; return TxChangeResult.Now; }
        public TxChangeResult SetStrength(float ratio, bool gate) { Ratio = ratio; Gate = gate; return TxChangeResult.Now; }
        public TxChangeResult SetAutoThreshold(bool on) => TxChangeResult.Now;
        public TxChangeResult SetLevel(string key, float value) => TxChangeResult.Now;
        public bool MonitorAvailable => false;
        public int MonitorMode { get; set; }
        public float MonitorVolume { get; set; } = 1;
        public TxNoiseGateSetup Capture() => new() { ExpanderMode = Mode, WdspDepthDb = Depth, RatioToOne = Ratio, GateSetting = Gate };
        public void ApplyNow(TxNoiseGateSetup gate) { Mode = gate.ExpanderMode; Depth = gate.WdspDepthDb; Ratio = gate.RatioToOne; Gate = gate.GateSetting; }
    }

    private static void RunOnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }

    [Fact]
    public void Homes_strength_field_is_reworded_on_every_mode_switch_and_each_label_names_what_it_writes()
    {
        using var f = new NoiseFixture();
        var gate = new ModeGate();
        f.Noise.TransmitGate = gate;
        RunOnSta(() =>
        {
            var strength = f.Noise.GateLever(TxGateLever.Strength)!;
            var mode = f.Noise.GateLever(TxGateLever.Mode)!;
            var strengthField = new CycleFieldControl();
            var modeField = new CycleFieldControl();
            var fields = new List<(CycleFieldControl Field, TxGateLever Lever, IReadOnlyList<(double Value, string Text)> Steps)>();
            foreach (var (field, lever) in new[] { (strengthField, strength), (modeField, mode) })
            {
                var steps = f.Noise.GateSteps(lever);
                field.Setup(f.Noise.GateLeverName(lever), steps.Select(s => s.Text).ToArray(), ScreenFieldsPanel.NearestGateStep(f.Noise, lever, steps));
                fields.Add((field, lever, steps));
            }
            int changes = 0;
            strengthField.SelectionChanged += (s, i) => changes++;

            // Each switch, by whatever road: the field's words and the value a choice writes agree.
            foreach (string to in new[] { ExpanderModes.Wdsp, ExpanderModes.Jj, ExpanderModes.Wdsp })
            {
                gate.Mode = to;
                Assert.True(ScreenFieldsPanel.RefreshGateChoices(f.Noise, fields));
                var entry = fields[0];
                for (int i = 0; i < entry.Steps.Count; i++)
                {
                    Assert.Equal(f.Noise.GateSteps(strength)[i].Text, strengthField.Options[i]);
                    f.Noise.SetGate(strength, entry.Steps[i].Value, "home");
                    if (to == ExpanderModes.Wdsp)
                        Assert.Contains(gate.Depth.ToString("0", System.Globalization.CultureInfo.InvariantCulture), strengthField.Options[i]);
                    else if (gate.Gate) Assert.Equal(Lexicon.Get("audio.noise_surface.tx.gate.strength_gate"), strengthField.Options[i]);
                    else Assert.Contains(gate.Ratio.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), strengthField.Options[i]);
                }
            }
            Assert.Equal(0, changes);                                                    // nothing written by the re-wording
            Assert.False(ScreenFieldsPanel.RefreshGateChoices(f.Noise, fields));        // nothing to re-word: nothing done

            // The mode field's WDSP choice follows the helper's availability.
            string reason = Lexicon.Get(DenoiseEngineCatalog.UnavailabilityKey(Unavailability.HelperMissing));
            Assert.DoesNotContain(reason, modeField.Options[1]);
            gate.Availability = Unavailability.HelperMissing;
            Assert.True(ScreenFieldsPanel.RefreshGateChoices(f.Noise, fields));
            Assert.Contains(reason, modeField.Options[1]);
        });
    }

    // ── N1 and N2 ───────────────────────────────────────────────────────

    private static float[] Buffer960() => new float[960];

    [Fact]
    public void Adopting_a_new_bank_and_the_first_fallback_allocate_nothing_on_the_callback()
    {
        var engines = new List<FakeExpanderEngine>();
        var scheduler = new JJTrace.ManualOffAudioScheduler();
        using var x = new TxWdspExpander(_ => { var e = new FakeExpanderEngine(); lock (engines) engines.Add(e); return e; }, scheduler);
        int told = 0;
        x.FellBack += () => Interlocked.Increment(ref told);
        var g = new TxNoiseGateSetup { ExpanderMode = ExpanderModes.Wdsp };
        x.WantWdsp(true);
        Assert.NotNull(x.BuildNow(TxWdspExpander.ValuesFor(g, -50f)));
        var buf = Buffer960();
        for (int i = 0; i < 20; i++) { x.Process(buf, buf.Length, Rate, false); x.Process(buf, buf.Length, Rate, true); }   // every road warmed
        Assert.NotNull(x.BuildNow(TxWdspExpander.ValuesFor(g, -50f)));            // a second bank waits
        long before = GC.GetAllocatedBytesForCurrentThread();
        x.Process(buf, buf.Length, Rate, false);                                    // adopts it, retires the first
        long adopt = GC.GetAllocatedBytesForCurrentThread() - before;
        for (int i = 0; i < 5; i++) x.Process(buf, buf.Length, Rate, true);
        lock (engines) engines[^1].Late = true;
        before = GC.GetAllocatedBytesForCurrentThread();
        x.Process(buf, buf.Length, Rate, true);                                     // the first fallback: latched, notice owed
        long fallback = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(x.RunningBank!.Latched);
        Assert.Equal(0, adopt);
        Assert.Equal(0, fallback);
        // The off-audio writer does the rest: the old bank is disposed, the notice raised.
        scheduler.Tick();
        HelperTestRig.Until(() => Volatile.Read(ref told) == 1, "the notice, from the off-audio road");
    }

    [Fact]
    public void An_always_late_engine_that_stays_available_is_told_once_until_it_really_works_again()
    {
        var engines = new List<FakeExpanderEngine>();
        var scheduler = new JJTrace.ManualOffAudioScheduler();
        using var x = new TxWdspExpander(_ => { var e = new FakeExpanderEngine { Late = true }; lock (engines) engines.Add(e); return e; }, scheduler);
        int told = 0;
        x.FellBack += () => Interlocked.Increment(ref told);
        x.WantWdsp(true);
        x.BuildNow(TxWdspExpander.ValuesFor(new TxNoiseGateSetup(), -50f));
        var buf = Buffer960();
        void Over() { for (int i = 0; i < 5; i++) x.Process(buf, buf.Length, Rate, false); for (int i = 0; i < 5; i++) x.Process(buf, buf.Length, Rate, true); scheduler.Tick(); }
        Over();
        HelperTestRig.Until(() => Volatile.Read(ref told) == 1, "the first notice");
        Over();
        Over();
        Thread.Sleep(150);
        Assert.Equal(1, Volatile.Read(ref told));            // available all along, never worked: one failure, one notice
        Assert.True(engines[0].IsAvailable);
        // It really works for a while, then fails again: a new failure, told again (the positive control).
        engines[0].Late = false;
        for (int i = 0; i < 5; i++) x.Process(buf, buf.Length, Rate, false);
        engines[0].Late = true;
        Over();
        HelperTestRig.Until(() => Volatile.Read(ref told) == 2, "the second failure's notice");
    }
}
