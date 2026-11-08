using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Display mode item for the front panel combo.
    /// </summary>
    public class DisplayModeItem
    {
        public string DisplayText { get; set; } = "";
        public object Value { get; set; } = null!;

        public override string ToString() => DisplayText;
    }

    /// <summary>
    /// Callbacks for the RadioInfo dialog.
    /// </summary>
    public class RadioInfoCallbacks
    {
        /// <summary>Get the current radio model.</summary>
        public required Func<string> GetModel { get; init; }

        /// <summary>Get the firmware version string.</summary>
        public required Func<string> GetVersion { get; init; }

        /// <summary>Get the serial number.</summary>
        public required Func<string> GetSerial { get; init; }

        /// <summary>Get the call sign.</summary>
        public required Func<string> GetCallsign { get; init; }

        /// <summary>Set the call sign.</summary>
        public required Action<string> SetCallsign { get; init; }

        /// <summary>Get the nickname.</summary>
        public required Func<string> GetNickname { get; init; }

        /// <summary>Set the nickname.</summary>
        public required Action<string> SetNickname { get; init; }

        /// <summary>Get the IP address string.</summary>
        public required Func<string> GetIPAddress { get; init; }

        /// <summary>Get the display mode items for the combo.</summary>
        public required Func<List<DisplayModeItem>> GetDisplayModes { get; init; }

        /// <summary>Get the current display mode value.</summary>
        public required Func<object> GetCurrentDisplayMode { get; init; }

        /// <summary>Set the display mode.</summary>
        public required Action<object> SetDisplayMode { get; init; }

        /// <summary>Get the feature availability text.</summary>
        public required Func<string> GetFeatureAvailabilityText { get; init; }

        /// <summary>Refresh the license state.</summary>
        public required Action RefreshLicense { get; init; }
    }

    /// <summary>
    /// Which tab to show initially.
    /// </summary>
    public enum RadioInfoTab
    {
        General = 0,
        FeatureAvailability = 1
    }

    public partial class RadioInfoDialog : JJFlexDialog
    {
        private readonly RadioInfoCallbacks _callbacks;
        private bool _suppressDisplayChange;

        // What each identity box showed when the dialog filled it, or the
        // last edit the radio took. The radio is told a callsign or a name ONLY when the box now
        // differs from this, which is to say the operator edited it here; a
        // value the radio has since corrected underneath is never sent back
        // (#477, repair round 2).
        private string _shownCallsign = "";
        private string _shownNickname = "";

        // After a REFUSED edit (no radio, or the change-nothing hold) the box
        // shows the radio's value again, and so does its baseline (#983,
        // repair one). Leaving the box then sends nothing, so the refusal is
        // not said again on every focus loss; and the box never shows a name
        // or callsign the radio does not have. The first build remembered
        // the refused text instead and left it in the box, which read as
        // though the radio had taken it (Codex B2); the brief allowed either
        // shape, and only this one keeps the box true.

        /// <summary>
        /// The text a commit had refused, or null: the box held an edit, and
        /// the baseline did not move to it.
        /// </summary>
        internal static string? RefusedEdit(string shownBefore, string inBox, string shownAfter)
            => EditedIdentity(shownBefore, inBox) != null && string.Equals(shownAfter ?? "", shownBefore ?? "", StringComparison.Ordinal)
                ? inBox : null;

        /// <summary>
        /// One commit of an identity box, and what the box shows after it:
        /// the edit when the radio took it, and the radio's own value when it
        /// was refused. The radio's value is read through
        /// <paramref name="heldNow"/>; with none (no radio), the last value
        /// shown stays.
        /// </summary>
        internal static (string Shown, string InBox) CommitAndShow(string shown, string inBox,
            Action<string> send, Func<string> heldNow, Func<string, string> asTheRadioKeepsIt)
        {
            string after = CommitIdentity(shown, inBox, send, heldNow, asTheRadioKeepsIt);
            if (RefusedEdit(shown, inBox, after) == null) return (after, inBox);
            string radio = heldNow() ?? "";
            if (radio.Length == 0) radio = shown ?? "";
            return (radio, radio);
        }

        /// <summary>
        /// The text to send for an identity box, or null when the operator has
        /// not changed it since it was shown. Compared with what the dialog
        /// SHOWED, never with the live value, which can move underneath.
        /// </summary>
        internal static string? EditedIdentity(string shown, string inBox)
            => string.Equals(shown ?? "", inBox ?? "", StringComparison.Ordinal) ? null : inBox;

        /// <summary>
        /// One commit of an identity box: send the edit, if there is one, and
        /// return the new baseline. The baseline moves to the edit only when
        /// the radio now holds it (sent, or already so); a refusal (no radio,
        /// the change-nothing hold) returns nothing to say so, and leaves the
        /// edit pending so the same text tried again is sent again (#477,
        /// repair round 3, Codex F3). The live value is read only to learn
        /// whether the edit took, never to decide what is sent.
        /// </summary>
        internal static string CommitIdentity(string shown, string inBox,
            Action<string> send, Func<string> heldNow, Func<string, string> asTheRadioKeepsIt)
        {
            var edited = EditedIdentity(shown, inBox);
            if (edited == null) return shown;
            send(edited);
            return string.Equals(asTheRadioKeepsIt(edited), heldNow() ?? "", StringComparison.Ordinal)
                ? edited : shown;
        }

        private void CommitCallsignIfEdited()
        {
            (_shownCallsign, string box) = CommitAndShow(_shownCallsign, CallBox.Text,
                _callbacks.SetCallsign, _callbacks.GetCallsign, Radios.FlexBase.CallsignAsTheRadioKeepsIt);
            if (!string.Equals(CallBox.Text, box, StringComparison.Ordinal)) CallBox.Text = box;
        }

        private void CommitNicknameIfEdited()
        {
            (_shownNickname, string box) = CommitAndShow(_shownNickname, NameBox.Text,
                name => _callbacks.SetNickname(name), _callbacks.GetNickname, Radios.FlexBase.NicknameAsTheRadioKeepsIt);
            if (!string.Equals(NameBox.Text, box, StringComparison.Ordinal)) NameBox.Text = box;
        }

        public RadioInfoDialog(RadioInfoCallbacks callbacks, RadioInfoTab initialTab = RadioInfoTab.General)
        {
            _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
            InitializeComponent();

            if ((int)initialTab >= 0 && (int)initialTab < InfoTabs.Items.Count)
                InfoTabs.SelectedIndex = (int)initialTab;

            ShowValues();
        }

        private void ShowValues()
        {
            ModelBox.Text = _callbacks.GetModel();
            VersionBox.Text = _callbacks.GetVersion();
            SerialBox.Text = _callbacks.GetSerial();
            CallBox.Text = _shownCallsign = _callbacks.GetCallsign() ?? "";
            NameBox.Text = _shownNickname = _callbacks.GetNickname() ?? "";
            IPBox.Text = _callbacks.GetIPAddress();

            // Populate display modes
            _suppressDisplayChange = true;
            DisplayCombo.Items.Clear();
            var modes = _callbacks.GetDisplayModes();
            var currentMode = _callbacks.GetCurrentDisplayMode();
            int selectedIdx = -1;
            for (int i = 0; i < modes.Count; i++)
            {
                DisplayCombo.Items.Add(modes[i]);
                if (modes[i].Value?.Equals(currentMode) == true)
                    selectedIdx = i;
            }
            if (selectedIdx >= 0)
                DisplayCombo.SelectedIndex = selectedIdx;
            _suppressDisplayChange = false;

            // Feature availability
            FeatureAvailabilityBox.Text = _callbacks.GetFeatureAvailabilityText();
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CallBox_LostFocus(object sender, RoutedEventArgs e) => CommitCallsignIfEdited();

        private void CallBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Return) CommitCallsignIfEdited();
        }

        private void NameBox_LostFocus(object sender, RoutedEventArgs e) => CommitNicknameIfEdited();

        private void NameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Return) CommitNicknameIfEdited();
        }

        private void DisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDisplayChange) return;
            if (DisplayCombo.SelectedItem is DisplayModeItem item)
                _callbacks.SetDisplayMode(item.Value);
        }

        private void RefreshLicenseButton_Click(object sender, RoutedEventArgs e)
        {
            _callbacks.RefreshLicense();
            FeatureAvailabilityBox.Text = _callbacks.GetFeatureAvailabilityText();
        }
    }
}
