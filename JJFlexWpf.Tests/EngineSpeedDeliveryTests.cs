using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Windows.Threading;
using Radios;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track L2: the speed test's lines are heard and in order (the
/// 2026-11-19 integration-day bench, findings 1 and 2). The session runs on a
/// private STA thread's dispatcher with no window, started the way a click
/// starts it (a Send-priority operation), and speaks through the real
/// <c>SpeechArbiter</c> behind an inert sink whose delivery pump never reaches
/// a line, so anything superseded is visibly taken back. Windowless: the test
/// thread is MTA; no window, reader, sound, radio or helper.
/// </summary>
public sealed class EngineSpeedDeliveryTests
{
    public EngineSpeedDeliveryTests() => NoiseReductionPerChannelTests.AssertWindowless();

    // ── The real arbiter, isolated ──

    /// <summary>
    /// The real SpeechArbiter behind an inert sink (as NoiseControllerTests'
    /// probe builds it), plus the pump's withdraw hook: every line is still
    /// unsent when anything supersedes it, the bench's case (NVDA had not
    /// reached them), so a superseded line is always taken back and counted.
    /// </summary>
    internal sealed class ArbiterRig : IDisposable
    {
        private readonly object _arbiter;
        private readonly Type _type;
        private long _ticket;
        public readonly ConcurrentQueue<(long Ticket, string Text, bool Salvaged)> Handed = new();
        public readonly ConcurrentDictionary<long, string> Withdrawn = new();

        public ArbiterRig()
        {
            var assembly = typeof(ScreenReaderOutput).Assembly;
            _type = assembly.GetType("Radios.Speech.SpeechArbiter")!;
            var ctor = _type.GetConstructors().Single();
            var sinkType = ctor.GetParameters()[2].ParameterType;
            var invoke = sinkType.GetMethod("Invoke")!;
            var ps = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var ticket = Expression.Call(Expression.Constant(this), typeof(ArbiterRig).GetMethod(nameof(Sink))!, ps[0], ps[5]);
            var handoff = Expression.Call(invoke.ReturnType.GetMethod("TrackedAs",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!, ticket);
            var sink = Expression.Lambda(sinkType, handoff, ps).Compile();
            var clock = Activator.CreateInstance(assembly.GetType("Radios.Speech.SystemSpeechClock")!);
            Func<long, string, bool> withdraw = (t, why) => { Withdrawn[t] = why; return true; };
            _arbiter = ctor.Invoke(new object?[] { clock, (Func<VerbosityLevel>)(() => VerbosityLevel.Chatty), sink,
                (Action)(() => { }), (Action<string, VerbosityLevel, SpeechIntent?, string?>)((_, _, _, _) => { }), null, null, withdraw });
        }

        public long Sink(string text, bool salvaged)
        {
            long t = Interlocked.Increment(ref _ticket);
            Handed.Enqueue((t, text, salvaged));
            return t;
        }

        /// <summary>The session's voice, mapped exactly as <c>ScreenReaderOutput.Speak</c> maps Queue and Interrupt onto the arbiter.</summary>
        public void Voice(string text, SpeechIntent intent, bool additive) =>
            _type.GetMethod("Emit")!.Invoke(_arbiter, new object?[] { text, intent != SpeechIntent.Queue, intent,
                VerbosityLevel.Terse, "speed test", SpeechSubject.EngineSpeedTest, additive });

        public void Withdraw(string by) =>
            _type.GetMethod("Supersede")!.Invoke(_arbiter, new object?[] { SpeechSubject.EngineSpeedTest, by, "speed test" });

        /// <summary>What a reader would still say: every hand-over not taken back, first hand-overs only, in order.</summary>
        public string[] Delivered() => Handed.Where(h => !h.Salvaged && !Withdrawn.ContainsKey(h.Ticket)).Select(h => h.Text).ToArray();

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

        public Run(bool competing = false, ManualResetEventSlim? holdBeforeBurst = null)
        {
            var bench = Bench(Thread.Dispatcher, holdBeforeBurst);
            Session = new EngineSpeedSession(Thread.Dispatcher, t => Box = t, () => competing,
                (onResult, cancel) => { Interlocked.Increment(ref TestRuns); return bench(onResult, cancel); },
                Words, Speech.Voice, Speech.Withdraw);
        }

        /// <summary>Press Start the way a click does: an operation at Send priority on the dispatcher.</summary>
        public void Press() => Thread.Dispatcher.Invoke(() => { _ = Session.Start(); }, DispatcherPriority.Send);

        public void WaitUntilDone()
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (Thread.Dispatcher.Invoke(() => Session.Running))
            {
                Assert.True(DateTime.UtcNow < until, "the speed test session never finished");
                System.Threading.Thread.Sleep(20);
            }
        }

