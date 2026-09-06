using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// Tracking Notch Filter management: list the notches, add and remove them,
/// adjust width, depth and whether the radio keeps them.
///
/// <para>Replaced Jim's WinForms <c>Radios/FlexTNF.cs</c> in Sprint 9 Track B;
/// that file was deleted in <c>074b2c78</c>. Nothing ever constructed this
/// dialog, so between the two commits the tracking notch filter left the
/// product while staying fully wired to the radio (#482). <see
/// cref="TNFLauncher"/> is the missing end of the contract.</para>
///
/// <para>All TNF operations go through delegates in plain ints, strings and
/// bools — no direct <c>FlexBase</c> or FlexLib <c>TNF</c> reference — which is
/// why the limits below are restated here rather than read from the radio
/// layer. <c>FlexBase.TNFWidthMinHz</c> and friends are the authority and these
/// must agree with them; <c>Radios.Tests.TNFLimitAgreementTests</c> refuses the
/// build if they drift.</para>
/// </summary>
public partial class TNFDialog : JJFlexDialog
{
    private const int WidthIncrement = 50;
    private const int WidthMin = 50;
    private const int WidthMax = 5000;
    private const int DepthMin = 1;
    private const int DepthMax = 3;

    private const string WidthLabel = "Notch width";
    private const string DepthLabel = "Notch depth";

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
        Title = Radios.Lexicon.Get("audio.tnf.title");

        RefreshList();
        if (TNFList.Items.Count > 0)
            TNFList.SelectedIndex = 0;
        TNFList.Focus();

