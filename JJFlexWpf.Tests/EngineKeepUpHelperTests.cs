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
/// A helper engine's "falls behind" is the helper's own processing time, not
/// a missed frame (Sprint 49 Track L repair 1, B1, after Codex's check): the
/// host keeps each answer's processing time, every live channel is added
/// together because the helper has one worker, and through the real pipeline
/// and the MIT stub helper a fast helper whose answers arrive late raises only
/// the delivery diagnostic. Also the live handshake's figures for the speed
/// test (B2). Windowless; the stub is hidden, in its own job.
/// </summary>
public sealed class EngineKeepUpHelperTests
{
    private readonly ITestOutputHelper _out;
    public EngineKeepUpHelperTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    /// <summary>A channel as the host makes it, answered once at this figure and time (0 for never answered).</summary>
    private static WdspChannel Channel(long nanos, long answeredAt, bool opened = true, bool closed = false)
    {
        // The constructor only stores the engine and the connection; the meter reads neither.
        var ch = new WdspChannel(null!, null!, 1, 2, Array.Empty<int>(), offline: false) { Opened = opened, Closed = closed };
        if (answeredAt != 0) ch.RecordAnswer(nanos, answeredAt);
        return ch;
    }

    /// <summary>One more answer on each channel, at this figure, now.</summary>
    private static void Answer(long nanos, params WdspChannel[] channels)
    {
        long at = Stopwatch.GetTimestamp();
        foreach (var ch in channels) ch.RecordAnswer(nanos, at);
    }

    [Fact]
    public void The_helpers_work_is_every_live_channel_added_together_because_it_has_one_worker()
    {
        long now() => Stopwatch.GetTimestamp();
        // One channel at 6 ms keeps up; stereo at 6 ms each does not.
        var mono = Channel(6_000_000, now());
        var meter = new HelperServiceMeter();
        Assert.Equal(0, meter.Read(new[] { mono }, now()).Frames); // first seen: its answers so far are not counted
        Answer(6_000_000, mono);
        var r = meter.Read(new[] { mono }, now());
        Assert.Equal((6_000_000L, 1), (r.PerFrameNanos, r.Frames));
        Assert.False(HelperServiceTime.Late(r.PerFrameNanos));

        var left = Channel(6_000_000, now()); var right = Channel(6_000_000, now());
        meter = new HelperServiceMeter();
        meter.Read(new[] { left, right }, now());
        Answer(6_000_000, left, right);
        r = meter.Read(new[] { left, right }, now());
        Assert.Equal((12_000_000L, 1), (r.PerFrameNanos, r.Frames));
        Assert.True(HelperServiceTime.Late(r.PerFrameNanos));

        // Both slots in stereo, each channel well inside 10 ms alone, together over it.
        var four = Enumerable.Range(0, 4).Select(_ => Channel(3_000_000, now())).ToArray();
        meter = new HelperServiceMeter();
        meter.Read(four, now());
        Answer(3_000_000, four);
        r = meter.Read(four, now());
        Assert.Equal((12_000_000L, 1), (r.PerFrameNanos, r.Frames));

        // A channel nobody feeds any more, one closed, one not open and one never answered do not count.
        long stale = now() - 2 * HelperServiceTime.RecentTicks;
        var fed = Channel(6_000_000, now());
        var others = new[] { fed, Channel(9_000_000, stale), Channel(9_000_000, now(), closed: true),
            Channel(9_000_000, now(), opened: false), Channel(9_000_000, 0) };
        meter = new HelperServiceMeter();
        meter.Read(others, now());
        Assert.Equal(1, meter.Following);
        Answer(6_000_000, fed);
        r = meter.Read(others, now());
        Assert.Equal((6_000_000L, 1), (r.PerFrameNanos, r.Frames));

        // Exactly the audio's length is not over it; and an unknown figure is not late.
        Assert.False(HelperServiceTime.Late(HelperServiceTime.FrameNanos));
        Assert.True(HelperServiceTime.Late(HelperServiceTime.FrameNanos + 1));
        Assert.False(HelperServiceTime.Late(-1));
        Assert.Equal(10_000_000, HelperServiceTime.FrameNanos);
    }

