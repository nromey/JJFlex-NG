using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Set the Modern tuning coarse and fine steps outright (#302).
    /// </summary>
    /// <remarks>
    /// <para><b>Why a picker as well as the ladder keys.</b> Alt+Left/Right
    /// and Shift+Left/Right walk one rung at a time without leaving the
    /// frequency field, which is what an operator wants while listening. This
    /// is the other question — "what can I have, and set me both at once" —
    /// and a relative key can never answer it. Same distinction the arrow keys
    /// and Settings already had, moved to where the tuning happens.</para>
    /// <para><b>It shares its options with everything else.</b> The lists come
    /// from <see cref="TuningSteps"/>, the table the ladder keys walk and the
    /// one the Settings dialog now fills its combos from.</para>
    /// </remarks>
    public partial class TuningStepsDialog : JJFlexDialog
    {
        /// <summary>
        /// One row: the step it stands for, and the words the row shows.
        /// </summary>
        /// <remarks>
        /// <b>The row CARRIES its step rather than being matched to one by
        /// position.</b> The first version put labels in the list and kept the
        /// choices in a parallel array, reading the operator's answer back by
        /// index — two representations of one fact, correct only for as long
        /// as both were appended in lockstep, and wrong SILENTLY if they ever
        /// were not: a mismatched index sets a step the operator did not pick
        /// and says nothing. Nothing about that would fail a build or a test.
        /// One representation removes the question.
        /// <para>ToString is what the ListBox renders and therefore what a
        /// screen reader reads — the same shape DialogCatalog.DialogEntry
        /// uses.</para>
        /// </remarks>
        private sealed record StepRow(TuningSteps.Choice Choice)
        {
            public override string ToString() => TuningSteps.LabelFor(Choice);
        }

        /// <summary>The chosen coarse step in Hz. Meaningful only when the
        /// dialog returned true.</summary>
        public int CoarseStepHz { get; private set; }

        /// <summary>The chosen fine step in Hz. Meaningful only when the
        /// dialog returned true.</summary>
        public int FineStepHz { get; private set; }

        /// <param name="currentCoarseHz">The coarse step in force right now.</param>
        /// <param name="currentFineHz">The fine step in force right now.</param>
        public TuningStepsDialog(int currentCoarseHz, int currentFineHz)
        {
            InitializeComponent();

            AutomationProperties.SetName(this, Title);

            CoarseStepHz = currentCoarseHz;
            FineStepHz = currentFineHz;

            // ChoicesIncluding, not the bare ladder: a picker that omits the
            // value currently in force would show one step while another was
            // running, and would change it the moment OK was pressed.
            Fill(CoarseList, TuningSteps.ChoicesIncluding(TuningSteps.Coarse, currentCoarseHz),
                currentCoarseHz);
            Fill(FineList, TuningSteps.ChoicesIncluding(TuningSteps.Fine, currentFineHz),
                currentFineHz);
        }

        private static void Fill(ListBox list,
            System.Collections.Generic.IReadOnlyList<TuningSteps.Choice> choices, int currentHz)
        {
            foreach (var choice in choices)
            {
                var row = new StepRow(choice);
                list.Items.Add(row);
                if (choice.Hz == currentHz) list.SelectedItem = row;
            }
            if (list.SelectedItem == null && list.Items.Count > 0) list.SelectedIndex = 0;
        }

        /// <summary>The step a list is showing as chosen, or the fallback when
        /// somehow nothing is selected.</summary>
        private static int ChosenHz(ListBox list, int fallbackHz) =>
            list.SelectedItem is StepRow row ? row.Choice.Hz : fallbackHz;

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            CoarseStepHz = ChosenHz(CoarseList, CoarseStepHz);
            FineStepHz = ChosenHz(FineList, FineStepHz);

            CloseWithResult(true);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseWithResult(false);
        }
    }
}
