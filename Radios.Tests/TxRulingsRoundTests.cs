using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Flex.Smoothlake.FlexLib;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track H2, the evening rulings round (2026-11-18): S16's held
    /// unkey (the rig half and the conditioner's drain), S13's automatic
    /// faster-capture try (its policy, the partial-run rule, and the re-open's
    /// wiring held equal to the first open's), and S13's busy test. Nothing here
    /// opens an audio device or touches a radio: the rig is an unconnected
    /// FlexBase whose command queue is switched to collect writes rather than
    /// send them, and the process globals its constructor writes are put back.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class TxRulingsRoundTests : IDisposable
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly DateTime Now = new(2026, 11, 18, 22, 0, 0, DateTimeKind.Utc);
        private static readonly long Cheap = TxCallbackRate.CostBudgetNanos - 1;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-h2rr-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        // ── S13: the automatic try's policy ──

        private static TxCallbackVerdict Verdict(bool passed, DateTime measured) => new()
        {
            Device = "Mic", HostApi = 13, SampleRate = 48000, Channels = 1,
            CallbacksPerSecond = TxCallbackRate.Fast, Passed = passed, MeasuredUtc = measured,
        };

        private static TxTryDecision Ask(int? overrideRate = null, long? cost = -2, TxCallbackVerdict? verdict = null,
            int rate = TxCallbackRate.Shipped, bool tried = false, bool keyed = false, bool busy = false, bool running = true)
            => TxCallbackRate.ShouldTry(overrideRate, cost == -2 ? Cheap : cost, verdict, Now, rate, tried, keyed, busy, running);

        [Fact]
        public void The_try_runs_once_the_engines_qualify_and_no_verdict_is_held_only_while_receiving_and_unbusy()
        {
            Assert.Equal(TxTryAnswer.Try, Ask().Answer);                                     // positive control
            Assert.Equal(TxTryAnswer.Try, Ask(verdict: Verdict(true, Now.AddDays(-31))).Answer);   // an expired pass
            Assert.Equal(TxTryAnswer.Try, Ask(verdict: Verdict(false, Now.AddDays(-31))).Answer);  // an expired failure
            Assert.Equal(TxTryAnswer.NotNow, Ask(keyed: true).Answer);
            Assert.Equal(TxTryAnswer.NotNow, Ask(busy: true).Answer);
            Assert.Equal(TxTryAnswer.NotNow, Ask(running: false).Answer);
            Assert.Equal(TxTryAnswer.NotNow, Ask(cost: null).Answer);                         // engines not yet qualified
            Assert.Equal(TxTryAnswer.NotNow, Ask(cost: TxCallbackRate.CostBudgetNanos).Answer);
            Assert.Equal(TxTryAnswer.NotThisConnection, Ask(verdict: Verdict(true, Now.AddDays(-1))).Answer);
            Assert.Equal(TxTryAnswer.NotThisConnection, Ask(verdict: Verdict(false, Now.AddDays(-1))).Answer);
            Assert.Equal(TxTryAnswer.NotThisConnection, Ask(tried: true).Answer);
            Assert.Equal(TxTryAnswer.NotThisConnection, Ask(rate: TxCallbackRate.Fast).Answer);
            var forced = Ask(overrideRate: 20);
            Assert.Equal(TxTryAnswer.NotThisConnection, forced.Answer);
            Assert.Contains(AudioBuffering.TxCallbackRateEnvironmentVariable, forced.Reason);
        }

        [Fact]
        public void A_running_try_fails_on_any_dropout_and_passes_after_twenty_clean_seconds()
        {
            int need = TxCallbackRate.Fast * TxCallbackRate.MinimumSecondsForVerdict;
            Assert.Equal(TxTryProgress.Running, TxCallbackRate.TryProgress(need - 1, 0, 0, TxCallbackRate.Fast));
            Assert.Equal(TxTryProgress.Passed, TxCallbackRate.TryProgress(need, 0, 0, TxCallbackRate.Fast));
            Assert.Equal(TxTryProgress.Failed, TxCallbackRate.TryProgress(10, 1, 0, TxCallbackRate.Fast));
            Assert.Equal(TxTryProgress.Failed, TxCallbackRate.TryProgress(need * 2, 0, 1, TxCallbackRate.Fast));
        }

        [Fact]
        public void A_partial_run_earns_no_verdict_either_way()
        {
            var mic = new TxCaptureIdentity("Mic", 13, 48000, 1);
            long plenty = TxCallbackRate.Fast * TxCallbackRate.MinimumSecondsForVerdict * 2L;
            Assert.True(TxCallbackRate.Judge(mic, TxCallbackRate.Fast, plenty, 0, 0, Now)!.Passed);    // positive control: a pass
            Assert.False(TxCallbackRate.Judge(mic, TxCallbackRate.Fast, plenty, 3, 0, Now)!.Passed);   // positive control: a failure
            Assert.Null(TxCallbackRate.Judge(mic, TxCallbackRate.Fast, plenty, 0, 0, Now, partialRun: true));
            Assert.Null(TxCallbackRate.Judge(mic, TxCallbackRate.Fast, plenty, 3, 0, Now, partialRun: true));
        }

        [Fact]
        public void The_try_waits_while_a_check_your_audio_take_holds_the_transmit_audio()
        {
            using var rig = new Rig();
            Assert.False(rig.Base.TxCheckOrRecordingRunning);                  // positive control
            var lease = (System.Threading.SemaphoreSlim)typeof(FlexBase).GetField("_iqLease", Inst)!.GetValue(rig.Base)!;
            Assert.True(lease.Wait(0));
            try { Assert.True(rig.Base.TxCheckOrRecordingRunning); }
            finally { lease.Release(); }
            Assert.False(rig.Base.TxCheckOrRecordingRunning);
        }

        /// <summary>
        /// There is ONE transmit capture open path (Sprint 49 Track H2, the join
        /// with the receive-audio track): the first open in remoteAudioProc, the
        /// automatic try, its fall back, the swap's rescue and the re-open after
        /// a death all call OpenTxCaptureAt, which wires every stream the same
        /// way. Until the join this test held two copies of the wiring equal; now
        /// it holds that no second copy can appear.
        /// </summary>
        [Fact]
        public void The_reopen_wires_the_stream_exactly_as_the_first_open_does()
        {
            string root = RepoRoot();
            string remote = File.ReadAllText(Path.Combine(root, "Radios", "FlexBase.RemoteAudio.cs"));
            string tryFile = File.ReadAllText(Path.Combine(root, "Radios", "FlexBase.TxCaptureTry.cs"));
            int open = tryFile.IndexOf("private JJAudioStream OpenTxCaptureAt(int rate, RemoteAudioWake wake)", StringComparison.Ordinal);
            int close = tryFile.IndexOf("private enum TxSwapResult", open, StringComparison.Ordinal);
            Assert.True(open > 0 && close > open, "the one open path was not found");
            string path = tryFile.Substring(open, close - open);

            static string[] Wiring(string block) => Regex.Matches(block,
                    @"(?:\b\w+(?:\.PortAudioStream)?\.(InputSource|InputProcessor|InputLufsMeter|StreamDiedHook)\s*=\s*[^;]+;)|(txConditioner\.KeySource\s*=\s*[^;]+;)|((?:txPipeline|pipeline)\.KeySource\s*=\s*[^;]+;)")
                .Select(m => Regex.Replace(m.Value, @"\s+", " ")).ToArray();

            // Positive control: the open path wires all six (five, and this run's death hook).
            Assert.Equal(6, Wiring(path).Length);
            Assert.Contains("OpenOpus(Devices.DeviceTypes.input, asked, sendOpusInput,", Regex.Replace(path, @"\s+", " "));
            Assert.Contains("OpusTxEncodeProfile", path);

            // The first open goes through it, with this run's wake.
            int first = remote.IndexOf("opusInputChannel = new audioChannelData(txStream, \"JJFlexRadio.OpusInputChan\");", StringComparison.Ordinal);
            int setup = remote.IndexOf("Tracing.TraceLine(\"remoteAudioProc:Opus Input Channel setup\"", first, StringComparison.Ordinal);
            Assert.True(first > 0 && setup > first, "the first open's block was not found in remoteAudioProc");
            string firstOpen = remote.Substring(first, setup - first);
            Assert.Contains("OpenTxCaptureAt(TxAudioCallbacksPerSecond, wake)", firstOpen);

            // And nowhere else opens a transmit capture or wires one: not the first open, not anything new.
            string outside = remote + tryFile.Substring(0, open) + tryFile.Substring(close);
            Assert.DoesNotContain("OpenOpus(Devices.DeviceTypes.input", Regex.Replace(outside, @"\s+", " "));
            // The one wiring line outside it is the receive speaker's own death
            // hook, in buildSpeakerStream (the receive-audio track's).
            Assert.Equal(new[] { "stream.StreamDiedHook = wake.Signal;" }, Wiring(outside));
            Assert.Contains("private bool buildSpeakerStream(", remote);
        }

        // ── S16: the held unkey, the rig's half ──

        /// <summary>An unconnected FlexBase whose command queue collects writes, with the globals its constructor writes put back.</summary>
        private sealed class Rig : IDisposable
        {
            private readonly string _programName = API.ProgramName;
            private readonly bool _isGui = API.IsGUI;
            private readonly Action<string> _traceSink = Vita.VitaSocket.TraceSink;
            public readonly FlexBase Base;
            public readonly List<(int Ms, Action End)> Scheduled = new();

            public Rig()
            {
                try { Base = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true }; }
                catch { Restore(); throw; }
                Base.q.MainLoop = true; // collect, do not send
                Base.HeldUnkeyScheduler = (ms, end) => Scheduled.Add((ms, end));
            }

            public void Keyed(bool on) => typeof(FlexBase).GetField("_Transmit", Inst)!.SetValue(Base, on);

            /// <summary>One keyed buffer through the conditioner, with a reducer that reports <paramref name="delaySamples"/> of delay.</summary>
            public void Delay(int delaySamples)
            {
                var c = Base.TxConditioner;
                c.NoiseReducer = (_, _, _) => { };
                c.NoiseReducerAppliedLatencySamples = () => delaySamples;
                c.KeySource = () => true;
                c.Process(new float[960], 960, 48000);
            }

            public List<string> Writes()
            {
                var names = new List<string>();
                while (Base.q.Count > 0) names.Add(Base.q.Dequeue().Name);
                return names;
            }

            private void Restore()
            {
                API.ProgramName = _programName;
                API.IsGUI = _isGui;
                Vita.VitaSocket.TraceSink = _traceSink;
            }

            public void Dispose()
            {
                try { Base?.Dispose(); } catch { }
                finally { Restore(); }
            }
        }

        [Fact]
        public void The_hold_is_the_measured_delay_plus_ten_percent_and_none_at_zero()
        {
            Assert.Equal(110, FlexBase.HeldUnkeyMs(100));
            Assert.Equal(198, FlexBase.HeldUnkeyMs(180));
            Assert.Equal(12, FlexBase.HeldUnkeyMs(10.5));   // rounded up
            Assert.Equal(0, FlexBase.HeldUnkeyMs(0));
            Assert.Equal(0, FlexBase.HeldUnkeyMs(-1));
        }

        [Fact]
        public void An_ordinary_unkey_waits_for_the_noise_delay_then_unkeys_and_drains_meanwhile()
        {
            using var rig = new Rig();
            rig.Keyed(true);
            rig.Delay(4800);                          // 100 ms at 48 kHz, as the reducer reported it
            rig.Base.UnkeyAfterNoiseDelay("test");
            Assert.True(rig.Base.HeldUnkeyPending);
            Assert.Equal(110, rig.Base.LastHeldUnkeyMs);  // the delay plus 10%: the hold's minimum
            // Rulings repair 1: the release schedules only the backstop; the end
            // proper waits for the drained tail to be handed on (see
            // TxRulingsRepairOneTests). This rig never sends a buffer, so the
            // backstop is what ends it.
            Assert.Equal(FlexBase.HeldUnkeyBackstopMs(100, FlexBase.TxAudioCallbacksPerSecond), Assert.Single(rig.Scheduled).Ms);
            Assert.Empty(rig.Writes());               // nothing written to the radio yet
            Assert.True(rig.Base.TxConditioner.DrainInput);
            rig.Scheduled[0].End();                   // the backstop runs out
            Assert.False(rig.Base.HeldUnkeyPending);
            Assert.Equal(new[] { "Mox" }, rig.Writes());
        }

        [Fact]
        public void With_no_noise_delay_the_unkey_is_immediate()
        {
            using var rig = new Rig();
            rig.Keyed(true);
            rig.Delay(0);
            rig.Base.UnkeyAfterNoiseDelay("test");
            Assert.False(rig.Base.HeldUnkeyPending);
            Assert.Empty(rig.Scheduled);
            Assert.Equal(new[] { "Mox" }, rig.Writes());
            Assert.False(rig.Base.TxConditioner.DrainInput);
        }

        [Fact]
        public void A_stale_delay_or_an_unkeyed_radio_does_not_hold()
        {
            using var rig = new Rig();
            rig.Delay(4800);
            rig.Keyed(false);                          // the radio already unkeyed (an external unkey's sync)
            rig.Base.UnkeyAfterNoiseDelay("test");
            Assert.Empty(rig.Scheduled);
            Assert.Equal(0, rig.Base.TxConditioner.RecentNoiseDelayMs(Environment.TickCount64 + TxAudioConditioner.DelayFreshnessMs + 1));
        }

        [Fact]
        public void A_key_down_during_the_hold_cancels_the_unkey_and_the_over_carries_on()
        {
            using var rig = new Rig();
            rig.Keyed(true);
            rig.Delay(4800);
            rig.Base.UnkeyAfterNoiseDelay("test");
            rig.Base.Transmit = true;                 // pressed again inside the hold
            Assert.False(rig.Base.HeldUnkeyPending);
            Assert.False(rig.Base.TxConditioner.DrainInput); // the microphone is heard again
            rig.Scheduled[0].End();                   // the old hold's end arrives late
            Assert.Equal(new[] { "Mox" }, rig.Writes()); // only the key-down's write; no unkey
        }

        [Fact]
        public void A_safety_unkey_during_the_hold_goes_at_once_and_the_hold_ends_with_it()
        {
            using var rig = new Rig();
            rig.Keyed(true);
            rig.Delay(4800);
            rig.Base.UnkeyAfterNoiseDelay("test");
            rig.Base.Transmit = false;                // the hard limit, the kill, the app closing
            Assert.False(rig.Base.HeldUnkeyPending);
            Assert.Equal(new[] { "Mox" }, rig.Writes());
            rig.Scheduled[0].End();
            Assert.Empty(rig.Writes());               // the hold's end writes nothing more
        }

        // ── S16: the conditioner's drain ──

        /// <summary>A reducer that delays by a fixed number of samples per channel, as the engines do.</summary>
        private sealed class DelayLine
        {
            private readonly Queue<float> _q = new();
            public DelayLine(int samples) { for (int i = 0; i < samples * 2; i++) _q.Enqueue(0f); }
            public void Process(float[] b, int count, uint rate)
            {
                for (int i = 0; i < count; i++) { _q.Enqueue(b[i]); b[i] = _q.Dequeue(); }
            }
        }

        private static float[] Ones(int n) { var a = new float[n]; Array.Fill(a, 1f); return a; }

        [Fact]
        public void While_draining_the_radio_gets_the_tail_then_silence_never_newer_microphone_audio()
        {
            const int delay = 480, buffer = 960;      // 10 ms of delay; 10 ms stereo buffers
            var c = new TxAudioConditioner();
            var line = new DelayLine(delay);
            c.NoiseReducer = line.Process;
            c.NoiseReducerAppliedLatencySamples = () => delay;
            bool keyed = true;
            c.KeySource = () => keyed;
            for (int i = 0; i < 3; i++) c.Process(Ones(buffer), buffer, 48000);   // speech, filling the delay

            // Positive control: undrained, the microphone keeps coming through.
            var control = Ones(buffer);
            c.Process(control, buffer, 48000);
            Assert.All(control, s => Assert.Equal(1f, s));

            c.DrainInput = true;                      // the release
            var first = Ones(buffer);                 // the microphone still hears speech
            c.Process(first, buffer, 48000);
            Assert.All(first.Take(delay * 2), s => Assert.Equal(1f, s));       // the tail: audio from before the release
            Assert.All(first.Skip(delay * 2), s => Assert.Equal(0f, s));       // then nothing newer
            var second = Ones(buffer);
            c.Process(second, buffer, 48000);
            Assert.All(second, s => Assert.Equal(0f, s));

            keyed = false;                            // the radio unkeyed: the drain ends with the over
            c.Process(Ones(buffer), buffer, 48000);
            Assert.False(c.DrainInput);
        }

        [Fact]
        public void The_delay_recorded_is_the_reducers_own_and_zero_when_stood_aside()
        {
            var c = new TxAudioConditioner { NoiseReducer = (_, _, _) => { }, NoiseReducerAppliedLatencySamples = () => 4800 };
            c.Process(new float[960], 960, 48000);
            Assert.Equal(100.0, c.RecentNoiseDelayMs(Environment.TickCount64), 6);
            c.BypassAll = true;                       // CW or digital
            c.Process(new float[960], 960, 48000);
            Assert.Equal(0.0, c.RecentNoiseDelayMs(Environment.TickCount64));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("no JJFlexRadio.sln above " + AppContext.BaseDirectory);
        }
    }
}
