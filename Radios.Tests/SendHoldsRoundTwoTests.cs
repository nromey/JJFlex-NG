#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Codex round 2 on the Track B fix round (#835): the VOX reply observer's
    /// deadlock, the monitor put-back across a real reconnect, and the
    /// interlock line kept between leases.
    /// </summary>
    public sealed class SendHoldsRoundTwoTests
    {
        private static readonly MethodInfo Raise =
            typeof(Flex.UiWpfFramework.Mvvm.ObservableObject).GetMethod("RaisePropertyChanged",
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null)!;

        private static void RaiseTrace(Radio radio) => Raise.Invoke(radio, new object[] { nameof(Radio.TelnetTraceOutput) });

        // ── Finding 1: the VOX reply observer ─────────────────────────────────

        /// <summary>
        /// The cycle, scheduled deterministically rather than hammered. The
        /// command thread does what <c>Radio.SendCommand</c> does: holds the
        /// TOPIC list, then wants the SEQUENCE list. The reply thread does what
        /// <c>Radio.ParseReply</c> does: holds the sequence list and the output
        /// list while it raises the trace's change, and the change is the VOX
        /// write's reply. On the old code the observer disposed its trace tap
        /// right there, and the tap's last lease removes FlexLib topics, which
        /// wants the topic list the command thread holds: neither finishes.
        /// The threads are background threads and the joins are bounded, so the
        /// old code fails this test rather than hanging the run.
        /// </summary>
        [Fact]
        public void A_vox_reply_and_a_traced_command_cannot_deadlock()
        {
            Assert.NotNull(Raise);
            var f = new RigOnVendorRadio("vox-reply-lock-test");
            Radio radio = f.Vendor.Radio;
            const string command = "transmit set vox_enable=0";
            Type replyType = typeof(FlexBase).GetNestedType("VoxWriteReply", BindingFlags.NonPublic)!;
            Assert.NotNull(replyType);
            var reply = (IDisposable)Activator.CreateInstance(replyType,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                new object[] { radio, command }, null)!;
            Assert.Contains("transmit", radio.SelectedTelnetTraceTopics);   // the observer's tap is the last lease

            // The command echo, as SendCommand traces it: the observer learns its sequence.
            lock (radio.TelnetTraceResponseSeqNums) lock (radio.TelnetTraceOutput)
            {
                radio.TelnetTraceOutput.Add(command);
                radio.TelnetTraceResponseSeqNums.Add(77);
            }
            RaiseTrace(radio);

            using var commandHoldsTopics = new ManualResetEventSlim();
            var commandThread = new Thread(() =>
            {
                lock (radio.SelectedTelnetTraceTopics)
                {
                    commandHoldsTopics.Set();
                    Thread.Sleep(200);   // let the reply thread take its locks and raise
                    lock (radio.TelnetTraceResponseSeqNums) { }
                }
            }) { IsBackground = true };
            var replyThread = new Thread(() =>
            {
                lock (radio.TelnetTraceResponseSeqNums)
                {
                    commandHoldsTopics.Wait(TimeSpan.FromSeconds(5));
                    lock (radio.TelnetTraceOutput)
                    {
                        radio.TelnetTraceOutput.Add("R77|0|");
                        RaiseTrace(radio);
                    }
                }
            }) { IsBackground = true };
            commandThread.Start();
            replyThread.Start();

            bool done = replyThread.Join(TimeSpan.FromSeconds(10)) & commandThread.Join(TimeSpan.FromSeconds(10));
            Assert.True(done, "a VOX reply and a traced command deadlocked on FlexLib's trace locks");

            // And the tap is still let go, off the reply thread.
            var until = DateTime.UtcNow.AddSeconds(5);
            while (radio.SelectedTelnetTraceTopics.Contains("transmit") && DateTime.UtcNow < until) Thread.Sleep(20);
            Assert.DoesNotContain("transmit", radio.SelectedTelnetTraceTopics);
            reply.Dispose();
            f.Dispose();
        }

        // ── Finding 2: the monitor put-back survives a reconnect ──────────────

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-send-monitor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// The record lives on disk, per radio serial, so the ordinary close and
        /// reopen (a new FlexBase every time) still finds it.
        /// </summary>
        [Fact]
        public void A_lost_put_back_survives_a_new_store_on_the_same_folder()
        {
            string dir = TempDir();
            try
            {
                new SendMonitorPutBackStore(dir).Add("1234-5678");
                var reopened = new SendMonitorPutBackStore(dir);
                Assert.True(reopened.Contains("1234-5678"));
                Assert.True(reopened.Contains("1234-5678".ToLowerInvariant()));
                Assert.False(reopened.Contains("9999"));
                reopened.Remove("1234-5678");
                Assert.False(new SendMonitorPutBackStore(dir).Contains("1234-5678"));
            }
            finally { Directory.Delete(dir, true); }
        }

        /// <summary>
        /// Applied on a FlexBase that never saw the loss, through the real
        /// FlexLib command path: the command goes out, and the record is
        /// cleared only once the radio acknowledges it.
        /// </summary>
        [Fact]
        public void The_put_back_is_made_by_a_fresh_rig_and_cleared_only_on_the_radios_acknowledgement()
        {
            string dir = TempDir();
            try
            {
                new SendMonitorPutBackStore(dir).Add("rig-two");
                using var f = new RigOnVendorRadio("rig-two");
                var store = new SendMonitorPutBackStore(dir);
                f.Rig.MonitorPutBackStoreOverride = store;

                f.Rig.PutBackMonitorNow(f.Vendor.Radio);

                Assert.Equal("transmit set mon=0", f.Vendor.LastCommand);
                Assert.True(store.Contains("rig-two"), "not cleared before the radio answers");
                f.Vendor.Reply(f.Vendor.LastSequence, 0x50000015, "refused");
                Assert.True(store.Contains("rig-two"), "a refusal keeps it for the next connect");

                f.Rig.PutBackMonitorNow(f.Vendor.Radio);
                f.Vendor.ReplyOk(f.Vendor.LastSequence);
                Assert.False(store.Contains("rig-two"));
            }
            finally { Directory.Delete(dir, true); }
        }

        /// <summary>
        /// The order that dropped it: the connect calls ran before the command
        /// loop existed, and work queued then is thrown away. The apply now runs
        /// in the main thread after <c>q.MainLoop = true</c>, which is after the
        /// station's profile load, and nowhere earlier.
        /// </summary>
        [Fact]
        public void The_put_back_is_queued_after_the_command_loop_starts_never_at_connect()
        {
            string main = SendFixRoundTests.ReadSource("Radios/FlexBase.MainThread.cs");
            int loop = main.IndexOf("q.MainLoop = true;", StringComparison.Ordinal);
            int station = main.IndexOf("EstablishStationOnConnect(", StringComparison.Ordinal);
            int apply = main.IndexOf("ApplyPendingMonitorPutBack();", StringComparison.Ordinal);
            Assert.True(station >= 0 && loop > station && apply > loop,
                "the put-back must follow the station's establishment and the command loop's start");
            Assert.DoesNotContain("PutBackLostSendMonitor",
                SendFixRoundTests.ReadSource("Radios/FlexBase.SliceRecorder.cs"));
        }

        // ── Round 3, finding 1: a newer monitor choice in this app wins ───────

        /// <summary>
        /// A put-back owed from an earlier disconnect is an intent, and the
        /// operator choosing the monitor in THIS app supersedes it: the record
        /// goes, and a recovery already queued finds it gone and sends nothing.
        /// Created state: a refused recovery keeps the record, then the real
        /// Monitor setter is used, then the recovery runs again.
        /// </summary>
        [Fact]
        public void A_monitor_choice_in_this_app_supersedes_an_owed_put_back()
        {
            string dir = TempDir();
            try
            {
                using var f = new RigOnVendorRadio("rig-three");
                var store = new SendMonitorPutBackStore(dir);
                f.Rig.MonitorPutBackStoreOverride = store;
                store.Add("rig-three");
                f.Rig.PutBackMonitorNow(f.Vendor.Radio);
                f.Vendor.Reply(f.Vendor.LastSequence, 0x50000015, "refused");
                Assert.True(store.Contains("rig-three"), "positive control: a refusal keeps the debt");
                int commands = f.Vendor.Transport.Commands.Count;

                f.Rig.Monitor = FlexBase.OffOnValues.on;   // the operator's choice, through the app's setter

                Assert.False(store.Contains("rig-three"), "the operator's choice must clear the old put-back");
                f.Rig.PutBackMonitorNow(f.Vendor.Radio);   // a recovery queued before the choice, running after it
                Assert.DoesNotContain("transmit set mon=0", f.Vendor.Transport.Commands.Skip(commands));
            }
            finally { Directory.Delete(dir, true); }
        }

        // ── Round 3, finding 2: the first status after a removed line is read ─

        [Fact]
        public void The_first_status_of_a_lease_that_removed_a_kept_line_is_read()
        {
            using var f = new RigOnVendorRadio("ptt-watch-cursor-test");
            f.Vendor.Radio.AddTelnetTraceTopic("transmit");
            using (f.Rig.WatchPttSource())
                f.Vendor.Status("interlock state=TRANSMITTING source=SW");   // kept as the last line
            f.Vendor.Status("transmit rfpower=25");                       // another topic appends after it

            using var watch = f.Rig.WatchPttSource();                     // removes the kept line
            f.Vendor.Status("interlock state=TRANSMITTING source=RCA,SW");   // ONE status

            Assert.Equal("RCA,SW", f.Rig.RawPttSource);
            Assert.True(f.Rig.PttSourceIsHardware);
        }

        // ── Finding 3 (follow-up): the line kept between leases ───────────────

        [Fact]
        public void The_interlock_line_kept_by_one_lease_goes_even_when_other_topics_append_between_leases()
        {
            using var f = new RigOnVendorRadio("ptt-watch-between-test");
            f.Vendor.Radio.AddTelnetTraceTopic("transmit");
            int InterlockLines() => f.Vendor.Radio.TelnetTraceOutput.Count(l => l.Contains("|interlock "));
            for (int send = 0; send < 20; send++)
            {
                using (f.Rig.WatchPttSource())
                {
                    f.Vendor.Status("interlock state=TRANSMITTING source=SW");
                    f.Vendor.Status("interlock state=READY source=");
                }
                f.Vendor.Status("transmit rfpower=" + send);   // the VOX restore's tap, between sends
            }
            Assert.True(InterlockLines() <= 1, InterlockLines() + " interlock lines retained after 20 sends");
            Assert.Equal(20, f.Vendor.Radio.TelnetTraceOutput.Count(l => l.Contains("|transmit ")));
        }
    }
}
