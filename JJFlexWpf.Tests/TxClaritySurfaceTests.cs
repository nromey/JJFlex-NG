using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using JJPortaudio;
using Radios;
using Radios.Listenback;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track J phase 2 (#736): JJ Clarity through the app's owner
/// (<see cref="TxAudioConditioning"/>), the noise controller's rule book, the
/// saved homes, the audition twin, the editor's model, and the suggest pass's
/// windowless flow. Windowless: MTA asserted in the constructor; the owner's
/// numerical chain on a radio object that is only its injected fields. No
/// window, device, radio or microphone; nothing is keyed and nothing plays.
/// </summary>
[Collection("Transmit conditioning statics")]
public sealed class TxClaritySurfaceTests
{
    private const int Rate = 48000;
    private const string Home = "home";
    private const string Layer = NoiseController.LayerSource;
    private const string Radio8600 = "1234-5678-9012-3456";

    public TxClaritySurfaceTests() => NoiseReductionPerChannelTests.AssertWindowless();

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
        public NoiseController Noise => F.Noise;
        public TxAudioConditioner Cond => X.Cond;
        public void Dispose() { F.Dispose(); X.Dispose(); }
        public void Unkey() { X.Keyed = false; X.Chain.ApplyDeferred(); X.Buffer(); }
    }

    private static TxClaritySetup Bright(bool on = true) => new()
    {
        Enabled = on,
        HighBand = new TxClarityBandSetup { Enabled = true, TuneHz = 1800, DriveDb = 15, MixPercent = 35, Harmonics = TxClarityHarmonics.Even },
        LowBand = new TxClarityBandSetup { Enabled = true, TuneHz = 120, DriveDb = 9, MixPercent = 30, Harmonics = TxClarityHarmonics.Both },
    };

    private static DynamicsLever Lever(string key) => NoiseController.FindDynamicsLever(DynamicsLever.ClarityStage, key)!;

    // ── the key discipline, through the owner (J4) ──

    [Fact]
    public void A_clarity_switch_made_keyed_waits_for_the_unkey_and_a_lever_is_live_and_never_carries_the_waiting_switch_in()
    {
        using var b = new Bridge();
        b.X.Keyed = true;
        b.X.Buffer();
        // The levers are live while keyed (S15)...
        Assert.Equal(TxChangeTiming.Now, TxAudioConditioning.SetClarityLevel("high.mix", 60).Timing);
        Assert.Equal(TxChangeTiming.Now, TxAudioConditioning.SetClarityLevel("low.tune", 200).Timing);
        // ...and every switch waits for the unkey: the whole stage, a band, its harmonics, a preset.
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetClarityEnabled(true).Timing);
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetClarityBandEnabled(false, true).Timing);
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetClarityHarmonics(true, TxClarityHarmonics.Odd).Timing);
        // A lever turned once something is held goes in order behind it (the one policy every transmit lever follows).
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.SetClarityLevel("high.drive", 20).Timing);
        b.X.Buffer();
        var now = b.Cond.Clarity.Settings;
        Assert.Equal(60f, now.High.MixPercent);
        Assert.Equal(200f, now.Low.TuneHz);
        // The live levers did not bring a waiting switch in with them, and the held lever has not landed.
        Assert.False(b.Cond.Clarity.Enabled);
        Assert.False(now.Low.Enabled);
        Assert.Equal(ClarityHarmonics.Even, now.High.Harmonics);
        Assert.Equal(ClaritySettings.HighDriveDefaultDb, now.High.DriveDb);
        // The intent holds what was asked for (settings are intents).
        Assert.Equal(60f, TxAudioConditioning.ClaritySetup.HighBand.MixPercent);

        b.Unkey();
        now = b.Cond.Clarity.Settings;
        Assert.True(b.Cond.Clarity.Enabled);
        Assert.True(now.Low.Enabled);
        Assert.Equal((ClarityHarmonics.Odd, 20f, 60f, 200f), (now.High.Harmonics, now.High.DriveDb, now.High.MixPercent, now.Low.TuneHz));
        Assert.True(TxAudioConditioning.ClaritySetup.Enabled);
        Assert.True(b.Cond.AnyStageActive);
    }

    [Fact]
    public void A_lever_turned_while_a_band_switch_waits_for_its_buffer_does_not_carry_the_switch_into_force()
    {
        using var b = new Bridge();
        // Unkeyed, and no buffer has run since: the band's switch is in the waiting record only.
        TxAudioConditioning.SetClarityBandEnabled(false, true);
        TxAudioConditioning.SetClarityHarmonics(true, TxClarityHarmonics.Odd);
        Assert.True(b.Cond.HasStagedGate);
        Assert.False(b.Cond.GateInForce.Clarity.Low.Enabled);
        // A lever now: it goes into the record in force AND the waiting one, each keeping its own switches.
        Assert.Equal(TxChangeTiming.Now, TxAudioConditioning.SetClarityLevel("high.mix", 60).Timing);
        var inForce = b.Cond.GateInForce.Clarity;
        Assert.Equal(60f, inForce.High.MixPercent);
        Assert.False(inForce.Low.Enabled);                          // the waiting switch did not ride in on the lever
        Assert.Equal(ClarityHarmonics.Even, inForce.High.Harmonics);
        b.X.Buffer();                                               // the waiting record lands, carrying the lever
        var landed = b.Cond.Clarity.Settings;
        Assert.Equal((60f, true, ClarityHarmonics.Odd), (landed.High.MixPercent, landed.Low.Enabled, landed.High.Harmonics));
    }

    [Fact]
    public void Every_clarity_lever_reaches_its_own_field_and_an_unknown_one_is_refused()
    {
        using var b = new Bridge();
        foreach (var lever in NoiseController.ClarityLevers)
        {
            double value = lever.Min + lever.Step * 3;
            b.Noise.SetDynamics(lever, value, Home);
            b.Unkey();
            Assert.Equal(value, b.Noise.DynamicsNumber(lever), 3);
            Assert.Equal(TxDynamicsEditorModel.ValueOf(lever, TxAudioConditioning.CaptureClarity()), b.Noise.DynamicsNumber(lever), 3);
            // The label and the Ctrl+F1 text are keys that exist (DRAFT PROSE until Codex words them), and name JJ Clarity.
            Assert.StartsWith("WORDING PLACEHOLDER: DRAFT PROSE: JJ Clarity", Lexicon.Get(lever.LabelKey));
            Assert.StartsWith("WORDING PLACEHOLDER: DRAFT PROSE:", Lexicon.Get(lever.HelpKey));
        }
        // Each one moved its OWN field: all six differ from a new Clarity's, and from each other's band.
        var s = TxAudioConditioning.ClaritySetup;
        Assert.Equal((15f, 1300f, 3f), (s.HighBand.MixPercent, s.HighBand.TuneHz, s.HighBand.DriveDb));
        Assert.Equal((15f, 90f, 3f), (s.LowBand.MixPercent, s.LowBand.TuneHz, s.LowBand.DriveDb));
        Assert.Equal(TxChangeTiming.Refused, TxAudioConditioning.SetClarityLevel("nope", 1).Timing);
        Assert.Equal(TxChangeTiming.Refused, TxAudioConditioning.SetClarityLevel("mid.mix", 1).Timing);
        Assert.Equal(TxChangeTiming.Refused, TxAudioConditioning.SetClarityLevel("high.nope", 1).Timing);
        Assert.Equal(TxChangeTiming.Refused, TxAudioConditioning.SetClarityLevel("high.mix", float.NaN).Timing);
        Assert.Equal(NoiseController.ClarityLevers.Length, NoiseController.ClarityLevers.Select(l => l.Key).Distinct().Count());
        Assert.Equal(new[] { "high.mix", "low.mix" }, NoiseController.ClarityLevers.Where(l => l.OnHome).Select(l => l.Key));
        Assert.All(NoiseController.ClarityLevers, l => Assert.False(l.Switch));   // every one is live while keyed
        // The shared lever functions find Clarity's levers by its stage, and still find the others'.
        Assert.Same(NoiseController.ClarityLevers[0], NoiseController.FindDynamicsLever(DynamicsLever.ClarityStage, "high.mix"));
        Assert.NotNull(NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "threshold"));
        Assert.NotNull(NoiseController.FindDynamicsLever(DynamicsLever.LimiterStage, "ceiling"));
    }

    [Fact]
    public void The_controllers_switches_say_what_they_did_on_claritys_own_subject()
    {
        using var b = new Bridge();
        b.F.Spoken.Clear();
        b.Noise.SetClarityOn(true, Home);
        b.Noise.SetClarityBandOn(false, true, Home);
        b.Noise.SetClarityHarmonics(true, TxClarityHarmonics.Both, Home);
        b.Noise.SetDynamics(Lever("high.mix"), 40, Home);
        b.Noise.SayClarityNow();
        b.Unkey();
        Assert.All(b.F.Spoken.Said, s => Assert.Equal(Radios.Speech.SpeechSubject.TransmitClarity, s.Subject));
        Assert.Equal(Lexicon.Get("audio.clarity.on"), b.F.Spoken.Said[0].Text);
        Assert.Equal(Lexicon.Get("audio.clarity.band_on", ("band", NoiseController.ClarityBandName(false))), b.F.Spoken.Said[1].Text);
        Assert.Equal(Lexicon.Get("audio.clarity.harmonics_set", ("band", NoiseController.ClarityBandName(true)),
            ("harmonics", NoiseController.ClarityHarmonicsName(TxClarityHarmonics.Both))), b.F.Spoken.Said[2].Text);
        Assert.True(b.Noise.ClarityOn);
        Assert.True(b.Noise.ClarityBandOn(false));
        Assert.Equal(TxClarityHarmonics.Both, b.Noise.ClarityHarmonicsOf(true));
        // On, with silence going through: it is adding nothing at this moment. In CW or a digital mode the chain
        // stands aside: on, and not running. Off, it says off.
        Assert.Equal(Lexicon.Get("audio.clarity.now_nothing"), b.Noise.ClarityNowSentence());
        b.Cond.BypassAll = true;
        Assert.Equal(Lexicon.Get("audio.clarity.now_not_running"), b.Noise.ClarityNowSentence());
        b.Cond.BypassAll = false;
        // Audio through it: the reading is a figure, and after an over, what that over added.
        var tone = new float[960];
        for (int i = 0; i < 480; i++) tone[2 * i] = tone[2 * i + 1] = (float)(0.25 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));
        for (int k = 0; k < 10; k++) b.Cond.Process((float[])tone.Clone(), 960, 48000);
        var reading = TxAudioConditioning.ReadClarityNow();
        Assert.True(reading.Running);
        Assert.True(reading.AddingDbfs > -60f && reading.AddingDbfs < 0f, $"adding {reading.AddingDbfs}");
        Assert.Equal(Lexicon.Get("audio.clarity.now", ("adding", Math.Round(reading.AddingDbfs, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))),
            b.Noise.ClarityNowSentence());
        b.Noise.SetClarityOn(false, Home);
        b.Unkey();
        Assert.Equal(Lexicon.Get("audio.clarity.now_off"), b.Noise.ClarityNowSentence());
        // The harmonics row cycles round the three.
        b.Noise.CycleClarityHarmonics(true, Home);
        b.Unkey();
        Assert.Equal(TxClarityHarmonics.Even, b.Noise.ClarityHarmonicsOf(true));
    }

    // ── presets: starting points (J2 on its surface) ──

    [Fact]
    public void A_clarity_preset_fills_both_bands_keeps_the_whole_switch_and_stays_the_operators()
    {
        using var b = new Bridge();
        b.Noise.LoadClarityPreset(ClarityPresets.Fuller, Home);
        b.Unkey();
        var now = TxAudioConditioning.ClaritySetup;
        Assert.False(now.Enabled);   // loading a preset does not switch JJ Clarity on
        Assert.Equal(ClarityPresets.Fuller, now.PresetId);
        Assert.Equal(ClarityPresets.Find(ClarityPresets.Fuller).Settings, now.ToSettings());
        Assert.Same(ClarityPresets.Find(ClarityPresets.Fuller), b.Noise.ClarityPresetInPlace);
        Assert.False(b.Cond.Clarity.Enabled);
        Assert.Equal(now.ToSettings(), b.Cond.Clarity.Settings);

        b.Noise.SetClarityOn(true, Home);
        b.Unkey();
        Assert.Same(ClarityPresets.Find(ClarityPresets.Fuller), b.Noise.ClarityPresetInPlace);   // on or off is not part of a preset

        // One lever turned: every field is the operator's, and it is no longer that preset.
        b.Noise.SetDynamics(Lever("high.mix"), 50, Home);
        Assert.Null(b.Noise.ClarityPresetInPlace);
        Assert.Equal(9f, TxAudioConditioning.ClaritySetup.LowBand.DriveDb);   // the rest of the preset stays
        Assert.True(TxAudioConditioning.ClaritySetup.Enabled);
        Assert.Equal(TxChangeTiming.Refused, TxAudioConditioning.LoadClarityPreset("nope").Timing);
        // Every preset has a name key that exists and names itself apart from "none".
        foreach (var preset in NoiseController.ClarityPresetList)
            Assert.NotEqual(Lexicon.Get("audio.clarity.preset_none"), NoiseController.ClarityPresetName(preset));
    }

    // ── whole setups: every road carries Clarity (item 13) ──

    [Fact]
    public void A_whole_setup_lands_clarity_on_one_unkeyed_buffer_and_is_captured_back_the_same()
    {
        using var b = new Bridge();
        b.X.Keyed = true; b.X.Buffer();
        TxAudioConditioning.ApplySetup(new TxNoiseSetup { Gate = new TxNoiseGateSetup { Enabled = true }, Clarity = Bright() });
        b.X.Buffer();
        Assert.False(b.Cond.Clarity.Enabled);   // keyed: nothing of it yet
        Assert.False(b.Cond.Gate.Enabled);
        b.Unkey();
        Assert.True(b.Cond.Gate.Enabled);
        Assert.True(b.Cond.Clarity.Enabled);
        Assert.Equal(Bright().ToSettings(), b.Cond.Clarity.Settings);
        var captured = TxAudioConditioning.CaptureSetup("");
        Assert.True(NoiseController.SameClarity(captured.Clarity, Bright()));
        Assert.Equal(TxAudioConditioner.StageIds.DefaultOrder, captured.StageOrder);
        // The controller's capture is the owner's.
        Assert.True(NoiseController.SameClarity(b.Noise.CaptureTransmitSetup("x").Clarity, Bright()));
        // Two settings that differ only in the preset's note act the same; a lever apart does not.
        var noted = Bright(); noted.PresetId = ClarityPresets.Gentle;
        Assert.True(NoiseController.SameClarity(Bright(), noted));
        noted.HighBand.MixPercent += 1;
        Assert.False(NoiseController.SameClarity(Bright(), noted));
    }

    [Fact]
    public void A_setup_saved_before_clarity_switches_it_off_and_so_does_the_recommended_state()
    {
        using var b = new Bridge();
        TxAudioConditioning.ApplySetup(new TxNoiseSetup { Clarity = Bright() });
        b.Unkey();
        Assert.True(b.Cond.Clarity.Enabled);
        // A setup with no Clarity section (every setup saved before this track): off, as a saved setup keeps its sound.
        TxAudioConditioning.ApplySetup(new TxNoiseSetup { Gate = new TxNoiseGateSetup { Enabled = true } });
        b.Unkey();
        Assert.False(b.Cond.Clarity.Enabled);
        Assert.False(TxAudioConditioning.ClaritySetup.Enabled);

        TxAudioConditioning.ApplySetup(new TxNoiseSetup { Clarity = Bright() });
        b.Unkey();
        TxAudioConditioning.ResetToRecommended();
        b.Unkey();
        Assert.False(TxAudioConditioning.ClaritySetup.Enabled);
        Assert.False(b.Cond.Clarity.Enabled);
    }

    [Fact]
    public void Changing_the_gate_or_another_stage_keeps_clarity_in_the_one_record()
    {
        // ToConditioner builds a WHOLE record on every one of these roads: anything it did not carry would be switched off.
        using var b = new Bridge();
        TxAudioConditioning.ApplySetup(new TxNoiseSetup { Clarity = Bright() });
        b.Unkey();
        TxAudioConditioning.ApplyGateNow(new TxNoiseGateSetup { Enabled = true });          // the gate's own road
        b.Unkey();
        Assert.True(b.Cond.Clarity.Enabled);
        TxAudioConditioning.ApplyStagesNow(new ParametricEqSetup(), new TxDeEsserSetup { Enabled = true }, null, null);   // the stages' road
        b.Unkey();
        Assert.True(b.Cond.Clarity.Enabled);
        Assert.Equal(Bright().ToSettings(), b.Cond.Clarity.Settings);
        // And ToConditioner itself: with a setup, Clarity as the setup has it; with none given, off.
        var record = TxAudioConditioning.ToConditioner(new TxNoiseSetup { Clarity = Bright() });
        Assert.True(record.ClarityEnabled);
        Assert.Equal(Bright().ToSettings(), record.Clarity);
        Assert.False(TxAudioConditioning.ToConditioner(new TxNoiseGateSetup(), null, null, true, 8f).ClarityEnabled);
    }

    // ── the layer's Escape owns Clarity (item 13, as Track CL's B4) ──

    /// <summary>A bridge whose controller has a setup store of its own, holding one setup with JJ Clarity on.</summary>
    private sealed class LayerBench : IDisposable
    {
        public readonly Bridge B = new();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjf-track-j2-layer-" + Guid.NewGuid().ToString("N"));
        public LayerBench(bool withGate)
        {
            Directory.CreateDirectory(_dir);
            var store = new TxNoiseSetupStore(_dir);
            B.F.Radio.Serial = Radio8600;
            B.Noise.TransmitSetupStore = () => store;
            var setup = new TxNoiseSetup { Name = "Bright", Clarity = Bright() };
            if (withGate) setup.Gate = new TxNoiseGateSetup { Enabled = true, AutoThreshold = false, ThresholdDb = -40, RatioToOne = 3 };
            Assert.True(store.Save(Radio8600, setup).Succeeded);
        }
        public void Dispose()
        {
            B.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
        public bool ClarityOn => TxAudioConditioning.ClaritySetup.Enabled && B.Cond.Clarity.Enabled;
        public bool ClarityOff => !TxAudioConditioning.ClaritySetup.Enabled && !B.Cond.Clarity.Enabled;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_setup_with_clarity_applied_from_the_layer_is_put_back_by_its_Escape(bool withGateChange)
    {
        using var l = new LayerBench(withGateChange);
        var b = l.B;
        var snap = b.Noise.TakeSnapshot();
        b.Noise.ApplyTransmitSetup("Bright", Layer);
        b.Unkey();
        Assert.True(l.ClarityOn, "the setup did not switch JJ Clarity on: the test proves nothing");
        Assert.Equal(35f, TxAudioConditioning.ClaritySetup.HighBand.MixPercent);

        Assert.True(b.Noise.RestoreSnapshot(snap, Layer));
        b.Unkey();
        Assert.True(l.ClarityOff, "Escape left JJ Clarity on, which the layer's setup switched on");
        Assert.Equal(ClaritySettings.Default, TxAudioConditioning.ClaritySetup.ToSettings());
        Assert.Equal(ClaritySettings.Default, b.Cond.Clarity.Settings);
        Assert.False(b.Cond.Gate.Enabled);
        Assert.False(b.Noise.LastRestoreKeptOutsideChanges);
    }

    [Fact]
    public void A_change_made_at_home_to_clarity_after_the_layers_is_kept_and_another_stage_still_goes_back()
    {
        using var l = new LayerBench(withGate: true);
        var b = l.B;
        var snap = b.Noise.TakeSnapshot();
        b.Noise.ApplyTransmitSetup("Bright", Layer);
        b.Unkey();
        b.Noise.SetDynamics(Lever("high.mix"), 50, Home);   // somebody else, afterwards
        b.Unkey();
        b.Noise.RestoreSnapshot(snap, Layer);
        b.Unkey();
        Assert.True(l.ClarityOn);                                             // not the layer's to undo any more
        Assert.Equal(50f, TxAudioConditioning.ClaritySetup.HighBand.MixPercent);
        Assert.False(b.Cond.Gate.Enabled);                                    // the gate still goes back
        Assert.True(b.Noise.LastRestoreKeptOutsideChanges);
    }

    [Fact]
    public void The_layers_own_clarity_switch_goes_back_and_a_setup_still_held_for_the_unkey_is_withdrawn()
    {
        using var l = new LayerBench(withGate: false);
        var b = l.B;
        var snap = b.Noise.TakeSnapshot();
        b.Noise.SetClarityOn(true, Layer);
        b.Unkey();
        Assert.True(l.ClarityOn);
        Assert.True(b.Noise.RestoreSnapshot(snap, Layer));
        b.Unkey();
        Assert.True(l.ClarityOff);

        // Asked for while keyed, Escape before the unkey: it never lands.
        snap = b.Noise.TakeSnapshot();
        b.X.Keyed = true; b.X.Buffer();
        b.Noise.ApplyTransmitSetup("Bright", Layer);
        Assert.True(b.Noise.RestoreSnapshot(snap, Layer));
        b.Unkey();
        Assert.True(l.ClarityOff);
    }

    [Fact]
    public void A_preset_that_changes_only_clarity_says_so_and_the_whole_chain_summary_names_every_stage()
    {
        using var b = new Bridge();
        b.F.Spoken.Clear();
        // A preset whose only change is JJ Clarity says so, never "nothing to change"; applied again, it changes nothing.
        var preset = new AudioChainPreset { Name = "Clarity only", TransmitNoise = new TxNoiseSetup { Clarity = Bright() } };
        string said = b.Noise.ApplyNoiseSectionsForWorkshop(preset, "workshop");
        Assert.DoesNotContain(Lexicon.Get("audio.noise_surface.preset_nothing_changed"), said);
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.phrase_clarity"), said);
        b.Unkey();
        Assert.True(b.Cond.Clarity.Enabled);
        Assert.Contains(Lexicon.Get("audio.noise_surface.preset_nothing_changed"), b.Noise.ApplyNoiseSectionsForWorkshop(preset, "workshop"));
        // A preset with no Clarity section (one saved before it existed) switches it off, and says so too.
        string off = b.Noise.ApplyNoiseSectionsForWorkshop(new AudioChainPreset { Name = "Older", TransmitNoise = new TxNoiseSetup() }, "workshop");
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.phrase_clarity"), off);
        b.Unkey();
        Assert.False(b.Cond.Clarity.Enabled);
        // The summary with nothing on.
        Assert.Equal(Lexicon.Get("audio.noise_surface.tx.active.none"), b.Noise.TransmitActiveSummary());
        // Everything after the noise slots on: each is named, in the order it runs (#1313 and item 11).
        TxAudioConditioning.ApplySetup(new TxNoiseSetup
        {
            Gate = new TxNoiseGateSetup { Enabled = true },
            Eq = new ParametricEqSetup { Enabled = true, Bands = { new ParametricEqBandSetup { GainDb = 3 } } },
            DeEsser = new TxDeEsserSetup { Enabled = true },
            Clarity = Bright(),
            Compressor = new TxCompressorSetup { Enabled = true },
            Limiter = new TxLimiterSetup { Enabled = true, CeilingDb = -1 },
        });
        b.Unkey();
        string summary = b.Noise.TransmitActiveSummary();
        var wanted = new[]
        {
            Lexicon.Get("audio.noise_surface.tx.active.eq", ("bands", "1")),
            Lexicon.Get("audio.noise_surface.tx.active.deesser"),
            Lexicon.Get("audio.noise_surface.tx.active.clarity_both"),
            Lexicon.Get("audio.noise_surface.tx.active.compressor"),
            Lexicon.Get("audio.noise_surface.tx.active.limiter", ("ceiling", "-1")),
        };
        int at = -1;
        foreach (string item in wanted)
        {
            int found = summary.IndexOf(item, StringComparison.Ordinal);
            Assert.True(found > at, $"'{item}' is missing or out of order in: {summary}");
            at = found;
        }
        // The expander's strength, which the summary used to leave out (#1313): the sentence around its value is there.
        string strength = Lexicon.Get("audio.noise_surface.tx.active.expander_strength", ("strength", "@@"));
        Assert.Contains(strength[..strength.IndexOf("@@", StringComparison.Ordinal)], summary);
        // Which bands add: one sentence each way.
        b.Noise.SetClarityBandOn(false, false, Home);
        b.Unkey();
        Assert.Contains(Lexicon.Get("audio.noise_surface.tx.active.clarity_high"), b.Noise.TransmitActiveSummary());
        b.Noise.SetClarityOn(false, Home);
        b.Unkey();
        Assert.DoesNotContain("JJ Clarity", b.Noise.TransmitActiveSummary());
    }

    // ── the saved homes (J5): audioConfig.xml ──

    [Fact]
    public void The_live_states_home_round_trips_clarity_and_a_file_from_before_it_has_it_off()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjf-track-j2-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        bool settingWas = AudioOutputConfig.RadioProcessorOffWithCompressorInForce;
        try
        {
            var setup = new TxNoiseSetup { Gate = new TxNoiseGateSetup { Enabled = true }, Clarity = Bright() };
            new AudioOutputConfig { TransmitNoise = setup }.Save(dir);
            var back = AudioOutputConfig.Load(dir);
            Assert.True(back.TransmitNoise!.SameContentAs(setup));
            Assert.True(back.TransmitNoise.Clarity.Enabled);

            // The same file as a build before this track wrote it: no Clarity element, and that build's stage order.
            string path = Directory.GetFiles(dir, "*.xml").Single();
            var doc = System.Xml.Linq.XDocument.Load(path);
            doc.Descendants("Clarity").ToList().ForEach(x => x.Remove());
            doc.Descendants("StageOrder").Elements().Where(x => x.Value == "clarity").ToList().ForEach(x => x.Remove());
            doc.Save(path);
            Assert.DoesNotContain("larity", File.ReadAllText(path));
            var old = AudioOutputConfig.Load(dir);
            Assert.False(old.TransmitNoise!.Clarity.Enabled);
            Assert.True(old.TransmitNoise.Gate.Enabled);
            Assert.True(old.TransmitNoise.RunsDefaultOrder);
            Assert.Equal(TxAudioConditioner.StageIds.DefaultOrder, old.TransmitNoise.StageOrder);
        }
        finally
        {
            AudioOutputConfig.RadioProcessorOffWithCompressorInForce = settingWas;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ── the policy tick's edges and the take header (items 5 and 7) ──

    [Fact]
    public void The_policy_tick_sends_both_filter_edges_through_the_conditioners_one_owner_and_never_through_claritys_setters()
    {
        string owner = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "TxAudioConditioning.cs"));
        Assert.Contains("cond.SetClarityPassband(rig.TXFilterLow, rig.TXFilterHigh);", owner);
        // Clarity's own edge setters build and publish a design by themselves: a second writer. Nobody in the app uses them.
        foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot(), "JJFlexWpf"), "*.cs", SearchOption.AllDirectories)
                     .Concat(new[] { Path.Combine(RepoRoot(), "JJPortaudio", "JJPortaudio", "TxAudioConditioner.cs") }))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("Clarity.PassbandHighEdgeHz =", text);
            Assert.DoesNotContain("Clarity.PassbandLowEdgeHz =", text);
            Assert.DoesNotContain("Clarity.Use(ClarityDesign.Create", text);
        }
        // ApplyLive only sets the switch and hands over the record's own design.
        string cond = File.ReadAllText(Path.Combine(RepoRoot(), "JJPortaudio", "JJPortaudio", "TxAudioConditioner.cs"));
        Assert.Contains("Clarity.Enabled = live.ClarityEnabled;", cond);
        Assert.Contains("Clarity.Use(live.ClarityBuilt);", cond);
    }

    [Fact]
    public void The_take_headers_line_names_clarity_when_it_is_on_and_says_off_when_it_is_not()
    {
        using var b = new Bridge();
        string off = TxAudioConditioning.DescribePcChain();
        Assert.Contains("clarity=[off]", off);
        Assert.DoesNotContain("stages=clarity", off);
        TxAudioConditioning.ApplySetup(new TxNoiseSetup { Clarity = Bright() });
        b.Unkey();
        string on = TxAudioConditioning.DescribePcChain();
        Assert.Contains("stages=clarity;", on);
        Assert.Contains("clarity=[high band tune 1800 Hz, drive 15 dB, mix 35 percent, even harmonics; low band tune 120 Hz, drive 9 dB, mix 30 percent, both harmonics]", on);
        // The key itself is Track CL's; nothing new was added to the take's dictionary.
        string listenback = File.ReadAllText(Path.Combine(RepoRoot(), "Radios", "FlexBase.Listenback.cs"));
        Assert.Equal(1, listenback.Split("PcTransmitChain.DescribeNow()").Length - 1);
    }

    // ── the audition twin (J6) ──

    private static ChainAuditionTwin Twin(TxNoiseSetup setup, ChainAuditionTwin.Passband? passband = null, Func<AuditionLiveState?>? live = null) =>
        new(() => setup, live ?? (() => null), () => new DenoiseEngineCatalog(), TimeSpan.FromSeconds(5))
        {
            PassbandSource = () => passband ?? default,
        };

    private static float[] Through(TxAudioConditioner c, float[] mono)
    {
        var output = new float[mono.Length];
        var buf = new float[960];
        for (int at = 0; at < mono.Length; at += 480)
        {
            for (int f = 0; f < 480; f++) { float v = at + f < mono.Length ? mono[at + f] : 0f; buf[2 * f] = v; buf[2 * f + 1] = v; }
            c.Process(buf, 960, Rate);
            for (int f = 0; f < 480 && at + f < output.Length; f++) output[at + f] = buf[2 * f];
        }
        return output;
    }

    private static readonly ChainAuditionTwin.Passband Ssb = new(2900f, 100f, 2900f);

    [Fact]
    public void Clarity_alone_renders_and_is_named_and_with_everything_off_nothing_is_rendered()
    {
        var take = ChainAuditionTwinTests.Take(6);
        var r = Twin(new TxNoiseSetup { Clarity = Bright() }, Ssb).Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        Assert.True(r.Available, r.UnavailableKey);
        Assert.Equal(new[] { "clarity" }, r.Stages);
        Assert.Contains(Enumerable.Range(0, take.Length), i => r.Processed[i] != take[i]);
        Assert.Equal(0, r.LatencySamples);
        // The verdict names it, by a key of its own (DRAFT PROSE).
        var result = ChainAudition.Analyse((float[])take.Clone(), Rate, Twin(new TxNoiseSetup { Clarity = Bright() }, Ssb));
        Assert.Contains(Lexicon.Get("audio.audition.stage.clarity"), ChainAudition.Verdict(result));
        // The controls: off, and on with both bands off, are nothing on.
        Assert.True(Twin(new TxNoiseSetup { Clarity = Bright(on: false) }, Ssb).Process((float[])take.Clone(), Rate, CancellationToken.None, null).NothingOn);
        var idle = Bright(); idle.HighBand.Enabled = false; idle.LowBand.Enabled = false;
        Assert.True(Twin(new TxNoiseSetup { Clarity = idle }, Ssb).Process((float[])take.Clone(), Rate, CancellationToken.None, null).NothingOn);
    }

    [Theory]
    [InlineData(100f, 2900f)]
    [InlineData(300f, 2000f)]   // a narrow filter: the high band's tune is clamped
    public void The_twin_equals_a_live_shaped_conditioner_sample_for_sample_with_clarity_and_both_passband_edges(float low, float high)
    {
        var setup = new TxNoiseSetup
        {
            Gate = new TxNoiseGateSetup { Enabled = true, AutoThreshold = false, ThresholdDb = -40, RatioToOne = 2 },
            DeEsser = new TxDeEsserSetup { Enabled = true },
            Clarity = Bright(),
            Compressor = new TxCompressorSetup { Enabled = true, ThresholdDb = -24, RatioToOne = 4, MakeupDb = 9 },
            Limiter = new TxLimiterSetup { Enabled = true, CeilingDb = -1 },
        };
        var edges = new ChainAuditionTwin.Passband(high, low, high);
        var take = ChainAuditionTwinTests.Take(6);
        var r = Twin(setup, edges).Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        Assert.True(r.Available, r.UnavailableKey);
        Assert.Equal(new[] { "expander", "deesser", "clarity", "compressor", "limiter" }, r.Stages);

        float[] LiveShape(float lowEdge, float highEdge, float mix)
        {
            var c = new TxAudioConditioner { KeySource = () => true };
            c.SetClarityPassband(lowEdge, highEdge);
            var record = TxAudioConditioning.ToConditioner(setup);
            record.Clarity = new ClaritySettings(record.Clarity.High.WithMix(mix), record.Clarity.Low);
            c.SetGateInForce(record);
            c.DeEsser.PassbandHighEdgeHz = highEdge;
            return Through(c, (float[])take.Clone());
        }
        var live = LiveShape(low, high, 35f);
        for (int i = 0; i < take.Length; i++)
            if (r.Processed[i] != live[i]) Assert.Fail($"sample {i}: twin {r.Processed[i]:R}, live {live[i]:R}");
        // The comparison can see each thing it claims to cover: another mix, another high edge, another low edge.
        Assert.Contains(Enumerable.Range(0, take.Length), i => LiveShape(low, high, 45f)[i] != live[i]);
        var otherHigh = LiveShape(low, high - 400f, 35f);
        Assert.Contains(Enumerable.Range(0, take.Length), i => otherHigh[i] != live[i]);
        var otherLow = LiveShape(low + 600f, high, 35f);
        Assert.Contains(Enumerable.Range(0, take.Length), i => otherLow[i] != live[i]);
    }

    [Fact]
    public void What_was_removed_is_unchanged_by_clarity_being_on()
    {
        var take = ChainAuditionTwinTests.Take(6);
        TxNoiseSetup Setup(bool clarity) => new()
        {
            Gate = new TxNoiseGateSetup { Enabled = true, AutoThreshold = false, ThresholdDb = -40, RatioToOne = 2 },
            Clarity = Bright(on: clarity),
        };
        var without = ChainAudition.Analyse((float[])take.Clone(), Rate, Twin(Setup(false), Ssb));
        var with = ChainAudition.Analyse((float[])take.Clone(), Rate, Twin(Setup(true), Ssb));
        var removedWithout = without.Render(AuditionRender.Removed)!;
        var removedWith = with.Render(AuditionRender.Removed)!;
        Assert.Equal(removedWithout.Length, removedWith.Length);
        // The three renders share one playback scale, which Clarity can move; compared with each scale taken out.
        for (int i = 0; i < removedWith.Length; i++)
            Assert.Equal(removedWithout[i] / without.PlaybackScale, removedWith[i] / with.PlaybackScale, 5);
        // The control: what is SENT did change.
        var sentWithout = Twin(Setup(false), Ssb).Process((float[])take.Clone(), Rate, CancellationToken.None, null).Processed;
        var sentWith = Twin(Setup(true), Ssb).Process((float[])take.Clone(), Rate, CancellationToken.None, null).Processed;
        Assert.Contains(Enumerable.Range(0, take.Length), i => sentWith[i] != sentWithout[i]);
    }

    [Fact]
    public void The_48k_note_is_said_with_clarity_exactly_when_the_live_rate_is_not_48k()
    {
        var take = ChainAuditionTwinTests.Take(6);
        string VerdictAt(uint rate)
        {
            var live = ChainAuditionTwin.LiveState(true, -40, false, LufsMeter.Floor, 8, "USB") with { TransmitRateHz = rate };
            return ChainAudition.Verdict(ChainAudition.Analyse((float[])take.Clone(), Rate, Twin(new TxNoiseSetup { Clarity = Bright() }, Ssb, () => live)));
        }
        string note = Lexicon.Get("audio.audition.verdict.rendered_at_48k", ("rate", "16"));
        Assert.Contains(note, VerdictAt(16000));
        Assert.DoesNotContain(note, VerdictAt(48000));
        Assert.DoesNotContain(note, VerdictAt(0));
    }

    [Fact]
    public void Rendering_changes_nothing_live_or_saved()
    {
        using var b = new Bridge();
        TxAudioConditioning.ApplySetup(new TxNoiseSetup { Clarity = Bright(on: false) });
        b.Unkey();
        var before = TxAudioConditioning.CaptureSetup("");
        var inForce = b.Cond.GateInForce;
        var take = ChainAuditionTwinTests.Take(6);
        var renders = Twin(new TxNoiseSetup { Clarity = Bright() }, Ssb).PrepareClarity((float[])take.Clone(), Rate, CancellationToken.None, null, out var refusal);
        Assert.Null(refusal);
        renders!.Sent(true, Bright().ToSettings());
        renders.AtLimiterInput(true, Bright().ToSettings());
        Twin(new TxNoiseSetup { Clarity = Bright() }, Ssb).Process((float[])take.Clone(), Rate, CancellationToken.None, null);
        Assert.True(TxAudioConditioning.CaptureSetup("").SameContentAs(before));
        Assert.Same(inForce, b.Cond.GateInForce);
        Assert.False(b.Cond.Clarity.Enabled);
    }

    // ── the suggest pass's renders: the guard judges at the limiter's INPUT (item 6) ──

    [Fact]
    public void The_guards_render_is_the_chain_up_to_the_limiter_and_the_played_render_is_the_whole_chain()
    {
        var setup = new TxNoiseSetup
        {
            Clarity = Bright(),
            Compressor = new TxCompressorSetup { Enabled = true, ThresholdDb = -30, RatioToOne = 2, MakeupDb = 18 },
            Limiter = new TxLimiterSetup { Enabled = true, CeilingDb = -6 },
        };
        var take = ChainAuditionTwinTests.Take(6);
        var renders = Twin(setup, Ssb).PrepareClarity((float[])take.Clone(), Rate, CancellationToken.None, null, out var refusal)!;
        Assert.Null(refusal);
        Assert.True(renders.LimiterOn);
        Assert.Equal(-6.0, renders.LimiterCeilingDbfs!.Value, 3);
        var settings = Bright().ToSettings();
        float ceiling = (float)Math.Pow(10, -6 / 20.0);
        var sent = renders.Sent(true, settings);
        var atInput = renders.AtLimiterInput(true, settings);
        // What is SENT is held to the ceiling: judged here, the guard's tests could never fire.
        Assert.True(sent.Max(Math.Abs) <= ceiling + 1e-6f, $"sent peaks at {sent.Max(Math.Abs)}");
        // The limiter's input is over it: this is where Clarity's added energy shows.
        Assert.True(atInput.Max(Math.Abs) > ceiling * 1.2f, $"the limiter's input peaks at {atInput.Max(Math.Abs)}");
        // It is the same chain with the limiter out and the compressor in: the same as a live-shaped conditioner without a limiter.
        var c = new TxAudioConditioner { KeySource = () => true };
        c.SetClarityPassband(Ssb.ClarityLowEdgeHz, Ssb.ClarityHighEdgeHz);
        var record = TxAudioConditioning.ToConditioner(setup);
        record.Limiter = TxLimiterSettings.Default;
        c.SetGateInForce(record);
        var live = Through(c, (float[])take.Clone());
        for (int i = 0; i < take.Length; i++)
            if (atInput[i] != live[i]) Assert.Fail($"sample {i}: twin {atInput[i]:R}, live {live[i]:R}");
        // More Clarity raises the peak at the limiter's input, and the stage switched off is the same as settings that add nothing.
        var gentle = new ClaritySettings(settings.High.WithMix(5f), settings.Low.WithMix(5f));
        Assert.True(renders.AtLimiterInput(true, gentle).Max(Math.Abs) < atInput.Max(Math.Abs));
        var none = new ClaritySettings(settings.High.WithEnabled(false), settings.Low.WithEnabled(false));
        Assert.Equal(renders.AtLimiterInput(false, settings), renders.AtLimiterInput(true, none));
        // With the limiter off there is no ceiling to test against.
        setup.Limiter.Enabled = false;
        var noLimiter = Twin(setup, Ssb).PrepareClarity((float[])take.Clone(), Rate, CancellationToken.None, null, out _)!;
        Assert.Null(noLimiter.LimiterCeilingDbfs);
        // A take at another rate is refused, never rendered as if it were the chain's.
        Assert.Null(Twin(setup, Ssb).PrepareClarity(new float[16000], 16000, CancellationToken.None, null, out var wrongRate));
        Assert.Equal(ChainAuditionTwin.WrongRateKey, wrongRate!.UnavailableKey);
    }

    // ── the editor (item 9): every way out but Close puts back, field by field ──

    [Fact]
    public void Every_way_out_of_claritys_editor_but_Close_puts_back_what_it_changed()
    {
        using var b = new Bridge();
        const string editor = "clarity editor";
        var model = b.Noise.DynamicsEditor(editor);
        Assert.False(model.ClaritySetting.Enabled);

        // In the editor: JJ Clarity switched on, a preset loaded, a lever, a band's switch, its harmonics.
        b.Noise.SetClarityOn(true, editor);
        b.Noise.LoadClarityPreset(ClarityPresets.Fuller, editor);
        b.Noise.SetDynamics(Lever("high.mix"), 55, editor, speak: false);
        b.Noise.SetClarityHarmonics(false, TxClarityHarmonics.Odd, editor);
        b.Unkey();
        Assert.True(b.Cond.Clarity.Enabled);
        Assert.Equal(55f, model.ClaritySetting.HighBand.MixPercent);

        // Close keeps everything.
        Assert.False(TxDynamicsEditorDialog.Dismissed(model, kept: true));
        b.Unkey();
        Assert.True(b.Noise.ClarityOn);
        Assert.Equal(55f, TxAudioConditioning.ClaritySetup.HighBand.MixPercent);

        // The window closing any other way (Escape, the title bar's close, Alt+F4, Cancel) reaches the same closing
        // event, and that puts all of it back.
        var second = b.Noise.DynamicsEditor(editor);
        b.Noise.SetClarityOn(false, editor);
        b.Noise.SetDynamics(Lever("low.drive"), 20, editor, speak: false);
        b.Unkey();
        Assert.True(TxDynamicsEditorDialog.Dismissed(second, kept: false));
        b.Unkey();
        Assert.True(b.Noise.ClarityOn);                                              // back to what the first editor's Close left
        Assert.Equal(9f, TxAudioConditioning.ClaritySetup.LowBand.DriveDb);
        Assert.True(b.Cond.Clarity.Enabled);
        // And with nothing left to put back, a second closing does nothing.
        Assert.False(TxDynamicsEditorDialog.Dismissed(second, kept: false));
        // An untouched editor's exit changes nothing, whatever it shows.
        var third = b.Noise.DynamicsEditor(editor);
        Assert.False(TxDynamicsEditorDialog.Dismissed(third, kept: false));
        Assert.Equal(55f, TxAudioConditioning.ClaritySetup.HighBand.MixPercent);
    }

    [Fact]
    public void An_edit_made_keyed_and_cancelled_before_the_unkey_never_lands_and_the_same_edit_kept_does()
    {
        foreach (bool keep in new[] { false, true })
        {
            using var b = new Bridge();
            const string editor = "clarity editor";
            var model = b.Noise.DynamicsEditor(editor);
            b.X.Keyed = true; b.X.Buffer();
            b.Noise.SetClarityOn(true, editor);                                   // a switch: held for the unkey
            b.Noise.LoadClarityPreset(ClarityPresets.Presence, editor);
            Assert.False(b.Cond.Clarity.Enabled);
            TxDynamicsEditorDialog.Dismissed(model, kept: keep);
            b.Unkey();
            Assert.Equal(keep, b.Cond.Clarity.Enabled);
            Assert.Equal(keep, TxAudioConditioning.ClaritySetup.Enabled);
            Assert.Equal(keep ? ClarityPresets.Presence : "", TxAudioConditioning.ClaritySetup.PresetId);
        }
    }

    [Fact]
    public void Somebody_elses_change_to_clarity_is_never_the_editors_to_undo()
    {
        using var b = new Bridge();
        const string editor = "clarity editor";
        var model = b.Noise.DynamicsEditor(editor);
        b.Noise.SetDynamics(Lever("high.mix"), 40, editor, speak: false);     // the editor
        b.Noise.SetDynamics(Lever("low.mix"), 45, Home);                      // Home, another field
        b.Noise.SetDynamics(Lever("high.mix"), 50, Home);                     // Home, the editor's own field, afterwards
        b.Unkey();
        Assert.False(TxDynamicsEditorDialog.Dismissed(model, kept: false));   // nothing reads as the editor left it
        b.Unkey();
        Assert.Equal(45f, TxAudioConditioning.ClaritySetup.LowBand.MixPercent);
        Assert.Equal(50f, TxAudioConditioning.ClaritySetup.HighBand.MixPercent);
    }

    /// <summary>Every field of the saved Clarity class, each as a path the test can set.</summary>
    public static IEnumerable<object[]> ClarityFields() => new[]
    {
        "Enabled", "PresetId",
        "HighBand.Enabled", "HighBand.TuneHz", "HighBand.DriveDb", "HighBand.MixPercent", "HighBand.Harmonics",
        "LowBand.Enabled", "LowBand.TuneHz", "LowBand.DriveDb", "LowBand.MixPercent", "LowBand.Harmonics",
    }.Select(f => new object[] { f });

    private static (object Owner, PropertyInfo Property) Field(TxClaritySetup setup, string path)
    {
        object owner = setup;
        var parts = path.Split('.');
        for (int i = 0; i < parts.Length - 1; i++) owner = owner.GetType().GetProperty(parts[i])!.GetValue(owner)!;
        return (owner, owner.GetType().GetProperty(parts[^1])!);
    }

    private static object Changed(object? value) => value switch
    {
        bool flag => !flag,
        float number => number + 7f,
        string word => word == TxClarityHarmonics.Odd ? TxClarityHarmonics.Both : TxClarityHarmonics.Odd,
        _ => throw new InvalidOperationException("a kind of field this test does not know: " + value?.GetType().Name),
    };

    [Theory]
    [MemberData(nameof(ClarityFields))]
    public void Each_field_of_the_saved_clarity_class_is_noted_and_put_back_on_its_own(string path)
    {
        var floor = Bright(); var left = Bright(); var before = Bright(); var after = Bright();
        var (owner, property) = Field(after, path);
        object was = property.GetValue(owner)!;
        property.SetValue(owner, Changed(was));
        OwnedFields.Note(floor, left, before, after);
        Assert.True(OwnedFields.Differ(floor, left), path + " was changed and the walk noted nothing");
        var now = after.Clone();
        Assert.Equal(1, OwnedFields.TakeBack(floor, left, now));   // exactly that one field goes back
        var (nowOwner, nowProperty) = Field(now, path);
        Assert.Equal(was, nowProperty.GetValue(nowOwner));
        Assert.True(NoiseController.SameClarity(now, Bright()) && now.PresetId == Bright().PresetId);
    }

    [Fact]
    public void The_list_of_clarity_fields_is_every_read_write_property_the_walk_can_see()
    {
        // If a later build adds a field to the saved class, this fails until the list (and so the put-back's test) has it.
        static IEnumerable<string> Walk(Type type, string prefix) => type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite)
            .SelectMany(p => p.PropertyType.IsPrimitive || p.PropertyType == typeof(string)
                ? new[] { prefix + p.Name } : Walk(p.PropertyType, prefix + p.Name + "."));
        Assert.Equal(ClarityFields().Select(f => (string)f[0]).OrderBy(s => s), Walk(typeof(TxClaritySetup), "").OrderBy(s => s));
        // The immutable stage types have nothing the walk can see, which is why the saved class exists.
        Assert.DoesNotContain(typeof(ClarityBand).GetProperties(), p => p.CanWrite);
        Assert.DoesNotContain(typeof(ClaritySettings).GetProperties(), p => p.CanWrite);
    }

    [Fact]
    public void The_editors_window_shows_claritys_sections_on_the_same_closing_put_back()
    {
        // What a windowless test can reach of the window itself: its source. The harness walk presses the keys.
        string dialog = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "Dialogs", "TxDynamicsEditorDialog.cs"));
        Assert.Equal(1, dialog.Split("Closing += (_, _) => Dismissed(_model, _kept);").Length - 1);   // ONE put-back, for either set of sections
        Assert.True(dialog.IndexOf("Closing += (_, _) => Dismissed(_model, _kept);", StringComparison.Ordinal)
            < dialog.IndexOf("BuildClaritySections();", StringComparison.Ordinal));
        Assert.Equal(0, dialog.Split("_model.Restore()").Length - 1);
        Assert.Contains("_noise?.SayClarityNow();", dialog);
        string doors = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "MainWindow.Noise.cs"));
        Assert.Contains("new TxDynamicsEditorDialog(model, Noise, source, TxShapingEditorSections.Clarity)", doors);
        // The suggest pass's own window writes nothing until Keep, and stops and clears on closing, whichever way it closes.
        string suggest = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "Dialogs", "ClaritySuggestDialog.cs"));
        Assert.Contains("Closing += (_, _) => End();", suggest);
        Assert.Equal(1, suggest.Split("KeepClaritySuggestion(").Length - 1);
        Assert.DoesNotContain("SetClarity", suggest);
        Assert.DoesNotContain("LoadClarity", suggest);
    }

    // ── the suggest pass's flow (J7; item 14) ──

    private const int TakeRate = 8000;

    /// <summary>A synthetic off-air take shaped like the reference speech spectrum with its presence region moved; USB unless told otherwise. Generated here.</summary>
    private static string OffAirTake(string root, string mode = "USB", double presenceDb = -10, Action<Dictionary<string, string>>? more = null)
    {
        string directory = Path.Combine(root, "take-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int samples = TakeRate * 3;
        var hz = new[] { 200.0, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500 };
        string iq = Path.Combine(directory, "rf.iq");
        using (var writer = new BinaryWriter(File.Create(iq)))
            for (int i = 0; i < samples; i++)
            {
                double re = 0, im = 0;
                foreach (double nominal in hz)
                {
                    int band = Array.IndexOf(LongTermSpectrumMeter.NominalCentresHz, (int)nominal);
                    double f = LongTermSpectrumMeter.CentreHz(band);
                    double a = 0.05 * Math.Pow(10, (ReferenceSpeechSpectrum.BandLevelDb((int)nominal) - ReferenceSpeechSpectrum.BandLevelDb(500)) / 20)
                        * (nominal >= 1600 ? Math.Pow(10, presenceDb / 20) : 1);
                    re += a * Math.Cos(2 * Math.PI * f * i / TakeRate);
                    im += a * Math.Sin(2 * Math.PI * f * i / TakeRate);
                }
                writer.Write((float)(32768 * re)); writer.Write((float)(32768 * im));
            }
        using (var header = new TakeHeader(Path.Combine(directory, "take.txt")))
        {
            var facts = new Dictionary<string, string> {
                ["serial"] = "test-radio", ["mode"] = mode, ["rate-echoed"] = TakeRate.ToString(),
                ["receive-antenna"] = "XVT-B", ["rf-gain"] = "32", ["tx-antenna"] = "XVT-A", ["rf-power"] = "1",
                ["tx-frequency-mhz"] = "14.2", ["pan-centre-mhz"] = "14.2", ["tx-filter-low"] = "100", ["tx-filter-high"] = "2900",
                ["dummy-load"] = "True", ["mic-input"] = "PC", ["mic-profile"] = "Default", ["reference-audio"] = "True",
                ["processor"] = "True", ["processor-level"] = "50", ["compander"] = "False", ["compander-level"] = "30", ["mic-gain"] = "42",
                ["render-scale"] = "1/32768; fixed; no normalization; no AGC", ["classifier"] = "noise-like",
            };
            more?.Invoke(facts);
            foreach (var fact in facts) header.Write(fact.Key, fact.Value);
            header.Event("keying", new { Source = "radio-TransmitChange", Keyed = true, AtPair = 0 });
            header.Event("keying", new { Source = "radio-TransmitChange", Keyed = false, AtPair = samples });
            header.Write("pairs-written", samples);
            header.Write("sha256", TakeHeader.Checksum(iq));
            header.Write("closed", "clean");
        }
        return directory;
    }

    /// <summary>The operator's own voice, synthetic: tones in bursts (words and pauses), at a level the guard passes or does not.</summary>
    private static float[] Voice(double amplitude)
    {
        var mono = new float[Rate * 6];
        for (int i = 0; i < mono.Length; i++)
        {
            bool word = (i / (Rate / 2)) % 2 == 0;   // half a second on, half a second off
            double t = i / (double)Rate;
            mono[i] = (float)((word ? amplitude : amplitude * 0.003) * (0.6 * Math.Sin(2 * Math.PI * 1200 * t) + 0.35 * Math.Sin(2 * Math.PI * 700 * t)));
        }
        return mono;
    }

    private sealed class FlowBench : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "jjf-track-j2-flow-" + Guid.NewGuid().ToString("N"));
        public TxClaritySetup Current = new();
        public TxNoiseSetup Chain = new();
        public string? Take;
        public float[]? Retained = Voice(0.3);
        /// <summary>The live transmit side the twin reads; null is no radio.</summary>
        public AuditionLiveState? Live;
        public int Loads;
        public FlowBench() => Directory.CreateDirectory(Root);
        public ClaritySuggestFlow Flow() => new(new ClaritySuggestFlow.Sources
        {
            NewestOffAirTake = () => Take,
            Retained = () => Retained == null ? null : new ClaritySuggestFlow.RetainedTake(Path.Combine(Root, "take-2026-11-24-230000.wav"), ClaritySuggestFlow.RetainedKind.RecordingsTake),
            Load = (_, _) => { Loads++; return Retained == null ? null : ((float[])Retained.Clone(), Rate); },
            Current = () => Current,
            Twin = () => Twin(Chain, Ssb, () => Live),
        });
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void The_pass_measures_proposes_guards_and_renders_and_changes_nothing()
    {
        using var b = new Bridge();
        using var f = new FlowBench();
        f.Take = OffAirTake(f.Root);
        f.Current = TxAudioConditioning.CaptureClarity();   // off, a new Clarity's settings
        var before = TxAudioConditioning.CaptureSetup("");
        var inForce = b.Cond.GateInForce;

        var o = f.Flow().Run(CancellationToken.None, null);
        Assert.True(o.Result.Available);
        Assert.Equal(ClaritySuggestSource.OffAirTake, o.Result.Source);
        Assert.True(o.Result.GuardRan);
        Assert.True(o.CanKeep);
        Assert.Same(o.Result.Guard!.Proposal, o.Offer);          // the offer is the GUARDED proposal, never the raw one
        Assert.True(o.Offer!.ProposesBrightness);
        Assert.True(o.KeepSwitchesOn);                           // JJ Clarity is off now
        // The whole switch reached the analysis: with the stage off, both bands were read as adding nothing,
        // so the suggestion is counted up from zero, never from a stored mix that nobody is hearing.
        Assert.True(o.Result.Proposal!.Current.AddsNothing);
        Assert.InRange(o.Result.Proposal.Proposed.High.MixPercent, 38f, 42f);   // 4 percent per dB of a 10 dB shortfall, from zero
        Assert.Equal(f.Current.ToSettings().High.DriveDb, o.Offer.Proposed.High.DriveDb);   // the stored drive is still the floor
        Assert.NotNull(o.Pair);
        Assert.Equal(o.Pair!.Before.Length, o.Pair.After.Length);
        Assert.Contains(Enumerable.Range(0, o.Pair.Before.Length), i => o.Pair.Before[i] != o.Pair.After[i]);   // before and after differ
        Assert.Equal(ClaritySuggestFlow.RetainedKind.RecordingsTake, o.Retained);

        // Nothing was applied, staged or saved: Discard and Escape have nothing to undo.
        Assert.True(TxAudioConditioning.CaptureSetup("").SameContentAs(before));
        Assert.Same(inForce, b.Cond.GateInForce);
        Assert.False(b.Cond.HasStagedGate);
        Assert.False(b.X.Chain.HasDeferredChanges);

        // The report says, in order: what was measured, what the radio was doing, each band, and that nothing has changed.
        var report = ClaritySuggestFlow.Report(o);
        Assert.Equal(Lexicon.Get("audio.clarity_suggest.source_off_air"), report[0]);
        // Which take: its header names the radio by serial here, and no date, which is said as not recorded.
        Assert.Equal(Lexicon.Get("audio.clarity_suggest.measured_take", ("radio", "test-radio"), ("mode", "USB"),
            ("when", Lexicon.Get("audio.clarity_suggest.fact_unknown"))), report[1]);
        Assert.Equal(Lexicon.Get("audio.clarity_suggest.radio_was", ("processor", "True"), ("processor_level", "50"),
            ("compander", "False"), ("compander_level", "30"), ("mic_gain", "42")), report[2]);
        Assert.Contains(report, s => s.StartsWith("WORDING PLACEHOLDER: DRAFT PROSE: Brightness: your voice is", StringComparison.Ordinal) && s.Contains("suggested"));
        Assert.Contains(Lexicon.Get("audio.clarity_suggest.can_keep_switches_on"), report);
        Assert.Contains(Lexicon.Get("audio.clarity_suggest.retained_recording", ("file", "take-2026-11-24-230000")), report);
        Assert.Equal(Lexicon.Get("audio.clarity_suggest.changes_nothing"), report[^1]);
        Assert.DoesNotContain(report, s => s.Contains("clipping", StringComparison.OrdinalIgnoreCase) || s.Contains("free of", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Keep_writes_exactly_the_guarded_offer_and_switches_clarity_on_and_says_so()
    {
        using var b = new Bridge();
        using var f = new FlowBench();
        f.Take = OffAirTake(f.Root);
        f.Current = TxAudioConditioning.CaptureClarity();
        var o = f.Flow().Run(CancellationToken.None, null);
        Assert.True(o.CanKeep);
        b.F.Spoken.Clear();

        var timing = b.Noise.KeepClaritySuggestion(o.Offer!.Proposed, Dialogs.ClaritySuggestDialog.Source);
        Assert.Equal(TxChangeTiming.Now, timing.Timing);
        b.Unkey();
        var kept = TxAudioConditioning.ClaritySetup;
        Assert.True(kept.Enabled);                                   // it was off: Keep switched it on (item 14)
        Assert.Equal(o.Offer.Proposed, kept.ToSettings());           // exactly the offer
        Assert.True(b.Cond.Clarity.Enabled);
        Assert.Equal(o.Offer.Proposed, b.Cond.Clarity.Settings);
        // One utterance: kept and switched on, then the next step. (A second sentence on the same subject would cover the first unheard.)
        Assert.Single(b.F.Spoken.Said);
        Assert.Equal(Lexicon.Get("audio.clarity_suggest.kept_switched_on") + " " + Lexicon.Get("audio.clarity_suggest.next_step"), b.F.Spoken.Said[0].Text);
        Assert.Equal(Radios.Speech.SpeechSubject.ClaritySuggest, b.F.Spoken.Said[0].Subject);

        // Already on: the same Keep says it without "switched on".
        b.F.Spoken.Clear();
        b.Noise.KeepClaritySuggestion(o.Offer.Proposed, Dialogs.ClaritySuggestDialog.Source);
        Assert.StartsWith(Lexicon.Get("audio.clarity_suggest.kept") + " ", b.F.Spoken.Said[0].Text);
        Assert.DoesNotContain(Lexicon.Get("audio.clarity_suggest.kept_switched_on"), b.F.Spoken.Said[0].Text);

        // Keyed: Keep waits for the unkey, like every switch, and says so.
        b.Noise.SetClarityOn(false, Home);
        b.Unkey();
        b.X.Keyed = true; b.X.Buffer();
        b.F.Spoken.Clear();
        Assert.Equal(TxChangeTiming.AtUnkey, b.Noise.KeepClaritySuggestion(o.Offer.Proposed, Dialogs.ClaritySuggestDialog.Source).Timing);
        Assert.Contains(Lexicon.Get("audio.denoise.tx.at_unkey"), b.F.Spoken.Said[0].Text);   // and it says it waits
        b.X.Buffer();
        Assert.False(b.Cond.Clarity.Enabled);
        b.Unkey();
        Assert.True(b.Cond.Clarity.Enabled);
    }

    [Fact]
    public void Adjust_loads_the_offer_as_the_editors_own_change_and_every_exit_but_Close_takes_it_back()
    {
        using var b = new Bridge();
        using var f = new FlowBench();
        f.Take = OffAirTake(f.Root);
        f.Current = TxAudioConditioning.CaptureClarity();
        var offer = f.Flow().Run(CancellationToken.None, null).Offer!.Proposed;
        const string editor = "clarity editor";
        foreach (bool keep in new[] { false, true })
        {
            b.Noise.SetClarityOn(false, Home);
            TxAudioConditioning.SetClarityWhole(new TxClaritySetup());
            b.Unkey();
            var model = b.Noise.DynamicsEditor(editor);
            b.Noise.LoadClarityProposalIntoEditor(offer, editor);                 // what the suggest pass's Adjust does
            b.Noise.SetDynamics(Lever("high.drive"), 20, editor, speak: false);   // and an adjustment on top
            b.Unkey();
            Assert.True(b.Cond.Clarity.Enabled);
            Assert.Equal(offer.High.MixPercent, TxAudioConditioning.ClaritySetup.HighBand.MixPercent);
            TxDynamicsEditorDialog.Dismissed(model, kept: keep);
            b.Unkey();
            Assert.Equal(keep, TxAudioConditioning.ClaritySetup.Enabled);
            Assert.Equal(keep ? 20f : ClaritySettings.HighDriveDefaultDb, TxAudioConditioning.ClaritySetup.HighBand.DriveDb);
            Assert.Equal(keep ? offer.High.MixPercent : ClaritySettings.MixDefaultPercent, TxAudioConditioning.ClaritySetup.HighBand.MixPercent);
        }
    }

    [Fact]
    public void Without_a_recording_of_the_operators_voice_nothing_can_be_kept_and_the_pass_says_one_is_needed()
    {
        using var f = new FlowBench();
        f.Take = OffAirTake(f.Root);
        f.Retained = null;
        var o = f.Flow().Run(CancellationToken.None, null);
        Assert.True(o.Result.Available);
        Assert.False(o.Result.Proposal!.IsNothing);     // something IS proposed...
        Assert.False(o.Result.GuardRan);
        Assert.False(o.CanKeep);                        // ...and it cannot be kept: there is no Keep without the guard
        Assert.Null(o.Offer);
        Assert.Null(o.Pair);
        Assert.Contains(Lexicon.Get("audio.clarity_suggest.recording_needed"), ClaritySuggestFlow.Report(o));
        Assert.Throws<InvalidOperationException>(() => ClaritySuggestFlow.WriteRenders(o, f.Root));
    }

    [Fact]
    public void A_take_in_another_mode_and_no_take_at_all_are_refused_with_their_reasons()
    {
        using var f = new FlowBench();
        f.Take = OffAirTake(f.Root, mode: "AM");
        var am = f.Flow().Run(CancellationToken.None, null);
        Assert.False(am.Result.Available);
        Assert.Equal(ClaritySuggestRefusal.NoSpeechRender, am.Result.Refusal);
        Assert.False(am.CanKeep);
        Assert.Equal(new[] { Lexicon.Get("audio.clarity_suggest.refused.no_speech_render", ("mode", "AM")), Lexicon.Get("audio.clarity_suggest.changes_nothing") },
            ClaritySuggestFlow.Report(am));

        f.Take = null;
        var none = f.Flow().Run(CancellationToken.None, null);
        Assert.Equal(ClaritySuggestRefusal.NoTake, none.Result.Refusal);
        Assert.Equal(Lexicon.Get("audio.clarity_suggest.refused.no_take"), ClaritySuggestFlow.Report(none)[0]);
        // A microphone recording never stands in for the off-air take: the retained take was there, and was not measured.
        Assert.False(none.CanKeep);
    }

    [Fact]
    public void A_voice_that_already_has_presence_gets_nothing_and_that_is_said_as_a_result()
    {
        using var f = new FlowBench();
        f.Take = OffAirTake(f.Root, presenceDb: 0);
        var o = f.Flow().Run(CancellationToken.None, null);
        Assert.True(o.Result.Available);
        Assert.True(o.Result.Proposal!.IsNothing);
        Assert.False(o.CanKeep);
        var report = ClaritySuggestFlow.Report(o);
        Assert.Contains(Lexicon.Get("audio.clarity_suggest.nothing_needed"), report);
        Assert.DoesNotContain(Lexicon.Get("audio.clarity_suggest.recording_needed"), report);
        // Nothing to hear, so no sentence about which recording before and after were made from.
        Assert.DoesNotContain(report, s => s.Contains("take-2026-11-24-230000", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hot_voice_makes_the_guard_withdraw_at_the_limiters_input_where_the_chains_output_would_have_shown_nothing()
    {
        using var f = new FlowBench();
        f.Take = OffAirTake(f.Root);
        // The operator's audio already goes past the limiter's ceiling before it: anything Clarity adds makes more.
        f.Retained = Voice(1.3);
        f.Chain = new TxNoiseSetup { Limiter = new TxLimiterSetup { Enabled = true } };
        var o = f.Flow().Run(CancellationToken.None, null);
        Assert.True(o.Result.GuardRan);
        Assert.Equal(0.0, o.Result.Guard!.CeilingDbfs!.Value, 3);   // the limiter's ceiling is the ceiling it tested against
        Assert.True(o.Result.Guard.Reduced);
        Assert.True(o.Result.Guard.Withdrawn);
        Assert.False(o.CanKeep);                                    // withdrawn: no Keep
        var report = ClaritySuggestFlow.Report(o);
        Assert.Contains(Lexicon.Get("audio.clarity_suggest.guard_withdrawn"), report);
        // Overs that were there before are said, never called fixed.
        Assert.True(o.Result.Guard.OverFullScaleBefore > 0);
        Assert.Contains(report, s => s.StartsWith("WORDING PLACEHOLDER: DRAFT PROSE: Your audio already goes past full scale", StringComparison.Ordinal));
        // The control: judged at the chain's OUTPUT, the limiter holds every render to the ceiling and the guard sees nothing to refuse.
        var renders = Twin(f.Chain, Ssb).PrepareClarity(Voice(1.3), Rate, CancellationToken.None, null, out _)!;
        var raw = o.Result.Proposal!;
        Assert.True(ClarityPeakGuard.CountOver(renders.Sent(true, raw.Proposed)) == 0);
        Assert.True(ClarityPeakGuard.PeakDbfs(renders.Sent(true, raw.Proposed)) <= 0.0);
        // At the limiter's INPUT the same proposal raises the peak past the ceiling and past what was there before: what the guard refused.
        Assert.True(ClarityPeakGuard.PeakDbfs(renders.AtLimiterInput(true, raw.Proposed)) > ClarityPeakGuard.PeakDbfs(renders.AtLimiterInput(false, raw.Current)));
        Assert.True(ClarityPeakGuard.PeakDbfs(renders.AtLimiterInput(false, raw.Current)) > 0.0);
    }

    [Fact]
    public void With_clarity_on_the_pass_adds_to_what_is_set_and_keeping_does_not_say_it_switched_on()
    {
        using var f = new FlowBench();
        f.Take = OffAirTake(f.Root);
        f.Current = Bright();
        f.Current.HighBand.MixPercent = 20;
        f.Chain = new TxNoiseSetup { Clarity = f.Current.Clone() };
        var o = f.Flow().Run(CancellationToken.None, null);
        Assert.True(o.CanKeep);
        Assert.False(o.KeepSwitchesOn);
        Assert.True(o.Offer!.Proposed.High.MixPercent > 20f);                  // more, never less
        Assert.Equal(f.Current.HighBand.DriveDb, o.Offer.Proposed.High.DriveDb);   // the stored drive is kept
        Assert.Equal(f.Current.ToSettings().Low, o.Offer.Proposed.Low);        // a band it did not propose is left exactly
        Assert.Contains(Lexicon.Get("audio.clarity_suggest.can_keep"), ClaritySuggestFlow.Report(o));
    }

    [Fact]
    public void The_retained_take_is_the_auditions_take_never_a_render_of_it_then_the_newest_kept_take()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjf-track-j2-retained-" + Guid.NewGuid().ToString("N"));
        string audition = Path.Combine(root, "audition"), recordings = Path.Combine(root, "recordings");
        try
        {
            Directory.CreateDirectory(audition); Directory.CreateDirectory(recordings);
            Assert.Null(ClaritySuggestFlow.FindRetained(audition, recordings));
            Assert.Null(ClaritySuggestFlow.FindRetained(Path.Combine(root, "missing"), ""));
            void Touch(string path, int minutesAgo) { File.WriteAllBytes(path, new byte[1]); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo)); }
            Touch(Path.Combine(recordings, "take-2026-11-24-100000.wav"), 60);
            Touch(Path.Combine(recordings, "take-2026-11-24-110000.wav"), 30);
            Touch(Path.Combine(recordings, "My CQ.wav"), 1);   // a voice message: not a take
            var kept = ClaritySuggestFlow.FindRetained(audition, recordings)!;
            Assert.Equal(ClaritySuggestFlow.RetainedKind.RecordingsTake, kept.Kind);
            Assert.EndsWith("take-2026-11-24-110000.wav", kept.Path);
            // The audition's own take wins while Check your audio holds one; its three renders are never taken for it.
            Touch(Path.Combine(audition, "audition-2026-11-24-120000-000.wav"), 20);
            foreach (string render in new[] { "cleaned", "raw", "removed" })
                Touch(Path.Combine(audition, "audition-2026-11-24-120000-000-" + render + ".wav"), 5);
            var live = ClaritySuggestFlow.FindRetained(audition, recordings)!;
            Assert.Equal(ClaritySuggestFlow.RetainedKind.AuditionTake, live.Kind);
            Assert.EndsWith("audition-2026-11-24-120000-000.wav", live.Path);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void The_two_renders_are_written_for_playing_into_the_pass_own_folder()
    {
        using var f = new FlowBench();
        f.Take = OffAirTake(f.Root);
        var o = f.Flow().Run(CancellationToken.None, null);
        string folder = Path.Combine(f.Root, "renders");
        var (before, after) = ClaritySuggestFlow.WriteRenders(o, folder);
        Assert.True(File.Exists(before) && File.Exists(after));
        Assert.Equal(folder, Path.GetDirectoryName(before));
        Assert.True(new FileInfo(before).Length > 44 && new FileInfo(before).Length == new FileInfo(after).Length);
        Assert.Equal(2, ChainAuditionFolder.Clear(folder));   // the window empties it on opening and on closing
    }

    [Fact]
    public void The_pass_and_its_window_never_call_anything_that_keys_records_or_makes_a_take()
    {
        // A source pin: the suggest pass reads recordings already made. Nothing in its flow, its window or the
        // controller's Clarity rule book reaches the transmit key, a tune carrier, a tone, a recorder or a new take.
        string[] forbidden =
        {
            ".Transmit =", "Mox", "PttSafety", "StartListenback", "ToggleTuneCarrier", "TxToneStart", "TxFilePlayer", "SendRecording",
            "RecordingNarrator", "MicRecorder", "MicProbe", "StartCheck", "ShowOrFocusAndStartCheck", "TransmitKey", "AudioCheck.Start",
        };
        foreach (string file in new[] { "ClaritySuggestFlow.cs", Path.Combine("Dialogs", "ClaritySuggestDialog.cs"), "NoiseController.Clarity.cs" })
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", file));
            foreach (string word in forbidden)
                Assert.False(text.Contains(word, StringComparison.Ordinal), file + " names " + word);
        }
        // The control: the same scan sees those names where they really are.
        string check = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"));
        Assert.Contains(forbidden, word => check.Contains(word, StringComparison.Ordinal));
    }

    // ── Command Finder (item 12) ──

    [Fact]
    public void A_search_for_clarity_finds_jj_claritys_rows_and_enhanced_signal_clarity_each_saying_which_it_is()
    {
        var rows = KeyInventory.CommandFinderItems()
            .Where(r => r.Keywords != null && r.Keywords.Contains("clarity")).ToList();
        var jj = rows.Where(r => r.Keywords!.Contains("jj")).ToList();
        var esc = rows.Where(r => r.Keywords!.Contains("enhanced")).ToList();
        Assert.Equal(4, jj.Count);
        Assert.Single(esc);
        Assert.All(rows, r => Assert.StartsWith("WORDING PLACEHOLDER: DRAFT PROSE:", r.Description));   // never real English here
        Assert.All(jj, r => Assert.Contains("JJ Clarity", r.Description));
        Assert.All(jj, r => Assert.Equal("Transmit menu, JJ Clarity (also Home, Transmission)", r.MenuText));
        Assert.Contains("Enhanced Signal Clarity", esc[0].Description);
        Assert.Contains("not JJ Clarity", esc[0].Description);
        // Doors, not keys: no chord is bound for JJ Clarity (the chords are proposed, and wait for a ruling).
        Assert.All(rows, r => Assert.True(string.IsNullOrEmpty(r.KeyDisplay)));
        Assert.DoesNotContain(KeyInventory.LayerCommands(KeyInventory.NoiseLayerContext),
            r => r.Description.Contains("Clarity", StringComparison.OrdinalIgnoreCase));
    }

    // ════════════════════════════════════════════════════════════════════
    //  Repair 1
    // ════════════════════════════════════════════════════════════════════

    // ── B1: the newest request wins, and a toggle or a "next" row steps from it ──

    private static string AtUnkey => Lexicon.Get("audio.denoise.tx.at_unkey");

    private static int WaitingEdits() => TxAudioConditioning.WaitingShapingEdits;

    /// <summary>One on-or-off switch of the three shaping stages, as the controller offers it: its toggle, its explicit set, and how it reads once it has run.</summary>
    private sealed record ShapingSwitch(string Name, Action<NoiseController> Toggle, Action<NoiseController, bool> Set, Func<Bridge, bool> Ran);

    private static ShapingSwitch SwitchOf(string name) => name switch
    {
        "clarity" => new(name, n => n.ToggleClarity(Home), (n, on) => n.SetClarityOn(on, Home), b => b.Noise.ClarityOn && b.Cond.Clarity.Enabled),
        "clarity high band" => new(name, n => n.ToggleClarityBand(true, Home), (n, on) => n.SetClarityBandOn(true, on, Home),
            b => b.Noise.ClarityBandOn(true) && b.Cond.Clarity.Settings.High.Enabled),
        "clarity low band" => new(name, n => n.ToggleClarityBand(false, Home), (n, on) => n.SetClarityBandOn(false, on, Home),
            b => b.Noise.ClarityBandOn(false) && b.Cond.Clarity.Settings.Low.Enabled),
        "compressor" => new(name, n => n.ToggleCompressor(Home), (n, on) => n.SetCompressorOn(on, Home), b => b.Noise.CompressorOn && b.Cond.Compressor.Settings.Enabled),
        "limiter" => new(name, n => n.ToggleLimiter(Home), (n, on) => n.SetLimiterOn(on, Home), b => b.Noise.LimiterOn && b.Cond.Limiter.Settings.Enabled),
        // The compressor's mode has no toggle of its own; an explicit pair is its test.
        "compressor split band" => new(name, n => n.SetCompressorSplitBand(!TxCompressorModes.IsSplit(TxAudioConditioning.AskedShaping().Compressor.CompressorMode), Home),
            (n, on) => n.SetCompressorSplitBand(on, Home), b => b.Noise.CompressorSplitBand && b.Cond.Compressor.Settings.SplitBand),
        _ => throw new ArgumentException(name),
    };

    public static IEnumerable<object[]> Switches() =>
        from name in new[] { "clarity", "clarity high band", "clarity low band", "compressor", "limiter", "compressor split band" }
        from initial in new[] { false, true }
        select new object[] { name, initial };

    private static Bridge BridgeWith(ShapingSwitch s, bool initial)
    {
        var b = new Bridge();
        try
        {
            s.Set(b.Noise, initial);
            b.Unkey();
            Assert.Equal(initial, s.Ran(b));
            b.X.Keyed = true; b.X.Buffer();
            b.F.Spoken.Clear();
            return b;
        }
        catch { b.Dispose(); throw; }
    }

    [Theory]
    [MemberData(nameof(Switches))]
    public void Two_presses_of_a_toggle_while_keyed_end_where_they_began_and_three_end_the_other_way(string name, bool initial)
    {
        var s = SwitchOf(name);
        using (var b = BridgeWith(s, initial))
        {
            s.Toggle(b.Noise);
            Assert.Equal(initial, s.Ran(b));                 // held: nothing has run
            s.Toggle(b.Noise);
            b.Unkey();
            Assert.Equal(initial, s.Ran(b));                 // two presses: back where it began
            Assert.Equal(0, WaitingEdits());
        }
        using (var b = BridgeWith(s, initial))
        {
            s.Toggle(b.Noise); s.Toggle(b.Noise); s.Toggle(b.Noise);
            b.Unkey();
            Assert.Equal(!initial, s.Ran(b));
        }
        // The control: one press does change it, so the two-press result is the second press undoing the first.
        using (var b = BridgeWith(s, initial))
        {
            s.Toggle(b.Noise);
            b.Unkey();
            Assert.Equal(!initial, s.Ran(b));
        }
    }

    [Theory]
    [MemberData(nameof(Switches))]
    public void An_explicit_request_made_after_one_still_waiting_is_queued_behind_it_and_wins(string name, bool initial)
    {
        var s = SwitchOf(name);
        using (var b = BridgeWith(s, initial))
        {
            s.Set(b.Noise, !initial);                        // waits for the unkey
            s.Set(b.Noise, initial);                         // the newest request: it must be queued, not dropped as "already so"
            Assert.Contains(AtUnkey, b.F.Spoken.Said[^1].Text);
            b.Unkey();
            Assert.Equal(initial, s.Ran(b));
        }
        // Asking again for what is already the newest request queues nothing more, and still says it waits.
        using (var b = BridgeWith(s, initial))
        {
            s.Set(b.Noise, !initial);
            int waiting = WaitingEdits();
            Assert.Equal(1, waiting);
            s.Set(b.Noise, !initial);
            Assert.Equal(waiting, WaitingEdits());
            Assert.Contains(AtUnkey, b.F.Spoken.Said[^1].Text);
            b.Unkey();
            Assert.Equal(!initial, s.Ran(b));
        }
        // And unkeyed, asking for what is in place says nothing about waiting.
        using (var b = BridgeWith(s, initial))
        {
            b.Unkey();
            b.F.Spoken.Clear();
            s.Set(b.Noise, initial);
            Assert.DoesNotContain(AtUnkey, b.F.Spoken.Said[^1].Text);
            Assert.Equal(0, WaitingEdits());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_next_harmonics_row_pressed_twice_while_keyed_moves_two_on_and_an_explicit_choice_after_a_waiting_one_wins(bool high)
    {
        string start = high ? TxClarityHarmonics.Even : TxClarityHarmonics.Both;   // a new Clarity's own
        var all = TxClarityHarmonics.All.ToList();
        string Then(int presses) => all[(all.IndexOf(start) + presses) % all.Count];
        string Ran(Bridge b) => b.Noise.ClarityHarmonicsOf(high);
        using (var b = new Bridge())
        {
            Assert.Equal(start, Ran(b));
            b.X.Keyed = true; b.X.Buffer();
            b.Noise.CycleClarityHarmonics(high, Home);
            b.Noise.CycleClarityHarmonics(high, Home);
            Assert.Equal(start, Ran(b));                     // held
            b.Unkey();
            Assert.Equal(Then(2), Ran(b));                   // two on, not one
            Assert.Equal(TxClarityHarmonics.ToChoice(Then(2)), (high ? b.Cond.Clarity.Settings.High : b.Cond.Clarity.Settings.Low).Harmonics);
        }
        using (var b = new Bridge())
        {
            b.X.Keyed = true; b.X.Buffer();
            for (int i = 0; i < all.Count; i++) b.Noise.CycleClarityHarmonics(high, Home);
            b.Unkey();
            Assert.Equal(start, Ran(b));                     // all the way round
        }
        using (var b = new Bridge())
        {
            b.X.Keyed = true; b.X.Buffer();
            b.Noise.SetClarityHarmonics(high, Then(1), Home);
            b.Noise.SetClarityHarmonics(high, start, Home);  // back to where it is: queued behind the first, not dropped
            Assert.Contains(AtUnkey, b.F.Spoken.Said[^1].Text);
            b.Unkey();
            Assert.Equal(start, Ran(b));
        }
    }

    [Fact]
    public void A_toggle_and_a_next_row_pressed_after_a_waiting_preset_step_from_the_presets_values()
    {
        using var b = new Bridge();
        // In place: the low band off, the high band's harmonics even. The preset "wide" has the low band on and the high band odd.
        Assert.False(b.Noise.ClarityBandOn(false));
        Assert.Equal(TxClarityHarmonics.Even, b.Noise.ClarityHarmonicsOf(true));
        b.X.Keyed = true; b.X.Buffer();
        b.Noise.LoadClarityPreset(ClarityPresets.Wide, Home);             // waits
        b.Noise.ToggleClarityBand(false, Home);                           // from the preset's ON: off
        b.Noise.CycleClarityHarmonics(true, Home);                        // from the preset's odd: both
        Assert.Contains(Lexicon.Get("audio.clarity.band_off", ("band", NoiseController.ClarityBandName(false))), b.F.Spoken.Said[^2].Text);
        Assert.Contains(Lexicon.Get("audio.clarity.harmonics_set", ("band", NoiseController.ClarityBandName(true)),
            ("harmonics", NoiseController.ClarityHarmonicsName(TxClarityHarmonics.Both))), b.F.Spoken.Said[^1].Text);
        b.Unkey();
        var now = TxAudioConditioning.ClaritySetup;
        Assert.False(now.LowBand.Enabled);                                // what was said is what landed
        Assert.Equal(TxClarityHarmonics.Both, now.HighBand.Harmonics);
        Assert.Equal((3000f, 9f, 30f), (now.HighBand.TuneHz, now.HighBand.DriveDb, now.HighBand.MixPercent));   // the preset did land, under them
        Assert.Equal(120f, now.LowBand.TuneHz);
        Assert.Equal(now.ToSettings(), b.Cond.Clarity.Settings);
    }

    [Fact]
    public void A_toggle_pressed_after_a_waiting_whole_setup_steps_from_the_setups_value_on_every_whole_road()
    {
        // The controller's road (a radio's named setup): JJ Clarity off in place, on in the setup.
        using (var l = new LayerBench(withGate: false))
        {
            var b = l.B;
            b.X.Keyed = true; b.X.Buffer();
            b.Noise.ApplyTransmitSetup("Bright", Home);                   // waits; it switches JJ Clarity on
            b.Noise.ToggleClarity(Home);                                  // from the setup's ON: off
            Assert.Contains(Lexicon.Get("audio.clarity.off"), b.F.Spoken.Said[^1].Text);
            b.Unkey();
            Assert.False(b.Noise.ClarityOn);
            Assert.False(b.Cond.Clarity.Enabled);
            Assert.Equal(35f, TxAudioConditioning.ClaritySetup.HighBand.MixPercent);   // the setup's bands did land
            Assert.Equal(0, WaitingEdits());
        }
        // The owner's road (a microphone's setup, the old settings shape).
        using (var b = new Bridge())
        {
            b.X.Keyed = true; b.X.Buffer();
            TxAudioConditioning.ApplySetup(new TxNoiseSetup { Clarity = Bright(), Compressor = new TxCompressorSetup { Enabled = true } });
            b.Noise.ToggleClarity(Home);
            b.Noise.ToggleCompressor(Home);
            b.Unkey();
            Assert.False(b.Noise.ClarityOn);
            Assert.False(b.Noise.CompressorOn);
            Assert.Equal(35f, TxAudioConditioning.ClaritySetup.HighBand.MixPercent);
        }
        // The recommended state: JJ Clarity off, the limiter on as a new setup has it.
        using (var b = new Bridge())
        {
            b.Noise.SetClarityOn(true, Home);
            b.Noise.SetLimiterOn(false, Home);
            b.Unkey();
            b.X.Keyed = true; b.X.Buffer();
            TxAudioConditioning.ResetToRecommended();
            b.Noise.ToggleClarity(Home);                                  // from the recommended OFF: on
            b.Noise.ToggleLimiter(Home);                                  // from the recommended ON: off
            b.Unkey();
            Assert.True(b.Noise.ClarityOn);
            Assert.False(b.Noise.LimiterOn);
        }
    }

    [Fact]
    public void A_request_its_owner_withdrew_is_not_counted_and_a_waiting_put_back_is()
    {
        const string editor = "clarity editor";
        // The editor asks for ON while keyed and is cancelled before the unkey: its request is gone, so a toggle
        // pressed next steps from OFF, where JJ Clarity is and will stay.
        using (var b = new Bridge())
        {
            var model = b.Noise.DynamicsEditor(editor);
            b.X.Keyed = true; b.X.Buffer();
            b.Noise.SetLimiterOn(true, Home);                                  // somebody else's, waiting first: it stays held
            b.Noise.SetClarityOn(true, editor);
            Assert.True(TxAudioConditioning.AskedShaping().Clarity.Enabled);   // counted while it waits
            Assert.Equal(2, WaitingEdits());
            TxDynamicsEditorDialog.Dismissed(model, kept: false);
            Assert.True(b.X.Chain.HasDeferredChanges);                         // Home's request is still held
            Assert.False(TxAudioConditioning.AskedShaping().Clarity.Enabled);  // the editor's was withdrawn: not counted
            Assert.True(TxAudioConditioning.AskedShaping().Limiter.Enabled);   // Home's still is
            Assert.Equal(1, WaitingEdits());
            b.Noise.ToggleClarity(Home);
            Assert.Contains(Lexicon.Get("audio.clarity.on"), b.F.Spoken.Said[^1].Text);
            b.Unkey();
            Assert.True(b.Noise.ClarityOn);
            Assert.True(b.Noise.LimiterOn);
            Assert.Equal(0, WaitingEdits());
        }
        // The editor's change LANDED, and it is cancelled while keyed: the put-back waits, and what it will take back
        // counts as asked for.
        using (var b = new Bridge())
        {
            var model = b.Noise.DynamicsEditor(editor);
            b.Noise.SetClarityOn(true, editor);
            b.Noise.SetCompressorOn(true, editor);
            b.Unkey();
            Assert.True(b.Noise.ClarityOn);
            b.X.Keyed = true; b.X.Buffer();
            Assert.True(TxDynamicsEditorDialog.Dismissed(model, kept: false));
            Assert.True(b.Noise.ClarityOn);                                    // not back yet
            Assert.False(TxAudioConditioning.AskedShaping().Clarity.Enabled);
            Assert.False(TxAudioConditioning.AskedShaping().Compressor.Enabled);
            b.Noise.ToggleClarity(Home);                                       // from OFF, where it is going: on
            Assert.Contains(Lexicon.Get("audio.clarity.on"), b.F.Spoken.Said[^1].Text);
            b.Unkey();
            Assert.True(b.Noise.ClarityOn);
            Assert.False(b.Noise.CompressorOn);                                // the put-back still took the compressor back
        }
        // The noise layer's Escape made while keyed: its put-back waits the same way.
        using (var l = new LayerBench(withGate: false))
        {
            var b = l.B;
            var snap = b.Noise.TakeSnapshot();
            b.Noise.ApplyTransmitSetup("Bright", Layer);
            b.Unkey();
            Assert.True(l.ClarityOn);
            b.X.Keyed = true; b.X.Buffer();
            Assert.True(b.Noise.RestoreSnapshot(snap, Layer));
            Assert.True(b.Noise.ClarityOn);                                    // not back yet
            Assert.False(TxAudioConditioning.AskedShaping().Clarity.Enabled);
            b.Noise.ToggleClarity(Home);
            Assert.Contains(Lexicon.Get("audio.clarity.on"), b.F.Spoken.Said[^1].Text);
            b.Unkey();
            Assert.True(b.Noise.ClarityOn);
            Assert.Equal(ClaritySettings.Default, TxAudioConditioning.ClaritySetup.ToSettings());   // the bands did go back
            Assert.Equal(0, WaitingEdits());
        }
    }

    [Fact]
    public void A_lever_stepped_twice_behind_a_waiting_switch_moves_two_steps()
    {
        using var b = new Bridge();
        var mix = Lever("high.mix");
        var threshold = NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "threshold")!;
        double mixWas = b.Noise.DynamicsNumber(mix), thresholdWas = b.Noise.DynamicsNumber(threshold);
        b.X.Keyed = true; b.X.Buffer();
        // Nothing held: a lever is live, and each step starts from the last (the control).
        b.Noise.AdjustDynamics(mix, +1, Home);
        b.Noise.AdjustDynamics(mix, +1, Home);
        Assert.Equal(mixWas + 2 * mix.Step, b.Noise.DynamicsNumber(mix));
        // A switch is waiting: the levers wait behind it, and the second step starts from the first.
        b.Noise.SetClarityOn(true, Home);
        b.Noise.AdjustDynamics(mix, +1, Home);
        b.Noise.AdjustDynamics(mix, +1, Home);
        b.Noise.AdjustDynamics(threshold, -1, Home);
        b.Noise.AdjustDynamics(threshold, -1, Home);
        Assert.Equal(mixWas + 2 * mix.Step, b.Noise.DynamicsNumber(mix));       // held
        b.Unkey();
        Assert.Equal(mixWas + 4 * mix.Step, b.Noise.DynamicsNumber(mix));
        Assert.Equal(thresholdWas - 2 * threshold.Step, b.Noise.DynamicsNumber(threshold));
        Assert.Equal((float)(mixWas + 4 * mix.Step), b.Cond.Clarity.Settings.High.MixPercent);
        Assert.Equal(0, WaitingEdits());
    }

    [Fact]
    public void With_nothing_waiting_what_was_asked_for_is_what_is_in_place_and_reading_it_changes_nothing()
    {
        using var b = new Bridge();
        b.Noise.SetClarityOn(true, Home);
        b.Noise.SetCompressorOn(true, Home);
        b.Unkey();
        var before = TxAudioConditioning.CaptureSetup("");
        var asked = TxAudioConditioning.AskedShaping();
        Assert.True(new TxNoiseSetup { Compressor = asked.Compressor, Limiter = asked.Limiter, Clarity = asked.Clarity }
            .SameContentAs(new TxNoiseSetup { Compressor = before.Compressor, Limiter = before.Limiter, Clarity = before.Clarity }));
        // A copy: changing it changes nothing.
        asked.Clarity.Enabled = false;
        asked.Compressor.Enabled = false;
        Assert.True(TxAudioConditioning.CaptureSetup("").SameContentAs(before));
        // While keyed, a waiting request shows in what was asked for and not in what is in place.
        b.X.Keyed = true; b.X.Buffer();
        b.Noise.SetClarityOn(false, Home);
        Assert.False(TxAudioConditioning.AskedShaping().Clarity.Enabled);
        Assert.True(TxAudioConditioning.ClaritySetup.Enabled);
        Assert.True(TxAudioConditioning.CaptureSetup("").SameContentAs(before));
        b.Unkey();
        Assert.False(TxAudioConditioning.ClaritySetup.Enabled);
        Assert.Equal(0, WaitingEdits());
    }

    [Fact]
    public void A_change_held_by_another_chain_is_not_counted_and_one_held_by_this_sessions_chain_is()
    {
        using var b = new Bridge();
        var other = new TxNoiseChain(TxNoiseChainTests.Catalog()) { KeySource = () => true };
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.Ask(other, lever: false, () => { }, s => s.Clarity.Enabled = true).Timing);
        Assert.False(TxAudioConditioning.AskedShaping().Clarity.Enabled);
        Assert.Equal(0, WaitingEdits());
        b.X.Keyed = true; b.X.Buffer();
        Assert.Equal(TxChangeTiming.AtUnkey, TxAudioConditioning.Ask(b.X.Chain, lever: false, () => { }, s => s.Clarity.Enabled = true).Timing);
        Assert.True(TxAudioConditioning.AskedShaping().Clarity.Enabled);
        Assert.Equal(1, WaitingEdits());
        // Not held (unkeyed, nothing ahead): the change ran, and nothing waits.
        b.Unkey();
        bool ran = false;
        Assert.Equal(TxChangeTiming.Now, TxAudioConditioning.Ask(b.X.Chain, lever: false, () => ran = true, s => s.Clarity.Enabled = true).Timing);
        Assert.True(ran);
        Assert.Equal(0, WaitingEdits());
    }

    [Fact]
    public void Keep_does_not_say_it_switched_clarity_on_when_on_was_already_asked_for_and_is_waiting()
    {
        using var b = new Bridge();
        using var f = new FlowBench();
        f.Take = OffAirTake(f.Root);
        f.Current = TxAudioConditioning.CaptureClarity();
        var o = f.Flow().Run(CancellationToken.None, null);
        b.X.Keyed = true; b.X.Buffer();
        b.Noise.SetClarityOn(true, Home);                                      // waits for the unkey
        b.F.Spoken.Clear();
        b.Noise.KeepClaritySuggestion(o.Offer!.Proposed, Dialogs.ClaritySuggestDialog.Source);
        Assert.StartsWith(Lexicon.Get("audio.clarity_suggest.kept") + " ", b.F.Spoken.Said[0].Text);
        Assert.DoesNotContain(Lexicon.Get("audio.clarity_suggest.kept_switched_on"), b.F.Spoken.Said[0].Text);
        b.Unkey();
        Assert.True(b.Noise.ClarityOn);
        Assert.Equal(o.Offer.Proposed, TxAudioConditioning.ClaritySetup.ToSettings());
    }

    // ── B3: a reading is "now" only while audio is arriving ──

    private static void StopAudio(TxAudioConditioner cond, int msAgo = 2000) =>
        typeof(TxAudioConditioner).GetField("_lastBufferMs", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cond, Environment.TickCount64 - msAgo);

    private static void Tone(TxAudioConditioner cond, double amplitude = 0.25, int buffers = 10)
    {
        var tone = new float[960];
        for (int i = 0; i < 480; i++) tone[2 * i] = tone[2 * i + 1] = (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));
        for (int k = 0; k < buffers; k++) cond.Process((float[])tone.Clone(), 960, 48000);
    }

    [Fact]
    public void The_reading_is_a_figure_while_audio_arrives_withheld_once_it_stops_and_a_figure_again_when_it_resumes()
    {
        using var b = new Bridge();
        b.Noise.SetClarityOn(true, Home);
        b.Noise.SetCompressorOn(true, Home);
        b.Noise.SetDynamics(NoiseController.FindDynamicsLever(DynamicsLever.CompressorStage, "threshold")!, -40, Home);
        b.Noise.SetLimiterOn(true, Home);
        b.Unkey();

        // Audio arriving: a figure (the positive control).
        Tone(b.Cond);
        Assert.True(b.Cond.HasRecentAudio);
        var live = TxAudioConditioning.ReadClarityNow();
        Assert.True(live.Running);
        Assert.InRange(live.AddingDbfs, -60f, 0f);
        Assert.StartsWith(Lexicon.Get("audio.clarity.now", ("adding", Math.Round(live.AddingDbfs, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))),
            b.Noise.ClarityNowSentence());
        var dynamics = TxAudioConditioning.ReadDynamicsNow();
        Assert.True(dynamics.CompressorRunning);
        Assert.True(dynamics.CompressorReductionDb > 0.5f, $"the compressor was not reducing ({dynamics.CompressorReductionDb} dB): the test proves nothing");
        Assert.True(dynamics.LimiterRunning);

        // The last buffer was two seconds ago and none has come since (the computer's audio was switched off, or its
        // stream was lost). The stage still holds that buffer's figure; it is not "now".
        StopAudio(b.Cond);
        Assert.False(b.Cond.HasRecentAudio);
        Assert.InRange(b.Cond.Clarity.AddingNowDbfs, -60f, 0f);            // the stale figure is still there to be misread
        var stopped = TxAudioConditioning.ReadClarityNow();
        Assert.False(stopped.Running);
        Assert.Equal(-200f, stopped.AddingDbfs);
        Assert.StartsWith(Lexicon.Get("audio.clarity.now_not_running"), b.Noise.ClarityNowSentence());
        Assert.DoesNotContain(Lexicon.Get("audio.clarity.now", ("adding", Math.Round(live.AddingDbfs, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))),
            b.Noise.ClarityNowSentence());
        var stoppedDynamics = TxAudioConditioning.ReadDynamicsNow();
        Assert.False(stoppedDynamics.CompressorRunning);
        Assert.Equal(0f, stoppedDynamics.CompressorReductionDb);
        Assert.False(stoppedDynamics.LimiterRunning);
        Assert.Equal(0f, stoppedDynamics.LimiterReductionDb);
        Assert.StartsWith(Lexicon.Get("audio.compressor.now_not_running"), b.Noise.CompressorNowSentence());
        // What was asked for is untouched: on is still on, in the intent and in the chain.
        Assert.True(b.Noise.ClarityOn);
        Assert.True(b.Cond.Clarity.Enabled);
        Assert.True(b.Noise.CompressorOn);
        Assert.True(b.Noise.LimiterOn);

        // Audio again: a figure again.
        Tone(b.Cond);
        var resumed = TxAudioConditioning.ReadClarityNow();
        Assert.True(resumed.Running);
        Assert.InRange(resumed.AddingDbfs, -60f, 0f);
        Assert.True(TxAudioConditioning.ReadDynamicsNow().CompressorRunning);
    }

    // ── B2: the suggest pass carries the live side its renders are not ──

    private static AuditionLiveState Live(string mode, uint rate) =>
        ChainAuditionTwin.LiveState(true, -40, false, LufsMeter.Floor, 8, mode) with { TransmitRateHz = rate };

    [Fact]
    public void The_pass_says_when_the_live_rate_differs_when_the_live_chain_stands_aside_and_when_there_is_none_and_still_offers()
    {
        using var b = new Bridge();
        using var f = new FlowBench();
        f.Take = OffAirTake(f.Root);
        f.Current = TxAudioConditioning.CaptureClarity();
        string at48k(string rate) => Lexicon.Get("audio.audition.verdict.rendered_at_48k", ("rate", rate));
        string aside(string mode) => Lexicon.Get("audio.clarity_suggest.stands_aside", ("mode", mode));
        string none = Lexicon.Get("audio.clarity_suggest.no_live_chain");
        (ClaritySuggestFlow.Outcome Outcome, IReadOnlyList<string> Report) Run(AuditionLiveState? live)
        {
            f.Live = live;
            var o = f.Flow().Run(CancellationToken.None, null);
            return (o, ClaritySuggestFlow.Report(o));
        }

        // The control: a voice mode at 48 kHz. Nothing to qualify.
        var usb = Run(Live("USB", 48000));
        Assert.True(usb.Outcome.CanKeep);
        Assert.True(usb.Outcome.Rendered);
        Assert.Equal(48000u, usb.Outcome.Live!.TransmitRateHz);
        Assert.DoesNotContain(usb.Report, s => s == at48k("48") || s == aside("USB") || s == none);

        // The live stream at 16 kHz: the renders are still 48 kHz, and the report says so. Keep is still offered.
        var slow = Run(Live("USB", 16000));
        Assert.True(slow.Outcome.CanKeep);
        Assert.Equal(16000u, slow.Outcome.Live!.TransmitRateHz);
        Assert.Contains(at48k("16"), slow.Report);
        Assert.DoesNotContain(aside("USB"), slow.Report);
        Assert.NotEqual(usb.Report, slow.Report);
        Assert.True(slow.Report.ToList().IndexOf(at48k("16")) < slow.Report.ToList().IndexOf(Lexicon.Get("audio.clarity_suggest.can_keep_switches_on")));

        // CW: the live chain stands aside, so nothing here is what is being sent now. Said; Keep is still offered.
        var cw = Run(Live("CW", 48000));
        Assert.True(cw.Outcome.CanKeep);
        Assert.True(cw.Outcome.Live!.ChainStandsAside);
        Assert.Contains(aside("CW"), cw.Report);
        Assert.NotEqual(usb.Report, cw.Report);
        // Both at once.
        var both = Run(Live("DIGU", 24000));
        Assert.Contains(at48k("24"), both.Report);
        Assert.Contains(aside("DIGU"), both.Report);

        // No radio: no live chain at all. The pass still runs and offers; it says the renders are the saved settings alone.
        var offline = Run(null);
        Assert.True(offline.Outcome.CanKeep);
        Assert.Null(offline.Outcome.Live);
        Assert.Contains(none, offline.Report);
        Assert.NotEqual(usb.Report, offline.Report);

        // The guard withdrew (a hot voice): there is nothing to keep, and its judgement was still made on a 48 kHz render.
        f.Retained = Voice(1.3);
        f.Chain = new TxNoiseSetup { Limiter = new TxLimiterSetup { Enabled = true } };
        var hot = Run(Live("USB", 16000));
        Assert.False(hot.Outcome.CanKeep);
        Assert.True(hot.Outcome.Result.Guard!.Withdrawn);
        Assert.Contains(at48k("16"), hot.Report);
        // With no recording of the operator's voice nothing was rendered, so there is nothing to qualify.
        f.Retained = null;
        var unrendered = Run(Live("CW", 16000));
        Assert.False(unrendered.Outcome.Rendered);
        Assert.DoesNotContain(unrendered.Report, s => s == at48k("16") || s == aside("CW") || s == none);

        // Nothing was changed by any of it.
        Assert.False(b.X.Chain.HasDeferredChanges);
        Assert.False(TxAudioConditioning.ClaritySetup.Enabled);
    }

    [Fact]
    public void The_twins_renders_keep_the_live_side_they_were_prepared_under()
    {
        var take = Voice(0.3);
        var live = Live("CW", 16000);
        var renders = Twin(new TxNoiseSetup(), Ssb, () => live).PrepareClarity(take, Rate, CancellationToken.None, null, out var refusal);
        Assert.Null(refusal);
        Assert.Same(live, renders!.Live);
        Assert.Null(Twin(new TxNoiseSetup(), Ssb).PrepareClarity(take, Rate, CancellationToken.None, null, out _)!.Live);
    }

    [Fact]
    public void The_report_names_the_measured_takes_radio_mode_and_date_from_its_own_header()
    {
        using var b = new Bridge();
        using var f = new FlowBench();
        var made = new DateTimeOffset(2026, 11, 20, 18, 30, 0, TimeSpan.Zero);
        f.Take = OffAirTake(f.Root, more: facts =>
        {
            facts["nickname"] = "Shack radio"; facts["model"] = "FLEX-8600";
            facts["created-utc"] = made.ToString("O");
        });
        f.Current = TxAudioConditioning.CaptureClarity();
        var o = f.Flow().Run(CancellationToken.None, null);
        Assert.Equal("Shack radio", o.TakeRadio);                              // the nickname first
        Assert.Equal(made, o.TakeMade);
        var report = ClaritySuggestFlow.Report(o);
        Assert.Equal(Lexicon.Get("audio.clarity_suggest.source_off_air"), report[0]);
        Assert.Equal(Lexicon.Get("audio.clarity_suggest.measured_take", ("radio", "Shack radio"), ("mode", "USB"),
            ("when", ClaritySuggestFlow.WhenText(made))), report[1]);
        // With no nickname, the model; and a refusal says nothing of the kind.
        f.Take = OffAirTake(f.Root, more: facts => facts["model"] = "FLEX-6300");
        Assert.Equal("FLEX-6300", f.Flow().Run(CancellationToken.None, null).TakeRadio);
        f.Take = OffAirTake(f.Root, mode: "AM");
        var refused = ClaritySuggestFlow.Report(f.Flow().Run(CancellationToken.None, null));
        Assert.Equal(2, refused.Count);                                        // why, and that nothing has changed
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
}
