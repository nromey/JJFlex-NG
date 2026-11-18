using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Radios;
using Radios.Speech;
using JJFlexWpf.Tests.Infrastructure;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track H2: the transmit noise reduction surface on the one
/// controller. The noise layer's T and every layer key in the transmit
/// direction, the transmit engine list, levers, the at-unkey timing, the
/// summary, the 0 key, the brackets, defaults, Escape, presets' transmit
/// section, setups, the gate, the mode policy, and #1123 (receive noise
/// reduction follows the active slice's mode).
/// </summary>
/// <remarks>
/// Windowless: the controller and the layer's key handler over the fixture's
/// fakes, a fake gate, and a setup store in a temporary folder. Every test
/// asserts the desk is safe first (an MTA thread, no window).
/// </remarks>
public sealed class TransmitNoiseSurfaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-h2-" + Guid.NewGuid().ToString("N"));
    private const string Radio8600 = "1234-5678-9012-3456";

    public TransmitNoiseSurfaceTests() => NoiseReductionPerChannelTests.AssertWindowless();

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>The gate and the cleanup monitor, in memory.</summary>
    internal sealed class FakeGate : ITransmitGate
    {
        public bool Keyed;
        public bool Enabled { get; private set; }
        public bool AutoThreshold { get; set; } = true;
        public float MarginDb { get; set; } = 8;
        public float ThresholdDb { get; set; } = -60;
        public float AttackMs { get; set; } = 3;
        public float HoldMs { get; set; } = 150;
        public float ReleaseMs { get; set; } = 200;
        public float RangeDb { get; set; } = 25;
        public bool MonitorAvailable { get; set; }
        public int MonitorMode { get; set; }
        public float MonitorVolume { get; set; } = 1;
        public TxChangeResult SetEnabled(bool on) { Enabled = on; return Keyed ? TxChangeResult.AtUnkey : TxChangeResult.Now; }
        public TxNoiseGateSetup Capture() => new()
        {
            Enabled = Enabled, AutoThreshold = AutoThreshold, ThresholdMarginDb = MarginDb, ThresholdDb = ThresholdDb,
            AttackMs = AttackMs, HoldMs = HoldMs, ReleaseMs = ReleaseMs, RangeDb = RangeDb,
        };
        public TxChangeResult Apply(TxNoiseGateSetup g)
        {
            Enabled = g.Enabled; AutoThreshold = g.AutoThreshold; MarginDb = g.ThresholdMarginDb; ThresholdDb = g.ThresholdDb;
            AttackMs = g.AttackMs; HoldMs = g.HoldMs; ReleaseMs = g.ReleaseMs; RangeDb = g.RangeDb;
            return Keyed ? TxChangeResult.AtUnkey : TxChangeResult.Now;
        }
    }

    private sealed class MemoryPresets : INoisePresetStore
    {
        public AudioChainPresets Store = new() { BalancedCaptureOffered = true };
        public AudioChainPresets Load() => Store;
        public bool Save(AudioChainPresets store) { Store = store; return true; }
    }

    private (NoiseFixture F, FakeGate Gate) Make()
    {
        var f = new NoiseFixture();
        var gate = new FakeGate();
        f.Noise.TransmitGate = gate;
        f.Radio.Serial = Radio8600;
        var store = new TxNoiseSetupStore(_root);
        f.Noise.TransmitSetupStore = () => store;
        f.Noise.PresetStore = new MemoryPresets();
        return (f, gate);
    }

    private static string Tx(string engineName) => Lexicon.Get("audio.noise_surface.tx.engine", ("engine", engineName));
    private static string AtUnkey => Lexicon.Get("audio.denoise.tx.at_unkey");

    private static NoiseLayerKeys LayerKeys(NoiseFixture f) => new(f.Noise);

    private static void EnterTransmit(NoiseFixture f, NoiseLayerKeys keys)
    {
        f.Noise.BeginSession();
        Assert.True(keys.Handle(NoiseLayerKeys.DirectionKey));
        Assert.Equal(DenoiseDirection.Transmit, f.Noise.LayerDirection);
        f.Spoken.Clear();
    }

    // ── Item 3: T and the layer's keys in both directions ──

    [Fact]
    public void T_switches_the_layer_to_transmit_and_back_on_its_own_subject_and_each_session_opens_on_receive()
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        f.Noise.BeginSession();
        Assert.True(keys.Handle(Keys.T));
        Assert.Equal(DenoiseDirection.Transmit, f.Noise.LayerDirection);
        Assert.Equal(SpeechSubject.NoiseLayerDirection, f.Spoken.Said[^1].Subject);
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.layer_transmit", VerbosityLevel.Chatty), f.Spoken.Last);
        Assert.True(keys.Handle(Keys.T));
        Assert.Equal(DenoiseDirection.Receive, f.Noise.LayerDirection);
        keys.Handle(Keys.T);
        f.Noise.BeginSession();
        Assert.Equal(DenoiseDirection.Receive, f.Noise.LayerDirection);
    }

    [Fact]
    public void After_T_a_Ctrl_letter_turns_the_transmit_engine_on_and_leaves_receive_alone()
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        Assert.True(keys.Handle(Keys.N | Keys.Control));
        Assert.True(f.Transmit.WantsEnabled(DenoiseFamily.Neural));
        Assert.Equal("nnr", f.Transmit.ChosenEngineId(DenoiseFamily.Neural));
        Assert.True(f.Noise.TransmitIsOn("nnr"));
        Assert.False(f.Pipeline.NeuralSlot.Enabled);
        Assert.False(f.Noise.IsOn("nnr"));
        Assert.Equal(SpeechSubject.TransmitNoiseEngineSwitch, f.Spoken.Said[^1].Subject);
        Assert.StartsWith(Lexicon.Get("audio.noise_surface.on", ("engine", Tx("NNR"))), f.Spoken.Last);

        // The same key in the receive direction is receive's, as before.
        keys.Handle(Keys.T);
        keys.Handle(Keys.N | Keys.Control);
        Assert.True(f.Noise.IsOn("nnr"));
        Assert.True(f.Noise.TransmitIsOn("nnr"));
    }

    [Fact]
    public void A_second_engine_of_the_family_replaces_the_first_and_says_so_for_transmit()
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.R | Keys.Control);
        keys.Handle(Keys.N | Keys.Control);
        Assert.Equal("nnr", f.Transmit.ChosenEngineId(DenoiseFamily.Neural));
        Assert.StartsWith(Lexicon.Get("audio.noise_surface.on_replacing", ("engine", Tx("NNR")), ("other", Tx("RNNoise"))), f.Spoken.Last);
        keys.Handle(Keys.N | Keys.Control);
        Assert.False(f.Transmit.WantsEnabled(DenoiseFamily.Neural));
        Assert.Equal(Lexicon.Get("audio.noise_surface.off", ("engine", Tx("NNR"))), f.Spoken.Last);
    }

    [Theory]
    [InlineData(Keys.N, "radio_nr")]
    [InlineData(Keys.B, "radio_nb")]
    [InlineData(Keys.S | Keys.Control, "spectral_capture")]
    public void After_T_a_radio_letter_or_Captured_noise_does_nothing_and_says_it_is_receive_only(Keys key, string id)
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        Assert.True(keys.Handle(key));
        Assert.Empty(f.Radio.Writes);
        Assert.False(f.Transmit.WantsEnabled(DenoiseFamily.Spectral));
        Assert.Equal(SpeechSubject.TransmitNoiseUnavailable, f.Spoken.Said[^1].Subject);
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.receive_only", ("engine", f.Noise.NameOf(id))), f.Spoken.Last);
    }

    [Fact]
    public void An_engine_change_made_while_keyed_waits_for_the_unkey_and_says_so()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Transmit.KeySource = () => true;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.D | Keys.Control);
        Assert.True(f.Transmit.WantsEnabled(DenoiseFamily.Neural));
        Assert.False(f.Transmit.NeuralSlot.Enabled);          // nothing audible until an unkeyed buffer
        Assert.EndsWith(AtUnkey, f.Spoken.Last);

        // Unkeyed, the same change says nothing about the unkey.
        f.Transmit.KeySource = () => false;
        keys.Handle(Keys.D | Keys.Control);
        keys.Handle(Keys.D | Keys.Control);
        Assert.DoesNotContain(AtUnkey, f.Spoken.Last);
    }

    [Fact]
    public void The_floor_moves_live_while_keyed_and_never_says_it_waits()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Transmit.KeySource = () => true;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.R | Keys.Control);                  // RNNoise: the floor is its first lever
        float before = f.Transmit.GetStrength("rnnoise");
        f.Spoken.Clear();
        Assert.True(keys.Handle(Keys.Up));
        Assert.NotEqual(before, f.Transmit.GetStrength("rnnoise"));
        Assert.Equal(f.Pipeline.NeuralSlot.GetStrength("rnnoise"), NoiseController.DefaultStrength("rnnoise"));
        Assert.Equal(SpeechSubject.TransmitNoiseLevel, f.Spoken.Said[^1].Subject);
        Assert.DoesNotContain(AtUnkey, f.Spoken.Last);
    }

    [Fact]
    public void A_rebuild_setting_waits_for_the_unkey_while_keyed()
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.N | Keys.Control);
        f.Transmit.KeySource = () => true;
        var model = f.Noise.Levers("nnr", false, DenoiseDirection.Transmit).Single(l => l.Key == "model");
        var other = f.Noise.OptionsFor(model).First(o => o.Name != f.Noise.ChoiceOf(model));
        f.Spoken.Clear();
        f.Noise.Set(model, null, other.Name, "test");
        Assert.Equal(other.Name, f.Transmit.NeuralSlot.GetSetting("nnr", "model").Choice);
        Assert.NotEqual(other.Name, f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        Assert.EndsWith(AtUnkey, f.Spoken.Last);
    }

    [Fact]
    public void Space_after_T_says_the_transmit_summary_and_in_CW_says_the_chain_is_bypassed()
    {
        var (f, gate) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.Space);
        Assert.Equal(SpeechSubject.TransmitNoiseActiveSummary, f.Spoken.Said[^1].Subject);
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.active.none", VerbosityLevel.Chatty), f.Spoken.Last);
        keys.Handle(Keys.R | Keys.Control);
        f.Noise.SetGateOn(true, "test");
        keys.Handle(Keys.Space);
        Assert.Contains("RNNoise", f.Spoken.Last);
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.active.gate_auto", VerbosityLevel.Chatty, ("margin", f.Noise.GateValueText(f.Noise.GateLever(TxGateLever.Margin)!))), f.Spoken.Last);
        Assert.DoesNotContain(Lexicon.Get("audio.noise_surface.tx.active.bypassed"), f.Spoken.Last);
        f.Radio.Mode = "CW";
        keys.Handle(Keys.Space);
        Assert.EndsWith(Lexicon.Get("audio.noise_surface.tx.active.bypassed"), f.Spoken.Last);
    }

    [Fact]
    public void Zero_after_T_turns_both_transmit_slots_off_and_back_and_leaves_receive_alone()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.Toggle("rnnoise", "test");                 // receive RNNoise on
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.D2 | Keys.Control);
        keys.Handle(Keys.N | Keys.Control);
        keys.Handle(Keys.D0);
        Assert.False(f.Transmit.WantsEnabled(DenoiseFamily.Spectral));
        Assert.False(f.Transmit.WantsEnabled(DenoiseFamily.Neural));
        Assert.True(f.Noise.IsOn("rnnoise"));
        Assert.StartsWith(Lexicon.Get("audio.noise_surface.tx.zero_off", VerbosityLevel.Chatty, ("engines", "")).TrimEnd(), f.Spoken.Last);
        Assert.Contains("NNR", f.Spoken.Last);
        keys.Handle(Keys.D0);
        Assert.True(f.Noise.TransmitIsOn("nr2"));
        Assert.True(f.Noise.TransmitIsOn("nnr"));
    }

    [Fact]
    public void The_brackets_after_T_step_the_one_favourites_list_in_transmit_skipping_receive_only_favourites()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "radio_nrl" });
        f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "rnnoise" });
        f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "spectral_capture" });
        f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "nnr" });
        Assert.Equal(new[] { "rnnoise", "nnr" }, f.Noise.TransmitStepList().Select(x => x.EngineId));
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.OemCloseBrackets);
        Assert.True(f.Noise.TransmitIsOn("rnnoise"));
        keys.Handle(Keys.OemCloseBrackets);
        Assert.True(f.Noise.TransmitIsOn("nnr"));
        Assert.False(f.Noise.TransmitIsOn("rnnoise"));
        Assert.Empty(f.Radio.Writes);
        Assert.False(f.Pipeline.NeuralSlot.Enabled);
    }

    [Fact]
    public void Favourites_after_T_mark_the_engine_in_its_transmit_model()
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.N | Keys.Control);
        var model = f.Noise.Levers("nnr", false, DenoiseDirection.Transmit).Single(l => l.Key == "model");
        var other = f.Noise.OptionsFor(model).First(o => o.Name != f.Noise.ChoiceOf(model));
        f.Noise.Set(model, null, other.Name, "test");
        keys.Handle(NoiseLayerKeys.FavouriteKey);
        var fav = Assert.Single(f.Config.NoiseFavourites);
        Assert.Equal(other.Name, fav.Model);
        Assert.True(f.Noise.IsFavourite("nnr", DenoiseDirection.Transmit));
    }

    [Fact]
    public void Defaults_after_T_return_the_transmit_floor_not_the_receive_one()
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.R | Keys.Control);
        keys.Handle(Keys.Up); keys.Handle(Keys.Up);
        Assert.NotEqual(NoiseFloor.DefaultStrength("rnnoise", DenoiseDirection.Transmit), f.Transmit.GetStrength("rnnoise"));
        keys.Handle(NoiseLayerKeys.DefaultsKey);
        Assert.Equal(NoiseFloor.DefaultStrength("rnnoise", DenoiseDirection.Transmit), f.Transmit.GetStrength("rnnoise"));
        Assert.Equal(SpeechSubject.TransmitNoiseDefaults, f.Spoken.Said[^1].Subject);
    }

    [Fact]
    public void Escape_puts_back_the_transmit_slots_the_layer_changed()
    {
        var (f, _) = Make();
        using var _f = f;
        var snap = f.Noise.TakeSnapshot();
        var keys = LayerKeys(f);
        keys.Handle(Keys.T);
        keys.Handle(Keys.N | Keys.Control);
        keys.Handle(Keys.D2 | Keys.Control);
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.False(f.Transmit.WantsEnabled(DenoiseFamily.Neural));
        Assert.False(f.Transmit.WantsEnabled(DenoiseFamily.Spectral));
    }

    // ── Items 1 and 6: what transmit offers ──

    [Fact]
    public void Transmit_offers_every_computer_reducer_but_Captured_noise_and_no_radio_engine()
    {
        var (f, _) = Make();
        using var _f = f;
        var ids = f.Noise.TransmitEngines().Select(e => e.Id).ToList();
        Assert.Equal(new[] { "nr2", "nr4", "rnnoise", "deepfilternet3", "nnr", "nvidia_afx" }.OrderBy(x => x), ids.OrderBy(x => x));
        Assert.All(f.Noise.TransmitEngines(DenoiseFamily.Spectral), e => Assert.Equal(DenoiseFamily.Spectral, e.Family));
        Assert.Empty(f.Noise.Levers("spectral_capture", true, DenoiseDirection.Transmit));
        Assert.Empty(f.Noise.Levers("radio_nrl", true, DenoiseDirection.Transmit));
        Assert.All(f.Noise.Levers("nnr", true, DenoiseDirection.Transmit), l => Assert.Equal(DenoiseDirection.Transmit, l.Direction));
        Assert.DoesNotContain(f.Noise.Levers("nnr", true, DenoiseDirection.Transmit), l => l.Kind == NoiseLeverKind.ListenRemoved);
        // The defaults are Track H's: RNNoise neural; never NNR (a receive-trained model).
        Assert.Equal("rnnoise", f.Transmit.ChosenEngineId(DenoiseFamily.Neural));
    }

    [Fact]
    public void The_Transmit_menu_chords_each_engine_in_the_layers_T_form()
    {
        Assert.Equal("\tCtrl+J, N, T, Ctrl+N", NativeMenuBar.TransmitNoiseChordText("nnr"));
        Assert.Equal("\tCtrl+J, N, T, Ctrl+2", NativeMenuBar.TransmitNoiseChordText("nr2"));
        var row = KeyInventory.LayerCommands(KeyInventory.NoiseLayerContext).Single(r => r.KeyDisplay == "T");
        Assert.True(LeaderChordParser.TryParseChord(row.KeyDisplay, out var k));
        Assert.Equal(NoiseLayerKeys.DirectionKey, k);
    }

    // ── Item 4: delays, presets ──

    [Fact]
    public void Choosing_a_transmit_engine_says_its_delay_unless_Say_delays_is_off()
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.N | Keys.Control);
        string clauseStart = Lexicon.Get("audio.noise_surface.tx.delay_clause", ("ms", 0)).Split('0')[0];
        Assert.Contains(clauseStart, f.Spoken.Last);
        keys.Handle(Keys.N | Keys.Control);
        f.Config.NoiseSayDelays = false;
        keys.Handle(Keys.N | Keys.Control);
        Assert.DoesNotContain(clauseStart, f.Spoken.Last);
    }

    [Fact]
    public void A_preset_carries_its_transmit_section_and_applying_it_puts_the_transmit_slots_and_gate_back()
    {
        var (f, gate) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("nnr", "test");
        f.Noise.SetGateOn(true, "test");
        Assert.NotNull(f.Noise.SavePreset("Mic quiet", includeNoise: false, includeChain: false, includeTransmitNoise: true, PresetCollision.Replace, "test"));
        var saved = Assert.Single(f.Noise.TransmitNoisePresets());
        Assert.Null(saved.ReceiveNoise);
        Assert.False(saved.HoldsTransmitChain);
        Assert.Empty(f.Noise.NoisePresets());                // not a receive preset
        Assert.Equal("nnr", NoiseController.TransmitMainEngineOf(saved));

        f.Noise.TransmitToggle("nnr", "test");
        f.Noise.SetGateOn(false, "test");
        f.Noise.ApplyPreset("Mic quiet", "test");
        Assert.True(f.Noise.TransmitIsOn("nnr"));
        Assert.True(gate.Enabled);
        Assert.Contains(Tx("NNR"), f.Spoken.Last);
        Assert.Equal(SpeechSubject.NoisePreset, f.Spoken.Said[^1].Subject);
    }

    [Fact]
    public void A_transmit_only_preset_applies_with_no_receive_pipeline_and_steps_after_T()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("deepfilternet3", "test");
        f.Noise.SavePreset("Desk", false, false, true, PresetCollision.Replace, "test");
        f.Noise.TransmitToggle("deepfilternet3", "test");
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(NoiseLayerKeys.NextPresetKey);
        Assert.True(f.Noise.TransmitIsOn("deepfilternet3"));
    }

    // ── The gate ──

    [Fact]
    public void The_gate_speaks_its_timing_steps_its_margin_to_the_ends_and_returns_to_defaults_keeping_on_or_off()
    {
        var (f, gate) = Make();
        using var _f = f;
        gate.Keyed = true;
        f.Noise.SetGateOn(true, "test");
        Assert.True(gate.Enabled);
        Assert.EndsWith(AtUnkey, f.Spoken.Last);
        Assert.Equal(SpeechSubject.TransmitNoiseGate, f.Spoken.Said[^1].Subject);
        gate.Keyed = false;

        var margin = f.Noise.GateLever(TxGateLever.Margin)!;
        f.Noise.SetGate(margin, 15, "test");
        f.Spoken.Clear();
        f.Noise.AdjustGate(margin, +1, "test");
        Assert.Equal(15, gate.MarginDb);
        Assert.Contains(f.Noise.GateLeverName(margin), f.Spoken.Last);
        gate.AttackMs = 9;

        f.Noise.GateReturnToDefaults("test");
        Assert.True(gate.Enabled);
        Assert.Equal(new TxNoiseGateSetup().ThresholdMarginDb, gate.MarginDb);
        Assert.Equal(new TxNoiseGateSetup().AttackMs, gate.AttackMs);
    }

    [Fact]
    public void The_threshold_is_a_lever_only_while_the_automatic_threshold_is_off()
    {
        var (f, gate) = Make();
        using var _f = f;
        Assert.DoesNotContain(f.Noise.GateLevers(true), l => l.Key == TxGateLever.Threshold);
        f.Noise.SetGateAutoThreshold(false, "test");
        Assert.False(gate.AutoThreshold);
        Assert.Contains(f.Noise.GateLevers(true), l => l.Key == TxGateLever.Threshold);
        Assert.Equal(new[] { TxGateLever.Margin }, f.Noise.GateLevers(false).Select(l => l.Key));
    }

    [Fact]
    public void Listen_to_cleanup_needs_a_radios_chain_and_changes_nothing_without_one()
    {
        var (f, gate) = Make();
        using var _f = f;
        f.Noise.SetListenToCleanup(2, "test");
        Assert.Equal(0, gate.MonitorMode);
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.listen_needs_radio"), f.Spoken.Last);
        gate.MonitorAvailable = true;
        f.Noise.SetListenToCleanup(2, "test");
        Assert.Equal(2, gate.MonitorMode);
    }

    // ── Setups ──

    [Fact]
    public void Setups_save_apply_rename_delete_and_copy_through_Track_Hs_store()
    {
        var (f, gate) = Make();
        using var _f = f;
        var repointed = new List<(string, string, string)>();
        f.Noise.RepointMicrophones = (r, a, b) => { repointed.Add((r, a, b)); return 1; };
        f.Noise.TransmitToggle("nnr", "test");
        f.Noise.SetGateOn(true, "test");
        Assert.True(f.Noise.SaveTransmitSetup("Studio", replace: false, "test").Succeeded);
        Assert.Equal(TxNoiseSetupOutcome.NameTaken, f.Noise.SaveTransmitSetup("studio", replace: false, "test").Outcome);
        Assert.Equal(new[] { "Studio" }, f.Noise.TransmitSetupNames());

        f.Noise.TransmitToggle("nnr", "test");
        f.Noise.SetGateOn(false, "test");
        f.Noise.ApplyTransmitSetup("Studio", "test");
        Assert.True(f.Noise.TransmitIsOn("nnr"));
        Assert.True(gate.Enabled);
        Assert.Equal(SpeechSubject.TransmitNoiseSetup, f.Spoken.Said[^1].Subject);

        Assert.True(f.Noise.RenameTransmitSetup("Studio", "Desk").Succeeded);
        Assert.Equal((Radio8600, "Studio", "Desk"), Assert.Single(repointed));

        const string other = "9999-8888-7777-6666";
        Assert.True(f.Noise.CopyTransmitSetupToRadio("Desk", other, "the 6300", replace: false).Succeeded);
        Assert.NotNull(new TxNoiseSetupStore(_root).Find(other, "Desk"));
        Assert.True(f.Noise.DeleteTransmitSetup("Desk").Succeeded);
        Assert.Empty(f.Noise.TransmitSetupNames());

        f.Spoken.Clear();
        f.Noise.ApplyTransmitSetup("Gone", "test");
        Assert.Equal(Lexicon.Get("audio.denoise.tx.setup_missing", ("setup", "Gone")), f.Spoken.Last);
    }

    [Fact]
    public void Setups_need_a_radio_and_say_so()
    {
        var (f, _) = Make();
        using var _f = f;
        f.RadioPresent = false;
        Assert.Empty(f.Noise.TransmitSetupNames());
        Assert.False(f.Noise.SaveTransmitSetup("X", false, "test").Succeeded);
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.setups_need_radio"), f.Spoken.Last);
        // The engines themselves are held with no radio (#1077).
        f.Noise.TransmitToggle("rnnoise", "test");
        Assert.True(f.Noise.TransmitIsOn("rnnoise"));
    }

    // ── Item 5: no per-mode memory on transmit ──

    [Fact]
    public void Transmit_changes_never_touch_the_mode_groups_and_a_mode_change_never_touches_transmit()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.Attach("USB");
        string before = Describe(f.Config.NoiseModeMemory);
        f.Noise.TransmitToggle("nnr", "test");
        f.Noise.TransmitToggle("nr4", "test");
        Assert.Equal(before, Describe(f.Config.NoiseModeMemory));
        f.Noise.OnModeChanged("CW");
        f.Noise.OnModeChanged("USB");
        Assert.True(f.Noise.TransmitIsOn("nnr"));
        Assert.True(f.Noise.TransmitIsOn("nr4"));
    }

    private static string Describe(NoiseModeMemoryConfig m) =>
        string.Join("|", new[] { m.Voice, m.Cw, m.Digital }.Select(g => g.Known + ":" + g.Spectral.On + g.Spectral.EngineId + ":" + g.Neural.On + g.Neural.EngineId));
}
