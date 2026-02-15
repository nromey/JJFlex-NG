using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for DefineCommands.vb.
/// Key binding editor with 5 scope tabs: Global, Radio, Classic, Modern, Logging.
/// Expands from the original 3-tab version (Sprint 8 Phase 8.6 added the 5-scope system;
/// the WinForms editor only had 3 tabs — this WPF version adds Classic + Modern).
///
/// Architecture:
/// - Key table data provided via delegates (no direct KeyCommands reference)
/// - Auto-clears conflicting bindings (VS Code / IntelliJ style)
/// - Scope-aware conflict detection: same scope = conflict, Global + anything = conflict
///
/// Sprint 9 Track B.
/// </summary>
public partial class DefineCommandsDialog : JJFlexDialog
{
    #region Data Model

    /// <summary>
    /// Scope enum matching KeyCommands.KeyScope values.
    /// </summary>
    public enum Scope { Global = 0, Radio = 1, Classic = 2, Modern = 3, Logging = 4 }

    /// <summary>
    /// Display item for the commands list.
    /// </summary>
    public class CommandDisplayItem
    {
        public int OriginalIndex { get; set; }
        public string KeyText { get; set; } = "";
        public string HelpText { get; set; } = "";
        public string GroupText { get; set; } = "";
        public Scope Scope { get; set; }
        public int KeyValue { get; set; }
        public int CommandId { get; set; }
        public bool IsCommand { get; set; }
    }

    #endregion

    #region Delegates

    /// <summary>Gets the full key table as display items.</summary>
    public Func<List<CommandDisplayItem>>? GetKeyTable { get; set; }

    /// <summary>Converts a key value to display string (e.g., "Ctrl+S").</summary>
    public Func<int, string>? FormatKey { get; set; }

    /// <summary>Gets the default key value for a command by ID.</summary>
    public Func<int, int?>? GetDefaultKey { get; set; }

    /// <summary>Called to save the modified key table. Parameter: list of (commandId, keyValue) pairs.</summary>
    public Action<List<(int commandId, int keyValue)>>? SaveChanges { get; set; }

    /// <summary>Called to update CW text keys.</summary>
    public Action<List<(int commandId, int keyValue)>>? UpdateCWText { get; set; }

    /// <summary>Called to speak text via screen reader.</summary>
    public Action<string>? Speak { get; set; }

    #endregion

    private List<CommandDisplayItem> _allItems = new();
    private bool _commandChanges;
    private bool _messageChanges;

    public DefineCommandsDialog()
    {
        InitializeComponent();
        ResizeMode = ResizeMode.CanResize;
        Loaded += DefineCommandsDialog_Loaded;
    }

    private void DefineCommandsDialog_Loaded(object sender, RoutedEventArgs e)
    {
        _allItems = GetKeyTable?.Invoke() ?? new();
        _commandChanges = false;
        _messageChanges = false;
        PopulateListView();
    }

    private Scope SelectedScope => (Scope)ScopeTabControl.SelectedIndex;

    private void PopulateListView()
    {
        var scope = SelectedScope;
        var filtered = _allItems.Where(i => i.Scope == scope).ToList();
        CommandsListView.ItemsSource = filtered;

        ValueBox.Text = "";
        ValueBox.IsEnabled = false;
        ConflictLabel.Text = "";

        Speak?.Invoke($"{scope} hotkeys tab, {filtered.Count} commands");
    }