        public string[] BoxLines() => Box.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        public void Dispose() { Speech.Dispose(); Thread.Dispose(); }
    }

    private static string L(string key) => Lexicon.Get(key);

    [Fact]
    public void Every_spoken_line_reaches_the_reader_in_order_and_none_is_taken_back()
    {
        using var run = new Run();
        run.Press();
        run.WaitUntilDone();

        var expected = new[] { L("audio.diagnostics.speed.started") }
            .Concat(Ids.Select(id => "line " + id))
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
        run.WaitUntilDone();

        var tail = new List<string> { L("audio.diagnostics.speed.defaults_note"), L("audio.diagnostics.speed.helper_pairs_note") };
        if (competing) tail.Add(L("audio.diagnostics.speed.competes"));
        tail.Add(L("audio.diagnostics.speed.finished"));
        var expected = new[] { L("audio.diagnostics.speed.started") }.Concat(Ids.Select(id => "line " + id)).Concat(tail).ToArray();
        Assert.Equal(expected, run.BoxLines());
        Assert.Equal(L("audio.diagnostics.speed.finished"), run.Speech.Delivered().Last());
    }

    [Fact]
    public void Pressing_Start_again_while_it_runs_says_busy_takes_nothing_back_and_restarts_nothing()
    {
        using var hold = new ManualResetEventSlim();
        using var run = new Run(holdBeforeBurst: hold);
        run.Press();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (run.Speech.Delivered().Length < 1 + EngineLines)
        {
            Assert.True(DateTime.UtcNow < until, "the engine lines never arrived");
            Thread.Sleep(10);
        }
        run.Press();                                         // busy, mid-run
        hold.Set();
        run.WaitUntilDone();

        Assert.Equal(1, run.TestRuns);
        Assert.Empty(run.Speech.Withdrawn);
        var said = run.Speech.Delivered();
        Assert.Contains(L("audio.diagnostics.speed.busy"), said);
        var lines = said.Where(s => s != L("audio.diagnostics.speed.busy")).ToArray();
        Assert.Equal(new[] { L("audio.diagnostics.speed.started") }.Concat(Ids.Select(id => "line " + id))
            .Append(L("audio.diagnostics.speed.finished")), lines);
    }

    [Fact]
    public void Closing_the_window_mid_run_takes_back_what_is_unsaid_and_says_nothing_after()
    {
        using var hold = new ManualResetEventSlim();
        using var run = new Run(holdBeforeBurst: hold);
        run.Press();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (run.Speech.Delivered().Length < 1 + EngineLines)
        {
            Assert.True(DateTime.UtcNow < until, "the engine lines never arrived");
            Thread.Sleep(10);
        }
        run.Thread.Dispatcher.Invoke(() => run.Session.Stop());
        int handed = run.Speech.Handed.Count;
        hold.Set();
        run.WaitUntilDone();

        // "started" interrupted, so it was already sounding; every queued line
        // after it was still unsaid, and is taken back.
        Assert.Equal(new[] { L("audio.diagnostics.speed.started") }, run.Speech.Delivered());
        Assert.Equal(EngineLines, run.Speech.Withdrawn.Count);
        Assert.Equal(handed, run.Speech.Handed.Count);      // and nothing handed over after the close
        Assert.DoesNotContain(L("audio.diagnostics.speed.finished"), run.BoxLines());
    }
}