    /// <summary>
    /// Repair 2, after Codex's re-check: each answer's processing time is
    /// counted exactly once, against the frame of audio it processed. Reading
    /// again with no new answer is no new work, however slow the last answer
    /// was; a burst counts each delayed answer once, at its own cost.
    /// </summary>
    [Fact]
    public void Each_answer_is_counted_once_and_no_new_answer_is_no_new_work()
    {
        var ch = Channel(3_400_000, Stopwatch.GetTimestamp());
        var channels = new[] { ch };
        var meter = new HelperServiceMeter();
        meter.Read(channels, Stopwatch.GetTimestamp());
        Answer(12_000_000, ch);
        var r = meter.Read(channels, Stopwatch.GetTimestamp());
        Assert.Equal((12_000_000L, 1), (r.PerFrameNanos, r.Frames));
        Assert.True(HelperServiceTime.Late(r.PerFrameNanos));
        // The same answer, read a hundred more times within its second: nothing new.
        for (int i = 0; i < 100; i++) Assert.Equal(0, meter.Read(channels, Stopwatch.GetTimestamp()).Frames);

        // A burst after the stall: five delayed answers, two at 12 ms and three at 2 ms, are five frames at their average.
        Answer(12_000_000, ch); Answer(12_000_000, ch);
        Answer(2_000_000, ch); Answer(2_000_000, ch); Answer(2_000_000, ch);
        r = meter.Read(channels, Stopwatch.GetTimestamp());
        Assert.Equal((6_000_000L, 5), (r.PerFrameNanos, r.Frames));
        Assert.Equal(0, meter.Read(channels, Stopwatch.GetTimestamp()).Frames);
    }

    /// <summary>
    /// Repair 2: a round waits for every served channel, so stereo is summed
    /// from both halves' own answers, never by repeating one half's figure;
    /// a channel that stops being served leaves the round; a channel caught
    /// mid-answer is read again later; and starting again counts nothing from
    /// before.
    /// </summary>
    [Fact]
    public void A_round_waits_for_every_served_channel_and_a_channel_no_longer_served_leaves_it()
    {
        var left = Channel(2_000_000, Stopwatch.GetTimestamp());
        var right = Channel(2_000_000, Stopwatch.GetTimestamp());
        var channels = new[] { left, right };
        var meter = new HelperServiceMeter();
        meter.Read(channels, Stopwatch.GetTimestamp());
        Assert.Equal(2, meter.Following);

        // Only the left has answered: no round yet.
        Answer(4_000_000, left);
        Assert.Equal(0, meter.Read(channels, Stopwatch.GetTimestamp()).Frames);
        Answer(4_000_000, left);
        Assert.Equal(0, meter.Read(channels, Stopwatch.GetTimestamp()).Frames);
        // The right answers: one round, each channel's own average, added; as many frames as the fewer answers.
        Answer(7_000_000, right);
        var r = meter.Read(channels, Stopwatch.GetTimestamp());
        Assert.Equal((11_000_000L, 1), (r.PerFrameNanos, r.Frames));
        Assert.Equal(0, meter.Read(channels, Stopwatch.GetTimestamp()).Frames);
        // Repair 3: the left's second answer was not forwarded, so it waits; the right's next answer completes its round.
        Answer(7_000_000, right);
        r = meter.Read(channels, Stopwatch.GetTimestamp());
        Assert.Equal((11_000_000L, 1), (r.PerFrameNanos, r.Frames));
        Assert.Equal(0, meter.Read(channels, Stopwatch.GetTimestamp()).Frames);

        // Caught mid-answer (the sequence odd): not read this time, read whole the next.
        Answer(1_000_000, left, right);
        Interlocked.Increment(ref right.ServedSeq);
        Assert.Equal(0, meter.Read(channels, Stopwatch.GetTimestamp()).Frames);
        Interlocked.Decrement(ref right.ServedSeq);
        r = meter.Read(channels, Stopwatch.GetTimestamp());
        Assert.Equal((2_000_000L, 1), (r.PerFrameNanos, r.Frames));

        // The right stops being served (no answer for over a second): the left's rounds go on alone.
        Answer(3_000_000, left);
        long later = Stopwatch.GetTimestamp() + 2 * HelperServiceTime.RecentTicks;
        left.RecordAnswer(3_000_000, later);
        r = meter.Read(channels, later);
        Assert.Equal(1, meter.Following);
        Assert.Equal((3_000_000L, 2), (r.PerFrameNanos, r.Frames));

        // Starting again: what was answered before is not counted.
        Answer(9_000_000, left);
        meter.Forget();
        Assert.Equal(0, meter.Read(channels, Stopwatch.GetTimestamp()).Frames);
        Answer(1_000_000, left, right);
        r = meter.Read(channels, Stopwatch.GetTimestamp());
        Assert.Equal((2_000_000L, 1), (r.PerFrameNanos, r.Frames));
    }

