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

    private static WdspChannel Channel(long nanos, long answeredAt, bool opened = true, bool closed = false)
    {
        // The constructor only stores the engine and the connection; the sum reads neither.
        var ch = new WdspChannel(null!, null!, 1, 2, Array.Empty<int>(), offline: false) { Opened = opened, Closed = closed };
        ch.ProcessingNanos = nanos;
        ch.ProcessedAt = answeredAt;
        return ch;
    }

    [Fact]
    public void The_helpers_work_is_every_live_channel_added_together_because_it_has_one_worker()
    {
        long now = Stopwatch.GetTimestamp();
        long fresh = now - Stopwatch.Frequency / 100;
        // One channel at 6 ms keeps up; stereo at 6 ms each does not.
        Assert.False(HelperServiceTime.Late(HelperServiceTime.Sum(new[] { Channel(6_000_000, fresh) }, now)));
        Assert.True(HelperServiceTime.Late(HelperServiceTime.Sum(new[] { Channel(6_000_000, fresh), Channel(6_000_000, fresh) }, now)));
        // Both slots in stereo, each channel well inside 10 ms alone, together over it.
        var four = Enumerable.Range(0, 4).Select(_ => Channel(3_000_000, fresh)).ToArray();
        Assert.Equal(12_000_000, HelperServiceTime.Sum(four, now));
        Assert.True(HelperServiceTime.Late(HelperServiceTime.Sum(four, now)));
        // A channel nobody feeds any more, one closed, one not open and one never answered do not count.
        long stale = now - 2 * HelperServiceTime.RecentTicks;
        var others = new[] { Channel(6_000_000, fresh), Channel(9_000_000, stale), Channel(9_000_000, fresh, closed: true),
            Channel(9_000_000, fresh, opened: false), Channel(9_000_000, 0) };
        Assert.Equal(6_000_000, HelperServiceTime.Sum(others, now));
        // Exactly the audio's length is not over it; and an unknown figure is not late.
        Assert.False(HelperServiceTime.Late(HelperServiceTime.FrameNanos));
        Assert.True(HelperServiceTime.Late(HelperServiceTime.FrameNanos + 1));
        Assert.False(HelperServiceTime.Late(-1));
        Assert.Equal(10_000_000, HelperServiceTime.FrameNanos);
    }

    private static float[] Audio(int salt) => Enumerable.Range(0, 960).Select(i => MathF.Sin((i + salt * 960) * 0.013f) * 0.3f).ToArray();

    /// <summary>A receive pipeline whose NNR runs on the stub helper, as HelperSlotTests builds it.</summary>
    private sealed class Pipe : IDisposable
    {
        public readonly HelperTestRig Rig = new();
        public readonly RxAudioPipeline Pipeline;
        public readonly ConcurrentBag<WdspHelperEngine> Made = new();
        public readonly ConcurrentQueue<EngineKeepUpResult> Results = new(), Delivery = new();
        private int _salt;

        public Pipe()
        {
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
            _out.WriteLine($"channel {ch.Id}: processing {Volatile.Read(ref ch.ProcessingNanos)} ns, answered at {Volatile.Read(ref ch.ProcessedAt)}");
            Assert.True(Volatile.Read(ref ch.ProcessedAt) != 0, "the answer's arrival was not kept");
            Assert.True(Volatile.Read(ref ch.ProcessingNanos) > 0, "the helper's processing time was thrown away");
        }
        var conn = HelperServiceTime.ConnectionOf(bank);
        Assert.Same(channels[0].Conn, conn);
        long now = Stopwatch.GetTimestamp();
        long sum = HelperServiceTime.PerFrameNanos(bank, now);
        Assert.Equal(2, conn!.Lives.Length);
        Assert.Equal(channels.Sum(c => Volatile.Read(ref c.ProcessingNanos)), sum);
        Assert.False(HelperServiceTime.Late(sum), "the stub's echo is far inside 10 ms");
    }

    [Fact]
    public void Through_the_real_pipeline_a_fast_helper_whose_warm_up_is_starved_of_answers_raises_only_the_delivery_diagnostic()
    {
        using var p = new Pipe();
        p.Pipeline.RnnEnabled = true;
        Assert.True(p.Pipeline.SelectPcEngine("nnr"));
        // Hurried from the start: the answers never arrive in time, so the warm-up never ends.
        // One buffer asks for the build; then wait, feeding nothing, until it is delivered.
        p.Hurried();
        HelperTestRig.Until(() => p.Pipeline.NeuralSlot.BuildDelivered("nnr"), "the nnr bank delivered");
        for (int i = 0; i < EngineKeepUpRule.DefaultStuckWarmup + 50; i++) p.Hurried();
        Assert.True(p.Has(p.Delivery, EngineKeepUpChange.FellBehind), "the warm-up that never ended was never seen");
        Assert.False(p.Has(p.Results, EngineKeepUpChange.FellBehind, 100), "a fast helper whose warm-up was starved was warned about");
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
