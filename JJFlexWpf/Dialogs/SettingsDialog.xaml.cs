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
            set { _rig = value; RefreshNetworkTabFromRig(); RefreshRadioSetupTab(); RefreshAudioTabFromRig(); }
        }

        /// <summary>
        /// Select a tab by its XAML header text ("Radio Setup", "Network", ...)
        /// before the dialog is shown, so advisories and other deep links can
        /// open Settings already sitting on the relevant tab instead of handing
        /// the user directions to it.
        /// </summary>
        public bool SelectTabByHeader(string header)
        {
            foreach (var item in SettingsTabs.Items)
            {
                if (item is TabItem tab
                    && string.Equals(tab.Header as string, header, StringComparison.OrdinalIgnoreCase))
                {
                    SettingsTabs.SelectedItem = tab;
                    return true;
                }
            }
            return false;
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

            // Track C: notice when the USER types in the Radio Setup name box
            // (programmatic refreshes never have keyboard focus inside it), so
            // OK/Apply can commit a typed-but-never-applied name instead of
            // discarding it.
            SetupRadioNameBox.TextChanged += (s, e) =>
            {
                if (SetupRadioNameBox.IsKeyboardFocusWithin) _setupNameEdited = true;
            };

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

            // Radio output levels — live-apply, see SettingsDialog.Audio.cs.
            InitializeAudioTab();

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

            // Mic-audio verdict output (Audio Arc Keys Track). Item order
            // matches MicVerdictOutputMode values.
            MicVerdictOutputCombo.Items.Add("Plain English plus decibels (default)"); // 0 Both
            MicVerdictOutputCombo.Items.Add("Plain English only");                    // 1 Plain
            MicVerdictOutputCombo.Items.Add("Decibels only");                         // 2 Numbers
            MicVerdictOutputCombo.SelectedIndex = Math.Clamp(_audioConfig.MicVerdictOutput, 0, 2);

            EarconsEnabledCheck.IsChecked = _audioConfig.EarconsEnabled;

            CwNotificationsCheck.IsChecked = _audioConfig.CwNotificationsEnabled;
            CwSidetoneBox.Text = _audioConfig.CwSidetoneHz.ToString();
            CwSpeedBox.Text = _audioConfig.CwSpeedWpm.ToString();
            CwModeAnnounceCheck.IsChecked = _audioConfig.CwModeAnnounce;

            MeterTonesNotifCheck.IsChecked = _audioConfig.MeterTonesEnabled;
            ShowPanadapterCheck.IsChecked = _audioConfig.ShowPanadapter;
            AnnounceSwrAfterTuneCheck.IsChecked = _audioConfig.AnnounceSwrAfterTune;
            SpeakConnectionProgressCheck.IsChecked = _audioConfig.SpeakConnectionProgress;

            // Network tab — defaults shown until Rig property is set (see RefreshNetworkTabFromRig)
            PortForwardEnabledCheck.IsChecked = false;
            PortForwardTcpBox.Text = "4992";
            PortForwardUdpBox.Text = "4992";
            PortForwardSeparatePortsCheck.IsChecked = false;
            PortForwardUdpBox.IsEnabled = false;
            PortForwardTcpLabel.Text = "Port (TCP and UDP):";
            NetworkCurrentStateText.Text = "No radio connected.";

            // Sprint 29 Track D — Updates tab.
            LoadUpdaterSettingsIntoUi();

            // Sprint 28 Phase 1 — Accessibility tab: set the double-tap tolerance radio
            // from the active config. Announcements on Checked are suppressed during this
            // initial load; the flag clears after this method returns.
            _suppressDoubleTapToleranceAnnouncements = true;
            try
            {
                switch (AccessibilityConfig.Current.DoubleTapTolerance)
                {
                    case DoubleTapTolerance.Quick:
                        DoubleTapQuickRadio.IsChecked = true;
                        break;
                    case DoubleTapTolerance.Relaxed:
                        DoubleTapRelaxedRadio.IsChecked = true;
                        break;
                    case DoubleTapTolerance.Leisurely:
                        DoubleTapLeisurelyRadio.IsChecked = true;
                        break;
                    default:
                        DoubleTapNormalRadio.IsChecked = true;
                        break;
                }
            }
            finally
            {
                _suppressDoubleTapToleranceAnnouncements = false;
            }

            // Audio tab — radio outputs and PC audio. The Rig setter refreshes
            // this again once it runs; calling it here means the advisory line
            // is never blank, even if Rig is never assigned.
            RefreshAudioTabFromRig();
        }

        private bool _suppressDoubleTapToleranceAnnouncements;

        /// <summary>
        /// Sprint 28 Phase 1 — shared handler for the four DoubleTapTolerance radio
        /// buttons' Checked events. Announces the selected tolerance via the screen
        /// reader for user-facing feedback; does not commit until the dialog's Save
        /// path runs (see SaveSettings).
        /// </summary>
        private void DoubleTapToleranceRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressDoubleTapToleranceAnnouncements) return;

            DoubleTapTolerance tolerance = GetSelectedDoubleTapTolerance();
            string name = tolerance switch
            {
                DoubleTapTolerance.Quick => "Quick",
                DoubleTapTolerance.Normal => "Normal",
                DoubleTapTolerance.Relaxed => "Relaxed",
                DoubleTapTolerance.Leisurely => "Leisurely",
                _ => tolerance.ToString()
            };
            int ms = (int)tolerance;
            ScreenReaderOutput.Speak(
                $"Double-tap tolerance, {name}, {ms} milliseconds",
                VerbosityLevel.Terse,
                interrupt: true);
        }

        /// <summary>
        /// Sprint 28 Phase 1 — read the DoubleTapTolerance radio group's current
        /// selection. Falls through to Normal if no button is checked (defensive
        /// default; shouldn't happen in a properly-initialized group).
        /// </summary>
        private DoubleTapTolerance GetSelectedDoubleTapTolerance()
        {
            if (DoubleTapQuickRadio?.IsChecked == true) return DoubleTapTolerance.Quick;
            if (DoubleTapRelaxedRadio?.IsChecked == true) return DoubleTapTolerance.Relaxed;
            if (DoubleTapLeisurelyRadio?.IsChecked == true) return DoubleTapTolerance.Leisurely;
            return DoubleTapTolerance.Normal;
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

            // Sprint 27 Track D — reflect the global Verbose preference regardless
            // of rig state. It's a viewing pref, not a radio / account pref.
            if (VerboseDiagnosticsCheck != null)
            {
                VerboseDiagnosticsCheck.IsChecked = Radios.SmartLink.DiagnosticVerbosityPreference.Verbose;
            }

            if (_rig == null || !_rig.IsConnected)
            {
                // QB Track C: the connect-first requirement is real for THIS tab —
                // port forwarding writes radio-persistent firmware state, so it
                // needs the radio. But it must not read as a dead end: per-radio
                // connection settings (including hole punch with no port-forward
                // config at all) live on the Radios tab and work offline.
                NetworkCurrentStateText.Text =
                    "No radio connected. Connect to a radio to configure port forwarding. " +
                    "Per-radio connection settings, including hole punch, are on the Radios tab " +
                    "and can be edited any time, connected or not.";
                PortForwardEnabledCheck.IsChecked = false;
                PortForwardTcpBox.Text = "4992";
                PortForwardUdpBox.Text = "4992";
                PortForwardSeparatePortsCheck.IsChecked = false;
                PortForwardUdpBox.IsEnabled = false;
                PortForwardTcpLabel.Text = "Port (TCP and UDP):";
                // Sprint 27 Track F — without a connected radio, force Tier 1
                // selection and disable Tier 2 / Tier 3. The radio group stays
                // visible + focusable so screen-reader users can explore the
                // choices; they just can't commit to 2/3 yet.
                SetCurrentConnectionMode(SmartLinkConnectionMode.ManualPortForwardOnly);
                if (Tier2Radio != null) Tier2Radio.IsEnabled = false;
                if (Tier3Radio != null) Tier3Radio.IsEnabled = false;
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
            // Track C wording fix: the radio advertises these external ports;
            // it listens on its LAN address at 4994/4993. Saying "listens on
            // your port" sent a live debugging session down the wrong road.
            NetworkCurrentStateText.Text = enabled
                ? (portsDiffer
                    ? $"The radio advertises external TCP port {tcp} and UDP port {udp} for SmartLink. "
                    : $"The radio advertises external port {tcp} (TCP and UDP) for SmartLink. ")
                  + DescribeRouterMapping(tcp, udp)
                : "Radio currently uses UPnP or hole-punch (no manual forwarding).";

            // Sprint 27 Track F — load the account's saved ConnectionMode into
            // the 3-option radio group. Null → Tier 1 (safe default for accounts
            // not yet bound to this session).
            var mode = _rig.CurrentAccountConnectionMode ?? SmartLinkConnectionMode.ManualPortForwardOnly;
            SetCurrentConnectionMode(mode);
            RecomputeConnectionModeAvailability();
        }

        private bool _suppressConnectionModeAnnouncements;

        /// <summary>
        /// Sprint 27 Track F — programmatic setter for the radio group that
        /// suppresses the ConnectionModeRadio_Checked announcement. Used by
        /// RefreshNetworkTabFromRig (load-from-account) and by the fallback
        /// path in RecomputeConnectionModeAvailability.
        /// </summary>
        private void SetCurrentConnectionMode(SmartLinkConnectionMode mode)
        {
            if (Tier1Radio == null) return;
            _suppressConnectionModeAnnouncements = true;
            try
            {
                switch (mode)
                {
                    case SmartLinkConnectionMode.AutomaticHolePunch:
                        Tier3Radio.IsChecked = true;
                        break;
                    case SmartLinkConnectionMode.ManualPlusUpnp:
                        Tier2Radio.IsChecked = true;
                        break;
                    default:
                        Tier1Radio.IsChecked = true;
                        break;
                }
            }
            finally
            {
                _suppressConnectionModeAnnouncements = false;
            }
        }

        /// <summary>
        /// Sprint 27 Track F — reads the radio group's current selection.
        /// Falls through to Tier 1 when no button is checked (which
        /// shouldn't happen in a properly-initialized group, but the
        /// dialog can be interrogated before RefreshNetworkTabFromRig runs).
        /// </summary>
        private SmartLinkConnectionMode GetCurrentConnectionMode()
        {
            if (Tier3Radio?.IsChecked == true) return SmartLinkConnectionMode.AutomaticHolePunch;
            if (Tier2Radio?.IsChecked == true) return SmartLinkConnectionMode.ManualPlusUpnp;
            return SmartLinkConnectionMode.ManualPortForwardOnly;
        }

        /// <summary>
        /// Sprint 27 Track F — Tier 2 / Tier 3 are gated on a valid Tier 1
        /// port being entered. When gating kicks in, force the selection
        /// back to Tier 1 so the UI is never showing a selected-but-inert
        /// option.
        /// </summary>
        private void RecomputeConnectionModeAvailability()
        {
            if (Tier1Radio == null || Tier2Radio == null || Tier3Radio == null) return;

            bool tier1On = PortForwardEnabledCheck?.IsChecked == true;
            bool portValid = int.TryParse(PortForwardTcpBox?.Text, out int tcp)
                             && SmartLinkAccountManager.IsValidPort(tcp);
            bool higherTiersValid = tier1On && portValid;

            // Tier 1 is always available (the mode's whole point is "always works").
            Tier1Radio.IsEnabled = true;
            Tier2Radio.IsEnabled = higherTiersValid;
            Tier3Radio.IsEnabled = higherTiersValid;

            if (!higherTiersValid && GetCurrentConnectionMode() != SmartLinkConnectionMode.ManualPortForwardOnly)
            {
                SetCurrentConnectionMode(SmartLinkConnectionMode.ManualPortForwardOnly);
            }
        }

        /// <summary>
        /// Sprint 27 Track F — re-evaluate radio-button enablement when the
        /// user toggles Tier 1 port-forward on/off.
        /// </summary>
        private void PortForwardEnabledCheck_Click(object sender, RoutedEventArgs e)
        {
            RecomputeConnectionModeAvailability();
        }

        /// <summary>
        /// Sprint 27 Track F — shared handler for Tier1Radio / Tier2Radio /
        /// Tier3Radio Checked events. Announces the selected mode via the
        /// existing ScreenReaderOutput.Speak path. Suppressed during
        /// programmatic loads (see <see cref="_suppressConnectionModeAnnouncements"/>).
        /// </summary>
        private void ConnectionModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressConnectionModeAnnouncements) return;
            string announcement = GetCurrentConnectionMode() switch
            {
                SmartLinkConnectionMode.AutomaticHolePunch => "Tier 1 plus 2 plus 3, automatic hole-punch enabled.",
                SmartLinkConnectionMode.ManualPlusUpnp => "Tier 1 plus 2, UPnP enabled.",
                _ => "Tier 1, manual port forwarding only.",
            };
            ScreenReaderOutput.Speak(announcement, VerbosityLevel.Terse, interrupt: true);
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
            // Sprint 27 Track F — port validity feeds into the Tier 2 / Tier 3 gate.
            RecomputeConnectionModeAvailability();
        }

        /// <summary>
        /// The plain-language router recipe for the current forwarding config —
        /// the two rules a router actually needs. External ports come from what
        /// the user chose; the radio-side ports are the fixed SmartLink
        /// listeners (TCP 4994, UDP 4993 — see the constants on FlexBase).
        /// Shown wherever forwarding state is reported, because the old "the
        /// radio listens on your port" story misled a live debugging session.
        /// </summary>
        private string DescribeRouterMapping(int tcp, int udp)
        {
            string lanIp = _rig?.CurrentRadioIP?.ToString() ?? "the radio's LAN IP";
            return $"Router rules: forward external TCP port {tcp} to {lanIp} port {FlexBase.SmartLinkRadioTlsPort}, " +
                   $"and external UDP port {udp} to {lanIp} port {FlexBase.SmartLinkRadioUdpPort}.";
        }

        /// <summary>
        /// Track C: port-forward edits are committed by the dialog's OK and
        /// Apply — the per-feature "Apply to connected radio" button is gone,
        /// so an edited port can no longer be silently discarded by OK.
        /// Only acts when the UI actually differs from what the radio reports.
        /// The authority gate and the confirmation dialog are unchanged from
        /// the button era: authority catches "not allowed to do this"; the
        /// dialog catches "accidental commit". Returns false only on a
        /// validation error (focus moved to the offending field); a declined
        /// confirmation or denied authority is reported in
        /// <paramref name="queued"/> and does not block the rest of OK.
        /// </summary>
        private bool CommitPortForwardIfDirty(List<string> queued, List<string> applied)
        {
            if (PortForwardEnabledCheck == null) return true;
            bool wantEnabled = PortForwardEnabledCheck.IsChecked == true;

            if (_rig == null || !_rig.IsConnected)
            {
                // Disconnected, the fields hold placeholders, not radio state.
                // An enabled checkbox is the one signal of real intent — and it
                // must not evaporate silently.
                if (wantEnabled)
                    queued.Add("Port forwarding was not changed. It writes settings into the radio itself, " +
                        "so it needs a connected radio. Connect, then set it again on the Network tab.");
                return true;
            }

            int tcp = 0, udp = 0;
            if (wantEnabled)
            {
                if (!int.TryParse(PortForwardTcpBox.Text, out tcp) || tcp < 1024 || tcp > 65535)
                {
                    SelectTabByHeader("Network");
                    NetworkCurrentStateText.Text = "Invalid TCP port. Must be 1024 to 65535.";
                    ScreenReaderOutput.Speak("Invalid TCP port.", VerbosityLevel.Terse, interrupt: true);
                    PortForwardTcpBox.Focus();
                    return false;
                }
                if (!int.TryParse(PortForwardUdpBox.Text, out udp) || udp < 1024 || udp > 65535)
                {
                    SelectTabByHeader("Network");
                    NetworkCurrentStateText.Text = "Invalid UDP port. Must be 1024 to 65535.";
                    ScreenReaderOutput.Speak("Invalid UDP port.", VerbosityLevel.Terse, interrupt: true);
                    PortForwardUdpBox.Focus();
                    return false;
                }
            }

            bool curEnabled = _rig.PortForwardingEnabled;
            int curTcp = _rig.PortForwardingTcpPort;
            int curUdp = _rig.PortForwardingUdpPort;
            bool dirty = wantEnabled != curEnabled
                         || (wantEnabled && (tcp != curTcp || udp != curUdp));

            if (dirty)
            {
                // Authorization gate. SmartLink port changes modify radio-persistent
                // state that affects future connections by any client. Passes on
                // presence (primary operator at the rig) OR the owner-declared
                // per-radio remote waiver (Settings > Radios) — a remote-base owner
                // is never at their radio and must not be locked out of it.
                string? declineNote = null;
                _rig.RequirePortSettingsAuthority(
                    reason: "change SmartLink port settings",
                    onConfirmed: () =>
                    {
                        // Confirmation dialog with default focus on No for conservative safety.
                        var confirm = new ConfirmPortForwardApplyDialog(wantEnabled, tcp, udp);
                        confirm.Owner = this;
                        if (confirm.ShowDialog() != true)
                        {
                            declineNote = "Port forwarding was left unchanged — you answered No to its confirmation.";
                            return;
                        }
                        if (PerformPortForwardApply(wantEnabled, tcp, udp))
                            applied.Add(NetworkCurrentStateText.Text);
                        else
                            declineNote = "The port forwarding command failed. See the trace file for details.";
                    },
                    onDenied: () =>
                    {
                        declineNote = "Port settings were not changed: you must be the primary operator at " +
                            "the radio, or turn on allowing port changes from remote connections, on the " +
                            "Radios tab.";
                    });
                if (declineNote != null)
                {
                    NetworkCurrentStateText.Text = declineNote;
                    queued.Add(declineNote);
                }
            }

            // The connection-mode ladder is an account preference, not radio
            // state — it needs no authority gate, and before this it was only
            // saved as a side effect of the port-forward button, so a
            // tier-only change was discarded by OK. Same defect, same fix.
            if (_rig.HasCurrentSmartLinkAccount)
            {
                var uiMode = GetCurrentConnectionMode();
                if (_rig.CurrentAccountConnectionMode != uiMode
                    && _rig.SaveCurrentAccountConnectionMode(uiMode))
                {
                    applied.Add($"SmartLink connection mode saved for {_rig.CurrentSmartLinkAccountEmail}.");
                }
            }
            return true;
        }

        /// <summary>
        /// Sprint 28 Phase 7 — extracted Apply body so both the guarded entrypoint
        /// and any future Apply callers reuse the same commit logic. Returns
        /// true when the radio accepted the command.
        /// </summary>
        private bool PerformPortForwardApply(bool enabled, int tcp, int udp)
        {
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

                    // Sprint 27 Track F / Phase F.1 — persist ConnectionMode as
                    // selected in the 3-option radio group. If Tier 1 is being
                    // disabled in this Apply (port forwarding off), force mode
                    // to ManualPortForwardOnly regardless of radio selection
                    // because higher tiers are meaningless without a port.
                    var desiredMode = enabled
                        ? GetCurrentConnectionMode()
                        : SmartLinkConnectionMode.ManualPortForwardOnly;
                    _rig.SaveCurrentAccountConnectionMode(desiredMode);
                }

                // Track C wording fix: the radio does NOT listen on the applied
                // ports — they are the external ports it advertises. The router
                // recipe names the real radio-side ports (4994 TCP / 4993 UDP).
                string baseMessage = enabled
                    ? $"Applied. The radio now advertises external TCP port {tcp} and UDP port {udp} for SmartLink. "
                      + DescribeRouterMapping(tcp, udp)
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
            return ok;
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
            NetworkCurrentStateText.Text =
                $"Port {port} is valid. Remember the two router rules: forward external TCP port {port} to the " +
                $"radio's LAN IP at port {FlexBase.SmartLinkRadioTlsPort}, and external UDP port {port} to the " +
                $"radio's LAN IP at port {FlexBase.SmartLinkRadioUdpPort}.";
            ScreenReaderOutput.Speak($"Port {port} is valid.", VerbosityLevel.Terse, interrupt: true);
        }

        /// <summary>
        /// Sprint 27 Track C / Phase C.3 — run a SmartLink NetworkTest probe
        /// against the connected radio via the active session's runner. Full
        /// probe, not local validation (that's Te_st port above). Announces a
        /// one-line summary to the diagnostic live region and speaks it.
        /// Full report (with ToMarkdown + copy/save) lands in Track D.
        /// </summary>
        /// <summary>
        /// QB Track D (item 7) — the network test makes the radio probe its
        /// own ports from outside, and on a hole-punched session that probe
        /// is known to kill the live connection (Connected flipped false
        /// 5-60ms after TestConnectionResults in every 2026-08-05 field
        /// test; same reason the automatic post-connect probe is gated off
        /// on punched sessions). The user may still choose to run it — a
        /// silent gate would hide a working feature — but only after a
        /// confirmation that names the consequence. Returns true to proceed.
        /// Shared by the Network tab and Radio Setup step 6 buttons.
        /// </summary>
        private bool ConfirmNetworkTestOnPunchedSession()
        {
            if (_rig == null || !_rig.IsConnected || !_rig.IsWanConnection || !_rig.RadioRequiresHolePunch)
                return true; // not a punched session — nothing to warn about

            var confirm = new ConfirmActionDialog(
                "Test Network on a Hole-Punched Connection",
                "This radio is connected through a hole-punched link. The network test asks the radio to probe its own ports from the internet, and on a hole-punched link that probe is known to drop the live connection.",
                new[]
                {
                    "You may lose this connection and have to reconnect to the radio.",
                },
                question: "Run the test anyway?",
                yesLabel: "_Run the test",
                noLabel: "_Not now")
            {
                Owner = this,
            };
            if (confirm.ShowDialog() == true) return true;

            ScreenReaderOutput.Speak("Network test not run. The connection stays up.", VerbosityLevel.Terse, interrupt: true);
            return false;
        }

        private async void TestNetworkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null || !_rig.IsConnected)
            {
                NetworkDiagnosticResultText.Text = "No radio connected. Connect to a radio first.";
                ScreenReaderOutput.Speak("No radio connected.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (!ConfirmNetworkTestOnPunchedSession())
            {
                NetworkDiagnosticResultText.Text =
                    "Network test not run — it can drop a hole-punched connection. Run it after disconnecting, or from a port-forwarded connection.";
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

        /// <summary>
        /// Sprint 27 Track D / Phase D.3 — copy the last NetworkDiagnosticReport
        /// to the clipboard as markdown. Precondition: a probe has run at
        /// least once (Test network button or an auto-probe). Announces
        /// success / "no report available yet" via live region + speech.
        /// </summary>
        private void CopyNetworkReportButton_Click(object sender, RoutedEventArgs e)
        {
            var report = _rig?.MostRecentNetworkReport;
            if (report == null)
            {
                NetworkDiagnosticResultText.Text = "No network diagnostic report yet. Run 'Test network' first.";
                ScreenReaderOutput.Speak("No report to copy.", VerbosityLevel.Terse, interrupt: true);
                return;
            }
            try
            {
                System.Windows.Clipboard.SetText(report.ToMarkdown());
                NetworkDiagnosticResultText.Text = "Network diagnostic report copied to clipboard as markdown.";
                ScreenReaderOutput.Speak("Report copied.", VerbosityLevel.Terse, interrupt: true);
            }
            catch (Exception ex)
            {
                NetworkDiagnosticResultText.Text = $"Copy failed: {ex.Message}";
                ScreenReaderOutput.Speak("Copy failed.", VerbosityLevel.Terse, interrupt: true);
            }
        }

        /// <summary>
        /// Sprint 27 Track D / Phase D.3 — save the last NetworkDiagnosticReport
        /// to a file via SaveFileDialog. Default filename encodes the
        /// timestamp so successive saves don't overwrite each other silently.
        /// </summary>
        private void SaveNetworkReportButton_Click(object sender, RoutedEventArgs e)
        {
            var report = _rig?.MostRecentNetworkReport;
            if (report == null)
            {
                NetworkDiagnosticResultText.Text = "No network diagnostic report yet. Run 'Test network' first.";
                ScreenReaderOutput.Speak("No report to save.", VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"JJFlex-NetworkDiagnostic-{report.TimestampUtc:yyyy-MM-dd-HHmm}.md",
                DefaultExt = ".md",
                Filter = "Markdown (*.md)|*.md|Text file (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Save network diagnostic report",
            };
            bool? ok = dlg.ShowDialog(this);
            if (ok != true) return;

            try
            {
                System.IO.File.WriteAllText(dlg.FileName, report.ToMarkdown());
                NetworkDiagnosticResultText.Text = $"Report saved to {dlg.FileName}.";
                ScreenReaderOutput.Speak("Report saved.", VerbosityLevel.Terse, interrupt: true);
            }
            catch (Exception ex)
            {
                NetworkDiagnosticResultText.Text = $"Save failed: {ex.Message}";
                ScreenReaderOutput.Speak("Save failed.", VerbosityLevel.Terse, interrupt: true);
            }
        }

        /// <summary>
        /// Sprint 27 Track D / Phase D.3 — open the help doc that best
        /// matches the current (status, report, mode) state via the user's
        /// default markdown viewer (System.Diagnostics.Process.Start with
        /// UseShellExecute so Windows picks the handler). Falls back to
        /// diagnostics.md when the resolver returns null (e.g., session is
        /// Connected but the user wants to look things up anyway).
        /// </summary>
        private void NetworkHelpButton_Click(object sender, RoutedEventArgs e)
        {
            string fileName = _rig?.CurrentHelpDocFileName ?? "networking-diagnostics.md";
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "help", fileName);
            if (!System.IO.File.Exists(path))
            {
                NetworkDiagnosticResultText.Text = $"Help file not found at {path}.";
                ScreenReaderOutput.Speak("Help file not found.", VerbosityLevel.Terse, interrupt: true);
                return;
            }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
                NetworkDiagnosticResultText.Text = $"Opened {fileName}.";
                ScreenReaderOutput.Speak($"Opened help for {fileName}.", VerbosityLevel.Terse, interrupt: true);
            }
            catch (Exception ex)
            {
                NetworkDiagnosticResultText.Text = $"Could not open help: {ex.Message}";
                ScreenReaderOutput.Speak("Could not open help.", VerbosityLevel.Terse, interrupt: true);
            }
        }

        /// <summary>
        /// Sprint 27 Track D / Phase D.3 — update the process-wide verbose
        /// flag immediately on checkbox toggle (no Apply click required;
        /// verbose is a viewing preference, not a radio / account preference).
        /// </summary>
        private void VerboseDiagnosticsCheck_Click(object sender, RoutedEventArgs e)
        {
            bool newState = VerboseDiagnosticsCheck?.IsChecked == true;
            Radios.SmartLink.DiagnosticVerbosityPreference.Verbose = newState;
            ScreenReaderOutput.Speak(newState ? "Verbose diagnostics on." : "Verbose diagnostics off.",
                VerbosityLevel.Terse, interrupt: true);
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
                SelectTabByHeader("PTT");  // Track C papercut: focusing a field on an unselected tab fails silently
                PttTimeoutBox.Focus();
                return false;
            }

            if (!int.TryParse(PttWarning1Box.Text, out int w1))
            {
                MessageBox.Show("First warning must be a number.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                SelectTabByHeader("PTT");  // Track C papercut: focusing a field on an unselected tab fails silently
                PttWarning1Box.Focus();
                return false;
            }

            if (!int.TryParse(PttWarning2Box.Text, out int w2))
            {
                MessageBox.Show("Second warning must be a number.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                SelectTabByHeader("PTT");  // Track C papercut: focusing a field on an unselected tab fails silently
                PttWarning2Box.Focus();
                return false;
            }

            if (!int.TryParse(PttOhCrapBox.Text, out int ohCrap))
            {
                MessageBox.Show("Final warning must be a number.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                SelectTabByHeader("PTT");  // Track C papercut: focusing a field on an unselected tab fails silently
                PttOhCrapBox.Focus();
                return false;
            }

            if (!int.TryParse(PttAlcBox.Text, out int alc) || alc < 0 || (alc > 0 && alc < 10) || alc > 300)
            {
                MessageBox.Show("ALC auto-release must be 0 (disabled) or between 10 and 300 seconds.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                SelectTabByHeader("PTT");  // Track C papercut: focusing a field on an unselected tab fails silently
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
            _audioConfig.MicVerdictOutput = Math.Clamp(MicVerdictOutputCombo.SelectedIndex, 0, 2);
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
            _audioConfig.SpeakConnectionProgress = SpeakConnectionProgressCheck.IsChecked == true;

            // Apply audio settings immediately
            _audioConfig.Apply();

            // Sprint 28 Phase 1 — Accessibility tab: commit DoubleTapTolerance selection.
            // Save updates AccessibilityConfig.Current as a side effect, so any consumer
            // reading the static Current accessor sees the new value after this returns.
            AccessibilityConfig.Current.DoubleTapTolerance = GetSelectedDoubleTapTolerance();
            if (!string.IsNullOrEmpty(ConfigDirectory) && !string.IsNullOrEmpty(OperatorName))
            {
                AccessibilityConfig.Current.Save(ConfigDirectory, OperatorName);
            }

            // Sprint 29 Track D — Updates tab.
            SaveUpdaterSettingsFromUi();

            return true;
        }

        /// <summary>
        /// Wired by NativeMenuBar. Runs after every successful commit — OK and
        /// Apply alike — and owns the app-side application and persistence that
        /// used to happen only after ShowDialog returned true (tuning steps to
        /// the handler, configs to disk). Without this, Apply-and-stay would
        /// save nothing until the dialog closed, which is the exact
        /// silently-not-kept defect this convention exists to end.
        /// </summary>
        public Action? SettingsApplied { get; set; }

        /// <summary>
        /// The whole commit, shared by OK and Apply (Track C: OK applies and
        /// closes, Apply applies and stays, Cancel discards — the same pair on
        /// every settings screen). Validation-first: returns false with focus
        /// on the offending field and nothing half-committed.
        ///
        /// Queued intents get their own voice: anything that cannot take
        /// effect until a connection is reported plainly instead of implying
        /// it happened. On OK the report is an OK-only dialog — the window is
        /// about to close, and speech alone is ephemeral, never reaches
        /// braille, and can be cut off. On Apply the dialog stays open, so the
        /// tab status lines hold the re-readable detail and one summary is
        /// spoken.
        /// </summary>
        private bool ApplyAllSettings(bool closing)
        {
            if (!SaveSettings()) return false;

            var queued = new List<string>();   // saved, but waits for a connection (or was declined)
            var applied = new List<string>();  // done now

            if (!CommitRadioProfiles(queued, applied)) return false;
            CommitSetupRadioNameIfDirty(applied);
            if (!CommitPortForwardIfDirty(queued, applied)) return false;

            SettingsApplied?.Invoke();

            if (closing)
            {
                if (queued.Count > 0)
                {
                    var body = new System.Text.StringBuilder(
                        "Everything you set was saved. These items cannot take effect yet:");
                    foreach (var note in queued)
                    {
                        body.AppendLine();
                        body.AppendLine();
                        body.Append(note);
                    }
                    if (applied.Count > 0)
                    {
                        body.AppendLine();
                        body.AppendLine();
                        body.Append("Done right away: " + string.Join(" ", applied));
                    }
                    AdvisoryDialog.Show("Saved — Some Items Wait", body.ToString());
                }
                else
                {
                    ScreenReaderOutput.Speak("Settings saved.", VerbosityLevel.Terse, interrupt: true);
                }
            }
            else
            {
                string summary = "Settings applied.";
                if (queued.Count > 0)
                    summary += " Waiting: " + string.Join(" ", queued);
                else if (applied.Count > 0)
                    summary += " " + string.Join(" ", applied);
                ScreenReaderOutput.Speak(summary, VerbosityLevel.Terse, interrupt: true);
            }
            return true;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (ApplyAllSettings(closing: true))
            {
                DialogResult = true;
                Close();
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyAllSettings(closing: false);
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

        /// <summary>Optional reference to FreqOutHandlers for tuning step access.</summary>
        public FreqOutHandlers? FreqHandlers { get; set; }

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
