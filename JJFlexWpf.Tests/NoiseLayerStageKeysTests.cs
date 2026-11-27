using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 noise keys: the noise layer's keys from the rulings of 2026-11-26.
/// Ctrl+E the radio's own graphic equaliser and Ctrl+Shift+E our parametric one
/// (#490, 13:07); Ctrl+K our compressor, Ctrl+L our limiter and Ctrl+P the
/// speech processor handoff setting, transmit (#1278, question 13); Shift+Alt+L
/// Learn the room (#1252, question 12); and the layer's Escape putting back
/// what each of them changed (the brief's item 4).
/// Windowless: MTA asserted in the constructor; the key handler and the
/// controller over fakes, and the transmit owner on a radio object that is only
/// its injected fields. No window, device or radio. In the transmit
/// conditioning statics collection because the compressor, the limiter and the
/// learn live in <see cref="TxAudioConditioning"/>'s statics.
/// </summary>
[Collection("Transmit conditioning statics")]
public sealed class NoiseLayerStageKeysTests
{
    private const string Layer = NoiseController.LayerSource;
    private const string Home = "home";

    public NoiseLayerStageKeysTests() => NoiseReductionPerChannelTests.AssertWindowless();

    // ── helpers ──

    private static FlexBase.EqSettings Flat(bool on = false) => new FlexBase.TxEqSettings { Enabled = on };

    private static int Band(NoiseFixture f, DenoiseDirection side, int band) => FlexBase.GetEqBand(f.Radio.RadioEq[side], band);

    /// <summary>A fixture whose radio has reported both equalisers, flat and off.</summary>
    private static NoiseFixture WithRadioEq(Func<long>? clock = null)
    {
        var f = new NoiseFixture(clock: clock);
        f.Radio.RadioEq[DenoiseDirection.Receive] = Flat();
        f.Radio.RadioEq[DenoiseDirection.Transmit] = Flat();
        return f;
    }

    private static NoiseLayerKeys Keys_(NoiseFixture f, bool transmit, List<bool>? tones = null)
    {
        var keys = new NoiseLayerKeys(f.Noise, tones == null ? null : on => tones.Add(on));
        if (transmit) Assert.True(keys.Handle(NoiseLayerKeys.DirectionKey));
        Assert.Equal(transmit ? DenoiseDirection.Transmit : DenoiseDirection.Receive, f.Noise.LayerDirection);
        f.Spoken.Clear();
        return keys;
    }

    /// <summary>The noise controller over fakes, with the app's transmit owner on a radio object that is only its injected fields (TxDynamicsSurfaceTests' bridge).</summary>
    private sealed class Bridge : IDisposable
    {
        public readonly TxGateOwnerTests.Context X = new();
        public readonly NoiseFixture F = new();
        private readonly bool _handoffWas = AudioOutputConfig.RadioProcessorOffWithCompressorInForce;
        public Bridge()
        {
            typeof(NoiseFixture).GetField("_transmit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(F, X.Chain);
            F.Noise.TransmitGate = new AppTransmitGate();
            TxAudioConditioning.ApplySetup(new TxNoiseSetup());
            X.Buffer();
        }
        public NoiseController Noise => F.Noise;
        public void Unkey() { X.Keyed = false; X.Chain.ApplyDeferred(); X.Buffer(); }
        public void Dispose()
        {
            F.Dispose(); X.Dispose();
            AudioOutputConfig.RadioProcessorOffWithCompressorInForce = _handoffWas;
        }
    }

    // ── the chords, against the inventory ──

