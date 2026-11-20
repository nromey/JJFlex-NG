#nullable enable

using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Yes to the tier offer retires the session a failed open left up BEFORE
    /// the SmartLink re-plan dials (the tiers work, Track A repair round 3,
    /// from Codex's re-check of round 2).
    /// </summary>
    /// <remarks>
    /// <para>The state: startup auto-connect's SmartLink leg refused by the
    /// tier (the offer earned), its Local leg connected, and Local's open
    /// failed with the command connection still up. The last walk end puts the
    /// offer; Yes re-plans. Until this round the re-plan's Connect replaced
    /// <c>theRadio</c> with the WAN object with the Local one still connected,
    /// so two sessions with this program's one client identity could be open
    /// to one radio.</para>
    /// <para>Driven at the re-plan boundary the app calls
    /// (<see cref="FlexBase.ReplanSmartLinkUnderRaisedTier(string, bool, bool, string)"/>),
    /// with a real rig and a real FlexLib <see cref="Radio"/> made live the
    /// way <c>ConnectionFallArchiveTests</c> does (no socket, no network). Only
    /// the dial is a stand-in, because the real one is
    /// <see cref="FlexBase.ReconnectRemote"/>, which would reach SmartLink; the
    /// stand-in records what the dial would meet. That production passes
    /// <c>ReconnectRemote</c>, and that <c>globals.vb</c> reaches the dial only
    /// through a Yes, are pinned from the source at the end. No account is
    /// saved, nothing speaks and no window is shown.</para>
    /// </remarks>
    // The suite runs sequentially by assembly policy (TestParallelism.cs),
    // which makes the process-wide archive hook and queue safe to set here.
    public sealed class TierOfferReplanRetireTests : IDisposable
    {
        private readonly Func<CaptureArchiveRequest, CaptureArchiveResult> _savedHook;
        private readonly Action<Action> _savedQueue;
        private int _archives;

        public TierOfferReplanRetireTests()
        {
            _savedHook = CaptureArchive.ArchiveHook;
            _savedQueue = CaptureArchive.Queue;
            // A fall our own teardown causes must archive nothing; count it if
            // it does, and never let it reach the disk.
            CaptureArchive.ArchiveHook = _ =>
            {
                Interlocked.Increment(ref _archives);
                return new CaptureArchiveResult { ArchivePath = @"C:\Traces\fall.zip", SuccessorOpened = true };
            };
            CaptureArchive.Queue = work => work();
        }

        public void Dispose()
        {
            CaptureArchive.ArchiveHook = _savedHook;
            CaptureArchive.Queue = _savedQueue;
        }

        private static FlexBase NewRig() => new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        /// <summary>How many qualified connection-state subscriptions the radio carries.</summary>
        private static int ProducerHandlerCopies(Radio radio)
        {
            FieldInfo? backing = typeof(Radio).GetField("CommandConnectionChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(backing != null, "CommandConnectionChanged's backing field moved; the count would be vacuous");
            var list = (Delegate?)backing!.GetValue(radio);
            return list == null ? 0 : list.GetInvocationList().Length;
        }

        /// <summary>
        /// Make the radio's command connection live with no socket: a SmartLink
        /// one through the offline TLS client, a local one through the TCP
        /// transport's own attempt with its writer on memory, the way
        /// <c>ProducerIdentityTests</c> does. Returns what the connection
        /// writes, or null for SmartLink (the offline TLS client keeps nothing).
        /// </summary>
        private static MemoryStream? MakeLive(Radio radio, bool wan)
        {
            if (wan)
            {
                OfflineCommandProducer.StartTls(radio);
                return null;
            }
            var transport = OfflineCommandProducer.Transport(radio);
            object attempt = OfflineCommandProducer.Call(transport, "BeginAttempt", IPAddress.Parse("192.0.2.1"), 4992, 0);
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream) { AutoFlush = true };
            FieldInfo? w = attempt.GetType().GetField("Writer");
            Assert.True(w != null, "TcpCommandCommunication's attempt Writer moved; the local case would be vacuous");
            w!.SetValue(attempt, writer);
            Assert.True((bool)OfflineCommandProducer.Call(transport, "PublishConnected", transport.CurrentConnection!));
            return stream;
        }

        /// <summary>This rig's GUI client handle, as the radio's own status would have set it.</summary>
        private static void GiveTheRigAHandle(FlexBase rig, uint handle)
        {
            FieldInfo? f = typeof(FlexBase).GetField("clientHandle", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(f != null, "FlexBase.clientHandle moved; the goodbye check would be vacuous");
            f!.SetValue(rig, handle);
        }

        /// <summary>What the dial met when it ran.</summary>
        private sealed class DialSeen
        {
            public int Calls;
            public bool OldRadioConnected;
            public bool RigConnected;
            public int OldRadioProducerHandlers;
            public bool PathForced;
        }

        private static Func<bool> Dial(FlexBase rig, Radio old, DialSeen seen, bool answer) => () =>
        {
            seen.Calls++;
            seen.OldRadioConnected = old.Connected;
            seen.RigConnected = rig.IsConnected;
            seen.OldRadioProducerHandlers = ProducerHandlerCopies(old);
            seen.PathForced = rig.SmartLinkPathForced;
            return answer;
        };

        [Theory]
        [InlineData(false, true)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void Yes_retires_the_session_the_failed_open_left_up_before_the_SmartLink_dial(bool wan, bool dialComesUp)
        {
            // Local is Codex's case; a SmartLink session whose open failed with
            // its connection up is retired the same way.
            var rig = NewRig();
            var old = OfflineCommandProducer.Radio(wan);
            try
            {
                rig.theRadio = old;
                OfflineCommandProducer.Wire(rig, old);
                MemoryStream? written = MakeLive(old, wan);
                GiveTheRigAHandle(rig, 0x4D2);
                // The failed open's state: the command connection still up.
                Assert.True(old.Connected);
                Assert.True(rig.IsConnected);
                Assert.Equal(1, ProducerHandlerCopies(old));
                long before = written?.Length ?? 0;

                var seen = new DialSeen();
                bool ok = rig.ReplanSmartLinkUnderRaisedTier(old.Serial, lowBW: true, pathForced: true,
                    "test (failed open, before giving up)", Dial(rig, old, seen, dialComesUp));

                // The dial ran once, and its answer is the re-plan's.
                Assert.Equal(1, seen.Calls);
                Assert.Equal(dialComesUp, ok);
                // By the time it ran, the old session was closed: the radio
                // object let go, the rig saw its own fall, and the rig's
                // handlers were off the object it is leaving.
                Assert.False(seen.OldRadioConnected, "the re-plan dialled with the failed open's session still up");
                Assert.False(seen.RigConnected);
                Assert.Equal(0, seen.OldRadioProducerHandlers);
                // The forced path is the caller's and reaches the dial.
                Assert.True(seen.PathForced);
                // Our own teardown: classed self-initiated, nothing archived.
                Assert.Equal(0, Volatile.Read(ref _archives));

                if (written != null)
                {
                    // What the retire said to the radio on the local session:
                    // the goodbye for our own registration, by our handle, and
                    // nothing else. No setting, no put-back.
                    string sent = Encoding.UTF8.GetString(written.ToArray(), (int)before, (int)(written.ToArray().Length - before));
                    // FlexLib's TCP close then writes its end-of-transmission
                    // byte (TcpCommandCommunication), which is the socket
                    // closing, not a command; it is taken off before counting.
                    Assert.EndsWith("\x04", sent, StringComparison.Ordinal);
                    sent = sent.Substring(0, sent.Length - 1);
                    string[] lines = sent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    Assert.Single(lines);
                    Assert.EndsWith("|client disconnect 1234", lines[0], StringComparison.Ordinal);
                }
            }
            finally
            {
                OfflineCommandProducer.Release(rig, old);
            }
        }

        [Fact]
        public void With_no_session_left_up_the_replan_touches_nothing_and_dials_as_before()
        {
            // The selector's walk end and the retries after the transport fell:
            // nothing to retire, so the object and the rig's wiring on it stay
            // exactly as they were, and the dial runs.
            var rig = NewRig();
            var old = OfflineCommandProducer.Radio(wan: false);
            try
            {
                rig.theRadio = old;
                OfflineCommandProducer.Wire(rig, old);
                Assert.False(old.Connected);

                var seen = new DialSeen();
                Assert.True(rig.ReplanSmartLinkUnderRaisedTier(old.Serial, lowBW: false, pathForced: false,
                    "test (selector walk end)", Dial(rig, old, seen, answer: true)));

                Assert.Equal(1, seen.Calls);
                Assert.False(seen.PathForced);
                Assert.Same(old, rig.theRadio);
                Assert.Equal(1, ProducerHandlerCopies(old));
                Assert.False(rig.RetireSessionBeforeReplan("test (again)"), "nothing was up, so nothing is retired");
                Assert.Equal(0, Volatile.Read(ref _archives));
            }
            finally
            {
                OfflineCommandProducer.Release(rig, old);
            }
        }

        [Fact]
        public void With_no_radio_object_at_all_the_replan_still_dials()
        {
            var rig = NewRig();
            try
            {
                var seen = 0;
                Assert.False(rig.ReplanSmartLinkUnderRaisedTier("none", false, false, "test (no radio)", () => { seen++; return false; }));
                Assert.Equal(1, seen);
            }
            finally
            {
                rig.theRadio = null;
                rig.Dispose();
            }
        }

        // ── From the source: what runs in production ─────────────────────

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(IntegrationPassTree.Root, Path.Combine(parts)));

        private static string Between(string src, string from, string to)
        {
            int start = src.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, from + " was not found");
            int stop = src.IndexOf(to, start + from.Length, StringComparison.Ordinal);
            Assert.True(stop > start, "no end for " + from);
            return src.Substring(start, stop - start);
        }

        [Fact]
        public void Production_dials_ReconnectRemote_only_after_the_retire_and_the_teardown_is_the_failed_opens_own()
        {
            string src = Read("Radios", "FlexBase.ConnectTiers.cs");

            // The public re-plan passes the real dial, with the arguments the
            // app passed before this round.
            string pub = Between(src, "public bool ReplanSmartLinkUnderRaisedTier(string serial, bool lowBW, bool pathForced, string where) =>", "internal bool ReplanSmartLinkUnderRaisedTier(");
            Assert.Contains("ReconnectRemote(serial, lowBW, forceWanPath: true, allowInteractiveLogin: true)", pub);

            // The retire, then the dial.
            string core = Between(src, "internal bool ReplanSmartLinkUnderRaisedTier(", "\n        }");
            int retire = core.IndexOf("RetireSessionBeforeReplan(where);", StringComparison.Ordinal);
            int dial = core.IndexOf("return dial();", StringComparison.Ordinal);
            Assert.True(retire > 0 && dial > retire, "the session must be retired before the dial");

            // The retire is Start's own failed-open teardown: the goodbye for
            // our own registration, then our own close. Never the operator's
            // Disconnect, which announces, plays the farewell and writes
            // put-backs to the radio.
            string body = Between(src, "internal bool RetireSessionBeforeReplan(string where)", "\n        }");
            int goodbye = body.IndexOf("releaseGuiClientRegistration(why);", StringComparison.Ordinal);
            int close = body.IndexOf("teardownDisconnect(why);", StringComparison.Ordinal);
            Assert.True(goodbye > 0 && close > goodbye, "the goodbye goes before the close, as in Start");
            Assert.DoesNotContain("Disconnect();", body);
            Assert.DoesNotContain("PCAudio", body);
        }

        [Fact]
        public void The_app_reaches_the_replan_only_through_a_Yes_and_never_dials_around_the_retire()
        {
            string g = Read("globals.vb");
            string body = Between(g,
                "Private Function OfferTierAndReplanSmartLink(serial As String, lowBW As Boolean, pathForced As Boolean, where As String) As Boolean",
                "\n    End Function");
            int gate = body.IndexOf("If Not OfferHigherSmartLinkTier(RigControl, where) Then Return False", StringComparison.Ordinal);
            int replan = body.IndexOf("RigControl.ReplanSmartLinkUnderRaisedTier(serial, lowBW, pathForced, where)", StringComparison.Ordinal);
            // No and Cancel (and a cancelled connect, which is never asked)
            // return False at the gate, before anything is dialled.
            Assert.True(gate > 0 && replan > gate, "the re-plan must follow the Yes gate");
            // And nothing in it dials SmartLink except through the retire.
            Assert.DoesNotContain("ReconnectRemote(", body);
        }
    }
}