        // An empty list reads as nothing at all. The screen reader announces
        // the dialog, then a list box with no items, and stops — which is
        // indistinguishable from a dialog that failed to populate. Say that
        // it is empty ON PURPOSE, and say what to press.
        if (TNFList.Items.Count == 0)
            Speak(Radios.Lexicon.Get("audio.tnf.empty"));
    }

    /// <summary>
    /// One keyed subject for the list's own state, shared with
    /// <see cref="TNFLauncher"/> so that "Notch removed" retires an unheard
    /// "No tracking notches" and neither expires on its word count (#503).
    /// </summary>
    private static void Speak(string message) =>
        Radios.ScreenReaderOutput.Speak(
            message,
            Radios.Speech.SpeechIntent.Queue,
            Radios.VerbosityLevel.Terse,
            subject: Radios.Speech.SpeechSubject.TrackingNotch);

    /// <summary>
    /// Say a value the operator just changed with an arrow key.
    ///
    /// <para>Setting <c>TextBox.Text</c> from code does not reliably reach a
    /// screen reader — the box has focus, but nothing was typed into it and no
    /// caret moved, so on the arrow keys the operator's own keypress is the
    /// only evidence anything happened. Keyed per field, so holding Up sweeps
    /// without a queue building behind it.</para>
    /// </summary>
    private static void SpeakValue(string label, string value) =>
        Radios.ScreenReaderOutput.Speak(
            Radios.Lexicon.Get("audio.field.display",
                ("label", label), ("value", value), ("unit", "")),
            Radios.Speech.SpeechIntent.Latest,
            Radios.VerbosityLevel.Terse,
            subject: Radios.Speech.SpeechSubject.ValueField(label));

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
            // Disabled, not merely inert. A WPF control that is IsEnabled=false
            // leaves the tab order, so an operator arrowing through the dialog
            // never lands on a box that cannot act — which is the difference
            // between "there is nothing selected" and "this control is broken",
            // and without sight those two sound identical.
            WidthBox.IsEnabled = hasSelection;
            DepthBox.IsEnabled = hasSelection;
            PermanentCombo.IsEnabled = hasSelection;
            RemoveButton.IsEnabled = hasSelection;

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

    /// <summary>
    /// Commit whatever is typed in the width box.
    ///
    /// <para><b>Shows what was ASKED FOR, not what the radio reads back.</b>
    /// The write is queued onto the radio's command thread, so re-reading the
    /// width on the next line returns the value from BEFORE the write almost
    /// every time. The original did exactly that: type 200, press Enter, watch
    /// the box snap back to the old number as though the entry had been
    /// rejected. Displaying the clamped request is both honest and stable —
    /// the clamp is the same one <c>FlexBase</c> applies, so the two agree.</para>
    /// </summary>
    private void ApplyWidth(int idx)
    {
        int val = int.TryParse(WidthBox.Text, out int typed)
            ? Math.Max(WidthMin, Math.Min(WidthMax, typed))
            : GetTNFWidth?.Invoke(idx) ?? WidthMin;
        SetTNFWidth?.Invoke(idx, val);
        WidthBox.Text = val.ToString();
    }

    private void AdjustWidth(int idx, int delta)
    {
        int current = GetTNFWidth?.Invoke(idx) ?? WidthMin;
        int newVal = Math.Max(WidthMin, Math.Min(WidthMax, current + delta));
        SetTNFWidth?.Invoke(idx, newVal);
        WidthBox.Text = newVal.ToString();
        SpeakValue(WidthLabel, newVal.ToString());
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

    /// <summary>Commit the typed depth. Shows the request, for the reason in <see cref="ApplyWidth"/>.</summary>
    private void ApplyDepth(int idx)
    {
        int val = int.TryParse(DepthBox.Text, out int typed)
            ? Math.Max(DepthMin, Math.Min(DepthMax, typed))
            : GetTNFDepth?.Invoke(idx) ?? DepthMin;
        SetTNFDepth?.Invoke(idx, val);
        DepthBox.Text = val.ToString();
    }

    private void AdjustDepth(int idx, int delta)
    {
        int current = GetTNFDepth?.Invoke(idx) ?? DepthMin;
        int newVal = Math.Max(DepthMin, Math.Min(DepthMax, current + delta));
        SetTNFDepth?.Invoke(idx, newVal);
        DepthBox.Text = newVal.ToString();
        SpeakValue(DepthLabel, newVal.ToString());
    }

    private void PermanentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        int idx = TNFList.SelectedIndex;
        if (idx < 0) return;
        bool permanent = PermanentCombo.SelectedIndex == 1;
        SetTNFPermanent?.Invoke(idx, permanent);
    }

    /// <summary>
    /// Ask the radio for a notch at the receive frequency.
    ///
    /// <para>Rebuilds from the radio rather than appending the returned string.
    /// Every other delegate here addresses a notch BY INDEX, so the list on
    /// screen and the list on the radio have to be the same list — and on a
    /// MultiFlex radio they can differ for reasons that have nothing to do with
    /// this operator. Appending assumes they cannot.</para>
    ///
    /// <para>The refusal is spoken by <see cref="TNFLauncher"/>, which is the
    /// only place that knows why the radio declined.</para>
    /// </summary>
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        string? display = AddTNF?.Invoke();
        TNFList.Focus();
        if (display == null) return;

        RefreshList();
        if (TNFList.Items.Count > 0)
            TNFList.SelectedIndex = TNFList.Items.Count - 1;
    }

    /// <summary>
    /// Remove the selected notch, then land on a neighbour rather than on
    /// nothing — an operator clearing three notches in a row should not have to
    /// re-enter the list between each one. The removal itself is announced by
    /// <see cref="TNFLauncher"/>, including when the radio refuses.
    /// </summary>
    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        int idx = TNFList.SelectedIndex;
        if (idx < 0) return;

        if (RemoveTNF?.Invoke(idx) != true)
        {
            TNFList.Focus();
            return;
        }

        RefreshList();
        if (TNFList.Items.Count > 0)
            TNFList.SelectedIndex = Math.Min(idx, TNFList.Items.Count - 1);
        else
            UpdateTNFProperties();
        TNFList.Focus();
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    #endregion
}
