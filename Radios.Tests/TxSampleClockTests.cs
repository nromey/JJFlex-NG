using System;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The accumulator that paces a self-clocked transmit source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Time is injected rather than waited for, so these run in microseconds and
    /// can express a fifteen-minute transmission without taking one.
    /// </para>
    /// <para>
    /// The property under test is not smoothness. It is that TOTAL frames track
    /// TOTAL elapsed time no matter how ragged the calls are — because the radio
    /// runs a jitter buffer, which forgives arrival jitter and cannot forgive
    /// sustained rate error.
    /// </para>
    /// </remarks>
    public class TxSampleClockTests
    {
        // The real transmit path: 24 kHz, Opus at 10 ms, so 240 samples a frame
        // and 100 frames a second.
        private const int Rate = 24000;
        private const int PerFrame = 240;
        private const long Tps = 10_000_000;      // round ticks/sec, like Stopwatch's
        private static long Ms(double ms) => (long)(ms * Tps / 1000.0);

        private static TxSampleClock Started()
        {
            var c = new TxSampleClock(Rate, PerFrame);
            c.Start(0);
            return c;
        }

        [Fact]
        public void One_second_owes_one_hundred_frames()
        {
            // The baseline. 24000 samples a second at 240 a frame is 100 frames.
            var c = Started();
            Assert.Equal(100, TotalOver(c, Ms(1000), Ms(10)));
        }

        [Fact]
        public void A_ragged_caller_still_owes_exactly_the_right_total()
        {
            // THE test. Poll at wildly uneven intervals — 3, 27, 1, 40, 9 ms —
            // and the total after a second is still exactly 100. A caller that
            // divided "time since last call" by the frame period would lose a
            // fraction each time and land short, which is the drift this class
            // exists to prevent.
            var c = Started();
            long t = 0, total = 0;
            int[] pattern = { 3, 27, 1, 40, 9 };
            int i = 0;
            while (t < Ms(1000))
            {
                t += Ms(pattern[i++ % pattern.Length]);
                if (t > Ms(1000)) t = Ms(1000);
                total += c.FramesDue(t, Tps);
            }
            Assert.Equal(100, total);
        }

        [Fact]
        public void A_coarse_timer_averaging_the_right_rate_is_fine()
        {
            // A 15 ms Windows tick is coarser than the 10 ms frame period, and
            // that is ACCEPTABLE — the jitter buffer absorbs it. Over a second
            // the total must still be 100.
            var c = Started();
            long t = 0, total = 0;
            while (t < Ms(1000))
            {
                t += Ms(15);
                if (t > Ms(1000)) t = Ms(1000);
                total += c.FramesDue(t, Tps);
            }
            Assert.Equal(100, total);
        }

        [Fact]
        public void Fifteen_minutes_of_polling_does_not_drift_by_a_single_frame()
        {
            // The failure mode being prevented, at the scale it shows up. A
            // per-call-delta implementation loses a sliver every call; over
            // 90,000 calls that is seconds of audio and a periodic correction
            // the operator hears as galloping.
            var c = Started();
            long t = 0, total = 0;
            long end = Ms(15 * 60 * 1000);
            while (t < end)
            {
                t += Ms(10);
                total += c.FramesDue(t, Tps);
            }
            Assert.Equal(15 * 60 * 100, total);   // 90,000 frames, exactly
        }

        [Fact]
        public void Nothing_is_owed_before_time_passes_or_after_Stop()
        {
            var c = Started();
            Assert.Equal(0, c.FramesDue(0, Tps));
            Assert.Equal(0, c.FramesDue(-Ms(5), Tps));

            c.FramesDue(Ms(100), Tps);
            c.Stop();
            Assert.Equal(0, c.FramesDue(Ms(200), Tps));
            Assert.False(c.Running);
        }

        [Fact]
        public void Restarting_does_not_owe_for_the_silence_in_between()
        {
            // Unkey, wait a minute, key again. The radio is owed nothing for the
            // minute nobody was transmitting — without the reset, the first call
            // of the new transmission would try to repay 6,000 frames.
            var c = Started();
            c.FramesDue(Ms(500), Tps);
            c.Stop();

            c.Start(Ms(60_000));
            Assert.Equal(0, c.FramesEmitted);
            Assert.Equal(1, c.FramesDue(Ms(60_010), Tps));
        }

        [Fact]
        public void A_long_stall_is_clamped_rather_than_burst_at_the_radio()
        {
            // A two-second freeze owes 200 frames. Dumping those at once is a
            // worse fault than the gap — the jitter buffer would discard most of
            // it anyway — so the excess is abandoned, deliberately and countably.
            var c = Started();
            int due = c.FramesDue(Ms(2000), Tps);

            Assert.Equal(TxSampleClock.MaxFramesPerCall, due);
            Assert.True(c.ClampedLastCall);
            Assert.Equal(200 - TxSampleClock.MaxFramesPerCall, c.FramesDroppedToClamp);
        }

        [Fact]
        public void After_a_clamp_the_clock_does_not_keep_repaying_the_written_off_debt()
        {
            // The subtle one. If the clamp only capped the RETURN and left the
            // debt on the books, every later call would come back clamped
            // forever and the stream would run permanently late. The abandoned
            // frames must count as emitted.
            var c = Started();
            c.FramesDue(Ms(2000), Tps);          // clamps
            int next = c.FramesDue(Ms(2010), Tps);

            Assert.Equal(1, next);               // back to normal immediately
            Assert.False(c.ClampedLastCall);
        }

        [Fact]
        public void The_realised_rate_reports_what_happened_not_what_was_intended()
        {
            // For the trace. A number that can disagree with 100 is the only
            // kind worth logging — one that always reads 100 would tell us
            // nothing on the day it is wrong.
            var c = Started();
            long t = 0;
            while (t < Ms(1000)) { t += Ms(10); c.FramesDue(t, Tps); }

            Assert.Equal(100.0, c.RealisedFramesPerSecond(t, Tps), 3);
        }

        [Fact]
        public void Frame_geometry_is_reported_for_the_trace()
        {
            var c = new TxSampleClock(Rate, PerFrame);
            Assert.Equal(240, c.SamplesPerFrame);
            Assert.Equal(10.0, c.FrameMs, 6);
        }

        [Fact]
        public void A_nonsense_rate_is_refused_rather_than_silently_wrong()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TxSampleClock(0, 240));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TxSampleClock(24000, 0));
            // A zero tick frequency would divide by zero; answer nothing instead.
            Assert.Equal(0, Started().FramesDue(Ms(100), 0));
        }


        [Fact]
        public void A_clock_knows_when_it_no_longer_matches_the_stream()
        {
            // Noel, 2026-08-24: "make sure that the sample rate doesn't change,
            // though not often, sometimes it happens." Windows can change a
            // device's shared-mode format and a reconnect can negotiate
            // differently. The capture-driven path noticed by accident, because
            // the callback changed with the device; a self-clocked one has to be
            // asked.
            var c = new TxSampleClock(Rate, PerFrame);

            Assert.True(c.Matches(Rate, PerFrame));
            Assert.False(c.Matches(48000, PerFrame));      // rate moved
            Assert.False(c.Matches(Rate, 480));            // frame size moved
            Assert.False(c.Matches(48000, 480));           // both
        }

        [Fact]
        public void A_mismatch_explains_itself_in_terms_a_trace_reader_can_act_on()
        {
            // The message is the whole value of the check. "Mismatch" tells a
            // reader nothing; the two rates and what happens if it is ignored
            // tell them what to do.
            var c = new TxSampleClock(Rate, PerFrame);

            Assert.Equal("", c.DescribeMismatch(Rate, PerFrame));

            string d = c.DescribeMismatch(48000, 480);
            Assert.Contains("24000", d);
            Assert.Contains("48000", d);
            Assert.Contains("rebuilt", d);
        }

        [Fact]
        public void The_rate_is_immutable_so_a_stale_clock_cannot_quietly_adapt()
        {
            // Deliberately no setter. A clock that could be re-pointed at a new
            // rate mid-flight would carry its old FramesEmitted into the new
            // timebase and drift from the first call. Rebuilding is the only
            // correct response to a rate change, and the type enforces it.
            var c = Started();
            c.FramesDue(Ms(500), Tps);

            Assert.Equal(Rate, c.SampleRate);
            Assert.Null(typeof(TxSampleClock).GetProperty("SampleRate")!.SetMethod);
        }

        /// <summary>Poll at a fixed step to the end and total what was owed.</summary>
        private static long TotalOver(TxSampleClock c, long endTicks, long stepTicks)
        {
            long t = 0, total = 0;
            while (t < endTicks)
            {
                t += stepTicks;
                if (t > endTicks) t = endTicks;
                total += c.FramesDue(t, Tps);
            }
            return total;
        }
    }
}