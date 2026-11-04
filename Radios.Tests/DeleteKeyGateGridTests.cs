using System;
using System.Collections.Generic;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// One press, one removal, under EVERY supported keyboard setting (#774,
    /// repair round 2). Codex's re-check found the gap was sized from the
    /// repeat DELAY alone: at delay setting 0 it was 375 ms, while the slowest
    /// repeat SPEED repeats every 400 ms, and the WinForms lists mark no
    /// repeats, so a held Delete removed on four downs of five. These replay
    /// the whole grid of delay settings 0-3 and speed settings 0-31 through
    /// the gate the adapters actually build.
    /// </summary>
    public sealed class DeleteKeyGateGridTests
    {
        private static DeleteKeyGate AdapterGate(int delaySetting, int speedSetting) =>
            new DeleteKeyGate(DeleteKeyGate.GapForKeyboard(
                KeyRepeatTiming.DelayMsFromSetting(delaySetting),
                KeyRepeatTiming.RepeatPeriodMsFromSetting(speedSetting)));

        public static IEnumerable<object[]> Grid()
        {
            for (int d = 0; d <= 3; d++)
                for (int s = 0; s <= 31; s++)
                    yield return new object[] { d, s };
        }

        [Theory]
        [MemberData(nameof(Grid))]
        public void A_held_key_that_WinForms_reports_without_a_repeat_flag_presses_once(int delaySetting, int speedSetting)
        {
            int delay = KeyRepeatTiming.DelayMsFromSetting(delaySetting);
            int period = KeyRepeatTiming.RepeatPeriodMsFromSetting(speedSetting);
            var g = AdapterGate(delaySetting, speedSetting);
            int presses = 0;
            if (g.Down(false, 0)) presses++;
            for (long t = delay; t <= 6000; t += period)
                if (g.Down(false, t)) presses++;
            Assert.Equal(1, presses);
        }

        [Theory]
        [MemberData(nameof(Grid))]
        public void A_held_key_that_WPF_reports_as_repeats_presses_once(int delaySetting, int speedSetting)
        {
            int delay = KeyRepeatTiming.DelayMsFromSetting(delaySetting);
            int period = KeyRepeatTiming.RepeatPeriodMsFromSetting(speedSetting);
            var g = AdapterGate(delaySetting, speedSetting);
            int presses = 0;
            if (g.Down(false, 0)) presses++;
            for (long t = delay; t <= 6000; t += period)
                if (g.Down(true, t)) presses++;
            Assert.Equal(1, presses);
        }

        [Theory]
        [MemberData(nameof(Grid))]
        public void A_held_key_under_jaws_pairs_presses_once(int delaySetting, int speedSetting)
        {
            // JAWS: the first pair at the repeat delay, then pairs about 250 ms
            // apart, each a fresh-looking down with its own up.
            int delay = KeyRepeatTiming.DelayMsFromSetting(delaySetting);
            var g = AdapterGate(delaySetting, speedSetting);
            int presses = 0;
            if (g.Down(false, 0)) presses++;
            g.Up(5);
            for (long t = delay; t <= 6000; t += 250)
            {
                if (g.Down(false, t)) presses++;
                g.Up(t + 5);
            }
            Assert.Equal(1, presses);
        }

        [Theory]
        [MemberData(nameof(Grid))]
        public void A_release_then_a_press_after_the_gap_presses_again(int delaySetting, int speedSetting)
        {
            // Positive control: the gap is finite under every setting.
            var g = AdapterGate(delaySetting, speedSetting);
            Assert.True(g.Down(false, 0));
            g.Up(80);
            Assert.True(g.Down(false, 80 + g.ReleaseGapMs + 1));
        }

        [Fact]
        public void The_gap_covers_the_delay_and_the_period_and_the_jaws_pair_spacing()
        {
            Assert.Equal(750, DeleteKeyGate.GapForKeyboard(500, 33));
            Assert.Equal(600, DeleteKeyGate.GapForKeyboard(250, 400));   // the case Codex replayed
            Assert.Equal(1500, DeleteKeyGate.GapForKeyboard(1000, 400));
            Assert.Equal(375, DeleteKeyGate.GapForKeyboard(250, 33));    // JAWS pairs 250 ms apart
        }

        [Fact]
        public void Downs_with_no_up_between_them_are_one_hold_even_past_a_short_gap()
        {
            // The second layer, on its own: a gate whose gap is SHORTER than the
            // repeat period still treats downs with no up between them as one
            // hold, because a key cannot go down twice without coming up unless
            // it is repeating. This is what tracking down-without-up gives a
            // WinForms list, which has no repeat flag.
            var g = new DeleteKeyGate(375);
            int presses = 0;
            if (g.Down(false, 0)) presses++;
            for (long t = 250; t <= 4000; t += 400)
                if (g.Down(false, t)) presses++;
            Assert.Equal(1, presses);
            // ...and an up lost to another window is not waited for forever.
            Assert.True(g.Down(false, 4000 + DeleteKeyGate.HeldWithoutUpMs + 1));
        }

        // ── A deliberate second press, and a hold across a question ──

        [Fact]
        public void Another_key_after_the_release_makes_the_next_Delete_a_new_press()
        {
            // The operator let go, moved to another item, and pressed Delete
            // again inside the gap: a new intent, not a held key.
            var g = AdapterGate(1, 15);
            Assert.True(g.Down(false, 0));
            g.Up(80);
            g.OtherInput(150);      // Down arrow to the next item
            Assert.True(g.Down(false, 300));
        }

        [Fact]
        public void Another_key_while_Delete_is_still_held_does_not_end_the_hold()
        {
            var g = AdapterGate(1, 15);
            Assert.True(g.Down(false, 0));
            g.OtherInput(100);      // no up seen: Delete is still down
            Assert.False(g.Down(true, 500));
            Assert.False(g.Down(false, 560));
        }

        [Fact]
        public void Jaws_pairs_with_no_other_key_between_them_stay_one_hold()
        {
            // The control for the rule above: Delete's own pairs never count
            // as another key.
            var g = AdapterGate(1, 15);
            Assert.True(g.Down(false, 0));
            g.Up(5);
            Assert.False(g.Down(false, 512));
            g.Up(517);
            Assert.False(g.Down(false, 762));
        }

        [Fact]
        public void A_held_key_across_a_question_cannot_act_again_after_it_closes()
        {
            // The press opens a confirmation; the list loses focus while Delete
            // is still held; the question is answered two seconds later, and
            // the held key's next down reaches the list again.
            var g = AdapterGate(1, 15);
            Assert.True(g.Down(false, 0));
            g.Interrupted(20);       // the question took focus
            g.Resumed(2000);         // and gave it back
            Assert.False(g.Down(false, 2100));   // a JAWS pair of the same hold
            g.Up(2105);
            Assert.False(g.Down(false, 2350));
        }

        [Fact]
        public void A_genuine_repeat_across_a_question_cannot_act_again_either()
        {
            var g = AdapterGate(1, 31);
            Assert.True(g.Down(false, 0));
            g.Interrupted(20);
            g.Resumed(2000);
            Assert.False(g.Down(false, 2033));   // WinForms: no repeat flag, no up
            Assert.False(g.Down(false, 2066));
        }

        [Fact]
        public void A_fresh_press_well_after_the_question_closes_presses()
        {
            var g = AdapterGate(1, 15);
            Assert.True(g.Down(false, 0));
            g.Interrupted(20);
            g.Resumed(2000);
            Assert.True(g.Down(false, 2000 + g.ReleaseGapMs + 1));
        }

        [Fact]
        public void The_WinForms_lists_own_decision_presses_once_on_a_slow_repeat_and_again_after_an_arrow()
        {
            // Codex's replay, through the adapter's decision: delay setting 0,
            // speed setting 0, downs at 0, 250, 650, 1050 and 1450 with no up.
            var g = AdapterGate(0, 0);
            int presses = 0;
            foreach (long t in new long[] { 0, 250, 650, 1050, 1450 })
                if (WinFormsDeleteKey.Decide(g, bareDelete: true, t)) presses++;
            Assert.Equal(1, presses);
            g.Up(1460);
            Assert.False(WinFormsDeleteKey.Decide(g, bareDelete: false, 1500));   // Down arrow
            Assert.True(WinFormsDeleteKey.Decide(g, bareDelete: true, 1600));     // a new press
        }

        [Fact]
        public void Both_adapters_size_the_gap_from_the_period_and_watch_focus_and_other_input()
        {
            string root = Root();
            string winforms = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "Radios", "DeleteKeyGate.cs"));
            Assert.Contains("SystemInformation.KeyboardSpeed", winforms);
            Assert.Contains("list.LostFocus += (_, _) => gate.Interrupted(", winforms);
            Assert.Contains("list.GotFocus += (_, _) => gate.Resumed(", winforms);
            Assert.Contains("list.MouseDown += (_, _) => gate.OtherInput(", winforms);
            string wpf = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "JJFlexWpf", "DeleteKeyRemoves.cs"));
            Assert.Contains("SystemParameters.KeyboardSpeed", wpf);
            Assert.Contains("list.IsKeyboardFocusWithinChanged += OnFocusWithinChanged;", wpf);
            Assert.Contains("list.PreviewMouseDown += OnPreviewMouseDown;", wpf);
        }

        private static string Root()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        [Fact]
        public void Leaving_the_list_after_the_hold_ended_holds_nothing_back()
        {
            // Focus moving when no Delete hold is live is not a held key: a
            // press on returning is a press.
            var g = AdapterGate(1, 15);
            Assert.True(g.Down(false, 0));
            g.Up(80);
            g.Interrupted(5000);
            g.Resumed(9000);
            g.OtherInput(9050);
            Assert.True(g.Down(false, 9100));
        }
    }
}
