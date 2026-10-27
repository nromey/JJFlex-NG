using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Escape in Check your audio (#835, fix round 1): Noel's rule is "while a
    /// check is transmitting, the first press unkeys and stays, the second
    /// closes; Escape never leaves you transmitting."
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this pins.</b> After the first Escape unkeyed, the session
    /// stayed Active through its post-unkey phases — three watcher ticks for
    /// Monitor and Test your microphone, the recorder's stop-then-play startup,
    /// and the whole of the Loopback playback — and the window's Escape branch
    /// asked only "is a session active?". So a prompt second Escape was
    /// consumed by the same branch and a THIRD was needed to close. The test
    /// that shipped with the window checked only that the first branch existed.
    /// </para>
    /// <para>
    /// <b>What decides now:</b> whether anything is still transmitting, not
    /// whether a session object is alive. The decision is
    /// <see cref="AudioCheckEscape.Decide"/>, run here press by press; the
    /// window's handler is pinned to route through it and to let the closing
    /// press fall through to the base dialog's close.
    /// </para>
    /// <para>
    /// <b>What these cannot see:</b> the real key event, the real close, or a
    /// radio slow to report its unkey. Bench step: Escape, Escape, promptly.
    /// </para>
    /// </remarks>
    public sealed class CheckYourAudioEscapeTests
    {
        private static string Shell() => File.ReadAllText(Path.Combine(IntegrationPassTree.Root,
            "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.cs")).Replace("\r\n", "\n");

        [Fact]
        public void Two_prompt_presses_unkey_then_close_in_every_post_unkey_phase()
        {
            // First press: keyed.
            Assert.Equal(AudioCheckEscapeAction.UnkeyAndStay,
                AudioCheckEscape.Decide(startPending: false, startCancelled: false, sessionActive: true, stillTransmitting: true));
            // Second press: unkeyed, the session still alive in its post-unkey
            // grace, its recorder startup or the Loopback playback. It closes.
            Assert.Equal(AudioCheckEscapeAction.EndAndClose,
                AudioCheckEscape.Decide(startPending: false, startCancelled: false, sessionActive: true, stillTransmitting: false));
        }

        [Fact]
        public void A_transmitter_still_keyed_after_the_unkey_keeps_the_window_open()
        {
            // A hardware PTT line holding the radio, or a radio slow to report
            // its unkey: Escape never leaves you transmitting.
            for (int press = 0; press < 3; press++)
                Assert.Equal(AudioCheckEscapeAction.UnkeyAndStay,
                    AudioCheckEscape.Decide(false, false, sessionActive: true, stillTransmitting: true));
        }

        [Fact]
        public void A_pending_start_is_cancelled_first_and_a_cancelled_one_closes()
        {
            Assert.Equal(AudioCheckEscapeAction.CancelPendingStart, AudioCheckEscape.Decide(true, false, false, false));
            Assert.Equal(AudioCheckEscapeAction.CloseCancelledStart, AudioCheckEscape.Decide(true, true, false, false));
            // A pending start outranks a session that is not transmitting.
            Assert.Equal(AudioCheckEscapeAction.CancelPendingStart, AudioCheckEscape.Decide(true, false, true, false));
        }

        [Fact]
        public void A_transmitting_radio_outranks_a_pending_start_on_every_press()
        {
            // Fix round 2: a hardware line asserted while the check was still
            // preparing. The first press cancelled the start without asking
            // for an unkey, and the second closed the window on a keyed radio.
            Assert.Equal(AudioCheckEscapeAction.UnkeyAndStay, AudioCheckEscape.Decide(true, false, false, true));
            Assert.Equal(AudioCheckEscapeAction.UnkeyAndStay, AudioCheckEscape.Decide(true, true, false, true));
            Assert.Equal(AudioCheckEscapeAction.UnkeyAndStay, AudioCheckEscape.Decide(true, false, true, true));
        }

        [Fact]
        public void An_idle_window_on_a_transmitting_radio_stays_open()
        {
            // No check at all, the radio keyed by a hardware line: the old
            // handler asked only the session and closed the window.
            Assert.Equal(AudioCheckEscapeAction.UnkeyAndStay, AudioCheckEscape.Decide(false, false, false, true));
        }

        [Fact]
        public void The_window_asks_the_radio_itself_whether_it_is_transmitting()
        {
            string shell = Shell();
            int at = shell.IndexOf("protected override void OnPreviewKeyDown(KeyEventArgs e)", StringComparison.Ordinal);
            string handler = shell.Substring(at, shell.IndexOf("base.OnPreviewKeyDown(e);", at, StringComparison.Ordinal) - at);
            Assert.Contains("_rig?.Transmit == true", handler);
            // With no session to stop, the stay arm asks the controller to
            // unkey and says plainly when a hardware line is holding the radio.
            int stay = handler.IndexOf("case AudioCheckEscapeAction.UnkeyAndStay:", StringComparison.Ordinal);
            string arm = handler.Substring(stay, handler.IndexOf("return;", stay, StringComparison.Ordinal) - stay);
            Assert.Contains("UnkeyOutsideACheck()", arm);
            Assert.Contains("audio.check.escape_radio_still_transmitting", Shell());
        }

        [Fact]
        public void Power_comes_back_only_once_the_radio_says_it_is_receiving()
        {
            // The inherited hazard: HandleUnkey released the dummy load and
            // restored capped power straight after the software unkey. With a
            // hardware PTT line still holding the radio, that put full power on
            // the air.
            string check = File.ReadAllText(Path.Combine(IntegrationPassTree.Root,
                "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs")).Replace("\r\n", "\n");
            string unkey = Body(check, "private void HandleUnkey(bool external)");
            Assert.DoesNotContain("DummyLoadMode = false", unkey);
            Assert.DoesNotContain("XmitPower = _savedPower", unkey);
            Assert.Contains("RestoreWhenReceiving(rig);", unkey);
            Assert.Contains("RestoreWhenReceiving(_changedRig);", Body(check, "public void ForceEnd(string? reason)"));
            string when = Body(check, "private void RestoreWhenReceiving(FlexBase rig)");
            int subscribe = when.IndexOf("rig.TransmitChange +=", StringComparison.Ordinal);
            int ask = when.IndexOf("rig.Transmit", subscribe + 1, StringComparison.Ordinal);
            Assert.True(subscribe >= 0 && ask > subscribe, "subscribe to the radio's report before asking it, so a receive that lands in between is not missed");
        }

        [Fact]
        public void The_shell_tells_the_dialog_door_which_window_runs_the_check()
        {
            // Fix round 2: the door's behaviour is JJFlexWpf.Tests'
            // DialogTransmitKeysTests; this pins that production wires it.
            string main = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "MainWindow.xaml.cs"));
            Assert.Contains("DialogTransmitKeys.WindowHandlesCheckEscape = w => w is Dialogs.CheckYourAudioDialog;", main);
            Assert.Contains("DialogTransmitKeys.CheckEscapeElsewhere = Dialogs.CheckYourAudioDialog.StopTransmittingCheckFromElsewhere;", main);
        }

        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "Could not find " + signature);
            int open = source.IndexOf('{', at), depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("Unbalanced braces after " + signature);
        }

        [Fact]
        public void With_nothing_running_Escape_is_the_dialogs_ordinary_close()
        {
            Assert.Equal(AudioCheckEscapeAction.Close, AudioCheckEscape.Decide(false, false, false, false));
        }

        [Fact]
        public void The_window_routes_Escape_through_the_decision_and_lets_the_closing_press_through()
        {
            string shell = Shell();
            int at = shell.IndexOf("protected override void OnPreviewKeyDown(KeyEventArgs e)", StringComparison.Ordinal);
            Assert.True(at >= 0);
            string handler = shell.Substring(at, shell.IndexOf("base.OnPreviewKeyDown(e);", at, StringComparison.Ordinal) - at);
            Assert.Contains("AudioCheckEscape.Decide(", handler);
            // The old single question is gone: a live session alone must not
            // swallow the press.
            Assert.DoesNotContain("if (e.Key == Key.Escape && _session?.Active == true)", handler);
            int end = handler.IndexOf("case AudioCheckEscapeAction.EndAndClose:", StringComparison.Ordinal);
            Assert.True(end >= 0, "the closing press has its own arm");
            string arm = handler.Substring(end, handler.IndexOf("break;", end, StringComparison.Ordinal) - end);
            Assert.Contains("ForceEnd(", arm);
            Assert.DoesNotContain("e.Handled = true", arm);
        }
    }
}
