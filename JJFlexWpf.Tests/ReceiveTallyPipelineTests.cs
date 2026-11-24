using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// The running tally (#1229) through the real <see cref="RxAudioPipeline"/>,
/// with the fake engines of <see cref="EngineKeepUpPipelineTests"/>: a fast and
/// a deliberately slow engine in this process, and a fake helper engine whose
/// processing time and missed deadlines the test sets. The tally's period is
/// shortened so a "minute" passes in a few buffers. No window, no radio, no
/// sound.
/// </summary>
public sealed class ReceiveTallyPipelineTests
{
    private readonly ITestOutputHelper _out;

    public ReceiveTallyPipelineTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    private static DenoiseEngineEntry Row(string id, DenoiseFamily family, Func<IDenoiseEngine> make) =>
        new(id, DenoiseSide.Pc, family, "audio.denoise." + id, new[] { DenoiseDirection.Receive },
            (_, _) => make(), () => Unavailability.None);

    private sealed class Rig : IDisposable
    {
        public RxAudioPipeline Pipeline { get; }
        public ConcurrentQueue<SlotTallyReport> Tallies { get; } = new();
        public volatile bool HelperLate;
        public volatile bool SlowNow;
        public long ServiceNanos = 3_400_000;
        private readonly float[] _audio = new float[960];
        /// <summary>The pipeline's off-audio writer, settled after each buffer (repair 2).</summary>
        public readonly SettledOffAudioWriter Writer = new();

        public Rig(long periodTicks)
        {
            var rows = new[]
            {
                Row("fast", DenoiseFamily.Neural, () => new EngineKeepUpPipelineTests.Engine("fast")),
                Row("slow", DenoiseFamily.Neural, () => new EngineKeepUpPipelineTests.Engine("slow", () => 6)),
                Row("fakehelper", DenoiseFamily.Neural, () => new EngineKeepUpPipelineTests.Engine("fakehelper", late: () => HelperLate)),
                Row("fastspectral", DenoiseFamily.Spectral, () => new EngineKeepUpPipelineTests.Engine("fastspectral")),
                Row("spectral_capture", DenoiseFamily.Spectral, () => new EngineKeepUpPipelineTests.Engine("spectral_capture")),
                Row("rnnoise", DenoiseFamily.Neural, () => new EngineKeepUpPipelineTests.Engine("rnnoise")),
                Row("switchable", DenoiseFamily.Neural, () => new EngineKeepUpPipelineTests.Engine("switchable", () => SlowNow ? 6 : 0)),
            };
            Pipeline = new RxAudioPipeline(48000, 2, new DenoiseEngineCatalog(rows), periodTicks, Writer)
            {
                IsHelperEngine = id => id is "fakehelper",
            };
            Pipeline.HelperService = (_, _, _) => HelperServiceReading.Fresh(Volatile.Read(ref ServiceNanos));
            Pipeline.TallyWritten += (_, r) => Tallies.Enqueue(r);
        }

        public void Pump(int buffers = 1)
        {
            for (int i = 0; i < buffers; i++) { Array.Fill(_audio, 0.1f); Pipeline.Process(_audio); Writer.Settle(); }
        }

        public void Choose(string id)
        {
            Assert.True(Pipeline.SelectPcEngine(id, out var refusal), refusal);
            if (Pipeline.NeuralSlot.ChosenEngineId == id) Pipeline.RnnEnabled = true; else Pipeline.SpectralEnabled = true;
            var slot = Pipeline.NeuralSlot.ChosenEngineId == id ? Pipeline.NeuralSlot : Pipeline.SpectralSlot;
            var t = Stopwatch.StartNew();
            while (slot.ProcessingEngineId != id && t.ElapsedMilliseconds < 5000) { Pump(); Thread.Sleep(1); }
            Assert.Equal(id, slot.ProcessingEngineId);
        }

        /// <summary>Wait a little for tally lines still on the pool thread.</summary>
        public SlotTallyReport[] Await(Func<SlotTallyReport, bool> match, int atLeast = 1)
        {
            // Every buffer settles the writer, and FeedEnded's totals are settled here, so
            // what has been published has been written: no wait (repair 2).
            Writer.Settle();
            return Tallies.Where(match).ToArray();
        }

        public void Dispose() { Pipeline.Dispose(); Writer.Settle(); Writer.Dispose(); }
    }

