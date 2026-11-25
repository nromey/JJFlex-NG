using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.Serialization;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track J phase 2 (#736): JJ Clarity as a member of the transmit
    /// conditioner (the one-owner record, the one owner of its passband edges,
    /// the key discipline, the early-return gate, the bypass), in the frame
    /// pipeline, and in the saved setup record. Radio-neutral; every file lives
    /// in a scratch folder; nothing here opens a window, a device or a radio.
    /// </summary>
    public sealed class TxClarityChainTests : IDisposable
    {
        private const int Fs = 48000;
        private const int Buf = 960;   // 480 stereo frames, the transmit stream's buffer
        private const string Radio = "1234-5678-9012-3456";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjf-track-j2-" + Guid.NewGuid().ToString("N"));

        public TxClarityChainTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>A 1 kHz tone, whole cycles per buffer, so buffers join without a step.</summary>
        private static float[] Tone(double amplitude, int buffers = 1, double hz = 1000)
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

        /// <summary>Brightness that a 1 kHz tone excites: the even harmonic from 2 kHz, driven hard, well mixed.</summary>
        private static readonly ClaritySettings Bright = new(
            new ClarityBand(true, 2000f, 18f, 60f, ClarityHarmonics.Even),
            new ClarityBand(false, 150f, 6f, 25f, ClarityHarmonics.Both));

        private static bool SameBits(float[] a, float[] b, int from = 0, int count = -1)
        {
            if (count < 0) count = a.Length - from;
            for (int i = from; i < from + count; i++)
                if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false;
            return true;
        }

        // ── the stage ids and the order (item 1) ──

        [Fact]
        public void The_default_order_puts_clarity_after_the_de_esser_and_before_the_compressor_and_both_older_defaults_run_as_the_default()
        {
            var order = TxAudioConditioner.StageIds.DefaultOrder;
            Assert.Equal(new[] { "noise", "expander", "eq", "deesser", "clarity", "compressor", "limiter" }, order);
            Assert.Equal("clarity", TxAudioConditioner.StageIds.Clarity);
            Assert.Equal(Array.IndexOf(order, "deesser") + 1, Array.IndexOf(order, "clarity"));
            Assert.Equal(Array.IndexOf(order, "clarity") + 1, Array.IndexOf(order, "compressor"));
            Assert.Equal(TxAudioConditioner.StageIds.Limiter, order[^1]);   // the limiter stays last
            // The defaults earlier builds wrote: Track I's four, and Track CL's six, which this track's seven replaced.
            Assert.True(TxAudioConditioner.StageIds.RunsAsDefault(new[] { "noise", "expander", "eq", "deesser" }));
            Assert.True(TxAudioConditioner.StageIds.RunsAsDefault(new[] { "noise", "expander", "eq", "deesser", "compressor", "limiter" }));
            Assert.Contains(TxAudioConditioner.StageIds.HistoricalDefaults, h => h.SequenceEqual(new[] { "noise", "expander", "eq", "deesser", "compressor", "limiter" }));
            // A custom order, and one no build wrote, is somebody's own.
            Assert.False(TxAudioConditioner.StageIds.RunsAsDefault(new[] { "noise", "expander", "eq", "clarity", "deesser", "compressor", "limiter" }));
            Assert.False(TxAudioConditioner.StageIds.RunsAsDefault(new[] { "noise", "expander", "eq", "deesser", "clarity" }));
        }

        /// <summary>A setup file as an EARLIER build wrote it: today's serializer's output with JJ Clarity's element taken out and the order cut to that build's default. Generated here; never copied from anyone's settings.</summary>
        private TxNoiseSetup SaveAsAnEarlierBuildWould(string name, string[] thatBuildsOrder, bool withDynamics)
        {
            var store = new TxNoiseSetupStore(_root);
            var setup = new TxNoiseSetup { Name = name };
            setup.Gate.Enabled = true;
            setup.Eq.Enabled = true;
            setup.Limiter.Enabled = withDynamics;
            Assert.True(store.Save(Radio, setup).Succeeded);
            string path = store.PathFor(Radio)!;
            var doc = XDocument.Load(path);
            var record = doc.Descendants("TxNoiseSetup").Single(r => (string?)r.Element("Name") == name);
            record.Element("Clarity")!.Remove();
            if (!withDynamics) { record.Element("Compressor")!.Remove(); record.Element("Limiter")!.Remove(); }
            var order = record.Element("StageOrder")!;
            order.Elements().Where(x => !thatBuildsOrder.Contains(x.Value)).Remove();
            Assert.Equal(thatBuildsOrder, order.Elements().Select(x => x.Value));
            doc.Save(path);
            Assert.DoesNotContain("Clarity", File.ReadAllText(path));
            Assert.DoesNotContain("clarity", File.ReadAllText(path));
            return new TxNoiseSetupStore(_root).Find(Radio, name)!;
        }

        [Theory]
        [InlineData(true)]    // Track CL's build: six stage ids, the compressor and the limiter, no JJ Clarity
        [InlineData(false)]   // Track I's build: four stage ids, none of the three
        public void A_setup_saved_with_an_older_default_order_loads_with_clarity_off_and_runs_as_the_default(bool sixStages)
        {
            var thatOrder = sixStages
                ? new[] { "noise", "expander", "eq", "deesser", "compressor", "limiter" }
                : new[] { "noise", "expander", "eq", "deesser" };
            var loaded = SaveAsAnEarlierBuildWould("Desk mic", thatOrder, withDynamics: sixStages);
            Assert.False(loaded.Clarity.Enabled);                                    // older files sound exactly as they did
            Assert.Equal(ClaritySettings.Default, loaded.Clarity.ToSettings());      // and a first switch-on gets a new Clarity's settings
            Assert.True(loaded.Gate.Enabled);
            Assert.True(loaded.Eq.Enabled);
            Assert.Equal(sixStages, loaded.Limiter.Enabled);
            Assert.True(loaded.RunsDefaultOrder);
            // The older default is this build's default, said in full, so two spellings of one order never read as different setups.
            Assert.Equal(TxAudioConditioner.StageIds.DefaultOrder, loaded.StageOrder);
            var same = new TxNoiseSetup { Name = "Desk mic" };
            same.Gate.Enabled = true; same.Eq.Enabled = true; same.Limiter.Enabled = sixStages;
            Assert.True(loaded.SameContentAs(same));
        }

        [Fact]
        public void A_custom_order_is_kept_exactly_and_is_not_the_default()
        {
            var store = new TxNoiseSetupStore(_root);
            // JJ Clarity before the de-esser: the order the harness walk renders for comparison.
            var custom = new[] { "noise", "expander", "eq", "clarity", "deesser", "compressor", "limiter" };
            Assert.True(store.Save(Radio, new TxNoiseSetup { Name = "Swapped", StageOrder = custom }).Succeeded);
            var back = new TxNoiseSetupStore(_root).Find(Radio, "Swapped")!;
            Assert.Equal(custom, back.StageOrder);
            Assert.False(back.RunsDefaultOrder);
        }

        // ── the stage in the chain (items 3 and 4) ──

        [Fact]
        public void A_new_chain_has_clarity_off_with_its_default_settings_and_nothing_active()
        {
            var cond = new TxAudioConditioner();
            Assert.False(cond.Clarity.Enabled);
            Assert.False(cond.Clarity.IsActive);
            Assert.False(cond.AnyStageActive);
            Assert.Equal(ClaritySettings.Default, cond.Clarity.Settings);
            Assert.False(cond.GateInForce.ClarityEnabled);
            Assert.NotNull(cond.GateInForce.ClarityDesign);
            var tone = Tone(0.25, 6);
            Assert.True(SameBits(tone, RunChain(cond, tone)));
        }

        [Fact]
        public void Clarity_alone_makes_the_chain_active_and_shapes_the_audio_and_bypass_stands_it_aside()
        {
            var cond = new TxAudioConditioner();
            cond.SetGateInForce(Record(g => { g.ClarityEnabled = true; g.Clarity = Bright; }));
            // The record in force says so before a buffer has run (TxDifferentialCapture asks off the audio thread).
            Assert.True(cond.AnyStageActive);
            var tone = Tone(0.25, 20);
            var sent = RunChain(cond, tone);
            Assert.True(cond.Clarity.IsActive);
            Assert.True(cond.AnyStageActive);
            // It ADDED: the second half (the fade-in long done) is not the input, and its second harmonic is there.
            Assert.False(SameBits(tone, sent, tone.Length / 2));
            Assert.True(Harmonic(sent, 2000) > -40, $"the second harmonic reads {Harmonic(sent, 2000):F1} dBFS");
            Assert.True(Harmonic(tone, 2000) < -100);   // the control: the input has none

            // On with a design that adds nothing is not an active stage, and touches nothing.
            var idle = new TxAudioConditioner();
            idle.SetGateInForce(Record(g => { g.ClarityEnabled = true; g.Clarity = new ClaritySettings(Bright.High.WithMix(0), Bright.Low); }));
            Assert.False(idle.AnyStageActive);
            Assert.True(SameBits(tone, RunChain(idle, tone)));

            // CW and digital: the whole chain stands aside, Clarity with it.
            cond.BypassAll = true;
            Assert.False(cond.AnyStageActive);
            Assert.True(SameBits(tone, RunChain(cond, tone)));
        }

        /// <summary>The level of one frequency in the last half second of interleaved stereo, dBFS (a single-bin correlation).</summary>
        private static double Harmonic(float[] stereo, double hz)
        {
            int frames = stereo.Length / 2, n = Math.Min(frames, Fs / 2), start = frames - n;
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double w = 2 * Math.PI * hz * (start + i) / Fs;
                re += stereo[2 * (start + i)] * Math.Cos(w);
                im += stereo[2 * (start + i)] * Math.Sin(w);
            }
            return 20 * Math.Log10(Math.Max(1e-12, 2 * Math.Sqrt(re * re + im * im) / n));
        }

        [Fact]
        public void Clarity_runs_after_the_de_esser_and_before_the_compressor_and_the_limiter_holds_what_it_adds()
        {
            // The order the stages are called in, read from the one place they are called.
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "JJPortaudio", "JJPortaudio", "TxAudioConditioner.cs"));
            int deEsser = source.IndexOf("DeEsser.Process(buffer", StringComparison.Ordinal);
            int clarity = source.IndexOf("Clarity.Process(buffer", StringComparison.Ordinal);
            int compressor = source.IndexOf("Compressor.Process(buffer", StringComparison.Ordinal);
            int limiter = source.LastIndexOf("Limiter.Process(buffer", StringComparison.Ordinal);
            Assert.True(deEsser > 0 && deEsser < clarity && clarity < compressor && compressor < limiter,
                $"de-esser {deEsser}, clarity {clarity}, compressor {compressor}, limiter {limiter}");

            // And by what it does: a hot tone that Clarity pushes past full scale is held by the limiter after it.
            bool keyed = true;
            var cond = new TxAudioConditioner { KeySource = () => keyed };
            cond.SetGateInForce(Record(g => { g.ClarityEnabled = true; g.Clarity = Bright; g.Limiter = new TxLimiterSettings { Enabled = true }; }));
            var sent = RunChain(cond, Tone(0.95, 30));
            Assert.True(sent.Max(Math.Abs) <= 1f, $"left the chain at {sent.Max(Math.Abs)}");
            var clarityOver = cond.TakeClarityOverStats();
            var (_, limiterOver) = cond.TakeDynamicsOverStats();
            Assert.True(clarityOver.OverFullScale > 100, "Clarity's output never went past full scale, so this proves nothing");
            Assert.True(limiterOver.Caught > 100);
            Assert.Equal(0, limiterOver.OutputOverFullScale);
        }

        [Fact]
        public void Clarity_adds_no_delay_and_what_was_removed_is_silent_with_it_on()
        {
            var cond = new TxAudioConditioner { MonitorMode = TxAudioConditioner.MonitorModes.Residual };
            double residual = 0;
            cond.MonitorSink = (b, n, r) => { for (int i = 0; i < n; i++) residual += b[i] * (double)b[i]; };
            cond.SetGateInForce(Record(g => { g.ClarityEnabled = true; g.Clarity = Bright; }));
            var tone = Tone(0.25, 20);
            var sent = RunChain(cond, tone);
            Assert.Equal(0, cond.AppliedDelayFrames);
            Assert.Equal(0, cond.ResidualDelayFrames);
            Assert.Equal(0, cond.Clarity.LatencyFrames);
            Assert.False(SameBits(tone, sent, tone.Length / 2));   // the control: what is SENT did change
            Assert.Equal(0.0, residual);                           // and nothing was "removed"
        }

        [Fact]
        public void Clarity_counts_keyed_buffers_only_and_the_older_readings_keep_their_shape()
        {
            bool keyed = false;
            var cond = new TxAudioConditioner { KeySource = () => keyed };
            cond.SetGateInForce(Record(g => { g.ClarityEnabled = true; g.Clarity = Bright; }));
            RunChain(cond, Tone(0.25, 10));
            var unkeyed = cond.TakeClarityOverStats();
            Assert.Equal(0, unkeyed.Samples);
            Assert.True(cond.Clarity.AddingNowDbfs > -60f);      // the on-demand reading still sees it
            keyed = true;
            RunChain(cond, Tone(0.25, 10));
            var over = cond.TakeClarityOverStats();
            Assert.Equal(10 * Buf, over.Samples);
            Assert.True(over.AddedRmsDbfs > -60 && over.AddedRmsDbfs < 0, $"added {over.AddedRmsDbfs:F1} dBFS");
            Assert.Equal(0, over.OverFullScale);
            Assert.Equal(0, cond.TakeClarityOverStats().Samples);   // taken is zeroed
            // Track I's reading is the same three figures it was (item 4: Clarity's are taken separately).
            (long eqOver, long deEsserFrames, float deEsserDeepest) = cond.TakeOverStats();
            Assert.Equal((0L, 0L), (eqOver, deEsserFrames));
            Assert.Equal(0f, deEsserDeepest);
        }

        // ── the key discipline ──

        [Fact]
        public void A_clarity_switch_made_keyed_waits_for_the_unkey_and_a_lever_is_live()
        {
            bool keyed = true;
            var cond = new TxAudioConditioner { KeySource = () => keyed };
            var tone = Tone(0.25);
            RunChain(cond, tone);
            cond.RequestSwitch(g => g.ClarityEnabled = true);
            RunChain(cond, tone);
            Assert.False(cond.Clarity.Enabled);                 // mid-over: not yet
            Assert.True(cond.HasStagedGate);
            // A lever turned meanwhile is in force at once, and does not carry the waiting switch with it.
            cond.ChangeGate(g => g.Clarity = new ClaritySettings(g.Clarity.High.WithMix(70f), g.Clarity.Low));
            RunChain(cond, tone);
            Assert.False(cond.Clarity.Enabled);
            Assert.Equal(70f, cond.Clarity.Settings.High.MixPercent);
            keyed = false;
            RunChain(cond, tone);
            Assert.True(cond.Clarity.Enabled);                  // at the unkey
            Assert.Equal(70f, cond.Clarity.Settings.High.MixPercent);
            Assert.False(cond.HasStagedGate);
        }

        // ── the one owner of the passband edges (item 2) ──

        [Fact]
        public void A_setup_staged_while_keyed_runs_at_the_unkey_with_edges_changed_after_it_was_staged()
        {
            bool keyed = true;
            var cond = new TxAudioConditioner { KeySource = () => keyed };
            cond.SetClarityPassband(100f, 2900f);
            var tone = Tone(0.25);
            RunChain(cond, tone);
            // Stage A: Clarity on, tune 2 kHz. On a 2.9 kHz filter that tune is not clamped.
            cond.StageGate(Record(g => { g.ClarityEnabled = true; g.Clarity = Bright; }));
            // Then the transmit filter is narrowed, while A still waits.
            cond.SetClarityPassband(100f, 2000f);
            RunChain(cond, tone);
            Assert.False(cond.Clarity.Enabled);                                    // A has not landed
            Assert.Equal(2000f, cond.GateInForce.ClarityDesign!.PassbandHighEdgeHz); // the record in force was rebuilt too
            Assert.Equal(2000f, cond.ClarityPassbandHighEdgeHz);
            keyed = false;
            RunChain(cond, tone);
            // A runs, with its own settings and the NEW edges: nothing resurrected the edges A was staged under.
            var running = cond.Clarity.Wanted;
            Assert.True(cond.Clarity.Enabled);
            Assert.Equal(Bright, running.Settings);
            Assert.Equal(100f, running.PassbandLowEdgeHz);
            Assert.Equal(2000f, running.PassbandHighEdgeHz);
            Assert.True(cond.Clarity.HighTuneClamped);
            Assert.Equal(1600f, cond.Clarity.AppliedHighTuneHz, 1);   // 0.8 of the 2 kHz ceiling
            Assert.Equal(2000f, cond.Clarity.Settings.High.TuneHz);   // the intent is kept
            Assert.Same(running, cond.GateInForce.ClarityDesign);     // the stage runs the record's own design
        }

        [Fact]
        public void An_edge_change_rebuilds_the_record_in_force_and_the_waiting_one_each_with_its_own_settings()
        {
            bool keyed = true;
            var cond = new TxAudioConditioner { KeySource = () => keyed };
            var gentle = new ClaritySettings(Bright.High.WithMix(10f), Bright.Low);
            cond.SetGateInForce(Record(g => { g.ClarityEnabled = true; g.Clarity = gentle; }));
            cond.StageGate(Record(g => { g.ClarityEnabled = true; g.Clarity = Bright; }));
            cond.SetClarityPassband(200f, 2400f);
            var tone = Tone(0.25);
            RunChain(cond, tone);
            Assert.Equal(gentle, cond.Clarity.Wanted.Settings);          // in force: its own settings, the new edges
            Assert.Equal(2400f, cond.Clarity.Wanted.PassbandHighEdgeHz);
            Assert.Equal(200f, cond.Clarity.Wanted.PassbandLowEdgeHz);
            keyed = false;
            RunChain(cond, tone);
            Assert.Equal(Bright, cond.Clarity.Wanted.Settings);          // adopted: ITS settings, the same new edges
            Assert.Equal(2400f, cond.Clarity.Wanted.PassbandHighEdgeHz);
        }

        [Fact]
        public void Unchanged_edges_publish_nothing_and_an_unknown_edge_is_zero()
        {
            var cond = new TxAudioConditioner();
            cond.SetClarityPassband(100f, 2900f);
            var was = cond.GateInForce;
            cond.SetClarityPassband(100f, 2900f);                 // the policy tick, twice a second
            Assert.Same(was, cond.GateInForce);
            cond.SetClarityPassband(100f, 2700f);
            Assert.NotSame(was, cond.GateInForce);
            Assert.Equal(2700f, cond.GateInForce.ClarityDesign!.PassbandHighEdgeHz);
            cond.SetClarityPassband(float.NaN, -5f);              // not a filter: unknown, nothing clamped
            Assert.Equal((0f, 0f), (cond.ClarityPassbandLowEdgeHz, cond.ClarityPassbandHighEdgeHz));
        }

        [Fact]
        public async Task An_edge_update_racing_a_pending_edit_ends_with_the_newest_edges_and_the_newest_edit()
        {
            // The audio thread adopts unkeyed while two control threads publish: one the edges, one an edit.
            for (int round = 0; round < 25; round++)
            {
                var cond = new TxAudioConditioner();
                cond.SetGateInForce(Record(g => { g.ClarityEnabled = true; g.Clarity = Bright; }));
                using var stop = new CancellationTokenSource();
                var audio = Task.Run(() =>
                {
                    var buf = Tone(0.25);
                    while (!stop.IsCancellationRequested) cond.Process((float[])buf.Clone(), Buf, Fs);
                });
                var edges = Task.Run(() => { for (int i = 0; i < 200; i++) cond.SetClarityPassband(100f + i, 2000f + i); });
                var edits = Task.Run(() =>
                {
                    for (int i = 0; i < 200; i++)
                    {
                        float mix = 10f + i % 80;
                        if (i % 2 == 0) cond.RequestSwitch(g => g.Clarity = new ClaritySettings(g.Clarity.High.WithMix(mix), g.Clarity.Low));
                        else cond.ChangeGate(g => g.Clarity = new ClaritySettings(g.Clarity.High.WithMix(mix), g.Clarity.Low));
                    }
                });
                await Task.WhenAll(edges, edits);
                stop.Cancel();
                await audio;
                cond.Process(Tone(0.25), Buf, Fs);   // one settled buffer
                var running = cond.Clarity.Wanted;
                Assert.Equal(299f, running.PassbandLowEdgeHz);       // the last edges asked for
                Assert.Equal(2199f, running.PassbandHighEdgeHz);
                Assert.Equal(10f + 199 % 80, running.Settings.High.MixPercent);   // the last edit asked for
                Assert.Same(running, cond.GateInForce.ClarityDesign);
                Assert.False(cond.HasStagedGate);
            }
        }

        [Fact]
        public void Adopting_a_waiting_clarity_record_and_an_edge_change_allocate_nothing_on_the_audio_thread()
        {
            bool keyed = false;
            var cond = new TxAudioConditioner { KeySource = () => keyed };
            var buf = Tone(0.25);
            cond.SetClarityPassband(100f, 2900f);
            cond.SetGateInForce(Record(g => { g.ClarityEnabled = true; g.Clarity = Bright; }));
            for (int i = 0; i < 20; i++) cond.Process(buf, Buf, Fs);   // warm: every path jitted, every fade done
            long least = long.MaxValue;
            for (int run = 0; run < 5; run++)
            {
                keyed = true;
                cond.Process(buf, Buf, Fs);
                // Built here, on the control side: a waiting record, then new edges for it and for the one in force.
                float mix = 20f + run;
                cond.StageGate(Record(g => { g.ClarityEnabled = true; g.Clarity = new ClaritySettings(Bright.High.WithMix(mix), Bright.Low); }));
                cond.SetClarityPassband(100f, 2500f + run);
                keyed = false;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 6; i++) cond.Process(buf, Buf, Fs);   // adopts the edges, then the record, and fades
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
                Assert.Equal(mix, cond.Clarity.Settings.High.MixPercent);
            }
            Assert.Equal(0, least);
        }

        // ── the early-return gate (item 3) ──

        [Fact]
        public void Switching_clarity_off_when_it_is_the_only_stage_fades_its_added_path_out_and_does_not_cut_it_dead()
        {
            var cond = new TxAudioConditioner();
            cond.SetGateInForce(Record(g => { g.ClarityEnabled = true; g.Clarity = Bright; }));
            var tone = Tone(0.25);
            for (int i = 0; i < 20; i++) cond.Process((float[])tone.Clone(), Buf, Fs);
            cond.RequestSwitch(g => g.ClarityEnabled = false);          // unkeyed: it lands on the next buffer
            var first = (float[])tone.Clone();
            cond.Process(first, Buf, Fs);
            // The first buffer after the switch: the stage reads as off and no fade had begun, and it is STILL
            // adding, on its way out. Skipping the stages here would have cut the added path in one sample.
            Assert.False(cond.Clarity.Enabled);
            Assert.False(SameBits(tone, first));
            double firstQuarter = 0, lastQuarter = 0;
            for (int i = 0; i < Buf / 4; i++) firstQuarter += Math.Abs(first[i] - tone[i]);
            for (int i = 3 * Buf / 4; i < Buf; i++) lastQuarter += Math.Abs(first[i] - tone[i]);
            Assert.True(lastQuarter < firstQuarter, $"the added path is not falling: {firstQuarter:F3} then {lastQuarter:F3}");
            // The fade is 20 ms: over by the end of the second buffer, and from then on the chain touches nothing.
            cond.Process((float[])tone.Clone(), Buf, Fs);
            var later = (float[])tone.Clone();
            cond.Process(later, Buf, Fs);
            Assert.True(SameBits(tone, later));
            Assert.False(cond.Clarity.IsActive);
            Assert.False(cond.Clarity.RunsOnThisBuffer);
            Assert.False(cond.AnyStageActive);
            // And it comes back on cleanly afterwards (a stage left half-adopted would not).
            cond.RequestSwitch(g => g.ClarityEnabled = true);
            var back = RunChain(cond, Tone(0.25, 10));
            Assert.True(Harmonic(back, 2000) > -40);
        }

        // ── the frame pipeline: a tone bypasses it, a file source does not ──

        private sealed class VoiceSource : ITxInputSource
        {
            private long _frame;
            public bool Engaged => true;
            public bool Idle => false;
            public bool BypassesConditioning { get; set; }
            public void Process(float[] b, int n, uint rate)
            {
                for (int i = 0; i + 1 < n; i += 2, _frame++)
                {
                    float v = (float)(0.25 * Math.Sin(2 * Math.PI * 1000 * _frame / rate));
                    b[i] = v; b[i + 1] = v;
                }
            }
        }

        [Theory]
        [InlineData(true)]    // the calibrated tone: the chain must not touch it
        [InlineData(false)]   // a recording sent through the chain: shaped like the microphone
        public void A_source_that_bypasses_conditioning_bypasses_clarity_and_one_that_does_not_is_shaped(bool bypasses)
        {
            var cond = new TxAudioConditioner();
            cond.SetGateInForce(Record(g => { g.ClarityEnabled = true; g.Clarity = Bright; }));
            var encoded = new List<float>();
            var p = new TxFramePipeline
            {
                Source = new VoiceSource { BypassesConditioning = bypasses },
                Conditioner = cond.Process,
                Encode = b => { encoded.AddRange(b.Take(Buf)); return new byte[1]; },
            };
            for (int i = 0; i < 40; i++) Assert.True(p.Emit(new float[Buf], Buf, Fs));
            double second = Harmonic(encoded.ToArray(), 2000);
            if (bypasses) Assert.True(second < -100, $"the tone was shaped: {second:F1} dBFS at 2 kHz");
            else Assert.True(second > -40, $"the file source was not shaped: {second:F1} dBFS at 2 kHz");
        }

        // ── the saved record (J5) ──

        private static TxNoiseSetup Full() => new()
        {
            Name = "Bright",
            Gate = new TxNoiseGateSetup { Enabled = true },
            Clarity = new TxClaritySetup
            {
                Enabled = true, PresetId = ClarityPresets.Fuller,
                HighBand = new TxClarityBandSetup { Enabled = true, TuneHz = 1800, DriveDb = 15, MixPercent = 35, Harmonics = TxClarityHarmonics.Odd },
                LowBand = new TxClarityBandSetup { Enabled = true, TuneHz = 120, DriveDb = 9, MixPercent = 30, Harmonics = TxClarityHarmonics.Both },
            },
        };

        [Fact]
        public void A_setups_clarity_defaults_to_off_with_a_new_claritys_settings()
        {
            var s = new TxNoiseSetup();
            Assert.False(s.Clarity.Enabled);
            Assert.False(s.Clarity.AddsSomething);
            Assert.Equal(ClaritySettings.Default, s.Clarity.ToSettings());
            Assert.True(s.Clarity.HighBand.Enabled);    // a new Clarity: brightness first
            Assert.False(s.Clarity.LowBand.Enabled);
            Assert.Equal("", s.Clarity.PresetId);
            s.Clarity.Enabled = true;
            Assert.True(s.Clarity.AddsSomething);
        }

        [Fact]
        public void A_full_record_round_trips_through_the_store_and_a_preset_file()
        {
            var store = new TxNoiseSetupStore(_root);
            Assert.True(store.Save(Radio, Full()).Succeeded);
            var back = new TxNoiseSetupStore(_root).Find(Radio, "Bright")!;
            Assert.True(back.SameContentAs(Full()));
            Assert.True(back.Clarity.Enabled);
            Assert.Equal(Full().Clarity.ToSettings(), back.Clarity.ToSettings());
            Assert.Equal(ClarityPresets.Fuller, back.Clarity.PresetId);
            Assert.Equal(TxAudioConditioner.StageIds.DefaultOrder, back.StageOrder);
            // A second save changes nothing.
            Assert.True(store.Save(Radio, back).Succeeded);
            Assert.True(new TxNoiseSetupStore(_root).Find(Radio, "Bright")!.SameContentAs(Full()));

            string file = Path.Combine(_root, "p.xml");
            using (var fs = File.Create(file)) new XmlSerializer(typeof(AudioChainPreset)).Serialize(fs, new AudioChainPreset { Name = "P", TransmitNoise = Full() });
            Assert.True(AudioChainPreset.TryLoad(file, out var preset, out _));
            Assert.True(preset!.TransmitNoise!.SameContentAs(Full()));
            Assert.Equal(Full().Clarity.ToSettings(), preset.TransmitNoise.Clarity.ToSettings());
        }

        public static IEnumerable<object[]> Fields() => new[]
        {
            "Enabled", "High.Enabled", "High.TuneHz", "High.DriveDb", "High.MixPercent", "High.Harmonics",
            "Low.Enabled", "Low.TuneHz", "Low.DriveDb", "Low.MixPercent", "Low.Harmonics",
        }.Select(f => new object[] { f });

        [Theory]
        [MemberData(nameof(Fields))]
        public void SameContentAs_sees_a_change_in_each_clarity_field(string field)
        {
            var a = Full();
            var b = Full();
            Assert.True(a.SameContentAs(b));
            var c = b.Clarity;
            switch (field)
            {
                case "Enabled": c.Enabled = false; break;
                case "High.Enabled": c.HighBand.Enabled = false; break;
                case "High.TuneHz": c.HighBand.TuneHz += 100; break;
                case "High.DriveDb": c.HighBand.DriveDb += 1; break;
                case "High.MixPercent": c.HighBand.MixPercent += 1; break;
                case "High.Harmonics": c.HighBand.Harmonics = TxClarityHarmonics.Even; break;
                case "Low.Enabled": c.LowBand.Enabled = false; break;
                case "Low.TuneHz": c.LowBand.TuneHz += 10; break;
                case "Low.DriveDb": c.LowBand.DriveDb += 1; break;
                case "Low.MixPercent": c.LowBand.MixPercent += 1; break;
                case "Low.Harmonics": c.LowBand.Harmonics = TxClarityHarmonics.Even; break;
                default: throw new InvalidOperationException(field);
            }
            Assert.False(a.SameContentAs(b), field + " changed and the two still read as the same setup");
        }

        [Fact]
        public void Clone_copies_clarity_and_its_bands_deeply()
        {
            var a = Full();
            var b = a.Clone();
            Assert.True(a.SameContentAs(b));
            Assert.NotSame(a.Clarity, b.Clarity);
            Assert.NotSame(a.Clarity.HighBand, b.Clarity.HighBand);
            Assert.NotSame(a.Clarity.LowBand, b.Clarity.LowBand);
            b.Clarity.HighBand.MixPercent = 99;
            b.Clarity.LowBand.Enabled = false;
            Assert.Equal(35f, a.Clarity.HighBand.MixPercent);
            Assert.True(a.Clarity.LowBand.Enabled);
        }

        [Fact]
        public void A_harmonics_word_this_build_does_not_know_runs_as_even_and_is_kept_on_save()
        {
            var store = new TxNoiseSetupStore(_root);
            var later = Full();
            later.Name = "Later";
            later.Clarity.HighBand.Harmonics = "fifth";
            Assert.True(store.Save(Radio, later).Succeeded);
            var back = new TxNoiseSetupStore(_root).Find(Radio, "Later")!;
            Assert.Equal("fifth", back.Clarity.HighBand.Harmonics);
            Assert.Equal(ClarityHarmonics.Even, back.Clarity.ToSettings().High.Harmonics);
        }

        [Fact]
        public void Every_clarity_preset_loads_as_listed_keeps_the_whole_switch_as_it_was_and_notes_its_id()
        {
            foreach (var preset in ClarityPresets.All)
            foreach (bool on in new[] { false, true })
            {
                var loaded = TxClaritySetup.Load(preset, on);
                Assert.Equal(on, loaded.Enabled);
                Assert.Equal(preset.Id, loaded.PresetId);
                Assert.Equal(preset.Settings, loaded.ToSettings());
                // A setup made from settings and read back is those settings: the two shapes agree field for field.
                Assert.Equal(preset.Settings, TxClaritySetup.From(loaded.ToSettings(), on).ToSettings());
            }
        }

        // ── the suggest pass's input carries the whole switch (item 6) ──

        [Fact]
        public void With_the_whole_stage_off_the_suggest_input_reads_each_band_as_off_and_keeps_every_stored_value()
        {
            var stored = new ClaritySettings(
                new ClarityBand(true, 1800f, 24f, 40f, ClarityHarmonics.Odd),
                new ClarityBand(true, 120f, 9f, 30f, ClarityHarmonics.Both));
            var on = new Radios.Listenback.ClaritySuggestInput(stored, StageEnabled: true);
            Assert.Same(stored, on.EffectiveCurrent);
            Assert.True(new Radios.Listenback.ClaritySuggestInput(stored).StageEnabled);   // the default: on, as phase 1 assumed
            var off = new Radios.Listenback.ClaritySuggestInput(stored, StageEnabled: false).EffectiveCurrent;
            Assert.True(off.AddsNothing);
            Assert.False(off.High.Enabled);
            Assert.False(off.Low.Enabled);
            // Every stored lever is kept: the drive floor and the harmonics are still the operator's.
            Assert.Equal(stored.High.WithEnabled(false), off.High);
            Assert.Equal(stored.Low.WithEnabled(false), off.Low);
        }

        // ── two renders of one take, matched for listening (the suggest pass's before and after) ──

        /// <summary>A voice-like test signal at 48 kHz: tones in half-second words with near-silence between.</summary>
        private static float[] Words(double amplitude, int seconds = 8)
        {
            var mono = new float[Fs * seconds];
            for (int i = 0; i < mono.Length; i++)
            {
                bool word = (i / (Fs / 2)) % 2 == 0;
                double t = i / (double)Fs;
                mono[i] = (float)((word ? amplitude : amplitude * 0.003) * (0.6 * Math.Sin(2 * Math.PI * 1200 * t) + 0.35 * Math.Sin(2 * Math.PI * 700 * t)));
            }
            return mono;
        }

        [Fact]
        public void A_pair_of_renders_is_lined_up_matched_on_speech_loudness_and_scaled_together()
        {
            var before = Words(0.1);
            var after = before.Select(v => v * 2f).ToArray();   // the same take, 6 dB louder
            var pair = ChainAudition.MatchPair(before, after, 0, Fs);
            Assert.True(pair.LevelMatched);
            Assert.Equal(-6.02f, pair.MatchGainDb, 1);
            Assert.Equal(before.Length, pair.Before.Length);
            for (int i = 0; i < before.Length; i += 997) Assert.Equal(pair.Before[i], pair.After[i], 3);   // matched: the level difference is gone
            // The latency is trimmed from both, so the take's first sample is the first played.
            var late = ChainAudition.MatchPair(before, after, 480, Fs);
            Assert.Equal(before.Length - 480, late.Before.Length);
            Assert.Equal(before[480], late.Before[0]);
            // One scale for both: a pair that would clip is turned down together, and stays matched.
            var hot = ChainAudition.MatchPair(Words(0.9), Words(0.9).Select(v => v * 4f).ToArray(), 0, Fs);
            Assert.True(hot.After.Max(Math.Abs) <= 1f && hot.Before.Max(Math.Abs) <= 1f);
            // No speech to match on: not matched, and said so by the flag, never a made-up gain.
            var silent = ChainAudition.MatchPair(new float[Fs * 4], new float[Fs * 4], 0, Fs);
            Assert.False(silent.LevelMatched);
            Assert.Equal(0f, silent.MatchGainDb);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
        }
    }
}
