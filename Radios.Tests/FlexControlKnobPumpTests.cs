using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JJFlexControl;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #520: what <c>Serial</c> runs per open. <see cref="KnobInputPump"/> is driven here with a
    /// fake clock and a fake buffer count, so no port is involved. Every read is held to 9600
    /// baud (<see cref="FlexControlKnobWire"/>).
    /// </summary>
    /// <remarks>
    /// What these cannot see: <c>Serial</c>'s lines that build a pump per open just before the
    /// port is asked to open, bind the port's callback to it first, run the buffer check on a
    /// timer that stops itself, and call <see cref="KnobInputPump.Stop"/> from <c>Close</c>. Those
    /// are read, not run; the bench covers them.
    /// </remarks>
    public class FlexControlKnobPumpTests
    {
        private const double Gap = KnobInputFilter.QuietGapMs;

        /// <summary>When a pump that saw nothing at open is checked and found quiet.</summary>
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

            /// <summary>A buffer check at <paramref name="at"/> that finds <paramref name="unread"/> bytes.</summary>
            public bool Check(double at, int unread = 0)
            {
                Now = at;
                return Pump.CheckQuiet(() => unread);
            }

            /// <summary>Found quiet at <see cref="Ready"/>, nothing read before.</summary>
            public Harness Quiet()
            {
                Assert.False(Check(Ready));
                return this;
            }
        }

        [Fact]
        public void Three_real_detents_in_one_read_after_ready_all_arrive_and_survive_the_close()
        {
            // Codex's case, at the pump: one read, one time, three real steps.
            var h = new Harness().Quiet();
            h.Read(Ready + 10, "D;D;D;");
            h.Pump.Stop();

            Assert.Equal(new[] { "D", "D", "D" }, h.Dispatched);
        }

        [Fact]
        public void A_replay_at_open_is_ignored_and_F0304_is_kept()
        {
            var h = new Harness();
            h.Read(13, "F0304;D;D;U;");
            h.Read(28, "D;X1S;F0304;D;");
            Assert.True(h.Check(28 + Gap - 1));   // not yet a full gap: still waiting
            h.Read(28 + Gap + 5, "U;");          // no check found it quiet, so this is ignored too
            Assert.False(h.Check(28 + 2 * Gap + 10));
            h.Read(28 + 2 * Gap + 20, "D;");     // after a check found it quiet: acted on

            Assert.Equal(new[] { "F0304", "F0304", "D" }, h.Dispatched);
            Assert.Equal(7, h.Pump.ReplayEventsDiscarded);
        }

        [Fact]
        public void A_check_that_finds_bytes_waiting_keeps_the_pump_waiting()
        {
            var h = new Harness();
            h.Read(13, "F0304;D;D;U;");
            Assert.True(h.Check(5_000, unread: 24));   // long after the last read, but bytes wait unread
            h.Read(5_010, "D;D;D;D;D;D;D;D;D;D;D;D;");  // the late callback: still ignored
            Assert.True(h.Check(5_010 + Gap - 1));
            Assert.False(h.Check(5_010 + Gap));

            Assert.Equal(new[] { "F0304" }, h.Dispatched);
            Assert.Equal(15, h.Pump.ReplayEventsDiscarded);
        }

        // ---- Round six: a read that ends mid-code still restarts the wait ----

        [Fact]
        public void A_read_that_ends_mid_code_restarts_the_wait_and_the_saved_code_never_acts()
        {
            // Codex's round-five sequence, exactly: a code read at 50 ms; the next read returns
            // only "D" at 549 ms; a check at 550 ms finds the port's buffer empty; the ";" is read
            // at 551 ms. "D;" spans a legal 2 ms at 9600 baud. Restart the wait only on whole
            // codes and the check declares ready and this saved D tunes the radio.
            var h = new Harness();
            h.Read(50, "D;");
            h.Read(549, "D");
            Assert.True(h.Check(550));
            h.Read(551, ";");

            Assert.Empty(h.Dispatched);
            Assert.True(h.Pump.WaitingForQuiet);
            Assert.DoesNotContain(h.Notes, n => n.StartsWith("knob ready"));
            Assert.Equal(2, h.Pump.ReplayEventsDiscarded);
        }

        [Fact]
        public void A_read_that_carries_no_whole_code_still_counts_as_text_read()
        {
            // A read of a lone delimiter, or of nulls, makes no token. It is still text read, so
            // the trace's "no text had been read" can only be said when it is true.
            var h = new Harness();
            h.Read(50, "D;");
            h.Read(549, ";");
            Assert.True(h.Check(550));
            h.Read(600, "\0");
            Assert.True(h.Check(1_099));
            Assert.False(h.Check(1_100));
            Assert.Contains(h.Notes, n => n.StartsWith("knob ready at 1100.0 ms after the open call: no text had been read from the port for 500.0 ms")
                && n.Contains("then gaps of 499.0, 51.0 ms"));
        }

        /// <summary>
        /// Text landing in the port's buffer a few bytes at a time, at 9600 baud, each fragment
        /// read when <paramref name="readAt"/> says (at once by default), with the buffer checked
        /// every <see cref="KnobInputFilter.QuietCheckMs"/> and just before every landing. Codes
        /// are split across reads wherever the fragments fall.
        /// </summary>
        private static Harness SimulateBytes(IEnumerable<(string text, double at)> fragments, Func<double, double>? readAt = null)
        {
            var landed = fragments.ToList();
            double previous = 0;
            foreach (var (text, at) in landed)
            {
                FlexControlKnobWire.AssertTextFits(previous, at, text);
                previous = at;
            }
            readAt ??= t => t;
            var reads = landed.Select(f => readAt(f.at)).ToList();
            double end = (landed.Count == 0 ? 0 : Math.Max(landed[^1].at, reads.Max())) + 2 * Gap + KnobInputFilter.QuietCheckMs;

            var events = new List<(double at, int order, int index)>();
            for (int i = 0; i < landed.Count; i++)
            {
                events.Add((landed[i].at, -1, -1));
                events.Add((landed[i].at, 0, i));
                events.Add((reads[i], 1, i));
            }
            for (double c = KnobInputFilter.QuietCheckMs; c <= end; c += KnobInputFilter.QuietCheckMs) events.Add((c, 2, -1));

            var h = new Harness();
            int unread = 0;
            foreach (var (at, order, index) in events.OrderBy(e => e.at).ThenBy(e => e.order).ThenBy(e => e.index))
            {
                if (order == 0) unread += landed[index].text.Length;
                else if (order == 1)
                {
                    unread -= landed[index].text.Length;
                    h.Now = at;
                    h.Pump.TakeText(landed[index].text);
                }
                else h.Check(at, unread);
            }
            return h;
        }

        /// <summary><paramref name="text"/> sent back to back from <paramref name="start"/>, landing <paramref name="bytes"/> bytes at a time.</summary>
        private static List<(string text, double at)> Fragments(string text, double start, int bytes)
        {
            var list = new List<(string, double)>();
            for (int i = 0; i < text.Length; i += bytes)
            {
                string part = text.Substring(i, Math.Min(bytes, text.Length - i));
                list.Add((part, start + FlexControlKnobWire.MsFor(i + part.Length)));
            }
            return list;
        }

        [Fact]
        public void A_replay_split_mid_code_across_reads_never_acts()
        {
            // Two saved batches landing three bytes at a time, so most reads end mid-code, with the
            // second batch's first fragment landing 1 ms under the gap after the first batch's last
            // read. Then a live turn after a full quiet gap.
            var first = Fragments("F0304;D;D;U;D;X1S;D;D;", 0, 3);
            var second = Fragments("U;D;D;F0304;D;U;", first[^1].at + Gap - 1 - FlexControlKnobWire.MsFor(3), 3);
            var live = Fragments("U;", second[^1].at + Gap + 2 * KnobInputFilter.QuietCheckMs, 2);

            Harness h = SimulateBytes(first.Concat(second).Concat(live));

            Assert.Equal(new[] { "F0304", "F0304", "U" }, h.Dispatched);
            Assert.Equal(12, h.Pump.ReplayEventsDiscarded);
        }

        [Fact]
        public void A_code_split_across_a_delayed_read_never_acts()
        {
            // The "D" of a saved code is read at once; its ";" lands 2 ms later but sits unread
            // until 1,200 ms. The buffer checks see it waiting the whole time.
            var h = SimulateBytes(new[] { ("D;", 2.1), ("D", 100.0), (";", 102.0), ("U;", 2_000.0) },
                readAt: t => t == 102.0 ? 1_200 : t);

            Assert.Equal(new[] { "U" }, h.Dispatched);
            Assert.Equal(2, h.Pump.ReplayEventsDiscarded);
        }

        [Fact]
        public void The_wait_s_summary_reaches_the_trace_from_the_pump()
        {
            var h = new Harness();
            h.Read(13, "F0304;D;D;U;");
            h.Read(28, "D;");
            h.Check(28 + Gap, unread: 2);
            h.Check(28 + Gap + 50);

            Assert.Contains(h.Notes, n => n.StartsWith("knob ready at 578.0 ms after the open call: no text had been read from the port for 550.0 ms, and the port's buffer was empty when checked")
                && n.Contains("ignored 4 input codes (D 3, U 1)")
                && n.Contains("1 buffer checks found bytes waiting unread")
                && n.Contains("then gaps of 15.0 ms"));
            Assert.Contains("device report kept:F0304", h.Notes);
        }

        [Fact]
        public void Every_open_has_its_own_wait()
        {
            var first = new Harness().Quiet();
            first.Pump.Stop();

            var second = new Harness(openedAt: 5_000);
            second.Read(5_005, "U;U;");

            Assert.Empty(second.Dispatched);
            Assert.True(second.Pump.WaitingForQuiet);
        }

        [Fact]
        public void Nothing_is_acted_on_after_the_port_closes()
        {
            var h = new Harness().Quiet();
            h.Pump.Stop();
            h.Read(Ready + 10, "U;");
            Assert.False(h.Check(Ready + 20));
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
            }).Quiet();

            h.Now = Ready + 10;
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
        public void A_buffer_check_waits_for_a_read_in_progress()
        {
            // The read is made inside the pump's lock, so a check cannot fall between taking the
            // text out of the port and handing it to the filter, and see an empty buffer while
            // saved turns sit in a thread's hands.
            var inRead = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var h = new Harness();
            h.Now = 10;
            Task read = Task.Run(() => h.Pump.ReadFrom(() =>
            {
                inRead.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return "D;D;";
            }));
            Assert.True(inRead.Wait(TimeSpan.FromSeconds(10)), "the read never started");

            Task<bool> check = Task.Run(() => h.Pump.CheckQuiet(() => 0));
            Assert.False(check.Wait(TimeSpan.FromMilliseconds(300)), "a buffer check ran while a read was in progress");

            release.Set();
            Assert.True(read.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(check.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(check.Result); // the read restarted the wait
            Assert.Empty(h.Dispatched);
        }

        [Fact]
        public void The_text_is_split_on_semicolons_with_nulls_and_empty_tokens_ignored()
        {
            var h = new Harness().Quiet();
            h.Read(Ready + 10, ";;D");
            h.Read(Ready + 20, "\0;");

            // Jim's parser returned ";D" for ";;D;", which no handler knows.
            Assert.Equal(new[] { "D" }, h.Dispatched);
        }

        [Fact]
        public void A_step_after_the_knob_is_ready_is_acted_on_in_the_read_that_brought_it()
        {
            var h = new Harness().Quiet();
            h.Read(Ready + 10, "U;");
            Assert.Equal(new[] { "U" }, h.Dispatched); // no hold, no timer
        }
    }
}
