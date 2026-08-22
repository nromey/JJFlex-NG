using System.Windows;
using System.Windows.Automation;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Auto-connect settings for one radio. Track C: carries the standard
    /// OK / Apply / Cancel pair (OK applies and closes, Apply applies and
    /// stays, Cancel discards) instead of the bespoke "Save" it used to have.
    /// The commit itself lives in the caller-supplied <see cref="SettingsApplied"/>
    /// delegate so Apply-and-stay actually saves — a dialog-local property
    /// read only after close cannot do that.
    /// </summary>
    public partial class AutoConnectSettingsDialog : JJFlexDialog
    {
        /// <summary>
        /// Whether auto-connect should be enabled for this radio.
        /// </summary>
        public bool AutoConnectEnabled { get; private set; }

        /// <summary>
        /// Whether to use low bandwidth mode.
        /// </summary>
        public bool LowBandwidth { get; private set; }

        /// <summary>
        /// Invoked with (autoConnect, lowBandwidth) on every OK or Apply.
        /// The caller owns persistence and any side effects (clearing other
        /// radios' auto-connect, refreshing lists, announcing).
        /// </summary>
        public Action<bool, bool>? SettingsApplied { get; set; }

        public AutoConnectSettingsDialog(string radioName, bool currentAutoConnect, bool currentLowBandwidth)
        {
            AutoConnectEnabled = currentAutoConnect;
            LowBandwidth = currentLowBandwidth;

            InitializeComponent();

            RadioNameText.Text = radioName;
            AutomationProperties.SetName(RadioNameText,
                Radios.Lexicon.Get("settings.auto_connect.radio_name_accessible", ("radioName", radioName)));
            AutoConnectCheckbox.IsChecked = currentAutoConnect;
            LowBandwidthCheckbox.IsChecked = currentLowBandwidth;
        }

        private void Commit()
        {
            AutoConnectEnabled = AutoConnectCheckbox.IsChecked == true;
            LowBandwidth = LowBandwidthCheckbox.IsChecked == true;
            SettingsApplied?.Invoke(AutoConnectEnabled, LowBandwidth);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Commit();
            DialogResult = true;
            Close();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            Commit();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.auto_connect.applied"),
                Radios.VerbosityLevel.Terse, interrupt: true);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Shows the dialog. <paramref name="onApplied"/> runs on every OK or
        /// Apply with the current checkbox values; Cancel after Apply does not
        /// roll back (Windows convention — settled in the Track C plan).
        /// Returns true when the dialog was closed with OK.
        /// </summary>
        public static bool ShowSettingsDialog(System.Windows.Window? owner, string radioName,
            bool autoConnect, bool lowBandwidth, Action<bool, bool> onApplied)
        {
            var dialog = new AutoConnectSettingsDialog(radioName, autoConnect, lowBandwidth)
            {
                SettingsApplied = onApplied,
            };
            if (owner != null)
            {
                try { dialog.Owner = owner; } catch { }
            }
            return dialog.ShowDialog() == true;
        }
    }
}
