using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track L2: the speed test's lines are heard and in order (the
/// 2026-11-19 integration-day bench, findings 1 and 2), and, since repair 1,
/// owned until said or taken back (Codex's check of 979bfa1f, B1 to B3, whose
/// three reproductions are adopted here). The session runs on a private STA
/// thread's dispatcher with no window, started the way a click starts it (a
/// Send-priority operation), and speaks through the REAL
/// <c>SpeechArbiter</c> and a real <see cref="SpokenSeries"/>, into a delivery
/// pump modelled on <c>PacedSpeechDelivery</c> and a reader the test steps.
/// Windowless: the test thread is MTA; no window, reader, sound, radio or
/// helper.
/// </summary>
public sealed class EngineSpeedDeliveryTests
{
    public EngineSpeedDeliveryTests() => NoiseReductionPerChannelTests.AssertWindowless();

    // ── The real arbiter, a modelled pump, a stepped reader ──

    /// <summary>
    /// The real SpeechArbiter behind a modelled pump, built by reflection as
    /// NoiseControllerTests' probe builds it. The pump keeps what it was handed
    /// in order. An interrupt does what <c>PacedSpeechDelivery.Interrupt</c>
    /// does: clears the queue, reports each cleared line unspoken and cut by
    /// us, and puts itself at the head; the arbiter holds the cleared lines and
    /// hands them back after its settle window. A withdrawal takes a line out
    /// only while it is still queued. The reader says the head of the queue
    /// when <see cref="SayOne"/> is called, and reports it finished.
    /// </summary>
    internal sealed class ArbiterRig : IDisposable
    {
        private readonly object _arbiter;
        private readonly Type _type;
        private readonly object _sync = new();
        private long _ticket;
        private readonly List<(long Ticket, string Text)> _queue = new();
        private readonly List<string> _said = new();
        public readonly ConcurrentQueue<(long Ticket, string Text, bool Interrupt, bool Salvaged)> Handed = new();
        public readonly ConcurrentDictionary<long, string> Withdrawn = new();

        public ArbiterRig()
        {
            var assembly = typeof(ScreenReaderOutput).Assembly;
            _type = assembly.GetType("Radios.Speech.SpeechArbiter")!;
            var ctor = _type.GetConstructors().Single();
            var sinkType = ctor.GetParameters()[2].ParameterType;
            var invoke = sinkType.GetMethod("Invoke")!;
            var ps = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var ticket = Expression.Call(Expression.Constant(this), typeof(ArbiterRig).GetMethod(nameof(Sink))!, ps[0], ps[1], ps[5]);
            var handoff = Expression.Call(invoke.ReturnType.GetMethod("TrackedAs", BindingFlags.Static | BindingFlags.Public)!, ticket);
            var sink = Expression.Lambda(sinkType, handoff, ps).Compile();
            var clock = Activator.CreateInstance(assembly.GetType("Radios.Speech.SystemSpeechClock")!);
            Func<long, string, bool> withdraw = (t, why) =>
            {
                lock (_sync)
                {
                    int at = _queue.FindIndex(q => q.Ticket == t);
                    if (at < 0) return false;
                    _queue.RemoveAt(at);
                }
                Withdrawn[t] = why;
                return true;
            };
            _arbiter = ctor.Invoke(new object?[] { clock, (Func<VerbosityLevel>)(() => VerbosityLevel.Chatty), sink,
                (Action)(() => { }), (Action<string, VerbosityLevel, SpeechIntent?, string?>)((_, _, _, _) => { }), null, null, withdraw });
        }

        public long Sink(string text, bool interrupt, bool salvaged)
        {
            long t = Interlocked.Increment(ref _ticket);
            List<(long Ticket, string Text)>? cleared = null;
            lock (_sync)
            {
                if (interrupt)
                {
                    cleared = new List<(long, string)>(_queue);
                    _queue.Clear();
                    _queue.Insert(0, (t, text));
                }
                else _queue.Add((t, text));
            }
            Handed.Enqueue((t, text, interrupt, salvaged));
            if (cleared != null)
                foreach (var c in cleared) Outcome(c.Ticket, c.Text, SpeechOutcome.Cancelled(0, 1, byUs: true, 0));
            return t;
        }

        private void Outcome(long ticket, string text, SpeechOutcome outcome) =>
            _type.GetMethod("OnOutcome")!.Invoke(_arbiter, new object?[] { ticket, text, outcome });

