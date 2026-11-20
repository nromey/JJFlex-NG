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
/// A render's playback request still waiting for its confirmation click when a
/// take starts is void: once the microphone is open it neither plays nor says
/// anything (#262, Sprint 49 Track Q2 repair 3; Noel 2026-11-19 12:22).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect, from Codex's re-check of <c>f9b67186</c>, whose reproduction
/// and positive control this adopts.</b> A press of Play waits 240 ms for its
/// click before it plays, asking the window only whether it is closing and the
/// radio is the same. The window's last act before the microphone opens stops
/// whatever is PLAYING, and a request still waiting is not playing. So Play then
/// Start inside the click, with the countdown off, opened the take first; the
/// request then found its render cleared by the new take and refused with an
/// earcon and a Critical interrupting sentence, inside the capture. A request
/// whose file survived would have started playing there instead.
/// </para>
/// <para>
/// <b>Windowless and silent.</b> Every test asserts
/// <see cref="NoiseReductionPerChannelTests.AssertWindowless"/> (an MTA
/// thread), <see cref="QuietRun.Silenced"/>, an isolated settings root and
/// render off. The playback request is the REAL
/// <see cref="ListenbackTakes.PlayAfterClickAsync"/>; the take is the REAL
/// <see cref="ChainAuditionTake.StartAfterAsync"/>; the window's predicate and
/// last act before opening are its own, on an uninitialized dialog whose
/// constructor never runs; the recorder is a fake that opens no microphone.
/// <b>The render is always gone by the time the request asks</b> (the new take
/// clears it), so even the positive control, and any mutation of the fix,
/// reaches only the player's refusal and never a sound device. What the app
/// asks to say or sound is read from the output transcript, never from a reader.
/// </para>
/// </remarks>
public sealed class AuditionPendingPlaybackTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jjflex-audition-pending-" + Guid.NewGuid().ToString("N"));
    private readonly string _transcript;

    public AuditionPendingPlaybackTests()
    {
        Directory.CreateDirectory(_folder);
        _transcript = Path.Combine(Path.GetTempPath(), "jjflex-audition-pending-" + Guid.NewGuid().ToString("N") + ".jsonl");
    }

    public void Dispose()
    {
        RecordingPlayer.StopForCapture();
        OutputChannelRecorder.Configure(render: false, record: false);
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { File.Delete(_transcript); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── What the window does ─────────────────────────────────────────

    [Fact]
    public async Task A_render_asked_for_before_the_take_neither_plays_nor_speaks_once_the_microphone_is_open()
    {
        var dialog = Dialog();
        var after = await PressPlayThenStart(dialog, WindowsPredicate(dialog));

        // What was asked to be said or sounded with the microphone open first,
        // since that is the defect; then that the player was never reached.
        Assert.DoesNotContain(Refusal, after.SinceOpen);
        Assert.DoesNotContain("LeaderInvalidTone", after.SinceOpen);
        Assert.DoesNotContain("RecordingPlayer.cs", after.SinceOpen);
        Assert.False(after.Played, "the waiting request still started, or tried to start, a playback");
        Assert.False(RecordingPlayer.IsPlaying);
    }

    [Fact]
    public void The_windows_predicate_says_wanted_until_a_take_opens_and_a_later_press_is_wanted_again()
    {
        // Positive control for the test above: it does not pass because the
        // window's predicate is simply false. And the fence voids requests
        // made BEFORE it, not every request ever after.
        NoiseReductionPerChannelTests.AssertWindowless();
        var dialog = Dialog();
        var before = WindowsPredicate(dialog);
        Assert.True(before(null), "a press with nothing since is wanted");

        Assert.True(Quiet(dialog));

        Assert.False(before(null), "a press from before the take is void");
        Assert.True(WindowsPredicate(dialog)(null), "a press after the fence is a press of its own");
    }

    // ── Positive controls: the instrument sees what the window must not do ──

    [Fact]
    public async Task Positive_control_a_request_that_ignores_the_take_refuses_aloud_while_the_microphone_is_open()
    {
        // Codex's reproduction, as found: a predicate that asks only about
        // closing and the radio, which is what the window used to pass. If this
        // ever stops seeing the refusal, the test above passes for nothing.
        var after = await PressPlayThenStart(Dialog(), _ => true);

        Assert.True(after.Played, "positive control: the request went on to the player");
        Assert.Contains(Refusal, after.SinceOpen);
        Assert.Contains("RecordingPlayer.cs", after.SinceOpen);
        Assert.Contains("LeaderInvalidTone", after.SinceOpen);
    }

    [Fact]
    public async Task Positive_control_a_revoked_request_is_silent()
    {
        // Codex's revoked-predicate control: a request its predicate turns down
        // after the click reaches neither the player nor the transcript.
        var after = await PressPlayThenStart(Dialog(), _ => false);

        Assert.False(after.Played);
        Assert.DoesNotContain(Refusal, after.SinceOpen);
        Assert.DoesNotContain("RecordingPlayer.cs", after.SinceOpen);
    }

    // ── The arrangement ──────────────────────────────────────────────

    private static string Refusal => Lexicon.Get("audio.take.play_file_gone", ("file", "raw"));

    private static CheckYourAudioDialog Dialog() =>
        (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));

    private static bool Quiet(CheckYourAudioDialog dialog) =>
        (bool)typeof(CheckYourAudioDialog).GetMethod("QuietBeforeTake", Private)!.Invoke(dialog, null)!;

    /// <summary>The predicate the window hands a render's playback request, taken at the press.</summary>
    private static Func<FlexBase?, bool> WindowsPredicate(CheckYourAudioDialog dialog) =>
        (Func<FlexBase?, bool>)typeof(CheckYourAudioDialog).GetMethod("AuditionPlaybackStillWanted", Private)!
            .Invoke(dialog, null)!;

    private sealed record After(bool Played, string SinceOpen);

    /// <summary>
    /// Press Play on a render (the real request, with <paramref name="stillWanted"/>
    /// as its predicate), then start the next take before its click has finished,
    /// with the window's own last act before opening. Returns whether the request
    /// went on to the player, and everything the transcript gained after the
    /// microphone opened, read once the request has finished.
    /// </summary>
    private async Task<After> PressPlayThenStart(CheckYourAudioDialog dialog, Func<FlexBase?, bool> stillWanted)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced);
        Assert.True(TestSettingsRoot.Isolated);
        Assert.False(OutputChannelRecorder.RenderEnabled);
        RecordingPlayer.StopForCapture();

        string render = Path.Combine(_folder, "raw.wav");
        using (var wav = new WaveFileWriter(render, new WaveFormat(48000, 16, 1)))
            wav.WriteSamples(new float[48000], 0, 48000);

        var oldVerbosity = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _transcript);
        try
        {
            bool asked = false, said = false;
            Task<bool> pending = ListenbackTakes.PlayAfterClickAsync(render, "A test render.", null,
                rig => { asked = true; said = stillWanted(rig); return said; });
            Assert.False(pending.IsCompleted, "the request is waiting for its click");
            Assert.False(RecordingPlayer.IsPlaying);

            var recorder = new Recorder();
            var take = new ChainAuditionTake(_folder, recorder, ChainNotConnectedYet.Instance, _ => null);
            Assert.True(await take.StartAfterAsync(() => Task.FromResult(true), () => true, () => DateTime.Now,
                beforeOpening: () => Quiet(dialog)));
            Assert.True(recorder.IsRunning);
            Assert.False(asked, "the click finished before the microphone opened: the arrangement did not race");
            Assert.False(File.Exists(render), "starting the next take cleared the previous render");
            int atOpen = ReadShared(_transcript).Length;

            Assert.False(await pending);
            Assert.True(asked, "the request asked its predicate after the click");
            Assert.True(recorder.IsRunning, "the microphone is still open as the request finishes");
            Assert.False(OutputChannelRecorder.RenderEnabled);
            return new After(said, ReadShared(_transcript).Substring(atOpen));
        }
        finally
        {
            RecordingPlayer.StopForCapture();
            OutputChannelRecorder.Configure(render: false, record: false);
            ScreenReaderOutput.CurrentVerbosity = oldVerbosity;
        }
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class Recorder : IAuditionRecorder
    {
        public bool IsRunning { get; private set; }
        public bool IsRecordingTo(string path) => IsRunning;
        public bool Start(string path, bool holdFileStart) { IsRunning = true; return true; }
        public void BeginFileAt(string path, long? stopwatchTimestamp) { }
        public void Stop() => IsRunning = false;
    }
}