    [Fact]
    public void The_new_keys_are_the_ruled_chords_each_with_one_inventory_row_and_none_on_an_engine_chord()
    {
        var ruled = new (Keys Key, Keys Chord)[]
        {
            (NoiseLayerKeys.RadioEqualizerKey, Keys.E | Keys.Control),
            (NoiseLayerKeys.EqualizerKey, Keys.E | Keys.Control | Keys.Shift),
            (NoiseLayerKeys.CompressorKey, Keys.K | Keys.Control),
            (NoiseLayerKeys.LimiterKey, Keys.L | Keys.Control),
            (NoiseLayerKeys.ProcessorHandoffKey, Keys.P | Keys.Control),
            (NoiseLayerKeys.LearnRoomKey, Keys.L | Keys.Shift | Keys.Alt),
        };
        var rows = KeyInventory.LayerCommands(KeyInventory.NoiseLayerContext);
        foreach (var (key, chord) in ruled)
        {
            Assert.Equal(chord, key);
            Assert.False(NoiseLayerKeys.EngineKeys.ContainsKey(chord), KeyManifest.FormatKey(chord) + " is an engine's chord");
            Assert.Single(rows, r => LeaderChordParser.TryParseChord(r.KeyDisplay, out var k) && k == chord);
        }
        Assert.Equal(ruled.Length, ruled.Select(r => r.Chord).Distinct().Count());
        Assert.Contains(NoiseLayerKeys.LearnRoomKey, NoiseLayerKeys.ShiftAltChords);
        // The parametric's row moved with its words; the new rows are drafts for Codex.
        Assert.Contains("parametric", rows.Single(r => r.KeyDisplay == "Ctrl+Shift+E").Description);
        foreach (string display in new[] { "Ctrl+E", "Ctrl+K", "Ctrl+L", "Ctrl+P", "Shift+Alt+L" })
            Assert.StartsWith("WORDING PLACEHOLDER: DRAFT PROSE:", rows.Single(r => r.KeyDisplay == display).Description);
    }

    // ── Ctrl+E: the radio's own equaliser ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ctrl_E_switches_the_radios_own_equaliser_in_the_layers_direction_and_never_the_parametric(bool transmit)
    {
        using var f = WithRadioEq();
        var tones = new List<bool>();
        var keys = Keys_(f, transmit, tones);
        var side = transmit ? DenoiseDirection.Transmit : DenoiseDirection.Receive;
        var other = transmit ? DenoiseDirection.Receive : DenoiseDirection.Transmit;
        bool parametricWas = f.Noise.EqualizerOn(side);

        Assert.True(keys.Handle(NoiseLayerKeys.RadioEqualizerKey));
        Assert.True(f.Radio.RadioEq[side].Enabled);
        Assert.False(f.Radio.RadioEq[other].Enabled);
        Assert.Equal(parametricWas, f.Noise.EqualizerOn(side));
        Assert.Equal(new[] { true }, tones);
        Assert.Equal(transmit ? SpeechSubject.TransmitRadioEqualizer : SpeechSubject.ReceiveRadioEqualizer, f.Spoken.Said[^1].Subject);
        Assert.Contains("radio's own", f.Spoken.Last);

        Assert.True(keys.Handle(NoiseLayerKeys.RadioEqualizerKey));
        Assert.False(f.Radio.RadioEq[side].Enabled);
        Assert.Equal(new[] { true, false }, tones);
    }

    [Fact]
    public void After_Ctrl_E_the_arrows_work_the_radios_nine_fixed_bands()
    {
        using var f = WithRadioEq();
        var keys = Keys_(f, transmit: false);
        var rx = DenoiseDirection.Receive;
        keys.Handle(NoiseLayerKeys.RadioEqualizerKey);

        Assert.True(keys.Handle(Keys.Right));                 // 63 Hz
        Assert.StartsWith("63 Hz", f.Spoken.Last);
        Assert.True(keys.Handle(Keys.Up));
        Assert.True(keys.Handle(Keys.Up));
        Assert.Equal(2, Band(f, rx, 1));
        Assert.Equal(0, Band(f, rx, 0));
        Assert.True(keys.Handle(Keys.Tab));                   // 125 Hz: Tab walks the bands too
        Assert.True(keys.Handle(Keys.Down));
        Assert.Equal(-1, Band(f, rx, 2));
        Assert.True(keys.Handle(Keys.Left));
        Assert.True(keys.Handle(Keys.Tab | Keys.Shift));      // 32 Hz
        Assert.True(keys.Handle(Keys.Left));                  // stays: the first band
        Assert.Contains("first band", f.Spoken.Last);
        for (int i = 0; i < 12; i++) keys.Handle(Keys.Down);
        Assert.Equal(FlexBase.EqLevelMin, Band(f, rx, 0));
        Assert.Contains("the least", f.Spoken.Last);
        Assert.True(keys.Handle(Keys.Space));
        Assert.StartsWith("32 Hz", f.Spoken.Last);

        // Fixed bands: the add and remove keys change nothing anywhere and say so.
        int writes = f.Radio.EqWrites.Count;
        var parametric = f.Noise.EqualizerSetup(rx).Bands.Count;
        Assert.True(keys.Handle(NoiseLayerKeys.AddBandKey));
        Assert.Contains("fixed bands", f.Spoken.Last);
        Assert.True(keys.Handle(NoiseLayerKeys.RemoveBandKey));
        Assert.Contains("fixed bands", f.Spoken.Last);
        Assert.Equal(writes, f.Radio.EqWrites.Count);
        Assert.Equal(parametric, f.Noise.EqualizerSetup(rx).Bands.Count);
        // The transmit side was never touched.
        Assert.DoesNotContain(f.Radio.EqWrites, w => w.StartsWith("Transmit", StringComparison.Ordinal));
    }