        /// <summary>The session's voice for "started" and "busy", mapped as <c>ScreenReaderOutput.Speak</c> maps Queue and Interrupt.</summary>
        public void Voice(string text, SpeechIntent intent, bool additive) =>
            _type.GetMethod("Emit")!.Invoke(_arbiter, new object?[] { text, intent != SpeechIntent.Queue, intent,
                VerbosityLevel.Terse, "speed test", SpeechSubject.EngineSpeedTest, additive });

        /// <summary>A series on the same arbiter, as <c>ScreenReaderOutput.NewSpokenSeries</c> makes one, polling on its own timer.</summary>
        public SpokenSeries NewSeries()
        {
            var ctor = typeof(SpokenSeries).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            int pollMs = (int)typeof(SpokenSeries).GetField("PollMs", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            return (SpokenSeries)ctor.Invoke(new object?[] { _arbiter, SpeechSubject.EngineSpeedTest, "speed test",
                null, null, pollMs, null });
        }

        /// <summary>Another part of the app interrupting, on a subject of its own.</summary>
        public void OtherInterrupt(string text) =>
            _type.GetMethod("Emit")!.Invoke(_arbiter, new object?[] { text, true, SpeechIntent.Interrupt,
                VerbosityLevel.Terse, "elsewhere", "elsewhere", false });

        /// <summary>The reader says the line at the head of the queue, if there is one.</summary>
        public bool SayOne()
        {
            (long Ticket, string Text) head;
            lock (_sync)
            {
                if (_queue.Count == 0) return false;
                head = _queue[0];
                _queue.RemoveAt(0);
                _said.Add(head.Text);
            }
            Outcome(head.Ticket, head.Text, SpeechOutcome.Completed(1, 1, 0));
            return true;
        }

        public string[] Queued() { lock (_sync) return _queue.Select(q => q.Text).ToArray(); }

        /// <summary>
        /// What the operator hears from here on: every line the reader has
        /// said, then every line still in its queue, in order. A line the
        /// arbiter holds for its put-back is in neither until it is handed
        /// back, which is why the tests let the reader run until nothing is
        /// owed before reading this.
        /// </summary>
        public string[] Delivered() { lock (_sync) return _said.Concat(_queue.Select(q => q.Text)).ToArray(); }

        public void Dispose() => _type.GetMethod("DiscardAll")!.Invoke(_arbiter, null);
    }

    /// <summary>A dispatcher on a private STA thread, with no window.</summary>
    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;
        public Dispatcher Dispatcher { get; private set; } = null!;

