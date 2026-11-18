using System;
using System.IO;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track H, item D: the transmit capture runs faster than the
    /// shipped ten callbacks a second only when this machine has earned it by
    /// BOTH measurements (the helper's own frame cost, and a dropout-free run
    /// of the microphone at that rate); the bench can force a rate for one
    /// launch; a faster run is judged from PortAudio's own dropout flags.
    /// </summary>
    public class TxCallbackRateTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-txcb-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        private static TxCallbackVerdict Verdict(bool passed) => new() { Device = "Mic", CallbacksPerSecond = TxCallbackRate.Fast, Passed = passed };

        [Fact]
        public void Both_measurements_are_needed_for_the_faster_rate()
        {
            long cheap = TxCallbackRate.CostBudgetNanos - 1, dear = TxCallbackRate.CostBudgetNanos;
            Assert.Equal(TxCallbackRate.Fast, TxCallbackRate.Decide(null, cheap, Verdict(true)).CallbacksPerSecond);
            Assert.Equal(TxCallbackRate.Shipped, TxCallbackRate.Decide(null, null, Verdict(true)).CallbacksPerSecond);   // no benchmark
            Assert.Equal(TxCallbackRate.Shipped, TxCallbackRate.Decide(null, dear, Verdict(true)).CallbacksPerSecond);   // too slow
            Assert.Equal(TxCallbackRate.Shipped, TxCallbackRate.Decide(null, cheap, null).CallbacksPerSecond);            // never measured
            Assert.Equal(TxCallbackRate.Shipped, TxCallbackRate.Decide(null, cheap, Verdict(false)).CallbacksPerSecond);  // dropped audio
            Assert.Equal(10, TxCallbackRate.Shipped);
        }

        [Fact]
        public void The_bench_override_wins_and_says_so()
        {
            var d = TxCallbackRate.Decide(20, null, null);
            Assert.Equal(20, d.CallbacksPerSecond);
            Assert.Contains(AudioBuffering.TxCallbackRateEnvironmentVariable, d.Reason);
            Assert.Equal(50, AudioBuffering.ParseTxCallbackRate(" 50 "));
            Assert.Null(AudioBuffering.ParseTxCallbackRate(""));
            Assert.Null(AudioBuffering.ParseTxCallbackRate("0"));
            Assert.Null(AudioBuffering.ParseTxCallbackRate("101"));
            Assert.Null(AudioBuffering.ParseTxCallbackRate("fast"));
        }

        [Fact]
        public void A_faster_run_is_judged_from_its_dropout_flags_and_a_short_or_shipped_run_proves_nothing()
        {
            var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
            long enough = (long)TxCallbackRate.Fast * TxCallbackRate.MinimumSecondsForVerdict;
            Assert.True(TxCallbackRate.Judge("Mic", 50, enough, 0, 0, now)!.Passed);
            Assert.False(TxCallbackRate.Judge("Mic", 50, enough, 1, 0, now)!.Passed);
            Assert.False(TxCallbackRate.Judge("Mic", 50, enough, 0, 3, now)!.Passed);
            Assert.Null(TxCallbackRate.Judge("Mic", 50, enough - 1, 0, 0, now));
            Assert.Null(TxCallbackRate.Judge("Mic", TxCallbackRate.Shipped, 100000, 0, 0, now));
        }

        [Fact]
        public void Verdicts_are_kept_per_device_and_rate_and_a_newer_one_replaces_the_older()
        {
            var store = new TxCallbackVerdictStore(_root);
            Assert.Null(store.Find("Mic", 50));
            Assert.True(store.Record(new TxCallbackVerdict { Device = "Mic", CallbacksPerSecond = 50, Passed = false }));
            Assert.True(store.Record(new TxCallbackVerdict { Device = "Headset", CallbacksPerSecond = 50, Passed = true }));
            Assert.True(store.Record(new TxCallbackVerdict { Device = "mic", CallbacksPerSecond = 50, Passed = true }));
            var back = new TxCallbackVerdictStore(_root);
            Assert.True(back.Find("MIC", 50)!.Passed);
            Assert.True(back.Find("Headset", 50)!.Passed);
            Assert.Null(back.Find("Mic", 20));

            File.WriteAllText(Path.Combine(_root, TxCallbackVerdictStore.FileName), "not xml");
            Assert.Null(new TxCallbackVerdictStore(_root).Find("Mic", 50)); // unreadable reads as never measured: the shipped rate
        }

        [Fact]
        public void The_default_store_lives_under_the_settings_root()
        {
            Assert.NotNull(TxCallbackVerdictStore.Default());
            Assert.False(string.IsNullOrEmpty(RadioConfig.AppDataRoot));
        }
    }
}
