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
        var engine = new FakeEngine { LastRecordingPath = "last.flac" };
        ComputerRecorder.Install(engine);
        Assert.True((await ComputerRecorder.TogglePlaybackAsync(false)).Success);
        ComputerRecorder.ChosenRecording = "chosen.flac";
        Assert.True((await ComputerRecorder.TogglePlaybackAsync(false)).Success);
        Assert.Equal(new[] { "play last.flac", "play chosen.flac" }, engine.Calls);
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
