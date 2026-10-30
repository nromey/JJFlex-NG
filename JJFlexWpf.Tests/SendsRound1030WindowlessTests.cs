using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using NAudio.Wave;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Check your audio #835, fix round 2026-10-30, track Sends: Enter in
/// Recorded QSOs sends while the operator holds the transmitter (ruled 23:54),
/// VOX comes back while a speaker playback is paused and pauses again before
/// it carries on (23:58), and a recording riding the lock is not cut at the
/// lock's timeout (00:00).
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless, proved rather than assumed.</b> Every test asserts it runs
/// on an MTA thread, where WPF throws on constructing any element
/// (<see cref="NoiseReductionPerChannelTests.AssertWindowless"/>), and that the
/// harness has silenced output and bound a throwaway settings root. The
/// recordings window is an UNINITIALIZED object (no constructor, no XAML, no
/// HWND); the player is fed a stand-in mixer, so no audio device opens; the
/// PTT controller drives an unconnected <see cref="FlexBase"/>, whose key
/// writes go to a queue that never runs, and its timers are driven by hand.
/// Nothing keys and nothing sounds.
/// </para>
/// <para>
/// What these cannot see: the radio confirming VOX off (stood in for here,
/// driven against a vendored radio in <c>Radios.Tests.LocalPlaybackVoxTests</c>),
/// a microphone hearing a speaker, and RF. Those are bench steps.
/// </para>
/// </remarks>
public sealed class SendsRound1030WindowlessTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjflex-sends-1030-" + Guid.NewGuid().ToString("N"));
    private readonly RecordedQsosDialog.ISendDoors _savedDoors = RecordedQsosDialog.SendDoors;

    public SendsRound1030WindowlessTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        // A red test can leave a carry-on waiting for a confirmation it will
        // never get, which holds the carry-on's static "under way" flag and
        // turns every later test's carry-on into Busy. Answer them, so one
        // red cannot paint the rest.
        foreach (var pending in _confirmations) pending.TrySetResult((null, LocalPlaybackVox.CarryOn.VoxUnconfirmed));
        foreach (var carry in _carries) { try { carry.Wait(2_000); } catch (AggregateException) { } }
        RecordingPlayer.StopWithoutSpeaking();
        LocalPlaybackVox.PauseForCarryOnOverride = null;
        LocalPlaybackVox.SpeakerTailMsOverride = null;
        LocalPlaybackVox.ConnectionOfOverride = null;
        RecordedQsosDialog.SendDoors = _savedDoors;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── Item 1, ruled 23:54: keyed, Enter in Recorded QSOs sends ─────────────

    private sealed class FakeDoors : RecordedQsosDialog.ISendDoors
    {
        public SendRoute Route = SendRoute.AsBefore;
        public readonly List<(string Path, SendRoute Route)> Sent = new();
        public SendRoute RouteFor(FlexBase rig) => Route;
        public bool ComputerRecordingInHand => false;
        public bool InFlight => false;
        public void SendComputerRecording(FlexBase rig, string path, SendRoute route) => Sent.Add((path, route));
        public bool AnswerPlayFromComputer() => false;
        public void Stop() { }
    }

    [Fact]
    public void Keyed_with_the_box_off_Enter_is_a_send_and_unkeyed_it_is_still_a_preview()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal("Send", RecordedQsosDialog.ChooseEnter(false, false, SendRoute.JoinTransmission).ToString());
        Assert.Equal("Send", RecordedQsosDialog.ChooseEnter(false, false, SendRoute.Box).ToString());
        Assert.Equal("Preview", RecordedQsosDialog.ChooseEnter(false, false, SendRoute.AsBefore).ToString());
        Assert.Equal("Preview", RecordedQsosDialog.ChooseEnter(false, false, null).ToString());
        // A preview of this window's own, and a send already in hand, answer first as before.
        Assert.Equal("PreviewTransport", RecordedQsosDialog.ChooseEnter(true, false, SendRoute.JoinTransmission).ToString());
        Assert.Equal("AnswerSend", RecordedQsosDialog.ChooseEnter(false, true, SendRoute.JoinTransmission).ToString());
    }

    /// <summary>
    /// The window's own Enter (the list's Enter, the Play button and a double
    /// click all call it) hands the selected recording to Play from computer's
    /// send on the operator's transmission: the route Ctrl+Alt+F11 reads,
    /// <see cref="SendRoute.JoinTransmission"/>, which never touches the key.
    /// </summary>
    [Fact]
    public void Holding_the_transmitter_Enter_sends_the_selected_recording_on_that_transmission()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var doors = new FakeDoors { Route = SendRoute.JoinTransmission };
        RecordedQsosDialog.SendDoors = doors;
        using var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        var dialog = (RecordedQsosDialog)RuntimeHelpers.GetUninitializedObject(typeof(RecordedQsosDialog));
        typeof(RecordedQsosDialog).GetField("_rig", Private)!.SetValue(dialog, rig);
        var browser = new RecordedQsoBrowser();
        typeof(RecordedQsosDialog).GetField("_browser", Private)!.SetValue(dialog, browser);
        string path = @"C:\nowhere\QSO-2026-10-30-000500.flac";
        browser.Select(new RecordedQso(path, "QSO-2026-10-30-000500", new DateTime(2026, 10, 30, 0, 5, 0),
            NamedByRecorder: true, Seconds: 170, Bytes: 1, SampleRate: 48000, Channels: 1, TotalFrames: 170 * 48000));

        Exception? fault = null;
        try
        {
            var task = (Task)typeof(RecordedQsosDialog).GetMethod("PlayOrPauseAsync", Private)!.Invoke(dialog, null)!;
            task.GetAwaiter().GetResult();
        }
        catch (TargetInvocationException ex) { fault = ex.InnerException ?? ex; }
        catch (Exception ex) { fault = ex; }

        Assert.True(doors.Sent.Count == 1,
            "keyed, Enter must hand the selected recording to the send; it did not"
            + (fault == null ? "." : " (it refused or went to the speakers and threw " + fault.GetType().Name + ")."));
        Assert.Equal(path, doors.Sent[0].Path);
        Assert.Equal(SendRoute.JoinTransmission, doors.Sent[0].Route);
        Assert.Null(fault);
    }

    // ── Item 3, ruled 23:58: VOX comes back while a speaker playback is paused ──

    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private readonly List<TaskCompletionSource<(IDisposable? Pause, LocalPlaybackVox.CarryOn Refusal)>> _confirmations = new();
    private readonly List<Task> _carries = new();

    /// <summary>A radio confirmation the test answers by hand, released at Dispose if it never does.</summary>
    private TaskCompletionSource<(IDisposable? Pause, LocalPlaybackVox.CarryOn Refusal)> HeldConfirmation()
    {
        var confirm = new TaskCompletionSource<(IDisposable? Pause, LocalPlaybackVox.CarryOn Refusal)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmations.Add(confirm);
        return confirm;
    }

    private Task<LocalPlaybackVox.CarryOn> StartCarryOn()
    {
        Task<LocalPlaybackVox.CarryOn> carrying = LocalPlaybackVox.CarryOnAsync();
        _carries.Add(carrying);
        return carrying;
    }

    private string Wav(double seconds, int rate = 44100)
    {
        string path = Path.Combine(_dir, "take-" + Guid.NewGuid().ToString("N") + ".wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, 1));
        var samples = Enumerable.Repeat(0.5f, (int)(seconds * rate)).ToArray();
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private void StartLookedAfterPlayback(IDisposable pause)
    {
        Assert.True(RecordingPlayer.PlayWithMixer(Wav(3), "A test recording.", null, false, _ => true));
        LocalPlaybackVox.HoldUntilPlaybackEnds(null, pause);
        Assert.True(LocalPlaybackVox.Holding);
        Assert.True(LocalPlaybackVox.Owning);
    }

    /// <summary>
    /// Paused, VOX comes back (the pause is given back); carried on, VOX is
    /// paused again and the radio's confirmation is awaited BEFORE the
    /// recording sounds; a second press meanwhile does nothing.
    /// </summary>
    [Fact]
    public async Task Paused_VOX_comes_back_and_carrying_on_pauses_it_again_before_a_sample_sounds()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        LocalPlaybackVox.SpeakerTailMsOverride = 0;
        var first = new Lease();
        StartLookedAfterPlayback(first);

        Assert.True(RecordingPlayer.Pause());
        Assert.True(first.Disposed, "paused: VOX must come back so the operator can talk on it");
        Assert.False(LocalPlaybackVox.Holding);
        Assert.True(LocalPlaybackVox.Owning, "paused is not over: carrying on must pause VOX again");

        var confirm = HeldConfirmation();
        int asked = 0;
        LocalPlaybackVox.PauseForCarryOnOverride = _ => { asked++; return confirm.Task; };
        Task<LocalPlaybackVox.CarryOn> carrying = StartCarryOn();
        Assert.Equal(1, asked);
        Assert.True(RecordingPlayer.IsPaused, "nothing may sound before the radio confirms VOX off");
        Assert.Equal(LocalPlaybackVox.CarryOn.Busy, await LocalPlaybackVox.CarryOnAsync());
        Assert.Equal(1, asked);

        var second = new Lease();
        confirm.SetResult((second, LocalPlaybackVox.CarryOn.Resumed));
        Assert.Equal(LocalPlaybackVox.CarryOn.Resumed, await carrying);
        Assert.False(RecordingPlayer.IsPaused);
        Assert.True(LocalPlaybackVox.Holding);
        Assert.False(second.Disposed);

        // And again: the next pause gives the new hold back.
        Assert.True(RecordingPlayer.Pause());
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task Refused_a_carry_on_leaves_the_recording_paused_and_says_why()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        LocalPlaybackVox.SpeakerTailMsOverride = 0;
        StartLookedAfterPlayback(new Lease());
        Assert.True(RecordingPlayer.Pause());

        LocalPlaybackVox.PauseForCarryOnOverride = _ => Task.FromResult<(IDisposable?, LocalPlaybackVox.CarryOn)>(
            (null, LocalPlaybackVox.CarryOn.Transmitting));
        Assert.Equal(LocalPlaybackVox.CarryOn.Transmitting, await LocalPlaybackVox.CarryOnAsync());
        Assert.True(RecordingPlayer.IsPaused);

        LocalPlaybackVox.PauseForCarryOnOverride = _ => Task.FromResult<(IDisposable?, LocalPlaybackVox.CarryOn)>(
            (null, LocalPlaybackVox.CarryOn.VoxUnconfirmed));
        Assert.Equal(LocalPlaybackVox.CarryOn.VoxUnconfirmed, await LocalPlaybackVox.CarryOnAsync());
        Assert.True(RecordingPlayer.IsPaused);
        Assert.False(LocalPlaybackVox.Holding);
    }

    /// <summary>
    /// Codex round 1030, B4, his schedule: pause A; ask to carry on and hold
    /// the radio's confirmation; stop A; start and pause B; then let A's
    /// confirmation arrive. The carry-on was asked for A, so it resumes
    /// nothing, B stays paused, and the pause it was handed is given back
    /// rather than kept.
    /// </summary>
    [Fact]
    public async Task A_carry_on_asked_for_one_playback_does_not_resume_its_replacement()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        LocalPlaybackVox.SpeakerTailMsOverride = 0;
        StartLookedAfterPlayback(new Lease());
        Assert.True(RecordingPlayer.Pause());

        var confirm = HeldConfirmation();
        LocalPlaybackVox.PauseForCarryOnOverride = _ => confirm.Task;
        Task<LocalPlaybackVox.CarryOn> carrying = StartCarryOn();
        Assert.False(carrying.IsCompleted, "the carry-on waits for the radio");

        RecordingPlayer.StopWithoutSpeaking();
        var bPause = new Lease();
        StartLookedAfterPlayback(bPause);
        Assert.True(RecordingPlayer.Pause());
        Assert.True(bPause.Disposed, "B is paused, so its own VOX pause has been given back");

        var aConfirmed = new Lease();
        confirm.SetResult((aConfirmed, LocalPlaybackVox.CarryOn.Resumed));
        LocalPlaybackVox.CarryOn answer = await carrying;

        Assert.True(RecordingPlayer.IsPaused, "B was never asked to carry on; it must stay paused");
        Assert.Equal(LocalPlaybackVox.CarryOn.Superseded, answer);
        Assert.True(aConfirmed.Disposed, "the pause confirmed for A is obsolete and must be given back, not kept");
        Assert.False(LocalPlaybackVox.Holding, "nothing sounds, so nothing is held");

        // Positive control: B's own carry-on still works.
        var bAgain = new Lease();
        LocalPlaybackVox.PauseForCarryOnOverride = _ => Task.FromResult<(IDisposable?, LocalPlaybackVox.CarryOn)>(
            (bAgain, LocalPlaybackVox.CarryOn.Resumed));
        Assert.Equal(LocalPlaybackVox.CarryOn.Resumed, await LocalPlaybackVox.CarryOnAsync());
        Assert.False(RecordingPlayer.IsPaused);
        Assert.False(bAgain.Disposed);
    }

    /// <summary>
    /// The same deferred state, one door over: while a carry-on waits for the
    /// radio, a resume by some other door still finds no VOX pause held, and
    /// is paused again. The wait is not a licence to sound.
    /// </summary>
    [Fact]
    public async Task A_resume_by_another_door_while_a_carry_on_waits_is_paused_again()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        LocalPlaybackVox.SpeakerTailMsOverride = 0;
        StartLookedAfterPlayback(new Lease());
        Assert.True(RecordingPlayer.Pause());

        var confirm = HeldConfirmation();
        LocalPlaybackVox.PauseForCarryOnOverride = _ => confirm.Task;
        Task<LocalPlaybackVox.CarryOn> carrying = StartCarryOn();

        Assert.True(RecordingPlayer.Resume());
        Assert.True(RecordingPlayer.IsPaused, "the radio has not confirmed VOX off; nothing may sound yet");

        var confirmed = new Lease();
        confirm.SetResult((confirmed, LocalPlaybackVox.CarryOn.Resumed));
        Assert.Equal(LocalPlaybackVox.CarryOn.Resumed, await carrying);
        Assert.False(RecordingPlayer.IsPaused);
        Assert.False(confirmed.Disposed);
    }

    /// <summary>
    /// B4's other two identities: the radio's connection changes while the
    /// carry-on waits (a reconnect, even to the same radio). Nothing resumes
    /// and the confirmed pause is given back; the same playback can then be
    /// carried on again on the new connection.
    /// </summary>
    [Fact]
    public async Task A_carry_on_across_a_connection_change_resumes_nothing_and_gives_its_pause_back()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        LocalPlaybackVox.SpeakerTailMsOverride = 0;
        using var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        object connection = new();
        LocalPlaybackVox.ConnectionOfOverride = r => ReferenceEquals(r, rig) ? connection : null;
        Assert.True(RecordingPlayer.PlayWithMixer(Wav(3), "A test recording.", null, false, _ => true));
        LocalPlaybackVox.HoldUntilPlaybackEnds(rig, new Lease());
        Assert.True(RecordingPlayer.Pause());

        var confirm = HeldConfirmation();
        FlexBase? asked = null;
        LocalPlaybackVox.PauseForCarryOnOverride = r => { asked = r; return confirm.Task; };
        Task<LocalPlaybackVox.CarryOn> carrying = StartCarryOn();
        Assert.Same(rig, asked);

        connection = new object(); // reconnected while the radio was confirming
        var confirmed = new Lease();
        confirm.SetResult((confirmed, LocalPlaybackVox.CarryOn.Resumed));

        Assert.Equal(LocalPlaybackVox.CarryOn.Superseded, await carrying);
        Assert.True(RecordingPlayer.IsPaused);
        Assert.True(confirmed.Disposed, "a pause confirmed on the old connection is obsolete");
        Assert.False(LocalPlaybackVox.Holding);

        // Positive control: on the new connection, carrying on works.
        var fresh = new Lease();
        LocalPlaybackVox.PauseForCarryOnOverride = _ => Task.FromResult<(IDisposable?, LocalPlaybackVox.CarryOn)>(
            (fresh, LocalPlaybackVox.CarryOn.Resumed));
        Assert.Equal(LocalPlaybackVox.CarryOn.Resumed, await LocalPlaybackVox.CarryOnAsync());
        Assert.False(RecordingPlayer.IsPaused);
        Assert.False(fresh.Disposed);
    }

    /// <summary>
    /// The rule is "VOX is never live while the speakers play": a resume that
    /// bypasses the carry-on finds no pause held and is paused again on the spot.
    /// </summary>
    [Fact]
    public void A_resume_that_skips_the_carry_on_is_paused_again()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        LocalPlaybackVox.SpeakerTailMsOverride = 0;
        StartLookedAfterPlayback(new Lease());
        Assert.True(RecordingPlayer.Pause());

        Assert.True(RecordingPlayer.Resume());
        Assert.True(RecordingPlayer.IsPaused, "a looked-after recording may not sound with VOX on");

        RecordingPlayer.StopWithoutSpeaking();
        Assert.False(LocalPlaybackVox.Owning, "stopped: the playback is forgotten");
    }

    /// <summary>
    /// The pause gives VOX back only once the recording's fade has left the
    /// speakers, not at the press.
    /// </summary>
    [Fact]
    public async Task VOX_comes_back_only_after_the_speakers_tail()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        LocalPlaybackVox.SpeakerTailMsOverride = 200;
        var held = new Lease();
        StartLookedAfterPlayback(held);
        Assert.True(RecordingPlayer.Pause());
        Assert.False(held.Disposed, "the fade is still in the speakers");
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!held.Disposed && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(held.Disposed);
    }

    // ── Item 4, ruled 00:00: a recording riding the lock is not cut at its timeout ──

    private static PttSafetyController Locked(FlexBase rig)
    {
        var ctl = new PttSafetyController(() => rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        ctl.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, ctl.State);
        return ctl;
    }

    /// <summary>Put the lock's clock <paramref name="seconds"/> into the past, as if held that long by hand.</summary>
    private static void HeldFor(PttSafetyController ctl, int seconds)
    {
        var clock = (LockTimeoutClock)typeof(PttSafetyController).GetField("_lockClock", Private)!.GetValue(ctl)!;
        clock.Start(Environment.TickCount64 - seconds * 1000L);
    }

    private static void Tick(PttSafetyController ctl) =>
        typeof(PttSafetyController).GetMethod("WarningTimerTick", Private)!.Invoke(ctl, new object?[] { null, EventArgs.Empty });

    [Fact]
    public void Past_the_timeout_a_riding_recording_keeps_the_lock_and_the_operators_own_time_still_ends_it()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        var ctl = Locked(rig);
        try
        {
            HeldFor(ctl, 200);   // the default timeout is 180 s
            ctl.BeginRecordingSendRide("the computer recording bench");
            Tick(ctl);
            Assert.Equal(PttSafetyController.PttState.Locked, ctl.State);

            ctl.EndRecordingSendRide("the computer recording bench");
            Tick(ctl);
            Assert.Equal(PttSafetyController.PttState.Idle, ctl.State);   // timed out on the operator's own time
        }
        finally
        {
            if (ctl.IsTransmitting) ctl.EscapeUnlock();
            ctl.Dispose();
        }
    }

    /// <summary>
    /// A ride whose send ended without saying so cannot leave the lock's
    /// timeout standing down: the ladder asks the send at every tick.
    /// </summary>
    [Fact]
    public void A_ride_whose_send_has_ended_is_ended_by_the_ladder_and_the_timeout_applies()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        var ctl = Locked(rig);
        try
        {
            bool going = true;
            HeldFor(ctl, 200);
            ctl.BeginRecordingSendRide("the computer recording bench", () => going);
            Tick(ctl);
            Assert.Equal(PttSafetyController.PttState.Locked, ctl.State);

            going = false;   // the send is gone, and nobody told the controller
            Tick(ctl);
            Assert.Equal(PttSafetyController.PttState.Idle, ctl.State);
        }
        finally
        {
            if (ctl.IsTransmitting) ctl.EscapeUnlock();
            ctl.Dispose();
        }
    }

    [Fact]
    public void A_warning_stands_down_for_the_recording_and_the_clock_carries_on_from_where_it_stood()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        var ctl = Locked(rig);
        try
        {
            HeldFor(ctl, 170);   // inside the 15-second warning
            Tick(ctl);
            Assert.Equal(PttSafetyController.PttState.Warning2, ctl.State);

            ctl.BeginRecordingSendRide("the computer recording bench");
            Assert.Equal(PttSafetyController.PttState.Locked, ctl.State);
            Tick(ctl);
            Assert.Equal(PttSafetyController.PttState.Locked, ctl.State);

            ctl.EndRecordingSendRide("the computer recording bench");
            Tick(ctl);
            // Not a fresh three minutes (that would read Locked), and not cut:
            // back in the warning the operator's own 170 seconds had reached.
            Assert.Equal(PttSafetyController.PttState.Warning2, ctl.State);
        }
        finally
        {
            if (ctl.IsTransmitting) ctl.EscapeUnlock();
            ctl.Dispose();
        }
    }
}
