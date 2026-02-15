using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Display mode item for the front panel combo.
    /// </summary>
    public class DisplayModeItem
    {
        public string DisplayText { get; set; } = "";
        public object Value { get; set; } = null!;

        public override string ToString() => DisplayText;
    }

    /// <summary>
    /// Callbacks for the RadioInfo dialog.
    /// </summary>
    public class RadioInfoCallbacks
    {
        /// <summary>Get the current radio model.</summary>
        public required Func<string> GetModel { get; init; }

        /// <summary>Get the firmware version string.</summary>
        public required Func<string> GetVersion { get; init; }

        /// <summary>Get the serial number.</summary>
        public required Func<string> GetSerial { get; init; }

        /// <summary>Get the call sign.</summary>
        public required Func<string> GetCallsign { get; init; }

        /// <summary>Set the call sign.</summary>
        public required Action<string> SetCallsign { get; init; }

        /// <summary>Get the nickname.</summary>
        public required Func<string> GetNickname { get; init; }

        /// <summary>Set the nickname.</summary>
        public required Action<string> SetNickname { get; init; }

        /// <summary>Get the IP address string.</summary>
        public required Func<string> GetIPAddress { get; init; }

        /// <summary>Get the display mode items for the combo.</summary>
        public required Func<List<DisplayModeItem>> GetDisplayModes { get; init; }

        /// <summary>Get the current display mode value.</summary>
        public required Func<object> GetCurrentDisplayMode { get; init; }

        /// <summary>Set the display mode.</summary>
        public required Action<object> SetDisplayMode { get; init; }

        /// <summary>Get the feature availability text.</summary>
        public required Func<string> GetFeatureAvailabilityText { get; init; }

        /// <summary>Refresh the license state.</summary>
        public required Action RefreshLicense { get; init; }
    }

    /// <summary>
    /// Which tab to show initially.
    /// </summary>
    public enum RadioInfoTab
    {
        General = 0,
        FeatureAvailability = 1
    }

    public partial class RadioInfoDialog : JJFlexDialog
    {
        private readonly RadioInfoCallbacks _callbacks;
        private bool _suppressDisplayChange;

        public RadioInfoDialog(RadioInfoCallbacks callbacks, RadioInfoTab initialTab = RadioInfoTab.General)
        {
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
            InitializeComponent();

            if ((int)initialTab >= 0 && (int)initialTab < InfoTabs.Items.Count)
                InfoTabs.SelectedIndex = (int)initialTab;

            ShowValues();
        }

        private void ShowValues()
        {
            ModelBox.Text = _callbacks.GetModel();
            VersionBox.Text = _callbacks.GetVersion();
            SerialBox.Text = _callbacks.GetSerial();
            CallBox.Text = _callbacks.GetCallsign();
            NameBox.Text = _callbacks.GetNickname();
            IPBox.Text = _callbacks.GetIPAddress();

            // Populate display modes
            _suppressDisplayChange = true;
            DisplayCombo.Items.Clear();
            var modes = _callbacks.GetDisplayModes();
            var currentMode = _callbacks.GetCurrentDisplayMode();
            int selectedIdx = -1;
            for (int i = 0; i < modes.Count; i++)
            {
                DisplayCombo.Items.Add(modes[i]);
                if (modes[i].Value?.Equals(currentMode) == true)
                    selectedIdx = i;
            }
            if (selectedIdx >= 0)
                DisplayCombo.SelectedIndex = selectedIdx;
            _suppressDisplayChange = false;

            // Feature availability
            FeatureAvailabilityBox.Text = _callbacks.GetFeatureAvailabilityText();
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CallBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var currentCallsign = _callbacks.GetCallsign();
            if (CallBox.Text != currentCallsign)
                _callbacks.SetCallsign(CallBox.Text);
        }

        private void CallBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                var currentCallsign = _callbacks.GetCallsign();
                if (CallBox.Text != currentCallsign)
                    _callbacks.SetCallsign(CallBox.Text);
            }
        }

        private void NameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var currentNickname = _callbacks.GetNickname();
            if (NameBox.Text != currentNickname)
                _callbacks.SetNickname(NameBox.Text);
        }

        private void NameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Return)
            {
                var currentNickname = _callbacks.GetNickname();
                if (NameBox.Text != currentNickname)
                    _callbacks.SetNickname(NameBox.Text);
            }
        }

        private void DisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDisplayChange) return;
            if (DisplayCombo.SelectedItem is DisplayModeItem item)
                _callbacks.SetDisplayMode(item.Value);
        }

        private void RefreshLicenseButton_Click(object sender, RoutedEventArgs e)
        {
            _callbacks.RefreshLicense();
            FeatureAvailabilityBox.Text = _callbacks.GetFeatureAvailabilityText();
        }
    }
}
