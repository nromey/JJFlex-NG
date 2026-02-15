using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for Radios\FlexMemories.cs.
/// Memory management dialog: sorted memory list, add/delete memories,
/// edit frequency/mode/filters/tone/squelch/offset/name/owner/group.
/// Mode-dependent visibility for FM-specific controls (tone, squelch, offset).
///
/// All rig communication uses delegates — no direct FlexLib references.
/// The caller wires up delegates that map to FlexBase/Memory operations.
///
/// Sprint 9 Track B.
/// </summary>
public partial class MemoriesDialog : JJFlexDialog
{
    #region Constants

    private const int FilterMin = -12000;
    private const int FilterMax = 12000;
    private const int FilterIncrement = 50;
    private const int SquelchMin = 0;
    private const int SquelchMax = 100;
    private const int SquelchIncrement = 5;

    #endregion

    #region Display Item

    /// <summary>
    /// Represents a memory in the list. Wraps an opaque memory reference.
    /// </summary>
    public class MemoryDisplayItem
    {
        public string FullName { get; set; } = "";
        public object MemoryRef { get; set; } = null!;
        public override string ToString() => FullName;
    }

    #endregion

    #region Rig Delegates

    /// <summary>Gets the sorted list of memory display items.</summary>
    public Func<List<MemoryDisplayItem>>? GetSortedMemories { get; set; }

    /// <summary>Requests a new memory from the radio. Returns the new item, or null.</summary>
    public Func<MemoryDisplayItem?>? AddMemory { get; set; }

    /// <summary>Removes the specified memory from the radio.</summary>
    public Action<object>? RemoveMemory { get; set; }

    /// <summary>Tunes the radio to the specified memory.</summary>
    public Action<object>? SelectMemory { get; set; }

    // Per-memory property access. The object parameter is MemoryRef.
    public Func<object, string>? FormatFrequency { get; set; }
    public Action<object, string>? SetFrequencyFromText { get; set; }

    public Func<object, string>? GetMode { get; set; }
    public Action<object, string>? SetMode { get; set; }

    public Func<object, int>? GetFilterLow { get; set; }
    public Action<object, int>? SetFilterLow { get; set; }
    public Func<object, int>? GetFilterHigh { get; set; }
    public Action<object, int>? SetFilterHigh { get; set; }

    public Func<object, string>? GetToneMode { get; set; }
    public Action<object, string>? SetToneMode { get; set; }
    public Func<object, string>? GetToneFrequency { get; set; }
    public Action<object, string>? SetToneFrequency { get; set; }

    public Func<object, bool>? GetSquelchOn { get; set; }
    public Action<object, bool>? SetSquelchOn { get; set; }
    public Func<object, int>? GetSquelchLevel { get; set; }
    public Action<object, int>? SetSquelchLevel { get; set; }

    public Func<object, string>? GetOffsetDirection { get; set; }
    public Action<object, string>? SetOffsetDirection { get; set; }
    public Func<object, int>? GetOffsetKHz { get; set; }
    public Action<object, int>? SetOffsetKHz { get; set; }

    public Func<object, string>? GetName { get; set; }
    public Action<object, string>? SetName { get; set; }
    public Func<object, string>? GetOwner { get; set; }
    public Action<object, string>? SetOwner { get; set; }
    public Func<object, string>? GetGroup { get; set; }
    public Action<object, string>? SetGroup { get; set; }

    /// <summary>Check for duplicate memory name. Returns true if name already exists.</summary>
    public Func<object, string, bool>? IsDuplicateName { get; set; }

    #endregion

    #region Combo Source Delegates

    /// <summary>List of mode strings (e.g., "lsb", "usb", "cw", "am", "fm").</summary>
    public List<string> ModeList { get; set; } = new();

    /// <summary>List of tone mode display strings.</summary>
    public List<string> ToneModeList { get; set; } = new();

    /// <summary>List of tone frequency display strings.</summary>
    public List<string> ToneFrequencyList { get; set; } = new();

    /// <summary>Offset direction values (e.g., "off", "minus", "plus").</summary>
    public List<string> OffsetDirectionList { get; set; } = new();

    #endregion

    /// <summary>
    /// Set by the dialog when the user selects a memory via Enter key.
    /// Indicates the caller should show the frequency display after closing.
    /// </summary>
    public bool ShowFreq { get; private set; }

    private List<MemoryDisplayItem> _memories = new();
    private bool _suppressSelection;

    // FM-related modes where tone/squelch/offset controls are visible
    private static readonly HashSet<string> FMModes = new(StringComparer.OrdinalIgnoreCase)
        { "fm" };
    private static readonly HashSet<string> NFMModes = new(StringComparer.OrdinalIgnoreCase)
        { "nfm", "dfm" };

