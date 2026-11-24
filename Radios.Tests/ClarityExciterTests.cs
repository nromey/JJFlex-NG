using System;
using System.Linq;
using JJPortaudio;
using Radios.Listenback;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track J (#736): JJ Clarity's exciter, numerically. Radio-neutral
    /// and windowless: nothing here touches a FlexBase, an audio device or a window.
    /// </summary>
    public class ClarityExciterTests
    {
        private readonly ITestOutputHelper _out;
        public ClarityExciterTests(ITestOutputHelper output) { _out = output; }

        public static readonly uint[] Rates = { 48000, 24000, 16000, 12000, 8000 };
        public static TheoryData<uint> AllRates() { var d = new TheoryData<uint>(); foreach (var r in Rates) d.Add(r); return d; }

        // ── signals and measurement ──

        private static float[] Tone(double hz, double amp, int frames, uint rate, double phase = 0)
        {
            var b = new float[frames * 2];
            for (int f = 0; f < frames; f++)
                b[2 * f] = b[2 * f + 1] = (float)(amp * Math.Sin(phase + 2 * Math.PI * hz * f / rate));
            return b;
        }

        private static float[] Noise(double amp, int frames, int seed)
        {
            var r = new Random(seed);
            var b = new float[frames * 2];
            for (int f = 0; f < frames; f++) b[2 * f] = b[2 * f + 1] = (float)(amp * (2 * r.NextDouble() - 1));
            return b;
        }

        /// <summary>Run a whole signal through in 10 ms buffers; returns the output. Optional hook runs before each buffer with its first frame.</summary>
        private static float[] Run(ClarityExciter c, float[] signal, uint rate, Action<int> before = null, bool countStats = true)
        {
            var output = (float[])signal.Clone();
            int block = (int)rate / 100 * 2;
            var buf = new float[block];
            for (int s = 0; s < output.Length; s += block)
            {
                before?.Invoke(s / 2);
                int n = Math.Min(block, output.Length - s);
                Array.Copy(output, s, buf, 0, n);
                c.Process(buf, n, rate, countStats);
                Array.Copy(buf, 0, output, s, n);
            }
            return output;
        }

        private static double[] Left(float[] stereo, int from = 0, int count = -1)
        {
            int frames = stereo.Length / 2;
            if (count < 0) count = frames - from;
            var m = new double[count];
            for (int i = 0; i < count; i++) m[i] = stereo[2 * (from + i)];
            return m;
        }

        /// <summary>The amplitude of a sinusoid at <paramref name="hz"/> in <paramref name="x"/>, Hann-windowed (exact for a tone on the frequency).</summary>
        private static double Amplitude(double[] x, double hz, double rate)
        {
            int n = x.Length;
            double re = 0, im = 0, wsum = 0;
            for (int i = 0; i < n; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
                double a = 2 * Math.PI * hz * i / rate;
                re += x[i] * w * Math.Cos(a); im -= x[i] * w * Math.Sin(a);
                wsum += w;
            }
            return 2 * Math.Sqrt(re * re + im * im) / wsum;
        }

        private static double Db(double v) => 20 * Math.Log10(Math.Max(1e-15, v));

        private static ClaritySettings High(float tune, float drive, float mix, ClarityHarmonics h) =>
            new ClaritySettings(new ClarityBand(true, tune, drive, mix, h), ClaritySettings.Default.Low);

        private static ClarityExciter Make(ClaritySettings s, uint rate, float low = 0, float high = 0)
        {
            var c = new ClarityExciter { Enabled = true };
            c.Use(ClarityDesign.Create(s, low, high, rate));
            return c;
        }

        // ── identity ──

        [Theory]
        [MemberData(nameof(AllRates))]
        public void Off_each_band_off_and_mix_at_zero_are_bit_identical_and_drive_with_mix_changes_it(uint rate)
        {
            var input = Noise(0.4, (int)rate / 2, 11);
            // Negative zeros: adding a zero to one gives a positive zero, so only a
            // stage that truly leaves the samples alone keeps these bits.
            for (int i = 0; i < 64; i += 2) input[i] = input[i + 1] = -0.0f;
            var active = High(2000, 12, 50, ClarityHarmonics.Both).WithLow(new ClarityBand(true, 150, 6, 30, ClarityHarmonics.Even));
            var cases = new (string name, ClarityExciter c)[]
            {
                ("stage off", new Func<ClarityExciter>(() => { var c = Make(active, rate); c.Enabled = false; return c; })()),
                ("both bands off", Make(active.WithHigh(active.High.WithEnabled(false)).WithLow(active.Low.WithEnabled(false)), rate)),
                ("both mixes zero", Make(active.WithHigh(active.High.WithMix(0)).WithLow(active.Low.WithMix(0)), rate)),
                ("nothing published", new ClarityExciter { Enabled = true }),
            };
            foreach (var (name, c) in cases)
            {
                var output = Run(c, input, rate);
                for (int i = 0; i < input.Length; i++)
                    Assert.True(BitConverter.SingleToInt32Bits(input[i]) == BitConverter.SingleToInt32Bits(output[i]),
                        $"{name} at {rate} Hz changed sample {i}");
            }
            // The positive control: drive above zero with the mix up changes the output.
            var on = Run(Make(active, rate), input, rate);
            Assert.True(Enumerable.Range(0, input.Length).Count(i => input[i] != on[i]) > input.Length / 2, $"Clarity on did nothing at {rate} Hz");
        }

        // ── harmonics ──

        [Theory]
        [InlineData(ClarityHarmonics.Even)]
        [InlineData(ClarityHarmonics.Odd)]
        public void A_tone_through_the_high_band_gets_its_second_or_third_harmonic_at_the_expected_level(ClarityHarmonics h)
        {
            const uint rate = 48000;
            const double f = 1500, amp = 0.05;
            float tune = h == ClarityHarmonics.Even ? 2000 : 3000; // input band starts at 1000 Hz either way
            var settings = High(tune, 6, 100, h);
            var design = ClarityDesign.Create(settings, 0, 0, rate);
            var c = new ClarityExciter { Enabled = true };
            c.Use(design);
            var output = Run(c, Tone(f, amp, (int)rate, rate), rate);
            var x = Left(output, (int)rate / 4, (int)rate / 2);

            // The law: with drive gain g and band amplitude A (the input filter's gain applied),
            // u = g A; the second harmonic is u times A, the third u squared times A;
            // then the added path's filter at the harmonic. The level control does not act (g A < 1).
            double g = Math.Pow(10, 6 / 20.0);
            double band = amp * Math.Pow(10, design.InputGainDb(true, f) / 20);
            double u = g * band;
            Assert.True(u < 1);
            int order = h == ClarityHarmonics.Even ? 2 : 3;
            double expected = (order == 2 ? u : u * u) * band * Math.Pow(10, design.AddedPathGainDb(true, order * f) / 20);
            double got = Amplitude(x, order * f, rate);
            _out.WriteLine($"{h}: harmonic {order} expected {Db(expected):F2} dBFS, got {Db(got):F2} dBFS");
            Assert.InRange(Db(got) - Db(expected), -0.5, 0.5);

            // And the other order is not made.
            int other = order == 2 ? 3 : 2;
            Assert.True(Db(Amplitude(x, other * f, rate)) < Db(got) - 40, $"{h} made harmonic {other} as well");
        }

        // ── fold-back ──

        [Theory]
        [MemberData(nameof(AllRates))]
        public void Nothing_folds_back_into_the_passband_at_any_rate_and_the_test_sees_fold_back_without_the_band_limit(uint rate)
        {
            // A tone placed so that a naive third-order shaper's harmonic folds to 2.6 kHz
            // (at 8 kHz: 1.8 kHz, whose 5.4 kHz third harmonic folds to 2.6 kHz).
            const double fold = 2600;
            double f = (rate - fold) / 3.0;
            var settings = High(2000, 24, 100, ClarityHarmonics.Odd);
            var input = Tone(f, 0.5, (int)rate, rate);

            double Folded(ClarityDesign d)
            {
                var c = new ClarityExciter { Enabled = true };
                c.Use(d);
                var x = Left(Run(c, input, rate), (int)rate / 4, (int)rate / 2);
                return Db(Amplitude(x, fold, rate)) - Db(0.5);
            }

            double guarded = Folded(ClarityDesign.Create(settings, 0, 0, rate));
            double naive = Folded(ClarityDesign.CreateWithoutInputBandLimitForTest(settings, 0, 0, rate));
            _out.WriteLine($"{rate} Hz: tone {f:F0} Hz, fold-back at {fold} Hz: {guarded:F1} dBc band-limited, {naive:F1} dBc without the limit");
            Assert.True(guarded < -60, $"fold-back {guarded:F1} dBc at {rate} Hz");
            Assert.True(naive > -30, $"the positive control did not show fold-back at {rate} Hz ({naive:F1} dBc)");
        }

        // ── the passband ──

        [Fact]
        public void With_the_high_edge_set_low_the_tune_clamps_the_intent_stays_and_almost_nothing_is_added_above_the_edge()
        {
            const uint rate = 48000;
            const float edge = 2000;
            var settings = High(3000, 12, 60, ClarityHarmonics.Even);
            var c = Make(settings, rate, 0, edge);
            Assert.True(c.HighTuneClamped);
            Assert.Equal(1600f, c.AppliedHighTuneHz, 3);
            Assert.Equal(3000f, c.Settings.High.TuneHz);
            Assert.Equal(3000f, c.Wanted.Settings.High.TuneHz);

            var input = Noise(0.3, (int)rate * 2, 5);
            var output = Run(c, input, rate);
            int from = (int)rate / 4, n = 65536;
            var added = new double[n];
            for (int i = 0; i < n; i++) added[i] = output[2 * (from + i)] - input[2 * (from + i)];
            var (above, total) = PowerAbove(added, rate, edge * 1.1);
            double ratio = 10 * Math.Log10(above / total);
            _out.WriteLine($"added energy above {edge * 1.1} Hz: {ratio:F1} dB of the total added");
            Assert.True(ratio < -25, $"Clarity added {ratio:F1} dB of its energy above the passband");
            // With no edge the same setting adds well above 2.2 kHz: the measurement can see it.
            var free = Run(Make(settings, rate), input, rate);
            for (int i = 0; i < n; i++) added[i] = free[2 * (from + i)] - input[2 * (from + i)];
            var (a2, t2) = PowerAbove(added, rate, edge * 1.1);
            Assert.True(10 * Math.Log10(a2 / t2) > -3);
        }

        private static (double Above, double Total) PowerAbove(double[] x, double rate, double hz)
        {
            var re = new double[x.Length];
            var im = new double[x.Length];
            for (int i = 0; i < x.Length; i++) re[i] = x[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / x.Length));
            Radix2Fft.Forward(re, im);
            double above = 0, total = 0;
            for (int k = 1; k < x.Length / 2; k++)
            {
                double p = re[k] * re[k] + im[k] * im[k];
                total += p;
                if (k * rate / x.Length > hz) above += p;
            }
            return (above, total);
        }

        // ── DC ──

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_even_setting_adds_no_DC(bool highBand)
        {
            const uint rate = 48000;
            var s = highBand
                ? High(2000, 12, 100, ClarityHarmonics.Even)
                : new ClaritySettings(ClaritySettings.Default.High.WithEnabled(false), new ClarityBand(true, 150, 12, 100, ClarityHarmonics.Even));
            double f = highBand ? 1500 : 100;
            var input = Tone(f, 0.3, (int)rate * 2, rate);
            var output = Run(Make(s, rate), input, rate);
            int from = (int)rate / 2, n = (int)rate;
            double mean = 0, sq = 0;
            for (int i = 0; i < n; i++) { double a = output[2 * (from + i)] - input[2 * (from + i)]; mean += a; sq += a * a; }
            mean /= n;
            double rms = Math.Sqrt(sq / n);
            _out.WriteLine($"{(highBand ? "high" : "low")} band even: added RMS {Db(rms):F1} dBFS, DC {mean:E2}");
            Assert.True(rms > 0.01, "the even setting added nothing to measure");
            Assert.True(Math.Abs(mean) < 1e-4 && Math.Abs(mean) < 1e-3 * rms, $"DC {mean:E2} against an added RMS of {rms:E2}");
        }

        // ── no click ──

        [Fact]
        public void A_lever_changed_mid_tone_fades_with_no_step_larger_than_the_signals_own()
        {
            const uint rate = 48000;
            const double f = 1200;
            var before = High(2000, 12, 20, ClarityHarmonics.Even);
            var after = High(2000, 12, 80, ClarityHarmonics.Even);
            var c = Make(before, rate);
            int change = (int)rate / 2; // on a buffer boundary
            var afterDesign = ClarityDesign.Create(after, 0, 0, rate);
            var input = Tone(f, 0.3, (int)rate, rate);
            var output = Run(c, input, rate, frame => { if (frame == change) c.Use(afterDesign); });
            var y = Left(output);

            double MaxStep(int from, int to) { double m = 0; for (int i = Math.Max(1, from); i < to; i++) m = Math.Max(m, Math.Abs(y[i] - y[i - 1])); return m; }
            double steady = Math.Max(MaxStep(change - 4800, change), MaxStep(change + 4800, change + 9600));
            double around = MaxStep(change - 10, change + 2000);
            _out.WriteLine($"largest step: steady {steady:F4}, around the change {around:F4}");
            Assert.True(around <= steady * 1.02, $"a step of {around:F4} at the change against {steady:F4} steady");

            // The fade is a fade: 5 ms in, the added harmonic is between the old level and the new.
            double Harmonic(int from) { var seg = new double[480]; for (int i = 0; i < 480; i++) seg[i] = y[from + i] - input[2 * (from + i)]; return Amplitude(seg, 2 * f, rate); }
            double oldLevel = Harmonic(change - 960), newLevel = Harmonic(change + 4800), mid = Harmonic(change); // 0 to 10 ms in: centred 5 ms into the 20 ms fade
            _out.WriteLine($"second harmonic: old {Db(oldLevel):F1}, 5 ms into the fade {Db(mid):F1}, new {Db(newLevel):F1} dBFS");
            Assert.True(mid > oldLevel * 1.2 && mid < newLevel * 0.85, "the change did not fade");
        }

        [Fact]
        public void Switching_off_mid_tone_fades_out_and_then_the_output_is_the_input_exactly()
        {
            const uint rate = 48000;
            var c = Make(High(2000, 12, 60, ClarityHarmonics.Even), rate);
            var input = Tone(1200, 0.3, (int)rate, rate);
            int change = (int)rate / 2;
            var output = Run(c, input, rate, frame => { if (frame == change) c.Enabled = false; });
            var y = Left(output);
            double MaxStep(int from, int to) { double m = 0; for (int i = from; i < to; i++) m = Math.Max(m, Math.Abs(y[i] - y[i - 1])); return m; }
            Assert.True(MaxStep(change - 10, change + 2000) <= MaxStep(change - 4800, change) * 1.02);
            int fade = (int)(ClarityExciter.CrossFadeMs * 0.001 * rate);
            // It is a fade: halfway through, Clarity is still adding something.
            double still = 0;
            for (int i = change + fade / 2 - 48; i < change + fade / 2 + 48; i++) still = Math.Max(still, Math.Abs(y[i] - input[2 * i]));
            Assert.True(still > 0.01, "switching off cut the added path instead of fading it");
            for (int i = 2 * (change + fade); i < input.Length; i++)
                Assert.True(BitConverter.SingleToInt32Bits(input[i]) == BitConverter.SingleToInt32Bits(output[i]), $"sample {i} after the fade out");
        }

        // ── readouts (repair 1, B3) ──

        [Fact]
        public void The_clamp_readouts_use_the_rate_in_force_not_the_rate_the_design_was_made_for()
        {
            // Codex's case: tune 8 kHz, even, edges 100 to 10,000 Hz, published at 48 kHz, run at 8 kHz.
            var c = new ClarityExciter { Enabled = true };
            c.Use(ClarityDesign.Create(High(8000, 12, 30, ClarityHarmonics.Even), 100, 10000, 48000));
            Assert.Equal(8000f, c.AppliedHighTuneHz);   // at 48 kHz, nothing clamps
            Assert.False(c.HighTuneClamped);
            var buf = Tone(1000, 0.1, 80, 8000);
            c.Process(buf, buf.Length, 8000);
            Assert.Equal(8000u, c.InForceRate);
            Assert.Equal(2400f, c.AppliedHighTuneHz, 2); // 0.8 of 0.375 x 8000
            Assert.True(c.HighTuneClamped);
            Assert.Equal(8000f, c.Settings.High.TuneHz);
            Assert.False(c.LowTuneClamped);
        }

        [Fact]
        public void The_adding_now_reading_falls_to_nothing_once_the_stage_has_faded_off_and_idle_buffers_stay_exact()
        {
            const uint rate = 48000;
            var c = Make(High(2000, 12, 60, ClarityHarmonics.Even), rate);
            Run(c, Tone(1200, 0.3, (int)rate / 2, rate), rate);
            Assert.True(c.AddingNowDbfs > -60);
            c.Enabled = false;
            Run(c, Tone(1200, 0.3, (int)rate / 20, rate), rate);   // 50 ms: the 20 ms fade and beyond
            for (int k = 0; k < 6; k++)
            {
                var idle = Tone(1200, 0.3, 480, rate, phase: k);
                for (int i = 0; i < 32; i += 2) idle[i] = idle[i + 1] = -0.0f;
                var copy = (float[])idle.Clone();
                c.Process(idle, idle.Length, rate);
                for (int i = 0; i < idle.Length; i++)
                    Assert.True(BitConverter.SingleToInt32Bits(copy[i]) == BitConverter.SingleToInt32Bits(idle[i]), $"idle buffer {k} sample {i}");
                Assert.Equal(-200f, c.AddingNowDbfs);
            }
            Assert.False(c.IsActive);
        }

        // ── delay ──

        [Theory]
        [MemberData(nameof(AllRates))]
        public void The_reported_delay_is_the_measured_delay(uint rate)
        {
            var both = High(2000, 12, 50, ClarityHarmonics.Both).WithLow(new ClarityBand(true, 150, 9, 40, ClarityHarmonics.Both));
            var c = Make(both, rate);
            var input = Noise(0.3, (int)rate, 3);
            var x = Left(input);
            var y = Left(Run(c, input, rate));
            int best = int.MinValue; double bestC = double.NegativeInfinity;
            for (int lag = -50; lag <= 50; lag++)
            {
                double s = 0;
                for (int i = 1000; i < x.Length - 1000; i++) s += x[i] * y[i + lag];
                if (s > bestC) { bestC = s; best = lag; }
            }
            Assert.Equal(c.LatencyFrames, best);
            Assert.Equal(0, c.LatencyFrames);
        }

        // ── counts ──

        [Fact]
        public void A_drive_that_pushes_the_output_over_full_scale_is_counted_and_one_that_does_not_counts_zero()
        {
            const uint rate = 48000;
            var hot = Make(High(2000, 24, 100, ClarityHarmonics.Even), rate);
            Run(hot, Tone(1500, 0.9, (int)rate / 2, rate), rate);
            var s1 = hot.TakeOverStats();
            _out.WriteLine($"hot: {s1.OverFullScale} over, added RMS {s1.AddedRmsDbfs:F1}, peak {s1.AddedPeakDbfs:F1} dBFS");
            Assert.True(s1.OverFullScale > 0);
            Assert.True(s1.AddedRmsDbfs > -30 && s1.AddedPeakDbfs > s1.AddedRmsDbfs);
            Assert.Equal(0, hot.TakeOverStats().OverFullScale); // zeroed by the take

            var mild = Make(High(2000, 0, 10, ClarityHarmonics.Even), rate);
            Run(mild, Tone(1500, 0.1, (int)rate / 2, rate), rate);
            var s2 = mild.TakeOverStats();
            Assert.Equal(0, s2.OverFullScale);
            Assert.True(s2.Samples > 0 && s2.AddedRmsDbfs > -200);

            // Buffers that do not count (unkeyed) are not counted.
            var quiet = Make(High(2000, 24, 100, ClarityHarmonics.Even), rate);
            Run(quiet, Tone(1500, 0.9, (int)rate / 2, rate), rate, countStats: false);
            var s3 = quiet.TakeOverStats();
            Assert.Equal(0, s3.OverFullScale);
            Assert.Equal(0, s3.Samples);
            Assert.True(quiet.AddingNowDbfs > -60, "the on-demand reading did not see what was added");
        }

        // ── the audio thread ──

        [Fact]
        public void Processing_adopting_a_design_fading_and_changing_rate_allocate_nothing()
        {
            var a = High(2000, 12, 30, ClarityHarmonics.Even).WithLow(new ClarityBand(true, 150, 6, 30, ClarityHarmonics.Both));
            var b = High(2500, 9, 50, ClarityHarmonics.Both);
            var c = new ClarityExciter { Enabled = true };
            var d48a = ClarityDesign.Create(a, 100, 2900, 48000);
            var d48b = ClarityDesign.Create(b, 100, 2900, 48000);
            var buf = Tone(1000, 0.3, 480, 48000);
            var buf16 = Tone(1000, 0.3, 160, 16000);
            c.Use(d48a);
            for (int i = 0; i < 20; i++) c.Process((float[])buf.Clone(), buf.Length, 48000); // warm up (JIT)
            c.Process(buf16, buf16.Length, 16000);
            c.Process(buf, buf.Length, 48000);
            var work = (float[])buf.Clone();
            long start = GC.GetAllocatedBytesForCurrentThread();
            c.Use(d48b);
            for (int i = 0; i < 10; i++) c.Process(work, work.Length, 48000);   // adopt, fade
            c.Enabled = false;
            for (int i = 0; i < 10; i++) c.Process(work, work.Length, 48000);   // fade out
            c.Enabled = true;
            c.Process(buf16, buf16.Length, 16000);                              // rate change: designs in place
            c.Use(d48a);
            for (int i = 0; i < 10; i++) c.Process(buf16, buf16.Length, 16000); // a 48 kHz design adopted at 16 kHz
            c.TakeOverStats();
            long used = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.Equal(0, used);
        }

        // ── settings and presets ──

        [Fact]
        public void Settings_are_clamped_to_their_ranges_and_a_non_finite_value_takes_the_default()
        {
            var s = new ClaritySettings(new ClarityBand(true, 50000, -3, 250, (ClarityHarmonics)9),
                new ClarityBand(true, float.NaN, float.PositiveInfinity, -5, ClarityHarmonics.Odd));
            Assert.Equal(ClaritySettings.HighTuneMaxHz, s.High.TuneHz);
            Assert.Equal(0f, s.High.DriveDb);
            Assert.Equal(100f, s.High.MixPercent);
            Assert.Equal(ClarityHarmonics.Even, s.High.Harmonics);
            Assert.Equal(ClaritySettings.LowTuneDefaultHz, s.Low.TuneHz);
            Assert.Equal(ClaritySettings.LowDriveDefaultDb, s.Low.DriveDb);
            Assert.Equal(0f, s.Low.MixPercent);
            Assert.True(s.Low.AddsNothing);
        }

        [Fact]
        public void A_new_setting_has_the_high_band_on_and_the_low_band_off_and_a_new_stage_is_off()
        {
            Assert.True(ClaritySettings.Default.High.Enabled);
            Assert.False(ClaritySettings.Default.Low.Enabled);
            Assert.False(new ClarityExciter().Enabled);
        }

        [Fact]
        public void Every_preset_is_within_range_has_a_unique_id_and_lands_inside_a_2900_Hz_SSB_filter_except_wide()
        {
            Assert.Equal(ClarityPresets.All.Count, ClarityPresets.All.Select(p => p.Id).Distinct().Count());
            foreach (var p in ClarityPresets.All)
            {
                Assert.Equal(new ClaritySettings(p.Settings.High, p.Settings.Low), p.Settings);
                Assert.False(p.Settings.AddsNothing);
                Assert.Same(p, ClarityPresets.Find(p.Id));
                var d = ClarityDesign.Create(p.Settings, 100, 2900, 48000);
                Assert.Equal(p.Id == ClarityPresets.Wide, d.HighTuneClamped);
                Assert.False(d.LowTuneClamped);
            }
            Assert.Null(ClarityPresets.Find("no-such-preset"));
        }

        [Fact]
        public void A_new_passband_edge_republishes_and_the_applied_tune_follows_while_the_intent_does_not()
        {
            var c = new ClarityExciter { Enabled = true };
            c.Use(High(2500, 12, 30, ClarityHarmonics.Even));
            Assert.False(c.HighTuneClamped);
            c.PassbandHighEdgeHz = 2700;
            Assert.True(c.HighTuneClamped);
            Assert.Equal(0.8f * 2700, c.AppliedHighTuneHz, 2);
            Assert.Equal(2500f, c.Settings.High.TuneHz);
            c.PassbandHighEdgeHz = 0;
            Assert.False(c.HighTuneClamped);
            c.PassbandLowEdgeHz = 200;
            Assert.Equal(200f, c.Wanted.PassbandLowEdgeHz);
        }
    }
}
