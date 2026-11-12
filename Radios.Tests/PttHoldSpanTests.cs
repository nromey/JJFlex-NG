using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1020: a held push to talk is counted from its FIRST press, so a screen
    /// reader's release-and-press pairs are one hold (ruled by Noel 2026-11-09
    /// 20:27). One rule, shared by the controller's limit and the system-wide
    /// watchdog, bounded by the hold filter's longest bridge.
    /// </summary>
    public class PttHoldSpanTests
    {
        [Fact]
        public void The_bound_is_the_hold_filters_longest_bridge_and_the_watchdogs_missed_release()
        {
            Assert.Equal(PttHoldFilter.MaxFirstGapDeferMs, PttHoldSpan.ContinuesWithinMs);
            Assert.Equal(SystemWidePttWatch.MissedReleaseMs, PttHoldSpan.ContinuesWithinMs);
        }

        [Fact]
        public void Jaws_pairs_for_sixteen_minutes_are_one_hold_from_the_first_press()
        {
            var span = new PttHoldSpan();
            Assert.False(span.NoteDown(0));
            long t = 512;
            while (t < 16 * 60_000)
            {
                span.NoteRelease(t);
                Assert.True(span.NoteDown(t + 250 - 1), "a JAWS-sized gap started a new hold at " + t);
                t += 250;
            }
            Assert.Equal(0, span.FirstPressMs);
            Assert.True(span.HeldMs(t) >= 16 * 60_000);
        }

        [Fact]
        public void A_down_at_the_bridge_or_later_is_a_new_hold()
        {
            var span = new PttHoldSpan();
            span.NoteDown(0);
            span.NoteRelease(60_000);
            Assert.True(span.NoteDown(60_000 + PttHoldSpan.ContinuesWithinMs - 1));
            span.NoteRelease(70_000);
            Assert.False(span.NoteDown(70_000 + PttHoldSpan.ContinuesWithinMs));
            Assert.Equal(70_000 + PttHoldSpan.ContinuesWithinMs, span.FirstPressMs);
        }

        [Fact]
        public void A_hold_ended_some_other_way_is_forgotten_and_the_next_down_is_new_however_soon()
        {
            // The app's own kill, Escape, the lock taking over, a teardown.
            var span = new PttHoldSpan();
            span.NoteDown(0);
            span.Forget();
            Assert.False(span.NoteDown(10));
            Assert.Equal(10, span.FirstPressMs);
            Assert.Equal(0, new PttHoldSpan().HeldMs(1_000_000));
        }

        [Fact]
        public void A_down_with_no_release_seen_continues_the_hold()
        {
            var span = new PttHoldSpan();
            span.NoteDown(0);
            Assert.True(span.NoteDown(5 * 60_000));
            Assert.Equal(0, span.FirstPressMs);
        }

        [Fact]
        public void A_release_with_no_hold_counted_is_nothing()
        {
            var span = new PttHoldSpan();
            span.NoteRelease(100);
            Assert.False(span.NoteDown(200));
            Assert.Equal(200, span.FirstPressMs);
        }
    }
}
