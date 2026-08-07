using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using JJTrace;
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
    /// back for, re-run it alone, and read the current state of the other six
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
            if (SetupConnectStatus == null) return;

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
            RefreshFirmwareStatus();
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

        // Cached answer from the SmartLink server for the current radio. Keyed by
        // serial so a different radio re-asks; refreshed at most once per dialog
        // instance because the answer only changes when registration itself runs
        // (and RegistrationSucceeded covers that case before this text is used).
        private string? _registrationQuerySerial;
        private FlexBase.SmartLinkRegistrationQuery? _registrationQueryResult;
        private bool _registrationQueryInFlight;

        private async void KickRegistrationQuery()
        {
            var rig = _rig;
            if (rig == null || !rig.IsConnected) return;

            string serial = rig.SelectedRadioSerial ?? string.Empty;
            if (serial.Length == 0) return;
            if (_registrationQueryInFlight) return;
            if (_registrationQueryResult != null && serial == _registrationQuerySerial) return;

            _registrationQueryInFlight = true;
            try
            {
                var result = await rig.QuerySmartLinkRegistrationAsync();
                _registrationQuerySerial = serial;
                // Unknown and NoAccount are not cached as answers — leave the
                // neutral text and let a later refresh try again rather than
                // pinning a shrug; NoAccount changes the moment the user signs in.
                _registrationQueryResult =
                    result is FlexBase.SmartLinkRegistrationQuery.Unknown
                           or FlexBase.SmartLinkRegistrationQuery.NoAccount
                    ? null
                    : result;
                if (_registrationQueryResult != null && IsLoaded)
                    RefreshSetupStatuses();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"KickRegistrationQuery: {ex.Message}", TraceLevel.Error);
            }
            finally
            {
                _registrationQueryInFlight = false;
            }
        }

        private void RefreshSetupStatuses()
        {
            bool connected = _rig != null && _rig.IsConnected;

            // Step 1 — connection.
            if (!connected)
            {
                SetupConnectStatus.Text =
                    "Not done. No radio is connected. Close Settings, connect to the radio, then come back.";
                SetupRegisterStatus.Text = "Waiting on step 1.";
                SetupAddressStatus.Text = "Waiting on step 1.";
                SetupWayInStatus.Text = "Waiting on step 1.";
                SetupCheckStatus.Text = "Waiting on step 1.";
                SetupRestartStatus.Text = "Waiting on step 1.";
                SetupRegisterButton.IsEnabled = false;
                SetupUnregisterButton.IsEnabled = false;
                SetupTestNetworkButton.IsEnabled = false;
                SetupRebootButton.IsEnabled = false;
                RefreshRadioNameField(connected: false);
                return;
            }

            SetupTestNetworkButton.IsEnabled = true;
            SetupRebootButton.IsEnabled = true;
            RefreshRadioNameField(connected: true);

            bool overSmartLink = _rig!.IsWanConnection;
            string where = _rig.CurrentRadioIP?.ToString() ?? "an unknown address";
            SetupConnectStatus.Text = overSmartLink
                ? "Done, over SmartLink. Steps 2 and 4 need you to be on the same network as the radio, so they are not available on this connection."
                : $"Done, on your local network at {where}.";

            // Step 2 — registration. Both buttons are off over SmartLink: getting
            // here that way proves the radio is already registered, and unregister
            // over SmartLink would cut the branch you are sitting on.
            var regCheck = _rig.PreflightSmartLinkRegistration();
            SetupRegisterButton.IsEnabled = regCheck.CanProceed;
            SetupUnregisterButton.IsEnabled = regCheck.CanProceed;

            if (overSmartLink)
            {
                SetupRegisterStatus.Text =
                    "Done. You are connected over SmartLink, which is only possible for a radio that is already registered.";
            }
            else if (!regCheck.CanProceed)
            {
                SetupRegisterStatus.Text = "Cannot register yet. " + regCheck.BlockReason;
            }
            else if (_rig.RegistrationSucceeded)
            {
                SetupRegisterStatus.Text = _rig.RegistrationStateText;
            }
            else
            {
                // Ask the SmartLink server whether this radio is in the account's
                // list — the only place the answer exists. Async because it can
                // take seconds; the text upgrades in place when the answer lands.
                SetupRegisterStatus.Text = _registrationQueryResult switch
                {
                    FlexBase.SmartLinkRegistrationQuery.Registered =>
                        $"Done. This radio is already registered to {regCheck.AccountEmail}. " +
                        "You only need the button below to register it again after unregistering.",
                    FlexBase.SmartLinkRegistrationQuery.NotRegistered =>
                        $"Not done. This radio is not registered to your SmartLink account ({regCheck.AccountEmail}). " +
                        "Use Register this radio below — without it, the radio cannot be reached from away from home.",
                    _ =>
                        $"Ready to register to {regCheck.AccountEmail}. Checking with SmartLink whether it already is... " +
                        _rig.RegistrationStateText,
                };
                KickRegistrationQuery();
            }

            // Step 4 — addressing.
            var staticIp = _rig.CurrentStaticIP;
            SetupAddressStatus.Text = staticIp != null
                ? $"Done. The radio is set to the fixed address {staticIp}."
                : "Not done. The radio takes whatever address the router gives it, which can change after a power cut.";

            // Step 5 — the way in from outside.
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
                SetupWayInStatus.Text =
                    $"Set on the radio. It listens on {ports}. {modeText} Your router still has to forward the same port to the radio — JJ Flex cannot do that part. Step 6 checks whether it worked.";
            }
            else
            {
                SetupWayInStatus.Text =
                    $"Not set. No port is forwarded on the radio. {modeText} If the router cannot be changed, allow hole-punch in Network settings and check it with step 6.";
            }

            // Step 6 — whether any of it works from outside. Reports the last probe
            // if one has run rather than pretending nothing is known; a stale answer
            // with a caveat beats no answer.
            var report = _rig.MostRecentNetworkReport;
            if (report == null)
            {
                SetupCheckStatus.Text = "Not run yet.";
            }
            else if (!report.ProbeCompleted)
            {
                SetupCheckStatus.Text = $"The last check did not finish. {report.ErrorDetail}";
            }
            else
            {
                SetupCheckStatus.Text = "Last check — " + BuildNetworkDiagnosticSummary(report);
            }

            // Step 7 — restart.
            SetupRestartStatus.Text = staticIp != null
                ? "A fixed address is set. If you set it just now, restart the radio to put it into use."
                : "Nothing is waiting on a restart.";
        }

        private void RadioSetupRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshRadioSetupTab();
            ScreenReaderOutput.Speak("Steps refreshed.", VerbosityLevel.Terse, interrupt: true);
        }

        #region Step 2 — radio name

        /// <summary>
        /// Keep the name box tracking the radio's actual name. Skipped while the
        /// user is typing in it — the status refresh runs on several triggers
        /// (address changes, registration completing) and clobbering a
        /// half-typed name would be rude.
        /// </summary>
        private void RefreshRadioNameField(bool connected)
        {
            if (SetupRadioNameBox == null) return;

            SetupRadioNameBox.IsEnabled = connected;
            SetupApplyNameButton.IsEnabled = connected;

            if (!SetupRadioNameBox.IsKeyboardFocusWithin)
                SetupRadioNameBox.Text = connected ? _rig!.RadioNickname : string.Empty;
        }

        /// <summary>
        /// Push the typed name to the radio. The name is stored in the radio
        /// itself and flows back through discovery, so it is what the radio
        /// list and SmartLink show from now on. Works over any connection type
        /// — renaming is a plain command, unlike registration.
        /// </summary>
        private void SetupApplyNameButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null || !_rig.IsConnected)
            {
                ScreenReaderOutput.Speak("No radio connected.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            string newName = SetupRadioNameBox.Text?.Trim() ?? string.Empty;
            if (newName.Length == 0)
            {
                // An empty name would show as Unknown everywhere — refuse and
                // put the current name back so the box matches reality.
                SetupRadioNameBox.Text = _rig.RadioNickname;
                ScreenReaderOutput.Speak(
                    "Type a name first. The radio keeps its current name.",
                    VerbosityLevel.Terse, interrupt: true);
                SetupRadioNameBox.Focus();
                return;
            }

            if (_rig.RenameRadio(newName))
            {
                // Critical: this is a confirmation of a radio-side change the
                // user cannot see any other way from here.
                ScreenReaderOutput.Speak($"Radio renamed to {newName}.", VerbosityLevel.Critical, interrupt: true);
                RefreshRadioNameField(connected: true);
            }
            else
            {
                ScreenReaderOutput.Speak("The radio could not be renamed.", VerbosityLevel.Terse, interrupt: true);
            }
        }

        #endregion

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
                SetupRegisterStatus.Text = check.BlockReason;
                ScreenReaderOutput.Speak(check.BlockReason, VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var confirm = new ConfirmActionDialog(
                "Register Radio with SmartLink",
                $"JJ Flex will register this radio to your SmartLink account, {check.AccountEmail}.",
                check.Warnings,
                question: "Have the microphone or CW key ready. Continue?",
                yesLabel: "Re_gister",
                radioModel: _rig.RadioModel);

            if (confirm.ShowDialog() != true)
            {
                SetupRegisterStatus.Text = "Cancelled. Nothing was sent to the radio.";
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
                SetupRegisterStatus.Text = check.BlockReason;
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
                yesLabel: "_Unregister",
                radioModel: _rig.RadioModel);

            if (confirm.ShowDialog() != true)
            {
                SetupRegisterStatus.Text = "Cancelled. The radio is still registered.";
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
            SetupRegisterStatus.Text = opening;
            ScreenReaderOutput.Speak(opening, VerbosityLevel.Terse, interrupt: true);

            void OnState(string text, bool terminal) => Dispatcher.Invoke(() =>
            {
                SetupRegisterStatus.Text = text;

                // The key-the-mic prompt must not be missed (ignore it and the
                // attempt times out) — and neither may a terminal verdict. Live
                // lesson 2026-08-04: the radio went WaitingForPTT -> FailedPTT in
                // the same millisecond (PTT line read as already active), and the
                // Terse failure speech got lost — the operator heard nothing at
                // all after "Registering".
                bool keyNow = text.Contains("key the microphone", StringComparison.OrdinalIgnoreCase);
                ScreenReaderOutput.Speak(
                    text,
                    keyNow || terminal ? VerbosityLevel.Critical : VerbosityLevel.Terse,
                    interrupt: true);

                if (terminal)
                {
                    RefreshSetupStatuses();
                    RefreshReachabilityStatus();
                }
            });

            if (!(register ? _rig.BeginSmartLinkRegistration(OnState) : _rig.BeginSmartLinkUnregistration(OnState)))
            {
                SetupRegisterStatus.Text =
                    "The command could not be sent. Check that you are signed in to SmartLink and see the trace file.";
                ScreenReaderOutput.Speak("Could not send the command.", VerbosityLevel.Terse, interrupt: true);
                RefreshSetupStatuses();
            }
        }

        #endregion

        #region Step 3 — firmware

        private string _chosenFirmwarePath = string.Empty;

        /// <summary>
        /// Report where the radio's firmware stands relative to what this build of
        /// FlexLib expects.
        ///
        /// Worth being careful with the wording here. FlexLib demands an exact
        /// version match and labels anything else as needing an update — but it
        /// never refuses to connect over it, and JJ Flex does not either. So a
        /// mismatch is reported as information, not as a failure, and the option to
        /// silence it is offered rather than a demand to fix it.
        /// </summary>
        private void RefreshFirmwareStatus()
        {
            if (SetupFirmwareStatus == null) return;

            if (_rig == null || !_rig.IsConnected)
            {
                SetupFirmwareStatus.Text = "Waiting on step 1.";
                SetupGetFirmwareButton.IsEnabled = false;
                SetupChooseFirmwareButton.IsEnabled = false;
                SetupSendFirmwareButton.IsEnabled = false;
                SetupSuppressVersionWarningButton.Visibility = Visibility.Collapsed;
                return;
            }

            bool local = !_rig.IsWanConnection;
            SetupGetFirmwareButton.IsEnabled = local;
            SetupChooseFirmwareButton.IsEnabled = local;
            SetupSendFirmwareButton.IsEnabled = local && !string.IsNullOrEmpty(_chosenFirmwarePath);

            string running = _rig.RadioFirmwareVersion;
            var parts = new List<string>
            {
                string.IsNullOrEmpty(running)
                    ? "The radio has not reported its firmware version."
                    : $"The radio is running firmware {running}."
            };

            if (_rig.IsInRecoveryState)
            {
                parts.Add("The radio is in recovery after an interrupted update. Sending the same firmware file again will finish it — this does not need anyone at the radio.");
            }

            if (FlexBase.FirmwareVersionCheckBypassed)
            {
                parts.Add("Firmware version checking is switched off on this computer, so no mismatch will be reported.");
                SetupSuppressVersionWarningButton.Visibility = Visibility.Collapsed;
            }
            else if (_rig.FirmwareDiffersFromLibraryExpectation)
            {
                parts.Add(
                    $"This build of JJ Flex was made against firmware {FlexBase.LibraryExpectedFirmwareVersion}, so the radio will show as needing an update. " +
                    "That is a label only — it does not stop JJ Flex connecting or working. You can silence it below.");
                SetupSuppressVersionWarningButton.Visibility = Visibility.Visible;
            }
            else
            {
                parts.Add("The firmware matches what this build of JJ Flex expects.");
                SetupSuppressVersionWarningButton.Visibility = Visibility.Collapsed;
            }

            if (!local)
                parts.Add("Firmware cannot be sent over SmartLink — the transfer uses a separate connection that SmartLink does not carry. Connect on the same network as the radio.");

            SetupFirmwareStatus.Text = string.Join(" ", parts);
        }

        /// <summary>
        /// Look the radio up in the JJ Flexible firmware catalogue and download
        /// the right image for it.
        ///
        /// "The catalogue isn't published yet" is a normal answer, not a fault —
        /// it is the expected answer on day one — so it reports plainly and points
        /// at the choose-a-file route rather than presenting as an error.
        /// </summary>
        private async void SetupGetFirmwareButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null || !_rig.IsConnected)
            {
                SetupFirmwareFileText.Text = "No radio is connected.";
                ScreenReaderOutput.Speak("No radio connected.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            string model = _rig.RadioModel;
            SetupGetFirmwareButton.IsEnabled = false;
            SetupFirmwareFileText.Text = "Looking for firmware for this radio...";
            ScreenReaderOutput.Speak("Looking for firmware.", VerbosityLevel.Terse, interrupt: true);

            try
            {
                var catalog = new JJFlexUpdater.Firmware.FirmwareCatalog();
                var manifest = await catalog.FetchAsync();
                var image = JJFlexUpdater.Firmware.FirmwareCatalog.BestImageFor(manifest, model, _rig.RadioIsBigBend);

                if (image == null)
                {
                    SetupFirmwareFileText.Text =
                        $"The firmware list does not have anything for a {model}. You can still choose a file from this computer.";
                    ScreenReaderOutput.Speak("No firmware listed for this radio.", VerbosityLevel.Terse, interrupt: true);
                    return;
                }

                string running = _rig.RadioFirmwareVersion;
                if (!string.IsNullOrEmpty(running)
                    && JJFlexUpdater.Firmware.FirmwareCatalog.CompareVersions(image.Version, running) <= 0)
                {
                    SetupFirmwareFileText.Text =
                        $"The radio is already running firmware {running}, and the newest offered is {image.Version}. There is nothing to update.";
                    ScreenReaderOutput.Speak("The radio firmware is already up to date.", VerbosityLevel.Terse, interrupt: true);
                    return;
                }

                // Advisory only. Getting a stepping-stone requirement wrong in
                // either direction is worse than telling the user what we know.
                if (!string.IsNullOrWhiteSpace(image.MinVersionForDirectUpdate)
                    && !string.IsNullOrEmpty(running)
                    && JJFlexUpdater.Firmware.FirmwareCatalog.CompareVersions(running, image.MinVersionForDirectUpdate) < 0)
                {
                    SetupFirmwareFileText.Text =
                        $"Firmware {image.Version} expects the radio to already be on {image.MinVersionForDirectUpdate} or newer, and this one is on {running}. " +
                        "You may need an in-between version first. Downloading anyway — check with FlexRadio before sending if you are unsure.";
                    // Speak the reason itself, not "see the message" — the
                    // information reaching the ear is the whole point.
                    ScreenReaderOutput.Speak(
                        $"Firmware {image.Version} expects the radio to already be on {image.MinVersionForDirectUpdate} or newer, and this one is on {running}. " +
                        "An in-between version may be needed. Downloading anyway.",
                        VerbosityLevel.Terse, interrupt: true);
                }

                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "JJFlexRadio", "firmware");

                // Drive a real ProgressBar and let the screen reader report it the
                // way the user configured. No spoken percentages: NVDA already
                // has a progress-bar setting (beep / speak / both / off), and the
                // app announcing its own percentages both overrides that choice
                // and interrupts other speech to do it.
                SetupFirmwareProgress.Visibility = Visibility.Visible;
                SetupFirmwareProgressText.Visibility = Visibility.Visible;
                SetupFirmwareProgress.IsIndeterminate = false;
                SetupFirmwareProgress.Value = 0;
                SetupFirmwareProgressText.Text = "Starting download...";

                string path;
                try
                {
                    path = await catalog.DownloadAsync(image, dir, onProgress: (read, total) =>
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (total.HasValue && total.Value > 0)
                            {
                                SetupFirmwareProgress.IsIndeterminate = false;
                                SetupFirmwareProgress.Value = (double)read / total.Value * 100.0;
                                SetupFirmwareProgressText.Text =
                                    $"{read / 1024.0 / 1024.0:F1} MB of {total.Value / 1024.0 / 1024.0:F1} MB";
                            }
                            else
                            {
                                // No Content-Length: an indeterminate bar is honest,
                                // a fake percentage is not.
                                SetupFirmwareProgress.IsIndeterminate = true;
                                SetupFirmwareProgressText.Text = $"{read / 1024.0 / 1024.0:F1} MB downloaded";
                            }
                        }));
                }
                finally
                {
                    SetupFirmwareProgress.Visibility = Visibility.Collapsed;
                    SetupFirmwareProgressText.Visibility = Visibility.Collapsed;
                }

                _chosenFirmwarePath = path;
                var check = _rig.PreflightFirmwareUpdate(path, image.Sha256);
                if (!check.CanProceed)
                {
                    _chosenFirmwarePath = string.Empty;
                    SetupSendFirmwareButton.IsEnabled = false;
                    SetupFirmwareFileText.Text = check.BlockReason;
                    ScreenReaderOutput.Speak(check.BlockReason, VerbosityLevel.Terse, interrupt: true);
                    return;
                }

                SetupSendFirmwareButton.IsEnabled = true;
                SetupFirmwareFileText.Text =
                    $"Downloaded firmware {image.Version} for this radio, {(check.SizeBytes / 1024.0 / 1024.0):F1} megabytes, and the checksum matches. " +
                    "Nothing has been sent to the radio yet — choose Send to radio.";
                ScreenReaderOutput.Speak($"Firmware {image.Version} downloaded and checked. Choose send to radio.",
                    VerbosityLevel.Terse, interrupt: true);
            }
            catch (JJFlexUpdater.Net.UpdaterFetchException ex)
            {
                JJTrace.Tracing.TraceLine($"SetupGetFirmware: {ex.Message}", System.Diagnostics.TraceLevel.Warning);
                SetupFirmwareFileText.Text =
                    "The firmware list could not be reached. It may not be published yet. You can choose a file from this computer instead.";
                ScreenReaderOutput.Speak("Could not reach the firmware list.", VerbosityLevel.Terse, interrupt: true);
            }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine($"SetupGetFirmware: {ex.Message}", System.Diagnostics.TraceLevel.Error);
                SetupFirmwareFileText.Text = "The firmware could not be downloaded. See the trace file for details.";
                ScreenReaderOutput.Speak("Download failed.", VerbosityLevel.Terse, interrupt: true);
            }
            finally
            {
                SetupGetFirmwareButton.IsEnabled = _rig != null && _rig.IsConnected && !_rig.IsWanConnection;
            }
        }

        private void SetupChooseFirmwareButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a firmware file",
                Filter = "Radio firmware (*.ssdr)|*.ssdr|All files (*.*)|*.*",
                CheckFileExists = true,
            };

            if (dlg.ShowDialog() != true)
            {
                ScreenReaderOutput.Speak("Cancelled.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            _chosenFirmwarePath = dlg.FileName;

            // Run the preflight straight away rather than at send time. Hashing a
            // 60 MB image takes a moment, and finding out it is the wrong file is
            // much better here than after committing to an update.
            var check = _rig?.PreflightFirmwareUpdate(_chosenFirmwarePath);
            if (check == null)
            {
                SetupFirmwareFileText.Text = "No radio is connected.";
                ScreenReaderOutput.Speak("No radio connected.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (!check.CanProceed)
            {
                _chosenFirmwarePath = string.Empty;
                SetupSendFirmwareButton.IsEnabled = false;
                SetupFirmwareFileText.Text = check.BlockReason;
                ScreenReaderOutput.Speak(check.BlockReason, VerbosityLevel.Terse, interrupt: true);
                return;
            }

            SetupSendFirmwareButton.IsEnabled = true;
            string mb = (check.SizeBytes / 1024.0 / 1024.0).ToString("F1");
            SetupFirmwareFileText.Text =
                $"Chosen: {check.FileName}, {mb} megabytes. Checksum {check.ActualSha256}. Nothing has been sent to the radio yet.";
            ScreenReaderOutput.Speak($"{check.FileName} ready to send.", VerbosityLevel.Terse, interrupt: true);
        }

        private void SetupSendFirmwareButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null || string.IsNullOrEmpty(_chosenFirmwarePath)) return;

            // Re-run the preflight. The radio may have started transmitting or
            // picked up another client since the file was chosen, and both matter.
            var check = _rig.PreflightFirmwareUpdate(_chosenFirmwarePath);
            if (!check.CanProceed)
            {
                SetupFirmwareFileText.Text = check.BlockReason;
                ScreenReaderOutput.Speak(check.BlockReason, VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var warnings = new List<string>(check.Warnings)
            {
                "Do not switch the radio off or unplug it while the update runs. Interrupting it partway is the one thing that can leave a radio needing a service visit.",
                "The radio will be unreachable for several minutes and will restart on its own when it is done.",
            };

            var confirm = new ConfirmActionDialog(
                "Send Firmware to Radio",
                $"JJ Flex will send {check.FileName} ({(check.SizeBytes / 1024.0 / 1024.0):F1} megabytes) to the radio.",
                warnings,
                question: "Continue?",
                yesLabel: "_Send",
                radioModel: _rig.RadioModel);

            if (confirm.ShowDialog() != true)
            {
                SetupFirmwareFileText.Text = "Cancelled. Nothing was sent to the radio.";
                ScreenReaderOutput.Speak("Cancelled.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            // Captured before the send: once the radio drops off the network the
            // connection is gone and neither of these can be read again.
            string serial = _rig.SelectedRadioSerial;
            string previousVersion = _rig.RadioFirmwareVersion;

            if (_rig.BeginFirmwareUpdate(_chosenFirmwarePath,
                onTransferFault: detail => Dispatcher.BeginInvoke(() =>
                {
                    // Spoken announcement comes from FlexBase; this is the text
                    // for whoever still has the dialog open. The watcher keeps
                    // running and will report what version the radio returns on.
                    if (SetupFirmwareFileText == null) return;
                    SetupFirmwareFileText.Text =
                        "The radio closed the connection during the upload, so the update was not applied. " +
                        "If the radio restarts anyway, JJ Flex is watching and will report the version it comes back on. " +
                        $"Detail: {detail}";
                })))
            {
                SetupFirmwareFileText.Text =
                    "Sending. The radio applies the update and restarts on its own; this takes several minutes. " +
                    "JJ Flex is watching for it to come back and will say so when the new firmware is confirmed — " +
                    "you can close Settings and leave it running.";
                ScreenReaderOutput.Speak(
                    "Sending firmware. Do not switch the radio off. This takes several minutes.",
                    VerbosityLevel.Critical, interrupt: true);

                // FlexLib reports nothing back, so watch the radio instead of
                // waiting to be told. The watcher outlives this dialog on purpose:
                // the answer arrives minutes later and the user should not have to
                // sit on this tab to hear it.
                _ = _rig.WatchFirmwareUpdateAsync(
                    serial,
                    previousVersion,
                    onProgress: p => Dispatcher.BeginInvoke(() =>
                    {
                        // The dialog may be gone by now; the spoken announcement
                        // from the watcher is the part that always lands.
                        if (SetupFirmwareFileText == null) return;
                        SetupFirmwareFileText.Text = p.Message;
                        if (p.IsTerminal)
                        {
                            RefreshFirmwareStatus();
                            RefreshSetupStatuses();
                        }
                    }),
                    speakResult: true);
            }
            else
            {
                SetupFirmwareFileText.Text = "The firmware could not be sent. See the trace file for details.";
                ScreenReaderOutput.Speak("Could not send the firmware.", VerbosityLevel.Terse, interrupt: true);
            }
        }

        private void SetupSuppressVersionWarningButton_Click(object sender, RoutedEventArgs e)
        {
            string path = FlexBase.CreateFirmwareVersionCheckBypass();
            if (string.IsNullOrEmpty(path))
            {
                SetupFirmwareFileText.Text = "The setting could not be written. See the trace file for details.";
                ScreenReaderOutput.Speak("Could not change the setting.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            SetupFirmwareFileText.Text =
                "Done. Version mismatches will no longer be reported. This takes effect the next time JJ Flex starts, and changes nothing on the radio.";
            ScreenReaderOutput.Speak("Version mismatch reporting switched off.", VerbosityLevel.Terse, interrupt: true);
            RefreshFirmwareStatus();
        }

        #endregion

        #region Steps 5 to 7

        /// <summary>
        /// Step 5 hands off to the Network tab rather than duplicating the port
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
        /// Step 6 runs the same SmartLink probe as the Network tab's Test network
        /// button, then folds the result into this step's status line so the
        /// checklist stays the single place to read.
        /// </summary>
        private async void SetupTestNetworkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null)
            {
                SetupCheckStatus.Text = "No radio is selected.";
                ScreenReaderOutput.Speak("No radio selected.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            SetupCheckStatus.Text = "Checking. This usually takes a few seconds.";
            ScreenReaderOutput.Speak("Checking the network.", VerbosityLevel.Terse, interrupt: true);

            Radios.SmartLink.NetworkDiagnosticReport? report;
            try
            {
                report = await _rig.RunNetworkDiagnosticAsync(forceRefresh: true);
            }
            catch (Exception ex)
            {
                SetupCheckStatus.Text = $"The check failed: {ex.Message}";
                ScreenReaderOutput.Speak("The check failed.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (report == null)
            {
                SetupCheckStatus.Text =
                    "There is no SmartLink session, so the outside check cannot run. Sign in to SmartLink and try again.";
                ScreenReaderOutput.Speak("No SmartLink session.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (!report.ProbeCompleted)
            {
                SetupCheckStatus.Text = $"The check did not finish. {report.ErrorDetail}";
                ScreenReaderOutput.Speak("The check did not finish.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            string summary = BuildNetworkDiagnosticSummary(report);
            SetupCheckStatus.Text = summary;
            ScreenReaderOutput.Speak(summary, VerbosityLevel.Terse, interrupt: true);
            RefreshReachabilityStatus();
        }

        /// <summary>
        /// Step 7 shares the reboot flow with the hotkey binding in globals.vb via
        /// <see cref="RadioMaintenance"/> — same confirmation, same naming of the
        /// other stations about to be dropped, same absence of a presence gate.
        /// </summary>
        private void SetupRebootButton_Click(object sender, RoutedEventArgs e)
        {
            if (RadioMaintenance.RebootWithConfirmation(_rig, OnRebootInitiated))
            {
                SetupRestartStatus.Text =
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