    /// <summary>
    /// Repair 3, adopted from Codex's second re-check: every measured answer
    /// reaches the rule exactly once, whatever order the channels' answers are
    /// published in. Left twice and right once, then right twice and left
    /// once, each answer 6 ms: three new answers a channel, three stereo
    /// frames, 36 ms of work. Repair 2 forwarded two frames at 12 ms, 24 ms,
    /// because a round advanced every channel past answers it did not forward.
    /// </summary>
    [Fact]
    public void Staggered_stereo_answers_forward_all_their_work_exactly_once()
    {
        long now = Stopwatch.GetTimestamp();
        var left = Channel(1_000_000, now); var right = Channel(1_000_000, now);
        var channels = new[] { left, right };
        var meter = new HelperServiceMeter();
        meter.Read(channels, now);
        left.RecordAnswer(6_000_000, now); left.RecordAnswer(6_000_000, now); right.RecordAnswer(6_000_000, now);
        var first = meter.Read(channels, now);
        right.RecordAnswer(6_000_000, now); right.RecordAnswer(6_000_000, now); left.RecordAnswer(6_000_000, now);
        var second = meter.Read(channels, now);
        long credited = first.PerFrameNanos * first.Frames + second.PerFrameNanos * second.Frames;
        _out.WriteLine($"3 new answers a channel, 36 ms of work: {first.Frames} + {second.Frames} frames forwarded, {credited / 1e6} ms");
        Assert.Equal((12_000_000L, 1), (first.PerFrameNanos, first.Frames));
        Assert.Equal((12_000_000L, 2), (second.PerFrameNanos, second.Frames));
        Assert.Equal(36_000_000L, credited);
        // Everything is forwarded: nothing is left to count again.
        Assert.Equal(0, meter.Read(channels, now).Frames);

        // A surplus keeps its share of the cost: the left answers twice (1 and 9 ms, 5 ms each on average), the right once.
        left.RecordAnswer(1_000_000, now); left.RecordAnswer(9_000_000, now); right.RecordAnswer(1_000_000, now);
        var r = meter.Read(channels, now);
        Assert.Equal((6_000_000L, 1), (r.PerFrameNanos, r.Frames)); // the left's two at their 5 ms average, and the right's 1
        right.RecordAnswer(2_000_000, now);
        r = meter.Read(channels, now);
        Assert.Equal((7_000_000L, 1), (r.PerFrameNanos, r.Frames)); // the left's remaining half, 5 ms, and the right's 2
        Assert.Equal(0, meter.Read(channels, now).Frames);
    }

    /// <summary>
    /// Repair 3, Codex's control from its second re-check, kept: a channel that
    /// stops answering holds the rounds back only for its one second of
    /// freshness; after that the other channel's waiting work is judged, all of
    /// it, so a stopped channel cannot freeze the warning.
    /// </summary>
    [Fact]
    public void A_channel_that_stops_answering_holds_the_rounds_for_one_second_and_then_the_waiting_work_is_judged()
    {
        long now = Stopwatch.GetTimestamp();
        var a = Channel(1_000_000, now); var b = Channel(1_000_000, now);
        var channels = new[] { a, b };
        var meter = new HelperServiceMeter();
        meter.Read(channels, now);
        for (int i = 1; i <= 99; i++)
        {
            long n = now + i * Stopwatch.Frequency / 100;
            a.RecordAnswer(12_000_000, n);
            Assert.Equal(0, meter.Read(channels, n).Frames);
        }
        long after = now + Stopwatch.Frequency + 1;
        a.RecordAnswer(12_000_000, after);
        var r = meter.Read(channels, after);
        _out.WriteLine($"after the stopped channel expires: following={meter.Following}, frames={r.Frames}, cost={r.PerFrameNanos}");
        Assert.Equal(1, meter.Following);
        Assert.Equal((12_000_000L, 100), (r.PerFrameNanos, r.Frames));
        var rule = new EngineKeepUpRule();
        rule.Restart("fakehelper");
        for (int i = 0; i < r.Frames; i++) rule.Observe(HelperServiceTime.Late(r.PerFrameNanos));
        Assert.True(rule.Behind);
    }

