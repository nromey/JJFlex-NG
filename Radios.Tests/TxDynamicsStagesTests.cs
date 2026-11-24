using System;
using System.Collections.Generic;
using System.Linq;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track CL (#1278): the transmit compressor stage (phase
    /// rotator, drive, compressor), the limiter, and both in the conditioner.
    /// Numerical and radio-neutral: nothing here touches a FlexBase, a window,
    /// a device or a radio.
    /// </summary>
    public class TxDynamicsStagesTests
    {
        /// <summary>The five rates the transmit stream can open at (JJAudioStream.OpusTxRates).</summary>
        public static IEnumerable<object[]> Rates() => new[] { 48000, 24000, 16000, 12000, 8000 }.Select(r => new object[] { r });

        // ── signals ──

        private static float[] StereoSine(double hz, double db, int frames, int rate)
        {
            double a = Math.Pow(10, db / 20);
            var b = new float[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                float v = (float)(a * Math.Sin(2 * Math.PI * hz * f / rate));
                b[2 * f] = v; b[2 * f + 1] = v;
            }
            return b;
        }

        private static float[] StereoNoise(double peak, int frames, int seed)
        {
            var r = new Random(seed);
            var b = new float[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                float v = (float)((r.NextDouble() * 2 - 1) * peak);
                b[2 * f] = v; b[2 * f + 1] = v;
            }
            return b;
        }

        private static double RmsDb(float[] b, int fromFrame, int frames)
        {
            double sum = 0;
            for (int f = fromFrame; f < fromFrame + frames; f++) sum += (double)b[2 * f] * b[2 * f];
            return 10 * Math.Log10(sum / frames + 1e-30);
        }

        /// <summary>Run a whole signal through a stage in the transmit stream's 10 ms buffers.</summary>
        private static float[] Run(Action<float[], int, uint> process, float[] input, int rate)
        {
            var output = (float[])input.Clone();
            int step = rate / 100 * 2;
            var buf = new float[step];
            for (int at = 0; at < output.Length; at += step)
            {
                int n = Math.Min(step, output.Length - at);
                Array.Copy(output, at, buf, 0, n);
                process(buf, n, (uint)rate);
                Array.Copy(buf, 0, output, at, n);
            }
            return output;
        }

        private static TxCompressor Compressor(TxCompressorSettings s) => new TxCompressor { Settings = s };

        private static readonly TxCompressorSettings Plain = new()
        {
            Enabled = true, ThresholdDb = -18, RatioToOne = 3, KneeDb = 6, AttackMs = 5, ReleaseMs = 150, MakeupDb = 0,
        };

        // ── the static curve ──

        [Theory]
        [InlineData(-30.0)]   // under the knee: untouched
        [InlineData(-20.0)]   // inside the knee, below the threshold
        [InlineData(-18.0)]   // at the threshold, the knee's middle
        [InlineData(-16.0)]   // inside the knee, above the threshold
        [InlineData(-6.0)]    // above the knee
        [InlineData(0.0)]     // at full scale
        public void A_steady_tone_gets_the_curves_gain_within_a_tenth_of_a_dB(double levelDb)
        {
            const int rate = 48000;
            var input = StereoSine(1000, levelDb, rate * 2, rate);
            var c = Compressor(Plain);
            var output = Run((b, n, r) => c.Process(b, n, r), input, rate);
            double measured = RmsDb(input, rate, rate / 2) - RmsDb(output, rate, rate / 2);
            double expected = TxCompressor.ReductionFor(levelDb, -18, 1 - 1 / 3.0, 6);
            Assert.InRange(measured, expected - 0.1, expected + 0.1);
            if (levelDb == -30.0) Assert.Equal(0.0, expected);
            if (levelDb == -6.0) Assert.Equal(8.0, expected, 6);
        }

        [Fact]
        public void The_published_curve_is_continuous_at_both_ends_of_the_knee_and_ratio_one_reduces_nothing()
        {
            const double t = -18, slope = 0.75, w = 10;
            Assert.Equal(0.0, TxCompressor.ReductionFor(t - w / 2 - 0.001, t, slope, w));
            Assert.InRange(TxCompressor.ReductionFor(t - w / 2 + 0.001, t, slope, w), 0.0, 1e-6);
            Assert.Equal(slope * (w / 2), TxCompressor.ReductionFor(t + w / 2, t, slope, w), 9);
            Assert.Equal(slope * 12, TxCompressor.ReductionFor(t + 12, t, slope, w), 9);
            // A hard corner.
            Assert.Equal(0.0, TxCompressor.ReductionFor(t, t, slope, 0));
            Assert.Equal(slope * 3, TxCompressor.ReductionFor(t + 3, t, slope, 0), 9);
            // The positive control: ratio 1 is slope 0, and nothing is reduced anywhere.
            for (double x = -60; x <= 0; x += 1.5) Assert.Equal(0.0, TxCompressor.ReductionFor(x, t, 0, w));
        }

        // ── the detector is linear-domain and follows the peak ──

        [Theory]
        [InlineData(300.0)]
        [InlineData(1000.0)]
        [InlineData(2500.0)]
        public void A_steady_sine_settles_to_the_reduction_its_peak_calls_for(double hz)
        {
            const int rate = 48000;
            var c = Compressor(Plain);
            Run((b, n, r) => c.Process(b, n, r), StereoSine(hz, -6, rate * 2, rate), rate);
            // -6 dBFS peak is 12 dB over the threshold: 8 dB at 3 to 1.
            Assert.InRange(c.ReductionDb, 7.5f, 8.05f);
            Assert.InRange(c.DetectorDb, -6.4f, -5.99f);
        }

        // ── attack and release, measured on a step, at every rate ──

        [Theory]
        [MemberData(nameof(Rates))]
        public void Attack_and_release_measure_as_set_on_a_step(int rate)
        {
            const float attackMs = 5, releaseMs = 150;
            var c = Compressor(Plain with { AttackMs = attackMs, ReleaseMs = releaseMs });
            var one = new float[2];
            // A step from silence to a steady magnitude of 0.5.
            int attackFrames = -1;
            for (int f = 0; f < rate; f++)
            {
                one[0] = one[1] = 0.5f;
                c.Process(one, 2, (uint)rate);
                // 63.2 percent of the step is 3.98 dB under it.
                if (attackFrames < 0 && c.DetectorDb >= 20 * Math.Log10(0.5) + 20 * Math.Log10(1 - Math.Exp(-1))) attackFrames = f + 1;
            }
            double attackMeasuredMs = attackFrames * 1000.0 / rate;
            Assert.InRange(attackMeasuredMs, attackMs * 0.95 - 1000.0 / rate, attackMs * 1.05 + 1000.0 / rate);

            // Then a step down to silence: 36.8 percent is 8.69 dB under the level.
            int releaseFrames = -1;
            for (int f = 0; f < rate; f++)
            {
                one[0] = one[1] = 0f;
                c.Process(one, 2, (uint)rate);
                if (releaseFrames < 0 && c.DetectorDb <= 20 * Math.Log10(0.5) - 20 / Math.Log(10)) releaseFrames = f + 1;
            }
            double releaseMeasuredMs = releaseFrames * 1000.0 / rate;
            // The decoupled detector: the attack smoothing follows the release,
            // so the fall takes the release plus about the attack. "Plus the
            // attack" within 3 percent holds HERE, where the attack is a
            // thirtieth of the release; the next test pins how far it holds.
            double expected = releaseMs + attackMs;
            Assert.InRange(releaseMeasuredMs, expected * 0.97, expected * 1.03);
        }

        /// <summary>
        /// The scope of "the fall takes the release plus the attack" (Codex's
        /// check measured these at 48 kHz): an approximation, not an identity
        /// across the levers' ranges. It is right when either time is much
        /// longer than the other, and short by most when the two are equal:
        /// 7.3 percent, two equal one-pole stages in a row.
        /// </summary>
        [Theory]
        [InlineData(5f, 150f, 155.083, 0.0)]
        [InlineData(0.1f, 2000f, 2000.104, 0.0)]
        [InlineData(300f, 5f, 305.042, 0.0)]
        [InlineData(300f, 300f, 643.854, 7.3)]
        public void The_fall_to_a_third_is_the_release_plus_the_attack_except_when_the_two_are_alike(float attackMs, float releaseMs, double measuredAtCheckMs, double percentOverTheSum)
        {
            const int rate = 48000;
            var c = Compressor(Plain with { AttackMs = attackMs, ReleaseMs = releaseMs });
            var one = new float[2];
            for (int f = 0; f < rate * 12; f++) { one[0] = one[1] = 0.5f; c.Process(one, 2, rate); }
            int frames = -1;
            for (int f = 0; f < rate * 12 && frames < 0; f++)
            {
                one[0] = one[1] = 0f;
                c.Process(one, 2, rate);
                if (c.DetectorDb <= 20 * Math.Log10(0.5) - 20 / Math.Log(10)) frames = f + 1;
            }
            double ms = frames * 1000.0 / rate;
            Assert.InRange(ms, measuredAtCheckMs * 0.99, measuredAtCheckMs * 1.01);
            double over = (ms / (attackMs + releaseMs) - 1) * 100;
            Assert.InRange(over, percentOverTheSum - 0.3, percentOverTheSum + 0.3);
        }

        // ── identity ──

        [Theory]
        [MemberData(nameof(Rates))]
        public void Ratio_one_with_no_drive_no_makeup_and_no_rotator_is_the_input_bit_for_bit(int rate)
        {
            var input = StereoNoise(1.0, rate, seed: rate);
            var c = Compressor(new TxCompressorSettings { Enabled = true, RatioToOne = 1, MakeupDb = 0, DriveDb = 0, RotatorStages = 0 });
            var output = Run((b, n, r) => c.Process(b, n, r), input, rate);
            Assert.Equal(input, output);
            Assert.Equal(0f, c.ReductionDb);

            // The positive control: the same audio at 3 to 1 is changed.
            var d = Compressor(Plain);
            Assert.NotEqual(input, Run((b, n, r) => d.Process(b, n, r), input, rate));
        }

        [Fact]
        public void Audio_under_the_knee_is_never_touched_with_drive_and_makeup_at_zero()
        {
            const int rate = 48000;
            var input = StereoNoise(Math.Pow(10, -40 / 20.0), rate, seed: 7);
            var c = Compressor(Plain);
            Assert.Equal(input, Run((b, n, r) => c.Process(b, n, r), input, rate));
        }

        [Fact]
        public void Off_is_the_input_bit_for_bit_and_reads_no_reduction()
        {
            const int rate = 48000;
            var input = StereoNoise(1.0, rate, seed: 3);
            var c = Compressor(Plain with { Enabled = false, DriveDb = 12, RotatorStages = 4, MakeupDb = 9 });
            Assert.Equal(input, Run((b, n, r) => c.Process(b, n, r), input, rate));
            Assert.Equal(0f, c.ReductionDb);
            Assert.Equal(0, c.LatencyFrames);
        }

        // ── drive ──

        [Fact]
        public void Drive_raises_what_the_detector_reads_and_what_is_sent_by_exactly_its_amount()
        {
            const int rate = 48000;
            var input = StereoSine(1000, -30, rate, rate);
            var none = Compressor(Plain with { RatioToOne = 1, DriveDb = 0 });
            Run((b, n, r) => none.Process(b, n, r), input, rate);
            var driven = Compressor(Plain with { RatioToOne = 1, DriveDb = 12 });
            var output = Run((b, n, r) => driven.Process(b, n, r), input, rate);
            // The positive control: with no drive the detector reads the tone's own peak.
            Assert.InRange(none.DetectorDb, -30.3f, -29.99f);
            Assert.InRange(driven.DetectorDb - none.DetectorDb, 11.95f, 12.05f);
            Assert.InRange(RmsDb(output, rate / 2, rate / 4) - RmsDb(input, rate / 2, rate / 4), 11.99, 12.01);
        }

        [Fact]
        public void Drive_pushes_a_quiet_voice_over_the_threshold()
        {
            const int rate = 48000;
            var input = StereoSine(1000, -24, rate, rate); // 6 dB under the threshold, under the knee
            var none = Compressor(Plain);
            Run((b, n, r) => none.Process(b, n, r), input, rate);
            var driven = Compressor(Plain with { DriveDb = 12 });  // now 6 dB over it
            Run((b, n, r) => driven.Process(b, n, r), input, rate);
            Assert.Equal(0f, none.ReductionDb);
            Assert.InRange(driven.ReductionDb, 3.7f, 4.05f); // 6 dB over at 3 to 1 is 4 dB
        }

        // ── makeup and the lever glide ──

        [Fact]
        public void A_lever_turned_while_a_tone_runs_glides_and_never_steps()
        {
            const int rate = 48000;
            var c = Compressor(Plain with { RatioToOne = 1 });
            var input = StereoSine(500, -20, rate, rate);
            var output = (float[])input.Clone();
            var buf = new float[960];
            for (int at = 0; at < output.Length; at += 960)
            {
                if (at == 960 * 50) c.Settings = c.Settings with { MakeupDb = 12 };
                Array.Copy(output, at, buf, 0, 960);
                c.Process(buf, 960, rate);
                Array.Copy(buf, 0, output, at, 960);
            }
            // The largest step between neighbouring samples of a 500 Hz tone at
            // -8 dBFS (the level it ends at) is about 0.026; a 12 dB jump made
            // at once at the tone's crest would step by about 0.3.
            double worst = 0;
            for (int f = 480 * 50 - 2; f < 480 * 60; f++) worst = Math.Max(worst, Math.Abs(output[2 * (f + 1)] - output[2 * f]));
            Assert.InRange(worst, 0.0, 0.035);
            // And it arrives: the last quarter second is 12 dB up.
            Assert.InRange(RmsDb(output, rate * 3 / 4, rate / 4) - RmsDb(input, rate * 3 / 4, rate / 4), 11.99, 12.01);
            // Partway through the glide it is still on its way (a glide, not a late jump).
            double early = RmsDb(output, 480 * 50 + 48, 96) - RmsDb(input, 480 * 50 + 48, 96);
            Assert.InRange(early, 0.5, 11.0);
        }

        // ── the readings ──

        [Fact]
        public void The_overs_readings_give_the_deepest_and_the_average_reduction_and_count_only_what_they_are_told_to()
        {
            const int rate = 48000;
            var c = Compressor(Plain);
            // An unkeyed stretch is not counted.
            Run((b, n, r) => c.Process(b, n, r, countStats: false), StereoSine(1000, -6, rate, rate), rate);
            Assert.Equal(0, c.TakeOverStats().Frames);
            // A keyed second at a settled 8 dB.
            Run((b, n, r) => c.Process(b, n, r, countStats: true), StereoSine(1000, -6, rate, rate), rate);
            var (max, average, frames) = c.TakeOverStats();
            Assert.Equal(rate, frames);
            Assert.InRange(max, 7.5f, 8.05f);
            Assert.InRange(average, 7.5f, 8.05f);
            // Taking zeroes them.
            Assert.Equal((0f, 0f, 0L), c.TakeOverStats());
        }

        // ── the phase rotator ──

        [Theory]
        [MemberData(nameof(Rates))]
        public void Every_rotator_centre_is_below_Nyquist_and_inside_the_design_range_at_every_rate(int rate)
        {
            for (int s = 0; s < PhaseRotator.MaxStages; s++)
            {
                Assert.True(PhaseRotator.CentreHz(s) < rate / 2.0, $"centre {PhaseRotator.CentreHz(s)} at {rate}");
                // And under the 0.49 x rate bound the section design clamps at, so no centre is moved.
                Assert.True(PhaseRotator.CentreHz(s) < 0.49 * rate);
            }
            Assert.Equal(2500.0, Enumerable.Range(0, PhaseRotator.MaxStages).Max(PhaseRotator.CentreHz));
        }

        [Theory]
        [MemberData(nameof(Rates))]
        public void The_rotator_changes_no_frequencys_level_at_any_stage_count(int rate)
        {
            for (int stages = 0; stages <= PhaseRotator.MaxStages; stages++)
                for (double hz = 50; hz < rate / 2.0 - 100; hz *= 1.07)
                    Assert.InRange(PhaseRotator.MagnitudeDb(stages, hz, rate), -0.01, 0.01);
        }

        [Theory]
        [MemberData(nameof(Rates))]
        public void A_tone_through_the_running_rotator_leaves_at_its_own_level(int rate)
        {
            foreach (double hz in new[] { 100.0, 300, 700, 1000, 1500, 2500, 3500 })
            {
                if (hz > rate / 2.0 - 200) continue;
                var input = StereoSine(hz, -12, rate, rate);
                var c = Compressor(new TxCompressorSettings { Enabled = true, RatioToOne = 1, MakeupDb = 0, RotatorStages = PhaseRotator.MaxStages });
                var output = Run((b, n, r) => c.Process(b, n, r), input, rate);
                Assert.InRange(RmsDb(output, rate / 2, rate / 2) - RmsDb(input, rate / 2, rate / 2), -0.01, 0.01);
                // The positive control that the rotator ran at all: the samples differ.
                Assert.NotEqual(input, output);
            }
        }

        /// <summary>A voice-like wave with lopsided peaks: harmonics of 120 Hz, in phase, falling at 1/k, up to 3 kHz.</summary>
        private static float[] LopsidedVoice(int frames, int rate)
        {
            var mono = new double[frames];
            double peak = 0;
            for (int f = 0; f < frames; f++)
            {
                double v = 0;
                for (int k = 1; k * 120 <= 3000; k++) v += Math.Cos(2 * Math.PI * k * 120.0 * f / rate) / k;
                mono[f] = v;
                peak = Math.Max(peak, Math.Abs(v));
            }
            var b = new float[frames * 2];
            for (int f = 0; f < frames; f++) { float v = (float)(mono[f] / peak * 0.5); b[2 * f] = v; b[2 * f + 1] = v; }
            return b;
        }

        private static (double CrestDb, double PositivePeak, double NegativePeak) Shape(float[] b, int fromFrame, int frames)
        {
            double sum = 0, pos = 0, neg = 0;
            for (int f = fromFrame; f < fromFrame + frames; f++)
            {
                double v = b[2 * f];
                sum += v * v;
                if (v > pos) pos = v;
                if (-v > neg) neg = -v;
            }
            double rms = Math.Sqrt(sum / frames);
            return (20 * Math.Log10(Math.Max(pos, neg) / rms), pos, neg);
        }

        [Fact]
        public void Four_rotator_stages_lower_a_lopsided_voices_peak_at_the_same_power()
        {
            const int rate = 48000;
            var input = LopsidedVoice(rate * 2, rate);
            var c = Compressor(new TxCompressorSettings { Enabled = true, RatioToOne = 1, MakeupDb = 0, RotatorStages = PhaseRotator.BroadcastStages });
            var output = Run((b, n, r) => c.Process(b, n, r), input, rate);
            var before = Shape(input, rate, rate);
            var after = Shape(output, rate, rate);
            // Same power (an all-pass changes no level)...
            Assert.InRange(RmsDb(output, rate, rate) - RmsDb(input, rate, rate), -0.02, 0.02);
            // ...under a lower peak: the peak-to-average ratio falls by at least 3 dB.
            Assert.True(before.CrestDb - after.CrestDb >= 3.0, $"crest fell by {before.CrestDb - after.CrestDb:F2} dB");
            // And the wave was lopsided going in: its positive peak well above its negative one.
            Assert.True(before.PositivePeak / before.NegativePeak > 1.5, $"asymmetry in {before.PositivePeak / before.NegativePeak:F2}");
        }

        // ── the limiter ──

        private static TxLimiter Limiter(TxLimiterSettings s) => new TxLimiter { Settings = s };

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0f, 2.5f)]
        [InlineData(-6f, 0f)]
        [InlineData(-6f, 5f)]
        public void Audio_that_never_exceeds_the_ceiling_passes_bit_for_bit(float ceilingDb, float lookAheadMs)
        {
            const int rate = 48000;
            float ceiling = ceilingDb >= 0 ? 1f : (float)Math.Pow(10, ceilingDb / 20.0);
            // Noise peaking just under the ceiling, with samples EXACTLY at it.
            var input = StereoNoise(ceiling * 0.9999, rate, seed: 11);
            for (int f = 100; f < rate; f += 997) { input[2 * f] = ceiling; input[2 * f + 1] = -ceiling; }
            var lim = Limiter(new TxLimiterSettings { Enabled = true, CeilingDb = ceilingDb, LookAheadMs = lookAheadMs });
            var output = Run((b, n, r) => lim.Process(b, n, r), input, rate);
            int delay = TxLimiter.LookAheadFramesFor(lookAheadMs, rate);
            Assert.Equal(delay, lim.AppliedLookAheadFrames);
            for (int i = delay * 2; i < output.Length; i++)
                if (output[i] != input[i - delay * 2]) Assert.Fail($"sample {i} changed: {input[i - delay * 2]} became {output[i]}");
            var stats = lim.TakeOverStats();
            Assert.Equal(0, stats.Caught);
            Assert.Equal(0, stats.LimitedFrames);
        }

        /// <summary>Square bursts at twice full scale, steps, single-sample spikes, and a sweep 6 dB over full scale.</summary>
        private static float[] Torture(int rate)
        {
            int frames = rate * 2;
            var b = new float[frames * 2];
            double phase = 0;
            for (int f = 0; f < frames; f++)
            {
                double t = (double)f / rate;
                double v;
                if (t < 0.4) v = (f / (rate / 400) % 2 == 0 ? 2.0 : -2.0) * (f / (rate / 20) % 2 == 0 ? 1 : 0);
                else if (t < 0.8) v = f / (rate / 10) % 2 == 0 ? 1.7 : -0.2;
                else if (t < 1.0) v = f % 211 == 0 ? 3.0 : 0.3 * Math.Sin(2 * Math.PI * 440 * t);
                else
                {
                    double hz = 50 + (t - 1.0) * (rate * 0.45 - 50);
                    phase += 2 * Math.PI * hz / rate;
                    v = 2.0 * Math.Sin(phase);
                }
                b[2 * f] = (float)v; b[2 * f + 1] = (float)(f % 3 == 0 ? -v : v);
            }
            return b;
        }

        public static IEnumerable<object[]> RatesAndLookAheads() =>
            from rate in new[] { 48000, 24000, 16000, 12000, 8000 }
            from ms in new[] { 0f, 2.5f, 5f }
            from ceiling in new[] { 0f, -3f }
            select new object[] { rate, ms, ceiling };

        [Theory]
        [MemberData(nameof(RatesAndLookAheads))]
        public void No_sample_leaves_above_the_ceiling_on_a_torture_signal(int rate, float lookAheadMs, float ceilingDb)
        {
            float ceiling = ceilingDb >= 0 ? 1f : (float)Math.Pow(10, ceilingDb / 20.0);
            var input = Torture(rate);
            var lim = Limiter(new TxLimiterSettings { Enabled = true, CeilingDb = ceilingDb, LookAheadMs = lookAheadMs, ReleaseMs = 50 });
            var output = Run((b, n, r) => lim.Process(b, n, r), input, rate);
            float worst = output.Max(Math.Abs);
            Assert.True(worst <= ceiling, $"a sample left at {worst}, above the ceiling {ceiling}");
            // The positive control: the input was over it, and the limiter caught and reduced it.
            Assert.True(input.Max(Math.Abs) > 1.5f);
            var stats = lim.TakeOverStats();
            Assert.True(stats.Caught > 1000, $"caught {stats.Caught}");
            Assert.True(stats.LimitedFrames > 1000);
            Assert.True(stats.MaxReductionDb > 6f);
            Assert.Equal(0, stats.OutputOverFullScale);
        }

        public static IEnumerable<object[]> NearUnity() =>
            from rate in new[] { 48000, 24000, 16000, 12000, 8000 }
            from ceiling in new[] { 0f, -6f, -12f }
            from ms in new[] { 0f, 2.5f, 5f }
            select new object[] { rate, ceiling, ms };

        /// <summary>
        /// Repair 1, B1 (Codex's check of 69ee357f). The gain a stream a
        /// millionth over the ceiling needs is within a hundred-thousandth of
        /// one, and the limiter's "home again" shortcut took that for home:
        /// every sample left over the ceiling, for as long as the stream ran.
        /// </summary>
        [Theory]
        [MemberData(nameof(NearUnity))]
        public void A_stream_a_millionth_over_the_ceiling_never_leaves_over_it(int rate, float ceilingDb, float lookAheadMs)
        {
            float ceiling = ceilingDb >= 0 ? 1f : (float)Math.Pow(10, ceilingDb / 20.0);
            float over = ceiling * 1.000001f;
            Assert.True(over > ceiling, "the test's stream is not over the ceiling in single precision");
            foreach (bool steady in new[] { true, false })
            {
                // A steady level, and a square wave at it: every sample over, both signs.
                var input = new float[rate * 2];
                for (int f = 0; f < rate; f++)
                {
                    float v = steady || (f / 16) % 2 == 0 ? over : -over;
                    input[2 * f] = v; input[2 * f + 1] = f % 5 == 0 ? -v : v;
                }
                var lim = Limiter(new TxLimiterSettings { Enabled = true, CeilingDb = ceilingDb, LookAheadMs = lookAheadMs });
                var output = Run((b, n, r) => lim.Process(b, n, r), input, rate);
                float worst = output.Max(Math.Abs);
                Assert.True(worst <= ceiling, $"a sample left at {worst:R}, above the ceiling {ceiling:R}");
                var stats = lim.TakeOverStats();
                Assert.True(stats.Caught > rate, $"caught {stats.Caught}"); // the control: it saw them as over
                // Held by the GAIN, on nearly every frame (not only by the last clamp).
                Assert.True(stats.LimitedFrames > rate * 9 / 10, $"the gain was reduced on only {stats.LimitedFrames} of {rate} frames");
                Assert.Equal(0, stats.OutputOverFullScale);
                Assert.Equal(TxLimiter.LookAheadFramesFor(lookAheadMs, (uint)rate), lim.AppliedLookAheadFrames);
            }
            // And the property the shortcut exists for is kept: the same stream AT the ceiling passes bit for bit.
            var at = new float[rate * 2];
            for (int i = 0; i < at.Length; i++) at[i] = (i / 2) % 3 == 0 ? -ceiling : ceiling;
            var idle = Limiter(new TxLimiterSettings { Enabled = true, CeilingDb = ceilingDb, LookAheadMs = lookAheadMs });
            var same = Run((b, n, r) => idle.Process(b, n, r), at, rate);
            int delay = TxLimiter.LookAheadFramesFor(lookAheadMs, (uint)rate) * 2;
            for (int i = delay; i < same.Length; i++) if (same[i] != at[i - delay]) Assert.Fail($"sample {i} changed");
            Assert.Equal(0, idle.TakeOverStats().LimitedFrames);
        }

        [Fact]
        public void After_an_over_a_stream_just_over_the_ceiling_is_still_held_while_the_gain_comes_home()
        {
            // The shortcut's other road: the gain is coming back from a real over
            // when the stream settles a millionth over the ceiling. It must stop
            // at what that stream needs, not carry on home.
            const int rate = 48000;
            var input = new float[rate * 4 * 2];
            for (int f = 0; f < input.Length / 2; f++)
            {
                float v = f < 480 ? 3f : 1.000001f;
                input[2 * f] = v; input[2 * f + 1] = v;
            }
            foreach (float ms in new[] { 0f, 5f })
            {
                var lim = Limiter(new TxLimiterSettings { Enabled = true, ReleaseMs = 100, LookAheadMs = ms });
                var output = Run((b, n, r) => lim.Process(b, n, r), input, rate);
                Assert.True(output.Max(Math.Abs) <= 1f, $"look-ahead {ms}: a sample left at {output.Max(Math.Abs):R}");
                Assert.Equal(0, lim.TakeOverStats().OutputOverFullScale);
            }
        }

        [Theory]
        [InlineData(48000, 2.5f)]
        [InlineData(48000, 5f)]
        [InlineData(8000, 5f)]
        [InlineData(16000, 1f)]
        public void The_look_aheads_reported_delay_is_the_delay_measured(int rate, float lookAheadMs)
        {
            // A click in silence, under the ceiling: it leaves untouched, later.
            var input = new float[rate * 2];
            input[2 * 1000] = 0.5f; input[2 * 1000 + 1] = 0.5f;
            var lim = Limiter(new TxLimiterSettings { Enabled = true, LookAheadMs = lookAheadMs });
            var output = Run((b, n, r) => lim.Process(b, n, r), input, rate);
            int found = Array.FindIndex(output, v => v != 0f) / 2;
            Assert.Equal((int)Math.Round(lookAheadMs * rate / 1000.0), lim.AppliedLookAheadFrames);
            Assert.Equal(lim.AppliedLookAheadFrames, found - 1000);
            Assert.True(lim.AppliedLookAheadFrames > 0);
        }

        [Fact]
        public void With_look_ahead_the_gain_is_already_down_when_the_over_arrives_and_without_it_steps_at_the_over()
        {
            const int rate = 48000;
            // A quiet tone, then the same tone at twice full scale.
            var input = StereoSine(500, -20, rate, rate);
            for (int f = rate / 2; f < rate; f++) { input[2 * f] *= 20; input[2 * f + 1] *= 20; }
            var ahead = Limiter(new TxLimiterSettings { Enabled = true, LookAheadMs = 5 });
            var withAhead = Run((b, n, r) => ahead.Process(b, n, r), input, rate);
            var instant = Limiter(new TxLimiterSettings { Enabled = true, LookAheadMs = 0 });
            var without = Run((b, n, r) => instant.Process(b, n, r), input, rate);
            int delay = ahead.AppliedLookAheadFrames;
            // One frame before the loud part leaves, the look-ahead has already turned the quiet part down...
            int before = rate / 2 + delay - 2;
            Assert.True(Math.Abs(withAhead[2 * before]) < Math.Abs(input[2 * (before - delay)]) * 0.9,
                "the gain had not started down before the over");
            // ...and without it the quiet part is untouched right up to the over (the control).
            Assert.Equal(input[2 * (rate / 2 - 2)], without[2 * (rate / 2 - 2)]);
        }

        [Fact]
        public void The_gain_comes_back_by_the_release_after_an_over()
        {
            const int rate = 48000;
            var input = StereoSine(1000, -6, rate * 5 / 2, rate);
            // One burst at four times full scale, 10 ms long, a quarter second in.
            for (int f = rate / 4; f < rate / 4 + 480; f++) { input[2 * f] = (f % 48 < 24 ? 4f : -4f); input[2 * f + 1] = input[2 * f]; }
            var lim = Limiter(new TxLimiterSettings { Enabled = true, ReleaseMs = 100 });
            var output = Run((b, n, r) => lim.Process(b, n, r), input, rate);
            // 100 ms after the burst (one time constant) the gain has recovered
            // to about 1 - 0.75/e = 0.72 of unity: between 0.6 and 0.85.
            double ratioAt(int frame) => RmsDb(output, frame, 96) - RmsDb(input, frame, 96);
            Assert.InRange(ratioAt(rate / 4 + 480 + 4800), 20 * Math.Log10(0.6), 20 * Math.Log10(0.85));
            // Long after (about eleven time constants), it is the input again, bit for bit.
            for (int i = rate * 2 * 2; i < output.Length; i++) Assert.Equal(input[i], output[i]);
            // And half a second after the burst it was not yet (the control that the comparison can fail).
            Assert.NotEqual(input[2 * (rate * 3 / 4) + 24], output[2 * (rate * 3 / 4) + 24]);
        }

        [Fact]
        public void The_limiter_counts_what_it_caught_per_over_and_off_it_counts_what_left_over_full_scale()
        {
            const int rate = 48000;
            var input = new float[960];
            for (int f = 0; f < 480; f++) { input[2 * f] = 0.1f; input[2 * f + 1] = 0.1f; }
            input[20] = 1.5f; input[21] = -1.2f; input[400] = 2f; // three samples over
            var lim = Limiter(new TxLimiterSettings { Enabled = true });
            var buf = (float[])input.Clone();
            lim.Process(buf, 960, rate, countStats: false); // unkeyed: not counted
            Assert.Equal(0, lim.TakeOverStats().Caught);
            lim.Settings = lim.Settings with { Enabled = false };
            lim.Process((float[])input.Clone(), 960, rate, countStats: false);
            lim.Settings = lim.Settings with { Enabled = true };
            buf = (float[])input.Clone();
            lim.Process(buf, 960, rate, countStats: true);
            var on = lim.TakeOverStats();
            Assert.Equal(3, on.Caught);
            Assert.Equal(0, on.OutputOverFullScale);
            Assert.True(buf.Max(Math.Abs) <= 1f);

            // Off: nothing is touched, and what left over full scale is counted (keyed only).
            lim.Settings = lim.Settings with { Enabled = false };
            buf = (float[])input.Clone();
            lim.Process(buf, 960, rate, countStats: true);
            Assert.Equal(input, buf);
            var off = lim.TakeOverStats();
            Assert.Equal(0, off.Caught);
            Assert.Equal(3, off.OutputOverFullScale);
            Assert.Equal(0, lim.AppliedLookAheadFrames);
        }

        [Fact]
        public void The_limiters_settings_are_clamped_to_their_ranges_and_its_default_ceiling_is_full_scale()
        {
            var d = TxLimiterSettings.Default;
            Assert.False(d.Enabled);
            Assert.Equal(0f, d.CeilingDb);
            Assert.Equal(0f, d.LookAheadMs);
            var wild = new TxLimiterSettings { CeilingDb = 6, ReleaseMs = 0, LookAheadMs = 99 };
            Assert.Equal(0f, wild.CeilingDb);
            Assert.Equal(TxLimiterSettings.MinReleaseMs, wild.ReleaseMs);
            Assert.Equal(TxLimiterSettings.MaxLookAheadMs, wild.LookAheadMs);
            Assert.Equal(-12f, new TxLimiterSettings { CeilingDb = -40 }.CeilingDb);
            Assert.Equal(0f, new TxLimiterSettings { CeilingDb = float.NaN }.CeilingDb);
            var c = new TxCompressorSettings { ThresholdDb = 9, RatioToOne = 0.2f, AttackMs = 0, ReleaseMs = 1e9f, KneeDb = -1, MakeupDb = 99, DriveDb = -4, RotatorStages = 40 };
            Assert.Equal((0f, 1f, 0.1f, 2000f, 0f, 24f, 0f, 6), (c.ThresholdDb, c.RatioToOne, c.AttackMs, c.ReleaseMs, c.KneeDb, c.MakeupDb, c.DriveDb, c.RotatorStages));
        }

        // ── neither stage allocates on the audio thread ──

        [Fact]
        public void The_compressor_and_the_limiter_allocate_nothing_while_they_run()
        {
            const int rate = 48000;
            var c = Compressor(Plain with { DriveDb = 6, RotatorStages = 4 });
            var split = Compressor(Plain with { SplitBand = true });
            var lim = Limiter(new TxLimiterSettings { Enabled = true, LookAheadMs = 5 });
            var buf = StereoSine(700, 3, 480, rate);
            for (int i = 0; i < 20; i++) { c.Process(buf, 960, rate); split.Process(buf, 960, rate); lim.Process(buf, 960, rate); }
            // The least of several runs: an allocation in the stages themselves
            // shows in every run (200 buffers each), while the runtime's own
            // one-off work on this thread (a method recompiled mid-run) shows in
            // one. The #1056 family of allocation flakes is why it is not one run.
            Assert.Equal(0, LeastAllocated(() =>
            {
                for (int i = 0; i < 200; i++) { c.Process(buf, 960, rate); split.Process(buf, 960, rate); lim.Process(buf, 960, rate); }
            }));
        }

        /// <summary>The fewest bytes one of five runs of <paramref name="work"/> allocated on this thread.</summary>
        internal static long LeastAllocated(Action work)
        {
            long least = long.MaxValue;
            for (int run = 0; run < 5; run++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                work();
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return least;
        }
    }
}
