using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JJPortaudio;
using POpusCodec;
using POpusCodec.Enums;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// A receive gap longer than concealment covers (#1155, Sprint 49 Track P2,
    /// item 5), through REAL Opus, the real receive tail (<see cref="RxOpusChain"/>)
    /// and the real playback queue's render. Measures the two ways the brief left
    /// open: join the seam straight after the last real frame, or conceal the
    /// first <see cref="ReceiveGap.ConcealCapFrames"/> frames first and join after
    /// them. <see cref="ReceiveGap.ConcealBeforeSeam"/> is set from this.
    /// </summary>
    public class RxSeamJoinTests
    {
        private const int Rate = 48000, Fpb = 480, Bpc = 10;
        private readonly ITestOutputHelper _out;
        public RxSeamJoinTests(ITestOutputHelper output) { RecordedQsoOggTests.OpusLibraryReady(); _out = output; }

        private static float[] Pcm(Func<int, float> f, int frames)
        {
            var a = new float[frames * 2];
            for (int n = 0; n < frames; n++) a[2 * n] = a[2 * n + 1] = f(n);
            return a;
        }

        private static List<byte[]> Encode(float[] pcm)
        {
            using var enc = new OpusEncoder(SamplingRate.Sampling48000, Channels.Stereo, OpusApplicationType.Audio, Delay.Delay10ms);
            var packets = new List<byte[]>();
            for (int i = 0; i + Fpb * 2 <= pcm.Length; i += Fpb * 2)
            {
                var chunk = new float[Fpb * 2];
                Array.Copy(pcm, i, chunk, 0, chunk.Length);
                packets.Add(enc.Encode(chunk));
            }
            return packets;
        }

        /// <summary>Packets through the chain into the queue, losing <paramref name="lost"/> after packet <paramref name="at"/>; returns the left channel heard.</summary>
        private static List<float> Play(List<byte[]> packets, int at, int lost, string how) => Play(packets, at, lost, how, out _, out _);

        /// <summary>As above, also giving the queue's starvations and the frames it had to play as silence.</summary>
        private static List<float> Play(List<byte[]> packets, int at, int lost, string how, out long starvations, out long silent)
        {
            using var dec = new OpusDecoder(SamplingRate.Sampling48000, Channels.Stereo);
            var q = new Queue();
            var mode = how == "today" ? RxCatchUpMode.Off : RxCatchUpMode.On;
            var playback = new RxPlaybackQueue("receive", Bpc, 10.0, Rate, 60, mode);
            var chain = new RxOpusChain(b => q.Enqueue(b), () => Rate, (b, cls, seam) => playback.NoteEnqueued(b, cls, seam));
            var heard = new List<float>();
            var output = new float[Fpb * Bpc * 2];
            float[] buffer = Array.Empty<float>(); uint offset = 0;
            int p = 0;
            while (p < packets.Count)
            {
                for (int k = 0; k < Bpc && p < packets.Count; k++, p++)
                {
                    if (p > at && p <= at + lost) continue;
                    if (p == at + lost + 1)
                    {
                        if (how == "conceal-then-join") chain.FillGap(dec, packets[p], ReceiveGap.ConcealCapFrames);
                        if (how != "today" && how != "unmarked") chain.MarkSeam();
                    }
                    chain.WritePacket(dec, packets[p]);
                }
                playback.RenderCallback(q, ref buffer, ref offset, output, false, 2, () => true);
                for (int i = 0; i < Fpb * Bpc; i++) heard.Add(output[2 * i]);
            }
            starvations = playback.Starvations;
            silent = playback.ShifterForTest?.SilentFrames ?? 0;
            if (how != "today" && how != "unmarked") Assert.True(playback.ShifterForTest.TotalSeamJoins == 1, $"{how}: {chain.SeamsMarked} marked, {playback.ShifterForTest.TotalSeamJoins} joined, dry {playback.ShifterForTest.SilentFrames}");
            return heard;
        }

        /// <summary>The largest second difference within <paramref name="from"/>..<paramref name="to"/>, against the steady stretch before: a step or a kink in the waveform.</summary>
        private static double Curvature(List<float> y, int from, int to)
        {
            double steady = 0, worst = 0;
            for (int i = Rate / 2; i < from && i < y.Count; i++) steady = Math.Max(steady, Math.Abs(y[i] - 2 * y[i - 1] + y[i - 2]));
            for (int i = Math.Max(2, from); i < to && i < y.Count; i++) worst = Math.Max(worst, Math.Abs(y[i] - 2 * y[i - 1] + y[i - 2]));
            return worst / Math.Max(steady, 1e-9);
        }

        /// <summary>The largest phase step and the deepest dip, Hann-windowed 4 ms demodulation, over samples [from, to).</summary>
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

        /// <summary>The deepest drop of the 2.5 ms RMS within the window, in dB below the median of the second before.</summary>
        private static double LevelDip(List<float> y, int from, int to)
        {
            double Rms(int a) { double e = 0; for (int i = a; i < a + 120; i++) e += (double)y[i] * y[i]; return Math.Sqrt(e / 120); }
            var before = new List<double>();
            for (int a = Rate / 2; a + 120 < from; a += 120) before.Add(Rms(a));
            double med = before.OrderBy(v => v).ElementAt(before.Count / 2), worst = med;
            for (int a = from; a + 120 < to && a + 120 < y.Count; a += 60) worst = Math.Min(worst, Rms(a));
            return 20 * Math.Log10(med / Math.Max(worst, 1e-9));
        }

        [Fact]
        public void Measures_joining_straight_away_against_concealing_first_and_the_constant_follows_the_measurement()
        {
            int frames = Rate * 3;
            var tone = Pcm(n => (float)(0.4 * Math.Sin(2 * Math.PI * 625 * n / Rate)), frames);
            var rng = new Random(11);
            double lp = 0;
            var noise = Pcm(n => { lp += ((rng.NextDouble() - 0.5) - lp) * 0.3; return (float)(lp * 0.6); }, frames);
            const int at = 150, lost = 6;
            // The seam is heard about one reserve plus a callback after the packets around it are fed.
            int seam = (at + 1) * Fpb, window = 14400;
            var results = new Dictionary<string, (double Tone, double Noise)>();
            foreach (string how in new[] { "today", "join", "conceal-then-join" })
            {
                var t = Play(Encode(tone), at, lost, how);
                var n = Play(Encode(noise), at, lost, how);
                // Over everything heard after start-up: the seam is the only event in it,
                // wherever each path's own start-up timing puts it.
                var (step, toneDip) = PhaseAndDip(t, 625, Rate, t.Count);
                double curve = step + toneDip / 10;  // one figure: radians plus a tenth of the dB dip
                double dip = LevelDip(n, Rate, n.Count - 240);
                results[how] = (curve, dip);
                _out.WriteLine($"{how}: 625 Hz tone, largest phase step {step:0.000} rad, deepest dip {toneDip:0.0} dB; band noise, deepest 2.5 ms dip {dip:0.0} dB");
            }
            // The join beats today's splice on the tone and makes no hole in noise. Concealing
            // first is better only if it is better on BOTH materials.
            Assert.True(results["join"].Tone < results["today"].Tone, "the join was no smoother than today's splice");
            // And the join itself is good, not merely better than a near-cancellation:
            // under half a radian and under 7 dB on the tone (measured 0.45 and 5.7).
            Assert.True(results["join"].Tone < 1.3, $"the join measured {results["join"].Tone:0.00} on the tone");
            bool concealBetter = results["conceal-then-join"].Tone < results["join"].Tone && results["conceal-then-join"].Noise < results["join"].Noise;
            _out.WriteLine($"conceal first measures {(concealBetter ? "better" : "no better")}; ReceiveGap.ConcealBeforeSeam is {ReceiveGap.ConcealBeforeSeam}");
            Assert.Equal(concealBetter, ReceiveGap.ConcealBeforeSeam);
        }

        /// <summary>
        /// Codex's check of 2567a596 (B3): the join skipped 2.5 ms of the codec's
        /// overlap but extended the audio before by less than that (104 frames
        /// against 120), so after six lost packets had spent the reserve it took the
        /// last 16 frames the callback needed: one starvation, and the 6.7 dB dip was
        /// that dry-out's fades, not the seam. The same arrival with no mark is the
        /// control: it never starves. A join must not starve where it does not.
        /// </summary>
        [Fact]
        public void A_join_after_six_lost_packets_spends_no_reserve_the_unmarked_stream_kept()
        {
            int frames = Rate * 3;
            var rng = new Random(11);
            double lp = 0;
            var noise = Pcm(n => { lp += ((rng.NextDouble() - 0.5) - lp) * 0.3; return (float)(lp * 0.6); }, frames);
            var tone = Pcm(n => (float)(0.4 * Math.Sin(2 * Math.PI * 625 * n / Rate)), frames);
            const int at = 150, lost = 6;
            foreach (var (name, pcm) in new[] { ("band noise", noise), ("625 Hz", tone) })
            {
                var packets = Encode(pcm);
                var control = Play(packets, at, lost, "unmarked", out long controlStarved, out long controlSilent);
                var joined = Play(packets, at, lost, "join", out long starved, out long silent);
                double controlDip = LevelDip(control, Rate, control.Count - 240), dip = LevelDip(joined, Rate, joined.Count - 240);
                _out.WriteLine($"{name}: unmarked {controlStarved} starvation(s), {controlSilent} silent frames, deepest dip {controlDip:0.0} dB; joined {starved}, {silent}, {dip:0.0} dB");
                // Positive control: the same arrival really reaches the reserve's edge (the
                // join is what decides whether it starves), and it is played unmarked without a gap.
                Assert.Equal(0, controlStarved);
                Assert.Equal(0, controlSilent);
                Assert.Equal(controlStarved, starved);
                Assert.Equal(controlSilent, silent);
            }
        }

        /// <summary>A processor that holds each seam one buffer and lets it out 100 frames in, as a delay would.</summary>
        private sealed class FakeProcessor : IRxReceiveTimeline
        {
            public RxContentClass Next = RxContentClass.Classified | RxContentClass.Speech;
            private bool _in, _held;
            public RxContentClass LastBufferClass { get; private set; }
            public int LastBufferSeamAt { get; private set; } = -1;
            public void MarkSeamIn() => _in = true;
            public void Process(float[] buffer)
            {
                LastBufferClass = Next;
                LastBufferSeamAt = _held ? 100 : -1;
                _held = _in; _in = false;
            }
        }

        [Fact]
        public void The_chain_takes_each_buffer_s_class_from_its_processor_and_marks_one_seam()
        {
            using var dec = new OpusDecoder(SamplingRate.Sampling48000, Channels.Stereo);
            var packets = Encode(Pcm(n => (float)(0.3 * Math.Sin(n * 0.05)), Fpb * 6));
            var seen = new List<(RxContentClass Cls, int Seam)>();
            var proc = new FakeProcessor();
            var chain = new RxOpusChain(_ => { }, () => Rate, (b, cls, seam) => seen.Add((cls, seam))) { PostDecodeProcessor = proc.Process };
            chain.WritePacket(dec, packets[0]);
            proc.Next = RxContentClass.Classified | RxContentClass.NotVoiceRun | RxContentClass.NoSplice;
            chain.MarkSeam();
            chain.WritePacket(dec, packets[1]);
            chain.WritePacket(dec, packets[2]);
            chain.PostDecodeProcessor = b => { };   // a processor that keeps no timeline: the buffer is unchanged in time
            chain.WritePacket(dec, packets[3]);
            chain.MarkSeam();
            chain.WritePacket(dec, packets[4]);
            // The seam comes out where the processor says, a buffer later and 100 frames
            // in, not on the buffer it went in with (Codex's check of 2567a596, B1).
            Assert.Equal(new[]
            {
                (RxContentClass.Classified | RxContentClass.Speech, -1),
                (RxContentClass.Classified | RxContentClass.NotVoiceRun | RxContentClass.NoSplice, -1),
                (RxContentClass.Classified | RxContentClass.NotVoiceRun | RxContentClass.NoSplice, 100),
                (RxContentClass.Unclassified, -1),
                (RxContentClass.Unclassified, 0),
            }, seen);
        }

        [Fact]
        public void A_digital_mode_through_a_forced_loss_gets_no_join_and_no_splice()
        {
            var q = new Queue();
            var playback = new RxPlaybackQueue("receive", Bpc, 10.0, Rate, 60, RxCatchUpMode.On);
            var output = new float[Fpb * Bpc * 2];
            float[] buffer = Array.Empty<float>(); uint offset = 0;
            var rng = new Random(3);
            int k = 0;
            for (int c = 0; c < 200; c++)
            {
                for (int p = 0; p < Bpc; p++, k++)
                {
                    if (k % 50 == 0 && k > 0) { k++; continue; }                // every 50th packet lost
                    var b = new float[Fpb * 2];
                    for (int i = 0; i < b.Length; i++) b[i] = (float)(rng.NextDouble() - 0.5) * 0.2f;
                    playback.NoteEnqueued(b, RxContentClass.Classified | RxContentClass.NotVoiceRun | RxContentClass.NoSplice, k % 50 == 1);
                    q.Enqueue(b);
                }
                playback.RenderCallback(q, ref buffer, ref offset, output, false, 2, () => true);
            }
            var sh = playback.ShifterForTest;
            _out.WriteLine($"DIGU: removed {sh.TotalRemoved}, added {sh.TotalAdded}, seam joins {sh.TotalSeamJoins}");
            Assert.Equal(0, sh.TotalPeriodSplices + sh.TotalGapSplices + sh.TotalSeamJoins);
        }
    }
}