    private static float[] Audio(int salt) => Enumerable.Range(0, 960).Select(i => MathF.Sin((i + salt * 960) * 0.013f) * 0.3f).ToArray();

    /// <summary>A receive pipeline whose NNR runs on the stub helper, as HelperSlotTests builds it.</summary>
    private sealed class Pipe : IDisposable
    {
        public readonly HelperTestRig Rig;
        public readonly RxAudioPipeline Pipeline;
        public readonly ConcurrentBag<WdspHelperEngine> Made = new();
        public readonly ConcurrentQueue<EngineKeepUpResult> Results = new(), Delivery = new();
        private int _salt;

        /// <param name="args">The stub's test options, if any (by launch).</param>
        public Pipe(Func<int, IReadOnlyList<string>>? args = null)
        {
            Rig = new HelperTestRig(args);
            var host = Rig.Host;
            var rows = new[] {
                new DenoiseEngineEntry("nnr", DenoiseSide.Pc, DenoiseFamily.Neural, "audio.denoise.nnr",
                    new[] { DenoiseDirection.Receive, DenoiseDirection.Transmit },
                    (initial, direction) => { var e = new WdspHelperEngine(host, "nnr", direction, initial); Made.Add(e); return e; },
                    () => host.Probe("nnr")),
                new DenoiseEngineEntry("spectral_capture", DenoiseSide.Pc, DenoiseFamily.Spectral, "audio.denoise.spectral_capture",
                    new[] { DenoiseDirection.Receive }, (_, _) => new SpectralCaptureEngine(), () => Unavailability.None,
                    SettingDescriptors: SpectralCaptureEngine.SettingDescriptors),
            };
            Pipeline = new RxAudioPipeline(catalog: new DenoiseEngineCatalog(rows));
            Pipeline.KeepUpResult += (_, r) => Results.Enqueue(r);
            Pipeline.DeliveryDiagnostic += (_, r) => Delivery.Enqueue(r);
        }

        /// <summary>One buffer, paced so the helper's answers are in time.</summary>
        public void Paced() { HelperTestRig.Pace(Made.ToArray()); Pipeline.Process(Audio(_salt++)); }

        /// <summary>One buffer straight after the last: faster than the helper can answer, so its answers are late.</summary>
        public void Hurried() => Pipeline.Process(Audio(_salt++));

        public bool Has(ConcurrentQueue<EngineKeepUpResult> queue, EngineKeepUpChange change, int waitMs = 500)
        {
            var t = Stopwatch.StartNew();
            while (true)
            {
                if (queue.Any(r => r.EngineId == "nnr" && r.Change == change)) return true;
                if (t.ElapsedMilliseconds >= waitMs) return false;
                Thread.Sleep(5);
            }
        }

        public void Dispose() { Pipeline.Dispose(); Rig.Dispose(); }
    }

