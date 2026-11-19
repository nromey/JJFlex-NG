using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track N, the receive noise round: #1124, #1126, #1131, #1148,
/// #1150 and #1152 (#1147 is pinned in <see cref="PcDenoiseSlotTests"/>).
/// </summary>
/// <remarks>
/// Windowless: the noise controller over <see cref="NoiseFixture"/>'s fakes,
/// on an MTA thread, with no window. Every test asserts the desk is safe first.
/// </remarks>
public sealed class ReceiveNoiseRoundTests
{
    public ReceiveNoiseRoundTests() => NoiseReductionPerChannelTests.AssertWindowless();

    // ── #1152: NR4 has a delay figure before it has run ──

    [Fact]
    public void NR4_has_a_delay_figure_before_it_has_run_in_both_directions()
    {
        using var f = new NoiseFixture();   // the fake NR4 reports no latency, as a real one does before it runs
        Assert.Equal(0, f.Pipeline.SpectralSlot.CurrentLatencySamples("nr4"));
        Assert.Equal(Lexicon.Get("audio.noise_surface.delay", ("ms", 11)), f.Noise.DelayText("nr4"));
        Assert.Equal(Lexicon.Get("audio.noise_surface.delay_figure", ("ms", 11)), f.Noise.DelayFigure("nr4"));
        Assert.Equal(Lexicon.Get("audio.noise_surface.delay", ("ms", 11)), f.Noise.DelayText("nr4", DenoiseDirection.Transmit));
    }

    // ── #1126: the digital group's first visit is its first visit ──

    [Fact]
    public void A_first_digital_visit_with_nothing_on_is_remembered_so_a_later_visit_is_not_called_the_first()
    {
        // The e4 listen: DIGU from CW with nothing on said nothing; DIGL later,
        // from AM with RNNoise on, said "First time in a digital mode".
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.OnModeChanged("CW");
        f.Spoken.Clear();
        f.Noise.OnModeChanged("DIGU");
        Assert.Empty(f.Spoken.Said);                        // nothing changed, nothing said
        Assert.True(f.Config.NoiseModeMemory.Digital.Known);
        f.Noise.OnModeChanged("AM");
        f.Noise.Toggle("rnnoise", "test");
        f.Spoken.Clear();
        f.Noise.OnModeChanged("DIGL");
        Assert.False(f.Pipeline.NeuralSlot.Enabled);         // digital still turns it off
        Assert.DoesNotContain(f.Spoken.Said, s => s.Text.StartsWith("First time in a digital mode", StringComparison.Ordinal));
        Assert.Contains(f.Spoken.Said, s => s.Text.StartsWith("Back in", StringComparison.Ordinal) && s.Text.Contains("RNNoise off"));
        // CW keeps its rule: a first visit with nothing on stays unknown.
        Assert.False(f.Config.NoiseModeMemory.Cw.Known);
    }

    // ── #1124: a stale mode status right after the app's own request ──

