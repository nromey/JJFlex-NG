using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace JJFlexWpf.Dialogs
{
    public partial class LOTWMergeDialog : JJFlexDialog
    {
        /// <summary>
        /// Validates the LOTW file. Returns error message or null on success.
        /// </summary>
        public Func<string, string?>? ValidateLOTWFile { get; set; }

        /// <summary>
        /// Validates the log file. Returns error message or null on success.
        /// </summary>
        public Func<string?>? ValidateLogFile { get; set; }

        /// <summary>
        /// The current log file path. Set before showing.
        /// </summary>
        public string LogFilePath { get; set; } = "";

        /// <summary>
        /// Called to start the merge. Receives (lotwFilePath, progressCallback).
        /// The progressCallback should be called with status text updates.
        /// Called on a background thread — the dialog handles marshaling to UI thread.
        /// </summary>
        public Action<string, Action<string>, Action<string>>? StartMerge { get; set; }

        /// <summary>
        /// Called to cancel an in-progress merge.
        /// </summary>
        public Action? CancelMerge { get; set; }

        /// <summary>
        /// Called when the dialog is closing to clean up resources.
        /// </summary>
        public Action? CleanupMerge { get; set; }

        private string _lotwFilePath = "";
        private bool _mergeRunning;

        public LOTWMergeDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Prompt for LOTW file
            var ofd = new OpenFileDialog
            {
                Filter = "ADIF files (*.adi;*.adif)|*.adi;*.adif|All files (*.*)|*.*",
                Title = "Select LOTW File",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (ofd.ShowDialog() != true)
            {
                DialogResult = false;
                Close();
                return;
            }
            _lotwFilePath = ofd.FileName;
            LOTWFileBox.Text = _lotwFilePath;

            // Validate LOTW file
            string? lotwError = ValidateLOTWFile?.Invoke(_lotwFilePath);
            if (lotwError != null)
            {
                MessageBox.Show(lotwError, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                DialogResult = false;
                Close();
                return;
            }

            // Validate log file
            string? logError = ValidateLogFile?.Invoke();
            if (logError != null)
            {
                MessageBox.Show(logError, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                DialogResult = false;
                Close();
                return;
            }

            LogBox.Text = LogFilePath;
        }

        private void MergeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mergeRunning) return;
            _mergeRunning = true;
            MergeButton.IsEnabled = false;
            ProgressBox.Text = "Merging ...";

            // Progress callback — marshals to UI thread
            void OnProgress(string text)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ProgressBox.AppendText(text);
                    ProgressBox.ScrollToEnd();
                });
            }

            // Completion callback
            void OnComplete(string summary)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ProgressBox.AppendText("\r\n" + summary);
                    ProgressBox.ScrollToEnd();
                    _mergeRunning = false;
                    CancelCloseButton.Content = "Done";
                    ProgressBox.Focus();
                });
            }

            // Start merge on background thread via delegate
            Task.Run(() =>
            {
                try
                {
                    StartMerge?.Invoke(_lotwFilePath, OnProgress, OnComplete);
                }
                catch (Exception ex)
                {
                    OnComplete($"Error: {ex.Message}");
                }
            });
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mergeRunning)
            {
                CancelMerge?.Invoke();
            }
            else
            {
                DialogResult = true;
                Close();
            }
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_mergeRunning)
            {
                CancelMerge?.Invoke();
            }
            CleanupMerge?.Invoke();
        }
    }
}
