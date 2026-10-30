using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Enter in Recorded QSOs follows the VOX send box like Play from computer
/// (#835, fix round 2026-10-29; bench 01:40-01:42: Enter there "ducked the
/// static and played to speakers" with the box ticked and VOX on). Box on and
/// VOX on, Enter is a send through Play from computer's send; otherwise it
/// previews on the speakers as before. Enter pauses and carries on a computer
/// recording's send, and Escape stops any send in hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> The rule is a pure function. The wiring tests drive the
/// window's own Enter and Escape handlers on an UNINITIALIZED dialog
/// (<see cref="RuntimeHelpers.GetUninitializedObject"/>, as
/// <see cref="RecorderRoundEightTests"/> does), so no constructor runs and no
/// window is ever made; the radio is an offline <see cref="FlexBase"/>, and the
/// send doors are a recording stand-in, so nothing keys and no audio device
/// opens.
/// </para>
/// <para>
/// What these cannot see: that Enter reaches <c>PlayOrPauseAsync</c> from the
/// list (the list's own key handler, unchanged), that Escape reaches
/// <c>StopSendOnEscape</c> through the window's key handler with the dialog
/// transmit door declining it, and anything the send itself does on a radio.
/// Those are bench steps.
/// </para>
/// </remarks>
public sealed class RecordedQsoEnterSendTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private sealed class FakeDoors : RecordedQsosDialog.ISendDoors
    {
        public SendRoute Route = SendRoute.AsBefore;
        public bool ComputerInHand;
        public bool AnyInFlight;
        public readonly List<(string Path, SendRoute Route)> Sent = new();
        public int Answered, Stopped, RouteAsked;

        public SendRoute RouteFor(FlexBase rig) { RouteAsked++; return Route; }
        public bool ComputerRecordingInHand => ComputerInHand;
        public bool InFlight => AnyInFlight;
        public void SendComputerRecording(FlexBase rig, string path, SendRoute route) => Sent.Add((path, route));
        public bool AnswerPlayFromComputer() { Answered++; return true; }
        public void Stop() => Stopped++;
    }

    [Theory]
    // The preview this window started answers first, whatever the box says.
    [InlineData(true, false, SendRoute.Box, "PreviewTransport")]
    [InlineData(true, true, SendRoute.Box, "PreviewTransport")]
    [InlineData(true, false, null, "PreviewTransport")]
    // A computer recording's send in hand: Enter pauses or carries it on.
    [InlineData(false, true, SendRoute.Box, "AnswerSend")]
    [InlineData(false, true, SendRoute.AsBefore, "AnswerSend")]
    [InlineData(false, true, SendRoute.JoinTransmission, "AnswerSend")]
    // The box rule: ticked and VOX on, Enter is a send.
    [InlineData(false, false, SendRoute.Box, "Send")]
    // Keyed (push to talk, the lock, a hardware line), Enter sends on that
    // transmission, as Ctrl+Alt+F11 does (ruled 2026-10-29 23:54; it refused
    // until the fix round of 2026-10-30, SendsRound1030WindowlessTests).
    [InlineData(false, false, SendRoute.JoinTransmission, "Send")]
    // Otherwise the preview, as before the box.
    [InlineData(false, false, SendRoute.AsBefore, "Preview")]
    [InlineData(false, false, null, "Preview")]
    public void Enter_follows_the_box_rule(bool previewActive, bool computerSendInHand, SendRoute? route,
        string expected)
    {
        // A string, because the act is internal to the app and a public test
        // method cannot take it as a parameter.
        Assert.Equal(expected, RecordedQsosDialog.ChooseEnter(previewActive, computerSendInHand, route).ToString());
    }

    [Fact]
    public void Enter_with_the_box_ticked_and_VOX_on_sends_the_selected_recording_through_Play_from_computers_send()
    {
        var doors = new FakeDoors { Route = SendRoute.Box };
        OnSta(doors, (dialog, rig) =>
        {
            var q = Recording(@"C:\nowhere\QSO-2026-10-29-014000.flac");
            Browser(dialog).Select(q);
            Exception? fault = PressEnter(dialog);

            Assert.True(doors.Sent.Count == 1,
                "Enter under the box must hand the selected recording to the send; it did not"
                + (fault == null ? "." : " (it went to the speakers' path and threw " + fault.GetType().Name + ")."));
            Assert.Equal(q.Path, doors.Sent[0].Path);
            Assert.Equal(SendRoute.Box, doors.Sent[0].Route);
            Assert.Equal(0, doors.Answered);
            Assert.Null(fault);
        });
    }

    [Fact]
    public void Enter_on_a_computer_recordings_send_in_hand_pauses_or_carries_it_on_and_starts_nothing_new()
    {
        var doors = new FakeDoors { Route = SendRoute.Box, ComputerInHand = true, AnyInFlight = true };
        OnSta(doors, (dialog, rig) =>
        {
            Browser(dialog).Select(Recording(@"C:\nowhere\QSO-2026-10-29-014100.flac"));
            Exception? fault = PressEnter(dialog);

            Assert.True(doors.Answered == 1,
                "Enter on the send in hand must answer it as Play from computer does"
                + (fault == null ? "." : " (it threw " + fault.GetType().Name + ")."));
            Assert.Empty(doors.Sent);
            Assert.Null(fault);
        });
    }

    [Fact]
    public void Enter_under_the_box_with_nothing_selected_sends_nothing()
    {
        var doors = new FakeDoors { Route = SendRoute.Box };
        OnSta(doors, (dialog, rig) =>
        {
            // The refusal writes the status line before it sounds or speaks,
            // and an uninitialized window has none, so reaching it throws
            // there, with nothing played and nothing spoken. The stack names
            // the method, which proves the refusal was reached and not some
            // other failure (Codex round 1: this used to discard the fault).
            Exception? fault = PressEnter(dialog);
            Assert.Empty(doors.Sent);
            Assert.IsType<NullReferenceException>(fault);
            Assert.Contains("RecordedQsosDialog.Refuse", fault!.StackTrace);
            Assert.Contains("RecordedQsosDialog.SendSelected", fault.StackTrace);
        });
    }

    /// <summary>
    /// The Stop button's handler (the button itself exists only in the
    /// constructor, which an uninitialized window never ran): a send in hand
    /// stops, as S does.
    /// </summary>
    [Fact]
    public void The_Stop_button_during_a_send_stops_the_send()
    {
        var doors = new FakeDoors { AnyInFlight = true };
        OnSta(doors, (dialog, rig) =>
        {
            typeof(RecordedQsosDialog).GetMethod("StopPressed", Private)!.Invoke(dialog, new object[] { "the Stop button" });
            Assert.Equal(1, doors.Stopped);
        });
    }

    [Fact]
    public void Escape_stops_a_send_in_hand_and_leaves_the_window_alone_when_there_is_none()
    {
        var doors = new FakeDoors();
        OnSta(doors, (dialog, rig) =>
        {
            Assert.False(dialog.StopSendOnEscape());
            Assert.Equal(0, doors.Stopped);

            doors.AnyInFlight = true;
            Assert.True(dialog.StopSendOnEscape());
            Assert.Equal(1, doors.Stopped);
        });
    }

    /// <summary>
    /// Codex round 1 (2026-10-29): during a send Enter started, S stopped
    /// nothing: it went to the preview's StopToStart, which only moved the
    /// play head. These drive the window's real key handler, OnPreviewKeyDown,
    /// with a real key event, so they hold the routing and not just a method.
    /// </summary>
    [Fact]
    public void S_during_a_send_stops_the_send_through_the_windows_own_key_handler()
    {
        var doors = new FakeDoors { AnyInFlight = true, ComputerInHand = true };
        OnSta(doors, (dialog, rig) =>
        {
            Browser(dialog).Select(Recording(@"C:\nowhere\QSO-2026-10-29-024000.flac"));
            var (handled, fault) = PressKey(dialog, System.Windows.Input.Key.S);
            Assert.True(doors.Stopped == 1,
                "S during a send must stop the send; it did not"
                + (fault == null ? "." : " (it went to the preview's stop and threw " + fault.GetType().Name + ")."));
            Assert.True(handled, "S must be consumed");
            Assert.Null(fault);
        });
    }

    [Fact]
    public void Escape_during_a_send_stops_it_through_the_windows_own_key_handler_and_is_consumed()
    {
        var doors = new FakeDoors { AnyInFlight = true };
        OnSta(doors, (dialog, rig) =>
        {
            var (handled, fault) = PressKey(dialog, System.Windows.Input.Key.Escape);
            Assert.Null(fault);
            Assert.Equal(1, doors.Stopped);
            Assert.True(handled, "consumed, so the base dialog's Escape does not close the window");
        });
    }

    // ── harness ─────────────────────────────────────────────────────────

    private static RecordedQso Recording(string path) =>
        new(path, System.IO.Path.GetFileNameWithoutExtension(path), new DateTime(2026, 10, 29, 1, 40, 0),
            NamedByRecorder: true, Seconds: 170, Bytes: 1, SampleRate: 48000, Channels: 1, TotalFrames: 170 * 48000);

    private static RecordedQsoBrowser Browser(RecordedQsosDialog dialog) =>
        (RecordedQsoBrowser)typeof(RecordedQsosDialog).GetField("_browser", Private)!.GetValue(dialog)!;

    /// <summary>
    /// A key event must name the presentation source it came from. This one
    /// is windowless: no HWND, no visual, nothing on the desktop.
    /// </summary>
    private sealed class NoWindowSource : System.Windows.PresentationSource
    {
        protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null!;
        public override System.Windows.Media.Visual RootVisual { get => null!; set { } }
        public override bool IsDisposed => false;
    }

    /// <summary>
    /// A real key event through the window's own <c>OnPreviewKeyDown</c>, the
    /// handler WPF calls for a key pressed anywhere in the window, after the
    /// dialog transmit door. Its source is <see cref="NoWindowSource"/>, and
    /// the event carries its routed event so Handled can be set as WPF sets it.
    /// </summary>
    private static (bool Handled, Exception? Fault) PressKey(RecordedQsosDialog dialog, System.Windows.Input.Key key)
    {
        var e = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new NoWindowSource(), 0, key)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
        };
        try
        {
            typeof(RecordedQsosDialog).GetMethod("OnPreviewKeyDown", Private)!.Invoke(dialog, new object[] { e });
            return (e.Handled, null);
        }
        catch (TargetInvocationException ex) { return (e.Handled, ex.InnerException ?? ex); }
        catch (Exception ex) { return (e.Handled, ex); }
    }

    /// <summary>The window's Enter: the same method the list's Enter, the Play button and a double click call.</summary>
    private static Exception? PressEnter(RecordedQsosDialog dialog)
    {
        try
        {
            var task = (Task)typeof(RecordedQsosDialog).GetMethod("PlayOrPauseAsync", Private)!.Invoke(dialog, null)!;
            task.GetAwaiter().GetResult();
            return null;
        }
        catch (TargetInvocationException ex) { return ex.InnerException ?? ex; }
        catch (Exception ex) { return ex; }
    }

    private static void OnSta(FakeDoors doors, Action<RecordedQsosDialog, FlexBase> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var saved = RecordedQsosDialog.SendDoors;
            try
            {
                RecordedQsosDialog.SendDoors = doors;
                using var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
                var dialog = (RecordedQsosDialog)RuntimeHelpers.GetUninitializedObject(typeof(RecordedQsosDialog));
                typeof(RecordedQsosDialog).GetField("_rig", Private)!.SetValue(dialog, rig);
                typeof(RecordedQsosDialog).GetField("_browser", Private)!.SetValue(dialog, new RecordedQsoBrowser());
                body(dialog, rig);
            }
            catch (Exception ex) { failure = ex; }
            finally { RecordedQsosDialog.SendDoors = saved; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }
}
