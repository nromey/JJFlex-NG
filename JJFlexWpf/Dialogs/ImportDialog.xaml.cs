using System;
using System.Windows;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for ImportForm.vb.
/// ADIF/CSV import dialog: lets user pick source file, shows destination log,
/// then imports records via delegate.
///
/// All log session and file operations use delegates — no direct LogSession references.
///
/// Sprint 9 Track B.
/// </summary>
public partial class ImportDialog : JJFlexDialog
{
    #region Delegates

    /// <summary>Gets the current log file name for display.</summary>
    public Func<string>? GetLogFileName { get; set; }

    /// <summary>
    /// Shows a file open dialog and returns the chosen path, or null if cancelled.
    /// Parameter: the suggested initial directory.
    /// </summary>
    public Func<string, string?>? PickInputFile { get; set; }

    /// <summary>
    /// Performs the import. Parameter: input file path.
    /// Returns true on success, false on error.
    /// </summary>
    public Func<string, bool>? DoImport { get; set; }

    #endregion

    private string? _inputFile;

    public ImportDialog()
    {
        InitializeComponent();
        Loaded += ImportDialog_Loaded;
    }

    // ExportDialog's twin, including the bug it carried: the WinForms
    // Load-time early exit assigned DialogResult directly, which in WPF throws
    // on any window not opened with ShowDialog() and aborted the Tier 1 dialog
    // suite on 2026-08-20/21 (#159). CloseWithResult is the guarded route; the
    // message states the real condition so the operator hears WHY the window
    // went away instead of experiencing a silent absence.
    private void ImportDialog_Loaded(object sender, RoutedEventArgs e)
    {
        string logFile = GetLogFileName?.Invoke() ?? "";
        if (string.IsNullOrEmpty(logFile))
        {
            MessageBox.Show(Radios.Lexicon.Get("logging.import.no_log_file"),
                Radios.Lexicon.Get("logging.import.no_log_file_title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            CloseWithResult(false);
            return;
        }

        ToName.Text = logFile;

        // Pick input file. A cancelled picker needs no message: the operator
        // just cancelled it themselves.
        _inputFile = PickInputFile?.Invoke(logFile);
        if (string.IsNullOrEmpty(_inputFile))
        {
            CloseWithResult(false);
            return;
        }

        FromName.Text = _inputFile;
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_inputFile)) return;

        ImportingLabel.Visibility = Visibility.Visible;

        bool success = DoImport?.Invoke(_inputFile!) ?? false;

        if (!success)
        {
            MessageBox.Show(Radios.Lexicon.Get("logging.import.failed"),
                Radios.Lexicon.Get("logging.import.failed_title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        CloseWithResult(success);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWithResult(false);
    }
}
