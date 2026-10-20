using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JJFlexControl;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #520: what <c>Serial</c> runs per open. <see cref="KnobInputPump"/> is driven here with a
    /// fake clock, so no port is involved.
    /// </summary>
    /// <remarks>
    /// What these cannot see: <c>Serial</c>'s lines that build a pump per open, bind the port's
    /// callback to it and call <see cref="KnobInputPump.Stop"/> from <c>Close</c>. Those are read,
    /// not run; the bench covers them.
    /// </remarks>
    public class FlexControlKnobPumpTests
    {
        private sealed class Harness
        {
            public double Now;
            public readonly List<string> Dispatched = new List<string>();
            public readonly KnobInputPump Pump;

            public Harness(Action<string> dispatch = null, double openedAt = 0)
            {
                Now = openedAt;
                Pump = new KnobInputPump(dispatch ?? (t => { lock (Dispatched) Dispatched.Add(t); }), () => Now);
            }
        }

        [Fact]
        public void Three_real_detents_in_one_read_after_the_window_all_arrive_and_survive_the_close()
        {
            // Codex's case, at the pump: one read, one arrival time, three real steps.
            var h = new Harness();
            h.Now = 1_000;
            h.Pump.TakeText("D;D;D;");
            h.Pump.Stop();

            Assert.Equal(new[] { "D", "D", "D" }, h.Dispatched);
        }

        [Fact]
        public void A_replay_at_open_is_ignored_and_F0304_is_kept()
        {
            var h = new Harness();
            h.Now = 1;
            h.Pump.TakeText("F0304;D;D;U;");
            h.Now = 9;
            h.Pump.TakeText("D;X1S;F0304;D;");
            h.Now = 400;
            h.Pump.TakeText("U;");

            Assert.Equal(new[] { "F0304", "F0304", "U" }, h.Dispatched);
            Assert.Equal(6, h.Pump.ReplayEventsDiscarded);
        }

        [Fact]
        public void Every_open_has_its_own_window()
        {
            var first = new Harness();
            first.Now = 1;
            first.Pump.TakeText("D;D;");
            first.Pump.Stop();

            var second = new Harness(openedAt: 5_000);
            second.Now = 5_002;
            second.Pump.TakeText("U;U;");

            Assert.Empty(first.Dispatched);
            Assert.Empty(second.Dispatched);
        }

        [Fact]
        public void Nothing_is_acted_on_after_the_port_closes()
        {
            var h = new Harness();
            h.Now = 1_000;
            h.Pump.Stop();
            h.Pump.TakeText("U;");
            h.Pump.Stop();

            Assert.Empty(h.Dispatched);
        }

        [Fact]
        public void Close_waits_for_a_dispatch_already_in_progress_and_nothing_follows_it()
        {
            var entered = new ManualResetEventSlim();
            var proceed = new ManualResetEventSlim();
            var dispatched = new List<string>();
            var h = new Harness(t =>
            {
                lock (dispatched) dispatched.Add(t);
                if (t == "X1S")
                {
                    entered.Set();
                    proceed.Wait(TimeSpan.FromSeconds(10));
                }
            });

            h.Now = 1_000;
            Task read = Task.Run(() => h.Pump.TakeText("X1S;"));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the dispatch never started");

            Task close = Task.Run(() => h.Pump.Stop());
            Assert.False(close.Wait(TimeSpan.FromMilliseconds(300)), "Close returned while a dispatch was still in progress");

            proceed.Set();
            Assert.True(close.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(read.Wait(TimeSpan.FromSeconds(10)));

            h.Pump.TakeText("D;");
            lock (dispatched) Assert.Equal(new[] { "X1S" }, dispatched);
        }

        [Fact]
        public void The_text_is_split_on_semicolons_with_nulls_and_empty_tokens_ignored()
        {
            var h = new Harness();
            h.Now = 1_000;
            h.Pump.TakeText(";;D");
            h.Pump.TakeText("\0;");

            // Jim's parser returned ";D" for ";;D;", which no handler knows.
            Assert.Equal(new[] { "D" }, h.Dispatched);
        }

        [Fact]
        public void A_step_after_the_window_is_acted_on_in_the_read_that_brought_it()
        {
            var h = new Harness();
            h.Now = 1_000;
            h.Pump.TakeText("U;");
            Assert.Equal(new[] { "U" }, h.Dispatched); // no hold, no timer
        }
    }
}
