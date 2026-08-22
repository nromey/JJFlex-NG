using System;
using System.Windows;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for ExportForm.vb.
/// ADIF/CSV export dialog: shows source file, lets user pick destination,
/// then exports records via delegate.
///
/// All log session operations use delegates — no direct LogSession references.
///
/// Sprint 9 Track B.
/// </summary>
public partial class ExportDialog : JJFlexDialog
{
    #region Delegates

    /// <summary>Gets the current log file name for display.</summary>
    public Func<string>? GetLogFileName { get; set; }

    /// <summary>
    /// Shows a file save dialog and returns the chosen path, or null if cancelled.
    /// Parameter: the suggested file name.
    /// </summary>
    public Func<string, string?>? PickOutputFile { get; set; }

    /// <summary>
    /// Performs the export. Parameter: output file path.
    /// Returns true on success, false on error.
    /// </summary>
    public Func<string, bool>? DoExport { get; set; }

    #endregion

    private string? _outputFile;

    public ExportDialog()
    {
        InitializeComponent();
        Loaded += ExportDialog_Loaded;
    }

    // Early exits happen at Loaded on purpose: this is the WinForms
    // ExportForm's Load-time flow carried over — bail out when there is no log
    // to export, and treat cancelling the output picker as cancelling the
    // export. What could NOT carry over is assigning DialogResult directly:
    // in WPF that throws on any window not opened with ShowDialog(), and on
    // 2026-08-20/21 exactly that throw, fired from here during realisation,
    // aborted the Tier 1 dialog suite (#159). CloseWithResult is the guarded
    // route that works under both Show() and ShowDialog().
    private void ExportDialog_Loaded(object sender, RoutedEventArgs e)
    {
        string logFile = GetLogFileName?.Invoke() ?? "";
        if (string.IsNullOrEmpty(logFile))
        {
            // Say WHY before vanishing. A dialog that opens and instantly
            // closes with no explanation is a silent absence to a screen
            // reader user — the honest guard in LogStats.ShowLogStats is the
            // model. State the condition, not an instruction to the developer.
            MessageBox.Show(Radios.Lexicon.Get("logging.export.no_log_file"),
                Radios.Lexicon.Get("logging.export.no_log_file_title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            CloseWithResult(false);
            return;
        }

        FromName.Text = logFile;

        // Pick output file. A cancelled picker needs no message: the operator
        // just cancelled it themselves.
        _outputFile = PickOutputFile?.Invoke(logFile);
        if (string.IsNullOrEmpty(_outputFile))
        {
            CloseWithResult(false);
            return;
        }

        ToName.Text = _outputFile;
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_outputFile)) return;

        ExportingLabel.Visibility = Visibility.Visible;

        bool success = DoExport?.Invoke(_outputFile!) ?? false;

        CloseWithResult(success);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWithResult(false);
    }
}
