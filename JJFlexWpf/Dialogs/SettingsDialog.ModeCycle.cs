using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Settings → Notifications → Mode Cycle: which modes Alt+M and
    /// Alt+Shift+M step through, in the operator's order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ruled by Noel 2026-08-26: <i>"we decided that we were going to have a
    /// setting to allow users to enable the modes they want to appear in the
    /// ring ... could be a sub page in settings verbosity"</i>. So it is its own
    /// section of the Notifications tab, which holds the verbosity settings.
    /// That reading of "sub page" is DRAFT, like every word here.
    /// </para>
    /// <para>
    /// <b>A filter on the cycle, never a capability gate</b> (see
    /// <see cref="ModeCycle"/>): nothing here writes to the radio, and the copy
    /// says "in the cycle", never "enabled", because "FM disabled" would tell a
    /// blind operator something false about the radio.
    /// </para>
    /// <para>
    /// <b>Master and detail</b>, the shape of the tracking notch dialog and the
    /// meter slots: one list of every mode, cycle members first in cycle order,
    /// then the rest; a checkbox and two buttons act on the selected mode. Four
    /// tab stops whatever the number of modes. Move Up and Move Down reorder
    /// within the cycle only, so they are disabled (and out of the tab order)
    /// on a mode that is not in it. Checking a mode adds it to the end of the
    /// cycle.
    /// </para>
    /// <para>
    /// Committed on OK or Apply through
    /// <see cref="AccessibilityConfig.ChooseModeCycle"/>, into the operator's
    /// own file at the settings root, the one the application loads (the
    /// dialog's own save goes to the per-radio folder, which nothing reads it
    /// back from). A radio's config never replaces it. Cancel discards, like
    /// every other page. Nothing is applied live.
    /// </para>
    /// </remarks>
    public partial class SettingsDialog
    {
        /// <summary>The cycle being edited, in the operator's order.</summary>
        private List<string> _modeCycleIn = new();

        /// <summary>Every mode on offer: the radio's list, or FlexLib's set,
        /// plus any saved mode neither knows.</summary>
        private List<string> _modeCycleKnown = new();

        /// <summary>The cycle as loaded, to tell an edit from no edit.</summary>
        private List<string> _modeCycleAtOpen = new();

        private bool _suppressModeCycleEvents;

        /// <summary>Fill the section from the saved cycle. Called once, from the constructor.</summary>
        private void InitializeModeCycle()
        {
            // DRAFT PROSE: every settings.mode_cycle string, awaiting Noel.
            ModeCycleGroup.Header = Lexicon.Get("settings.mode_cycle.header");
            AutomationProperties.SetName(ModeCycleGroup, Lexicon.Get("settings.mode_cycle.header"));
            ModeCycleIntro.Text = Lexicon.Get("settings.mode_cycle.intro");
            AutomationProperties.SetName(ModeCycleList, Lexicon.Get("settings.mode_cycle.list_name"));
            JJFlexHelp.SetText(ModeCycleList, Lexicon.Get("settings.mode_cycle.list_help"));
            ModeCycleIncludeCheck.Content = Lexicon.Get("settings.mode_cycle.include");
            AutomationProperties.SetName(ModeCycleIncludeCheck, Lexicon.Get("settings.mode_cycle.include"));
            ModeCycleMoveUpButton.Content = Lexicon.Get("settings.mode_cycle.move_up");
            AutomationProperties.SetName(ModeCycleMoveUpButton, Lexicon.Get("settings.mode_cycle.move_up_name"));
            ModeCycleMoveDownButton.Content = Lexicon.Get("settings.mode_cycle.move_down");
            AutomationProperties.SetName(ModeCycleMoveDownButton, Lexicon.Get("settings.mode_cycle.move_down_name"));

            _modeCycleIn = ModeCycle.FromSaved(AccessibilityConfig.Current.ModeCycleModes).ToList();
            _modeCycleAtOpen = new List<string>(_modeCycleIn);
            _modeCycleKnown = ModeCycle.KnownModes(null).ToList();
            _radioModes = new List<string>();   // none reported until a radio is handed in
            AddUnknownModes(_modeCycleIn);
            UpdateModeCycleMenuNote();
            RebuildModeCycleList(_modeCycleIn.Count > 0 ? _modeCycleIn[0] : _modeCycleKnown.FirstOrDefault());

            // The radio is handed to this dialog after it is built, so its own
            // mode list is merged when the section is first shown.
            ModeCycleList.Loaded += (_, _) => MergeRadioModes();
        }

        /// <summary>Write the edited cycle into the operator's config, on OK or Apply.</summary>
        private void CommitModeCycle()
        {
            if (_modeCycleIn.SequenceEqual(_modeCycleAtOpen, StringComparer.OrdinalIgnoreCase)) return;
            // Into the file start-up reads, not only the per-radio folder the
            // dialog saves the rest of this object to (see ChooseModeCycle).
            AccessibilityConfig.ChooseModeCycle(_modeCycleIn.ToArray());
            _modeCycleAtOpen = new List<string>(_modeCycleIn);
        }

        /// <summary>Add what this radio reports that the list does not yet hold.</summary>
        private void MergeRadioModes() => ApplyRadioModes(_rig?.RadioModeList);

        /// <summary>The modes the connected radio reported; empty when none has.</summary>
        private List<string> _radioModes = new();

        /// <summary>
        /// Offer what the connected radio reports, once it has reported a list.
        /// </summary>
        /// <remarks>
        /// Found on the 8600 at the bench, 2026-11-07: it reports no DSB, the
        /// page offered DSB from FlexLib's list anyway, and a DSB in the cycle
        /// was written, refused (the radio went to DIGU) and spoken as "DSB".
        /// So with a list reported the page offers that list, and a saved mode
        /// it does not hold stays in the cycle (the file is the operator's,
        /// for any radio) but reads "not available on this radio"; the cycle
        /// key skips it (<see cref="ModeCycle.OnRadio"/>). With no radio the
        /// full list stays, so an offline choice still works.
        /// </remarks>
        private void ApplyRadioModes(IReadOnlyList<string>? radio)
        {
            if (radio == null || radio.Count == 0) return;
            string? selected = SelectedCycleMode();
            _radioModes = ModeCycle.KnownModes(radio).ToList();
            _modeCycleKnown = new List<string>(_radioModes);
            AddUnknownModes(_modeCycleIn);
            UpdateModeCycleMenuNote();
            RebuildModeCycleList(selected);
        }

        /// <summary>True when a radio has reported its modes and this is not one of them.</summary>
        private bool UnavailableOnRadio(string mode) =>
            _radioModes.Count > 0 && !ModeCycle.Contains(_radioModes, mode);

        /// <summary>
        /// Say which of the modes on offer the Mode menu also reaches, and
        /// name the ones it does not.
        /// </summary>
        /// <remarks>
        /// The page used to promise that a mode left out of the cycle "is
        /// still there in the Mode menu and on its own key". DSB is offered
        /// (ruled 2026-10-19) and is in neither: the menu is built from
        /// <see cref="RigCaps.ModeTable"/>, which has no DSB, and only seven
        /// modes have a key of their own. So the page says only what holds,
        /// worked out from the list the menu itself is built from, and makes
        /// no promise about keys. When the menu gains a mode, this note
        /// follows without an edit.
        /// </remarks>
        private void UpdateModeCycleMenuNote()
        {
            var notInMenu = ModeCyclesNotInMenu(_modeCycleKnown.Where(m => !UnavailableOnRadio(m)));
            // Once a radio has reported its modes, the note speaks of THIS
            // radio's modes only: "every mode here" would take in a saved
            // mode the list marks "not available on this radio", which is in
            // no menu at all (Codex B1 on 4ffb2106).
            bool onRadio = _radioModes.Count > 0;
            ModeCycleMenuNote.Text = notInMenu.Count == 0
                ? Lexicon.Get(onRadio ? "settings.mode_cycle.menu_note_radio_all" : "settings.mode_cycle.menu_note_all")
                : Lexicon.Get(notInMenu.Count == 1
                        ? (onRadio ? "settings.mode_cycle.menu_note_radio_except" : "settings.mode_cycle.menu_note_except")
                        : (onRadio ? "settings.mode_cycle.menu_note_radio_except_many" : "settings.mode_cycle.menu_note_except_many"),
                    ("modes", JoinModes(notInMenu)));
        }

        /// <summary>"DSB", "DSB and RTTY", "A, B and C".</summary>
        private static string JoinModes(IReadOnlyList<string> modes) =>
            modes.Count <= 1 ? string.Join("", modes)
            : string.Join(", ", modes.Take(modes.Count - 1)) + " and " + modes[modes.Count - 1];

        /// <summary>The modes on offer that the Mode menu does not list.</summary>
        internal static List<string> ModeCyclesNotInMenu(IEnumerable<string> offered) =>
            offered.Where(m => !ModeCycle.Contains(RigCaps.ModeTable, m)).ToList();

        private void AddUnknownModes(IEnumerable<string> modes)
        {
            foreach (string m in modes)
                if (!ModeCycle.Contains(_modeCycleKnown, m)) _modeCycleKnown.Add(m);
        }

        /// <summary>Cycle members first, in cycle order, then every other known mode.</summary>
        private List<string> ModeCycleRows() =>
            _modeCycleIn.Concat(_modeCycleKnown.Where(m => !ModeCycle.Contains(_modeCycleIn, m))).ToList();

        private void RebuildModeCycleList(string? select)
        {
            _suppressModeCycleEvents = true;
            try
            {
                ModeCycleList.Items.Clear();
                var rows = ModeCycleRows();
                foreach (string m in rows)
                {
                    bool inCycle = ModeCycle.Contains(_modeCycleIn, m);
                    bool unavailable = UnavailableOnRadio(m);
                    ModeCycleList.Items.Add(new ListBoxItem
                    {
                        Content = Lexicon.Get(
                            inCycle
                                ? (unavailable ? "settings.mode_cycle.row_in_unavailable" : "settings.mode_cycle.row_in")
                                : (unavailable ? "settings.mode_cycle.row_out_unavailable" : "settings.mode_cycle.row_out"),
                            ("mode", m)),
                        Tag = m,
                    });
                }
                int at = select == null ? -1
                    : rows.FindIndex(r => string.Equals(r, select, StringComparison.OrdinalIgnoreCase));
                ModeCycleList.SelectedIndex = at >= 0 ? at : (rows.Count > 0 ? 0 : -1);
            }
            finally
            {
                _suppressModeCycleEvents = false;
            }
            UpdateModeCycleDetail();
        }

        private string? SelectedCycleMode() => (ModeCycleList.SelectedItem as ListBoxItem)?.Tag as string;

        private void UpdateModeCycleDetail()
        {
            string? mode = SelectedCycleMode();
            int at = mode == null ? -1
                : _modeCycleIn.FindIndex(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));

            _suppressModeCycleEvents = true;
            try
            {
                ModeCycleIncludeCheck.IsEnabled = mode != null;
                ModeCycleIncludeCheck.IsChecked = at >= 0;
            }
            finally
            {
                _suppressModeCycleEvents = false;
            }
            ModeCycleMoveUpButton.IsEnabled = at > 0;
            ModeCycleMoveDownButton.IsEnabled = at >= 0 && at < _modeCycleIn.Count - 1;
        }

        private void ModeCycleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressModeCycleEvents) return;
            UpdateModeCycleDetail();
        }

        private void ModeCycleIncludeCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressModeCycleEvents) return;
            string? mode = SelectedCycleMode();
            if (mode == null) return;

            bool include = ModeCycleIncludeCheck.IsChecked == true;
            int at = _modeCycleIn.FindIndex(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));
            if (include && at < 0) _modeCycleIn.Add(mode);
            else if (!include && at >= 0) _modeCycleIn.RemoveAt(at);
            else return;

            // The row moves into or out of the cycle's block; the selection
            // follows it, and the checkbox the operator is on says checked or
            // not checked itself.
            RebuildModeCycleList(mode);
        }

        private void ModeCycleMoveUpButton_Click(object sender, RoutedEventArgs e) => MoveCycleMode(-1);

        private void ModeCycleMoveDownButton_Click(object sender, RoutedEventArgs e) => MoveCycleMode(+1);

        /// <summary>Move the selected cycle member one place, and say where it landed.</summary>
        private void MoveCycleMode(int delta)
        {
            string? mode = SelectedCycleMode();
            if (mode == null) return;
            int at = _modeCycleIn.FindIndex(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));
            int to = at + delta;
            if (at < 0 || to < 0 || to >= _modeCycleIn.Count) return;

            (_modeCycleIn[at], _modeCycleIn[to]) = (_modeCycleIn[to], _modeCycleIn[at]);
            RebuildModeCycleList(mode);

            // DRAFT PROSE. The button keeps focus, so the list's new order is
            // not read out by itself; say where the mode went.
            ScreenReaderOutput.Speak(
                Lexicon.Get("settings.mode_cycle.moved",
                    ("mode", mode), ("position", to + 1), ("count", _modeCycleIn.Count)),
                Radios.Speech.SpeechIntent.Latest, VerbosityLevel.Terse,
                subject: Radios.Speech.SpeechSubject.ValueField("Mode cycle"));

            // A button that has just reached an end is disabled, and a
            // disabled control holding focus leaves the operator nowhere:
            // hand focus to the list, on the mode that moved.
            var pressed = delta < 0 ? ModeCycleMoveUpButton : ModeCycleMoveDownButton;
            if (!pressed.IsEnabled)
            {
                if (ModeCycleList.SelectedItem is ListBoxItem row) row.Focus();
                else ModeCycleList.Focus();
            }
        }
    }
}