    [Fact]
    public void Ctrl_Shift_E_is_the_parametric_with_its_bands_on_Left_Right_Tab_and_Delete_and_the_radios_is_untouched()
    {
        using var f = WithRadioEq();
        var keys = Keys_(f, transmit: false);
        var rx = DenoiseDirection.Receive;
        Assert.True(keys.Handle(NoiseLayerKeys.EqualizerKey));
        Assert.True(f.Noise.EqualizerOn(rx));
        f.Noise.LoadEqualizerPreset(rx, "presence", Home);
        int bands = f.Noise.EqualizerSetup(rx).Bands.Count;
        Assert.True(bands > 0, "the preset gave the parametric no bands: the test proves nothing");
        Assert.True(keys.Handle(Keys.Right));
        Assert.True(keys.Handle(Keys.Tab));
        Assert.True(keys.Handle(NoiseLayerKeys.RemoveBandKey));
        Assert.Equal(bands - 1, f.Noise.EqualizerSetup(rx).Bands.Count);
        Assert.Empty(f.Radio.EqWrites);
        Assert.False(f.Radio.RadioEq[rx].Enabled);
    }

    [Fact]
    public void Ctrl_E_before_the_radio_has_reported_its_equaliser_asks_for_it_says_so_and_writes_nothing()
    {
        using var f = new NoiseFixture();
        var keys = Keys_(f, transmit: true);
        Assert.True(keys.Handle(NoiseLayerKeys.RadioEqualizerKey));
        Assert.Equal(new[] { DenoiseDirection.Transmit }, f.Radio.EqRequests);
        Assert.Equal(Lexicon.Get("audio.eq.not_reported_yet", VerbosityLevel.Chatty), f.Spoken.Last);
        Assert.True(keys.Handle(Keys.Up));                     // the arrows are on it, and find nothing yet
        Assert.Empty(f.Radio.EqWrites);
        // Once it answers, the same arrows work it.
        f.Radio.RadioEq[DenoiseDirection.Transmit] = Flat();
        Assert.True(keys.Handle(Keys.Up));
        Assert.Equal(1, Band(f, DenoiseDirection.Transmit, 0));
    }

    [Fact]
    public void A_second_press_steps_from_what_the_first_asked_for_while_the_radio_has_not_echoed_it()
    {
        // The rig queues the apply, so a read straight after a write still has the old curve.
        using var f = WithRadioEq();
        f.Radio.LagEqWrites = true;
        var keys = Keys_(f, transmit: false);
        keys.Handle(NoiseLayerKeys.RadioEqualizerKey);
        keys.Handle(Keys.Up);
        keys.Handle(Keys.Up);
        Assert.EndsWith(":on:2,0,0,0,0,0,0,0,0", f.Radio.EqWrites[^1]);
        f.Radio.FlushEq();
        Assert.Equal(2, Band(f, DenoiseDirection.Receive, 0));
    }

    // ── Escape: the radio's equaliser ──

