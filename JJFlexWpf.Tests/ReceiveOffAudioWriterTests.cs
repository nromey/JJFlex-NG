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

    // ── Repair 1, B1: the captured-noise engine allocates nothing per FFT frame ──

    /// <summary>
    /// The subtraction as it stood at af0fc643, transcribed for one mono channel: the
    /// reference the repaired provider must equal sample for sample. Its one difference
    /// from the repair is exactly the thing repaired: FFT.Forward(double[]), which
    /// returns a new complex array every frame.
    /// </summary>
    private sealed class OldSubtraction
    {
        private const int N = 1024, Hop = 512, Ring = N * 4;
        private readonly double[] _window = new FftSharp.Windows.Hanning().Create(N);
        private readonly double[] _fft = new double[N];
        private readonly float[] _accum = new float[N];
        private readonly float[] _out = new float[Ring];
        private int _count, _read, _write;
        public double[]? Profile;
        public float Strength = 0.7f, Floor = 0.02f;

        public void Process(float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                _accum[_count++] = buffer[i];
                if (_count >= N)
                {
                    Frame();
                    Array.Copy(_accum, Hop, _accum, 0, N - Hop);
                    _count = N - Hop;
                }
            }
            for (int i = 0; i < buffer.Length; i++)
                if (_read != _write) { buffer[i] = _out[_read]; _read = (_read + 1) % Ring; }
        }

        private void Frame()
        {
            var profile = Profile!;
            for (int i = 0; i < N; i++) _fft[i] = _accum[i] * _window[i];
            System.Numerics.Complex[] spectrum = FftSharp.FFT.Forward(_fft);
            for (int i = 0; i < spectrum.Length && i < profile.Length; i++)
            {
                double mag = spectrum[i].Magnitude, phase = spectrum[i].Phase;
                double clean = mag - profile[i] * Strength;
                double floor = mag * Floor;
                if (clean < floor) clean = floor;
                spectrum[i] = System.Numerics.Complex.FromPolarCoordinates(clean, phase);
            }
            FftSharp.FFT.Inverse(spectrum);
            for (int i = 0; i < N && i < spectrum.Length; i++) _fft[i] = spectrum[i].Real;
            for (int i = 0; i < N; i++) { int pos = (_write + i) % Ring; _out[pos] += (float)(_fft[i] * _window[i]); }
            for (int i = 0; i < Hop; i++) _out[(_write + N + i) % Ring] = 0;
            _write = (_write + Hop) % Ring;
        }
    }

    private static double[] CapturedProfile(int seed)
    {
        var capture = new SpectralSubtractionProvider(48000, 1) { LineScheduler = new ManualOffAudioScheduler() };
        capture.StartSampling(1);
        var rng = new Random(seed);
        var noise = new float[F];
        while (capture.IsSampling)
        {
            for (int i = 0; i < F; i++) noise[i] = (float)((rng.NextDouble() - 0.5) * 0.1);
            capture.ProcessInPlace(noise, 0, F, 1);
        }
        return capture.GetChannelProfile(0)!;
    }

    /// <summary>The repaired subtraction is the old one, bit for bit, on a fixed input with a real captured profile.</summary>
    [Fact]
    public void The_repaired_subtraction_is_bit_for_bit_the_old_one()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var profile = CapturedProfile(21);
        var provider = new SpectralSubtractionProvider(48000, 1) { Enabled = true };
        provider.SetChannelProfile(0, profile);
        var reference = new OldSubtraction { Profile = profile };
        var rng = new Random(22);
        long compared = 0, differing = 0;
        var a = new float[F]; var b = new float[F];
        for (int block = 0; block < 400; block++)
        {
            for (int i = 0; i < F; i++)
            {
                double t = (block * F + i) / 48000.0;
                a[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 700 * t) + (rng.NextDouble() - 0.5) * 0.1);
            }
            Array.Copy(a, b, F);
            provider.ProcessInPlace(a, 0, F, 1);
            reference.Process(b);
            for (int i = 0; i < F; i++) { compared++; if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) differing++; }
        }
        _out.WriteLine($"{compared} samples compared, {differing} differ");
        Assert.True(compared > 100000);
        Assert.Equal(0, differing);
    }

    /// <summary>
    /// The engine the catalog really selects for captured noise ("spectral_capture"),
    /// running in the real pipeline with a real captured profile, allocates nothing per
    /// Process call. Counted inside a no-collection region, so a background collection
    /// started elsewhere cannot add bytes this thread never allocated. Before repair 1
    /// every FFT frame allocated a 16 KB complex array here.
    /// </summary>
    [Fact]
    public void The_captured_noise_engine_with_a_real_profile_allocates_nothing_per_buffer_in_the_real_pipeline()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var writer = new ManualOffAudioScheduler();
        using var p = new RxAudioPipeline(48000, 2, null, 0, writer);
        var audio = new float[2 * F];
        var rng = new Random(31);
        void Fill() { for (int i = 0; i < F; i++) { float n = (float)((rng.NextDouble() - 0.5) * 0.1); audio[2 * i] = n; audio[2 * i + 1] = n * 0.8f; } }
        // A real capture through the pipeline, as the operator takes one.
        p.StartNoiseSampling(1);
        for (int b = 0; b < 400 && p.IsNoiseSampling; b++) { Fill(); p.Process(audio); }
        Assert.False(p.IsNoiseSampling, "the capture never finished");
        Assert.True(p.HasNoiseProfile);
        Assert.Equal("spectral_capture", p.SpectralSlot.ChosenEngineId);
        p.SpectralEnabled = true;
        var t = Stopwatch.StartNew();
        while (p.SpectralSlot.ProcessingEngineId != "spectral_capture" && t.ElapsedMilliseconds < 5000) { Fill(); p.Process(audio); Thread.Sleep(1); }
        Assert.Equal("spectral_capture", p.SpectralSlot.ProcessingEngineId);
        for (int b = 0; b < 200; b++) { Fill(); p.Process(audio); }      // past the turn-on, the delay adoption and the JIT
        writer.Tick();

        long[]? bytes = null;
        for (int attempt = 0; attempt < 3 && bytes == null; attempt++)
        {
            bool region = false;
            for (long size = 16L * 1024 * 1024; size >= 1024 * 1024 && !region; size /= 2)
            {
                try { region = GC.TryStartNoGCRegion(size); break; }
                catch (ArgumentOutOfRangeException) { }
            }
            var counted = new long[300];
            for (int b = 0; b < counted.Length; b++)
            {
                Fill();
                long before = GC.GetAllocatedBytesForCurrentThread();
                p.Process(audio);
                counted[b] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            bool held = System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion;
            if (held) GC.EndNoGCRegion();
            if (region && held) bytes = counted;
        }
        Assert.True(bytes != null, "no window was held free of collections in three tries");
        Assert.Equal("spectral_capture", p.SpectralSlot.ProcessingEngineId);
        var hits = bytes!.Select((v, i) => (v, i)).Where(x => x.v != 0).Select(x => $"buffer {x.i}: {x.v} bytes").ToArray();
        _out.WriteLine(hits.Length == 0 ? "300 buffers with the captured-noise engine running: nothing allocated" : string.Join("; ", hits));
        Assert.Empty(hits);
    }

    // ── Repair 1, B2: the capture's end line is written off the receive thread ──

    /// <summary>Blocks every line carrying its marker until released (three seconds at most), recording the thread.</summary>
    private sealed class GatedLines : TraceListener
    {
        private readonly string _match;
        public readonly ManualResetEventSlim Release = new(false);
        public readonly ConcurrentQueue<(string Line, int Thread)> Got = new();
        public GatedLines(string match) { _match = match; }
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message == null || !message.Contains(_match)) return;
            Got.Enqueue((message, Environment.CurrentManagedThreadId));
            Release.Wait(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// A noise capture ends inside Process, on the receive thread, and wrote its
    /// "noise profile captured" line there. With a listener that blocks, the buffer
    /// that ends the capture returns at once; the writer then writes the same line on
    /// another thread, and stands its work down.
    /// </summary>
    [Fact]
    public void A_capture_s_end_line_is_written_by_the_writer_not_by_the_buffer_that_ends_it()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var writer = new ManualOffAudioScheduler();
        // Three channels and two seconds: a line no other test writes (186 frames, 3 channel(s)).
        var provider = new SpectralSubtractionProvider(48000, 3) { LineScheduler = writer };
        const string expected = "SpectralSubtraction: noise profile captured (186 frames, 3 channel(s))";
        var observed = new ConcurrentQueue<(string Line, int Thread)>();
        provider.LineObserverForTest = l => observed.Enqueue((l, Environment.CurrentManagedThreadId));
        var gate = new GatedLines("(186 frames, 3 channel(s))");
        Trace.Listeners.Add(gate);
        try
        {
            provider.StartSampling(2);
            Assert.True(writer.IsRegistered(provider.CaptureLineForTest!));
            var buffer = new float[3 * F];
            var rng = new Random(41);
            int me = Environment.CurrentManagedThreadId;
            long slowest = 0;
            while (provider.IsSampling)
            {
                for (int i = 0; i < buffer.Length; i++) buffer[i] = (float)((rng.NextDouble() - 0.5) * 0.1);
                long t0 = Stopwatch.GetTimestamp();
                provider.ProcessInPlace(buffer, 0, buffer.Length, 3);
                slowest = Math.Max(slowest, Stopwatch.GetTimestamp() - t0);
            }
            double slowestMs = slowest * 1000.0 / Stopwatch.Frequency;
            _out.WriteLine($"slowest buffer {slowestMs:F2} ms");
            Assert.True(slowestMs < 500, "the buffer that ended the capture waited for the listener");
            Assert.Empty(gate.Got);
            Assert.Empty(observed);
            Assert.True(provider.CaptureLineForTest!.Pending, "the capture's end was not recorded");

            int writerThread = 0;
            var tick = Task.Run(() => { writerThread = Environment.CurrentManagedThreadId; writer.Tick(); });
            var wait = Stopwatch.StartNew();
            while (gate.Got.IsEmpty && wait.ElapsedMilliseconds < 5000) Thread.Sleep(2);
            Assert.True(gate.Got.TryPeek(out var got), "the writer never wrote the line");
            Assert.EndsWith(expected, got.Line);
            Assert.NotEqual(me, got.Thread);
            gate.Release.Set();
            Assert.True(tick.Wait(10000));
            Assert.Equal(new[] { expected }, observed.Select(o => o.Line));
            Assert.Equal(writerThread, observed.Single().Thread);
            Assert.False(writer.IsRegistered(provider.CaptureLineForTest!), "the work was not stood down after the capture");
        }
        finally { gate.Release.Set(); Trace.Listeners.Remove(gate); }
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
