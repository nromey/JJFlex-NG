using Radios;
using Xunit;
using Down = Radios.LockPressClassifier.DownAction;
using Up = Radios.LockPressClassifier.UpAction;

namespace Radios.Tests
{
    /// <summary>
    /// The transmit lock's press classifier (#778c repair round two): a held
    /// Shift+Space toggles once under both delivery shapes, and a deliberate
    /// press is never swallowed, because nothing here guesses whether the key
    /// is still held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The JAWS timelines are the MEASURED ones from
    /// <see cref="PttHoldFilterTests"/>: the first synthesised pair at the
    /// Windows repeat delay (517 ms in the cold stream), later pairs about
    /// 250 ms apart, each up 3 to 5 ms after its down; and the other measured
    /// shape of a first press, an up 3 ms after the down and the re-down at
    /// 501 ms (JJFlexRadioTrace-20260826-181039.txt, 312397 to 312898). Both
    /// are replayed. NVDA passes a real hold through: one down, repeats the
    /// callers drop, one up.
    /// </para>
    /// <para>
    /// There is NO measured JAWS tap in this tree or the memory: nothing
    /// records how JAWS delivers a quick Shift+Space press. If it delivers a
    /// tap as a 3 ms pair, the pending-press rule ignores it when it follows
    /// another press inside the window; a first tap after a quiet gap toggles
    /// at its down regardless. That is the first press on the JAWS bench.
    /// </para>
    /// </remarks>
    public class LockPressClassifierTests
    {
        private static LockPressClassifier Fresh()
        {
            var c = new LockPressClassifier();
            c.SetKeyRepeatDelay(500);
            return c;
        }

        // ── NVDA and plain Windows: one down, one up ────────────────────

        [Fact]
        public void A_real_hold_toggles_at_its_down_and_its_up_does_nothing()
        {
            var c = Fresh();
            Assert.Equal(Down.Toggle, c.NoteDown(0));
            Assert.Equal(Up.Nothing, c.NoteUp(2000));
        }

        [Fact]
        public void A_quick_tap_after_a_quiet_gap_toggles_at_once_whatever_its_length()
        {
            // The everyday press keeps its feel: decided at the down, not the up.
            var c = Fresh();
            Assert.Equal(Down.Toggle, c.NoteDown(5_000));
            Assert.Equal(Up.Nothing, c.NoteUp(5_080));
        }

        // ── JAWS: the measured streams ──────────────────────────────────

        [Fact]
        public void The_measured_cold_jaws_stream_toggles_once()
        {
            // As the edges see it (PttHoldFilterTests.The_measured_jaws_stream_arms_on_the_second_pair).
            var c = Fresh();
            int toggles = 0;
            void Tally(Down d) { if (d == Down.Toggle) toggles++; }
            void TallyUp(Up u) { if (u == Up.Toggle) toggles++; }

            Tally(c.NoteDown(0));
            TallyUp(c.NoteUp(517));
            Assert.Equal(Down.Pending, c.NoteDown(762));
            Assert.Equal(Up.Nothing, c.NoteUp(767));
            long t = 1012;
            for (int i = 0; i < 8; i++)
            {
                Assert.Equal(Down.Pending, c.NoteDown(t));
                Assert.Equal(Up.Nothing, c.NoteUp(t + 5));
                t += 250;
            }
            Assert.Equal(1, toggles);
        }

        [Fact]
        public void A_re_down_with_no_up_between_is_ignored_like_a_repeat()
        {
            // The raw shape behind the cold stream: the first pair's down at
            // the repeat delay lands while the key is still down. Push to talk
            // swallows it with its repeat guard; the lock has no such guard,
            // so the classifier must.
            var c = Fresh();
            Assert.Equal(Down.Toggle, c.NoteDown(0));
            Assert.Equal(Down.Ignore, c.NoteDown(512));
            Assert.Equal(Up.Nothing, c.NoteUp(517));
            Assert.Equal(Down.Pending, c.NoteDown(762));
            Assert.Equal(Up.Nothing, c.NoteUp(767));
        }

        [Fact]
        public void The_other_measured_first_press_shape_toggles_once()
        {
            // Up 3 ms after the first down, re-down at 501 (the 2026-08-26 trace).
            var c = Fresh();
            Assert.Equal(Down.Toggle, c.NoteDown(312_397));
            Assert.Equal(Up.Nothing, c.NoteUp(312_400));
            Assert.Equal(Down.Pending, c.NoteDown(312_898));
            Assert.Equal(Up.Nothing, c.NoteUp(312_901));
            Assert.Equal(Down.Pending, c.NoteDown(313_148));
            Assert.Equal(Up.Nothing, c.NoteUp(313_153));
        }

