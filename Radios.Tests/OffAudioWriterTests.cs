using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JJPortaudio;
using JJTrace;
using POpusCodec;
using POpusCodec.Enums;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track RT (#1232): the shared helper itself, the record ring and the
    /// off-audio writer, and the two receive-side owners that live in JJPortaudio
    /// apart from the callback (the buffer queue and the receive chain). No window,
    /// no device, no sound.
    /// </summary>
    public sealed class OffAudioWriterTests
    {
        private readonly ITestOutputHelper _out;
        public OffAudioWriterTests(ITestOutputHelper output) { _out = output; }

        private struct Rec { public long A, B; public int C; }

        // ── The record ring ──

        [Fact]
        public void A_full_ring_drops_the_new_record_counts_it_and_never_overwrites()
        {
            var ring = new RecordRing<Rec>(4);
            for (int i = 0; i < 6; i++) ring.TryRecord(new Rec { A = i });
            Assert.True(ring.Pending);
            Assert.True(ring.TakePending());
            Assert.Equal(2, ring.TakeLost());
            var got = new List<long>();
            while (ring.TryTake(out var r)) got.Add(r.A);
            Assert.Equal(new long[] { 0, 1, 2, 3 }, got);
            Assert.False(ring.Pending);
            Assert.True(ring.TryRecord(new Rec { A = 9 }));     // room again once read
            Assert.True(ring.Pending);
        }

        [Fact]
        public void Recording_allocates_nothing_and_takes_no_lock_a_reader_holds()
        {
            var ring = new RecordRing<Rec>(64);
            ring.TryRecord(new Rec());                           // the JIT's first pass
            ring.TryTake(out _);
            using var held = new ManualResetEventSlim(false);
            using var done = new ManualResetEventSlim(false);
            var reader = new Thread(() => { lock (ring) { held.Set(); done.Wait(5000); } }) { IsBackground = true };
            reader.Start();
            try
            {
                Assert.True(held.Wait(5000));
                var sw = new Stopwatch();
                long before = GC.GetAllocatedBytesForCurrentThread();
                sw.Start();
                for (int i = 0; i < 200; i++) ring.TryRecord(new Rec { A = i, B = i, C = i });
                sw.Stop();
                Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
                Assert.True(sw.ElapsedMilliseconds < 50);
                Assert.Equal(136, ring.TakeLost());
            }
            finally { done.Set(); reader.Join(5000); }
        }

        /// <summary>
        /// No ring can be a large object: a large-object allocation can start a background
        /// collection, which makes every zero-allocation test in the process report bytes it
        /// never allocated (memory project_large_arrays_skew_allocation_tests). The ring
        /// refuses such a capacity; every ring this track made is under the threshold.
        /// </summary>
        [Fact]
        public void No_record_ring_is_a_large_object()
        {
            long threshold = RecordRing<Rec>.LargeObjectThreshold;
            // Positive control: a capacity whose array would be large is refused, and an array that size IS a large object.
            int tooMany = (int)(threshold / 20) + 1;
            Assert.Throws<ArgumentOutOfRangeException>(() => new RecordRing<Rec>(tooMany));
            Assert.Equal(2, GC.GetGeneration(new Rec[tooMany]));

            // So every ring that exists is under it by construction: the owners below
            // build theirs in their constructors, and would throw here if one were not.
            var stream = new AudioCallbackOffAudioTests.Stream();
            try
            {
                var chain = new RxOpusChain(_ => { }, () => 48000, scheduler: new ManualOffAudioScheduler());
                Assert.IsAssignableFrom<Array>(stream.Cb.Lines.StorageForTest);
                Assert.IsAssignableFrom<Array>(stream.Cb.Playback.LineStorageForTest);
                Assert.Equal(RxPlaybackQueue.LineCapacity, ((Array)stream.Cb.Playback.LineStorageForTest).Length);
                Assert.True(RecordRing<Rec>.ArrayBytes(64) < threshold);
                _out.WriteLine($"threshold {threshold} bytes; a 64-record ring of 20-byte records is {RecordRing<Rec>.ArrayBytes(64)}");
            }
            finally { stream.Dispose(); }
        }

        // ── The drain's discipline ──

        /// <summary>A work whose first pass records one more item, as an audio thread would while it drains.</summary>
        private sealed class CountingWork : OffAudioWork
        {
            public readonly RecordRing<Rec> Ring = new(16);
            public readonly ConcurrentQueue<long> Written = new();
            public Action? DuringFirstPass;
            private int _passes;
            public int Passes => _passes;
            public override bool Pending => base.Pending || Ring.Pending;
            protected override void DrainCore()
            {
                Ring.TakePending();
                for (int n = Ring.Count; n > 0 && Ring.TryTake(out var r); n--) Written.Enqueue(r.A);
                if (Interlocked.Increment(ref _passes) == 1) DuringFirstPass?.Invoke();
            }
        }

        [Fact]
        public void A_record_made_while_a_drain_runs_is_written_by_that_drain_with_no_further_tick()
        {
            var work = new CountingWork();
            work.DuringFirstPass = () => work.Ring.TryRecord(new Rec { A = 2 });
            var writer = new ManualOffAudioScheduler();
            writer.Register(work);
            work.Ring.TryRecord(new Rec { A = 1 });
            Assert.Equal(1, writer.Tick());
            Assert.Equal(new long[] { 1, 2 }, work.Written.ToArray());    // looked again before standing down
            Assert.Equal(1, work.Drains);
            Assert.False(work.Pending);
        }

        /// <summary>
        /// The final record: one made by the last buffer, with nothing after it, is written
        /// within one period by the real writer (Codex's re-check 3 of Track P2: the slot's
        /// entry-only request left it waiting for a buffer that might not come).
        /// </summary>
        [Fact]
        public void The_real_writer_writes_a_lone_last_record_within_a_period_or_two()
        {
            using var writer = new OffAudioDrainer(20, "rt final-record writer");
            var work = new CountingWork();
            writer.Register(work);
            work.Ring.TryRecord(new Rec { A = 7 });
            var wait = Stopwatch.StartNew();
            while (work.Written.Count == 0 && wait.ElapsedMilliseconds < 2000) Thread.Sleep(2);
            _out.WriteLine($"written after {wait.ElapsedMilliseconds} ms");
            Assert.Equal(new long[] { 7 }, work.Written.ToArray());
            Assert.Equal(writer.ThreadId, work.LastDrainThread);
            Assert.NotEqual(Environment.CurrentManagedThreadId, work.LastDrainThread);
        }

        [Fact]
        public void The_writer_sleeps_when_nothing_is_registered_and_wakes_for_a_registration()
        {
            using var writer = new OffAudioDrainer(5, "rt idle writer");
            var work = new CountingWork();
            writer.Register(work);
            Thread.Sleep(60);
            Assert.True(writer.Ticks > 2, "the writer is not running its period");
            writer.Unregister(work);
            Thread.Sleep(30);
            long idle = writer.Ticks;
            Thread.Sleep(100);
            Assert.InRange(writer.Ticks, idle, idle + 1);        // waiting indefinitely, not every 5 ms
            var next = new CountingWork();
            next.Ring.TryRecord(new Rec { A = 3 });
            writer.Register(next);
            var wait = Stopwatch.StartNew();
            while (next.Written.Count == 0 && wait.ElapsedMilliseconds < 2000) Thread.Sleep(2);
            Assert.Equal(new long[] { 3 }, next.Written.ToArray());
        }

        [Fact]
        public void A_retiring_work_is_drained_once_more_even_if_not_pending_then_dropped()
        {
            var writer = new ManualOffAudioScheduler();
            var work = new CountingWork();
            writer.Register(work);
            writer.RetireAfterDrain(work);
            Assert.False(writer.IsRegistered(work));
            Assert.Equal(1, writer.Tick());
            Assert.Equal(1, work.Drains);
            Assert.Equal(0, writer.Tick());
        }

        private sealed class ThrowingWork : OffAudioWork
        {
            protected override void DrainCore() => throw new InvalidOperationException("a broken writer");
        }

        [Fact]
        public void A_drain_that_throws_does_not_stop_the_writer_for_anyone_else()
        {
            using var writer = new OffAudioDrainer(5, "rt fault writer");
            var bad = new ThrowingWork();
            var good = new CountingWork();
            writer.Register(bad);
            writer.Register(good);
            bad.Signal();
            good.Ring.TryRecord(new Rec { A = 5 });
            var wait = Stopwatch.StartNew();
            while ((good.Written.Count == 0 || writer.Faults == 0) && wait.ElapsedMilliseconds < 2000) Thread.Sleep(2);
            Assert.True(writer.Faults > 0);
            Assert.Equal(new long[] { 5 }, good.Written.ToArray());
            good.Ring.TryRecord(new Rec { A = 6 });
            wait.Restart();
            while (good.Written.Count < 2 && wait.ElapsedMilliseconds < 2000) Thread.Sleep(2);
            Assert.Equal(new long[] { 5, 6 }, good.Written.ToArray());
        }

        // ── The buffer queue (item 6) ──

        [Fact]
        public void The_buffer_queue_is_first_in_first_out_and_a_full_one_drops_the_new_buffer()
        {
            var q = new RxBufferQueue(4);
            var bufs = Enumerable.Range(0, 6).Select(i => new float[] { i }).ToArray();
            foreach (var b in bufs) q.Enqueue(b);
            Assert.Equal(4, q.Count);
            Assert.Equal(2, q.Dropped);
            for (int i = 0; i < 4; i++) { Assert.True(q.TryDequeue(out var b)); Assert.Same(bufs[i], b); }
            Assert.False(q.TryDequeue(out _));
            Assert.Throws<InvalidOperationException>(() => q.Dequeue());
        }

        [Fact]
        public void Clearing_from_another_thread_discards_what_was_queued_and_keeps_what_comes_after()
        {
            var q = new RxBufferQueue(8);
            var old = new[] { new float[1], new float[1], new float[1] };
            foreach (var b in old) q.Enqueue(b);
            var t = new Thread(q.Clear);                         // the stop path, or the server's start
            t.Start(); t.Join();
            Assert.Equal(0, q.Count);
            var fresh = new float[1];
            q.Enqueue(fresh);
            Assert.Equal(1, q.Count);
            Assert.True(q.TryDequeue(out var got));
            Assert.Same(fresh, got);
            Assert.False(q.TryDequeue(out _));
            // Room is given back: the ring wraps past the cleared buffers.
            for (int i = 0; i < 8; i++) Assert.True(q.Enqueue(new float[1]));
            Assert.Equal(0, q.Dropped);
        }

        [Fact]
        public void The_buffer_queue_allocates_nothing_either_side()
        {
            var q = new RxBufferQueue();
            var b = new float[960];
            q.Enqueue(b); q.TryDequeue(out _); q.Clear(); q.TryDequeue(out _);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++) { q.Enqueue(b); _ = q.Count; q.TryDequeue(out _); if (i % 100 == 0) q.Clear(); }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // ── The receive chain's own lines (item 7) ──

        private static byte[] TenMilliseconds()
        {
            using var enc = new OpusEncoder(SamplingRate.Sampling48000, Channels.Stereo, OpusApplicationType.Audio, Delay.Delay10ms);
            var pcm = new float[960];
            for (int n = 0; n < 480; n++) pcm[2 * n] = pcm[2 * n + 1] = (float)(0.2 * Math.Sin(2 * Math.PI * 440 * n / 48000));
            return enc.Encode(pcm);
        }

        /// <summary>
        /// The receive loop's level line, every 500 packets, was formatted and written on
        /// the loop. Now the 500th packet records it and returns; the writer writes it,
        /// with the same words, on another thread. Positive control: the record is there
        /// before the tick, and the line after it.
        /// </summary>
        [Fact]
        public void The_chain_s_level_line_is_recorded_on_the_receive_loop_and_written_off_it()
        {
            RecordedQsoOggTests.OpusLibraryReady();
            var writer = new ManualOffAudioScheduler();
            var played = 0;
            var chain = new RxOpusChain(_ => played++, () => 48000, scheduler: writer);
            var lines = new ConcurrentQueue<(string Line, int Thread)>();
            chain.LineObserverForTest = l => lines.Enqueue((l, Environment.CurrentManagedThreadId));
            byte[] packet = TenMilliseconds();
            using var dec = new OpusDecoder(SamplingRate.Sampling48000, Channels.Stereo);
            for (int i = 0; i < 499; i++) chain.WritePacket(dec, packet);
            Assert.False(chain.LinesForTest.Pending);
            chain.WritePacket(dec, packet);                     // the 500th: the line's moment
            Assert.Equal(500, played);
            Assert.True(chain.LinesForTest.Pending, "the 500th packet recorded nothing");
            Assert.Empty(lines);                                // nothing written on the receive loop
            int loop = Environment.CurrentManagedThreadId;
            Task.Run(() => writer.Tick()).Wait(10000);
            var got = lines.ToArray();
            foreach (var l in got) _out.WriteLine(l.Line);
            Assert.Single(got);
            Assert.StartsWith("OpusAudio: raw peak=", got[0].Line);
            Assert.Contains("output loudness=", got[0].Line);
            Assert.NotEqual(loop, got[0].Thread);
            Assert.True(RecordRing<Rec>.ArrayBytes(8) < RecordRing<Rec>.LargeObjectThreshold);
            chain.Detach();
            Assert.False(writer.IsRegistered(chain.LinesForTest));
        }
    }
}
