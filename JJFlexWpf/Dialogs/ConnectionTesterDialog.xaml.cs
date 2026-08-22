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

            var connType = (isRemote ? Lexicon.Get("connect.tester.type_remote") : Lexicon.Get("connect.tester.type_local"));
            var bwText = lowBW ? Lexicon.Get("connect.tester.low_bandwidth_suffix") : "";
            RadioInfoText.Text = Lexicon.Get("connect.tester.radio_info",
                ("radioName", radioName), ("radioSerial", radioSerial),
                ("connType", connType), ("bwText", bwText));
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_testRunning)
            {
                // Stop button behavior
                _tester?.Cancel();
                StartButton.IsEnabled = false;
                StatusText.Text = Lexicon.Get("connect.tester.cancelling_status");
                ScreenReaderOutput.Speak(Lexicon.Get("connect.tester.cancelling_speech"), VerbosityLevel.Terse);
                return;
            }

            // Validate parameters
            if (!int.TryParse(TestCountBox.Text, out int testCount) || testCount < 3)
            {
                ScreenReaderOutput.Speak(Lexicon.Get("connect.tester.count_too_low"), VerbosityLevel.Critical);
                TestCountBox.Focus();
                return;
            }
            if (!int.TryParse(DelayBox.Text, out int delay) || delay < 1)
            {
                ScreenReaderOutput.Speak(Lexicon.Get("connect.tester.delay_too_low"), VerbosityLevel.Critical);
                DelayBox.Focus();
                return;
            }
            if (!int.TryParse(ManualDelayBox.Text, out int manualDelay) || manualDelay < 0)
            {
                ScreenReaderOutput.Speak(Lexicon.Get("connect.tester.user_delay_negative"), VerbosityLevel.Critical);
                ManualDelayBox.Focus();
                return;
            }

            var mode = (ConnectMode)ModeBox.SelectedIndex;

            // Lock UI for test run
            _testRunning = true;
            StartButton.Content = Lexicon.Get("connect.tester.stop_button");
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
                    StatusText.Text = Lexicon.Get("connect.tester.phase_status",
                        ("testNum", testNum), ("testCount", testCount), ("phase", phase));
                });

            _tester.TestCompleted += (testNum, success, reason, durationMs) =>
                Dispatcher.BeginInvoke(() =>
                {
                    string passText = (success ? Lexicon.Get("connect.tester.pass") : Lexicon.Get("connect.tester.fail"));
                    string seconds = (durationMs / 1000.0).ToString("F1");
                    string line = Lexicon.Get("connect.tester.result_line",
                        ("testNum", testNum.ToString("D2")), ("passText", passText),
                        ("seconds", seconds), ("reason", reason));
                    ResultsBox.Items.Add(line);
                    ResultsBox.ScrollIntoView(line);

                    StatusText.Text = Lexicon.Get("connect.tester.result_status",
                        ("testNum", testNum), ("passText", passText), ("seconds", seconds));
                    ScreenReaderOutput.Speak(
                        Lexicon.Get("connect.tester.result_speech",
                            ("testNum", testNum), ("passText", passText)),
                        VerbosityLevel.Critical);
                });

            _tester.AllTestsCompleted += (summary) =>
                Dispatcher.BeginInvoke(() =>
                {
                    _testRunning = false;
                    StatusText.Text = Lexicon.Get("connect.tester.complete_status",
                        ("passed", summary.Passed), ("testCount", summary.TestCount),
                        ("failed", summary.Failed), ("mode", summary.Mode));
                    StartButton.Content = Lexicon.Get("connect.tester.start_button");
                    StartButton.IsEnabled = true;
                    CloseButton.IsEnabled = true;
                    TestCountBox.IsEnabled = true;
                    DelayBox.IsEnabled = true;
                    ModeBox.IsEnabled = true;
                    ManualDelayBox.IsEnabled = true;

                    var msg = Lexicon.Get("connect.tester.complete_speech",
                        ("passed", summary.Passed), ("testCount", summary.TestCount),
                        ("failed", summary.Failed), ("mode", summary.Mode));
                    ScreenReaderOutput.Speak(msg, VerbosityLevel.Critical);
                });

            // STA needed for WebView2 login fallback in setupRemote
            var testThread = new Thread(() => _tester.Run())
            {
                IsBackground = true,
                Name = "ConnectionTester"
            };
            testThread.SetApartmentState(ApartmentState.STA);
            testThread.Start();

            ScreenReaderOutput.Speak(
                Lexicon.Get("connect.tester.starting",
                    ("testCount", testCount), ("mode", mode), ("radioName", _radioName)),
                VerbosityLevel.Terse);
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