    [Fact]
    public void The_host_keeps_each_answers_processing_time_and_the_running_bank_reaches_its_connection()
    {
        using var p = new Pipe();
        p.Pipeline.RnnEnabled = true;
        Assert.True(p.Pipeline.SelectPcEngine("nnr"));
        HelperTestRig.Until(() => p.Pipeline.NeuralSlot.ProcessingEngineId == "nnr", "nnr processing", step: p.Paced);
        for (int i = 0; i < 20; i++) p.Paced();
        Thread.Sleep(100); // the answers still in flight arrive, so the figures stop moving
        var bank = p.Pipeline.NeuralSlot.RunningBank!;
        var channels = Enumerable.Range(0, bank.Channels).Select(c => ((WdspHelperEngine)bank.EngineFor(c)).Channel!).ToArray();
        Assert.Equal(2, channels.Length);
        foreach (var ch in channels)
        {
            Assert.True(ch.TryReadServed(out long answers, out long total));
            _out.WriteLine($"channel {ch.Id}: processing {Volatile.Read(ref ch.ProcessingNanos)} ns, answered at {Volatile.Read(ref ch.ProcessedAt)}, {answers} answers, {total} ns in all");
            Assert.True(Volatile.Read(ref ch.ProcessedAt) != 0, "the answer's arrival was not kept");
            Assert.True(Volatile.Read(ref ch.ProcessingNanos) > 0, "the helper's processing time was thrown away");
            Assert.True(answers >= 20, "the host did not count each answer: " + answers);
            Assert.True(total >= answers, "the host did not add each answer's processing time");
        }
        var conn = HelperServiceTime.ConnectionOf(bank);
        Assert.Same(channels[0].Conn, conn);
        Assert.Equal(2, conn!.Lives.Length);
        // A meter over the running bank: nothing new while no buffer is fed; then each new answer, on both channels.
        var meter = new HelperServiceMeter();
        Assert.Equal(0, meter.Read(bank, Stopwatch.GetTimestamp()).Frames);
        Assert.Equal(2, meter.Following);
        Assert.Equal(0, meter.Read(bank, Stopwatch.GetTimestamp()).Frames);
        for (int i = 0; i < 5; i++) p.Paced();
        Thread.Sleep(100);
        var r = meter.Read(bank, Stopwatch.GetTimestamp());
        _out.WriteLine($"after five buffers: {r.Frames} frames at {r.PerFrameNanos} ns for both channels");
        Assert.True(r.Frames >= 1, "the new answers were not seen");
        Assert.True(r.PerFrameNanos > 0);
        Assert.False(HelperServiceTime.Late(r.PerFrameNanos), "the stub's echo is far inside 10 ms");
        Assert.Equal(0, meter.Read(bank, Stopwatch.GetTimestamp()).Frames);
    }

