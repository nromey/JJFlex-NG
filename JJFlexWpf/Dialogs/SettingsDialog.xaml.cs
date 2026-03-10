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

            // Slider value change labels
            EarconVolumeSlider.ValueChanged += (s, e) =>
                EarconVolumeLabel.Text = ((int)EarconVolumeSlider.Value).ToString();
            MeterVolumeSlider.ValueChanged += (s, e) =>
                MeterVolumeLabel.Text = ((int)MeterVolumeSlider.Value).ToString();

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

            // Country selector
            string[] countries = { "US" }; // Future: add UK, CA, etc.
            foreach (var c in countries)
                CountryCombo.Items.Add(c);
            CountryCombo.SelectedItem = _licenseConfig.Country ?? "US";
            if (CountryCombo.SelectedIndex < 0) CountryCombo.SelectedIndex = 0;

            EnforceTxRulesCheckbox.IsChecked = _licenseConfig.EnforceTxRules;

            // Audio tab
            var devices = EarconPlayer.GetOutputDevices();
            foreach (var (devNum, name) in devices)
            {
                EarconDeviceCombo.Items.Add(name);
                if (devNum == _audioConfig.EarconDeviceNumber)
                    EarconDeviceCombo.SelectedIndex = EarconDeviceCombo.Items.Count - 1;
            }
            if (EarconDeviceCombo.SelectedIndex < 0) EarconDeviceCombo.SelectedIndex = 0;

            EarconVolumeSlider.Value = _audioConfig.MasterEarconVolume;
            EarconVolumeLabel.Text = _audioConfig.MasterEarconVolume.ToString();

            // Meter Tones tab
            MeterTonesEnabledCheck.IsChecked = _audioConfig.MeterTonesEnabled;

            foreach (var preset in MeterPresetOptions)
            {
                MeterPresetCombo.Items.Add(preset);
                if (preset == _audioConfig.MeterPreset)
                    MeterPresetCombo.SelectedIndex = MeterPresetCombo.Items.Count - 1;
            }
            if (MeterPresetCombo.SelectedIndex < 0) MeterPresetCombo.SelectedIndex = 0;

            int meterVolPct = (int)(_audioConfig.MeterMasterVolume * 100);
            MeterVolumeSlider.Value = meterVolPct;
            MeterVolumeLabel.Text = meterVolPct.ToString();

            PeakWatcherCheck.IsChecked = _audioConfig.PeakWatcherEnabled;
            MeterSpeechCheck.IsChecked = _audioConfig.MeterSpeechEnabled;
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
            _licenseConfig.Country = CountryCombo.SelectedItem as string ?? "US";
            _licenseConfig.EnforceTxRules = EnforceTxRulesCheckbox.IsChecked == true;

            // Audio tab
            var devices = EarconPlayer.GetOutputDevices();
            int devIdx = EarconDeviceCombo.SelectedIndex;
            if (devIdx >= 0 && devIdx < devices.Count)
                _audioConfig.EarconDeviceNumber = devices[devIdx].deviceNumber;
            _audioConfig.MasterEarconVolume = (int)EarconVolumeSlider.Value;

            // Meter Tones tab
            _audioConfig.MeterTonesEnabled = MeterTonesEnabledCheck.IsChecked == true;
            int presetIdx = MeterPresetCombo.SelectedIndex;
            if (presetIdx >= 0 && presetIdx < MeterPresetOptions.Length)
                _audioConfig.MeterPreset = MeterPresetOptions[presetIdx];
            _audioConfig.MeterMasterVolume = (float)MeterVolumeSlider.Value / 100f;
            _audioConfig.PeakWatcherEnabled = PeakWatcherCheck.IsChecked == true;
            _audioConfig.MeterSpeechEnabled = MeterSpeechCheck.IsChecked == true;

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
    }
}
