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

    /// <summary>
    /// How far one arrow press moves a notch (#773), in Hz: Up and Down move it
    /// <see cref="FrequencyCoarseStepHz"/>, Shift with them
    /// <see cref="FrequencyFineStepHz"/>. Named constants rather than the
    /// operator's tuning steps: the launcher cannot reach those without a
    /// signature change in another lane's files, and a tuning step of 5 kHz
    /// would jump a notch straight across a sideband passband.
    /// </summary>
    internal const int FrequencyCoarseStepHz = 100;
    internal const int FrequencyFineStepHz = 10;

    private const string WidthLabel = "Notch width";
    private const string DepthLabel = "Notch depth";

    private bool _suppressEvents;

    #region Delegates

    /// <summary>Gets the number of TNFs.</summary>
    public Func<int>? GetTNFCount { get; set; }

    /// <summary>Gets the formatted frequency display for TNF at index.</summary>
    public Func<int, string>? GetTNFFrequencyDisplay { get; set; }

    /// <summary>Gets the frequency (Hz) of TNF at index; 0 when it is gone (#773).</summary>
    public Func<int, ulong>? GetTNFFrequencyHz { get; set; }

    /// <summary>
    /// Moves TNF at index to a frequency (Hz) and returns the frequency asked
    /// for after the radio layer's clamp, or 0 when the notch is gone (#773).
    /// </summary>
    public Func<int, ulong, ulong>? SetTNFFrequencyHz { get; set; }

    /// <summary>Formats a frequency (Hz) the way every other frequency in the app is (#773).</summary>
    public Func<ulong, string>? FormatTNFFrequency { get; set; }

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
        if (!ListNaming.RenameNow(TNFList.IsKeyboardFocused, _namingOnFocus, TNFList.Items.Count))
        {
            _nameOwed = true;
            return;
        }
        _nameOwed = false;
        AutomationProperties.SetName(TNFList, ListName(TNFList.Items.Count));
    }

    private bool _namingOnFocus;
    private bool _nameOwed;

    /// <summary>Focus a row (<see cref="ListNaming.FocusRow"/>).</summary>
    private void FocusRow(int index) => ListNaming.FocusRow(TNFList, index);

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
            FrequencyBox.IsEnabled = hasSelection;
            WidthBox.IsEnabled = hasSelection;
            DepthBox.IsEnabled = hasSelection;
            PermanentCheck.IsEnabled = hasSelection;
            RemoveButton.IsEnabled = hasSelection;

            // A new selection starts from the radio's own frequency again.
            _freqAskedIndex = -1;
            if (hasSelection)
            {
                ShowFrequency(GetTNFFrequencyHz?.Invoke(idx) ?? 0);
                ShowWidth(GetTNFWidth?.Invoke(idx) ?? 0);
                ShowDepth(GetTNFDepth?.Invoke(idx) ?? 1);
                PermanentCheck.IsChecked = GetTNFPermanent?.Invoke(idx) ?? false;
            }
            else
            {
                FrequencyBox.Text = "";
                _freqShown = "";
                WidthBox.Text = "";
                _widthShown = null;
                DepthBox.Text = "";
                _depthShown = null;
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

    // ---- #773: moving the selected notch ----

    /// <summary>The text the frequency box was last given; a Tab-through that leaves it as it was writes nothing.</summary>
    private string _freqShown = "";

    /// <summary>The notch the last move was asked for, and where; steps build on the ask, not on a read-back that may not have caught up.</summary>
    private int _freqAskedIndex = -1;
    private ulong _freqAskedHz;

    private string FormatFrequency(ulong hz) => FormatTNFFrequency?.Invoke(hz) ?? hz.ToString();

    private void ShowFrequency(ulong hz)
    {
        _freqShown = hz == 0 ? "" : FormatFrequency(hz);
        FrequencyBox.Text = _freqShown;
    }

    /// <summary>Where the notch is, as far as this dialog knows: the last move asked for, else the radio's report.</summary>
    private ulong CurrentFrequencyHz(int idx) =>
        _freqAskedIndex == idx ? _freqAskedHz : GetTNFFrequencyHz?.Invoke(idx) ?? 0;

    private void FrequencyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int idx = TNFList.SelectedIndex;
        if (idx < 0) return;
        bool fine = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ApplyTypedFrequency(idx);
                break;
            case Key.Up:
                e.Handled = true;
                StepFrequency(idx, fine ? FrequencyFineStepHz : FrequencyCoarseStepHz);
                break;
            case Key.Down:
                e.Handled = true;
                StepFrequency(idx, -(fine ? FrequencyFineStepHz : FrequencyCoarseStepHz));
                break;
        }
    }

    /// <summary>
    /// Leaving the box writes only a typed change. A notch is radio-wide, so a
    /// Tab-through that wrote the shown frequency back could move it for every
    /// client by a formatting round trip; the shown text is not even a number
    /// the typed-entry parser reads.
    /// </summary>
    private void FrequencyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        int idx = TNFList.SelectedIndex;
        if (idx >= 0 && FrequencyBox.Text != _freqShown) ApplyTypedFrequency(idx);
    }

    /// <summary>One arrow press: move from where the notch is, and say where it now is.</summary>
    internal void StepFrequency(int idx, int deltaHz)
    {
        ulong current = CurrentFrequencyHz(idx);
        if (current == 0) return;
        long wanted = (long)current + deltaHz;
        MoveTo(idx, wanted < 0 ? 0UL : (ulong)wanted);
    }

    /// <summary>
    /// The typed frequency, read by <see cref="ParseFrequencyHz"/>: the shown
    /// form edited in place, or anything the tuning entry's parser reads.
    /// Unchanged text writes nothing.
    /// </summary>
    internal void ApplyTypedFrequency(int idx)
    {
        string typed = FrequencyBox.Text.Trim();
        if (typed == _freqShown.Trim()) return;
        ulong? hz = ParseFrequencyHz(typed);
        if (hz == null)
        {
            EarconPlayer.LeaderInvalidTone();
            // A ladder: Noel's chatty and terse sentences (batch 14 words),
            // picked by the operator's verbosity.
            Speak(Radios.Lexicon.Get("audio.tnf.frequency_invalid", Radios.ScreenReaderOutput.CurrentVerbosity));
            ShowFrequency(CurrentFrequencyHz(idx));
            return;
        }
        if (!FrequencyInRange(hz.Value))
        {
            // #1189 item 2: a number that was read but lies outside where a
            // notch can go is refused here, before MoveTo, rather than moved
            // silently to the edge, which put the notch somewhere other than
            // where it was typed. The radio layer's clamp stays as a
            // backstop. The arrows (StepFrequency) still stop at the edge.
            EarconPlayer.LeaderInvalidTone();
            // DRAFT wording, settings.tnf.frequency_out_of_range.
            Speak(Radios.Lexicon.Get("settings.tnf.frequency_out_of_range"));
            ShowFrequency(CurrentFrequencyHz(idx));
            return;
        }
        MoveTo(idx, hz.Value);
    }

    /// <summary>
    /// Whether a notch can be put at this frequency: the radio layer's own
    /// range, <see cref="Radios.FlexBase.TNFFrequencyMinHz"/> through
    /// <see cref="Radios.FlexBase.TNFFrequencyMaxHz"/>, both included. Pure.
    /// </summary>
    internal static bool FrequencyInRange(ulong hz) =>
        hz >= Radios.FlexBase.TNFFrequencyMinHz && hz <= Radios.FlexBase.TNFFrequencyMaxHz;

    /// <summary>
    /// What the Frequency box's text says, in Hz, or null (batch 14 repair 3,
    /// Codex's B1 on #773). Every form the box shows reads back as the same
    /// frequency, so an in-place edit of the shown value works.
    /// </summary>
    /// <remarks>
    /// <para><b>The shown form.</b> The box shows the app's own frequency
    /// format, megahertz, a point, three kilohertz digits, a point, three hertz
    /// digits ("14.201.000", <c>globals.FormatFreqUlong</c>). The tuning
    /// entry's parser reads a second point as nothing at all, so an edit of
    /// that text was refused. Two points are therefore read here, exactly, in
    /// whole hertz: megahertz, then kilohertz, then the rest read as a decimal
    /// fraction of a kilohertz, so "14.201.1" is 14,201,100 Hz, as
    /// "14.201.100" is.</para>
    /// <para><b>Everything else</b> goes to the tuning entry's own parser,
    /// <see cref="FreqOutHandlers.ParseQuickTypeFreq"/>: one point is
    /// megahertz, no point kilohertz, and a comma groups kilohertz.</para>
    /// </remarks>
    internal static ulong? ParseFrequencyHz(string text)
    {
        text = text.Trim();
        string[] parts = text.Split('.');
        if (parts.Length == 3)
        {
            if (parts[0].Length == 0 || parts[1].Length != 3 || parts[2].Length == 0 || parts[2].Length > 3) return null;
            foreach (string part in parts)
                foreach (char c in part)
                    if (c < '0' || c > '9') return null;
            if (!ulong.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong mhz)
                || mhz > 999) return null;
            ulong khz = ulong.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            ulong rest = ulong.Parse(parts[2].PadRight(3, '0'), System.Globalization.CultureInfo.InvariantCulture);
            return mhz * 1_000_000UL + khz * 1_000UL + rest;
        }
        double? typedMhz = FreqOutHandlers.ParseQuickTypeFreq(text);
        return typedMhz == null ? null : (ulong)Math.Round(typedMhz.Value * 1_000_000.0);
    }

    private void MoveTo(int idx, ulong hz)
    {
        ulong asked = SetTNFFrequencyHz?.Invoke(idx, hz) ?? 0;
        if (asked == 0) return;
        _freqAskedIndex = idx;
        _freqAskedHz = asked;
        ShowFrequency(asked);
        // The row is named by its frequency; keep it true. Focus is in the
        // box, not the list, so the rename is not announced.
        _suppressEvents = true;
        try
        {
            if (idx < TNFList.Items.Count)
            {
                TNFList.Items[idx] = _freqShown;
                TNFList.SelectedIndex = idx;
            }
        }
        finally { _suppressEvents = false; }
        SpeakValue(Radios.Lexicon.Get("audio.tnf.frequency_label"), _freqShown);
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
    /// The width the box was last given (#1198): the radio's on selection, or
    /// the last one asked for. A commit that leaves it unchanged writes
    /// nothing. Compared with this and never with <see cref="GetTNFWidth"/>,
    /// whose read-back lags a write (see <see cref="ApplyWidth"/>). Null with
    /// nothing selected.
    /// </summary>
    private int? _widthShown;

    private void ShowWidth(int hz)
    {
        _widthShown = hz;
        WidthBox.Text = hz.ToString();
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
    ///
    /// <para><b>Writes only a change</b> (#1198): a width equal to the one last
    /// shown is not sent, so a Tab through the box, or an Enter on a value
    /// the arrows already sent, writes nothing to the radio. Text that is not
    /// a number puts back the width last shown, and writes nothing.</para>
    /// </summary>
    private void ApplyWidth(int idx)
    {
        int val = int.TryParse(WidthBox.Text, out int typed)
            ? Math.Max(WidthMin, Math.Min(WidthMax, typed))
            : _widthShown ?? GetTNFWidth?.Invoke(idx) ?? WidthMin;
        bool changed = val != _widthShown;
        ShowWidth(val);
        if (changed) SetTNFWidth?.Invoke(idx, val);
    }

    private void AdjustWidth(int idx, int delta)
    {
        int current = GetTNFWidth?.Invoke(idx) ?? WidthMin;
        int newVal = Math.Max(WidthMin, Math.Min(WidthMax, current + delta));
        SetTNFWidth?.Invoke(idx, newVal);
        ShowWidth(newVal);   // what the next commit compares with (#1198)
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

    /// <summary>
    /// The depth the box was last given (#1198), as <see cref="_widthShown"/>
    /// is for the width: the radio's on selection, or the last one asked for.
    /// A commit that leaves it unchanged writes nothing. Null with nothing
    /// selected.
    /// </summary>
    private int? _depthShown;

    private void ShowDepth(int depth)
    {
        _depthShown = depth;
        DepthBox.Text = depth.ToString();
    }

    /// <summary>
    /// Commit the typed depth. Shows the request, for the reason in
    /// <see cref="ApplyWidth"/>, and, as the width does, writes only a change
    /// (#1198): a Tab through the box, or an Enter on a value the arrows
    /// already sent, writes nothing to the radio. Text that is not a number
    /// puts back the depth last shown, and writes nothing.
    /// </summary>
    private void ApplyDepth(int idx)
    {
        int val = int.TryParse(DepthBox.Text, out int typed)
            ? Math.Max(DepthMin, Math.Min(DepthMax, typed))
            : _depthShown ?? GetTNFDepth?.Invoke(idx) ?? DepthMin;
        bool changed = val != _depthShown;
        ShowDepth(val);
        if (changed) SetTNFDepth?.Invoke(idx, val);
    }

    private void AdjustDepth(int idx, int delta)
    {
        int current = GetTNFDepth?.Invoke(idx) ?? DepthMin;
        int newVal = Math.Max(DepthMin, Math.Min(DepthMax, current + delta));
        SetTNFDepth?.Invoke(idx, newVal);
        ShowDepth(newVal);   // what the next commit compares with (#1198)
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
