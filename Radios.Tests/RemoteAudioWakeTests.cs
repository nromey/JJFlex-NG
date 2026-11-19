using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Flex.Smoothlake.Vita;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1156: the remote receive loop waits on <see cref="RemoteAudioWake"/>
    /// instead of spinning on <c>Thread.Yield()</c> at Highest priority (the
    /// 2026-11-18 bench measured the spin at 98.6 to 99.4 percent of a core).
    /// <para>No window, no audio device, no radio, and nothing is spoken. The
    /// timing assertions are ONE-SIDED on purpose: a test process holds no
    /// WASAPI stream, so its timer resolution is about 15.6 ms and a 5 ms
    /// wait really lasts up to about that.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RemoteAudioWakeTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(RemoteAudioWakeTests));
        public void Dispose() => _scope.Dispose();

        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [Fact]
        public void A_signal_before_the_wait_is_never_lost()
        {
            using var wake = new RemoteAudioWake();
            wake.Signal();
            var sw = Stopwatch.StartNew();
            Assert.True(wake.WaitForWork(5000));
            Assert.True(sw.ElapsedMilliseconds < 1000,
                "a signal set before the wait must end it at once; it took " + sw.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void An_unsignalled_wait_lasts_at_least_its_timeout_and_says_so()
        {
            using var wake = new RemoteAudioWake();
            var sw = Stopwatch.StartNew();
            Assert.False(wake.WaitForWork(30));
            // One-sided: the timer may round up, never meaningfully down.
            Assert.True(sw.ElapsedMilliseconds >= 25, "the wait returned after " + sw.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void Several_signals_while_busy_collapse_into_one_wake()
        {
            using var wake = new RemoteAudioWake();
            wake.Signal(); wake.Signal(); wake.Signal();
            Assert.True(wake.WaitForWork(0));
            Assert.False(wake.WaitForWork(0));
        }

        [Fact]
        public void A_disposed_wake_never_throws_and_never_blocks()
        {
            var wake = new RemoteAudioWake();
            wake.Dispose();
            wake.Signal();
            wake.Dispose();
            var sw = Stopwatch.StartNew();
            Assert.True(wake.WaitForWork(5000));
            Assert.True(sw.ElapsedMilliseconds < 1000);
        }

        [Fact]
        public void Signals_racing_the_disposal_never_throw_on_the_signalling_thread()
        {
            for (int round = 0; round < 50; round++)
            {
                var wake = new RemoteAudioWake();
                Exception? fault = null;
                var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
                {
                    try { for (int i = 0; i < 2000; i++) wake.Signal(); }
                    catch (Exception ex) { fault = ex; }
                })).ToArray();
                foreach (var t in threads) t.Start();
                wake.Dispose();
                foreach (var t in threads) t.Join();
                Assert.Null(fault);
            }
        }

        /// <summary>
        /// Stock FlexLib's packet event wakes it. The positive control is that
        /// the wake is NOT set before the packet is added, and
        /// <c>ListenbackIntegrityTests</c> proves the event fires once per Opus
        /// packet by the same reflection route.
        /// </summary>
        [Fact]
        public void A_receive_packet_arriving_wakes_the_loop()
        {
            var radio = new VendorRadioFixture("1156-WAKE").Radio;
            var stream = new RXAudioStream(radio);
            using var wake = new RemoteAudioWake();
            RXAudioStream.OpusPacketReceivedEventHandler handler = wake.Signal;
            stream.OpusPacketReceived += handler;

            Assert.False(wake.WaitForWork(0));
            typeof(RXAudioStream).GetMethod("AddRXData", Private, null,
                new[] { typeof(VitaOpusDataPacket) }, null)!.Invoke(stream,
                new object[] { new VitaOpusDataPacket { TimestampInt = 1, payload = new byte[] { 1 } } });
            Assert.True(wake.WaitForWork(0));

            // And unhooked, a packet no longer reaches a disposed wake.
            stream.OpusPacketReceived -= handler;
            wake.Dispose();
            typeof(RXAudioStream).GetMethod("AddRXData", Private, null,
                new[] { typeof(VitaOpusDataPacket) }, null)!.Invoke(stream,
                new object[] { new VitaOpusDataPacket { TimestampInt = 2, payload = new byte[] { 1 } } });
        }

        /// <summary>
        /// A loop of the receive loop's idle shape, with nothing arriving,
        /// runs a bounded number of passes. The spin it replaced made millions
        /// in the same 200 ms.
        /// </summary>
        [Fact]
        public void An_idle_loop_runs_at_a_bounded_rate()
        {
            using var wake = new RemoteAudioWake();
            int passes = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 200)
            {
                passes++;
                wake.WaitForWork(RemoteAudioWake.IdleWakeMilliseconds);
            }
            Assert.True(passes >= 5, "the loop barely ran: " + passes + " passes");
            Assert.True(passes < 100, "an idle loop made " + passes + " passes in 200 ms; it is spinning");
        }

        [Fact]
        public void The_idle_wait_is_short_and_named()
        {
            Assert.InRange(RemoteAudioWake.IdleWakeMilliseconds, 1, 20);
            Assert.Equal(10, RemoteAudioWake.DisconnectingWaitMilliseconds);
        }

        /// <summary>A key edge signals the current run's wake; a repeat of the same state does not.</summary>
        [Fact]
        public void A_key_change_wakes_the_loop_and_a_repeat_does_not()
        {
            using var rig = new RigOnVendorRadio("1156-KEY-0001");
            var wakeField = typeof(FlexBase).GetField("_remoteAudioWake", Private)!;
            var noteMox = typeof(FlexBase).GetMethod("noteRadioMox", Private)!;
            Assert.NotNull(wakeField); Assert.NotNull(noteMox);
            using var wake = new RemoteAudioWake();
            wakeField.SetValue(rig.Rig, wake);

            noteMox.Invoke(rig.Rig, new object[] { true });
            Assert.True(wake.WaitForWork(0), "a key-down did not wake the loop");
            noteMox.Invoke(rig.Rig, new object[] { true });
            Assert.False(wake.WaitForWork(0), "a repeated key-down woke the loop; only an edge should");
            noteMox.Invoke(rig.Rig, new object[] { false });
            Assert.True(wake.WaitForWork(0), "a key-up did not wake the loop");
            wakeField.SetValue(rig.Rig, null);
        }

        /// <summary>
        /// A stop request ends an idle wait at once. The stand-in audio thread
        /// waits up to five seconds on the run's wake; the stop must release it
        /// long before that.
        /// </summary>
        [Fact]
        public void A_stop_request_ends_the_wait_at_once()
        {
            using var rig = new RigOnVendorRadio("1156-STOP-0001");
            var wakeField = typeof(FlexBase).GetField("_remoteAudioWake", Private)!;
            var pcAudioField = typeof(FlexBase).GetField("_PCAudio", Private)!;
            var threadField = typeof(FlexBase).GetField("remoteAudioThread", Private)!;
            var stopMethod = typeof(FlexBase).GetMethod("stopRemoteAudioThread", Private)!;
            Assert.True(wakeField != null && pcAudioField != null && threadField != null && stopMethod != null);

            using var wake = new RemoteAudioWake();
            wakeField.SetValue(rig.Rig, wake);
            long waited = -1;
            using var started = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                started.Set();
                var sw = Stopwatch.StartNew();
                wake.WaitForWork(5000);
                waited = sw.ElapsedMilliseconds;
            }) { IsBackground = true };
            thread.Start();
            started.Wait();
            Thread.Sleep(50);
            pcAudioField.SetValue(rig.Rig, true);
            threadField.SetValue(rig.Rig, thread);

            stopMethod.Invoke(rig.Rig, null);
            Assert.False(thread.IsAlive);
            Assert.True(waited >= 0 && waited < 2000, "the stop did not end the wait; it lasted " + waited + " ms");
            pcAudioField.SetValue(rig.Rig, false);
            wakeField.SetValue(rig.Rig, null);
        }

        // ── Source scans of the loop itself ──

        private static string RemoteAudioProcCode()
        {
            string source = FlexBasePartials.JoinedText();
            const string signature = "private void remoteAudioProc(long run)";
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "remoteAudioProc was not found; every scan below would be vacuous");
            int next = source.IndexOf("\n        private ", start + signature.Length, StringComparison.Ordinal);
            string body = source.Substring(start, (next < 0 ? source.Length : next) - start);
            Assert.True(body.Length > 2000, "the scan read only " + body.Length + " characters of remoteAudioProc");
            // Code only: the comments are allowed to remember the spin.
            return string.Join("\n", body.Split('\n').Select(line =>
            {
                int c = line.IndexOf("//", StringComparison.Ordinal);
                return c < 0 ? line : line.Substring(0, c);
            }));
        }

        [Fact]
        public void The_loop_no_longer_yields_and_its_idle_paths_wait_on_the_wake()
        {
            string code = RemoteAudioProcCode();
            Assert.DoesNotContain("Thread.Yield(", code);
            Assert.DoesNotContain("Thread.Sleep(10)", code);
            Assert.Contains("wake.WaitForWork(RemoteAudioWake.IdleWakeMilliseconds)", code);
            Assert.Contains("wake.WaitForWork(RemoteAudioWake.DisconnectingWaitMilliseconds)", code);
        }

        [Fact]
        public void The_packet_wake_is_hooked_unhooked_and_retired_by_this_run_alone()
        {
            string code = RemoteAudioProcCode();
            Assert.Contains("rxForWake.OpusPacketReceived += packetWake", code);
            Assert.Contains("rxForWake.OpusPacketReceived -= packetWake", code);
            Assert.Contains("Interlocked.CompareExchange(ref _remoteAudioWake, null, wake)", code);
            Assert.Contains("wake.Dispose()", code);
            // Unhooked before anything else in the teardown.
            int done = code.IndexOf("remoteDone:", StringComparison.Ordinal);
            int unhook = code.IndexOf("rxForWake.OpusPacketReceived -= packetWake", StringComparison.Ordinal);
            int firstClose = code.IndexOf("stopOpusOutputChannel()", done, StringComparison.Ordinal);
            Assert.True(done >= 0 && unhook > done && unhook < firstClose,
                "the packet wake must be unhooked first in remoteDone, before any close");
            // Retired last.
            int retire = code.IndexOf("wake.Dispose()", StringComparison.Ordinal);
            int exiting = code.IndexOf("remoteAudioProc exiting", StringComparison.Ordinal);
            Assert.True(retire > firstClose && retire < exiting);
        }
    }
}
