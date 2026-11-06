using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios.StationConnect;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Junk box repair round 5 (#596; Codex's fourth re-check, B7 and B8). The
    /// round-four admissions were asked BEFORE the write: a send's release
    /// checked its admission and then called the ordinary setter, whose queued
    /// OFF ran later with no admission (B7); the VOX owner checked its pin and
    /// then the vendor wrote through whatever connection was current when it
    /// actually wrote (B8). Round five's rule: the check travels with the
    /// write. Every schedule here changes the world INSIDE an interval between
    /// a check and its write: a parked command queue, or the vendor's own
    /// pre-write boundary (FlexLib raises <c>TelnetTraceOutput</c> just before
    /// its transport write) with a reconnect on another thread, as a preempted
    /// thread would see it.
    /// The real <see cref="FlexBase"/> on the vendored Radio and its fake
    /// transport; what went out is read off the transport, with the
    /// connection it went out on. No socket, no radio, nothing keys.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RepairFiveTransmitTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const uint Foreign = 0x0BADF00D;
        private const string Serial = "0596-0861-0005-0001";

        private readonly RadioConfigStaticsScope _scope = new(nameof(RepairFiveTransmitTests));
        private readonly RigOnVendorRadio _f;
        private readonly Thread _loop;
        private volatile bool _stop;
        private volatile bool _hold;

        /// <summary>Every write the transport took: the command, and the connection current, and its state, at that write.</summary>
        private readonly List<(CommandConnection Connection, CommandConnectionState State, string Command)> _wire = new();

        public RepairFiveTransmitTests()
        {
            _f = new RigOnVendorRadio(Serial);
            _f.Vendor.Radio.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Mox")
                    typeof(FlexBase).GetMethod("noteRadioMox", Private)!.Invoke(_f.Rig, new object[] { _f.Vendor.Radio.Mox });
            };
            _f.Vendor.Transport.CommandWritten = c =>
            {
                var connection = _f.Vendor.Transport.CurrentConnection!;
                lock (_wire) _wire.Add((connection, connection.State, c));
            };
            // The vendor raises "a new command connection begins" from the
            // transport's BeginConnection, unconditionally (Radio's
            // _commandCommunication_ConnectionChanged, the Connecting arm), and
            // FlexBase hears it through wireProducerHandlers. The fixture plants
            // its transport after the Radio subscribed to its own, so that one
            // report is forwarded here to the production handler, as the vendor
            // forwards it. Later states are not forwarded: they would start the
            // whole connect path, which this test does not need.
            var handler = typeof(FlexBase).GetMethod("onCommandConnectionChanged", Private)!;
            _f.Vendor.Transport.ConnectionChanged += report =>
            {
                if (report.State == CommandConnectionState.Connecting)
                    handler.Invoke(_f.Rig, new object[] { _f.Vendor.Radio, report });
            };
            _f.Rig.q.MainLoop = true;
            _loop = new Thread(() =>
            {
                while (!_stop)
                {
                    if (!_hold && _f.Rig.q.Count > 0)
                    {
                        try { _f.Rig.RunQueuedItem(_f.Rig.q.Dequeue()); }
                        catch { /* the main loop traces and goes on */ }
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
            _scope.Dispose();
        }

        private static void Set(object o, string field, object? value) =>
            o.GetType().GetField(field, Private)!.SetValue(o, value);

        private static object? Get(object o, string field) => o.GetType().GetField(field, Private)!.GetValue(o);

        private void Interlock(string state, uint client = RigOnVendorRadio.OurHandle) =>
            _f.Vendor.Status($"interlock state={state} source=SW tx_client_handle=0x{client:X8}");

        private string[] Commands(string prefix) =>
            _f.Vendor.Transport.Commands.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();

        /// <summary>The commands that went out on a CONNECTED connection other than <paramref name="original"/>: what a real transport would have delivered to the replacement.</summary>
        private string[] OnAnotherLiveConnection(CommandConnection original, string prefix)
        {
            lock (_wire)
                return _wire.Where(w => !ReferenceEquals(w.Connection, original) && w.State == CommandConnectionState.Connected
                        && w.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(w => w.Command).ToArray();
        }

        private string[] OnConnection(CommandConnection connection, string prefix)
        {
            lock (_wire)
                return _wire.Where(w => ReferenceEquals(w.Connection, connection) && w.State == CommandConnectionState.Connected
                        && w.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(w => w.Command).ToArray();
        }

        private static void WaitFor(Func<bool> condition, string what, int ms = 5000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < until, "timed out waiting for " + what);
                Thread.Sleep(2);
            }
        }

        /// <summary>Everything queued so far has RUN: a marker behind it on the one loop has.</summary>
        private void Drained()
        {
            using var reached = new ManualResetEventSlim();
            _f.Rig.q.Enqueue((FlexBase.FunctionDel)(() => reached.Set()), "test marker");
            Assert.True(reached.Wait(5000), "the queue never reached the marker");
        }

        private void OurCarrierUp()
        {
            Interlock("TRANSMITTING");
            Set(_f.Vendor.Radio, "_mox", true);
            Set(_f.Rig, "_Transmit", true);
        }

        private void ForeignCarrierUp()
        {
            Interlock("TRANSMITTING", Foreign);
            Set(_f.Vendor.Radio, "_mox", true);
            Set(_f.Rig, "_Transmit", true);
        }

        /// <summary>The link falls and the same Radio connects again (a new command connection), as a drop and a retry make it.</summary>
        private void FallAndReconnect()
        {
            _f.Rig.RevokeAdmittedTransmitWork("the connection fell (test)");
            _f.Vendor.Transport.Reconnect();
        }

        /// <summary>
        /// At the vendor's pre-write boundary for <paramref name="command"/>:
        /// on ANOTHER thread, the link falls and the same Radio reconnects, then
        /// the replacement reports <paramref name="afterReconnect"/>. The
        /// writing thread waits up to <paramref name="holdMs"/> for that thread
        /// before it goes on to its transport write, so a replacement that is
        /// not serialised with the write completes first, exactly as a
        /// preempted writer would see it. Returns whether the reconnect had
        /// finished before the write went on.
        /// </summary>
        private Func<bool> ReconnectAtTheWriteOf(string command, Action afterReconnect, int holdMs = 400)
        {
            bool armed = true;
            bool finishedFirst = false;
            _f.Vendor.Radio.AddTelnetTraceTopic(command.Split(' ')[0]);
            ((System.ComponentModel.INotifyPropertyChanged)_f.Vendor.Radio).PropertyChanged += (_, e) =>
            {
                if (!armed || e.PropertyName != nameof(Radio.TelnetTraceOutput)) return;
                string? last;
                var output = _f.Vendor.Radio.TelnetTraceOutput;
                lock (output) last = output.LastOrDefault();
                if (last != command) return;
                armed = false;
                // The replacement's status lines are parsed under a vendor
                // trace lock the writer holds through this notification, so
                // what is waited for is the reconnect itself, not them.
                var connected = new ManualResetEventSlim();
                var reconnect = new Thread(() =>
                {
                    _f.Vendor.Transport.Fall();
                    _f.Vendor.Transport.Reconnect();
                    connected.Set();
                    afterReconnect();
                }) { IsBackground = true };
                reconnect.Start();
                finishedFirst = connected.Wait(holdMs);
            };
            return () => finishedFirst;
        }

        // ── B7: a send's own release carries its admission and ownership to the write ──

        [Fact]
        public void A_sends_release_already_queued_does_not_unkey_a_foreign_carrier_on_the_returned_connection()
        {
            var send = new FlexBaseVoiceMessageRig(_f.Rig);         // admitted on the first connection
            OurCarrierUp();
            _hold = true;
            send.Transmit = false;                                  // its OFF is queued while that connection stands
            FallAndReconnect();
            ForeignCarrierUp();                                     // another client's carrier on the returned link
            _hold = false;
            Drained();
            Assert.Empty(Commands("xmit"));
        }

        [Fact]
        public void A_box_sends_release_already_queued_does_not_unkey_a_foreign_carrier_on_the_returned_connection()
        {
            using var box = BoxSendOwnedRig.Own(_f.Rig, new FlexBaseVoiceMessageRig(_f.Rig), "repair five test");
            OurCarrierUp();
            _hold = true;
            box.Transmit = false;
            FallAndReconnect();
            ForeignCarrierUp();
            _hold = false;
            Drained();
            Assert.Empty(Commands("xmit"));
        }

        [Fact]
        public void A_sends_release_already_queued_is_refused_on_the_returned_connection_by_identity_alone()
        {
            // The carrier on the returned link is not yet named to any client
            // (tx_client_handle 0), so the ownership question would let an
            // unkey through: only the admission's identity, checked with the
            // write, refuses it. The two tests above have both layers.
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurCarrierUp();
            _hold = true;
            send.Transmit = false;
            FallAndReconnect();
            Interlock("TRANSMITTING", 0);
            Set(_f.Vendor.Radio, "_mox", true);
            _hold = false;
            Drained();
            Assert.Empty(Commands("xmit"));
        }

        [Fact]
        public void A_sends_release_on_its_own_live_connection_leaves_another_clients_carrier_alone()
        {
            // The carrier passed to another client on the SAME connection (#686).
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurCarrierUp();
            ForeignCarrierUp();
            send.Transmit = false;
            Drained();
            Assert.Empty(Commands("xmit"));

            using var box = BoxSendOwnedRig.Own(_f.Rig, new FlexBaseVoiceMessageRig(_f.Rig), "repair five test");
            box.Transmit = false;
            Drained();
            Assert.Empty(Commands("xmit"));
        }

        [Fact]
        public void A_sends_own_release_queued_and_then_run_still_unkeys_its_own_carrier()
        {
            // The control for the three above: the same adapters, the queue
            // parked and released, nothing changed, unkey their own carrier.
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurCarrierUp();
            _hold = true;
            send.Transmit = false;
            _hold = false;
            Drained();
            Assert.Single(Commands("xmit 0"));

            using var box = BoxSendOwnedRig.Own(_f.Rig, new FlexBaseVoiceMessageRig(_f.Rig), "repair five control");
            OurCarrierUp();
            _hold = true;
            box.Transmit = false;
            _hold = false;
            Drained();
            Assert.Equal(2, Commands("xmit 0").Length);
        }

        [Fact]
        public void A_sends_release_past_its_check_does_not_reach_the_replacement_connection()
        {
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurCarrierUp();
            var original = _f.Vendor.Radio.CurrentCommandConnection!;
            var reconnectedFirst = ReconnectAtTheWriteOf("xmit 0", ForeignCarrierUp);
            send.Transmit = false;
            Drained();
            WaitFor(() => !ReferenceEquals(_f.Vendor.Radio.CurrentCommandConnection, original)
                && _f.Vendor.Radio.CurrentCommandConnection!.State == CommandConnectionState.Connected, "the reconnect");
            Assert.False(reconnectedFirst(), "the replacement connection was completed while a write checked on the old one was still on its way");
            Assert.Empty(OnAnotherLiveConnection(original, "xmit"));
        }

        [Fact]
        public void A_key_admitted_with_no_radio_never_keys_a_radio_attached_later()
        {
            var radio = _f.Vendor.Radio;
            Set(_f.Rig, "theRadio", null);
            var send = new FlexBaseVoiceMessageRig(_f.Rig);         // no radio at the press
            _hold = true;
            send.Transmit = true;
            Set(_f.Rig, "theRadio", radio);                         // a radio attached before the queue runs
            _hold = false;
            Drained();
            Assert.Empty(Commands("xmit"));
        }

        [Fact]
        public void A_send_admitted_on_the_new_connection_still_keys_it()
        {
            // The control for the old-admission refusals: fresh work after a
            // reconnect is not blanket-refused.
            FallAndReconnect();
            Interlock("READY");
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            send.Transmit = true;
            Drained();
            Assert.Single(Commands("xmit 1"));
        }

        [Fact]
        public void A_tune_on_queued_before_a_reconnect_does_not_key_the_new_connection()
        {
            // What else answers the same wrong way: the tune carrier's queued
            // ON checked a generation and nothing else, so a reconnect after
            // the fall's revocation let it key the replacement.
            Interlock("READY");
            _f.Vendor.Transport.Fall();
            _f.Rig.RevokeAdmittedTransmitWork("the connection fell (test)");
            _hold = true;
            _f.Rig.TxTune = true;                                    // asked after the fall, before the retry
            _f.Vendor.Transport.Reconnect();
            _hold = false;
            Drained();
            Assert.Empty(Commands("transmit tune"));
        }

        [Fact]
        public void Every_send_adapter_carries_its_admission_to_the_write_and_the_kill_does_not()
        {
            // The slice send's adapter lives in JJFlexWpf, out of this
            // fixture's reach, so the three adapters are pinned at their
            // source: each send's key and release go through TransmitAdmitted,
            // and none calls the ordinary setter. The emergency kill keeps its
            // unconditional DropCarrier (Codex: do not make every unkey
            // conditional).
            string voice = System.IO.File.ReadAllText(Repo("Radios/VoiceMessageSend.cs"));
            string box = System.IO.File.ReadAllText(Repo("Radios/BoxSendOwnedRig.cs"));
            string slice = System.IO.File.ReadAllText(Repo("JJFlexWpf/VoiceMessageSender.cs"));
            string kill = System.IO.File.ReadAllText(Repo("Radios/TransmitKillSwitch.cs"));
            Assert.Contains("_rig.TransmitAdmitted(_admission, value, value ? \"a send's key\" : \"a send's unkey\")", voice);
            Assert.Contains("_rig.TransmitAdmitted(_admission, value, value ? \"a slice send's key\" : \"a slice send's unkey\")", slice);
            Assert.Contains("else _rig.TransmitAdmitted(_admission, false, \"a box send's unkey\");", box);
            Assert.Contains("TransmitKillSwitch.RaiseCarrier(_rig, TransmitKillSwitch.Carrier.Mox, _admission)", box);
            Assert.DoesNotContain("_rig.Transmit = value", voice + slice + box);
            Assert.DoesNotContain("DropCarrier", box.Replace("<see cref=\"TransmitKillSwitch.DropCarrier\"/>", ""));
            int request = kill.IndexOf("public static void Request(Source source", StringComparison.Ordinal);
            Assert.True(request > 0 && kill.IndexOf("DropCarrier(rig, Carrier.Mox);", request, StringComparison.Ordinal) > request,
                "the emergency kill no longer drops the carrier unconditionally");
        }

        // ── #861 B1 at the vendor's pre-write boundary: the mic put-back ──

        [Fact]
        public void A_mic_put_back_past_its_check_does_not_reach_the_replacement_connection()
        {
            Interlock("READY", 0);
            _f.Vendor.Status("transmit mic_selection=PC");
            var swapType = typeof(FlexBase).GetNestedType("MicInputSwap", BindingFlags.NonPublic)!;
            var putBackNow = typeof(FlexBase).GetMethod("PutBackNow", Private)!;
            object swap = Activator.CreateInstance(swapType, BindingFlags.Public | BindingFlags.Instance, null,
                new object?[] { "MIC", _f.Vendor.Radio, 0L, false }, null)!;    // saved on this connection
            var original = _f.Vendor.Radio.CurrentCommandConnection!;
            var reconnectedFirst = ReconnectAtTheWriteOf("mic input MIC", () => Interlock("READY", 0));

            putBackNow.Invoke(_f.Rig, new object[] { _f.Vendor.Radio, swap, "repair five test", 0L });
            WaitFor(() => !ReferenceEquals(_f.Vendor.Radio.CurrentCommandConnection, original)
                && _f.Vendor.Radio.CurrentCommandConnection!.State == CommandConnectionState.Connected, "the reconnect");
            Assert.False(reconnectedFirst(), "the replacement connection was completed while the mic put-back checked on the old one was still on its way");
            Assert.Empty(OnAnotherLiveConnection(original, "mic input"));
        }

        [Fact]
        public void A_pinned_write_stuck_after_its_check_delays_a_new_connection_by_the_bound_and_no_more()
        {
            // The interval the bound exists for: a write still counted (its
            // own handlers can be slow, past the vendor write) must not hold a
            // reconnect for ever, nor let it go at once.
            var radio = _f.Vendor.Radio;
            var connection = radio.CurrentCommandConnection!;
            using var inside = new ManualResetEventSlim();
            var writer = new Thread(() => _f.Rig.WriteOnPinnedTarget(radio, connection, null, () =>
            {
                inside.Set();
                Thread.Sleep(2500);
            })) { IsBackground = true };
            writer.Start();
            Assert.True(inside.Wait(2000));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _f.Vendor.Transport.Reconnect();
            long waited = clock.ElapsedMilliseconds;
            Assert.InRange(waited, (long)FlexBase.PinnedWriteDrainBound.TotalMilliseconds - 50, 2400);
            Assert.True(writer.Join(5000));
        }

        [Fact]
        public void A_retry_resets_the_mic_session_before_it_connects_again()
        {
            // Codex's #861 B1: RetryConnect reused the Radio without the reset
            // Connect makes. Pinned at the source; the lifetime check behind it
            // is exercised in PcMicHealerOwnershipTests.
            string text = System.IO.File.ReadAllText(Repo("Radios/FlexBase.cs"));
            int retry = text.IndexOf("public bool RetryConnect()", StringComparison.Ordinal);
            Assert.True(retry > 0, "RetryConnect moved");
            int reset = text.IndexOf("ResetPcMicSession(\"RetryConnect to \"", retry, StringComparison.Ordinal);
            int connect = text.IndexOf("rv = theRadio.Connect(prepareClientIdentityForConnect(\"RetryConnect\"));", retry, StringComparison.Ordinal);
            Assert.True(reset > retry && connect > reset, "a retry can connect again with the old connection's mic debt still held");
        }

        private static string Repo(string relative)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.Path.Combine(dir!.FullName, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        // ── B8: an old VOX release cannot write onto a replacement connection ──

        private VoxPauseOwner.Lease WithheldVoxRestoreAdoptedOnTheSameConnection()
        {
            _f.AddOwnSlice(0, 14.25, "USB");
            Set(_f.Rig, "_TXVFO", 0);
            Set(_f.Rig, "_IsConnected", true);
            _f.Vendor.Status("transmit vox_enable=1");
            Interlock("READY");
            var first = _f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
            WaitFor(() => Commands("transmit set vox_enable=0").Length == 1, "VOX off");
            Interlock("TRANSMITTING", Foreign);
            Assert.False(first.Release(immediate: true));            // withheld under the carrier, kept owed
            Interlock("READY", 0);
            var lease = _f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;   // adopts the debt, same connection
            Assert.True(lease.Paused);
            return lease;
        }

        [Fact]
        public void An_old_VOX_release_past_its_last_check_does_not_enable_VOX_on_the_replacement_connection()
        {
            var lease = WithheldVoxRestoreAdoptedOnTheSameConnection();
            var original = _f.Vendor.Radio.CurrentCommandConnection!;
            var reconnectedFirst = ReconnectAtTheWriteOf("transmit set vox_enable=1", () =>
            {
                Interlock("READY", 0);
                _f.Vendor.Status("transmit vox_enable=0");
            });

            lease.Release(immediate: true);
            WaitFor(() => !ReferenceEquals(_f.Vendor.Radio.CurrentCommandConnection, original)
                && _f.Vendor.Radio.CurrentCommandConnection!.State == CommandConnectionState.Connected, "the reconnect");
            Assert.False(reconnectedFirst(), "the replacement connection was completed while the VOX write checked on the old one was still on its way");
            Assert.Empty(OnAnotherLiveConnection(original, "transmit set vox_enable=1"));
        }

        [Fact]
        public void The_same_VOX_release_with_nothing_changed_restores_VOX_on_its_own_connection()
        {
            // The control at the same boundary: the restore is owed, the radio
            // receives, nobody reconnects, so it is written, once, on its own
            // connection.
            var lease = WithheldVoxRestoreAdoptedOnTheSameConnection();
            var original = _f.Vendor.Radio.CurrentCommandConnection!;
            Assert.True(lease.Release(immediate: true));
            Assert.Single(OnConnection(original, "transmit set vox_enable=1"));
        }
    }
}
