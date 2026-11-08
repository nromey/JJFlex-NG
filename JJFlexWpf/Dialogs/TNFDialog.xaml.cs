using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
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
        // Delete on the list presses this button (#774).
        DeleteKeyRemoves.SetButton(TNFList, RemoveButton);
        // The list's name carries the count; it is brought up to date on the
        // way in, where a rename cannot be heard as a second announcement,
        // and a name owed while the list itself held focus is paid the
        // moment that focus moves on (#941 repair one).
        TNFList.GotKeyboardFocus += (_, _) =>
        {
            _namingOnFocus = true;
            try { NameTheList(); }
            finally { _namingOnFocus = false; }
        };
        TNFList.LostKeyboardFocus += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, TNFList) || !_nameOwed) return;
            _nameOwed = false;
            AutomationProperties.SetName(TNFList, ListName(TNFList.Items.Count));
        };
        Loaded += TNFDialog_Loaded;
    }

    /// <summary>
    /// The list says what it holds and how many (#941): "Tracking notches by
    /// frequency, 3 on this radio", or "none on this radio". Said as "on this
    /// radio", never "3 of 5", so it cannot be heard as a list position (the
    /// meters panel's lesson, #946). DRAFT wording.
    /// </summary>
    internal static string ListName(int count) => count <= 0
        ? Radios.Lexicon.Get("settings.tnf.list_name_none")
        : Radios.Lexicon.Get("settings.tnf.list_name_count", ("count", count));

    /// <summary>
    /// Keep the list's name true after every rebuild (#941 repair one, Codex
    /// B1). Until then the list was not renamed at all while focus was
    /// anywhere inside it, so Add or Delete from the list left the old count
    /// in its name until focus next came in from outside.
    /// </summary>
    /// <remarks>
    /// <para>A screen reader speaks a rename of the object it is focused ON,
    /// and that is the one case held back: the list element itself has
    /// keyboard focus while it has rows, a passing state that Add and Remove
    /// end by putting focus on a row. The name is then owed, and paid when
    /// the list's own focus moves (to its row, or away). When focus is on a
    /// row, the list is the row's container and the rename is made at once:
    /// the reader is on the row, not the list.</para>
    /// <para>An EMPTY list holding focus is renamed at once: "none on this
    /// radio" is the news after the last notch goes, said once.</para>
    /// </remarks>
    private void NameTheList()
    {
        if (!RenameNow(TNFList.IsKeyboardFocused, _namingOnFocus, TNFList.Items.Count))
        {
            _nameOwed = true;
            return;
        }
        _nameOwed = false;
        AutomationProperties.SetName(TNFList, ListName(TNFList.Items.Count));
    }

    /// <summary>
    /// Whether the list may be renamed now: always, except while the list
    /// element itself (not one of its rows) holds keyboard focus with rows in
    /// it, outside the way in.
    /// </summary>
    internal static bool RenameNow(bool listItselfFocused, bool namingOnFocus, int rows)
        => !listItselfFocused || namingOnFocus || rows == 0;

    private bool _namingOnFocus;
    private bool _nameOwed;

    /// <summary>
    /// Put focus on a row, so the reader lands on it and the list itself does
    /// not keep focus with a name owed; the list when there is no row.
    /// </summary>
    private void FocusRow(int index)
    {
        if (index >= 0 && index < TNFList.Items.Count)
        {
            TNFList.UpdateLayout();
            if (TNFList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem row && row.Focus())
                return;
        }
        TNFList.Focus();
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
                // Empty as well as null: the radio layer returns an empty
                // string for a notch that vanished between the count and this
                // read, and a blank row is a row a screen reader skips over
                // silently — the operator would arrow past a notch that is
                // really there. Name it by position instead. Spelled out
                // rather than "TNF", which reads as three letters.
                string display = GetTNFFrequencyDisplay?.Invoke(i) ?? "";
                if (string.IsNullOrWhiteSpace(display))
                    display = Radios.Lexicon.Get("audio.tnf.unnamed", ("n", (i + 1).ToString()));
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
        NameTheList();
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
            PermanentCheck.IsEnabled = hasSelection;
            RemoveButton.IsEnabled = hasSelection;

            if (hasSelection)
            {
                WidthBox.Text = (GetTNFWidth?.Invoke(idx) ?? 0).ToString();
                DepthBox.Text = (GetTNFDepth?.Invoke(idx) ?? 1).ToString();
                PermanentCheck.IsChecked = GetTNFPermanent?.Invoke(idx) ?? false;
            }
            else
            {
                WidthBox.Text = "";
                DepthBox.Text = "";
                PermanentCheck.IsChecked = false;
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
        // Through the one width formatter, as every filter width is (#983):
        // "2.75 k" at Terse from 1 kHz up, the whole number otherwise.
        SpeakValue(WidthLabel, Radios.HertzSpeech.Width(newVal, Radios.HertzUnitStyle.Bare));
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

    /// <summary>
    /// The keep-it checkbox. The write goes straight to the radio, so the
    /// toggle answers with the on or off tone like every other checkbox that
    /// acts at once; the screen reader says checked or not checked itself.
    /// </summary>
    private void PermanentCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        int idx = TNFList.SelectedIndex;
        if (idx < 0) return;
        bool permanent = PermanentCheck.IsChecked == true;
        SetTNFPermanent?.Invoke(idx, permanent);
        EarconPlayer.ToggleTone(permanent);
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
        if (display == null)
        {
            TNFList.Focus();
            return;
        }

        // Rebuilt and renamed BEFORE focus goes in, so the way in hears the
        // new count (#941 repair one); focus then lands on the new row.
        RefreshList();
        if (TNFList.Items.Count > 0)
            TNFList.SelectedIndex = TNFList.Items.Count - 1;
        FocusRow(TNFList.SelectedIndex);
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
        FocusRow(TNFList.SelectedIndex);
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    #endregion
}
