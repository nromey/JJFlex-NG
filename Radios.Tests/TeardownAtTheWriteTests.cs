using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Junk box repair round 2 (#596 N1, N4; #861 B1, B2): the teardown's
    /// guards are checked at the WRITE, not at the decision. Codex's re-check
    /// reproduced each schedule below on this fixture: the real
    /// <see cref="FlexBase"/> on the vendored Radio and its fake transport,
    /// with a command loop that can be HELD so a state can change between a
    /// write being queued and it going out. What went out is read off the
    /// transport. No socket, no radio, nothing keys.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TeardownAtTheWriteTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly MethodInfo NoteRadioMox = typeof(FlexBase).GetMethod("noteRadioMox", Private)!;
        private static readonly MethodInfo PutBackNow = typeof(FlexBase).GetMethod("PutBackNow", Private)!;
        private static readonly MethodInfo DropTransmit = typeof(FlexBase).GetMethod("dropTransmitWithConnection", Private)!;
        private static readonly FieldInfo MicInputSwap = typeof(FlexBase).GetField("_micInputSwap", Private)!;
        private static readonly Type SwapType = typeof(FlexBase).GetNestedType("MicInputSwap", BindingFlags.NonPublic)!;
        private const uint Foreign = 0x0BADF00D;

        private readonly RigOnVendorRadio _f;
        private readonly Thread _loop;
        private volatile bool _stop;
        private volatile bool _hold;

        public TeardownAtTheWriteTests()
        {
            Assert.True(NoteRadioMox != null && PutBackNow != null && DropTransmit != null && MicInputSwap != null && SwapType != null,
                "a seam this test reaches has moved; every assertion below would be vacuous");
            _f = new RigOnVendorRadio("teardown-at-the-write");
            _f.Vendor.Radio.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Mox") NoteRadioMox.Invoke(_f.Rig, new object[] { _f.Vendor.Radio.Mox });
            };
            _f.Rig.q.MainLoop = true;
            _loop = new Thread(() =>
            {
                while (!_stop)
                {
                    if (!_hold && _f.Rig.q.Count > 0)
                    {
                        var item = _f.Rig.q.Dequeue().Item;
                        if (item is FlexBase.FunctionDel a) a();
                    }
                    else Thread.Sleep(2);
                }
            }) { IsBackground = true };
            _loop.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _loop.Join(1000);
            _f.Rig.q.MainLoop = false;
            _f.Dispose();
        }

        private void Interlock(string state, uint client = RigOnVendorRadio.OurHandle) =>
            _f.Vendor.Status($"interlock state={state} source=SW tx_client_handle=0x{client:X8}");

        private string[] Writes(string prefix) =>
            _f.Vendor.Transport.Commands.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();

        private object Swap(string saved) => Activator.CreateInstance(SwapType, BindingFlags.Public | BindingFlags.Instance, null,
            new object?[] { saved, _f.Vendor.Radio, 0L, false }, null)!;

        private void PutBackMic(object swap)
        {
            _f.Vendor.Status("transmit mic_selection=PC");
            PutBackNow.Invoke(_f.Rig, new object[] { _f.Vendor.Radio, swap, "test" });
        }

        private static void WaitFor(Func<bool> condition, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < until, "timed out waiting for " + what);
                Thread.Sleep(2);
            }
        }

        // ── #596 N1 and #861 B2: the unkey's ownership at its write ──────

        [Fact]
        public void A_teardown_unkey_queued_for_our_carrier_is_not_sent_when_the_carrier_has_passed_to_another_client()
        {
            // Codex's reproduction: transmitting with our handle, the queue
            // held until it holds the unkey, the interlock replayed with
            // another client's handle, the queue released.
            Interlock("TRANSMITTING");
            Assert.True(_f.Rig.Transmit, "positive control: our carrier reached FlexBase");
            _hold = true;
            var teardown = new Thread(() => _f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(400), 200))
                { IsBackground = true };
            teardown.Start();
            WaitFor(() => _f.Rig.q.Count >= 1, "the teardown's unkey to be queued");
            Interlock("TRANSMITTING", Foreign);
            _hold = false;
            teardown.Join();

            Assert.DoesNotContain("xmit 0", Writes("xmit"));
            Assert.Empty(Writes("transmit tune 0"));
        }

        [Fact]
        public void A_teardown_unkey_still_goes_out_when_the_carrier_is_ours_at_its_write()
        {
            // The control for the test above: the same schedule with our own
            // carrier still up when the queue is released sends xmit 0.
            Interlock("TRANSMITTING");
            _hold = true;
            var teardown = new Thread(() => _f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(400), 200))
                { IsBackground = true };
            teardown.Start();
            WaitFor(() => _f.Rig.q.Count >= 1, "the teardown's unkey to be queued");
            _hold = false;
            teardown.Join();

            Assert.Contains("xmit 0", Writes("xmit"));
        }

        [Fact]
        public void The_own_carrier_unkey_leaves_another_clients_carrier_alone()
        {
            // What the push-to-talk controller's release at teardown and at a
            // drop now sends (PttSafetyController.ReleaseForTeardown,
            // DropWithConnection): never xmit 0 under someone else's carrier.
            Interlock("TRANSMITTING", Foreign);
            Assert.True(_f.Vendor.Radio.Mox, "positive control: FlexLib's Mox reads true for another client's carrier");
            _f.Rig.UnkeyOwnCarrier("test");
            WaitFor(() => _f.Rig.q.Count == 0, "the queue to drain");
            Thread.Sleep(20);
            Assert.Empty(Writes("xmit"));

            Interlock("TRANSMITTING");                     // control: ours
            _f.Rig.UnkeyOwnCarrier("test");
            WaitFor(() => Writes("xmit").Length > 0, "our own unkey");
            Assert.Contains("xmit 0", Writes("xmit"));
        }

        // ── #861 B1: the microphone put-back asks the interlock at its write ──

        [Fact]
        public void A_carrier_that_keys_after_the_wait_still_withholds_the_mic_put_back_and_keeps_it_owed()
        {
            // Codex's reproduction: READY, the teardown says go, then another
            // client keys before the put-back writes.
            Interlock("READY");
            Assert.True(_f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(50), 50));
            Interlock("TRANSMITTING", Foreign);
            object swap = Swap("MIC");
            PutBackMic(swap);

            Assert.DoesNotContain("mic input MIC", Writes("mic input"));
            Assert.Same(swap, MicInputSwap.GetValue(_f.Rig));   // kept owed, not lost
        }

        [Fact]
        public void A_stuck_input_is_not_receive()
        {
            // STUCK_INPUT means something holds the transmitter down. Round
            // one's wait counted it as receive and wrote MIC.
            Interlock("STUCK_INPUT");
            Assert.False(_f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(50), 100));
            PutBackMic(Swap("MIC"));
            Assert.DoesNotContain("mic input MIC", Writes("mic input"));
        }

        [Fact]
        public void An_ordinary_stop_under_a_carrier_withholds_the_mic_put_back_too()
        {
            // Not only at teardown: the stop of PC audio from the menu or
            // Settings writes through the same put-back.
            Interlock("TRANSMITTING", Foreign);
            PutBackMic(Swap("MIC"));
            Assert.DoesNotContain("mic input MIC", Writes("mic input"));

            Interlock("READY");                            // control: receive writes it
            PutBackMic(Swap("BAL"));
            Assert.Contains("mic input BAL", Writes("mic input"));
        }

        // ── A key-on already queued at the teardown ──────────────────────

        [Fact]
        public void A_key_on_queued_before_the_teardown_never_keys()
        {
            // Codex's pending-key schedule: our push to talk's xmit 1 sits in
            // the queue while the interlock still reads READY; the teardown
            // used to see nothing keyed, return at once, and let it key.
            Interlock("READY");
            _hold = true;
            _f.Rig.Transmit = true;
            Assert.Equal(1, _f.Rig.q.Count);
            var teardown = new Thread(() => _f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(400), 200))
                { IsBackground = true };
            teardown.Start();
            Thread.Sleep(50);
            _hold = false;
            teardown.Join();
            // The queued key-on must have reached the loop before this looks.
            WaitFor(() => _f.Rig.q.Count == 0, "the queue to drain");
            Thread.Sleep(50);

            Assert.DoesNotContain("xmit 1", Writes("xmit"));
        }

        [Fact]
        public void A_key_on_queued_with_nothing_in_the_way_keys()
        {
            // Control: the generation does not stop ordinary keying.
            Interlock("READY");
            _f.Rig.Transmit = true;
            WaitFor(() => Writes("xmit").Length > 0, "the key-on");
            Assert.Contains("xmit 1", Writes("xmit"));
            _f.Rig.Transmit = false;
        }

        // ── #596 N4: work admitted before a drop ─────────────────────────

        [Fact]
        public void A_key_on_and_CW_queued_before_the_connection_fell_are_revoked()
        {
            Interlock("READY");
            _hold = true;
            _f.Rig.Transmit = true;
            Assert.True(_f.Rig.SendCW("CQ TEST"));
            Assert.True(_f.Rig.SendCW('K'));
            Assert.Equal(3, _f.Rig.q.Count);

            DropTransmit.Invoke(_f.Rig, new object[] { new Action<string, System.Diagnostics.TraceLevel>((_, _) => { }) });

            // Whatever reaches the loop now is the dropped connection's work.
            int sent = 0;
            while (_f.Rig.q.Count > 0)
            {
                var item = _f.Rig.q.Dequeue().Item;
                if (item is FlexBase.FunctionDel a) { a(); sent++; }
                else sent += CwWouldSend(item) ? 1 : 0;
            }
            _hold = false;
            Assert.DoesNotContain("xmit 1", Writes("xmit"));
            Assert.Equal(1, sent);   // only the key-on's closure ran, and it refused
        }

        /// <summary>
        /// A queued CW item, judged as the main loop judges it: true when it
        /// would still be handed to the CW sender.
        /// </summary>
        private bool CwWouldSend(object item)
        {
            var gen = item.GetType().GetField("Generation");
            Assert.NotNull(gen);
            long now = (long)typeof(FlexBase).GetField("_transmitWorkGeneration", Private)!.GetValue(_f.Rig)!;
            return (long)gen!.GetValue(item)! == now;
        }
    }
}
