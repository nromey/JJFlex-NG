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

    /// <summary>
    /// The memory whose values the details panel is showing, or null when the
    /// panel is clear. Kept equal to <see cref="SelectedMemoryRef"/> after every
    /// refresh of the list (<see cref="SyncDetailsToSelection"/>), because the
    /// text boxes write what they show into the SELECTED memory when they lose
    /// focus: a panel left showing one memory while another is selected turns
    /// a Tab through the fields into a silent overwrite (#1062).
    /// </summary>
    private object? _shownMemoryRef;

    // FM-related modes where tone/squelch/offset controls are visible
    private static readonly HashSet<string> FMModes = new(StringComparer.OrdinalIgnoreCase)
        { "fm" };
    private static readonly HashSet<string> NFMModes = new(StringComparer.OrdinalIgnoreCase)
        { "nfm", "dfm" };

    public MemoriesDialog()
    {
        InitializeComponent();
        // Delete on the list presses this button (#774).
        DeleteKeyRemoves.SetButton(MemoryList, DeleteButton);
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

        // Selects the first memory when there is one and shows its details.
        RefreshMemoryList(null);

        // On the selected memory, not the list itself; the list when there
        // are no memories (RefreshMemoryList selects the first row whenever
        // there is one).
        ListNaming.FocusRow(MemoryList, MemoryList.SelectedIndex);
    }

    /// <summary>
    /// Focus starts on the selected memory, as <see cref="MemoriesDialog_Loaded"/>
    /// puts it. The base class moves focus to the first control in tab order
    /// again once the window has rendered, and the Add, Delete and Done
    /// buttons come first, so without this the operator arrived on Add (#996,
    /// as #935 fixed the Command Finder). With no memory selected, the list
    /// itself: Loaded selects the first row whenever there is one, so that
    /// means an empty list, and selecting a row here would show (and
    /// let Enter tune to) a memory the operator did not choose.
    /// </summary>
    protected override void FocusFirstControl() => ListNaming.FocusRow(MemoryList, MemoryList.SelectedIndex);

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

    /// <summary>
    /// Re-reads the memories and selects <paramref name="selectRef"/>, or the
    /// first memory when it is null; then makes the details panel show the
    /// memory that is really selected, or clears it when none is.
    /// </summary>
    /// <remarks>
    /// The selection changes here with <see cref="MemoryList_SelectionChanged"/>
    /// suppressed, so that handler cannot be what shows the details. Until
    /// #1062 each caller had to remember to, and Delete and a failed Add did
    /// not: the panel kept the old memory while the list selected another, and
    /// leaving any text box wrote the old memory's value into the new one.
    /// Doing it here, once, covers every caller, present and future. A refresh
    /// that keeps the same memory selected (after a frequency, name or group
    /// edit) leaves the panel exactly as it is.
    /// </remarks>
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

        SyncDetailsToSelection();
    }

    /// <summary>
    /// The details panel shows the selected memory: re-shown when the
    /// selection is a different memory from the one shown, cleared when
    /// nothing is selected, and left alone when it is the same memory.
    /// </summary>
    private void SyncDetailsToSelection()
    {
        var selected = SelectedMemoryRef;
        if (selected == null)
            ClearMemoryDetails();
        else if (!ReferenceEquals(selected, _shownMemoryRef))
            ShowMemoryDetails(selected);
    }

    private void MemoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (SelectedMemoryRef != null)
            ShowMemoryDetails(SelectedMemoryRef);
        else
            ClearMemoryDetails();
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

        _shownMemoryRef = memRef;
        _suppressSelection = false;
    }

    /// <summary>
    /// Nothing selected (the last memory was deleted): the panel shows no
    /// memory's values, so nothing in it can be written anywhere (#1062).
    /// The handlers already write nothing with no memory selected.
    /// </summary>
    private void ClearMemoryDetails()
    {
        _suppressSelection = true;

        FreqBox.Text = "";
        ModeCombo.SelectedIndex = -1;
        FilterLowBox.Text = "";
        FilterHighBox.Text = "";
        ToneModeCombo.SelectedIndex = -1;
        ToneFreqCombo.SelectedIndex = -1;
        SquelchCombo.SelectedIndex = -1;
        SquelchLevelBox.Text = "";
        OffsetDirCombo.SelectedIndex = -1;
        OffsetBox.Text = "";
        NameBox.Text = "";
        OwnerBox.Text = "";
        GroupBox.Text = "";

        _shownMemoryRef = null;
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

    // After Add and Delete, focus goes to the selected memory's row, as it
    // does when the dialog opens, and to the list itself only when it is
    // empty (#996). The refresh has already shown that memory's details.
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var newItem = AddMemory?.Invoke();
        RefreshMemoryList(newItem?.MemoryRef);
        ListNaming.FocusRow(MemoryList, MemoryList.SelectedIndex);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef != null)
        {
            RemoveMemory?.Invoke(SelectedMemoryRef);
            RefreshMemoryList(null);
        }
        ListNaming.FocusRow(MemoryList, MemoryList.SelectedIndex);
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

    /// <summary>
    /// True when a box's value is what the selected memory already holds,
    /// read back through the memory's own getter: leaving the box then writes
    /// nothing, so tabbing through the details never writes (#1062). False
    /// when there is no getter to read it back with, and the box writes as it
    /// always did. A deliberate edit differs from the memory, so it still
    /// writes exactly as before.
    /// </summary>
    private static bool Holds<T>(Func<object, T>? getter, object memRef, T value) =>
        getter != null && EqualityComparer<T>.Default.Equals(getter(memRef), value);

    private void FreqBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef == null) return;
        if (Holds(FormatFrequency, SelectedMemoryRef, FreqBox.Text)) return;
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
            if (Holds(GetFilterLow, SelectedMemoryRef, val)) return;
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
            if (Holds(GetFilterHigh, SelectedMemoryRef, val)) return;
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
            if (Holds(GetSquelchLevel, SelectedMemoryRef, val)) return;
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
            if (Holds(GetOffsetKHz, SelectedMemoryRef, val)) return;
            SetOffsetKHz?.Invoke(SelectedMemoryRef, val);
        }
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef == null) return;
        string newName = NameBox.Text;
        // Unchanged: no write, and no duplicate-name question either.
        if (Holds(GetName, SelectedMemoryRef, newName)) return;
        if (IsDuplicateName?.Invoke(SelectedMemoryRef, newName) == true)
        {
            // Escape/No now means "go back and change it" (nothing saved) —
            // the old raw message box saved the duplicate on Escape, which is
            // exactly backwards for a muscle-memory dismissal.
            var confirm = new ConfirmActionDialog(
                "Duplicate Name",
                $"Another memory already has the name {newName}.",
                question: "Keep the duplicate name anyway?",
                yesLabel: "_Keep duplicate",
                noLabel: "_Change it");
            if (confirm.ShowDialog() != true)
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
        if (Holds(GetOwner, SelectedMemoryRef, OwnerBox.Text)) return;
        SetOwner?.Invoke(SelectedMemoryRef, OwnerBox.Text);
    }

    private void GroupBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedMemoryRef == null) return;
        if (Holds(GetGroup, SelectedMemoryRef, GroupBox.Text)) return;
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
