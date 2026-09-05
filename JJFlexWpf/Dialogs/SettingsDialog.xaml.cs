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
        /// Category-list navigation (Sprint 32 Track G, task #134). Owns the
        /// list contents, the two-way sync with SettingsTabs, and the
        /// Ctrl+Tab / Ctrl+Shift+Tab pair. Held so deep links can ask it to put
        /// focus on the category they just selected.
        /// </summary>
        private CategoryNavigator? _categories;

        /// <summary>
        /// Put focus on the selected category in the list, announcing where the
        /// operator has landed.
        /// </summary>
        /// <remarks>
        /// Deep links used to call <c>TabItem.Focus()</c> for this, which
        /// worked while the tab strip was a real focusable visual. The strip is
        /// templated away now, so that call would silently do nothing and the
        /// operator would arrive with no evidence they were anywhere but plain
        /// Settings. Every deep link routes through here instead.
        /// </remarks>
        public bool FocusCategory() => _categories?.FocusSelectedCategory() ?? false;

        /// <summary>
        /// Settings opens on the category list.
        /// </summary>
        /// <remarks>
        /// Naming the target rather than letting the base walk tab order, for
        /// the reason RemoveRadioDialog names its own: the walk lands on
        /// whatever happens to be first today. Here it landed on the OK button,
        /// which is a strange first thing to hear from a dialog holding eleven
        /// categories of settings. Landing on the list answers "where am I and
        /// what else is there" in one announcement.
        /// </remarks>
        protected override void FocusFirstControl()
        {
            if (FocusCategory()) return;
            base.FocusFirstControl();
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

        // The step options this dialog shows.
        //
        // THEY ARE NO LONGER THIS DIALOG'S OWN LIST (#302, 2026-08-27). They
        // used to be two private static arrays holding 1/2/5 kHz and
        // 5/10/100 Hz. The moment a key could size the step from the Home
        // surface, that private list became a second vocabulary with a real
        // bite: an operator who set 10 kHz with the arrows, then opened
        // Settings, would have seen a combo with no matching item — which
        // falls back to index 0 — and pressing OK would have silently reset
        // their step to 1 kHz. Same table as the ladder keys and the picker,
        // widened to hold the current value even if it came from somewhere
        // else entirely.
        private readonly IReadOnlyList<TuningSteps.Choice> _coarseStepOptions;
        private readonly IReadOnlyList<TuningSteps.Choice> _fineStepOptions;

        private static readonly (string label, HamBands.Bands.Licenses value)[] LicenseClassMap =
        {
            (Lexicon.Get("settings.license.class_extra"), HamBands.Bands.Licenses.extra),
            (Lexicon.Get("settings.license.class_advanced"), HamBands.Bands.Licenses.advanced),
            (Lexicon.Get("settings.license.class_general"), HamBands.Bands.Licenses.general),
            (Lexicon.Get("settings.license.class_technician"), HamBands.Bands.Licenses.technition)
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
            _coarseStepOptions = TuningSteps.ChoicesIncluding(TuningSteps.Coarse, currentCoarseStep);
            _fineStepOptions = TuningSteps.ChoicesIncluding(TuningSteps.Fine, currentFineStep);
            BandMemoryEnabled = pttConfig.BandMemoryEnabled;

            InitializeComponent();

            // The PC-audio status line explains the checkbox directly above it,
            // so it belongs to that checkbox rather than to the tab order
            // (#211). Ctrl+F1 on the checkbox now reads what it does AND what
            // it is doing right now, and the sentence no longer sits between
            // the operator and the next control.
            //
            // The two other read-only lines on this dialog — the Radio Outputs
            // advisory and the Radio Profile status — are deliberately still
            // tab stops. Both are SECTION signposts rather than notes about a
            // control: the Radio Outputs advisory exists to say why the panel
            // beneath it is empty, which is exactly when there is no control
            // to hang it on, and the profile status reports the outcome of the
            // whole tab. Text that is the only carrier of its information stays
            // reachable — the same rule DecorativeText states from the other
            // side.
            JJFlexHelp.SetNoteFor(PcAudioStatusText, PcAudioCheck);

            // Category navigation (task #134). Attached before anything else
            // touches SettingsTabs, so a tab selected later in this
            // constructor — or by SelectTabByHeader before the dialog is
            // shown — arrives with the list already tracking it.
            _categories = CategoryNavigator.Attach(this, SettingsTabs, CategoryListBox);

            // Select all text when tabbing into any TextBox
            AddHandler(TextBox.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(TextBox_GotKeyboardFocus));

            // F1 opens help AT the page for where you are, per tab (Sprint 30
            // Track E). Tunnel phase, so this wins over the app-global F1
            // route, which can only open the help file's front door. Ctrl+F1
            // (explain the focused control) is untouched — modifier check.
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.F1 &&
                    System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
                {
                    bool onDiagnostics =
                        SettingsTabs.SelectedItem is System.Windows.Controls.TabItem tab &&
                        (tab.Header as string) == "Diagnostics";
                    HelpLauncher.ShowHelp(onDiagnostics ? "DiagnosticLog" : "SettingsDialog");
                    e.Handled = true;
                }
            };

            // Track C: notice when the USER types in the Radio Setup name box
            // (programmatic refreshes never have keyboard focus inside it), so
            // OK/Apply can commit a typed-but-never-applied name instead of
            // discarding it.
            SetupRadioNameBox.TextChanged += (s, e) =>
            {
                if (SetupRadioNameBox.IsKeyboardFocusWithin) _setupNameEdited = true;
            };

            // Configure volume controls
            MasterVolumeControl.Label = Lexicon.Get("settings.audio.master_volume_label");
            MasterVolumeControl.Min = 0;
            MasterVolumeControl.Max = 100;
            MasterVolumeControl.Step = 5;

            EarconVolumeControl.Label = Lexicon.Get("settings.audio.alert_volume_label");
            EarconVolumeControl.Min = 0;
            EarconVolumeControl.Max = 100;
            EarconVolumeControl.Step = 5;

            MeterVolumeControl.Label = Lexicon.Get("settings.audio.meter_volume_label");
            MeterVolumeControl.Min = 0;
            MeterVolumeControl.Max = 100;
            MeterVolumeControl.Step = 5;

            // Radio output levels — live-apply, see SettingsDialog.Audio.cs.
            InitializeAudioTab();

            // The warning duck and per-sound trims (#116, #384) — live-apply
            // for the same reason the radio output levels are.
            InitializeSoundAdjustments();

            // The reflected cut's full explanation is Ctrl+F1, on demand —
            // NOT AutomationProperties.HelpText, which NVDA recites as the
            // control's description on every focus (#91).
            JJFlexHelp.SetText(ReflectedCutCheckbox,
                Lexicon.Get("settings.ptt.reflected_cut_help"));

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

            // Suppressed like every other programmatic load in this dialog: an
            // operator who saved the cut OFF has already heard the warning and
            // made the choice — re-lecturing them on every open of Settings
            // would teach them to stop hearing it.
            _suppressReflectedCutWarning = true;
            try { ReflectedCutCheckbox.IsChecked = _pttConfig.CutTransmitOnReflectedAlarm; }
            finally { _suppressReflectedCutWarning = false; }

            // Tuning tab
            foreach (var choice in _coarseStepOptions)
            {
                CoarseStepCombo.Items.Add(TuningSteps.LabelFor(choice));
                if (choice.Hz == CoarseTuneStep)
                    CoarseStepCombo.SelectedIndex = CoarseStepCombo.Items.Count - 1;
            }
            if (CoarseStepCombo.SelectedIndex < 0) CoarseStepCombo.SelectedIndex = 0;

            foreach (var choice in _fineStepOptions)
            {
                FineStepCombo.Items.Add(TuningSteps.LabelFor(choice));
                if (choice.Hz == FineTuneStep)
                    FineStepCombo.SelectedIndex = FineStepCombo.Items.Count - 1;
            }
            if (FineStepCombo.SelectedIndex < 0) FineStepCombo.SelectedIndex = 0;

            BandMemoryCheckbox.IsChecked = BandMemoryEnabled;

            // Tuning debounce
            TuneDebounceCheckbox.IsChecked = _audioConfig.TuneDebounceEnabled;
            DebounceDelayBox.Text = _audioConfig.TuneDebounceMs.ToString();
            DebounceDelayPanel.IsEnabled = _audioConfig.TuneDebounceEnabled;

            // Frequency units combo
            FreqUnitsCombo.Items.Add(Lexicon.Get("settings.tuning.frequency_units_dotted"));
            FreqUnitsCombo.Items.Add(Lexicon.Get("settings.tuning.frequency_units_kilohertz"));
            FreqUnitsCombo.Items.Add(Lexicon.Get("settings.tuning.frequency_units_megahertz"));
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
            _countryMap = new[] { ("US", Lexicon.Get("settings.license.country_united_states")) };
            // Future: add ("UK", "United Kingdom"), etc.
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
            MeterDeviceCombo.Items.Add(Lexicon.Get("settings.audio.meter_device_same_as_alerts"));
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
            SpeechVerbosityCombo.Items.Add(Lexicon.Get("settings.speech.verbosity_off"));     // 0
            SpeechVerbosityCombo.Items.Add(Lexicon.Get("settings.speech.verbosity_terse"));   // 1
            SpeechVerbosityCombo.Items.Add(Lexicon.Get("settings.speech.verbosity_chatty"));  // 2
            SpeechVerbosityCombo.SelectedIndex = Math.Clamp(_audioConfig.SpeechVerbosity, 0, 2);

            // Mic-audio verdict output (Audio Arc Keys Track). Item order
            // matches MicVerdictOutputMode values.
            MicVerdictOutputCombo.Items.Add(Lexicon.Get("settings.speech.mic_verdict_both"));    // 0 Both
            MicVerdictOutputCombo.Items.Add(Lexicon.Get("settings.speech.mic_verdict_plain"));   // 1 Plain
            MicVerdictOutputCombo.Items.Add(Lexicon.Get("settings.speech.mic_verdict_numbers")); // 2 Numbers
            MicVerdictOutputCombo.SelectedIndex = Math.Clamp(_audioConfig.MicVerdictOutput, 0, 2);

            EarconsEnabledCheck.IsChecked = _audioConfig.EarconsEnabled;
            EarconConnectionCheck.IsChecked = _audioConfig.EarconConnectionEnabled;
            EarconTransmitCheck.IsChecked = _audioConfig.EarconTransmitEnabled;
            EarconDialogsCheck.IsChecked = _audioConfig.EarconDialogsEnabled;
            EarconTuningCheck.IsChecked = _audioConfig.EarconTuningEnabled;
            EarconCommandsCheck.IsChecked = _audioConfig.EarconCommandsEnabled;
            EarconWarningsCheck.IsChecked = _audioConfig.EarconWarningsEnabled;

            // Sprint 33 Track F — the three "how the app sounds" pickers. All
            // three preview on selection change, so an operator arrowing
            // through hears each option rather than reading its name. That is
            // also why the previews are suppressed while this method runs:
            // populating a combo raises SelectionChanged, and a Settings dialog
            // that plays three sounds on the way open would be a bug.
            _suppressSoundPreviews = true;
            try
            {
                // #147 — alert tone set. Item order matches EarconVoiceSet.
                foreach (string label in EarconVoices.SetLabels)
                    AlertToneSetCombo.Items.Add(label);
                AlertToneSetCombo.SelectedIndex =
                    Math.Clamp(_audioConfig.EarconVoiceSet, 0, EarconVoices.SetLabels.Count - 1);

                // #146 — pitch source. Index 0 is the configured tone, which is
                // both the default and the behaviour every existing config has.
                CwPitchSourceCombo.Items.Add(Lexicon.Get("settings.sound.cw_pitch_from_setting"));
                CwPitchSourceCombo.Items.Add(Lexicon.Get("settings.sound.cw_pitch_follows_radio"));
                CwPitchSourceCombo.SelectedIndex = _audioConfig.CwPitchFollowsRadio ? 1 : 0;

                // #145 — keying tone shape.
                foreach (var w in EarconVoices.CwWaveforms)
                    CwWaveformCombo.Items.Add(w.Label);
                int waveIdx = 0;
                for (int i = 0; i < EarconVoices.CwWaveforms.Count; i++)
                {
                    if (string.Equals(EarconVoices.CwWaveforms[i].Id, _audioConfig.CwWaveform,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        waveIdx = i;
                        break;
                    }
                }
                CwWaveformCombo.SelectedIndex = waveIdx;

                CwNotificationsCheck.IsChecked = _audioConfig.CwNotificationsEnabled;
                CwSidetoneBox.Text = _audioConfig.CwSidetoneHz.ToString();
                CwSpeedBox.Text = _audioConfig.CwSpeedWpm.ToString();
                CwModeAnnounceCheck.IsChecked = _audioConfig.CwModeAnnounce;
            }
            finally
            {
                _suppressSoundPreviews = false;
            }

            MeterTonesNotifCheck.IsChecked = _audioConfig.MeterTonesEnabled;
            ShowPanadapterCheck.IsChecked = _audioConfig.ShowPanadapter;
            AnnounceSwrAfterTuneCheck.IsChecked = _audioConfig.AnnounceSwrAfterTune;
            SpeakConnectionProgressCheck.IsChecked = _audioConfig.SpeakConnectionProgress;
            OfferStationSaveOnDisconnectCheck.IsChecked = _audioConfig.OfferStationSaveOnDisconnect;

            // Network tab — defaults shown until Rig property is set (see RefreshNetworkTabFromRig)
            PortForwardEnabledCheck.IsChecked = false;
            PortForwardTcpBox.Text = "4992";
            PortForwardUdpBox.Text = "4992";
            PortForwardSeparatePortsCheck.IsChecked = false;
            PortForwardUdpBox.IsEnabled = false;
            PortForwardTcpLabel.Text = Lexicon.Get("settings.network.port_label_shared");
            NetworkCurrentStateText.Text = Lexicon.Get("settings.no_radio_connected");

            // Task #102 — path learning (Network tab, bottom). Commits live
            // rather than on OK/Apply; see SettingsDialog.PathLearning.cs.
            LoadPathLearningSettings();

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

                // Sprint 43 Track E (#318) — slice arrow direction, same tab,
                // same suppression window.
                if (AccessibilityConfig.Current.SliceArrowOrder == SliceArrowOrder.BottomToTop)
                    SliceOrderBottomToTopRadio.IsChecked = true;
                else
                    SliceOrderTopToBottomRadio.IsChecked = true;
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

            // DELETED 2026-08-18: each RadioButton's AutomationProperties.Name
            // already carries the name AND the milliseconds - "Quick, 250
            // milliseconds, for fast typists" - and the screen reader announces
            // it on selection. This utterance was a strict SUBSET of that, and
            // because the handler fires on every arrow press through the group,
            // the interrupt cut the fuller announcement to deliver the shorter
            // one. Nothing is lost by standing down.
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
        /// Sprint 43 Track E (#318) — shared handler for the slice arrow
        /// direction pair. Silent for the same reason
        /// <see cref="DoubleTapToleranceRadio_Checked"/> is: each button's
        /// AutomationProperties.Name already says what it does, the reader
        /// announces it on selection, and speaking here would interrupt that
        /// fuller sentence to deliver a shorter one on every arrow press
        /// through the group.
        /// </summary>
        private void SliceArrowOrderRadio_Checked(object sender, RoutedEventArgs e)
        {
        }

        /// <summary>
        /// Read the slice arrow direction group. Falls through to the reading
        /// order default if neither button is checked.
        /// </summary>
        private SliceArrowOrder GetSelectedSliceArrowOrder()
            => SliceOrderBottomToTopRadio?.IsChecked == true
                ? SliceArrowOrder.BottomToTop
                : SliceArrowOrder.TopToBottom;

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
                    Lexicon.Get("settings.network.no_radio_configure_forwarding");
                PortForwardEnabledCheck.IsChecked = false;
                PortForwardTcpBox.Text = "4992";
                PortForwardUdpBox.Text = "4992";
                PortForwardSeparatePortsCheck.IsChecked = false;
                PortForwardUdpBox.IsEnabled = false;
                PortForwardTcpLabel.Text = Lexicon.Get("settings.network.port_label_shared");
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
            PortForwardTcpLabel.Text = portsDiffer ? Lexicon.Get("settings.network.port_label_tcp_only") : Lexicon.Get("settings.network.port_label_shared");
            // Track C wording fix: the radio advertises these external ports;
            // it listens on its LAN address at 4994/4993. Saying "listens on
            // your port" sent a live debugging session down the wrong road.
            NetworkCurrentStateText.Text = enabled
                ? (portsDiffer
                    ? Lexicon.Get("settings.network.advertises_separate_ports",
                        ("tcp", tcp), ("udp", udp))
                    : Lexicon.Get("settings.network.advertises_one_port", ("tcp", tcp)))
                  + DescribeRouterMapping(tcp, udp)
                : Lexicon.Get("settings.network.uses_upnp_or_hole_punch");

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
            // DELETED: `announcement` is a near-verbatim copy of the selected
            // RadioButton's own AutomationProperties.Name, which the screen
            // reader announces on selection. Because this fires on every arrow
            // press through the group, the interrupt cut that announcement
            // mid-word and then restated it. Surveyed 2026-08-18.
            _ = announcement;
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
            PortForwardTcpLabel.Text = separate ? Lexicon.Get("settings.network.port_label_tcp_only") : Lexicon.Get("settings.network.port_label_shared");
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
            string lanIp = _rig?.CurrentRadioIP?.ToString()
                ?? Lexicon.Get("settings.network.radio_lan_ip_unknown");
            return Lexicon.Get("settings.network.router_rules",
                ("tcp", tcp), ("udp", udp), ("lanIp", lanIp),
                ("tlsPort", FlexBase.SmartLinkRadioTlsPort),
                ("udpPort", FlexBase.SmartLinkRadioUdpPort));
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
                    queued.Add(Lexicon.Get("settings.network.forwarding_needs_a_radio"));
                return true;
            }

            int tcp = 0, udp = 0;
            if (wantEnabled)
            {
                if (!int.TryParse(PortForwardTcpBox.Text, out tcp) || tcp < 1024 || tcp > 65535)
                {
                    SelectTabByHeader("Network");
                    NetworkCurrentStateText.Text = Lexicon.Get("settings.network.tcp_port_invalid");
                    ScreenReaderOutput.Speak(Lexicon.Get("settings.network.tcp_port_invalid_spoken"),
                        VerbosityLevel.Terse, interrupt: true);
                    PortForwardTcpBox.Focus();
                    return false;
                }
                if (!int.TryParse(PortForwardUdpBox.Text, out udp) || udp < 1024 || udp > 65535)
                {
                    SelectTabByHeader("Network");
                    NetworkCurrentStateText.Text = Lexicon.Get("settings.network.udp_port_invalid");
                    ScreenReaderOutput.Speak(Lexicon.Get("settings.network.udp_port_invalid_spoken"),
                        VerbosityLevel.Terse, interrupt: true);
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
                    reason: Lexicon.Get("settings.network.authority_reason"),
                    onConfirmed: () =>
                    {
                        // Confirmation dialog with default focus on No for conservative safety.
                        var confirm = new ConfirmPortForwardApplyDialog(wantEnabled, tcp, udp);
                        confirm.Owner = this;
                        if (confirm.ShowDialog() != true)
                        {
                            declineNote = Lexicon.Get("settings.network.forwarding_declined");
                            return;
                        }
                        if (PerformPortForwardApply(wantEnabled, tcp, udp))
                            applied.Add(NetworkCurrentStateText.Text);
                        else
                            declineNote = Lexicon.Get("settings.network.forwarding_command_failed");
                    },
                    onDenied: () =>
                    {
                        declineNote = Lexicon.Get("settings.network.authority_denied");
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
                    applied.Add(Lexicon.Get("settings.network.connection_mode_saved",
                        ("email", _rig.CurrentSmartLinkAccountEmail)));
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
                    ? Lexicon.Get("settings.network.forwarding_applied", ("tcp", tcp), ("udp", udp))
                      + DescribeRouterMapping(tcp, udp)
                    : Lexicon.Get("settings.network.forwarding_disabled");
                string prefSuffix = savedPreference
                    ? (enabled
                        ? Lexicon.Get("settings.network.preference_saved",
                            ("email", _rig.CurrentSmartLinkAccountEmail))
                        : Lexicon.Get("settings.network.preference_cleared",
                            ("email", _rig.CurrentSmartLinkAccountEmail)))
                    : string.Empty;
                NetworkCurrentStateText.Text = baseMessage + prefSuffix;
                ScreenReaderOutput.Speak(enabled
                    ? Lexicon.Get("settings.network.forwarding_set_spoken", ("tcp", tcp))
                    : Lexicon.Get("settings.network.forwarding_disabled_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
            }
            else if (_rig.ChangeNothingActive)
            {
                // Not a failure — the operator's own hold refused it, and the
                // rig has already spoken the refusal by name. The status line
                // carries the same sentence so it can be re-read; the generic
                // "command failed" would be a lie with a worse diagnosis (#403).
                NetworkCurrentStateText.Text = Lexicon.Get("settings.guard.refused",
                    ("action", Lexicon.Get("settings.guard.action.ports")));
            }
            else
            {
                NetworkCurrentStateText.Text = Lexicon.Get("settings.network.command_failed");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.network.command_failed_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
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
                NetworkCurrentStateText.Text = Lexicon.Get("settings.network.port_not_a_number");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.network.port_not_a_number_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
                PortForwardTcpBox.Focus();
                return;
            }
            if (!SmartLinkAccountManager.IsValidPort(port))
            {
                NetworkCurrentStateText.Text =
                    Lexicon.Get("settings.network.port_out_of_range", ("port", port));
                ScreenReaderOutput.Speak(
                    Lexicon.Get("settings.network.port_out_of_range_spoken", ("port", port)),
                    VerbosityLevel.Terse, interrupt: true);
                PortForwardTcpBox.Focus();
                return;
            }
            // Common-conflict blocklist — ports above 1024 where users are likely
            // to already run unrelated services. Warn, don't block; the port is
            // still technically valid.
            string? conflictHint = port switch
            {
                3389 => Lexicon.Get("settings.network.port_conflict_remote_desktop"),
                5900 => Lexicon.Get("settings.network.port_conflict_vnc"),
                8080 => Lexicon.Get("settings.network.port_conflict_web"),
                _ => null,
            };
            if (conflictHint != null)
            {
                NetworkCurrentStateText.Text = Lexicon.Get("settings.network.port_valid_but_common",
                    ("port", port), ("conflictHint", conflictHint));
                ScreenReaderOutput.Speak(
                    Lexicon.Get("settings.network.port_valid_but_common_spoken",
                        ("port", port), ("conflictHint", conflictHint)),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }
            NetworkCurrentStateText.Text = Lexicon.Get("settings.network.port_valid",
                ("port", port),
                ("tlsPort", FlexBase.SmartLinkRadioTlsPort),
                ("udpPort", FlexBase.SmartLinkRadioUdpPort));
            ScreenReaderOutput.Speak(Lexicon.Get("settings.network.port_valid_spoken", ("port", port)),
                VerbosityLevel.Terse, interrupt: true);
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
                Lexicon.Get("settings.network.punched_test_title"),
                Lexicon.Get("settings.network.punched_test_body"),
                new[]
                {
                    Lexicon.Get("settings.network.punched_test_warning"),
                },
                question: Lexicon.Get("settings.network.punched_test_question"),
                yesLabel: Lexicon.Get("settings.network.punched_test_yes"),
                noLabel: Lexicon.Get("settings.network.punched_test_no"))
            {
                Owner = this,
            };
            if (confirm.ShowDialog() == true) return true;

            ScreenReaderOutput.Speak(Lexicon.Get("settings.network.punched_test_declined_spoken"),
                VerbosityLevel.Terse, interrupt: true);
            return false;
        }

        private async void TestNetworkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_rig == null || !_rig.IsConnected)
            {
                NetworkDiagnosticResultText.Text = Lexicon.Get("settings.network.test_needs_a_radio");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.no_radio_connected"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (!ConfirmNetworkTestOnPunchedSession())
            {
                NetworkDiagnosticResultText.Text =
                    Lexicon.Get("settings.network.test_punched_declined");
                return;
            }

            NetworkDiagnosticResultText.Text = Lexicon.Get("settings.network.probing");
            ScreenReaderOutput.Speak(Lexicon.Get("settings.network.probing_spoken"),
                VerbosityLevel.Terse, interrupt: true);

            Radios.SmartLink.NetworkDiagnosticReport? report;
            try
            {
                report = await _rig.RunNetworkDiagnosticAsync(forceRefresh: true);
            }
            catch (Exception ex)
            {
                NetworkDiagnosticResultText.Text =
                    Lexicon.Get("settings.network.probe_failed", ("message", ex.Message));
                ScreenReaderOutput.Speak(Lexicon.Get("settings.network.probe_failed_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (report == null)
            {
                NetworkDiagnosticResultText.Text = Lexicon.Get("settings.network.no_smartlink_session");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.radio.check.no_smartlink_session_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            if (!report.ProbeCompleted)
            {
                NetworkDiagnosticResultText.Text =
                    Lexicon.Get("settings.network.probe_incomplete", ("detail", report.ErrorDetail));
                ScreenReaderOutput.Speak(Lexicon.Get("settings.network.probe_incomplete_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
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
            string Yn(bool? v) => v switch
            {
                true => Lexicon.Get("settings.network.diagnostic_yes"),
                false => Lexicon.Get("settings.network.diagnostic_no"),
                null => Lexicon.Get("settings.network.diagnostic_unknown"),
            };
            return Lexicon.Get("settings.network.diagnostic_summary",
                ("upnpTcp", Yn(r.UpnpTcpReachable)),
                ("upnpUdp", Yn(r.UpnpUdpReachable)),
                ("manualTcp", Yn(r.ManualForwardTcpReachable)),
                ("manualUdp", Yn(r.ManualForwardUdpReachable)),
                ("holePunch", Yn(r.NatSupportsHolePunch)));
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
                NetworkDiagnosticResultText.Text = Lexicon.Get("settings.network.no_report_yet");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.network.no_report_to_copy_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }
            try
            {
                System.Windows.Clipboard.SetText(report.ToMarkdown());
                NetworkDiagnosticResultText.Text = Lexicon.Get("settings.network.report_copied");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.network.report_copied_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
            }
            catch (Exception ex)
            {
                NetworkDiagnosticResultText.Text =
                    Lexicon.Get("settings.network.copy_failed", ("message", ex.Message));
                ScreenReaderOutput.Speak(Lexicon.Get("settings.network.copy_failed_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
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
                NetworkDiagnosticResultText.Text = Lexicon.Get("settings.network.no_report_yet");
                ScreenReaderOutput.Speak(Lexicon.Get("settings.network.no_report_to_save_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"JJFlex-NetworkDiagnostic-{report.TimestampUtc:yyyy-MM-dd-HHmm}.md",
                DefaultExt = ".md",
                Filter = Lexicon.Get("settings.network.save_report_filter"),
                Title = Lexicon.Get("settings.network.save_report_title"),
            };
            bool? ok = dlg.ShowDialog(this);
            if (ok != true) return;

            try
            {
                System.IO.File.WriteAllText(dlg.FileName, report.ToMarkdown());
                NetworkDiagnosticResultText.Text =
                    Lexicon.Get("settings.network.report_saved", ("fileName", dlg.FileName));
                ScreenReaderOutput.Speak(Lexicon.Get("settings.network.report_saved_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
            }
            catch (Exception ex)
            {
                NetworkDiagnosticResultText.Text =
                    Lexicon.Get("settings.network.save_failed", ("message", ex.Message));
                ScreenReaderOutput.Speak(Lexicon.Get("settings.network.save_failed_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
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
                NetworkDiagnosticResultText.Text =
                    Lexicon.Get("settings.network.help_file_missing", ("path", path));
                ScreenReaderOutput.Speak(Lexicon.Get("settings.network.help_file_missing_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
                return;
            }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
                NetworkDiagnosticResultText.Text =
                    Lexicon.Get("settings.network.help_opened", ("fileName", fileName));
                ScreenReaderOutput.Speak(
                    Lexicon.Get("settings.network.help_opened_spoken", ("fileName", fileName)),
                    VerbosityLevel.Terse, interrupt: true);
            }
            catch (Exception ex)
            {
                NetworkDiagnosticResultText.Text =
                    Lexicon.Get("settings.network.help_open_failed", ("message", ex.Message));
                ScreenReaderOutput.Speak(Lexicon.Get("settings.network.help_open_failed_spoken"),
                    VerbosityLevel.Terse, interrupt: true);
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
            // DELETED: the CheckBox announces its own name and new state. The
            // setting takes effect exactly as the box shows - no read-back, no
            // divergence - so this restated it and cut the announcement to do
            // so.
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
            // 0 — was "Click beep", maps to Beep enum
            TypingSoundCombo.Items.Add(Lexicon.Get("settings.sound.typing_musical_notes"));
            TypingSoundCombo.Items.Add(Lexicon.Get("settings.sound.typing_single_tone"));
            TypingSoundCombo.Items.Add(Lexicon.Get("settings.sound.typing_random_tones"));

            // Unlockable modes slot in between the always-on audio modes and "Off".
            bool mechUnlocked = FreqOutHandlers.IsCalibrationUnlocked(CalibrationEngine.Ref2, _audioConfig.TuningHash);
            bool dtmfUnlocked = FreqOutHandlers.IsCalibrationUnlocked(CalibrationEngine.Ref1, _audioConfig.TuningHash);

            int mechIdx = -1, dtmfIdx = -1;
            if (mechUnlocked)
            {
                mechIdx = TypingSoundCombo.Items.Count;
                TypingSoundCombo.Items.Add(Lexicon.Get("settings.sound.typing_mechanical_keyboard"));
            }
            if (dtmfUnlocked)
            {
                dtmfIdx = TypingSoundCombo.Items.Count;
                TypingSoundCombo.Items.Add(Lexicon.Get("settings.sound.typing_touch_tone"));
            }

            // "Off" is always last.
            int offIdx = TypingSoundCombo.Items.Count;
            TypingSoundCombo.Items.Add(Lexicon.Get("settings.sound.typing_off"));

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
                MessageBox.Show(Lexicon.Get("settings.ptt.timeout_out_of_range"),
                    Lexicon.Get("settings.dialog_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                SelectTabByHeader("PTT");  // Track C papercut: focusing a field on an unselected tab fails silently
                PttTimeoutBox.Focus();
                return false;
            }

            if (!int.TryParse(PttWarning1Box.Text, out int w1))
            {
                MessageBox.Show(Lexicon.Get("settings.ptt.warning1_not_a_number"),
                    Lexicon.Get("settings.dialog_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                SelectTabByHeader("PTT");  // Track C papercut: focusing a field on an unselected tab fails silently
                PttWarning1Box.Focus();
                return false;
            }

            if (!int.TryParse(PttWarning2Box.Text, out int w2))
            {
                MessageBox.Show(Lexicon.Get("settings.ptt.warning2_not_a_number"),
                    Lexicon.Get("settings.dialog_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                SelectTabByHeader("PTT");  // Track C papercut: focusing a field on an unselected tab fails silently
                PttWarning2Box.Focus();
                return false;
            }

            if (!int.TryParse(PttOhCrapBox.Text, out int ohCrap))
            {
                MessageBox.Show(Lexicon.Get("settings.ptt.final_warning_not_a_number"),
                    Lexicon.Get("settings.dialog_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                SelectTabByHeader("PTT");  // Track C papercut: focusing a field on an unselected tab fails silently
                PttOhCrapBox.Focus();
                return false;
            }

            if (!int.TryParse(PttAlcBox.Text, out int alc) || alc < 0 || (alc > 0 && alc < 10) || alc > 300)
            {
                MessageBox.Show(Lexicon.Get("settings.ptt.alc_out_of_range"),
                    Lexicon.Get("settings.dialog_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
            _pttConfig.CutTransmitOnReflectedAlarm = ReflectedCutCheckbox.IsChecked == true;
            _pttConfig.Validate();

            // Tuning tab
            if (CoarseStepCombo.SelectedIndex >= 0 && CoarseStepCombo.SelectedIndex < _coarseStepOptions.Count)
                CoarseTuneStep = _coarseStepOptions[CoarseStepCombo.SelectedIndex].Hz;
            if (FineStepCombo.SelectedIndex >= 0 && FineStepCombo.SelectedIndex < _fineStepOptions.Count)
                FineTuneStep = _fineStepOptions[FineStepCombo.SelectedIndex].Hz;
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
            _audioConfig.EarconConnectionEnabled = EarconConnectionCheck.IsChecked == true;
            _audioConfig.EarconTransmitEnabled = EarconTransmitCheck.IsChecked == true;
            _audioConfig.EarconDialogsEnabled = EarconDialogsCheck.IsChecked == true;
            _audioConfig.EarconTuningEnabled = EarconTuningCheck.IsChecked == true;
            _audioConfig.EarconCommandsEnabled = EarconCommandsCheck.IsChecked == true;
            _audioConfig.EarconWarningsEnabled = EarconWarningsCheck.IsChecked == true;
            _audioConfig.CwNotificationsEnabled = CwNotificationsCheck.IsChecked == true;
            // Sprint 33 Track F. Read back by index rather than by the label
            // text: the labels are user-facing prose and will be reworded, and
            // a setting that stops persisting the day someone improves a
            // sentence is the worst kind of coupling.
            _audioConfig.EarconVoiceSet = Math.Clamp(AlertToneSetCombo.SelectedIndex, 0,
                EarconVoices.SetLabels.Count - 1);
            _audioConfig.CwPitchFollowsRadio = CwPitchSourceCombo.SelectedIndex == 1;
            int wave = CwWaveformCombo.SelectedIndex;
            _audioConfig.CwWaveform = wave >= 0 && wave < EarconVoices.CwWaveforms.Count
                ? EarconVoices.CwWaveforms[wave].Id
                : EarconVoices.DefaultCwWaveformId;
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
            _audioConfig.OfferStationSaveOnDisconnect =
                OfferStationSaveOnDisconnectCheck.IsChecked == true;

            // #116 / #384 — the duck and the per-sound trims apply LIVE (they
            // are found by ear; see InitializeSoundAdjustments), so by now the
            // statics are ahead of this config object. Capture them back
            // BEFORE Apply(), or Apply() would push the stale values loaded at
            // dialog-open straight over the ones the operator just set and
            // auditioned — and the commit's CaptureFromEngine would then
            // persist the wiped state as if it were chosen.
            _audioConfig.RxDuckEnabled = RxDuck.Enabled;
            _audioConfig.RxDuckDepthDb = RxDuck.DepthDb;
            _audioConfig.RxDuckTiming = RxDuck.TimingName(RxDuck.Timing);
            var liveTrims = new List<AudioOutputConfig.EarconLevelTrim>();
            foreach (var kv in EarconPlayer.GetAllLevelTrimsDb())
                liveTrims.Add(new AudioOutputConfig.EarconLevelTrim { Id = kv.Key, Db = kv.Value });
            // Sorted by id for a stable file, mirroring CaptureFromEngine.
            liveTrims.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            _audioConfig.EarconLevelTrims = liveTrims;

            // Apply audio settings immediately
            _audioConfig.Apply();

            // Sprint 28 Phase 1 — Accessibility tab: commit DoubleTapTolerance selection.
            // Save updates AccessibilityConfig.Current as a side effect, so any consumer
            // reading the static Current accessor sees the new value after this returns.
            AccessibilityConfig.Current.DoubleTapTolerance = GetSelectedDoubleTapTolerance();
            // Sprint 43 Track E (#318). Committed alongside the tolerance so
            // the whole Accessibility tab saves or does not save together.
            AccessibilityConfig.Current.SliceArrowOrder = GetSelectedSliceArrowOrder();
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
                        Lexicon.Get("settings.commit.some_items_wait_lead"));
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
                        body.Append(Lexicon.Get("settings.commit.done_right_away_lead")
                            + string.Join(" ", applied));
                    }
                    AdvisoryDialog.Show(Lexicon.Get("settings.commit.some_items_wait_title"),
                        body.ToString());
                }
                else
                {
                    ScreenReaderOutput.Speak(Lexicon.Get("settings.commit.saved_spoken"),
                        VerbosityLevel.Terse, interrupt: true);
                }
            }
            else
            {
                string summary = Lexicon.Get("settings.commit.applied_spoken");
                if (queued.Count > 0)
                    summary += Lexicon.Get("settings.commit.waiting_lead") + string.Join(" ", queued);
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

        /// <summary>True while LoadSettings assigns the reflected-cut checkbox,
        /// so a saved OFF does not lecture the operator on every open.</summary>
        private bool _suppressReflectedCutWarning;

        /// <summary>
        /// Disarming the reflected-power cut is never a silent flip (#224).
        /// </summary>
        /// <remarks>
        /// The failure this guards: an operator unchecks it casually, forgets,
        /// and months later transmits into a genuinely failed antenna trusting
        /// a guard that is off — a defeatable safety that is off and still
        /// trusted is worse than no safety. So the uncheck itself says what is
        /// being given up, out loud, right now. Critical because it must
        /// outrank the verbosity filter the way the reflected warning itself
        /// does; no interrupt, so the screen reader's own "not checked" lands
        /// first and this follows. The flip still commits on OK like its
        /// neighbours — this is the receipt for the choice, not the apply.
        /// Re-checking is deliberately quiet: "checked" already says the
        /// protection is back, and speech that repeats the obvious trains the
        /// operator to stop listening (see the reflected warning's own
        /// once-per-transmission rule).
        /// </remarks>
        private void ReflectedCutCheckbox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressReflectedCutWarning) return;
            ScreenReaderOutput.Speak(Lexicon.Get("settings.ptt.reflected_cut_off_spoken"),
                VerbosityLevel.Critical);
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
                MessageBox.Show(Lexicon.Get("settings.filter_presets.needs_operator_profile"),
                    Lexicon.Get("settings.filter_presets.needs_operator_profile_title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // #49 family: a corrupt preset file is sidelined and announced,
            // never silently replaced by defaults that then get edited and
            // saved over the operator's real tuning.
            var presets = Radios.FilterPresets.Load(ConfigDirectory, OperatorName,
                out string? corruptPath);
            if (corruptPath != null)
            {
                Radios.ScreenReaderOutput.Speak(
                    Lexicon.Get("settings.filter_presets.unreadable",
                        ("fileName", System.IO.Path.GetFileName(corruptPath))),
                    Radios.VerbosityLevel.Critical);
            }
            var editor = new FilterPresetEditorDialog(presets);
            editor.Owner = this;
            if (editor.ShowDialog() == true && editor.Changed)
            {
                // Announce what actually happened — a failed write spoken as
                // "saved" is the lying receipt this arc keeps hunting down.
                if (presets.Save(ConfigDirectory, OperatorName))
                    Radios.ScreenReaderOutput.Speak(Lexicon.Get("settings.filter_presets.saved"), true);
                else
                    Radios.ScreenReaderOutput.Speak(
                        Lexicon.Get("settings.filter_presets.not_written"),
                        Radios.VerbosityLevel.Critical, true);
            }
        }

        #region #116 / #384 — the warning duck and per-sound loudness (Sprint 42 Track F)

        // Every sound the trim picker offers, in combo order: category groups
        // in catalog order, then anything uncategorised — the same walk of the
        // same registry the Earcon Explorer makes, so the two surfaces can
        // never disagree about what exists.
        private readonly List<EarconEntry> _trimEntries = new();

        // Populating the combo raises SelectionChanged, and a Settings dialog
        // that plays a sound on the way open would be a bug — the same
        // reasoning as _suppressSoundPreviews for the three sound pickers.
        private bool _suppressTrimPreview;

        /// <summary>
        /// Wire the duck (#116) and per-sound trim (#384) controls on the
        /// Notifications tab.
        /// </summary>
        /// <remarks>
        /// Both surfaces apply LIVE, which is the Audio tab's rule for values
        /// found by ear: a dip depth or a loudness trim that only lands after
        /// an OK-and-reopen round trip cannot be found by ear at all. They
        /// initialise from the live statics rather than from
        /// <see cref="_audioConfig"/> for the same reason — the statics ARE
        /// the audible truth — and <see cref="SaveSettings"/> captures them
        /// back into the config before Apply() so a commit persists exactly
        /// what the operator is hearing.
        /// </remarks>
        private void InitializeSoundAdjustments()
        {
            // #116 — the duck. The depth panel collapses rather than disables
            // when the duck is off (house rule: nothing that cannot act stays
            // in the tab order).
            RxDuckEnabledCheck.IsChecked = RxDuck.Enabled;
            RxDuckDepthPanel.Visibility =
                RxDuck.Enabled ? Visibility.Visible : Visibility.Collapsed;
            RxDuckDepthControl.Setup(Lexicon.Get("settings.sound.duck_depth_label"),
                0, (int)RxDuck.MaxDepthDb, 1, (int)Math.Round(RxDuck.DepthDb),
                unit: Lexicon.Get("settings.audio.decibel_unit"));
            RxDuckDepthControl.ValueChanged += RxDuckDepth_ValueChanged;

            // #535 — how the dip moves. Checking a radio here fires its Checked
            // handler, which writes the same preset straight back: idempotent,
            // so no suppression flag is needed.
            switch (RxDuck.Timing)
            {
                case RxDuckTimingPreset.Gentle: RxDuckTimingGentleRadio.IsChecked = true; break;
                case RxDuckTimingPreset.Lingering: RxDuckTimingLingeringRadio.IsChecked = true; break;
                default: RxDuckTimingQuickRadio.IsChecked = true; break;
            }

            // #384 — the trims. The value control is set up BEFORE the combo
            // is filled, because filling the combo selects an entry and the
            // selection handler writes this control — against the default
            // 0..100 range a -6 would clamp to zero.
            EarconTrimControl.Setup(Lexicon.Get("settings.sound.trim_label"),
                (int)EarconPlayer.MinLevelTrimDb, (int)EarconPlayer.MaxLevelTrimDb, 1,
                unit: Lexicon.Get("settings.audio.decibel_unit"));
            EarconTrimControl.ValueChanged += EarconTrim_ValueChanged;

            _suppressTrimPreview = true;
            try
            {
                foreach (var category in EarconCatalog.Categories)
                    foreach (var entry in EarconCatalog.InCategory(category))
                        AddTrimEntry(entry);
                foreach (var entry in EarconCatalog.Uncategorised)
                    AddTrimEntry(entry);
                if (EarconTrimSoundCombo.Items.Count > 0)
                    EarconTrimSoundCombo.SelectedIndex = 0;
            }
            finally
            {
                _suppressTrimPreview = false;
            }
        }

        private void AddTrimEntry(EarconEntry entry)
        {
            _trimEntries.Add(entry);
            EarconTrimSoundCombo.Items.Add(entry.Label + " — " + entry.CategoryLabel);
        }

        private EarconEntry? SelectedTrimEntry()
        {
            int idx = EarconTrimSoundCombo.SelectedIndex;
            return idx >= 0 && idx < _trimEntries.Count ? _trimEntries[idx] : null;
        }

        /// <summary>Whether this entry's family switch currently lets it play.</summary>
        private static bool TrimEntryCategoryLive(EarconEntry entry) =>
            entry.Category is not { } cat || EarconPlayer.GetCategoryEnabled(cat);

        private void EarconTrimSoundCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var entry = SelectedTrimEntry();
            if (entry == null) return;

            EarconTrimControl.SuppressEvents = true;
            EarconTrimControl.Value = (int)Math.Round(EarconPlayer.GetLevelTrimDb(entry.Id));
            EarconTrimControl.SuppressEvents = false;

            // The Stop button exists only while it could do something.
            EarconTrimStopButton.Visibility =
                entry.IsContinuous ? Visibility.Visible : Visibility.Collapsed;

            // Preview on selection, because finding a sound by ear beats
            // finding it by name — the same rule as the three sound pickers.
            // Continuous sounds are excluded (arrowing past one must not
            // start a loop), and a gated family previews as silence rather
            // than as a spoken refusal on every arrow press; the Play button
            // gives the refusal its words on demand.
            if (_suppressTrimPreview || entry.IsContinuous) return;
            if (!EarconPlayer.EarconsEnabled || !TrimEntryCategoryLive(entry)) return;
            entry.Play();
        }

        private void EarconTrim_ValueChanged(object? sender, int value)
        {
            var entry = SelectedTrimEntry();
            if (entry == null) return;
            // Live, so the next preview or Play is already at the new trim.
            // The player clamps and treats zero as "no trim".
            EarconPlayer.SetLevelTrimDb(entry.Id, value);
        }

        private void EarconTrimPlayButton_Click(object sender, RoutedEventArgs e)
        {
            var entry = SelectedTrimEntry();
            if (entry == null) return;

            // Never a dead button: say why instead of playing silence.
            if (!EarconPlayer.EarconsEnabled)
            {
                ScreenReaderOutput.Speak(Lexicon.Get("settings.sound.trim_master_off"),
                    VerbosityLevel.Terse, true);
                return;
            }
            if (!TrimEntryCategoryLive(entry))
            {
                ScreenReaderOutput.Speak(Lexicon.Get("settings.sound.trim_family_off"),
                    VerbosityLevel.Terse, true);
                return;
            }

            entry.Play();
        }

        private void EarconTrimStopButton_Click(object sender, RoutedEventArgs e)
        {
            // Stopping an already-stopped sound is a harmless no-op, and the
            // silence that follows a real stop is its own confirmation.
            SelectedTrimEntry()?.Stop?.Invoke();
        }

        /// <summary>
        /// Checked AND Unchecked, not Click, so the handler hears every state
        /// change however it happens — a press, the Space bar, or code — and
        /// stays idempotent because of it (integration-pass rule).
        /// </summary>
        private void RxDuckEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (RxDuckDepthPanel == null) return; // fired mid-construction
            bool on = RxDuckEnabledCheck.IsChecked == true;
            // Live — the next warning obeys immediately, and unchecking
            // mid-audition is itself an audition ("play the alarm, now
            // without the dip").
            RxDuck.Enabled = on;
            RxDuckDepthPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RxDuckDepth_ValueChanged(object? sender, int value)
        {
            RxDuck.DepthDb = value; // live; the setter clamps 0..12
        }

        /// <summary>
        /// Checked only, not Unchecked: a radio group raises exactly one Checked
        /// per change, and the radio giving up the selection has nothing to say.
        /// Idempotent — it reads the group's state rather than trusting the
        /// sender — so firing it from InitializeSoundAdjustments is harmless.
        /// </summary>
        private void RxDuckTimingRadio_Checked(object sender, RoutedEventArgs e)
        {
            // Fired mid-construction, before the later radios exist.
            if (RxDuckTimingQuickRadio == null || RxDuckTimingGentleRadio == null
                || RxDuckTimingLingeringRadio == null) return;

            // Live, like the depth, and for the same reason: timing is found by
            // ear, and the audition path is the same one — pick a step, play
            // the alarm from Per-Sound Loudness, listen to how the band moves.
            // No speech of our own: the reader already announces which radio
            // is checked, and a second voice saying the same thing is noise.
            RxDuck.Timing = CheckedDuckTiming();
        }

        private RxDuckTimingPreset CheckedDuckTiming()
        {
            if (RxDuckTimingLingeringRadio.IsChecked == true) return RxDuckTimingPreset.Lingering;
            if (RxDuckTimingGentleRadio.IsChecked == true) return RxDuckTimingPreset.Gentle;
            return RxDuckTimingPreset.Quick;
        }

        #endregion
    }
}
