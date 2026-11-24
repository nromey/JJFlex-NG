using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using JJPortaudio;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track I, repair round 1 (Codex's check of ce447d0e): the noise
/// layer's Escape puts back the equaliser and the de-esser (B1); the transmit
/// equaliser editor's Cancel puts back its on or off (B2); a preset that
/// changes only the new stages says so (B3); the receive equaliser's switches
/// take the receive switch choices (B4); the live state's version 1 upgrade
/// in any element order (N1); the receive equaliser in presets (N2); the
/// "is the chain doing anything" answer (N3); nothing allocated when "what was
/// removed" is chosen (N4); a learn held to the unkey (N6); the editor's
/// output gain shown with no band (N7). Windowless: MTA asserted; no window,
/// no sound (the switch beep is never turned on here), no radio.
/// </summary>
[Collection("Transmit conditioning statics")]
public sealed class TxSoundStagesRepairOneTests
{
    private const string Layer = NoiseController.LayerSource;

    public TxSoundStagesRepairOneTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static readonly BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;

    /// <summary>The owner's statics (Context) and a noise controller whose transmit road is the owner's own chain and real gate bridge.</summary>
    private sealed class Bridge : IDisposable
    {
        public readonly TxGateOwnerTests.Context X = new();
        public readonly NoiseFixture F = new();
        public Bridge()
        {
            typeof(NoiseFixture).GetField("_transmit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(F, X.Chain);
            F.Noise.TransmitGate = new AppTransmitGate();
            TxAudioConditioning.ApplySetup(new TxNoiseSetup());
            X.Buffer();
        }
        public void Dispose() { F.Dispose(); X.Dispose(); }
    }

    private static ParametricEqSetup Bell(bool on, double gain = 6) => new()
    {
        Enabled = on, Bands = { new() { Kind = ParametricEqKinds.Peaking, FrequencyHz = 1000, GainDb = (float)gain, Q = 1 } },
    };

    private static bool SameEq(ParametricEqSetup a, ParametricEqSetup b) =>
        new TxNoiseSetup { Eq = a }.SameContentAs(new TxNoiseSetup { Eq = b });

    // ── B1: the layer's Escape ──

    [Fact]
    public void Escape_puts_back_the_receive_equaliser_the_layer_changed_and_says_it_restored_something()
    {
        using var f = new NoiseFixture();
        var before = f.Noise.EqualizerSetup(DenoiseDirection.Receive);
        var snap = f.Noise.TakeSnapshot();
        var layer = new NoiseLayerKeys(f.Noise);
        Assert.True(layer.Handle(NoiseLayerKeys.EqualizerKey));
        Assert.True(layer.Handle(NoiseLayerKeys.AddBandKey));
        Assert.True(f.Noise.EqualizerOn(DenoiseDirection.Receive));
        bool restored = f.Noise.RestoreSnapshot(snap, Layer);
        Assert.True(restored);
        Assert.True(SameEq(before, f.Noise.EqualizerSetup(DenoiseDirection.Receive)));
        Assert.Equal("audio.noise_layer.restored", f.Noise.RestoreClosingKey(restored));
    }

    [Fact]
    public void Escape_keeps_a_receive_equaliser_change_made_elsewhere_after_the_layers_own()
    {
        using var f = new NoiseFixture();
        var snap = f.Noise.TakeSnapshot();
        var layer = new NoiseLayerKeys(f.Noise);
        layer.Handle(NoiseLayerKeys.EqualizerKey);                         // the layer's
        f.Noise.LoadEqualizerPreset(DenoiseDirection.Receive, "hiss_cut", "home"); // elsewhere, after it
        var kept = f.Noise.EqualizerSetup(DenoiseDirection.Receive);
        f.Noise.RestoreSnapshot(snap, Layer);
        Assert.True(SameEq(kept, f.Noise.EqualizerSetup(DenoiseDirection.Receive)));
        Assert.True(f.Noise.LastRestoreKeptOutsideChanges);
    }

    [Fact]
    public void Escape_puts_back_the_transmit_equaliser_and_de_esser_and_withdraws_one_held_for_the_unkey()
    {
        using var b = new Bridge();
        var f = b.F;
        var eqBefore = TxAudioConditioning.EqSetup;
        var snap = f.Noise.TakeSnapshot();
        var layer = new NoiseLayerKeys(f.Noise);
        Assert.True(layer.Handle(NoiseLayerKeys.DirectionKey));
        Assert.True(layer.Handle(NoiseLayerKeys.EqualizerKey));
        Assert.True(layer.Handle(NoiseLayerKeys.DeEsserKey));
        Assert.True(TxAudioConditioning.EqSetup.Enabled);
        Assert.True(TxAudioConditioning.DeEsserSetup.Enabled);
        // And one more, held: keyed, the layer adds a band, which waits.
        b.X.Keyed = true;
        Assert.True(layer.Handle(NoiseLayerKeys.AddBandKey));
        Assert.True(f.Noise.RestoreSnapshot(snap, Layer));
        b.X.Keyed = false;
        b.X.Chain.ApplyDeferred();
        b.X.Buffer();
        Assert.True(SameEq(eqBefore, TxAudioConditioning.EqSetup));
        Assert.False(TxAudioConditioning.DeEsserSetup.Enabled);
        Assert.False(b.X.Cond.DeEsser.Enabled);
    }

    // ── B2: the transmit editor's Cancel ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_transmit_editors_cancel_puts_back_its_on_or_off_too_even_a_switch_held_for_the_unkey(bool keyed)
    {
        using var b = new Bridge();
        var f = b.F;
        const string source = "equalizer editor";
        var editor = f.Noise.EqualizerEditor(DenoiseDirection.Transmit, source);
        var opened = editor.Setting;
        Assert.False(opened.Enabled);
        b.X.Keyed = keyed;
        f.Noise.SetEqualizerOn(DenoiseDirection.Transmit, true, source); // the dialog's On box
        editor.AddBand();
        editor.Restore(opened);                                         // Cancel
        b.X.Keyed = false;
        b.X.Chain.ApplyDeferred();
        b.X.Buffer();
        var now = TxAudioConditioning.EqSetup;
        Assert.False(now.Enabled);
        Assert.Empty(now.Bands);
        Assert.False(b.X.Cond.Eq.IsActive);
    }

    // ── B3 and N2: presets say what they change ──

    [Fact]
    public void A_transmit_preset_that_changes_only_the_equaliser_and_de_esser_says_so_not_nothing_to_change()
    {
        using var b = new Bridge();
        var preset = new AudioChainPreset
        {
            Name = "Stages only",
            TransmitNoise = new TxNoiseSetup { Eq = Bell(true), DeEsser = new TxDeEsserSetup { Enabled = true } },
        };
        string summary = b.F.Noise.ApplyNoiseSectionsForWorkshop(preset, "workshop");
        Assert.DoesNotContain(Lexicon.Get("audio.noise_surface.preset_nothing_changed"), summary);
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.phrase_eq"), summary);
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.phrase_deesser"), summary);
        Assert.True(TxAudioConditioning.EqSetup.Enabled);
        // The same preset again changes nothing, and says so.
        Assert.Contains(Lexicon.Get("audio.noise_surface.preset_nothing_changed"), b.F.Noise.ApplyNoiseSectionsForWorkshop(preset, "workshop"));
    }

    [Fact]
    public void A_receive_preset_carries_the_equaliser_applies_it_and_says_so_and_an_older_preset_leaves_it_alone()
    {
        using var f = new NoiseFixture();
        f.Pipeline.SetEqualizer(Bell(true));
        var captured = f.Noise.CaptureReceiveNoise();
        Assert.NotNull(captured.Eq);
        Assert.True(SameEq(Bell(true), captured.Eq!));

        f.Pipeline.SetEqualizer(new ParametricEqSetup());
        string said = f.Noise.ApplyNoiseSectionsForWorkshop(new AudioChainPreset { Name = "R", ReceiveNoise = captured }, "workshop");
        Assert.True(SameEq(Bell(true), f.Pipeline.EqualizerSetup));
        Assert.Contains(Lexicon.Get("audio.noise_surface.phrase_eq"), said);

        // A preset from before the section existed (no Eq) changes nothing here.
        var old = f.Noise.CaptureReceiveNoise();
        old.Eq = null;
        f.Pipeline.SetEqualizer(Bell(false, 3));
        f.Noise.ApplyNoiseSectionsForWorkshop(new AudioChainPreset { Name = "Old", ReceiveNoise = old }, "workshop");
        Assert.True(SameEq(Bell(false, 3), f.Pipeline.EqualizerSetup));
    }

    // ── B4: the receive switch choices ──

    [Fact]
    public void A_receive_equaliser_switch_is_said_only_when_switch_speech_is_on()
    {
        using var f = new NoiseFixture();
        f.Config.NoiseSwitchBeep = false; // never a sound in a test
        f.Config.NoiseSwitchSpeech = false;
        f.Spoken.Clear();
        f.Noise.SetEqualizerOn(DenoiseDirection.Receive, true, "home");
        Assert.Empty(f.Spoken.Said);
        f.Config.NoiseSwitchSpeech = true;
        f.Noise.SetEqualizerOn(DenoiseDirection.Receive, false, "home");
        Assert.Single(f.Spoken.Said);
        Assert.Equal(Radios.Speech.SpeechSubject.NoiseEngineSwitch, f.Spoken.Said[0].Subject);
    }

    private static float[] RunSwitch(RxAudioPipeline p, bool isSwitch)
    {
        var tone = new float[48000 * 2];
        for (int i = 0; i < 48000; i++) tone[2 * i] = tone[2 * i + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * i / 48000));
        var buf = new float[960];
        for (int at = 0, k = 0; at < tone.Length; at += 960, k++)
        {
            if (k == 20) p.SetEqualizer(Bell(true, 12), isSwitch);
            Array.Copy(tone, at, buf, 0, 960);
            p.Process(buf);
            Array.Copy(buf, 0, tone, at, 960);
        }
        return tone;
    }

    [Fact]
    public void With_Mute_on_a_receive_equaliser_switch_leaves_a_silent_gap_and_a_band_edit_never_does()
    {
        using var p = new RxAudioPipeline { EqMuteOverrideForTest = true };
        var switched = RunSwitch(p, isSwitch: true);
        int start = 20 * 480;
        // 5 ms of ramp, then 100 ms of silence.
        double gap = 0;
        for (int i = start + 300; i < start + 4800; i++) gap = Math.Max(gap, Math.Abs(switched[2 * i]));
        Assert.True(gap < 1e-9, $"the Mute gap is silent ({gap:E2})");
        using var q = new RxAudioPipeline { EqMuteOverrideForTest = true };
        var edited = RunSwitch(q, isSwitch: false);
        double min = double.MaxValue;
        for (int i = start + 300; i < start + 4800; i += 48) min = Math.Min(min, Enumerable.Range(i, 48).Max(k => Math.Abs(edited[2 * k])));
        Assert.True(min > 0.05, "a band edit is never muted");
    }

    [Fact]
    public void With_cross_fade_off_a_receive_equaliser_switch_is_a_straight_cut()
    {
        using var p = new RxAudioPipeline { EqMuteOverrideForTest = false, EqCrossFadeOverrideForTest = false };
        var cut = RunSwitch(p, isSwitch: true);
        using var q = new RxAudioPipeline { EqMuteOverrideForTest = false, EqCrossFadeOverrideForTest = true };
        var faded = RunSwitch(q, isSwitch: true);
        int start = 20 * 480;
        // 2 ms after the switch the cut is already at the boosted level; the fade is not.
        double Peak(float[] x) => Enumerable.Range(start + 96, 48).Max(k => Math.Abs(x[2 * k]));
        Assert.True(Peak(cut) > 0.35, $"cut: already boosted ({Peak(cut):F3})");
        Assert.True(Peak(faded) < Peak(cut) - 0.05, $"fade: still on its way ({Peak(faded):F3})");
    }

    // ── N1: the live state's upgrade in any element order ──

    [Theory]
    [InlineData("v1")]
    [InlineData("no_version")]
    [InlineData("gate_first")]
    [InlineData("newer")]
    public void The_live_transmit_state_in_audio_config_upgrades_in_any_element_order(string shape)
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjf-track-i-r1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "audioConfig.xml"), "<?xml version=\"1.0\" encoding=\"utf-8\"?><AudioOutputConfig>"
                + VersionOneShapes.Of(shape) + "</AudioOutputConfig>");
            var tx = AudioOutputConfig.Load(dir).TransmitNoise!;
            if (shape == "newer")
            {
                Assert.Equal(3, tx.Version);
                Assert.False(tx.Gate.GateSetting);
                return;
            }
            Assert.Equal(TxNoiseSetup.CurrentVersion, tx.Version);
            Assert.True(tx.Gate.Enabled);
            Assert.True(tx.Gate.GateSetting);
            Assert.False(tx.Gate.SideChainFilter);
            Assert.Equal(0f, tx.Gate.LookAheadMs);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ── N3: is the chain doing anything ──

    [Fact]
    public void The_chain_is_active_only_when_a_stage_is_switched_on_and_never_in_cw_or_digital()
    {
        using var x = new TxGateOwnerTests.Context();
        var rig = (FlexBase)typeof(TxAudioConditioning).GetField("_rig", Statics)!.GetValue(null)!;
        // Plugged exactly as Attach plugs it: a plugged stage with both slots off is not "doing anything".
        TxAudioConditioning.PlugNoiseStage(x.Cond, () => x.Keyed, (b, n, r) => { }, () => 0, TxAudioConditioning.EngagedOf(x.Chain));
        Assert.Equal(false, Radios.ChainChecks.TxDifferentialCapture.ConditioningActive(rig));
        x.Chain.SetEnabled(DenoiseFamily.Neural, true);
        Assert.Equal(true, Radios.ChainChecks.TxDifferentialCapture.ConditioningActive(rig)); // the positive control
        x.Chain.SetEnabled(DenoiseFamily.Neural, false);
        TxAudioConditioning.SetEqWhole(Bell(true));
        x.Buffer();
        Assert.Equal(true, Radios.ChainChecks.TxDifferentialCapture.ConditioningActive(rig));
        x.Cond.BypassAll = true;
        Assert.Equal(false, Radios.ChainChecks.TxDifferentialCapture.ConditioningActive(rig));
        x.Cond.BypassAll = false;
        TxAudioConditioning.SetEqWhole(new ParametricEqSetup());
        TxAudioConditioning.SetDeEsserEnabled(true);
        x.Buffer();
        Assert.Equal(true, Radios.ChainChecks.TxDifferentialCapture.ConditioningActive(rig));
        TxAudioConditioning.SetDeEsserEnabled(false);
    }

    // ── N4: nothing allocated when "what was removed" is chosen ──

    [Fact]
    public void Choosing_what_was_removed_allocates_nothing_on_the_audio_thread()
    {
        var c = new TxAudioConditioner();
        c.SetGateInForce(new TxAudioConditioner.StagedGate { Enabled = true });
        var buf = new float[960];
        for (int i = 0; i < 5; i++) c.Process(buf, buf.Length, 48000); // warm, monitor off
        c.MonitorSink = (b, n, r) => { };
        c.MonitorMode = TxAudioConditioner.MonitorModes.Residual;
        long before = GC.GetAllocatedBytesForCurrentThread();
        c.Process(buf, buf.Length, 48000);
        c.MonitorMode = TxAudioConditioner.MonitorModes.Split;
        c.Process(buf, buf.Length, 48000);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_transmit_equaliser_change_reaches_the_audio_thread_without_allocating_there()
    {
        var c = new TxAudioConditioner();
        var buf = new float[960];
        for (int i = 0; i < 5; i++) c.Process(buf, buf.Length, 48000);
        var design = new ParametricEqDesign(new[] { new ParametricEqBand(BiquadKind.Peaking, 1000, 6, 1) });
        c.ChangeGate(g => g.Eq = design); // made off the audio thread, as a lever does
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5; i++) c.Process(buf, buf.Length, 48000);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(c.Eq.IsActive);
    }

    // ── N6: a learn held to the unkey ──

    [Fact]
    public void A_learn_that_finishes_while_keyed_waits_for_the_unkey_before_it_moves_the_threshold()
    {
        using var x = new TxGateOwnerTests.Context();
        TxAudioConditioning.ApplySetup(new TxNoiseSetup { Gate = new TxNoiseGateSetup { Enabled = true, AutoThreshold = false, ThresholdDb = -70, SideChainFilter = false } });
        var room = new float[960];
        var rng = new Random(3);
        void Feed(double seconds)
        {
            for (int n = 0; n < (int)(seconds * 100); n++)
            {
                for (int i = 0; i < 960; i += 2) room[i] = room[i + 1] = (float)((rng.NextDouble() * 2 - 1) * 0.01);
                x.Cond.Process(room, 960, 48000);
            }
        }
        Feed(0.1);
        TxAudioConditioning.LearnRoom();
        Feed(3.6);
        bool said = false;
        Action<TxAudioConditioning.RoomLearnedResult> listen = _ => said = true;
        TxAudioConditioning.RoomLearned += listen;
        try
        {
            TxAudioConditioning.FinishLearnIfDone(x.Cond, keyed: true);
            Assert.False(said);
            Assert.Equal(-70f, x.Cond.Gate.ThresholdDb);
            TxAudioConditioning.FinishLearnIfDone(x.Cond, keyed: false);
            Assert.True(said);
            Assert.NotEqual(-70f, x.Cond.Gate.ThresholdDb);
        }
        finally { TxAudioConditioning.RoomLearned -= listen; }
    }

    // ── N7: the editor's output gain with no band ──

    [Fact]
    public void The_editor_shows_the_output_gain_with_no_band_selected()
    {
        var flatWithGain = new ParametricEqSetup { OutputGainDb = 3 };
        var d = ParametricEqEditorModel.Display(flatWithGain, -1);
        Assert.False(d.HasBand);
        Assert.Equal(30, d.OutputTenths);
        var m = ParametricEqEditorModel.Detached(DenoiseDirection.Receive);
        m.AddBand();
        m.AdjustOutputGain(+1);
        m.RemoveBand();
        Assert.Equal(5, ParametricEqEditorModel.Display(m.Setting, m.Band).OutputTenths);
    }
}

