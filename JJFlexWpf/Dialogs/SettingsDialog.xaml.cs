using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;

namespace JJFlexWpf.Dialogs
{
    public partial class SettingsDialog : JJFlexDialog
    {
        private readonly PttConfig _pttConfig;

        // Tuning step results (read after DialogResult == true)
        public int CoarseTuneStep { get; private set; }
        public int FineTuneStep { get; private set; }
        public bool BandMemoryEnabled { get; private set; }

        // License results (stub — wired to LicenseConfig when Track C merges)
        public string LicenseClass { get; set; } = "Extra";
        public bool BandBoundaryNotifications { get; set; }
        public bool TxBandLockout { get; set; }

        private static readonly (int hz, string label)[] CoarseStepOptions =
        {
            (1000, "1 kHz"), (2000, "2 kHz"), (5000, "5 kHz")
        };

        private static readonly (int hz, string label)[] FineStepOptions =
        {
            (5, "5 Hz"), (10, "10 Hz"), (100, "100 Hz")
        };

        private static readonly string[] LicenseClasses =
        {
            "Extra", "Advanced", "General", "Technician"
        };

        public SettingsDialog(
            PttConfig pttConfig,
            int currentCoarseStep,
            int currentFineStep)
        {
            _pttConfig = pttConfig;
            CoarseTuneStep = currentCoarseStep;
            FineTuneStep = currentFineStep;
            BandMemoryEnabled = pttConfig.BandMemoryEnabled;

            InitializeComponent();

            // Select all text when tabbing into any TextBox
            AddHandler(TextBox.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(TextBox_GotKeyboardFocus));

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

            // License tab
            foreach (var cls in LicenseClasses)
                LicenseClassCombo.Items.Add(cls);
            LicenseClassCombo.SelectedItem = LicenseClass;
            if (LicenseClassCombo.SelectedIndex < 0) LicenseClassCombo.SelectedIndex = 0;

            BandBoundaryCheckbox.IsChecked = BandBoundaryNotifications;
            TxLockoutCheckbox.IsChecked = TxBandLockout;
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
            _pttConfig.Validate();

            // Tuning tab
            if (CoarseStepCombo.SelectedIndex >= 0 && CoarseStepCombo.SelectedIndex < CoarseStepOptions.Length)
                CoarseTuneStep = CoarseStepOptions[CoarseStepCombo.SelectedIndex].hz;
            if (FineStepCombo.SelectedIndex >= 0 && FineStepCombo.SelectedIndex < FineStepOptions.Length)
                FineTuneStep = FineStepOptions[FineStepCombo.SelectedIndex].hz;
            BandMemoryEnabled = BandMemoryCheckbox.IsChecked == true;
            _pttConfig.BandMemoryEnabled = BandMemoryEnabled;

            // License tab
            LicenseClass = LicenseClassCombo.SelectedItem?.ToString() ?? "Extra";
            BandBoundaryNotifications = BandBoundaryCheckbox.IsChecked == true;
            TxBandLockout = TxLockoutCheckbox.IsChecked == true;

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
    }
}
