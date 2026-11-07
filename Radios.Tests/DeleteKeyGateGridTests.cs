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

        // ── Repair round 3: a click is new intent only once Delete is really up ──

        /// <summary>
        /// Codex's round-two replay, through the WinForms list's own decisions
        /// (ruled by Noel 2026-11-04: a click counts as a new intent only once
        /// Delete has really been released). Delay setting 1 (500 ms), gap
        /// 750: a press, the first JAWS pair's synthetic up at 5, a click on
        /// the list at 200 while the key is still physically held, then the
        /// hold's next pair at 512. A click is not a keystroke, so it cannot
        /// stop the held key's stream, and that next pair is the same hold.
        /// </summary>
        [Fact]
        public void A_click_during_a_jaws_hold_does_not_make_its_next_pair_a_press_on_a_WinForms_list()
        {
            var g = AdapterGate(1, 31);
            Assert.Equal(750, g.ReleaseGapMs);
            Assert.True(WinFormsDeleteKey.Decide(g, bareDelete: true, 0));
            g.Up(5);
            WinFormsDeleteKey.Clicked(g, 200);
            Assert.False(WinFormsDeleteKey.Decide(g, bareDelete: true, 512));
            g.Up(517);
            Assert.False(WinFormsDeleteKey.Decide(g, bareDelete: true, 762));
        }

        [Fact]
        public void A_click_after_Delete_was_really_released_leaves_the_next_press_a_press()
        {
            // The control: released (an up, then quiet past the gap), a click on
            // another item, a press. And round two's keyboard route still ends
            // a hold at once after an up, because another key stops the held
            // key's repeats.
            var g = AdapterGate(1, 31);
            Assert.True(WinFormsDeleteKey.Decide(g, bareDelete: true, 0));
            g.Up(80);
            WinFormsDeleteKey.Clicked(g, 80 + g.ReleaseGapMs + 10);
            Assert.True(WinFormsDeleteKey.Decide(g, bareDelete: true, 80 + g.ReleaseGapMs + 50));

            var k = AdapterGate(1, 31);
            Assert.True(WinFormsDeleteKey.Decide(k, bareDelete: true, 0));
            k.Up(80);
            Assert.False(WinFormsDeleteKey.Decide(k, bareDelete: false, 150));   // Down arrow
            Assert.True(WinFormsDeleteKey.Decide(k, bareDelete: true, 300));
        }

        /// <summary>
        /// Codex's third re-check, reproduced through the WinForms list's own
        /// key decision (repair round 4): a press, the first JAWS pair's
        /// synthetic up at 5, a modifier tapped at 200 while Delete is still
        /// physically held, then the hold's next pair at 512. A modifier does
        /// not stop the held key's stream, so it is not a new intent and the
        /// next pair is the same hold. Every modifier, either side.
        /// </summary>
        [Theory]
        [InlineData(System.Windows.Forms.Keys.ShiftKey, System.Windows.Forms.Keys.Shift)]
        [InlineData(System.Windows.Forms.Keys.LShiftKey, System.Windows.Forms.Keys.Shift)]
        [InlineData(System.Windows.Forms.Keys.ControlKey, System.Windows.Forms.Keys.Control)]
        [InlineData(System.Windows.Forms.Keys.RControlKey, System.Windows.Forms.Keys.Control)]
        [InlineData(System.Windows.Forms.Keys.Menu, System.Windows.Forms.Keys.Alt)]
        [InlineData(System.Windows.Forms.Keys.LMenu, System.Windows.Forms.Keys.Alt)]
        [InlineData(System.Windows.Forms.Keys.LWin, System.Windows.Forms.Keys.None)]
        [InlineData(System.Windows.Forms.Keys.RWin, System.Windows.Forms.Keys.None)]
        [InlineData(System.Windows.Forms.Keys.Insert, System.Windows.Forms.Keys.None)]
        [InlineData(System.Windows.Forms.Keys.CapsLock, System.Windows.Forms.Keys.None)]
        // Every remaining arm of the classification, one row each (Codex's
        // fourth re-check: removing an arm no row named left the suite green).
        [InlineData(System.Windows.Forms.Keys.RShiftKey, System.Windows.Forms.Keys.Shift)]
        [InlineData(System.Windows.Forms.Keys.LControlKey, System.Windows.Forms.Keys.Control)]
        [InlineData(System.Windows.Forms.Keys.RMenu, System.Windows.Forms.Keys.Alt)]
        [InlineData(System.Windows.Forms.Keys.NumLock, System.Windows.Forms.Keys.None)]
        [InlineData(System.Windows.Forms.Keys.Scroll, System.Windows.Forms.Keys.None)]
        public void A_modifier_tapped_during_a_jaws_hold_does_not_make_its_next_pair_a_press_on_a_WinForms_list(
            System.Windows.Forms.Keys modifierKey, System.Windows.Forms.Keys modifiers)
        {
            var g = AdapterGate(1, 31);
            Assert.Equal(750, g.ReleaseGapMs);
            Assert.True(WinFormsDeleteKey.DecideKey(g, System.Windows.Forms.Keys.Delete, System.Windows.Forms.Keys.None, 0));
            g.Up(5);
            Assert.False(WinFormsDeleteKey.DecideKey(g, modifierKey, modifiers, 200));
            Assert.False(WinFormsDeleteKey.DecideKey(g, System.Windows.Forms.Keys.Delete, System.Windows.Forms.Keys.None, 512));
            g.Up(517);
            Assert.False(WinFormsDeleteKey.DecideKey(g, System.Windows.Forms.Keys.Delete, System.Windows.Forms.Keys.None, 762));
        }

        /// <summary>
        /// The controls for the modifier rule: a modifier after Delete was
        /// really released, then a press, presses; and round two's arrow on the
        /// next item still makes a prompt press a press, through the same key
        /// decision.
        /// </summary>
        [Fact]
        public void A_modifier_after_a_real_release_and_an_arrow_after_an_up_still_leave_the_next_press_a_press()
        {
            var g = AdapterGate(1, 31);
            Assert.True(WinFormsDeleteKey.DecideKey(g, System.Windows.Forms.Keys.Delete, System.Windows.Forms.Keys.None, 0));
            g.Up(80);
            Assert.False(WinFormsDeleteKey.DecideKey(g, System.Windows.Forms.Keys.ShiftKey, System.Windows.Forms.Keys.Shift, 80 + g.ReleaseGapMs + 10));
            Assert.True(WinFormsDeleteKey.DecideKey(g, System.Windows.Forms.Keys.Delete, System.Windows.Forms.Keys.None, 80 + g.ReleaseGapMs + 50));

            var k = AdapterGate(1, 31);
            Assert.True(WinFormsDeleteKey.DecideKey(k, System.Windows.Forms.Keys.Delete, System.Windows.Forms.Keys.None, 0));
            k.Up(80);
            Assert.False(WinFormsDeleteKey.DecideKey(k, System.Windows.Forms.Keys.Down, System.Windows.Forms.Keys.None, 150));
            Assert.True(WinFormsDeleteKey.DecideKey(k, System.Windows.Forms.Keys.Delete, System.Windows.Forms.Keys.None, 300));
        }

        /// <summary>
        /// A held Delete with Alt held over it for longer than the gap (Codex's
        /// sustained non-qualifying pairs): each modified pair continues the
        /// hold, so the bare pair after them is still the same hold. The
        /// control: the same history with nothing between is a release.
        /// </summary>
        [Fact]
        public void Modified_Delete_pairs_held_past_the_gap_keep_the_next_bare_pair_held_on_a_WinForms_list()
        {
            var g = AdapterGate(1, 31);
            Assert.True(WinFormsDeleteKey.DecideKey(g, System.Windows.Forms.Keys.Delete, System.Windows.Forms.Keys.None, 0));
            g.Up(5);
            Assert.False(WinFormsDeleteKey.DecideKey(g, System.Windows.Forms.Keys.Menu, System.Windows.Forms.Keys.Alt, 200));
            foreach (long t in new long[] { 512, 762, 1012, 1262 })
            {
                Assert.False(WinFormsDeleteKey.DecideKey(g, System.Windows.Forms.Keys.Delete, System.Windows.Forms.Keys.Alt, t));
                g.Up(t + 5);
            }
            Assert.False(WinFormsDeleteKey.DecideKey(g, System.Windows.Forms.Keys.Delete, System.Windows.Forms.Keys.None, 1512));

            var quiet = AdapterGate(1, 31);
            Assert.True(WinFormsDeleteKey.DecideKey(quiet, System.Windows.Forms.Keys.Delete, System.Windows.Forms.Keys.None, 0));
            quiet.Up(5);
            Assert.True(WinFormsDeleteKey.DecideKey(quiet, System.Windows.Forms.Keys.Delete, System.Windows.Forms.Keys.None, 1512));
        }

        [Fact]
        public void A_click_during_a_hold_with_no_up_yet_changes_nothing()
        {
            var g = AdapterGate(1, 31);
            Assert.True(g.Down(false, 0));
            WinFormsDeleteKey.Clicked(g, 100);
            Assert.False(g.Down(false, 500));
        }

        [Fact]
        public void A_release_then_a_long_idle_then_leaving_and_returning_lets_the_next_press_press()
        {
            // Codex's coverage note: the interruption's own expiry, with no
            // other input to clear a wrongly kept hold.
            var g = AdapterGate(1, 15);
            Assert.True(g.Down(false, 0));
            g.Up(80);
            g.Interrupted(5000);
            g.Resumed(9000);
            Assert.True(g.Down(false, 9100));
        }

        [Fact]
        public void Both_adapters_size_the_gap_from_the_period_and_watch_focus_and_other_input()
        {
            string root = Root();
            string winforms = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "Radios", "DeleteKeyGate.cs"));
            Assert.Contains("SystemInformation.KeyboardSpeed", winforms);
            Assert.Contains("list.LostFocus += (_, _) => gate.Interrupted(", winforms);
            Assert.Contains("list.GotFocus += (_, _) => gate.Resumed(", winforms);
            Assert.Contains("list.MouseDown += (_, _) => Clicked(gate, ", winforms);
            // The key handler decides by the real key, so a modifier alone is
            // not another key (repair round 4).
            Assert.Contains("bool press = DecideKey(gate, e.KeyCode, e.Modifiers, Environment.TickCount64);", winforms);
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
