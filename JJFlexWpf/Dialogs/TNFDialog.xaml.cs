using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// WPF replacement for FlexTNF (Radios/FlexTNF.cs).
/// Tracking Notch Filter management: list TNFs, add/remove, adjust width/depth/permanent.
///
/// All TNF operations use delegates — no direct FlexBase or TNF references.
///
/// Sprint 9 Track B.
/// </summary>
public partial class TNFDialog : JJFlexDialog
{
    private const int WidthIncrement = 50;
    private const int WidthMin = 50;
    private const int WidthMax = 5000;
    private const int DepthMin = 1;
    private const int DepthMax = 3;

    private bool _suppressEvents;

    #region Delegates

    /// <summary>Gets the number of TNFs.</summary>
    public Func<int>? GetTNFCount { get; set; }

    /// <summary>Gets the formatted frequency display for TNF at index.</summary>
    public Func<int, string>? GetTNFFrequencyDisplay { get; set; }

    /// <summary>Gets the width (Hz) of TNF at index.</summary>
    public Func<int, int>? GetTNFWidth { get; set; }

    /// <summary>Sets the width (Hz) of TNF at index.</summary>
    public Action<int, int>? SetTNFWidth { get; set; }

    /// <summary>Gets the depth (1-3) of TNF at index.</summary>
    public Func<int, int>? GetTNFDepth { get; set; }

    /// <summary>Sets the depth (1-3) of TNF at index.</summary>
    public Action<int, int>? SetTNFDepth { get; set; }

    /// <summary>Gets the permanent flag of TNF at index.</summary>
    public Func<int, bool>? GetTNFPermanent { get; set; }

    /// <summary>Sets the permanent flag of TNF at index.</summary>
    public Action<int, bool>? SetTNFPermanent { get; set; }

    /// <summary>
    /// Adds a TNF at the current VFO frequency.
    /// Returns the formatted frequency display of the new TNF, or null on failure.
    /// </summary>
    public Func<string?>? AddTNF { get; set; }

    /// <summary>
    /// Removes the TNF at the given index.
    /// Returns true on success.
    /// </summary>
    public Func<int, bool>? RemoveTNF { get; set; }

    #endregion

    public TNFDialog()
    {
        InitializeComponent();
        Loaded += TNFDialog_Loaded;
    }

    private void TNFDialog_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshList();
        if (TNFList.Items.Count > 0)
            TNFList.SelectedIndex = 0;
        TNFList.Focus();
    }

    private void RefreshList()
    {
        _suppressEvents = true;
        try
        {
            int selected = TNFList.SelectedIndex;
            TNFList.Items.Clear();
            int count = GetTNFCount?.Invoke() ?? 0;
            for (int i = 0; i < count; i++)
            {
                string display = GetTNFFrequencyDisplay?.Invoke(i) ?? $"TNF {i + 1}";
                TNFList.Items.Add(display);
            }
            if (selected >= 0 && selected < TNFList.Items.Count)
                TNFList.SelectedIndex = selected;
            else if (TNFList.Items.Count > 0)
                TNFList.SelectedIndex = 0;
        }
        finally
        {
            _suppressEvents = false;
        }
        UpdateTNFProperties();
    }

    private void UpdateTNFProperties()
    {
        int idx = TNFList.SelectedIndex;
        bool hasSelection = idx >= 0;

        _suppressEvents = true;
        try
        {
            WidthBox.IsEnabled = hasSelection;
            DepthBox.IsEnabled = hasSelection;
            PermanentCombo.IsEnabled = hasSelection;

            if (hasSelection)
            {
                WidthBox.Text = (GetTNFWidth?.Invoke(idx) ?? 0).ToString();
                DepthBox.Text = (GetTNFDepth?.Invoke(idx) ?? 1).ToString();
                PermanentCombo.SelectedIndex = (GetTNFPermanent?.Invoke(idx) ?? false) ? 1 : 0;
            }
            else
            {
                WidthBox.Text = "";
                DepthBox.Text = "";
                PermanentCombo.SelectedIndex = -1;
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    #region Event Handlers

    private void TNFList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        UpdateTNFProperties();
    }

    private void WidthBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int idx = TNFList.SelectedIndex;
        if (idx < 0) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ApplyWidth(idx);
                break;
            case Key.Up:
                e.Handled = true;
                AdjustWidth(idx, WidthIncrement);
                break;
            case Key.Down:
                e.Handled = true;
                AdjustWidth(idx, -WidthIncrement);
                break;
        }
    }

    private void WidthBox_LostFocus(object sender, RoutedEventArgs e)
    {
        int idx = TNFList.SelectedIndex;
        if (idx >= 0) ApplyWidth(idx);
    }

    private void ApplyWidth(int idx)
    {
        if (int.TryParse(WidthBox.Text, out int val))
        {
            val = Math.Max(WidthMin, Math.Min(WidthMax, val));
            SetTNFWidth?.Invoke(idx, val);
        }
        WidthBox.Text = (GetTNFWidth?.Invoke(idx) ?? 0).ToString();
    }

    private void AdjustWidth(int idx, int delta)
    {
        int current = GetTNFWidth?.Invoke(idx) ?? WidthMin;
        int newVal = Math.Max(WidthMin, Math.Min(WidthMax, current + delta));
        SetTNFWidth?.Invoke(idx, newVal);
        WidthBox.Text = newVal.ToString();
    }

    private void DepthBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int idx = TNFList.SelectedIndex;
        if (idx < 0) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ApplyDepth(idx);
                break;
            case Key.Up:
                e.Handled = true;
                AdjustDepth(idx, 1);
                break;
            case Key.Down:
                e.Handled = true;
                AdjustDepth(idx, -1);
                break;
        }
    }

    private void DepthBox_LostFocus(object sender, RoutedEventArgs e)
    {
        int idx = TNFList.SelectedIndex;
        if (idx >= 0) ApplyDepth(idx);
    }

    private void ApplyDepth(int idx)
    {
        if (int.TryParse(DepthBox.Text, out int val))
        {
            val = Math.Max(DepthMin, Math.Min(DepthMax, val));
            SetTNFDepth?.Invoke(idx, val);
        }
        DepthBox.Text = (GetTNFDepth?.Invoke(idx) ?? 1).ToString();
    }

    private void AdjustDepth(int idx, int delta)
    {
        int current = GetTNFDepth?.Invoke(idx) ?? DepthMin;
        int newVal = Math.Max(DepthMin, Math.Min(DepthMax, current + delta));
        SetTNFDepth?.Invoke(idx, newVal);
        DepthBox.Text = newVal.ToString();
    }

    private void PermanentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        int idx = TNFList.SelectedIndex;
        if (idx < 0) return;
        bool permanent = PermanentCombo.SelectedIndex == 1;
        SetTNFPermanent?.Invoke(idx, permanent);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        string? display = AddTNF?.Invoke();
        if (display != null)
        {
            TNFList.Items.Add(display);
            TNFList.SelectedIndex = TNFList.Items.Count - 1;
            TNFList.Focus();
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        int idx = TNFList.SelectedIndex;
        if (idx < 0) return;

        if (RemoveTNF?.Invoke(idx) == true)
        {
            TNFList.Items.RemoveAt(idx);
            TNFList.SelectedIndex = -1;
            UpdateTNFProperties();
            TNFList.Focus();
        }
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    #endregion
}
