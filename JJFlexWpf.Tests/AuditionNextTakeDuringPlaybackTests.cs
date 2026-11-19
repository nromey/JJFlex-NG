using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using NAudio.Wave;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The next audition take, started while a render of the last one is still
/// playing, opens its microphone with nothing of the app's still to be said
/// (#262, Sprint 49 Track Q2 repair 1). The window stops that render just
/// before capture opens, AFTER the countdown's gate; the player's ordinary
/// stop queues "Playback stopped" at that instant, and the microphone opened
/// straight after, so the sentence could land in the take. Codex's check of
/// <c>dd7c03b5</c> found it and proved the order with this arrangement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless and silent.</b> Every test asserts
/// <see cref="NoiseReductionPerChannelTests.AssertWindowless"/> (an MTA
/// thread, on which WPF cannot build an element), that the run is
/// <see cref="QuietRun.Silenced"/> and that render is off. The player is the
/// REAL <see cref="RecordingPlayer"/>, given a synthetic file and a mixer that
/// accepts it and plays nothing; the dialog is an uninitialized object, so its
/// constructor never runs; the recorder is a fake that opens no microphone.
/// What the app asks to say is read from the output transcript the app's own
/// speech path writes, never from a reader.
/// </para>
/// <para>
/// <b>What these cannot see:</b> what NVDA or JAWS actually say, and sound in
/// the room. Those are the harness walk's.
/// </para>
/// </remarks>
public sealed class AuditionNextTakeDuringPlaybackTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jjflex-audition-next-take-" + Guid.NewGuid().ToString("N"));
    private readonly string _render;

    public AuditionNextTakeDuringPlaybackTests()
    {
        Directory.CreateDirectory(_folder);
        // The render must sit in the audition's own folder: that is what the
        // window's stop recognises as a render of ours.
        Directory.CreateDirectory(ChainAuditionFolder.DefaultPath);
        _render = Path.Combine(ChainAuditionFolder.DefaultPath, "next-take-" + Guid.NewGuid().ToString("N") + ".wav");
        using var w = new WaveFileWriter(_render, new WaveFormat(48000, 16, 1));
        w.WriteSamples(new float[48000], 0, 48000);
    }

    public void Dispose()
    {
        RecordingPlayer.StopWithoutSpeaking();
        OutputChannelRecorder.Configure(render: false, record: false);
        try { File.Delete(_render); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_next_take_started_while_a_render_plays_opens_with_nothing_of_the_players_queued()
    {
        var dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
        var stop = typeof(CheckYourAudioDialog).GetMethod("StopAuditionPlayback", BindingFlags.Instance | BindingFlags.NonPublic)!;

        string atOpen = await StartTheNextTakeWhilePlaying(() => stop.Invoke(dialog, null));

        Assert.DoesNotContain(Lexicon.Get("audio.check.playback_stopped"), atOpen);
        Assert.DoesNotContain(Lexicon.Get("audio.take.limiter_engaged"), atOpen);
        Assert.DoesNotContain("RecordingPlayer.cs", atOpen);
    }

    [Fact]
    public async Task Positive_control_the_players_ordinary_stop_at_the_same_moment_is_seen_queued_before_capture_opens()
    {
        // The arrangement the repair closed, with the player's speaking stop
        // put back in the window's place. If this ever stops seeing the
        // sentence, the test above passes for nothing.
        string atOpen = await StartTheNextTakeWhilePlaying(RecordingPlayer.Stop);

        Assert.Contains(Lexicon.Get("audio.check.playback_stopped"), atOpen);
        Assert.Contains("RecordingPlayer.cs", atOpen);
    }

    /// <summary>
    /// Play the last take's render, then start the next take through the
    /// take's own countdown-then-open path with <paramref name="beforeOpening"/>
    /// as the window's last act before the microphone opens. Returns the
    /// transcript as it stood the instant the microphone opened.
    /// </summary>
    private async Task<string> StartTheNextTakeWhilePlaying(Action beforeOpening)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced);
        Assert.True(TestSettingsRoot.Isolated);
        Assert.False(OutputChannelRecorder.RenderEnabled);

        string transcript = Path.Combine(_folder, "output.jsonl");
        var oldVerbosity = ScreenReaderOutput.CurrentVerbosity;
        // Chatty, so nothing the player asks to say is lost to the verbosity gate.
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        try
        {
            Assert.True(RecordingPlayer.PlayWithMixer(_render, "Next take positive control", null, false, _ => true));
            Assert.True(RecordingPlayer.IsPlaying, "positive control: the last take's render is playing");
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: transcript);

            bool gateReturned = false;
            string? atOpen = null;
            var recorder = new Recorder(() =>
            {
                Assert.True(gateReturned, "the microphone opened before the countdown's gate returned");
                Assert.False(RecordingPlayer.IsPlaying, "the microphone opened with the last take's render still playing");
                atOpen = ReadShared(transcript);
            });
            var take = new ChainAuditionTake(Path.Combine(_folder, "new-take"), recorder,
                ChainNotConnectedYet.Instance, _ => null);

            Assert.True(await take.StartAfterAsync(
                () => { gateReturned = true; return Task.FromResult(true); },
                () => true, () => DateTime.Now, beforeOpening));
            Assert.True(recorder.IsRunning);
            Assert.NotNull(atOpen);
            Assert.Contains("session-start", atOpen);
            Assert.False(OutputChannelRecorder.RenderEnabled);
            return atOpen!;
        }
        finally
        {
            RecordingPlayer.StopWithoutSpeaking();
            OutputChannelRecorder.Configure(render: false, record: false);
            ScreenReaderOutput.CurrentVerbosity = oldVerbosity;
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class Recorder(Action opening) : IAuditionRecorder
    {
        public bool IsRunning { get; private set; }
        public bool IsRecordingTo(string path) => IsRunning;
        public bool Start(string path) { opening(); IsRunning = true; return true; }
        public void Stop() => IsRunning = false;
    }
}
