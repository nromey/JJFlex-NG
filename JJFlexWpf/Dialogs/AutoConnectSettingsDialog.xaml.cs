using System.Windows;
using System.Windows.Automation;

namespace JJFlexWpf.Dialogs
{
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

        public AutoConnectSettingsDialog(string radioName, bool currentAutoConnect, bool currentLowBandwidth)
        {
            AutoConnectEnabled = currentAutoConnect;
            LowBandwidth = currentLowBandwidth;

            InitializeComponent();

            RadioNameText.Text = radioName;
            AutomationProperties.SetName(RadioNameText, "Radio name: " + radioName);
            AutoConnectCheckbox.IsChecked = currentAutoConnect;
            LowBandwidthCheckbox.IsChecked = currentLowBandwidth;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            AutoConnectEnabled = AutoConnectCheckbox.IsChecked == true;
            LowBandwidth = LowBandwidthCheckbox.IsChecked == true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Shows the dialog and returns true if user clicked Save.
        /// </summary>
        public static bool ShowSettingsDialog(System.Windows.Window? owner, string radioName,
            ref bool autoConnect, ref bool lowBandwidth)
        {
            var dialog = new AutoConnectSettingsDialog(radioName, autoConnect, lowBandwidth);
            if (owner != null)
            {
                try { dialog.Owner = owner; } catch { }
            }
            if (dialog.ShowDialog() == true)
            {
                autoConnect = dialog.AutoConnectEnabled;
                lowBandwidth = dialog.LowBandwidth;
                return true;
            }
            return false;
        }
    }
}
