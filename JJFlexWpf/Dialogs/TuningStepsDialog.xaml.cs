using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
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
        private readonly IReadOnlyList<TuningSteps.Choice> _coarseChoices;
        private readonly IReadOnlyList<TuningSteps.Choice> _fineChoices;

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
            _coarseChoices = TuningSteps.ChoicesIncluding(TuningSteps.Coarse, currentCoarseHz);
            _fineChoices = TuningSteps.ChoicesIncluding(TuningSteps.Fine, currentFineHz);

            Fill(CoarseList, _coarseChoices, currentCoarseHz);
            Fill(FineList, _fineChoices, currentFineHz);
        }

        private static void Fill(System.Windows.Controls.ListBox list,
            IReadOnlyList<TuningSteps.Choice> choices, int currentHz)
        {
            for (int i = 0; i < choices.Count; i++)
            {
                list.Items.Add(TuningSteps.LabelFor(choices[i]));
                if (choices[i].Hz == currentHz) list.SelectedIndex = i;
            }
            if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (CoarseList.SelectedIndex >= 0 && CoarseList.SelectedIndex < _coarseChoices.Count)
                CoarseStepHz = _coarseChoices[CoarseList.SelectedIndex].Hz;
            if (FineList.SelectedIndex >= 0 && FineList.SelectedIndex < _fineChoices.Count)
                FineStepHz = _fineChoices[FineList.SelectedIndex].Hz;

            CloseWithResult(true);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseWithResult(false);
        }
    }
}
