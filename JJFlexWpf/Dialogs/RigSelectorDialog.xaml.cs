using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Radio display data for the selector list.
    /// </summary>
    public class RadioListItem
    {
        public string Serial { get; set; } = "";
        public string Name { get; set; } = "";
        public string ModelName { get; set; } = "";
        public bool IsRemote { get; set; }
        public bool AutoConnect { get; set; }
        public bool LowBW { get; set; }
        public object RigData { get; set; } = null!;

        public string DisplayText
        {
            get
            {
                var autoConn = AutoConnect ? "[AutoConnect] " : "";
                var lbw = LowBW ? "[LowBW] " : "";
                var namePart = string.IsNullOrWhiteSpace(Name) ? "Unknown" : Name;
                var modelPart = string.IsNullOrWhiteSpace(ModelName) ? "Unknown" : ModelName;
                var serialPart = string.IsNullOrWhiteSpace(Serial) ? "NoSerial" : Serial;
                return $"{autoConn}{lbw}{namePart} {modelPart} {serialPart}";
            }
        }

        public override string ToString() => DisplayText;
    }

    /// <summary>
    /// Callbacks for the RigSelector dialog.
    /// </summary>
    public class RigSelectorCallbacks
    {
        /// <summary>Start local radio discovery.</summary>
        public required Action StartLocalDiscovery { get; init; }

        /// <summary>Start remote (SmartLink) radio discovery.</summary>
        public required Action StartRemoteDiscovery { get; init; }

        /// <summary>Register for radio-found events. Action receives RadioListItem.</summary>
        public required Action<Action<RadioListItem>> RegisterRadioFound { get; init; }

        /// <summary>Unregister from radio-found events.</summary>
        public required Action UnregisterRadioFound { get; init; }

        /// <summary>Auto-connect serial from saved config (empty if none).</summary>
        public string AutoConnectSerial { get; init; } = "";

        /// <summary>Whether auto-connect is desired for the saved radio.</summary>
        public bool AutoConnectDesired { get; init; }

        /// <summary>Whether low bandwidth is set for the saved auto-connect radio.</summary>
        public bool AutoConnectLowBW { get; init; }

        /// <summary>Whether this is the initial startup (enables auto-connect timer).</summary>
        public bool IsInitialBringup { get; init; }

        /// <summary>Whether global auto-connect on startup is enabled.</summary>
        public bool GlobalAutoConnectEnabled { get; init; }

        /// <summary>Save auto-connect settings. Params: serial, radioName, isRemote, lowBW, enabled.</summary>
        public required Action<string, string, bool, bool, bool> SaveAutoConnectSettings { get; init; }

        /// <summary>Save global auto-connect on startup setting.</summary>
        public required Action<bool> SaveGlobalAutoConnect { get; init; }

        /// <summary>Check if a different radio has auto-connect enabled. Returns (hasOther, otherName).</summary>
        public required Func<string, (bool hasOther, string otherName)> CheckOtherAutoConnect { get; init; }

        /// <summary>Screen reader speak (message, interrupt).</summary>
        public Action<string, bool>? ScreenReaderSpeak { get; init; }

        /// <summary>Current SmartLink email for config saving.</summary>
        public string CurrentSmartLinkEmail { get; init; } = "";

        /// <summary>OpenParms for creating test FlexBase instances.</summary>
        public FlexBase.OpenParms? OpenParms { get; init; }

        /// <summary>Show a WinForms connecting window (message). Returns an action to close it.</summary>
        public Func<string, Action>? ShowConnecting { get; init; }

    }

    public partial class RigSelectorDialog : JJFlexDialog
    {
        private const string MustSelect = "You must select a radio.";

        private readonly RigSelectorCallbacks _callbacks;
        private readonly List<RadioListItem> _radiosList = new();
        private readonly object _radiosLock = new();
        private readonly DispatcherTimer _autoConnectTimer;
        /// <summary>
        /// The selected radio data, or null if cancelled.
        /// </summary>
        public object? SelectedRigData { get; private set; }

        /// <summary>
        /// The serial number of the selected radio, or null if cancelled.
        /// </summary>
        public string? SelectedSerial { get; private set; }

        /// <summary>
        /// Whether low bandwidth was selected for the connection.
        /// </summary>
        public bool SelectedLowBW { get; private set; }

        /// <summary>
        /// Whether the selected radio is a remote (SmartLink) radio.
        /// </summary>
        public bool SelectedIsRemote { get; private set; }

        public RigSelectorDialog(RigSelectorCallbacks callbacks)
        {
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));

            InitializeComponent();

            GlobalAutoConnectCheckbox.IsChecked = callbacks.GlobalAutoConnectEnabled;

            // Set up auto-connect timer
            _autoConnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _autoConnectTimer.Tick += AutoConnectTimer_Tick;

            // Register for radio discovery events
            _callbacks.RegisterRadioFound(OnRadioFound);

            // Start local discovery
            _callbacks.StartLocalDiscovery();

            // Announce empty list after discovery settles (500ms)
            Loaded += async (_, _) =>
            {
                await System.Threading.Tasks.Task.Delay(500);
                if (RadiosBox.Items.Count == 0)
                {
                    _callbacks.ScreenReaderSpeak?.Invoke("No radios found", false);
                }
            };

            // Start auto-connect timer if appropriate
            if (callbacks.IsInitialBringup &&
                callbacks.GlobalAutoConnectEnabled &&
                !string.IsNullOrEmpty(callbacks.AutoConnectSerial) &&
                callbacks.AutoConnectDesired)
            {
                _autoConnectTimer.Start();
            }
        }

        private void OnRadioFound(RadioListItem radio)
        {
            // Apply saved auto-connect state
            if (_callbacks.AutoConnectSerial == radio.Serial)
            {
                radio.AutoConnect = _callbacks.AutoConnectDesired;
                radio.LowBW = _callbacks.AutoConnectLowBW;
            }

            lock (_radiosLock)
            {
                _radiosList.RemoveAll(r => r.Serial == radio.Serial);
                _radiosList.Add(radio);
            }

            Dispatcher.Invoke(() =>
            {
                // Close the connecting window — radios have arrived
                if (_closeConnecting != null)
                {
                    _closeConnecting();
                    _closeConnecting = null;

                    // Reclaim focus from the closing connecting form
                    Activate();
                    RadiosBox.Focus();
                }

                RefreshRadiosList();
            });
        }

        private void RefreshRadiosList()
        {
            var selectedSerial = (RadiosBox.SelectedItem as RadioListItem)?.Serial;
            RadiosBox.Items.Clear();
            lock (_radiosLock)
            {
                foreach (var radio in _radiosList)
                    RadiosBox.Items.Add(radio);
            }

            // Restore selection
            if (selectedSerial != null)
            {
                for (int i = 0; i < RadiosBox.Items.Count; i++)
                {
                    if (((RadioListItem)RadiosBox.Items[i]).Serial == selectedSerial)
                    {
                        RadiosBox.SelectedIndex = i;
                        break;
                    }
                }
            }

            // Auto-select if there's only one radio in the list
            if (RadiosBox.SelectedIndex < 0 && RadiosBox.Items.Count == 1)
            {
                RadiosBox.SelectedIndex = 0;
                var radio = (RadioListItem)RadiosBox.Items[0];
                var name = string.IsNullOrWhiteSpace(radio.Name) ? "radio" : radio.Name;
                _callbacks.ScreenReaderSpeak?.Invoke($"{name} selected. Press Enter to connect.", false);
            }

            // Update accessible label for empty list
            if (RadiosBox.Items.Count == 0)
            {
                System.Windows.Automation.AutomationProperties.SetName(
                    RadiosBox, "Radio list, empty, no radios found");
            }
            else
            {
                System.Windows.Automation.AutomationProperties.SetName(
                    RadiosBox, "Available radios");
            }
        }

        private RadioListItem? GetSelectedRadio()
        {
            return RadiosBox.SelectedItem as RadioListItem;
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                if (RadiosBox.Items.Count == 0)
                    ShowNoRadiosGuidance();
                else
                {
                    new MessageDialog { Title = "Select Radio", Message = MustSelect, Owner = this }.ShowDialog();
                    RadiosBox.Focus();
                }
                return;
            }

            DoConnect(radio);
        }

        private void DoConnect(RadioListItem radio)
        {
            var radioName = string.IsNullOrWhiteSpace(radio.Name) ? "radio" : radio.Name;
            _callbacks.ScreenReaderSpeak?.Invoke($"Connecting to {radioName}", true);

            SelectedRigData = radio.RigData;
            SelectedSerial = radio.Serial;
            SelectedLowBW = radio.LowBW;
            SelectedIsRemote = radio.IsRemote;
            DialogResult = true;
            Close();
        }

        private void LowBWButton_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                new MessageDialog { Title = "Select Radio", Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }

            radio.LowBW = !radio.LowBW;
            RefreshRadiosList();
            RadiosBox.Focus();
        }

        private Action? _closeConnecting;

        private void RemoteButton_Click(object sender, RoutedEventArgs e)
        {
            // Show WinForms connecting window to hold focus while SmartLink auth runs.
            // WinForms because standalone WPF windows from VB.NET lose keyboard focus.
            _closeConnecting = _callbacks.ShowConnecting?.Invoke("Connecting to SmartLink...");

            _callbacks.StartRemoteDiscovery();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void AutoConnectTimer_Tick(object? sender, EventArgs e)
        {
            RadioListItem? radio = null;
            lock (_radiosLock)
            {
                radio = _radiosList.Find(r => r.Serial == _callbacks.AutoConnectSerial);
            }

            if (radio != null)
            {
                _autoConnectTimer.Stop();
                DoConnect(radio);
            }
        }

        private void AutoConnectMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                new MessageDialog { Title = "Select Radio", Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }

            // Check if another radio has auto-connect
            var (hasOther, otherName) = _callbacks.CheckOtherAutoConnect(radio.Serial);
            if (hasOther && !radio.AutoConnect)
            {
                var displayOther = string.IsNullOrEmpty(otherName) ? "Another radio" : otherName;
                var result = MessageBox.Show(
                    $"{displayOther} currently has auto-connect enabled.\n\nSwitch auto-connect to {radio.Name}?",
                    "Switch Auto-Connect",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            // Show settings dialog
            var newAutoConnect = radio.AutoConnect;
            var newLowBW = radio.LowBW;

            if (AutoConnectSettingsDialog.ShowSettingsDialog(this, radio.Name, ref newAutoConnect, ref newLowBW))
            {
                // Clear auto-connect from other radios
                if (newAutoConnect)
                {
                    lock (_radiosLock)
                    {
                        foreach (var r in _radiosList)
                        {
                            if (r.Serial != radio.Serial)
                                r.AutoConnect = false;
                        }
                    }
                }

                radio.AutoConnect = newAutoConnect;
                radio.LowBW = newLowBW;

                // Save settings
                _callbacks.SaveAutoConnectSettings(
                    radio.Serial, radio.Name, radio.IsRemote,
                    newLowBW, newAutoConnect);

                RefreshRadiosList();

                if (newAutoConnect)
                    _callbacks.ScreenReaderSpeak?.Invoke($"Auto-connect set for {radio.Name}", true);
                else
                    _callbacks.ScreenReaderSpeak?.Invoke($"Auto-connect cleared for {radio.Name}", true);
            }
        }

        private void ConnectMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ConnectButton_Click(sender, e);
        }

        private void LowBWMenuItem_Click(object sender, RoutedEventArgs e)
        {
            LowBWButton_Click(sender, e);
        }

        private void RadiosBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ConnectButton.IsDefault = true;
        }

        private void RadiosBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ConnectButton.IsDefault = false;
        }

        private void GlobalAutoConnectCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            var enabled = GlobalAutoConnectCheckbox.IsChecked == true;
            _callbacks.SaveGlobalAutoConnect(enabled);

            if (enabled)
                _callbacks.ScreenReaderSpeak?.Invoke("Auto-connect on startup enabled", true);
            else
                _callbacks.ScreenReaderSpeak?.Invoke("Auto-connect on startup disabled", true);
        }

        private void RadiosBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // TestButton stays enabled for tab-order accessibility.
            // Click handler validates selection. Only disable during active test.

            // Auto-connect button requires a selected radio
            AutoConnectButton.IsEnabled = RadiosBox.SelectedItem != null;
        }

        private void AutoConnectButton_Click(object sender, RoutedEventArgs e)
        {
            AutoConnectMenuItem_Click(sender, e);
        }

        private void TestButton_Click(object sender, RoutedEventArgs e)
        {
            var radio = GetSelectedRadio();
            if (radio == null)
            {
                new MessageDialog { Title = "Select Radio", Message = MustSelect, Owner = this }.ShowDialog();
                RadiosBox.Focus();
                return;
            }

            if (_callbacks.OpenParms == null)
            {
                _callbacks.ScreenReaderSpeak?.Invoke("Connection testing not available", true);
                return;
            }

            // Launch standalone ConnectionTesterDialog
            var dialog = new ConnectionTesterDialog(
                radio.Name,
                radio.Serial,
                radio.IsRemote,
                radio.LowBW,
                _callbacks.OpenParms!,
                _callbacks.CurrentSmartLinkEmail)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }

        private void ShowNoRadiosGuidance()
        {
            new MessageDialog
            {
                Title = "No Radios Found",
                Message = "No radios found. Click SmartLink to discover remote radios.",
                Owner = this
            }.ShowDialog();
        }

        private void RigSelectorDialog_Closing(object? sender, CancelEventArgs e)
        {
            _autoConnectTimer.Stop();
            _callbacks.UnregisterRadioFound();
        }
    }
}
