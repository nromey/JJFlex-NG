using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;

namespace JJFlexWpf.Dialogs
{
    public partial class FilterPresetEditorDialog : JJFlexDialog
    {
        private readonly FilterPresets _presets;

        /// <summary>True if the user made changes and clicked OK.</summary>
        public bool Changed { get; private set; }

        public FilterPresetEditorDialog(FilterPresets presets)
        {
            _presets = presets ?? throw new ArgumentNullException(nameof(presets));
            InitializeComponent();
            // Delete on the list presses this button (#774).
            DeleteKeyRemoves.SetButton(PresetList, DeleteButton);

            foreach (var mode in FilterPresets.ModeKeys)
                ModeCombo.Items.Add(mode);
            ModeCombo.SelectedIndex = 0;

            // #314: export and import one preset. DRAFT words, for Codex.
            ExportButton.Content = Lexicon.Get("settings.filter_presets.export_button");
            System.Windows.Automation.AutomationProperties.SetName(ExportButton,
                Lexicon.Get("settings.filter_presets.export_name"));
            JJFlexHelp.SetText(ExportButton, Lexicon.Get("settings.filter_presets.export_help"));
            ImportButton.Content = Lexicon.Get("settings.filter_presets.import_button");
            System.Windows.Automation.AutomationProperties.SetName(ImportButton,
                Lexicon.Get("settings.filter_presets.import_name"));
            JJFlexHelp.SetText(ImportButton, Lexicon.Get("settings.filter_presets.import_help"));
        }

        private string CurrentMode => ModeCombo.SelectedItem as string ?? "SSB";

        private List<FilterPreset> GetCurrentPresets()
        {
            return _presets.GetPresetsForMode(CurrentMode);
        }

        private void RefreshList()
        {
            int prevIdx = PresetList.SelectedIndex;
            PresetList.Items.Clear();
            var presets = GetCurrentPresets();
            foreach (var p in presets)
            {
                // List text, so the displayed form: never rounded (#747).
                string bw = HertzSpeech.DisplayedWidth(p.Width, HertzUnitStyle.Symbols);
                PresetList.Items.Add(Lexicon.Get("audio.filter.presets.list_item", ("name", p.Name), ("bw", bw)));
            }

            if (prevIdx >= 0 && prevIdx < PresetList.Items.Count)
                PresetList.SelectedIndex = prevIdx;
            else if (PresetList.Items.Count > 0)
                PresetList.SelectedIndex = 0;

            UpdateButtonStates();
        }

        private void UpdateButtonStates()
        {
            bool hasSel = PresetList.SelectedIndex >= 0;
            EditButton.IsEnabled = hasSel;
            DeleteButton.IsEnabled = hasSel;
            ExportButton.IsEnabled = hasSel;
            MoveUpButton.IsEnabled = hasSel && PresetList.SelectedIndex > 0;
            MoveDownButton.IsEnabled = hasSel && PresetList.SelectedIndex < PresetList.Items.Count - 1;
        }

        private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshList();
        }

