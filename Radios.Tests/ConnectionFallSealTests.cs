using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The capture seal is taken where a drop is signalled on EVERY path: our
    /// Radio's <c>Connected</c> property falling. Sprint 45 Track H5.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the trigger moved.</b> Tracks H, H2 and H3 sealed on
    /// <c>API.RadioRemoved</c>. FlexLib raises that event only for a serial in
    /// its LAN discovery dictionary (<c>API.RemoveRadio</c> returns without an
    /// event otherwise), and only LAN discovery writes the dictionary. A radio
    /// reached only through SmartLink is never in it, so on that path — Don's
    /// 6300 — no drop was ever sealed. The bench's 8600 is on the LAN too,
    /// which is why every bench run looked right. Established by Track H4 from
    /// the vendor source and from Don's 2026-09-06 trace.</para>
    ///
    /// <para><b>These tests drive FlexLib's own objects, not stand-ins.</b> The
    /// loss enters where a real one does: the transport's
    /// <c>IsConnectedChanged(false)</c>, which the <c>Radio</c> hooked in its
    /// constructor. From there it is the vendor's own chain — <c>Connected</c>
    /// falls, the <c>Radio</c> calls its <c>Disconnect</c>, that calls
    /// <c>API.RemoveRadio</c> — into this rig's production handlers. The
    /// dual-homed case puts the radio in FlexLib's real discovery dictionary so
    /// that <c>RadioRemoved</c> genuinely fires, which is also the positive
    /// control for the SmartLink-only case: the same counter that stays at zero
    /// there is shown to count here.</para>
    ///
    /// <para><b>Nothing here opens a socket, speaks or shows a window.</b> The
    /// rig's own <c>Disconnect</c> is never called, because it speaks; the
    /// deliberate cases enter through <c>teardownDisconnect</c> and through the
    /// <c>Disconnecting</c> latch that <c>Disconnect</c> sets.</para>
    /// </remarks>
    // The suite runs sequentially by assembly policy (TestParallelism.cs),
    // which is what makes the process-wide seal hook, queue, claim, trace
    // session and FlexLib's static discovery dictionary safe to drive here.
    public sealed class ConnectionFallSealTests : IDisposable
    {
        private readonly Func<CaptureSealRequest, CaptureSealResult> _savedHook;
        private readonly Action<Action> _savedQueue;
        private readonly TraceSession _savedSession;
        private int _seals;

        public ConnectionFallSealTests()
        {
            _savedHook = CaptureSeal.SealHook;
            _savedQueue = CaptureSeal.Queue;
            _savedSession = TraceSessionContext.Current;
            CaptureSeal.ForgetClaimForTests();

            // Count seals, and run the worker inline so a count is a fact the
            // moment the call returns rather than something to wait for.
            CaptureSeal.SealHook = _ =>
            {
                Interlocked.Increment(ref _seals);
                return new CaptureSealResult { ArchivePath = @"C:\Traces\fall.zip", SuccessorOpened = true };
            };
            CaptureSeal.Queue = work => work();
            TraceSessionContext.BeginSession();
        }

        public void Dispose()
        {
            CaptureSeal.SealHook = _savedHook;
            CaptureSeal.Queue = _savedQueue;
            CaptureSeal.ForgetClaimForTests();
            typeof(TraceCoordinator)
                .GetMethod("RestoreSessionForTests", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { _savedSession });
        }

        // ────────────────────────────────────────────────────────────────
        //  The harness: FlexLib's own objects, reached where a real loss is
        // ────────────────────────────────────────────────────────────────

        private static FlexBase NewRig() =>
            new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        /// <summary>Let the rig go without its Disconnect, which speaks.</summary>
        private static void Release(FlexBase rig)
        {
            rig.theRadio = null;
            rig.Dispose();
        }

        private static string UniqueSerial() =>
            "H5-" + Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>
        /// A WAN <see cref="Radio"/> built the way the SmartLink list parse
        /// builds one: the internal constructor, a serial, and no network. It
        /// is never put in FlexLib's discovery dictionary, exactly as a
        /// SmartLink-only radio never is.
        /// </summary>
        private static Radio NewWanRadio(string serial, string nickname = "")
        {
            var radio = (Radio)Activator.CreateInstance(
                typeof(Radio), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: new object[] { true }, culture: null);
            Assert.True(radio != null, "Radio's internal ctor moved; every test here would be vacuous.");
            SetInternal(radio, nameof(Radio.Serial), serial);
            // Every radio from the server's list has an address, and FlexLib's
            // own ToString (which RemoveRadio logs) assumes one. A documentation
            // address; nothing here ever sends to it.
            FieldInfo ip = typeof(Radio).GetField("_ip", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(ip != null, "Radio._ip moved.");
            ip!.SetValue(radio, System.Net.IPAddress.Parse("192.0.2.1"));
            if (!string.IsNullOrEmpty(nickname))
            {
                // The field, not the property: the public setter sends the
                // radio a rename command.
                FieldInfo field = typeof(Radio).GetField("_nickname", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.True(field != null, "Radio._nickname moved.");
                field!.SetValue(radio, nickname);
            }
            return radio;
        }

        private static void SetInternal(Radio radio, string property, object value)
        {
            MethodInfo setter = typeof(Radio).GetProperty(property)!.GetSetMethod(nonPublic: true);
            Assert.True(setter != null, "Radio." + property + "'s internal setter moved.");
            setter!.Invoke(radio, new[] { value });
        }

        private static object Transport(Radio radio)
        {
            object transport = typeof(Radio)
                .GetField("_commandCommunication", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(radio);
            Assert.NotNull(transport);
            return transport!;
        }

        /// <summary>
        /// Put the radio and its transport in the state a live connection has:
        /// both say connected. Set on the fields, not through the setters, so no
        /// rise is dispatched — only the fall is under test.
        /// </summary>
        private static void MarkLive(Radio radio, FlexBase rig = null)
        {
            // The rig's IsConnected is set ONLY by its property handler seeing
            // Connected change. Raised here so its falling afterwards is proof
            // the fall was dispatched to the rig — the positive control for
            // every "nothing was sealed" below.
            if (rig != null)
                typeof(FlexBase).GetField("_IsConnected", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(rig, true);
            typeof(Radio).GetField("_connected", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(radio, true);
            object transport = Transport(radio);
            FieldInfo isConnected = transport.GetType()
                .GetField("_isConnected", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(isConnected != null, "the TLS transport's connected flag moved");
            isConnected!.SetValue(transport, true);
            Assert.True(radio.Connected);
        }

        /// <summary>The rig's own handler really received the fall.</summary>
        private static void AssertTheRigSawTheFall(FlexBase rig) =>
            Assert.False(rig.IsConnected, "the fall never reached the rig's property handler");

        /// <summary>
        /// The link dies. Enters exactly where a real loss does: the transport
        /// goes down and raises <c>IsConnectedChanged(false)</c>. Everything
        /// after that is FlexLib's own chain and this rig's own handlers.
        /// </summary>
        private static void LoseTheTransport(Radio radio)
        {
            object transport = Transport(radio);
            transport.GetType().GetMethod("Disconnect", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(transport, null);
        }

        /// <summary>Wire the property handler the way Connect does.</summary>
        private static void WireAsConnectDoes(FlexBase rig, Radio radio) =>
            typeof(FlexBase).GetMethod("wireRadioPropertyHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig, new object[] { radio });

        /// <summary>Let go of it the way Disconnect does.</summary>
        private static void UnwireAsDisconnectDoes(FlexBase rig, Radio radio) =>
            typeof(FlexBase).GetMethod("unwireRadioPropertyHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig, new object[] { radio });

        /// <summary>How many copies of THIS rig's property handler the radio carries.</summary>
        private static int HandlerCopies(FlexBase rig, Radio radio)
        {
            FieldInfo backing = null;
            for (Type t = typeof(Radio); t != null && backing == null; t = t.BaseType)
                backing = t.GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(backing != null, "PropertyChanged's backing field moved; the count would be vacuous");
            var list = (PropertyChangedEventHandler)backing!.GetValue(radio);
            if (list == null) return 0;
            return list.GetInvocationList().Count(d =>
                ReferenceEquals(d.Target, rig) && d.Method.Name == "radioPropertyChangedHandler");
        }

        /// <summary>
        /// Put the radio in FlexLib's REAL LAN discovery dictionary, the way a
        /// discovery packet does, so <c>API.RemoveRadio</c> really raises
        /// <c>RadioRemoved</c> for it. Returns the undo.
        /// </summary>
        private static Action MakeDualHomed(Radio radio)
        {
            Type info = typeof(API).GetNestedType("RadioInfo", BindingFlags.NonPublic);
            Assert.True(info != null, "API.RadioInfo moved; the dual-homed case would be vacuous");
            object entry = Activator.CreateInstance(info!, radio);
            var dict = (IDictionary)typeof(API)
                .GetField("RadioDictionary", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;
            dict[radio.Serial] = entry;
            return () => { if (dict.Contains(radio.Serial)) dict.Remove(radio.Serial); };
        }

        /// <summary>
        /// Subscribe the rig's production removal handler to FlexLib's static
        /// event, as <c>apiInit</c> does — without <c>API.Init</c>, which would
        /// start discovery on the network. Returns the undo.
        /// </summary>
        private static Action HookRemovals(FlexBase rig)
        {
            var handler = (API.RadioRemovedEventHandler)Delegate.CreateDelegate(
                typeof(API.RadioRemovedEventHandler), rig,
                typeof(FlexBase).GetMethod("apiRadioRemovedHandler", BindingFlags.Instance | BindingFlags.NonPublic)!);
            API.RadioRemoved += handler;
            return () => API.RadioRemoved -= handler;
        }

        /// <summary>Counts FlexLib's RadioRemoved for one radio. Returns the undo.</summary>
        private static Action CountRemovals(Radio radio, Action onRemoved)
        {
            API.RadioRemovedEventHandler counter = r => { if (ReferenceEquals(r, radio)) onRemoved(); };
            API.RadioRemoved += counter;
            return () => API.RadioRemoved -= counter;
        }

        // ────────────────────────────────────────────────────────────────
        //  The SmartLink-only path: the case the move exists for
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// <b>Don's path.</b> A radio reached only through SmartLink loses its
        /// link. FlexLib drops <c>Connected</c>, disconnects the object and
        /// calls <c>RemoveRadio</c> — which raises nothing, because the serial
        /// was never in its LAN dictionary. Under Tracks H to H3 this sealed
        /// nothing, on every loss; this is the test that goes red if the
        /// trigger is put back on <c>RadioRemoved</c>.
        /// </summary>
        [Fact]
        public void A_SmartLink_only_drop_seals_though_FlexLib_never_raises_RadioRemoved()
        {
            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "Don's 6300");
            int removals = 0;
            Action unhook = HookRemovals(rig);
            Action uncount = CountRemovals(radio, () => Interlocked.Increment(ref removals));
            try
            {
                rig.theRadio = radio;
                WireAsConnectDoes(rig, radio);
                MarkLive(radio, rig);

                LoseTheTransport(radio);

                // FlexLib really did let go of it...
                Assert.False(radio.Connected);
                AssertTheRigSawTheFall(rig);
                // ...and really raised no removal for it. (The dual-homed test
                // below shows this same counter counting.)
                Assert.Equal(0, removals);
                // And the drop was sealed anyway, once.
                Assert.Equal(1, Volatile.Read(ref _seals));
            }
            finally
            {
                uncount();
                unhook();
                Release(rig);
            }
        }

        /// <summary>
        /// <b>The bench's path.</b> The radio is on the LAN too, so both signals
        /// arrive: <c>Connected</c> false first, then — on the same thread —
        /// <c>RadioRemoved</c> for the same object. Exactly one seal.
        /// </summary>
        [Fact]
        public void A_dual_homed_drop_seals_exactly_once_though_both_signals_arrive()
        {
            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "8600");
            int removals = 0;
            Action undoDict = MakeDualHomed(radio);
            Action unhook = HookRemovals(rig);
            Action uncount = CountRemovals(radio, () => Interlocked.Increment(ref removals));
            try
            {
                rig.theRadio = radio;
                WireAsConnectDoes(rig, radio);
                MarkLive(radio, rig);

                LoseTheTransport(radio);

                Assert.False(radio.Connected);
                AssertTheRigSawTheFall(rig);
                // The positive control for the SmartLink-only test: here the
                // removal genuinely fired, through the rig's own handler too.
                Assert.Equal(1, removals);
                Assert.Equal(1, Volatile.Read(ref _seals));
            }
            finally
            {
                uncount();
                unhook();
                undoDict();
                Release(rig);
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  A deliberate disconnect never seals, on either path
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void A_deliberate_disconnect_does_not_seal(bool dualHomed, bool throughTheDisconnectingLatch)
        {
            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "A");
            int removals = 0;
            Action undoDict = dualHomed ? MakeDualHomed(radio) : () => { };
            Action unhook = HookRemovals(rig);
            Action uncount = CountRemovals(radio, () => Interlocked.Increment(ref removals));
            try
            {
                rig.theRadio = radio;
                WireAsConnectDoes(rig, radio);
                MarkLive(radio, rig);

                if (throughTheDisconnectingLatch)
                {
                    // FlexBase.Disconnect sets this latch, then calls the
                    // radio's Disconnect. Its own body speaks, so it is not
                    // called here; the two steps that matter are.
                    rig.Disconnecting = true;
                    radio.Disconnect();
                }
                else
                {
                    // The station-name-timeout and cancel path.
                    typeof(FlexBase).GetMethod("teardownDisconnect", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(rig, new object[] { "test" });
                }

                // The fall really happened and really reached the handler's
                // path (the radio is no longer connected), and it sealed nothing.
                Assert.False(radio.Connected);
                AssertTheRigSawTheFall(rig);
                Assert.Equal(dualHomed ? 1 : 0, removals);
                Assert.Equal(0, Volatile.Read(ref _seals));
            }
            finally
            {
                uncount();
                unhook();
                undoDict();
                Release(rig);
            }
        }

        private static FieldInfo FirmwareSentField =>
            typeof(FlexBase).GetField("_firmwareUpdateSent", BindingFlags.Instance | BindingFlags.NonPublic)!;

        /// <summary>FlexLib's own updating flag, set the way SendUpdateFile
        /// sets it once the radio has handed back an upgrade port.</summary>
        private static void MarkFlexLibUpdating(Radio radio, bool updating)
        {
            FieldInfo f = typeof(Radio).GetField("_updating", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(f != null, "Radio._updating moved");
            f!.SetValue(radio, updating);
        }

        /// <summary>
        /// A radio restarting because we sent it firmware is not a drop. While
        /// the seal lived on <c>RadioRemoved</c> it was never reached here —
        /// FlexLib's <c>RemoveRadio</c> returns early for an updating radio — and
        /// moving the trigger must not start sealing every firmware update the
        /// operator asked for.
        ///
        /// <para>Track H6: the exemption now needs FlexLib's confirmation as
        /// well as our flag, so this sets both — the state a real transfer
        /// leaves — and it checks the exemption is SPENT by the restart.</para>
        /// </summary>
        [Fact]
        public void A_radio_restarting_after_we_sent_it_firmware_does_not_seal()
        {
            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "A");
            try
            {
                rig.theRadio = radio;
                WireAsConnectDoes(rig, radio);
                MarkLive(radio, rig);
                FirmwareSentField.SetValue(rig, true);
                MarkFlexLibUpdating(radio, true);

                LoseTheTransport(radio);

                Assert.False(radio.Connected);
                AssertTheRigSawTheFall(rig);
                Assert.Equal(0, Volatile.Read(ref _seals));
                // One restart, one exemption.
                Assert.False((bool)FirmwareSentField.GetValue(rig)!);
            }
            finally
            {
                Release(rig);
            }
        }

        /// <summary>
        /// <b>A firmware transfer that failed does not hide a later real
        /// drop.</b> Sol's review of H3, finding 6: the flag was set before
        /// <c>SendUpdateFile</c> and cleared only at the next Connect, but
        /// FlexLib's <c>SendUpdateFile</c> returns WITHOUT THROWING on a missing
        /// file — and on an upgrade port it cannot parse, and after catching a
        /// failed transfer. After any of those, a genuine loss of that same
        /// connection was misfiled as a firmware restart and sealed nothing.
        ///
        /// <para>Driven through the real <c>BeginFirmwareUpdate</c> and the real
        /// FlexLib <c>SendUpdateFile</c>, with an image that does not exist:
        /// FlexLib returns a completed task and reports nothing wrong. Then the
        /// link genuinely drops, entering where a real loss does.</para>
        ///
        /// <para>Positive control, run by hand at H6: with the exemption keyed
        /// to the flag alone and nothing clearing it, this test goes red — no
        /// seal.</para>
        /// </summary>
        [Fact]
        public void A_failed_firmware_transfer_does_not_hide_a_later_real_drop()
        {
            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "A");
            // Firmware is LAN only; BeginFirmwareUpdate refuses a SmartLink
            // connection before it sends anything.
            SetInternal(radio, nameof(Radio.IsWan), false);
            try
            {
                rig.theRadio = radio;
                WireAsConnectDoes(rig, radio);
                MarkLive(radio, rig);

                string missing = Path.Combine(Path.GetTempPath(),
                    "jjflex-h6-no-such-image-" + Guid.NewGuid().ToString("N") + ".ssdr");
                Assert.False(File.Exists(missing));

                // FlexLib says nothing is wrong...
                Assert.True(rig.BeginFirmwareUpdate(missing));
                Task settled = (Task)typeof(FlexBase)
                    .GetField("_firmwareTransferSettled", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(rig)!;
                Assert.True(settled.Wait(TimeSpan.FromSeconds(10)), "the transfer never settled");
                // ...but it is not updating, so the exemption is gone.
                Assert.False(FlexBase.RadioReportsUpdating(radio));
                Assert.False((bool)FirmwareSentField.GetValue(rig)!);

                // A genuine drop on this same connection.
                LoseTheTransport(radio);

                Assert.False(radio.Connected);
                AssertTheRigSawTheFall(rig);
                Assert.Equal(1, Volatile.Read(ref _seals));
            }
            finally
            {
                Release(rig);
            }
        }

        /// <summary>
        /// Our flag alone — an image handed to FlexLib whose transfer then
        /// failed inside FlexLib, which clears its own updating flag — is not a
        /// firmware restart. The fall seals, even with the flag still set (the
        /// transfer's settle has not run yet, say).
        /// </summary>
        [Fact]
        public void Our_flag_without_FlexLib_updating_is_a_drop()
        {
            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "A");
            try
            {
                rig.theRadio = radio;
                WireAsConnectDoes(rig, radio);
                MarkLive(radio, rig);
                FirmwareSentField.SetValue(rig, true);
                MarkFlexLibUpdating(radio, false);

                LoseTheTransport(radio);

                AssertTheRigSawTheFall(rig);
                Assert.Equal(1, Volatile.Read(ref _seals));
            }
            finally
            {
                Release(rig);
            }
        }

        /// <summary>
        /// The exemption reads FlexLib's internal <c>Radio.Updating</c> by
        /// reflection. If a FlexLib upgrade renames it, the reader answers false
        /// — firmware restarts would start sealing as drops — and THIS goes red
        /// the same day, rather than the bench finding it.
        /// </summary>
        [Fact]
        public void The_FlexLib_updating_flag_is_still_where_the_exemption_reads_it()
        {
            var radio = NewWanRadio(UniqueSerial(), "A");
            Assert.False(FlexBase.RadioReportsUpdating(radio));
            MarkFlexLibUpdating(radio, true);
            Assert.True(FlexBase.RadioReportsUpdating(radio));
        }

        [Fact]
        public void The_fall_seals_only_for_our_own_unasked_loss()
        {
            Assert.True(FlexBase.ConnectionFallSealsTheCapture(
                FlexBase.RadioRemovalKind.ConnectionLostOurRadio, firmwareUpdateSent: false, radioUpdating: false));

            // A firmware restart is our flag AND FlexLib's; either alone is a
            // drop (Track H6).
            Assert.False(FlexBase.ConnectionFallSealsTheCapture(
                FlexBase.RadioRemovalKind.ConnectionLostOurRadio, firmwareUpdateSent: true, radioUpdating: true));
            Assert.True(FlexBase.ConnectionFallSealsTheCapture(
                FlexBase.RadioRemovalKind.ConnectionLostOurRadio, firmwareUpdateSent: true, radioUpdating: false));
            Assert.True(FlexBase.ConnectionFallSealsTheCapture(
                FlexBase.RadioRemovalKind.ConnectionLostOurRadio, firmwareUpdateSent: false, radioUpdating: true));

            Assert.False(FlexBase.ConnectionFallSealsTheCapture(
                FlexBase.RadioRemovalKind.SelfInitiated, firmwareUpdateSent: false, radioUpdating: false));
            Assert.False(FlexBase.ConnectionFallSealsTheCapture(
                FlexBase.RadioRemovalKind.DiscoveryLoss, firmwareUpdateSent: false, radioUpdating: false));
        }

        /// <summary>
        /// Another Radio object's fall says nothing about our session — a
        /// handler this rig left on an object it has since moved away from, for
        /// instance.
        /// </summary>
        [Fact]
        public void A_fall_on_an_object_that_is_not_our_connection_does_not_seal()
        {
            var rig = NewRig();
            var ours = NewWanRadio(UniqueSerial(), "ours");
            var other = NewWanRadio(UniqueSerial(), "other");
            try
            {
                rig.theRadio = ours;
                WireAsConnectDoes(rig, other);
                MarkLive(other, rig);

                LoseTheTransport(other);

                Assert.False(other.Connected);
                AssertTheRigSawTheFall(rig);
                Assert.Equal(0, Volatile.Read(ref _seals));
            }
            finally
            {
                Release(rig);
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  One handler per object, however often it is reconnected
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Connect, disconnect, connect on the same Radio object — the
        /// SmartLink handle bank can hand one back — and it carries one copy of
        /// the handler, not two. And a second Connect without a Disconnect in
        /// between, which a failed leg of the connect walk produces, does not
        /// stack one either.
        /// </summary>
        [Fact]
        public void A_reused_object_carries_one_handler()
        {
            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "A");
            try
            {
                WireAsConnectDoes(rig, radio);
                Assert.Equal(1, HandlerCopies(rig, radio));

                UnwireAsDisconnectDoes(rig, radio);
                Assert.Equal(0, HandlerCopies(rig, radio));

                WireAsConnectDoes(rig, radio);
                Assert.Equal(1, HandlerCopies(rig, radio));

                WireAsConnectDoes(rig, radio);
                Assert.Equal(1, HandlerCopies(rig, radio));
            }
            finally
            {
                Release(rig);
            }
        }

        /// <summary>
        /// The count above is only worth something if Connect and Disconnect
        /// really use those two methods, and if nothing else subscribes the
        /// handler raw. Source-read, because neither can run without a radio.
        /// </summary>
        [Fact]
        public void Connect_and_Disconnect_are_what_wire_and_unwire_it()
        {
            string source = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.cs"));

            // Positive control: the reader finds the handler at all.
            Assert.Contains("private void radioPropertyChangedHandler(object sender, PropertyChangedEventArgs e)",
                            source, StringComparison.Ordinal);

            // Nothing subscribes it raw any more — the old Connect line did,
            // and it stacked a copy per same-object reconnect.
            Assert.DoesNotContain("PropertyChanged += new PropertyChangedEventHandler(radioPropertyChangedHandler)",
                                  source, StringComparison.Ordinal);

            int connect = source.IndexOf("public bool Connect(string serial, bool lowBW", StringComparison.Ordinal);
            int acquire = source.IndexOf("theRadio = findRadioForConnect(", connect, StringComparison.Ordinal);
            int wire = source.IndexOf("wireRadioPropertyHandler(theRadio);", acquire, StringComparison.Ordinal);
            Assert.True(connect > 0 && acquire > connect && wire > acquire && wire < acquire + 6000,
                        "Connect does not wire the property handler through the idempotent helper");

            int disconnect = source.IndexOf("public void Disconnect()", StringComparison.Ordinal);
            int nextMember = source.IndexOf("public const int SkFarewellFallbackMs", disconnect, StringComparison.Ordinal);
            Assert.True(disconnect > 0 && nextMember > disconnect);
            string body = source.Substring(disconnect, nextMember - disconnect);
            Assert.Contains("unwireRadioPropertyHandler(releasing);", body, StringComparison.Ordinal);
        }

        // ────────────────────────────────────────────────────────────────
        //  Where the seal is, and where it must not be
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// One call site in the whole file, reached from the Connected case, and
        /// none in the removal handler. The structural half of "exactly once":
        /// the removal arm cannot ask for a second seal because it does not ask
        /// for one at all. The connection lifetime's claim covers repeats of the
        /// one call.
        /// </summary>
        [Fact]
        public void The_seal_is_taken_on_the_fall_and_nowhere_else()
        {
            string source = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.cs"));

            int calls = 0;
            for (int at = source.IndexOf("CaptureSeal.AfterConnectionDrop(", StringComparison.Ordinal);
                 at >= 0;
                 at = source.IndexOf("CaptureSeal.AfterConnectionDrop(", at + 1, StringComparison.Ordinal))
                calls++;
            Assert.Equal(1, calls);

            int method = source.IndexOf("private void sealIfOurConnectionDropped(Radio r)", StringComparison.Ordinal);
            Assert.True(method > 0, "the fall's seal method is gone");
            int seal = source.IndexOf("CaptureSeal.AfterConnectionDrop(", StringComparison.Ordinal);
            int methodEnd = source.IndexOf("private void wireRadioPropertyHandler(", method, StringComparison.Ordinal);
            Assert.True(seal > method && seal < methodEnd, "the one seal call is not in sealIfOurConnectionDropped");

            // Reached from the Connected property's handler, when it falls.
            // Track H6 moved that handler out of the switch and ahead of every
            // ordinary trace line (the fall must not wait on the trace gate
            // before its claim), so it is read from its own method now.
            int handlerTop = source.IndexOf("private void radioPropertyChangedHandler(object sender, PropertyChangedEventArgs e)", StringComparison.Ordinal);
            Assert.True(handlerTop > 0);
            string dispatch = source.Substring(handlerTop, source.IndexOf("switch (e.PropertyName)", handlerTop, StringComparison.Ordinal) - handlerTop);
            Assert.Contains("onRadioConnectedChanged(r);", dispatch, StringComparison.Ordinal);
            int connectedMethod = source.IndexOf("private void onRadioConnectedChanged(Radio r)", StringComparison.Ordinal);
            Assert.True(connectedMethod > 0, "the Connected handler moved");
            string connectedBody = source.Substring(connectedMethod,
                source.IndexOf("private void radioPropertyChangedHandler(", connectedMethod, StringComparison.Ordinal) - connectedMethod);
            Assert.Contains("if (!nowConnected) sealIfOurConnectionDropped(r);", connectedBody, StringComparison.Ordinal);

            // And the removal handler's drop arm is bookkeeping: no seal there.
            int handler = source.IndexOf("private void apiRadioRemovedHandler(Radio r)", StringComparison.Ordinal);
            int handlerEnd = source.IndexOf("RaiseRadioRemoved(this, r.Serial", handler, StringComparison.Ordinal);
            Assert.True(handler > 0 && handlerEnd > handler);
            Assert.DoesNotContain("AfterConnectionDrop", source.Substring(handler, handlerEnd - handler),
                                  StringComparison.Ordinal);
        }

        // ────────────────────────────────────────────────────────────────
        //  The fall does not wait on a file (Track H6)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// <b>A slow transition holds the trace gate, and the fall returns
        /// anyway, having claimed the drop and queued its seal.</b> Sol's review
        /// of H3, finding 1: the drop read <c>TraceCoordinator.CurrentHandle</c>
        /// through the same gate that is held across a transition's flush,
        /// close, move and successor open, and wrote an ordinary trace line
        /// first — so a disk stalled under a problem-report checkpoint stalled
        /// FlexLib's transport thread, and the claim waited with it.
        ///
        /// <para>Driven with a real session in a temporary tree, at capture
        /// detail, and a checkpoint held INSIDE the gate on a barrier. The
        /// barrier is proved to hold the gate first — an ordinary writer is
        /// shown to block — so a fall that returns is a measurement, not a gate
        /// nobody was holding. Then the lines the fall deferred are shown to
        /// land in the session they describe, the sealed one.</para>
        ///
        /// <para>Positive control, run by hand at H6 (see the report): with
        /// <c>CurrentHandle</c> put back behind the gate, the fall does not
        /// return and this test goes red.</para>
        /// </summary>
        [Fact]
        public void A_fall_returns_while_a_slow_transition_holds_the_trace_gate()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-h6-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string livePath = Path.Combine(dir, "JJFlexRadioTrace.txt");
            string savedRoot = TraceCoordinator.ArchiveRootDir;
            bool savedOn = Tracing.On;
            TraceLevel savedLevel = Tracing.TheSwitch.Level;

            var queued = new List<Action>();
            var inside = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            TraceArchiveTicket sealedTicket = null;
            Thread checkpoint = null, writer = null, fall = null;

            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "Don's 6300");
            try
            {
                try
                {
                    typeof(TraceCoordinator)
                        .GetMethod("RestoreSessionForTests", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, new object[] { null });
                    TraceCoordinator.ArchiveRootDir = Path.Combine(dir, "Traces");
                    Tracing.TheSwitch.Level = TraceLevel.Verbose;
                    Tracing.On = true;
                    TraceTransitionResult began = TraceCoordinator.Begin(livePath, TraceLevel.Verbose, asDetailedCapture: false);
                    Assert.Equal(TraceTransition.Accepted, began.Status);
                    TraceSessionHandle session = began.Successor;

                    // The hook does what the application's does: seal the
                    // session the drop named, through the boundary.
                    CaptureSeal.SealHook = req =>
                    {
                        Interlocked.Increment(ref _seals);
                        var lines = new List<string>();
                        if (!string.IsNullOrEmpty(req.PartialMeterLine)) lines.Add(req.PartialMeterLine);
                        TraceTransitionResult sealedNow = TraceCoordinator.TrySeal(new TraceSealRequest
                        {
                            Expected = (TraceSessionHandle)req.ExpectedSession,
                            OperationId = req.DropOperationId,
                            Outcome = TraceSessionOutcome.ConnectionDropped,
                            OutcomeDetail = req.OutcomeDetail,
                            TerminalLines = lines,
                            Resume = TraceResumeIntent.None,
                        });
                        sealedTicket = sealedNow.Ticket;
                        return new CaptureSealResult { Refused = !sealedNow.Owned };
                    };
                    // Hold the seal worker back so its timing is ours.
                    CaptureSeal.Queue = work => { lock (queued) queued.Add(work); };

                    rig.theRadio = radio;
                    WireAsConnectDoes(rig, radio);
                    MarkLive(radio, rig);

                    // A problem-report checkpoint that stalls on the disk,
                    // holding the gate while it does.
                    TraceCoordinator.TransitionProbeForTests = point =>
                    {
                        if (point != "checkpoint:detached") return;
                        inside.Set();
                        release.Wait(TimeSpan.FromSeconds(30));
                    };
                    checkpoint = new Thread(() => TraceCoordinator.SnapshotForBundle(session)) { IsBackground = true };
                    checkpoint.Start();
                    Assert.True(inside.Wait(TimeSpan.FromSeconds(10)), "the checkpoint never reached its probe");

                    // The barrier really holds the gate: an ordinary line cannot
                    // get in.
                    writer = new Thread(() => Tracing.TraceLine("H6: an ordinary line, held at the gate")) { IsBackground = true };
                    writer.Start();
                    Assert.False(writer.Join(TimeSpan.FromMilliseconds(300)),
                        "an ordinary trace line got past the barrier — the gate is not held, and the measurement below would be vacuous");

                    // The fall, on its own thread, entering where a real one
                    // does. What is measured is OUR handler for the Connected
                    // fall reaching its end — ConnectionStateChanged is its last
                    // statement — not FlexLib's whole teardown: after our handler
                    // returns, FlexLib's own Disconnect raises further property
                    // changes on this same thread, and those still trace through
                    // the gate (reported by H6, not changed: the ruling is about
                    // the claim).
                    var handlerDone = new ManualResetEventSlim(false);
                    rig.ConnectionStateChanged += connected => { if (!connected) handlerDone.Set(); };
                    fall = new Thread(() => LoseTheTransport(radio)) { IsBackground = true };
                    fall.Start();
                    Assert.True(handlerDone.Wait(TimeSpan.FromSeconds(5)),
                        "the Connected fall's handler did not return while a transition held the trace gate — FlexLib's transport thread is waiting on a file before the drop is claimed");
                    Assert.True(checkpoint.IsAlive, "the gate was released before the handler returned; nothing was measured");

                    // And it did its job before returning: the drop was claimed
                    // and its seal queued, not put off until the disk recovered.
                    AssertTheRigSawTheFall(rig);
                    lock (queued) Assert.Single(queued);
                }
                finally
                {
                    release.Set();
                    TraceCoordinator.TransitionProbeForTests = null;
                    checkpoint?.Join(TimeSpan.FromSeconds(10));
                    writer?.Join(TimeSpan.FromSeconds(10));
                    fall?.Join(TimeSpan.FromSeconds(10));
                    // The writers this test holds at the gate are slow writes by
                    // construction, and the #434 slow-write marker allows itself
                    // one a minute. Give that budget back, or a later test that
                    // measures the marker inherits this one's stall.
                    typeof(Tracing).GetField("lastSlowMarkerStamp", BindingFlags.NonPublic | BindingFlags.Static)
                        ?.SetValue(null, 0L);
                }

                // Now let the worker run: it writes the deferred lines first,
                // then seals.
                Action work;
                lock (queued) work = queued[0];
                work();

                Assert.Equal(1, Volatile.Read(ref _seals));
                Assert.NotNull(sealedTicket);
                Assert.True(sealedTicket.Completion.Wait(TimeSpan.FromSeconds(60)), "the archive worker never finished");
                TraceArchiveCompletion done = sealedTicket.Completion.Result;
                Assert.True(done.ArchiveCommitted, "the archive was not committed: " + done.FailureStage);
                string text = File.ReadAllText(SessionArchive.ExtractTraceText(
                    done.ArchiveFullPath, Path.Combine(dir, "extract")));

                // The reader is looking at the sealed part, the one that began
                // after the checkpoint...
                Assert.Contains("--- trace continues from part 001", text, StringComparison.Ordinal);
                // ...and the lines the fall wrote on FlexLib's thread are in it:
                // the session they describe, not a successor.
                Assert.Contains("Connected:False", text, StringComparison.Ordinal);
                Assert.Contains("our connection dropped without us asking", text, StringComparison.Ordinal);
            }
            finally
            {
                if (TraceCoordinator.Recording)
                {
                    TraceCoordinator.TrySeal(new TraceSealRequest
                    {
                        ShutdownAuthority = true,
                        Outcome = TraceSessionOutcome.CleanExit,
                        Resume = TraceResumeIntent.None,
                        OperationId = Guid.NewGuid(),
                    });
                }
                TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(20));
                TraceCoordinator.ArchiveRootDir = savedRoot;
                Tracing.TheSwitch.Level = savedLevel;
                Tracing.On = savedOn;
                Release(rig);
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  What this track does NOT cover, pinned so it cannot be forgotten
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// <b>A KNOWN GAP, NOT A DESIGN (#620).</b> FlexLib's
        /// <c>Radio.Disconnect</c> unhooks the object from its transport and
        /// re-hooks only for a firmware update; <c>Radio.Connect</c> never
        /// hooks. So a Radio object connected a SECOND time — every successful
        /// remote <c>RetryConnect</c>, and a SmartLink reconnect handed the same
        /// object — raises nothing when that connection dies: <c>Connected</c>
        /// stays true, nothing is removed, and no seal can be taken. Fixing it
        /// is a three-line change inside FlexLib that awaits Noel's ruling.
        ///
        /// <para><b>When this test fails, the re-hook has landed.</b> That is
        /// the intended signal: turn it round so it asserts the second loss
        /// seals, and do not restore the old assertion.</para>
        /// </summary>
        [Fact]
        public void Until_the_620_rehook_lands_a_reused_Radio_is_deaf_to_its_second_loss()
        {
            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "A");
            try
            {
                rig.theRadio = radio;
                WireAsConnectDoes(rig, radio);
                MarkLive(radio, rig);

                LoseTheTransport(radio);
                Assert.Equal(1, Volatile.Read(ref _seals));   // the first loss is covered
                AssertTheRigSawTheFall(rig);

                // The object comes back into use and its link dies again.
                MarkLive(radio, rig);
                LoseTheTransport(radio);

                Assert.True(radio.Connected, "the second loss reached the Radio — has the #620 re-hook landed?");
                // The application still believes it is connected to a dead radio.
                Assert.True(rig.IsConnected);
                Assert.Equal(1, Volatile.Read(ref _seals));
            }
            finally
            {
                Release(rig);
            }

            // And the vendor's Connect is where the re-hook would go: read it, so
            // the emulation above cannot be the only evidence.
            string vendor = File.ReadAllText(Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "FlexLib_API", "FlexLib", "Radio.cs"));
            int connect = vendor.IndexOf("public bool Connect(string gui_client_id = null)", StringComparison.Ordinal);
            Assert.True(connect > 0, "Radio.Connect moved; find it before trusting this test");
            int connectEnd = vendor.IndexOf("public void Disconnect()", connect, StringComparison.Ordinal);
            Assert.True(connectEnd > connect);
            // Positive control: the hook this looks for is really spelled this
            // way, in the constructor.
            Assert.Contains("_commandCommunication.IsConnectedChanged += _commandCommunication_IsConnectedChanged;",
                            vendor, StringComparison.Ordinal);
            Assert.DoesNotContain("IsConnectedChanged +=", vendor.Substring(connect, connectEnd - connect),
                                  StringComparison.Ordinal);
        }
    }
}