/// <summary>"Desk mic" as the base commit serialized it (the same probe as Radios.Tests' fixture), in the shapes the upgrade must survive, under the live state's element name.</summary>
internal static class VersionOneShapes
{
    private const string Gate = "<Gate><Enabled>true</Enabled><AutoThreshold>false</AutoThreshold><ThresholdMarginDb>9</ThresholdMarginDb><ThresholdDb>-42</ThresholdDb><AttackMs>4</AttackMs><HoldMs>220</HoldMs><ReleaseMs>260</ReleaseMs><RangeDb>30</RangeDb></Gate>";
    private const string Slots = "<Name>Desk mic</Name><Spectral><Enabled>false</Enabled><EngineId /><Engines /></Spectral><Neural><Enabled>false</Enabled><EngineId /><Engines /></Neural>";

    public static string Of(string shape) => shape switch
    {
        "no_version" => "<TransmitNoise>" + Slots + Gate + "</TransmitNoise>",
        "gate_first" => "<TransmitNoise>" + Gate + "<Version>1</Version>" + Slots + "</TransmitNoise>",
        "newer" => "<TransmitNoise><Version>3</Version>" + Slots + Gate + "</TransmitNoise>",
        _ => "<TransmitNoise><Version>1</Version>" + Slots + Gate + "</TransmitNoise>",
    };
}
