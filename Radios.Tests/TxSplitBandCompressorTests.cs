using System;
using System.Collections.Generic;
using System.Linq;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track CL, phase 2 (#1278; Noel 2026-11-23 20:48, a better tool
    /// "by all means"): the compressor's split-band mode. Three bands from
    /// fourth-order Linkwitz-Riley crossovers that sum flat in level, each band
    /// compressed by its own settings. Numerical; nothing here touches a radio.
    /// </summary>
    public class TxSplitBandCompressorTests
    {
        public static IEnumerable<object[]> Rates() => new[] { 48000, 24000, 16000, 12000, 8000 }.Select(r => new object[] { r });

        /// <summary>A split whose bands never reduce: thresholds at full scale, ratio 1, no makeup.</summary>
        private static TxCompressorSettings Idle(float low = 300, float high = 1800) => new()
        {
            Enabled = true, SplitBand = true, MakeupDb = 0, KneeDb = 0, CrossoverLowHz = low, CrossoverHighHz = high,
            Low = new TxCompressorBand { ThresholdDb = 0, RatioToOne = 1 },
            Mid = new TxCompressorBand { ThresholdDb = 0, RatioToOne = 1 },
            High = new TxCompressorBand { ThresholdDb = 0, RatioToOne = 1 },
        };

        private static float[] Tones(int rate, int frames, params (double Hz, double Db)[] tones)
        {
            var b = new float[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                double v = 0;
                foreach (var (hz, db) in tones) v += Math.Pow(10, db / 20) * Math.Sin(2 * Math.PI * hz * f / rate);
                b[2 * f] = (float)v; b[2 * f + 1] = (float)v;
            }
            return b;
        }

        private static float[] Run(TxCompressor c, float[] input, int rate)
        {
            var output = (float[])input.Clone();
            int step = rate / 100 * 2;
            var buf = new float[step];
            for (int at = 0; at + step <= output.Length; at += step)
            {
                Array.Copy(output, at, buf, 0, step);
                c.Process(buf, step, (uint)rate);
                Array.Copy(buf, 0, output, at, step);
            }
            return output;
        }

        /// <summary>One tone's level in a signal, dB: its correlation with a sine and a cosine at that frequency over a whole number of cycles.</summary>
        private static double ToneDb(float[] b, int rate, double hz, int fromFrame, int frames)
        {
            double s = 0, c = 0;
            for (int f = fromFrame; f < fromFrame + frames; f++)
            {
                double w = 2 * Math.PI * hz * f / rate;
                s += b[2 * f] * Math.Sin(w); c += b[2 * f] * Math.Cos(w);
            }
            double amplitude = 2 * Math.Sqrt(s * s + c * c) / frames;
            return 20 * Math.Log10(amplitude + 1e-30);
        }

        // ── the crossover's promise: flat in level ──

        [Theory]
        [MemberData(nameof(Rates))]
        public void An_idle_split_changes_no_frequencys_level_by_a_tenth_of_a_dB(int rate)
        {
            var (low, high) = TxCompressor.AppliedCrossovers(300, 1800, (uint)rate);
            var probes = new List<double> { low, high, Math.Sqrt(low * high) };
            for (double hz = 50; hz < rate / 2.0 - 300; hz *= 1.35) probes.Add(hz);
            foreach (double hz in probes)
            {
                // A whole number of cycles in the measured second, so the correlation is exact.
                double tone = Math.Round(hz);
                var input = Tones(rate, rate * 2, (tone, -20));
                var c = new TxCompressor { Settings = Idle() };
                var output = Run(c, input, rate);
                double change = ToneDb(output, rate, tone, rate, rate) - ToneDb(input, rate, tone, rate, rate);
                Assert.True(Math.Abs(change) < 0.1, $"{tone} Hz at {rate}: the idle split changed its level by {change:F3} dB");
                Assert.Equal(0f, c.ReductionDb);
            }
        }

        [Fact]
        public void An_idle_split_is_an_all_pass_of_the_input_not_the_input_itself()
        {
            // The stated trade: level unchanged, phase not. A complementary
            // split would pass the samples; this one does not, and says so.
            const int rate = 48000;
            var input = Tones(rate, rate, (500, -20));
            var output = Run(new TxCompressor { Settings = Idle() }, input, rate);
            Assert.NotEqual(input, output);
            Assert.Equal(0, new TxCompressor().LatencyFrames);
        }

        // ── each band is compressed by its own settings ──

        [Fact]
        public void A_low_tone_over_its_bands_threshold_is_reduced_and_a_high_tone_under_its_own_is_untouched()
        {
            const int rate = 48000;
            var input = Tones(rate, rate * 3, (100, -6), (3000, -30));
            var split = Idle() with
            {
                Low = new TxCompressorBand { ThresholdDb = -24, RatioToOne = 4, AttackMs = 10, ReleaseMs = 400 },
            };
            var c = new TxCompressor { Settings = split };
            var output = Run(c, input, rate);
            double lowChange = ToneDb(output, rate, 100, rate * 2, rate) - ToneDb(input, rate, 100, rate * 2, rate);
            double highChange = ToneDb(output, rate, 3000, rate * 2, rate) - ToneDb(input, rate, 3000, rate * 2, rate);
            // 18 dB over at 4 to 1 is 13.5 dB of reduction on the low band...
            Assert.InRange(lowChange, -14.5, -12.5);
            // ...and the high band, under its own threshold, keeps its level.
            Assert.InRange(highChange, -0.2, 0.2);
            Assert.InRange(c.BandReductionDb(0), 12.5f, 14f);
            Assert.Equal(0f, c.BandReductionDb(1));
            Assert.Equal(0f, c.BandReductionDb(2));
            Assert.Equal(c.BandReductionDb(0), c.ReductionDb);

            // The control: ONE band at the same settings turns the high tone down with the low one.
            var single = new TxCompressor
            {
                Settings = new TxCompressorSettings { Enabled = true, ThresholdDb = -24, RatioToOne = 4, KneeDb = 0, MakeupDb = 0, AttackMs = 10, ReleaseMs = 400 },
            };
            var together = Run(single, input, rate);
            double highWithOneBand = ToneDb(together, rate, 3000, rate * 2, rate) - ToneDb(input, rate, 3000, rate * 2, rate);
            Assert.True(highWithOneBand < -10, $"one band moved the high tone by {highWithOneBand:F2} dB");
        }

        // The isolation tests put each tone two octaves or more from a crossover
        // (crossovers at 200 and 3,200 Hz; tones at 60, 800 and 8,000 Hz). At 24 dB
        // an octave a neighbouring band still carries a little of a tone that sits
        // nearer: with the default crossovers (300 and 1,800 Hz) the bands either
        // side of an 800 Hz tone hold about 2 and 4 percent of it, so a 12 dB cut
        // in the middle band measures about 10.5 dB at 800 Hz. That is what a
        // crossover is, and it is in the report; these tests measure the bands.
        private static TxCompressorSettings Wide() => Idle(200, 3200);

        [Theory]
        [InlineData(0, 60.0)]
        [InlineData(1, 800.0)]
        [InlineData(2, 8000.0)]
        public void Each_bands_makeup_moves_its_own_band_and_leaves_the_other_two(int band, double itsTone)
        {
            const int rate = 48000;
            var tones = new[] { 60.0, 800.0, 8000.0 };
            var input = Tones(rate, rate * 2, (60, -30), (800, -30), (8000, -30));
            var lifted = new TxCompressorBand { ThresholdDb = 0, RatioToOne = 1, MakeupDb = 6 };
            var s = band == 0 ? Wide() with { Low = lifted } : band == 1 ? Wide() with { Mid = lifted } : Wide() with { High = lifted };
            var output = Run(new TxCompressor { Settings = s }, input, rate);
            foreach (double hz in tones)
            {
                double change = ToneDb(output, rate, hz, rate, rate) - ToneDb(input, rate, hz, rate, rate);
                if (hz == itsTone) Assert.InRange(change, 5.7, 6.3);
                else Assert.InRange(change, -0.3, 0.3);
            }
        }

        [Fact]
        public void Each_bands_threshold_and_ratio_act_on_its_band_only()
        {
            const int rate = 48000;
            // Three loud tones, one in each band; only the middle band compresses.
            var input = Tones(rate, rate * 3, (60, -12), (800, -12), (8000, -12));
            var s = Wide() with { Mid = new TxCompressorBand { ThresholdDb = -30, RatioToOne = 3, AttackMs = 5, ReleaseMs = 300 } };
            var c = new TxCompressor { Settings = s };
            var output = Run(c, input, rate);
            double low = ToneDb(output, rate, 60, rate * 2, rate) - ToneDb(input, rate, 60, rate * 2, rate);
            double mid = ToneDb(output, rate, 800, rate * 2, rate) - ToneDb(input, rate, 800, rate * 2, rate);
            double high = ToneDb(output, rate, 8000, rate * 2, rate) - ToneDb(input, rate, 8000, rate * 2, rate);
            Assert.InRange(mid, -13.0, -11.0); // 18 dB over at 3 to 1 is 12 dB
            Assert.InRange(low, -0.3, 0.3);
            Assert.InRange(high, -0.3, 0.3);
            Assert.Equal(0f, c.BandReductionDb(0));
            Assert.InRange(c.BandReductionDb(1), 11f, 12.5f);
            Assert.Equal(0f, c.BandReductionDb(2));
        }

        // ── the crossovers as applied ──

        [Fact]
        public void The_crossovers_keep_their_spacing_and_stay_under_the_rate()
        {
            Assert.Equal((300f, 1800f), TxCompressor.AppliedCrossovers(300, 1800, 48000));
            // Too close together: the higher is moved up to one and a half times the lower.
            Assert.Equal((800f, 1200f), TxCompressor.AppliedCrossovers(800, 900, 48000));
            // At 8 kHz the higher cannot pass 45 percent of the rate.
            var (low, high) = TxCompressor.AppliedCrossovers(1000, 6000, 8000);
            Assert.Equal(3600f, high);
            Assert.True(low <= high / 1.5f + 1e-3f);
            foreach (int rate in new[] { 48000, 24000, 16000, 12000, 8000 })
            {
                var (l, h) = TxCompressor.AppliedCrossovers(TxCompressorSettings.MaxCrossoverLowHz, TxCompressorSettings.MaxCrossoverHighHz, (uint)rate);
                Assert.True(h < rate / 2.0, $"{h} at {rate}");
                Assert.True(h >= l * 1.5f - 1e-3f);
            }
            // The chosen values are kept as the intent; only what runs is clamped.
            var s = new TxCompressorSettings { CrossoverLowHz = 1000, CrossoverHighHz = 6000 };
            Assert.Equal((1000f, 6000f), (s.CrossoverLowHz, s.CrossoverHighHz));
        }

        [Fact]
        public void The_split_band_preset_runs_split_and_the_others_run_one_band()
        {
            Assert.True(TxCompressorPresets.Find("bass_control")!.Settings.ToSettings().SplitBand);
            Assert.All(TxCompressorPresets.All.Where(p => p.Id != "bass_control"), p => Assert.False(p.Settings.ToSettings().SplitBand));
            // Its low band is held more firmly than its other two.
            var bass = TxCompressorPresets.Find("bass_control")!.Settings;
            Assert.True(bass.LowBand.RatioToOne > bass.MidBand.RatioToOne && bass.LowBand.RatioToOne > bass.HighBand.RatioToOne);
            Assert.True(bass.LowBand.ThresholdDb < bass.MidBand.ThresholdDb);
        }

        [Fact]
        public void Too_much_bass_is_held_without_touching_the_rest_of_the_voice()
        {
            // What the mode is for (#1278: "the tool for too much bass without
            // an EQ fight"): a boomy low end 12 dB over the voice's middle.
            const int rate = 48000;
            var input = Tones(rate, rate * 3, (120, -6), (1000, -18), (2500, -24));
            var s = TxCompressorPresets.Find("bass_control")!.Settings.ToSettings() with { Enabled = true };
            // Take the bands' makeup out, so what is measured is the compression alone.
            s = s with { Low = s.Low with { MakeupDb = 0 }, Mid = s.Mid with { MakeupDb = 0 }, High = s.High with { MakeupDb = 0 } };
            var output = Run(new TxCompressor { Settings = s }, input, rate);
            double bass = ToneDb(output, rate, 120, rate * 2, rate) - ToneDb(input, rate, 120, rate * 2, rate);
            double middle = ToneDb(output, rate, 1000, rate * 2, rate) - ToneDb(input, rate, 1000, rate * 2, rate);
            double top = ToneDb(output, rate, 2500, rate * 2, rate) - ToneDb(input, rate, 2500, rate * 2, rate);
            Assert.True(bass < -10, $"the low band came down by only {bass:F2} dB");
            Assert.InRange(middle, -1.5, 0.3); // at its knee: barely touched
            Assert.InRange(top, -0.5, 0.3);
        }
    }
}
