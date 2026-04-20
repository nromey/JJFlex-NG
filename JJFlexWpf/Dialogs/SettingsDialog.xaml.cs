using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;

namespace JJFlexWpf.Dialogs
{
    public partial class SettingsDialog : JJFlexDialog
    {
        private readonly PttConfig _pttConfig;
        private readonly LicenseConfig _licenseConfig;
        private readonly AudioOutputConfig _audioConfig;
        private (string, string)[] _countryMap = Array.Empty<(string, string)>();

        /// <summary>Config directory for per-operator file storage (filter presets, etc.).</summary>
        public string? ConfigDirectory { get; set; }

        /// <summary>Current operator name for per-operator file naming.</summary>
        public string? OperatorName { get; set; }

        /// <summary>
        /// Connected radio, used by the Network tab to configure SmartLink port forwarding.
        /// Set by NativeMenuBar after construction. Setter refreshes the Network tab UI.
        /// </summary>
        private FlexBase? _rig;
        public FlexBase? Rig
        {
            get => _rig;
            set { _rig = value; RefreshNetworkTabFromRig(); }
        }

        // Tuning step results (read after DialogResult == true)
        public int CoarseTuneStep { get; private set; }
        public int FineTuneStep { get; private set; }
        public bool BandMemoryEnabled { get; private set; }

        private static readonly (int hz, string label)[] CoarseStepOptions =
        {
            (1000, "1 kHz"), (2000, "2 kHz"), (5000, "5 kHz")
        };

        private static readonly (int hz, string label)[] FineStepOptions =
        {
            (5, "5 Hz"), (10, "10 Hz"), (100, "100 Hz")
        };

        private static readonly (string label, HamBands.Bands.Licenses value)[] LicenseClassMap =
        {
            ("Extra", HamBands.Bands.Licenses.extra),
            ("Advanced", HamBands.Bands.Licenses.advanced),
            ("General", HamBands.Bands.Licenses.general),
            ("Technician", HamBands.Bands.Licenses.technition)
        };

        private static readonly string[] MeterPresetOptions =
        {
            "RX Monitor", "TX Monitor", "Full Monitor"
        };

        public SettingsDialog(
            PttConfig pttConfig,
            int currentCoarseStep,
            int currentFineStep,
            LicenseConfig? licenseConfig = null,
            AudioOutputConfig? audioConfig = null)
        {
            _pttConfig = pttConfig;
            _licenseConfig = licenseConfig ?? new LicenseConfig();
            _audioConfig = audioConfig ?? new AudioOutputConfig();
            CoarseTuneStep = currentCoarseStep;
            FineTuneStep = currentFineStep;
            BandMemoryEnabled = pttConfig.BandMemoryEnabled;

            InitializeComponent();

            // Select all text when tabbing into any TextBox
            AddHandler(TextBox.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(TextBox_GotKeyboardFocus));

            // Configure volume controls
            MasterVolumeControl.Label = "Master volume";
            MasterVolumeControl.Min = 0;
            MasterVolumeControl.Max = 100;
            MasterVolumeControl.Step = 5;

            EarconVolumeControl.Label = "Alert volume";
            EarconVolumeControl.Min = 0;
            EarconVolumeControl.Max = 100;
            EarconVolumeControl.Step = 5;

            MeterVolumeControl.Label = "Meter volume";
            MeterVolumeControl.Min = 0;
            MeterVolumeControl.Max = 100;
            MeterVolumeControl.Step = 5;

            LoadSettings();
        }

