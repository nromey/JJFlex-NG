using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using JJFlexWpf.Tests.Infrastructure;
using JJPortaudio;
using Radios;
using Xunit;
using Keys = System.Windows.Forms.Keys;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track H2, the evening rulings round (2026-11-18), as carried onto
/// the integration branch without the unkey hold (Noel's H1 = B, 2026-11-19
/// 07:03; the hold's tests stay on sprint49/h2-join): the S15 detail (the
/// derived threshold holds still while keyed), S17 (the live transmit choices
/// persist, through receive's own mechanism), and the 21:23 ruling pinned
/// (turning noise reduction on never switches both sides on).
/// </summary>
/// <remarks>
/// Windowless: every test asserts an MTA thread (WPF refuses to build any
/// element there), constructs no control, and touches no radio. The rigs are an
/// uninitialised FlexBase holding only numerical parts (TxGateOwnerTests'
/// Context) or a NoiseFixture. QuietRun has every sound off before any type
/// here loads.
/// </remarks>
[Collection("Transmit conditioning statics")]
public sealed class RulingsRoundTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-h2rr-" + Guid.NewGuid().ToString("N"));

    public RulingsRoundTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static void SetKeyed(FlexBase rig, bool on) => typeof(FlexBase).GetField("_Transmit", Any)!.SetValue(rig, on);

    // ── S15 detail (ruled 21:27): the derived threshold holds still while keyed ──

    [Fact]
    public void The_derived_threshold_holds_still_while_keyed_and_resumes_on_the_first_unkeyed_tick()
    {
        using var x = new TxGateOwnerTests.Context();
        x.FillMeter();
        TxAudioConditioning.AutoThreshold = true;
        TxAudioConditioning.ThresholdMarginDb = 8;
        x.Buffer();
        var rig = (FlexBase)typeof(TxAudioConditioning).GetField("_rig", Any)!.GetValue(null)!;
        float expected = x.Meter.Profile.NoiseFloorLufs + 8;

        x.Cond.Gate.ThresholdDb = -70f;  // a sentinel inside the gate's range
        SetKeyed(rig, true);                  // our Mox (the interlock reads the same way)
        x.Tick();
        Assert.Equal(-70f, x.Cond.Gate.ThresholdDb);   // held still: the voice is not measured as noise

        SetKeyed(rig, false);
        x.Tick();
        Assert.Equal(expected, x.Cond.Gate.ThresholdDb, 4);   // resumes at once
    }

    [Fact]
    public void The_derived_threshold_rule_is_auto_and_unkeyed_and_a_known_floor()
    {
        Assert.Equal(-50f, TxAudioConditioning.DerivedThreshold(true, false, true, -58f, 8f));
        Assert.Null(TxAudioConditioning.DerivedThreshold(true, true, true, -58f, 8f));
        Assert.Null(TxAudioConditioning.DerivedThreshold(false, false, true, -58f, 8f));
        Assert.Null(TxAudioConditioning.DerivedThreshold(true, false, false, -58f, 8f));
        Assert.Null(TxAudioConditioning.DerivedThreshold(true, false, true, LufsMeter.Floor, 8f));
        // S15 itself, unchanged: the continuous levers stay live while keyed.
        Assert.True(TxNoiseChain.ContinuousLeversLiveWhileKeyed);
    }

    // ── S17 (ruled 21:35): the live transmit choices persist, as receive's do ──

    private static void FreshStatics(TxGateOwnerTests.Context x)
    {
        typeof(TxAudioConditioning).GetField("_autoThreshold", Any)!.SetValue(null, true);
        typeof(TxAudioConditioning).GetField("_thresholdMarginDb", Any)!.SetValue(null, 8f);
    }

    [Fact]
    public void The_live_transmit_choices_round_trip_through_the_app_wide_audio_config()
    {
        using var x = new TxGateOwnerTests.Context();
        var chain = x.Chain;
        chain.ChooseEngine(DenoiseFamily.Neural, "rnnoise");
        chain.SetEnabled(DenoiseFamily.Neural, true);
        chain.SetStrength("rnnoise", 0.6f);
        TxAudioConditioning.GateEnabled = true;
        TxAudioConditioning.GateHoldMs = 420;
        TxAudioConditioning.AutoThreshold = false;
        TxAudioConditioning.GateThresholdDb = -37;
        var before = TransmitNoiseConfig.CaptureLive();

        var config = new AudioOutputConfig();
        Assert.True(TransmitNoiseConfig.Capture(config));
        Assert.False(TransmitNoiseConfig.Capture(config));          // unchanged: nothing to save
        Directory.CreateDirectory(_root);
        config.Save(_root);
        var loaded = AudioOutputConfig.Load(_root);
        Assert.NotNull(loaded.TransmitNoise);

        // A new session: a fresh chain at its shipped start, the gate as shipped.
        TxAudioConditioning.UseChainForTest(new TxNoiseChain(TxNoiseChainTests.Catalog()));
        typeof(TxAudioConditioning).GetField("_gateIntent", Any)!.SetValue(null, new TxNoiseGateSetup());
        FreshStatics(x);
        Assert.False(TransmitNoiseConfig.CaptureLive().SameContentAs(before));   // positive control: it really was reset
        TransmitNoiseConfig.Restore(loaded);
        var after = TransmitNoiseConfig.CaptureLive();
        Assert.True(after.Neural.Enabled);
        Assert.False(after.Spectral.Enabled);
        Assert.Equal(420f, after.Gate.HoldMs);
        Assert.False(after.Gate.AutoThreshold);
        Assert.True(after.Gate.Enabled);
        Assert.Equal(0.6f, after.Neural.Engines.Single(e => e.EngineId == "rnnoise").Strength, 4);
    }

    [Fact]
    public void A_choice_made_with_no_radio_is_saved_to_the_root_for_the_next_radio_and_launch()
    {
        using var x = new TxGateOwnerTests.Context();
        typeof(TxAudioConditioning).GetField("_rig", Any)!.SetValue(null, null);   // no radio
        Directory.CreateDirectory(_root);
        x.Chain.SetEnabled(DenoiseFamily.Spectral, true);
        Assert.True(TransmitNoiseConfig.Persist(null, _root));
        Assert.False(TransmitNoiseConfig.Persist(null, _root));    // nothing new
        var onDisk = AudioOutputConfig.Load(_root);
        Assert.True(onDisk.TransmitNoise!.Spectral.Enabled);
        Assert.False(onDisk.TransmitNoise.Neural.Enabled);
    }

    [Fact]
    public void Every_change_that_runs_raises_the_save_and_a_held_one_raises_it_when_it_runs()
    {
        using var x = new TxGateOwnerTests.Context();
        int raised = 0;
        Action count = () => raised++;
        TxAudioConditioning.HookNotices(x.Chain);
        TxAudioConditioning.IntentChanged += count;
        try
        {
            x.Chain.SetEnabled(DenoiseFamily.Neural, true);
            Assert.Equal(1, raised);
            TxAudioConditioning.GateHoldMs = 300;                 // a gate lever, through the chain's order
            Assert.Equal(2, raised);
            x.Keyed = true;
            TxAudioConditioning.ApplySetup(new TxNoiseSetup());   // held for the unkey: not yet a choice in force
            Assert.Equal(2, raised);
            x.Keyed = false;
            x.Chain.ApplyDeferred();                               // the unkey runs it
            Assert.Equal(3, raised);
        }
        finally { TxAudioConditioning.IntentChanged -= count; }
    }

    // ── The 21:23 ruling, pinned: turning noise reduction on never switches both sides on ──

    private static int BothOn(TxNoiseChain c) =>
        (c.WantsEnabled(DenoiseFamily.Spectral) ? 1 : 0) + (c.WantsEnabled(DenoiseFamily.Neural) ? 1 : 0);

    [Fact]
    public void A_fresh_transmit_chain_starts_with_both_sides_off()
    {
        var chain = new TxNoiseChain(TxNoiseChainTests.Catalog());
        Assert.Equal(0, BothOn(chain));
        chain.SetEnabled(DenoiseFamily.Neural, true);   // the Workshop's old switch: the neural slot alone
        Assert.Equal(1, BothOn(chain));
        Assert.False(chain.WantsEnabled(DenoiseFamily.Spectral));
    }

    /// <summary>Every key of the noise layer, plain and shifted, and every Shift+Alt chord, pressed in the transmit direction from both sides off: none turns both on.</summary>
    [Fact]
    public void No_key_of_the_noise_layer_turns_both_transmit_sides_on_from_off()
    {
        var keys = new List<Keys>();
        for (var k = Keys.A; k <= Keys.Z; k++) { keys.Add(k); keys.Add(k | Keys.Shift); }
        for (var k = Keys.D0; k <= Keys.D9; k++) keys.Add(k);
        keys.AddRange(new[] { Keys.OemOpenBrackets, Keys.OemCloseBrackets, Keys.Space, Keys.Enter });
        keys.AddRange(NoiseLayerKeys.ShiftAltChords);
        int pressed = 0, enabledOne = 0;
        foreach (var key in keys)
        {
            using var f = new NoiseFixture();
            // Favourites from both families, so the brackets have something to step.
            f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "nr4" });
            f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "rnnoise" });
            var layer = new NoiseLayerKeys(f.Noise);
            f.Noise.BeginSession();
            Assert.True(layer.Handle(NoiseLayerKeys.DirectionKey));
            Assert.Equal(DenoiseDirection.Transmit, f.Noise.LayerDirection);
            Assert.Equal(0, BothOn(f.Transmit));
            try { if ((key & Keys.Alt) != 0) layer.HandleShiftAlt(key); else layer.Handle(key); }
            catch (InvalidOperationException) { /* a key that would open a window refuses on this MTA thread */ }
            f.Transmit.ApplyDeferred();
            pressed++;
            int on = BothOn(f.Transmit);
            if (on == 1) enabledOne++;
            Assert.True(on <= 1, key + " turned both transmit sides on from off");
        }
        Assert.True(pressed > 60);
        Assert.True(enabledOne > 0, "positive control: some key turned one side on, so the probe reaches the chain");
    }

    /// <summary>Each controller action that turns a transmit engine on, for every transmit engine, from both sides off.</summary>
    [Fact]
    public void No_single_transmit_action_turns_both_sides_on_from_off()
    {
        using var probe = new NoiseFixture();
        var ids = probe.Catalog.Entries.Where(e => e.Side == DenoiseSide.Pc && e.Family != null && e.Serves(DenoiseDirection.Transmit))
            .Select(e => (e.Id, Family: e.Family!.Value)).ToList();
        Assert.True(ids.Count >= 4);
        var actions = new List<(string Name, Action<NoiseFixture> Act)>();
        foreach (var (id, family) in ids)
        {
            actions.Add(("TransmitSetOn " + id, f => f.Noise.TransmitSetOn(id, true, "test")));
            actions.Add(("TransmitToggle " + id, f => f.Noise.TransmitToggle(id, "test")));
            actions.Add(("TransmitSetShownOn " + id, f => f.Noise.TransmitSetShownOn(family, id, true, "test")));
            actions.Add(("TransmitChooseSlotEngine " + id, f => f.Noise.TransmitChooseSlotEngine(family, id, "test")));
            actions.Add(("TransmitReturnToDefaults " + id, f => f.Noise.TransmitReturnToDefaults(id, "test")));
        }
        foreach (var family in new[] { DenoiseFamily.Spectral, DenoiseFamily.Neural })
            actions.Add(("TransmitSetSlotOn " + family, f => f.Noise.TransmitSetSlotOn(family, true, "test")));
        actions.Add(("TransmitComputerOffAndBack from off", f => f.Noise.TransmitComputerOffAndBack("test")));
        actions.Add(("TransmitStep forward", f => f.Noise.TransmitStep(1, "test")));
        actions.Add(("TransmitStep back", f => f.Noise.TransmitStep(-1, "test")));
        int enabledOne = 0;
        foreach (var (name, act) in actions)
        {
            using var f = new NoiseFixture();
            f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "nr4" });
            f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "rnnoise" });
            Assert.Equal(0, BothOn(f.Transmit));
            act(f);
            f.Clock.RunAll();
            f.Transmit.ApplyDeferred();
            int on = BothOn(f.Transmit);
            if (on == 1) enabledOne++;
            Assert.True(on <= 1, name + " turned both transmit sides on from off");
        }
        Assert.True(enabledOne >= ids.Count, "positive control: the actions really turn a side on");
    }

    /// <summary>
    /// The structural half: every call in the app that can enable a transmit
    /// slot, counted per file. Today each is one of: a single slot (the engine
    /// switch), the 0 key's restore, a whole setup, preset or microphone setup,
    /// the layer's Escape putting back what it changed, or (this round) the
    /// saved live choices restored at launch. A new one fails here until someone
    /// decides it cannot switch both sides on by default.
    /// </summary>
    [Fact]
    public void Every_road_that_can_enable_a_transmit_slot_is_a_known_one()
    {
        string root = Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf");
        var pattern = new Regex(@"SetEnabled\([^)]*,\s*true\)|ApplySlotsNow\(|ApplySlotNow\(|\.ApplySlots\(|ApplySetup\(|ApplyNamedSetup\(|ApplySettings\(TxConditioningSettings|NrEnabled\s*=\s*");
        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal)) continue;
            foreach (var line in File.ReadLines(path))
            {
                string t = line.Trim();
                if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!pattern.IsMatch(t)) continue;
                found[rel] = found.TryGetValue(rel, out var n) ? n + 1 : 1;
            }
        }
        var expected = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            // Sprint 49 Track Q phase B: the audition's twin puts the setup on its
            // OWN chain, built for one offline render and torn down after it; never
            // the live chain (ChainAuditionTwinTests proves the live one untouched).
            ["ChainAuditionTwin.cs"] = 1,
            // The Workshop: a microphone's setup, and the old settings migrated onto the neural slot.
            ["Dialogs/AudioWorkshopDialog.MicProfiles.cs"] = 2,
            // Escape puts back only the slots the layer changed, as they were.
            ["NoiseController.Snapshot.cs"] = 2,
            // TxTurnOn (one family, by construction), the 0 key's restore, a whole preset or setup.
            ["NoiseController.Transmit.cs"] = 3,
            // S17: the saved live choices, restored at launch.
            ["TransmitNoiseConfig.cs"] = 1,
            // The setup API itself (definitions and its own calls).
            ["TxAudioConditioning.cs"] = 6,
            ["TxNoiseChain.cs"] = 3,
        };
        Assert.Equal(expected, found);
    }
}
