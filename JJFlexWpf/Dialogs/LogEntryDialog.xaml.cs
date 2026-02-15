using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for LogEntry.vb.
/// Standalone QSO log entry dialog (not the LogEntryControl used in Logging Mode —
/// this is the separate dialog for browsing/editing individual log records).
///
/// Architecture:
/// - The log form content is provided by the caller as a UIElement (via SetFormContent)
/// - Session operations (start, seek, read, write, navigate) use delegates
/// - Field operations use delegates (get/set field text, goto field)
/// - Key commands (PageUp/Down for record navigation, custom hotkeys) handled in PreviewKeyDown
/// - No direct references to LogSession, LogEntry, Form1, or KeyCommands
///
/// Sprint 9 Track B.
/// </summary>
public partial class LogEntryDialog : JJFlexDialog
{
    #region Delegates

    /// <summary>Starts a log session. Returns true on success.</summary>
    public Func<bool>? StartSession { get; set; }

    /// <summary>Ends the current log session.</summary>
    public Action? EndSession { get; set; }

    /// <summary>Navigates to the previous record. Returns true if successful.</summary>
    public Func<bool>? PreviousRecord { get; set; }

    /// <summary>Navigates to the next record. Returns true if successful.</summary>
    public Func<bool>? NextRecord { get; set; }

    /// <summary>Navigates to the first record.</summary>
    public Action? SeekToFirst { get; set; }

    /// <summary>Navigates to the last record.</summary>
    public Action? SeekToLast { get; set; }

    /// <summary>Shows the current record on screen.</summary>
    public Action? ShowEntry { get; set; }

    /// <summary>Creates a new blank entry.</summary>
    public Action? NewEntry { get; set; }

    /// <summary>Writes the current entry. Returns true on success.</summary>
    public Func<bool>? WriteEntry { get; set; }

    /// <summary>Asks user whether to write unsaved changes. Returns false if cancelled.</summary>
    public Func<bool>? OptionalWrite { get; set; }

    /// <summary>Gets field text by ADIF tag. Returns the text, or null.</summary>
    public Func<string, string?>? GetFieldText { get; set; }

    /// <summary>Sets field text by ADIF tag.</summary>
    public Action<string, string>? SetFieldText { get; set; }

    /// <summary>Focuses the field identified by ADIF tag.</summary>
    public Action<string>? GotoField { get; set; }

    /// <summary>Handles a key command. Returns true if the key was consumed.</summary>
    public Func<int, bool>? DoCommand { get; set; }

    /// <summary>Whether the dialog is in search-argument mode (not full entry mode).</summary>
    public bool IsSearchMode { get; set; }

    /// <summary>Whether a write is needed.</summary>
    public bool NeedsWrite { get; set; }

    /// <summary>File position of the record (-1 for new entry at end).</summary>
    public long FilePosition { get; set; } = -1;

    #endregion

    private bool _wasActive;

    public LogEntryDialog()
    {
        InitializeComponent();
        ResizeMode = ResizeMode.CanResize;

        Loaded += LogEntryDialog_Loaded;
        Activated += LogEntryDialog_Activated;
        Closing += LogEntryDialog_Closing;
    }

    /// <summary>
    /// Sets the log form content (the dynamic field layout created by the log session).
    /// </summary>
    public void SetFormContent(UIElement content)
    {
        FormContent.Content = content;
    }

    private void LogEntryDialog_Loaded(object sender, RoutedEventArgs e)
    {
        _wasActive = false;

        if (IsSearchMode)
        {
            Title = "Get Search Arguments";
            ShowEntry?.Invoke();
        }
        else
        {
            Title = "Log Entry Form";
            if (StartSession?.Invoke() != true)
            {
                DialogResult = false;
                Close();
                return;
            }
            if (FilePosition != -1)
                ShowEntry?.Invoke();
        }
    }

    private void LogEntryDialog_Activated(object sender, EventArgs e)
    {
        if (!_wasActive)
        {
            _wasActive = true;
            GotoField?.Invoke("CALL");
        }
    }

    private void LogEntryDialog_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        EndSession?.Invoke();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        // Record navigation with PageUp/PageDown
        if (e.Key == Key.PageUp && !IsSearchMode)
        {
            OptionalWrite?.Invoke();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                SeekToFirst?.Invoke();
                NextRecord?.Invoke();
            }
            else
            {
                PreviousRecord?.Invoke();
            }
            ShowEntry?.Invoke();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.PageDown && !IsSearchMode)
        {
            OptionalWrite?.Invoke();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                SeekToLast?.Invoke();
            NextRecord?.Invoke();
            ShowEntry?.Invoke();
            e.Handled = true;
            return;
        }

        // Forward to command handler
        int winFormsKey = (int)WpfKeyConverter.ToWinFormsKeys(e);
        if (DoCommand?.Invoke(winFormsKey) == true)
        {
            e.Handled = true;
        }
    }

    private void OKButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Updates the status bar text.
    /// </summary>
    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}
