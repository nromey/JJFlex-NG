using System.Windows;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Radio Setup tab — the ordered path for bringing a radio up, written for
    /// the case that actually hurts: a radio that will live somewhere nobody can
    /// walk to. Every step here is something that is painful or impossible to fix
    /// remotely once it is wrong.
    ///
    /// A numbered checklist rather than a modal wizard, deliberately. A wizard
    /// forces one order and forgets everything on cancel; a checklist lets a
    /// screen-reader user move by heading straight to the one step they came
    /// back for, re-run it alone, and read the current state of the other four
    /// without touching them. It doubles as a status board, which is what you
    /// want when the question is "did I actually set that before I shipped the
    /// radio?"
    /// </summary>
    public partial class SettingsDialog
    {
        /// <summary>
        /// Invoked on the UI thread once a reboot has been confirmed and is about
        /// to be sent. MainWindow supplies powerNowOff() here so the display stops
        /// showing radio state that is about to go away. Optional — the reboot
        /// still works without it.
        /// </summary>
        public Action? OnRebootInitiated { get; set; }

        /// <summary>
        /// Push the current radio into both hosted copies of the addressing
        /// control and recompute every step's status line. Called from the Rig
        /// setter and from the tab's Refresh button.
        /// </summary>
        private bool _radioSetupSubscribed;

        private void RefreshRadioSetupTab()
        {
            // The Rig setter can fire before InitializeComponent has built these.
            if (SetupStep1Status == null) return;

            // Either copy of the addressing control can change the radio, and both
            // then hold a stale view — the Network copy would still be showing DHCP
            // after Radio Setup pinned an address. Re-reading both on any change is
            // what keeps the two hosts from disagreeing.
            if (!_radioSetupSubscribed)
            {
                _radioSetupSubscribed = true;
                if (SetupStaticIpControl != null) SetupStaticIpControl.AddressChanged += StaticIpControl_AddressChanged;
                if (NetworkStaticIpControl != null) NetworkStaticIpControl.AddressChanged += StaticIpControl_AddressChanged;
            }

            if (SetupStaticIpControl != null) SetupStaticIpControl.Rig = _rig;
            if (NetworkStaticIpControl != null) NetworkStaticIpControl.Rig = _rig;

            RefreshSetupStatuses();
        }

        /// <summary>
        /// One control changed the radio's addressing; refresh the other and the
        /// step statuses. Deliberately does not re-enter <see cref="RefreshRadioSetupTab"/>,
        /// which would re-assign Rig on the control that raised this and make it
        /// refresh mid-callback.
        /// </summary>
        private void StaticIpControl_AddressChanged(object? sender, EventArgs e)
        {
            if (!ReferenceEquals(sender, SetupStaticIpControl)) SetupStaticIpControl?.Refresh();
            if (!ReferenceEquals(sender, NetworkStaticIpControl)) NetworkStaticIpControl?.Refresh();
            RefreshSetupStatuses();
        }

        private void RefreshSetupStatuses()
        {
            bool connected = _rig != null && _rig.IsConnected;

            // Step 1 — connection.
            if (!connected)
            {
                SetupStep1Status.Text =
                    "Not done. No radio is connected. Close Settings, connect to the radio, then come back.";
                SetupStep2Status.Text = "Waiting on step 1.";
                SetupStep3Status.Text = "Waiting on step 1.";
                SetupStep4Status.Text = "Waiting on step 1.";
                SetupStep5Status.Text = "Waiting on step 1.";
                SetupTestNetworkButton.IsEnabled = false;
                SetupRebootButton.IsEnabled = false;
                return;
            }

            SetupTestNetworkButton.IsEnabled = true;
            SetupRebootButton.IsEnabled = true;

            bool overSmartLink = _rig!.RemoteRig;
            string where = _rig.CurrentRadioIP?.ToString() ?? "an unknown address";
            SetupStep1Status.Text = overSmartLink
                ? $"Done, over SmartLink. Note that a fixed address in step 2 cannot be worked out over SmartLink — for that step you need to be on the same network as the radio."
                : $"Done, on your local network at {where}.";

            // Step 2 — addressing.
            var staticIp = _rig.CurrentStaticIP;
            SetupStep2Status.Text = staticIp != null
                ? $"Done. The radio is set to the fixed address {staticIp}."
                : "Not done. The radio takes whatever address the router gives it, which can change after a power cut.";

            // Step 3 — the way in from outside.
            bool forwarding = _rig.PortForwardingEnabled;
            int tcp = _rig.PortForwardingTcpPort;
            int udp = _rig.PortForwardingUdpPort;
            var mode = _rig.CurrentAccountConnectionMode ?? SmartLinkConnectionMode.ManualPortForwardOnly;
            string modeText = mode switch
            {
                SmartLinkConnectionMode.AutomaticHolePunch => "Hole-punch is allowed as a fallback.",
                SmartLinkConnectionMode.ManualPlusUpnp => "The radio may also ask the router via UPnP.",
                _ => "Only the forwarded port will be used.",
            };

            if (forwarding && tcp > 0)
            {
                string ports = (udp > 0 && udp != tcp)
                    ? $"TCP {tcp} and UDP {udp}"
                    : $"port {tcp}, TCP and UDP";
                SetupStep3Status.Text =
                    $"Set on the radio. It listens on {ports}. {modeText} Your router still has to forward the same port to the radio — JJ Flex cannot do that part. Step 4 checks whether it worked.";
            }
            else
            {
                SetupStep3Status.Text =
                    $"Not set. No port is forwarded on the radio. {modeText} If the router cannot be changed, allow hole-punch in Network settings and check it with step 4.";
            }

            // Step 4 — whether any of it works from outside. Reports the last
            // probe if one has run this session rather than pretending nothing is
            // known; a stale answer with a caveat beats no answer.
            var report = _rig.MostRecentNetworkReport;
            if (report == null)
            {
                SetupStep4Status.Text = "Not run yet.";
            }
            else if (!report.ProbeCompleted)
            {
                SetupStep4Status.Text = $"The last check did not finish. {report.ErrorDetail}";
            }
            else
            {
                SetupStep4Status.Text = "Last check — " + BuildNetworkDiagnosticSummary(report);
            }

            // Step 5 — restart. Called out when step 2 has left something pending.
            SetupStep5Status.Text = staticIp != null
                ? "A fixed address is set. If you set it just now, restart the radio to put it into use."
                : "Nothing is waiting on a restart.";
        }

        private void RadioSetupRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshRadioSetupTab();
            ScreenReaderOutput.Speak("Steps refreshed.", VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// Step 3 hands off to the Network tab rather than duplicating the port
        /// and connection-mode controls. Moving focus to the tab header (not just
        /// selecting it) is what makes this work with a screen reader — otherwise
        /// the tab changes silently and the user is left reading the old one.
        /// </summary>
        private void SetupGoToNetworkButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in SettingsTabs.Items)
            {
                if (item is TabItem tab && (tab.Header as string) == "Network")
                {
                    SettingsTabs.SelectedItem = tab;
                    tab.Focus();
                    ScreenReaderOutput.Speak("Network settings.", VerbosityLevel.Terse, interrupt: true);
                    return;
                }
            }
        }

        /// <summary>
        /// Step 4 runs the same SmartLink probe as the Network tab's Test network
        /// button, then folds the result into this step's status line so the
        /// checklist stays the single place to read.
        /// </summary>
        private async void SetupTestNetworkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null || !_rig.IsConnected)
            {
                SetupStep4Status.Text = "No radio is connected.";
                ScreenReaderOutput.Speak("No radio connected.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            SetupStep4Status.Text = "Checking. This usually takes a few seconds.";
            ScreenReaderOutput.Speak("Checking the network.", VerbosityLevel.Terse, interrupt: true);

            Radios.SmartLink.NetworkDiagnosticReport? report;
            try
            {
                report = await _rig.RunNetworkDiagnosticAsync(forceRefresh: true);
            }
            catch (Exception ex)
            {
                SetupStep4Status.Text = $"The check failed: {ex.Message}";
                ScreenReaderOutput.Speak("The check failed.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (report == null)
            {
                SetupStep4Status.Text =
                    "There is no SmartLink session, so the outside check cannot run. Sign in to SmartLink and try again.";
                ScreenReaderOutput.Speak("No SmartLink session.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (!report.ProbeCompleted)
            {
                SetupStep4Status.Text = $"The check did not finish. {report.ErrorDetail}";
                ScreenReaderOutput.Speak("The check did not finish.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            string summary = BuildNetworkDiagnosticSummary(report);
            SetupStep4Status.Text = summary;
            ScreenReaderOutput.Speak(summary, VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// Step 5 shares the reboot flow with the hotkey binding in globals.vb via
        /// <see cref="RadioMaintenance"/> — same confirmation, same naming of the
        /// other stations about to be dropped, same absence of a presence gate.
        /// </summary>
        private void SetupRebootButton_Click(object sender, RoutedEventArgs e)
        {
            if (RadioMaintenance.RebootWithConfirmation(_rig, OnRebootInitiated))
            {
                SetupStep5Status.Text =
                    "Restarting. The radio will be unreachable for a few minutes, then you can connect again.";
            }
        }
    }
}
