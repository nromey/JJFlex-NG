using System;
using System.Collections.Generic;
using System.Linq;
using JJPortaudio;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// The lag shifter's core on its own (#873 phase 2, #473): no device, no
    /// radio, no window, nothing played. Every negative here has its positive
    /// control in the same class, and every "no change" is sample for sample.
    /// </summary>
    public class RxLagShifterTests
    {
        private const int Rate = 48000;
        private const int Block = 480;
        private readonly ITestOutputHelper _out;
        public RxLagShifterTests(ITestOutputHelper output) { _out = output; }

        // ── Signals ──

        private static float[] Tone(double hz, int frames, double amp = 0.5, int channels = 2, double phase = 0)
        {
            var a = new float[frames * channels];
            for (int n = 0; n < frames; n++)
            {
                float v = (float)(amp * Math.Sin(2 * Math.PI * hz * n / Rate + phase));
                for (int c = 0; c < channels; c++) a[n * channels + c] = v;
            }
            return a;
        }

        private static float[] Noise(int frames, int seed, double amp = 0.2, int channels = 2, bool sameEars = true)
        {
            var rng = new Random(seed);
            var a = new float[frames * channels];
            for (int n = 0; n < frames; n++)
            {
                float v = (float)((rng.NextDouble() * 2 - 1) * amp);
                for (int c = 0; c < channels; c++) a[n * channels + c] = sameEars || c == 0 ? v : (float)((rng.NextDouble() * 2 - 1) * amp);
            }
            return a;
        }

        /// <summary>
        /// Owner two's shape: <paramref name="prefill"/> frames written first, then
        /// a buffer written and a buffer rendered each step, with the request made
        /// after the prefill. Returns the output, stereo interleaved.
        /// </summary>
        private static float[] Drain(RxLagShifter s, float[] input, int prefill, long request, in LagPolicy policy,
            RxContentClass cls = RxContentClass.Unclassified, Action<RxLagShifter, int> perStep = null)
        {
            var c = cls;
            return Drain(s, input, prefill, request, policy, _ => c, perStep);
        }

        /// <summary>As above, with each block's class from <paramref name="classAt"/> (its first frame).</summary>
        private static float[] Drain(RxLagShifter s, float[] input, int prefill, long request, in LagPolicy policy,
            Func<int, RxContentClass> classAt, Action<RxLagShifter, int> perStep = null)
        {
            int ch = s.Channels;
            int frames = input.Length / ch;
            var output = new List<float>(input.Length);
            for (int at = 0; at < prefill; at += Block)
            {
                int n = Math.Min(Block, prefill - at);
                s.Write(input.AsSpan(at * ch, n * ch), classAt(at));
            }
            s.Request(request, policy, LagCause.LateAudio);
            var buf = new float[Block * ch];
            int step = 0;
            for (int at = prefill; at + Block <= frames; at += Block)
            {
                s.Write(input.AsSpan(at * ch, Block * ch), classAt(at));
                s.Render(buf);
                output.AddRange(buf);
                perStep?.Invoke(s, step++);
            }
            return output.ToArray();
        }

        /// <summary>
        /// Per 1 ms hop on channel c: the tone's phase against a free-running
        /// reference, and its amplitude, over a Hann-windowed 4 ms window (a bare
        /// 1 ms block is not a whole number of periods at 625 or 700 Hz, and its
        /// leakage reads as a phase wobble of a few tenths of a radian).
        /// </summary>
        private static (double[] Phase, double[] Amp) Demod(float[] y, double hz, int c = 0, int channels = 2)
        {
            int frames = y.Length / channels, w = 192, hop = 48;
            int blocks = Math.Max(0, (frames - w) / hop);
            var ph = new double[blocks]; var amp = new double[blocks];
            double wsum = 0;
            for (int k = 0; k < w; k++) wsum += 0.5 - 0.5 * Math.Cos(2 * Math.PI * (k + 0.5) / w);
            for (int b = 0; b < blocks; b++)
            {
                double i = 0, q = 0;
                for (int k = 0; k < w; k++)
                {
                    int n = b * hop + k;
                    double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (k + 0.5) / w);
                    double x = y[n * channels + c] * win;
                    i += x * Math.Sin(2 * Math.PI * hz * n / Rate);
                    q += x * Math.Cos(2 * Math.PI * hz * n / Rate);
                }
                ph[b] = Math.Atan2(q, i);
                amp[b] = 2 * Math.Sqrt(i * i + q * q) / wsum;
            }
            return (ph, amp);
        }

        /// <summary>The largest phase step between consecutive 1 ms blocks, and the deepest envelope dip in dB, after <paramref name="skipBlocks"/>.</summary>
        private static (double Step, double DipDb) PhaseAndDip(float[] y, double hz, int skipBlocks, int c = 0)
        {
            var (ph, amp) = Demod(y, hz, c);
            double step = 0, steady = amp.Skip(skipBlocks).OrderBy(v => v).ElementAt((amp.Length - skipBlocks) / 2);
            double worst = steady;
            for (int b = skipBlocks + 1; b < ph.Length; b++)
            {
                double d = ph[b] - ph[b - 1];
                while (d > Math.PI) d -= 2 * Math.PI;
                while (d < -Math.PI) d += 2 * Math.PI;
                step = Math.Max(step, Math.Abs(d));
                worst = Math.Min(worst, amp[b]);
            }
            return (step, 20 * Math.Log10(steady / Math.Max(worst, 1e-9)));
        }

        // ── Negative control: nothing asked, nothing changed ──

        [Fact]
        public void With_no_request_the_output_is_the_input_sample_for_sample()
        {
            var s = new RxLagShifter(2, Rate * 2, Rate);
            var input = Noise(Rate * 3, 1);
            var output = new List<float>();
            var buf = new float[Block * 2];
            for (int at = 0; at + Block <= Rate * 3; at += Block)
            {
                s.Write(input.AsSpan(at * 2, Block * 2));
                s.Render(buf);
                output.AddRange(buf);
            }
            Assert.Equal(input.Take(output.Count), output);
            Assert.Equal(0, s.ReadsPastWrite);
        }

        [Fact]
        public void A_zero_request_moves_nothing_and_a_real_one_does_positive_control()
        {
            var input = Noise(Rate * 4, 2);
            var zero = Drain(new RxLagShifter(2, Rate * 2), input, Rate / 4, 0, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun);
            // The rendered stream starts at frame 0 of the input: compare directly.
            for (int i = 0; i < zero.Length; i++) Assert.Equal(input[i], zero[i]);
            var s = new RxLagShifter(2, Rate * 2);
            var moved = Drain(s, input, Rate / 4, 4800, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun);
            Assert.True(s.TotalRemoved > 0, "the positive control removed nothing");
            Assert.NotEqual(zero, moved);
        }

        [Fact]
        public void A_fixed_pad_is_exactly_a_plain_delay_line_including_silence_before_the_start()
        {
            var s = new RxLagShifter(2, 12000 + 4096, Rate);
            var input = Noise(Rate, 3);
            var pads = new[] { 0, 960, 3408, 3408, 500, 12000, 1 };
            var history = new List<float>();
            int at = 0, k = 0;
            while (at + Block <= Rate)
            {
                int pad = pads[k++ % pads.Length];
                var buf = input.AsSpan(at * 2, Block * 2).ToArray();
                history.AddRange(buf);
                s.ProcessFixed(buf, pad);
                for (int f = 0; f < Block; f++)
                {
                    long src = at + f - pad;
                    for (int c = 0; c < 2; c++)
                        Assert.Equal(src < 0 ? 0f : history[(int)src * 2 + c], buf[f * 2 + c]);
                }
                at += Block;
            }
        }

        // ── Pitch: whole periods, at 500, 625 and 700 Hz ──

        [Theory]
        [InlineData(500.0)]
        [InlineData(625.0)]
        [InlineData(700.0)]
        public void A_tone_through_splices_shows_no_phase_step(double hz)
        {
            var input = Tone(hz, Rate * 8);
            var s = new RxLagShifter(2, Rate * 2);
            // Labelled confirmed noise, so the class gate would let a gap splice through:
            // only the shifter's own tonal veto keeps the tone off the gap path.
            var y = Drain(s, input, Rate / 2, 9600, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun);
            var (step, dip) = PhaseAndDip(y, hz, 2);
            _out.WriteLine($"{hz} Hz: removed {s.TotalRemoved} in {s.TotalPeriodSplices} period and {s.TotalGapSplices} gap splices; largest phase step {step:0.000} rad, deepest dip {dip:0.00} dB");
            Assert.True(s.TotalPeriodSplices >= 3, "too few splices to test anything");
            Assert.Equal(0, s.TotalGapSplices);           // the tonal veto
            Assert.True(step < 0.25, $"phase step {step:0.000} rad");
            Assert.True(dip < 1.0, $"dip {dip:0.00} dB");
        }

        [Theory]
        [InlineData(500.0)]
        [InlineData(625.0)]
        [InlineData(700.0)]
        public void Positive_control_without_the_whole_period_search_the_same_tone_steps(double hz)
        {
            var input = Tone(hz, Rate * 8);
            var s = new RxLagShifter(2, Rate * 2) { WholePeriodSearch = false };
            var y = Drain(s, input, Rate / 2, 9600, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun);
            var (step, dip) = PhaseAndDip(y, hz, 2);
            _out.WriteLine($"{hz} Hz, no period search: removed {s.TotalRemoved}; largest phase step {step:0.000} rad, deepest dip {dip:0.00} dB");
            Assert.True(s.TotalRemoved > 0);
            Assert.True(step > 0.5, $"the positive control did not step: {step:0.000} rad");
        }

        // ── The tonal veto: a steady 625 Hz carrier ──

        [Fact]
        public void A_steady_625_Hz_carrier_never_goes_down_the_gap_path_and_without_the_veto_it_does()
        {
            var input = Tone(625, Rate * 8);
            var vetoed = new RxLagShifter(2, Rate * 2);
            var y = Drain(vetoed, input, Rate / 2, 9600, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun);
            var (step, dip) = PhaseAndDip(y, 625, 2);
            var open = new RxLagShifter(2, Rate * 2) { TonalVeto = false };
            var y2 = Drain(open, input, Rate / 2, 9600, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun);
            var (step2, dip2) = PhaseAndDip(y2, 625, 2);
            _out.WriteLine($"veto: {vetoed.TotalGapSplices} gap splices, step {step:0.000} rad, dip {dip:0.00} dB; no veto: {open.TotalGapSplices} gap splices, step {step2:0.000} rad, dip {dip2:0.00} dB");
            Assert.Equal(0, vetoed.TotalGapSplices);
            Assert.True(step < 0.25 && dip < 1.0);
            Assert.True(open.TotalGapSplices > 0, "the positive control never took the gap path");
            Assert.True(step2 > 0.5 || dip2 > 6, $"the positive control was clean: step {step2:0.000}, dip {dip2:0.00}");
        }

        [Fact]
        public void A_carrier_the_detector_calls_speech_is_still_tonal_and_never_takes_the_gap_path()
        {
            // The stored v0.1.1 voice branch calls a steady 625 Hz carrier speech
            // (probability 0.999, RxContentClassifierTests). The veto is the
            // shifter's own periodicity, so the class cannot lift it.
            var input = Tone(625, Rate * 8);
            var s = new RxLagShifter(2, Rate * 2);
            var y = Drain(s, input, Rate / 2, 9600, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.Speech | RxContentClass.Active);
            var (step, dip) = PhaseAndDip(y, 625, 2);
            _out.WriteLine($"carrier classed as speech: {s.TotalGapSplices} gap and {s.TotalPeriodSplices} period splices, step {step:0.000} rad, dip {dip:0.00} dB");
            Assert.Equal(0, s.TotalGapSplices);
            Assert.True(s.TotalPeriodSplices > 0);
            Assert.True(step < 0.25 && dip < 1.0);
        }

        // ── Level: noise splices hold level; a linear fade on uncorrelated noise dips 3 dB ──

        [Fact]
        public void Noise_splices_hold_level_within_half_a_decibel_and_a_linear_fade_dips_three()
        {
            var input = Noise(Rate * 12, 7, 0.3);
            var s = new RxLagShifter(2, Rate * 2);
            var spliceAt = new List<long>();
            long lastSplices = 0;
            var y = Drain(s, input, Rate / 2, Rate, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun,
                (sh, step) =>
                {
                    long now = sh.TotalGapSplices + sh.TotalPeriodSplices;
                    if (now != lastSplices) spliceAt.Add((long)(step + 1) * Block - 1); // within this buffer
                    lastSplices = now;
                });
            Assert.True(s.TotalGapSplices >= 10, $"only {s.TotalGapSplices} splices");
            double MeanSq(float[] a, long from, int len) { double e = 0; for (long i = from; i < from + len; i++) e += (double)a[i * 2] * a[i * 2]; return e / len; }
            // Level over each buffer that held a splice against the second before it.
            var deltas = new List<double>();
            foreach (long end in spliceAt)
            {
                long start = end - Block + 1;
                if (start < Rate) continue;
                deltas.Add(10 * Math.Log10(MeanSq(y, start, Block) / MeanSq(y, start - Rate / 2, Rate / 2)));
            }
            double mean = deltas.Average();
            _out.WriteLine($"{deltas.Count} splice buffers, mean level {mean:+0.00;-0.00} dB against the half second before");
            Assert.InRange(mean, -0.5, 0.5);

            // The positive control for the meter: a linear fade between two unrelated noises, at mid-fade.
            var a = Noise(Rate, 11, 0.3); var b = Noise(Rate, 12, 0.3);
            var mid = new float[480 * 2];
            for (int k = 0; k < 480; k++) { float t = (k + 1f) / 480; mid[2 * k] = a[2 * k] * (1 - t) + b[2 * k] * t; }
            double ref0 = MeanSq(a, 0, Rate);
            double centre = 0; for (int k = 160; k < 320; k++) centre += (double)mid[2 * k] * mid[2 * k]; centre /= 160;
            double linearDip = 10 * Math.Log10(ref0 / centre);
            _out.WriteLine($"linear fade, middle third: {linearDip:0.00} dB down");
            Assert.InRange(linearDip, 2.0, 4.0);
        }

        // ── Accounting, budget, look-ahead ──

        [Theory]
        [InlineData(2400)]
        [InlineData(9600)]
        [InlineData(-4800)]
        public void What_is_removed_or_added_matches_the_request_within_one_splice(long request)
        {
            var input = Noise(Rate * 15, 21, 0.2);
            var s = new RxLagShifter(2, Rate * 3);
            Drain(s, input, Rate / 2, request, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun);
            Assert.True(s.TryTakeCompleted(out var e), "the event never ended");
            long done = e.Removed - e.Added;
            _out.WriteLine(e.Describe(Rate));
            Assert.False(e.Abandoned);
            Assert.True(Math.Abs(request - done) < s.MinLagFrames, $"asked {request}, did {done}");
            Assert.Equal(request - done, e.Remainder);
        }

        [Fact]
        public void The_budget_is_never_exceeded_in_any_window()
        {
            var input = Noise(Rate * 20, 31, 0.2);
            var s = new RxLagShifter(2, Rate * 3);
            var removedAt = new List<long>();
            Drain(s, input, Rate, Rate, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun,
                (sh, step) => removedAt.Add(sh.TotalRemoved));
            // Over any window of W buffers: removed <= rate x W x frames + one largest splice of each kind.
            double rate = LagPolicy.CatchUp.GapRate + LagPolicy.CatchUp.PeriodRate;
            int cap = RxLagShifter.MaxGapFramesAt(Rate) + RxLagShifter.MaxLagFramesAt(Rate);
            foreach (int w in new[] { 1, 5, 20, 100 })
                for (int i = w; i < removedAt.Count; i++)
                {
                    long got = removedAt[i] - removedAt[i - w];
                    Assert.True(got <= rate * w * Block + cap + 2 * Block, $"window {w} at {i}: {got}");
                }
            Assert.True(s.TotalRemoved > Rate / 2, "too little was drained to test the budget");
        }

        [Fact]
        public void No_read_ever_passes_the_frames_in_hand_under_random_arrivals_requests_and_dry_spells()
        {
            var rng = new Random(4242);
            var s = new RxLagShifter(2, Rate * 2) { DryFades = true };
            var noise = Noise(Rate * 2, 5, 0.2);
            var tone = Tone(700, Rate * 2);
            var buf = new float[4800 * 2];
            for (int step = 0; step < 4000; step++)
            {
                int arrive = rng.Next(0, 12) * Block;
                var src = rng.Next(2) == 0 ? noise : tone;
                int off = rng.Next(0, Rate) * 2;
                var cls = (RxContentClass)(rng.Next(0, 2) == 0 ? 0 : 1 | (rng.Next(2) * 16) | (rng.Next(4) == 0 ? 32 : 0));
                if (arrive > 0) s.Write(src.AsSpan(off, arrive * 2), cls, seamBefore: rng.Next(20) == 0);
                if (rng.Next(10) == 0) s.Request(rng.Next(-6000, 9000), rng.Next(3) switch { 0 => LagPolicy.EngineSwitch, 1 => LagPolicy.Drift, _ => LagPolicy.CatchUp }, LagCause.LateAudio);
                if (rng.Next(200) == 0) s.RequestCut(s.ReadPosition + rng.Next(0, 4800), rng.Next(480, 9600));
                int render = rng.Next(1, 10) * Block;
                s.Render(buf.AsSpan(0, render * 2));
                Assert.Equal(0, s.ReadsPastWrite);
            }
            _out.WriteLine($"removed {s.TotalRemoved}, added {s.TotalAdded}, cut {s.TotalCutFrames}, seams {s.TotalSeamJoins}, silent {s.SilentFrames}");
            Assert.True(s.TotalRemoved > 0 && s.TotalAdded > 0, "the fuzz never exercised both directions");
        }

        [Fact]
        public void A_drain_never_takes_audio_the_render_still_needs_so_it_never_runs_dry()
        {
            // Owner one's shape: a buffer written, a buffer rendered, a 2,000-frame
            // delay, and a request for far more than is there. The drain may take the
            // delay down to nothing, never further: a removal that ate into what this
            // render still has to play would run the line dry.
            var s = new RxLagShifter(2, Rate * 2);
            var input = Noise(Rate * 20, 91, 0.2);
            var buf = new float[Block * 2];
            s.Write(input.AsSpan(0, 2000 * 2), RxContentClass.Classified | RxContentClass.NotVoiceRun);
            s.Request(Rate, LagPolicy.CatchUp, LagCause.LateAudio);
            for (int at = 2000; at + Block <= Rate * 20; at += Block)
            {
                s.Write(input.AsSpan(at * 2, Block * 2), RxContentClass.Classified | RxContentClass.NotVoiceRun);
                s.Render(buf);
            }
            _out.WriteLine($"removed {s.TotalRemoved} of the 2,000 there were; {s.SilentFrames} silent frames");
            Assert.True(s.TotalRemoved > 1500, "the drain hardly ran");
            Assert.Equal(0, s.SilentFrames);
            Assert.Equal(0, s.ReadsPastWrite);
        }

        // ── The classes govern the gap path (Codex's check of 2567a596, B2) ──

        /// <summary>
        /// Stationary broadband noise, identical in every case, with one class
        /// written for all of it, and a 14,400-frame catch-up asked: Codex's probe.
        /// Energy alone calls every block a gap, so only the class can tell them apart.
        /// </summary>
        private static RxLagShifter GapRun(RxContentClass cls)
        {
            var s = new RxLagShifter(2, 96000);
            long at = 0;
            var chunk = new float[960];
            var output = new float[960];
            void Write()
            {
                for (int i = 0; i < 480; i++, at++)
                {
                    ulong x = (ulong)at * 0x9E3779B97F4A7C15UL; x ^= x >> 29; x *= 0xBF58476D1CE4E5B9UL;
                    chunk[2 * i] = chunk[2 * i + 1] = ((x & 65535) / 65535f - 0.5f) * 0.3f;
                }
                s.Write(chunk, cls);
            }
            for (int i = 0; i < 50; i++) Write();
            s.Request(14400, LagPolicy.CatchUp, LagCause.LateAudio);
            for (int i = 0; i < 200; i++) { Write(); s.Render(output); }
            return s;
        }

        [Fact]
        public void Only_confirmed_noise_takes_the_fast_gap_path_and_speech_and_unclassified_audio_never_do()
        {
            const RxContentClass C = RxContentClass.Classified;
            var noise = GapRun(C | RxContentClass.NotVoiceRun);
            var speech = GapRun(C | RxContentClass.Speech | RxContentClass.Active);
            var unclassified = GapRun(RxContentClass.Unclassified);
            var shortPause = GapRun(C);                                  // not voice, but not yet for 100 ms
            var uncertain = GapRun(C | RxContentClass.NotVoiceRun | RxContentClass.Uncertain);
            foreach (var (name, sh) in new[] { ("confirmed noise", noise), ("speech", speech), ("unclassified", unclassified), ("a pause under 100 ms", shortPause), ("noise, voice probability 0.1 to 0.3", uncertain) })
                _out.WriteLine($"{name}: removed {sh.TotalRemoved} in {sh.TotalGapSplices} gap and {sh.TotalPeriodSplices} period splices");
            // Positive control: the same audio really reaches the gap path, and drains.
            Assert.Equal(14400, noise.TotalRemoved);
            Assert.True(noise.TotalGapSplices > 0);
            // Speech and unclassified audio are voice: never the fast noise policy (brief,
            // item 2: "treat audio as voice ... never the fast noise policy by default").
            Assert.Equal(0, speech.TotalGapSplices);
            Assert.Equal(0, unclassified.TotalGapSplices);
            // The fast policy only after 100 ms of consecutive not-voice.
            Assert.Equal(0, shortPause.TotalGapSplices);
            // Noise with a voice probability between 0.1 and 0.3 still drains by gaps.
            Assert.True(uncertain.TotalGapSplices > 0);
        }

        [Fact]
        public void A_speech_block_inside_a_quiet_span_stops_a_gap_splice_across_it()
        {
            // Confirmed noise everywhere except one 10 ms block the detector called
            // speech in each 200 ms: no gap splice may span a speech block.
            const RxContentClass NoiseClass = RxContentClass.Classified | RxContentClass.NotVoiceRun;
            var s = new RxLagShifter(2, 96000);
            var input = Noise(Rate * 6, 77, 0.1);
            var buf = new float[Block * 2];
            int blocks = 0;
            RxContentClass ClassOf(int b) => b % 20 == 10 ? RxContentClass.Classified | RxContentClass.Speech : NoiseClass;
            for (; blocks < 50; blocks++) s.Write(input.AsSpan(blocks * Block * 2, Block * 2), ClassOf(blocks));
            s.Request(Rate, LagPolicy.CatchUp, LagCause.LateAudio);
            s.LogSplices = true;
            for (; (blocks + 1) * Block * 2 <= input.Length; blocks++) { s.Write(input.AsSpan(blocks * Block * 2, Block * 2), ClassOf(blocks)); s.Render(buf); }
            Assert.True(s.TotalGapSplices > 0, "no gap splice at all, so nothing was tested");
            for (int i = 0; i < Math.Min(s.SpliceLogCount, s.SpliceLog.Length); i++)
            {
                var r = s.SpliceLog[i];
                if (r.Kind != 1) continue;
                long from = Math.Min(r.OldSide, r.NewSide), to = Math.Max(r.OldSide, r.NewSide) + r.Fade;
                for (long b = from / Block; b * Block < to; b++)
                    Assert.False(b % 20 == 10, $"gap splice {r} spans the speech block {b}");
            }
        }

        [Fact]
        public void CW_key_ups_still_take_gap_splices_with_or_without_a_detector()
        {
            // The slice mode says CW even when no detector loaded: key-ups are found by
            // energy, and the tonal veto guards the key-downs.
            foreach (var cls in new[] { RxContentClass.Cw, RxContentClass.Cw | RxContentClass.Classified })
            {
                var s = new RxLagShifter(2, 96000);
                var y = new float[Rate * 8 * 2];
                var rng = new Random(3);
                for (int n = 0; n < Rate * 8; n++)
                {
                    bool down = n % 17280 < 2880;      // 60 ms elements, 300 ms word spaces (over the 150 ms pause floor)
                    y[2 * n] = y[2 * n + 1] = down ? (float)(0.4 * Math.Sin(2 * Math.PI * 625 * n / Rate)) : (float)((rng.NextDouble() - 0.5) * 0.002);
                }
                var buf = new float[Block * 2];
                int b = 0;
                for (; b < 50; b++) s.Write(y.AsSpan(b * Block * 2, Block * 2), cls);
                s.Request(Rate / 2, LagPolicy.CatchUp, LagCause.LateAudio);
                for (; (b + 1) * Block * 2 <= y.Length; b++) { s.Write(y.AsSpan(b * Block * 2, Block * 2), cls); s.Render(buf); }
                _out.WriteLine($"{cls}: {s.TotalGapSplices} gap splices, removed {s.TotalRemoved}");
                Assert.True(s.TotalGapSplices > 0, $"{cls}: no key-up was shortened");
            }
        }

        // ── Growth: no region repeated twice ──

        [Fact]
        public void Growth_never_repeats_a_region_twice()
        {
            // A ramp names every sample; a full-weight output sample (outside a
            // fade) is an exact multiple of the step. Counted per index.
            const float stepSize = 1f / 4096;
            int frames = Rate * 10;
            var input = new float[frames * 2];
            for (int n = 0; n < frames; n++) input[2 * n] = input[2 * n + 1] = (n + 200000) * stepSize;
            var s = new RxLagShifter(2, Rate * 3) { TonalVeto = false };
            var y = Drain(s, input, Rate / 4, -Rate, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun);
            var counts = new Dictionary<long, int>();
            for (int i = 0; i < y.Length / 2; i++)
            {
                double idx = y[2 * i] / stepSize - 200000;
                if (Math.Abs(idx - Math.Round(idx)) > 1e-3) continue; // inside a fade
                long k = (long)Math.Round(idx);
                counts[k] = counts.TryGetValue(k, out int v) ? v + 1 : 1;
            }
            int most = counts.Values.Max();
            _out.WriteLine($"added {s.TotalAdded} in {s.TotalGapSplices + s.TotalPeriodSplices} splices; the most any sample was played at full weight: {most}");
            Assert.True(s.TotalAdded > Rate / 4, "too little growth to test");
            Assert.True(most <= 2, $"a region was repeated twice (played {most} times)");
        }

        // ── Stereo ──

        [Fact]
        public void Identical_ears_in_give_identical_ears_out()
        {
            var input = Noise(Rate * 8, 41, 0.2);
            var s = new RxLagShifter(2, Rate * 2);
            var y = Drain(s, input, Rate / 2, 9600, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun);
            Assert.True(s.TotalRemoved > 0);
            for (int i = 0; i < y.Length; i += 2) Assert.Equal(y[i], y[i + 1]);
        }

        [Fact]
        public void Different_material_in_each_ear_gets_one_splice_point_and_each_ear_stays_clean()
        {
            // A carrier in the left ear, band noise in the right: the carrier
            // decides the splice point (the noise is at its own floor), and the
            // noise ear takes the same splice with its own matched fade.
            int frames = Rate * 10;
            var l = Tone(625, frames, channels: 1); var r = Noise(frames, 77, 0.2, channels: 1);
            var input = new float[frames * 2];
            for (int n = 0; n < frames; n++) { input[2 * n] = l[n]; input[2 * n + 1] = r[n]; }
            var s = new RxLagShifter(2, Rate * 2);
            var y = Drain(s, input, Rate / 2, 9600, LagPolicy.CatchUp);
            var (stepL, dipL) = PhaseAndDip(y, 625, 2, 0);
            double MeanSq(int from, int len) { double e = 0; for (int i = from; i < from + len; i++) e += (double)y[2 * i + 1] * y[2 * i + 1]; return e / len; }
            double whole = MeanSq(Rate / 2, y.Length / 2 - Rate / 2), worst = 0;
            for (int b = Rate / 2; b + 4800 <= y.Length / 2; b += 4800) worst = Math.Max(worst, Math.Abs(10 * Math.Log10(MeanSq(b, 4800) / whole)));
            _out.WriteLine($"removed {s.TotalRemoved} in {s.TotalPeriodSplices} period and {s.TotalGapSplices} gap splices; left 625 Hz step {stepL:0.000} dip {dipL:0.00}; right noise level within {worst:0.00} dB per 100 ms");
            Assert.True(s.TotalPeriodSplices > 0);
            Assert.Equal(0, s.TotalGapSplices); // the carrier vetoes the gap path for both ears
            Assert.True(stepL < 0.25 && dipL < 1, "the carrier ear stepped");
            Assert.True(worst < 1.0, $"the noise ear's level moved {worst:0.00} dB");
        }

        [Fact]
        public void Two_different_carriers_wait_rather_than_step_either_ear()
        {
            // 500 Hz left and 700 Hz right share no period up to 5 ms, so no splice
            // is clean for both: the drain waits. Slower, never a step.
            int frames = Rate * 6;
            var l = Tone(500, frames, channels: 1); var r = Tone(700, frames, channels: 1);
            var input = new float[frames * 2];
            for (int n = 0; n < frames; n++) { input[2 * n] = l[n]; input[2 * n + 1] = r[n]; }
            var s = new RxLagShifter(2, Rate * 2);
            var y = Drain(s, input, Rate / 2, 9600, LagPolicy.CatchUp);
            var (stepL, _) = PhaseAndDip(y, 500, 2, 0);
            var (stepR, _) = PhaseAndDip(y, 700, 2, 1);
            _out.WriteLine($"removed {s.TotalRemoved}; left step {stepL:0.000}, right step {stepR:0.000}");
            Assert.True(stepL < 0.25 && stepR < 0.25);
        }

        // ── The pause floor, and the digital modes ──

        [Fact]
        public void No_pause_is_shortened_below_150_ms()
        {
            // Bursts of a 300 Hz buzz (not a carrier: amplitude modulated) between
            // pauses of 80 to 400 ms of quiet noise.
            var rng = new Random(9);
            var x = new List<float>();
            var cls = new List<RxContentClass>();
            var pauses = new[] { 80, 120, 160, 200, 260, 320, 400 };
            for (int k = 0; k < 40; k++)
            {
                int on = 200 * 48;
                for (int n = 0; n < on; n++)
                {
                    float v = (float)(0.4 * Math.Sin(2 * Math.PI * 300 * n / Rate) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 7 * n / Rate)) + (rng.NextDouble() - 0.5) * 0.1);
                    x.Add(v); x.Add(v);
                    cls.Add(RxContentClass.Classified | RxContentClass.Speech | RxContentClass.Active);
                }
                int off = pauses[k % pauses.Length] * 48;
                for (int n = 0; n < off; n++)
                {
                    float v = (float)((rng.NextDouble() - 0.5) * 0.004); x.Add(v); x.Add(v);
                    // As the classifier labels a pause: speech held for the 80 ms hangover,
                    // then not-voice, and the fast noise policy only from 100 ms after that.
                    cls.Add(n < 80 * 48 ? RxContentClass.Classified | RxContentClass.Speech
                        : n < 180 * 48 ? RxContentClass.Classified : RxContentClass.Classified | RxContentClass.NotVoiceRun);
                }
            }
            var input = x.ToArray();
            var s = new RxLagShifter(2, Rate * 3);
            var y = Drain(s, input, Rate / 2, Rate, LagPolicy.CatchUp, at => cls[at]);
            // Quiet runs in the output, in 1 ms blocks.
            var runs = new List<int>(); int run = 0;
            for (int b = 0; b + 48 <= y.Length / 2; b += 48)
            {
                double e = 0; for (int k = 0; k < 48; k++) e += (double)y[2 * (b + k)] * y[2 * (b + k)];
                bool quiet = e / 48 < 1e-4;
                if (quiet) run++; else { if (run > 0) runs.Add(run); run = 0; }
            }
            _out.WriteLine($"removed {s.TotalRemoved} in {s.TotalGapSplices} gap splices; output pauses (ms): {string.Join(", ", runs.Take(40))}");
            Assert.True(s.TotalGapSplices > 0, "no pause was shortened at all");
            // Every pause that was 150 ms or longer is still at least 150 ms (a millisecond of measuring slack);
            // every shorter one is untouched (its length is one of the input's).
            foreach (int ms in runs.Skip(1)) Assert.True(ms >= 148 || pauses.Any(p => Math.Abs(p - ms) <= 2), $"a {ms} ms pause");
        }

        [Fact]
        public void A_digital_mode_gets_no_splice_at_all_and_a_voice_mode_does()
        {
            var input = Noise(Rate * 6, 51, 0.2);
            var digital = new RxLagShifter(2, Rate * 2);
            var y = Drain(digital, input, Rate / 2, 9600, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun | RxContentClass.NoSplice);
            Assert.Equal(0, digital.TotalRemoved + digital.TotalAdded);
            for (int i = 0; i < y.Length; i++) Assert.Equal(input[i], y[i]);
            var voice = new RxLagShifter(2, Rate * 2);
            Drain(voice, input, Rate / 2, 9600, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.NotVoiceRun);
            Assert.True(voice.TotalRemoved > 0);
        }

        // ── CW timing ──

        [Fact]
        public void CW_elements_change_by_no_more_than_5_ms_at_the_tonal_gate()
        {
            // 20 WPM: a 60 ms dit. "PARIS " repeated, keyed at 700 Hz with 5 ms edges, over faint noise.
            string paris = ".--.|.-|.-.|..|...| ";
            var keyed = new List<bool>();
            int unit = 60 * 48;
            for (int rep = 0; rep < 4; rep++)
                foreach (char ch in paris)
                {
                    if (ch == '.') { keyed.AddRange(Enumerable.Repeat(true, unit)); keyed.AddRange(Enumerable.Repeat(false, unit)); }
                    else if (ch == '-') { keyed.AddRange(Enumerable.Repeat(true, 3 * unit)); keyed.AddRange(Enumerable.Repeat(false, unit)); }
                    else if (ch == '|') keyed.AddRange(Enumerable.Repeat(false, 2 * unit));
                    else keyed.AddRange(Enumerable.Repeat(false, 4 * unit));
                }
            int frames = keyed.Count;
            var rng = new Random(3);
            var input = new float[frames * 2];
            double env = 0;
            for (int n = 0; n < frames; n++)
            {
                env += ((keyed[n] ? 1.0 : 0.0) - env) / (5 * 48 / 4.0);
                float v = (float)(0.5 * env * Math.Sin(2 * Math.PI * 700 * n / Rate) + (rng.NextDouble() - 0.5) * 0.002);
                input[2 * n] = input[2 * n + 1] = v;
            }
            var s = new RxLagShifter(2, Rate * 3);
            var y = Drain(s, input, Rate / 2, 9600, LagPolicy.CatchUp, RxContentClass.Classified | RxContentClass.Cw);
            // Element lengths at sample resolution: a 1 ms envelope against a third of the keyed level.
            List<double> Elements(float[] a)
            {
                var lens = new List<double>(); int len = 0; double envl = 0;
                for (int i = 0; i < a.Length / 2; i++)
                {
                    envl += (Math.Abs(a[2 * i]) - envl) / 48.0;
                    bool on = envl > 0.1;
                    if (on) len++; else { if (len > 0) lens.Add(len / 48.0); len = 0; }
                }
                return lens;
            }
            var before = Elements(input);
            var after = Elements(y);
            int n2 = Math.Min(before.Count, after.Count);
            double worst = 0;
            for (int i = 0; i < n2; i++) worst = Math.Max(worst, Math.Abs(before[i] - after[i]));
            _out.WriteLine($"removed {s.TotalRemoved} in {s.TotalPeriodSplices} period and {s.TotalGapSplices} gap splices; {n2} elements compared, worst change {worst:0.00} ms");
            Assert.True(s.TotalRemoved > 0);
            Assert.True(worst <= 5.0, $"an element changed by {worst:0.00} ms");
        }

        // ── Allocation ──

        [Fact]
        public void Writing_rendering_and_splicing_allocate_nothing()
        {
            var input = Noise(Rate * 6, 61, 0.2);
            var s = new RxLagShifter(2, Rate * 2) { DryFades = true };
            var buf = new float[Block * 2];
            s.Write(input.AsSpan(0, Rate), RxContentClass.Classified | RxContentClass.NotVoiceRun);
            s.Request(9600, LagPolicy.CatchUp, LagCause.LateAudio);
            for (int k = 0; k < 50; k++) { s.Write(input.AsSpan(Rate + k * Block * 2, Block * 2), RxContentClass.Classified | RxContentClass.NotVoiceRun); s.Render(buf); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int k = 50; k < 300; k++)
            {
                s.Write(input.AsSpan(Rate + k * Block * 2, Block * 2), RxContentClass.Classified | RxContentClass.NotVoiceRun, seamBefore: k % 40 == 0);
                s.Render(buf);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            long removed = s.TotalRemoved;
            Assert.Equal(0, allocated);
            Assert.True(removed > 0, "nothing was spliced while counting");
        }

        // ── The seam join ──

        [Theory]
        [InlineData(500.0)]
        [InlineData(625.0)]
        [InlineData(700.0)]
        public void A_seam_is_joined_without_a_step_and_an_unmarked_one_steps(double hz)
        {
            // About 60 ms lost, chosen so the audio after the loss is 0.4 of a
            // period out of phase with the audio before: a splice there is a
            // 2.5 radian step, the click #1155 describes.
            int frames = Rate * 2;
            double period = Rate / hz;
            int missing = (int)Math.Round((Math.Round(2880 / period) + 0.4) * period);
            var tone = Tone(hz, frames + missing);
            float[] Run(bool mark)
            {
                var s = new RxLagShifter(2, Rate * 2);
                var o = new List<float>();
                var buf = new float[Block * 2];
                int seamAt = Rate;
                for (int at = 0; at + Block <= frames; at += Block)
                {
                    int src = at < seamAt ? at : at + missing;
                    s.Write(tone.AsSpan(src * 2, Block * 2), RxContentClass.Unclassified, seamBefore: mark && at == seamAt);
                    if (at >= 3 * Block) { s.Render(buf); o.AddRange(buf); }
                }
                Assert.Equal(mark ? 1 : 0, s.TotalSeamJoins);
                return o.ToArray();
            }
            var (stepJ, dipJ) = PhaseAndDip(Run(true), hz, 2);
            var (stepS, dipS) = PhaseAndDip(Run(false), hz, 2);
            _out.WriteLine($"{hz} Hz, {missing} frames lost: joined: phase step {stepJ:0.000} rad, dip {dipJ:0.00} dB; unmarked: phase step {stepS:0.000} rad, dip {dipS:0.00} dB");
            Assert.True(stepJ < 0.25, $"the join stepped {stepJ:0.000} rad");
            Assert.True(dipJ < 1.0, $"the join dipped {dipJ:0.00} dB");
            Assert.True(stepS > 1.0, $"the positive control did not step: {stepS:0.000} rad");
        }
    }
}
