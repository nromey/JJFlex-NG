using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track E, repair round 1: Codex's B1 and B3 to B8 and the
/// Listen-to-what-was-removed control shape, each pinned against the real
/// controller and slots over fakes. Windowless (asserted by the fixture):
/// a fake radio, fake engines carrying the real descriptors, generated audio.
/// </summary>
/// <remarks>
/// Where a setting only a new instance can take is involved (NNR's model,
/// NR4's estimator), these tests read the RUNNING engine instance, the one
/// the audio thread is playing, never only the slot's stored value: Codex's
/// check showed a stored value can be right while the engine you hear is not.
/// </remarks>
public sealed class NoiseRepairRoundOneTests
{
    public NoiseRepairRoundOneTests() => NoiseReductionPerChannelTests.AssertWindowless();

    // ── Helpers ──

    /// <summary>A setting as the running instance holds it, or null with nothing running.</summary>
    private static string? Running(PcDenoiseSlot slot, string name) =>
        slot.RunningBankForTest?.EngineFor(0) is { } engine && engine.Settings.Any(d => d.Name == name)
            ? engine.GetSetting(name).Choice : null;

    /// <summary>Feed audio until the condition holds (the swap happens on the audio thread, a tenth of a second after the build).</summary>
    private static void PumpUntil(NoiseFixture f, Func<bool> done)
    {
        var audio = new float[960];
        var time = Stopwatch.StartNew();
        while (!done() && time.ElapsedMilliseconds < 5000)
        {
            Array.Fill(audio, 0.1f);
            f.Pipeline.Process(audio);
            Thread.Sleep(1);
        }
    }

    /// <summary>Feed audio for a while whatever happens, so a build that should NOT come would have had time to.</summary>
    private static void Pump(NoiseFixture f, int buffers = 150)
    {
        var audio = new float[960];
        for (int i = 0; i < buffers; i++) { Array.Fill(audio, 0.1f); f.Pipeline.Process(audio); Thread.Sleep(1); }
    }

