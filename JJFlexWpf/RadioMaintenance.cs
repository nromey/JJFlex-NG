using System.Diagnostics;
using System.Threading;
using JJTrace;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// Radio maintenance actions that are reachable from more than one place in
    /// the UI. Right now that means Reboot, which is bound both to a hotkey
    /// (globals.vb <c>.RebootRadio</c>) and to a button in Settings → Radio Setup.
    ///
    /// Extracted rather than duplicated on purpose. Reboot carries three things
    /// that must not drift between call sites: the no-silent-keystrokes
    /// announcement when there is no radio, the confirmation dialog that *names*
    /// the other stations about to be dropped, and the deliberate absence of an
    /// operator-presence gate. Two copies of that would eventually disagree, and
    /// the copy that disagreed would be the one a remote owner hit at 2am.
    /// </summary>
    public static class RadioMaintenance
    {
        /// <summary>
        /// Confirm and reboot the radio.
        ///
        /// Deliberately NOT gated on <c>RequireOperatorPresence</c>. Reboot is the
        /// primary remote-recovery tool: an owner whose radio has gone dumb must be
        /// able to restart it even when another client holds IsLocalPtt. Worst case
        /// the radio drops briefly and comes back; locking the owner out of recovery
        /// is the worse failure. The confirmation dialog is the safeguard instead.
        /// </summary>
        /// <param name="rig">The radio abstraction. May be null.</param>
        /// <param name="onRebootInitiated">
        /// Invoked on the calling (UI) thread once the user has confirmed and just
        /// before the command goes out — MainWindow uses this to run powerNowOff()
        /// so the UI stops showing live radio state. Optional.
        /// </param>
        /// <returns>True if a reboot was actually started.</returns>
        public static bool RebootWithConfirmation(FlexBase? rig, Action? onRebootInitiated = null)
        {
            // No-silent-keystrokes rule: a bound key must always say something,
            // even when there's no radio to act on.
            if (rig == null || !rig.IsConnected)
            {
                ScreenReaderOutput.SpeakNoRadioConnected("reboot the radio");
                return false;
            }

            // Name names. On a MultiFlex radio, "this will disconnect Don" is the
            // single most decision-relevant fact, and JJ Flex already knows it.
            var others = rig.OtherConnectedStations;

            // No Owner assignment: JJ Flex's WPF main window is hosted in an
            // ElementHost rather than shown as a WPF Window, so setting Owner to a
            // never-shown Window throws. JJFlexDialog already parents itself to the
            // process main window handle for modality.
            var confirm = new Dialogs.ConfirmRebootDialog(others);
            if (confirm.ShowDialog() != true)
            {
                ScreenReaderOutput.Speak("Reboot cancelled.", VerbosityLevel.Terse, interrupt: true);
                return false;
            }

            ScreenReaderOutput.Speak(
                "Rebooting the radio. This takes several minutes.",
                VerbosityLevel.Critical, interrupt: true);

            onRebootInitiated?.Invoke();

            // Fire and forget. An earlier version called Join() immediately after
            // Start(), which blocked the UI thread for the whole reboot and froze
            // the app — the thread bought nothing. The radio needs no further input
            // from us once the command is away.
            var rebootThread = new Thread(() =>
            {
                try
                {
                    rig.Reboot(!rig.RemoteRig);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("RebootRadio: " + ex.Message, TraceLevel.Error);
                }
            })
            {
                Name = "reboot",
                IsBackground = true
            };
            rebootThread.Start();
            return true;
        }
    }
}
