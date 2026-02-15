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

    private void ExportDialog_Loaded(object sender, RoutedEventArgs e)
    {
        string logFile = GetLogFileName?.Invoke() ?? "";
        if (string.IsNullOrEmpty(logFile))
        {
            MessageBox.Show("You must specify a log file.", "Export",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            Close();
            return;
        }

        FromName.Text = logFile;

        // Pick output file
        _outputFile = PickOutputFile?.Invoke(logFile);
        if (string.IsNullOrEmpty(_outputFile))
        {
            DialogResult = false;
            Close();
            return;
        }

        ToName.Text = _outputFile;
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_outputFile)) return;

        ExportingLabel.Visibility = Visibility.Visible;

        bool success = DoExport?.Invoke(_outputFile!) ?? false;

        DialogResult = success;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
