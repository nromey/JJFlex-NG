using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace JJFlexWpf.Dialogs
{
    public partial class TuningStepEditorDialog : JJFlexDialog
    {
        private readonly List<int> _coarseSteps;
        private readonly List<int> _fineSteps;

        /// <summary>Final coarse step values (sorted ascending).</summary>
        public int[] CoarseSteps => _coarseSteps.OrderBy(s => s).ToArray();

        /// <summary>Final fine step values (sorted ascending).</summary>
        public int[] FineSteps => _fineSteps.OrderBy(s => s).ToArray();

        /// <summary>True if user made changes.</summary>
        public bool Changed { get; private set; }

        public TuningStepEditorDialog(int[] coarseSteps, int[] fineSteps)
        {
            _coarseSteps = new List<int>(coarseSteps ?? new[] { 1000, 2000, 5000 });
            _fineSteps = new List<int>(fineSteps ?? new[] { 5, 10, 100 });

            InitializeComponent();
            RefreshCoarseList();
            RefreshFineList();
        }

        private void RefreshCoarseList()
        {
            int sel = CoarseList.SelectedIndex;
            CoarseList.Items.Clear();
            foreach (int hz in _coarseSteps.OrderBy(s => s))
                CoarseList.Items.Add(FormatStep(hz));
            if (sel >= 0 && sel < CoarseList.Items.Count) CoarseList.SelectedIndex = sel;
            CoarseRemoveButton.IsEnabled = _coarseSteps.Count > 1;
        }

        private void RefreshFineList()
        {
            int sel = FineList.SelectedIndex;
            FineList.Items.Clear();
            foreach (int hz in _fineSteps.OrderBy(s => s))
                FineList.Items.Add(FormatStep(hz));
            if (sel >= 0 && sel < FineList.Items.Count) FineList.SelectedIndex = sel;
            FineRemoveButton.IsEnabled = _fineSteps.Count > 1;
        }

        private static string FormatStep(int hz)
        {
            if (hz >= 1000) return $"{hz / 1000.0:0.###} kHz";
            return $"{hz} Hz";
        }

        private void CoarseAddButton_Click(object sender, RoutedEventArgs e)
        {
            if (PromptForStep("Add Coarse Step", "Step size in Hz:", out int hz))
            {
                if (!_coarseSteps.Contains(hz))
                {
                    _coarseSteps.Add(hz);
                    Changed = true;
                    RefreshCoarseList();
                }
            }
        }

        private void CoarseRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_coarseSteps.Count <= 1) return;
            int idx = CoarseList.SelectedIndex;
            if (idx < 0) return;
            var sorted = _coarseSteps.OrderBy(s => s).ToList();
            if (idx < sorted.Count)
            {
                _coarseSteps.Remove(sorted[idx]);
                Changed = true;
                RefreshCoarseList();
            }
        }

        private void FineAddButton_Click(object sender, RoutedEventArgs e)
        {
            if (PromptForStep("Add Fine Step", "Step size in Hz:", out int hz))
            {
                if (!_fineSteps.Contains(hz))
                {
                    _fineSteps.Add(hz);
                    Changed = true;
                    RefreshFineList();
                }
            }
        }

        private void FineRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_fineSteps.Count <= 1) return;
            int idx = FineList.SelectedIndex;
            if (idx < 0) return;
            var sorted = _fineSteps.OrderBy(s => s).ToList();
            if (idx < sorted.Count)
            {
                _fineSteps.Remove(sorted[idx]);
                Changed = true;
                RefreshFineList();
            }
        }

        private bool PromptForStep(string title, string label, out int hz)
        {
            hz = 0;
            var dlg = new StepEntryDialog(title, label);
            dlg.Owner = this;
            if (dlg.ShowDialog() == true && dlg.StepHz > 0)
            {
                hz = dlg.StepHz;
                return true;
            }
            return false;
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

    /// <summary>Simple entry dialog for a single tuning step value.</summary>
    public partial class StepEntryDialog : JJFlexDialog
    {
        public int StepHz { get; private set; }
        private TextBox ValueBox;

        public StepEntryDialog(string title, string label)
        {
            Title = title;
            Width = 300;
            Height = 140;

            var stack = new StackPanel { Margin = new Thickness(12) };

            var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) };
            stack.Children.Add(lbl);

            ValueBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(ValueBox, "Step size in hertz");
            stack.Children.Add(ValueBox);

            var buttonPanel = CreateButtonPanel(
                onOk: () =>
                {
                    if (!int.TryParse(ValueBox.Text, out int val) || val <= 0)
                    {
                        MessageBox.Show("Enter a positive integer (Hz).", "Validation",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    StepHz = val;
                },
                onCancel: null);
            stack.Children.Add(buttonPanel);

            Content = stack;
        }

        protected override void FocusFirstControl()
        {
            ValueBox?.Focus();
        }
    }
}