    [Fact]
    public void A_running_engine_writes_a_tally_line_each_period_with_the_buffers_it_processed()
    {
        using var rig = new Rig(Stopwatch.Frequency / 20); // a 50 ms "minute"
        rig.Choose("fast");
        for (int i = 0; i < 40; i++) { rig.Pump(5); Thread.Sleep(5); }
        var minutes = rig.Await(r => r.Reason == SlotTallyReason.Minute && r.EngineId == "fast", 2);
        foreach (var r in minutes) _out.WriteLine(r.Describe());
        Assert.True(minutes.Length >= 2, "no tally line was written each period");
        Assert.All(minutes, r =>
        {
            Assert.Equal(DenoiseFamily.Neural, r.Family);
            Assert.False(r.Helper);
            Assert.True(r.Buffers > 0);
            Assert.Equal(0, r.Late);
            Assert.True(r.AverageMsPer10 >= 0 && r.AverageMsPer10 < 10, r.Describe());
        });
        // The spectral slot ran nothing, so it wrote nothing.
        Assert.DoesNotContain(rig.Tallies, r => r.Family == DenoiseFamily.Spectral);
    }

    [Fact]
    public void A_slow_engine_in_this_process_is_counted_late_with_its_worst_second_over_its_audio()
    {
        using var rig = new Rig(0); // the real minute: only PC audio off closes it here
        rig.Choose("slow");
        rig.Pump(130);
        rig.Pipeline.FeedEnded();
        var ended = rig.Await(r => r.Reason == SlotTallyReason.FeedEnded && r.EngineId == "slow").Single();
        var session = rig.Await(r => r.Reason == SlotTallyReason.Session && r.Family == DenoiseFamily.Neural).Single();
        _out.WriteLine(ended.Describe());
        _out.WriteLine(session.Describe());
        // Grace excluded (repair 1): of 130 buffers the rule judges at most 80, all late.
        Assert.True(ended.Late >= 70 && ended.Late == ended.Judged && ended.Judged <= ended.Buffers - 51, ended.Describe());
        Assert.True(ended.AverageMsPer10 > 10, ended.Describe());
        Assert.True(ended.WorstSecondMsPer10 >= 10, ended.Describe());
        Assert.True(ended.Seconds >= 1);
        Assert.Equal(1, session.Periods);
        Assert.Equal(ended.Buffers, session.Buffers);
        // And the other slot says it ran nothing.
        var spectral = rig.Await(r => r.Reason == SlotTallyReason.Session && r.Family == DenoiseFamily.Spectral).Single();
        Assert.Contains("ran no engine", spectral.Describe());
    }

    [Fact]
    public void A_fast_engine_is_counted_with_no_late_buffers_for_as_long()
    {
        // The negative beside the slow engine above, over the same buffers.
        using var rig = new Rig(0);
        rig.Choose("fast");
        rig.Pump(130);
        rig.Pipeline.FeedEnded();
        var ended = rig.Await(r => r.Reason == SlotTallyReason.FeedEnded && r.EngineId == "fast").Single();
        _out.WriteLine(ended.Describe());
        Assert.Equal(0, ended.Late);
        Assert.True(ended.Buffers >= 130);
        Assert.True(ended.AverageMsPer10 < 10, ended.Describe());
    }

    [Fact]
    public void A_helper_engine_tally_counts_its_late_frames_its_missed_deadlines_and_the_dip_that_began()
    {
        using var rig = new Rig(0);
        rig.Choose("fakehelper");
        rig.Pump(300);
        rig.HelperLate = true;                    // answers miss their deadline: the delivery diagnostic dips
        rig.Pump(200);
        rig.HelperLate = false;
        Volatile.Write(ref rig.ServiceNanos, 12_000_000); // the helper's own work slower than the audio
        rig.Pump(200); // two seconds, so at least one whole second of audio is all slow
        rig.Pipeline.FeedEnded();
        var ended = rig.Await(r => r.Reason == SlotTallyReason.FeedEnded && r.EngineId == "fakehelper").Single();
        _out.WriteLine(ended.Describe());
        Assert.True(ended.Helper);
        Assert.True(ended.Missed >= 150, ended.Describe());
        Assert.Equal(1, ended.DipsBegan);
        Assert.Equal(200, ended.Late);
        Assert.True(ended.Judged >= 649, ended.Describe());
        Assert.True(ended.WorstSecondMsPer10 >= 12 - 0.01, ended.Describe());
    }

