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
    /// fake clock, so no port is involved. Every read is held to 9600 baud
    /// (<see cref="FlexControlKnobWire"/>).
    /// </summary>
    /// <remarks>
    /// What these cannot see: <c>Serial</c>'s lines that build a pump per open just before the
    /// port is asked to open, bind the port's callback to it first, and call
    /// <see cref="KnobInputPump.Stop"/> from <c>Close</c>. Those are read, not run; the bench
    /// covers them.
    /// </remarks>
    public class FlexControlKnobPumpTests
    {
        private const double Gap = KnobInputFilter.QuietGapMs;

        /// <summary>Comfortably after the knob is ready, for a pump that saw nothing at open.</summary>
        private const double Ready = 1_000;

        private sealed class Harness
        {
            public double Now;
            private double _lastRead;
            public readonly List<string> Dispatched = new List<string>();
            public readonly List<string> Notes = new List<string>();
            public readonly KnobInputPump Pump;

            public Harness(Action<string> dispatch = null, double openedAt = 0)
            {
                Now = openedAt;
                _lastRead = openedAt;
                Pump = new KnobInputPump(dispatch ?? (t => { lock (Dispatched) Dispatched.Add(t); }), () => Now, Notes.Add);
            }

            /// <summary>The port hands over <paramref name="text"/> at <paramref name="at"/>, no faster than the wire.</summary>
            public void Read(double at, string text)
            {
                FlexControlKnobWire.AssertTextFits(_lastRead, at, text);
                _lastRead = at;
                Now = at;
                Pump.TakeText(text);
            }
        }

        [Fact]
        public void Three_real_detents_in_one_read_after_ready_all_arrive_and_survive_the_close()
        {
            // Codex's case, at the pump: one read, one arrival time, three real steps.
            var h = new Harness();
            h.Read(Ready, "D;D;D;");
            h.Pump.Stop();

            Assert.Equal(new[] { "D", "D", "D" }, h.Dispatched);
        }

        [Fact]
        public void A_replay_at_open_is_ignored_and_F0304_is_kept()
        {
            var h = new Harness();
            h.Read(13, "F0304;D;D;U;");
            h.Read(28, "D;X1S;F0304;D;");
            h.Read(28 + Gap - 1, "U;");   // still inside the wait: ignored
            h.Read(28 + 2 * Gap, "D;");   // a full gap after the last arrival: acted on

            Assert.Equal(new[] { "F0304", "F0304", "D" }, h.Dispatched);
            Assert.Equal(7, h.Pump.ReplayEventsDiscarded);
        }

        [Fact]
        public void The_wait_s_summary_reaches_the_trace_from_the_pump()
        {
            var h = new Harness();
            h.Read(13, "F0304;D;D;U;");
            h.Read(28, "D;");
            h.Read(28 + Gap, "U;");

            Assert.Contains(h.Notes, n => n.StartsWith("knob ready 528.0 ms after the open call")
                && n.Contains("ignored 4 input codes (D 3, U 1)")
                && n.Contains("then gaps of 15.0 ms"));
            Assert.Contains("device report kept:F0304", h.Notes);
        }

        [Fact]
        public void Every_open_has_its_own_wait()
        {
            var first = new Harness();
            first.Read(5, "D;D;");
            first.Pump.Stop();

            var second = new Harness(openedAt: 5_000);
            second.Read(5_005, "U;U;");

            Assert.Empty(first.Dispatched);
            Assert.Empty(second.Dispatched);
        }

        [Fact]
        public void Nothing_is_acted_on_after_the_port_closes()
        {
            var h = new Harness();
            h.Now = Ready;
            h.Pump.Stop();
            h.Read(Ready + 10, "U;");
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

            h.Now = Ready;
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
            h.Read(Ready, ";;D");
            h.Read(Ready + 10, "\0;");

            // Jim's parser returned ";D" for ";;D;", which no handler knows.
            Assert.Equal(new[] { "D" }, h.Dispatched);
        }

        [Fact]
        public void A_step_after_the_knob_is_ready_is_acted_on_in_the_read_that_brought_it()
        {
            var h = new Harness();
            h.Read(Ready, "U;");
            Assert.Equal(new[] { "U" }, h.Dispatched); // no hold, no timer
        }
    }
}
