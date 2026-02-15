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
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                FileNameBox.Text = System.IO.Path.Combine(docs, "JJRadioTrace.txt");
            }

            LevelListBox.SelectedIndex = DefaultLevel;
            UpdateToggleButton();
        }

        private void UpdateToggleButton()
        {
            ToggleButton.Content = _isTracing ? "Stop" : "Start";
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