    [Fact]
    public void Turning_the_slot_off_closes_its_period_as_stopped()
    {
        using var rig = new Rig(0);
        rig.Choose("fast");
        rig.Pump(50);
        rig.Pipeline.RnnEnabled = false;
        var t = Stopwatch.StartNew();
        while (t.ElapsedMilliseconds < 1500) { rig.Pump(); Thread.Sleep(5); }
        var stopped = rig.Await(r => r.Reason == SlotTallyReason.Stopped && r.EngineId == "fast").Single();
        _out.WriteLine(stopped.Describe());
        Assert.True(stopped.Buffers >= 50);
        Assert.Equal("", rig.Pipeline.TallyFor(DenoiseFamily.Neural).EngineId);
    }

    [Fact]
    public void Slow_buffers_inside_the_rules_grace_are_not_counted_as_judged_or_late()
    {
        // Codex's probe: the rule restarts (as after a gap), twenty slow
        // buffers follow inside its grace, and the rule judges none of them.
        // Until repair 1 the tally said 21 judged and 20 late.
        using var rig = new Rig(0);
        rig.Choose("switchable");
        rig.Pump(100);                                   // past the grace, all fast
        var rule = rig.Pipeline.KeepUpRuleFor(DenoiseFamily.Neural);
        rule.Restart("switchable");
        rig.SlowNow = true;
        rig.Pump(20);
        rig.SlowNow = false;
        Assert.Equal(0, rule.Filled);
        Assert.Equal(0, rule.LateInWindow);
        rig.Pipeline.FeedEnded();
        var ended = rig.Await(r => r.Reason == SlotTallyReason.FeedEnded && r.EngineId == "switchable").Single();
        _out.WriteLine(ended.Describe());
        Assert.Equal(0, ended.Late);
        Assert.True(ended.Buffers >= 120);
    }

    [Fact]
    public void Slow_buffers_after_the_grace_are_counted_exactly_as_the_rule_judged_them()
    {
        // The positive control for the test above, over the same buffers.
        using var rig = new Rig(0);
        rig.Choose("switchable");
        rig.Pump(100);
        var rule = rig.Pipeline.KeepUpRuleFor(DenoiseFamily.Neural);
        rig.SlowNow = true;
        rig.Pump(20);
        rig.SlowNow = false;
        Assert.Equal(20, rule.LateInWindow);
        rig.Pipeline.FeedEnded();
        var ended = rig.Await(r => r.Reason == SlotTallyReason.FeedEnded && r.EngineId == "switchable").Single();
        _out.WriteLine(ended.Describe());
        Assert.Equal(20, ended.Late);
    }

    [Fact]
    public void A_helper_batch_that_crosses_the_end_of_the_grace_is_judged_only_in_part_as_the_rule_does()
    {
        using var rig = new Rig(0);
        rig.Choose("fakehelper");
        rig.Pump(100);                                   // past the grace, fast
        var rule = rig.Pipeline.KeepUpRuleFor(DenoiseFamily.Neural);
        rule.Restart("fakehelper");                      // fifty frames of grace again
        int calls = 0;
        rig.Pipeline.HelperService = (_, _, _) => Interlocked.Increment(ref calls) == 1
            ? new HelperServiceReading(12_000_000, 70)  // one batch of 70 slow frames: 50 grace, 20 judged
            : HelperServiceReading.NoNewWork;
        rig.Pump(1);
        Assert.Equal(20, rule.Filled);
        Assert.Equal(20, rule.LateInWindow);
        rig.Pipeline.FeedEnded();
        var ended = rig.Await(r => r.Reason == SlotTallyReason.FeedEnded && r.EngineId == "fakehelper").Single();
        _out.WriteLine(ended.Describe());
        Assert.Equal(20, ended.Late);
    }

    [Fact]
    public void Process_allocates_nothing_while_the_tally_closes_periods()
    {
        using var rig = new Rig(Stopwatch.Frequency / 1000); // a period every millisecond
        rig.Choose("fastspectral");
        rig.Choose("fakehelper");
        rig.Pump(200);
        var audio = new float[960];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            Array.Fill(audio, 0.1f); rig.Pipeline.Process(audio);
            long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2000;
            while (Stopwatch.GetTimestamp() < until) { }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var lines = rig.Await(r => r.Reason == SlotTallyReason.Minute, 20);
        _out.WriteLine($"{allocated} bytes on the audio thread over 300 buffers; {lines.Length} tally lines written off it");
        Assert.True(lines.Length >= 20, "the periods were not closed, so this proves nothing");
        Assert.Equal(0, allocated);
    }
}