        private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtonStates();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var editDialog = new FilterPresetEntryDialog(Lexicon.Get("audio.filter.presets.new_name"), 100, 2500);
            editDialog.Owner = this;
            if (editDialog.ShowDialog() == true)
            {
                var preset = new FilterPreset(editDialog.PresetName, editDialog.LowHz, editDialog.HighHz);
                EnsureModeExists(CurrentMode);
                var modePresets = _presets.Modes.First(m =>
                    string.Equals(m.Mode, CurrentMode, StringComparison.OrdinalIgnoreCase));
                modePresets.Presets.Add(preset);
                Changed = true;
                RefreshList();
                PresetList.SelectedIndex = PresetList.Items.Count - 1;
            }
        }

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            int idx = PresetList.SelectedIndex;
            if (idx < 0) return;

            // Materialize the defaults first. Before this, editing while the
            // mode had no saved presets edited a TRANSIENT defaults list —
            // the dialog showed the change, marked itself Changed, and saved
            // nothing for the mode, so the edit silently evaporated on the
            // next open. Delete and the move buttons just went quietly dead.
            EnsureModeExists(CurrentMode);
            var presets = GetCurrentPresets();
            if (idx >= presets.Count) return;

            var current = presets[idx];
            var editDialog = new FilterPresetEntryDialog(current.Name, current.Low, current.High);
            editDialog.Owner = this;
            if (editDialog.ShowDialog() == true)
            {
                current.Name = editDialog.PresetName;
                current.Low = editDialog.LowHz;
                current.High = editDialog.HighHz;
                Changed = true;
                RefreshList();
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            int idx = PresetList.SelectedIndex;
            if (idx < 0) return;

            EnsureModeExists(CurrentMode); // see EditButton_Click
            var modePresets = _presets.Modes.FirstOrDefault(m =>
                string.Equals(m.Mode, CurrentMode, StringComparison.OrdinalIgnoreCase));
            if (modePresets == null || idx >= modePresets.Presets.Count) return;

            modePresets.Presets.RemoveAt(idx);
            Changed = true;
            RefreshList();
        }

        private void MoveUpButton_Click(object sender, RoutedEventArgs e)
        {
            int idx = PresetList.SelectedIndex;
            if (idx <= 0) return;

            EnsureModeExists(CurrentMode); // see EditButton_Click
            var modePresets = _presets.Modes.FirstOrDefault(m =>
                string.Equals(m.Mode, CurrentMode, StringComparison.OrdinalIgnoreCase));
            if (modePresets == null) return;

            (modePresets.Presets[idx - 1], modePresets.Presets[idx]) =
                (modePresets.Presets[idx], modePresets.Presets[idx - 1]);
            Changed = true;
            RefreshList();
            PresetList.SelectedIndex = idx - 1;
        }

        private void MoveDownButton_Click(object sender, RoutedEventArgs e)
        {
            int idx = PresetList.SelectedIndex;
            if (idx < 0) return;

            EnsureModeExists(CurrentMode); // see EditButton_Click
            var modePresets = _presets.Modes.FirstOrDefault(m =>
                string.Equals(m.Mode, CurrentMode, StringComparison.OrdinalIgnoreCase));
            if (modePresets == null || idx >= modePresets.Presets.Count - 1) return;

            (modePresets.Presets[idx], modePresets.Presets[idx + 1]) =
                (modePresets.Presets[idx + 1], modePresets.Presets[idx]);
            Changed = true;
            RefreshList();
            PresetList.SelectedIndex = idx + 1;
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = new ConfirmActionDialog(
                Lexicon.Get("audio.filter.presets.reset_title"),
                Lexicon.Get("audio.filter.presets.reset_body", ("mode", CurrentMode)),
                question: Lexicon.Get("audio.filter.presets.reset_question"),
                yesLabel: Lexicon.Get("audio.filter.presets.reset_yes"));
            if (confirm.ShowDialog() != true) return;

            // Remove saved presets for this mode — GetPresetsForMode will fall back to defaults
            _presets.Modes.RemoveAll(m =>
                string.Equals(m.Mode, CurrentMode, StringComparison.OrdinalIgnoreCase));
            Changed = true;
            RefreshList();
        }

        // Copy the defaults into an editable list (FilterPresets.EditableList,
        // shared with import since #314).
        private void EnsureModeExists(string mode) => _presets.EditableList(mode);

        private static void SayFileOutcome(string sentence, VerbosityLevel level) =>
            ScreenReaderOutput.Speak(sentence, level, interrupt: true,
                subject: Radios.Speech.SpeechSubject.FilterPresetFile);

        /// <summary>
        /// Export the selected preset as a file another operator can import
        /// (#314), in the shape of the Audio Workshop preset export. Needs no
        /// radio: a preset is a file.
        /// </summary>
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            int idx = PresetList.SelectedIndex;
            var presets = GetCurrentPresets();
            if (idx < 0 || idx >= presets.Count) return;
            var preset = presets[idx];

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = Lexicon.Get("settings.filter_presets.file_filter"),
                DefaultExt = ".xml",
                FileName = SafeFileName(CurrentMode + " " + preset.Name) + ".xml",
            };
            if (sfd.ShowDialog(this) != true) return;

            string file = System.IO.Path.GetFileName(sfd.FileName);
            if (FilterPresets.ExportPreset(sfd.FileName, CurrentMode, preset))
                SayFileOutcome(Lexicon.Get("settings.filter_presets.exported", ("name", preset.Name), ("file", file)),
                    VerbosityLevel.Terse);
            else
                SayFileOutcome(Lexicon.Get("settings.filter_presets.export_failed", ("file", file)),
                    VerbosityLevel.Critical);
        }

        /// <summary>
        /// Import one preset from a file into its mode list (#314). Never
        /// applies anything to the radio, works with no radio, and never moves
        /// or rewrites the file it reads. The preset joins this editor's
        /// working list like an added one, so OK keeps it and Cancel drops it.
        /// The editor opens only with an operator profile loaded, so there is
        /// always somewhere for OK to save it.
        /// </summary>
        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = Lexicon.Get("settings.filter_presets.file_filter"),
                DefaultExt = ".xml",
            };
            if (ofd.ShowDialog(this) != true) return;

            string file = System.IO.Path.GetFileName(ofd.FileName);
            if (!FilterPresets.TryReadPresetFile(ofd.FileName,
                    System.IO.Path.GetFileNameWithoutExtension(ofd.FileName), out var read) || read == null)
            {
                // Honest failure: a bad file never quietly becomes a preset.
                SayFileOutcome(Lexicon.Get("settings.filter_presets.import_unreadable", ("file", file)),
                    VerbosityLevel.Critical);
                return;
            }

            var (added, mode) = _presets.ImportPreset(read, CurrentMode);
            Changed = true;
            if (!string.Equals(mode, CurrentMode, StringComparison.OrdinalIgnoreCase))
                ModeCombo.SelectedItem = mode; // refreshes the list
            else
                RefreshList();
            int at = GetCurrentPresets().FindIndex(p => ReferenceEquals(p, added));
            if (at >= 0) PresetList.SelectedIndex = at;
            SayFileOutcome(Lexicon.Get("settings.filter_presets.imported", ("name", added.Name), ("mode", mode)),
                VerbosityLevel.Terse);
        }

        private static string SafeFileName(string name)
        {
            var bad = System.IO.Path.GetInvalidFileNameChars();
            var chars = name.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray();
            string safe = new string(chars).Trim();
            return safe.Length == 0 ? "filter preset" : safe;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Changed = false;
            DialogResult = false;
            Close();
        }
    }

    /// <summary>
    /// Simple entry dialog for a single filter preset (name, low Hz, high Hz).
    /// </summary>
    public partial class FilterPresetEntryDialog : JJFlexDialog
    {
        public string PresetName => NameBox.Text.Trim();
        public int LowHz { get; private set; }
        public int HighHz { get; private set; }

        private TextBox NameBox;
        private TextBox LowBox;
        private TextBox HighBox;

        public FilterPresetEntryDialog(string name, int low, int high)
        {
            Title = Lexicon.Get("audio.filter.presets.entry_title");
            Width = 320;
            Height = 200;

            var grid = new Grid { Margin = new Thickness(12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddLabel(grid, 0, Lexicon.Get("audio.filter.presets.label_name"));
            NameBox = AddTextBox(grid, 0, Lexicon.Get("audio.filter.presets.name_field"), name);

            AddLabel(grid, 1, Lexicon.Get("audio.filter.presets.label_low"));
            LowBox = AddTextBox(grid, 1, Lexicon.Get("audio.filter.presets.low_field"), low.ToString());

            AddLabel(grid, 2, Lexicon.Get("audio.filter.presets.label_high"));
            HighBox = AddTextBox(grid, 2, Lexicon.Get("audio.filter.presets.high_field"), high.ToString());

            var buttonPanel = CreateButtonPanel(
                onOk: () =>
                {
                    if (string.IsNullOrWhiteSpace(NameBox.Text))
                    {
                        MessageBox.Show(Lexicon.Get("audio.filter.presets.name_required"),
                            Lexicon.Get("audio.filter.presets.validation_caption"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (!int.TryParse(LowBox.Text, out int lo) || !int.TryParse(HighBox.Text, out int hi))
                    {
                        MessageBox.Show(Lexicon.Get("audio.filter.presets.edges_must_be_whole"),
                            Lexicon.Get("audio.filter.presets.entry_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (hi <= lo)
                    {
                        MessageBox.Show(Lexicon.Get("audio.filter.presets.high_above_low"),
                            Lexicon.Get("audio.filter.presets.entry_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    LowHz = lo;
                    HighHz = hi;
                },
                onCancel: null);
            Grid.SetRow(buttonPanel, 3);
            Grid.SetColumnSpan(buttonPanel, 2);

            grid.Children.Add(buttonPanel);
            Content = grid;
        }

        private static void AddLabel(Grid grid, int row, string text)
        {
            var label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4)
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);
        }

        private static TextBox AddTextBox(Grid grid, int row, string automationName, string value)
        {
            var box = new TextBox
            {
                Text = value,
                Margin = new Thickness(0, 4, 0, 4)
            };
            System.Windows.Automation.AutomationProperties.SetName(box, automationName);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            return box;
        }

        protected override void FocusFirstControl()
        {
            NameBox?.Focus();
            NameBox?.SelectAll();
        }
    }
}