    [Fact]
    public void Escape_puts_back_the_radios_equaliser_on_each_side_the_layer_changed_and_writes_nothing_to_the_other()
    {
        using var f = WithRadioEq();
        FlexBase.SetEqBand(f.Radio.RadioEq[DenoiseDirection.Receive], 4, 3);
        var snap = f.Noise.TakeSnapshot();
        var keys = Keys_(f, transmit: false);
        keys.Handle(NoiseLayerKeys.RadioEqualizerKey);
        keys.Handle(Keys.Right);
        keys.Handle(Keys.Up);
        int writes = f.Radio.EqWrites.Count;

        Assert.True(f.Noise.RestoreSnapshot(snap, Layer));
        Assert.False(f.Radio.RadioEq[DenoiseDirection.Receive].Enabled);
        Assert.Equal(0, Band(f, DenoiseDirection.Receive, 1));
        Assert.Equal(3, Band(f, DenoiseDirection.Receive, 4));   // a band it never touched keeps its level
        Assert.Equal(writes + 1, f.Radio.EqWrites.Count);
        Assert.DoesNotContain(f.Radio.EqWrites, w => w.StartsWith("Transmit", StringComparison.Ordinal));
        Assert.False(f.Noise.LastRestoreKeptOutsideChanges);
        Assert.False(f.Noise.LastRestoreLeftRadioDifferences);
        Assert.Equal("audio.noise_layer.restored", f.Noise.RestoreClosingKey(true));
    }

    [Fact]
    public void Escape_keeps_a_change_made_elsewhere_to_the_radios_equaliser_after_the_layers()
    {
        long now = 0;
        using var f = WithRadioEq(clock: () => now);
        var snap = f.Noise.TakeSnapshot();
        var keys = Keys_(f, transmit: true);
        keys.Handle(NoiseLayerKeys.RadioEqualizerKey);          // the layer's: on
        now += 5000;                                            // the radio has long since echoed it
        FlexBase.SetEqBand(f.Radio.RadioEq[DenoiseDirection.Transmit], 2, 5);   // Home's dialog, afterwards
        int writes = f.Radio.EqWrites.Count;

        f.Noise.RestoreSnapshot(snap, Layer);
        Assert.Equal(writes, f.Radio.EqWrites.Count);           // not the layer's to undo any more
        Assert.True(f.Radio.RadioEq[DenoiseDirection.Transmit].Enabled);
        Assert.Equal(5, Band(f, DenoiseDirection.Transmit, 2));
        Assert.True(f.Noise.LastRestoreLeftRadioDifferences);
    }

    [Fact]
    public void A_change_made_elsewhere_before_the_layers_next_edit_becomes_what_Escape_goes_back_to()
    {
        long now = 0;
        using var f = WithRadioEq(clock: () => now);
        var snap = f.Noise.TakeSnapshot();
        var keys = Keys_(f, transmit: false);
        keys.Handle(NoiseLayerKeys.RadioEqualizerKey);          // the layer's: on
        now += 5000;
        FlexBase.SetEqBand(f.Radio.RadioEq[DenoiseDirection.Receive], 8, -4); // elsewhere: 8 kHz down 4
        keys.Handle(Keys.Up);                                   // the layer's again: 32 Hz up 1
        f.Noise.RestoreSnapshot(snap, Layer);
        var rx = f.Radio.RadioEq[DenoiseDirection.Receive];
        Assert.True(rx.Enabled);                                // back to what stood before the layer's edit
        Assert.Equal(-4, FlexBase.GetEqBand(rx, 8));            // the change made elsewhere is kept
        Assert.Equal(0, FlexBase.GetEqBand(rx, 0));             // the layer's own edit goes
    }

    // ── Ctrl+K and Ctrl+L: our compressor and limiter, transmit ──

