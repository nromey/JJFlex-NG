#nullable enable
using System;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The operator's own step lists (#346 part two), ruled by Noel 2026-11-09
    /// 20:39 and 2026-11-11 11:45 (night question J3, "yes to all five"): two
    /// lists, edited in the list S opens, today's five as the starting list;
    /// a step typed with Hz or kHz; fine 1 Hz to 1 kHz, coarse 10 Hz to 1 MHz;
    /// no duplicates WITHIN a list (no check across lists); kept sorted; the
    /// last step cannot be removed; a removed step in force stays in force.
    /// </summary>
    /// <remarks>
    /// In the settings statics collection, with <see cref="TuningStepsTests"/>,
    /// because one test sets <see cref="TuningSteps.OperatorCoarse"/>, which
    /// is process-wide; that class is the only other reader of the ladder in
    /// this project.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TuningStepListTests
    {
        public TuningStepListTests() => Lexicon.Load(Lexicon.Partitions);

        [Theory]
        [InlineData("250 Hz", 250)]
        [InlineData("250hz", 250)]
        [InlineData("2.5 kHz", 2500)]
        [InlineData("10 KHZ", 10000)]
        [InlineData(" 1000 kHz ", 1000000)]
        public void A_step_is_a_number_with_hz_or_khz(string typed, int hz)
        {
            Assert.True(TuningSteps.TryParseStep(typed, out int got));
            Assert.Equal(hz, got);
        }

        [Theory]
        [InlineData("500")]          // no unit: hertz or kilohertz?
        [InlineData("1 MHz")]        // the ruling names Hz and kHz
        [InlineData("1.5 Hz")]       // not a whole hertz
        [InlineData("0.0005 kHz")]   // not a whole hertz
        [InlineData("0 Hz")]
        [InlineData("-5 Hz")]
        [InlineData("five Hz")]
        [InlineData("")]
        [InlineData(null)]
        public void Anything_else_is_not_a_step(string? typed)
        {
            Assert.False(TuningSteps.TryParseStep(typed, out _));
        }

        /// <summary>
        /// A very long pasted number is refused, not thrown (#346): one near
        /// decimal's limit used to overflow the kHz multiply and throw out of
        /// the S picker's Add.
        /// </summary>
        [Theory]
        [InlineData("79228162514264337593543950335 kHz")]
        [InlineData("79228162514264337593543950335.5 khz")]
        [InlineData("10000000000000000000000000 kHz")]
        [InlineData("2147484 kHz")]
        [InlineData("2147483648 Hz")]
        [InlineData("999999999999999999999999999999999999 Hz")]
        public void A_number_too_large_for_any_step_is_refused_not_thrown(string typed)
        {
            Assert.False(TuningSteps.TryParseStep(typed, out int hz));
            Assert.Equal(0, hz);
        }

        [Fact]
        public void Each_list_keeps_its_own_range()
        {
            var coarse = TuningSteps.HzOf(TuningSteps.StartingCoarse);
            var fine = TuningSteps.HzOf(TuningSteps.StartingFine);
            Assert.Equal(TuningSteps.ListEdit.OutOfRange, TuningSteps.Add(coarse, 9, coarse: true).Outcome);
            Assert.Equal(TuningSteps.ListEdit.Added, TuningSteps.Add(coarse, 10, coarse: true).Outcome);
            Assert.Equal(TuningSteps.ListEdit.Added, TuningSteps.Add(coarse, 1000000, coarse: true).Outcome);
            Assert.Equal(TuningSteps.ListEdit.OutOfRange, TuningSteps.Add(coarse, 1000001, coarse: true).Outcome);
            Assert.Equal(TuningSteps.ListEdit.Added, TuningSteps.Add(fine, 2, coarse: false).Outcome);
            Assert.Equal(TuningSteps.ListEdit.Added, TuningSteps.Add(fine, 1000, coarse: false).Outcome);
            Assert.Equal(TuningSteps.ListEdit.OutOfRange, TuningSteps.Add(fine, 1001, coarse: false).Outcome);
            Assert.Equal(TuningSteps.ListEdit.OutOfRange, TuningSteps.Add(fine, 0, coarse: false).Outcome);
        }

        /// <summary>
        /// No duplicate within a list, kept sorted; and NO check across lists
        /// (Noel, 2026-11-11 11:45: "I'd allow that"). 100 Hz is in the fine
        /// starting list and may be added to the coarse one.
        /// </summary>
        [Fact]
        public void A_step_is_added_in_order_never_twice_in_one_list_and_may_sit_in_both()
        {
            var coarse = TuningSteps.HzOf(TuningSteps.StartingCoarse);
            var (outcome, after) = TuningSteps.Add(coarse, 3000, coarse: true);
            Assert.Equal(TuningSteps.ListEdit.Added, outcome);
            Assert.Equal(new[] { 500, 1000, 2000, 3000, 5000, 10000 }, after);
            Assert.Equal(TuningSteps.ListEdit.AlreadyThere, TuningSteps.Add(after, 3000, coarse: true).Outcome);

            Assert.Contains(100, TuningSteps.HzOf(TuningSteps.StartingFine));
            Assert.Equal(TuningSteps.ListEdit.Added, TuningSteps.Add(coarse, 100, coarse: true).Outcome);
        }

        [Fact]
        public void The_last_step_stays_and_only_a_listed_step_is_removed()
        {
            var (outcome, after) = TuningSteps.Remove(new[] { 500, 1000 }, 500);
            Assert.Equal(TuningSteps.ListEdit.Removed, outcome);
            Assert.Equal(new[] { 1000 }, after);
            Assert.Equal(TuningSteps.ListEdit.LastStepStays, TuningSteps.Remove(after, 1000).Outcome);
            Assert.Equal(new[] { 1000 }, TuningSteps.Remove(after, 1000).List);
            Assert.Equal(TuningSteps.ListEdit.NotInList, TuningSteps.Remove(after, 7).Outcome);
        }

        /// <summary>
        /// A removed step that is in force stays in force (J3): the ladder
        /// still walks from it, and the pickers still show it, through the
        /// rules that already handle an off-list value.
        /// </summary>
        [Fact]
        public void A_removed_step_in_force_is_still_shown_and_walked_from()
        {
            var list = TuningSteps.ListOf(new[] { 500, 2000, 10000 }, coarse: true);
            Assert.Contains(TuningSteps.ChoicesIncluding(list, 5000), c => c.Hz == 5000);
            Assert.Equal(10000, TuningSteps.Step(list, 5000, 1).Hz);
            Assert.Equal(2000, TuningSteps.Step(list, 5000, -1).Hz);
        }

        /// <summary>
        /// A stored list is read back within range, without duplicates and
        /// sorted, keeping the starting labels; nothing usable means the
        /// starting list, so a list is never empty.
        /// </summary>
        [Fact]
        public void A_stored_list_is_read_back_tidy_and_never_empty()
        {
            Assert.Same(TuningSteps.StartingCoarse, TuningSteps.ListOf(null, coarse: true));
            Assert.Same(TuningSteps.StartingFine, TuningSteps.ListOf(Array.Empty<int>(), coarse: false));
            Assert.Same(TuningSteps.StartingFine, TuningSteps.ListOf(new[] { 5000, 0 }, coarse: false));
            var read = TuningSteps.ListOf(new[] { 5000, 250, 5000, 2000000 }, coarse: true);
            Assert.Equal(new[] { 250, 5000 }, read.Select(c => c.Hz));
            Assert.Equal("settings.tuning.coarse_step_5_khz", read[1].LabelKey);
            Assert.Equal("250 hertz", TuningSteps.LabelFor(read[0]));
            Assert.True(TuningSteps.IsStartingList(new[] { 10000, 500, 1000, 2000, 5000 }, coarse: true));
            Assert.False(TuningSteps.IsStartingList(new[] { 500 }, coarse: true));
        }

        /// <summary>
        /// THE rule: the operator's list FEEDS the one table. With the
        /// application's reader set, <see cref="TuningSteps.Coarse"/>, which
        /// the ladder keys, the picker and Settings all read, is the operator's
        /// list; a reader that throws, or no reader, gives the starting list.
        /// </summary>
        [Fact]
        public void The_operators_list_is_the_one_the_ladder_and_the_pickers_read()
        {
            var before = TuningSteps.OperatorCoarse;
            try
            {
                TuningSteps.OperatorCoarse = () => new[] { 250, 3000 };
                Assert.Equal(new[] { 250, 3000 }, TuningSteps.Coarse.Select(c => c.Hz));
                Assert.Equal(3000, TuningSteps.Step(TuningSteps.Coarse, 250, 1).Hz);
                Assert.Same(TuningSteps.StartingFine, TuningSteps.Fine);   // the other list is its own

                TuningSteps.OperatorCoarse = () => null;
                Assert.Same(TuningSteps.StartingCoarse, TuningSteps.Coarse);
                TuningSteps.OperatorCoarse = () => throw new InvalidOperationException("no record");
                Assert.Same(TuningSteps.StartingCoarse, TuningSteps.Coarse);
            }
            finally { TuningSteps.OperatorCoarse = before; }
            Assert.Same(TuningSteps.StartingCoarse, TuningSteps.Coarse);
        }

        private static string Src(params string[] parts) =>
            System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { IntegrationPassTree.Root }.Concat(parts).ToArray()))
                .Replace("\r\n", "\n");

        /// <summary>
        /// The wiring, from source: the app hands the table the CURRENT
        /// operator's lists, the step memory keeps them on that record, the
        /// picker's lists are noted before its steps are applied, and inside
        /// the picker Enter in a new-step box adds rather than pressing OK,
        /// and putting a starting list back asks first.
        /// </summary>
        [Fact]
        public void The_lists_are_wired_from_the_operator_record_through_the_picker()
        {
            string app = Src("ApplicationEvents.vb");
            Assert.Contains("Radios.TuningSteps.OperatorCoarse =", app, StringComparison.Ordinal);
            Assert.Contains("Return If(CurrentOp Is Nothing, Nothing, CurrentOp.CoarseStepList)", app, StringComparison.Ordinal);
            Assert.Contains("Return If(CurrentOp Is Nothing, Nothing, CurrentOp.FineStepList)", app, StringComparison.Ordinal);
            Assert.Contains("op.CoarseStepList = coarseList", app, StringComparison.Ordinal);
            Assert.Contains("op.FineStepList = fineList", app, StringComparison.Ordinal);

            string window = Src("JJFlexWpf", "MainWindow.xaml.cs");
            int note = window.IndexOf("StepMemory?.NoteLists(dialog.CoarseStepListHz, dialog.FineStepListHz)", StringComparison.Ordinal);
            int apply = window.IndexOf("_freqOutHandlers.ApplyStepSizes(dialog.CoarseStepHz, dialog.FineStepHz, speak: true);", StringComparison.Ordinal);
            Assert.True(note > 0 && apply > note, "the lists are noted before the steps are applied");

            string dialog = Src("JJFlexWpf", "Dialogs", "TuningStepsDialog.xaml.cs");
            // Enter adds the typed step; in an EMPTY box it presses OK (#346,
            // ruled 2026-11-11 19:44; until then Enter always added).
            Assert.Contains("if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;\n            e.Handled = true;\n            if (EnterPressesOk(list.NewBox.Text)) Ok();\n            else Add(list);", dialog, StringComparison.Ordinal);
            int ask = dialog.IndexOf("if (confirm.ShowDialog() != true) return;", StringComparison.Ordinal);
            int reset = dialog.IndexOf("list.Hz = TuningSteps.HzOf(list.Coarse ? TuningSteps.StartingCoarse : TuningSteps.StartingFine).ToList();", StringComparison.Ordinal);
            Assert.True(ask > 0 && reset > ask, "putting the starting list back asks first");
            // A step changes only by a pick, so a removed step in force stays.
            Assert.Contains("CoarseStepHz = _coarse.Picked ? ChosenHz(CoarseList, CoarseStepHz) : CoarseStepHz;", dialog, StringComparison.Ordinal);
            // The picker and Settings read the one table.
            Assert.Contains("Hz = TuningSteps.HzOf(TuningSteps.Coarse).ToList()", dialog, StringComparison.Ordinal);
            Assert.Contains("TuningSteps.ChoicesIncluding(TuningSteps.Coarse", Src("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"), StringComparison.Ordinal);
        }
    }
}
