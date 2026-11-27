using System;
using System.Collections.Generic;
using System.Linq;
using JJPortaudio;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// #1349: the transmit expander never closed on steady room noise at its
    /// default margin, because the room's floor was measured as a mean and
    /// the detector rides peaks. The floor is now read with the expander's own
    /// detector at a high percentile, and a ratio's knee is placed from the
    /// room so the room comes down by depth times (1 - 1/ratio). These tests
    /// hold that, that words keep their starts and ends while the pauses come
    /// down, that the room meter still reads the same with the stage on or
    /// off (#1252), that Learn the room still comes from the room, and that
    /// WDSP's mode keeps its threshold over the room's average. Each carries
    /// a positive control. Numerical and radio-neutral.
    /// </summary>
    public class ExpanderFloorTests
    {
        private readonly ITestOutputHelper _out;
        public ExpanderFloorTests(ITestOutputHelper output) => _out = output;

        private const uint Fs = 48000;
        private const int Frames = 480;              // 10 ms, the transmit stream's buffer
        private const int Delay = 144;               // the default 3 ms look-ahead at 48 kHz

        // ── signals ──

        private static double[] Room(string kind, double rmsDb, int frames, int seed)
        {
            var r = new Random(seed);
            var x = new double[frames];
            double b0 = 0, b1 = 0, b2 = 0;
            for (int i = 0; i < frames; i++)
            {
                double u1 = 1 - r.NextDouble(), u2 = r.NextDouble();
                double w = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                if (kind == "pink") { b0 = 0.99765 * b0 + w * 0.0990460; b1 = 0.96300 * b1 + w * 0.2965164; b2 = 0.57000 * b2 + w * 1.0526913; w = b0 + b1 + b2 + w * 0.1848; }
                x[i] = w;
            }
            double rms = Math.Sqrt(x.Sum(v => v * v) / frames), g = Math.Pow(10, rmsDb / 20) / rms;
            for (int i = 0; i < frames; i++) x[i] *= g;
            return x;
        }

        /// <summary>Word-like bursts: a 150 Hz voice-band harmonic mix at a level, 15 ms rise, 30 ms fall, over a room; the word spans returned.</summary>
        private static double[] Words(double[] room, double wordDb, int first, int count, int wordFrames, int gapFrames, List<(int Start, int End)> spans)
        {
            var x = (double[])room.Clone();
            double a = Math.Pow(10, wordDb / 20) / Math.Sqrt(0.5 * 8);   // eight harmonics of equal amplitude: RMS at wordDb
            int rise = (int)(0.015 * Fs), fall = (int)(0.030 * Fs);
            for (int w = 0; w < count; w++)
            {
                int s = first + w * (wordFrames + gapFrames);
                for (int i = 0; i < wordFrames && s + i < x.Length; i++)
                {
                    double env = i < rise ? 0.5 - 0.5 * Math.Cos(Math.PI * i / rise) : i > wordFrames - fall ? 0.5 - 0.5 * Math.Cos(Math.PI * (wordFrames - i) / fall) : 1;
                    double v = 0;
                    for (int h = 1; h <= 8; h++) v += Math.Sin(2 * Math.PI * 150 * h * (s + i) / Fs + h);
                    x[s + i] += a * env * v;
                }
                spans.Add((s, s + wordFrames));
            }
            return x;
        }

        /// <summary>
        /// The conditioner end to end, as the app drives it: a key source the
        /// test controls, the threshold rule applied twice a second, unkeyed
        /// before <paramref name="keyFrom"/>, keyed after. Channel 0 out.
        /// </summary>
        private static double[] Run(TxAudioConditioner c, double[] mono, int keyFrom, Action<int>? afterBuffer = null)
        {
            bool keyed = false;
            c.KeySource = () => keyed;
            var y = new double[mono.Length];
            var buf = new float[Frames * 2];
            for (int b = 0; (b + 1) * Frames <= mono.Length; b++)
            {
                int at = b * Frames;
                keyed = at >= keyFrom;
                if (b % 50 == 0) c.RefreshDerivedThreshold(keyed);
                for (int i = 0; i < Frames; i++) { float v = (float)mono[at + i]; buf[2 * i] = v; buf[2 * i + 1] = v; }
                c.Process(buf, buf.Length, Fs);
                for (int i = 0; i < Frames; i++) y[at + i] = buf[2 * i];
                afterBuffer?.Invoke(b);
            }
            return y;
        }

        private static TxAudioConditioner.StagedGate NewSetup(float ratio = 2f, bool gate = false, bool enabled = true) => new()
        {
            // A new setup's expander: side-chain filter on, 3 ms of look-ahead, 2:1, depth 25, automatic at margin 8.
            Enabled = enabled, GateSetting = gate, RatioToOne = ratio, RangeDb = 25f, SideChainEnabled = true, LookAheadMs = 3f,
            AutoThreshold = true, ThresholdMarginDb = 8f, AttackMs = 3f, HoldMs = 150f, ReleaseMs = 200f,
        };

        /// <summary>How far the output is under the input on [from, to), dB, the input aligned by the look-ahead.</summary>
        private static double Down(double[] x, double[] y, int from, int to)
        {
            double a = 0, b = 0;
            for (int i = from; i < to; i++) { a += x[i] * x[i]; b += y[i + Delay] * y[i + Delay]; }
            return 10 * Math.Log10(a / Math.Max(b, 1e-30));
        }

        // ── the floor is the detector's own reading ──

        [Theory]
        [InlineData("white", false)]
        [InlineData("white", true)]
        [InlineData("pink", true)]
        public void The_floor_is_what_the_expanders_own_detector_reads_on_the_room_at_a_high_percentile(string kind, bool sideChain)
        {
            var room = Room(kind, -50, 8 * (int)Fs, 11);
            var meter = new TxRoomFloor();
            meter.SetSideChain(sideChain, TxNoiseGate.DefaultSideChainLowHz, TxNoiseGate.DefaultSideChainHighHz);
            var buf = new float[Frames * 2];
            for (int at = 0; at + Frames <= room.Length; at += Frames)
            {
                for (int i = 0; i < Frames; i++) { buf[2 * i] = (float)room[at + i]; buf[2 * i + 1] = (float)room[at + i]; }
                meter.Process(buf, buf.Length, Fs);
            }

            // The same reading worked out independently: the detector over the
            // same filtered room, once a millisecond, the 95th percentile of each
            // 400 ms block. Steady noise: the floor sits among those blocks.
            var filter = new SideChainFilter(); filter.Design(TxNoiseGate.DefaultSideChainLowHz, TxNoiseGate.DefaultSideChainHighHz, Fs);
            var det = new ExpanderDetector(); det.Design(Fs);
            var readings = new List<double>();
            double meanPower = 0;
            for (int i = 0; i < room.Length; i++)
            {
                double l = sideChain ? filter.Step(0, room[i]) : room[i];
                double r = sideChain ? filter.Step(1, room[i]) : room[i];
                double p = l * l + r * r;
                meanPower += p;
                double e = det.Step(p);
                if (i % 48 == 47) readings.Add(e);
            }
            var blocks = new List<double>();
            for (int s = 400; s + 400 <= readings.Count; s += 100)
            {
                var b = readings.Skip(s).Take(400).OrderBy(v => v).ToList();
                blocks.Add(10 * Math.Log10(b[(int)Math.Round(0.95 * 399)]));
            }
            blocks.Sort();
            double meanDb = 10 * Math.Log10(meanPower / room.Length);

            Assert.True(meter.HasFloor);
            Assert.InRange(meter.FloorDb, blocks[0] - 0.3, blocks[^1] + 0.3);
            // Positive control: the room's average, which the floor used to be, sits well under it: that gap was #1349.
            Assert.InRange(meter.MeanFloorDb, meanDb - 1.0, meanDb + 0.5);
            Assert.True(meter.FloorDb > meter.MeanFloorDb + 3, $"{kind}: the detector's reading {meter.FloorDb:F1} dB against the average {meter.MeanFloorDb:F1} dB");
        }

        // ── the stage closes, and a ratio delivers what it says ──

        [Theory]
        [InlineData("white", 2f, false, 12.5)]
        [InlineData("pink", 2f, false, 12.5)]
        [InlineData("pink", 4f, false, 18.75)]
        [InlineData("white", 2f, true, 25.0)]
        [InlineData("pink", 2f, true, 25.0)]
        public void At_the_default_margin_steady_room_noise_comes_down_by_what_the_settings_say(string kind, float ratio, bool gate, double stated)
        {
            // #1349's own case, through the real conditioner: 15 s of room unkeyed,
            // the rule twice a second, then 10 s keyed on the same room.
            var room = Room(kind, -50, 25 * (int)Fs, 21);
            int key = 15 * (int)Fs;
            var c = new TxAudioConditioner();
            c.SetGateInForce(NewSetup(ratio, gate));
            var y = Run(c, room, key);
            double down = Down(room, y, key + 2 * (int)Fs, room.Length - Delay);
            // The gate setting holds the depth exactly. A ratio follows the detector,
            // whose reading moves by a few decibels on noise, and the stage rises
            // faster than it falls, so it lands a little short of its law's figure.
            double tolerance = gate ? 0.5 : 1.5;
            _out.WriteLine($"{kind} {(gate ? "gate setting" : ratio + ":1")}: the room came down by {down:F2} dB against {stated} stated; threshold {c.Gate.ThresholdDb:F1} dB over a floor of {c.RoomFloor.FloorDb:F1} (average {c.RoomFloor.MeanFloorDb:F1})");
            Assert.InRange(down, stated - tolerance, stated + 0.5);

            // Positive control: the same room with the stage off is not turned down at all.
            var off = new TxAudioConditioner();
            off.SetGateInForce(NewSetup(ratio, gate, enabled: false));
            var y0 = Run(off, room, key);
            double none = 0;
            { double a = 0, b = 0; for (int i = key + 2 * (int)Fs; i < room.Length; i++) { a += room[i] * room[i]; b += y0[i] * y0[i]; } none = 10 * Math.Log10(a / b); }
            Assert.InRange(none, -0.01, 0.01);
        }

        [Theory]
        [InlineData(2f, 25f)]
        [InlineData(4f, 25f)]
        [InlineData(10f, 25f)]
        [InlineData(2f, 40f)]
        [InlineData(1.5f, 12f)]
        public void A_ratio_turns_the_room_down_by_depth_times_one_minus_one_over_the_ratio(float ratio, float depth)
        {
            // A constant level (a quadrature pair: a steady channel-power sum, so
            // the detector reads it exactly) sitting the margin under the threshold.
            double room = -48;
            float threshold = -40f;
            var g = new TxNoiseGate { Enabled = true, ThresholdDb = threshold, GateSetting = false, RatioToOne = ratio, RangeDb = depth, RoomBelowThresholdDb = 8f };
            Steady(g, room, 3.0);
            double law = depth * (1 - 1 / ratio);
            Assert.InRange(g.AttenuationDb, law - 0.05, law + 0.05);

            // Ten decibels under the room: (ratio - 1) dB more per dB, up to the depth.
            var q = new TxNoiseGate { Enabled = true, ThresholdDb = threshold, GateSetting = false, RatioToOne = ratio, RangeDb = depth, RoomBelowThresholdDb = 8f };
            Steady(q, room - 10, 3.0);
            double deeper = Math.Min(depth, law + (ratio - 1) * 10);
            Assert.InRange(q.AttenuationDb, deeper - 0.05, deeper + 0.05);

            // Positive control: with no room known the knee is the close point, as
            // before #1349, and the same room gets (ratio - 1) x 5 dB, whatever the depth.
            var old = new TxNoiseGate { Enabled = true, ThresholdDb = threshold, GateSetting = false, RatioToOne = ratio, RangeDb = depth };
            Steady(old, room, 3.0);
            double before = Math.Min(depth, (ratio - 1) * (threshold - TxNoiseGate.CloseBelowThresholdDb - room));
            Assert.InRange(old.AttenuationDb, before - 0.05, before + 0.05);
        }

        [Fact]
        public void One_to_one_turns_nothing_down_wherever_the_room_is()
        {
            var g = new TxNoiseGate { Enabled = true, ThresholdDb = -40f, GateSetting = false, RatioToOne = 1f, RangeDb = 40f, RoomBelowThresholdDb = 8f };
            Steady(g, -70, 2.0);
            Assert.InRange(g.AttenuationDb, -0.01, 0.01);
        }

        [Fact]
        public void The_conditioner_places_the_room_the_margin_in_force_under_the_threshold()
        {
            var c = new TxAudioConditioner();
            var s = NewSetup(); s.ThresholdMarginDb = 11f;
            c.SetGateInForce(s);
            c.Process(new float[Frames * 2], Frames * 2, Fs);
            Assert.Equal(11f, c.Gate.RoomBelowThresholdDb);
            c.ChangeGate(g => g.ThresholdMarginDb = 6f);
            c.Process(new float[Frames * 2], Frames * 2, Fs);
            Assert.Equal(6f, c.Gate.RoomBelowThresholdDb);
        }

        /// <summary>A steady channel-power sum at a level (dB) for a while.</summary>
        private static void Steady(TxNoiseGate g, double db, double seconds)
        {
            double a = Math.Pow(10, db / 20), phase = 0;
            var b = new float[Frames * 2];
            g.ResetsOnStreamGap = false;
            for (int k = 0; k < (int)(seconds * Fs / Frames); k++)
            {
                for (int f = 0; f < Frames; f++) { b[2 * f] = (float)(a * Math.Sin(phase)); b[2 * f + 1] = (float)(a * Math.Cos(phase)); phase += 2 * Math.PI * 1000 / Fs; }
                g.Process(b, b.Length, Fs);
            }
        }

        // ── words keep their starts and ends while the pauses come down ──

        [Theory]
        [InlineData(2f, false)]
        [InlineData(2f, true)]
        public void Words_keep_their_starts_and_ends_while_the_pauses_come_down(float ratio, bool gate)
        {
            // Pauses of 2 s, so the stage has finished closing before the last half second of each.
            int key = 15 * (int)Fs, word = (int)(0.4 * Fs), gap = 2 * (int)Fs;
            var room = Room("pink", -50, key + 22 * (word + gap) + (int)Fs, 31);
            var spans = new List<(int Start, int End)>();
            var x = Words(room, -20, key + (int)Fs, 20, word, gap, spans);
            var c = new TxAudioConditioner();
            c.SetGateInForce(NewSetup(ratio, gate));
            var y = Run(c, x, key);

            var onsets = spans.Select(s => Down(x, y, s.Start, s.Start + (int)(0.020 * Fs))).OrderBy(v => v).ToList();
            var tails = spans.Select(s => Down(x, y, s.End - (int)(0.100 * Fs), s.End)).OrderBy(v => v).ToList();
            var pauses = spans.Select(s => Down(x, y, s.End + (int)(1.5 * Fs), s.End + gap)).OrderBy(v => v).ToList();
            _out.WriteLine($"onsets median {onsets[onsets.Count / 2]:F2}, worst {onsets[^1]:F2}; tails worst {tails[^1]:F2}; pauses least {pauses[0]:F2} dB");
            Assert.True(onsets[onsets.Count / 2] < 1.0 && onsets[^1] < 3.0, "onsets: " + string.Join(", ", onsets.Select(v => v.ToString("F1"))));
            Assert.True(tails[^1] < 1.0, "tails: " + string.Join(", ", tails.Select(v => v.ToString("F1"))));
            // Not passing on an idle stage: the last part of every pause came down.
            double promised = gate ? 25 : 25 * (1 - 1 / ratio);
            Assert.True(pauses[0] > promised - 4, $"pauses: {string.Join(", ", pauses.Select(v => v.ToString("F1")))} against {promised}");

            // Positive control: the gate setting with a threshold set by hand at the
            // top of its range, over the words, takes their starts: the measure can see a loss.
            var hand = new TxAudioConditioner();
            var h = NewSetup(ratio, gate: true); h.AutoThreshold = false; h.HasThreshold = true; h.ThresholdDb = -10f;
            hand.SetGateInForce(h);
            var yh = Run(hand, x, key);
            var lost = spans.Select(s => Down(x, yh, s.Start, s.Start + (int)(0.020 * Fs))).OrderBy(v => v).ToList();
            Assert.True(lost[lost.Count / 2] > 5, "the control: a threshold over the words must take their starts (" + lost[lost.Count / 2].ToString("F1") + " dB)");
        }

        // ── #1252: the meter sits upstream of the stage ──

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_room_floor_reads_the_same_with_the_expander_on_and_off(bool gate)
        {
            int word = (int)(0.4 * Fs), gap = (int)(0.7 * Fs);
            var room = Room("pink", -50, 30 * (int)Fs, 41);
            var x = Words(room, -20, 4 * (int)Fs, 20, word, gap, new List<(int, int)>());
            var on = new TxAudioConditioner(); on.SetGateInForce(NewSetup(2f, gate));
            var off = new TxAudioConditioner(); off.SetGateInForce(NewSetup(2f, gate, enabled: false));
            var floorsOn = new List<(float, float)>(); var floorsOff = new List<(float, float)>();
            var yOn = Run(on, x, int.MaxValue, _ => floorsOn.Add((on.RoomFloor.FloorDb, on.RoomFloor.MeanFloorDb)));
            Run(off, x, int.MaxValue, _ => floorsOff.Add((off.RoomFloor.FloorDb, off.RoomFloor.MeanFloorDb)));
            Assert.True(on.RoomFloor.HasFloor);
            for (int i = 0; i < floorsOn.Count; i++)
            {
                Assert.Equal(floorsOff[i].Item1, floorsOn[i].Item1);
                Assert.Equal(floorsOff[i].Item2, floorsOn[i].Item2);
            }

            // Positive control: the same meter fed what the stage put out reads far
            // lower, so a meter in the wrong place could not pass the comparison above.
            var after = new TxRoomFloor(); after.SetSideChain(true, TxNoiseGate.DefaultSideChainLowHz, TxNoiseGate.DefaultSideChainHighHz);
            var buf = new float[Frames * 2];
            for (int at = 0; at + Frames + Delay <= yOn.Length; at += Frames)
            {
                for (int i = 0; i < Frames; i++) { buf[2 * i] = (float)yOn[at + i + Delay]; buf[2 * i + 1] = buf[2 * i]; }
                after.Process(buf, buf.Length, Fs);
            }
            Assert.True(after.FloorDb < on.RoomFloor.FloorDb - 8, $"a meter after the stage reads {after.FloorDb:F1} dB against {on.RoomFloor.FloorDb:F1} dB at its input");
        }

        // ── Learn the room ──

        [Fact]
        public void Learn_the_room_comes_from_the_room_and_one_cough_does_not_decide_it()
        {
            float Learned(double roomDb, bool cough)
            {
                var c = new TxAudioConditioner();
                c.SetGateInForce(NewSetup());
                var x = Room("pink", -55, 6 * (int)Fs, 51).Concat(Room("pink", roomDb, 4 * (int)Fs, 52)).ToArray();
                if (cough) for (int i = 0; i < (int)(0.15 * Fs); i++) x[8 * (int)Fs + i] += 0.05 * Math.Sin(2 * Math.PI * 700 * i / Fs) * Math.Sin(Math.PI * i / (0.15 * Fs));
                Run(c, x, int.MaxValue, b => { if (b == 600) c.RoomFloor.Learn(); });
                Assert.Equal(1, c.RoomFloor.LearnsFinished);
                Assert.True(c.RoomFloor.LearnedDb > c.RoomFloor.LearnedMeanDb + 3, "the learned floor is the detector's reading of the room, over its average");
                return c.RoomFloor.LearnedDb;
            }
            float quiet = Learned(-45, false), withCough = Learned(-45, true);
            Assert.InRange(withCough - quiet, -0.5, 0.5);
            // Positive control: the learn follows the room it hears, 10 dB for 10 dB.
            float louder = Learned(-35, false);
            Assert.InRange(louder - quiet, 9.3, 10.7);
        }

        // ── WDSP's mode keeps its threshold over the room's average ──

        [Fact]
        public void An_averaging_detector_is_given_the_margin_over_the_rooms_average_not_over_our_reading()
        {
            var c = new TxAudioConditioner();
            c.SetGateInForce(NewSetup());
            Assert.Null(c.AveragingDetectorThreshold());   // no floor yet
            Run(c, Room("pink", -50, 6 * (int)Fs, 61), int.MaxValue);
            var ours = c.RefreshDerivedThreshold(false);
            var averaging = c.AveragingDetectorThreshold();
            Assert.Equal(c.RoomFloor.FloorDb + 8f, ours!.Value, 3);
            Assert.Equal(c.RoomFloor.MeanFloorDb + 8f, averaging!.Value, 3);
            // Positive control: the two differ by the detector's reading over the average.
            Assert.True(ours.Value > averaging.Value + 3, $"ours {ours:F1} dB, the averaging detector's {averaging:F1} dB");
            // A threshold set by hand is the hand's, in either mode.
            c.ChangeGate(g => g.AutoThreshold = false);
            Assert.Null(c.AveragingDetectorThreshold());
        }
    }
}