    /// <summary>
    /// The fixture with both groups remembered: voice with RNNoise on, CW with
    /// NNR on (its starting point), so every group change swaps the neural
    /// engine and says so. Back in voice when it returns.
    /// </summary>
    private static NoiseFixture VoiceAndCwKnown(Func<long> clock)
    {
        var f = new NoiseFixture(clock: clock);
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        f.Noise.OnModeChanged("CW");
        Assert.Equal("nnr", f.Pipeline.NeuralSlot.ChosenEngineId);
        f.Noise.OnModeChanged("USB");
        Assert.Equal("rnnoise", f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.True(f.Pipeline.NeuralSlot.Enabled);
        f.Spoken.Clear();
        return f;
    }

    private static string Neural(NoiseFixture f) => f.Pipeline.NeuralSlot.ChosenEngineId;

    [Fact]
    public void A_stale_status_that_bounces_back_within_the_window_after_a_request_moves_nothing_and_says_nothing()
    {
        long now = 1_000_000;
        using var f = VoiceAndCwKnown(() => now);
        f.Noise.NoteModeRequested();                   // the operator asks for CW
        f.Noise.OnModeChanged("CW");                   // FlexLib's own value at the write: entered at once
        Assert.Equal(NoiseModeGroup.Cw, f.Noise.CurrentGroup);
        Assert.Equal("nnr", Neural(f));
        int said = f.Spoken.Said.Count;
        now += 120;
        f.Noise.OnModeChanged("USB");                  // the stale status another client's connect repeated
        Assert.Equal(NoiseModeGroup.Cw, f.Noise.CurrentGroup);
        Assert.Equal("nnr", Neural(f));                // no engine swap
        now += 200;
        f.Noise.OnModeChanged("CW");                   // the radio's own answer
        f.Clock.RunAll();
        Assert.Equal(NoiseModeGroup.Cw, f.Noise.CurrentGroup);
        Assert.Equal("nnr", Neural(f));
        Assert.Equal(said, f.Spoken.Said.Count);       // no false "Back in a voice mode"
        Assert.DoesNotContain(f.Spoken.Said, s => s.Text.StartsWith("Back in a voice mode", StringComparison.Ordinal));
    }

    [Fact]
    public void A_report_that_stands_for_the_window_is_entered_when_the_window_ends()
    {
        // The radio refused the request (or really changed): its mode is followed, only later.
        long now = 1_000_000;
        using var f = VoiceAndCwKnown(() => now);
        f.Noise.NoteModeRequested();
        f.Noise.OnModeChanged("CW");
        now += 300;
        f.Noise.OnModeChanged("USB");
        Assert.Equal(NoiseModeGroup.Cw, f.Noise.CurrentGroup);
        f.Clock.RunAll();                              // the window ends with USB still reported
        Assert.Equal(NoiseModeGroup.Voice, f.Noise.CurrentGroup);
        Assert.Equal("rnnoise", Neural(f));
        Assert.Contains(f.Spoken.Said, s => s.Text.StartsWith("Back in a voice mode", StringComparison.Ordinal));
    }

    [Fact]
    public void Outside_the_window_or_with_no_request_a_group_change_is_entered_at_once_as_before()
    {
        long now = 1_000_000;
        using var f = VoiceAndCwKnown(() => now);
        // No request: two changes in quick succession act at once (another program, the radio itself).
        f.Noise.OnModeChanged("CW");
        f.Noise.OnModeChanged("USB");
        Assert.Equal(NoiseModeGroup.Voice, f.Noise.CurrentGroup);
        // A request, its answer, then a change after the window: at once.
        f.Noise.NoteModeRequested();
        f.Noise.OnModeChanged("CW");
        now += NoiseController.ModeBounceWindowMs;
        f.Noise.OnModeChanged("USB");
        Assert.Equal(NoiseModeGroup.Voice, f.Noise.CurrentGroup);
        Assert.Equal(0, f.Clock.Pending);
        // A second request inside the window: its answer is an answer, not a bounce
        // (the e4 listen's Alt+C then Alt+U 317 ms later).
        f.Noise.NoteModeRequested();
        f.Noise.OnModeChanged("CW");
        now += 317;
        f.Noise.NoteModeRequested();
        f.Noise.OnModeChanged("USB");
        Assert.Equal(NoiseModeGroup.Voice, f.Noise.CurrentGroup);
    }

    [Fact]
    public void The_poll_road_obeys_the_same_window()
    {
        // FollowReceiveMode (the active slice's mode at a poll, #1123) reads the
        // same stale status FlexLib now holds.
        long now = 1_000_000;
        using var f = VoiceAndCwKnown(() => now);
        f.Noise.NoteModeRequested();
        f.Radio.Mode = "CW";
        f.Noise.OnModeChanged("CW");
        now += 100;
        f.Radio.Mode = "USB";
        f.Noise.FollowReceiveMode("test poll");
        Assert.Equal(NoiseModeGroup.Cw, f.Noise.CurrentGroup);
        f.Radio.Mode = "CW";
        f.Noise.OnModeChanged("CW");
        f.Clock.RunAll();
        Assert.Equal(NoiseModeGroup.Cw, f.Noise.CurrentGroup);
        Assert.DoesNotContain(f.Spoken.Said, s => s.Text.StartsWith("Back in a voice mode", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_mode_request_and_band_jump_tells_the_noise_layer_it_asked()
    {
        // MainWindow.NoteModeChoice is the one count every mode request
        // (AskRadioForMode) and band jump passes (#1117); it is where the
        // window opens. Read from source: MainWindow cannot be built windowless.
        string main = Source("JJFlexWpf", "MainWindow.xaml.cs");
        int at = main.IndexOf("public int NoteModeChoice()", StringComparison.Ordinal);
        Assert.True(at >= 0);
        int end = main.IndexOf("return _modeChoices.Note();", at, StringComparison.Ordinal);
        Assert.True(end > at);
        Assert.Contains("_noise?.NoteModeRequested();", main.Substring(at, end - at));
        Assert.Contains("NoteModeChoice();", main.Substring(main.IndexOf("public void AskRadioForMode(Action ask)", StringComparison.Ordinal), 200));
    }

    // ── #1148: Home's receive noise fields follow an engine change made elsewhere ──

    private static readonly string[] Levers = { "strength" };

    [Fact]
    public void Home_follows_a_slot_engine_changed_by_another_road_on_its_own_side()
    {
        // The menu or the layer put DeepFilterNet3 in the neural slot while Home showed RNNoise.
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "test");
        string shown = "rnnoise";
        var listed = new[] { "rnnoise", "deepfilternet3", "nnr" };
        Assert.Equal(Controls.HomeNoiseFieldsSync.Action.RefreshValues, Controls.HomeNoiseFieldsSync.Decide(
            DenoiseFamily.Neural, shown, f.Noise.SlotEngine(DenoiseFamily.Neural), listed, Levers, Levers, standingInLevers: false));
        f.Noise.Toggle("deepfilternet3", "Audio menu");
        Assert.Equal("deepfilternet3", f.Noise.SlotEngine(DenoiseFamily.Neural));
        Assert.Equal(Controls.HomeNoiseFieldsSync.Action.ShowSlotEngine, Controls.HomeNoiseFieldsSync.Decide(
            DenoiseFamily.Neural, shown, f.Noise.SlotEngine(DenoiseFamily.Neural), listed, Levers, Levers, standingInLevers: false));
    }

    [Fact]
    public void Home_waits_while_the_operator_stands_in_the_levers_and_never_follows_on_the_radio_side_or_to_an_unlisted_engine()
    {
        var listed = new[] { "rnnoise", "deepfilternet3" };
        Assert.Equal(Controls.HomeNoiseFieldsSync.Action.RefreshValues, Controls.HomeNoiseFieldsSync.Decide(
            DenoiseFamily.Neural, "rnnoise", "deepfilternet3", listed, Levers, new[] { "other" }, standingInLevers: true));
        Assert.Equal(Controls.HomeNoiseFieldsSync.Action.RefreshValues, Controls.HomeNoiseFieldsSync.Decide(
            null, "radio_nr", "", listed, Levers, Levers, standingInLevers: false));
        Assert.Equal(Controls.HomeNoiseFieldsSync.Action.RefreshValues, Controls.HomeNoiseFieldsSync.Decide(
            DenoiseFamily.Neural, "rnnoise", "not_listed_here", listed, Levers, Levers, standingInLevers: false));
    }

    [Fact]
    public void Home_rebuilds_the_shown_engines_levers_when_its_lever_set_changes_elsewhere()
    {
        // NR2's trained values come and go with its gain method; Advanced opened from the menu adds levers.
        using var f = new NoiseFixture();
        f.Noise.Attach("USB");
        f.Noise.SetAdvanced("nr2", true);
        var before = Controls.HomeNoiseFieldsSync.WantedLevers(f.Noise, "nr2").ToList();
        Assert.DoesNotContain("trained_t1", before);
        var method = f.Noise.Levers("nr2", includeAdvanced: true).Single(l => l.Key == "gain_method");
        f.Noise.Set(method, null, "trained", "Audio menu");
        var after = Controls.HomeNoiseFieldsSync.WantedLevers(f.Noise, "nr2").ToList();
        Assert.Contains("trained_t1", after);
        Assert.Equal(Controls.HomeNoiseFieldsSync.Action.RebuildEngine, Controls.HomeNoiseFieldsSync.Decide(
            DenoiseFamily.Spectral, "nr2", "nr2", new[] { "nr2" }, before, after, standingInLevers: false));
        Assert.Equal(Controls.HomeNoiseFieldsSync.Action.RefreshValues, Controls.HomeNoiseFieldsSync.Decide(
            DenoiseFamily.Spectral, "nr2", "nr2", new[] { "nr2" }, after, after, standingInLevers: false));
    }

    [Fact]
    public void Homes_receive_poll_asks_which_engine_to_show_before_it_refreshes_values()
    {
        // The panel is a WPF control; its poll is read from source.
        string panel = Source("JJFlexWpf", "Controls", "ScreenFieldsPanel.Noise.cs");
        int poll = panel.IndexOf("private void PollNoise()", StringComparison.Ordinal);
        Assert.True(poll >= 0);
        string body = panel.Substring(poll, panel.IndexOf("public void OpenNoise(", poll, StringComparison.Ordinal) - poll);
        int decide = body.IndexOf("HomeNoiseFieldsSync.Decide(", StringComparison.Ordinal);
        Assert.True(decide >= 0 && decide < body.IndexOf("RefreshNoiseValues();", StringComparison.Ordinal));
        Assert.Contains("ShowNoiseSide(selectEngine: slotEngine)", body);
        Assert.Contains("ShowNoiseEngine(_noiseFieldsFor)", body);
        Assert.Contains("_noiseLevers.IsKeyboardFocusWithin || _noiseAdvancedLevers.IsKeyboardFocusWithin", body);
    }

    private static string Source(params string[] path)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, System.IO.Path.Combine(path))).Replace("\r\n", "\n");
    }
}