    [Fact]
    public void In_receive_the_compressor_limiter_handoff_and_learn_keys_change_nothing_and_say_they_are_transmit_keys()
    {
        using var b = new Bridge();
        b.Noise.SetProcessorHandoffSetting(true, Home);
        var keys = Keys_(b.F, transmit: false);
        var checks = new (Keys Key, string Sentence, string Subject)[]
        {
            (NoiseLayerKeys.CompressorKey, "audio.noise_layer.compressor_transmit_only", SpeechSubject.TransmitCompressor),
            (NoiseLayerKeys.LimiterKey, "audio.noise_layer.limiter_transmit_only", SpeechSubject.TransmitLimiter),
            (NoiseLayerKeys.ProcessorHandoffKey, "audio.noise_layer.handoff_transmit_only", SpeechSubject.ProcessorHandoff),
            (NoiseLayerKeys.LearnRoomKey, "audio.noise_layer.learn_room_transmit_only", SpeechSubject.TransmitRoomLearn),
        };
        foreach (var (key, sentence, subject) in checks)
        {
            bool handled = (key & Keys.Alt) != 0 ? keys.HandleShiftAlt(key) : keys.Handle(key);
            Assert.True(handled);
            Assert.Equal(Lexicon.Get(sentence, VerbosityLevel.Chatty), b.F.Spoken.Last);
            Assert.Equal(subject, b.F.Spoken.Said[^1].Subject);
        }
        Assert.False(TxAudioConditioning.CompressorSetup.Enabled);
        Assert.False(TxAudioConditioning.LimiterSetup.Enabled);
        Assert.True(b.Noise.ProcessorHandoffSetting);
        Assert.False(b.X.Cond.RoomFloor.Learning);
        Assert.False(b.X.Chain.HasDeferredChanges);
    }

    [Fact]
    public void In_transmit_Ctrl_K_and_Ctrl_L_switch_our_compressor_and_limiter_through_the_menus_own_road()
    {
        using var b = new Bridge();
        var tones = new List<bool>();
        var keys = Keys_(b.F, transmit: true, tones);
        Assert.True(keys.Handle(NoiseLayerKeys.CompressorKey));
        Assert.True(TxAudioConditioning.CompressorSetup.Enabled);
        Assert.True(b.X.Cond.Compressor.Settings.Enabled || TxAudioConditioning.CompressorSetup.Enabled);
        Assert.Equal(SpeechSubject.TransmitCompressor, b.F.Spoken.Said[^1].Subject);
        // The menu's sentence, said by the same function.
        string fromLayer = b.F.Spoken.Last;
        b.Noise.ToggleCompressor("menu"); b.Noise.ToggleCompressor("menu");
        Assert.Equal(fromLayer, b.F.Spoken.Last);

        Assert.True(keys.Handle(NoiseLayerKeys.LimiterKey));
        Assert.True(TxAudioConditioning.LimiterSetup.Enabled);
        Assert.Equal(SpeechSubject.TransmitLimiter, b.F.Spoken.Said[^1].Subject);
        Assert.Equal(new[] { true, true }, tones);
    }

    [Fact]
    public void Keyed_the_compressor_key_waits_for_the_unkey_as_the_transmit_menus_does()
    {
        using var b = new Bridge();
        var keys = Keys_(b.F, transmit: true);
        b.X.Keyed = true; b.X.Buffer();
        Assert.True(keys.Handle(NoiseLayerKeys.CompressorKey));
        Assert.True(b.X.Chain.HasDeferredChanges);
        Assert.False(TxAudioConditioning.CompressorSetup.Enabled);   // not under the over
        Assert.Contains(Lexicon.Get("audio.denoise.tx.at_unkey", VerbosityLevel.Chatty), string.Join(" ", b.F.Spoken.Said.Select(s => s.Text)));
        b.Unkey();
        Assert.True(TxAudioConditioning.CompressorSetup.Enabled);
        Assert.True(b.X.Cond.Compressor.Settings.Enabled);
    }

    [Fact]
    public void After_Ctrl_K_the_arrows_Tab_and_Space_work_the_compressors_settings_single_band_ones_only_in_single_band_mode()
    {
        using var b = new Bridge();
        var keys = Keys_(b.F, transmit: true);
        keys.Handle(NoiseLayerKeys.CompressorKey);
        float threshold = TxAudioConditioning.CompressorSetup.ThresholdDb;
        Assert.True(keys.Handle(Keys.Down));
        Assert.Equal(threshold - 1, TxAudioConditioning.CompressorSetup.ThresholdDb);
        Assert.True(keys.Handle(Keys.Tab));
        var ratio = NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "ratio")!;
        Assert.StartsWith(b.Noise.DynamicsLeverName(ratio), b.F.Spoken.Last);
        float r0 = TxAudioConditioning.CompressorSetup.RatioToOne;
        Assert.True(keys.Handle(Keys.Up));
        Assert.Equal(r0 + 0.5f, TxAudioConditioning.CompressorSetup.RatioToOne, 3);
        Assert.True(keys.Handle(Keys.Space));
        Assert.StartsWith(b.Noise.DynamicsLeverName(ratio), b.F.Spoken.Last);