    public MemoriesDialog()
    {
        InitializeComponent();
        // ResizeMode from base is NoResize; override for this larger dialog
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 500;
        MinHeight = 350;

        Loaded += MemoriesDialog_Loaded;
    }

    private void MemoriesDialog_Loaded(object sender, RoutedEventArgs e)
    {
        // Populate combo box sources
        ModeCombo.ItemsSource = ModeList;
        ToneModeCombo.ItemsSource = ToneModeList;
        ToneFreqCombo.ItemsSource = ToneFrequencyList;
        SquelchCombo.Items.Add("Off");
        SquelchCombo.Items.Add("On");
        OffsetDirCombo.ItemsSource = OffsetDirectionList;

        RefreshMemoryList(null);
        if (SelectedMemoryRef != null)
            ShowMemoryDetails(SelectedMemoryRef);

        MemoryList.Focus();
    }

    private object? SelectedMemoryRef
    {
        get
        {
            if (MemoryList.SelectedItem is MemoryDisplayItem item)
                return item.MemoryRef;
            return null;
        }
    }

    #region Memory List Management

    private void RefreshMemoryList(object? selectRef)
    {
        _suppressSelection = true;

        _memories = GetSortedMemories?.Invoke() ?? new();
        MemoryList.ItemsSource = null;
        MemoryList.ItemsSource = _memories;

        if (selectRef != null)
        {
            var item = _memories.FirstOrDefault(m => m.MemoryRef == selectRef);
            if (item != null)
                MemoryList.SelectedItem = item;
        }
        else if (_memories.Count > 0)
        {
            MemoryList.SelectedIndex = 0;
        }

        _suppressSelection = false;
    }