    private void ScopeTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_allItems.Count > 0)
            PopulateListView();
    }

    private void CommandsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommandsListView.SelectedItem is not CommandDisplayItem item)
        {
            ValueBox.IsEnabled = false;
            ValueBox.Text = "";
            PressKeyLabel.Text = "";
            return;
        }

        ValueBox.Text = item.KeyText;
        ValueBox.IsEnabled = true;
        PressKeyLabel.Text = "Press desired key to change";
    }

    private void ValueBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ignore modifier-only keys
        if (e.Key == Key.Tab || e.Key == Key.System ||
            e.Key == Key.LeftShift || e.Key == Key.RightShift ||
            e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
            e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
            return;

        if (CommandsListView.SelectedItem is not CommandDisplayItem item)
            return;

        e.Handled = true;

        // Convert WPF key to WinForms-compatible key value
        int winFormsKey = (int)WpfKeyConverter.ToWinFormsKeys(e);
        if (winFormsKey == item.KeyValue)
            return;

        if (item.IsCommand)
            _commandChanges = true;
        else
            _messageChanges = true;

        // Delete key clears the binding
        if (e.Key == Key.Delete)
            winFormsKey = 0; // Keys.None

        // Auto-clear conflicting bindings
        if (winFormsKey != 0)
        {
            AutoClearConflicts(winFormsKey, item.Scope, item.OriginalIndex);
        }

        item.KeyValue = winFormsKey;
        item.KeyText = FormatKey?.Invoke(winFormsKey) ?? winFormsKey.ToString();
        ValueBox.Text = item.KeyText;

        // Refresh the list to show updated key text
        var source = CommandsListView.ItemsSource;
        CommandsListView.ItemsSource = null;
        CommandsListView.ItemsSource = source;
        CommandsListView.SelectedItem = item;

        if (string.IsNullOrEmpty(ConflictLabel.Text))
            Speak?.Invoke($"{item.KeyText} assigned to {item.HelpText}");

        CommandsListView.Focus();
    }

    private void AutoClearConflicts(int keyValue, Scope scope, int excludeIndex)
    {
        foreach (var other in _allItems)
        {
            if (other.OriginalIndex == excludeIndex) continue;
            if (other.KeyValue != keyValue) continue;

            bool isConflict = (scope == other.Scope) ||
                              (scope == Scope.Global) ||
                              (other.Scope == Scope.Global);
            if (!isConflict) continue;

            string oldKeyText = other.KeyText;
            string oldCmd = other.HelpText;
            other.KeyValue = 0;
            other.KeyText = FormatKey?.Invoke(0) ?? "";

            ConflictLabel.Text = $"Cleared {oldKeyText} from {oldCmd}";
            Speak?.Invoke($"Cleared {oldKeyText} from {oldCmd}");
            _commandChanges = true;
        }
    }

    private bool HasAnyConflicts()
    {
        for (int i = 0; i < _allItems.Count; i++)
        {
            if (_allItems[i].KeyValue == 0) continue;
            for (int j = i + 1; j < _allItems.Count; j++)
            {
                if (_allItems[j].KeyValue != _allItems[i].KeyValue) continue;
                var s1 = _allItems[i].Scope;
                var s2 = _allItems[j].Scope;
                if (s1 == s2) return true;
                if (s1 == Scope.Global || s2 == Scope.Global) return true;
            }
        }
        return false;
    }

    private void ValueBox_GotFocus(object sender, RoutedEventArgs e)
    {
        PressKeyLabel.Text = "Press desired key to change";
    }

    private void ValueBox_LostFocus(object sender, RoutedEventArgs e)
    {
        PressKeyLabel.Text = "";
    }

    private void OKButton_Click(object sender, RoutedEventArgs e)
    {
        if (_commandChanges || _messageChanges)
        {
            if (HasAnyConflicts())
            {
                MessageBox.Show(
                    "There are conflicting key assignments. Please resolve them before saving.",
                    "Duplicate key Definitions", MessageBoxButton.OK, MessageBoxImage.Warning);
                Speak?.Invoke("Cannot save. Conflicting key assignments exist.");
                return;
            }

            var changes = _allItems.Select(i => (i.CommandId, i.KeyValue)).ToList();

            if (_commandChanges)
                SaveChanges?.Invoke(changes);
            if (_messageChanges)
                UpdateCWText?.Invoke(changes);
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommandsListView.SelectedItem is not CommandDisplayItem item) return;

        var defaultKey = GetDefaultKey?.Invoke(item.CommandId);
        if (defaultKey.HasValue)
        {
            item.KeyValue = defaultKey.Value;
            item.KeyText = FormatKey?.Invoke(defaultKey.Value) ?? "";
            ValueBox.Text = item.KeyText;
            _commandChanges = true;

            // Refresh
            var source = CommandsListView.ItemsSource;
            CommandsListView.ItemsSource = null;
            CommandsListView.ItemsSource = source;
            CommandsListView.SelectedItem = item;

            Speak?.Invoke($"Reset to {item.KeyText}");
        }
    }

    private void ResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        var scope = SelectedScope;
        var filtered = _allItems.Where(i => i.Scope == scope);
        foreach (var item in filtered)
        {
            var defaultKey = GetDefaultKey?.Invoke(item.CommandId);
            if (defaultKey.HasValue)
            {
                item.KeyValue = defaultKey.Value;
                item.KeyText = FormatKey?.Invoke(defaultKey.Value) ?? "";
            }
        }
        _commandChanges = true;
        PopulateListView();
        Speak?.Invoke($"All {scope} keys reset to defaults");
    }
}
