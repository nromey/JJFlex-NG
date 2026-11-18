using System;
using System.IO;
using JJPortaudio;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 49 Track H, item D, and repair round 1 R7: the transmit capture
    /// runs faster than the shipped ten callbacks a second only when this
    /// machine has earned it by BOTH measurements (the helper's own frame cost,
    /// and a dropout-free run of THIS capture, by name, host API and format, at
    /// that rate, within the last 30 days); the bench can force a rate for one
    /// launch; a faster run is judged from PortAudio's own dropout flags.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class TxCallbackRateTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-txcb-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        private static readonly TxCaptureIdentity Mic = new("Mic", 13, 48000, 1);

        private static TxCallbackVerdict Verdict(bool passed, DateTime? measured = null) => new()
        {
            Device = Mic.Device, HostApi = Mic.HostApi, SampleRate = Mic.SampleRate, Channels = Mic.Channels,
            CallbacksPerSecond = TxCallbackRate.Fast, Passed = passed, MeasuredUtc = measured ?? Now.AddDays(-1),
        };

        [Fact]
        public void Both_measurements_are_needed_for_the_faster_rate()
        {
            long cheap = TxCallbackRate.CostBudgetNanos - 1, dear = TxCallbackRate.CostBudgetNanos;
            Assert.Equal(TxCallbackRate.Fast, TxCallbackRate.Decide(null, cheap, Verdict(true), Now).CallbacksPerSecond);
            Assert.Equal(TxCallbackRate.Shipped, TxCallbackRate.Decide(null, null, Verdict(true), Now).CallbacksPerSecond);   // no benchmark
            Assert.Equal(TxCallbackRate.Shipped, TxCallbackRate.Decide(null, dear, Verdict(true), Now).CallbacksPerSecond);   // too slow
            Assert.Equal(TxCallbackRate.Shipped, TxCallbackRate.Decide(null, cheap, null, Now).CallbacksPerSecond);            // never measured
            Assert.Equal(TxCallbackRate.Shipped, TxCallbackRate.Decide(null, cheap, Verdict(false), Now).CallbacksPerSecond);  // dropped audio
            Assert.Equal(10, TxCallbackRate.Shipped);
        }

        [Fact]
        public void A_pass_older_than_thirty_days_is_not_trusted()
        {
            long cheap = TxCallbackRate.CostBudgetNanos - 1;
            Assert.Equal(TxCallbackRate.Fast, TxCallbackRate.Decide(null, cheap, Verdict(true, Now.AddDays(-29)), Now).CallbacksPerSecond);
            var stale = TxCallbackRate.Decide(null, cheap, Verdict(true, Now.AddDays(-31)), Now);
            Assert.Equal(TxCallbackRate.Shipped, stale.CallbacksPerSecond);
            Assert.Contains("measuring again", stale.Reason);
        }

        [Fact]
        public void The_bench_override_wins_and_says_so()
        {
            var d = TxCallbackRate.Decide(20, null, null, Now);
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
            long enough = (long)TxCallbackRate.Fast * TxCallbackRate.MinimumSecondsForVerdict;
            var pass = TxCallbackRate.Judge(Mic, 50, enough, 0, 0, Now)!;
            Assert.True(pass.Passed);
            Assert.True(pass.Describes(Mic));
            Assert.False(TxCallbackRate.Judge(Mic, 50, enough, 1, 0, Now)!.Passed);
            Assert.False(TxCallbackRate.Judge(Mic, 50, enough, 0, 3, Now)!.Passed);
            Assert.Null(TxCallbackRate.Judge(Mic, 50, enough - 1, 0, 0, Now));
            Assert.Null(TxCallbackRate.Judge(Mic, TxCallbackRate.Shipped, 100000, 0, 0, Now));
        }

        [Fact]
        public void A_verdict_belongs_to_one_device_host_API_and_format()
        {
            var store = new TxCallbackVerdictStore(_root);
            Assert.True(store.Record(Verdict(true)));
            var back = new TxCallbackVerdictStore(_root);
            Assert.True(back.Find(Mic with { Device = "MIC" }, 50)!.Passed);       // names compare without case
            Assert.Null(back.Find(Mic with { HostApi = 2 }, 50));                  // another host API
            Assert.Null(back.Find(Mic with { SampleRate = 24000 }, 50));           // another rate
            Assert.Null(back.Find(Mic with { Channels = 2 }, 50));                 // stereo, not mono
            Assert.Null(back.Find(Mic with { Device = "Headset" }, 50));
            Assert.Null(back.Find(Mic, 20));

            Assert.True(store.Record(Verdict(false)));                            // a newer one replaces it
            Assert.False(new TxCallbackVerdictStore(_root).Find(Mic, 50)!.Passed);

            File.WriteAllText(Path.Combine(_root, TxCallbackVerdictStore.FileName), "not xml");
            Assert.Null(new TxCallbackVerdictStore(_root).Find(Mic, 50)); // unreadable reads as never measured: the shipped rate
        }

        [Fact]
        public void The_rate_is_worked_out_again_when_the_helpers_benchmark_arrives()
        {
            var storeBefore = TxCallbackRate.StoreForTest;
            var captureBefore = TxCallbackRate.CurrentCapture;
            var costBefore = TxCallbackRate.HelperFrameCostP99Nanos;
            try
            {
                var store = new TxCallbackVerdictStore(_root);
                Assert.True(store.Record(Verdict(true, DateTime.UtcNow.AddDays(-1))));
                TxCallbackRate.StoreForTest = store;
                TxCallbackRate.CurrentCapture = Mic;
                TxCallbackRate.CurrentRate = TxCallbackRate.Shipped;
                TxCallbackRate.HelperFrameCostP99Nanos = null;
                TxCallbackRate.Reconsider();
                Assert.Equal(TxCallbackRate.Shipped, TxCallbackRate.NextOpen!.CallbacksPerSecond);

                TxCallbackRate.HelperFrameCostP99Nanos = 1_000_000; // the handshake: reconsidered off this thread
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (TxCallbackRate.NextOpen!.CallbacksPerSecond != TxCallbackRate.Fast && DateTime.UtcNow < deadline)
                    System.Threading.Thread.Sleep(5);
                Assert.Equal(TxCallbackRate.Fast, TxCallbackRate.NextOpen!.CallbacksPerSecond);
            }
            finally
            {
                TxCallbackRate.StoreForTest = storeBefore;
                TxCallbackRate.CurrentCapture = captureBefore;
                TxCallbackRate.HelperFrameCostP99Nanos = costBefore;
            }
        }

        [Fact]
        public void The_default_store_lives_under_the_settings_root()
        {
            Assert.NotNull(TxCallbackVerdictStore.Default());
            Assert.False(string.IsNullOrEmpty(RadioConfig.AppDataRoot));
        }
    }
}