        // Tab walks the single-band settings and comes round, never reaching a split-band one.
        var single = NoiseController.CompressorLevers.Where(l => !l.SplitBandOnly).ToArray();
        var heard = new List<string>();
        for (int i = 0; i < single.Length; i++) { keys.Handle(Keys.Tab); heard.Add(b.F.Spoken.Last); }
        Assert.StartsWith(b.Noise.DynamicsLeverName(ratio), heard[^1]);   // round again to where it was
        foreach (var split in NoiseController.CompressorLevers.Where(l => l.SplitBandOnly))
            Assert.DoesNotContain(heard, h => h.StartsWith(b.Noise.DynamicsLeverName(split) + " ", StringComparison.Ordinal));
        Assert.Equal(SpeechSubject.TransmitCompressor, b.F.Spoken.Said[^1].Subject);
    }

    [Fact]
    public void After_Ctrl_L_the_arrows_work_the_limiters_settings()
    {
        using var b = new Bridge();
        var keys = Keys_(b.F, transmit: true);
        keys.Handle(NoiseLayerKeys.LimiterKey);
        float ceiling = TxAudioConditioning.LimiterSetup.CeilingDb;
        Assert.True(keys.Handle(Keys.Down));
        Assert.Equal(ceiling - 0.5f, TxAudioConditioning.LimiterSetup.CeilingDb, 3);
        Assert.Equal(SpeechSubject.TransmitLimiter, b.F.Spoken.Said[^1].Subject);
        Assert.True(keys.Handle(Keys.Tab));
        var release = NoiseController.FindDynamicsLever(DynamicsLever.LimiterStage, "release")!;
        Assert.StartsWith(b.Noise.DynamicsLeverName(release), b.F.Spoken.Last);
        float r0 = TxAudioConditioning.LimiterSetup.ReleaseMs;
        Assert.True(keys.Handle(Keys.Up));
        Assert.Equal(r0 + 10, TxAudioConditioning.LimiterSetup.ReleaseMs, 3);
    }

    // ── Ctrl+P: the handoff setting ──

    [Fact]
    public void In_transmit_Ctrl_P_switches_the_handoff_setting_and_says_what_the_menu_says()
    {
        using var b = new Bridge();
        b.Noise.SetProcessorHandoffSetting(true, Home);
        var keys = Keys_(b.F, transmit: true);
        Assert.True(keys.Handle(NoiseLayerKeys.ProcessorHandoffKey));
        Assert.False(b.Noise.ProcessorHandoffSetting);
        Assert.Equal(Lexicon.Get("audio.processor_handoff.setting_off", VerbosityLevel.Chatty), b.F.Spoken.Last);
        Assert.Equal(SpeechSubject.ProcessorHandoff, b.F.Spoken.Said[^1].Subject);
        Assert.True(keys.Handle(NoiseLayerKeys.ProcessorHandoffKey));
        Assert.True(b.Noise.ProcessorHandoffSetting);
    }

    // ── Escape: the compressor, the limiter and the handoff setting ──

    [Fact]
    public void Escape_puts_back_the_compressor_the_limiter_and_the_handoff_setting_the_layer_changed()
    {
        using var b = new Bridge();
        b.Noise.SetProcessorHandoffSetting(true, Home);
        var entryCompressor = TxAudioConditioning.CompressorSetup.Clone();
        var entryLimiter = TxAudioConditioning.LimiterSetup.Clone();
        var snap = b.Noise.TakeSnapshot();
        var keys = Keys_(b.F, transmit: true);
        keys.Handle(NoiseLayerKeys.CompressorKey);
        keys.Handle(Keys.Down);                                   // threshold
        keys.Handle(NoiseLayerKeys.LimiterKey);
        keys.Handle(Keys.Down);                                   // ceiling
        keys.Handle(NoiseLayerKeys.ProcessorHandoffKey);
        Assert.True(TxAudioConditioning.CompressorSetup.Enabled);
        Assert.True(TxAudioConditioning.LimiterSetup.Enabled);
        Assert.False(b.Noise.ProcessorHandoffSetting);

        Assert.True(b.Noise.RestoreSnapshot(snap, Layer));
        b.Unkey();
        Assert.True(NoiseController.SameCompressor(entryCompressor, TxAudioConditioning.CompressorSetup));
        Assert.True(NoiseController.SameLimiter(entryLimiter, TxAudioConditioning.LimiterSetup));
        Assert.False(b.X.Cond.Compressor.Settings.Enabled);
        Assert.False(b.X.Cond.Limiter.Settings.Enabled);
        Assert.True(b.Noise.ProcessorHandoffSetting);
        Assert.False(b.Noise.LastRestoreKeptOutsideChanges);
    }

