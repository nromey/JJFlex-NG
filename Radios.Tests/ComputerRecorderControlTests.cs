using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// The computer recorder as the controls drive it (#787, Tape Deck track 2):
/// "record to file" and "play from computer". The engine is Tape Deck track
/// 3's and does not exist yet, so these hold the CONTROLS' side of the
/// proposed contract against a fake engine, and the refusing stub that stands
/// in until the real one is installed.
/// </summary>
/// <remarks>
/// What these cannot see: any real recording. The fake records nothing; the
/// format, the folder and what a full disk does are track 3's to test.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ComputerRecorderControlTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ComputerRecorderControlTests));

    public void Dispose()
    {
        ComputerRecorder.Install(null);
        ComputerRecorder.ChosenRecording = null;
        _scope.Dispose();
    }

    private sealed class FakeEngine : IComputerRecorder
    {
        public bool IsRecording { get; set; }
        public bool IsPlaying { get; set; }
        public string LastRecordingPath { get; set; }
        public readonly List<string> Calls = new();
        private static Task<RecorderOutcome> Ok(string what) =>
            Task.FromResult(new RecorderOutcome(true, what, Speech.SpeechSubject.ComputerRecorder));
        public Task<RecorderOutcome> StartRecordingAsync(FlexBase rig) { Calls.Add("start"); return Ok("started"); }
        public Task<RecorderOutcome> StopRecordingAsync() { Calls.Add("stop"); return Ok("stopped"); }
        public Task<RecorderOutcome> PlayAsync(string path) { Calls.Add("play " + path); return Ok("playing"); }
        public Task<RecorderOutcome> StopPlaybackAsync() { Calls.Add("stop playback"); return Ok("stopped playing"); }
    }

    [Fact]
    public async Task Finishing_a_new_recording_puts_Play_from_computer_back_on_the_newest()
    {
        // Noel, 2026-10-25 10:41: a recording chosen in Recorded QSOs plays
        // until a new one is made.
        var engine = new FakeEngine { IsRecording = true };
        ComputerRecorder.Install(engine);
        ComputerRecorder.ChosenRecording = @"C:\rec\older.flac";
        var stopped = await ComputerRecorder.ToggleRecordingAsync(null);
        Assert.True(stopped.Success);
        Assert.Null(ComputerRecorder.ChosenRecording);

        // Positive control: a choice survives anything that is not a finished recording.
        engine.IsRecording = false;
        ComputerRecorder.ChosenRecording = @"C:\rec\older.flac";
        await ComputerRecorder.TogglePlaybackAsync(transmitting: false);
        Assert.Equal(@"C:\rec\older.flac", ComputerRecorder.ChosenRecording);
    }

    [Fact]
    public async Task Stopping_Play_from_computer_gives_the_tape_deck_click_not_the_recording_stop_beep()
    {
        // The 2026-10-25 bench heard a double beep: StopPlaybackAsync said Stop, the recording cue.
        var engine = new ComputerFlacRecorder(_ => true, () => { }, () => true);
        var outcome = await engine.StopPlaybackAsync();
        Assert.Equal(RecorderCue.PlayStop, outcome.Cue);
    }

    [Fact]
    public void The_real_engine_defaults_to_radio_audio_in_mono()
    {
        var engine = new ComputerFlacRecorder(_ => true, () => { }, () => false);
        Assert.False(engine.RecordWhatYouHeard);
        Assert.False(engine.Stereo);
    }

    [Fact]
    public async Task Until_an_engine_is_installed_both_controls_refuse_in_a_sentence()
    {
        ComputerRecorder.Install(null);
        using var c = new RadioCase();
        var record = await ComputerRecorder.ToggleRecordingAsync(c.F.Rig);
        Assert.False(record.Success);
        Assert.Equal(Lexicon.Get("audio.recorder.computer_record_unavailable"), record.Message);
        Assert.Equal(Speech.SpeechSubject.ComputerRecorder, record.Subject);
        var play = await ComputerRecorder.TogglePlaybackAsync(transmitting: false);
        Assert.False(play.Success);
        Assert.Equal(Lexicon.Get("audio.recorder.computer_play_unavailable"), play.Message);
        Assert.False(ComputerRecorder.Current.IsRecording);
        Assert.Null(ComputerRecorder.ChosenForPlayback());
    }

    [Fact]
    public async Task Play_from_computer_is_refused_while_transmitting_before_the_engine_is_asked()
    {
        var engine = new FakeEngine { LastRecordingPath = "last.flac" };
        ComputerRecorder.Install(engine);
        var refused = await ComputerRecorder.TogglePlaybackAsync(transmitting: true);
        Assert.False(refused.Success);
        Assert.Equal(Lexicon.Get("audio.recorder.computer_play_refused_transmitting"), refused.Message);
        Assert.Empty(engine.Calls);

        // Stopping a playback is never refused.
        engine.IsPlaying = true;
        Assert.True((await ComputerRecorder.TogglePlaybackAsync(transmitting: true)).Success);
        Assert.Equal(new[] { "stop playback" }, engine.Calls);
    }

    [Fact]
    public async Task Play_from_computer_plays_the_chosen_recording_else_the_last_one()
    {
        // Real files since Tape Deck track 4: a choice or a last recording
        // that no longer exists is skipped (see RecordedQsoCatalogTests).
        string last = System.IO.Path.Combine(_scope.Directory, "last.flac");
        string chosen = System.IO.Path.Combine(_scope.Directory, "chosen.flac");
        System.IO.File.WriteAllText(last, "");
        System.IO.File.WriteAllText(chosen, "");
        var engine = new FakeEngine { LastRecordingPath = last };
        ComputerRecorder.Install(engine);
        Assert.True((await ComputerRecorder.TogglePlaybackAsync(false)).Success);
        ComputerRecorder.ChosenRecording = chosen;
        Assert.True((await ComputerRecorder.TogglePlaybackAsync(false)).Success);
        Assert.Equal(new[] { "play " + last, "play " + chosen }, engine.Calls);
    }

    [Fact]
    public void Disconnected_stop_is_reachable_from_menu_registry_and_home()
    {
        Assert.False(System.Linq.Enumerable.Single(AudioMenuLayout.Entries, e => e.Id == "record-to-file").NeedsRadio);
        string keys = System.IO.File.ReadAllText(System.IO.Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "KeyCommands.cs"));
        Assert.Contains("\"Record to File\", false, FunctionGroups.Audio, KeyScope.Global)", keys);
        Assert.Contains("new(Keys.F11 | Keys.Alt, CommandValues.RecordToFile, KeyScope.Global)", keys);
        Assert.Contains("new(Keys.F11 | Keys.Control | Keys.Alt, CommandValues.PlayFromComputer, KeyScope.Global)", keys);
        string home = System.IO.File.ReadAllText(System.IO.Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "MainWindow.xaml"));
        Assert.Contains("DisconnectedRecorderStop", home);
    }

    [Fact]
    public void Playback_help_describes_automatic_vox_pause_without_promising_a_queued_play()
    {
        string help = HelpPages.Read("JJKey.md");
        var line = System.Linq.Enumerable.Single(help.Split('\n'), l => l.StartsWith("- **Ctrl+P**"));
        Assert.Contains("already transmitting", line);
        Assert.Contains("pauses VOX", line);
        Assert.DoesNotContain("waits until", line);
    }

    [Fact]
    public async Task Local_computer_play_uses_only_the_speaker_path_even_with_VOX_on()
    {
        string last = System.IO.Path.Combine(_scope.Directory, "last.flac");
        System.IO.File.WriteAllText(last, "");
        var engine = new FakeEngine { LastRecordingPath = last };
        ComputerRecorder.Install(engine);
        Assert.True((await ComputerRecorder.TogglePlaybackAsync(false)).Success);
        Assert.Equal(new[] { "play " + last }, engine.Calls);
        engine.IsPlaying = true;
        Assert.True((await ComputerRecorder.TogglePlaybackAsync(false)).Success);
        Assert.Equal(new[] { "play " + last, "stop playback" }, engine.Calls);
        string controls = System.IO.File.ReadAllText(System.IO.Path.Combine(IntegrationPassTree.Root,
            "JJFlexWpf", "RecorderControls.cs"));
        Assert.DoesNotContain("rig?.Vox", controls);
    }

    [Fact]
    public async Task Record_to_file_starts_with_a_radio_and_stops_without_one()
    {
        var engine = new FakeEngine();
        ComputerRecorder.Install(engine);
        var noRadio = await ComputerRecorder.ToggleRecordingAsync(null);
        Assert.False(noRadio.Success);
        Assert.Equal(Lexicon.Get("audio.no_radio_connected"), noRadio.Message);
        Assert.Empty(engine.Calls);

        using var c = new RadioCase();
        Assert.True((await ComputerRecorder.ToggleRecordingAsync(c.F.Rig)).Success);
        engine.IsRecording = true;
        // The radio went away mid-recording: stopping must still work.
        Assert.True((await ComputerRecorder.ToggleRecordingAsync(null)).Success);
        Assert.Equal(new[] { "start", "stop" }, engine.Calls);
    }
}
