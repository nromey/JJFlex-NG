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

    private void ImportDialog_Loaded(object sender, RoutedEventArgs e)
    {
        string logFile = GetLogFileName?.Invoke() ?? "";
        if (string.IsNullOrEmpty(logFile))
        {
            MessageBox.Show("You must specify a log file.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            Close();
            return;
        }

        ToName.Text = logFile;

        // Pick input file
        _inputFile = PickInputFile?.Invoke(logFile);
        if (string.IsNullOrEmpty(_inputFile))
        {
            DialogResult = false;
            Close();
            return;
        }

        FromName.Text = _inputFile;
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_inputFile)) return;

        ImportingLabel.Visibility = Visibility.Visible;

        bool success = DoImport?.Invoke(_inputFile!) ?? false;

        if (success)
        {
            DialogResult = true;
        }
        else
        {
            MessageBox.Show("Import did not complete successfully.", "Import",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
        }

        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
