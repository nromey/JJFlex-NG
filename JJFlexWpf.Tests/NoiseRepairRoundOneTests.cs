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

    private sealed class MemoryPresets : INoisePresetStore
    {
        public AudioChainPresets Data = new() { BalancedCaptureOffered = true };
        public AudioChainPresets Load() => Data;
        public bool Save(AudioChainPresets store) { Data = store; return true; }
    }
}
