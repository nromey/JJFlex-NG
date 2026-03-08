using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using Radios;

namespace JJFlexWpf.Dialogs
{
    public partial class ConnectionTesterDialog : JJFlexDialog
    {
        private readonly string _radioName;
        private readonly string _radioSerial;
        private readonly bool _isRemote;
        private readonly bool _lowBW;
        private readonly FlexBase.OpenParms _openParms;
        private readonly string _smartLinkEmail;

        private ConnectionTester _tester;
        private bool _testRunning;

        public ConnectionTesterDialog(
            string radioName,
            string radioSerial,
            bool isRemote,
            bool lowBW,
            FlexBase.OpenParms openParms,
            string smartLinkEmail = "")
        {
            _radioName = radioName;
            _radioSerial = radioSerial;
            _isRemote = isRemote;
            _lowBW = lowBW;
            _openParms = openParms;
            _smartLinkEmail = smartLinkEmail ?? "";

            InitializeComponent();

            var connType = isRemote ? "Remote (SmartLink)" : "Local";
            var bwText = lowBW ? ", Low Bandwidth" : "";
            RadioInfoText.Text = $"{radioName}  Serial: {radioSerial}  {connType}{bwText}";
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_testRunning)
            {
                // Stop button behavior
                _tester?.Cancel();
                StartButton.IsEnabled = false;
                StatusText.Text = "Cancelling...";
                ScreenReaderOutput.Speak("Cancelling test");
                return;
            }

            // Validate parameters
            if (!int.TryParse(TestCountBox.Text, out int testCount) || testCount < 3)
            {
                ScreenReaderOutput.Speak("Test count must be at least 3");
                TestCountBox.Focus();
                return;
            }
            if (!int.TryParse(DelayBox.Text, out int delay) || delay < 1)
            {
                ScreenReaderOutput.Speak("Delay must be at least 1 second");
                DelayBox.Focus();
                return;
            }
            if (!int.TryParse(ManualDelayBox.Text, out int manualDelay) || manualDelay < 0)
            {
                ScreenReaderOutput.Speak("User delay must be 0 or more seconds");
                ManualDelayBox.Focus();
                return;
            }

            var mode = (ConnectMode)ModeBox.SelectedIndex;

            // Lock UI for test run
            _testRunning = true;
            StartButton.Content = "Stop";
            CloseButton.IsEnabled = false;
            TestCountBox.IsEnabled = false;
            DelayBox.IsEnabled = false;
            ModeBox.IsEnabled = false;
            ManualDelayBox.IsEnabled = false;
            ResultsBox.Items.Clear();

            _tester = new ConnectionTester
            {
                TestCount = testCount,
                DelayBetweenTestsMs = delay * 1000,
                RadioSerial = _radioSerial,
                RadioName = _radioName,
                LowBandwidth = _lowBW,
                IsRemote = _isRemote,
                OpenParms = _openParms,
                Mode = mode,
                ManualDelayMs = manualDelay * 1000,
                CurrentSmartLinkEmail = _smartLinkEmail,
                // Auto-select most recent saved account — same as manual connect
                AccountSelector = (mgr) =>
                {
                    var accounts = mgr.Accounts;
                    if (accounts.Count == 0)
                        return (true, null, true); // no accounts → trigger new login
                    var best = accounts.OrderByDescending(a => a.LastUsed).First();
                    return (false, best, true); // use most recent account
                }
            };

            _tester.PhaseChanged += (testNum, phase) =>
                Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = $"Test {testNum} of {testCount}: {phase}";
                });

            _tester.TestCompleted += (testNum, success, reason, durationMs) =>
                Dispatcher.BeginInvoke(() =>
                {
                    string passText = success ? "PASS" : "FAIL";
                    string line = $"#{testNum:D2} {passText} {durationMs / 1000.0:F1}s {reason}";
                    ResultsBox.Items.Add(line);
                    ResultsBox.ScrollIntoView(line);

                    StatusText.Text = $"Test {testNum}: {passText} ({durationMs / 1000.0:F1}s)";
                    ScreenReaderOutput.Speak($"Test {testNum} {passText}");
                });

            _tester.AllTestsCompleted += (summary) =>
                Dispatcher.BeginInvoke(() =>
                {
                    _testRunning = false;
                    StatusText.Text = $"Complete: {summary.Passed}/{summary.TestCount} passed, {summary.Failed} failed. Mode: {summary.Mode}";
                    StartButton.Content = "Start";
                    StartButton.IsEnabled = true;
                    CloseButton.IsEnabled = true;
                    TestCountBox.IsEnabled = true;
                    DelayBox.IsEnabled = true;
                    ModeBox.IsEnabled = true;
                    ManualDelayBox.IsEnabled = true;

                    var msg = $"All tests complete. {summary.Passed} of {summary.TestCount} passed. " +
                              $"{summary.Failed} failed. Mode {summary.Mode}. Report saved.";
                    ScreenReaderOutput.Speak(msg);
                });

            // STA needed for WebView2 login fallback in setupRemote
            var testThread = new Thread(() => _tester.Run())
            {
                IsBackground = true,
                Name = "ConnectionTester"
            };
            testThread.SetApartmentState(ApartmentState.STA);
            testThread.Start();

            ScreenReaderOutput.Speak($"Starting {testCount} {mode} tests on {_radioName}");
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ConnectionTesterDialog_Closing(object sender, CancelEventArgs e)
        {
            if (_testRunning)
            {
                _tester?.Cancel();
            }
        }
    }
}
