using System;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Every teardown unkeys first, and a microphone input is never put back
    /// while the radio still transmits (#596, #861; Codex's checks of the
    /// 2026-11-02 batch). Driven through the real <see cref="FlexBase"/>
    /// methods on the vendored Radio and its fake transport: what went out on
    /// the wire is read from the transport, and the radio's interlock is
    /// replayed as the status line it sends. No socket, no radio, nothing keys.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TeardownUnkeyTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly MethodInfo NoteRadioMox = typeof(FlexBase).GetMethod("noteRadioMox", Private)!;
        private static readonly MethodInfo PutBackNow = typeof(FlexBase).GetMethod("PutBackNow", Private)!;
        private static readonly Type SwapType = typeof(FlexBase).GetNestedType("MicInputSwap", BindingFlags.NonPublic)!;

        private readonly RigOnVendorRadio _f;

        public TeardownUnkeyTests()
        {
            Assert.True(NoteRadioMox != null && PutBackNow != null && SwapType != null,
                "the teardown's seams are not where this test reaches them; every assertion below would be vacuous");
            _f = new RigOnVendorRadio("teardown-unkey");
            _f.Vendor.Radio.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Mox") NoteRadioMox.Invoke(_f.Rig, new object[] { _f.Vendor.Radio.Mox });
            };
            // The command loop, as the rig's main thread runs it: what the
            // teardown queues goes out on the fake transport, so its unkey
            // and its drain are real.
            _f.Rig.q.MainLoop = true;
            _loop = new System.Threading.Thread(() =>
            {
                while (!_stop)
                {
                    if (_f.Rig.q.Count > 0) { if (_f.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a(); }
                    else System.Threading.Thread.Sleep(2);
                }
            }) { IsBackground = true };
            _loop.Start();
        }

        private readonly System.Threading.Thread _loop;
        private volatile bool _stop;

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

        /// <summary>The put-back's own write, as the stop makes it: saved MIC, the radio on the PC we set.</summary>
        private void PutBackMic()
        {
            _f.Vendor.Status("transmit mic_selection=PC");
            object swap = Activator.CreateInstance(SwapType, BindingFlags.Public | BindingFlags.Instance, null,
                new object?[] { "MIC", _f.Vendor.Radio, 0L, false }, null)!;
            PutBackNow.Invoke(_f.Rig, new object[] { _f.Vendor.Radio, swap, "test", 0L });
        }

        [Fact]
        public void Receiving_the_put_back_goes_ahead()
        {
            // Positive control: nothing keyed, receive confirmed at once, and
            // the saved input is written.
            Interlock("READY");
            Assert.True(_f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(50), 50));
            PutBackMic();
            Assert.Contains("mic input MIC", Writes("mic input"));
        }

        [Fact]
        public void Our_own_transmission_is_unkeyed_before_anything_is_put_back()
        {
            Interlock("TRANSMITTING");
            Assert.True(_f.Rig.Transmit, "positive control: the fixture's interlock reached FlexBase.Transmit");

            // The unkey goes out; the radio has not yet reported receive when
            // the wait ends, so the put-back is withheld.
            bool receive = _f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(500), 100);

            Assert.Contains("xmit 0", Writes("xmit"));
            Assert.False(receive);
            PutBackMic();
            Assert.DoesNotContain("mic input MIC", Writes("mic input"));
        }

        [Fact]
        public void Another_clients_transmission_withholds_the_put_back_and_is_not_unkeyed()
        {
            // The radio is transmitting for someone else. FlexLib's Mox reads
            // true for it on an unbound GUI client, so "ours" has to come from
            // the interlock's transmitting client: no xmit 0 goes out, and the
            // microphone input is left on PC rather than switched under their
            // carrier.
            Interlock("TRANSMITTING", client: 0x0BADF00D);
            Assert.True(_f.Vendor.Radio.InterlockState == InterlockState.Transmitting, "positive control: the interlock arrived");

            Assert.False(_f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(500), 100));

            Assert.Empty(Writes("xmit"));
            PutBackMic();
            Assert.DoesNotContain("mic input MIC", Writes("mic input"));
        }

        [Fact]
        public void A_radio_that_reaches_receive_lets_the_put_back_through()
        {
            // Keyed when the teardown starts; the radio reports receive within
            // the wait (as it does once our xmit 0 lands): the put-back runs.
            Interlock("TRANSMITTING");
            var t = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(150);
                Interlock("READY");
            }) { IsBackground = true };
            t.Start();
            bool receive = _f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(50), 2000);
            t.Join();

            Assert.True(receive);
            PutBackMic();
            Assert.Contains("mic input MIC", Writes("mic input"));
        }
    }
}
