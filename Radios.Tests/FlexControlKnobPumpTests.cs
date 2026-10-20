using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JJFlexControl;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #520 repair round one: closing or reconnecting the Flex Control loses no real step, and
    /// nothing from a closed port is acted on after the close. <see cref="KnobInputPump"/> is what
    /// <c>Serial</c> runs per open; these drive it with a fake clock and a recorded schedule, so
    /// no port and no real timer are involved.
    /// </summary>
    /// <remarks>
    /// What these cannot see: <c>Serial</c>'s three lines that build a pump per open, bind the
    /// port's callback to it and call <see cref="KnobInputPump.Stop"/> from <c>Close</c>. Those
    /// are read, not run; the bench covers them.
    /// </remarks>
    public class FlexControlKnobPumpTests
    {
        private sealed class Harness
        {
            public double Now;
            public readonly List<string> Dispatched = new List<string>();
            public readonly List<long> Schedules = new List<long>();
            public readonly KnobInputPump Pump;

            public Harness(Action<string> dispatch = null)
            {
                Pump = new KnobInputPump(dispatch ?? (t => { lock (Dispatched) Dispatched.Add(t); }),
                    () => Now, wait => Schedules.Add(wait));
            }
        }

        [Fact]
        public void A_real_step_still_held_when_the_port_closes_is_acted_on()
        {
            var h = new Harness();
            h.Pump.TakeText("D;");
            Assert.Empty(h.Dispatched); // held for half a tick, in case it starts a burst

            h.Pump.Stop();

            Assert.Equal(new[] { "D" }, h.Dispatched);
        }

        [Fact]
        public void A_burst_still_held_when_the_port_closes_is_still_discarded()
        {
            var h = new Harness();
            h.Pump.TakeText("D;D;U;D;X1S;");

            h.Pump.Stop();

            Assert.Empty(h.Dispatched);
            Assert.Equal(5, h.Pump.BurstEventsDiscarded);
        }

        [Fact]
        public void Nothing_is_acted_on_after_the_port_closes()
        {
            var h = new Harness();
            h.Pump.Stop();

            h.Pump.TakeText("U;");
            h.Now = 1_000;
            h.Pump.ReleaseDue();
            h.Pump.Stop();

            Assert.Empty(h.Dispatched);
        }

        [Fact]
        public void Close_waits_for_a_dispatch_already_past_its_check_and_nothing_follows_it()
        {
            // The timer's callback has taken the lock, passed its stopped check and is inside a
            // key handler when Close arrives on another thread.
            var entered = new ManualResetEventSlim();
            var proceed = new ManualResetEventSlim();
            var dispatched = new List<string>();
            Harness h = null!;
            h = new Harness(t =>
            {
                lock (dispatched) dispatched.Add(t);
                if (t == "X1S")
                {
                    entered.Set();
                    proceed.Wait(TimeSpan.FromSeconds(10));
                }
            });

            h.Pump.TakeText("X1S;");
            h.Now = 100;
            Task timer = Task.Run(() => h.Pump.ReleaseDue());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the dispatch never started");

            Task close = Task.Run(() => h.Pump.Stop());
            Assert.False(close.Wait(TimeSpan.FromMilliseconds(300)), "Close returned while a dispatch was still in progress");

            proceed.Set();
            Assert.True(close.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(timer.Wait(TimeSpan.FromSeconds(10)));

            h.Now = 10_000;
            h.Pump.ReleaseDue();
            h.Pump.TakeText("D;");
            lock (dispatched)
            {
                // The dispatch in progress finished; nothing taken or due after Close was acted on.
                Assert.Equal(new[] { "X1S" }, dispatched);
            }
        }

        [Fact]
        public void The_text_is_split_on_semicolons_with_nulls_and_empty_tokens_ignored()
        {
            var h = new Harness();
            h.Pump.TakeText(";;D");
            h.Pump.TakeText("\0;");
            h.Now = 100;
            h.Pump.ReleaseDue();

            // Jim's parser returned ";D" for ";;D;", which no handler knows.
            Assert.Equal(new[] { "D" }, h.Dispatched);
        }

        [Fact]
        public void A_device_report_is_acted_on_the_moment_it_arrives()
        {
            var h = new Harness();
            h.Pump.TakeText("F0304;");
            Assert.Equal(new[] { "F0304" }, h.Dispatched);
        }

        [Fact]
        public void The_timer_is_armed_for_half_a_tick_and_released_on_time()
        {
            var h = new Harness();
            h.Pump.TakeText("U;");
            Assert.Equal((long)Math.Ceiling(KnobInputFilter.GroupCloseMs), h.Schedules[^1]);

            h.Now = KnobInputFilter.GroupCloseMs;
            h.Pump.ReleaseDue();

            Assert.Equal(new[] { "U" }, h.Dispatched);
            Assert.Equal(Timeout.Infinite, h.Schedules[^1]);
        }
    }
}
