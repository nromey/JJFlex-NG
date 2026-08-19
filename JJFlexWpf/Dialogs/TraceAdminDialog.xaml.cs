using System.Windows;
using Microsoft.Win32;

namespace JJFlexWpf.Dialogs
{
    public partial class TraceAdminDialog : JJFlexDialog
    {
        private static readonly string[] TraceLevels = { "Off", "Error", "Warning", "Info", "Verbose" };
        private bool _isTracing;

        /// <summary>
        /// Initial file path for trace file. Set before showing.
        /// </summary>
        public string InitialFilePath { get; set; } = "";

        /// <summary>
        /// Default trace level index (0-4). Default is 3 (Info).
        /// </summary>
        public int DefaultLevel { get; set; } = 3;

        /// <summary>
        /// Called to start tracing. Receives (filePath, levelIndex).
        /// </summary>
        public Action<string, int>? StartTracing { get; set; }

        /// <summary>
        /// Called to stop tracing.
        /// </summary>
        public Action? StopTracing { get; set; }

        /// <summary>
        /// The selected trace file path result.
        /// </summary>
        public string ResultFilePath { get; private set; } = "";

        /// <summary>
        /// The selected trace level index result.
        /// </summary>
        public int ResultLevel { get; private set; } = 3;

        public TraceAdminDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LevelListBox.ItemsSource = TraceLevels;

            if (!string.IsNullOrEmpty(InitialFilePath))
            {
                FileNameBox.Text = InitialFilePath;
            }
            else
            {
                // The live log, NOT Documents\JJRadioTrace.txt.
                //
                // That old default put the file outside the settings folder,
                // where nothing rotates, archives, prunes or bundles it — and
                // it still carried Jim's pre-rename "JJRadio" name. A file the
                // reporting pipeline cannot see is a file the operator cannot
                // send, which is the opposite of what this dialog is for.
                string live = "";
                try { live = JJFlexWpf.DiagnosticsBridge.LiveLogPath?.Invoke() ?? ""; }
                catch { /* bridge unwired — fall through to the folder default */ }
                if (string.IsNullOrEmpty(live))
                {
                    string folder = "";
                    try { folder = JJFlexWpf.DiagnosticsBridge.LogFolder?.Invoke() ?? ""; } catch { }
                    if (string.IsNullOrEmpty(folder))
                        folder = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "JJFlexRadio");
                    live = System.IO.Path.Combine(folder, "JJFlexRadioTrace.txt");
                }
                FileNameBox.Text = live;
            }

            // Read the LIVE state, every time the dialog opens.
            //
            // This used to initialize to false unconditionally, so opening the
            // dialog mid-trace announced "Start tracing" for a trace that was
            // already running — and pressing the button restarted to a new file,
            // silently discarding the capture the operator was in the middle of.
            // The accessible-name fix of 2026-08-17 was correct and made this
            // worse: the button then faithfully reported a state that was
            // fiction.
            _isTracing = JJTrace.Tracing.On;

            LevelListBox.SelectedIndex = DefaultLevel;
            UpdateToggleButton();
        }

        private void UpdateToggleButton()
        {
            // Content and accessible name change together. The XAML used to
            // hardcode AutomationProperties.Name="Start or stop tracing", so a
            // screen reader heard the same words in both states — the exact
            // defect Noel reported 2026-08-11. This whole dialog is retired by
            // the ratified diagnostic-log design
            // (docs/planning/active/diagnostic-log-surface.md) and no menu opens
            // it any more; it survives one release as a fallback, so it has to
            // tell the truth for that release.
            ToggleButton.Content = _isTracing ? "Stop" : "Start";
            System.Windows.Automation.AutomationProperties.SetName(
                ToggleButton, _isTracing ? "Stop tracing" : "Start tracing");
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isTracing = !_isTracing;
            UpdateToggleButton();

            if (_isTracing)
            {
                if (string.IsNullOrWhiteSpace(FileNameBox.Text))
                {
                    MessageBox.Show("You must specify a file name.", Title,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    _isTracing = false;
                    UpdateToggleButton();
                    return;
                }
                if (LevelListBox.SelectedIndex < 0)
                {
                    MessageBox.Show("You must select a trace level.", Title,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    _isTracing = false;
                    UpdateToggleButton();
                    return;
                }
                ResultFilePath = FileNameBox.Text;
                ResultLevel = LevelListBox.SelectedIndex;
                StartTracing?.Invoke(ResultFilePath, ResultLevel);
            }
            else
            {
                StopTracing?.Invoke();
            }

            DialogResult = true;
            Close();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Trace File",
                FileName = FileNameBox.Text
            };
            if (ofd.ShowDialog() == true)
            {
                FileNameBox.Text = ofd.FileName;
            }
        }
    }
}