        public DispatcherThread()
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() => { Dispatcher = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); })
            { IsBackground = true, Name = "speed test dispatcher" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
        }

        public void Dispose()
        {
            Dispatcher.InvokeShutdown();
            _thread.Join(5000);
        }
    }

    // ── A run like the bench's ──

    private const int EngineLines = 8, HelperLines = 7;

    private static EngineSpeedResult Result(string id) =>
        new(new[] { id }, null, EngineSpeedVerdict.KeepsUp, null, 1, 1);

    private static readonly string[] Ids =
        Enumerable.Range(0, EngineLines).Select(i => "engine" + i)
        .Concat(Enumerable.Range(0, HelperLines).Select(i => "helper" + i)).ToArray();

    /// <summary>
    /// Eight engine lines a few milliseconds apart, then seven helper lines in
    /// one burst at the end, as the real test makes them. Just before the
    /// burst the dispatcher is kept busy for a moment (the bench's dispatcher
    /// was speaking and drawing), so the burst and whatever comes after it are
    /// all waiting in its queue together.
    /// </summary>
    private static EngineSpeedSession.SpeedTest Bench(Dispatcher dispatcher, ManualResetEventSlim? holdBeforeBurst = null) =>
        (onResult, cancel) =>
        {
            var results = new List<EngineSpeedResult>();
            for (int i = 0; i < EngineLines; i++)
            {
                cancel.ThrowIfCancellationRequested();
                Thread.Sleep(15);
                var r = Result(Ids[i]); results.Add(r); onResult(r);
            }
            holdBeforeBurst?.Wait(10000);
            cancel.ThrowIfCancellationRequested();
            dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Thread.Sleep(300));
            for (int i = EngineLines; i < Ids.Length; i++) { var r = Result(Ids[i]); results.Add(r); onResult(r); }
            return results;
        };

    private static string Words(EngineSpeedResult r) => "line " + r.EngineIds[0];

    private sealed class Run : IDisposable
    {
        public readonly DispatcherThread Thread = new();
        public readonly ArbiterRig Speech = new();
        public string Box = "";
        public readonly EngineSpeedSession Session;
        public int TestRuns;

        public Run(bool competing = false, ManualResetEventSlim? holdBeforeBurst = null, EngineSpeedSession.SpeedTest? test = null)
        {
            var bench = test ?? Bench(Thread.Dispatcher, holdBeforeBurst);
            Session = new EngineSpeedSession(Thread.Dispatcher, t => Box = t, () => competing,
                (onResult, cancel) => { Interlocked.Increment(ref TestRuns); return bench(onResult, cancel); },
                Words, Speech.Voice, Speech.NewSeries);
        }

        /// <summary>Press Start the way a click does: an operation at Send priority on the dispatcher.</summary>
        public void Press() => Thread.Dispatcher.Invoke(() => { _ = Session.Start(); }, DispatcherPriority.Send);

        public void Close() => Thread.Dispatcher.Invoke(() => Session.Stop());

        public bool Running => Thread.Dispatcher.Invoke(() => Session.Running);

        public bool Speaking => Thread.Dispatcher.Invoke(() => Session.Speaking);

        /// <summary>The test's own work is over (its lines may still be owed).</summary>
        public void WaitUntilDone()
        {
            Assert.True(SpinWait.SpinUntil(() => !Running, 15000), "the speed test session never finished");
        }

        /// <summary>Let the reader run until the test is over and nothing of it is owed or queued.</summary>
        public void SayAll()
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                Assert.True(DateTime.UtcNow < until, "the reader never caught up with the speed test");
                if (Speech.SayOne()) continue;
                if (!Running && !Speaking && Speech.Queued().Length == 0) return;
                System.Threading.Thread.Sleep(5);
            }
        }

        /// <summary>Wait until the queue holds <paramref name="count"/> lines of the test's own results.</summary>
        public void WaitForQueuedLines(int count) =>
            Assert.True(SpinWait.SpinUntil(() => Speech.Queued().Count(q => q.StartsWith("line ", StringComparison.Ordinal)) >= count, 10000),
                $"{count} result lines never reached the reader's queue");

        public string[] BoxLines() => Box.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        public void Dispose() { Speech.Dispose(); Thread.Dispose(); }
    }

    private static string L(string key) => Lexicon.Get(key);

    /// <summary>The series' batch: how many results are handed over at once once the reader is free.</summary>
    private static readonly int Batch =
        (int)typeof(SpokenSeries).GetField("BatchSize", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string[] AllLines => Ids.Select(id => "line " + id).ToArray();

    [Fact]
    public void Every_spoken_line_reaches_the_reader_in_order_and_none_is_taken_back()
    {
        using var run = new Run();
        run.Press();
        run.SayAll();

        var expected = new[] { L("audio.diagnostics.speed.started") }
            .Concat(AllLines)
            .Append(L("audio.diagnostics.speed.finished")).ToArray();
        Assert.Empty(run.Speech.Withdrawn);
        Assert.Equal(expected, run.Speech.Delivered());

        // Positive control, a fresh arbiter of the same build: queued lines on
        // the test's subject that RESTATE it (the delivery before Track L2)
        // each take the one before back, so only the last would be heard.
        using var control = new ArbiterRig();
        foreach (var line in expected.Skip(1)) control.Voice(line, SpeechIntent.Queue, additive: false);
        Assert.Equal(expected.Length - 2, control.Withdrawn.Count);
        Assert.Equal(new[] { expected.Last() }, control.Delivered());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_helper_lines_and_the_notes_come_before_finished_in_the_box_and_in_speech(bool competing)
    {
        using var run = new Run(competing);
        run.Press();
        run.SayAll();

        var tail = new List<string> { L("audio.diagnostics.speed.defaults_note"), L("audio.diagnostics.speed.helper_pairs_note") };
        if (competing) tail.Add(L("audio.diagnostics.speed.competes"));
        tail.Add(L("audio.diagnostics.speed.finished"));
        var expected = new[] { L("audio.diagnostics.speed.started") }.Concat(AllLines).Concat(tail).ToArray();
        Assert.Equal(expected, run.BoxLines());
        Assert.Equal(new[] { L("audio.diagnostics.speed.started") }.Concat(AllLines).Append(L("audio.diagnostics.speed.finished")),
            run.Speech.Delivered());
    }

    /// <summary>
    /// Codex's B3, adopted: an interrupt while results are waiting in the
    /// reader's queue, "busy" (a second Start) or anyone else's, then the
    /// helper burst and "finished" arriving at once. The pump clears its queue
    /// and the arbiter holds the cleared lines for its settle window; what the
    /// operator hears from then on is the interrupter, the rescued lines, the
    /// rest, and "finished" last. With waitForRescue the burst is released only
    /// after the put-back (Codex's passing control). saidBefore is how many
    /// engine lines the reader has said when the interrupt comes: none (engine0
    /// is owed and seven more are ready), one (a whole batch is owed), or six
    /// (engine6 and engine7 are owed, nothing else is ready, and the helper
    /// burst arrives while they are held: Codex's arrangement).
    /// </summary>
    [Theory]
    [InlineData(true, false, 0, 1)]
    [InlineData(true, true, 0, 1)]
    [InlineData(false, false, 0, 1)]
    [InlineData(false, true, 0, 1)]
    [InlineData(true, false, 1, 4)]
    [InlineData(false, true, 1, 4)]
    [InlineData(true, false, 6, 2)]
    [InlineData(true, true, 6, 2)]
    [InlineData(false, false, 6, 2)]
    [InlineData(false, true, 6, 2)]
    public void After_an_interrupt_every_surviving_line_comes_before_finished_in_order(bool busy, bool waitForRescue, int saidBefore, int owed)
    {
        using var hold = new ManualResetEventSlim();
        using var run = new Run(holdBeforeBurst: hold);
        run.Press();
        Assert.True(SpinWait.SpinUntil(() => run.BoxLines().Length == 1 + EngineLines, 10000), "the engine lines were never made");
        Assert.True(run.Speech.SayOne());                    // "started"
        run.WaitForQueuedLines(1);
        var saidFirst = new List<string> { L("audio.diagnostics.speed.started") };
        for (int i = 0; i < saidBefore; i++)
        {
            string line = "line engine" + i;
            Assert.True(SpinWait.SpinUntil(() => run.Speech.Queued().FirstOrDefault() == line, 10000), line + " never reached the reader");
            Assert.True(run.Speech.SayOne());
            saidFirst.Add(line);
        }
        run.WaitForQueuedLines(owed);
        Thread.Sleep(3 * 40);                                // a few of the series' polls: nothing more is handed over
        int rescued = run.Speech.Queued().Length;
        Assert.Equal(owed, rescued);

        string interrupter = busy ? L("audio.diagnostics.speed.busy") : "something else";
        if (busy) run.Press(); else run.Speech.OtherInterrupt(interrupter);
        if (waitForRescue)
            Assert.True(SpinWait.SpinUntil(() => run.Speech.Handed.Count(h => h.Salvaged) == rescued, 5000), "the rescue never came back");
        hold.Set();
        run.SayAll();

        Assert.Equal(1, run.TestRuns);
        Assert.Empty(run.Speech.Withdrawn);
        Assert.Equal(saidFirst.Append(interrupter)
            .Concat(AllLines.Skip(saidFirst.Count - 1)).Append(L("audio.diagnostics.speed.finished")), run.Speech.Delivered());
    }

    [Fact]
    public void Closing_the_window_mid_run_takes_back_what_is_unsaid_and_says_nothing_after()
    {
        using var hold = new ManualResetEventSlim();
        using var run = new Run(holdBeforeBurst: hold);
        run.Press();
        Assert.True(SpinWait.SpinUntil(() => run.BoxLines().Length == 1 + EngineLines, 10000), "the engine lines were never made");
        run.Close();
        int handed = run.Speech.Handed.Count;
        hold.Set();
        run.WaitUntilDone();
        Thread.Sleep(3 * 40);                                 // a few of the series' polls

        // "started" interrupted, so the reader already has it; every result
        // after it was still unsaid, and is taken back or never handed over.
        Assert.Equal(new[] { L("audio.diagnostics.speed.started") }, run.Speech.Delivered());
        Assert.Equal(handed, run.Speech.Handed.Count);       // nothing handed over after the close
        Assert.False(run.Speaking);
        Assert.DoesNotContain(L("audio.diagnostics.speed.finished"), run.BoxLines());
    }

    /// <summary>Codex's B1, adopted: the test has finished, its lines have not been said, and the window closes.</summary>
    [Fact]
    public void Closing_after_the_test_has_finished_still_takes_back_every_unsaid_line()
    {
        using var run = new Run();
        run.Press();
        run.WaitUntilDone();
        Assert.Contains(L("audio.diagnostics.speed.finished"), run.BoxLines());
        Assert.True(run.Speaking, "nothing was left to say, so the close proves nothing");

        run.Close();
        Thread.Sleep(3 * 40);

        Assert.Equal(new[] { L("audio.diagnostics.speed.started") }, run.Speech.Delivered());
        Assert.False(run.Speaking);
    }

    /// <summary>A fresh Start after a run has finished computing takes back what that run still had to say.</summary>
    [Fact]
    public void A_fresh_start_takes_back_what_the_last_run_had_left_to_say()
    {
        using var run = new Run();
        run.Press();
        run.WaitUntilDone();
        Assert.True(run.Speaking);

        run.Press();                                         // not busy: the first run's work is over
        run.SayAll();

        Assert.Equal(2, run.TestRuns);
        Assert.Equal(new[] { L("audio.diagnostics.speed.started") }.Concat(AllLines)
            .Append(L("audio.diagnostics.speed.finished")), run.Speech.Delivered());
    }

    /// <summary>The same after a failure: "test failed" is a line like the others, owned until said.</summary>
    [Fact]
    public void Closing_after_the_test_failed_takes_back_its_lines_and_test_failed_too()
    {
        using var run = new Run(test: (onResult, cancel) =>
        {
            for (int i = 0; i < 3; i++) onResult(Result(Ids[i]));
            throw new InvalidOperationException("no engine");
        });
        run.Press();
        run.WaitUntilDone();
        Assert.Contains(L("audio.diagnostics.speed.test_failed"), run.BoxLines());
        Assert.True(run.Speaking);

        run.Close();
        Thread.Sleep(3 * 40);

        Assert.Equal(new[] { L("audio.diagnostics.speed.started") }, run.Speech.Delivered());

        // Control: let a failed run be said, and "test failed" comes last.
        using var said = new Run(test: (onResult, cancel) =>
        {
            for (int i = 0; i < 3; i++) onResult(Result(Ids[i]));
            throw new InvalidOperationException("no engine");
        });
        said.Press();
        said.SayAll();
        Assert.Equal(new[] { L("audio.diagnostics.speed.started") }.Concat(AllLines.Take(3))
            .Append(L("audio.diagnostics.speed.test_failed")), said.Speech.Delivered());
    }

    /// <summary>
    /// Codex's B2, adopted at the real length: eighteen results (eight local
    /// engines and pairings, NR2 and four NNR model lines, five helper
    /// pairings) are waiting, the reader has said the first, and the window
    /// closes before "finished". Sixteen is Codex's passing control; eighteen
    /// lost two lines to the arbiter's 16-entry ledger before repair 1.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(18)]
    public void Closing_with_a_long_list_waiting_takes_back_every_unsaid_line(int count)
    {
        using var hold = new ManualResetEventSlim();
        var ids = Enumerable.Range(0, count).Select(i => "result" + i).ToArray();
        using var run = new Run(test: (onResult, cancel) =>
        {
            foreach (var id in ids) onResult(Result(id));
            hold.Wait(10000);
            cancel.ThrowIfCancellationRequested();
            return Array.Empty<EngineSpeedResult>();
        });
        try
        {
            run.Press();
            Assert.True(SpinWait.SpinUntil(() => run.BoxLines().Length == 1 + count, 10000), "the results were never made");
            Assert.True(run.Speech.SayOne());                // "started"
            run.WaitForQueuedLines(1);
            Assert.True(run.Speech.SayOne());                // "line result0"; the next lines are handed over
            run.WaitForQueuedLines(Batch);

            run.Close();
            Thread.Sleep(3 * 40);

            Assert.Equal(new[] { L("audio.diagnostics.speed.started"), "line result0" }, run.Speech.Delivered());
            Assert.False(run.Speaking);
        }
        finally
        {
            hold.Set();
            SpinWait.SpinUntil(() => !run.Running, 10000);
        }
    }
}
