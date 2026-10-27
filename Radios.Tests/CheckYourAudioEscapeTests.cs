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
            // A pending start outranks a session, as the handler always had it.
            Assert.Equal(AudioCheckEscapeAction.CancelPendingStart, AudioCheckEscape.Decide(true, false, true, true));
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
