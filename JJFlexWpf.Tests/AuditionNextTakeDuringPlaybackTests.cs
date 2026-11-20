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
/// The next audition take opens its microphone only when nothing of ours is
/// sounding, and with nothing of the app's still to be said (#262, Sprint 49
/// Track Q2 repairs 1 and 2; Noel 2026-11-19 12:22).
/// </summary>
/// <remarks>
/// <para>
/// Repair 1: the window stopped a render still playing just before capture
/// opened, AFTER the countdown's gate, and the player's ordinary stop queued
/// "Playback stopped" at that instant, so the sentence could land in the take
/// (Codex's check of <c>dd7c03b5</c>, whose arrangement this adopts). Repair 2:
/// it stopped only a render of the audition's own, so an Off the air take went
/// on playing into the capture, and its stop could still say the limiter
/// notice. Now every playback stops, saying nothing at all, and the transmit
/// tone's monitor with it.
/// </para>
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
/// <b>What these cannot see:</b> a radio's slice playback (no radio here; its
/// stop is source-pinned in <c>CheckYourAudioAuditionWiringTests</c>), what
/// NVDA or JAWS actually say, and sound in the room. Those are the harness walk's.
/// </para>
/// </remarks>
public sealed class AuditionNextTakeDuringPlaybackTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jjflex-audition-next-take-" + Guid.NewGuid().ToString("N"));
    private readonly string _render;
    private readonly string _offAir;

    public AuditionNextTakeDuringPlaybackTests()
    {
        Directory.CreateDirectory(_folder);
        // A render must sit in the audition's own folder: that is what made
        // the old stop recognise it as a render of ours.
        Directory.CreateDirectory(ChainAuditionFolder.DefaultPath);
        _render = Path.Combine(ChainAuditionFolder.DefaultPath, "next-take-" + Guid.NewGuid().ToString("N") + ".wav");
        Write(_render, 0f);
        // An Off the air take is anywhere else, and loud enough that its +30 dB
        // listening gain reaches the limiter once a little of it has been read.
        _offAir = Path.Combine(_folder, "heard.wav");
        Write(_offAir, 0.5f);
    }

    public void Dispose()
    {
        RecordingPlayer.StopWithoutSpeaking();
        OutputChannelRecorder.Configure(render: false, record: false);
        try { File.Delete(_render); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── What the window does ─────────────────────────────────────────

    [Fact]
    public async Task A_next_take_started_while_a_render_plays_opens_with_nothing_of_the_players_queued()
    {
        string atOpen = await StartTheNextTakeWhilePlaying(_render, offAirGain: false, WindowsQuietStep());

        AssertNothingOfThePlayers(atOpen);
    }

    [Fact]
    public async Task A_next_take_started_while_an_Off_the_air_take_plays_stops_it_saying_nothing_not_even_the_limiter()
    {
        string atOpen = await StartTheNextTakeWhilePlaying(_offAir, offAirGain: true, WindowsQuietStep());

        AssertNothingOfThePlayers(atOpen);
        Assert.DoesNotContain(Lexicon.Get("audio.take.playback_finished"), atOpen);
    }

    [Fact]
    public void The_transmit_tones_monitor_is_stopped_before_the_take_and_the_take_may_open()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var dialog = Dialog();
        typeof(CheckYourAudioDialog).GetField("_toneMonitorSounding", Private)!.SetValue(dialog, true);
        Assert.True((bool)typeof(CheckYourAudioDialog).GetProperty("CheckPlaybackRunning", Private)!.GetValue(dialog)!,
            "positive control: the window counts its tone monitor as sounding");

        Assert.True(Quiet(dialog));
        Assert.False((bool)typeof(CheckYourAudioDialog).GetField("_toneMonitorSounding", Private)!.GetValue(dialog)!);
    }

    // ── Positive controls: the instrument sees what the window must not do ──

    [Fact]
    public async Task Positive_control_the_players_ordinary_stop_at_the_same_moment_is_seen_queued_before_capture_opens()
    {
        // The arrangement repair 1 closed, with the player's speaking stop put
        // back in the window's place. If this ever stops seeing the sentence,
        // the tests above pass for nothing.
        string atOpen = await StartTheNextTakeWhilePlaying(_render, offAirGain: false,
            () => { RecordingPlayer.Stop(); return true; });

        Assert.Contains(Lexicon.Get("audio.check.playback_stopped"), atOpen);
        Assert.Contains("RecordingPlayer.cs", atOpen);
    }

    [Fact]
    public async Task Positive_control_an_Off_the_air_take_here_has_reached_the_limiter_and_a_stop_without_its_sentence_still_says_so()
    {
        // StopWithoutSpeaking leaves out only the stop sentence: this proves
        // the Off the air take above really had reached the limiter, so its
        // notice was due and the window's stop is what kept it out.
        string atOpen = await StartTheNextTakeWhilePlaying(_offAir, offAirGain: true,
            () => { RecordingPlayer.StopWithoutSpeaking(); return true; });

        Assert.Contains(Lexicon.Get("audio.take.limiter_engaged"), atOpen);
        Assert.DoesNotContain(Lexicon.Get("audio.check.playback_stopped"), atOpen);
    }

    // ── The arrangement ──────────────────────────────────────────────

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static CheckYourAudioDialog Dialog() =>
        (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));

    private static bool Quiet(CheckYourAudioDialog dialog) =>
        (bool)typeof(CheckYourAudioDialog).GetMethod("QuietBeforeTake", Private)!.Invoke(dialog, null)!;

    /// <summary>The window's own last act before the microphone opens.</summary>
    private static Func<bool> WindowsQuietStep()
    {
        var dialog = Dialog();
        return () => Quiet(dialog);
    }

    private static void AssertNothingOfThePlayers(string atOpen)
    {
        Assert.DoesNotContain(Lexicon.Get("audio.check.playback_stopped"), atOpen);
        Assert.DoesNotContain(Lexicon.Get("audio.take.limiter_engaged"), atOpen);
        Assert.DoesNotContain(Lexicon.Get("audio.audition.not_quiet"), atOpen);
        Assert.DoesNotContain("RecordingPlayer.cs", atOpen);
    }

    /// <summary>
    /// Play <paramref name="path"/>, then start the next take through the
    /// take's own countdown-then-open path with <paramref name="beforeOpening"/>
    /// as the window's last act before the microphone opens. Returns the
    /// transcript as it stood the instant the microphone opened.
    /// </summary>
    private async Task<string> StartTheNextTakeWhilePlaying(string path, bool offAirGain, Func<bool> beforeOpening)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced);
        Assert.True(TestSettingsRoot.Isolated);
        Assert.False(OutputChannelRecorder.RenderEnabled);

        string transcript = Path.Combine(_folder, "output-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var oldVerbosity = ScreenReaderOutput.CurrentVerbosity;
        // Chatty, so nothing the player asks to say is lost to the verbosity gate.
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        try
        {
            ISampleProvider? mixed = null;
            Assert.True(RecordingPlayer.PlayWithMixer(path, "Next take positive control", null, offAirGain,
                p => { mixed = p; return true; }));
            Assert.True(RecordingPlayer.IsPlaying, "positive control: the playback is playing");
            // A little of it is heard, as a mixer would read it.
            var buffer = new float[4410];
            mixed!.Read(buffer.AsSpan());
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: transcript);

            bool gateReturned = false;
            string? atOpen = null;
            var recorder = new Recorder(() =>
            {
                Assert.True(gateReturned, "the microphone opened before the countdown's gate returned");
                Assert.False(RecordingPlayer.IsPlaying, "the microphone opened with a playback still playing");
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

    private static void Write(string path, float level)
    {
        using var w = new WaveFileWriter(path, new WaveFormat(48000, 16, 1));
        var samples = new float[48000];
        for (int i = 0; i < samples.Length; i++) samples[i] = (i & 1) == 0 ? level : -level;
        w.WriteSamples(samples, 0, samples.Length);
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
        public bool Start(string path, TimeSpan fileStartsAfter) { opening(); IsRunning = true; return true; }
        public void Stop() => IsRunning = false;
    }
}