    /// <summary>
    /// Sprint 49 Track P2 repair 4, for Track L's record (Codex's re-check 3 of the P2
    /// branch at 7a8b2f90): this starved the warm-up by feeding buffers as fast as
    /// possible and assuming the fast echo helper could not answer in time. That is a
    /// throughput race: any extra work per buffer (P2's classifier, a loaded machine) gave
    /// the answers time to arrive, the warm-up ended, and it failed one run in eight
    /// alone and once in a full x86 run. The starvation is now explicit, in the test
    /// only: the stub holds each channel's answer until sixteen more of that channel's
    /// frames have arrived (past the deepest pipeline the Advanced tab allows), so every
    /// answer is late by frames whatever the pace, while answers keep arriving and the
    /// connection stays healthy. Nothing in the product changed; no deadline moved.
    /// </summary>
    [Fact]
    public void Through_the_real_pipeline_a_fast_helper_whose_warm_up_is_starved_of_answers_raises_only_the_delivery_diagnostic()
    {
        const int behind = 16;
        Assert.True(behind > WdspPipelineDepth.MaxReceiveDepth, "the gate must hold answers past every pipeline depth");
        using var p = new Pipe(_ => new[] { "--answer-behind", behind.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        p.Pipeline.RnnEnabled = true;
        Assert.True(p.Pipeline.SelectPcEngine("nnr"));
        // One buffer asks for the build; then wait, feeding nothing, until it is delivered.
        p.Hurried();
        HelperTestRig.Until(() => p.Pipeline.NeuralSlot.BuildDelivered("nnr"), "the nnr bank delivered");
        // The answers are to be starved by the gate, so every hurried frame must reach
        // the helper: a frame given to a channel still opening is never sent
        // (WdspHelperEngine.ProcessFrame), which starves the warm-up by the connection,
        // the case the last assertion below rules out. Until Sprint 49 Track RT this held
        // only because the audio thread was slow enough; with nothing traced on it any
        // more, the 350 hurried buffers can all go before the stub has opened a channel.
        HelperTestRig.Until(() => p.Made.Count == 2 && p.Made.All(e => e.Channel is { Opened: true }), "both nnr channels open");
        // Nor may a hurried frame find the channel's send ring full: the audio thread
        // drops a frame it cannot queue (WdspChannel.TrySend, eight frames), which is the
        // connection starving the warm-up again. So each buffer still goes at once, never
        // waiting for an ANSWER, but only once the writer has room for its frame. Sprint
        // 49 Track RT repair 1: under a full suite's load the loop (about 80 us a buffer
        // with nothing traced on it) outran the writer thread and the answers fell to 21.
        var channels = p.Made.Select(e => e.Channel!).ToArray();
        for (int i = 0; i < EngineKeepUpRule.DefaultStuckWarmup + 50; i++)
        {
            var room = Stopwatch.StartNew();
            while (channels.Any(c => c.SendHead - Volatile.Read(ref c.SendTail) >= WdspChannel.SendCapacity) && room.ElapsedMilliseconds < 2000)
                Thread.Sleep(0);
            p.Hurried();
        }
        Assert.True(p.Has(p.Delivery, EngineKeepUpChange.FellBehind), "the warm-up that never ended was never seen");
        Assert.False(p.Has(p.Results, EngineKeepUpChange.FellBehind, 100), "a fast helper whose warm-up was starved was warned about");
        // Starved by the gate, not by silence: the answers kept coming, each one fast.
        long answers = 0, nanos = 0;
        foreach (var e in p.Made)
            if (e.Channel is { } ch && ch.TryReadServed(out long a, out long t)) { answers += a; nanos += t; }
        _out.WriteLine($"{answers} answers arrived, every one {behind} frames late, at {(answers > 0 ? nanos / answers : 0)} ns each; the connection stayed up");
        Assert.True(answers >= EngineKeepUpRule.DefaultStuckWarmup, "the answers stopped: the connection, not the gate, starved the warm-up");
        Assert.False(HelperServiceTime.Late(nanos / answers), "the stub's echo is far inside 10 ms");
    }

    [Fact]
    public void Through_the_real_pipeline_a_fast_running_helper_whose_answers_turn_late_raises_only_the_delivery_diagnostic()
    {
        using var p = new Pipe();
        p.Pipeline.RnnEnabled = true;
        Assert.True(p.Pipeline.SelectPcEngine("nnr"));

        // Paced: it warms, runs and keeps up.
        HelperTestRig.Until(() => p.Pipeline.NeuralSlot.ProcessingEngineId == "nnr", "nnr processing", step: p.Paced);
        HelperTestRig.Until(() => p.Results.Any(r => r.EngineId == "nnr" && r.Change == EngineKeepUpChange.KeepingUp),
            "nnr keeping up", ms: 20000, step: p.Paced);

        // Hurried again while running: late answers, fast processing.
        int deliveryFalls = p.Delivery.Count(r => r.Change == EngineKeepUpChange.FellBehind);
        long lateBefore = p.Pipeline.NeuralSlot.RunningBank!.LateFrames;
        for (int i = 0; i < 400; i++) p.Hurried();
        long late = p.Pipeline.NeuralSlot.RunningBank!.LateFrames - lateBefore;
        _out.WriteLine($"{late} late frames over 400 hurried buffers");
        Assert.True(late >= 100, "the hurried buffers did not make the answers late: " + late);
        HelperTestRig.Until(() => p.Delivery.Count(r => r.Change == EngineKeepUpChange.FellBehind) > deliveryFalls,
            "the delivery diagnostic while running", ms: 2000);
        foreach (var r in p.Delivery) _out.WriteLine(r.Describe());
        foreach (var r in p.Results) _out.WriteLine(r.Describe());
        Assert.False(p.Has(p.Results, EngineKeepUpChange.FellBehind, 100), "a fast helper whose answers were late was warned about");
    }

    [Fact]
    public void The_speed_test_reads_the_running_helpers_handshake_figures_without_starting_one()
    {
        using var rig = new HelperTestRig();
        // Nothing connected: nothing to read, and asking starts nothing.
        Assert.Null(rig.Host.CurrentConnection);
        Assert.Empty(HelperSpeedFigures.From(null, rig.Host.CurrentConnection?.Hello).Costs);
        Assert.Equal(0, rig.Host.StartsAttempted);

        var engine = rig.Engine("nnr");
        HelperTestRig.Until(() => engine.IsAvailable, "nnr open");
        var hello = rig.Host.CurrentConnection?.Hello;
        Assert.NotNull(hello);
        var figures = HelperSpeedFigures.From(null, hello);
        // The stub offers both engines, 100 calibration frames, 50 and 90 microseconds.
        foreach (var id in new[] { "nnr", "nr2" })
        {
            var line = Assert.Single(figures.Lines(id));
            Assert.Equal((HelperCostSource.Handshake, (string?)null, 100), (line.Source, line.Model, line.Frames));
            Assert.Equal(0.05, line.MedianMs, 6);
            Assert.Equal(0.09, line.P99Ms, 6);
            Assert.Same(line, figures.AtDefaults(id, "standard"));
        }
    }
}
