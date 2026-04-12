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
        }

        // Typing sound combo indices (always-available items first, then unlockable)
        // 0: Musical notes, 1: Single tone, 2: Random tones, 3: Off
        // 4+: Mechanical keyboard (if unlocked), Touch-tone (if unlocked)
        private void PopulateTypingSoundCombo()
        {
            TypingSoundCombo.Items.Clear();
            TypingSoundCombo.Items.Add("Musical notes");       // 0 — was "Click beep", maps to Beep enum
            TypingSoundCombo.Items.Add("Single tone");         // 1
            TypingSoundCombo.Items.Add("Random tones");        // 2
            TypingSoundCombo.Items.Add("Off");                 // 3

            // Unlockable modes only shown when TuningHash contains them
            bool mechUnlocked = FreqOutHandlers.IsCalibrationUnlocked(CalibrationEngine.Ref2, _audioConfig.TuningHash);
            bool dtmfUnlocked = FreqOutHandlers.IsCalibrationUnlocked(CalibrationEngine.Ref1, _audioConfig.TuningHash);

            int mechIdx = -1, dtmfIdx = -1;
            if (mechUnlocked) { mechIdx = TypingSoundCombo.Items.Count; TypingSoundCombo.Items.Add("Mechanical keyboard"); }
            if (dtmfUnlocked) { dtmfIdx = TypingSoundCombo.Items.Count; TypingSoundCombo.Items.Add("Touch-tone (DTMF)"); }

            // Select current mode
            int idx = _audioConfig.TypingSound switch
            {
                TypingSoundMode.Beep => 0,
                TypingSoundMode.SingleTone => 1,
                TypingSoundMode.RandomTones => 2,
                TypingSoundMode.Off => 3,
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

            // Typing sound mode — map combo index back to enum
            // Fixed indices: 0=Musical notes(Beep), 1=SingleTone, 2=RandomTones, 3=Off
            // Dynamic indices 4+ depend on which easter eggs are unlocked
            bool mechUnlocked = FreqOutHandlers.IsCalibrationUnlocked(CalibrationEngine.Ref2, _audioConfig.TuningHash);
            bool dtmfUnlocked = FreqOutHandlers.IsCalibrationUnlocked(CalibrationEngine.Ref1, _audioConfig.TuningHash);
            int tsIdx = TypingSoundCombo.SelectedIndex;
            int mechIdx = mechUnlocked ? 4 : -1;
            int dtmfIdx = dtmfUnlocked ? (mechUnlocked ? 5 : 4) : -1;
            _audioConfig.TypingSound = tsIdx switch
            {
                0 => TypingSoundMode.Beep,
                1 => TypingSoundMode.SingleTone,
                2 => TypingSoundMode.RandomTones,
                3 => TypingSoundMode.Off,
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
            if (int.TryParse(CwSpeedBox.Text, out int cwSpeed) && cwSpeed >= 10 && cwSpeed <= 30)
                _audioConfig.CwSpeedWpm = cwSpeed;
            _audioConfig.CwModeAnnounce = CwModeAnnounceCheck.IsChecked == true;

            // Sync the meter tones checkbox on Notifications tab with Audio tab
            _audioConfig.MeterTonesEnabled = MeterTonesNotifCheck.IsChecked == true;

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