    [Fact]
    public void Escape_puts_back_a_handoff_setting_changed_alone_and_while_keyed_holds_it_for_the_unkey_as_the_editors_cancel_does()
    {
        using var b = new Bridge();
        b.Noise.SetProcessorHandoffSetting(true, Home);
        var snap = b.Noise.TakeSnapshot();
        var keys = Keys_(b.F, transmit: true);
        keys.Handle(NoiseLayerKeys.ProcessorHandoffKey);
        Assert.False(b.Noise.ProcessorHandoffSetting);
        b.X.Keyed = true; b.X.Buffer();

        Assert.True(b.Noise.RestoreSnapshot(snap, Layer));
        Assert.Equal(TxChangeTiming.AtUnkey, b.Noise.LastRestoreTiming);
        Assert.False(b.Noise.ProcessorHandoffSetting);            // not under the over
        b.Unkey();
        Assert.True(b.Noise.ProcessorHandoffSetting);
    }

    [Fact]
    public void Escape_keeps_a_handoff_setting_somebody_else_changed_after_the_layer()
    {
        using var b = new Bridge();
        b.Noise.SetProcessorHandoffSetting(true, Home);
        var snap = b.Noise.TakeSnapshot();
        var keys = Keys_(b.F, transmit: true);
        keys.Handle(NoiseLayerKeys.ProcessorHandoffKey);          // the layer's: off
        keys.Handle(NoiseLayerKeys.ProcessorHandoffKey);          // the layer's: on again
        b.Noise.SetProcessorHandoffSetting(false, Home);          // Home's, afterwards
        Assert.False(b.Noise.RestoreSnapshot(snap, Layer));
        b.Unkey();
        Assert.False(b.Noise.ProcessorHandoffSetting);            // Home's change stays
        Assert.True(b.Noise.LastRestoreKeptOutsideChanges);
    }

    [Fact]
    public void A_handoff_setting_somebody_else_put_back_is_not_the_layers_to_undo_and_Escape_says_nothing_was_undone()
    {
        using var b = new Bridge();
        b.Noise.SetProcessorHandoffSetting(true, Home);
        var snap = b.Noise.TakeSnapshot();
        var keys = Keys_(b.F, transmit: true);
        keys.Handle(NoiseLayerKeys.ProcessorHandoffKey);          // the layer's: off
        b.Noise.SetProcessorHandoffSetting(true, Home);           // Home's: on again, afterwards
        Assert.False(b.Noise.RestoreSnapshot(snap, Layer));       // it no longer reads as the layer left it
        Assert.True(b.Noise.ProcessorHandoffSetting);
        Assert.Equal("audio.noise_layer.restored_nothing", b.Noise.RestoreClosingKey(false));
    }

    // ── Shift+Alt+L: Learn the room ──

    [Fact]
    public void In_transmit_Shift_Alt_L_learns_the_room_through_the_road_the_Home_button_takes()
    {
        using var b = new Bridge();
        var stereo = new float[960];
        var rng = new Random(7);
        for (int n = 0; n < 10; n++)
        {
            for (int i = 0; i < stereo.Length; i++) stereo[i] = (float)((rng.NextDouble() * 2 - 1) * 0.001);
            b.X.Cond.Process(stereo, stereo.Length, 48000);
        }
        var keys = Keys_(b.F, transmit: true);
        Assert.True(keys.HandleShiftAlt(NoiseLayerKeys.LearnRoomKey));
        Assert.True(b.X.Cond.RoomFloor.Learning);
        Assert.Equal(SpeechSubject.TransmitRoomLearn, b.F.Spoken.Said[^1].Subject);
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.learn_started", VerbosityLevel.Chatty), b.F.Spoken.Last);
    }
}
