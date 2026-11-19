using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// The receive pipeline's keep-up timing (Sprint 49 Track L, #866), with fake
/// engines: a deliberately slow one in this process, and fake helper engines
/// whose processing time is set by the test, each raising the warning, beside
/// the fast engines that do not. A fake helper whose answers come back late
/// while its processing is fast raises only the delivery diagnostic (repair 1,
/// B1), and an engine slower than the gap threshold still warns (B3). The
/// audio thread allocates nothing for it, and the event is raised off that
/// thread.
/// </summary>
public sealed class EngineKeepUpPipelineTests
{
    private readonly ITestOutputHelper _out;
    public EngineKeepUpPipelineTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    /// <summary>A fake engine: copies its input, optionally spending time doing it, optionally answering late.</summary>
    internal sealed class Engine : IDenoiseEngine
    {
        private readonly Func<double> _spinMs;
        private readonly Func<bool> _late;
        public Engine(string id, Func<double>? spinMs = null, Func<bool>? late = null)
        { Id = id; _spinMs = spinMs ?? (() => 0); _late = late ?? (() => false); }
        public string Id { get; }
        public int SampleRate => 48000;
        public int FrameLength => 480;
        public bool IsAvailable => true;
        public int LatencySamples => 0;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output)
        {
            double ms = _spinMs();
            if (ms > 0)
            {
                long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
                while (Stopwatch.GetTimestamp() < until) { }
            }
            input.CopyTo(output);
            return !_late();
        }
        public void Reset() { }
        public void Dispose() { }
    }

    private static DenoiseEngineEntry Row(string id, DenoiseFamily family, Func<IDenoiseEngine> make) =>
        new(id, DenoiseSide.Pc, family, "audio.denoise." + id, new[] { DenoiseDirection.Receive },
            (_, _) => make(), () => Unavailability.None);

    internal sealed class Rig : IDisposable
    {
        public RxAudioPipeline Pipeline { get; }
        public ConcurrentQueue<(EngineKeepUpResult Result, int Thread)> Results { get; } = new();
        public ConcurrentQueue<EngineKeepUpResult> Delivery { get; } = new();
        public double SlowMs = 6;          // per frame per channel: 12 ms for a 10 ms stereo buffer
        public volatile bool HelperLate;   // the fake helper's answers stop coming back in time
        /// <summary>The helper's processing time per 10 ms frame, every channel together: fast (two channels at 1.7 ms) until a test says otherwise.</summary>
        public long ServiceNanos = FastService;
        public const long FastService = 3_400_000, SlowService = 12_000_000;
        private readonly float[] _audio = new float[960];

        public Rig()
        {
            var rows = new[]
            {
                Row("rnnoise", DenoiseFamily.Neural, () => new Engine("rnnoise")),
                Row("fast", DenoiseFamily.Neural, () => new Engine("fast")),
                Row("slow", DenoiseFamily.Neural, () => new Engine("slow", () => SlowMs)),
                Row("fakehelper", DenoiseFamily.Neural, () => new Engine("fakehelper", late: () => HelperLate)),
                Row("stuckhelper", DenoiseFamily.Neural, () => new Engine("stuckhelper", late: () => true)),
                Row("spectral_capture", DenoiseFamily.Spectral, () => new Engine("spectral_capture")),
                Row("fastspectral", DenoiseFamily.Spectral, () => new Engine("fastspectral")),
            };
            Pipeline = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(rows))
            {
                IsHelperEngine = id => id is "fakehelper" or "stuckhelper",
            };
            // One fresh answer's work on every buffer, at the figure the test sets.
            Pipeline.HelperService = (_, _, _) => HelperServiceReading.Fresh(Volatile.Read(ref ServiceNanos));
            Pipeline.KeepUpResult += (_, r) => Results.Enqueue((r, Environment.CurrentManagedThreadId));
            Pipeline.DeliveryDiagnostic += (_, r) => Delivery.Enqueue(r);
        }

        public void Service(long nanos) => Volatile.Write(ref ServiceNanos, nanos);

        /// <summary>Wait a little for a delivery change that may still be on the pool thread.</summary>
        public bool HasDelivery(string id, EngineKeepUpChange change)
        {
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < 500) { if (Delivery.Any(r => r.EngineId == id && r.Change == change)) return true; Thread.Sleep(5); }
            return false;
        }

        /// <summary>Run before every pumped buffer: a test that plays the helper's reader thread publishes its answers here.</summary>
        public Action? BeforeBuffer;

        public void Pump(int buffers = 1)
        {
            for (int i = 0; i < buffers; i++) { BeforeBuffer?.Invoke(); Array.Fill(_audio, 0.1f); Pipeline.Process(_audio); }
        }

        public void Choose(string id)
        {
            Assert.True(Pipeline.SelectPcEngine(id, out var refusal), refusal);
            if (Pipeline.NeuralSlot.ChosenEngineId == id) Pipeline.RnnEnabled = true; else Pipeline.SpectralEnabled = true;
        }

        /// <summary>
        /// One buffer asks for the chosen engine's bank; then wait, feeding
        /// nothing, until it is delivered, so every buffer after counts as a
        /// warm-up buffer. The bank is built off the audio thread; without this
        /// a fast pump spends most of its buffers before the bank arrives
        /// (repair 2: on x86 the stuck warm-up controls failed two runs in five).
        /// </summary>
        public void UntilDelivered(string id, DenoiseFamily family = DenoiseFamily.Neural)
        {
            var slot = family == DenoiseFamily.Neural ? Pipeline.NeuralSlot : Pipeline.SpectralSlot;
            Pump();
            var t = Stopwatch.StartNew();
            while (!slot.BuildDelivered(id) && t.ElapsedMilliseconds < 5000) Thread.Sleep(1);
            Assert.True(slot.BuildDelivered(id), id + " was never delivered");
        }

        /// <summary>Feed buffers until the slot runs the chosen engine.</summary>
        public void UntilRunning(string id, DenoiseFamily family = DenoiseFamily.Neural)
        {
            var slot = family == DenoiseFamily.Neural ? Pipeline.NeuralSlot : Pipeline.SpectralSlot;
            var t = Stopwatch.StartNew();
            while (slot.ProcessingEngineId != id && t.ElapsedMilliseconds < 5000) { Pump(); Thread.Sleep(1); }
            Assert.Equal(id, slot.ProcessingEngineId);
        }

        /// <summary>Feed buffers until a change for this engine arrives, or the limit; true when it did.</summary>
        public bool PumpUntil(string id, EngineKeepUpChange change, int maxBuffers)
        {
            for (int i = 0; i < maxBuffers; i++)
            {
                Pump();
                if (Has(id, change)) return true;
            }
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < 500) { if (Has(id, change)) return true; Thread.Sleep(5); }
            return false;
        }

        public bool Has(string id, EngineKeepUpChange change) => Results.Any(r => r.Result.EngineId == id && r.Result.Change == change);

        public void Dispose() => Pipeline.Dispose();
    }

    [Fact]
    public void A_deliberately_slow_engine_in_this_process_raises_fell_behind_and_a_fast_one_does_not()
    {
        // Positive control.
        using (var slow = new Rig())
        {
            slow.Choose("slow"); slow.UntilRunning("slow");
            Assert.True(slow.PumpUntil("slow", EngineKeepUpChange.FellBehind, 200), "the slow engine was never said to fall behind");
            var r = slow.Results.First(x => x.Result.Change == EngineKeepUpChange.FellBehind).Result;
            _out.WriteLine(r.Describe());
            Assert.Equal(DenoiseFamily.Neural, r.Family);
            Assert.False(r.Helper);
            Assert.True(r.AverageMs > r.BufferMs, r.Describe());
            Assert.DoesNotContain(slow.Results, x => x.Result.Change == EngineKeepUpChange.KeepingUp);
        }
        // The negative, for at least as long.
        using var fast = new Rig();
        fast.Choose("fast"); fast.UntilRunning("fast");
        Assert.True(fast.PumpUntil("fast", EngineKeepUpChange.KeepingUp, 300), "a fast engine was never said to keep up");
        fast.Pump(300);
        Thread.Sleep(100);
        Assert.DoesNotContain(fast.Results, x => x.Result.Change == EngineKeepUpChange.FellBehind);
    }

    [Fact]
    public void A_fake_helper_whose_answers_come_back_late_but_whose_processing_is_fast_is_not_warned_about_and_one_whose_processing_is_slow_is()
    {
        // Repair 1, B1: delivery is not cost. Late answers, fast processing.
        using (var rig = new Rig())
        {
            rig.Choose("fakehelper"); rig.UntilRunning("fakehelper");
            Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.KeepingUp, 300));
            rig.HelperLate = true;
            rig.Pump(400);
            // Positive control: the late answers were seen, as the delivery diagnostic.
            Assert.True(rig.HasDelivery("fakehelper", EngineKeepUpChange.FellBehind), "the missed answers were never seen");
            var d = rig.Delivery.First(x => x.Change == EngineKeepUpChange.FellBehind);
            _out.WriteLine(d.Describe());
            Assert.True(d.Delivery);
            Thread.Sleep(100);
            Assert.False(rig.Has("fakehelper", EngineKeepUpChange.FellBehind), "a fast helper whose answers were delayed was warned about");
        }

        // On-time answers, processing slower than the audio: falls behind.
        using var slow = new Rig();
        slow.Choose("fakehelper"); slow.UntilRunning("fakehelper");
        Assert.True(slow.PumpUntil("fakehelper", EngineKeepUpChange.KeepingUp, 300));
        slow.Pump(200);
        Assert.False(slow.Has("fakehelper", EngineKeepUpChange.FellBehind));
        slow.Service(Rig.SlowService);
        Assert.True(slow.PumpUntil("fakehelper", EngineKeepUpChange.FellBehind, 60), "slow helper processing was never said to fall behind");
        var r = slow.Results.First(x => x.Result.Change == EngineKeepUpChange.FellBehind).Result;
        _out.WriteLine(r.Describe());
        Assert.True(r.Helper, r.Describe());
        Assert.False(r.Delivery);
        Assert.Equal(EngineKeepUpRule.DefaultFallAt, r.LateBuffers);
        // The average is over the window, so it has only begun to rise from the fast figure.
        Assert.True(r.AverageMs > Rig.FastService / 1e6 && Math.Abs(r.BufferMs - 10) < 0.01, r.Describe());
        Assert.Empty(slow.Delivery);
        // It keeps running: lateness is reported, nothing is stopped or switched.
        Assert.Equal("fakehelper", slow.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.Equal("fakehelper", slow.Pipeline.NeuralSlot.RunningEngineId);
        Assert.True(slow.Pipeline.RnnEnabled);
    }

    [Fact]
    public void A_helper_warm_up_that_never_ends_is_warned_about_only_when_its_processing_is_too_slow()
    {
        // Slow processing: its answers are always late, so it never warms; the warning comes from its cost.
        using (var rig = new Rig())
        {
            rig.Service(Rig.SlowService);
            rig.Choose("stuckhelper");
            rig.UntilDelivered("stuckhelper");
            Assert.True(rig.PumpUntil("stuckhelper", EngineKeepUpChange.FellBehind, EngineKeepUpRule.DefaultStuckWarmup + 100),
                "a helper engine too slow to warm up was never said to fall behind");
            Assert.NotEqual("stuckhelper", rig.Pipeline.NeuralSlot.RunningEngineId);
            Assert.Equal("stuckhelper", rig.Pipeline.NeuralSlot.ChosenEngineId);
            Assert.True(rig.Results.First(x => x.Result.Change == EngineKeepUpChange.FellBehind).Result.Helper);
        }
        // Fast processing, answers never in time: the delivery diagnostic only.
        using (var fast = new Rig())
        {
            fast.Choose("stuckhelper");
            fast.UntilDelivered("stuckhelper");
            fast.Pump(EngineKeepUpRule.DefaultStuckWarmup + 100);
            Assert.True(fast.HasDelivery("stuckhelper", EngineKeepUpChange.FellBehind), "the warm-up that never ended was never seen");
            Thread.Sleep(100);
            Assert.False(fast.Has("stuckhelper", EngineKeepUpChange.FellBehind), "a fast helper starved of delivery was warned about");
        }
        // The same warm-up time for an engine that warms up normally says nothing of either kind.
        using var ok = new Rig();
        ok.Choose("fakehelper");
        ok.Pump(EngineKeepUpRule.DefaultStuckWarmup + 100);
        Thread.Sleep(100);
        Assert.False(ok.Has("fakehelper", EngineKeepUpChange.FellBehind));
        Assert.Empty(ok.Delivery);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_pause_in_the_stream_restarts_the_grace_so_lateness_straight_after_it_is_start_up(bool helper)
    {
        string id = helper ? "fakehelper" : "slow";
        void Late(Rig r, bool on) { if (helper) r.Service(on ? Rig.SlowService : Rig.FastService); else r.SlowMs = on ? 6 : 0; }
        // Positive control: forty late buffers with no pause, after the engine settled, fall behind.
        using (var control = new Rig())
        {
            Late(control, false);
            control.Choose(id); control.UntilRunning(id);
            control.Pump(EngineKeepUpRule.DefaultGrace + 10);
            Late(control, true);
            Assert.True(control.PumpUntil(id, EngineKeepUpChange.FellBehind, 40));
        }
        using var rig = new Rig();
        Late(rig, false);
        rig.Choose(id); rig.UntilRunning(id);
        rig.Pump(EngineKeepUpRule.DefaultGrace + 10);
        Thread.Sleep((int)(RxAudioPipeline.KeepUpGapTicks * 1000 / Stopwatch.Frequency) + 50);
        Late(rig, true);
        rig.Pump(40);
        Late(rig, false);
        rig.Pump(200);
        Thread.Sleep(100);
        Assert.False(rig.Has(id, EngineKeepUpChange.FellBehind));
    }

    /// <summary>
    /// Repair 1, B3, adopted from Codex's reproduction (2026-11-19): the time an
    /// engine spends inside the pipeline is never a gap in the audio, however
    /// long it is. At 55 ms a channel a buffer takes over 110 ms, more than the
    /// gap threshold; measured from start to start, every buffer reset the
    /// grace and the warning never came.
    /// </summary>
    [Theory]
    [InlineData(6)]
    [InlineData(55)]
    public void Sustained_slow_processing_must_warn_even_above_the_gap_threshold(double millisecondsPerChannel)
    {
        using var rig = new Rig { SlowMs = millisecondsPerChannel };
        rig.Choose("slow");
        rig.UntilRunning("slow");
        bool warned = rig.PumpUntil("slow", EngineKeepUpChange.FellBehind, 90);
        var rule = rig.Pipeline.KeepUpRuleFor(DenoiseFamily.Neural);
        _out.WriteLine($"Per stereo buffer at least {millisecondsPerChannel * 2} ms for 10 ms of audio; warning={warned}; window filled={rule.Filled}; late={rule.LateInWindow}");
        Assert.True(warned, "The gap grace hid sustained engine processing slower than 100 ms per buffer.");
    }

    /// <summary>A helper channel for the real meter, open and answered once at this figure.</summary>
    private static WdspChannel AnsweredChannel(long nanos) => AnsweredChannel(nanos, Stopwatch.GetTimestamp());

    /// <summary>The same, answered at a given time, for a test that drives the meter's clock itself.</summary>
    private static WdspChannel AnsweredChannel(long nanos, long at)
    {
        // The constructor only stores the engine and the connection; the meter reads neither.
        var ch = new WdspChannel(null!, null!, 1, 2, Array.Empty<int>(), offline: false) { Opened = true };
        ch.RecordAnswer(nanos, at);
        return ch;
    }

    /// <summary>
    /// Repair 2, adopted from Codex's re-check reproduction (2026-11-19,
    /// <c>CodexTrackLRepairReviewTests</c>): one slow answer followed by a
    /// stall in delivery is one slow computation, not a sustained fall. The
    /// real pipeline reads a real <see cref="HelperServiceMeter"/> over a real
    /// channel, whose answers the test publishes as the reader thread does.
    /// Settled on fast answers, then exactly one 12 ms answer, then 35 buffers
    /// with late answers and no new ones, 10 ms apart (below the gap
    /// threshold). Repair 1 re-read the last figure and warned, 20 late of 100.
    /// The positive control publishes a fresh slow answer on every buffer, and
    /// must warn.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delivery_starvation_after_one_slow_answer_is_not_sustained_slow_computation(bool freshSlowAnswers)
    {
        using var rig = new Rig();
        var ch = AnsweredChannel(Rig.FastService);
        var channels = new[] { ch };
        rig.Pipeline.HelperService = (_, meter, now) => meter.Read(channels, now);
        rig.BeforeBuffer = () => ch.RecordAnswer(Rig.FastService, Stopwatch.GetTimestamp());
        rig.Choose("fakehelper"); rig.UntilRunning("fakehelper");
        Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.KeepingUp, 300));

        // Exactly one costly answer; the answers after it are delayed.
        rig.BeforeBuffer = null;
        ch.RecordAnswer(Rig.SlowService, Stopwatch.GetTimestamp());
        long first = Volatile.Read(ref ch.ProcessedAt);
        rig.HelperLate = true;
        int newSlowAnswers = 1;
        for (int i = 0; i < 35; i++)
        {
            if (freshSlowAnswers) { ch.RecordAnswer(Rig.SlowService, Stopwatch.GetTimestamp()); newSlowAnswers++; }
            rig.Pump();
            Thread.Sleep(10);
        }
        Thread.Sleep(100);
        bool warned = rig.Has("fakehelper", EngineKeepUpChange.FellBehind);
        _out.WriteLine($"new slow answers={newSlowAnswers}; warning={warned}; last answer age={(Stopwatch.GetTimestamp() - first) * 1000.0 / Stopwatch.Frequency:F1} ms");
        foreach (var r in rig.Results) _out.WriteLine(r.Result.Describe());
        Assert.True(rig.HasDelivery("fakehelper", EngineKeepUpChange.FellBehind));
        Assert.Equal(freshSlowAnswers, warned);
    }

    /// <summary>
    /// Repair 2: the real meter, stereo, through the real pipeline. Two
    /// channels answering every buffer at 1.7 ms each keep up; at 6 ms each
    /// (12 ms together, every channel inside 10 ms alone) they fall behind.
    /// Delivery stays on time throughout.
    /// </summary>
    [Fact]
    public void Through_the_real_meter_stereo_answers_slow_only_together_fall_behind_and_fast_ones_keep_up()
    {
        using var rig = new Rig();
        var left = AnsweredChannel(1_700_000);
        var right = AnsweredChannel(1_700_000);
        var channels = new[] { left, right };
        long each = 1_700_000;
        rig.Pipeline.HelperService = (_, meter, now) => meter.Read(channels, now);
        rig.BeforeBuffer = () =>
        {
            long n = Volatile.Read(ref each), at = Stopwatch.GetTimestamp();
            left.RecordAnswer(n, at); right.RecordAnswer(n, at);
        };
        rig.Choose("fakehelper"); rig.UntilRunning("fakehelper");
        Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.KeepingUp, 300));
        rig.Pump(200);
        Thread.Sleep(50);
        Assert.False(rig.Has("fakehelper", EngineKeepUpChange.FellBehind));
        Volatile.Write(ref each, 6_000_000);
        Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.FellBehind, 60), "stereo at 6 ms a channel was never said to fall behind");
        var r = rig.Results.First(x => x.Result.Change == EngineKeepUpChange.FellBehind).Result;
        _out.WriteLine(r.Describe());
        Assert.True(r.Helper);
        Assert.Empty(rig.Delivery);
    }

    /// <summary>
    /// Repair 2: a burst of answers after a stall counts each answer once, at
    /// its own processing time, against the frame of audio it processed. Thirty
    /// delayed answers that each took 12 ms are thirty slow computations, and
    /// warn; thirty delayed fast ones are not, and do not. Nothing is counted
    /// for the stall itself.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_burst_of_delayed_answers_counts_each_answer_once_at_its_own_cost(bool slowAnswers)
    {
        using var rig = new Rig();
        var ch = AnsweredChannel(Rig.FastService);
        var channels = new[] { ch };
        rig.Pipeline.HelperService = (_, meter, now) => meter.Read(channels, now);
        rig.BeforeBuffer = () => ch.RecordAnswer(Rig.FastService, Stopwatch.GetTimestamp());
        rig.Choose("fakehelper"); rig.UntilRunning("fakehelper");
        Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.KeepingUp, 300));

        rig.BeforeBuffer = null;
        rig.Pump(30); // the stall: no new answers
        var rule = rig.Pipeline.KeepUpRuleFor(DenoiseFamily.Neural);
        Assert.Equal(0, rule.LateInWindow);
        long at = Stopwatch.GetTimestamp();
        for (int i = 0; i < 30; i++) ch.RecordAnswer(slowAnswers ? Rig.SlowService : Rig.FastService, at);
        rig.Pump();
        Thread.Sleep(100);
        _out.WriteLine($"slow={slowAnswers}: late in window {rule.LateInWindow} of {rule.Filled}");
        Assert.Equal(slowAnswers ? 30 : 0, rule.LateInWindow);
        Assert.Equal(slowAnswers, rig.Has("fakehelper", EngineKeepUpChange.FellBehind));
    }

    /// <summary>
    /// Repair 2: a helper warm-up is judged on new work too. One slow answer
    /// and then a warm-up that never ends for want of answers is a delivery
    /// fact, not a slow helper; the positive control, a slow answer on every
    /// buffer of the warm-up, warns.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_never_ending_warm_up_after_one_slow_answer_is_not_a_slow_helper(bool freshSlowAnswers)
    {
        using var rig = new Rig();
        var ch = AnsweredChannel(Rig.FastService);
        var channels = new[] { ch };
        rig.Pipeline.HelperService = (_, meter, now) => meter.Read(channels, now);
        rig.Choose("stuckhelper");
        rig.UntilDelivered("stuckhelper");
        rig.Pump(5);
        ch.RecordAnswer(Rig.SlowService, Stopwatch.GetTimestamp());
        if (freshSlowAnswers) rig.BeforeBuffer = () => ch.RecordAnswer(Rig.SlowService, Stopwatch.GetTimestamp());
        rig.Pump(EngineKeepUpRule.DefaultStuckWarmup + 100);
        Assert.True(rig.HasDelivery("stuckhelper", EngineKeepUpChange.FellBehind), "the warm-up that never ended was never seen");
        Thread.Sleep(100);
        Assert.Equal(freshSlowAnswers, rig.Has("stuckhelper", EngineKeepUpChange.FellBehind));
    }

    /// <summary>
    /// Repair 3, adopted from Codex's second re-check (2026-11-19,
    /// <c>CodexTrackLRecheck2Tests</c>): the same measured work reaches the
    /// same fall whatever order the helper's reader publishes the two stereo
    /// channels' answers in. The host's writer drains one channel's queue
    /// before the next, so a reader can see the left two answers ahead of the
    /// right and then the right two ahead of the left. Settled on fast stereo
    /// answers, then twenty-one new stereo frames at 6 ms a channel (12 ms
    /// together): staggered seven times as two-and-one then one-and-two, or,
    /// the positive control, balanced. Repair 2 advanced every channel past
    /// its surplus answers while forwarding only the fewest, so the staggered
    /// schedule recorded 14 late frames and never warned; balanced recorded 21
    /// and warned.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Twenty_one_slow_stereo_frames_warn_whatever_order_their_answers_are_published_in(bool staggered)
    {
        using var rig = new Rig();
        var left = AnsweredChannel(1_000_000);
        var right = AnsweredChannel(1_000_000);
        var channels = new[] { left, right };
        rig.Pipeline.HelperService = (_, meter, now) => meter.Read(channels, now);
        rig.BeforeBuffer = () => { long n = Stopwatch.GetTimestamp(); left.RecordAnswer(1_700_000, n); right.RecordAnswer(1_700_000, n); };
        rig.Choose("fakehelper"); rig.UntilRunning("fakehelper");
        Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.KeepingUp, 300));
        rig.BeforeBuffer = null;
        if (staggered)
        {
            for (int i = 0; i < 7; i++)
            {
                long n = Stopwatch.GetTimestamp();
                left.RecordAnswer(6_000_000, n); left.RecordAnswer(6_000_000, n); right.RecordAnswer(6_000_000, n); rig.Pump();
                right.RecordAnswer(6_000_000, n); right.RecordAnswer(6_000_000, n); left.RecordAnswer(6_000_000, n); rig.Pump();
            }
        }
        else
        {
            for (int i = 0; i < 21; i++) { long n = Stopwatch.GetTimestamp(); left.RecordAnswer(6_000_000, n); right.RecordAnswer(6_000_000, n); rig.Pump(); }
        }
        Thread.Sleep(100);
        var rule = rig.Pipeline.KeepUpRuleFor(DenoiseFamily.Neural);
        _out.WriteLine($"staggered={staggered}: 21 new stereo frames at 12 ms each; late in window={rule.LateInWindow}, behind={rule.Behind}");
        Assert.True(rig.Has("fakehelper", EngineKeepUpChange.FellBehind), "the same measured work did not reach the same fall");
    }

    /// <summary>
    /// Repair 4, adopted from Codex's third re-check (2026-11-19,
    /// <c>CodexTrackLRecheck3Tests.Retained_work_reaches_recovery_after_the_other_channel_expires</c>):
    /// every frame the meter forwards reaches the rule, however many arrive at
    /// once. Settled on fast stereo answers; then 800 buffers where the left
    /// answers twice and the right once, every answer 6 ms, so the stereo rule
    /// falls behind and the left keeps 800 surplus answers. The right stops; once
    /// its second of freshness has passed, the left answers once, and the meter
    /// forwards all 801 left answers at 6 ms, which is keeping up. Repair 3's
    /// pipeline judged 100 of them and discarded 701, so with no answers after
    /// it the engine stayed behind. The positive control feeds the same rule the
    /// omitted 701 by hand; after the fix the pipeline gets there by itself and
    /// the control changes nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_batch_of_retained_answers_larger_than_the_window_all_reach_the_rule_and_it_recovers(bool feedTheOmittedByHand)
    {
        using var rig = new Rig();
        long time = Stopwatch.GetTimestamp();
        var a = AnsweredChannel(1_000_000, time);
        var b = AnsweredChannel(1_000_000, time);
        var channels = new[] { a, b };
        rig.Pipeline.HelperService = (_, meter, _) => meter.Read(channels, time);
        rig.BeforeBuffer = () => { a.RecordAnswer(1_700_000, time); b.RecordAnswer(1_700_000, time); };
        rig.Choose("fakehelper"); rig.UntilRunning("fakehelper");
        Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.KeepingUp, 300));
        rig.BeforeBuffer = null;
        for (int i = 0; i < 800; i++)
        {
            time += Stopwatch.Frequency / 100;
            a.RecordAnswer(6_000_000, time); a.RecordAnswer(6_000_000, time); b.RecordAnswer(6_000_000, time);
            rig.Pump();
        }
        var rule = rig.Pipeline.KeepUpRuleFor(DenoiseFamily.Neural);
        Assert.True(rule.Behind, "the 12 ms stereo frames never fell behind, so the recovery below proves nothing");
        time += Stopwatch.Frequency + 1;
        a.RecordAnswer(6_000_000, time);
        rig.Pump();      // B has expired: the meter forwards 801 fast mono frames.
        rig.Pump(10);    // No new answers: nothing more to judge.
        _out.WriteLine($"801 retained mono frames at 6 ms: behind={rule.Behind}, late={rule.LateInWindow}, filled={rule.Filled}");
        if (feedTheOmittedByHand)
        {
            // Codex's positive control: the same rule given the 701 a window-sized pipeline omitted.
            for (int i = 0; i < 701; i++) rule.Observe(false);
            _out.WriteLine($"after 701 more fast frames by hand: behind={rule.Behind}");
        }
        Assert.False(rule.Behind, "the meter forwarded 801 answers but they did not all reach the rule");
    }

    /// <summary>
    /// Repair 4, the warm-up path Codex named beside it: a helper engine whose
    /// warm-up is reported as one batch of 301 slow frames (12 ms each) has
    /// spent more than the stuck mark warming slowly, and warns. Repair 3 judged
    /// 100 of them and, with no answers after, never did. The positive control
    /// is the same 301 slow frames one per buffer.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_warm_up_reported_in_one_large_batch_of_slow_frames_warns_like_one_reported_frame_by_frame(bool oneAtATime)
    {
        using var rig = new Rig();
        var ch = AnsweredChannel(Rig.FastService);
        var channels = new[] { ch };
        rig.Pipeline.HelperService = (_, meter, now) => meter.Read(channels, now);
        rig.Choose("stuckhelper");
        rig.UntilDelivered("stuckhelper");
        rig.Pump(5);
        int slow = EngineKeepUpRule.DefaultStuckWarmup + 1;
        if (oneAtATime)
        {
            for (int i = 0; i < slow; i++) { ch.RecordAnswer(Rig.SlowService, Stopwatch.GetTimestamp()); rig.Pump(); }
        }
        else
        {
            long n = Stopwatch.GetTimestamp();
            for (int i = 0; i < slow; i++) ch.RecordAnswer(Rig.SlowService, n);
            rig.Pump();
        }
        rig.Pump(10);
        Thread.Sleep(100);
        Assert.True(rig.Has("stuckhelper", EngineKeepUpChange.FellBehind), $"{slow} slow warm-up frames did not all reach the rule");
    }

    [Fact]
    public void An_engine_switch_is_not_counted_and_the_new_engine_starts_its_own_count()
    {
        using var rig = new Rig();
        rig.Choose("fast"); rig.UntilRunning("fast");
        Assert.True(rig.PumpUntil("fast", EngineKeepUpChange.KeepingUp, 300));
        rig.Choose("fastspectral");
        rig.UntilRunning("fastspectral", DenoiseFamily.Spectral);
        Assert.True(rig.PumpUntil("fastspectral", EngineKeepUpChange.KeepingUp, 300));
        Assert.Equal("fast", rig.Pipeline.KeepUpRuleFor(DenoiseFamily.Neural).EngineId);
        Assert.Equal("fastspectral", rig.Pipeline.KeepUpRuleFor(DenoiseFamily.Spectral).EngineId);
        Assert.Equal(DenoiseFamily.Spectral, rig.Results.Last().Result.Family);
    }

    [Fact]
    public void The_event_is_raised_off_the_audio_thread()
    {
        using var rig = new Rig();
        rig.Choose("fast"); rig.UntilRunning("fast");
        Assert.True(rig.PumpUntil("fast", EngineKeepUpChange.KeepingUp, 300));
        Assert.All(rig.Results, r => Assert.NotEqual(Environment.CurrentManagedThreadId, r.Thread));
    }

    [Fact]
    public void Process_allocates_nothing_while_it_times_both_slots_including_a_fall_behind_and_a_delivery_change()
    {
        using var rig = new Rig();
        rig.Choose("fastspectral"); rig.UntilRunning("fastspectral", DenoiseFamily.Spectral);
        rig.Choose("fakehelper"); rig.UntilRunning("fakehelper");
        Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.KeepingUp, 300));
        rig.Pump(100);
        var audio = new float[960];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++) { Array.Fill(audio, 0.1f); rig.Pipeline.Process(audio); }
        long steady = GC.GetAllocatedBytesForCurrentThread() - before;
        // The delivery diagnostic's fall: late answers, fast processing.
        rig.HelperLate = true;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { Array.Fill(audio, 0.1f); rig.Pipeline.Process(audio); }
        long delivery = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(rig.HasDelivery("fakehelper", EngineKeepUpChange.FellBehind), "the delivery change was not raised");
        // The warning's fall: slow processing.
        rig.Service(Rig.SlowService);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { Array.Fill(audio, 0.1f); rig.Pipeline.Process(audio); }
        long falling = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(rig.PumpUntil("fakehelper", EngineKeepUpChange.FellBehind, 0), "the fall was not raised");
        // Repair 2: the real meter over four answered channels (both slots in stereo), answering every buffer, and then not.
        var channels = Enumerable.Range(0, 4).Select(_ => AnsweredChannel(Rig.FastService)).ToArray();
        rig.Pipeline.HelperService = (_, meter, now) => meter.Read(channels, now);
        for (int i = 0; i < 20; i++) { foreach (var c in channels) c.RecordAnswer(Rig.FastService, Stopwatch.GetTimestamp()); Array.Fill(audio, 0.1f); rig.Pipeline.Process(audio); }
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { Array.Fill(audio, 0.1f); rig.Pipeline.Process(audio); }
        long stalled = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { foreach (var c in channels) c.RecordAnswer(Rig.FastService, Stopwatch.GetTimestamp()); Array.Fill(audio, 0.1f); rig.Pipeline.Process(audio); }
        long metered = GC.GetAllocatedBytesForCurrentThread() - before;
        // Repair 3: the channels answering unequally, so each round carries a surplus forward.
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            long n = Stopwatch.GetTimestamp();
            for (int c = 0; c < channels.Length; c++) { channels[c].RecordAnswer(Rig.FastService, n); if ((c + i) % 2 == 0) channels[c].RecordAnswer(Rig.FastService, n); }
            Array.Fill(audio, 0.1f); rig.Pipeline.Process(audio);
        }
        long staggered = GC.GetAllocatedBytesForCurrentThread() - before;
        // Repair 4: batches far larger than the window, every frame judged.
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
        {
            long n = Stopwatch.GetTimestamp();
            foreach (var c in channels) for (int k = 0; k < 500; k++) c.RecordAnswer(i % 2 == 0 ? Rig.SlowService : Rig.FastService, n);
            Array.Fill(audio, 0.1f); rig.Pipeline.Process(audio);
        }
        long batched = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"Managed bytes on the audio thread: {steady} over 300 buffers keeping up, {delivery} over 100 buffers of late answers, {falling} over 100 buffers falling behind, {metered} over 100 buffers read by the real meter over four channels, {stalled} over 100 with no new answers, {staggered} over 100 with the channels answering unequally, {batched} over 10 buffers each judging a batch of 500 frames.");
        Assert.Equal(0, steady);
        Assert.Equal(0, delivery);
        Assert.Equal(0, falling);
        Assert.Equal(0, metered);
        Assert.Equal(0, stalled);
        Assert.Equal(0, staggered);
    }
}
