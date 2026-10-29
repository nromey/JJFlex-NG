#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The PTT source watch against FlexLib's own threads (#835, Codex round 1
    /// of the 2026-10-28 fix round, findings 1, 2 and 4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The lock order.</b> <c>Radio.ParseReply</c> raises the trace's change
    /// WHILE it holds <c>TelnetTraceOutput</c>; <c>Radio.SendCommand</c> raises
    /// its own after letting go of it. A watch callback that took its own gate
    /// first and FlexLib's list second could therefore deadlock the reply
    /// thread against a command thread. The hammer below drives exactly those
    /// two shapes from two threads; on the old ordering it hangs, which the
    /// test reports as a failure after a bound rather than hanging the run
    /// (the stuck threads are background threads).
    /// </para>
    /// <para>
    /// What it cannot prove: that no OTHER lock in FlexLib forms a cycle with
    /// these two. It proves the cycle Codex named is gone.
    /// </para>
    /// </remarks>
    public sealed class PttSourceWatchSafetyTests
    {
        private static readonly MethodInfo Raise =
            typeof(Flex.UiWpfFramework.Mvvm.ObservableObject).GetMethod("RaisePropertyChanged",
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null)!;

        private static void RaiseTrace(Radio radio) => Raise.Invoke(radio, new object[] { nameof(Radio.TelnetTraceOutput) });

        private static void Interlock(RigOnVendorRadio f, string source) =>
            f.Vendor.Status($"interlock state=TRANSMITTING source={source} tx_client_handle=0x{RigOnVendorRadio.OurHandle:X8}");

        [Fact]
        public void A_reply_raised_under_the_trace_lock_and_a_command_raised_outside_it_cannot_deadlock_the_watch()
        {
            Assert.NotNull(Raise);
            // Not "using": after a deadlock the watch's own gate is held by a
            // stuck thread, and disposing would hang the run instead of failing it.
            var f = new RigOnVendorRadio("ptt-watch-lock-test");
            var watch = f.Rig.WatchPttSource();
            Radio radio = f.Vendor.Radio;
            var stop = DateTime.UtcNow.AddMilliseconds(1500);

            // The reply thread: ParseReply's shape, holding the list while it raises.
            var reply = new Thread(() =>
            {
                while (DateTime.UtcNow < stop)
                {
                    List<string> output = radio.TelnetTraceOutput;
                    lock (output)
                    {
                        output.Add("R1|0|");
                        RaiseTrace(radio);
                    }
                }
            }) { IsBackground = true };
            // The command thread: SendCommand's shape, raising after letting go.
            var command = new Thread(() =>
            {
                while (DateTime.UtcNow < stop)
                {
                    List<string> output = radio.TelnetTraceOutput;
                    lock (output) output.Add("C2|interlock set");
                    RaiseTrace(radio);
                }
            }) { IsBackground = true };
            reply.Start();
            command.Start();

            bool replyDone = reply.Join(TimeSpan.FromSeconds(15));
            bool commandDone = command.Join(TimeSpan.FromSeconds(15));
            Assert.True(replyDone && commandDone,
                "the reply and command threads deadlocked in the PTT watch's trace callback");
            watch.Dispose();
            f.Dispose();
        }

        /// <summary>
        /// Finding 2: the interlock lines the watch causes FlexLib to keep are
        /// not left in its never-trimmed list. At most one stays, because the
        /// newest line may still be read as "the last line" by another trace
        /// reader handling the same change.
        /// </summary>
        [Fact]
        public void The_interlock_lines_the_watch_causes_are_not_retained_across_sends()
        {
            using var f = new RigOnVendorRadio("ptt-watch-trim-test");
            int InterlockLines() => f.Vendor.Radio.TelnetTraceOutput.Count(l => l.Contains("|interlock "));
            for (int send = 0; send < 20; send++)
            {
                using var watch = f.Rig.WatchPttSource();
                for (int i = 0; i < 10; i++) Interlock(f, i % 2 == 0 ? "SW" : "RCA,SW");
                Assert.Equal("RCA,SW", f.Rig.RawPttSource);   // the watch still read every line
                Assert.True(InterlockLines() <= 1, "during send " + send + ": " + InterlockLines() + " retained");
            }
            Assert.True(InterlockLines() <= 1, InterlockLines() + " interlock lines retained after 20 sends");
            Assert.Null(f.Rig.RawPttSource);   // released: no raw word
        }

        /// <summary>The trimming never touches lines the watch did not cause.</summary>
        [Fact]
        public void Lines_from_other_topics_are_left_alone()
        {
            using var f = new RigOnVendorRadio("ptt-watch-trim-others-test");
            f.Vendor.Radio.AddTelnetTraceTopic("transmit");
            using (f.Rig.WatchPttSource())
            {
                f.Vendor.Status("transmit rfpower=25");
                Interlock(f, "SW");
                f.Vendor.Status("transmit rfpower=24");
                Interlock(f, "RCA,SW");
                f.Vendor.Status("transmit rfpower=23");
            }
            Assert.Equal(3, f.Vendor.Radio.TelnetTraceOutput.Count(l => l.Contains("|transmit ")));
        }

        /// <summary>
        /// Finding 4: a disconnect or a new connection revokes every lease at
        /// once, so the topic is not left selected across a reconnect (where a
        /// space-less command such as the connect fence's bare ping would throw
        /// in Radio.SendCommand), and a lease disposed later does not undo a
        /// newer watch.
        /// </summary>
        [Fact]
        public void A_disconnect_revokes_the_watch_and_a_stale_lease_cannot_end_a_newer_one()
        {
            using var f = new RigOnVendorRadio("ptt-watch-revoke-test");
            var stale = f.Rig.WatchPttSource();
            var stale2 = f.Rig.WatchPttSource();
            Interlock(f, "RCA,SW");
            Assert.True(f.Rig.PttSourceIsHardware);

            f.Rig.RevokePttWatch("test disconnect");

            Assert.DoesNotContain("interlock", f.Vendor.Radio.SelectedTelnetTraceTopics);
            Assert.Null(f.Rig.RawPttSource);

            var fresh = f.Rig.WatchPttSource();
            stale.Dispose();
            stale2.Dispose();
            Assert.Contains("interlock", f.Vendor.Radio.SelectedTelnetTraceTopics);
            fresh.Dispose();
            Assert.DoesNotContain("interlock", f.Vendor.Radio.SelectedTelnetTraceTopics);
        }

        [Fact]
        public void Disconnect_and_every_connect_revoke_the_watch_first()
        {
            string fb = SendFixRoundTests.ReadSource("Radios/FlexBase.cs");
            string disconnect = SendFixRoundTests.Body(fb, "public void Disconnect()");
            int revoke = disconnect.IndexOf("RevokeSendHoldsForDisconnect(", StringComparison.Ordinal);
            int teardown = disconnect.IndexOf("CancelStationAttempt(", StringComparison.Ordinal);
            Assert.True(revoke >= 0 && revoke < teardown, "Disconnect must revoke while the link is still up");

            // Finding 3: the disconnect path puts the monitor back itself; where
            // a lost put-back is applied is pinned in SendHoldsRoundTwoTests.
            string monitor = SendFixRoundTests.ReadSource("Radios/FlexBase.SendMonitor.cs");
            string revokeHolds = SendFixRoundTests.Body(monitor, "internal void RevokeSendHoldsForDisconnect(string why)");
            Assert.Contains("RevokePttWatch(why);", revokeHolds);
            Assert.Contains("owner.TakeBackForDisconnect()", revokeHolds);
            Assert.Contains("\"transmit set mon=0\"", revokeHolds);

            // Every Radio.Connect in FlexBase is preceded, in the same method, by a revoke.
            int at = 0, sites = 0;
            while ((at = fb.IndexOf(".Connect(", at, StringComparison.Ordinal)) >= 0)
            {
                int lineStart = fb.LastIndexOf('\n', at) + 1;
                string line = fb.Substring(lineStart, at - lineStart);
                if (line.Contains("theRadio") && !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    sites++;
                    int revokeBefore = fb.LastIndexOf("RevokePttWatch(", at, StringComparison.Ordinal);
                    int methodStart = fb.LastIndexOf("\n        p", at, StringComparison.Ordinal);
                    Assert.True(revokeBefore > methodStart, "a Radio.Connect without a revoke before it: " + line.Trim());
                }
                at++;
            }
            Assert.True(sites >= 3, "found only " + sites + " Radio.Connect sites; the scan is broken");
        }
    }
}
