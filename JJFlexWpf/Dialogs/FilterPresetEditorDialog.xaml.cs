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
        private static readonly string[] ModeKeys = { "SSB", "CW", "DIGI", "AM", "FM" };

        /// <summary>True if the user made changes and clicked OK.</summary>
        public bool Changed { get; private set; }

        public FilterPresetEditorDialog(FilterPresets presets)
        {
            _presets = presets ?? throw new ArgumentNullException(nameof(presets));
            InitializeComponent();

            foreach (var mode in ModeKeys)
                ModeCombo.Items.Add(mode);
            ModeCombo.SelectedIndex = 0;
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
                string bw = p.Width >= 1000
                    ? $"{p.Width / 1000.0:0.#} kHz"
                    : $"{p.Width} Hz";
                PresetList.Items.Add($"{p.Name} — {bw}");
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
            var editDialog = new FilterPresetEntryDialog("New Preset", 100, 2500);
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
            var modePresets = _presets.Modes.FirstOrDefault(m =>
                string.Equals(m.Mode, CurrentMode, StringComparison.OrdinalIgnoreCase));
            if (modePresets == null || idx < 0 || idx >= modePresets.Presets.Count - 1) return;

            (modePresets.Presets[idx], modePresets.Presets[idx + 1]) =
                (modePresets.Presets[idx + 1], modePresets.Presets[idx]);
            Changed = true;
            RefreshList();
            PresetList.SelectedIndex = idx + 1;
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                $"Reset all {CurrentMode} presets to defaults? Custom presets for this mode will be lost.",
                "Reset Presets", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;

            // Remove saved presets for this mode — GetPresetsForMode will fall back to defaults
            _presets.Modes.RemoveAll(m =>
                string.Equals(m.Mode, CurrentMode, StringComparison.OrdinalIgnoreCase));
            Changed = true;
            RefreshList();
        }

        private void EnsureModeExists(string mode)
        {
            if (!_presets.Modes.Any(m =>
                string.Equals(m.Mode, mode, StringComparison.OrdinalIgnoreCase)))
            {
                // Copy defaults into editable list
                var defaults = _presets.GetPresetsForMode(mode);
                _presets.Modes.Add(new ModePresets
                {
                    Mode = mode,
                    Presets = defaults.Select(p => new FilterPreset(p.Name, p.Low, p.High)).ToList()
                });
            }
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
            Title = "Filter Preset";
            Width = 320;
            Height = 200;

            var grid = new Grid { Margin = new Thickness(12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddLabel(grid, 0, "Name:");
            NameBox = AddTextBox(grid, 0, "Preset name", name);

            AddLabel(grid, 1, "Low (Hz):");
            LowBox = AddTextBox(grid, 1, "Low frequency in hertz", low.ToString());

            AddLabel(grid, 2, "High (Hz):");
            HighBox = AddTextBox(grid, 2, "High frequency in hertz", high.ToString());

            var buttonPanel = CreateButtonPanel(
                onOk: () =>
                {
                    if (string.IsNullOrWhiteSpace(NameBox.Text))
                    {
                        MessageBox.Show("Name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (!int.TryParse(LowBox.Text, out int lo) || !int.TryParse(HighBox.Text, out int hi))
                    {
                        MessageBox.Show("Low and High must be integers.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (hi <= lo)
                    {
                        MessageBox.Show("High must be greater than Low.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
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