        private void TextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.OriginalSource is TextBox tb)
                tb.SelectAll();
        }

        private void LoadSettings()
        {
            // PTT tab
            PttTimeoutBox.Text = _pttConfig.TimeoutSeconds.ToString();
            PttWarning1Box.Text = _pttConfig.Warning1SecondsBeforeTimeout.ToString();
            PttWarning2Box.Text = _pttConfig.Warning2SecondsBeforeTimeout.ToString();
            PttOhCrapBox.Text = _pttConfig.OhCrapSecondsBeforeTimeout.ToString();
            PttAlcBox.Text = _pttConfig.AlcAutoReleaseSeconds.ToString();
            PttSpeechCheckbox.IsChecked = _pttConfig.SpeechEnabled;
            ChirpEnabledCheckbox.IsChecked = _pttConfig.ChirpEnabled;

            // Tuning tab
            foreach (var (hz, label) in CoarseStepOptions)
            {
                CoarseStepCombo.Items.Add(label);
                if (hz == CoarseTuneStep)
                    CoarseStepCombo.SelectedIndex = CoarseStepCombo.Items.Count - 1;
            }
            if (CoarseStepCombo.SelectedIndex < 0) CoarseStepCombo.SelectedIndex = 0;

            foreach (var (hz, label) in FineStepOptions)
            {
                FineStepCombo.Items.Add(label);
                if (hz == FineTuneStep)
                    FineStepCombo.SelectedIndex = FineStepCombo.Items.Count - 1;
            }
            if (FineStepCombo.SelectedIndex < 0) FineStepCombo.SelectedIndex = 1; // 10 Hz default

            BandMemoryCheckbox.IsChecked = BandMemoryEnabled;

            // Tuning debounce
            TuneDebounceCheckbox.IsChecked = _audioConfig.TuneDebounceEnabled;
            DebounceDelayBox.Text = _audioConfig.TuneDebounceMs.ToString();
            DebounceDelayPanel.IsEnabled = _audioConfig.TuneDebounceEnabled;

            // Frequency units combo
            FreqUnitsCombo.Items.Add("Dotted (14.225.000)");
            FreqUnitsCombo.Items.Add("Kilohertz (14,225 kHz)");
            FreqUnitsCombo.Items.Add("Megahertz (14.225 MHz)");
            FreqUnitsCombo.SelectedIndex = (int)_pttConfig.FrequencyDisplayUnits;

            // License tab — populate from LicenseConfig
            foreach (var (label, _) in LicenseClassMap)
                LicenseClassCombo.Items.Add(label);
            // Find the matching entry for the current license
            int licIdx = 0;
            for (int i = 0; i < LicenseClassMap.Length; i++)
            {
                if (LicenseClassMap[i].value == _licenseConfig.LicenseClass) { licIdx = i; break; }
            }
            LicenseClassCombo.SelectedIndex = licIdx;

            BandBoundaryCheckbox.IsChecked = _licenseConfig.BoundaryNotifications;
            TxLockoutCheckbox.IsChecked = _licenseConfig.TxLockout;

            // Country selector — display names, stored as country codes
            _countryMap = new[] { ("US", "United States") }; // Future: add ("UK", "United Kingdom"), etc.
            foreach (var (code, name) in _countryMap)
                CountryCombo.Items.Add(name);
            string currentCode = _licenseConfig.Country ?? "US";
            int countryIdx = Array.FindIndex(_countryMap, c => c.Item1 == currentCode);
            CountryCombo.SelectedIndex = countryIdx >= 0 ? countryIdx : 0;

            EnforceTxRulesCheckbox.IsChecked = _licenseConfig.EnforceTxRules;

            // Audio tab — master volume
            MasterVolumeControl.Value = (int)(_audioConfig.MasterVolume * 100);

            // Alert section
            EarconVolumeControl.Value = (int)(_audioConfig.AlertVolume * 100);

            var devices = EarconPlayer.GetOutputDevices();
            foreach (var (devNum, name) in devices)
            {
                EarconDeviceCombo.Items.Add(name);
                if (devNum == _audioConfig.EarconDeviceNumber)
                    EarconDeviceCombo.SelectedIndex = EarconDeviceCombo.Items.Count - 1;
            }
            if (EarconDeviceCombo.SelectedIndex < 0) EarconDeviceCombo.SelectedIndex = 0;

            // Meter section
            MeterVolumeControl.Value = (int)(_audioConfig.MeterMasterVolume * 100);

            // Meter device dropdown: first item is "Same as Alerts", then all devices
            MeterDeviceCombo.Items.Add("Same as Alerts");
            foreach (var (devNum, name) in devices)
                MeterDeviceCombo.Items.Add(name);
            if (_audioConfig.MeterDeviceNumber == -1)
            {
                MeterDeviceCombo.SelectedIndex = 0; // "Same as Alerts"
            }
            else
            {
                // Find matching device: offset by 1 for the "Same as Alerts" entry
                int meterDevIdx = -1;
                for (int i = 0; i < devices.Count; i++)
                {
                    if (devices[i].deviceNumber == _audioConfig.MeterDeviceNumber)
                    { meterDevIdx = i + 1; break; }
                }
                MeterDeviceCombo.SelectedIndex = meterDevIdx >= 0 ? meterDevIdx : 0;
            }

            foreach (var preset in MeterPresetOptions)
            {
                MeterPresetCombo.Items.Add(preset);
                if (preset == _audioConfig.MeterPreset)
                    MeterPresetCombo.SelectedIndex = MeterPresetCombo.Items.Count - 1;
            }
            if (MeterPresetCombo.SelectedIndex < 0) MeterPresetCombo.SelectedIndex = 0;

            PeakWatcherCheck.IsChecked = _audioConfig.PeakWatcherEnabled;
            MeterSpeechCheck.IsChecked = _audioConfig.MeterSpeechEnabled;

            // Typing sound mode
            PopulateTypingSoundCombo();

            // Braille section
            BrailleEnabledCheck.IsChecked = _audioConfig.BrailleEnabled;
            int[] cellOptions = { 20, 32, 40, 80 };
            foreach (int cells in cellOptions)
                BrailleCellsCombo.Items.Add(cells.ToString());
            int cellIdx = Array.IndexOf(cellOptions, _audioConfig.BrailleCellCount);
            BrailleCellsCombo.SelectedIndex = cellIdx >= 0 ? cellIdx : 2; // default 40

            // Verbosity & Notifications tab
            SpeechVerbosityCombo.Items.Add("Off (critical only)");  // 0
            SpeechVerbosityCombo.Items.Add("Terse");                // 1
            SpeechVerbosityCombo.Items.Add("Chatty");               // 2
            SpeechVerbosityCombo.SelectedIndex = Math.Clamp(_audioConfig.SpeechVerbosity, 0, 2);

            EarconsEnabledCheck.IsChecked = _audioConfig.EarconsEnabled;

            CwNotificationsCheck.IsChecked = _audioConfig.CwNotificationsEnabled;
            CwSidetoneBox.Text = _audioConfig.CwSidetoneHz.ToString();
            CwSpeedBox.Text = _audioConfig.CwSpeedWpm.ToString();
            CwModeAnnounceCheck.IsChecked = _audioConfig.CwModeAnnounce;

            MeterTonesNotifCheck.IsChecked = _audioConfig.MeterTonesEnabled;
            ShowPanadapterCheck.IsChecked = _audioConfig.ShowPanadapter;
            AnnounceSwrAfterTuneCheck.IsChecked = _audioConfig.AnnounceSwrAfterTune;

            // Network tab — defaults shown until Rig property is set (see RefreshNetworkTabFromRig)
            PortForwardEnabledCheck.IsChecked = false;
            PortForwardTcpBox.Text = "4992";
            PortForwardUdpBox.Text = "4992";
            PortForwardSeparatePortsCheck.IsChecked = false;
            PortForwardUdpBox.IsEnabled = false;
            PortForwardTcpLabel.Text = "Port (TCP and UDP):";
            NetworkCurrentStateText.Text = "No radio connected.";
        }

        /// <summary>
        /// Populate the Network tab from the connected radio's current state.
        /// Called whenever the Rig property is assigned.
        /// </summary>
        private void RefreshNetworkTabFromRig()
        {
            // These controls are only present after InitializeComponent. If the Rig setter
            // is called before the constructor finishes, skip.
            if (PortForwardEnabledCheck == null) return;

            if (_rig == null || !_rig.IsConnected)
            {
                NetworkCurrentStateText.Text = "No radio connected. Connect to a radio to configure port forwarding.";
                PortForwardEnabledCheck.IsChecked = false;
                PortForwardTcpBox.Text = "4992";
                PortForwardUdpBox.Text = "4992";
                PortForwardSeparatePortsCheck.IsChecked = false;
                PortForwardUdpBox.IsEnabled = false;
                PortForwardTcpLabel.Text = "Port (TCP and UDP):";
                // Sprint 27 Track B — Tier 2 UPnP is meaningless without a connected radio.
                if (UPnPEnabledCheck != null)
                {
                    UPnPEnabledCheck.IsChecked = false;
                    UPnPEnabledCheck.IsEnabled = false;
                }
                return;
            }

            bool enabled = _rig.PortForwardingEnabled;
            int tcp = _rig.PortForwardingTcpPort;
            int udp = _rig.PortForwardingUdpPort;
            bool portsDiffer = enabled && tcp > 0 && udp > 0 && tcp != udp;

            PortForwardEnabledCheck.IsChecked = enabled;
            PortForwardTcpBox.Text = (tcp > 0 ? tcp : 4992).ToString();
            PortForwardUdpBox.Text = (udp > 0 ? udp : 4992).ToString();
            PortForwardSeparatePortsCheck.IsChecked = portsDiffer;
            PortForwardUdpBox.IsEnabled = portsDiffer;
            PortForwardTcpLabel.Text = portsDiffer ? "TCP port:" : "Port (TCP and UDP):";
            NetworkCurrentStateText.Text = enabled
                ? (portsDiffer
                    ? $"Radio currently listens on TCP {tcp}, UDP {udp}."
                    : $"Radio currently listens on port {tcp} (TCP and UDP).")
                : "Radio currently uses UPnP or hole-punch (no manual forwarding).";

            // Sprint 27 Track B — UPnP checkbox reflects the account preference,
            // not the radio's firmware state. Null / no-account → unchecked + disabled.
            if (UPnPEnabledCheck != null)
            {
                UPnPEnabledCheck.IsChecked = _rig.CurrentAccountUPnPEnabled ?? false;
            }
            RecomputeUPnPCheckboxEnablement();
        }

        /// <summary>
        /// Sprint 27 Track B — Tier 2 requires a valid Tier 1 port first.
        /// Disable the UPnP checkbox until port-forward is enabled and the
        /// TCP port parses to a valid value. When disabling, also uncheck
        /// so the user can't leave the checkbox showing "checked but inert".
        /// </summary>
        private void RecomputeUPnPCheckboxEnablement()
        {
            if (UPnPEnabledCheck == null) return;

            bool tier1On = PortForwardEnabledCheck?.IsChecked == true;
            bool portValid = int.TryParse(PortForwardTcpBox?.Text, out int tcp)
                             && SmartLinkAccountManager.IsValidPort(tcp);
            bool shouldEnable = tier1On && portValid;

            UPnPEnabledCheck.IsEnabled = shouldEnable;
            if (!shouldEnable && UPnPEnabledCheck.IsChecked == true)
            {
                UPnPEnabledCheck.IsChecked = false;
            }
        }

        /// <summary>
        /// Sprint 27 Track B — re-evaluate UPnP checkbox enablement when the
        /// user toggles Tier 1 port-forward on/off.
        /// </summary>
        private void PortForwardEnabledCheck_Click(object sender, RoutedEventArgs e)
        {
            RecomputeUPnPCheckboxEnablement();
        }

        /// <summary>
        /// Advanced checkbox: when checked, UDP field is editable. When unchecked,
        /// UDP automatically mirrors TCP.
        /// </summary>
        private void PortForwardSeparatePortsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (PortForwardUdpBox == null || PortForwardTcpLabel == null) return;
            bool separate = PortForwardSeparatePortsCheck.IsChecked == true;
            PortForwardUdpBox.IsEnabled = separate;
            PortForwardTcpLabel.Text = separate ? "TCP port:" : "Port (TCP and UDP):";
            if (!separate)
                PortForwardUdpBox.Text = PortForwardTcpBox.Text;
        }

        /// <summary>
        /// When the user edits the TCP port, sync UDP to match unless the advanced
        /// "use different ports" checkbox is on.
        /// </summary>
        private void PortForwardTcpBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (PortForwardUdpBox == null || PortForwardSeparatePortsCheck == null) return;
            if (PortForwardSeparatePortsCheck.IsChecked != true)
                PortForwardUdpBox.Text = PortForwardTcpBox.Text;
            // Sprint 27 Track B — port validity feeds into UPnP gate.
            RecomputeUPnPCheckboxEnablement();
        }

        /// <summary>
        /// Apply the Network tab's port forwarding settings to the connected radio.
        /// Sends a "wan set" command to the radio's firmware (persists until changed again).
        /// </summary>
        private void ApplyPortForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null || !_rig.IsConnected)
            {
                NetworkCurrentStateText.Text = "No radio connected. Connect locally to the radio first.";
                ScreenReaderOutput.Speak("No radio connected.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            bool enabled = PortForwardEnabledCheck.IsChecked == true;
            int tcp = 0, udp = 0;
            if (enabled)
            {
                if (!int.TryParse(PortForwardTcpBox.Text, out tcp) || tcp < 1024 || tcp > 65535)
                {
                    NetworkCurrentStateText.Text = "Invalid TCP port. Must be 1024 to 65535.";
                    ScreenReaderOutput.Speak("Invalid TCP port.", VerbosityLevel.Terse, interrupt: true);
                    PortForwardTcpBox.Focus();
                    return;
                }
                if (!int.TryParse(PortForwardUdpBox.Text, out udp) || udp < 1024 || udp > 65535)
                {
                    NetworkCurrentStateText.Text = "Invalid UDP port. Must be 1024 to 65535.";
                    ScreenReaderOutput.Speak("Invalid UDP port.", VerbosityLevel.Terse, interrupt: true);
                    PortForwardUdpBox.Focus();
                    return;
                }
            }

            bool ok = _rig.SetSmartLinkPortForwarding(enabled, tcp, udp);
            if (ok)
            {
                // Sprint 27 Track A / Phase A.3 — also persist as account preference
                // so future connections (see FlexBase.ApplyAccountPortPreferenceIfAny)
                // auto-apply without user action. Only meaningful when a SmartLink
                // account is bound (gated by HasCurrentSmartLinkAccount). If the user
                // used advanced mode with separate TCP/UDP, we save the TCP value —
                // per-account model is single-port; advanced mode is per-session only.
                bool savedPreference = false;
                if (_rig.HasCurrentSmartLinkAccount)
                {
                    int? preference = enabled ? (int?)tcp : null;
                    savedPreference = _rig.SaveCurrentAccountListenPort(preference);

                    // Sprint 27 Track B / Phase B.2 — also persist Tier 2 UPnP opt-in.
                    // When Tier 1 is disabled (enabled=false), UPnP is meaningless;
                    // force the saved preference to false regardless of checkbox state.
                    bool upnpPref = enabled && UPnPEnabledCheck?.IsChecked == true;
                    _rig.SaveCurrentAccountUPnPEnabled(upnpPref);
                }

                string baseMessage = enabled
                    ? $"Applied. Radio now listens on TCP {tcp}, UDP {udp}. Configure your router to forward these ports to the radio's LAN IP."
                    : "Applied. Port forwarding disabled on the radio.";
                string prefSuffix = savedPreference
                    ? (enabled
                        ? $" Saved as the preference for SmartLink account {_rig.CurrentSmartLinkAccountEmail}."
                        : $" Preference cleared for SmartLink account {_rig.CurrentSmartLinkAccountEmail}.")
                    : string.Empty;
                NetworkCurrentStateText.Text = baseMessage + prefSuffix;
                ScreenReaderOutput.Speak(enabled
                    ? $"Port forwarding set to {tcp}."
                    : "Port forwarding disabled.", VerbosityLevel.Terse, interrupt: true);
            }
            else
            {
                NetworkCurrentStateText.Text = "Command failed. See trace file for details.";
                ScreenReaderOutput.Speak("Command failed.", VerbosityLevel.Terse, interrupt: true);
            }
        }

        /// <summary>
        /// Sprint 27 Track A / Phase A.3 — local validation of the TCP port
        /// field. Does NOT touch the radio, does NOT persist, does NOT test
        /// remote reachability. Verifies the value parses as an integer, is
        /// in the manual range (1024–65535), and warns on a small blocklist
        /// of ports likely to be in use by other common services. Announces
        /// the verdict to the Network tab's live region and speaks it.
        /// Actual reachability testing from the user's public IP to the
        /// radio is Track C's NetworkTest job, not this button's.
        /// </summary>
        private void TestPortButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortForwardTcpBox.Text, out int port))
            {
                NetworkCurrentStateText.Text = "Port is not a number. Enter a value between 1024 and 65535.";
                ScreenReaderOutput.Speak("Port is not a number.", VerbosityLevel.Terse, interrupt: true);
                PortForwardTcpBox.Focus();
                return;
            }
            if (!SmartLinkAccountManager.IsValidPort(port))
            {
                NetworkCurrentStateText.Text = $"Port {port} is out of the manual range. Use 1024 to 65535.";
                ScreenReaderOutput.Speak($"Port {port} out of range.", VerbosityLevel.Terse, interrupt: true);
                PortForwardTcpBox.Focus();
                return;
            }
            // Common-conflict blocklist — ports above 1024 where users are likely
            // to already run unrelated services. Warn, don't block; the port is
            // still technically valid.
            string? conflictHint = port switch
            {
                3389 => "Windows Remote Desktop",
                5900 => "VNC screen sharing",
                8080 => "web servers and HTTP proxies",
                _ => null,
            };
            if (conflictHint != null)
            {
                NetworkCurrentStateText.Text = $"Port {port} is valid, but it is commonly used by {conflictHint}. If you have that running on this network you should pick a different port; otherwise you can keep this one.";
                ScreenReaderOutput.Speak($"Port {port} valid but often used by {conflictHint}.", VerbosityLevel.Terse, interrupt: true);
                return;
            }
            NetworkCurrentStateText.Text = $"Port {port} is valid. Remember to forward it on your router to the radio's LAN IP address, TCP and UDP.";
            ScreenReaderOutput.Speak($"Port {port} is valid.", VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// Sprint 27 Track C / Phase C.3 — run a SmartLink NetworkTest probe
        /// against the connected radio via the active session's runner. Full
        /// probe, not local validation (that's Te_st port above). Announces a
        /// one-line summary to the diagnostic live region and speaks it.
        /// Full report (with ToMarkdown + copy/save) lands in Track D.
        /// </summary>
        private async void TestNetworkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null || !_rig.IsConnected)
            {
                NetworkDiagnosticResultText.Text = "No radio connected. Connect to a radio first.";
                ScreenReaderOutput.Speak("No radio connected.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            NetworkDiagnosticResultText.Text = "Probing SmartLink. Waiting for results — this usually takes a few seconds.";
            ScreenReaderOutput.Speak("Probing network.", VerbosityLevel.Terse, interrupt: true);

            Radios.SmartLink.NetworkDiagnosticReport? report;
            try
            {
                report = await _rig.RunNetworkDiagnosticAsync(forceRefresh: true);
            }
            catch (Exception ex)
            {
                NetworkDiagnosticResultText.Text = $"Probe failed: {ex.Message}";
                ScreenReaderOutput.Speak("Probe failed.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (report == null)
            {
                NetworkDiagnosticResultText.Text = "No active SmartLink session. Connect via SmartLink first.";
                ScreenReaderOutput.Speak("No SmartLink session.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (!report.ProbeCompleted)
            {
                NetworkDiagnosticResultText.Text = $"Probe did not complete: {report.ErrorDetail}";
                ScreenReaderOutput.Speak("Probe did not complete.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            // One-line summary for the dialog; Track D surfaces the full
            // markdown report with copy-to-clipboard + save-to-file.
            string summary = BuildNetworkDiagnosticSummary(report);
            NetworkDiagnosticResultText.Text = summary;
            ScreenReaderOutput.Speak(summary, VerbosityLevel.Terse, interrupt: true);
        }

        private static string BuildNetworkDiagnosticSummary(Radios.SmartLink.NetworkDiagnosticReport r)
        {
            string Yn(bool? v) => v switch { true => "yes", false => "no", null => "unknown" };
            return
                $"UPnP TCP {Yn(r.UpnpTcpReachable)}, UPnP UDP {Yn(r.UpnpUdpReachable)}, " +
                $"manual TCP {Yn(r.ManualForwardTcpReachable)}, manual UDP {Yn(r.ManualForwardUdpReachable)}, " +
                $"hole-punch support {Yn(r.NatSupportsHolePunch)}.";
        }

        // Typing sound combo order: always-available audio modes first, then any
        // unlocked easter-egg modes, then "Off" pinned at the end. "Off" lives at
        // the bottom of the list independent of how many easter eggs are unlocked
        // so the "disabled" choice is always where users expect it.
        //   0: Musical notes, 1: Single tone, 2: Random tones
        //   3+: Mechanical keyboard (if unlocked), Touch-tone (if unlocked)
        //   last: Off
        private void PopulateTypingSoundCombo()
        {
            TypingSoundCombo.Items.Clear();
            TypingSoundCombo.Items.Add("Musical notes");       // 0 — was "Click beep", maps to Beep enum
            TypingSoundCombo.Items.Add("Single tone");         // 1
            TypingSoundCombo.Items.Add("Random tones");        // 2

            // Unlockable modes slot in between the always-on audio modes and "Off".
            bool mechUnlocked = FreqOutHandlers.IsCalibrationUnlocked(CalibrationEngine.Ref2, _audioConfig.TuningHash);
            bool dtmfUnlocked = FreqOutHandlers.IsCalibrationUnlocked(CalibrationEngine.Ref1, _audioConfig.TuningHash);

            int mechIdx = -1, dtmfIdx = -1;
            if (mechUnlocked) { mechIdx = TypingSoundCombo.Items.Count; TypingSoundCombo.Items.Add("Mechanical keyboard"); }
            if (dtmfUnlocked) { dtmfIdx = TypingSoundCombo.Items.Count; TypingSoundCombo.Items.Add("Touch-tone (DTMF)"); }

            // "Off" is always last.
            int offIdx = TypingSoundCombo.Items.Count;
            TypingSoundCombo.Items.Add("Off");

            // Select current mode
            int idx = _audioConfig.TypingSound switch
            {
                TypingSoundMode.Beep => 0,
                TypingSoundMode.SingleTone => 1,
                TypingSoundMode.RandomTones => 2,
                TypingSoundMode.Off => offIdx,
                TypingSoundMode.Mechanical when mechIdx >= 0 => mechIdx,
                TypingSoundMode.TouchTone when dtmfIdx >= 0 => dtmfIdx,
                _ => 0
            };
            TypingSoundCombo.SelectedIndex = idx;
        }

        private bool SaveSettings()
        {
            // PTT tab — parse and validate
            if (!int.TryParse(PttTimeoutBox.Text, out int timeout) || timeout < 10 || timeout > 900)
            {
                MessageBox.Show("Timeout must be between 10 and 900 seconds.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                PttTimeoutBox.Focus();
                return false;
            }

            if (!int.TryParse(PttWarning1Box.Text, out int w1))
            {
                MessageBox.Show("First warning must be a number.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                PttWarning1Box.Focus();
                return false;
            }

            if (!int.TryParse(PttWarning2Box.Text, out int w2))
            {
                MessageBox.Show("Second warning must be a number.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                PttWarning2Box.Focus();
                return false;
            }

            if (!int.TryParse(PttOhCrapBox.Text, out int ohCrap))
            {
                MessageBox.Show("Final warning must be a number.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                PttOhCrapBox.Focus();
                return false;
            }

            if (!int.TryParse(PttAlcBox.Text, out int alc) || alc < 0 || (alc > 0 && alc < 10) || alc > 300)
            {
                MessageBox.Show("ALC auto-release must be 0 (disabled) or between 10 and 300 seconds.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                PttAlcBox.Focus();
                return false;
            }

            // Write back to PttConfig (Validate() will clamp)
            _pttConfig.TimeoutSeconds = timeout;
            _pttConfig.Warning1SecondsBeforeTimeout = w1;
            _pttConfig.Warning2SecondsBeforeTimeout = w2;
            _pttConfig.OhCrapSecondsBeforeTimeout = ohCrap;
            _pttConfig.AlcAutoReleaseSeconds = alc;
            _pttConfig.SpeechEnabled = PttSpeechCheckbox.IsChecked == true;
            _pttConfig.ChirpEnabled = ChirpEnabledCheckbox.IsChecked == true;
            _pttConfig.Validate();

            // Tuning tab
            if (CoarseStepCombo.SelectedIndex >= 0 && CoarseStepCombo.SelectedIndex < CoarseStepOptions.Length)
                CoarseTuneStep = CoarseStepOptions[CoarseStepCombo.SelectedIndex].hz;
            if (FineStepCombo.SelectedIndex >= 0 && FineStepCombo.SelectedIndex < FineStepOptions.Length)
                FineTuneStep = FineStepOptions[FineStepCombo.SelectedIndex].hz;
            BandMemoryEnabled = BandMemoryCheckbox.IsChecked == true;
            _pttConfig.BandMemoryEnabled = BandMemoryEnabled;
            if (FreqUnitsCombo.SelectedIndex >= 0)
                _pttConfig.FrequencyDisplayUnits = (Radios.FrequencyUnits)FreqUnitsCombo.SelectedIndex;

            // Tuning debounce
            _audioConfig.TuneDebounceEnabled = TuneDebounceCheckbox.IsChecked == true;
            if (int.TryParse(DebounceDelayBox.Text, out int debounceMs))
                _audioConfig.TuneDebounceMs = Math.Clamp(debounceMs, 50, 1000);
            else
                _audioConfig.TuneDebounceMs = 300;

            // License tab — write back to LicenseConfig
            int selIdx = LicenseClassCombo.SelectedIndex;
            if (selIdx >= 0 && selIdx < LicenseClassMap.Length)
                _licenseConfig.LicenseClass = LicenseClassMap[selIdx].value;
            _licenseConfig.BoundaryNotifications = BandBoundaryCheckbox.IsChecked == true;
            _licenseConfig.TxLockout = TxLockoutCheckbox.IsChecked == true;
            int cIdx = CountryCombo.SelectedIndex;
            _licenseConfig.Country = cIdx >= 0 && cIdx < _countryMap.Length ? _countryMap[cIdx].Item1 : "US";
            _licenseConfig.EnforceTxRules = EnforceTxRulesCheckbox.IsChecked == true;

            // Audio tab — master volume
            _audioConfig.MasterVolume = MasterVolumeControl.Value / 100f;

            // Alert section
            _audioConfig.AlertVolume = EarconVolumeControl.Value / 100f;
            _audioConfig.MasterEarconVolume = EarconVolumeControl.Value; // backward compat
            var devices = EarconPlayer.GetOutputDevices();
            int devIdx = EarconDeviceCombo.SelectedIndex;
            if (devIdx >= 0 && devIdx < devices.Count)
                _audioConfig.EarconDeviceNumber = devices[devIdx].deviceNumber;

            // Meter section
            _audioConfig.MeterMasterVolume = MeterVolumeControl.Value / 100f;
            int meterDevSel = MeterDeviceCombo.SelectedIndex;
            if (meterDevSel <= 0)
            {
                _audioConfig.MeterDeviceNumber = -1; // Same as Alerts
            }
            else
            {
                // Offset by 1 for the "Same as Alerts" entry
                int devListIdx = meterDevSel - 1;
                if (devListIdx >= 0 && devListIdx < devices.Count)
                    _audioConfig.MeterDeviceNumber = devices[devListIdx].deviceNumber;
            }
            int presetIdx = MeterPresetCombo.SelectedIndex;
            if (presetIdx >= 0 && presetIdx < MeterPresetOptions.Length)
                _audioConfig.MeterPreset = MeterPresetOptions[presetIdx];
            _audioConfig.PeakWatcherEnabled = PeakWatcherCheck.IsChecked == true;
            _audioConfig.MeterSpeechEnabled = MeterSpeechCheck.IsChecked == true;

            // Typing sound mode — map combo index back to enum. Order mirrors
            // PopulateTypingSoundCombo exactly:
            //   0-2: Musical notes, Single tone, Random tones
            //   3+:  Mechanical (if unlocked), then DTMF (if unlocked)
            //   last: Off (always pinned to the end)
            bool mechUnlocked = FreqOutHandlers.IsCalibrationUnlocked(CalibrationEngine.Ref2, _audioConfig.TuningHash);
            bool dtmfUnlocked = FreqOutHandlers.IsCalibrationUnlocked(CalibrationEngine.Ref1, _audioConfig.TuningHash);
            int tsIdx = TypingSoundCombo.SelectedIndex;
            int mechIdx = mechUnlocked ? 3 : -1;
            int dtmfIdx = dtmfUnlocked ? (mechUnlocked ? 4 : 3) : -1;
            int offIdx = 3 + (mechUnlocked ? 1 : 0) + (dtmfUnlocked ? 1 : 0);
            _audioConfig.TypingSound = tsIdx switch
            {
                0 => TypingSoundMode.Beep,
                1 => TypingSoundMode.SingleTone,
                2 => TypingSoundMode.RandomTones,
                _ when tsIdx == offIdx => TypingSoundMode.Off,
                _ when tsIdx == mechIdx => TypingSoundMode.Mechanical,
                _ when tsIdx == dtmfIdx => TypingSoundMode.TouchTone,
                _ => TypingSoundMode.Beep
            };

            // Braille section
            _audioConfig.BrailleEnabled = BrailleEnabledCheck.IsChecked == true;
            int[] cellOpts = { 20, 32, 40, 80 };
            int bcIdx = BrailleCellsCombo.SelectedIndex;
            _audioConfig.BrailleCellCount = bcIdx >= 0 && bcIdx < cellOpts.Length ? cellOpts[bcIdx] : 40;

            // Verbosity & Notifications tab
            _audioConfig.SpeechVerbosity = SpeechVerbosityCombo.SelectedIndex;
            _audioConfig.EarconsEnabled = EarconsEnabledCheck.IsChecked == true;
            _audioConfig.CwNotificationsEnabled = CwNotificationsCheck.IsChecked == true;
            if (int.TryParse(CwSidetoneBox.Text, out int sidetone) && sidetone >= 400 && sidetone <= 1200)
                _audioConfig.CwSidetoneHz = sidetone;
            // Sprint 26 Phase 6: soft cap raised from 30 to 60 WPM for CW experts.
            if (int.TryParse(CwSpeedBox.Text, out int cwSpeed) && cwSpeed >= 10 && cwSpeed <= 60)
                _audioConfig.CwSpeedWpm = cwSpeed;
            _audioConfig.CwModeAnnounce = CwModeAnnounceCheck.IsChecked == true;

            // Sync the meter tones checkbox on Notifications tab with Audio tab
            _audioConfig.MeterTonesEnabled = MeterTonesNotifCheck.IsChecked == true;

            _audioConfig.ShowPanadapter = ShowPanadapterCheck.IsChecked == true;
            _audioConfig.AnnounceSwrAfterTune = AnnounceSwrAfterTuneCheck.IsChecked == true;

            // Apply audio settings immediately
            _audioConfig.Apply();

            return true;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (SaveSettings())
            {
                DialogResult = true;
                Close();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TuneDebounceCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            DebounceDelayPanel.IsEnabled = TuneDebounceCheckbox.IsChecked == true;
        }

        private void AudioWorkshopButton_Click(object sender, RoutedEventArgs e)
        {
            var workshop = new AudioWorkshopDialog();
            workshop.Owner = this;
            workshop.ShowDialog();
        }

        /// <summary>Optional reference to FreqOutHandlers for tuning step editing.</summary>
        public FreqOutHandlers? FreqHandlers { get; set; }

        private void EditTuningStepsButton_Click(object sender, RoutedEventArgs e)
        {
            if (FreqHandlers == null)
            {
                MessageBox.Show("Tuning steps require an active radio connection.",
                    "Not Available", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var editor = new TuningStepEditorDialog(
                FreqHandlers.GetCoarseSteps(),
                FreqHandlers.GetFineSteps());
            editor.Owner = this;
            if (editor.ShowDialog() == true && editor.Changed)
            {
                FreqHandlers.SetCoarseSteps(editor.CoarseSteps);
                FreqHandlers.SetFineSteps(editor.FineSteps);
                FreqHandlers.SaveStepSizes?.Invoke(FreqHandlers.CoarseTuneStep, FreqHandlers.FineTuneStep);
                Radios.ScreenReaderOutput.Speak("Tuning steps saved", true);
            }
        }

        private void EditFilterPresetsButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ConfigDirectory) || string.IsNullOrEmpty(OperatorName))
            {
                MessageBox.Show("Filter presets require an active operator profile.",
                    "Not Available", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var presets = Radios.FilterPresets.Load(ConfigDirectory, OperatorName);
            var editor = new FilterPresetEditorDialog(presets);
            editor.Owner = this;
            if (editor.ShowDialog() == true && editor.Changed)
            {
                presets.Save(ConfigDirectory, OperatorName);
                Radios.ScreenReaderOutput.Speak("Filter presets saved", true);
            }
        }
    }
}
