using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JJPortaudio;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// The playback queue's catch-up (Sprint 49 Track P2; #473, #873 phase 2,
    /// #1155, #1154), driven through the REAL render path
    /// (<see cref="RxPlaybackQueue.RenderCallback"/>, which <c>Audio.outputCallback</c>
    /// calls) by scripted arrival schedules, with no device and no radio. The
    /// shipped shape: 48 kHz, 10 ms packets, ten callbacks a second, a 60 ms
    /// reserve. Each case beside today's code (catch-up off) as its control.
    /// </summary>
    public class RxCatchUpQueueTests
    {
        private const int Rate = 48000, Fpb = 480, Bpc = 10, CallbackFrames = Fpb * Bpc;
        private readonly ITestOutputHelper _out;
        public RxCatchUpQueueTests(ITestOutputHelper output) { _out = output; }

        /// <summary>One receive stream: a real queue, the real playback queue, the real render.</summary>
        private sealed class Stream
        {
            public readonly Queue Q = new Queue();
            public readonly RxPlaybackQueue Playback;
            public readonly List<float> Out = new();
            public readonly List<string> Lines = new();
            public float[] Buffer = Array.Empty<float>();
            public uint Offset;
            public long Packet;                 // the next packet the source makes
            public readonly Func<long, float> Signal;
            public readonly RxContentClass Class;
            private readonly float[] _out = new float[CallbackFrames * 2];
            private bool _seamNext;

            public Stream(RxCatchUpMode mode, Func<long, float> signal, double reserveMs = 60,
                RxContentClass cls = RxContentClass.Classified | RxContentClass.NotVoiceRun, bool playAtOnce = true)
            {
                Playback = new RxPlaybackQueue("receive", Bpc, 10.0, Rate, reserveMs, mode) { PlayAtOnce = playAtOnce };
                Playback.LineObserver = Lines.Add;
                Signal = signal;
                Class = cls;
            }

            /// <summary><paramref name="packets"/> packets arrive, the next <paramref name="lost"/> before them never do.</summary>
            public void Arrive(int packets, int lostBefore = 0, bool markSeam = true)
            {
                if (lostBefore > 0) { Packet += lostBefore; _seamNext = markSeam; }
                for (int p = 0; p < packets; p++)
                {
                    var b = new float[Fpb * 2];
                    for (int i = 0; i < Fpb; i++) b[2 * i] = b[2 * i + 1] = Signal(Packet * Fpb + i);
                    Packet++;
                    Playback.NoteEnqueued(b, Class, _seamNext);
                    _seamNext = false;
                    Q.Enqueue(b);
                }
            }

            public void Callback()
            {
                Playback.RenderCallback(Q, ref Buffer, ref Offset, _out, false, 2, () => true);
                for (int i = 0; i < CallbackFrames; i++) Out.Add(_out[2 * i]);
            }

            public long DepthFrames => Q.Count * (long)Fpb + (Playback.ShifterForTest?.Available ?? 0);

            public void Steady(int callbacks) { for (int c = 0; c < callbacks; c++) { Arrive(Bpc); Callback(); } }

            public void Close() { Playback.TraceSummary(); }
        }

        private static readonly Func<long, float> Noise = n =>
        {
            // A cheap deterministic hash: band-ish noise, identical in every run.
            ulong x = (ulong)n * 0x9E3779B97F4A7C15UL; x ^= x >> 29; x *= 0xBF58476D1CE4E5B9UL; x ^= x >> 32;
            return ((x & 0xFFFF) / 65535f - 0.5f) * 0.3f;
        };

        private static Func<long, float> Tone(double hz) => n => (float)(0.5 * Math.Sin(2 * Math.PI * hz * n / Rate));

        // ── Negative controls: today's path, sample for sample ──

        /// <summary>
        /// Today's callback, transcribed from <c>Audio.outputCallback</c> as it stood at
        /// <c>45a90516</c>, before Track P2 moved it: the reference the moved code
        /// must equal.
        /// </summary>
        private static void TodayReference(RxPlaybackQueue queue, Queue q, ref float[] buffer, ref uint offset, float[] output)
        {
            int at = 0, end = output.Length;
            long silentFrames = 0; int consumed = 0;
            var plan = queue.Begin(q.Count, offset != 0);
            for (int i = 0; i < plan.Discard && q.Count > 0; i++) q.Dequeue();
            if (plan.HoldForPrime) { while (at != end) output[at++] = 0f; queue.NotePrimingBuffer((end - 0) / 2); return; }
            while (true)
            {
                bool silence = false;
                if (offset == 0)
                {
                    if (q.Count == 0) { silence = true; silentFrames += (end - at) / 2; }
                    else { buffer = (float[])q.Dequeue(); consumed++; }
                }
                if (silence) { while (at != end) output[at++] = 0f; }
                else
                {
                    while ((offset < buffer.Length) & (at != end)) output[at++] = buffer[offset++];
                    if (offset == buffer.Length) offset = 0;
                }
                if (at == end) break;
            }
            queue.NotePlayed(consumed, silentFrames);
        }

        private static int[] Schedule(int seed, int callbacks)
        {
            var rng = new Random(seed);
            return Enumerable.Range(0, callbacks).Select(_ => rng.Next(10) switch { 0 => 0, 1 => 3, 2 => 25, _ => 10 }).ToArray();
        }

        [Theory]
        [InlineData(60.0, RxCatchUpMode.Off)]      // JJFLEX_RX_CATCHUP=off
        [InlineData(0.0, RxCatchUpMode.On)]        // no reserve: the CW monitor, JJFLEX_RX_PRIME_MS=0 (catch-up on is ignored)
        [InlineData(60.0, RxCatchUpMode.Shadow)]   // shadow traces, and leaves the output alone
        public void Today_s_path_is_today_s_output_sample_for_sample(double reserveMs, RxCatchUpMode mode)
        {
            var sched = Schedule(7, 400);
            var s = new Stream(mode, Noise, reserveMs);
            var refQueue = new RxPlaybackQueue("receive", Bpc, 10.0, Rate, reserveMs, RxCatchUpMode.Off);
            var refQ = new Queue(); float[] refBuf = Array.Empty<float>(); uint refOff = 0;
            var refOut = new float[CallbackFrames * 2];
            long packet = 0;
            for (int c = 0; c < sched.Length; c++)
            {
                for (int p = 0; p < sched[c]; p++)
                {
                    var b = new float[Fpb * 2];
                    for (int i = 0; i < Fpb; i++) b[2 * i] = b[2 * i + 1] = Noise(packet * Fpb + i);
                    packet++;
                    refQ.Enqueue(b);
                }
                s.Arrive(sched[c]);
                TodayReference(refQueue, refQ, ref refBuf, ref refOff, refOut);
                int before = s.Out.Count;
                s.Callback();
                for (int i = 0; i < CallbackFrames; i++) Assert.Equal(refOut[2 * i], s.Out[before + i]);
            }
            Assert.Equal(reserveMs > 0 ? mode : RxCatchUpMode.Off, s.Playback.CatchUp);
            Assert.Equal(refQueue.Trims, s.Playback.Trims);
            Assert.Equal(refQueue.Starvations, s.Playback.Starvations);
            Assert.True(refQueue.Trims + refQueue.ReleaseTrims + refQueue.Starvations > 0, "the schedule never exercised today's policy");
            s.Close();
            s.Playback.DrainLines();
            if (mode == RxCatchUpMode.Shadow)
                Assert.Contains(s.Lines, l => l.Contains("shadow: what catch-up WOULD have done"));
            else Assert.DoesNotContain(s.Lines, l => l.Contains("WOULD have done"));
        }

        // ── (a) a stall then a burst: caught up, nothing discarded ──

        [Fact]
        public void A_300_ms_stall_then_a_burst_is_caught_up_not_discarded()
        {
            (long trims, long released, long removed, int back) Run(RxCatchUpMode mode)
            {
                var s = new Stream(mode, Noise);
                s.Steady(30);
                for (int c = 0; c < 3; c++) s.Callback();           // 300 ms with nothing
                s.Arrive(30); s.Callback();                          // the stall's packets, all at once
                long releasedBefore = s.Playback.ReleaseTrimmedBuffers;
                int back = -1;
                for (int c = 0; c < 200; c++)
                {
                    s.Arrive(Bpc); s.Callback();
                    if (back < 0 && s.DepthFrames - (Bpc + 6) * (long)Fpb < Rate / 100) back = c + 1;
                }
                s.Close();
                return (s.Playback.Trims, s.Playback.ReleaseTrimmedBuffers - releasedBefore + (mode == RxCatchUpMode.Off ? s.Playback.ReleaseTrimmedBuffers : 0),
                    s.Playback.CatchUpRemovedFrames + s.Playback.CutFrames, back);
            }
            var on = Run(RxCatchUpMode.On);
            var off = Run(RxCatchUpMode.Off);
            _out.WriteLine($"catch-up: {on.trims} ceiling trim(s), {on.removed / 48.0:0} ms caught up, back within 10 ms of target after {on.back} callbacks; today: {off.trims} ceiling trim(s), {off.released} buffer(s) discarded at re-prime release");
            Assert.Equal(0, on.trims);
            Assert.True(on.removed > 0, "nothing was caught up");
            Assert.InRange(on.back, 1, 80);                      // 300 ms at the gap rate: well inside 8 s
            Assert.True(off.trims + off.released > 1, "the positive control discarded nothing");
        }

        // ── (b) a burst that only refills the reserve; (f) a tune-shaped arrival pattern ──

        [Fact]
        public void Bursts_that_only_refill_the_reserve_and_a_tune_shaped_pattern_trigger_nothing()
        {
            // (b): 8 and 12 packets in turn, ten a callback on average.
            var s = new Stream(RxCatchUpMode.On, Noise);
            s.Steady(20);
            for (int c = 0; c < 300; c++) { s.Arrive(c % 2 == 0 ? 8 : 12); s.Callback(); }
            // (f): arrow-step tuning, about five steps a second: each a 30 ms pause and the burst that makes it up.
            for (int c = 0; c < 300; c++) { s.Arrive(c % 2 == 0 ? 7 : 13); s.Callback(); }
            var sh = s.Playback.ShifterForTest;
            _out.WriteLine($"removed {sh.TotalRemoved}, added {sh.TotalAdded}, starvations {s.Playback.Starvations}");
            Assert.Equal(0, sh.TotalRemoved + sh.TotalAdded);
            Assert.Equal(0, s.Playback.Starvations);

            // Bursts that run up to 100 ms over the target for three callbacks running,
            // then are used up by a lull (15, 15, 5, 5): at a callback's entry that reads
            // as late audio, but the least depth over a second is the target, so nothing.
            var u = new Stream(RxCatchUpMode.On, Noise);
            for (int c = 0; c < 20; c++) { u.Arrive(Bpc); u.Callback(); }
            for (int c = 0; c < 300; c++) { u.Arrive(c % 4 < 2 ? 15 : 5); u.Callback(); }
            Assert.Equal(0, u.Playback.Starvations);
            Assert.Equal(0, u.Playback.ShifterForTest.TotalRemoved + u.Playback.ShifterForTest.TotalAdded);

            // Positive control: a real surplus that stays is caught up.
            var t = new Stream(RxCatchUpMode.On, Noise);
            t.Steady(20);
            t.Arrive(8);
            for (int c = 0; c < 100; c++) { t.Arrive(Bpc); t.Callback(); }
            Assert.True(t.Playback.ShifterForTest.TotalRemoved > 0, "the positive control caught nothing up");
        }

        // ── (c) six lost packets: joined, and the reserve rebuilt ──

        [Theory]
        [InlineData(625.0)]
        [InlineData(510.0)]
        public void Six_lost_packets_are_joined_without_a_step_and_the_reserve_is_rebuilt(double hz)
        {
            (double step, double dip, bool rebuilt) Run(RxCatchUpMode mode)
            {
                var s = new Stream(mode, Tone(hz), cls: RxContentClass.Unclassified);
                s.Steady(30);
                s.Arrive(Bpc, lostBefore: 6); s.Callback();
                // After a callback the queue holds the reserve (60 ms); the loss spent it.
                bool rebuilt = false;
                for (int c = 0; c < 60; c++) { s.Arrive(Bpc); s.Callback(); if (s.DepthFrames >= 6L * Fpb - Rate / 100) rebuilt = true; }
                var (st, dp) = PhaseAndDip(s.Out, hz, 30 * CallbackFrames - 4800, 30 * CallbackFrames + 14400);
                return (st, dp, rebuilt);
            }
            var on = Run(RxCatchUpMode.On);
            var off = Run(RxCatchUpMode.Off);
            _out.WriteLine($"{hz} Hz: joined: step {on.step:0.000} rad, dip {on.dip:0.00} dB, reserve rebuilt {on.rebuilt}; today's splice: step {off.step:0.000} rad, dip {off.dip:0.00} dB");
            Assert.True(on.step < 0.3, $"the join stepped {on.step:0.000} rad");
            Assert.True(on.dip < 1.5, $"the join dipped {on.dip:0.00} dB");
            Assert.True(on.rebuilt, "the reserve was not rebuilt");
            Assert.True(off.step > 1.0, "today's splice did not step");
        }

        // ── (d) a one second loss: no click at either edge, both refill choices ──

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_one_second_loss_fades_out_and_back_in_with_no_click(bool playAtOnce)
        {
            double Worst(List<float> y)
            {
                // The largest step between neighbours, against the tone's own largest.
                double own = 0.5 * 2 * Math.PI * 500 / Rate, worst = 0;
                for (int i = 1; i < y.Count; i++) worst = Math.Max(worst, Math.Abs(y[i] - y[i - 1]));
                return worst / own;
            }
            (double jump, long silentMs) Run(RxCatchUpMode mode)
            {
                // The tone offset so its edges do not fall at a zero crossing by luck.
                var s = new Stream(mode, n => (float)(0.5 * Math.Sin(2 * Math.PI * 500 * n / Rate + 1.1)), cls: RxContentClass.Unclassified, playAtOnce: playAtOnce);
                s.Steady(30);
                for (int c = 0; c < 10; c++) s.Callback();          // a second of nothing
                for (int c = 0; c < 40; c++) { s.Arrive(Bpc); s.Callback(); }
                return (Worst(s.Out.Skip(20 * CallbackFrames).ToList()), (long)s.Playback.StarvationSilenceMilliseconds);
            }
            var on = Run(RxCatchUpMode.On);
            var off = Run(RxCatchUpMode.Off);
            _out.WriteLine($"play at once {playAtOnce}: catch-up: largest step {on.jump:0.00}x the tone's own, {on.silentMs} ms of silence; today: {off.jump:0.00}x, {off.silentMs} ms");
            Assert.True(on.jump < 1.3, $"a click: {on.jump:0.00}x");
            Assert.True(off.jump > 3, "today's edges did not click");
        }

        [Fact]
        public void After_a_loss_play_at_once_resumes_sooner_than_today_s_wait_and_a_second_dry_spell_falls_back_to_it()
        {
            int Resume(bool playAtOnce, bool twice)
            {
                var s = new Stream(RxCatchUpMode.On, Noise, playAtOnce: playAtOnce);
                s.Steady(30);
                if (twice) { for (int c = 0; c < 3; c++) s.Callback(); for (int c = 0; c < 10; c++) { s.Arrive(Bpc); s.Callback(); } }
                for (int c = 0; c < 5; c++) s.Callback();                // dry
                int start = s.Out.Count;
                s.Arrive(3); s.Callback();                               // 30 ms comes back
                for (int c = 0; c < 4; c++) { s.Arrive(Bpc); s.Callback(); }
                for (int i = start; i < s.Out.Count; i++) if (Math.Abs(s.Out[i]) > 1e-6) return i - start;
                return int.MaxValue;
            }
            int atOnce = Resume(true, false), wait = Resume(false, false), secondSpell = Resume(true, true);
            _out.WriteLine($"audio back after {atOnce} samples playing at once, {wait} waiting for the reserve, {secondSpell} on a second dry spell within 5 s");
            Assert.True(atOnce < wait, "play-at-once did not resume sooner");
            Assert.True(secondSpell > atOnce, "a second dry spell within 5 s did not fall back to the wait");
        }

        // ── (e) a starvation during a drain abandons it; (item 6) a step from a third owner ──

        [Fact]
        public void A_starvation_during_a_drain_abandons_it()
        {
            // The queue's own catch-up ends as the depth falls, before the queue can run
            // dry (the least depth over a second falls with it), so a drain still running
            // at a starvation is a step handed in from outside: SilkTune's, one day.
            var s = new Stream(RxCatchUpMode.On, Noise);
            s.Steady(20);
            s.Playback.ExternalStep(Rate);                   // "the content moved a whole second": far more than is there
            for (int c = 0; c < 30; c++) { s.Arrive(c < 10 ? Bpc : 0); s.Callback(); }
            s.Playback.DrainLines();
            _out.WriteLine(string.Join(Environment.NewLine, s.Lines.Where(l => l.Contains("External") || l.Contains("dry"))));
            Assert.True(s.Playback.Starvations > 0, "the queue never ran dry");
            Assert.Contains(s.Lines, l => l.Contains("External") && l.Contains("abandoned"));
            Assert.Equal(0, s.Playback.ShifterForTest.Pending);
        }

        [Fact]
        public void A_step_from_outside_is_drained_on_the_engine_switch_policy_and_the_controller_waits_for_it()
        {
            var s = new Stream(RxCatchUpMode.On, Noise);
            s.Steady(20);
            s.Arrive(3);                                     // a little over: the controller would act on its own
            s.Playback.ExternalStep(1440);
            for (int c = 0; c < 40; c++) { s.Arrive(Bpc); s.Callback(); }
            s.Playback.DrainLines();
            Assert.Contains(s.Lines, l => l.Contains("External") && l.Contains("EngineSwitch policy") && l.Contains("removed 30.0 ms"));
        }

        // ── Drift: a slow clock difference is corrected, period splices only ──

        [Fact]
        public void A_slow_surplus_held_for_30_seconds_is_drained_as_drift_by_period_splices()
        {
            var s = new Stream(RxCatchUpMode.On, Tone(440), cls: RxContentClass.Unclassified);
            s.Steady(20);
            s.Arrive(2);                                  // 20 ms over, and it stays
            for (int c = 0; c < 450; c++) { s.Arrive(Bpc); s.Callback(); }
            s.Playback.DrainLines();
            var sh = s.Playback.ShifterForTest;
            _out.WriteLine($"removed {sh.TotalRemoved} in {sh.TotalPeriodSplices} period and {sh.TotalGapSplices} gap splices");
            Assert.Equal(0, sh.TotalGapSplices);
            Assert.True(sh.TotalRemoved >= 480, "the drift was not drained");
            Assert.Contains(s.Lines, l => l.Contains("Drift"));
        }

        // ── Nothing on the callback allocates ──

        [Fact]
        public void The_render_with_catch_up_allocates_nothing()
        {
            var s = new Stream(RxCatchUpMode.On, Noise);
            var bufs = Enumerable.Range(0, 600).Select(k => { var b = new float[Fpb * 2]; for (int i = 0; i < Fpb; i++) b[2 * i] = b[2 * i + 1] = Noise(k * Fpb + i); return b; }).ToArray();
            int next = 0;
            void Arrive(int n) { for (int p = 0; p < n; p++) { var b = bufs[next++ % bufs.Length]; s.Playback.NoteEnqueued(b, RxContentClass.Classified | RxContentClass.NotVoiceRun, p == 0 && next % 97 == 1); s.Q.Enqueue(b); } }
            var output = new float[CallbackFrames * 2];
            float[] buffer = Array.Empty<float>(); uint offset = 0;
            Func<bool> active = () => true;
            for (int c = 0; c < 30; c++) { Arrive(Bpc); s.Playback.RenderCallback(s.Q, ref buffer, ref offset, output, false, 2, active); }
            Arrive(30);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int c = 0; c < 200; c++) { Arrive(c % 7 == 0 ? 0 : Bpc); s.Playback.RenderCallback(s.Q, ref buffer, ref offset, output, false, 2, active); }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            long worked = s.Playback.ShifterForTest.TotalRemoved + s.Playback.ShifterForTest.TotalAdded;
            Assert.Equal(0, allocated);
            Assert.True(worked > 0);
        }

        // ── Measures ──

        /// <summary>The largest phase step and the deepest dip, Hann-windowed 4 ms demodulation, over output samples [from, to).</summary>
        private static (double Step, double DipDb) PhaseAndDip(List<float> y, double hz, int from, int to)
        {
            int w = 192, hop = 48;
            double wsum = 0; for (int k = 0; k < w; k++) wsum += 0.5 - 0.5 * Math.Cos(2 * Math.PI * (k + 0.5) / w);
            var ph = new List<double>(); var amp = new List<double>();
            for (int b = from; b + w <= Math.Min(to, y.Count); b += hop)
            {
                double i = 0, q = 0;
                for (int k = 0; k < w; k++)
                {
                    int n = b + k; double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (k + 0.5) / w);
                    i += y[n] * win * Math.Sin(2 * Math.PI * hz * n / Rate); q += y[n] * win * Math.Cos(2 * Math.PI * hz * n / Rate);
                }
                ph.Add(Math.Atan2(q, i)); amp.Add(2 * Math.Sqrt(i * i + q * q) / wsum);
            }
            double step = 0, steady = amp.OrderBy(v => v).ElementAt(amp.Count / 2), worst = steady;
            for (int b = 1; b < ph.Count; b++)
            {
                double d = ph[b] - ph[b - 1]; while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI;
                step = Math.Max(step, Math.Abs(d)); worst = Math.Min(worst, amp[b]);
            }
            return (step, 20 * Math.Log10(steady / Math.Max(worst, 1e-9)));
        }

        /// <summary>
        /// Codex's check of 2567a596 (C1): the long-backlog cut took 1.8 s out of a
        /// digital mode. The brief says nothing is cut or repeated in DIGU, DIGL and
        /// RTTY; the research proposed keeping the cut there. Until Noel rules, one
        /// constant decides (RxPlaybackQueue.DigitalModesTakeTheLongCut, false), and
        /// both answers are tested.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_digital_mode_takes_the_long_backlog_cut_only_when_the_constant_says_so(bool digitalCut)
        {
            long Cut(RxContentClass cls, bool? overrideCut)
            {
                RxPlaybackQueue.DigitalModesTakeTheLongCutOverrideForTest = overrideCut;
                try
                {
                    var p = new RxPlaybackQueue("probe", 10, 10, 48000, 60, RxCatchUpMode.On);
                    var q = new Queue(); float[] b = Array.Empty<float>(); uint off = 0; var output = new float[9600]; long n = 0;
                    void Arrive(int packets)
                    {
                        for (int j = 0; j < packets; j++)
                        {
                            var x = new float[960];
                            for (int i = 0; i < 480; i++, n++) x[2 * i] = x[2 * i + 1] = (float)(0.3 * Math.Sin(n * 2 * Math.PI * 625 / 48000));
                            p.NoteEnqueued(x, cls, false); q.Enqueue(x);
                        }
                    }
                    void Render() => p.RenderCallback(q, ref b, ref off, output, false, 2, () => true);
                    for (int j = 0; j < 30; j++) { Arrive(10); Render(); }
                    Arrive(200);
                    for (int j = 0; j < 30; j++) { Arrive(10); Render(); }
                    _out.WriteLine($"{cls}, digital cut {overrideCut?.ToString() ?? "as the constant"}: cut {p.CutFrames}, caught up {p.CatchUpRemovedFrames}");
                    return p.CutFrames;
                }
                finally { RxPlaybackQueue.DigitalModesTakeTheLongCutOverrideForTest = null; }
            }
            const RxContentClass digital = RxContentClass.Classified | RxContentClass.NoSplice;
            // Positive control: the same backlog in a voice mode is cut, either way.
            Assert.True(Cut(RxContentClass.Classified | RxContentClass.NotVoiceRun, digitalCut) > 48000);
            long cut = Cut(digital, digitalCut);
            if (digitalCut) Assert.True(cut > 48000, "with the research's answer, a digital mode is cut above 1.5 s");
            else Assert.Equal(0, cut);
            // And the shipped value is the brief's: no cut in a digital mode.
            Assert.False(RxPlaybackQueue.DigitalModesTakeTheLongCut);
            Assert.Equal(0, Cut(digital, null));
        }

        /// <summary>
        /// Codex's re-check of 5a0a8c93: past the ring's 4.5 s, a backlog (a digital
        /// mode is never cut while C1 waits on Noel; or steering cannot keep up) was
        /// dropped with no fade, a 700 ms jump. Now the overflow keeps the audio that
        /// was about to play, lengthens the drop to the best-matching point, and fades.
        /// A 625 Hz tone, where the bare drop lands half a period out of phase: the
        /// faded overflow shows no phase step and no dip (Hann-windowed demodulation,
        /// as the lost-packet tests measure); with the fade off (the positive
        /// control) the phase flips by half a cycle.
        /// </summary>
        [Fact]
        public void An_overflow_of_the_ring_fades_instead_of_jumping()
        {
            (double Step, double DipDb, long Overflow) Run(bool fades)
            {
                var p = new RxPlaybackQueue("probe", 10, 10, 48000, 60, RxCatchUpMode.On);
                if (!fades) p.ShifterForTest.OverflowFades = false;   // the control; the faded run is the shipped default
                var q = new Queue(); float[] b = Array.Empty<float>(); uint off = 0; var output = new float[9600]; long n = 0;
                const RxContentClass digital = RxContentClass.Classified | RxContentClass.NoSplice;
                void Arrive(int packets)
                {
                    for (int j = 0; j < packets; j++)
                    {
                        var x = new float[960];
                        for (int i = 0; i < 480; i++, n++) x[2 * i] = x[2 * i + 1] = (float)(0.4 * Math.Sin(n * 2 * Math.PI * 625 / 48000));
                        p.NoteEnqueued(x, digital, false); q.Enqueue(x);
                    }
                }
                var heard = new List<float>();
                void Render() { p.RenderCallback(q, ref b, ref off, output, false, 2, () => true); for (int i = 0; i < 4800; i++) heard.Add(output[2 * i]); }
                for (int j = 0; j < 30; j++) { Arrive(10); Render(); }
                int mark = heard.Count;
                Arrive(500);
                for (int j = 0; j < 10; j++) { Arrive(10); Render(); }
                var (step, dip) = PhaseAndDip(heard, 625, mark - 4800, heard.Count);
                return (step, dip, p.ShifterForTest.OverflowFrames);
            }
            var faded = Run(true);
            var hard = Run(false);
            _out.WriteLine($"faded overflow: phase step {faded.Step:0.000} rad, deepest dip {faded.DipDb:0.00} dB, {faded.Overflow} frames dropped; "
                + $"hard drop: phase step {hard.Step:0.000} rad, dip {hard.DipDb:0.00} dB, {hard.Overflow} dropped");
            Assert.True(faded.Overflow > 0 && hard.Overflow > 0, "the ring never overflowed, so nothing was tested");
            Assert.True(hard.Step > 1.0 && hard.DipDb > 6, "positive control: the hard drop should step the phase and dip (a 4 ms window splits a half-cycle flip over two)");
            Assert.True(faded.Step < 0.25, $"the faded overflow stepped {faded.Step:0.000} rad");
            Assert.True(faded.DipDb < 1.0, $"the faded overflow dipped {faded.DipDb:0.00} dB");
        }
    }
}
