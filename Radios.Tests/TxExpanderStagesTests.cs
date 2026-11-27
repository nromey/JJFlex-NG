using System;
using System.Linq;
using System.Reflection;
using JJPortaudio;
using Radios.Tests.Frozen;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track I: the transmit gate converted to a downward expander
    /// (#432), its side-chain filter and look-ahead, the room's floor measured
    /// at its input (#1252), and the parametric equaliser and de-esser after
    /// it. Numerical, radio-neutral: nothing here touches a FlexBase.
    /// </summary>
    public class TxExpanderStagesTests
    {
        private const int Fs = 48000;
        private const int Buf = 960; // 480 stereo frames, the Opus path's buffer

        // ── signals ──

        /// <summary>A quadrature pair (sine left, cosine right): a constant channel-power sum, so the detector reads its level with no ripple.</summary>
        private static float[] Quadrature(double hz, double db, int frames, ref double phase)
        {
            double a = Math.Pow(10, db / 20);
            var b = new float[frames * 2];
            for (int f = 0; f < frames; f++)
            {
                b[2 * f] = (float)(a * Math.Sin(phase));
                b[2 * f + 1] = (float)(a * Math.Cos(phase));
                phase += 2 * Math.PI * hz / Fs;
            }
            return b;
        }

        /// <summary>A mono signal duplicated to stereo, as the transmit stream carries the microphone.</summary>
        private static float[] Stereo(float[] mono)
        {
            var b = new float[mono.Length * 2];
            for (int i = 0; i < mono.Length; i++) { b[2 * i] = mono[i]; b[2 * i + 1] = mono[i]; }
            return b;
        }

        private static float[] Sine(double hz, double db, int frames, double phase0 = 0)
        {
            double a = Math.Pow(10, db / 20);
            var m = new float[frames];
            for (int i = 0; i < frames; i++) m[i] = (float)(a * Math.Sin(phase0 + 2 * Math.PI * hz * i / Fs));
            return m;
        }

        private static float[] Noise(double db, int frames, int seed)
        {
            // Gaussian-ish noise (sum of uniforms), RMS at db.
            var r = new Random(seed);
            double sigma = Math.Pow(10, db / 20);
            var m = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                double s = 0; for (int k = 0; k < 12; k++) s += r.NextDouble();
                m[i] = (float)((s - 6) * sigma);
            }
            return m;
        }

        /// <summary>Speech-like bursts over a room: 0.6 s of a voice-band tone mix, 0.9 s gaps.</summary>
        private static float[] Speechy(double seconds, double speechDb, double roomDb, int seed)
        {
            int n = (int)(seconds * Fs);
            var room = Noise(roomDb, n, seed);
            var m = new float[n];
            double a = Math.Pow(10, speechDb / 20);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Fs;
                bool talking = (t % 1.5) < 0.6;
                double voice = talking ? a * (0.6 * Math.Sin(2 * Math.PI * 220 * t) + 0.3 * Math.Sin(2 * Math.PI * 660 * t) + 0.2 * Math.Sin(2 * Math.PI * 1500 * t)) : 0;
                m[i] = (float)(voice + room[i]);
            }
            return m;
        }

        private static void Run(Action<float[], int, uint> process, float[] stereo)
        {
            var b = new float[Buf];
            for (int at = 0; at < stereo.Length; at += Buf)
            {
                int n = Math.Min(Buf, stereo.Length - at);
                Array.Copy(stereo, at, b, 0, n);
                process(b, n, Fs);
                Array.Copy(b, 0, stereo, at, n);
            }
        }

        private static double Db(double power) => 10 * Math.Log10(power + 1e-30);

        private static double Rms(float[] m, int from, int count)
        {
            double s = 0; for (int i = from; i < from + count; i++) s += (double)m[i] * m[i];
            return Math.Sqrt(s / count);
        }

        // ── the expander's law (item 1) ──

        [Theory]
        [InlineData(2f, 10.0, 10.0)]   // 10 dB below the close point at 2:1: 10 dB
        [InlineData(3f, 5.0, 10.0)]    // 5 dB below at 3:1: 10 dB
        [InlineData(2f, 4.0, 4.0)]
        [InlineData(4f, 20.0, 25.0)]   // the law asks 60; the depth caps it at 25
        [InlineData(1f, 20.0, 0.0)]    // 1:1 is no expansion at all (the positive control of the law's zero)
        public void The_expander_attenuates_by_the_ratio_law_below_the_close_point_capped_at_the_depth(float ratio, double belowClose, double expected)
        {
            const float threshold = -40f;
            var g = new TxNoiseGate { Enabled = true, ThresholdDb = threshold, GateSetting = false, RatioToOne = ratio, RangeDb = 25f };
            double level = threshold - TxNoiseGate.CloseBelowThresholdDb - belowClose;
            double ph = 0;
            var sig = Quadrature(997, level, (int)(2.5 * Fs), ref ph);
            Run(g.Process, sig);
            Assert.InRange(g.AttenuationDb, expected - 0.25, expected + 0.25);
        }

        [Fact]
        public void The_law_has_no_step_where_the_decision_closes()
        {
            // Just below the close point the attenuation is near zero, not the depth.
            var g = new TxNoiseGate { Enabled = true, ThresholdDb = -40f, GateSetting = false, RatioToOne = 4f, RangeDb = 40f };
            double ph = 0;
            Run(g.Process, Quadrature(997, -43.5, (int)(2.5 * Fs), ref ph));
            Assert.InRange(g.AttenuationDb, 1.0, 2.0); // (4 - 1) x 0.5 dB
        }

        // ── the gate setting is today's gate, sample for sample (S7) ──

        private static float[] HarnessSignal()
        {
            // Speech bursts over a room, a long silence, a loud onset, quiet hiss.
            var a = Speechy(4.0, -18, -55, 1);
            var b = new float[(int)(1.5 * Fs)];
            var c = Sine(997, -15, (int)(0.3 * Fs));
            var d = Noise(-62, (int)(2.0 * Fs), 2);
            return Stereo(a.Concat(b).Concat(c).Concat(d).ToArray());
        }

        [Fact]
        public void At_the_migrated_state_the_expander_is_the_old_gate_sample_for_sample()
        {
            var oldGate = new FrozenTxNoiseGate { Enabled = true, ThresholdDb = -45f, AttackMs = 3, HoldMs = 150, ReleaseMs = 200, RangeDb = 25 };
            var newGate = new TxNoiseGate { Enabled = true, ThresholdDb = -45f, AttackMs = 3, HoldMs = 150, ReleaseMs = 200, RangeDb = 25,
                GateSetting = true, SideChainEnabled = false, LookAheadMs = 0 };
            oldGate.ResetsOnStreamGap = newGate.ResetsOnStreamGap = false;
            var x1 = HarnessSignal(); var x2 = (float[])x1.Clone();
            // Key-downs midway, as the conditioner tells them.
            var b1 = new float[Buf]; var b2 = new float[Buf];
            for (int at = 0, k = 0; at < x1.Length; at += Buf, k++)
            {
                int n = Math.Min(Buf, x1.Length - at);
                if (k == 300 || k == 700) { oldGate.ResetOpen(); newGate.ResetOpen(); }
                Array.Copy(x1, at, b1, 0, n); Array.Copy(x2, at, b2, 0, n);
                oldGate.Process(b1, n, Fs); newGate.Process(b2, n, Fs);
                for (int i = 0; i < n; i++) Assert.Equal(b1[i], b2[i]);
            }
        }

        [Fact]
        public void The_same_comparison_at_two_to_one_differs_so_it_can_see_a_difference()
        {
            var oldGate = new FrozenTxNoiseGate { Enabled = true, ThresholdDb = -45f };
            var newGate = new TxNoiseGate { Enabled = true, ThresholdDb = -45f, GateSetting = false, RatioToOne = 2f };
            var x1 = HarnessSignal(); var x2 = (float[])x1.Clone();
            Run(oldGate.Process, x1); Run(newGate.Process, x2);
            Assert.Contains(Enumerable.Range(0, x1.Length), i => x1[i] != x2[i]);
        }

        // ── what must survive ──

        [Fact]
        public void Attenuation_never_exceeds_forty_decibels_and_open_or_closed_is_no_longer_a_reading()
        {
            var g = new TxNoiseGate { Enabled = true, ThresholdDb = -10f, RangeDb = 90f, GateSetting = false, RatioToOne = 20f };
            Run(g.Process, Stereo(new float[(int)(2 * Fs)]));
            Assert.InRange(g.AttenuationDb, 0, 40.001);
            var t = typeof(TxNoiseGate);
            Assert.Null(t.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Instance));
            Assert.Null(t.GetProperty("CurrentGainDb", BindingFlags.Public | BindingFlags.Instance));
        }

        // ── the side-chain filter (item 1a) ──

        [Theory]
        [InlineData(50.0)]    // fan rumble below the low corner
        [InlineData(9000.0)]  // hiss above the high corner
        public void An_out_of_band_tone_does_not_hold_the_stage_open_with_the_filter_on_and_does_with_it_off(double hz)
        {
            TxNoiseGate Make(bool sc) => new() { Enabled = true, ThresholdDb = -40f, SideChainEnabled = sc };
            var on = Make(true); var off = Make(false);
            double p1 = 0, p2 = 0;
            Run(on.Process, Quadrature(hz, -25, 2 * (int)Fs, ref p1));
            Run(off.Process, Quadrature(hz, -25, 2 * (int)Fs, ref p2));
            Assert.True(off.AttenuationDb < 0.5, "the control: unfiltered, the tone holds it open");
            Assert.True(on.AttenuationDb > 20, $"filtered, it does not ({on.AttenuationDb:F1} dB)");
        }

        [Fact]
        public void While_open_the_audio_passed_is_bit_identical_with_the_filter_on_or_off()
        {
            var on = new TxNoiseGate { Enabled = true, ThresholdDb = -40f, SideChainEnabled = true };
            var off = new TxNoiseGate { Enabled = true, ThresholdDb = -40f, SideChainEnabled = false };
            double p1 = 0, p2 = 0;
            var a = Quadrature(800, -15, Fs, ref p1); var b = Quadrature(800, -15, Fs, ref p2);
            Run(on.Process, a); Run(off.Process, b);
            Assert.Equal(a, b);
        }

        // ── look-ahead (item 1b) ──

        [Fact]
        public void With_look_ahead_a_burst_after_silence_is_at_full_gain_at_its_first_sample_and_without_it_is_not()
        {
            double First(float ms)
            {
                var g = new TxNoiseGate { Enabled = true, ThresholdDb = -45f, AttackMs = 1f, LookAheadMs = ms };
                Run(g.Process, Stereo(new float[(int)(1.5 * Fs)]));
                int la = TxNoiseGate.LookAheadFramesFor(ms, Fs);
                // A burst of DC-free square: every sample has the same magnitude, so the first sample's gain is readable.
                var burst = new float[(int)(0.05 * Fs) + la];
                for (int i = 0; i < burst.Length - la; i++) burst[i] = (i & 1) == 0 ? 0.2f : -0.2f;
                var s = Stereo(burst);
                Run(g.Process, s);
                return 20 * Math.Log10(Math.Abs(s[2 * la]) / 0.2);
            }
            Assert.True(First(10f) > -0.05, "10 ms of look-ahead with a 1 ms attack: open before the word");
            Assert.True(First(0f) < -10, "the control: without look-ahead the first sample meets the depth");
        }

        [Fact]
        public void At_zero_look_ahead_the_stage_equals_the_stage_without_it_sample_for_sample()
        {
            var a = new TxNoiseGate { Enabled = true, ThresholdDb = -45f, GateSetting = false, LookAheadMs = 0f };
            var b = new TxNoiseGate { Enabled = true, ThresholdDb = -45f, GateSetting = false };
            var x = HarnessSignal(); var y = (float[])x.Clone();
            Run(a.Process, x); Run(b.Process, y);
            Assert.Equal(x, y);
        }

        [Theory]
        [InlineData(3f)]
        [InlineData(7.5f)]
        [InlineData(10f)]
        public void The_reported_look_ahead_equals_the_delay_measured_by_cross_correlation(float ms)
        {
            var g = new TxNoiseGate { Enabled = true, ThresholdDb = -80f, LookAheadMs = ms };
            var mono = Noise(-15, (int)Fs, 7);
            var s = Stereo(mono);
            Run(g.Process, s);
            int measured = Lag(mono, s, 1000);
            Assert.Equal(g.AppliedLookAheadFrames, measured);
            Assert.Equal(TxNoiseGate.LookAheadFramesFor(ms, Fs), measured);
        }

        /// <summary>The lag (0..max) at which the stereo output's left channel best matches the mono input.</summary>
        internal static int Lag(float[] input, float[] stereoOut, int max)
        {
            int best = 0; double bestScore = double.MinValue;
            int n = input.Length - max;
            for (int lag = 0; lag <= max; lag++)
            {
                double sum = 0;
                for (int i = Fs / 2; i < n; i++) sum += (double)input[i] * stereoOut[2 * (i + lag)];
                if (sum > bestScore) { bestScore = sum; best = lag; }
            }
            return best;
        }

        // ── the room's floor and the threshold rule (item 2, #1252) ──

        [Fact]
        public void Before_any_measurement_the_rule_leaves_the_inert_default_in_place()
        {
            var c = new TxAudioConditioner();
            c.SetGateInForce(new TxAudioConditioner.StagedGate { Enabled = true, AutoThreshold = true });
            Assert.False(c.RoomFloor.HasFloor);
            Assert.Null(c.RefreshDerivedThreshold(false));
            c.Process(new float[Buf], Buf, Fs);
            Assert.Equal(TxNoiseGate.DefaultThresholdDb, c.Gate.ThresholdDb);
        }

        [Fact]
        public void The_floor_is_the_cleaned_floor_at_the_expanders_input_not_the_raw_microphone()
        {
            // A noise reducer that takes 12 dB off everything.
            var c = new TxAudioConditioner { KeySource = () => false, NoiseReducer = (b, n, r) => { for (int i = 0; i < n; i++) b[i] *= 0.25f; } };
            var room = Stereo(Noise(-40, 4 * (int)Fs, 3));
            Run(c.Process, room);
            Assert.True(c.RoomFloor.HasFloor);
            // The raw room reads -40 dB per channel, so -37 dB as a channel-power sum; cleaned, 12 dB less.
            // That is the room's average (#1349: the floor itself is the detector's higher reading of it).
            Assert.InRange(c.RoomFloor.MeanFloorDb, -49.6, -48.4);
            Assert.True(c.RoomFloor.FloorDb > c.RoomFloor.MeanFloorDb + 3, $"the detector reads the room over its average ({c.RoomFloor.FloorDb:F1} against {c.RoomFloor.MeanFloorDb:F1})");
        }

        [Theory]
        [InlineData(false)] // nothing on: the chain's early road
        [InlineData(true)]  // the expander on: the full road
        public void Keyed_buffers_never_teach_it_and_unkeyed_ones_do(bool expanderOn)
        {
            bool keyed = true;
            var c = new TxAudioConditioner { KeySource = () => keyed };
            c.SetGateInForce(new TxAudioConditioner.StagedGate { Enabled = expanderOn });
            Run(c.Process, Stereo(Noise(-40, 4 * (int)Fs, 4)));
            Assert.False(c.RoomFloor.HasFloor);
            Assert.Equal(0, c.RoomFloor.Blocks);
            keyed = false;
            Run(c.Process, Stereo(Noise(-40, 4 * (int)Fs, 5)));
            Assert.True(c.RoomFloor.HasFloor);
            float learned = c.RoomFloor.FloorDb;
            keyed = true;
            Run(c.Process, Stereo(Noise(-10, 6 * (int)Fs, 6)));
            Assert.Equal(learned, c.RoomFloor.FloorDb);
        }

        [Fact]
        public void A_single_loud_unkeyed_burst_shorter_than_the_window_does_not_move_the_floor()
        {
            var c = new TxAudioConditioner { KeySource = () => false };
            Run(c.Process, Stereo(Noise(-50, 10 * (int)Fs, 8)));
            float before = c.RoomFloor.FloorDb;
            // Two seconds of loud receive audio from a shack speaker, then the room again.
            Run(c.Process, Stereo(Noise(-15, 2 * (int)Fs, 9)));
            Run(c.Process, Stereo(Noise(-50, 1 * (int)Fs, 10)));
            Assert.InRange(c.RoomFloor.FloorDb, before - 0.3, before + 0.3);
        }

        [Fact]
        public void A_room_that_changes_for_good_is_followed_down_quickly_and_up_slowly()
        {
            var c = new TxAudioConditioner { KeySource = () => false };
            Run(c.Process, Stereo(Noise(-40, 10 * (int)Fs, 11)));
            float start = c.RoomFloor.FloorDb;
            Run(c.Process, Stereo(Noise(-55, 4 * (int)Fs, 12)));
            Assert.True(c.RoomFloor.FloorDb < start - 10, "a quieter room is followed within seconds");
            float quiet = c.RoomFloor.FloorDb;
            Run(c.Process, Stereo(Noise(-40, 10 * (int)Fs, 13)));
            float risen = c.RoomFloor.FloorDb;
            Assert.True(risen > quiet + 1 && risen < start - 2, $"a louder room is followed slowly ({quiet:F1} to {risen:F1})");
        }

        [Fact]
        public void Learn_the_room_takes_the_floor_afresh_from_the_next_quiet_stretch()
        {
            var c = new TxAudioConditioner { KeySource = () => false };
            Run(c.Process, Stereo(Noise(-55, 6 * (int)Fs, 14)));
            long before = c.RoomFloor.LearnsFinished;
            c.RoomFloor.Learn();
            Run(c.Process, Stereo(Noise(-35, 4 * (int)Fs, 15)));
            Assert.Equal(before + 1, c.RoomFloor.LearnsFinished);
            Assert.InRange(c.RoomFloor.LearnedMeanDb, -32.6, -31.4); // -35 per channel is -32 as a sum: the room's average
            Assert.True(c.RoomFloor.LearnedDb > c.RoomFloor.LearnedMeanDb + 3, "the learned floor is the detector's reading, over the average (#1349)");
            Assert.InRange(c.RoomFloor.FloorDb, c.RoomFloor.LearnedDb - 0.1, c.RoomFloor.LearnedDb + 0.1); // tracking carries on from the learn
            Assert.InRange(c.RoomFloor.MeanFloorDb, c.RoomFloor.LearnedMeanDb - 0.1, c.RoomFloor.LearnedMeanDb + 0.1);
        }

        [Fact]
        public void A_learn_asked_while_keyed_waits_for_the_unkey()
        {
            bool keyed = true;
            var c = new TxAudioConditioner { KeySource = () => keyed };
            c.RoomFloor.Learn();
            Run(c.Process, Stereo(Noise(-20, 5 * (int)Fs, 16)));
            Assert.Equal(0, c.RoomFloor.LearnsFinished);
            keyed = false;
            Run(c.Process, Stereo(Noise(-50, 4 * (int)Fs, 17)));
            Assert.Equal(1, c.RoomFloor.LearnsFinished);
            Assert.InRange(c.RoomFloor.LearnedMeanDb, -47.6, -46.4);
        }

        [Fact]
        public void The_rule_is_radio_neutral_and_holds_still_while_keyed()
        {
            bool keyed = false;
            var c = new TxAudioConditioner { KeySource = () => keyed };
            c.SetGateInForce(new TxAudioConditioner.StagedGate { Enabled = true, AutoThreshold = true, ThresholdMarginDb = 8 });
            Run(c.Process, Stereo(Noise(-50, 4 * (int)Fs, 18)));
            var t = c.RefreshDerivedThreshold(false);
            Assert.NotNull(t);
            Assert.Equal(c.RoomFloor.FloorDb + 8, t!.Value, 3);
            Assert.Equal(t.Value, c.Gate.ThresholdDb, 3);
            Assert.Null(c.RefreshDerivedThreshold(true));
            Assert.Null(TxRoomFloor.ThresholdRule(false, false, true, -50, 8));
        }

        // ── the equaliser (item 3) ──

        private static double MeasuredGainDb(ParametricEqDesign d, double hz)
        {
            var eq = new ParametricEq();
            eq.Use(d);
            int frames = (int)(0.6 * Fs);
            var mono = Sine(hz, -20, frames);
            var s = Stereo(mono);
            Run((b, n, r) => eq.Process(b, n, r), s);
            int from = (int)(0.3 * Fs), count = (int)(0.25 * Fs);
            var outMono = new float[frames];
            for (int i = 0; i < frames; i++) outMono[i] = s[2 * i];
            return 20 * Math.Log10(Rms(outMono, from, count) / Rms(mono, from, count));
        }

        [Theory]
        [InlineData(BiquadKind.Peaking, 1000.0, 6.0, 1.0, 1000.0, 6.0)]
        [InlineData(BiquadKind.Peaking, 2500.0, -9.0, 4.0, 2500.0, -9.0)]
        [InlineData(BiquadKind.LowShelf, 200.0, 6.0, 0.7071, 200.0, 3.0)]   // a shelf is at half its gain at its corner
        [InlineData(BiquadKind.HighShelf, 3000.0, -6.0, 0.7071, 3000.0, -3.0)]
        [InlineData(BiquadKind.HighPass, 150.0, 0.0, 0.7071, 150.0, -3.0103)]
        [InlineData(BiquadKind.LowPass, 3000.0, 0.0, 0.7071, 3000.0, -3.0103)]
        public void Each_band_type_measures_its_setting_at_its_defining_frequency(BiquadKind kind, double hz, double gain, double q, double at, double expected)
        {
            var d = new ParametricEqDesign(new[] { new ParametricEqBand(kind, hz, gain, q) });
            Assert.InRange(MeasuredGainDb(d, at), expected - 0.1, expected + 0.1);
            Assert.InRange(d.MagnitudeDb(at), expected - 0.05, expected + 0.05);
        }

        [Fact]
        public void A_notch_takes_its_frequency_out()
        {
            var d = new ParametricEqDesign(new[] { new ParametricEqBand(BiquadKind.Notch, 1000, 0, 5) });
            Assert.True(MeasuredGainDb(d, 1000) < -30);
        }

        [Fact]
        public void Off_or_flat_is_bit_identical_and_the_latency_is_zero()
        {
            var eq = new ParametricEq();
            var x = Stereo(Noise(-20, Fs, 20)); var y = (float[])x.Clone();
            Run((b, n, r) => eq.Process(b, n, r), y);
            Assert.Equal(x, y);
            eq.Use(new ParametricEqDesign(Array.Empty<ParametricEqBand>()));
            Run((b, n, r) => eq.Process(b, n, r), y);
            Assert.Equal(x, y);
            // Latency: a broad gentle boost of noise lines up at lag zero.
            var lat = new ParametricEq();
            lat.Use(new ParametricEqDesign(new[] { new ParametricEqBand(BiquadKind.Peaking, 1000, 3, 0.5) }));
            var mono = Noise(-20, Fs, 21);
            var s = Stereo(mono);
            Run((b, n, r) => lat.Process(b, n, r), s);
            Assert.Equal(0, Lag(mono, s, 200));
            Assert.Equal(0, lat.LatencyFrames);
        }

        [Fact]
        public void A_change_mid_stream_fades_without_a_step()
        {
            // A steady 1 kHz tone; the band at 1 kHz jumps from +12 dB to -12 dB.
            var eq = new ParametricEq();
            eq.Use(new ParametricEqDesign(new[] { new ParametricEqBand(BiquadKind.Peaking, 1000, 12, 1) }));
            var s = Stereo(Sine(1000, -30, (int)(0.6 * Fs)));
            var b = new float[Buf];
            float maxStep = 0, prev = 0;
            for (int at = 0, k = 0; at < s.Length; at += Buf, k++)
            {
                if (k == 30) eq.Use(new ParametricEqDesign(new[] { new ParametricEqBand(BiquadKind.Peaking, 1000, -12, 1) }));
                Array.Copy(s, at, b, 0, Buf);
                eq.Process(b, Buf, Fs);
                for (int i = 0; i < Buf; i += 2)
                {
                    if (at + i > 0) maxStep = Math.Max(maxStep, Math.Abs(b[i] - prev));
                    prev = b[i];
                }
            }
            // A 1 kHz tone at -30 dBFS +12 dB moves by at most 2*pi*f/fs*A, about 0.033, per sample; a step would be far more.
            Assert.True(maxStep < 0.035f, $"largest sample-to-sample step {maxStep:F4}");

            // And it is a FADE: 5 ms after the change (a quarter of the way) the
            // tone is still well above where it ends, where a swap with no fade
            // is already there within a couple of milliseconds.
            var eq2 = new ParametricEq();
            eq2.Use(new ParametricEqDesign(new[] { new ParametricEqBand(BiquadKind.Peaking, 1000, 12, 1) }));
            var t = Stereo(Sine(1000, -30, (int)(0.6 * Fs)));
            int changeAt = 30 * Buf;
            for (int at = 0, k = 0; at < t.Length; at += Buf, k++)
            {
                if (k == 30) eq2.Use(new ParametricEqDesign(new[] { new ParametricEqBand(BiquadKind.Peaking, 1000, -12, 1) }));
                Array.Copy(t, at, b, 0, Buf);
                eq2.Process(b, Buf, Fs);
                Array.Copy(b, 0, t, at, Buf);
            }
            double Peak(int fromFrame, int frames) { double m = 0; for (int i = fromFrame; i < fromFrame + frames; i++) m = Math.Max(m, Math.Abs(t[2 * i])); return m; }
            double atFiveMs = 20 * Math.Log10(Peak(changeAt / 2 + (int)(0.004 * Fs), 48) / Math.Pow(10, -30 / 20.0));
            double settled = 20 * Math.Log10(Peak(changeAt / 2 + (int)(0.1 * Fs), 48) / Math.Pow(10, -30 / 20.0));
            Assert.InRange(settled, -12.2, -11.8);
            Assert.True(atFiveMs > 0, $"a quarter of the way through the fade the tone is still above unity ({atFiveMs:F1} dB)");
        }

        [Fact]
        public void The_over_full_scale_counter_counts_a_boost_that_clips_and_not_one_that_does_not()
        {
            var eq = new ParametricEq();
            eq.Use(new ParametricEqDesign(new[] { new ParametricEqBand(BiquadKind.Peaking, 1000, 12, 1) }));
            Run((b, n, r) => eq.Process(b, n, r), Stereo(Sine(1000, -20, Fs)));
            Assert.Equal(0, eq.TakeOverFullScale());
            Run((b, n, r) => eq.Process(b, n, r), Stereo(Sine(1000, -6, Fs)));
            Assert.True(eq.TakeOverFullScale() > 0);
            Assert.Equal(0, eq.TakeOverFullScale()); // taken means zeroed
        }

        // ── the de-esser (item 4) ──

        [Fact]
        public void A_sibilant_burst_is_reduced_by_no_more_than_the_amount_and_a_vowel_at_the_same_level_is_untouched()
        {
            var de = new DeEsser { Enabled = true, FrequencyHz = 5000, AmountDb = 6, ThresholdDb = -30 };
            var ess = Stereo(Sine(5000, -15, Fs));
            var before = (float[])ess.Clone();
            Run((b, n, r) => de.Process(b, n, r), ess);
            int from = Fs, count = Fs / 2;
            double inR = 0, outR = 0;
            for (int i = from; i < from + count; i += 2) { inR += before[i] * before[i]; outR += ess[i] * ess[i]; }
            double red = -10 * Math.Log10(outR / inR);
            Assert.True(red > 3, $"the detector heard the burst and turned it down ({red:F2} dB)");
            Assert.True(red <= 6.05, $"by no more than the amount ({red:F2} dB)");
            var (frames, deepest) = de.TakeOverStats();
            Assert.True(frames > 0 && deepest > 3 && deepest <= 6.01);

            var de2 = new DeEsser { Enabled = true, FrequencyHz = 5000, AmountDb = 6, ThresholdDb = -30 };
            var vowel = Stereo(Sine(300, -15, Fs));
            var copy = (float[])vowel.Clone();
            Run((b, n, r) => de2.Process(b, n, r), vowel);
            Assert.Equal(copy, vowel);
        }

        [Fact]
        public void The_band_clamps_below_the_transmit_filter_and_the_choice_is_kept()
        {
            var de = new DeEsser { FrequencyHz = 6000 };
            Assert.False(de.Clamped);
            de.PassbandHighEdgeHz = 2900;
            Assert.True(de.Clamped);
            Assert.Equal(6000f, de.FrequencyHz);
            Assert.Equal(2900f * DeEsser.PassbandFraction, de.AppliedFrequencyHz, 2);
            de.PassbandHighEdgeHz = 0;
            Assert.Equal(6000f, de.AppliedFrequencyHz);
        }

        // ── the chain (items 5 and 9, and S8) ──

        private static TxAudioConditioner ChainWith(ParametricEqDesign? eq, bool deEss, bool gate = false, float lookAhead = 0)
        {
            var c = new TxAudioConditioner { KeySource = () => true };
            c.SetGateInForce(new TxAudioConditioner.StagedGate
            {
                Enabled = gate, GateSetting = false, RatioToOne = 2, LookAheadMs = lookAhead, HasThreshold = true, ThresholdDb = -30,
                Eq = eq, DeEsserEnabled = deEss, DeEsserThresholdDb = -40,
            });
            return c;
        }

        [Fact]
        public void Cw_and_digital_bypass_every_stage()
        {
            var c = ChainWith(new ParametricEqDesign(new[] { new ParametricEqBand(BiquadKind.Peaking, 1000, 12, 1) }), true, gate: true, lookAhead: 5);
            c.BypassAll = true;
            var x = Stereo(Noise(-20, Fs / 2, 30)); var y = (float[])x.Clone();
            Run(c.Process, y);
            Assert.Equal(x, y);
        }

        [Fact]
        public void An_equaliser_boost_leaves_what_was_removed_silent_and_an_expander_cut_shows_in_it()
        {
            float[]? residual = null;
            double Removed(TxAudioConditioner c, float[] signal)
            {
                c.MonitorMode = TxAudioConditioner.MonitorModes.Residual;
                double e = 0;
                c.MonitorSink = (b, n, r) => { for (int i = 0; i < n; i++) e += (double)b[i] * b[i]; residual = b; };
                Run(c.Process, signal);
                return e;
            }
            var boost = ChainWith(new ParametricEqDesign(new[] { new ParametricEqBand(BiquadKind.Peaking, 1000, 9, 1) }), false);
            var boosted = Stereo(Sine(1000, -20, Fs));
            var original = (float[])boosted.Clone();
            Assert.Equal(0.0, Removed(boost, boosted));
            Assert.NotEqual(original, boosted); // the control: what goes out did change
            var cut = ChainWith(null, false, gate: true);
            Assert.True(Removed(cut, Stereo(Sine(1000, -50, Fs))) > 0, "an expander's cut is something removed");
        }

        [Fact]
        public void The_chain_reports_its_delay_as_noise_reduction_plus_look_ahead_and_the_shaping_stages_add_none()
        {
            var c = ChainWith(new ParametricEqDesign(new[] { new ParametricEqBand(BiquadKind.Peaking, 1000, 3, 1) }), true, gate: true, lookAhead: 4);
            c.NoiseReducer = (b, n, r) => { };
            c.NoiseReducerAppliedLatencySamples = () => 480;
            Run(c.Process, Stereo(Noise(-20, Fs / 10, 31)));
            Assert.Equal(480 + TxNoiseGate.LookAheadFramesFor(4, Fs), c.AppliedDelayFrames);
            Assert.Equal(0, c.Eq.LatencyFrames);
            Assert.Equal(0, c.DeEsser.LatencyFrames);
        }

        [Fact]
        public void A_switch_waits_for_an_unkeyed_buffer_and_a_continuous_lever_lands_at_once()
        {
            bool keyed = true;
            var c = new TxAudioConditioner { KeySource = () => keyed };
            c.Process(new float[Buf], Buf, Fs);
            c.RequestSwitch(g => g.DeEsserEnabled = true);
            c.ChangeGate(g => g.DeEsserAmountDb = 12);
            c.Process(new float[Buf], Buf, Fs);
            Assert.False(c.DeEsser.Enabled);
            Assert.Equal(12f, c.DeEsser.AmountDb);
            keyed = false;
            c.Process(new float[Buf], Buf, Fs);
            Assert.True(c.DeEsser.Enabled);
            Assert.Equal(12f, c.DeEsser.AmountDb);
        }
    }
}
