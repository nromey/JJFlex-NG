using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Radios.Listenback;
using Xunit;

namespace JJFlexWpf.Tests;

// LEXICON_SCANNER_EXEMPT: the assertions below quote a sentence the check says, to prove it is NOT said.

/// <summary>
/// #913: Play last take right after Stop in Check your audio. The check's
/// automatic playback starts about a second and a half after Stop, while the
/// button still read "Play last take"; a press dispatched just after it began
/// became a Stop, the take stopped 51 ms in, and "Playing your take." was then
/// said over silence (the junk box's harness, 2026-11-09, check 7). And #1040:
/// a Back up first that did not finish left the check silently unstarted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless, proved:</b> an MTA thread (<see cref="NoiseReductionPerChannelTests.AssertWindowless"/>),
/// an UNINITIALIZED dialog with no button, an unconnected <see cref="FlexBase"/>,
/// render off throughout. The session is the production object, uninitialized,
/// given the fields its keyed state has after StartAsync (as
/// <see cref="ReferenceEndSpeechTests"/> builds it), and brought to its
/// pending playback through the real unkey, <c>HandleUnkey</c>.
/// </para>
/// <para>
/// <b>What these cannot see:</b> the button's text on screen, and a real radio
/// answering a playback. With no button, <c>_playTakeOffersStop</c> is what the
/// label would say (both are set from the same value in <c>SyncPlayTakeLabel</c>),
/// and an unconnected radio reports no slice playing. Bench steps in the #913 report.
/// </para>
/// </remarks>
[Collection("SliceOverwriteWarning")]
public sealed class PlayAfterStopTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const string PlayingYourTake = "Playing your take.";

    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly CheckYourAudioDialog _dialog;
    private readonly VerbosityLevel _verbosityBefore;
    private string? _transcript;

    public PlayAfterStopTests()
    {
        AssertTheDeskIsSafe();
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;   // nothing below is gated away unseen
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        _dialog = (CheckYourAudioDialog)RuntimeHelpers.GetUninitializedObject(typeof(CheckYourAudioDialog));
        Field("_rig").SetValue(_dialog, _rig);
        Field("_checkPlayTargets").SetValue(_dialog, new HashSet<SliceRecorderTarget>());
        Field("_checkStarts").SetValue(_dialog, new AudioCheckStartGate());
    }

    public void Dispose()
    {
        OutputChannelRecorder.Configure(render: false, record: false);
        if (_transcript != null) try { File.Delete(_transcript); } catch { }
        RecorderControls.FactsOverride = null;
        RecorderControls.BackUpOverride = null;
        RecorderControls.AskOverride = null;
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        Field("_session").SetValue(_dialog, null);
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        _rig.Dispose();
    }

    private static void AssertTheDeskIsSafe()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    private static FieldInfo Field(string name)
    {
        var f = typeof(CheckYourAudioDialog).GetField(name, Any);
        Assert.True(f != null, "production field CheckYourAudioDialog." + name + " is missing");
        return f!;
    }

    private static readonly Type SessionType =
        typeof(CheckYourAudioDialog).GetNestedType("AudioCheckSession", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("production type CheckYourAudioDialog.AudioCheckSession is missing");

    private static FieldInfo SessionField(string name)
    {
        var f = SessionType.GetField(name, Any);
        Assert.True(f != null, "production field AudioCheckSession." + name + " is missing");
        return f!;
    }

    private static object? Call(object target, string name, params object?[] args)
    {
        var m = target.GetType().GetMethod(name, Any);
        Assert.True(m != null, "production method " + target.GetType().Name + "." + name + " is missing");
        return m!.Invoke(target, args);
    }

    private bool OffersStop => (bool)Field("_playTakeOffersStop").GetValue(_dialog)!;
    private bool TakePlayingOrOnItsWay =>
        (bool)typeof(CheckYourAudioDialog).GetProperty("TakePlayingOrOnItsWay", Any)!.GetValue(_dialog)!;
    private static bool OnItsWay(object session) =>
        (bool)SessionType.GetProperty("AutoPlaybackOnItsWay", Any)!.GetValue(session)!;
    private static bool Active(object session) =>
        (bool)SessionType.GetProperty("Active", Any)!.GetValue(session)!;

    private void Relabel() => Call(_dialog, "SyncPlayTakeLabel");
    private void Press(bool asStop) => Call(_dialog, "PlayLastTake", asStop);
    private static void TheWatcherTicks(object session) => Call(session, "Watcher_Tick", null, EventArgs.Empty);

    /// <summary>
    /// A Record and play back check, keyed as StartAsync leaves it, as this
    /// window's session; then the operator's Stop, through the real unkey.
    /// The check's key is not held by the controller here: HandleUnkey's
    /// external form is the one a hardware or reference end takes, and lets
    /// go of nothing.
    /// </summary>
    private object TheCheckIsStopped()
    {
        var session = RuntimeHelpers.GetUninitializedObject(SessionType);
        SessionField("_owner").SetValue(session, _dialog);
        SessionField("_ptt").SetValue(session, _controller);
        SessionField("_changedRig").SetValue(session, _rig);
        SessionField("_method").SetValue(session, AudioCheckListenMethods.RecordPlayback);
        SessionField("_recorderTarget").SetValue(session, SliceRecorderTarget.Letter('A'));
        SessionField("_restores").SetValue(session, Activator.CreateInstance(SessionField("_restores").FieldType));
        SessionField("_keyedAt").SetValue(session, DateTime.UtcNow.AddSeconds(-6));
        SessionField("_watcher").SetValue(session, Activator.CreateInstance(SessionField("_watcher").FieldType));
        SessionField("_phase").SetValue(session, Enum.Parse(SessionField("_phase").FieldType, "Keyed"));
        SessionType.GetProperty("Active", Any)!.SetValue(session, true);
        Field("_session").SetValue(_dialog, session);

        Relabel();
        Assert.False(OffersStop, "positive control: while the check is keyed, the button offers Play");
        Call(session, "HandleUnkey", true);
        return session;
    }

    // ── #913, the press ──

    /// <summary>The rule itself: a press does what the button said, never the opposite.</summary>
    [Theory]
    [InlineData(false, false, "Play")]
    [InlineData(false, true, "Nothing")]   // red when a Play press on a running take became a Stop (#913)
    [InlineData(true, true, "Stop")]
    [InlineData(true, false, "Nothing")]   // and a Stop press on nothing never starts a take
    public void A_press_does_what_the_button_said_when_it_was_pressed(bool pressedAsStop, bool playing, string expected)
    {
        Assert.Equal(expected, CheckYourAudioDialog.DecidePlayTakePress(pressedAsStop, playing).ToString());
    }

    /// <summary>
    /// From the moment Stop commits the automatic playback, the button offers
    /// Stop, not a second and a half later when it sounds.
    /// </summary>
    [Fact]
    public void Stop_commits_the_playback_and_the_button_offers_Stop_at_once()
    {
        var session = TheCheckIsStopped();
        Assert.True(OnItsWay(session), "the automatic playback is on its way from the unkey");
        Assert.True(TakePlayingOrOnItsWay);
        Assert.True(OffersStop, "the button still offers Play while a playback is on its way");
    }

    /// <summary>
    /// The harness's press: Play, read while the automatic playback was on its
    /// way. It leaves that playback alone and starts nothing.
    /// </summary>
    [Fact]
    public void A_Play_press_finding_a_playback_on_its_way_leaves_it_and_starts_nothing()
    {
        var session = TheCheckIsStopped();
        Press(asStop: false);
        Assert.True(Active(session), "a Play press ended the check: it was turned into a Stop");
        Assert.True(OnItsWay(session), "a Play press abandoned the automatic playback");
        Assert.True(OffersStop);
    }

    /// <summary>A Stop press while it is on its way abandons it, and the button offers Play again.</summary>
    [Fact]
    public void A_Stop_press_abandons_the_playback_on_its_way_and_the_button_offers_Play_again()
    {
        var session = TheCheckIsStopped();
        Press(asStop: true);
        Assert.False(OnItsWay(session));
        Assert.False(Active(session), "the check went on after its playback was abandoned");
        Assert.False(OffersStop, "the button still offers Stop for a playback that was abandoned");
    }

    // ── #913, every route that abandons the pending playback puts the label back ──

    [Theory]
    [InlineData("stop-or-a-new-check")]
    [InlineData("window-closed")]
    [InlineData("radio-gone")]
    [InlineData("failed-start")]
    public void Every_route_that_abandons_the_playback_puts_the_label_back(string route)
    {
        var session = TheCheckIsStopped();
        Assert.True(OffersStop, "positive control: the pending playback is offered as Stop");
        switch (route)
        {
            case "stop-or-a-new-check":
                // Stop, and a new Ctrl+Enter, reach a live session through StopCheck.
                Call(session, "StopCheck", false);
                break;
            case "window-closed":
                // The window's Closed handler.
                Call(session, "ForceEnd", "Audio check ended", false);
                break;
            case "radio-gone":
                // The window lets go of its radio; the watcher's next tick finds it gone.
                Field("_rig").SetValue(_dialog, null);
                TheWatcherTicks(session);
                break;
            case "failed-start":
                // The radio refused the playback.
                SessionField("_playStart").SetValue(session,
                    Task.FromResult(new RecorderOutcome(false, "refused", Radios.Speech.SpeechSubject.SliceRecorder("A"))));
                TheWatcherTicks(session);
                break;
        }
        Assert.False(Active(session), "positive control: the route ended the check");
        Assert.False(OffersStop, "the button still offers Stop after the " + route + " route abandoned the playback");
    }

    // ── #913, "Playing your take." ──

    /// <summary>
    /// The radio said yes to the playback, and by the time the watcher took it
    /// up the playback was no longer running (stopped by a press, another
    /// door, or a short take at its end). "Playing your take." is not said.
    /// </summary>
    [Fact]
    public void Playing_your_take_is_not_said_for_a_playback_no_longer_running()
    {
        var session = TheCheckIsStopped();
        SessionField("_playStart").SetValue(session,
            Task.FromResult(new RecorderOutcome(true, "", Radios.Speech.SpeechSubject.SliceRecorder("A"))));
        Listen();
        ScreenReaderOutput.Speak("marker", VerbosityLevel.Terse);
        Assert.False(_rig.IsSlicePlaying(SliceRecorderTarget.Letter('A')), "positive control: nothing is playing");

        TheWatcherTicks(session);

        var targets = (HashSet<SliceRecorderTarget>)Field("_checkPlayTargets").GetValue(_dialog)!;
        Assert.Contains(SliceRecorderTarget.Letter('A'), targets);           // the success branch was reached
        Assert.False(Active(session), "positive control: the check ended after its playback was taken up");
        var said = Said();
        Assert.Contains("marker", said.Select(s => s.Text));                  // the transcript sees Terse speech
        Assert.DoesNotContain(PlayingYourTake, said.Select(s => s.Text));
    }

    // ── #1040 ──

    private const string Serial = "1234-5678-8600-1040";
    private readonly List<RecorderControls.SliceOverwriteQuestion> _asked = new();

    private void TheSliceHoldsARecording(SliceOverwriteAnswer answer, SliceBackupResult backup)
    {
        RecorderControls.FactsOverride = (rig, target) =>
            ReferenceEquals(rig, _rig) ? new SliceOverwriteFacts(Serial, "A", true, 0.1, null!, true) : rig.SliceOverwriteFactsFor(target);
        RecorderControls.AskOverride = question => { _asked.Add(question); return new SliceOverwriteReply(answer, false); };
        RecorderControls.BackUpOverride = (rig, target) => Task.FromResult<SliceBackupOutcome?>(new(backup, "A", 0.1));
    }

    private RecorderControls.OverwriteGo Confirm() =>
        RecorderControls.ConfirmOverwriteAsync(_rig, SliceRecorderTarget.Letter('A')).GetAwaiter().GetResult();

    private void TheCheckAsks() =>
        ((Task)Call(_dialog, "AskBeforeCheckOverwritesAsync", _rig)!).GetAwaiter().GetResult();

    private static string NotStarted => Lexicon.Get("audio.check.not_started_backup_unfinished");

    /// <summary>The outcome tells a backup that did not finish from the operator saying no, and from a backup that did.</summary>
    [Fact]
    public void The_outcome_marks_only_a_backup_that_did_not_finish()
    {
        TheSliceHoldsARecording(SliceOverwriteAnswer.BackUpFirst, SliceBackupResult.Incomplete);
        var unfinished = Confirm();
        Assert.False(unfinished.Proceed);
        Assert.True(unfinished.BackupUnfinished);

        TheSliceHoldsARecording(SliceOverwriteAnswer.Cancel, SliceBackupResult.Incomplete);
        var cancelled = Confirm();
        Assert.False(cancelled.Proceed);
        Assert.False(cancelled.BackupUnfinished);

        TheSliceHoldsARecording(SliceOverwriteAnswer.BackUpFirst, SliceBackupResult.BackedUp);
        var backedUp = Confirm();
        Assert.True(backedUp.Proceed);
        Assert.False(backedUp.BackupUnfinished);
        Assert.Equal(3, _asked.Count);   // positive control: the warning was asked each time
    }

    /// <summary>
    /// Back up first, and the backup could not finish (the harness's 0.1 s
    /// recording): the check says it did not start, once, queued behind the
    /// backup's own sentence so it cuts nothing.
    /// </summary>
    [Fact]
    public void A_backup_that_did_not_finish_says_the_check_did_not_start_once_and_queued()
    {
        TheSliceHoldsARecording(SliceOverwriteAnswer.BackUpFirst, SliceBackupResult.Incomplete);
        Listen();
        TheCheckAsks();
        var said = Said();
        var line = Assert.Single(said, s => s.Text == NotStarted);
        Assert.Equal("Queue", line.Intent);
        Assert.Single(_asked);
    }

    /// <summary>Cancel or Escape on the warning stays silent (#999, ruled C3).</summary>
    [Fact]
    public void Cancel_on_the_warning_says_nothing()
    {
        TheSliceHoldsARecording(SliceOverwriteAnswer.Cancel, SliceBackupResult.Incomplete);
        Listen();
        ScreenReaderOutput.Speak("marker", VerbosityLevel.Terse);
        TheCheckAsks();
        var said = Said();
        Assert.Single(_asked);                                     // positive control: the warning was asked
        Assert.Equal(new[] { "marker" }, said.Select(s => s.Text));  // and only the marker was said
    }

    // ── the transcript ──

    private void Listen()
    {
        // Withdraw what earlier tests left in the paced delivery (their checks'
        // endings), or it is delivered into this test's transcript.
        ScreenReaderOutput.Silence();
        Thread.Sleep(200);
        _transcript = Path.Combine(Path.GetTempPath(), "jjflex-play-after-stop-" + Guid.NewGuid().ToString("N") + ".jsonl");
        OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _transcript);
        Assert.False(OutputChannelRecorder.RenderEnabled, "Refusing: render is on");
    }

    private sealed record Line(string Text, string? Intent);

    /// <summary>Every sentence said since <see cref="Listen"/>, after the arbiter's salvage window.</summary>
    private List<Line> Said()
    {
        Thread.Sleep(900);   // past SpeechArbiter's 600 ms salvage settle, so a replay is counted
        using var stream = new FileStream(_transcript!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("event").GetString() == "speech"
                        && !e.GetProperty("gated").GetBoolean() && !e.GetProperty("suppressed").GetBoolean())
            .Select(e => new Line(e.GetProperty("text").GetString()!,
                e.GetProperty("intent").ValueKind == JsonValueKind.String ? e.GetProperty("intent").GetString() : null))
            .ToList();
    }
}
