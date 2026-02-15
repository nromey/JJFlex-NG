using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for FindLogEntry.vb.
/// Log search dialog: displays search results in a list, allows navigating to individual entries.
///
/// Architecture:
/// - Search runs asynchronously; results are added to the list as they arrive
/// - DispatcherTimer replaces WinForms Timer for queue processing
/// - ResultSelected event fires when user clicks/enters on a result
/// - No direct references to LogSession, LogEntry, or Form1
///
/// Sprint 9 Track B.
/// </summary>
public partial class FindLogEntryDialog : JJFlexDialog
{
    #region Data Model

    /// <summary>
    /// Represents a search result item.
    /// </summary>
    public class SearchResultItem
    {
        public string DisplayText { get; set; } = "";
        public long FilePosition { get; set; }
        public override string ToString() => DisplayText;
    }

    #endregion

    #region Delegates

    /// <summary>
    /// Starts the search. The delegate should begin searching in the background
    /// and call AddResult() for each match found.
    /// </summary>
    public Action? StartSearch { get; set; }

    /// <summary>
    /// Called when a search result is selected (double-click or Enter).
    /// Parameter: the file position of the selected entry.
    /// </summary>
    public Action<long>? OnResultSelected { get; set; }

    /// <summary>
    /// Called when the dialog is closing, to stop the search thread.
    /// </summary>
    public Action? StopSearch { get; set; }

    /// <summary>
    /// Set to the call sign to search for (if pre-specified).
    /// </summary>
    public string? SearchCall { get; set; }

    #endregion

    private readonly ObservableCollection<SearchResultItem> _results = new();
    private readonly DispatcherTimer _checkTimer;
    private readonly Queue<SearchResultItem> _pendingResults = new();
    private readonly object _queueLock = new();
    private bool _searchComplete;

    public FindLogEntryDialog()
    {
        InitializeComponent();
        ResizeMode = ResizeMode.CanResize;

        ItemList.ItemsSource = _results;

        _checkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _checkTimer.Tick += CheckTimer_Tick;

        Loaded += FindLogEntryDialog_Loaded;
        Closing += FindLogEntryDialog_Closing;
    }

    private void FindLogEntryDialog_Loaded(object sender, RoutedEventArgs e)
    {
        _searchComplete = false;
        StartSearch?.Invoke();
        _checkTimer.Start();
    }

    private void FindLogEntryDialog_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _checkTimer.Stop();
        StopSearch?.Invoke();
    }

    /// <summary>
    /// Called from the search thread to add a result. Thread-safe.
    /// </summary>
    public void AddResult(SearchResultItem item)
    {
        lock (_queueLock)
        {
            _pendingResults.Enqueue(item);
        }
    }

    /// <summary>
    /// Called from the search thread when the search is complete. Thread-safe.
    /// </summary>
    public void SearchFinished()
    {
        _searchComplete = true;
    }

    private void CheckTimer_Tick(object? sender, EventArgs e)
    {
        // Drain the queue
        lock (_queueLock)
        {
            while (_pendingResults.Count > 0)
            {
                _results.Add(_pendingResults.Dequeue());
            }
        }

        if (_searchComplete)
        {
            _checkTimer.Stop();
            if (_results.Count == 0)
            {
                MessageBox.Show("No items were found.", "Find Log Entries",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = false;
                Close();
            }
        }
    }

    private void SelectCurrentItem()
    {
        if (ItemList.SelectedItem is SearchResultItem item)
        {
            OnResultSelected?.Invoke(item.FilePosition);
        }
    }

    private void ItemList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SelectCurrentItem();
    }

    private void ItemList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            SelectCurrentItem();
            e.Handled = true;
        }
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
