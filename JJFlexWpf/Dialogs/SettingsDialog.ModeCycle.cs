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
    /// Committed on OK or Apply into <see cref="AccessibilityConfig"/>, the
    /// operator's own file, which a radio's config never replaces; Cancel
    /// discards, like every other page. Nothing is applied live.
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
            AddUnknownModes(_modeCycleIn);
            RebuildModeCycleList(_modeCycleIn.Count > 0 ? _modeCycleIn[0] : _modeCycleKnown.FirstOrDefault());

            // The radio is handed to this dialog after it is built, so its own
            // mode list is merged when the section is first shown.
            ModeCycleList.Loaded += (_, _) => MergeRadioModes();
        }

        /// <summary>Write the edited cycle into the operator's config, on OK or Apply.</summary>
        private void CommitModeCycle()
        {
            if (_modeCycleIn.SequenceEqual(_modeCycleAtOpen, StringComparer.OrdinalIgnoreCase)) return;
            AccessibilityConfig.Current.ModeCycleModes = _modeCycleIn.ToArray();
            _modeCycleAtOpen = new List<string>(_modeCycleIn);
        }

        /// <summary>Add what this radio reports that the list does not yet hold.</summary>
        private void MergeRadioModes()
        {
            var radio = _rig?.RadioModeList;
            if (radio == null || radio.Count == 0) return;
            string? selected = SelectedCycleMode();
            AddUnknownModes(ModeCycle.KnownModes(radio));
            RebuildModeCycleList(selected);
        }

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
                    ModeCycleList.Items.Add(new ListBoxItem
                    {
                        Content = ModeCycle.Contains(_modeCycleIn, m)
                            ? Lexicon.Get("settings.mode_cycle.row_in", ("mode", m))
                            : Lexicon.Get("settings.mode_cycle.row_out", ("mode", m)),
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
