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
    /// Sprint 49 Track H2, the evening rulings round (2026-11-18), as carried
    /// without the unkey hold (Noel's H1 = B, 2026-11-19; the hold's tests stay
    /// on sprint49/h2-join): S13's automatic faster-capture try (its policy, the
    /// partial-run rule, and the one transmit open path), and S13's busy test.
    /// Nothing here
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

        /// <summary>An unconnected FlexBase, with the globals its constructor writes put back.</summary>
        private sealed class Rig : IDisposable
        {
            private readonly string _programName = API.ProgramName;
            private readonly bool _isGui = API.IsGUI;
            private readonly Action<string> _traceSink = Vita.VitaSocket.TraceSink;
            public readonly FlexBase Base;

            public Rig()
            {
                try { Base = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true }; }
                catch { Restore(); throw; }
                Base.q.MainLoop = true; // collect, do not send
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
