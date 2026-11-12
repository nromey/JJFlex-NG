using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;
using Radios.Speech;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// Set the Modern tuning coarse and fine steps outright (#302), and edit
    /// the two step lists themselves (#346 part two).
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
    /// one the Settings dialog fills its combos from. The operator's edits
    /// here become that table (<see cref="TuningSteps.OperatorCoarse"/>), so
    /// there is still one list, three surfaces.</para>
    /// <para><b>Editing (#346 part two, ruled by Noel 2026-11-09 20:39 and
    /// 2026-11-11 11:45, night question J3).</b> Steps are added and removed
    /// HERE, the list S opens; Settings only shows the list. A step is typed
    /// with a unit, Hz or kHz; fine steps run 1 Hz to 1 kHz and coarse 10 Hz
    /// to 1 MHz; no duplicates within a list (the same step may sit in both);
    /// kept sorted; the last step in a list cannot be removed; a removed step
    /// that is the step in force stays in force until another is picked; and
    /// putting a starting list back asks first. Edits are kept when OK is
    /// pressed, like the step choice; Cancel keeps nothing.</para>
    /// <para><b>Leaving with a changed list asks (#346, ruled by Noel
    /// 2026-11-11 19:40, J15 option b).</b> Cancel, Escape and closing the
    /// window all ask before dropping a changed list, from one place,
    /// <see cref="OnClosing"/>, so no way out can skip it. Yes drops the
    /// changes and closes; No keeps the window open. Escape while transmitting
    /// never gets here: the dialog door unkeys and consumes it first ("Escape
    /// unkeys first", ruled 2026-10-21), so the next Escape asks. And Enter in
    /// an EMPTY new-step box presses OK, as Enter does everywhere else (19:44);
    /// with something typed it still adds the step.</para>
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

        /// <summary>
        /// One of the two lists being edited: its working copy, the step in
        /// force when the dialog opened, and whether the operator has picked
        /// another since.
        /// </summary>
        private sealed class EditedList
        {
            public bool Coarse;
            public ListBox Box = null!;
            public TextBox NewBox = null!;
            public List<int> Hz = new();
            public int CurrentHz;
            public bool Picked;
            public bool Changed;
            public bool Filling;
        }

        private readonly EditedList _coarse;
        private readonly EditedList _fine;

        /// <summary>The chosen coarse step in Hz. Meaningful only when the
        /// dialog returned true.</summary>
        public int CoarseStepHz { get; private set; }

        /// <summary>The chosen fine step in Hz. Meaningful only when the
        /// dialog returned true.</summary>
        public int FineStepHz { get; private set; }

        /// <summary>
        /// The coarse step list to keep, in Hz, when <see cref="StepListsChanged"/>;
        /// null means the starting list. Meaningful only when the dialog
        /// returned true.
        /// </summary>
        public int[]? CoarseStepListHz { get; private set; }

        /// <summary>The fine step list to keep; see <see cref="CoarseStepListHz"/>.</summary>
        public int[]? FineStepListHz { get; private set; }

        /// <summary>True when either list was edited.</summary>
        public bool StepListsChanged { get; private set; }

        /// <param name="currentCoarseHz">The coarse step in force right now.</param>
        /// <param name="currentFineHz">The fine step in force right now.</param>
        public TuningStepsDialog(int currentCoarseHz, int currentFineHz)
        {
            InitializeComponent();

            AutomationProperties.SetName(this, Title);

            CoarseStepHz = currentCoarseHz;
            FineStepHz = currentFineHz;

            _coarse = new EditedList
            {
                Coarse = true, Box = CoarseList, NewBox = CoarseNewBox,
                Hz = TuningSteps.HzOf(TuningSteps.Coarse).ToList(), CurrentHz = currentCoarseHz,
            };
            _fine = new EditedList
            {
                Coarse = false, Box = FineList, NewBox = FineNewBox,
                Hz = TuningSteps.HzOf(TuningSteps.Fine).ToList(), CurrentHz = currentFineHz,
            };

            Label(CoarseNewLabel, CoarseNewBox, "settings.tuning.steps_list_new_coarse");
            Label(FineNewLabel, FineNewBox, "settings.tuning.steps_list_new_fine");
            Caption(CoarseAddButton, "settings.tuning.steps_list_add_coarse");
            Caption(FineAddButton, "settings.tuning.steps_list_add_fine");
            Caption(CoarseStartingButton, "settings.tuning.steps_list_starting_coarse");
            Caption(FineStartingButton, "settings.tuning.steps_list_starting_fine");

            // Enter in a new-step box adds the step; it must not press OK,
            // which would close the window with the typed step lost.
            CoarseNewBox.PreviewKeyDown += (_, e) => AddOnEnter(_coarse, e);
            FineNewBox.PreviewKeyDown += (_, e) => AddOnEnter(_fine, e);

            AskToDropChanges = AskToDrop;

            foreach (var list in new[] { _coarse, _fine })
            {
                var l = list;
                l.Box.SelectionChanged += (_, _) =>
                {
                    if (!l.Filling) l.Picked = true;
                    NameRemoveButton(l);
                };
                // ChoicesIncluding, not the bare list: a picker that omits the
                // value currently in force would show one step while another
                // was running, and would change it the moment OK was pressed.
                Fill(l, l.CurrentHz);
            }
        }

        private static void Label(TextBlock label, TextBox box, string key)
        {
            string text = Lexicon.Get(key);
            label.Text = text;
            AutomationProperties.SetName(box, text);
        }

        private static void Caption(Button button, string key)
        {
            string text = Lexicon.Get(key);
            button.Content = text;
            AutomationProperties.SetName(button, text);
        }

        /// <summary>
        /// The Remove button names the step it would remove, the row selected
        /// (#346): "Remove coarse step" alone left the operator to remember
        /// which row was selected. With nothing selected it says the plain
        /// caption. The words are DRAFT keys, Codex's to word.
        /// </summary>
        private void NameRemoveButton(EditedList list)
        {
            Button button = list.Coarse ? CoarseRemoveButton : FineRemoveButton;
            string text = RemoveCaption(list.Coarse, list.Box.SelectedItem is StepRow row ? row.ToString() : null);
            button.Content = text;
            AutomationProperties.SetName(button, text);
        }

        /// <summary>The Remove button's words for a list, naming the selected step when there is one.</summary>
        internal static string RemoveCaption(bool coarse, string? selectedStep) =>
            selectedStep != null
                ? Lexicon.Get(coarse ? "settings.tuning.steps_list_remove_coarse_named" : "settings.tuning.steps_list_remove_fine_named",
                    ("step", selectedStep))
                : Lexicon.Get(coarse ? "settings.tuning.steps_list_remove_coarse" : "settings.tuning.steps_list_remove_fine");

        /// <summary>
        /// Show a list as it is being edited, with the step in force included
        /// even when it is no longer in the list (J3: it stays in force until
        /// another is picked), selecting <paramref name="selectHz"/>, or the
        /// step in force when that is gone. Programmatic: not a pick.
        /// </summary>
        private static void Fill(EditedList list, int selectHz)
        {
            list.Filling = true;
            try
            {
                list.Box.Items.Clear();
                var choices = TuningSteps.ChoicesIncluding(TuningSteps.ListOf(list.Hz, list.Coarse), list.CurrentHz);
                StepRow? select = null, current = null;
                foreach (var choice in choices)
                {
                    var row = new StepRow(choice);
                    list.Box.Items.Add(row);
                    if (choice.Hz == selectHz) select = row;
                    if (choice.Hz == list.CurrentHz) current = row;
                }
                list.Box.SelectedItem = select ?? current;
                if (list.Box.SelectedItem == null && list.Box.Items.Count > 0) list.Box.SelectedIndex = 0;
            }
            finally { list.Filling = false; }
        }

        /// <summary>The step a list is showing as chosen, or the fallback when
        /// somehow nothing is selected.</summary>
        private static int ChosenHz(ListBox list, int fallbackHz) =>
            list.SelectedItem is StepRow row ? row.Choice.Hz : fallbackHz;

        private static void Say(string text) =>
            ScreenReaderOutput.Speak(text, SpeechIntent.Interrupt, VerbosityLevel.Terse,
                subject: SpeechSubject.TuningStepListEdit);

        private void AddOnEnter(EditedList list, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            if (EnterPressesOk(list.NewBox.Text)) Ok();
            else Add(list);
        }

        /// <summary>
        /// Enter in an empty new-step box presses OK, as Enter does everywhere
        /// else; with something typed it adds the step and the window stays
        /// open (#346, ruled by Noel 2026-11-11 19:44).
        /// </summary>
        internal static bool EnterPressesOk(string? typed) => string.IsNullOrWhiteSpace(typed);

        private void CoarseAddButton_Click(object sender, RoutedEventArgs e) => Add(_coarse);
        private void FineAddButton_Click(object sender, RoutedEventArgs e) => Add(_fine);
        private void CoarseRemoveButton_Click(object sender, RoutedEventArgs e) => Remove(_coarse);
        private void FineRemoveButton_Click(object sender, RoutedEventArgs e) => Remove(_fine);
        private void CoarseStartingButton_Click(object sender, RoutedEventArgs e) => PutStartingBack(_coarse);
        private void FineStartingButton_Click(object sender, RoutedEventArgs e) => PutStartingBack(_fine);

        private void Add(EditedList list)
        {
            if (!TuningSteps.TryParseStep(list.NewBox.Text, out int hz))
            {
                Say(Lexicon.Get("settings.tuning.steps_list_not_a_step"));
                return;
            }
            var (outcome, after) = TuningSteps.Add(list.Hz, hz, list.Coarse);
            string step = TuningSteps.FormatForSpeech(hz);
            switch (outcome)
            {
                case TuningSteps.ListEdit.OutOfRange:
                    Say(Lexicon.Get(list.Coarse ? "settings.tuning.steps_list_coarse_range" : "settings.tuning.steps_list_fine_range"));
                    return;
                case TuningSteps.ListEdit.AlreadyThere:
                    Say(Lexicon.Get("settings.tuning.steps_list_already", ("step", step)));
                    return;
            }
            list.Hz = after.ToList();
            list.Changed = true;
            // Adding a step does not pick it: the selection stays where it was.
            Fill(list, ChosenHz(list.Box, list.CurrentHz));
            list.NewBox.Clear();
            Say(Lexicon.Get("settings.tuning.steps_list_added", ("step", step)));
        }

        private void Remove(EditedList list)
        {
            if (list.Box.SelectedItem is not StepRow row) return;
            int hz = row.Choice.Hz;
            string step = TuningSteps.FormatForSpeech(hz);
            var (outcome, after) = TuningSteps.Remove(list.Hz, hz);
            switch (outcome)
            {
                case TuningSteps.ListEdit.NotInList:
                    Say(Lexicon.Get("settings.tuning.steps_list_not_in_list", ("step", step)));
                    return;
                case TuningSteps.ListEdit.LastStepStays:
                    Say(Lexicon.Get("settings.tuning.steps_list_last_stays"));
                    return;
            }
            list.Hz = after.ToList();
            list.Changed = true;
            // The removed row is gone, so the selection falls back to the step
            // in force, which is still shown when it was the one removed (J3).
            // That is not a pick: OK keeps the step in force.
            list.Picked = false;
            Fill(list, list.CurrentHz);
            Say(Lexicon.Get(hz == list.CurrentHz
                ? "settings.tuning.steps_list_removed_current"
                : "settings.tuning.steps_list_removed", ("step", step)));
        }

        private void PutStartingBack(EditedList list)
        {
            var confirm = new ConfirmActionDialog(
                Lexicon.Get("settings.tuning.steps_list_starting_title"),
                Lexicon.Get(list.Coarse ? "settings.tuning.steps_list_starting_coarse_body" : "settings.tuning.steps_list_starting_fine_body"),
                question: Lexicon.Get("settings.tuning.steps_list_starting_question"));
            if (confirm.ShowDialog() != true) return;
            list.Hz = TuningSteps.HzOf(list.Coarse ? TuningSteps.StartingCoarse : TuningSteps.StartingFine).ToList();
            list.Changed = true;
            Fill(list, ChosenHz(list.Box, list.CurrentHz));
            Say(Lexicon.Get(list.Coarse ? "settings.tuning.steps_list_starting_coarse_back" : "settings.tuning.steps_list_starting_fine_back"));
        }

        /// <summary>The list to keep: null for the starting list, so a later
        /// change to our starting list reaches an operator who never edited.</summary>
        private static int[]? ToKeep(EditedList list) =>
            TuningSteps.IsStartingList(list.Hz, list.Coarse) ? null : list.Hz.ToArray();

        private void OkButton_Click(object sender, RoutedEventArgs e) => Ok();

        private void Ok()
        {
            // A step is changed only by a pick (J3: a removed step in force
            // stays in force until another is picked).
            CoarseStepHz = _coarse.Picked ? ChosenHz(CoarseList, CoarseStepHz) : CoarseStepHz;
            FineStepHz = _fine.Picked ? ChosenHz(FineList, FineStepHz) : FineStepHz;

            StepListsChanged = _coarse.Changed || _fine.Changed;
            CoarseStepListHz = ToKeep(_coarse);
            FineStepListHz = ToKeep(_fine);

            CloseWithResult(true);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseWithResult(false);
        }

        /// <summary>
        /// True once the operator answered Yes to dropping the changes, so the
        /// close that follows does not ask again.
        /// </summary>
        private bool _dropConfirmed;

        /// <summary>
        /// True from a No until the input that asked has finished: a close
        /// through CloseWithResult reaches <see cref="OnClosing"/> twice (the
        /// DialogResult assignment, then its own Close), and one press must ask
        /// once.
        /// </summary>
        private bool _keptOpenThisPress;

        /// <summary>The question, put in a window; replaceable so a test can answer it.</summary>
        internal Func<bool> AskToDropChanges { get; set; }

        /// <summary>
        /// Whether a close must ask first: not on OK (it keeps the edits), not
        /// when no list changed, and not once the operator said to drop them.
        /// Pure, so the windowless tests can hold every branch.
        /// </summary>
        internal static bool AsksBeforeClosing(bool? dialogResult, bool coarseChanged, bool fineChanged, bool dropConfirmed) =>
            dialogResult != true && (coarseChanged || fineChanged) && !dropConfirmed;

        /// <summary>
        /// Every way out lands here (Cancel, Escape, Alt+F4, the title bar), so
        /// the question is asked in one place. True when the window must stay
        /// open: the operator said No, or already said No to this press.
        /// </summary>
        internal bool KeepOpenOnClose(bool? dialogResult)
        {
            if (!AsksBeforeClosing(dialogResult, _coarse.Changed, _fine.Changed, _dropConfirmed)) return false;
            if (_keptOpenThisPress) return true;
            if (AskToDropChanges())
            {
                _dropConfirmed = true;
                return false;
            }
            _keptOpenThisPress = true;
            Dispatcher?.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => _keptOpenThisPress = false));
            return true;
        }

        private bool AskToDrop()
        {
            var confirm = new ConfirmActionDialog(
                Lexicon.Get("settings.tuning.steps_list_drop_title"),
                Lexicon.Get("settings.tuning.steps_list_drop_body"),
                question: Lexicon.Get("settings.tuning.steps_list_drop_question"),
                yesLabel: Lexicon.Get("settings.tuning.steps_list_drop_yes"),
                noLabel: Lexicon.Get("settings.tuning.steps_list_drop_no"))
            {
                Owner = this,
            };
            return confirm.ShowDialog() == true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!e.Cancel && KeepOpenOnClose(DialogResult))
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }
    }
}
