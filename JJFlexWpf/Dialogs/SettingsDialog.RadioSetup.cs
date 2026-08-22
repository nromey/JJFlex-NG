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

            LoadEnforcePrivateIpIntoUi();
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
                    Lexicon.Get("settings.radio.connect.not_done");
                SetupRegisterStatus.Text = Lexicon.Get("settings.radio.waiting_on_step_1");
                SetupAddressStatus.Text = Lexicon.Get("settings.radio.waiting_on_step_1");
                SetupWayInStatus.Text = Lexicon.Get("settings.radio.waiting_on_step_1");
                SetupCheckStatus.Text = Lexicon.Get("settings.radio.waiting_on_step_1");
                SetupRestartStatus.Text = Lexicon.Get("settings.radio.waiting_on_step_1");
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
            string where = _rig.CurrentRadioIP?.ToString()
                ?? Lexicon.Get("settings.radio.address_unknown");
            SetupConnectStatus.Text = overSmartLink
                ? Lexicon.Get("settings.radio.connect.done_over_smartlink")
                : Lexicon.Get("settings.radio.connect.done_local", ("where", where));

            // Step 2 — registration. Both buttons are off over SmartLink: getting
            // here that way proves the radio is already registered, and unregister
            // over SmartLink would cut the branch you are sitting on.
            var regCheck = _rig.PreflightSmartLinkRegistration();
            SetupRegisterButton.IsEnabled = regCheck.CanProceed;
            SetupUnregisterButton.IsEnabled = regCheck.CanProceed;

            if (overSmartLink)
            {
                SetupRegisterStatus.Text =
                    Lexicon.Get("settings.radio.register.done_over_smartlink");
            }
            else if (!regCheck.CanProceed)
            {
                SetupRegisterStatus.Text = Lexicon.Get("settings.radio.register.blocked",
                    ("reason", regCheck.BlockReason));
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
                        Lexicon.Get("settings.radio.register.already_registered",
                            ("accountEmail", regCheck.AccountEmail)),
                    FlexBase.SmartLinkRegistrationQuery.NotRegistered =>
                        Lexicon.Get("settings.radio.register.not_registered",
                            ("accountEmail", regCheck.AccountEmail)),
                    _ =>
                        Lexicon.Get("settings.radio.register.checking",
                            ("accountEmail", regCheck.AccountEmail),
                            ("state", _rig.RegistrationStateText)),
                };
                KickRegistrationQuery();
            }

            // Step 4 — addressing.
            var staticIp = _rig.CurrentStaticIP;
            SetupAddressStatus.Text = staticIp != null
                ? Lexicon.Get("settings.radio.address.done", ("staticIp", staticIp))
                : Lexicon.Get("settings.radio.address.not_done");

            // Step 5 — the way in from outside.
            bool forwarding = _rig.PortForwardingEnabled;
            int tcp = _rig.PortForwardingTcpPort;
            int udp = _rig.PortForwardingUdpPort;
            var mode = _rig.CurrentAccountConnectionMode ?? SmartLinkConnectionMode.ManualPortForwardOnly;
            string modeText = mode switch
            {
                SmartLinkConnectionMode.AutomaticHolePunch =>
                    Lexicon.Get("settings.radio.mode.hole_punch_allowed"),
                SmartLinkConnectionMode.ManualPlusUpnp =>
                    Lexicon.Get("settings.radio.mode.upnp_allowed"),
                _ => Lexicon.Get("settings.radio.mode.forward_only"),
            };

            if (forwarding && tcp > 0)
            {
                // Track C wording fix: the radio advertises these external
                // ports; it listens on its LAN address at TCP 4994 / UDP 4993,
                // and the router rules must say so.
                string ports = (udp > 0 && udp != tcp)
                    ? Lexicon.Get("settings.radio.way_in.ports_separate", ("tcp", tcp), ("udp", udp))
                    : Lexicon.Get("settings.radio.way_in.ports_same", ("tcp", tcp));
                int udpShown = udp > 0 ? udp : tcp;
                SetupWayInStatus.Text = Lexicon.Get("settings.radio.way_in.set",
                    ("ports", ports),
                    ("modeText", modeText),
                    ("tcp", tcp),
                    ("tlsPort", FlexBase.SmartLinkRadioTlsPort),
                    ("udpShown", udpShown),
                    ("udpPort", FlexBase.SmartLinkRadioUdpPort));
            }
            else
            {
                SetupWayInStatus.Text = Lexicon.Get("settings.radio.way_in.not_set",
                    ("modeText", modeText));
            }

            // Step 6 — whether any of it works from outside. Reports the last probe
            // if one has run rather than pretending nothing is known; a stale answer
            // with a caveat beats no answer.
            var report = _rig.MostRecentNetworkReport;
            if (report == null)
            {
                SetupCheckStatus.Text = Lexicon.Get("settings.radio.check.not_run");
            }
            else if (!report.ProbeCompleted)
            {
                SetupCheckStatus.Text = Lexicon.Get("settings.radio.check.last_did_not_finish",
                    ("detail", report.ErrorDetail));
            }
            else
            {
                SetupCheckStatus.Text = Lexicon.Get("settings.radio.check.last",
                    ("summary", BuildNetworkDiagnosticSummary(report)));
            }

            // Step 7 — restart.
            SetupRestartStatus.Text = staticIp != null
                ? Lexicon.Get("settings.radio.restart.pending")
                : Lexicon.Get("settings.radio.restart.nothing_waiting");
        }

        private void RadioSetupRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshRadioSetupTab();
            ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.steps_refreshed"),
                VerbosityLevel.Terse, interrupt: true);
        }

        #region Step 2 — radio name

        /// <summary>
        /// True once the user has typed in the setup-tab name box this session.
        /// Set from a TextChanged hook (wired in the constructor) that only
        /// fires while keyboard focus is inside the box, so programmatic
        /// refreshes never count. Track C: this is what lets OK/Apply commit a
        /// typed-but-never-applied name instead of discarding it — while a box
        /// that merely holds a stale copy of the radio's name stays inert.
        /// </summary>
        private bool _setupNameEdited;

        /// <summary>
        /// Fold a typed-but-not-yet-applied setup-tab name into the dialog's
        /// OK/Apply commit. The "Apply name" button remains for the checklist's
        /// do-it-now flow; this is the safety net under it. Runs AFTER the
        /// Radios-tab profile commit, so if both name boxes were edited for the
        /// same radio, the one typed here wins (deterministic, and this box is
        /// only editable while connected — the more deliberate act).
        /// </summary>
        private void CommitSetupRadioNameIfDirty(System.Collections.Generic.List<string> applied)
        {
            if (!_setupNameEdited) return;
            if (SetupRadioNameBox == null || _rig == null || !_rig.IsConnected) return;

            string newName = SetupRadioNameBox.Text?.Trim() ?? string.Empty;
            if (newName.Length == 0 || newName == _rig.RadioNickname)
            {
                _setupNameEdited = false;
                if (newName.Length == 0) RefreshRadioNameField(connected: true);
                return;
            }

            if (_rig.RenameRadio(newName))
            {
                applied.Add(Lexicon.Get("settings.radio.name.renamed", ("newName", newName)));
                RefreshRadioNameField(connected: true);
            }
            else
            {
                applied.Add(Lexicon.Get("settings.radio.name.rename_failed_note"));
            }
            _setupNameEdited = false;
        }

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
            {
                SetupRadioNameBox.Text = connected ? _rig!.RadioNickname : string.Empty;
                _setupNameEdited = false;
            }
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
                ScreenReaderOutput.Speak(Lexicon.Get("settings.no_radio_connected"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            string newName = SetupRadioNameBox.Text?.Trim() ?? string.Empty;
            if (newName.Length == 0)
            {
                // An empty name would show as Unknown everywhere — refuse and
                // put the current name back so the box matches reality.
                SetupRadioNameBox.Text = _rig.RadioNickname;
                ScreenReaderOutput.Speak(
                    Lexicon.Get("settings.radio.name.type_one_first"),
                    VerbosityLevel.Terse, interrupt: true);
                SetupRadioNameBox.Focus();
                return;
            }

            if (_rig.RenameRadio(newName))
            {
                // Critical: this is a confirmation of a radio-side change the
                // user cannot see any other way from here.
                ScreenReaderOutput.Speak(
                    Lexicon.Get("settings.radio.name.renamed_spoken", ("newName", newName)),
                    VerbosityLevel.Critical, interrupt: true);
                _setupNameEdited = false;
                RefreshRadioNameField(connected: true);
            }
            else
            {
                ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.name.rename_failed_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
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
                Lexicon.Get("settings.radio.register.confirm_title"),
                Lexicon.Get("settings.radio.register.confirm_body",
                    ("accountEmail", check.AccountEmail)),
                check.Warnings,
                question: Lexicon.Get("settings.radio.register.confirm_question"),
                yesLabel: Lexicon.Get("settings.radio.register.confirm_yes"),
                radioModel: _rig.RadioModel);

            if (confirm.ShowDialog() != true)
            {
                SetupRegisterStatus.Text = Lexicon.Get("settings.radio.cancelled_nothing_sent");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.cancelled"), VerbosityLevel.Terse, interrupt: true);
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
                Lexicon.Get("settings.radio.unregister.confirm_title"),
                Lexicon.Get("settings.radio.unregister.confirm_body"),
                new[]
                {
                    Lexicon.Get("settings.radio.unregister.warning_needs_someone_there"),
                    Lexicon.Get("settings.radio.unregister.warning_strands_the_radio"),
                },
                question: Lexicon.Get("settings.radio.unregister.confirm_question"),
                yesLabel: Lexicon.Get("settings.radio.unregister.confirm_yes"),
                radioModel: _rig.RadioModel);

            if (confirm.ShowDialog() != true)
            {
                SetupRegisterStatus.Text = Lexicon.Get("settings.radio.unregister.cancelled");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.cancelled"), VerbosityLevel.Terse, interrupt: true);
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
                ? Lexicon.Get("settings.radio.register.starting")
                : Lexicon.Get("settings.radio.unregister.starting");
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
                    Lexicon.Get("settings.radio.register.command_not_sent");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.register.command_not_sent_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
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
                SetupFirmwareStatus.Text = Lexicon.Get("settings.radio.waiting_on_step_1");
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
                    ? Lexicon.Get("settings.radio.firmware.version_unreported")
                    : Lexicon.Get("settings.radio.firmware.running", ("running", running))
            };

            if (_rig.IsInRecoveryState)
            {
                parts.Add(Lexicon.Get("settings.radio.firmware.in_recovery"));
            }

            if (FlexBase.FirmwareVersionCheckBypassed)
            {
                parts.Add(Lexicon.Get("settings.radio.firmware.check_switched_off"));
                SetupSuppressVersionWarningButton.Visibility = Visibility.Collapsed;
            }
            else if (_rig.FirmwareDiffersFromLibraryExpectation)
            {
                parts.Add(Lexicon.Get("settings.radio.firmware.differs_from_library",
                    ("expected", FlexBase.LibraryExpectedFirmwareVersion)));
                SetupSuppressVersionWarningButton.Visibility = Visibility.Visible;
            }
            else
            {
                parts.Add(Lexicon.Get("settings.radio.firmware.matches"));
                SetupSuppressVersionWarningButton.Visibility = Visibility.Collapsed;
            }

            if (!local)
                parts.Add(Lexicon.Get("settings.radio.firmware.smartlink_cannot_carry"));

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
                SetupFirmwareFileText.Text = Lexicon.Get("settings.radio.firmware.no_radio");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.no_radio_connected"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            string model = _rig.RadioModel;
            SetupGetFirmwareButton.IsEnabled = false;
            SetupFirmwareFileText.Text = Lexicon.Get("settings.radio.firmware.looking");
            ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.firmware.looking_spoken"),
                VerbosityLevel.Terse, interrupt: true);

            try
            {
                var catalog = new JJFlexUpdater.Firmware.FirmwareCatalog();
                var manifest = await catalog.FetchAsync();
                var image = JJFlexUpdater.Firmware.FirmwareCatalog.BestImageFor(manifest, model, _rig.RadioIsBigBend);

                if (image == null)
                {
                    SetupFirmwareFileText.Text =
                        Lexicon.Get("settings.radio.firmware.none_for_model", ("model", model));
                    ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.firmware.none_for_model_spoken"),
                        VerbosityLevel.Terse, interrupt: true);
                    return;
                }

                string running = _rig.RadioFirmwareVersion;
                if (!string.IsNullOrEmpty(running)
                    && JJFlexUpdater.Firmware.FirmwareCatalog.CompareVersions(image.Version, running) <= 0)
                {
                    SetupFirmwareFileText.Text =
                        Lexicon.Get("settings.radio.firmware.up_to_date",
                            ("running", running), ("version", image.Version));
                    ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.firmware.up_to_date_spoken"),
                        VerbosityLevel.Terse, interrupt: true);
                    return;
                }

                // Advisory only. Getting a stepping-stone requirement wrong in
                // either direction is worse than telling the user what we know.
                if (!string.IsNullOrWhiteSpace(image.MinVersionForDirectUpdate)
                    && !string.IsNullOrEmpty(running)
                    && JJFlexUpdater.Firmware.FirmwareCatalog.CompareVersions(running, image.MinVersionForDirectUpdate) < 0)
                {
                    SetupFirmwareFileText.Text =
                        Lexicon.Get("settings.radio.firmware.stepping_stone",
                            ("version", image.Version),
                            ("minVersion", image.MinVersionForDirectUpdate),
                            ("running", running));
                    // Speak the reason itself, not "see the message" — the
                    // information reaching the ear is the whole point.
                    ScreenReaderOutput.Speak(
                        Lexicon.Get("settings.radio.firmware.stepping_stone_spoken",
                            ("version", image.Version),
                            ("minVersion", image.MinVersionForDirectUpdate),
                            ("running", running)),
                        VerbosityLevel.Terse, interrupt: true);
                }

                string dir = System.IO.Path.Combine(
                    Radios.RadioConfig.AppDataRoot, "firmware");

                // Drive a real ProgressBar and let the screen reader report it the
                // way the user configured. No spoken percentages: NVDA already
                // has a progress-bar setting (beep / speak / both / off), and the
                // app announcing its own percentages both overrides that choice
                // and interrupts other speech to do it.
                SetupFirmwareProgress.Visibility = Visibility.Visible;
                SetupFirmwareProgressText.Visibility = Visibility.Visible;
                SetupFirmwareProgress.IsIndeterminate = false;
                SetupFirmwareProgress.Value = 0;
                SetupFirmwareProgressText.Text = Lexicon.Get("settings.radio.firmware.download_starting");

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
                                    Lexicon.Get("settings.radio.firmware.download_progress",
                                        ("read", (read / 1024.0 / 1024.0).ToString("F1")),
                                        ("total", (total.Value / 1024.0 / 1024.0).ToString("F1")));
                            }
                            else
                            {
                                // No Content-Length: an indeterminate bar is honest,
                                // a fake percentage is not.
                                SetupFirmwareProgress.IsIndeterminate = true;
                                SetupFirmwareProgressText.Text =
                                    Lexicon.Get("settings.radio.firmware.download_progress_no_total",
                                        ("read", (read / 1024.0 / 1024.0).ToString("F1")));
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
                    Lexicon.Get("settings.radio.firmware.downloaded",
                        ("version", image.Version),
                        ("megabytes", (check.SizeBytes / 1024.0 / 1024.0).ToString("F1")));
                ScreenReaderOutput.Speak(
                    Lexicon.Get("settings.radio.firmware.downloaded_spoken", ("version", image.Version)),
                    VerbosityLevel.Terse, interrupt: true);
            }
            catch (JJFlexUpdater.Net.UpdaterFetchException ex)
            {
                JJTrace.Tracing.TraceLine($"SetupGetFirmware: {ex.Message}", System.Diagnostics.TraceLevel.Warning);
                SetupFirmwareFileText.Text =
                    Lexicon.Get("settings.radio.firmware.list_unreachable");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.firmware.list_unreachable_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
            }
            catch (Exception ex)
            {
                JJTrace.Tracing.TraceLine($"SetupGetFirmware: {ex.Message}", System.Diagnostics.TraceLevel.Error);
                SetupFirmwareFileText.Text = Lexicon.Get("settings.radio.firmware.download_failed");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.firmware.download_failed_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
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
                Title = Lexicon.Get("settings.radio.firmware.choose_file_title"),
                Filter = Lexicon.Get("settings.radio.firmware.choose_file_filter"),
                CheckFileExists = true,
            };

            if (dlg.ShowDialog() != true)
            {
                ScreenReaderOutput.Speak(Lexicon.Get("settings.cancelled"), VerbosityLevel.Terse, interrupt: true);
                return;
            }

            _chosenFirmwarePath = dlg.FileName;

            // Run the preflight straight away rather than at send time. Hashing a
            // 60 MB image takes a moment, and finding out it is the wrong file is
            // much better here than after committing to an update.
            var check = _rig?.PreflightFirmwareUpdate(_chosenFirmwarePath);
            if (check == null)
            {
                SetupFirmwareFileText.Text = Lexicon.Get("settings.radio.firmware.no_radio");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.no_radio_connected"),
                    VerbosityLevel.Terse, interrupt: true);
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
                Lexicon.Get("settings.radio.firmware.chosen",
                    ("fileName", check.FileName),
                    ("megabytes", mb),
                    ("checksum", check.ActualSha256));
            ScreenReaderOutput.Speak(
                Lexicon.Get("settings.radio.firmware.chosen_spoken", ("fileName", check.FileName)),
                VerbosityLevel.Terse, interrupt: true);
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
                Lexicon.Get("settings.radio.firmware.warning_do_not_interrupt"),
                Lexicon.Get("settings.radio.firmware.warning_unreachable_while_updating"),
            };

            var confirm = new ConfirmActionDialog(
                Lexicon.Get("settings.radio.firmware.send_confirm_title"),
                Lexicon.Get("settings.radio.firmware.send_confirm_body",
                    ("fileName", check.FileName),
                    ("megabytes", (check.SizeBytes / 1024.0 / 1024.0).ToString("F1"))),
                warnings,
                question: Lexicon.Get("settings.radio.firmware.send_confirm_question"),
                yesLabel: Lexicon.Get("settings.radio.firmware.send_confirm_yes"),
                radioModel: _rig.RadioModel);

            if (confirm.ShowDialog() != true)
            {
                SetupFirmwareFileText.Text = Lexicon.Get("settings.radio.cancelled_nothing_sent");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.cancelled"), VerbosityLevel.Terse, interrupt: true);
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
                        Lexicon.Get("settings.radio.firmware.transfer_fault", ("detail", detail));
                })))
            {
                SetupFirmwareFileText.Text =
                    Lexicon.Get("settings.radio.firmware.sending");
                ScreenReaderOutput.Speak(
                    Lexicon.Get("settings.radio.firmware.sending_spoken"),
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
                SetupFirmwareFileText.Text = Lexicon.Get("settings.radio.firmware.send_failed");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.firmware.send_failed_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
            }
        }

        private void SetupSuppressVersionWarningButton_Click(object sender, RoutedEventArgs e)
        {
            string path = FlexBase.CreateFirmwareVersionCheckBypass();
            if (string.IsNullOrEmpty(path))
            {
                SetupFirmwareFileText.Text = Lexicon.Get("settings.radio.firmware.bypass_write_failed");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.firmware.bypass_write_failed_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            SetupFirmwareFileText.Text =
                Lexicon.Get("settings.radio.firmware.bypass_done");
            ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.firmware.bypass_done_spoken"),
                VerbosityLevel.Terse, interrupt: true);
            RefreshFirmwareStatus();
        }

        #endregion

        #region Steps 5 to 7

        /// <summary>
        /// Step 5 hands off to the Network category rather than duplicating the
        /// port and connection-mode controls. Moving focus (not just selecting)
        /// is what makes this work with a screen reader — otherwise the
        /// category changes silently and the user is left reading the old one.
        /// </summary>
        /// <remarks>
        /// The focus TARGET moved with the category list (Sprint 32 Track G,
        /// task #134). It was <c>tab.Focus()</c>, which worked because the tab
        /// strip was a real focusable visual; the strip is templated away now,
        /// so that call would silently do nothing and this button would look
        /// broken to precisely the operator it exists for. FocusCategory lands
        /// on the list row instead and announces the same way.
        /// </remarks>
        private void SetupGoToNetworkButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in SettingsTabs.Items)
            {
                if (item is TabItem tab && (tab.Header as string) == "Network")
                {
                    SettingsTabs.SelectedItem = tab;
                    FocusCategory();
                    // DELETED: the focus change raises a UIA event and the
                    // screen reader announces the category name and its
                    // position itself. Speaking "Network settings." raced that
                    // announcement for the same instant and won, replacing a
                    // fuller message with a shorter one.
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
                SetupCheckStatus.Text = Lexicon.Get("settings.radio.check.no_radio_selected");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.check.no_radio_selected_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            // QB Track D (item 7): on a hole-punched session this probe can
            // drop the live connection — warn and confirm, never a silent
            // gate. Shared guard with the Network tab's Test network button.
            if (!ConfirmNetworkTestOnPunchedSession())
            {
                SetupCheckStatus.Text =
                    Lexicon.Get("settings.radio.check.punched_declined");
                return;
            }

            SetupCheckStatus.Text = Lexicon.Get("settings.radio.check.running");
            ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.check.running_spoken"),
                VerbosityLevel.Terse, interrupt: true);

            Radios.SmartLink.NetworkDiagnosticReport? report;
            try
            {
                report = await _rig.RunNetworkDiagnosticAsync(forceRefresh: true);
            }
            catch (Exception ex)
            {
                SetupCheckStatus.Text = Lexicon.Get("settings.radio.check.failed", ("message", ex.Message));
                ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.check.failed_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (report == null)
            {
                SetupCheckStatus.Text =
                    Lexicon.Get("settings.radio.check.no_smartlink_session");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.check.no_smartlink_session_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (!report.ProbeCompleted)
            {
                SetupCheckStatus.Text = Lexicon.Get("settings.radio.check.did_not_finish",
                    ("detail", report.ErrorDetail));
                ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.check.did_not_finish_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
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
                    Lexicon.Get("settings.radio.restart.started");
            }
        }

        #endregion

        #region Network tab — reachability, private-IP enforcement

        // QB Track C: the account-level hole-punch port editor that lived here
        // (HolePunchPortBox + random/clear/save) is gone. It wrote the CLIENT
        // punch port into SmartLinkAccount.ConfiguredListenPort — the same slot
        // the port-forward Apply writes the RADIO-side forwarded port into. One
        // field, two meanings. The punch port now lives in the per-radio
        // profile (Radios tab, RadioConfig.FixedHolePunchPort); the account
        // field keeps only the forwarded-port meaning. Legacy values written by
        // the old editor are still honored by sendRemoteConnect's fallback.

        private void LoadEnforcePrivateIpIntoUi()
        {
            if (EnforcePrivateIpCheck != null && _rig != null && _rig.IsConnected)
            {
                _suppressPrivateIpAnnouncement = true;
                try { EnforcePrivateIpCheck.IsChecked = _rig.EnforcePrivateIPConnections; }
                finally { _suppressPrivateIpAnnouncement = false; }
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
                ReachabilityStatusText.Text = Lexicon.Get("settings.network.reachability.no_radio");
                if (EnforcePrivateIpCheck != null) EnforcePrivateIpCheck.IsEnabled = false;
                return;
            }

            if (EnforcePrivateIpCheck != null) EnforcePrivateIpCheck.IsEnabled = true;

            var parts = new List<string>
            {
                _rig.IsWanConnection
                    ? Lexicon.Get("settings.network.reachability.via_smartlink")
                    : Lexicon.Get("settings.network.reachability.direct")
            };

            if (_rig.RadioPortForwardActive)
            {
                parts.Add(Lexicon.Get("settings.network.reachability.forward_seen"));
            }
            else
            {
                parts.Add(Lexicon.Get("settings.network.reachability.forward_not_seen"));
            }

            int tls = _rig.RadioPublicTlsPort;
            int udp = _rig.RadioPublicUdpPort;
            if (tls > 0 || udp > 0)
            {
                parts.Add(Lexicon.Get("settings.network.reachability.public_ports",
                    ("tls", tls > 0 ? tls.ToString()
                        : Lexicon.Get("settings.network.reachability.port_none")),
                    ("udp", udp > 0 ? udp.ToString()
                        : Lexicon.Get("settings.network.reachability.port_none"))));
            }

            if (_rig.RadioRequiresHolePunch)
            {
                parts.Add(Lexicon.Get("settings.network.reachability.needs_hole_punch"));
            }
            else
            {
                parts.Add(Lexicon.Get("settings.network.reachability.no_hole_punch_needed"));
            }

            int last = _rig.LastHolePunchPort;
            parts.Add(last > 0
                ? Lexicon.Get("settings.network.reachability.last_hole_punch_port", ("last", last))
                : Lexicon.Get("settings.network.reachability.no_last_hole_punch"));

            ReachabilityStatusText.Text = string.Join(" ", parts);
        }

        private void EnforcePrivateIpCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressPrivateIpAnnouncement) return;

            if (_rig == null || !_rig.IsConnected)
            {
                ScreenReaderOutput.Speak(Lexicon.Get("settings.no_radio_connected"),
                    VerbosityLevel.Terse, interrupt: true);
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
                    ? Lexicon.Get("settings.network.private_ip.local_only")
                    : Lexicon.Get("settings.network.private_ip.any_address"),
                VerbosityLevel.Terse, interrupt: true);
            RefreshReachabilityStatus();
        }

        #endregion
    }
}