    private static string Source(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(path))).Replace("\r\n", "\n");
    }

    // ── B1: Escape after a slice jump ──

    [Fact]
    public void B1_a_slice_jump_with_nothing_changed_then_Escape_writes_nothing_to_the_radio()
    {
        // Codex's probe, exactly: open on slice A, Shift+B to a slice whose
        // settings differ, change nothing, Escape. Before the repair Escape
        // wrote A's Noise Blanker (off, level 0) onto B.
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.NoteActiveSlice(0);
        var snap = f.Noise.TakeSnapshot();
        f.Radio.Switches["radio_nb"] = true;            // slice B's own settings
        f.Radio.Levels["radio_nb"] = 42;
        f.Radio.SliceIdentity = "slice B";             // the radio now reads B (round 2: by identity)
        f.Noise.NoteActiveSlice(1);                     // the jump, as LayerSliceJump reports it
        f.Radio.Writes.Clear();
        Assert.False(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.Empty(f.Radio.Writes);
        Assert.True(f.Radio.Switches["radio_nb"]);
        Assert.Equal(42, f.Radio.Levels["radio_nb"]);
    }

    [Fact]
    public void B1_work_done_on_the_slice_left_is_kept_and_nothing_is_written_to_the_new_one()
    {
        // LayerSliceJump's rule: leaving a slice confirms that slice's work.
        // Before the repair the switch the operator turned on on A was still
        // believed on for two seconds after the jump, and Escape wrote it OFF
        // to B.
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.NoteActiveSlice(0);
        var snap = f.Noise.TakeSnapshot();
        f.Noise.Toggle("radio_nb", "test");             // on, on slice A
        f.Radio.Switches["radio_nb"] = false;           // slice B has it off
        f.Radio.Levels["radio_nb"] = 10;
        f.Radio.SliceIdentity = "slice B";             // the radio now reads B (round 2: by identity)
        f.Noise.NoteActiveSlice(1);
        f.Radio.Writes.Clear();
        Assert.False(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.Empty(f.Radio.Writes);
    }

    [Fact]
    public void B1_after_a_jump_Escape_puts_back_what_the_layer_changed_on_the_new_slice_to_the_new_slices_values()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.NoteActiveSlice(0);
        var snap = f.Noise.TakeSnapshot();              // A: Noise Blanker off, level 0
        f.Radio.Switches["radio_nb"] = true;            // B: on, level 42
        f.Radio.Levels["radio_nb"] = 42;
        f.Radio.SliceIdentity = "slice B";             // the radio now reads B (round 2: by identity)
        f.Noise.NoteActiveSlice(1);
        f.Noise.Adjust(f.Noise.Levers("radio_nb", false)[0], +1, "test");   // 45, on B
        Assert.Equal(45, f.Radio.Levels["radio_nb"]);
        f.Radio.Writes.Clear();
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.Equal(new[] { "radio_nb.level=42" }, f.Radio.Writes);     // B's, not A's 0; the switch untouched
    }

    [Fact]
    public void B1_Escape_puts_back_only_what_the_layer_itself_changed()
    {
        // A value that changed under the layer by another road (another
        // program, the radio) is not the layer's to undo.
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.NoteActiveSlice(0);
        var snap = f.Noise.TakeSnapshot();
        f.Radio.Switches["radio_wnb"] = true;           // not by the layer
        f.Noise.Toggle("radio_nb", "test");             // by the layer
        f.Radio.Writes.Clear();
        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        Assert.Equal(new[] { "radio_nb=off" }, f.Radio.Writes);
    }

    // ── B3: presets and Escape rebuild what only a new instance can take ──

    [Fact]
    public void B3_a_preset_with_another_NNR_model_rebuilds_the_running_engine_and_says_so()
    {
        using var f = new NoiseFixture();
        f.Noise.PresetStore = new MemoryPresets();
        f.Noise.Toggle("nnr", "test");
        PumpUntil(f, () => Running(f.Pipeline.NeuralSlot, "model") == "standard");
        Assert.NotNull(f.Noise.SavePreset("Standard", includeNoise: true, includeChain: false, PresetCollision.Ask, "test"));
        var model = f.Noise.Levers("nnr", false).Single(l => l.Key == "model");
        f.Noise.Set(model, null, "premium", "test");
        // The positive control inside the test: the ordinary lever reaches the running engine.
        PumpUntil(f, () => Running(f.Pipeline.NeuralSlot, "model") == "premium");
        Assert.Equal("premium", Running(f.Pipeline.NeuralSlot, "model"));

        f.Noise.ApplyPreset("Standard", "test");
        PumpUntil(f, () => Running(f.Pipeline.NeuralSlot, "model") == "standard");
        Assert.Equal("standard", f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        Assert.Equal("standard", Running(f.Pipeline.NeuralSlot, "model"));
        Assert.Equal("Standard preset: NNR model, Standard voice.", f.Spoken.Last);
    }

    [Fact]
    public void B3_Escape_after_a_model_change_rebuilds_the_running_engine_on_the_model_put_back()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("nnr", "test");
        PumpUntil(f, () => Running(f.Pipeline.NeuralSlot, "model") == "standard");
        var snap = f.Noise.TakeSnapshot();
        var model = f.Noise.Levers("nnr", false).Single(l => l.Key == "model");
        f.Noise.Set(model, null, "premium_cw", "test");
        PumpUntil(f, () => Running(f.Pipeline.NeuralSlot, "model") == "premium_cw");
        Assert.Equal("premium_cw", Running(f.Pipeline.NeuralSlot, "model"));

        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        PumpUntil(f, () => Running(f.Pipeline.NeuralSlot, "model") == "standard");
        Assert.Equal("standard", f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        Assert.Equal("standard", Running(f.Pipeline.NeuralSlot, "model"));
    }

    [Fact]
    public void B3_Escape_after_an_NR4_estimator_change_rebuilds_NR4_on_the_estimator_put_back()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("nr4", "test");
        string start = f.Pipeline.SpectralSlot.GetSetting("nr4", "noise_estimator").Choice!;
        PumpUntil(f, () => Running(f.Pipeline.SpectralSlot, "noise_estimator") == start);
        var snap = f.Noise.TakeSnapshot();
        var estimator = f.Noise.Levers("nr4", true).Single(l => l.Key == "noise_estimator");
        string other = f.Noise.OptionsFor(estimator).Select(o => o.Name).First(n => n != start);
        f.Noise.Set(estimator, null, other, "test");
        PumpUntil(f, () => Running(f.Pipeline.SpectralSlot, "noise_estimator") == other);
        Assert.Equal(other, Running(f.Pipeline.SpectralSlot, "noise_estimator"));

        Assert.True(f.Noise.RestoreSnapshot(snap, "test"));
        PumpUntil(f, () => Running(f.Pipeline.SpectralSlot, "noise_estimator") == start);
        Assert.Equal(start, Running(f.Pipeline.SpectralSlot, "noise_estimator"));
    }

    [Fact]
    public void B3_a_preset_that_changes_only_settings_says_what_changed_and_journals_it()
    {
        using var f = new NoiseFixture();
        f.Noise.PresetStore = new MemoryPresets();
        f.Noise.Toggle("rnnoise", "test");
        Assert.NotNull(f.Noise.SavePreset("Gentle", true, false, PresetCollision.Ask, "test"));   // strength 80
        var strength = f.Noise.Levers("rnnoise", false)[0];
        f.Noise.Set(strength, 50, null, "test");

        var entries = new List<NoiseJournalEntry>();
        void Listen(NoiseJournalEntry e) { lock (entries) entries.Add(e); }
        NoiseSettingsJournal.Recorded += Listen;
        try { f.Noise.ApplyPreset("Gentle", "test"); }
        finally { NoiseSettingsJournal.Recorded -= Listen; }

        Assert.Equal(0.8f, f.Pipeline.NeuralSlot.GetStrength("rnnoise"), 3);
        Assert.Equal("Gentle preset: RNNoise strength, 80 percent.", f.Spoken.Last);
        Assert.DoesNotContain("nothing to change", f.Spoken.Last);
        lock (entries)
        {
            var applied = Assert.Single(entries, e => e.Kind == NoiseJournalKind.PresetApplied && e.Source == "test");
            Assert.Contains(applied.Changes, c => c.EngineId == "rnnoise" && c.Setting == "strength" && c.OldValue == "50" && c.NewValue == "80");
        }
    }

    [Fact]
    public void B3_a_preset_identical_to_what_is_running_still_says_nothing_to_change_and_rebuilds_nothing()
    {
        using var f = new NoiseFixture();
        f.Noise.PresetStore = new MemoryPresets();
        f.Noise.Toggle("nnr", "test");
        PumpUntil(f, () => Running(f.Pipeline.NeuralSlot, "model") == "standard");
        Assert.NotNull(f.Noise.SavePreset("Same", true, false, PresetCollision.Ask, "test"));
        int built = f.Built.Count(id => id == "nnr");
        f.Noise.ApplyPreset("Same", "test");
        Pump(f);
        Assert.Equal(built, f.Built.Count(id => id == "nnr"));
        Assert.Equal("Same preset: nothing to change.", f.Spoken.Last);
    }

    // ── B4: the CW model rule on every road a model enters by ──

    [Fact]
    public void B4_a_voice_model_favourite_stepped_to_in_CW_lands_on_the_CW_model_and_says_so()
    {
        // Codex's probe: NNR Standard voice marked in voice, CW entered,
        // the brackets step to it with NNR already on.
        using var f = new NoiseFixture();
        f.Noise.Toggle("nnr", "test");
        f.Noise.ToggleFavourite("nnr", "test");                  // NNR Standard voice
        f.Noise.OnModeChanged("CW");                             // CW's starting point: Premium CW
        f.Noise.BeginSession();
        f.Noise.Step(+1, "test");
        Assert.Equal("premium_cw", f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice);
        PumpUntil(f, () => Running(f.Pipeline.NeuralSlot, "model") == "premium_cw");
        Assert.Equal("premium_cw", Running(f.Pipeline.NeuralSlot, "model"));
        Assert.Equal("NNR Premium CW on. NNR switched to Premium CW for CW; the voice model comes back when you leave CW.", f.Spoken.Last);
        Assert.Single(f.Noise.Favourites);                       // the favourite stays in the list
        Assert.Equal("standard", f.Config.NoiseModeMemory.NnrVoiceModelOwed);
    }

    [Fact]
    public void B4_stepping_between_two_voice_model_favourites_of_NNR_in_CW_never_leaves_a_CW_model()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("nnr", "test");
        f.Noise.ToggleFavourite("nnr", "test");                  // Standard voice
        f.Noise.Set(f.Noise.Levers("nnr", false).Single(l => l.Key == "model"), null, "premium", "test");
        f.Noise.ToggleFavourite("nnr", "test");                  // Premium voice
        f.Noise.OnModeChanged("CW");
        f.Noise.BeginSession();
        f.Noise.Step(+1, "test");
        Assert.True(NoiseModeRules.IsCwModel(f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice));
        f.Noise.Step(+1, "test");                                // the same-engine branch
        Assert.True(NoiseModeRules.IsCwModel(f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice));
        Assert.True(f.Noise.IsOn("nnr"));
        PumpUntil(f, () => NoiseModeRules.IsCwModel(Running(f.Pipeline.NeuralSlot, "model")));
        Assert.True(NoiseModeRules.IsCwModel(Running(f.Pipeline.NeuralSlot, "model")));
    }

    [Fact]
    public void B4_a_preset_bringing_NNR_back_in_CW_lands_on_the_CW_model_and_keeps_its_own_voice_model()
    {
        using var f = new NoiseFixture();
        var store = new MemoryPresets();
        f.Noise.PresetStore = store;
        f.Noise.Toggle("nnr", "test");
        Assert.NotNull(f.Noise.SavePreset("Voice NNR", true, false, PresetCollision.Ask, "test"));
        f.Noise.Toggle("rnnoise", "test");                       // NNR replaced
        f.Noise.OnModeChanged("CW");
        f.Noise.Toggle("rnnoise", "test");                       // whatever CW started on, RNNoise now
        if (!f.Noise.IsOn("rnnoise")) f.Noise.Toggle("rnnoise", "test");
        Assert.Equal("rnnoise", f.Pipeline.NeuralSlot.ChosenEngineId);

        f.Noise.ApplyPreset("Voice NNR", "test");
        Assert.Equal("nnr", f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.True(NoiseModeRules.IsCwModel(f.Pipeline.NeuralSlot.GetSetting("nnr", "model").Choice));
        PumpUntil(f, () => f.Pipeline.NeuralSlot.RunningEngineId == "nnr" && NoiseModeRules.IsCwModel(Running(f.Pipeline.NeuralSlot, "model")));
        Assert.True(NoiseModeRules.IsCwModel(Running(f.Pipeline.NeuralSlot, "model")));
        // The preset itself still holds the voice model, for voice.
        var held = store.Data.NoisePresets.Single(p => p.Name == "Voice NNR").ReceiveNoise!.Neural.Settings!;
        Assert.DoesNotContain(held.Values, v => v.Name == "model" && NoiseModeRules.IsCwModel(v.Choice));
    }

    // ── B5: a choice made with the slot off is remembered per mode ──

    [Fact]
    public void B5_an_engine_chosen_while_its_slot_is_off_is_what_the_mode_comes_back_to()
    {
        using var f = new NoiseFixture();
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.Toggle("rnnoise", "test");                       // off
        f.Noise.ChooseSlotEngine(DenoiseFamily.Neural, "deepfilternet3", "test");
        f.Clock.RunAll();
        Assert.Equal("deepfilternet3", f.Pipeline.NeuralSlot.ChosenEngineId);
        f.Noise.OnModeChanged("CW");
        f.Noise.OnModeChanged("USB");
        Assert.Equal("deepfilternet3", f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.False(f.Pipeline.NeuralSlot.Enabled);
    }

    // ── B6: connecting again in CW keeps NR2's voice tracking ──

    [Fact]
    public void B6_reconnecting_in_CW_does_not_write_CWs_NR2_tracking_over_the_voice_one()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        string voice = f.Pipeline.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice!;
        f.Noise.OnModeChanged("CW");
        Assert.Equal(voice, f.Config.NoiseModeMemory.Nr2OtherTracking);
        Assert.Equal(NoiseModeRules.Nr2CwTrackingDefault, f.Pipeline.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice);
        f.Noise.Detach();
        f.Noise.Attach("CW");                                    // back where the settings were saved
        Assert.Equal(voice, f.Config.NoiseModeMemory.Nr2OtherTracking);
        Assert.Equal(NoiseModeRules.Nr2CwTrackingDefault, f.Pipeline.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice);
        f.Noise.OnModeChanged("USB");
        Assert.Equal(voice, f.Pipeline.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice);
    }

    [Fact]
    public void B6_connecting_in_voice_after_the_settings_were_saved_in_CW_puts_the_voice_tracking_back()
    {
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        string voice = f.Pipeline.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice!;
        f.Noise.OnModeChanged("CW");
        f.Noise.Detach();
        f.Noise.Attach("USB");                                   // a real change of group, across the connect
        Assert.Equal(voice, f.Pipeline.SpectralSlot.GetSetting("nr2", "noise_estimator").Choice);
        Assert.Equal(NoiseModeRules.Nr2CwTrackingDefault, f.Config.NoiseModeMemory.Nr2CwTracking);
        Assert.Equal("voice", f.Config.NoiseModeMemory.LastGroup);
    }

    // ── B7: the four held levels' write path ──

    [Fact]
    public void B7_every_level_the_radio_never_reports_is_written_through_the_held_path_and_no_other()
    {
        var catalog = new DenoiseEngineCatalog();
        var held = catalog.Entries.Where(e => e.Level != null && !e.Level.ReportedBack)
            .ToDictionary(e => e.Id, e => e.Level!.Held);
        Assert.Equal(new Dictionary<string, HeldNoiseLevel?>
        {
            ["radio_nrl"] = HeldNoiseLevel.LegacyNoiseReduction,
            ["radio_nrs"] = HeldNoiseLevel.SpectralNoiseReduction,
            ["radio_nrf"] = HeldNoiseLevel.NoiseReductionFilter,
            ["radio_anfl"] = HeldNoiseLevel.LegacyAutoNotch,
        }, held);
        Assert.All(catalog.Entries.Where(e => e.Level is { ReportedBack: true }), e => Assert.Null(e.Level!.Held));
    }

    // ── B8: Home's Audio Peak Filter checkbox ──

    [Fact]
    public void B8_Homes_Audio_Peak_Filter_checkbox_goes_through_the_controller_and_is_refused_outside_CW()
    {
        // A source pin, said as one: the checkbox lives on a control that
        // needs a window. The behaviour it now reaches is the controller's,
        // driven below.
        string panel = Source("JJFlexWpf", "Controls", "ScreenFieldsPanel.xaml.cs");
        string noise = Source("JJFlexWpf", "Controls", "ScreenFieldsPanel.Noise.cs");
        Assert.DoesNotMatch(@"_rig\.APF\s*=(?!=)", panel);   // no write; the poll's read stays
        Assert.Contains("_apfCheck.Checked += (s, e) => SetApfThroughController(true);", panel);
        Assert.Contains("_apfCheck.Unchecked += (s, e) => SetApfThroughController(false);", panel);
        Assert.Contains("noise.SetOn(\"radio_apf\", on, NoiseSource);", noise);

        using var f = new NoiseFixture();
        f.Radio.Mode = "USB";
        f.Noise.SetOn("radio_apf", true, "home");
        Assert.Empty(f.Radio.Writes);
        Assert.False(f.Noise.IsOn("radio_apf"));
        Assert.Equal("Audio Peak Filter works in CW only. Change to a CW mode to use it.", f.Spoken.Last);
        f.Radio.Mode = "CW";
        f.Noise.SetOn("radio_apf", true, "home");
        Assert.Equal(new[] { "radio_apf=on" }, f.Radio.Writes);
    }

    // ── Listen to what was removed is a two-state control ──

    [Fact]
    public void Listen_to_what_was_removed_has_two_states_and_ends_like_any_lever()
    {
        // Before the repair its Next never ended, so a field built from the
        // steps listed Off and On a thousand times.
        using var f = new NoiseFixture();
        f.Noise.Toggle("rnnoise", "test");
        var listen = f.Noise.Levers("rnnoise", true).Single(l => l.Kind == NoiseLeverKind.ListenRemoved);
        var steps = f.Noise.LeverSteps(listen);
        Assert.Equal(new double?[] { 0, 1 }, steps.Select(s => s.Shown));
        Assert.Equal(new[] { "off", "on" }, steps.Select(s => s.Text));
        f.Noise.Adjust(listen, +1, "test");
        Assert.True(f.Pipeline.NeuralSlot.ListenToRemoved);
        f.Noise.Adjust(listen, +1, "test");                       // already on: stays, and says so
        Assert.True(f.Pipeline.NeuralSlot.ListenToRemoved);
        Assert.EndsWith("the most.", f.Spoken.Last);
        f.Noise.Adjust(listen, -1, "test");
        Assert.False(f.Pipeline.NeuralSlot.ListenToRemoved);
    }

    [Fact]
    public void Listen_to_what_was_removed_is_a_checkbox_on_Home()
    {
        // A source pin: Home's fields need a window. The ruled shape is a
        // checkbox (question 13), never the cycle field the steps used to fill.
        string noise = Source("JJFlexWpf", "Controls", "ScreenFieldsPanel.Noise.cs");
        Assert.Contains("lever.Kind == NoiseLeverKind.ListenRemoved\n                        ? MakeListenCheck(noise, lever) : MakeLeverField(noise, lever)", noise);
        Assert.Contains("private CheckBox MakeListenCheck(NoiseController noise, NoiseLever lever)", noise);
    }

    // ── Follow-up round 5: two Track E surfaces from the no-radio harness walk ──

    [Fact]
    public void FollowUp5_Measured_delays_list_every_computer_engine_with_no_radio_and_each_row_is_one_phrase()
    {
        // The walk heard "Connect a radio to see the delays" with the guard
        // off and seven delays with no radio once it was on, and a row read
        // "NR2: Delay: about 105 milliseconds". The figures are the
        // computer's, so no radio is needed, and the guard locks editing,
        // not reading: the rows depend on nothing else.
        using var f = new NoiseFixture();
        f.RadioPresent = false;
        var rows = JJFlexWpf.Dialogs.SettingsDialog.MeasuredDelayRows(f.Noise);
        var computer = f.Noise.Engines().Where(e => e.Side == DenoiseSide.Pc).ToList();
        Assert.Equal(computer.Count, rows.Count);
        Assert.Equal(computer.Select(e => f.Noise.NameOf(e.Id) + ":"), rows.Select(r => r[..(r.IndexOf(':') + 1)]));
        Assert.Contains("NR2: about 105 milliseconds", rows);
        Assert.Contains("DeepFilterNet3: measured once it is running", rows);
        Assert.All(rows, r => Assert.DoesNotContain("Delay", r));
        Assert.All(rows, r => Assert.DoesNotContain("radio", r));
        // Home's own readout still says what it is.
        Assert.Equal("Delay: about 105 milliseconds", f.Noise.DelayText("nr2"));
        // Only with no controller at all is there a line saying so, and it claims no reason.
        Assert.Equal(new[] { "No delays to show" }, JJFlexWpf.Dialogs.SettingsDialog.MeasuredDelayRows(null));
    }

    [Fact]
    public void FollowUp5_assigning_the_controller_to_Settings_refills_the_delays_and_the_guard_never_touches_them()
    {
        // A source pin, said as one: Settings is a window. The controller is
        // assigned after the constructor built the tab, which is why the list
        // held the no-controller line until a focus event replaced it.
        string noise = Source("JJFlexWpf", "Dialogs", "SettingsDialog.Noise.cs");
        Assert.Contains("set { _noise = value; FillMeasuredDelays(); }", noise);
        int gate = noise.IndexOf("void Gate()", StringComparison.Ordinal);
        Assert.True(gate > 0);
        string gateBody = noise.Substring(gate, noise.IndexOf("Gate();", gate + 12, StringComparison.Ordinal) - gate);
        Assert.DoesNotContain("_advancedDelays", gateBody);
        Assert.Contains("foreach (string row in MeasuredDelayRows(Noise)) _advancedDelays.Items.Add(row);", noise);
    }

    [Theory]
    [InlineData("Neural NR")]
    [InlineData("FFT Auto-Notch")]
    public void FollowUp5_a_switch_only_engines_favourite_row_carries_no_needs_a_radio_marker(string name)
    {
        // The walk read "Neural NR favourite - connect a radio: Off" beside
        // every other engine's "Favourite: Off". A favourite needs no radio;
        // the marker belongs on the engine's own row, before the tab, once.
        Assert.Equal(name + " favourite", NativeMenuBar.SwitchOnlyFavouriteLabel(name));

        // And the builder uses it unmarked, while the engine row above keeps
        // its marker (DspOnAudioMenuTests pins that one). A source pin: the
        // menu builder needs a window.
        string menu = Source("JJFlexWpf", "NativeMenuBar.cs");
        int body = menu.IndexOf("private void BuildNoiseItems(IntPtr parent, DenoiseGroup group)", StringComparison.Ordinal);
        Assert.True(body > 0);
        string switchOnly = menu.Substring(body, menu.IndexOf("continue;", body, StringComparison.Ordinal) - body);
        Assert.Contains("AddChecked(parent, R(named + chord),", switchOnly);
        Assert.Contains("AddChecked(parent, SwitchOnlyFavouriteLabel(name),", switchOnly);
        Assert.DoesNotContain("R(SwitchOnlyFavouriteLabel", switchOnly);
        Assert.DoesNotContain("engine_favourite", switchOnly);
    }

    private sealed class MemoryPresets : INoisePresetStore
    {
        public AudioChainPresets Data = new() { BalancedCaptureOffered = true };
        public AudioChainPresets Load() => Data;
        public bool Save(AudioChainPresets store) { Data = store; return true; }
    }
}
