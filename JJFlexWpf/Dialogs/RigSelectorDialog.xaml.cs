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

        /// <summary>Connect to a radio by serial. Returns true on success.</summary>
        public required Func<string, bool, bool> Connect { get; init; }

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

        /// <summary>SmartLink account selector for test connections.</summary>
        public Func<SmartLinkAccountManager, (bool newLogin, SmartLinkAccount selected, bool ok)?>? AccountSelector { get; init; }

    }

    public partial class RigSelectorDialog : JJFlexDialog
    {
        private const string MustSelect = "You must select a radio.";

        private readonly RigSelectorCallbacks _callbacks;
        private readonly List<RadioListItem> _radiosList = new();
        private readonly object _radiosLock = new();
        private readonly DispatcherTimer _autoConnectTimer;
        private ConnectionTester? _tester;
        private bool _testRunning;

        /// <summary>
        /// The selected radio data, or null if cancelled.
        /// </summary>
        public object? SelectedRigData { get; private set; }

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

            // Start auto-connect timer if appropriate
            if (callbacks.IsInitialBringup &&
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

            Dispatcher.Invoke(RefreshRadiosList);
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

            if (_callbacks.Connect(radio.Serial, radio.LowBW))
            {
                _callbacks.ScreenReaderSpeak?.Invoke($"Connected to {radioName}", true);
                SelectedRigData = radio.RigData;
                DialogResult = true;
                Close();
            }
            else
            {
                _callbacks.ScreenReaderSpeak?.Invoke($"Failed to connect to {radioName}", true);
            }
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

        private void RemoteButton_Click(object sender, RoutedEventArgs e)
        {
            _callbacks.StartRemoteDiscovery();
            RadiosBox.Focus();
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
            var newAutoConnect = !radio.AutoConnect;
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

            // Validate test config
            if (!int.TryParse(TestCountBox.Text, out int testCount) || testCount < 25)
            {
                ScreenReaderOutput.Speak("Test count must be at least 25");
                TestCountBox.Focus();
                return;
            }
            if (!int.TryParse(DelayBox.Text, out int delay) || delay < 1)
            {
                ScreenReaderOutput.Speak("Delay must be at least 1 second");
                DelayBox.Focus();
                return;
            }

            // Show test config panel
            TestPanel.Visibility = Visibility.Visible;

            // Disable all buttons except Cancel
            _testRunning = true;
            ConnectButton.IsEnabled = false;
            TestButton.IsEnabled = false;
            RadiosBox.IsEnabled = false;
            TestCountBox.IsEnabled = false;
            DelayBox.IsEnabled = false;
            GlobalAutoConnectCheckbox.IsEnabled = false;

            _tester = new ConnectionTester
            {
                TestCount = testCount,
                DelayBetweenTestsMs = delay * 1000,
                RadioSerial = radio.Serial,
                RadioName = radio.Name,
                LowBandwidth = radio.LowBW,
                IsRemote = radio.IsRemote,
                OpenParms = _callbacks.OpenParms,
                AccountSelector = _callbacks.AccountSelector
            };

            _tester.PhaseChanged += (testNum, phase) =>
                Dispatcher.BeginInvoke(() =>
                {
                    TestStatusText.Text = $"Test {testNum} of {testCount}: {phase}";
                });

            _tester.TestCompleted += (testNum, success, reason, durationMs) =>
                Dispatcher.BeginInvoke(() =>
                {
                    string result = success ? "PASS" : $"FAIL ({reason})";
                    TestStatusText.Text = $"Test {testNum}: {result} ({durationMs / 1000.0:F1}s)";
                });

            _tester.AllTestsCompleted += (summary) =>
                Dispatcher.BeginInvoke(() =>
                {
                    _testRunning = false;
                    TestStatusText.Text = $"Complete: {summary.Passed}/{summary.TestCount} passed.";
                    TestButton.Content = "Done";

                    // Re-enable UI
                    ConnectButton.IsEnabled = true;
                    RadiosBox.IsEnabled = true;
                    GlobalAutoConnectCheckbox.IsEnabled = true;

                    _callbacks.ScreenReaderSpeak?.Invoke(
                        $"All tests complete. {summary.Passed} of {summary.TestCount} passed. " +
                        $"{summary.Failed} failed. Report saved.", true);
                });

            // Run on background STA thread
            var testThread = new System.Threading.Thread(() => _tester.Run())
            {
                IsBackground = true,
                Name = "ConnectionTester"
            };
            testThread.SetApartmentState(System.Threading.ApartmentState.STA);
            testThread.Start();
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
            _tester?.Cancel();
            _callbacks.UnregisterRadioFound();
        }
    }
}
