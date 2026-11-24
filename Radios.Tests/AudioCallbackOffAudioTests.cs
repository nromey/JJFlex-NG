using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using JJPortaudio;
using JJTrace;
using PortAudio = PortAudioSharp.PortAudio;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track RT (#1232): the real PortAudio output callback records and
    /// returns; it never formats, traces or blocks. Each trace site the callback
    /// had is driven through <c>Audio.outputCallback</c> itself, with a trace
    /// listener that blocks until released (Codex's DelayListener in his
    /// re-check 2 probe is the model), with tracing on and off. In every case the
    /// callback returns at once, allocates nothing, and the listener is never
    /// called on its thread; and, the positive control, the site WAS reached
    /// (its record is waiting) and the off-audio writer then writes that exact
    /// line, on another thread.
    /// </summary>
    /// <remarks>
    /// In the audio statics collection: the test-only abort switch is a static.
    /// No window, no device, no sound: the callback is handed memory, and the
    /// stream's writer is a manual one this test ticks.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AudioCallbackOffAudioTests
    {
        private const int Frames = 480;
        private const uint Size = 960;    // stereo floats in one callback
        private readonly ITestOutputHelper _out;
        public AudioCallbackOffAudioTests(ITestOutputHelper output) { _out = output; }

        /// <summary>An output stream in the stream table, as the audio server leaves an open one, with a manual writer.</summary>
        internal sealed class Stream : IDisposable
        {
            public readonly int Key;
            public readonly Audio.StreamCB Cb;
            public readonly ManualOffAudioScheduler Writer = new();
            public readonly string Name = "rt-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            public readonly ConcurrentQueue<(string Line, int Thread)> Written = new();
            private readonly IntPtr _out;

            public Stream(double reserveMs = 0, bool attach = true)
            {
                Key = Audio.queues.Add();
                Cb = Audio.queues.getQ(Key);
                Cb.StreamName = Name;
                Cb.Active = true;
                Cb.Channels = 2;
                Cb.BufferSize = Size;
                Cb.SampleRate = 48000;
                Cb.Playback = new RxPlaybackQueue(Name, 1, 10.0, 48000, reserveMs, RxCatchUpMode.Off);
                Cb.Playback.LineObserver = l => Written.Enqueue((l, Environment.CurrentManagedThreadId));
                Cb.Lines.LineObserverForTest = l => Written.Enqueue((l, Environment.CurrentManagedThreadId));
                Cb.Lines.Scheduler = Writer;
                if (attach) Cb.Lines.Attach();
                _out = Marshal.AllocHGlobal((int)Size * sizeof(float));
            }

            public void Feed() => Cb.Q.Enqueue(new float[Size]);

            public PortAudio.PaStreamCallbackResult Callback(PortAudio.PaStreamCallbackFlags flags = 0)
            {
                var time = new PortAudio.PaStreamCallbackTimeInfo();
                return Audio.outputCallback(IntPtr.Zero, _out, Frames, ref time, flags, new IntPtr(Key));
            }

            public string[] Lines => Written.Select(w => w.Line).ToArray();

            public void Dispose()
            {
                Cb.Lines.Detach();
                Audio.queues.Remove(Key);
                Marshal.FreeHGlobal(_out);
            }
        }

        /// <summary>
        /// Blocks every line naming its stream until released (three seconds at most,
        /// so a broken build fails rather than hangs), and records where it was
        /// called. Also catches the deleted "silence" line, which named no stream.
        /// </summary>
        internal sealed class GatedListener : TraceListener
        {
            private readonly string _name;
            public readonly ManualResetEventSlim Release = new(false);
            public readonly ConcurrentQueue<(string Line, int Thread)> Got = new();
            public GatedListener(string name) { _name = name; }
            public override void Write(string message) { }
            public override void WriteLine(string message)
            {
                if (message == null) return;
                bool silence = message.TrimEnd().EndsWith("silence", StringComparison.Ordinal) && !message.Contains(" stream");
                if (!silence && !message.Contains(_name)) return;
                Got.Enqueue((message, Environment.CurrentManagedThreadId));
                Release.Wait(TimeSpan.FromSeconds(3));
            }
        }

        private static (bool On, TraceSwitch Switch) Tracing_(bool on)
        {
            var saved = (Tracing.On, Tracing.TheSwitch);
            Tracing.TheSwitch = new TraceSwitch("rt-callback", "rt-callback") { Level = TraceLevel.Verbose };
            Tracing.On = on;
            return saved;
        }

        /// <summary>Each site: how to bring the stream to it, the callback that reaches it, what it returns, and the line it leads to.</summary>
        public enum Site { NewStatusFlag, StarvedCallback, CompletingCallback, ArmedAbort }

        private static void Arrange(Stream s, Site site)
        {
            if (site == Site.NewStatusFlag) return;
            // Every other site starts from a stream that has played.
            s.Feed();
            Assert.Equal(PortAudio.PaStreamCallbackResult.paContinue, s.Callback());
            s.Writer.Tick();
            if (site == Site.CompletingCallback) s.Cb.Active = false;
            if (site == Site.ArmedAbort) { TestStreamAbort.Armed = true; s.Cb.TestAbortDueTick = 1; }
        }

        private static PortAudio.PaStreamCallbackResult Act(Stream s, Site site) =>
            site == Site.NewStatusFlag ? s.Callback(PortAudio.PaStreamCallbackFlags.paOutputUnderflow) : s.Callback();

        private static PortAudio.PaStreamCallbackResult Expected(Site site) => site switch
        {
            Site.CompletingCallback => PortAudio.PaStreamCallbackResult.paComplete,
            Site.ArmedAbort => PortAudio.PaStreamCallbackResult.paAbort,
            _ => PortAudio.PaStreamCallbackResult.paContinue,
        };

        /// <summary>The exact text the site's first line carries.</summary>
        private static string Line(Stream s, Site site) => site switch
        {
            Site.NewStatusFlag => "audio " + s.Name + " stream: PortAudio reports paOutputUnderflow at callback 1 — output samples were not supplied in time, "
                + "the device played a gap. Further occurrences are counted silently; totals logged when the stream closes.",
            Site.StarvedCallback => "audio " + s.Name + " stream: the playback queue ran dry mid-stream at callback 2",
            Site.CompletingCallback => "audio " + s.Name + " stream summary: 2 callbacks, no PortAudio status flags (no glitches reported)",
            _ => "audio " + s.Name + " stream: " + TestStreamAbort.Variable + " aborts it now",
        };

        [Theory]
        [InlineData(Site.NewStatusFlag, true)]
        [InlineData(Site.NewStatusFlag, false)]
        [InlineData(Site.StarvedCallback, true)]
        [InlineData(Site.StarvedCallback, false)]
        [InlineData(Site.CompletingCallback, true)]
        [InlineData(Site.CompletingCallback, false)]
        [InlineData(Site.ArmedAbort, true)]
        [InlineData(Site.ArmedAbort, false)]
        public void Each_trace_site_on_the_callback_records_and_returns_and_the_writer_writes_its_line_on_another_thread(Site site, bool tracingOn)
        {
            var saved = Tracing_(tracingOn);
            GatedListener? listener = null;
            try
            {
                // The JIT's first pass through the site, on a stream of its own.
                using (var warm = new Stream()) { Arrange(warm, site); Act(warm, site); warm.Writer.Tick(); }
                TestStreamAbort.Armed = false;

                using var s = new Stream();
                listener = new GatedListener(s.Name);
                Trace.Listeners.Add(listener);
                Arrange(s, site);
                Thread.Sleep(20);
                listener.Release.Reset();
                while (listener.Got.TryDequeue(out _)) { }
                while (s.Written.TryDequeue(out _)) { }

                int callbackThread = Environment.CurrentManagedThreadId;
                var sw = new Stopwatch();
                long before = GC.GetAllocatedBytesForCurrentThread();
                sw.Start();
                var rv = Act(s, site);
                sw.Stop();
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                TestStreamAbort.Armed = false;
                _out.WriteLine($"{site}, tracing {(tracingOn ? "on" : "off")}: {sw.Elapsed.TotalMilliseconds:F2} ms, {bytes} bytes, returned {rv}");

                Assert.Equal(Expected(site), rv);
                Assert.True(sw.ElapsedMilliseconds < 50, $"the callback took {sw.Elapsed.TotalMilliseconds:F1} ms");
                Assert.Empty(listener.Got);               // the listener was not called, on this thread or any other
                Assert.Empty(s.Written);                  // nothing is written by the callback itself
                Assert.Equal(0, bytes);
                // Positive control, part one: the site was really reached.
                Assert.True(s.Cb.Lines.Pending, "nothing was recorded, so the callback never reached the site");

                // The writer's period, on another thread.
                var tick = Task.Run(() => s.Writer.Tick());
                if (tracingOn)
                {
                    var wait = Stopwatch.StartNew();
                    while (listener.Got.IsEmpty && wait.ElapsedMilliseconds < 5000) Thread.Sleep(2);
                    Assert.True(listener.Got.TryPeek(out var first), "the writer never wrote the line");
                    Assert.NotEqual(callbackThread, first.Thread);
                    Assert.Contains(Line(s, site), first.Line);
                }
                listener.Release.Set();
                Assert.True(tick.Wait(10000), "the writer did not finish");
                foreach (var l in s.Lines) _out.WriteLine("  " + l);
                // Positive control, part two: that exact line, from another thread.
                Assert.Contains(s.Written, w => w.Line.StartsWith(Line(s, site), StringComparison.Ordinal) && w.Thread != callbackThread);
                if (!tracingOn) Assert.Empty(listener.Got);
                else Assert.All(listener.Got, g => Assert.NotEqual(callbackThread, g.Thread));
                // The deleted line (#1232): no "silence" from a starved callback, at Verbose or at all.
                Assert.DoesNotContain(listener.Got, g => !g.Line.Contains(s.Name));
                Assert.False(s.Cb.Lines.Pending);
            }
            finally
            {
                TestStreamAbort.Armed = false;
                if (listener != null) { listener.Release.Set(); Trace.Listeners.Remove(listener); }
                Tracing.On = saved.On; Tracing.TheSwitch = saved.Switch;
            }
        }

        /// <summary>
        /// The silence the deleted line used to announce is still counted: a starved
        /// callback on a stream that has played is a starvation (and its time is a
        /// line of the queue's own), and one before anything played is priming.
        /// </summary>
        [Fact]
        public void A_starved_callback_is_still_counted_without_the_silence_line()
        {
            using var s = new Stream();
            s.Callback();                                   // nothing has played: the wait for the first audio
            Assert.Equal(1, s.Cb.Playback.PrimeCallbacks);
            s.Feed(); s.Callback();
            s.Callback();                                   // played, then dry: a starvation
            Assert.Equal(1, s.Cb.Playback.Starvations);
            Assert.True(s.Cb.Playback.StarvationSilenceMilliseconds > 0);
        }

        // ── The summaries: once per start, in order, nothing after the totals ──

        private static void AssertSummaryOrder(Stream s, string[] lines)
        {
            string[] order = { " stream summary:", " latency summary:", " queue policy:", " queue summary:", " standing latency:", " backlog trims:", " queue depth:" };
            int at = -1;
            foreach (var part in order)
            {
                int i = Array.FindIndex(lines, l => l.Contains("audio " + s.Name + part));
                Assert.True(i > at, $"'{part}' is missing or out of order");
                at = i;
            }
            Assert.Equal(lines.Length - 1, at);             // nothing after the totals
        }

        [Fact]
        public void A_completing_callback_s_summary_is_written_once_by_the_writer_with_the_queue_s_lines_first()
        {
            using var s = new Stream();
            s.Feed(); s.Callback();
            s.Callback();                                    // a starvation: its line is recorded, not yet written
            s.Cb.Active = false;
            Assert.Equal(PortAudio.PaStreamCallbackResult.paComplete, s.Callback());
            Assert.Empty(s.Written);
            s.Writer.Tick();
            var lines = s.Lines;
            foreach (var l in lines) _out.WriteLine(l);
            Assert.Equal(1, s.Cb.Lines.Summaries);
            int dry = Array.FindIndex(lines, l => l.Contains("ran dry mid-stream at callback 2"));
            Assert.True(dry >= 0 && dry < Array.FindIndex(lines, l => l.Contains(" queue summary:")));
            AssertSummaryOrder(s, lines);
            // The server's stop path comes next and writes nothing more.
            s.Cb.Lines.EndRun();
            s.Writer.Tick();
            Assert.Equal(lines.Length, s.Lines.Length);
            Assert.Equal(1, s.Cb.Lines.Summaries);
        }

        [Fact]
        public void The_stop_path_writes_the_summary_once_when_the_writer_has_not_yet()
        {
            using var s = new Stream();
            s.Feed(); s.Callback();
            s.Cb.Active = false;
            s.Callback();                                    // completes; the writer has not run
            s.Cb.Lines.EndRun();                             // the audio server, after Pa_StopStream
            Assert.Equal(1, s.Cb.Lines.Summaries);
            AssertSummaryOrder(s, s.Lines);
            int count = s.Lines.Length;
            s.Writer.Tick();                                 // the writer comes after: nothing more
            Assert.Equal(count, s.Lines.Length);
            Assert.Equal(1, s.Cb.Lines.Summaries);
        }

        /// <summary>
        /// The endings with no completing callback (the server's abort of a stream still
        /// active after 2.2 s; a device that died under it): before Track RT nothing was
        /// written for them at all. The stop path writes the summary, once.
        /// </summary>
        [Fact]
        public void A_stream_that_ends_with_no_completing_callback_gets_its_summary_from_the_stop_path_once()
        {
            using var s = new Stream();
            s.Feed(); s.Callback();
            s.Callback();
            s.Writer.Tick();
            Assert.Equal(0, s.Cb.Lines.Summaries);           // still running as far as anyone knows
            s.Cb.Lines.EndRun();
            Assert.Equal(1, s.Cb.Lines.Summaries);
            AssertSummaryOrder(s, s.Lines);
            s.Cb.Lines.EndRun();                             // a second stop or close: nothing more
            s.Writer.Tick();
            Assert.Equal(1, s.Cb.Lines.Summaries);
        }

        [Fact]
        public void A_restarted_stream_gets_a_summary_for_each_start()
        {
            using var s = new Stream();
            s.Feed(); s.Callback();
            s.Cb.Active = false; s.Callback();
            s.Cb.Lines.EndRun();
            Assert.Equal(1, s.Cb.Lines.Summaries);
            s.Cb.Lines.BeginRun();                           // the server's next start
            s.Cb.Active = true;
            s.Feed(); s.Callback();
            s.Writer.Tick();
            Assert.Equal(1, s.Cb.Lines.Summaries);           // running: no summary yet
            s.Cb.Active = false; s.Callback();
            s.Writer.Tick();
            Assert.Equal(2, s.Cb.Lines.Summaries);
        }

        /// <summary>
        /// Repair 1, N1 (Codex's check of af0fc643): nothing after the totals, even when the
        /// completing callback records a line WHILE the writer is writing an earlier one.
        /// The writer took its count of the ring, then read whether the stream had
        /// completed; a flag recorded and a completion marked in between had the totals
        /// written first and the flag's line after them. Reproduced by holding the writer
        /// inside the first line.
        /// </summary>
        [Fact]
        public void A_line_the_completing_callback_records_while_the_writer_is_busy_still_comes_before_the_totals()
        {
            using var s = new Stream();
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var lines = new ConcurrentQueue<string>();
            s.Cb.Lines.LineObserverForTest = l =>
            {
                lines.Enqueue(l);
                if (l.Contains("paOutputUnderflow")) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
            };
            s.Cb.Playback.LineObserver = lines.Enqueue;
            try
            {
                s.Feed(); s.Callback();
                s.Callback(PortAudio.PaStreamCallbackFlags.paOutputUnderflow);   // the first flag's record
                var writer = Task.Run(() => s.Writer.Tick());
                Assert.True(entered.Wait(5000), "the writer never reached the first line");
                // While it writes that line: a new flag, then the completing callback.
                s.Cb.Active = false;
                Assert.Equal(PortAudio.PaStreamCallbackResult.paComplete, s.Callback(PortAudio.PaStreamCallbackFlags.paOutputOverflow));
                release.Set();
                Assert.True(writer.Wait(10000));
                s.Writer.Tick();
                var got = lines.ToArray();
                foreach (var l in got) _out.WriteLine(l);
                int overflow = Array.FindIndex(got, l => l.Contains("PortAudio reports paOutputOverflow"));
                int summary = Array.FindIndex(got, l => l.Contains(" stream summary:"));
                Assert.True(overflow >= 0, "the second flag's line was never written");
                Assert.True(summary >= 0, "no summary was written");
                Assert.True(overflow < summary, "the second flag's line came after the totals");
                AssertSummaryOrder(s, got);
                Assert.Equal(1, s.Cb.Lines.Summaries);
            }
            finally { s.Cb.Lines.LineObserverForTest = null; release.Set(); }
        }

        // ── Muted receive: nothing is fed, and the lines are still written ──

        /// <summary>
        /// Codex's other inherited limit (re-check 2 of Track T): the playback queue's
        /// lines were handed on once per received packet, so while receive was muted (no
        /// packet written) nothing was written until feeding resumed. The stream's own
        /// work drains them on the writer's period: with no packet written, a starvation
        /// recorded by a callback is written by the next tick.
        /// </summary>
        [Fact]
        public void Lines_recorded_while_no_packet_is_written_are_still_written()
        {
            using var s = new Stream();
            s.Feed(); s.Callback();
            s.Writer.Tick();
            while (s.Written.TryDequeue(out _)) { }
            for (int i = 0; i < 250; i++) s.Callback();      // muted: no packet for two and a half seconds of callbacks
            Assert.True(s.Cb.Playback.LinesPending);
            s.Writer.Tick();                                  // no WriteOpus, no packet: the writer alone
            var lines = s.Lines;
            foreach (var l in lines) _out.WriteLine(l);
            Assert.Contains(lines, l => l.Contains("ran dry mid-stream at callback 2"));
            Assert.Contains(lines, l => l.Contains("starvation(s) costing"));
            Assert.False(s.Cb.Playback.LinesPending);
        }

        // ── Nothing queued to the pool, nothing allocated, with the writer blocked ──

        [Fact]
        public void With_the_writer_blocked_128_recording_callbacks_queue_nothing_and_allocate_nothing()
        {
            using var writer = new OffAudioDrainer(5, "rt callback test writer");
            using var s = new Stream(attach: false);
            s.Cb.Lines.Scheduler = writer;
            s.Cb.Lines.Attach();
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            s.Cb.Lines.LineObserverForTest = l => { entered.Set(); release.Wait(TimeSpan.FromSeconds(20)); };
            s.Cb.Playback.LineObserver = l => { entered.Set(); release.Wait(TimeSpan.FromSeconds(20)); };
            try
            {
                s.Feed(); s.Callback();
                s.Callback(PortAudio.PaStreamCallbackFlags.paOutputUnderflow);   // a record: the writer takes it and blocks
                Assert.True(entered.Wait(15000), "the writer never started");
                Assert.Equal(1, s.Cb.Lines.Drains);
                using var watch = new PoolQueueWatch();
                Assert.True(watch.Probe(), "the watch sees nothing, so its zero proves nothing");
                watch.Reset();
                var bytes = new long[128];
                for (int i = 0; i < 128; i++)
                {
                    if (i % 2 == 0) s.Feed();                // alternate played and starved: a record or a window per callback
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    s.Callback(i % 7 == 0 ? PortAudio.PaStreamCallbackFlags.paOutputOverflow : 0);
                    bytes[i] = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                Assert.Equal(0, watch.Count);
                Assert.All(bytes, b => Assert.Equal(0, b));
                Assert.Equal(1, s.Cb.Lines.Drains);          // still the one drain, held
                Assert.True(s.Cb.Playback.LinesPending);     // and the callbacks really recorded
            }
            finally
            {
                s.Cb.Lines.LineObserverForTest = null;
                s.Cb.Playback.LineObserver = null;
                release.Set();
            }
        }

        // ── The queue's lock (item 6) ──

        /// <summary>
        /// The queue between the receive loop and the callback was Queue.Synchronized:
        /// every Count and Dequeue the callback made took the lock the receive loop's
        /// Enqueue takes. Hold the queue (its own monitor, the lock a synchronized
        /// queue used) on another thread and run the callback: it returns at once and
        /// still plays what is queued.
        /// </summary>
        [Fact]
        public void A_thread_holding_the_queue_does_not_hold_the_callback()
        {
            using var s = new Stream();
            s.Feed(); s.Callback();
            s.Feed();
            using var held = new ManualResetEventSlim(false);
            using var done = new ManualResetEventSlim(false);
            var holder = new Thread(() => { lock (s.Cb.Q) { held.Set(); done.Wait(5000); } }) { IsBackground = true };
            holder.Start();
            try
            {
                Assert.True(held.Wait(5000));
                var sw = Stopwatch.StartNew();
                var rv = s.Callback();
                sw.Stop();
                Assert.Equal(PortAudio.PaStreamCallbackResult.paContinue, rv);
                Assert.True(sw.ElapsedMilliseconds < 50, $"the callback waited {sw.Elapsed.TotalMilliseconds:F1} ms for the queue");
                Assert.Equal(0, s.Cb.Q.Count);                // it played the buffer, not silence
                Assert.Equal(0, s.Cb.Playback.Starvations);
            }
            finally { done.Set(); holder.Join(5000); }
        }
    }
}
