using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
[Collection("Transmit conditioning statics")]
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
    /// <summary>
    /// The gate and the cleanup monitor, in memory, DEFERRING LIKE THE REAL ONE
    /// (repair round 2, item 2): with a chain, its on and off go through the
    /// chain's held changes as TxAudioConditioning.SetGateEnabled does, and its
    /// levels and Auto through the chain's continuous-lever order. An immediate
    /// fake hid the stale-value replies Codex found through the real bridge.
    /// </summary>
    internal sealed class FakeGate : ITransmitGate
    {
        /// <summary>The chain whose order the gate follows; null answers by <see cref="Keyed"/> alone.</summary>
        public TxNoiseChain? Chain;
        public bool Keyed;
        public bool Enabled { get; set; }
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
        public TxChangeResult SetEnabled(bool on)
        {
            if (Chain != null) return Chain.RunOrDefer(() => Enabled = on);
            Enabled = on;
            return Keyed ? TxChangeResult.AtUnkey : TxChangeResult.Now;
        }
        private TxChangeResult Level(Action write)
        {
            if (Chain != null) return Chain.Continuous(write);
            write();
            return TxChangeResult.Now;
        }
        public TxChangeResult SetAutoThreshold(bool on) => Level(() => AutoThreshold = on);
        public TxChangeResult SetLevel(string key, float value) => Level(() =>
        {
            switch (key)
            {
                case TxGateLever.Margin: MarginDb = value; break;
                case TxGateLever.Threshold: ThresholdDb = value; break;
                case TxGateLever.Attack: AttackMs = value; break;
                case TxGateLever.Hold: HoldMs = value; break;
                case TxGateLever.Release: ReleaseMs = value; break;
                case TxGateLever.Depth: RangeDb = value; break;
            }
        });
        public TxNoiseGateSetup Capture() => new()
        {
            Enabled = Enabled, AutoThreshold = AutoThreshold, ThresholdMarginDb = MarginDb, ThresholdDb = ThresholdDb,
            AttackMs = AttackMs, HoldMs = HoldMs, ReleaseMs = ReleaseMs, RangeDb = RangeDb,
        };
        public void ApplyNow(TxNoiseGateSetup g)
        {
            Enabled = g.Enabled; AutoThreshold = g.AutoThreshold; MarginDb = g.ThresholdMarginDb; ThresholdDb = g.ThresholdDb;
            AttackMs = g.AttackMs; HoldMs = g.HoldMs; ReleaseMs = g.ReleaseMs; RangeDb = g.RangeDb;
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
        var gate = new FakeGate { Chain = f.Transmit };
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
        Assert.StartsWith(Lexicon.Get("audio.noise_surface.tx.zero_off", VerbosityLevel.Chatty, ("engines", "")).Split('')[0], f.Spoken.Last);
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
    public void Escape_puts_back_the_transmit_slots_the_layer_changed_engines_floors_and_settings_included()
    {
        var (f, _) = Make();
        using var _f = f;
        // Before the layer: RNNoise on, its floor moved; NNR chosen nowhere.
        f.Noise.TransmitToggle("rnnoise", "test");
        f.Transmit.SetStrength("rnnoise", 0.5f);
        string nnrModelBefore = f.Transmit.NeuralSlot.GetSetting("nnr", "model").Choice!;
        var snap = f.Noise.TakeSnapshot();
        var keys = LayerKeys(f);
        keys.Handle(Keys.T);
        keys.Handle(Keys.N | Keys.Control);                  // NNR replaces RNNoise
        var model = f.Noise.Levers("nnr", false, DenoiseDirection.Transmit).Single(l => l.Key == "model");
        f.Noise.Set(model, null, f.Noise.OptionsFor(model).First(o => o.Name != nnrModelBefore).Name, NoiseController.LayerSource);
        keys.Handle(Keys.D2 | Keys.Control);
        // The layer's own floor move (Escape ownership round: a write from
        // outside the layer is kept, so the layer makes every change here).
        var floor = f.Noise.Levers("rnnoise", true, DenoiseDirection.Transmit).Single(l => l.Key == "floor");
        f.Noise.Set(floor, 10, null, NoiseController.LayerSource);
        Assert.NotEqual(0.5f, f.Transmit.GetStrength("rnnoise"), 3);
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.True(f.Transmit.WantsEnabled(DenoiseFamily.Neural));
        Assert.Equal("rnnoise", f.Transmit.ChosenEngineId(DenoiseFamily.Neural));
        Assert.Equal(0.5f, f.Transmit.GetStrength("rnnoise"), 3);
        Assert.Equal(nnrModelBefore, f.Transmit.NeuralSlot.GetSetting("nnr", "model").Choice);
        Assert.False(f.Transmit.WantsEnabled(DenoiseFamily.Spectral));
    }

    [Fact]
    public void Escape_while_keyed_holds_the_transmit_part_for_the_unkey_and_the_closing_sentence_says_so()
    {
        var (f, _) = Make();
        using var _f = f;
        var snap = f.Noise.TakeSnapshot();
        f.Noise.TransmitToggle("nnr", NoiseController.LayerSource);
        f.Transmit.KeySource = () => true;
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.Equal(TxChangeTiming.AtUnkey, f.Noise.LastRestoreTiming);
        Assert.True(f.Transmit.WantsEnabled(DenoiseFamily.Neural));      // held: nothing changed yet
        Assert.EndsWith(AtUnkey, f.Noise.WithRestoreTiming("closed."));
        f.Transmit.KeySource = () => false;
        f.Transmit.ApplyDeferred();
        Assert.False(f.Transmit.WantsEnabled(DenoiseFamily.Neural));
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
        f.PipelinePresent = false;                          // repair round 1, item 11: really no pipeline
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(NoiseLayerKeys.NextPresetKey);
        Assert.True(f.Noise.TransmitIsOn("deepfilternet3"));
        Assert.Equal(SpeechSubject.TransmitNoisePreset, f.Spoken.Said[^1].Subject);
        // Positive control: a receive preset with no pipeline is still refused.
        f.PipelinePresent = true;
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.SavePreset("Rx", true, false, false, PresetCollision.Replace, "test");
        f.PipelinePresent = false;
        f.Spoken.Clear();
        f.Noise.ApplyPreset("Rx", "test");
        Assert.Equal(Lexicon.Get("connect.command_needs_radio"), f.Spoken.Last);
    }

    // ── The gate ──

    [Fact]
    public void The_gate_speaks_its_timing_steps_its_margin_to_the_ends_and_returns_to_defaults_keeping_on_or_off()
    {
        var (f, gate) = Make();
        using var _f = f;
        f.Transmit.KeySource = () => true;
        f.Noise.SetGateOn(true, "test");
        Assert.False(gate.Enabled);                           // held, as the real gate is
        Assert.EndsWith(AtUnkey, f.Spoken.Last);
        Assert.Equal(SpeechSubject.TransmitNoiseGate, f.Spoken.Said[^1].Subject);
        f.Transmit.KeySource = () => false;
        f.Transmit.ApplyDeferred();
        Assert.True(gate.Enabled);

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

    // ── Item 7: #1123, the active slice's mode ──

    [Fact]
    public void Moving_to_a_CW_slice_enters_CWs_group_and_moving_back_enters_voice_again()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.Attach("USB");
        f.Noise.NoteActiveSlice(0);
        f.Noise.Toggle("rnnoise", "test");                  // voice: RNNoise
        f.Noise.OnModeChanged("CW");                        // CW: its starting point, NNR
        Assert.True(f.Noise.IsOn("nnr"));
        f.Noise.OnModeChanged("USB");
        Assert.True(f.Noise.IsOn("rnnoise"));

        // Slice B is in CW. The radio reports no mode change when the active
        // slice changes: only the slice and its mode, read by the poll.
        f.Radio.SliceIdentity = "slice B";
        f.Radio.Mode = "CW";
        f.Spoken.Clear();
        f.Noise.NoteActiveSlice(1);
        Assert.Equal(NoiseModeGroup.Cw, f.Noise.CurrentGroup);
        Assert.True(f.Noise.IsOn("nnr"));
        Assert.Contains(SpeechSubject.NoiseModeStart, f.Spoken.Said.Select(s => s.Subject));

        // And back to slice A, in USB.
        f.Radio.SliceIdentity = "slice A";
        f.Radio.Mode = "USB";
        f.Noise.NoteActiveSlice(0);
        Assert.Equal(NoiseModeGroup.Voice, f.Noise.CurrentGroup);
        Assert.True(f.Noise.IsOn("rnnoise"));

        // The main window's poll reaches the same path when Home's fields are hidden.
        f.Radio.SliceIdentity = "slice B";
        f.Radio.Mode = "CW";
        f.Noise.FollowReceiveMode("test");
        Assert.True(f.Noise.IsOn("nnr"));
        // A second report of the same group does nothing more.
        f.Spoken.Clear();
        f.Noise.OnModeChanged("CW");
        Assert.Empty(f.Spoken.Said);
    }

    [Fact]
    public void Before_a_connect_has_entered_a_group_the_slice_follow_does_nothing()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Radio.Mode = "CW";
        f.Noise.FollowReceiveMode("test");
        f.Noise.NoteActiveSlice(1);
        Assert.Empty(f.Spoken.Said);
        Assert.False(f.Config.NoiseModeMemory.Cw.Known);
    }

    [Fact]
    public void The_main_window_poll_follows_the_receive_mode_whether_or_not_Home_is_shown()
    {
        // Source pin: the main window's poll asks the controller before, and
        // outside, the field panel's visibility check (#1123).
        string source = File.ReadAllText(Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "MainWindow.xaml.cs"));
        int follow = source.IndexOf("_noise?.FollowReceiveMode(", StringComparison.Ordinal);
        int panel = source.IndexOf("if (FieldsPanel.Visibility == Visibility.Visible)", StringComparison.Ordinal);
        Assert.True(follow > 0 && panel > follow, "the poll must follow the receive mode before the panel's own visibility check");
        // And the live radio reads the ACTIVE slice's mode, never the transmit one.
        string radio = File.ReadAllText(Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "NoiseRadio.cs"));
        Assert.Contains("public string ReceiveMode => _rig.RXMode", radio);
    }

    // ── Repair round 1 (Codex's check of H2) ──

    [Fact]
    public void Home_On_starts_the_engine_the_fields_show_even_while_its_choice_is_still_settling()
    {
        var (f, _) = Make();
        using var _f = f;
        // Codex's reproduction: the slot holds RNNoise; the operator arrows to NNR
        // and ticks On inside the 700 ms settle.
        Assert.Equal("rnnoise", f.Transmit.ChosenEngineId(DenoiseFamily.Neural));
        f.Noise.TransmitNoteSlotChoicePending(DenoiseFamily.Neural, "nnr");
        f.Noise.TransmitChooseSlotEngine(DenoiseFamily.Neural, "nnr", "home");
        f.Noise.TransmitSetShownOn(DenoiseFamily.Neural, "nnr", true, "home");
        Assert.True(f.Noise.TransmitIsOn("nnr"));
        Assert.False(f.Noise.TransmitIsOn("rnnoise"));
        Assert.Contains(Tx("NNR"), f.Spoken.Last);
        f.Clock.RunAll();
        Assert.True(f.Noise.TransmitIsOn("nnr"));
        // Off acts on the slot, whatever is pending.
        f.Noise.TransmitSetShownOn(DenoiseFamily.Neural, "nnr", false, "home");
        Assert.False(f.Transmit.WantsEnabled(DenoiseFamily.Neural));
    }

    [Fact]
    public void A_settle_never_overwrites_a_newer_action_on_its_slot()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("rnnoise", "test");
        f.Noise.TransmitNoteSlotChoicePending(DenoiseFamily.Neural, "nnr");
        f.Noise.TransmitChooseSlotEngine(DenoiseFamily.Neural, "nnr", "home");
        f.Noise.TransmitToggle("deepfilternet3", "menu");   // newer, from another road
        f.Clock.RunAll();
        Assert.True(f.Noise.TransmitIsOn("deepfilternet3"));
        Assert.Equal("deepfilternet3", f.Noise.TransmitSlotEngine(DenoiseFamily.Neural));
    }

    [Fact]
    public void A_transmit_choice_settling_after_the_layer_opens_never_moves_a_receive_lever()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.Toggle("rnnoise", "test");                   // receive RNNoise on
        float receiveBefore = f.Pipeline.NeuralSlot.GetStrength("rnnoise");
        // Codex's reproduction: a Home transmit choice still settling, then a fresh receive layer.
        f.Noise.TransmitNoteSlotChoicePending(DenoiseFamily.Neural, "rnnoise");
        f.Transmit.ChooseEngine(DenoiseFamily.Neural, "nnr");
        f.Noise.TransmitChooseSlotEngine(DenoiseFamily.Neural, "rnnoise", "home");
        var keys = LayerKeys(f);
        f.Noise.BeginSession();
        f.Clock.RunAll();                                    // the transmit choice lands
        Assert.Equal("rnnoise", f.Transmit.ChosenEngineId(DenoiseFamily.Neural)); // the Home intent still happened
        f.Spoken.Clear();
        keys.Handle(Keys.Down);
        Assert.Equal(receiveBefore, f.Pipeline.NeuralSlot.GetStrength("rnnoise"));
        Assert.Equal(Lexicon.Get("audio.noise_surface.no_engine_yet", VerbosityLevel.Chatty), f.Spoken.Last);
    }

    [Fact]
    public void A_receive_choice_settling_after_T_never_becomes_the_transmit_cursor()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.ChooseSlotEngine(DenoiseFamily.Neural, "nnr", "home");
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        f.Clock.RunAll();
        Assert.Null(f.Noise.CurrentInLayer);
        float before = f.Transmit.GetStrength("nnr");
        keys.Handle(Keys.Up);
        Assert.Equal(before, f.Transmit.GetStrength("nnr"));
    }

    [Fact]
    public void A_receive_choice_settling_after_the_layer_opens_is_not_the_layers_cursor_either()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.Toggle("rnnoise", "test");
        double before = f.Pipeline.NeuralSlot.GetSetting("nnr", "mask_floor_db").Number;
        f.Noise.ChooseSlotEngine(DenoiseFamily.Neural, "nnr", "home");
        f.Noise.BeginSession();                              // the same direction, a new session
        f.Clock.RunAll();                                    // NNR replaces RNNoise: the Home intent stands
        Assert.True(f.Noise.IsOn("nnr"));
        Assert.Null(f.Noise.CurrentInLayer);
        LayerKeys(f).Handle(Keys.Down);
        Assert.Equal(before, f.Pipeline.NeuralSlot.GetSetting("nnr", "mask_floor_db").Number);
    }

    [Fact]
    public void The_transmit_zero_list_ends_with_the_layer_session()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("nnr", "test");
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.D0);
        Assert.False(f.Noise.TransmitIsOn("nnr"));
        // The host's own sequence at Enter: EndLayerSession, EndSnapshot; then a new session.
        f.Noise.EndLayerSession();
        f.Noise.EndSnapshot();
        EnterTransmit(f, keys);
        keys.Handle(Keys.D0);
        Assert.False(f.Noise.TransmitIsOn("nnr"));
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.zero_already"), f.Spoken.Last);
    }

    [Fact]
    public void Choosing_with_the_slot_off_while_keyed_says_it_waits()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Transmit.KeySource = () => true;
        f.Noise.TransmitChooseSlotEngine(DenoiseFamily.Neural, "nnr", "home");
        f.Clock.RunAll();
        Assert.EndsWith(AtUnkey, f.Spoken.Last);
    }

    [Fact]
    public void The_brackets_say_the_timing_their_parts_returned_and_the_delay()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "rnnoise" });
        f.Config.NoiseFavourites.Add(new NoiseFavourite { EngineId = "nnr" });
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.OemCloseBrackets);                   // RNNoise
        keys.Handle(Keys.OemCloseBrackets);                   // NNR, with its delay
        string clauseStart = Lexicon.Get("audio.noise_surface.tx.delay_clause", ("ms", 0)).Split('0')[0];
        Assert.Contains(clauseStart, f.Spoken.Last);
        Assert.DoesNotContain(AtUnkey, f.Spoken.Last);
        f.Transmit.KeySource = () => true;
        keys.Handle(Keys.OemCloseBrackets);                   // back to RNNoise while keyed
        Assert.EndsWith(AtUnkey, f.Spoken.Last);
    }

    [Fact]
    public void At_Terse_a_transmit_engine_says_the_short_delay()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Spoken.Verbosity = VerbosityLevel.Terse;
        f.Noise.TransmitToggle("nnr", "test");
        string shortStart = Lexicon.Get("audio.noise_surface.tx.delay_clause_short", ("ms", 0)).Split('0')[0];
        Assert.Contains(shortStart, f.Spoken.Last);
    }

    [Fact]
    public void A_whole_setup_and_its_gate_values_wait_for_the_unkey_together()
    {
        var (f, gate) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("nnr", "test");
        gate.MarginDb = 12;
        Assert.True(f.Noise.SaveTransmitSetup("Desk", false, "test").Succeeded);
        f.Noise.TransmitToggle("nnr", "test");                // off
        gate.MarginDb = 5;
        f.Transmit.KeySource = () => true;
        f.Spoken.Clear();
        f.Noise.ApplyTransmitSetup("Desk", "test");
        Assert.False(f.Noise.TransmitIsOn("nnr"));
        Assert.Equal(5, gate.MarginDb);                       // the gate's values wait too
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.setup_waiting", ("setup", "Desk")), f.Spoken.Last);
        Assert.EndsWith(AtUnkey, f.Spoken.Last);
        f.Transmit.KeySource = () => false;
        f.Transmit.ApplyDeferred();
        Assert.True(f.Noise.TransmitIsOn("nnr"));
        Assert.Equal(12, gate.MarginDb);
    }

    [Fact]
    public void The_gates_defaults_leave_the_engines_alone()
    {
        var (f, gate) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("nnr", "test");
        gate.AttackMs = 9;
        f.Noise.GateReturnToDefaults("test");
        Assert.True(f.Noise.TransmitIsOn("nnr"));
        Assert.Equal(new TxNoiseGateSetup().AttackMs, gate.AttackMs);
    }

    [Fact]
    public void A_preset_that_changes_only_a_floor_or_the_gate_says_what_it_changed()
    {
        var (f, gate) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("rnnoise", "test");
        f.Transmit.SetStrength("rnnoise", 0.3f);
        f.Noise.SavePreset("Soft", false, false, true, PresetCollision.Replace, "test");
        f.Transmit.SetStrength("rnnoise", 0.8f);
        f.Spoken.Clear();
        f.Noise.ApplyPreset("Soft", "test", DenoiseDirection.Transmit);
        Assert.Equal(0.3f, f.Transmit.GetStrength("rnnoise"), 3);
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.phrase_settings", ("engine", Tx("RNNoise"))), f.Spoken.Last);
        Assert.DoesNotContain(Lexicon.Get("audio.noise_surface.preset_nothing_changed"), f.Spoken.Last);

        gate.AttackMs = 15;
        f.Noise.ApplyPreset("Soft", "test", DenoiseDirection.Transmit);
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.phrase_gate"), f.Spoken.Last);
        f.Noise.ApplyPreset("Soft", "test", DenoiseDirection.Transmit);
        Assert.Contains(Lexicon.Get("audio.noise_surface.preset_nothing_changed"), f.Spoken.Last);
    }

    [Fact]
    public void A_preset_holding_both_directions_applies_both_and_names_each()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.TransmitToggle("nnr", "test");
        f.Noise.SavePreset("Both", true, false, true, PresetCollision.Replace, "test");
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.TransmitToggle("nnr", "test");
        f.Spoken.Clear();
        f.Noise.ApplyPreset("Both", "test");
        Assert.True(f.Noise.IsOn("rnnoise"));
        Assert.True(f.Noise.TransmitIsOn("nnr"));
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.preset_part", ("list", Lexicon.Get("audio.noise_surface.phrase_on", ("engine", Tx("NNR")))))
            .Split(Lexicon.Get("audio.noise_surface.phrase_on", ("engine", Tx("NNR"))))[0], f.Spoken.Last);
    }

    [Fact]
    public void The_layers_no_engine_reply_after_T_is_a_transmit_answer()
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(NoiseLayerKeys.FavouriteKey);
        Assert.Equal(SpeechSubject.TransmitNoiseLevel, f.Spoken.Said[^1].Subject);
        keys.Handle(Keys.T);
        keys.Handle(NoiseLayerKeys.FavouriteKey);
        Assert.Equal(SpeechSubject.NoiseLevel, f.Spoken.Said[^1].Subject);
    }

    [Fact]
    public void The_transmit_summary_tells_chosen_from_running()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("nnr", "test");
        // Chosen, never processed a buffer (no radio's microphone audio): not yet running.
        Assert.Contains(Lexicon.Get("audio.noise_surface.active.waiting", VerbosityLevel.Chatty, ("engine", "NNR")), f.Noise.TransmitActiveSummary());
        // Chosen while keyed: starts at the unkey.
        f.Transmit.KeySource = () => true;
        f.Noise.TransmitToggle("deepfilternet3", "test");
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.active.at_unkey", ("engine", "DeepFilterNet3")), f.Noise.TransmitActiveSummary());
        // Cannot run here at all.
        f.Transmit.KeySource = () => false;
        f.Unavailable["deepfilternet3"] = Unavailability.ModelMissing;
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.active.cannot_run", ("engine", "DeepFilterNet3")), f.Noise.TransmitActiveSummary());
    }

    [Fact]
    public void A_rename_whose_microphones_could_not_be_repointed_is_put_back_and_said()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.RepointMicrophones = (r, a, b) => NoiseController.RepointNotSaved;
        Assert.True(f.Noise.SaveTransmitSetup("Studio", false, "test").Succeeded);
        var result = f.Noise.RenameTransmitSetup("Studio", "Desk");
        Assert.False(result.Succeeded);
        Assert.Equal(new[] { "Studio" }, f.Noise.TransmitSetupNames());
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.setup_rename_undone", ("old", "Studio"), ("new", "Desk")), f.Spoken.Last);
    }

    // ── Repair round 1, part 2: the floor's own answer ──

    [Fact]
    public void SetStrength_answers_its_timing_live_while_keyed_held_behind_a_waiting_setup_and_refused_off_transmit()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Transmit.KeySource = () => true;
        Assert.Equal(TxChangeTiming.Now, f.Transmit.SetStrength("rnnoise", 0.6f).Timing);   // live, the ruling
        Assert.Equal(0.6f, f.Transmit.GetStrength("rnnoise"), 3);
        f.Transmit.ApplySlots(new TxNoiseSlotSetup(), new TxNoiseSlotSetup());              // a whole setup, held
        Assert.Equal(TxChangeTiming.AtUnkey, f.Transmit.SetStrength("rnnoise", 0.4f).Timing);
        Assert.Equal(TxChangeTiming.Refused, f.Transmit.SetStrength("spectral_capture", 0.4f).Timing);
        // Unkeyed but not yet drained: still behind the held setup (Track H repair round 2's order).
        f.Transmit.KeySource = () => false;
        f.Transmit.ApplyDeferred();
        Assert.Equal(0.4f, f.Transmit.GetStrength("rnnoise"), 3);
    }

    [Fact]
    public void A_floor_held_behind_a_waiting_setup_says_the_value_asked_for_and_that_it_waits()
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.R | Keys.Control);                  // RNNoise: the floor is its first lever
        f.Transmit.KeySource = () => true;
        f.Transmit.ApplySlots(f.Transmit.CaptureSlots().Spectral, f.Transmit.CaptureSlots().Neural);
        var floor = f.Noise.FloorLeverOf("rnnoise", DenoiseDirection.Transmit)!;
        double before = f.Noise.NumberOf(floor)!.Value;
        keys.Handle(Keys.Up);
        Assert.Equal(before, f.Noise.NumberOf(floor));       // held: nothing changed yet
        Assert.EndsWith(AtUnkey, f.Spoken.Last);
        double asked = before + NoiseFloor.Step * NoiseController.FloorSign(+1);
        Assert.Contains(asked.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), f.Spoken.Last);
        f.Transmit.KeySource = () => false;
        f.Transmit.ApplyDeferred();
        Assert.Equal(asked, f.Noise.NumberOf(floor));

        // Unkeyed with a setup still held (Track H repair round 2's order): the
        // held setup runs first, then the floor, so the floor asked for last wins
        // and the answer is Now.
        f.Transmit.KeySource = () => true;
        f.Transmit.ApplySlots(f.Transmit.CaptureSlots().Spectral, f.Transmit.CaptureSlots().Neural);
        f.Transmit.KeySource = () => false;
        f.Spoken.Clear();
        keys.Handle(Keys.Up);
        Assert.DoesNotContain(AtUnkey, f.Spoken.Last);
        Assert.False(f.Transmit.HasDeferredChanges);
        Assert.Equal(asked + NoiseFloor.Step * NoiseController.FloorSign(+1), f.Noise.NumberOf(floor));
    }

    // ── Repair round 2 (Codex's re-check of 4797e9b2) ──

    [Fact]
    public void A_newer_gate_auto_choice_follows_a_held_setup_through_the_real_bridge()
    {
        var (f, _) = Make();
        using var _f = f;
        bool oldAuto = TxAudioConditioning.AutoThreshold;
        float oldMargin = TxAudioConditioning.ThresholdMarginDb;
        TxAudioConditioning.UseChainForTest(f.Transmit);
        try
        {
            f.Noise.TransmitGate = new AppTransmitGate();
            f.Noise.SetGateAutoThreshold(true, "home");
            f.Noise.SavePreset("Automatic", false, false, true, PresetCollision.Replace, "test");
            // The preset must differ from the picture to be held at all (bench round 2:
            // an identical whole request with nothing waiting queues nothing).
            TxAudioConditioning.ThresholdMarginDb += 1;
            f.Transmit.KeySource = () => true;
            f.Noise.ApplyPreset("Automatic", "test", DenoiseDirection.Transmit);
            Assert.True(f.Transmit.HasDeferredChanges);
            f.Spoken.Clear();
            f.Noise.SetGateAutoThreshold(false, "home");
            Assert.EndsWith(AtUnkey, f.Spoken.Last);           // it waits behind the setup, and says so
            f.Transmit.KeySource = () => false; f.Transmit.ApplyDeferred();
            Assert.False(TxAudioConditioning.AutoThreshold);  // the newer choice wins
        }
        finally
        {
            f.Transmit.KeySource = () => false; f.Transmit.ApplyDeferred();
            TxAudioConditioning.AutoThreshold = oldAuto;
            TxAudioConditioning.ThresholdMarginDb = oldMargin;
            TxAudioConditioning.UseChainForTest(null!);
        }
    }

    [Fact]
    public void A_gate_margin_held_after_a_setup_says_its_asked_value_and_timing_through_the_real_bridge()
    {
        var (f, _) = Make();
        using var _f = f;
        float oldMargin = TxAudioConditioning.ThresholdMarginDb;
        TxAudioConditioning.UseChainForTest(f.Transmit);
        try
        {
            f.Noise.TransmitGate = new AppTransmitGate();
            TxAudioConditioning.ThresholdMarginDb = 8;
            f.Noise.SavePreset("Gate", false, false, true, PresetCollision.Replace, "test");
            TxAudioConditioning.ThresholdMarginDb = 9;          // differs, so the preset is held (bench round 2)
            f.Transmit.KeySource = () => true;
            f.Noise.ApplyPreset("Gate", "test", DenoiseDirection.Transmit);
            Assert.True(f.Transmit.HasDeferredChanges);
            f.Spoken.Clear();
            f.Noise.SetGate(f.Noise.GateLever(TxGateLever.Margin)!, 15, "home");
            string answer = f.Spoken.Last;
            Assert.Equal(9, TxAudioConditioning.ThresholdMarginDb);
            f.Transmit.KeySource = () => false; f.Transmit.ApplyDeferred();
            Assert.Equal(15, TxAudioConditioning.ThresholdMarginDb);
            Assert.True(answer.Contains(AtUnkey) && answer.Contains("15"), answer);
        }
        finally
        {
            f.Transmit.KeySource = () => false; f.Transmit.ApplyDeferred();
            TxAudioConditioning.ThresholdMarginDb = oldMargin;
            TxAudioConditioning.UseChainForTest(null!);
        }
    }

    [Fact]
    public void A_held_default_floor_says_the_default_it_asked_for()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("rnnoise", "test");
        var floor = f.Noise.FloorLeverOf("rnnoise", DenoiseDirection.Transmit)!;
        f.Noise.Set(floor, 70, null, "test");
        Assert.Equal(70, f.Noise.NumberOf(floor));
        f.Transmit.KeySource = () => true;
        f.Transmit.ApplySlots(f.Transmit.CaptureSlots().Spectral, f.Transmit.CaptureSlots().Neural);
        f.Spoken.Clear();
        f.Noise.TransmitReturnToDefaults("rnnoise", "test");
        Assert.Equal(70, f.Noise.NumberOf(floor));
        Assert.Contains(AtUnkey, f.Spoken.Last);
        Assert.Contains("20", f.Spoken.Last);
        Assert.DoesNotContain("70", f.Spoken.Last);
        f.Transmit.KeySource = () => false; f.Transmit.ApplyDeferred();
        Assert.Equal(20, f.Noise.NumberOf(floor));
    }

    [Fact]
    public void The_gates_held_defaults_say_the_defaults_asked_for()
    {
        var (f, gate) = Make();
        using var _f = f;
        gate.MarginDb = 13;
        f.Transmit.KeySource = () => true;
        f.Noise.GateReturnToDefaults("test");
        Assert.Equal(13, gate.MarginDb);                      // held
        Assert.Contains(new TxNoiseGateSetup().ThresholdMarginDb.ToString("0", System.Globalization.CultureInfo.InvariantCulture), f.Spoken.Last);
        Assert.DoesNotContain("13", f.Spoken.Last);
        Assert.EndsWith(AtUnkey, f.Spoken.Last);
    }

    [Fact]
    public void A_preset_that_changes_an_off_slots_engine_does_not_say_nothing_changed()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("nnr", "test"); f.Noise.TransmitToggle("nnr", "test");
        f.Noise.SavePreset("Off", false, false, true, PresetCollision.Replace, "test");
        f.Transmit.ChooseEngine(DenoiseFamily.Neural, "rnnoise");
        f.Spoken.Clear();
        f.Noise.ApplyPreset("Off", "test", DenoiseDirection.Transmit);
        Assert.Equal("nnr", f.Transmit.ChosenEngineId(DenoiseFamily.Neural));
        Assert.DoesNotContain(Lexicon.Get("audio.noise_surface.preset_nothing_changed"), f.Spoken.Last);
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.phrase_engine_choice", ("engine", Tx("NNR"))), f.Spoken.Last);
    }

    [Fact]
    public void A_held_setup_from_all_off_is_still_in_the_active_readout()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("nnr", "test");
        f.Noise.SavePreset("Enabled", false, false, true, PresetCollision.Replace, "test");
        f.Noise.TransmitToggle("nnr", "test");
        f.Transmit.KeySource = () => true;
        f.Noise.ApplyPreset("Enabled", "test", DenoiseDirection.Transmit);
        Assert.True(f.Transmit.HasDeferredChanges);
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.active.held"), f.Noise.TransmitActiveSummary());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_same_session_settle_never_steals_a_newer_cursor(bool transmit)
    {
        // Codex's adversarial stress case. Not reachable from the UI as far as I
        // can establish (the layer owns the keyboard, and only Home's engine
        // fields and the Noise Profiles window schedule a settle), but cheap to
        // close: any newer cursor assignment makes an older delayed one stale.
        var (f, _) = Make();
        using var _f = f;
        f.Noise.BeginSession();
        if (transmit)
        {
            f.Noise.SwitchLayerDirection();
            f.Noise.TransmitChooseSlotEngine(DenoiseFamily.Neural, "nnr", "home");
            f.Noise.TransmitToggle("nr4", "noise layer");
        }
        else
        {
            f.Noise.ChooseSlotEngine(DenoiseFamily.Neural, "nnr", "home");
            f.Noise.Toggle("nr4", "noise layer");
        }
        Assert.Equal("nr4", f.Noise.CurrentInLayer);
        f.Clock.RunAll();
        Assert.Equal("nnr", transmit ? f.Transmit.ChosenEngineId(DenoiseFamily.Neural) : f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.Equal("nr4", f.Noise.CurrentInLayer);
    }

    [Fact]
    public void The_Workshops_load_list_holds_the_noise_only_presets_its_save_makes_after_the_chain_presets()
    {
        var store = new AudioChainPresets();
        store.Presets.Add(new AudioChainPreset("Ragchew"));
        store.NoisePresets.Add(new AudioChainPreset("Desk") { HoldsTransmitChain = false, TransmitNoise = new TxNoiseSetup() });
        Assert.Equal(new[] { "Ragchew", "Desk" }, Dialogs.AudioWorkshopDialog.WorkshopPresetList(store).Select(p => p.Name));
    }

    // ── H3's edge probe through H2's own whole-change roads ──

    private sealed class PausingDescriptors : IReadOnlyList<EngineSettingDescriptor>
    {
        private readonly IReadOnlyList<EngineSettingDescriptor> _inner;
        public Action? Once;
        public PausingDescriptors(IReadOnlyList<EngineSettingDescriptor> inner) => _inner = inner;
        public int Count => _inner.Count;
        public EngineSettingDescriptor this[int index] => _inner[index];
        public IEnumerator<EngineSettingDescriptor> GetEnumerator()
        {
            System.Threading.Interlocked.Exchange(ref Once, null)?.Invoke();
            return _inner.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Codex's H3 edge probe, driven through each H2 road that applies a whole
    /// transmit change: a held change is admitted, paused part way, a new over
    /// starts, and the audio must not change until the next unkeyed buffer,
    /// when the whole change lands. The test engines multiply by gain (RNNoise
    /// 2) and "amount": 2 before, 6 after.
    /// </summary>
    [Theory]
    [InlineData("setup")]
    [InlineData("preset")]
    [InlineData("escape")]
    public void A_whole_change_through_H2s_roads_never_lands_partway_through_a_new_over(string road)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var entries = TxNoiseChainTests.Catalog().Entries;
        var pause = new PausingDescriptors(entries.Single(e => e.Id == "nr4").Settings);
        var catalog = new DenoiseEngineCatalog(entries.Select(e => e.Id == "nr4" ? e with { SettingDescriptors = pause } : e));
        var c = new TxNoiseChain(catalog);
        bool keyed = false;
        c.KeySource = () => System.Threading.Volatile.Read(ref keyed);
        var radio = new NoiseFixture.FakeRadio { Serial = Radio8600 };
        var config = new AudioOutputConfig();
        var noise = new NoiseController(catalog, () => radio, () => null, () => config, () => { },
            new NoiseFixture.Speaker(), new NoiseFixture.Intents(), new NoiseFixture.Scheduler(), transmitChain: () => c);
        var setups = new TxNoiseSetupStore(_root);
        noise.TransmitSetupStore = () => setups;
        noise.TransmitGate = new FakeGate { Chain = c };
        var presets = new MemoryPresets();
        noise.PresetStore = presets;

        c.SetStrength("rnnoise", 1);
        c.SetEnabled(DenoiseFamily.Neural, true);
        float[] Run()
        {
            var b = Enumerable.Repeat(1f, 960).ToArray();
            c.Process(b, b.Length, 48000, System.Threading.Volatile.Read(ref keyed));
            return b;
        }
        Assert.All(Run(), v => Assert.Equal(2f, v));

        // What each road will put in place: the neural slot's RNNoise at amount 3.
        var target = new TxNoiseSetup
        {
            Name = "Probe",
            Neural = new TxNoiseSlotSetup { EngineId = "rnnoise", Enabled = true,
                Engines = { new DenoiseEngineSettings { EngineId = "rnnoise", Direction = DenoiseDirection.Transmit, Strength = 1,
                    Values = { new DenoiseNamedValue { Name = "amount", Number = 3 } } } } },
        };
        NoiseSnapshot? snap = null;
        if (road == "escape")
        {
            // The picture Escape puts back holds amount 3; the layer then moved it to 1.
            c.SetSetting("rnnoise", "amount", new EngineSettingValue(3));
            Assert.All(Run(), v => Assert.Equal(6f, v));
            snap = noise.TakeSnapshot();
            // The layer's own move (Escape ownership round): Escape undoes only those.
            var amount = noise.Levers("rnnoise", true, DenoiseDirection.Transmit).Single(l => l.Key == "amount");
            noise.Set(amount, 1, null, NoiseController.LayerSource);
            Assert.Equal(1, c.NeuralSlot.GetSetting("rnnoise", "amount").Number);
            // And the spectral slot (off, so nothing heard): Escape now puts back
            // only the slots the layer changed, and the pause sits in NR4's
            // settings, so the probe needs the layer to have changed that slot too.
            noise.Set(noise.Levers("nr4", true, DenoiseDirection.Transmit).Single(l => l.Key == "floor"), 30, null, NoiseController.LayerSource);
            Assert.All(Run(), v => Assert.Equal(2f, v));
        }
        else if (road == "setup") Assert.True(setups.Save(Radio8600, target).Succeeded);
        else presets.Store.NoisePresets.Add(new AudioChainPreset("Probe") { HoldsTransmitChain = false, TransmitNoise = target });

        System.Threading.Volatile.Write(ref keyed, true);
        switch (road)
        {
            case "setup": noise.ApplyTransmitSetup("Probe", "test"); break;
            case "preset": noise.ApplyPreset("Probe", "test", DenoiseDirection.Transmit); break;
            case "escape": Assert.True(noise.RestoreSnapshot(snap!, "test")); break;
        }
        Assert.True(c.HasDeferredChanges, road + ": the road did not hold its change while keyed");

        using var entered = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        Exception? workerError = null;
        pause.Once = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("release"); };
        System.Threading.Volatile.Write(ref keyed, false);
        var worker = new System.Threading.Thread(() => { try { c.ApplyDeferred(); } catch (Exception e) { workerError = e; } });
        worker.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), road + ": the change was not admitted, so this proves nothing");
            System.Threading.Volatile.Write(ref keyed, true); // a NEW over, after the drain's key check
            for (int i = 0; i < 3; i++) Assert.All(Run(), v => Assert.Equal(2f, v));
            release.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(workerError);
            var observed = Enumerable.Range(0, 4).Select(_ => Run()[0]).ToList();
            Assert.True(observed.All(v => v == 2f), road + ": the whole change landed mid-over: " + string.Join(", ", observed));
            System.Threading.Volatile.Write(ref keyed, false);
            Assert.All(Run(), v => Assert.Equal(6f, v));
        }
        finally
        {
            release.Set();
            if (worker.IsAlive) Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public void The_gates_whole_defaults_run_inside_one_held_change_and_reach_the_gate_only_at_the_unkey()
    {
        // The gate half of H3's staging is TxAudioConditioner.StageGate, which
        // needs a connected radio's conditioner; this proves H2's road reaches
        // the gate's whole-apply ONLY through the held change. The app's
        // ApplyNow is TxAudioConditioning.ApplyGateNow, which stages (source pin below).
        var (f, gate) = Make();
        using var _f = f;
        gate.MarginDb = 13;
        f.Transmit.KeySource = () => true;
        f.Noise.GateReturnToDefaults("test");
        Assert.Equal(13, gate.MarginDb);
        f.Transmit.KeySource = () => false; f.Transmit.ApplyDeferred();
        Assert.Equal(new TxNoiseGateSetup().ThresholdMarginDb, gate.MarginDb);
        string source = File.ReadAllText(Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "TxAudioConditioning.cs"));
        int apply = source.IndexOf("internal static void ApplyGateNow", StringComparison.Ordinal);
        Assert.True(apply > 0 && source.IndexOf("StageGate(", apply, StringComparison.Ordinal) > apply,
            "ApplyGateNow must stage the gate for an unkeyed buffer (Track H repair round 3)");
        string bridge = File.ReadAllText(Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "NoiseController.Transmit.cs"));
        Assert.Contains("public void ApplyNow(TxNoiseGateSetup gate) => TxAudioConditioning.ApplyGateNow(gate);", bridge);
    }

    // ── Words round: the four source changes Codex found ──

    [Fact]
    public void A_transmit_model_change_never_promises_a_cross_fade()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("nnr", "test");
        var model = f.Noise.Levers("nnr", false, DenoiseDirection.Transmit).Single(l => l.Key == "model");
        var other = f.Noise.OptionsFor(model).First(o => o.Name != f.Noise.ChoiceOf(model));
        f.Spoken.Clear();
        f.Noise.Set(model, null, other.Name, "test");
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.lever_model", VerbosityLevel.Chatty,
            ("engine", Tx("NNR")), ("value", f.Noise.OptionText(other))), f.Spoken.Last);
        Assert.NotEqual(Lexicon.Get("audio.noise_surface.tx.lever_model", VerbosityLevel.Chatty, ("engine", "x"), ("value", "y")),
            Lexicon.Get("audio.noise_surface.lever_model", VerbosityLevel.Chatty, ("engine", "x"), ("value", "y")));
    }

    [Fact]
    public void After_T_the_no_engine_hints_name_the_transmit_keys_and_receive_keeps_its_own()
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        f.Noise.BeginSession();
        keys.Handle(Keys.Up);
        Assert.Equal(Lexicon.Get("audio.noise_surface.no_engine_yet", VerbosityLevel.Chatty), f.Spoken.Last);
        Assert.Equal("audio.noise_layer.switches_only", f.Noise.SwitchesOnlyKey);
        keys.Handle(Keys.T);
        keys.Handle(Keys.Up);
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.no_engine_yet", VerbosityLevel.Chatty), f.Spoken.Last);
        Assert.Equal("audio.noise_layer.tx.switches_only", f.Noise.SwitchesOnlyKey);
        string commands = File.ReadAllText(Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "KeyCommands.cs"));
        Assert.Contains("PickTargetHint = () => Radios.Lexicon.Get(noise.SwitchesOnlyKey)", commands);
        Assert.Contains("WrongAxisHint = () => Radios.Lexicon.Get(noise.SwitchesOnlyKey)", commands);
    }

    [Fact]
    public void Escape_puts_back_the_transmit_gate_a_preset_changed_inside_the_layer()
    {
        var (f, gate) = Make();
        using var _f = f;
        gate.Enabled = true; gate.MarginDb = 12; gate.AttackMs = 5;
        f.Noise.SavePreset("Gate", false, false, true, PresetCollision.Replace, "test");
        gate.Enabled = false; gate.MarginDb = 8; gate.AttackMs = 3;
        var snap = f.Noise.TakeSnapshot();
        f.Noise.StepTransmitPreset(+1, "noise layer");        // the preset changes the gate inside the layer
        Assert.True(gate.Enabled);
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.False(gate.Enabled);
        Assert.Equal(8, gate.MarginDb);
        Assert.Equal(3, gate.AttackMs);
        // Keyed, the gate waits for the unkey with the slots, and the closing sentence says so.
        snap = f.Noise.TakeSnapshot();
        f.Noise.StepTransmitPreset(+1, NoiseController.LayerSource);   // unkeyed: it lands now
        Assert.Equal(12, gate.MarginDb);
        f.Transmit.KeySource = () => true;
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.Equal(12, gate.MarginDb);
        Assert.Equal(TxChangeTiming.AtUnkey, f.Noise.LastRestoreTiming);
        f.Transmit.KeySource = () => false; f.Transmit.ApplyDeferred();
        Assert.Equal(8, gate.MarginDb);
        Assert.False(gate.Enabled);
    }

    // ── Escape ownership round: Escape undoes only the layer's own changes ──

    [Fact]
    public void Escape_keeps_a_gate_change_made_outside_the_layer_when_the_layer_changed_nothing()
    {
        // Codex's first reproduction: restored=True and the gate back to off with no layer edit.
        var (f, gate) = Make();
        using var _f = f;
        var snap = f.Noise.TakeSnapshot();
        f.Noise.SetGateOn(true, "Home");
        Assert.True(gate.Enabled);
        Assert.False(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.True(gate.Enabled);
    }

    [Fact]
    public void Escape_keeps_a_setup_applied_through_the_real_owner_after_the_picture()
    {
        // Codex's third reproduction, through TxAudioConditioning itself: a setup
        // applied outside the layer after the picture was gate off, margin 8 again.
        var (f, _) = Make();
        using var _f = f;
        using var ctx = new TxGateOwnerTests.Context();
        TxAudioConditioning.UseChainForTest(f.Transmit);
        f.Noise.TransmitGate = new AppTransmitGate();
        var snap = f.Noise.TakeSnapshot();
        var setup = f.Noise.CaptureTransmitSetup("Outside");
        setup.Gate.Enabled = true;
        setup.Gate.ThresholdMarginDb = 14;
        TxAudioConditioning.ApplySetup(setup);
        Assert.True(TxAudioConditioning.GateEnabled);
        Assert.False(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.True(TxAudioConditioning.GateEnabled);
        Assert.Equal(14, TxAudioConditioning.ThresholdMarginDb, 3);
    }

    [Fact]
    public void Escape_keeps_an_outside_change_made_after_the_layers_own_change_to_the_same_part()
    {
        var (f, gate) = Make();
        using var _f = f;
        gate.Enabled = true; gate.MarginDb = 12;
        f.Noise.SavePreset("Gate", false, false, true, PresetCollision.Replace, "test");
        gate.Enabled = false; gate.MarginDb = 8;
        var snap = f.Noise.TakeSnapshot();
        f.Noise.StepTransmitPreset(+1, NoiseController.LayerSource);    // the layer turns the gate on, margin 12
        f.Noise.SetGateOn(false, "Home");                                // then Home turns it off
        f.Noise.SetGate(f.Noise.GateLever(TxGateLever.Margin)!, 14, "Home");
        Assert.False(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.False(gate.Enabled);
        Assert.Equal(14, gate.MarginDb);
    }

    [Fact]
    public void Escape_withdraws_a_preset_the_layer_asked_for_while_keyed_slots_and_gate_alike()
    {
        // Codex's second reproduction: before Escape off, waiting; then on at the unkey.
        var (f, gate) = Make();
        using var _f = f;
        gate.Enabled = true;
        f.Noise.TransmitToggle("nnr", "test");
        f.Noise.SavePreset("Waiting", false, false, true, PresetCollision.Replace, "test");   // NNR on, gate on
        f.Noise.TransmitToggle("nnr", "test");
        gate.Enabled = false;
        string slotsBefore = Describe(f.Transmit);
        var snap = f.Noise.TakeSnapshot();
        f.Transmit.KeySource = () => true;
        f.Noise.StepTransmitPreset(+1, NoiseController.LayerSource);
        Assert.True(f.Transmit.HasDeferredChanges);
        Assert.False(gate.Enabled);
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));       // the layer's own waiting change was withdrawn
        Assert.False(f.Transmit.HasDeferredChanges);
        Assert.Equal(TxChangeTiming.Now, f.Noise.LastRestoreTiming);
        f.Transmit.KeySource = () => false;
        f.Transmit.ApplyDeferred();
        Assert.False(gate.Enabled);
        Assert.False(f.Transmit.WantsEnabled(DenoiseFamily.Neural));
        Assert.Equal(slotsBefore, Describe(f.Transmit));
    }

    [Fact]
    public void Escape_withdraws_only_the_layers_waiting_changes_and_an_earlier_outside_one_still_lands()
    {
        var (f, gate) = Make();
        using var _f = f;
        gate.Enabled = true; gate.MarginDb = 12;
        f.Noise.SavePreset("Gate", false, false, true, PresetCollision.Replace, "test");
        gate.Enabled = false; gate.MarginDb = 8;
        var snap = f.Noise.TakeSnapshot();
        f.Transmit.KeySource = () => true;
        f.Noise.SetGateOn(true, "Home");                                  // outside, held for the unkey
        f.Noise.StepTransmitPreset(+1, NoiseController.LayerSource);     // the layer's, held behind it
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));                 // the layer's own waiting change withdrawn
        Assert.True(f.Transmit.HasDeferredChanges);                       // Home's change still waits
        f.Transmit.KeySource = () => false;
        f.Transmit.ApplyDeferred();
        Assert.True(gate.Enabled);                                        // Home's on landed
        Assert.Equal(8, gate.MarginDb);                                   // the layer's margin 12 never did
    }

    [Fact]
    public void A_put_back_held_behind_an_outside_change_asks_again_and_keeps_that_change()
    {
        var (f, _) = Make();
        using var _f = f;
        var snap = f.Noise.TakeSnapshot();
        f.Noise.TransmitToggle("nnr", NoiseController.LayerSource);       // the layer's own: NNR on, now
        Assert.True(f.Transmit.WantsEnabled(DenoiseFamily.Neural));
        f.Transmit.KeySource = () => true;
        var (spectral, neural) = f.Transmit.CaptureSlots();
        neural.Enabled = true;
        neural.EngineId = "rnnoise";
        f.Transmit.ApplySlots(spectral, neural);                          // a setup from outside, held
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));               // still as the layer left it: queued
        Assert.Equal(TxChangeTiming.AtUnkey, f.Noise.LastRestoreTiming);
        f.Transmit.KeySource = () => false;
        f.Transmit.ApplyDeferred();
        Assert.True(f.Transmit.WantsEnabled(DenoiseFamily.Neural));       // the outside setup won
        Assert.Equal("rnnoise", f.Transmit.ChosenEngineId(DenoiseFamily.Neural));
    }

    [Fact]
    public void A_layer_change_held_for_the_unkey_that_lands_before_Escape_is_still_the_layers_to_put_back()
    {
        var (f, gate) = Make();
        using var _f = f;
        gate.Enabled = true; gate.MarginDb = 12;
        f.Noise.SavePreset("Gate", false, false, true, PresetCollision.Replace, "test");
        gate.Enabled = false; gate.MarginDb = 8;
        var snap = f.Noise.TakeSnapshot();
        f.Transmit.KeySource = () => true;
        f.Noise.StepTransmitPreset(+1, NoiseController.LayerSource);
        f.Transmit.KeySource = () => false;
        f.Transmit.ApplyDeferred();                                       // the unkey, with the layer still open
        Assert.True(gate.Enabled);
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.False(gate.Enabled);
        Assert.Equal(8, gate.MarginDb);
    }

    // ── Escape ownership round 2 ──

    [Fact]
    public void An_over_starting_between_the_layers_change_and_its_record_still_lets_Escape_put_the_change_back()
    {
        // Codex's deterministic reproduction through the real owner: the drain
        // reads unkeyed until the layer's held gate-on has run, and a new over
        // starts before the record after it. Escape then withdrew the record,
        // never knew of the change, and left the gate on.
        var (f, _) = Make();
        using var _f = f;
        using var ctx = new TxGateOwnerTests.Context();
        TxAudioConditioning.UseChainForTest(f.Transmit);
        f.Noise.TransmitGate = new AppTransmitGate();
        var snap = f.Noise.TakeSnapshot();
        Assert.False(TxAudioConditioning.GateEnabled);
        f.Transmit.KeySource = () => true;
        f.Noise.SetGateOn(true, NoiseController.LayerSource);           // held for the unkey
        Assert.True(f.Transmit.HasDeferredChanges);
        f.Transmit.KeySource = () => TxAudioConditioning.GateEnabled;   // keyed again the moment the change has run
        f.Transmit.ApplyDeferred();
        Assert.True(TxAudioConditioning.GateEnabled);
        Assert.True(f.Transmit.HasDeferredChanges);                     // the record, cut off by the new over
        Assert.Null(snap.LeftGate);
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.NotNull(snap.LeftGate);                                  // finalized as it was withdrawn
        Assert.Equal(TxChangeTiming.AtUnkey, f.Noise.LastRestoreTiming);
        f.Transmit.KeySource = () => false;
        f.Transmit.ApplyDeferred();
        Assert.False(TxAudioConditioning.GateEnabled);
        Assert.False(f.Transmit.HasDeferredChanges);
    }

    [Fact]
    public void Escape_that_withdraws_only_the_layers_bookkeeping_says_nothing_changed()
    {
        // Codex's reproduction: Home's gate-on held while keyed, then the layer asks
        // for off, which is what the gate reads. Escape reported "All changes
        // reverted" for withdrawing nothing but the layer's own records.
        var (f, gate) = Make();
        using var _f = f;
        var snap = f.Noise.TakeSnapshot();
        f.Transmit.KeySource = () => true;
        f.Noise.SetGateOn(true, "Home");
        f.Noise.SetGateOn(false, NoiseController.LayerSource);
        Assert.False(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.Equal(TxChangeTiming.Now, f.Noise.LastRestoreTiming);
        Assert.Equal(Lexicon.Get("audio.noise_layer.restored_nothing"),
            f.Noise.WithRestoreTiming(Lexicon.Get("audio.noise_layer.restored_nothing")));
        f.Transmit.KeySource = () => false;
        f.Transmit.ApplyDeferred();
        Assert.True(gate.Enabled);                                      // Home's change landed
        Assert.False(f.Transmit.HasDeferredChanges);
    }

    [Fact]
    public void Withdrawal_runs_an_owners_records_in_order_and_counts_only_its_changes()
    {
        var chain = new TxNoiseChain(TxNoiseChainTests.Catalog()) { KeySource = () => true };
        var owner = new object();
        var order = new List<string>();
        chain.RunOrDefer(() => order.Add("outside"));
        using (chain.OwnedBy(owner))
        {
            chain.AfterHeld(() => order.Add("record 1"));
            chain.RunOrDefer(() => order.Add("change"));
            chain.AfterHeld(() => order.Add("record 2"));
        }
        Assert.Equal(1, chain.CancelHeld(owner));
        Assert.Equal(new[] { "record 1", "record 2" }, order);
        chain.KeySource = () => false;
        chain.ApplyDeferred();
        Assert.Equal(new[] { "record 1", "record 2", "outside" }, order);
    }

    // ── Bench round (the 2026-11-18 harness walk and dummy-load bench) ──

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoPaths.RequireRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void Bench_1_with_no_radio_the_gates_and_engines_Advanced_sections_still_open()
    {
        // No radio means no audio config: the Advanced boxes ticked and revealed nothing.
        using var f = new NoiseFixture();
        var noise = new NoiseController(f.Catalog, () => null, () => null, () => null, () => { },
            new NoiseFixture.Speaker(), new NoiseFixture.Intents(), new NoiseFixture.Scheduler(), transmitChain: () => new TxNoiseChain(f.Catalog));
        Assert.False(noise.AdvancedShown("tx:gate"));
        noise.ShowAdvanced("tx:gate", true);
        Assert.True(noise.AdvancedShown("tx:gate"));
        noise.SetAdvanced("rnnoise", true, DenoiseDirection.Transmit);
        Assert.True(noise.IsAdvanced("rnnoise", DenoiseDirection.Transmit));
        noise.SetAdvanced("rnnoise", true);
        Assert.True(noise.IsAdvanced("rnnoise"));
        string panel = Source("JJFlexWpf", "Controls", "ScreenFieldsPanel.TransmitNoise.cs");
        Assert.DoesNotContain("CurrentAudioConfig", panel);
        Assert.Contains("StartTransmitNoiseNoRadioPoll();", panel);
        Assert.Contains("if (_rig != null || !IsVisible || !TxExpander.IsExpanded || _polling) return;", panel);
    }

    [Fact]
    public void Bench_2_a_setup_dialogs_answer_waits_for_the_window_to_settle_and_is_said_once_queued()
    {
        var (f, _) = Make();
        using var _f = f;
        var later = new List<Action>();
        f.Noise.AfterWindowSettles = later.Add;
        f.Spoken.Clear();
        using (var answer = f.Noise.HoldAnswer())
        {
            answer.AfterTheWindowCloses();
            Assert.True(f.Noise.SaveTransmitSetup("Contest", false, "test").Succeeded);
        }
        Assert.Empty(f.Spoken.Said);                                   // nothing as the window closes
        Assert.Single(later);
        later[0]();
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.setup_saved", ("setup", "Contest")), f.Spoken.Last);
        Assert.Contains(0, f.Spoken.SaidAfter);                        // queued behind the reader's own announcement
        // A hold left alone says it at once, as before.
        f.Spoken.Clear();
        using (f.Noise.HoldAnswer()) f.Noise.RenameTransmitSetup("Contest", "DX");
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.setup_renamed", ("old", "Contest"), ("new", "DX")), f.Spoken.Last);
        Assert.Single(later);
        string dialogs = Source("JJFlexWpf", "Dialogs", "TransmitSetupDialogs.cs");
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(dialogs, @"answer\.AfterTheWindowCloses\(\)").Count);
        string workshop = Source("JJFlexWpf", "Dialogs", "AudioWorkshopDialog.Toolbar.cs");
        Assert.Contains("WindowSettledSpeech.After(Dispatcher, () => ScreenReaderOutput.Speak(said", workshop);
        Assert.Contains("answer.AfterTheWindowCloses();", workshop);
        Assert.Contains("noise.AfterWindowSettles = say => WindowSettledSpeech.After(Dispatcher, say);", Source("JJFlexWpf", "MainWindow.xaml.cs"));
    }

    [Fact]
    public void Bench_3_the_setups_menu_rebuilds_when_a_setup_is_saved_renamed_or_deleted()
    {
        var (f, _) = Make();
        using var _f = f;
        int changed = 0;
        f.Noise.TransmitSetupsChanged += () => changed++;
        f.Noise.SaveTransmitSetup("A", false, "test");
        f.Noise.RenameTransmitSetup("A", "B");
        f.Noise.DeleteTransmitSetup("B");
        Assert.Equal(3, changed);
        f.Noise.DeleteTransmitSetup("missing");
        Assert.Equal(3, changed);                                      // nothing changed, nothing rebuilt
        Assert.Contains("noise.TransmitSetupsChanged += () => QueueMenuRebuild(", Source("JJFlexWpf", "NativeMenuBar.TransmitNoise.cs"));
    }

    [Fact]
    public void Bench_4_DeepFilterNet3_says_its_transmit_delay_before_it_has_run()
    {
        Assert.Equal(30, DenoiseEngineCatalog.ExpectedEngineDelayMs("deepfilternet3", null));
        Assert.Equal(1440, DeepFilterNet3Engine.LatencyAtFrame(480));
        var (f, _) = Make();
        using var _f = f;
        f.Spoken.Clear();
        f.Noise.TransmitToggle("deepfilternet3", "test");
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.delay_clause", VerbosityLevel.Chatty, ("ms", 30)), f.Spoken.Last);
        Assert.Equal(Lexicon.Get("audio.noise_surface.delay", ("ms", 30)), f.Noise.DelayText("deepfilternet3", DenoiseDirection.Transmit));
        Assert.Contains("string delay = noise.DelayText(_txNoiseFieldsFor, DenoiseDirection.Transmit);", Source("JJFlexWpf", "Controls", "ScreenFieldsPanel.TransmitNoise.cs"));
    }

    [Fact]
    public void Bench_7_switching_off_while_keyed_reads_as_on_until_the_unkey()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("rnnoise", "test");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (f.Transmit.NeuralSlot.RunningEngineId != "rnnoise" || !f.Transmit.NeuralSlot.Enabled)
        {
            Assert.True(DateTime.UtcNow < deadline, "the engine never ran");
            var b = new float[960];
            f.Transmit.Process(b, b.Length, 48000, false);
            System.Threading.Thread.Sleep(2);
        }
        f.Transmit.KeySource = () => true;
        f.Noise.TransmitToggle("rnnoise", "test");                      // off, mid-over
        Assert.True(f.Transmit.NeuralSlot.Enabled);                     // still running to the unkey
        string summary = f.Noise.TransmitActiveSummary();
        Assert.NotEqual(Lexicon.Get("audio.noise_surface.tx.active.none"), summary);
        Assert.Contains("RNNoise", summary);
        Assert.EndsWith(Lexicon.Get("audio.noise_surface.tx.active.held"), summary);
        f.Transmit.KeySource = () => false;
        var u = new float[960];
        f.Transmit.Process(u, u.Length, 48000, false);
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.active.none"), f.Noise.TransmitActiveSummary());
    }

    [Fact]
    public void Bench_8_the_slice_menu_holds_the_noise_answer_and_joins_it_to_the_slice_sentence()
    {
        string menu = Source("JJFlexWpf", "NativeMenuBar.cs");
        int row = menu.IndexOf("Selection with active slice checkmark", StringComparison.Ordinal);
        Assert.True(row > 0);
        string body = menu.Substring(row, 2500);
        Assert.Contains("var noiseAnswer = _window.Noise.HoldModeAnswer();", body);
        Assert.Contains("MenuModeSentence.SayWhenTheMenuHasClosed(", body);
        Assert.True(body.IndexOf("HoldModeAnswer", StringComparison.Ordinal) < body.IndexOf("rig.RXVFO = sliceNum", StringComparison.Ordinal),
            "the hold must be taken before the slice changes, or the poll can answer first");
    }

    [Fact]
    public void Bench_9_a_refused_engine_is_never_the_cursor_and_the_brackets_skip_it()
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        keys.Handle(Keys.G | Keys.Control);
        Assert.Equal(Lexicon.Get("audio.noise_surface.not_in_version", ("engine", Tx("NVIDIA NR"))), f.Spoken.Last);
        keys.Handle(Keys.Up);                                           // the arrows have no engine: it was never the cursor
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.no_engine_yet", VerbosityLevel.Chatty), f.Spoken.Last);
        f.Noise.Toggle("nvidia_afx", "test");                           // receive's twin
        Assert.NotEqual("nvidia_afx", f.Noise.Current);
        Assert.DoesNotContain(f.Noise.TransmitStepList(), x => x.EngineId == "nvidia_afx");
        f.Noise.SetFavourite("nvidia_afx", true, "test");
        Assert.DoesNotContain(f.Noise.TransmitStepList(), x => x.EngineId == "nvidia_afx");
    }

    [Fact]
    public void Bench_9_setups_with_no_radio_refuse_before_the_name_and_the_questions_close_on_Escape()
    {
        var (f, _) = Make();
        using var _f = f;
        Assert.True(f.Noise.TransmitSetupsReadyOrSay());
        f.RadioPresent = false;
        f.Spoken.Clear();
        Assert.False(f.Noise.TransmitSetupsReadyOrSay());
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.setups_need_radio"), f.Spoken.Last);
        string dialogs = Source("JJFlexWpf", "Dialogs", "TransmitSetupDialogs.cs");
        Assert.Contains("if (!controller.TransmitSetupsReadyOrSay()) return;", dialogs);
        Assert.DoesNotContain("MessageBox", dialogs);
        Assert.Contains("new ConfirmActionDialog(", dialogs);
        Assert.Contains("if (n.TransmitSetupsReadyOrSay()) _window.ShowTransmitSetupSave();", Source("JJFlexWpf", "NativeMenuBar.TransmitNoise.cs"));
    }

    [Fact]
    public void Bench_9_a_preset_writes_a_radio_level_only_when_it_differs()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Radio.Levels["radio_wnb"] = 90;
        f.Noise.SavePreset("Same", true, false, PresetCollision.Replace, "test");
        f.Radio.Writes.Clear();
        f.Noise.ApplyPreset("Same", "test");
        Assert.DoesNotContain(f.Radio.Writes, w => w.StartsWith("radio_wnb.level", StringComparison.Ordinal));
        f.Radio.Levels["radio_wnb"] = 40;                               // positive control: a difference is written
        f.Noise.ApplyPreset("Same", "test");
        Assert.Contains("radio_wnb.level=90", f.Radio.Writes);
    }

    // ── Bench round 2 ──

    [Fact]
    public void Bench2_a_settled_choice_of_a_refused_engine_never_becomes_the_cursor_either_direction()
    {
        var (f, _) = Make();
        using var _f = f;
        var keys = LayerKeys(f);
        EnterTransmit(f, keys);
        f.Noise.TransmitChooseSlotEngine(DenoiseFamily.Neural, "nvidia_afx", "home");
        f.Clock.RunAll();
        keys.Handle(Keys.Up);
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.no_engine_yet", VerbosityLevel.Chatty), f.Spoken.Last);
        Assert.Equal("rnnoise", f.Transmit.ChosenEngineId(DenoiseFamily.Neural));
        f.Noise.ChooseSlotEngine(DenoiseFamily.Neural, "nvidia_afx", "home");   // receive's twin
        f.Clock.RunAll();
        Assert.NotEqual("nvidia_afx", f.Noise.Current);
        // An engine only unavailable now is still a valid intent, and may be the cursor.
        f.Noise.TransmitChooseSlotEngine(DenoiseFamily.Neural, "nnr", "home");
        f.Clock.RunAll();
        keys.Handle(Keys.Up);
        Assert.NotEqual(Lexicon.Get("audio.noise_surface.tx.no_engine_yet", VerbosityLevel.Chatty), f.Spoken.Last);
    }

    [Fact]
    public void Bench2_an_identical_preset_queues_nothing_so_Escape_reverts_nothing()
    {
        var (f, gate) = Make();
        using var _f = f;
        f.Noise.SavePreset("Same", false, false, true, PresetCollision.Replace, "test");   // the picture as it is
        var snap = f.Noise.TakeSnapshot();
        f.Transmit.KeySource = () => true;
        f.Noise.StepTransmitPreset(+1, NoiseController.LayerSource);
        Assert.False(f.Transmit.HasDeferredChanges);
        Assert.False(f.Noise.RestoreSnapshot(snap, "test"));
    }

    [Fact]
    public void Bench2_an_identical_preset_still_overrides_a_different_intent_already_waiting()
    {
        var (f, gate) = Make();
        using var _f = f;
        f.Noise.SavePreset("Off", false, false, true, PresetCollision.Replace, "test");    // gate off
        f.Transmit.KeySource = () => true;
        f.Noise.SetGateOn(true, "Home");                                // waiting for the unkey
        f.Noise.StepTransmitPreset(+1, "transmit menu");               // the same as now, asked after it
        f.Transmit.KeySource = () => false;
        f.Transmit.ApplyDeferred();
        Assert.False(gate.Enabled);                                     // the later request still wins
    }

    [Fact]
    public void Bench2_the_gate_fields_are_never_rebuilt_out_from_under_a_focused_gate_field()
    {
        string panel = Source("JJFlexWpf", "Controls", "ScreenFieldsPanel.TransmitNoise.cs");
        int side = panel.IndexOf("private void ShowTransmitNoiseSide(", StringComparison.Ordinal);
        string body = panel.Substring(side, panel.IndexOf("private void ShowTransmitNoiseEngine(", StringComparison.Ordinal) - side);
        Assert.Contains("if (_txGateAdvancedFields.IsKeyboardFocusWithin) _txGateRebuildOwed = true;", body);
        Assert.DoesNotContain("\n        BuildTransmitGateFields();\n", body.Replace("\r\n", "\n"));
        Assert.Contains("|| setupsChanged || _txGateRebuildOwed))", panel);
    }

    [Fact]
    public void Bench2_the_two_placeholders_carry_Codexs_words()
    {
        Assert.Equal("NR2 and NNR for transmit bypassed until unkey.",
            Lexicon.Get("audio.denoise.tx.fell_back_both", ("first", "NR2"), ("second", "NNR")));
        Assert.Equal("Continue?", Lexicon.Get("audio.noise_setups.confirm_question"));
        Assert.Contains("WindowSettledSpeech.After(Dispatcher, () =>", Source("JJFlexWpf", "Dialogs", "RecordedQsosDialog.cs"));
    }

    // ── Slice-mode round: transmit noise reduction follows the TRANSMIT slice's mode ──

    [Fact]
    public void A_CW_active_slice_never_bypasses_cleanup_on_a_USB_transmit_slice()
    {
        // Harness walk 2: a second slice in CW bypassed cleanup on the USB
        // transmit slice, and the summary said "not running yet".
        var (f, _) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("rnnoise", "test");
        f.Radio.Mode = "CW";                                            // the active slice
        f.Radio.TransmitModeOverride = "USB";                           // the slice that transmits
        string summary = f.Noise.TransmitActiveSummary();
        Assert.DoesNotContain(Lexicon.Get("audio.noise_surface.tx.active.bypassed"), summary);
    }

    [Fact]
    public void A_CW_transmit_slice_bypasses_cleanup_whatever_the_active_slice_and_the_summary_says_so()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("rnnoise", "test");
        f.Radio.Mode = "USB";                                           // the active slice
        f.Radio.TransmitModeOverride = "CW";                            // the slice that transmits
        string summary = f.Noise.TransmitActiveSummary();
        Assert.EndsWith(Lexicon.Get("audio.noise_surface.tx.active.bypassed"), summary);
        Assert.DoesNotContain(Lexicon.Get("audio.noise_surface.active.waiting", ("engine", "RNNoise")), summary);   // named as chosen, not "not running yet"
        Assert.Contains("RNNoise", summary);
    }

    [Fact]
    public void Moving_the_transmit_slice_to_a_CW_slice_and_back_follows_it()
    {
        var (f, _) = Make();
        using var _f = f;
        f.Noise.TransmitToggle("rnnoise", "test");
        f.Radio.Mode = "USB";
        f.Radio.TransmitModeOverride = "USB";
        Assert.DoesNotContain(Lexicon.Get("audio.noise_surface.tx.active.bypassed"), f.Noise.TransmitActiveSummary());
        f.Radio.TransmitModeOverride = "CW";                            // the transmit designation moves to a CW slice
        Assert.EndsWith(Lexicon.Get("audio.noise_surface.tx.active.bypassed"), f.Noise.TransmitActiveSummary());
        f.Radio.TransmitModeOverride = "USB";                           // and back
        Assert.DoesNotContain(Lexicon.Get("audio.noise_surface.tx.active.bypassed"), f.Noise.TransmitActiveSummary());
    }

    [Fact]
    public void The_chains_mode_policy_reads_the_transmit_slice_on_every_event_that_can_change_it()
    {
        Assert.True(TxAudioConditioning.BypassedIn("CW"));
        Assert.True(TxAudioConditioning.BypassedIn("DIGU"));
        Assert.False(TxAudioConditioning.BypassedIn("USB"));
        string owner = Source("JJFlexWpf", "TxAudioConditioning.cs");
        Assert.Contains("rig.TransmitModeMayHaveChanged += OnTransmitModeMayHaveChanged;", owner);
        Assert.Contains("rig.TransmitModeMayHaveChanged -= OnTransmitModeMayHaveChanged;", owner);
        Assert.Contains("private static void OnModeChanged(string newMode) => OnTransmitModeMayHaveChanged();", owner);   // never the active slice's mode
        Assert.Contains("ApplyModePolicy(TransmitModeOf(rig));", owner);
        Assert.Contains("string designated = rig.DesignatedTransmitMode;", owner);
        // FlexBase raises it on any slice's mode, a transmit designation, the
        // active slice changing, and a slice going.
        string flex = Source("Radios", "FlexBase.cs").Replace("\r\n", "\n");
        foreach (string marker in new[] { "case \"Active\":", "case \"DemodMode\":", "case \"IsTransmitSlice\":", "private void sliceRemoved(" })
        {
            int at = flex.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at > 0, marker);
            int end = flex.IndexOf(marker.StartsWith("case") ? "break;" : "\n        }\n", at, StringComparison.Ordinal);
            Assert.Contains("TransmitModeMayHaveChanged?.Invoke();", flex.Substring(at, end - at));
        }
        // Any slice's mode, not only the active one's: outside the active-only branch.
        int demod = flex.IndexOf("case \"DemodMode\":", StringComparison.Ordinal);
        string demodBody = flex.Substring(demod, flex.IndexOf("if (s.Active)\n                            {\n                                FilterObj.RXFreqChange(s);", demod, StringComparison.Ordinal) - demod);
        Assert.Contains("TransmitModeMayHaveChanged?.Invoke();", demodBody);
    }

    [Fact]
    public void Both_release_rows_join_the_noise_answer_to_the_release_sentence()
    {
        string menu = Source("JJFlexWpf", "NativeMenuBar.cs");
        foreach (string row in new[] { "AddWired(selSub, \"Release Active Slice\"", "AddWired(parent, \"Release Slice\"" })
        {
            int at = menu.IndexOf(row, StringComparison.Ordinal);
            Assert.True(at > 0, row);
            string body = menu.Substring(at, 2200);
            int hold = body.IndexOf("_window.Noise.HoldModeAnswer();", StringComparison.Ordinal);
            Assert.True(hold > 0, row + ": no hold");
            Assert.True(hold < body.IndexOf("RemoveSlice(", StringComparison.Ordinal), row + ": the hold must be taken before the slice goes");
            Assert.Contains("SaySliceAfterMenuClose(", body);
        }
    }

    // ── Slice-mode round 2: the real FlexBase and the real policy, windowless ──
    //
    // Codex's seam (its slice-mode check): a normally constructed FlexBase, a
    // Radio(false), vendor slices set by field, and a command channel whose
    // Connect throws, so no socket is ever opened. The real private slice
    // handlers run, and the real TxAudioConditioning policy writes the real
    // conditioner's BypassAll.

    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Stat = BindingFlags.Static | BindingFlags.NonPublic;
    private static void SetField(object x, string n, object? v) => x.GetType().GetField(n, Inst)!.SetValue(x, v);
    private static object GetField(object x, string n) => x.GetType().GetField(n, Inst)!.GetValue(x)!;
    private static void RunPolicy() => typeof(TxAudioConditioning).GetMethod("OnTransmitModeMayHaveChanged", Stat)!.Invoke(null, null);

    private sealed class NoSocket : Flex.Smoothlake.FlexLib.CommandCommunicationBase
    {
        public NoSocket() => PublishConnected(BeginConnection());
        public override System.Net.IPAddress? LocalIp { get; set; }
        public override bool Connect(System.Net.IPAddress ip, int port, int source, out Flex.Smoothlake.FlexLib.CommandConnection? c) =>
            throw new InvalidOperationException("No socket in this test");
        public override void Disconnect() { }
        public override void Write(string value) { }
    }

    private sealed class SliceRig : IDisposable
    {
        // Slice-mode round 3 (Codex): FlexBase's constructor writes three process
        // globals, so they are captured BEFORE it runs and put back in Dispose, or
        // here if construction fails part way, with Context's statics.
        private readonly FlexBaseGlobals _globals;
        private readonly TxGateOwnerTests.Context _saved;
        public readonly FlexBase Rig;
        public readonly Flex.Smoothlake.FlexLib.Radio Radio;
        public readonly Radios.StationConnect.ObservationBinding Binding;
        public int Events;

        public SliceRig()
        {
            Assert.True(QuietRun.Silenced);
            Assert.True(TestSettingsRoot.Isolated);
            _globals = FlexBaseGlobals.Capture();
            _saved = new TxGateOwnerTests.Context();
            try
            {
                Rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "SliceModeTests" });
                Radio = (Flex.Smoothlake.FlexLib.Radio)Activator.CreateInstance(typeof(Flex.Smoothlake.FlexLib.Radio), Inst, null, new object[] { false }, null)!;
            }
            catch
            {
                try { _saved.Dispose(); } finally { _globals.Dispose(); }
                throw;
            }
            SetField(Radio, "_commandCommunication", new NoSocket());
            SetField(Rig, "theRadio", Radio);
            SetField(Rig, "clientHandle", 0u);
            Rig.SuppressSpeech = true;
            typeof(FlexBase).GetProperty("CanTransmit")!.SetValue(Rig, true);
            Binding = new Radios.StationConnect.ObservationBinding(Radio, new Radios.StationConnect.ConnectionAttempt("slice-mode tests"));
            typeof(TxAudioConditioning).GetField("_rig", Stat)!.SetValue(null, Rig);
            Rig.TransmitModeMayHaveChanged += RunPolicy;
            Rig.TransmitModeMayHaveChanged += Count;
        }

        private void Count() => Events++;

        public Flex.Smoothlake.FlexLib.Slice Slice(int index, string mode, bool tx, bool active, bool ours = true)
        {
            var s = (Flex.Smoothlake.FlexLib.Slice)Activator.CreateInstance(typeof(Flex.Smoothlake.FlexLib.Slice), Inst, null, new object[] { Radio }, null)!;
            SetField(s, "_index", index); SetField(s, "_demodMode", mode); SetField(s, "_isTransmitSlice", tx); SetField(s, "_active", active);
            SetField(s, "_clientHandle", 0u);
            Radio.SliceList.Add(s);
            if (ours) ((List<Flex.Smoothlake.FlexLib.Slice>)GetField(Rig, "mySlices")).Add(s);
            return s;
        }

        public void Positions(int tx, int rx, string rxMode) { SetField(Rig, "_TXVFO", tx); SetField(Rig, "_RXVFO", rx); SetField(Rig, "_RXMode", rxMode); }
        public void Notify(Flex.Smoothlake.FlexLib.Slice s, string property) =>
            typeof(FlexBase).GetMethod("slicePropertyChangedHandler", Inst)!.Invoke(Rig, new object[] { s, new System.ComponentModel.PropertyChangedEventArgs(property) });
        public void Arrive(Flex.Smoothlake.FlexLib.Slice s) => typeof(FlexBase).GetMethod("sliceAdded", Inst)!.Invoke(Rig, new object[] { s, Binding });
        public bool Bypassed => Rig.TxConditioner.BypassAll;

        public void Dispose()
        {
            try
            {
                Rig.TransmitModeMayHaveChanged -= RunPolicy;
                Rig.TransmitModeMayHaveChanged -= Count;
                SetField(Rig, "theRadio", null);
                Rig.Dispose();
            }
            finally
            {
                try { _saved.Dispose(); } finally { _globals.Dispose(); }
            }
        }
    }

    [Fact]
    public void The_windowless_rig_puts_back_the_globals_a_FlexBase_constructor_writes()
    {
        // Codex's sentinel (slice-mode round 3): after the rig, API.ProgramName,
        // API.IsGUI and VitaSocket.TraceSink are what they were before it.
        var name = Flex.Smoothlake.FlexLib.API.ProgramName;
        var gui = Flex.Smoothlake.FlexLib.API.IsGUI;
        var sink = Vita.VitaSocket.TraceSink;
        try
        {
            Flex.Smoothlake.FlexLib.API.ProgramName = "isolation-sentinel";
            Flex.Smoothlake.FlexLib.API.IsGUI = false;
            Action<string> sentinel = _ => { };
            Vita.VitaSocket.TraceSink = sentinel;
            using (var r = new SliceRig())
            {
                Assert.True(Flex.Smoothlake.FlexLib.API.IsGUI);          // positive control: the constructor did change them
                Assert.NotSame(sentinel, Vita.VitaSocket.TraceSink);
            }
            Assert.Equal("isolation-sentinel", Flex.Smoothlake.FlexLib.API.ProgramName);
            Assert.False(Flex.Smoothlake.FlexLib.API.IsGUI);
            Assert.Same(sentinel, Vita.VitaSocket.TraceSink);
        }
        finally
        {
            Flex.Smoothlake.FlexLib.API.ProgramName = name;
            Flex.Smoothlake.FlexLib.API.IsGUI = gui;
            Vita.VitaSocket.TraceSink = sink;
        }
    }

    [Fact]
    public void The_real_policy_follows_the_designated_slice_through_mode_changes_and_a_designation_move()
    {
        // Codex's positive control, kept as a behaviour test.
        using var r = new SliceRig();
        var a = r.Slice(0, "USB", tx: true, active: false);
        var b = r.Slice(1, "CW", tx: false, active: true);
        r.Positions(0, 1, "CW");
        RunPolicy();
        Assert.False(r.Bypassed);                                       // a CW active slice never bypasses a USB transmit slice
        SetField(a, "_demodMode", "CW"); r.Notify(a, "DemodMode");
        Assert.True(r.Bypassed);
        SetField(a, "_demodMode", "USB"); r.Notify(a, "DemodMode");
        Assert.False(r.Bypassed);
        SetField(a, "_isTransmitSlice", false); SetField(b, "_isTransmitSlice", true); r.Notify(b, "IsTransmitSlice");
        Assert.True(r.Bypassed);                                        // the designation moved to the CW slice
        Assert.Equal("CW", TxAudioConditioning.TransmitModeOf(r.Rig));
    }

    [Fact]
    public void When_the_radio_removes_the_designation_the_active_slices_receive_mode_decides()
    {
        // Codex: TXModeAsReported followed the old position, so the old CW stayed.
        using var r = new SliceRig();
        var a = r.Slice(0, "CW", tx: true, active: false);
        r.Slice(1, "USB", tx: false, active: true);
        r.Positions(0, 1, "USB");
        RunPolicy();
        Assert.True(r.Bypassed);
        SetField(a, "_isTransmitSlice", false); r.Notify(a, "IsTransmitSlice");
        Assert.Equal("", r.Rig.DesignatedTransmitMode);
        Assert.Equal("USB", TxAudioConditioning.TransmitModeOf(r.Rig));
        Assert.False(r.Bypassed);
    }

    [Fact]
    public void With_no_designation_switching_the_active_slice_switches_the_fallback()
    {
        using var r = new SliceRig();
        var a = r.Slice(0, "USB", tx: false, active: true);
        var b = r.Slice(1, "CW", tx: false, active: false);
        r.Positions(-1, 0, "USB");
        RunPolicy();
        Assert.False(r.Bypassed);
        SetField(a, "_active", false); SetField(b, "_active", true);    // the radio moves the active slice
        r.Notify(b, "Active");
        Assert.Equal("CW", r.Rig.ActiveSliceModeAsReported);            // read live, not the cached receive mode
        Assert.True(r.Bypassed);
        // And the radio's own ActiveSlice notice raises it again once that is
        // true (FlexLib reports the slice's Active before the radio's ActiveSlice).
        string flex = Source("Radios", "FlexBase.cs").Replace("\r\n", "\n");
        int at = flex.IndexOf("case \"ActiveSlice\":", StringComparison.Ordinal);
        Assert.Contains("TransmitModeMayHaveChanged?.Invoke();", flex.Substring(at, flex.IndexOf("break;", at, StringComparison.Ordinal) - at));
    }

    [Fact]
    public void A_slice_that_arrives_already_designated_refreshes_the_policy()
    {
        // Codex: a ready slice's arrival raised nothing, so the chain stayed un-bypassed on a CW transmit slice.
        using var r = new SliceRig();
        r.Slice(0, "USB", tx: false, active: true);
        r.Positions(-1, 0, "USB");
        RunPolicy();
        Assert.False(r.Bypassed);
        var arriving = r.Slice(1, "CW", tx: true, active: false, ours: false);
        r.Events = 0;
        r.Arrive(arriving);
        Assert.True(r.Events >= 1);
        Assert.Equal("CW", TxAudioConditioning.TransmitModeOf(r.Rig));
        Assert.True(r.Bypassed);
    }

    [Theory]
    [InlineData("DFM", true)]
    [InlineData("FDV", true)]
    [InlineData("FDM", true)]
    [InlineData("DIGU", true)]
    [InlineData("CWL", true)]
    [InlineData("RTTY", true)]
    [InlineData("FM", false)]
    [InlineData("NFM", false)]
    [InlineData("USB", false)]
    [InlineData("AM", false)]
    public void The_radios_own_digital_modes_bypass_the_chain_and_voice_modes_do_not(string mode, bool bypassed)
    {
        using var r = new SliceRig();
        r.Slice(0, mode, tx: true, active: true);
        r.Positions(0, 0, mode);
        RunPolicy();
        Assert.Equal(bypassed, r.Bypassed);
        Assert.Equal(bypassed, TxAudioConditioning.BypassedIn(mode));
    }

    private static string Describe(TxNoiseChain chain)
    {
        var (s, n) = chain.CaptureSlots();
        string One(TxNoiseSlotSetup x) => (x.Enabled ? "on " : "off ") + x.EngineId + " " + string.Join(",", x.Engines.Select(e => e.EngineId + ":" + e.Strength));
        return One(s) + " | " + One(n);
    }

    [Fact]
    public void The_setup_rename_prompt_carries_its_own_empty_name_help()
    {
        string dialogs = File.ReadAllText(Path.Combine(RepoPaths.RequireRoot(), "JJFlexWpf", "Dialogs", "TransmitSetupDialogs.cs"));
        const string key = "audio.noise_help.setups.rename";
        Assert.Contains(key, dialogs);
        Assert.DoesNotContain("PLACEHOLDER", Lexicon.Get(key));
    }

    [Fact]
    public void No_H_or_H2_transmit_key_still_carries_a_placeholder_marker()
    {
        foreach (string key in new[] { "audio.denoise.tx.at_unkey", "audio.noise_surface.tx.engine", "audio.noise_surface.tx.layer_transmit",
            "audio.noise_surface.tx.gate_defaults", "audio.noise_home.tx.on", "audio.noise_help.tx_home.gate.margin", "audio.noise_setups.copy",
            "audio.cleanup.door", "audio.noise_surface.lever.floor", "audio.noise_surface.defaults_floor" })
            foreach (var level in new[] { VerbosityLevel.Critical, VerbosityLevel.Terse, VerbosityLevel.Chatty })
                Assert.DoesNotContain("PLACEHOLDER", Lexicon.Get(key, level));
    }
}
