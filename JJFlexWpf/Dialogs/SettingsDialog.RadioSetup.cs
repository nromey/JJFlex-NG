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
    /// back for, re-run it alone, and read the current state of the other five
    /// without touching them. It doubles as a status board, which is what you
    /// want when the question is "did I actually set that before I shipped the
    /// radio?"
    ///
    /// Also holds the Network tab's live-status pieces (hole-punch port,
    /// reachability readout, private-IP enforcement), since they share the same
    /// refresh path.
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

        private bool _radioSetupSubscribed;
        private bool _suppressPrivateIpAnnouncement;

        /// <summary>
        /// Push the current radio into both hosted copies of the addressing
        /// control and recompute every step's status line. Called from the Rig
        /// setter and from the tab's Refresh button.
        /// </summary>
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

            LoadHolePunchPortIntoUi();
            RefreshSetupStatuses();
            RefreshReachabilityStatus();
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
                SetupStep6Status.Text = "Waiting on step 1.";
                SetupRegisterButton.IsEnabled = false;
                SetupUnregisterButton.IsEnabled = false;
                SetupTestNetworkButton.IsEnabled = false;
                SetupRebootButton.IsEnabled = false;
                return;
            }

            SetupTestNetworkButton.IsEnabled = true;
            SetupRebootButton.IsEnabled = true;

            bool overSmartLink = _rig!.IsWanConnection;
            string where = _rig.CurrentRadioIP?.ToString() ?? "an unknown address";
            SetupStep1Status.Text = overSmartLink
                ? "Done, over SmartLink. Steps 2 and 3 need you to be on the same network as the radio, so they are not available on this connection."
                : $"Done, on your local network at {where}.";

            // Step 2 — registration. Both buttons are off over SmartLink: getting
            // here that way proves the radio is already registered, and unregister
            // over SmartLink would cut the branch you are sitting on.
            var regCheck = _rig.PreflightSmartLinkRegistration();
            SetupRegisterButton.IsEnabled = regCheck.CanProceed;
            SetupUnregisterButton.IsEnabled = regCheck.CanProceed;

            if (overSmartLink)
            {
                SetupStep2Status.Text =
                    "Done. You are connected over SmartLink, which is only possible for a radio that is already registered.";
            }
            else if (!regCheck.CanProceed)
            {
                SetupStep2Status.Text = "Cannot register yet. " + regCheck.BlockReason;
            }
            else if (_rig.RegistrationSucceeded)
            {
                SetupStep2Status.Text = _rig.RegistrationStateText;
            }
            else
            {
                SetupStep2Status.Text =
                    $"Ready to register to {regCheck.AccountEmail}. " + _rig.RegistrationStateText;
            }

            // Step 3 — addressing.
            var staticIp = _rig.CurrentStaticIP;
            SetupStep3Status.Text = staticIp != null
                ? $"Done. The radio is set to the fixed address {staticIp}."
                : "Not done. The radio takes whatever address the router gives it, which can change after a power cut.";

            // Step 4 — the way in from outside.
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
                SetupStep4Status.Text =
                    $"Set on the radio. It listens on {ports}. {modeText} Your router still has to forward the same port to the radio — JJ Flex cannot do that part. Step 5 checks whether it worked.";
            }
            else
            {
                SetupStep4Status.Text =
                    $"Not set. No port is forwarded on the radio. {modeText} If the router cannot be changed, allow hole-punch in Network settings and check it with step 5.";
            }

            // Step 5 — whether any of it works from outside. Reports the last probe
            // if one has run rather than pretending nothing is known; a stale answer
            // with a caveat beats no answer.
            var report = _rig.MostRecentNetworkReport;
            if (report == null)
            {
                SetupStep5Status.Text = "Not run yet.";
            }
            else if (!report.ProbeCompleted)
            {
                SetupStep5Status.Text = $"The last check did not finish. {report.ErrorDetail}";
            }
            else
            {
                SetupStep5Status.Text = "Last check — " + BuildNetworkDiagnosticSummary(report);
            }

            // Step 6 — restart.
            SetupStep6Status.Text = staticIp != null
                ? "A fixed address is set. If you set it just now, restart the radio to put it into use."
                : "Nothing is waiting on a restart.";
        }

        private void RadioSetupRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshRadioSetupTab();
            ScreenReaderOutput.Speak("Steps refreshed.", VerbosityLevel.Terse, interrupt: true);
        }

        #region Step 2 — SmartLink registration

        /// <summary>
        /// Register the radio to the signed-in SmartLink account.
        ///
        /// The interesting part for a blind operator is the middle of the
        /// handshake: the radio asks for a physical keypress and gives no
        /// indication of its own that a screen reader can see. So every state
        /// change is spoken, and the "key the mic now" state is spoken at Critical
        /// verbosity — if that one is missed, the attempt times out and has to be
        /// started again.
        /// </summary>
        private void SetupRegisterButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null) return;

            var check = _rig.PreflightSmartLinkRegistration();
            if (!check.CanProceed)
            {
                SetupStep2Status.Text = check.BlockReason;
                ScreenReaderOutput.Speak(check.BlockReason, VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var confirm = new ConfirmActionDialog(
                "Register Radio with SmartLink",
                $"JJ Flex will register this radio to your SmartLink account, {check.AccountEmail}.",
                check.Warnings,
                question: "Have the microphone or CW key ready. Continue?",
                yesLabel: "Re_gister");

            if (confirm.ShowDialog() != true)
            {
                SetupStep2Status.Text = "Cancelled. Nothing was sent to the radio.";
                ScreenReaderOutput.Speak("Cancelled.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            StartRegistration(register: true);
        }

        /// <summary>
        /// Unregister — kept because testing registration end to end needs it, and
        /// hiding a destructive action does not make it less destructive. Guarded
        /// with the plainest warning available: re-registering needs a person at
        /// the radio, so doing this to a radio you cannot reach strands it for good.
        /// </summary>
        private void SetupUnregisterButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null) return;

            var check = _rig.PreflightSmartLinkRegistration();
            if (!check.CanProceed)
            {
                SetupStep2Status.Text = check.BlockReason;
                ScreenReaderOutput.Speak(check.BlockReason, VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var confirm = new ConfirmActionDialog(
                "Unregister Radio from SmartLink",
                "JJ Flex will remove this radio's SmartLink registration. You will not be able to reach it from away from home until it is registered again.",
                new[]
                {
                    "Registering again requires someone to key the microphone or CW key at the radio. There is no remote way to do it.",
                    "If this radio is somewhere you cannot get to, unregistering it means you cannot get it back without travelling there or asking someone on site.",
                },
                question: "This is almost never what you want. Continue?",
                yesLabel: "_Unregister");

            if (confirm.ShowDialog() != true)
            {
                SetupStep2Status.Text = "Cancelled. The radio is still registered.";
                ScreenReaderOutput.Speak("Cancelled.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            StartRegistration(register: false);
        }

        private void StartRegistration(bool register)
        {
            if (_rig == null) return;

            SetupRegisterButton.IsEnabled = false;
            SetupUnregisterButton.IsEnabled = false;

            string opening = register
                ? "Registering. Watch for the prompt to key the microphone."
                : "Unregistering.";
            SetupStep2Status.Text = opening;
            ScreenReaderOutput.Speak(opening, VerbosityLevel.Terse, interrupt: true);

            void OnState(string text, bool terminal) => Dispatcher.Invoke(() =>
            {
                SetupStep2Status.Text = text;

                // The key-the-mic prompt is the one state that must not be missed:
                // ignore it and the whole attempt times out. Everything else is
                // ordinary progress.
                bool keyNow = text.Contains("key the microphone", StringComparison.OrdinalIgnoreCase);
                ScreenReaderOutput.Speak(
                    text,
                    keyNow ? VerbosityLevel.Critical : VerbosityLevel.Terse,
                    interrupt: true);

                if (terminal)
                {
                    RefreshSetupStatuses();
                    RefreshReachabilityStatus();
                }
            });

            if (!(register ? _rig.BeginSmartLinkRegistration(OnState) : _rig.BeginSmartLinkUnregistration(OnState)))
            {
                SetupStep2Status.Text =
                    "The command could not be sent. Check that you are signed in to SmartLink and see the trace file.";
                ScreenReaderOutput.Speak("Could not send the command.", VerbosityLevel.Terse, interrupt: true);
                RefreshSetupStatuses();
            }
        }

        #endregion

        #region Steps 4 to 6

        /// <summary>
        /// Step 4 hands off to the Network tab rather than duplicating the port
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
        /// Step 5 runs the same SmartLink probe as the Network tab's Test network
        /// button, then folds the result into this step's status line so the
        /// checklist stays the single place to read.
        /// </summary>
        private async void SetupTestNetworkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null)
            {
                SetupStep5Status.Text = "No radio is selected.";
                ScreenReaderOutput.Speak("No radio selected.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            SetupStep5Status.Text = "Checking. This usually takes a few seconds.";
            ScreenReaderOutput.Speak("Checking the network.", VerbosityLevel.Terse, interrupt: true);

            Radios.SmartLink.NetworkDiagnosticReport? report;
            try
            {
                report = await _rig.RunNetworkDiagnosticAsync(forceRefresh: true);
            }
            catch (Exception ex)
            {
                SetupStep5Status.Text = $"The check failed: {ex.Message}";
                ScreenReaderOutput.Speak("The check failed.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (report == null)
            {
                SetupStep5Status.Text =
                    "There is no SmartLink session, so the outside check cannot run. Sign in to SmartLink and try again.";
                ScreenReaderOutput.Speak("No SmartLink session.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (!report.ProbeCompleted)
            {
                SetupStep5Status.Text = $"The check did not finish. {report.ErrorDetail}";
                ScreenReaderOutput.Speak("The check did not finish.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            string summary = BuildNetworkDiagnosticSummary(report);
            SetupStep5Status.Text = summary;
            ScreenReaderOutput.Speak(summary, VerbosityLevel.Terse, interrupt: true);
            RefreshReachabilityStatus();
        }

        /// <summary>
        /// Step 6 shares the reboot flow with the hotkey binding in globals.vb via
        /// <see cref="RadioMaintenance"/> — same confirmation, same naming of the
        /// other stations about to be dropped, same absence of a presence gate.
        /// </summary>
        private void SetupRebootButton_Click(object sender, RoutedEventArgs e)
        {
            if (RadioMaintenance.RebootWithConfirmation(_rig, OnRebootInitiated))
            {
                SetupStep6Status.Text =
                    "Restarting. The radio will be unreachable for a few minutes, then you can connect again.";
            }
        }

        #endregion

        #region Network tab — hole-punch port, reachability, private-IP enforcement

        private void LoadHolePunchPortIntoUi()
        {
            if (HolePunchPortBox == null) return;
            int? configured = _rig?.ConfiguredHolePunchPort;
            HolePunchPortBox.Text = configured.HasValue ? configured.Value.ToString() : string.Empty;

            if (EnforcePrivateIpCheck != null && _rig != null && _rig.IsConnected)
            {
                _suppressPrivateIpAnnouncement = true;
                try { EnforcePrivateIpCheck.IsChecked = _rig.EnforcePrivateIPConnections; }
                finally { _suppressPrivateIpAnnouncement = false; }
            }
        }

        private void RandomHolePunchPortButton_Click(object sender, RoutedEventArgs e)
        {
            // Same range SmartSDR uses, so a port that works there works here.
            int port = Random.Shared.Next(25000, 65000);
            HolePunchPortBox.Text = port.ToString();
            ScreenReaderOutput.Speak($"Port {port}. Choose save port to keep it.", VerbosityLevel.Terse, interrupt: true);
        }

        private void ClearHolePunchPortButton_Click(object sender, RoutedEventArgs e)
        {
            HolePunchPortBox.Text = string.Empty;
            ScreenReaderOutput.Speak("Cleared. Choose save port to use a new port each time.",
                VerbosityLevel.Terse, interrupt: true);
        }

        private void SaveHolePunchPortButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null || !_rig.HasCurrentSmartLinkAccount)
            {
                ReachabilityStatusText.Text =
                    "The hole-punch port is saved with your SmartLink account, and no account is signed in.";
                ScreenReaderOutput.Speak("No SmartLink account signed in.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            string text = HolePunchPortBox.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                _rig.SaveCurrentAccountListenPort(null);
                ReachabilityStatusText.Text =
                    "Saved. JJ Flex will pick a new hole-punch port for every connection, which is the recommended setting.";
                ScreenReaderOutput.Speak("Saved. A new port each time.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (!int.TryParse(text, out int port) || port < 1024 || port > 65535)
            {
                ReachabilityStatusText.Text = "The hole-punch port must be a number between 1024 and 65535, or blank.";
                ScreenReaderOutput.Speak("Invalid port.", VerbosityLevel.Terse, interrupt: true);
                HolePunchPortBox.Focus();
                return;
            }

            if (_rig.SaveCurrentAccountListenPort(port))
            {
                ReachabilityStatusText.Text =
                    $"Saved. Hole-punch will use port {port}. If hole-punch starts failing on and off, clear this — a fixed port can clash with a leftover mapping in the router.";
                ScreenReaderOutput.Speak($"Saved port {port}.", VerbosityLevel.Terse, interrupt: true);
            }
            else
            {
                ReachabilityStatusText.Text = "The port could not be saved. See the trace file for details.";
                ScreenReaderOutput.Speak("Could not save the port.", VerbosityLevel.Terse, interrupt: true);
            }
        }

        private void RefreshReachabilityButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshReachabilityStatus();
            ScreenReaderOutput.Speak(ReachabilityStatusText.Text, VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// Build the "what the radio reports" readout. Written as sentences rather
        /// than a field list because it is read aloud — "hole-punch: true" tells a
        /// listener much less than a sentence saying what that means for them.
        /// </summary>
        private void RefreshReachabilityStatus()
        {
            if (ReachabilityStatusText == null) return;

            if (_rig == null || !_rig.IsConnected)
            {
                ReachabilityStatusText.Text = "No radio is connected, so there is nothing to report yet.";
                if (EnforcePrivateIpCheck != null) EnforcePrivateIpCheck.IsEnabled = false;
                return;
            }

            if (EnforcePrivateIpCheck != null) EnforcePrivateIpCheck.IsEnabled = true;

            var parts = new List<string>
            {
                _rig.IsWanConnection
                    ? "This connection is going through SmartLink."
                    : "This connection is direct on your local network."
            };

            if (_rig.RadioPortForwardActive)
            {
                parts.Add("SmartLink sees a forwarded port for this radio, so a way in from the internet is open.");
            }
            else
            {
                parts.Add("SmartLink does not see a forwarded port for this radio.");
            }

            int tls = _rig.RadioPublicTlsPort;
            int udp = _rig.RadioPublicUdpPort;
            if (tls > 0 || udp > 0)
            {
                parts.Add($"From the internet the radio is reachable on TCP {(tls > 0 ? tls.ToString() : "none")} and UDP {(udp > 0 ? udp.ToString() : "none")}. If you did not forward those yourself, the router opened them by UPnP.");
            }

            if (_rig.RadioRequiresHolePunch)
            {
                parts.Add("SmartLink says this radio needs a hole-punch, meaning neither a forwarded port nor UPnP gave it a way in.");
            }
            else
            {
                parts.Add("SmartLink does not need a hole-punch for this radio.");
            }

            int last = _rig.LastHolePunchPort;
            parts.Add(last > 0
                ? $"The last connection used hole-punch port {last}."
                : "The last connection did not use a hole-punch.");

            ReachabilityStatusText.Text = string.Join(" ", parts);
        }

        private void EnforcePrivateIpCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressPrivateIpAnnouncement) return;

            if (_rig == null || !_rig.IsConnected)
            {
                ScreenReaderOutput.Speak("No radio connected.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            bool wanted = EnforcePrivateIpCheck.IsChecked == true;
            _rig.EnforcePrivateIPConnections = wanted;

            // Read the value back rather than trusting the click. The radio is the
            // authority here, and a toggle that reports the state the user asked
            // for instead of the state that exists is the exact bug class the
            // DSP-toggle work already fixed once.
            bool actual = _rig.EnforcePrivateIPConnections;
            _suppressPrivateIpAnnouncement = true;
            try { EnforcePrivateIpCheck.IsChecked = actual; }
            finally { _suppressPrivateIpAnnouncement = false; }

            ScreenReaderOutput.Speak(
                actual
                    ? "Only local addresses may connect."
                    : "Any address may connect, including Tailscale.",
                VerbosityLevel.Terse, interrupt: true);
            RefreshReachabilityStatus();
        }

        #endregion
    }
}