    private void MemoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (SelectedMemoryRef != null)
            ShowMemoryDetails(SelectedMemoryRef);
    }

    private void MemoryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && SelectedMemoryRef != null)
        {
            SelectMemory?.Invoke(SelectedMemoryRef);
            ShowFreq = true;
            DialogResult = true;
            Close();
            e.Handled = true;
        }
    }

    #endregion

    #region Show Memory Details

    private void ShowMemoryDetails(object memRef)
    {
        _suppressSelection = true;

        FreqBox.Text = FormatFrequency?.Invoke(memRef) ?? "";

        var mode = GetMode?.Invoke(memRef) ?? "";
        ModeCombo.SelectedItem = mode;

        FilterLowBox.Text = (GetFilterLow?.Invoke(memRef) ?? 0).ToString();
        FilterHighBox.Text = (GetFilterHigh?.Invoke(memRef) ?? 0).ToString();

        // Tone/squelch/offset
        ToneModeCombo.SelectedItem = GetToneMode?.Invoke(memRef);
        ToneFreqCombo.SelectedItem = GetToneFrequency?.Invoke(memRef);
        SquelchCombo.SelectedIndex = (GetSquelchOn?.Invoke(memRef) ?? false) ? 1 : 0;
        SquelchLevelBox.Text = (GetSquelchLevel?.Invoke(memRef) ?? 0).ToString();
        OffsetDirCombo.SelectedItem = GetOffsetDirection?.Invoke(memRef);
        OffsetBox.Text = (GetOffsetKHz?.Invoke(memRef) ?? 0).ToString();

        // Name/Owner/Group
        NameBox.Text = GetName?.Invoke(memRef) ?? "";
        OwnerBox.Text = GetOwner?.Invoke(memRef) ?? "";
        GroupBox.Text = GetGroup?.Invoke(memRef) ?? "";

        // Mode-dependent visibility
        UpdateModeVisibility(mode);

        _suppressSelection = false;
    }

    private void UpdateModeVisibility(string mode)
    {
        bool showFM = FMModes.Contains(mode);
        bool showSquelch = showFM || NFMModes.Contains(mode);

        FMControlsPanel.Visibility = (showFM || showSquelch) ? Visibility.Visible : Visibility.Collapsed;

        // Within FM panel, tone controls only for full FM mode
        ToneModeCombo.IsEnabled = showFM;
        ToneFreqCombo.IsEnabled = showFM;
        OffsetDirCombo.IsEnabled = showFM;
        OffsetBox.IsEnabled = showFM;
    }

    #endregion

    #region Button Handlers

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var newItem = AddMemory?.Invoke();
        RefreshMemoryList(newItem?.MemoryRef);
        if (newItem != null)
            ShowMemoryDetails(newItem.MemoryRef);
        MemoryList.Focus();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef != null)
        {
            RemoveMemory?.Invoke(SelectedMemoryRef);
            RefreshMemoryList(null);
        }
        MemoryList.Focus();
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    #endregion

    #region Property Change Handlers

    private void FreqBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            FreqBox_LostFocus(sender, e);
            e.Handled = true;
            // Move focus forward
            FreqBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    private void FreqBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef == null) return;
        SetFrequencyFromText?.Invoke(SelectedMemoryRef, FreqBox.Text);
        RefreshMemoryList(SelectedMemoryRef);
    }

    private void ModeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || SelectedMemoryRef == null) return;
        if (ModeCombo.SelectedItem is string mode)
        {
            SetMode?.Invoke(SelectedMemoryRef, mode);
            UpdateModeVisibility(mode);
        }
    }

    private void FilterLowBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef == null) return;
        if (int.TryParse(FilterLowBox.Text, out int val))
        {
            val = Math.Clamp(val, FilterMin, FilterMax);
            FilterLowBox.Text = val.ToString();
            SetFilterLow?.Invoke(SelectedMemoryRef, val);
        }
    }

    private void FilterHighBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef == null) return;
        if (int.TryParse(FilterHighBox.Text, out int val))
        {
            val = Math.Clamp(val, FilterMin, FilterMax);
            FilterHighBox.Text = val.ToString();
            SetFilterHigh?.Invoke(SelectedMemoryRef, val);
        }
    }

    private void ToneModeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || SelectedMemoryRef == null) return;
        if (ToneModeCombo.SelectedItem is string val)
            SetToneMode?.Invoke(SelectedMemoryRef, val);
    }

    private void ToneFreqCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || SelectedMemoryRef == null) return;
        if (ToneFreqCombo.SelectedItem is string val)
            SetToneFrequency?.Invoke(SelectedMemoryRef, val);
    }

    private void SquelchCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || SelectedMemoryRef == null) return;
        SetSquelchOn?.Invoke(SelectedMemoryRef, SquelchCombo.SelectedIndex == 1);
    }

    private void SquelchLevelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef == null) return;
        if (int.TryParse(SquelchLevelBox.Text, out int val))
        {
            val = Math.Clamp(val, SquelchMin, SquelchMax);
            SquelchLevelBox.Text = val.ToString();
            SetSquelchLevel?.Invoke(SelectedMemoryRef, val);
        }
    }

    private void OffsetDirCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || SelectedMemoryRef == null) return;
        if (OffsetDirCombo.SelectedItem is string val)
            SetOffsetDirection?.Invoke(SelectedMemoryRef, val);
    }

    private void OffsetBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef == null) return;
        if (int.TryParse(OffsetBox.Text, out int val))
        {
            SetOffsetKHz?.Invoke(SelectedMemoryRef, val);
        }
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef == null) return;
        string newName = NameBox.Text;
        if (IsDuplicateName?.Invoke(SelectedMemoryRef, newName) == true)
        {
            var result = MessageBox.Show(
                "Warning: Duplicate name.\nDo you want to change it?",
                "Warning", MessageBoxButton.YesNo);
            if (result == MessageBoxResult.Yes)
            {
                NameBox.Focus();
                return;
            }
        }
        SetName?.Invoke(SelectedMemoryRef, newName);
        RefreshMemoryList(SelectedMemoryRef);
    }

    private void OwnerBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef == null) return;
        SetOwner?.Invoke(SelectedMemoryRef, OwnerBox.Text);
    }

    private void GroupBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef == null) return;
        SetGroup?.Invoke(SelectedMemoryRef, GroupBox.Text);
        RefreshMemoryList(SelectedMemoryRef);
    }

    private void TextFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            e.Handled = true;
            if (sender is TextBox tb)
                tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    #endregion

    #region Number Box KeyDown

    private void NumberBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (!int.TryParse(box.Text, out int value)) return;

        int min, max, inc;
        if (box == FilterLowBox || box == FilterHighBox)
        {
            min = FilterMin; max = FilterMax; inc = FilterIncrement;
        }
        else if (box == SquelchLevelBox)
        {
            min = SquelchMin; max = SquelchMax; inc = SquelchIncrement;
        }
        else
        {
            return; // OffsetBox doesn't have arrow-key stepping
        }

        switch (e.Key)
        {
            case Key.Up:
                value = Math.Min(value + inc, max);
                box.Text = value.ToString();
                e.Handled = true;
                break;
            case Key.Down:
                value = Math.Max(value - inc, min);
                box.Text = value.ToString();
                e.Handled = true;
                break;
        }
    }

    #endregion
}
