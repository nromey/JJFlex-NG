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
    /// The capture archive is taken where a drop is signalled on EVERY path: our
    /// Radio's <c>Connected</c> property falling. Sprint 45 Track H5.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the trigger moved.</b> Tracks H, H2 and H3 archived on
    /// <c>API.RadioRemoved</c>. FlexLib raises that event only for a serial in
    /// its LAN discovery dictionary (<c>API.RemoveRadio</c> returns without an
    /// event otherwise), and only LAN discovery writes the dictionary. A radio
    /// reached only through SmartLink is never in it, so on that path — Don's
    /// 6300 — no drop was ever archived. The bench's 8600 is on the LAN too,
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
    // which is what makes the process-wide archive hook, queue, claim, trace
    // session and FlexLib's static discovery dictionary safe to drive here.
    public sealed class ConnectionFallArchiveTests : IDisposable
    {
        private readonly Func<CaptureArchiveRequest, CaptureArchiveResult> _savedHook;
        private readonly Action<Action> _savedQueue;
        private readonly TraceSession _savedSession;
        private int _archives;

        public ConnectionFallArchiveTests()
        {
            _savedHook = CaptureArchive.ArchiveHook;
            _savedQueue = CaptureArchive.Queue;
            _savedSession = TraceSessionContext.Current;
            CaptureArchive.ForgetClaimForTests();
            // Both claim spaces, together. A drop's operation id is built from
            // the connection token's ordinal, and forgetting the tokens
            // restarts the ordinals — so a ticket another class's test left in
            // the coordinator under that id would answer this class's archives
            // with AlreadyClaimed and somebody else's session (seen 2026-09-24
            // once DropNoticeStateTests began leaving real tickets behind).
            TraceCoordinator.ResetClaimsForTests();

            // Count archives, and run the worker inline so a count is a fact the
            // moment the call returns rather than something to wait for.
            CaptureArchive.ArchiveHook = _ =>
            {
                Interlocked.Increment(ref _archives);
                return new CaptureArchiveResult { ArchivePath = @"C:\Traces\fall.zip", SuccessorOpened = true };
            };
            CaptureArchive.Queue = work => work();
            TraceSessionContext.BeginSession();
        }

        public void Dispose()
        {
            CaptureArchive.ArchiveHook = _savedHook;
            CaptureArchive.Queue = _savedQueue;
            CaptureArchive.ForgetClaimForTests();
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
            OfflineCommandProducer.StartTls(radio);
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

        /// <summary>
        /// Wire the radio the way Connect does: both seams. The station wiring
        /// carries the radio's PropertyChanged through the attempt's binding
        /// (Track G); the producer wiring carries the qualified connection-state
        /// and client reports (Track H). Connect calls the two in this order.
        /// </summary>
        private static void WireAsConnectDoes(FlexBase rig, Radio radio)
        {
            rig.WireStationHandlers(radio);
            typeof(FlexBase).GetMethod("wireProducerHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig, new object[] { radio });
        }

        /// <summary>Let go of both the way Disconnect does.</summary>
        private static void UnwireAsDisconnectDoes(FlexBase rig, Radio radio)
        {
            typeof(FlexBase).GetMethod("unwireProducerHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(rig, new object[] { radio });
            rig.UnwireStationHandlers(radio);
        }

        /// <summary>
        /// How many PropertyChanged subscriptions the radio carries, in all.
        /// Read against a baseline taken before wiring, because the vendor's
        /// own ALE composite subscribes one in the Radio constructor. The
        /// station wiring's closure is what carries the rig's property handler
        /// now, so a name filter on the rig's own method would count nothing
        /// and prove nothing; the delta is the count that means something.
        /// </summary>
        private static int PropertyHandlerCopies(Radio radio)
        {
            FieldInfo backing = null;
            for (Type t = typeof(Radio); t != null && backing == null; t = t.BaseType)
                backing = t.GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(backing != null, "PropertyChanged's backing field moved; the count would be vacuous");
            var list = (PropertyChangedEventHandler)backing!.GetValue(radio);
            return list == null ? 0 : list.GetInvocationList().Length;
        }

        /// <summary>How many qualified connection-state subscriptions the radio carries.</summary>
        private static int ProducerHandlerCopies(Radio radio)
        {
            FieldInfo backing = typeof(Radio).GetField("CommandConnectionChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(backing != null, "CommandConnectionChanged's backing field moved; the count would be vacuous");
            var list = (Delegate)backing!.GetValue(radio);
            return list == null ? 0 : list.GetInvocationList().Length;
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
        /// was never in its LAN dictionary. Under Tracks H to H3 this archived
        /// nothing, on every loss; this is the test that goes red if the
        /// trigger is put back on <c>RadioRemoved</c>.
        /// </summary>
        [Fact]
        public void A_SmartLink_only_drop_archives_though_FlexLib_never_raises_RadioRemoved()
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
                // And the drop was archived anyway, once.
                Assert.Equal(1, Volatile.Read(ref _archives));
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
        /// <c>RadioRemoved</c> for the same object. Exactly one archive.
        /// </summary>
        [Fact]
        public void A_dual_homed_drop_archives_exactly_once_though_both_signals_arrive()
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
                Assert.Equal(1, Volatile.Read(ref _archives));
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
        //  A deliberate disconnect never archives, on either path
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void A_deliberate_disconnect_does_not_archive(bool dualHomed, bool throughTheDisconnectingLatch)
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
                // path (the radio is no longer connected), and it archived nothing.
                Assert.False(radio.Connected);
                AssertTheRigSawTheFall(rig);
                Assert.Equal(dualHomed ? 1 : 0, removals);
                Assert.Equal(0, Volatile.Read(ref _archives));
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
        /// the archive lived on <c>RadioRemoved</c> it was never reached here —
        /// FlexLib's <c>RemoveRadio</c> returns early for an updating radio — and
        /// moving the trigger must not start archiving every firmware update the
        /// operator asked for.
        ///
        /// <para>Track H6: the exemption now needs FlexLib's confirmation as
        /// well as our flag, so this sets both — the state a real transfer
        /// leaves — and it checks the exemption is SPENT by the restart.</para>
        /// </summary>
        [Fact]
        public void A_radio_restarting_after_we_sent_it_firmware_does_not_archive()
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
                Assert.Equal(0, Volatile.Read(ref _archives));
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
        /// connection was misfiled as a firmware restart and archived nothing.
        ///
        /// <para>Driven through the real <c>BeginFirmwareUpdate</c> and the real
        /// FlexLib <c>SendUpdateFile</c>, with an image that does not exist:
        /// FlexLib returns a completed task and reports nothing wrong. Then the
        /// link genuinely drops, entering where a real loss does.</para>
        ///
        /// <para>Positive control, run by hand at H6: with the exemption keyed
        /// to the flag alone and nothing clearing it, this test goes red — no
        /// archive.</para>
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
                Assert.Equal(1, Volatile.Read(ref _archives));
            }
            finally
            {
                Release(rig);
            }
        }

        /// <summary>
        /// Our flag alone — an image handed to FlexLib whose transfer then
        /// failed inside FlexLib, which clears its own updating flag — is not a
        /// firmware restart. The fall archives, even with the flag still set (the
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
                Assert.Equal(1, Volatile.Read(ref _archives));
            }
            finally
            {
                Release(rig);
            }
        }

        /// <summary>
        /// The exemption reads FlexLib's internal <c>Radio.Updating</c> by
        /// reflection. If a FlexLib upgrade renames it, the reader answers false
        /// — firmware restarts would start archiving as drops — and THIS goes red
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
        public void The_fall_archives_only_for_our_own_unasked_loss()
        {
            Assert.True(FlexBase.ConnectionFallArchivesTheCapture(
                FlexBase.RadioRemovalKind.ConnectionLostOurRadio, firmwareUpdateSent: false, radioUpdating: false));

            // A firmware restart is our flag AND FlexLib's; either alone is a
            // drop (Track H6).
            Assert.False(FlexBase.ConnectionFallArchivesTheCapture(
                FlexBase.RadioRemovalKind.ConnectionLostOurRadio, firmwareUpdateSent: true, radioUpdating: true));
            Assert.True(FlexBase.ConnectionFallArchivesTheCapture(
                FlexBase.RadioRemovalKind.ConnectionLostOurRadio, firmwareUpdateSent: true, radioUpdating: false));
            Assert.True(FlexBase.ConnectionFallArchivesTheCapture(
                FlexBase.RadioRemovalKind.ConnectionLostOurRadio, firmwareUpdateSent: false, radioUpdating: true));

            Assert.False(FlexBase.ConnectionFallArchivesTheCapture(
                FlexBase.RadioRemovalKind.SelfInitiated, firmwareUpdateSent: false, radioUpdating: false));
            Assert.False(FlexBase.ConnectionFallArchivesTheCapture(
                FlexBase.RadioRemovalKind.DiscoveryLoss, firmwareUpdateSent: false, radioUpdating: false));
        }

        /// <summary>
        /// <b>A fall on an abandoned object leaves the live rig connected, and
        /// archives nothing.</b> Another Radio object's fall says nothing about our
        /// session or our connection — a handler this rig left on an object it
        /// has since moved away from, for instance.
        ///
        /// <para><b>This test used to assert the defect.</b> Until Track H6 it
        /// checked that the rig's <c>IsConnected</c> FELL here — "the rig saw
        /// the fall" — which is exactly Sol's finding 7: a stranded handler
        /// flipping the live rig's state. It now asserts the live rig stays
        /// connected and hears nothing, and uses the ignored-change counter as
        /// its proof that the fall really reached the handler.</para>
        ///
        /// <para>Positive control, run by hand at H6: with the sender gate
        /// removed, this goes red.</para>
        /// </summary>
        [Fact]
        public void A_fall_on_an_abandoned_object_leaves_the_live_rig_connected()
        {
            var rig = NewRig();
            var ours = NewWanRadio(UniqueSerial(), "ours");
            var other = NewWanRadio(UniqueSerial(), "other");
            int stateChanges = 0;
            rig.ConnectionStateChanged += _ => Interlocked.Increment(ref stateChanges);
            try
            {
                rig.theRadio = ours;
                WireAsConnectDoes(rig, ours);
                MarkLive(ours, rig);
                WireAsConnectDoes(rig, other);
                MarkLive(other);

                int ignoredBefore = rig.StrandedConnectionChangesIgnored;
                Volatile.Write(ref stateChanges, 0); // Setup now uses a real producer rise.
                LoseTheTransport(other);

                Assert.False(other.Connected);
                // The fall reached this rig's handler...
                Assert.Equal(ignoredBefore + 1, rig.StrandedConnectionChangesIgnored);
                // ...and changed nothing about the live connection.
                Assert.True(ours.Connected);
                Assert.True(rig.IsConnected, "an abandoned object's fall flipped the live rig's IsConnected");
                Assert.Equal(0, Volatile.Read(ref stateChanges));
                Assert.Equal(0, Volatile.Read(ref _archives));

                // Positive control: our own radio's fall still does all three.
                LoseTheTransport(ours);
                AssertTheRigSawTheFall(rig);
                Assert.Equal(1, Volatile.Read(ref stateChanges));
                Assert.Equal(1, Volatile.Read(ref _archives));
            }
            finally
            {
                Release(rig);
            }
        }

        /// <summary>
        /// <b>A stranded object's fall leaves no bare <c>Connected:False</c>
        /// in the live trace</b> (Sol's review of H6, finding 7): the
        /// unqualified line is written only after the sender check, so the
        /// only line about a stranger's fall is the one that names the
        /// stranger and says it was ignored. Read from a real session file,
        /// with our own radio's fall as the positive control that the bare
        /// line still appears for the rig's own connection.
        /// </summary>
        [Fact]
        public void A_stranded_objects_fall_leaves_no_unqualified_connected_line()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-h7-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string livePath = Path.Combine(dir, "JJFlexRadioTrace.txt");
            string savedRoot = TraceCoordinator.ArchiveRootDir;
            bool savedOn = Tracing.On;
            TraceLevel savedLevel = Tracing.TheSwitch.Level;
            var rig = NewRig();
            var ours = NewWanRadio(UniqueSerial(), "ours");
            var other = NewWanRadio(UniqueSerial(), "other");
            try
            {
                typeof(TraceCoordinator)
                    .GetMethod("RestoreSessionForTests", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { null });
                TraceCoordinator.ArchiveRootDir = Path.Combine(dir, "Traces");
                Tracing.TheSwitch.Level = TraceLevel.Verbose;
                Tracing.On = true;
                Assert.Equal(TraceTransition.Accepted,
                    TraceCoordinator.Begin(livePath, TraceLevel.Verbose, asDetailedCapture: false).Status);

                rig.theRadio = ours;
                WireAsConnectDoes(rig, ours);
                MarkLive(ours, rig);
                WireAsConnectDoes(rig, other);
                MarkLive(other);

                LoseTheTransport(other);
                Tracing.FlushDeferred();
                string afterStranger = ReadLive(livePath);
                Assert.Contains("which is not this rig's connection", afterStranger, StringComparison.Ordinal);
                foreach (string line in afterStranger.Split('\n'))
                {
                    if (!line.Contains("Connected:False", StringComparison.Ordinal)) continue;
                    Assert.Contains(other.Serial, line, StringComparison.Ordinal);
                }

                // Positive control: our own fall writes the bare line, so the
                // reader above really would have seen one.
                LoseTheTransport(ours);
                Tracing.FlushDeferred();
                Assert.Contains(ReadLive(livePath).Split('\n'),
                    l => l.Contains("Connected:False", StringComparison.Ordinal)
                         && !l.Contains(other.Serial, StringComparison.Ordinal)
                         && !l.Contains("ignored", StringComparison.Ordinal));
            }
            finally
            {
                if (TraceCoordinator.Recording)
                {
                    TraceCoordinator.TryArchive(new TraceArchiveRequest
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

        private static string ReadLive(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        /// <summary>
        /// With no radio at all, a fall of the object being let go of still
        /// leaves the rig disconnected — that is the only true state — while a
        /// stranger's RISE never makes it connected.
        /// </summary>
        [Fact]
        public void Only_our_radio_speaks_for_the_rig_unless_it_has_none()
        {
            Assert.True(FlexBase.ConnectionStateAppliesToRig(fromOurRadio: true, rigHasRadio: true, nowConnected: true));
            Assert.True(FlexBase.ConnectionStateAppliesToRig(fromOurRadio: true, rigHasRadio: true, nowConnected: false));
            Assert.False(FlexBase.ConnectionStateAppliesToRig(fromOurRadio: false, rigHasRadio: true, nowConnected: false));
            Assert.False(FlexBase.ConnectionStateAppliesToRig(fromOurRadio: false, rigHasRadio: true, nowConnected: true));
            Assert.True(FlexBase.ConnectionStateAppliesToRig(fromOurRadio: false, rigHasRadio: false, nowConnected: false));
            Assert.False(FlexBase.ConnectionStateAppliesToRig(fromOurRadio: false, rigHasRadio: false, nowConnected: true));

            // Driven: a rig whose Disconnect has already let go of theRadio,
            // and whose radio lets go late.
            var rig = NewRig();
            var releasing = NewWanRadio(UniqueSerial(), "late");
            try
            {
                rig.theRadio = releasing;
                WireAsConnectDoes(rig, releasing);
                MarkLive(releasing, rig);
                rig.theRadio = null;
                rig.Disconnecting = true;

                LoseTheTransport(releasing);

                AssertTheRigSawTheFall(rig);
                Assert.Equal(0, rig.StrandedConnectionChangesIgnored);
                Assert.Equal(0, Volatile.Read(ref _archives));
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
        /// each handler, not two. And a second Connect without a Disconnect in
        /// between, which a failed leg of the connect walk produces, does not
        /// stack one either. Both seams are counted: the station wiring's
        /// PropertyChanged closure (one per rig, the previous unwired first)
        /// and the producer subscription (one per radio object, guarded).
        /// Until the Track H merge the producer helper also subscribed the
        /// property handler raw, so the merged tree would have carried two
        /// PropertyChanged subscriptions and dispatched every change twice —
        /// this is the count that would have said so.
        /// </summary>
        [Fact]
        public void A_reused_object_carries_one_handler()
        {
            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "A");
            try
            {
                // The baseline: what the vendor object carries before we touch
                // it (its own ALE composite is one PropertyChanged subscriber).
                int property = PropertyHandlerCopies(radio);
                int producer = ProducerHandlerCopies(radio);
                Assert.Equal(0, producer);

                WireAsConnectDoes(rig, radio);
                Assert.Equal(property + 1, PropertyHandlerCopies(radio));
                Assert.Equal(producer + 1, ProducerHandlerCopies(radio));

                UnwireAsDisconnectDoes(rig, radio);
                Assert.Equal(property, PropertyHandlerCopies(radio));
                Assert.Equal(producer, ProducerHandlerCopies(radio));

                WireAsConnectDoes(rig, radio);
                Assert.Equal(property + 1, PropertyHandlerCopies(radio));
                Assert.Equal(producer + 1, ProducerHandlerCopies(radio));

                WireAsConnectDoes(rig, radio);
                Assert.Equal(property + 1, PropertyHandlerCopies(radio));
                Assert.Equal(producer + 1, ProducerHandlerCopies(radio));
            }
            finally
            {
                Release(rig);
            }
        }

        /// <summary>
        /// The count above is only worth something if Connect and Disconnect
        /// really use those methods, and if nothing else subscribes the
        /// handler raw. Source-read, because neither can run without a radio.
        /// </summary>
        [Fact]
        public void Connect_and_Disconnect_are_what_wire_and_unwire_it()
        {
            string source = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.cs"));

            // Positive control: the reader finds the handler at all. It takes
            // the attempt's binding (Track G), which is why only the station
            // wiring's closure can subscribe it.
            Assert.Contains("private void radioPropertyChangedHandler(object sender, PropertyChangedEventArgs e, ObservationBinding binding)",
                            source, StringComparison.Ordinal);

            // Nothing subscribes it raw any more — the old Connect line did,
            // and it stacked a copy per same-object reconnect; Track H's
            // helper did too, before the merge, beside Track G's closure.
            Assert.DoesNotContain("PropertyChanged += new PropertyChangedEventHandler(radioPropertyChangedHandler)",
                                  source, StringComparison.Ordinal);
            Assert.DoesNotContain("PropertyChanged += radioPropertyChangedHandler", source, StringComparison.Ordinal);

            int connect = source.IndexOf("public bool Connect(string serial, bool lowBW", StringComparison.Ordinal);
            int acquire = source.IndexOf("theRadio = findRadioForConnect(", connect, StringComparison.Ordinal);
            int station = source.IndexOf("WireStationHandlers(theRadio);", acquire, StringComparison.Ordinal);
            int producer = source.IndexOf("wireProducerHandlers(theRadio);", acquire, StringComparison.Ordinal);
            Assert.True(connect > 0 && acquire > connect && station > acquire && station < acquire + 8000,
                        "Connect does not wire the property handler through the station wiring");
            Assert.True(producer > station && producer < acquire + 8000,
                        "Connect does not wire the producer seam through the idempotent helper, after the station wiring");

            int disconnect = source.IndexOf("public void Disconnect()", StringComparison.Ordinal);
            int nextMember = source.IndexOf("public const int SkFarewellFallbackMs", disconnect, StringComparison.Ordinal);
            Assert.True(disconnect > 0 && nextMember > disconnect);
            string body = source.Substring(disconnect, nextMember - disconnect);
            Assert.Contains("unwireProducerHandlers(releasing);", body, StringComparison.Ordinal);
            Assert.Contains("UnwireStationHandlers(releasing);", body, StringComparison.Ordinal);
        }

        // ────────────────────────────────────────────────────────────────
        //  The fall cancels the station attempt (Track G's cancellation,
        //  carried into Track H's Connected handler at the merge)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Track G cancelled the station attempt in the property switch's
        /// <c>Connected</c> case, so a coordinator blocked in a wait ends
        /// Cancelled rather than running out its deadline against a dead link.
        /// Track H moved <c>Connected</c> out of that switch into
        /// <c>onRadioConnectedChanged</c>. Taking H's side dropped G's
        /// cancellation silently — no conflict, no build error. This goes red
        /// if the <c>CancelStationAttempt("connection dropped")</c> line is
        /// removed from onRadioConnectedChanged (the mutation control).
        /// </summary>
        [Fact]
        public void A_fall_of_our_own_connection_cancels_the_station_attempt()
        {
            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "ours");
            try
            {
                rig.theRadio = radio;
                rig.BeginStationAttempt(radio, "fall test");
                WireAsConnectDoes(rig, radio);
                MarkLive(radio, rig);

                // Control: there is a live attempt to cancel.
                var attempt = rig.StationAttempt;
                Assert.True(attempt.IsLive, "the attempt was not live before the fall; the cancellation below would be vacuous");

                LoseTheTransport(radio);

                AssertTheRigSawTheFall(rig);
                Assert.False(attempt.IsLive, "our own connection fell and the station attempt is still live");
                Assert.Contains("connection dropped", attempt.CancelReason, StringComparison.Ordinal);
            }
            finally
            {
                Release(rig);
            }
        }

        /// <summary>
        /// The other half of "on the path where a fall of THIS rig's connection
        /// is established": a stranded object's fall reaches the handler and is
        /// refused (H6), so it must not cancel the live attempt either. Goes red
        /// if the cancellation is placed ahead of the stranded-object check.
        /// </summary>
        [Fact]
        public void A_stranded_objects_fall_leaves_the_station_attempt_live()
        {
            var rig = NewRig();
            var ours = NewWanRadio(UniqueSerial(), "ours");
            var other = NewWanRadio(UniqueSerial(), "other");
            try
            {
                rig.theRadio = ours;
                rig.BeginStationAttempt(ours, "stranded test");
                WireAsConnectDoes(rig, ours);
                MarkLive(ours, rig);
                WireAsConnectDoes(rig, other);
                MarkLive(other);
                var attempt = rig.StationAttempt;
                Assert.True(attempt.IsLive);

                int ignoredBefore = rig.StrandedConnectionChangesIgnored;
                LoseTheTransport(other);

                // Control: the fall really reached the handler and was refused there.
                Assert.Equal(ignoredBefore + 1, rig.StrandedConnectionChangesIgnored);
                Assert.True(rig.IsConnected);
                Assert.True(attempt.IsLive, "a stranded object's fall cancelled the live rig's station attempt");
            }
            finally
            {
                Release(rig);
            }
        }

        /// <summary>
        /// Where the cancellation sits: in onRadioConnectedChanged, after the
        /// archive (CancelStationAttempt traces through the ordinary gate the
        /// fall's claim must not wait on) and before ConnectionStateChanged (its
        /// subscribers see the attempt already ended, as they did when the
        /// cancel sat in the switch). And the switch no longer has a Connected
        /// case of its own to cancel from.
        /// </summary>
        [Fact]
        public void The_cancellation_sits_after_the_archive_and_before_the_state_event()
        {
            string source = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.cs"));
            int connectedMethod = source.IndexOf("private void onRadioConnectedChanged(Radio r, CommandConnectionChanged report, ConnectionLifetime.Token token)", StringComparison.Ordinal);
            Assert.True(connectedMethod > 0, "the Connected handler moved");
            int methodEnd = source.IndexOf("private void radioPropertyChangedHandler(", connectedMethod, StringComparison.Ordinal);
            string body = source.Substring(connectedMethod, methodEnd - connectedMethod);

            int stranded = body.IndexOf("if (!ConnectionStateAppliesToRig(", StringComparison.Ordinal);
            int archive = body.IndexOf("if (!nowConnected) archiveIfOurConnectionDropped(r, token, fall);", StringComparison.Ordinal);
            int cancel = body.IndexOf("if (!nowConnected) CancelStationAttempt(\"connection dropped\");", StringComparison.Ordinal);
            int raise = body.IndexOf("ConnectionStateChanged?.Invoke(nowConnected);", StringComparison.Ordinal);
            Assert.True(stranded > 0 && archive > stranded && cancel > archive && raise > cancel,
                        "the stranded check, then the archive, then the cancellation, then ConnectionStateChanged");

            // The property switch has no Connected case; the only cancellation
            // on a fall is the one above.
            int handlerTop = source.IndexOf("private void radioPropertyChangedHandler(object sender, PropertyChangedEventArgs e, ObservationBinding binding)", StringComparison.Ordinal);
            Assert.True(handlerTop > 0);
            Assert.Equal(1, CountOf(source, "CancelStationAttempt(\"connection dropped\");"));
            Assert.DoesNotContain("case \"Connected\":", source.Substring(handlerTop), StringComparison.Ordinal);
        }

        private static int CountOf(string text, string needle)
        {
            int n = 0;
            for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal)) n++;
            return n;
        }

        // ────────────────────────────────────────────────────────────────
        //  Where the archive is, and where it must not be
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// One call site in the whole file, reached from the Connected case, and
        /// none in the removal handler. The structural half of "exactly once":
        /// the removal arm cannot ask for a second archive because it does not ask
        /// for one at all. The connection lifetime's claim covers repeats of the
        /// one call.
        /// </summary>
        [Fact]
        public void The_archive_is_taken_on_the_fall_and_nowhere_else()
        {
            string source = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "Radios", "FlexBase.cs"));

            int calls = 0;
            for (int at = source.IndexOf("CaptureArchive.AfterConnectionDrop(", StringComparison.Ordinal);
                 at >= 0;
                 at = source.IndexOf("CaptureArchive.AfterConnectionDrop(", at + 1, StringComparison.Ordinal))
                calls++;
            Assert.Equal(1, calls);

            int method = source.IndexOf("private void archiveIfOurConnectionDropped(Radio r, ConnectionLifetime.Token token, JJTrace.TraceSessionHandle fall)", StringComparison.Ordinal);
            Assert.True(method > 0, "the fall's archive method is gone");
            int archive = source.IndexOf("CaptureArchive.AfterConnectionDrop(", StringComparison.Ordinal);
            int methodEnd = source.IndexOf("private void wireProducerHandlers(", method, StringComparison.Ordinal);
            Assert.True(archive > method && archive < methodEnd, "the one archive call is not in archiveIfOurConnectionDropped");

            // Reached from the Connected property's handler, when it falls.
            // Track H6 moved that handler out of the switch and ahead of every
            // ordinary trace line (the fall must not wait on the trace gate
            // before its claim), so it is read from its own method now.
            int handlerTop = source.IndexOf("private void radioPropertyChangedHandler(object sender, PropertyChangedEventArgs e, ObservationBinding binding)", StringComparison.Ordinal);
            Assert.True(handlerTop > 0);
            string dispatch = source.Substring(handlerTop, source.IndexOf("switch (e.PropertyName)", handlerTop, StringComparison.Ordinal) - handlerTop);
            Assert.DoesNotContain("onRadioConnectedChanged(", dispatch, StringComparison.Ordinal);
            Assert.Contains("onRadioConnectedChanged(radio, report, binding?.Lifetime);", source, StringComparison.Ordinal);
            int connectedMethod = source.IndexOf("private void onRadioConnectedChanged(Radio r, CommandConnectionChanged report, ConnectionLifetime.Token token)", StringComparison.Ordinal);
            Assert.True(connectedMethod > 0, "the Connected handler moved");
            string connectedBody = source.Substring(connectedMethod,
                source.IndexOf("private void radioPropertyChangedHandler(", connectedMethod, StringComparison.Ordinal) - connectedMethod);
            Assert.Contains("if (!nowConnected) archiveIfOurConnectionDropped(r, token, fall);", connectedBody, StringComparison.Ordinal);

            // And the removal handler's drop arm is bookkeeping: no archive there.
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
        /// <b>A slow transition holds the trace gate, and the WHOLE of
        /// FlexLib's teardown returns anyway</b> — our handler, its claim, its
        /// queued archive, and then <c>Radio.Disconnect</c> to its end. Sol's
        /// review of H3, finding 1: the drop read
        /// <c>TraceCoordinator.CurrentHandle</c> through the same gate that is
        /// held across a transition's flush, close, move and successor open,
        /// and wrote an ordinary trace line first — so a disk stalled under a
        /// problem-report checkpoint stalled FlexLib's transport thread. H6
        /// freed the claim; Sol's review of H6 (finding 1, second paragraph)
        /// found that was not the whole thread: after our handler returned,
        /// FlexLib's own <c>Disconnect</c> raised further property changes on
        /// the same thread, each traced through the gate as an ordinary line.
        ///
        /// <para>Driven with a real session in a temporary tree, at capture
        /// detail, and a checkpoint held INSIDE the gate on a barrier. The
        /// barrier is proved to hold the gate first — a GATED READ is shown to
        /// block — so a fall that returns is a measurement, not a gate nobody
        /// was holding. (Until H7 the proof was an ordinary writer blocking;
        /// an ordinary writer no longer blocks on a transition, which is the
        /// point of H7, so the proof moved to a read.) What is measured is the
        /// fall thread's call into the transport RETURNING — which is after
        /// <c>Radio.Disconnect</c> has finished, not merely after our handler
        /// has. Then the lines the fall deferred are shown to land in the
        /// session they describe, the archived one.</para>
        ///
        /// <para>Positive controls, run by hand: with <c>CurrentHandle</c> put
        /// back behind the gate, the fall does not return (H6); with the
        /// router's transition check removed so an ordinary line waits on the
        /// gate again, the fall thread does not return within the bound and
        /// this test goes red (H7).</para>
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
            TraceArchiveTicket archivedTicket = null;
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

                    // The hook does what the application's does: archive the
                    // session the drop named, through the boundary.
                    CaptureArchive.ArchiveHook = req =>
                    {
                        Interlocked.Increment(ref _archives);
                        var lines = new List<TraceRecord>();
                        if (!string.IsNullOrEmpty(req.PartialMeterLine?.Text)) lines.Add(req.PartialMeterLine);
                        TraceTransitionResult archivedNow = TraceCoordinator.TryArchive(new TraceArchiveRequest
                        {
                            Expected = (TraceSessionHandle)req.ExpectedSession,
                            OperationId = req.DropOperationId,
                            Outcome = TraceSessionOutcome.ConnectionDropped,
                            OutcomeDetail = req.OutcomeDetail,
                            TerminalLines = lines,
                            Resume = TraceResumeIntent.None,
                        });
                        archivedTicket = archivedNow.Ticket;
                        return new CaptureArchiveResult { Refused = !archivedNow.Owned };
                    };
                    // Hold the archive worker back so its timing is ours.
                    CaptureArchive.Queue = work => { lock (queued) queued.Add(work); };

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

                    // The barrier really holds the gate: a gated READ cannot get
                    // in. (Not a writer — since H7 an ordinary writer defers
                    // rather than waits on a transition, so a writer returning
                    // would prove nothing about the gate.)
                    Assert.True(TraceCoordinator.TransitionInProgress, "the checkpoint is at its probe but no transition is flagged");
                    writer = new Thread(() => { _ = TraceCoordinator.Recording; }) { IsBackground = true };
                    writer.Start();
                    Assert.False(writer.Join(TimeSpan.FromMilliseconds(300)),
                        "a gated read got past the barrier — the gate is not held, and the measurement below would be vacuous");

                    // The fall, on its own thread, entering where a real one
                    // does. What is measured is that thread RETURNING from the
                    // transport's Disconnect — which is after FlexLib's own
                    // Radio.Disconnect has run to its end on that thread, with
                    // every property change it raises and every handler of
                    // ours those reach. Our Connected handler is only the first
                    // thing on that path.
                    var handlerDone = new ManualResetEventSlim(false);
                    rig.ConnectionStateChanged += connected => { if (!connected) handlerDone.Set(); };
                    fall = new Thread(() => LoseTheTransport(radio)) { IsBackground = true };
                    fall.Start();
                    Assert.True(handlerDone.Wait(TimeSpan.FromSeconds(5)),
                        "the Connected fall's handler did not return while a transition held the trace gate — FlexLib's transport thread is waiting on a file before the drop is claimed");
                    Assert.True(fall.Join(TimeSpan.FromSeconds(5)),
                        "FlexLib's Radio.Disconnect did not finish while a transition held the trace gate — something on the teardown path after our handler still waits on the gate");
                    Assert.True(checkpoint.IsAlive, "the gate was released before the teardown finished; nothing was measured");

                    // And it did its job before returning: the drop was claimed
                    // and its archive queued, not put off until the disk recovered.
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
                // then archives.
                Action work;
                lock (queued) work = queued[0];
                work();

                Assert.Equal(1, Volatile.Read(ref _archives));
                Assert.NotNull(archivedTicket);
                Assert.True(archivedTicket.Completion.Wait(TimeSpan.FromSeconds(60)), "the archive worker never finished");
                TraceArchiveCompletion done = archivedTicket.Completion.Result;
                Assert.True(done.ArchiveCommitted, "the archive was not committed: " + done.FailureStage);
                string text = File.ReadAllText(SessionArchive.ExtractTraceText(
                    done.ArchiveFullPath, Path.Combine(dir, "extract")));

                // The reader is looking at the archived part, the one that began
                // after the checkpoint...
                Assert.Contains("--- trace continues from part 001", text, StringComparison.Ordinal);
                // ...and the lines the fall wrote on FlexLib's thread are in it:
                // the session they describe, not a successor — bound to it at
                // the fall, and written by its own archive under the gate (H7).
                Assert.Contains("Connected:False", text, StringComparison.Ordinal);
                Assert.Contains("our connection dropped without us asking", text, StringComparison.Ordinal);
                // And none of them was refused: every deferred line found the
                // session it was bound to still current when the archive drained
                // it. (The harness radio has no slices or panadapters, so
                // FlexLib's Disconnect raises nothing further for our handlers
                // to trace here; the proof that the rest of the teardown does
                // not wait is the fall thread's Join above, and the router's
                // own test in TraceCoordinatorTests covers a line written
                // during a transition.)
                Assert.DoesNotContain("TraceDeferred: REFUSED", text, StringComparison.Ordinal);
            }
            finally
            {
                if (TraceCoordinator.Recording)
                {
                    TraceCoordinator.TryArchive(new TraceArchiveRequest
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
        //  One fall, one session (Track H8)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// <b>A Stop completes between the fall's first line and its later
        /// ones, and the fall keeps one identity.</b> Sol's review of H7 (the
        /// item for a harder reader): each deferred line read the published
        /// handle for itself, and the archive read it once more, so a Stop
        /// finishing in the gap bound the first line to the old session and
        /// the rest — and the archive — to the successor. The real callback is
        /// held on a probe after its FIRST deferred line, a Stop is run to
        /// completion on this thread, and the callback is released. Every
        /// later line and the archive request name the OLD session: the archive is
        /// refused NotCurrent, and the successor carries the fall's later
        /// lines only as refusal records, never bare.
        ///
        /// <para>Positive controls: the Stop really completed in the gap (the
        /// published handle is the successor before the release); the first
        /// line, queued before the Stop, is in the OLD session's archive; and
        /// a line bound to the successor by hand lands in it bare — so the
        /// instrument tells bound-to-old from bound-to-current.</para>
        /// </summary>
        [Fact]
        public void A_Stop_between_the_falls_first_line_and_its_archive_does_not_split_the_fall()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-h8-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string livePath = Path.Combine(dir, "JJFlexRadioTrace.txt");
            string savedRoot = TraceCoordinator.ArchiveRootDir;
            bool savedOn = Tracing.On;
            TraceLevel savedLevel = Tracing.TheSwitch.Level;

            var queued = new List<Action>();
            var atFirstLine = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            int probeHits = 0;
            TraceSessionHandle archiveExpected = null;
            TraceTransitionResult archiveResult = null;
            string partialSeen = null;
            Thread fall = null;

            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "Don's 6300");
            try
            {
                TraceTransitionResult stop = null;
                TraceSessionHandle old;
                try
                {
                    typeof(TraceCoordinator)
                        .GetMethod("RestoreSessionForTests", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, new object[] { null });
                    TraceCoordinator.ArchiveRootDir = Path.Combine(dir, "Traces");
                    TraceCoordinator.SetStandingIntent(true, TraceLevel.Verbose);
                    Tracing.TheSwitch.Level = TraceLevel.Verbose;
                    Tracing.On = true;
                    Tracing.ResetDeferredCountersForTests();
                    TraceTransitionResult began = TraceCoordinator.Begin(livePath, TraceLevel.Verbose, asDetailedCapture: false);
                    Assert.Equal(TraceTransition.Accepted, began.Status);
                    old = began.Successor;

                    CaptureArchive.ArchiveHook = req =>
                    {
                        Interlocked.Increment(ref _archives);
                        archiveExpected = (TraceSessionHandle)req.ExpectedSession;
                        partialSeen = req.PartialMeterLine?.Text;
                        archiveResult = TraceCoordinator.TryArchive(new TraceArchiveRequest
                        {
                            Expected = archiveExpected,
                            OperationId = req.DropOperationId,
                            Outcome = TraceSessionOutcome.ConnectionDropped,
                            OutcomeDetail = req.OutcomeDetail,
                            Resume = TraceResumeIntent.Standing,
                        });
                        return new CaptureArchiveResult
                        {
                            Refused = !archiveResult.Owned,
                            RefusalReason = archiveResult.Owned ? null : archiveResult.Explanation,
                        };
                    };
                    CaptureArchive.Queue = work => { lock (queued) queued.Add(work); };

                    rig.theRadio = radio;
                    WireAsConnectDoes(rig, radio);
                    MarkLive(radio, rig);

                    // Hold the real callback after its first deferred line.
                    Tracing.DeferredLineProbeForTests = text =>
                    {
                        if (!text.Contains("propertyChanged:Radio:Connected", StringComparison.Ordinal)) return;
                        if (Interlocked.Increment(ref probeHits) != 1) return;
                        atFirstLine.Set();
                        release.Wait(TimeSpan.FromSeconds(30));
                    };
                    fall = new Thread(() => LoseTheTransport(radio)) { IsBackground = true };
                    fall.Start();
                    Assert.True(atFirstLine.Wait(TimeSpan.FromSeconds(10)), "the fall never wrote its first line");

                    // The Stop, run to completion in the gap.
                    stop = TraceCoordinator.TryArchive(new TraceArchiveRequest
                    {
                        Expected = old, OperationId = Guid.NewGuid(),
                        Outcome = TraceSessionOutcome.CleanExit, Resume = TraceResumeIntent.Standing,
                    });
                    Assert.Equal(TraceTransition.Accepted, stop.Status);
                    Assert.NotNull(stop.Successor);
                    // Positive control: the world really moved under the fall.
                    Assert.Equal(stop.Successor.SessionId, TraceCoordinator.CurrentHandle.SessionId);
                }
                finally
                {
                    release.Set();
                    Tracing.DeferredLineProbeForTests = null;
                    fall?.Join(TimeSpan.FromSeconds(10));
                    typeof(Tracing).GetField("lastSlowMarkerStamp", BindingFlags.NonPublic | BindingFlags.Static)
                        ?.SetValue(null, 0L);
                }
                Assert.False(fall.IsAlive, "the fall thread did not return");
                AssertTheRigSawTheFall(rig);
                lock (queued) Assert.Single(queued);

                // The archive: it names the session the fall began under, and is
                // refused because that session is gone. Nothing archives the
                // successor as dropped — it was not recording when the
                // connection fell.
                Action work;
                lock (queued) work = queued[0];
                work();
                Assert.Equal(1, Volatile.Read(ref _archives));
                Assert.NotNull(archiveExpected);
                Assert.Equal(old.SessionId, archiveExpected.SessionId);
                Assert.Equal(TraceTransition.NotCurrent, archiveResult.Status);
                Assert.Equal(stop.Successor.SessionId, TraceCoordinator.CurrentHandle.SessionId);

                // The fall's later lines were bound to the OLD session and
                // refused into the successor as refusal records — never bare.
                Tracing.FlushDeferred();
                Assert.True(Tracing.DeferredLinesRefused >= 2,
                    "expected the fall's later lines to be refused as bound to the old session; refused=" + Tracing.DeferredLinesRefused);
                string successorText;
                using (var fs = new FileStream(livePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) successorText = sr.ReadToEnd();
                Assert.Contains("TraceDeferred: REFUSED", successorText, StringComparison.Ordinal);
                Assert.Contains("our connection dropped without us asking", successorText, StringComparison.Ordinal);
                // Sol's review of H8, blocker 4: the archive WORKER's own lines
                // — "archiving the running capture as connection_dropped"
                // before the hook, and the refusal after it — used to be
                // unbound and landed bare here, so this file claimed its own
                // capture was being drop-archived. Positive control first: the
                // pre-archive claim IS in this file (the instrument sees it);
                // then: every CaptureArchive line here is a refusal record.
                Assert.Contains("archiving the running capture as " + TraceSessionOutcome.ConnectionDropped,
                                successorText, StringComparison.Ordinal);
                Assert.Contains("CaptureArchive: the archive was refused", successorText, StringComparison.Ordinal);
                int captureArchiveLines = 0;
                foreach (string line in successorText.Split('\n'))
                {
                    if (line.Contains("CaptureArchive:", StringComparison.Ordinal)) captureArchiveLines++;
                    if (!line.Contains("Connected:False", StringComparison.Ordinal)
                        && !line.Contains("connection fell", StringComparison.Ordinal)
                        && !line.Contains("propertyChanged:Radio:Connected", StringComparison.Ordinal)
                        && !line.Contains("CaptureArchive:", StringComparison.Ordinal)) continue;
                    Assert.Contains("TraceDeferred: REFUSED", line, StringComparison.Ordinal);
                    Assert.Contains("while session " + old.SessionId + " was recording", line, StringComparison.Ordinal);
                }
                Assert.True(captureArchiveLines >= 2, "expected the pre-archive claim and the refusal; saw " + captureArchiveLines);
                // The meter window the drop collected is not discarded with
                // the refused request: it is in this file too, as a refusal
                // record naming the session it describes. The production
                // collector renders a line even with no meter data (see
                // CaptureMeterSet), so this is exercised, not skipped.
                Assert.False(string.IsNullOrEmpty(partialSeen), "the drop collected no meter window; the recovery below is not exercised");
                string partialLine = successorText.Split('\n').Single(l => l.Contains(partialSeen, StringComparison.Ordinal));
                Assert.Contains("TraceDeferred: REFUSED", partialLine, StringComparison.Ordinal);
                Assert.Contains("kept as evidence because its session had already been archived", partialLine, StringComparison.Ordinal);

                // The first line, queued before the Stop, went where it
                // belonged: the old session's archive — and no drop-archive
                // claim did, because the drop archive never happened.
                Assert.True(stop.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
                Assert.True(stop.Ticket.Completion.Result.ArchiveCommitted);
                string oldText = File.ReadAllText(SessionArchive.ExtractTraceText(
                    stop.Ticket.Completion.Result.ArchiveFullPath, Path.Combine(dir, "extract")));
                Assert.Contains("propertyChanged:Radio:Connected", oldText, StringComparison.Ordinal);
                Assert.DoesNotContain("TraceDeferred: REFUSED", oldText, StringComparison.Ordinal);
                Assert.DoesNotContain("CaptureArchive:", oldText, StringComparison.Ordinal);
                Assert.DoesNotContain(partialSeen, oldText, StringComparison.Ordinal);

                // Since H10 the window ALSO has a destination that needs no
                // successor: a late-evidence file beside the old session's
                // archive, which the successor's refusal record points at
                // (Sol's review of H9, blocker 3). Here the successor exists,
                // so both hold it; the test below is the case where only the
                // file does.
                string beside = SessionArchive.LateEvidencePathFor(stop.Ticket.Completion.Result.ArchiveFullPath);
                Assert.True(File.Exists(beside), "no late-evidence file beside the old session's archive");
                string besideText = File.ReadAllText(beside);
                Assert.Contains(partialSeen, besideText, StringComparison.Ordinal);
                Assert.Contains(old.SessionId.ToString(), besideText, StringComparison.Ordinal);
                Assert.Contains("also kept beside that session's archive at " + beside, partialLine, StringComparison.Ordinal);

                // Positive control for the instrument: a line bound to the
                // successor lands in it bare.
                Tracing.TraceLineDeferred("H8 control: bound to the successor", TraceLevel.Warning, stop.Successor);
                Tracing.FlushDeferred();
                using (var fs = new FileStream(livePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) successorText = sr.ReadToEnd();
                string control = successorText.Split('\n').Single(l => l.Contains("H8 control", StringComparison.Ordinal));
                Assert.DoesNotContain("REFUSED", control, StringComparison.Ordinal);
            }
            finally
            {
                if (TraceCoordinator.CurrentHandle != null)
                {
                    TraceCoordinator.TryArchive(new TraceArchiveRequest
                    {
                        ShutdownAuthority = true,
                        Outcome = TraceSessionOutcome.CleanExit,
                        Resume = TraceResumeIntent.None,
                        OperationId = Guid.NewGuid(),
                    });
                }
                TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(20));
                TraceCoordinator.ArchiveRootDir = savedRoot;
                TraceCoordinator.SetStandingIntent(true, TraceLevel.Info);
                Tracing.TheSwitch.Level = savedLevel;
                Tracing.On = savedOn;
                Tracing.ResetDeferredCountersForTests();
                Release(rig);
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// <b>Sol's review of H9, blocker 3.</b> The same Stop-in-the-gap
        /// race with the STANDING LOG OFF: the Stop opens no successor, so
        /// there is no sink for the drop's bound lines to be refused into,
        /// and the drain consumes them without writing. H9's refusal record
        /// was the partial meter window's only destination, and here it
        /// does not exist. Since H10 the window is kept in a late-evidence
        /// file beside the old session's archive, which needs no sink: after
        /// the worker runs, exactly one file on disk holds the window, it is
        /// that one, it names the old session, and the old session's own
        /// archived archive does not contain it.
        /// </summary>
        [Fact]
        public void A_Stop_with_no_standing_log_leaves_the_refused_drops_meter_window_beside_the_old_archive()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-h10-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string livePath = Path.Combine(dir, "JJFlexRadioTrace.txt");
            string savedRoot = TraceCoordinator.ArchiveRootDir;
            bool savedOn = Tracing.On;
            TraceLevel savedLevel = Tracing.TheSwitch.Level;

            var queued = new List<Action>();
            var atFirstLine = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            int probeHits = 0;
            TraceTransitionResult archiveResult = null;
            string partialSeen = null;
            Thread fall = null;

            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "Don's 6300");
            try
            {
                TraceTransitionResult stop = null;
                TraceSessionHandle old;
                try
                {
                    typeof(TraceCoordinator)
                        .GetMethod("RestoreSessionForTests", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, new object[] { null });
                    TraceCoordinator.ArchiveRootDir = Path.Combine(dir, "Traces");
                    // The drop's operation id is the connection token's
                    // ordinal; the fixture restarts the ordinals, so a ticket
                    // an earlier test archived under the same id would answer
                    // this archive with AlreadyClaimed. Both claim spaces, reset.
                    TraceCoordinator.ResetClaimsForTests();
                    // The one difference from the test above.
                    TraceCoordinator.SetStandingIntent(false, TraceLevel.Verbose);
                    Tracing.TheSwitch.Level = TraceLevel.Verbose;
                    Tracing.On = true;
                    Tracing.ResetDeferredCountersForTests();
                    TraceTransitionResult began = TraceCoordinator.Begin(livePath, TraceLevel.Verbose, asDetailedCapture: true);
                    Assert.Equal(TraceTransition.Accepted, began.Status);
                    old = began.Successor;

                    CaptureArchive.ArchiveHook = req =>
                    {
                        Interlocked.Increment(ref _archives);
                        partialSeen = req.PartialMeterLine?.Text;
                        archiveResult = TraceCoordinator.TryArchive(new TraceArchiveRequest
                        {
                            Expected = (TraceSessionHandle)req.ExpectedSession,
                            OperationId = req.DropOperationId,
                            Outcome = TraceSessionOutcome.ConnectionDropped,
                            OutcomeDetail = req.OutcomeDetail,
                            Resume = TraceResumeIntent.Standing,
                        });
                        return new CaptureArchiveResult
                        {
                            Refused = !archiveResult.Owned,
                            RefusalReason = archiveResult.Owned ? null : archiveResult.Explanation,
                        };
                    };
                    CaptureArchive.Queue = work => { lock (queued) queued.Add(work); };

                    rig.theRadio = radio;
                    WireAsConnectDoes(rig, radio);
                    MarkLive(radio, rig);

                    Tracing.DeferredLineProbeForTests = text =>
                    {
                        if (!text.Contains("propertyChanged:Radio:Connected", StringComparison.Ordinal)) return;
                        if (Interlocked.Increment(ref probeHits) != 1) return;
                        atFirstLine.Set();
                        release.Wait(TimeSpan.FromSeconds(30));
                    };
                    fall = new Thread(() => LoseTheTransport(radio)) { IsBackground = true };
                    fall.Start();
                    Assert.True(atFirstLine.Wait(TimeSpan.FromSeconds(10)), "the fall never wrote its first line");

                    // The Stop, in the gap: the capture ends and, with no
                    // standing log, NOTHING opens after it.
                    stop = TraceCoordinator.TryArchive(new TraceArchiveRequest
                    {
                        Expected = old, OperationId = Guid.NewGuid(), RequireCaptureRunning = true,
                        Outcome = TraceSessionOutcome.CleanExit, Resume = TraceResumeIntent.Standing,
                    });
                    Assert.Equal(TraceTransition.Accepted, stop.Status);
                    Assert.Null(stop.Successor);
                    Assert.False(stop.TracingOn);
                    Assert.Null(TraceCoordinator.CurrentHandle);
                }
                finally
                {
                    release.Set();
                    Tracing.DeferredLineProbeForTests = null;
                    fall?.Join(TimeSpan.FromSeconds(10));
                    typeof(Tracing).GetField("lastSlowMarkerStamp", BindingFlags.NonPublic | BindingFlags.Static)
                        ?.SetValue(null, 0L);
                }
                Assert.False(fall.IsAlive, "the fall thread did not return");
                AssertTheRigSawTheFall(rig);
                lock (queued) Assert.Single(queued);

                Action work;
                lock (queued) work = queued[0];
                Tracing.FlushDeferred();
                Assert.True(stop.Ticket.Completion.Wait(TimeSpan.FromSeconds(60)));
                Assert.True(stop.Ticket.Completion.Result.ArchiveCommitted);
                // Positive control for the instrument below: before the worker
                // runs, no file in the directory holds a partial window — the
                // window exists only in the request.
                Assert.DoesNotContain(Directory.GetFiles(dir, "*", SearchOption.AllDirectories),
                    f => !f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                         && File.ReadAllText(f).Contains("partial=connection_dropped", StringComparison.Ordinal));

                // The worker: refused (nothing is recording, so nothing to
                // archive), and the window kept beside the old archive.
                work();
                Assert.Equal(1, Volatile.Read(ref _archives));
                Assert.NotNull(archiveResult);
                Assert.False(archiveResult.Owned, "the drop's archive was not refused: " + archiveResult.Status + " — " + archiveResult.Explanation);
                Assert.Equal(TraceTransition.NoSession, archiveResult.Status);
                Assert.False(string.IsNullOrEmpty(partialSeen), "the drop collected no meter window; nothing below is exercised");
                Tracing.FlushDeferred();
                Assert.Null(TraceCoordinator.CurrentHandle);
                Assert.False(File.Exists(livePath), "a live file exists, so a successor was opened after all");

                string beside = SessionArchive.LateEvidencePathFor(stop.Ticket.Completion.Result.ArchiveFullPath);
                Assert.True(File.Exists(beside), "the window was kept nowhere: no late-evidence file beside the old archive");
                string besideText = File.ReadAllText(beside);
                Assert.Contains(partialSeen, besideText, StringComparison.Ordinal);
                Assert.Contains("Late evidence for trace session " + old.SessionId, besideText, StringComparison.Ordinal);
                Assert.Contains("kept as evidence because its session had already been archived", besideText, StringComparison.Ordinal);

                // Exactly one file on disk holds the window, and it is that
                // one — not the archived archive, not a live trace.
                string extracted = SessionArchive.ExtractTraceText(
                    stop.Ticket.Completion.Result.ArchiveFullPath, Path.Combine(dir, "extract"));
                Assert.DoesNotContain(partialSeen, File.ReadAllText(extracted), StringComparison.Ordinal);
                var holders = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                && File.ReadAllText(f).Contains(partialSeen, StringComparison.Ordinal))
                    .ToList();
                Assert.Equal(new[] { beside }, holders);
            }
            finally
            {
                if (TraceCoordinator.CurrentHandle != null)
                {
                    TraceCoordinator.TryArchive(new TraceArchiveRequest
                    {
                        ShutdownAuthority = true,
                        Outcome = TraceSessionOutcome.CleanExit,
                        Resume = TraceResumeIntent.None,
                        OperationId = Guid.NewGuid(),
                    });
                }
                TraceCoordinator.DrainArchives(TimeSpan.FromSeconds(20));
                TraceCoordinator.ArchiveRootDir = savedRoot;
                TraceCoordinator.SetStandingIntent(true, TraceLevel.Info);
                Tracing.TheSwitch.Level = savedLevel;
                Tracing.On = savedOn;
                Tracing.ResetDeferredCountersForTests();
                Release(rig);
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  What this track does NOT cover, pinned so it cannot be forgotten
        // ────────────────────────────────────────────────────────────────

        /// <summary>A reused Radio's second producer still reports its loss and archives once.</summary>
        [Fact]
        public void A_reused_Radio_reports_and_archives_its_second_loss()
        {
            var rig = NewRig();
            var radio = NewWanRadio(UniqueSerial(), "A");
            try
            {
                rig.theRadio = radio;
                WireAsConnectDoes(rig, radio);
                MarkLive(radio, rig);

                LoseTheTransport(radio);
                Assert.Equal(1, Volatile.Read(ref _archives));   // the first loss is covered
                AssertTheRigSawTheFall(rig);

                // The object comes back into use and its link dies again.
                MarkLive(radio, rig);
                LoseTheTransport(radio);

                Assert.False(radio.Connected);
                Assert.False(rig.IsConnected);
                Assert.Equal(2, Volatile.Read(ref _archives));
            }
            finally
            {
                Release(rig);
            }

            string vendor = File.ReadAllText(Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "FlexLib_API", "FlexLib", "Radio.cs"));
            Assert.Contains("_commandCommunication.ConnectionChanged += _commandCommunication_ConnectionChanged;", vendor);
            Assert.DoesNotContain("_commandCommunication.ConnectionChanged -=", vendor);
        }
    }
}
