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
            Assert.Contains("RadioStillTransmittingSentence(audioCheck: false", Shell());
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
            Assert.Contains("RestoreWhenReceiving(_changedRig);", Body(check, "public void ForceEnd(string? reason, bool connectionEnding = false)"));
            // Fix round 3: the session no longer restores anything itself. It
            // hands its changes to the radio's owner, which asks the radio
            // before EACH write; CheckRestorationTests runs that behaviour.
            string when = Body(check, "private void RestoreWhenReceiving(FlexBase rig)");
            Assert.Contains("rig.CheckRestorations.Release(entry)", when);
            Assert.DoesNotContain("XmitPower", when);
            Assert.DoesNotContain("DummyLoadMode", when);
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

        private static string Check() => File.ReadAllText(Path.Combine(IntegrationPassTree.Root,
            "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs")).Replace("\r\n", "\n");

        [Fact]
        public void Our_own_tune_is_never_blamed_on_a_hardware_line()
        {
            // 2026-11-02, the harness: tune up, Escape said a hardware P T T line
            // or VOX was holding the radio and kept the window open, because it
            // asked only the push-to-talk controller and a tune carrier does not
            // go through it. Both places that say that sentence ask about our
            // tune first.
            string outside = Body(Shell(), "private void UnkeyOutsideACheck()");
            int tune = outside.IndexOf("EscapeOurTune()", StringComparison.Ordinal);
            int blame = outside.IndexOf("RadioStillTransmittingSentence(", StringComparison.Ordinal);
            Assert.True(tune >= 0 && blame > tune);
            Assert.Contains("!ourTune && _rig?.Transmit == true", outside);

            string stop = Body(Check(), "public void StopCheck(bool escape = false)");
            tune = stop.IndexOf("_owner.EscapeOurTune()", StringComparison.Ordinal);
            blame = stop.IndexOf("RadioStillTransmittingSentence(", StringComparison.Ordinal);
            Assert.True(tune >= 0 && blame > tune);

            // Only Escape turns it off; the Stop button names it.
            Assert.Contains("escape && _owner.EscapeOurTune()", stop);
            Assert.Contains("!escape && _owner.OurTuneHolds()", stop);

            // The carrier goes off through Home's explicit off; an ATU cycle is named.
            string ours = Body(Shell(), "internal bool EscapeOurTune()");
            Assert.Contains("TuneOffSource?.Invoke() == true", ours);
            Assert.Contains("audio.tune.tuner_still_tuning", ours);
        }

        private static string Handler()
        {
            string shell = Shell();
            int at = shell.IndexOf("protected override void OnPreviewKeyDown(KeyEventArgs e)", StringComparison.Ordinal);
            return shell.Substring(at, shell.IndexOf("base.OnPreviewKeyDown(e);", at, StringComparison.Ordinal) - at);
        }

        [Fact]
        public void A_tune_still_in_the_command_queue_counts_as_transmitting()
        {
            // Codex blocker 2: the tune key's write is queued, and the getter
            // changes only when it runs; Escape in that gap closed the window.
            Assert.Contains("|| _rig?.TuneAskedOrUp == true", Handler());
            string flex = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "Radios", "FlexBase.TuneAnswer.cs"));
            string asked = Body(flex, "public bool TuneAskedOrUp");
            // Counted until it runs or is revoked; no timeout stands in for the
            // queue. The behaviour is TuneWriteLifetimeTests'.
            Assert.Contains("return _tuneOnQueued || radio.TXTune;", asked);
            Assert.DoesNotContain("Pending", asked);
        }

        [Fact]
        public void Escape_with_a_session_tells_the_session_it_is_an_escape()
        {
            string handler = Handler();
            int stay = handler.IndexOf("case AudioCheckEscapeAction.UnkeyAndStay:", StringComparison.Ordinal);
            string arm = handler.Substring(stay, handler.IndexOf("return;", stay, StringComparison.Ordinal) - stay);
            Assert.Contains("_session.StopCheck(escape: true)", arm);
        }

        [Fact]
        public void The_microphone_listening_Escape_turns_a_tune_of_ours_off_on_the_same_press()
        {
            // Codex blocker 3: this branch returned before anything asked about tune.
            string handler = Handler();
            int at = handler.IndexOf("ComputerMicCheckRunning && _session?.Active != true", StringComparison.Ordinal);
            string branch = handler.Substring(at, handler.IndexOf("return;", at, StringComparison.Ordinal) - at);
            int tune = branch.IndexOf("EscapeOurTune();", StringComparison.Ordinal);
            int mic = branch.IndexOf("StopComputerMicCheck(speak: true);", StringComparison.Ordinal);
            Assert.True(tune > 0 && mic > tune);
        }

        [Fact]
        public void The_tune_key_works_in_the_window_and_starts_no_carrier_under_a_check()
        {
            string shell = Shell();
            int at = shell.IndexOf("protected override void OnPreviewKeyDown(KeyEventArgs e)", StringComparison.Ordinal);
            string handler = shell.Substring(at, shell.IndexOf("base.OnPreviewKeyDown(e);", at, StringComparison.Ordinal) - at);
            int key = handler.IndexOf("TakesTheTuneKey(e)", StringComparison.Ordinal);
            int enter = handler.IndexOf("e.Key == Key.Enter", StringComparison.Ordinal);
            Assert.True(key >= 0 && enter > key);

            string takes = Body(shell, "private bool TakesTheTuneKey(KeyEventArgs e)");
            // The live key table, so a remap is honoured; a plain key is not taken.
            Assert.Contains("KeyCommands.IsTuneKey(chord)", takes);
            Assert.Contains("System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt", takes);
            // A start is refused while a check owns the transmitter; an off never is.
            int start = takes.IndexOf("case PressAct.Start:", StringComparison.Ordinal);
            int refuse = takes.IndexOf("TuneDoors.RefuseTuneDuringCheck()", StringComparison.Ordinal);
            int toggle = takes.IndexOf("ToggleTuneSource?.Invoke()", StringComparison.Ordinal);
            Assert.True(start > 0 && refuse > start && toggle > refuse);
            Assert.Contains("if (checkOwnsTransmitter) TuneDoors.RefuseTuneDuringCheck();", takes);

            string main = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "MainWindow.xaml.cs"));
            Assert.Contains("Dialogs.CheckYourAudioDialog.ToggleTuneSource = () => ToggleTuneCarrier();", main);
            Assert.Contains("Dialogs.CheckYourAudioDialog.TuneOffSource = () => TurnTuneCarrierOff();", main);
            string keys = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "KeyCommands.cs"));
            Assert.Contains("Lookup(k)?.KeyDef?.Id == CommandValues.TuneToggle", keys);
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
