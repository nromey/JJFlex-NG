using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JJPortaudio;
using JJTrace;
using Radios;
using Radios.Tests;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track RT (#1232): inside <see cref="RxAudioPipeline.Process"/> and what
/// it calls, nothing traces, takes a lock another thread can hold, allocates, or
/// queues work. The noise slots, the engine banks and the keep-up raiser only
/// record; the off-audio writer (here a manual one, or a private thread, never the
/// shared one when a test blocks it) writes, raises and disposes. Windowless: every
/// test asserts it, and nothing here makes a sound.
/// </summary>
public sealed class ReceiveOffAudioWriterTests
{
    private const int F = 480;
    private readonly ITestOutputHelper _out;
    public ReceiveOffAudioWriterTests(ITestOutputHelper output) { NoiseReductionPerChannelTests.AssertWindowless(); _out = output; }

    /// <summary>A pass-through engine that says which thread disposed it.</summary>
    private sealed class DisposingEngine : IDenoiseEngine
    {
        public DisposingEngine(string id) { Id = id; }
        public string Id { get; }
        public int SampleRate => 48000;
        public int FrameLength => F;
        public bool IsAvailable => true;
        public int LatencySamples => 0;
        public int DisposedOn;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output) { input.CopyTo(output); return true; }
        public void Reset() { }
        public void Dispose() { if (DisposedOn == 0) DisposedOn = Environment.CurrentManagedThreadId; }
    }

    private sealed class Engines
    {
        public readonly ConcurrentQueue<DisposingEngine> Made = new();
        public IDenoiseEngine Make(string id) { var e = new DisposingEngine(id); Made.Enqueue(e); return e; }
        public DisposingEngine[] Of(string id) => Made.Where(e => e.Id == id).ToArray();
    }

    private static DenoiseEngineEntry Row(Engines engines, string id, DenoiseDirection direction) =>
        new(id, DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { direction }, (_, _) => engines.Make(id), () => Unavailability.None);

    private static void Until(Func<bool> condition, string what)
    {
        var t = Stopwatch.StartNew();
        while (!condition() && t.ElapsedMilliseconds < 5000) Thread.Sleep(1);
        Assert.True(condition(), what);
    }

    /// <summary>Tick the writer on another thread, as the real writer would be; return that thread.</summary>
    private static int TickElsewhere(ManualOffAudioScheduler writer)
    {
        int thread = 0;
        Assert.True(Task.Run(() => { thread = Environment.CurrentManagedThreadId; writer.Tick(); }).Wait(10000), "the writer did not finish");
        return thread;
    }

    // ── The final record ──

    /// <summary>
    /// A line recorded by the LAST Process call, with no call after it, is written by
    /// the writer's next period (Codex's re-check 3 of Track P2: the slot asked for its
    /// drain only at the start of Process, so this line waited for a buffer that might
    /// never come, or for the slot's disposal).
    /// </summary>
    [Fact]
    public void A_line_recorded_by_the_last_buffer_is_written_with_no_further_buffer()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        DenoiseEngineEntry Row480() => new("rnnoise", DenoiseSide.Pc, DenoiseFamily.Neural, "label", new[] { DenoiseDirection.Receive },
            (_, _) => new AlignedSwitchTests.DelayGainEngine("rnnoise", 480, 1), () => Unavailability.None);
        var writer = new ManualOffAudioScheduler();
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "rnnoise", 48000, 2, new DenoiseEngineCatalog(new[] { Row480() }), scheduler: writer);
        var lines = new ConcurrentQueue<string>();
        try
        {
            slot.TraceObserverForTest = lines.Enqueue;
            slot.CrossFadeOverrideForTest = true; slot.ShifterOverrideForTest = true; slot.MuteOverrideForTest = false;
            slot.SetStrength("rnnoise", 1);
            slot.Enabled = true;
            var x = new float[2 * F];
            var rng = new Random(11);
            void Next() { for (int i = 0; i < F; i++) x[2 * i] = x[2 * i + 1] = (float)(rng.NextDouble() - 0.5) * 0.2f; slot.Process(x, true, true, false, null, RxContentClass.Classified | RxContentClass.NotVoiceRun); }
            for (int b = 0; b < 40; b++) Next();
            writer.Tick();
            slot.Enabled = false;
            Next();                                   // the stream's last buffer: it records the turn-off
            Assert.True(slot.TracePendingForTest, "the last buffer recorded nothing, so this proves nothing");
            Assert.Empty(lines);
            TickElsewhere(writer);                    // the writer's period; no Process after the last
            Assert.Single(lines);
            Assert.Contains("turned off", lines.Single());
            Assert.False(slot.TracePendingForTest);
        }
        finally { slot.CloseAdmission(); slot.DisposeOwned(); }
    }

    // ── Nothing queued to the pool from Process, with the writer blocked ──

    /// <summary>
    /// Codex's re-check 2 of Track P2 reproduced 1,280 bytes allocated on the audio path
    /// when a blocked writer let pool items pile up. With the writer blocked, 128 lines
    /// each followed by a Process call queue nothing to the pool from this thread and
    /// allocate nothing; the writer's thread is not the pool and is not counted.
    /// </summary>
    [Fact]
    public void With_the_writer_blocked_128_buffers_that_record_queue_nothing_to_the_pool()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        using var writer = new OffAudioDrainer(5, "rt slot pool test writer");
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "", 48000, 2, new DenoiseEngineCatalog(Array.Empty<DenoiseEngineEntry>()), scheduler: writer);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        try
        {
            slot.TraceObserverForTest = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(20)); };
            var audio = new float[2 * F];
            for (int i = 0; i < 8; i++) slot.Process(audio, true, true, false, null);
            slot.TraceForTest(0);
            slot.Process(audio, true, true, false, null);
            Assert.True(entered.Wait(5000), "the writer never started");
            using var watch = new PoolQueueWatch();
            Assert.True(watch.Probe(), "the watch sees nothing, so its zero proves nothing");
            watch.Reset();
            var bytes = new long[128];
            for (int i = 0; i < 128; i++)
            {
                slot.TraceForTest(i + 1);
                long before = GC.GetAllocatedBytesForCurrentThread();
                slot.Process(audio, true, true, false, null);
                bytes[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            _out.WriteLine($"pool items queued from this thread: {watch.Count}; drains {slot.TraceWorkForTest.Drains}");
            Assert.Equal(0, watch.Count);
            Assert.All(bytes, b => Assert.Equal(0, b));
            Assert.Equal(1, slot.TraceWorkForTest.Drains);
        }
        finally { slot.TraceObserverForTest = null; release.Set(); slot.CloseAdmission(); slot.DisposeOwned(); }
    }

    // ── Switch-time work: results and retirements (item 3) ──

    /// <summary>
    /// A receive engine switch: from the build's delivery through the warm-up to the
    /// switch, no Process call allocates or queues pool work (Publish and Retire each
    /// queued a capturing lambda to the pool from here). The results arrive in order,
    /// raised by the writer; the retired bank is disposed by the writer, never on the
    /// audio thread.
    /// </summary>
    [Fact]
    public void A_receive_switch_allocates_nothing_on_the_audio_path_and_its_result_and_retirement_come_from_the_writer()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var engines = new Engines();
        var writer = new ManualOffAudioScheduler();
        var catalog = new DenoiseEngineCatalog(new[] { Row(engines, "a", DenoiseDirection.Receive), Row(engines, "b", DenoiseDirection.Receive) });
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "a", 48000, 2, catalog, scheduler: writer);
        var results = new ConcurrentQueue<(DenoiseSelectionResult Result, int Thread)>();
        slot.SelectionResult += (_, r) => results.Enqueue((r, Environment.CurrentManagedThreadId));
        try
        {
            slot.CrossFadeOverrideForTest = false;     // a cut on a buffer boundary: phase 1's switch
            slot.Enabled = true;
            var audio = new float[2 * F];
            for (int i = 0; i < 4; i++) slot.Process(audio, true, true, false, null);
            Assert.Empty(results);                     // "a" is announced by the writer, not by the buffer
            int first = TickElsewhere(writer);
            Assert.Equal(new[] { "a" }, results.Select(r => r.Result.EngineId));

            Assert.True(slot.Select("b", out var refusal), refusal);
            Until(() => slot.BuildDelivered("b"), "b was never built");
            using var watch = new PoolQueueWatch();
            Assert.True(watch.Probe(), "the watch sees nothing, so its zero proves nothing");
            watch.Reset();
            var hits = new List<string>();
            for (int i = 0; i < 60 && slot.RunningEngineId != "b"; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                slot.Process(audio, true, true, false, null);
                long d = GC.GetAllocatedBytesForCurrentThread() - before;
                if (d != 0) hits.Add($"buffer {i}: {d} bytes");
            }
            Assert.Equal("b", slot.RunningEngineId);
            _out.WriteLine(hits.Count == 0 ? "nothing allocated" : string.Join("; ", hits));
            Assert.Empty(hits);
            Assert.Equal(0, watch.Count);
            var a = engines.Of("a");
            Assert.All(a, e => Assert.Equal(0, e.DisposedOn));            // retired, not disposed, by the audio thread
            Assert.Single(results);                                       // nothing raised from the buffer

            int writerThread = TickElsewhere(writer);
            Assert.Equal(new[] { "a", "b" }, results.Select(r => r.Result.EngineId));
            Assert.True(results.Last().Result.Processing);
            Assert.Equal(writerThread, results.Last().Thread);
            Assert.NotEqual(Environment.CurrentManagedThreadId, writerThread);
            Assert.All(a, e => Assert.Equal(writerThread, e.DisposedOn)); // disposed by the writer
        }
        finally { slot.CloseAdmission(); slot.DisposeOwned(); }
    }

    /// <summary>The same on the transmit side: Publish and Retire are shared, so a transmit swap records and hands off exactly as a receive one.</summary>
    [Fact]
    public void A_transmit_swap_allocates_nothing_on_the_audio_path_and_its_result_and_retirement_come_from_the_writer()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var engines = new Engines();
        var writer = new ManualOffAudioScheduler();
        var catalog = new DenoiseEngineCatalog(new[] { Row(engines, "ta", DenoiseDirection.Transmit), Row(engines, "tb", DenoiseDirection.Transmit) });
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "ta", 48000, 2, catalog, DenoiseDirection.Transmit, scheduler: writer);
        var results = new ConcurrentQueue<(DenoiseSelectionResult Result, int Thread)>();
        slot.SelectionResult += (_, r) => results.Enqueue((r, Environment.CurrentManagedThreadId));
        try
        {
            slot.Enabled = true;
            var audio = new float[2 * F];
            for (int i = 0; i < 4; i++) slot.Process(audio, true, true, false, null);
            TickElsewhere(writer);
            Assert.Equal(new[] { "ta" }, results.Select(r => r.Result.EngineId));
            Assert.True(slot.Select("tb", out var refusal), refusal);
            Until(() => slot.BuildDelivered("tb"), "tb was never built");
            using var watch = new PoolQueueWatch();
            Assert.True(watch.Probe());
            watch.Reset();
            long before = GC.GetAllocatedBytesForCurrentThread();
            slot.Process(audio, true, true, false, null);             // the swap, unkeyed
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal("tb", slot.RunningEngineId);
            Assert.Equal(0, bytes);
            Assert.Equal(0, watch.Count);
            Assert.Single(results);
            Assert.All(engines.Of("ta"), e => Assert.Equal(0, e.DisposedOn));
            int writerThread = TickElsewhere(writer);
            Assert.Equal(new[] { "ta", "tb" }, results.Select(r => r.Result.EngineId));
            Assert.Equal(writerThread, results.Last().Thread);
            Assert.All(engines.Of("ta"), e => Assert.Equal(writerThread, e.DisposedOn));
        }
        finally { slot.CloseAdmission(); slot.DisposeOwned(); }
    }

    // ── The keep-up raiser (item 4) ──

    private static DenoiseEngineEntry KeepUpRow(string id, DenoiseFamily family) =>
        new(id, DenoiseSide.Pc, family, "audio.denoise." + id, new[] { DenoiseDirection.Receive },
            (_, _) => new EngineKeepUpPipelineTests.Engine(id), () => Unavailability.None);

    /// <summary>
    /// The pipeline's Process publishes keep-up changes and tallies and only sets the
    /// raiser's flag: it allocates nothing and queues nothing (Track L queued the
    /// raiser to the pool from inside a buffer). The writer raises them; a tally closed
    /// by the LAST Process and the session totals queued at FeedEnded arrive with no
    /// further buffer (Codex's contract from re-check 2).
    /// </summary>
    [Fact]
    public void The_keep_up_raiser_is_signalled_not_queued_and_the_last_buffer_s_and_FeedEnded_s_lines_still_arrive()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var writer = new ManualOffAudioScheduler();
        var rows = new[] { KeepUpRow("rnnoise", DenoiseFamily.Neural), KeepUpRow("fast", DenoiseFamily.Neural), KeepUpRow("spectral_capture", DenoiseFamily.Spectral) };
        using var p = new RxAudioPipeline(48000, 2, new DenoiseEngineCatalog(rows), Stopwatch.Frequency / 50, writer);
        var tallies = new ConcurrentQueue<(SlotTallyReport Report, int Thread)>();
        var keepUps = new ConcurrentQueue<(EngineKeepUpResult Result, int Thread)>();
        p.TallyWritten += (_, r) => tallies.Enqueue((r, Environment.CurrentManagedThreadId));
        p.KeepUpResult += (_, r) => keepUps.Enqueue((r, Environment.CurrentManagedThreadId));
        var audio = new float[960];
        void Pump() { Array.Fill(audio, 0.1f); p.Process(audio); }
        Assert.True(p.SelectPcEngine("fast", out var refusal), refusal);
        p.RnnEnabled = true;
        var t = Stopwatch.StartNew();
        while (p.NeuralSlot.ProcessingEngineId != "fast" && t.ElapsedMilliseconds < 5000) { Pump(); Thread.Sleep(1); }
        Assert.Equal("fast", p.NeuralSlot.ProcessingEngineId);
        TickElsewhere(writer);
        while (tallies.TryDequeue(out _)) { }
        while (keepUps.TryDequeue(out _)) { }

        using var watch = new PoolQueueWatch();
        Assert.True(watch.Probe(), "the watch sees nothing, so its zero proves nothing");
        watch.Reset();
        var hits = new List<string>();
        int pumped = 0;
        // Until the rule's window fills clean after its grace: the buffer that fills it
        // publishes KeepingUp, and is the last one this test gives the pipeline.
        while (pumped < 600 && p.KeepUpRuleFor(DenoiseFamily.Neural).Filled < EngineKeepUpRule.DefaultWindow)
        {
            Array.Fill(audio, 0.1f);
            long before = GC.GetAllocatedBytesForCurrentThread();
            p.Process(audio);
            long d = GC.GetAllocatedBytesForCurrentThread() - before;
            if (d != 0) hits.Add($"buffer {pumped}: {d} bytes");
            pumped++;
            Thread.Sleep(1);                           // let the shortened period pass in real time
        }
        Assert.True(p.KeepUpWorkForTest.Pending, "nothing was published, so this proves nothing");
        _out.WriteLine($"{pumped} buffers; {(hits.Count == 0 ? "nothing allocated" : string.Join("; ", hits))}; pool items {watch.Count}");
        Assert.Empty(hits);
        Assert.Equal(0, watch.Count);
        Assert.Empty(tallies);                         // nothing raised from the buffer
        Assert.Empty(keepUps);

        // The last buffer was the one above: no further Process. The writer raises what it published.
        int writerThread = TickElsewhere(writer);
        Assert.NotEmpty(tallies);
        Assert.Contains(keepUps, k => k.Result.EngineId == "fast" && k.Result.Change == EngineKeepUpChange.KeepingUp);
        Assert.All(tallies, x => Assert.Equal(writerThread, x.Thread));
        Assert.All(keepUps, x => Assert.Equal(writerThread, x.Thread));

        // PC audio off: the session totals, with no buffer after.
        while (tallies.TryDequeue(out _)) { }
        p.FeedEnded();
        Assert.Empty(tallies);
        TickElsewhere(writer);
        Assert.Contains(tallies, x => x.Report.Reason == SlotTallyReason.Session);
    }

    // ── No ring is a large object ──

    [Fact]
    public void Every_ring_on_the_receive_processing_side_is_built_and_none_is_a_large_object()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        // The ring refuses a capacity that would make it a large object (positive
        // control in Radios.Tests' OffAudioWriterTests), so a ring that was built is
        // under the threshold. These are the ones this track moved or made.
        var engines = new Engines();
        var writer = new ManualOffAudioScheduler();
        var slot = new PcDenoiseSlot(DenoiseFamily.Neural, "a", 48000, 2,
            new DenoiseEngineCatalog(new[] { Row(engines, "a", DenoiseDirection.Receive) }), scheduler: writer);
        try
        {
            Assert.Equal(PcDenoiseSlot.TraceCapacity, ((Array)slot.TraceStorageForTest).Length);
            Assert.Equal(PcDenoiseSlot.PublishCapacity, ((Array)slot.PublishStorageForTest).Length);
            var tally = new SlotTally(DenoiseFamily.Neural, Stopwatch.Frequency);
            Assert.IsAssignableFrom<Array>(tally.StorageForTest);
            using var bank = new PerChannelDenoiserBank(() => new DisposingEngine("x"), 2, writer);
            Assert.True(writer.IsRegistered(bank.LinesForTest));
            Assert.True(writer.IsRegistered(slot.TraceWorkForTest) && writer.IsRegistered(slot.EventWorkForTest));
        }
        finally { slot.CloseAdmission(); slot.DisposeOwned(); }
        Assert.False(writer.IsRegistered(slot.TraceWorkForTest) || writer.IsRegistered(slot.EventWorkForTest));
    }

    // ── The bank's own lines (item 7) ──

    private sealed class FailingEngine : IDenoiseEngine
    {
        public string Id => "breaks";
        public int SampleRate => 48000;
        public int FrameLength => F;
        public bool IsAvailable => true;
        public int LatencySamples => 0;
        public bool ProcessFrame(ReadOnlySpan<float> input, Span<float> output) => throw new InvalidOperationException("engine fault");
        public void Reset() { }
        public void Dispose() { }
    }

    /// <summary>A channel's first failure was traced from inside Process, with the message built there. Now it is recorded, and written off the audio thread in the same words.</summary>
    [Fact]
    public void A_bank_s_failure_line_is_recorded_in_Process_and_written_by_the_writer()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var writer = new ManualOffAudioScheduler();
        using var bank = new PerChannelDenoiserBank(() => new FailingEngine(), 2, writer);
        var lines = new ConcurrentQueue<(string Line, int Thread)>();
        var listener = new LineListener("PerChannelDenoiserBank: breaks", lines);
        Trace.Listeners.Add(listener);
        try
        {
            var audio = new float[2 * F];
            bank.Process(audio, 1f);                   // the JIT's first pass and the first failure
            Assert.Empty(lines);
            Assert.True(bank.LinesForTest.Pending, "nothing was recorded");
            int writerThread = TickElsewhere(writer);
            var got = lines.ToArray();
            foreach (var l in got) _out.WriteLine(l.Line);
            Assert.Equal(2, got.Length);               // once per channel, as before
            Assert.All(got, l => Assert.Contains("failed (InvalidOperationException: engine fault); passing that channel through unprocessed until it recovers.", l.Line));
            Assert.All(got, l => Assert.Equal(writerThread, l.Thread));
            bank.Process(audio, 1f);                   // still failing: counted, nothing more recorded
            Assert.False(bank.LinesForTest.Pending);
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    private sealed class LineListener : TraceListener
    {
        private readonly string _match;
        private readonly ConcurrentQueue<(string, int)> _lines;
        public LineListener(string match, ConcurrentQueue<(string, int)> lines) { _match = match; _lines = lines; }
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message != null && message.Contains(_match)) _lines.Enqueue((message, Environment.CurrentManagedThreadId));
        }
    }
}
