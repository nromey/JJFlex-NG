using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Connection Tester setup dialog.
    /// Discovers radios (local + SmartLink), lets user configure test parameters,
    /// then runs the automated test loop with progress reporting.
    /// </summary>
    public partial class ConnectionTesterDialog : JJFlexDialog
    {
        private readonly ConnectionTesterCallbacks _callbacks;
        private Action<FlexBase.RigData> _radioFoundCallback;
        private bool _testRunning;
        private ConnectionTester _tester;

        public ConnectionTesterDialog(ConnectionTesterCallbacks callbacks)
        {
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
            InitializeComponent();

            // Start local discovery
            _radioFoundCallback = OnRadioFound;
            _callbacks.RegisterRadioFound(_radioFoundCallback);
            _callbacks.StartLocalDiscovery();
        }

        private void OnRadioFound(FlexBase.RigData radio)
        {
            Dispatcher.Invoke(() =>
            {
                // Avoid duplicates
                foreach (RadioListItem item in RadioList.Items)
                {
                    if (item.Serial == radio.Serial) return;
                }
                RadioList.Items.Add(new RadioListItem
                {
                    Name = radio.Name,
                    Serial = radio.Serial,
                    Remote = radio.Remote,
                    ModelName = radio.ModelName
                });
            });
        }

        private void SmartLinkButton_Click(object sender, RoutedEventArgs e)
        {
            SmartLinkButton.IsEnabled = false;
            StatusText.Text = "Connecting to SmartLink...";
            ScreenReaderOutput.Speak("Connecting to SmartLink");
            _callbacks.StartRemoteDiscovery();

            // Re-enable after a delay
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                SmartLinkButton.IsEnabled = true;
                StatusText.Text = RadioList.Items.Count > 0
                    ? $"Found {RadioList.Items.Count} radio(s). Select one and click Start."
                    : "No radios found. Try SmartLink again.";
            };
            timer.Start();
        }

        private void RadioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            StartButton.IsEnabled = RadioList.SelectedItem != null && !_testRunning;
            if (RadioList.SelectedItem is RadioListItem item)
            {
                StatusText.Text = $"Selected: {item.Name} ({(item.Remote ? "Remote" : "Local")})";
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (RadioList.SelectedItem is not RadioListItem selected) return;

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

            // Unregister discovery events before starting tests
            _callbacks.UnregisterRadioFound();

            // Disable UI
            _testRunning = true;
            StartButton.IsEnabled = false;
            SmartLinkButton.IsEnabled = false;
            RadioList.IsEnabled = false;
            TestCountBox.IsEnabled = false;
            DelayBox.IsEnabled = false;
            StartButton.Content = "Running...";

            // Create and configure tester
            _tester = new ConnectionTester
            {
                TestCount = testCount,
                DelayBetweenTestsMs = delay * 1000,
                RadioSerial = selected.Serial,
                RadioName = selected.Name,
                LowBandwidth = false,
                IsRemote = selected.Remote,
                OpenParms = _callbacks.OpenParms,
                AccountSelector = _callbacks.AccountSelector
            };

            _tester.PhaseChanged += (testNum, phase) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = $"Test {testNum} of {testCount}: {phase}";
                });
            };

            _tester.TestCompleted += (testNum, success, reason, durationMs) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    string result = success ? "PASS" : $"FAIL ({reason})";
                    StatusText.Text = $"Test {testNum}: {result} ({durationMs / 1000.0:F1}s)";
                });
            };

            _tester.AllTestsCompleted += (summary) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _testRunning = false;
                    StatusText.Text = $"Complete: {summary.Passed}/{summary.TestCount} passed. Report: {summary.ReportPath ?? "not saved"}";
                    StartButton.Content = "Done";

                    ScreenReaderOutput.Speak(
                        $"All tests complete. {summary.Passed} of {summary.TestCount} passed. " +
                        $"{summary.Failed} failed. Report saved.");
                });
            };

            // Run tests on background thread
            var testThread = new Thread(() => _tester.Run())
            {
                IsBackground = true,
                Name = "ConnectionTester"
            };
            testThread.SetApartmentState(ApartmentState.STA);
            testThread.Start();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _tester?.Cancel();
            _callbacks.UnregisterRadioFound();
            base.OnClosing(e);
        }

        private class RadioListItem
        {
            public string Name { get; set; }
            public string Serial { get; set; }
            public string ModelName { get; set; }
            public bool Remote { get; set; }

            public override string ToString() =>
                $"{Name} ({(Remote ? "SmartLink" : "Local")})";
        }
    }

    /// <summary>
    /// Callbacks provided by the host application to the Connection Tester dialog.
    /// </summary>
    public class ConnectionTesterCallbacks
    {
        public Action StartLocalDiscovery { get; set; }
        public Action StartRemoteDiscovery { get; set; }
        public Action<Action<FlexBase.RigData>> RegisterRadioFound { get; set; }
        public Action UnregisterRadioFound { get; set; }
        public FlexBase.OpenParms OpenParms { get; set; }
        public Func<SmartLinkAccountManager, (bool newLogin, SmartLinkAccount selected, bool ok)?> AccountSelector { get; set; }
    }
}
