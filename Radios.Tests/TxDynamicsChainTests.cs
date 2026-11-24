using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Xml.Serialization;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track CL (#1278): the compressor and the limiter as members of
    /// the transmit conditioner (the one-owner record, the key discipline, the
    /// bypass, the delay, "what was removed"), in the frame pipeline, and in
    /// the saved setup record. Also #1293, the residual's delay line sized for
    /// both noise slots. Radio-neutral; every file lives in a scratch folder.
    /// </summary>
    public sealed class TxDynamicsChainTests : IDisposable
    {
        private const int Fs = 48000;
        private const int Buf = 960;
        private const string Radio = "1234-5678-9012-3456";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjf-track-cl-" + Guid.NewGuid().ToString("N"));

        public TxDynamicsChainTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static float[] Tone(double amplitude, int buffers = 1, double hz = 700)
        {
            var b = new float[Buf * buffers];
            for (int f = 0; f < b.Length / 2; f++)
            {
                float v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * f / Fs));
                b[2 * f] = v; b[2 * f + 1] = v;
            }
            return b;
        }

        private static float[] RunChain(TxAudioConditioner cond, float[] input)
        {
            var output = (float[])input.Clone();
            var buf = new float[Buf];
            for (int at = 0; at < output.Length; at += Buf)
            {
                Array.Copy(output, at, buf, 0, Buf);
                cond.Process(buf, Buf, Fs);
                Array.Copy(buf, 0, output, at, Buf);
            }
            return output;
        }

        private static TxAudioConditioner.StagedGate Record(Action<TxAudioConditioner.StagedGate> change)
        {
            var r = new TxAudioConditioner.StagedGate();
            change(r);
            return r;
        }

        private static readonly TxCompressorSettings Hot = new() { Enabled = true, ThresholdDb = -30, RatioToOne = 4, MakeupDb = 24, AttackMs = 20 };
        private static readonly TxLimiterSettings LimiterOn = new() { Enabled = true };

        // ── the one-owner record and the key discipline ──

        [Fact]
        public void A_new_chain_has_both_stages_off_and_nothing_active()
        {
            var cond = new TxAudioConditioner();
            Assert.False(cond.Compressor.Settings.Enabled);
            Assert.False(cond.Limiter.Settings.Enabled);
            Assert.False(cond.AnyStageActive);
            var input = Tone(1.8);
            Assert.Equal(input, RunChain(cond, input)); // even over full scale: off is off
        }

        [Fact]
        public void Either_stage_alone_makes_the_chain_active_and_bypass_stands_both_aside()
        {
            foreach (var record in new[]
                     {
                         Record(g => g.Comp = Hot),
                         Record(g => g.Limiter = LimiterOn),
                     })
            {
                var cond = new TxAudioConditioner();
                cond.SetGateInForce(record);
                Assert.True(cond.AnyStageActive);
                var input = Tone(1.8, buffers: 20);
                // The positive control: the stage changes this audio...
                Assert.NotEqual(input, RunChain(cond, input));
                // ...and in CW or digital the whole chain stands aside.
                cond.BypassAll = true;
                Assert.False(cond.AnyStageActive);
                Assert.Equal(input, RunChain(cond, input));
            }
        }

        [Fact]
        public void A_switch_made_keyed_waits_for_the_unkey_and_a_lever_is_live()
        {
            bool keyed = true;
            var cond = new TxAudioConditioner { KeySource = () => keyed };
            var buf = Tone(0.2);

            cond.RequestSwitch(g => g.Comp = g.Comp with { Enabled = true });
            cond.RequestSwitch(g => g.Limiter = g.Limiter with { Enabled = true, LookAheadMs = 5 });
            cond.ChangeGate(g => g.Comp = g.Comp with { ThresholdDb = -30 });
            cond.ChangeGate(g => g.Limiter = g.Limiter with { CeilingDb = -3 });
            cond.Process(buf, Buf, Fs);
            // Keyed: the switches wait, the levers are already in force.
            Assert.True(cond.HasStagedGate);
            Assert.False(cond.Compressor.Settings.Enabled);
            Assert.False(cond.Limiter.Settings.Enabled);
            Assert.Equal(-30f, cond.Compressor.Settings.ThresholdDb);
            Assert.Equal(-3f, cond.Limiter.Settings.CeilingDb);
            Assert.Equal(0, cond.AppliedDelayFrames);

            keyed = false;
            cond.Process(buf, Buf, Fs);
            // Unkeyed: the switches land, carrying the levers turned while they waited.
            Assert.False(cond.HasStagedGate);
            Assert.True(cond.Compressor.Settings.Enabled);
            Assert.True(cond.Limiter.Settings.Enabled);
            Assert.Equal(-30f, cond.Compressor.Settings.ThresholdDb);
            Assert.Equal(-3f, cond.Limiter.Settings.CeilingDb);
            Assert.Equal(5f, cond.Limiter.Settings.LookAheadMs);
            Assert.Equal(240, cond.AppliedDelayFrames);
        }

        // ── order: the compressor, then the limiter last ──

        [Fact]
        public void The_limiter_runs_after_the_compressor_and_holds_what_its_makeup_adds()
        {
            var input = Tone(0.5, buffers: 50);
            var unlimited = new TxAudioConditioner();
            unlimited.SetGateInForce(Record(g => g.Comp = Hot));
            // The control: this compressor's makeup sends samples past full scale.
            Assert.True(RunChain(unlimited, input).Max(Math.Abs) > 1.2f);

            var limited = new TxAudioConditioner();
            limited.SetGateInForce(Record(g => { g.Comp = Hot; g.Limiter = LimiterOn; }));
            Assert.True(RunChain(limited, input).Max(Math.Abs) <= 1f);
        }

        // ── delay ──

        [Fact]
        public void The_chains_delay_counts_the_limiters_look_ahead_and_the_residuals_does_not()
        {
            var cond = new TxAudioConditioner();
            cond.SetGateInForce(Record(g =>
            {
                g.Enabled = true; g.LookAheadMs = 3;                 // the expander's: 144 frames
                g.Comp = Hot;                                        // none
                g.Limiter = LimiterOn with { LookAheadMs = 5 };      // 240 frames
            }));
            RunChain(cond, Tone(0.2, buffers: 4));
            Assert.Equal(144 + 240, cond.AppliedDelayFrames);
            Assert.Equal(144, cond.ResidualDelayFrames);
            Assert.Equal(0, cond.Compressor.LatencyFrames);
            Assert.Equal(240, cond.Limiter.AppliedLookAheadFrames);

            // The control: with the limiter's look-ahead at its default the two figures agree.
            var plain = new TxAudioConditioner();
            plain.SetGateInForce(Record(g => { g.Enabled = true; g.LookAheadMs = 3; g.Comp = Hot; g.Limiter = LimiterOn; }));
            RunChain(plain, Tone(0.2, buffers: 4));
            Assert.Equal(144, plain.AppliedDelayFrames);
            Assert.Equal(144, plain.ResidualDelayFrames);
        }

        // ── "what was removed" ends at the expander ──

        private static float[] Removed(Action<TxAudioConditioner.StagedGate> dynamics, TxAudioConditioner.MonitorModes mode)
        {
            var heard = new List<float>();
            var cond = new TxAudioConditioner
            {
                MonitorMode = mode,
                MonitorSink = (b, n, r) => { for (int i = 0; i < n; i++) heard.Add(b[i]); },
            };
            cond.SetGateInForce(Record(g =>
            {
                g.Enabled = true; g.GateSetting = false; g.RatioToOne = 3; g.HasThreshold = true; g.ThresholdDb = -30; g.LookAheadMs = 3;
                dynamics(g);
            }));
            // Speech-like: loud and quiet stretches, so the expander removes something.
            var input = new float[Buf * 200];
            var rng = new Random(5);
            for (int f = 0; f < input.Length / 2; f++)
            {
                double level = f < 24000 ? 0.6 : 0.004; // half a second loud, then a second and a half quiet (the detector falls slowly)
                float v = (float)(level * (rng.NextDouble() * 2 - 1));
                input[2 * f] = v; input[2 * f + 1] = v;
            }
            RunChain(cond, input);
            return heard.ToArray();
        }

        [Fact]
        public void What_was_removed_is_the_same_with_the_compressor_and_the_limiter_on_or_off()
        {
            Action<TxAudioConditioner.StagedGate> on = g =>
            {
                g.Comp = Hot with { DriveDb = 12, RotatorStages = 4 };
                g.Limiter = LimiterOn with { LookAheadMs = 5, CeilingDb = -6 };
            };
            var without = Removed(_ => { }, TxAudioConditioner.MonitorModes.Residual);
            var with = Removed(on, TxAudioConditioner.MonitorModes.Residual);
            Assert.True(without.Max(Math.Abs) > 0.001f, "the expander removed nothing: the test proves nothing");
            Assert.Equal(without, with);
            // The control: what is SENT does change with them on.
            Assert.NotEqual(Removed(_ => { }, TxAudioConditioner.MonitorModes.Output), Removed(on, TxAudioConditioner.MonitorModes.Output));
            Assert.Equal(TxAudioConditioner.StageIds.Expander, TxAudioConditioner.ResidualEndsAfter);
        }

        // ── the readings ──

        [Fact]
        public void The_overs_readings_count_keyed_buffers_only()
        {
            bool keyed = false;
            var cond = new TxAudioConditioner { KeySource = () => keyed };
            cond.SetGateInForce(Record(g => { g.Comp = Hot with { MakeupDb = 0 }; g.Limiter = LimiterOn; }));
            RunChain(cond, Tone(1.8, buffers: 10));
            var unkeyed = cond.TakeDynamicsOverStats();
            Assert.Equal(0, unkeyed.Compressor.Frames);
            Assert.Equal(0, unkeyed.Limiter.Caught);

            keyed = true;
            var loud = new TxAudioConditioner { KeySource = () => keyed };
            loud.SetGateInForce(Record(g => g.Limiter = LimiterOn));
            RunChain(loud, Tone(1.8, buffers: 10));
            var over = loud.TakeDynamicsOverStats();
            Assert.True(over.Limiter.Caught > 100);
            Assert.Equal(0, over.Limiter.OutputOverFullScale);
            Assert.True(over.Limiter.MaxReductionDb > 4f);
        }

        // ── the stage ids and the order ──

        [Fact]
        public void The_default_order_ends_compressor_then_limiter_and_an_older_default_runs_as_the_default()
        {
            var order = TxAudioConditioner.StageIds.DefaultOrder;
            Assert.Equal(new[] { "noise", "expander", "eq", "deesser", "compressor", "limiter" }, order);
            Assert.Equal(TxAudioConditioner.StageIds.Limiter, order[^1]);
            Assert.True(TxAudioConditioner.StageIds.RunsAsDefault(order));
            // Track I's default, written before these stages existed.
            Assert.True(TxAudioConditioner.StageIds.RunsAsDefault(new[] { "noise", "expander", "eq", "deesser" }));
            // Not the default: another order, an id this build does not know, a repeat.
            Assert.False(TxAudioConditioner.StageIds.RunsAsDefault(new[] { "noise", "eq", "expander", "deesser" }));
            Assert.False(TxAudioConditioner.StageIds.RunsAsDefault(new[] { "noise", "expander", "eq", "deesser", "limiter", "compressor" }));
            Assert.False(TxAudioConditioner.StageIds.RunsAsDefault(new[] { "noise", "expander", "sparkle", "eq" }));
            Assert.False(TxAudioConditioner.StageIds.RunsAsDefault(new[] { "noise", "noise" }));
        }

        // ── the frame pipeline: a tone bypasses both, a file source does not ──

        private sealed class LoudSource : ITxInputSource
        {
            public bool Engaged => true;
            public bool Idle => false;
            public bool BypassesConditioning { get; set; }
            public void Process(float[] b, int n, uint rate)
            {
                for (int i = 0; i < n; i++) b[i] = (i / 2) % 40 < 20 ? 1.6f : -1.6f;
            }
        }

        [Theory]
        [InlineData(true)]   // a calibrated tone: the chain must not touch it
        [InlineData(false)]  // a recording sent through the chain: compressed and limited like the microphone
        public void A_source_that_bypasses_conditioning_bypasses_both_stages_and_one_that_does_not_is_limited(bool bypasses)
        {
            var cond = new TxAudioConditioner();
            cond.SetGateInForce(Record(g => { g.Comp = Hot with { MakeupDb = 0 }; g.Limiter = LimiterOn; }));
            float worst = 0;
            var p = new TxFramePipeline
            {
                Source = new LoudSource { BypassesConditioning = bypasses },
                Conditioner = cond.Process,
                Encode = b => { worst = Math.Max(worst, b.Take(Buf).Max(Math.Abs)); return new byte[1]; },
            };
            for (int i = 0; i < 30; i++) Assert.True(p.Emit(new float[Buf], Buf, Fs));
            if (bypasses) Assert.Equal(1.6f, worst);
            else Assert.True(worst <= 1f, $"a file source left the chain at {worst}");
        }

        // ── #1293: the residual's delay line holds both noise slots in series ──

        [Fact]
        public void The_two_slot_worst_case_delay_is_adopted_with_nothing_allocated_on_the_callback()
        {
            // #1293's figure: NR2 (5,024) plus NNR Premium (3,894), each bank's
            // possible 479-frame carry, and the expander's longest look-ahead at
            // 48 kHz (480): 10,356 frames.
            const int worstCase = 5024 + 3894 + 479 + 479 + 480;
            Assert.Equal(10356, worstCase);
            Assert.True(TxAudioConditioner.ResidualDelayCapacityFrames >= worstCase);

            int delay = 100;
            var cond = new TxAudioConditioner
            {
                NoiseReducer = (b, n, r) => { },
                NoiseReducerAppliedLatencySamples = () => delay,
            };
            var buf = Tone(0.2);
            for (int i = 0; i < 30; i++) cond.Process(buf, Buf, Fs); // warm, at a small delay
            // The least of several adoptions (see TxDynamicsStagesTests.LeastAllocated):
            // each run moves the delay to the worst case and back, so the line is re-laid twice.
            long least = TxDynamicsStagesTests.LeastAllocated(() =>
            {
                delay = worstCase - 480; // the noise slots' part; the look-ahead is the expander's
                cond.Process(buf, Buf, Fs);
                delay = worstCase;
                cond.Process(buf, Buf, Fs);
                delay = 100;
                cond.Process(buf, Buf, Fs);
            });
            Assert.Equal(0, least);
            delay = worstCase;
            cond.Process(buf, Buf, Fs);
            Assert.Equal(worstCase, cond.ResidualDelayFrames);
        }

        // ── the saved record ──

        [Fact]
        public void A_setups_compressor_and_limiter_default_to_the_stages_own_defaults_and_off()
        {
            var s = new TxNoiseSetup();
            Assert.False(s.Compressor.Enabled);
            Assert.False(s.Limiter.Enabled);
            Assert.Equal(TxCompressorSettings.Default, s.Compressor.ToSettings());
            Assert.Equal(TxLimiterSettings.Default, s.Limiter.ToSettings());
            // A plain compressor when first switched on: no drive, no rotator, one band.
            Assert.Equal(0f, s.Compressor.DriveDb);
            Assert.Equal(0, s.Compressor.RotatorStages);
            Assert.Equal(TxCompressorModes.Single, s.Compressor.CompressorMode);
            // A NEW setup's limiter is on at full scale (question 7's default); a record's own default is off.
            var fresh = TxLimiterSetup.ForNewSetup();
            Assert.Equal(TxLimiterSetup.NewSetupsStartOn, fresh.Enabled);
            Assert.Equal(0f, fresh.CeilingDb);
            Assert.Equal(0f, fresh.LookAheadMs);
        }

        /// <summary>A full setup with every compressor and limiter field away from its default.</summary>
        private static TxNoiseSetup Full() => new()
        {
            Name = "Contest",
            Compressor = new TxCompressorSetup
            {
                Enabled = true, PresetId = "dx", CompressorMode = TxCompressorModes.Split, ThresholdDb = -22, RatioToOne = 6, AttackMs = 2, ReleaseMs = 90,
                KneeDb = 4, MakeupDb = 11, DriveDb = 6, RotatorStages = 4, CrossoverLowHz = 250, CrossoverHighHz = 2100,
                LowBand = new TxCompressorBandSetup { ThresholdDb = -25, RatioToOne = 5, AttackMs = 12, ReleaseMs = 210, MakeupDb = 3 },
                MidBand = new TxCompressorBandSetup { ThresholdDb = -19, RatioToOne = 3, AttackMs = 6, ReleaseMs = 160, MakeupDb = 4 },
                HighBand = new TxCompressorBandSetup { ThresholdDb = -21, RatioToOne = 2, AttackMs = 3, ReleaseMs = 110, MakeupDb = 5 },
            },
            Limiter = new TxLimiterSetup { Enabled = true, CeilingDb = -1.5f, ReleaseMs = 60, LookAheadMs = 2 },
        };

        /// <summary>
        /// A setup file as the BASE of this track wrote it (Track I's record):
        /// today's serializer's output with the two elements and the two stage
        /// ids this track added taken out, which is exactly what that build
        /// wrote. Generated here; never copied from anyone's settings.
        /// </summary>
        private string WriteBaseFile()
        {
            var store = new TxNoiseSetupStore(_root);
            var setup = new TxNoiseSetup { Name = "Desk mic" };
            setup.Gate.Enabled = true;
            setup.Eq.Enabled = true;
            Assert.True(store.Save(Radio, setup).Succeeded);
            string path = store.PathFor(Radio)!;
            var doc = XDocument.Load(path);
            var record = doc.Descendants("TxNoiseSetup").Single();
            record.Element("Compressor")!.Remove();
            record.Element("Limiter")!.Remove();
            var order = record.Element("StageOrder")!;
            order.Elements().Where(x => x.Value is "compressor" or "limiter").Remove();
            Assert.Equal(new[] { "noise", "expander", "eq", "deesser" }, order.Elements().Select(x => x.Value));
            doc.Save(path);
            Assert.DoesNotContain("Compressor", File.ReadAllText(path));
            Assert.DoesNotContain("Limiter", File.ReadAllText(path));
            return path;
        }

        [Fact]
        public void A_base_written_setup_file_loads_with_both_off_and_runs_the_default_order()
        {
            WriteBaseFile();
            var loaded = new TxNoiseSetupStore(_root).Find(Radio, "Desk mic")!;
            Assert.False(loaded.Compressor.Enabled);
            Assert.False(loaded.Limiter.Enabled); // saved setups keep exactly their sound: the limiter is NOT switched on for them
            Assert.Equal(TxCompressorSettings.Default, loaded.Compressor.ToSettings());
            Assert.True(loaded.Gate.Enabled);
            Assert.True(loaded.Eq.Enabled);
            Assert.True(loaded.RunsDefaultOrder);
            // The older default order is this build's default, said in full, so the two never read as different setups.
            Assert.Equal(TxAudioConditioner.StageIds.DefaultOrder, loaded.StageOrder);
            var same = new TxNoiseSetup { Name = "Desk mic" };
            same.Gate.Enabled = true; same.Eq.Enabled = true;
            Assert.True(loaded.SameContentAs(same));
        }

        [Fact]
        public void An_order_this_build_does_not_understand_is_still_kept_exactly()
        {
            var store = new TxNoiseSetupStore(_root);
            var odd = new TxNoiseSetup { Name = "Later", StageOrder = new[] { "noise", "expander", "sparkle", "eq", "deesser" } };
            Assert.True(store.Save(Radio, odd).Succeeded);
            var back = store.Find(Radio, "Later")!;
            Assert.Equal(new[] { "noise", "expander", "sparkle", "eq", "deesser" }, back.StageOrder);
            Assert.False(back.RunsDefaultOrder);
        }

        [Fact]
        public void A_full_record_round_trips_through_the_store_and_a_preset_file()
        {
            var store = new TxNoiseSetupStore(_root);
            Assert.True(store.Save(Radio, Full()).Succeeded);
            var back = store.Find(Radio, "Contest")!;
            Assert.True(back.SameContentAs(Full()));
            Assert.Equal(Full().Compressor.ToSettings(), back.Compressor.ToSettings());
            Assert.Equal(Full().Limiter.ToSettings(), back.Limiter.ToSettings());
            Assert.Equal("dx", back.Compressor.PresetId);
            // A second save changes nothing.
            Assert.True(store.Save(Radio, back).Succeeded);
            Assert.True(store.Find(Radio, "Contest")!.SameContentAs(Full()));

            string file = Path.Combine(_root, "p.xml");
            using (var fs = File.Create(file)) new XmlSerializer(typeof(AudioChainPreset)).Serialize(fs, new AudioChainPreset { Name = "P", TransmitNoise = Full() });
            Assert.True(AudioChainPreset.TryLoad(file, out var preset, out _));
            Assert.True(preset!.TransmitNoise!.SameContentAs(Full()));
        }

        public static IEnumerable<object[]> Fields() => new[]
        {
            "c.Enabled", "c.CompressorMode", "c.ThresholdDb", "c.RatioToOne", "c.AttackMs", "c.ReleaseMs", "c.KneeDb", "c.MakeupDb", "c.DriveDb",
            "c.RotatorStages", "c.CrossoverLowHz", "c.CrossoverHighHz", "c.Low.ThresholdDb", "c.Mid.RatioToOne", "c.High.AttackMs",
            "c.Low.ReleaseMs", "c.Mid.MakeupDb", "l.Enabled", "l.CeilingDb", "l.ReleaseMs", "l.LookAheadMs",
        }.Select(f => new object[] { f });

        [Theory]
        [MemberData(nameof(Fields))]
        public void SameContentAs_sees_a_change_in_each_compressor_and_limiter_field(string field)
        {
            var a = Full();
            var b = Full();
            Assert.True(a.SameContentAs(b));
            var c = b.Compressor; var l = b.Limiter;
            switch (field)
            {
                case "c.Enabled": c.Enabled = false; break;
                case "c.CompressorMode": c.CompressorMode = TxCompressorModes.Single; break;
                case "c.ThresholdDb": c.ThresholdDb += 1; break;
                case "c.RatioToOne": c.RatioToOne += 1; break;
                case "c.AttackMs": c.AttackMs += 1; break;
                case "c.ReleaseMs": c.ReleaseMs += 1; break;
                case "c.KneeDb": c.KneeDb += 1; break;
                case "c.MakeupDb": c.MakeupDb += 1; break;
                case "c.DriveDb": c.DriveDb += 1; break;
                case "c.RotatorStages": c.RotatorStages += 1; break;
                case "c.CrossoverLowHz": c.CrossoverLowHz += 10; break;
                case "c.CrossoverHighHz": c.CrossoverHighHz += 10; break;
                case "c.Low.ThresholdDb": c.LowBand.ThresholdDb += 1; break;
                case "c.Mid.RatioToOne": c.MidBand.RatioToOne += 1; break;
                case "c.High.AttackMs": c.HighBand.AttackMs += 1; break;
                case "c.Low.ReleaseMs": c.LowBand.ReleaseMs += 1; break;
                case "c.Mid.MakeupDb": c.MidBand.MakeupDb += 1; break;
                case "l.Enabled": l.Enabled = false; break;
                case "l.CeilingDb": l.CeilingDb -= 1; break;
                case "l.ReleaseMs": l.ReleaseMs += 1; break;
                case "l.LookAheadMs": l.LookAheadMs += 1; break;
                default: throw new InvalidOperationException(field);
            }
            Assert.False(a.SameContentAs(b), field + " changed and SameContentAs did not see it");
        }

        [Fact]
        public void Clone_copies_the_compressor_its_bands_and_the_limiter_deeply()
        {
            var a = Full();
            var b = a.Clone();
            Assert.True(a.SameContentAs(b));
            Assert.NotSame(a.Compressor, b.Compressor);
            Assert.NotSame(a.Compressor.LowBand, b.Compressor.LowBand);
            Assert.NotSame(a.Compressor.MidBand, b.Compressor.MidBand);
            Assert.NotSame(a.Compressor.HighBand, b.Compressor.HighBand);
            Assert.NotSame(a.Limiter, b.Limiter);
            b.Compressor.LowBand.RatioToOne = 9;
            b.Limiter.CeilingDb = -9;
            Assert.Equal(5f, a.Compressor.LowBand.RatioToOne);
            Assert.Equal(-1.5f, a.Limiter.CeilingDb);
        }

        [Fact]
        public void A_mode_word_this_build_does_not_know_runs_as_one_band_and_is_kept_on_save()
        {
            var store = new TxNoiseSetupStore(_root);
            var later = Full();
            later.Compressor.CompressorMode = "wdsp";
            Assert.True(store.Save(Radio, later).Succeeded);
            var back = store.Find(Radio, "Contest")!;
            Assert.Equal("wdsp", back.Compressor.CompressorMode);
            Assert.False(back.Compressor.ToSettings().SplitBand);
        }

        // ── presets: starting points, as data ──

        [Fact]
        public void Every_preset_loads_as_listed_keeps_the_switch_as_it_was_and_stays_editable()
        {
            Assert.Equal(new[] { "plain", "ragchew", "dx", "level", "bass_control" }, TxCompressorPresets.All.Select(p => p.Id));
            foreach (var preset in TxCompressorPresets.All)
            {
                foreach (bool on in new[] { true, false })
                {
                    var loaded = TxCompressorPresets.Load(preset, on);
                    Assert.Equal(on, loaded.Enabled);          // its on or off is the operator's
                    Assert.Equal(preset.Id, loaded.PresetId);
                    var want = preset.Settings.Clone();
                    want.Enabled = on; want.PresetId = preset.Id;
                    Assert.True(new TxNoiseSetup { Compressor = loaded }.SameContentAs(new TxNoiseSetup { Compressor = want }));
                    // Every field is the operator's afterwards, and changing the copy never changes the preset.
                    loaded.ThresholdDb -= 3;
                    loaded.LowBand.RatioToOne += 1;
                    Assert.NotEqual(loaded.ThresholdDb, preset.Settings.ThresholdDb);
                    Assert.NotEqual(loaded.LowBand.RatioToOne, preset.Settings.LowBand.RatioToOne);
                }
                // Nothing in a preset is out of the stage's range (it would be silently clamped).
                var s = preset.Settings;
                var applied = s.ToSettings();
                Assert.Equal((s.ThresholdDb, s.RatioToOne, s.AttackMs, s.ReleaseMs, s.KneeDb, s.MakeupDb, s.DriveDb, s.RotatorStages),
                    (applied.ThresholdDb, applied.RatioToOne, applied.AttackMs, applied.ReleaseMs, applied.KneeDb, applied.MakeupDb, applied.DriveDb, applied.RotatorStages));
                Assert.StartsWith("audio.compressor.preset.", preset.NameKey);
            }
            Assert.Same(TxCompressorPresets.All[2], TxCompressorPresets.Find(" DX "));
            Assert.Null(TxCompressorPresets.Find("nope"));
            // The plain preset IS the stage's defaults; the drive and the rotator come on only in a preset that says so.
            Assert.Equal(TxCompressorSettings.Default, TxCompressorPresets.Find("plain")!.Settings.ToSettings());
            Assert.Equal((6f, 4), (TxCompressorPresets.Find("dx")!.Settings.DriveDb, TxCompressorPresets.Find("dx")!.Settings.RotatorStages));
            Assert.All(TxCompressorPresets.All.Where(p => p.Id != "dx"), p => Assert.Equal((0f, 0), (p.Settings.DriveDb, p.Settings.RotatorStages)));
        }

        [Theory]
        [InlineData("plain", 6.0)]
        [InlineData("ragchew", 6.0)]
        [InlineData("dx", 6.0)]
        [InlineData("level", 6.0)]
        public void A_single_band_preset_leaves_a_voice_peaking_at_minus_six_near_where_it_came_in(string id, double peakInDb)
        {
            var s = TxCompressorPresets.Find(id)!.Settings;
            double driven = -peakInDb + s.DriveDb;
            double outDb = driven - TxCompressor.ReductionFor(driven, s.ThresholdDb, 1 - 1.0 / s.RatioToOne, s.KneeDb) + s.MakeupDb;
            // "Same peak, more average power": within 2.5 dB of the -6 dBFS it came in at, and never past full scale once settled.
            Assert.InRange(outDb, -8.5, -5.0);
        }
    }
}