        [Fact]
        public void A_deliberate_tap_straight_after_a_synthesised_stream_toggles_at_its_up()
        {
            // Codex B2: five pairs, then a 120 ms press 133 ms after the last
            // up. Round one's hold filter swallowed it; this must not.
            var c = Fresh();
            foreach (long at in new long[] { 100_000, 100_512, 100_762, 101_012, 101_262 })
            {
                c.NoteDown(at);
                c.NoteUp(at + 5);
            }
            Assert.Equal(Down.Pending, c.NoteDown(101_400));
            Assert.Equal(Up.Toggle, c.NoteUp(101_520));
        }

        // ── Two real presses ────────────────────────────────────────────

        [Fact]
        public void Two_taps_300_ms_apart_toggle_twice_the_second_at_its_up()
        {
            var c = Fresh();
            Assert.Equal(Down.Toggle, c.NoteDown(0));
            Assert.Equal(Up.Nothing, c.NoteUp(120));
            Assert.Equal(Down.Pending, c.NoteDown(300));
            Assert.Equal(Up.Toggle, c.NoteUp(420));
        }

        [Fact]
        public void Two_taps_well_apart_both_toggle_at_their_downs()
        {
            var c = Fresh();
            Assert.Equal(Down.Toggle, c.NoteDown(0));
            c.NoteUp(120);
            Assert.Equal(Down.Toggle, c.NoteDown(2_000));
            Assert.Equal(Up.Nothing, c.NoteUp(2_120));
        }

        // ── Lost ups, and doors with none ───────────────────────────────

        [Fact]
        public void A_pending_press_whose_up_never_arrives_does_not_toggle_and_the_next_press_is_fresh()
        {
            var c = Fresh();
            c.NoteDown(0);
            c.NoteUp(120);
            Assert.Equal(Down.Pending, c.NoteDown(300));   // then focus leaves before the up
            Assert.Equal(Down.Toggle, c.NoteDown(3_000));  // a new press is not stuck behind it
        }

        [Fact]
        public void A_lost_up_does_not_leave_the_key_down_for_ever()
        {
            var c = Fresh();
            Assert.Equal(Down.Toggle, c.NoteDown(0));      // its up lands in a window we do not see
            Assert.Equal(Down.Toggle, c.NoteDown(5_000));  // outside the window: a fresh press, not a re-down
        }

        [Fact]
        public void The_system_wide_door_toggles_at_once_every_time_because_it_never_delivers_an_up()
        {
            var c = Fresh();
            c.NoteDown(0);
            c.NoteUp(120);
            Assert.Equal(Down.Toggle, c.NoteDownWithoutUp(300));   // inside the window, still decided now
            Assert.Equal(Down.Toggle, c.NoteDownWithoutUp(500));   // and again, no up in between
            Assert.False(c.PressPending);
        }

        [Fact]
        public void Push_to_talks_up_is_nothing_to_the_lock_but_starts_its_window()
        {
            var c = Fresh();
            Assert.Equal(Up.Nothing, c.NoteUp(1_000));              // Ctrl+Space released
            Assert.Equal(Down.Pending, c.NoteDown(1_200));          // a lock press soon after waits for its up
            Assert.Equal(Up.Toggle, c.NoteUp(1_320));
        }

        // ── Reset and the window ────────────────────────────────────────

        [Fact]
        public void Reset_forgets_the_press_in_flight_but_keeps_the_window()
        {
            var c = Fresh();
            c.NoteDown(0);
            c.NoteUp(120);
            Assert.Equal(Down.Pending, c.NoteDown(300));
            c.Reset();                                              // Escape
            Assert.False(c.PressPending);
            Assert.Equal(Up.Nothing, c.NoteUp(420));                // the up of the forgotten press toggles nothing
            Assert.Equal(Down.Pending, c.NoteDown(600));            // still inside the window of the last up
        }

        [Fact]
        public void The_window_comes_from_this_machines_repeat_delay_and_is_bounded()
        {
            var slow = new LockPressClassifier();
            slow.SetKeyRepeatDelay(1000);
            var fast = new LockPressClassifier();
            fast.SetKeyRepeatDelay(250);

            Assert.True(slow.PairWindowMs >= 1000, "the first pair lands at the repeat delay; the window must outlast it");
            Assert.True(slow.PairWindowMs <= PttHoldFilter.MaxFirstGapDeferMs);
            Assert.True(fast.PairWindowMs >= PttHoldFilter.DefaultDeferMs, "the floor still covers the 250 ms stream");
            Assert.True(slow.PairWindowMs > fast.PairWindowMs);
        }

        [Fact]
        public void The_synthetic_press_length_is_the_hold_filters()
        {
            Assert.Equal(PttHoldFilter.ImplausibleReleaseMs, LockPressClassifier.SyntheticPressMs);
        }
    }
}
