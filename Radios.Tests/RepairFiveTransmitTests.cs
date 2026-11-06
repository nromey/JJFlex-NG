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
        /// writing thread waits up to <paramref name="holdMs"/> (past round
        /// 5's one-second bound, by default) for that thread before it goes on
        /// to its transport write, exactly as a preempted writer would see it.
        /// Returns whether the reconnect had finished before the write went on.
        /// </summary>
        private Func<bool> ReconnectAtTheWriteOf(string command, Action afterReconnect, int holdMs = 1700)
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
            // Round 6 (Codex's B9/N1): the writer is held PAST the old one-second
            // bound, and the replacement completes first. The transport gate refuses.
            Assert.True(reconnectedFirst(), "the replacement never completed while the old write waited; the schedule would prove nothing");
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
            // Round 6 (Codex's B9/N1): the writer is held PAST the old one-second
            // bound, and the replacement completes first. The transport gate refuses.
            Assert.True(reconnectedFirst(), "the replacement never completed while the old write waited; the schedule would prove nothing");
            Assert.Empty(OnAnotherLiveConnection(original, "mic input"));
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

        // ── Round 6: the write-time gate in the transport (MIGRATION.md item 21) ──

        /// <summary>
        /// At the vendor's pre-write boundary for <paramref name="command"/>, on
        /// the writing thread: the radio reports <paramref name="report"/>
        /// through the vendor's parser and the transport, AFTER every check of
        /// ours has passed and BEFORE the transport write (Codex's B10 and N3).
        /// </summary>
        private void ReportAtTheWriteOf(string command, Action report)
        {
            bool armed = true;
            _f.Vendor.Radio.AddTelnetTraceTopic(command.Split(' ')[0]);
            ((System.ComponentModel.INotifyPropertyChanged)_f.Vendor.Radio).PropertyChanged += (_, e) =>
            {
                if (!armed || e.PropertyName != nameof(Radio.TelnetTraceOutput)) return;
                string? last;
                var output = _f.Vendor.Radio.TelnetTraceOutput;
                lock (output) last = output.LastOrDefault();
                if (last != command) return;
                armed = false;
                report();
            };
        }

        [Fact]
        public void A_sends_release_whose_carrier_passes_to_another_client_after_its_check_is_not_written()
        {
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurCarrierUp();
            ReportAtTheWriteOf("xmit 0", () => Interlock("TRANSMITTING", Foreign));
            send.Transmit = false;
            Drained();
            Assert.Empty(Commands("xmit"));
        }

        [Fact]
        public void The_same_release_with_its_own_carrier_reported_at_the_write_is_written()
        {
            // The control: the same boundary, a report that leaves the carrier ours.
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurCarrierUp();
            ReportAtTheWriteOf("xmit 0", () => Interlock("TRANSMITTING"));
            send.Transmit = false;
            Drained();
            Assert.Single(Commands("xmit 0"));
        }

        [Fact]
        public void The_emergency_kill_unkeys_whoever_holds_the_carrier()
        {
            // The kill passes no predicate that could refuse it: with another
            // client's carrier reported, DropCarrier still sends its xmit 0.
            ForeignCarrierUp();
            TransmitKillSwitch.DropCarrier(_f.Rig, TransmitKillSwitch.Carrier.Mox);
            Drained();
            Assert.Single(Commands("xmit 0"));
        }

        private void PutBackMicWith(Action atTheWrite)
        {
            Interlock("READY", 0);
            _f.Vendor.Status("transmit mic_selection=PC");
            var swapType = typeof(FlexBase).GetNestedType("MicInputSwap", BindingFlags.NonPublic)!;
            object swap = Activator.CreateInstance(swapType, BindingFlags.Public | BindingFlags.Instance, null,
                new object?[] { "MIC", _f.Vendor.Radio, 0L, false }, null)!;
            ReportAtTheWriteOf("mic input MIC", atTheWrite);
            typeof(FlexBase).GetMethod("PutBackNow", Private)!.Invoke(_f.Rig, new object[] { _f.Vendor.Radio, swap, "repair six test", 0L });
        }

        private object? SavedMic() => typeof(FlexBase).GetField("_micInputSwap", Private)!.GetValue(_f.Rig);

        [Fact]
        public void A_mic_put_back_meeting_a_reported_carrier_at_its_write_is_not_written_and_stays_owed()
        {
            PutBackMicWith(() => Interlock("TRANSMITTING", Foreign));
            Assert.Empty(Commands("mic input"));
            Assert.NotNull(SavedMic());                            // still owed on this connection
            Assert.Equal("PC", _f.Vendor.Radio.MicInput);          // the unsent value is not left in the cache
        }

        [Fact]
        public void A_mic_put_back_meeting_a_newer_choice_at_its_write_is_not_written_and_the_choice_stands()
        {
            PutBackMicWith(() => _f.Vendor.Status("transmit mic_selection=BAL"));
            Assert.Empty(Commands("mic input"));
            Assert.Null(SavedMic());
            Assert.Equal("BAL", _f.Vendor.Radio.MicInput);
        }

        [Fact]
        public void The_same_mic_put_back_with_nothing_reported_at_its_write_is_written()
        {
            PutBackMicWith(() => { });
            Assert.Equal(new[] { "mic input MIC" }, Commands("mic input"));
            Assert.Null(SavedMic());
        }

        [Fact]
        public void A_mic_put_back_meeting_a_repeated_PC_report_at_its_write_is_still_written()
        {
            // Codex's #861 B3 (repair round 7): a PC report is the radio still
            // holding the PC this app left, not a newer choice.
            PutBackMicWith(() => _f.Vendor.Status("transmit mic_selection=PC"));
            Assert.Equal(new[] { "mic input MIC" }, Commands("mic input"));
            Assert.Null(SavedMic());
        }

        [Fact]
        public void A_mic_put_back_whose_radio_reported_the_saved_input_before_a_carrier_leaves_the_cache_on_the_radios_word()
        {
            // Codex's T1: the radio really reported MIC, then a carrier refused
            // the write. The cache is the radio's word, MIC, and nothing is owed.
            PutBackMicWith(() =>
            {
                _f.Vendor.Status("transmit mic_selection=MIC");
                Interlock("TRANSMITTING", Foreign);
            });
            Assert.Empty(Commands("mic input"));
            Assert.Equal("MIC", _f.Vendor.Radio.MicInput);
            Assert.Null(SavedMic());
        }

        [Fact]
        public void A_mic_put_back_under_a_busy_report_reader_is_not_written_and_stays_owed()
        {
            // A restore fails closed when its answer cannot be had in time, and
            // keeps its debt; the cache goes back to the PC the radio holds.
            Thread? holder = null;
            PutBackMicWith(() => holder = HoldDispatchFor(350));
            Assert.True(holder!.Join(2000));
            Assert.Empty(Commands("mic input"));
            Assert.NotNull(SavedMic());
        }

        private static AudioChainPreset SnapshotWithoutEq() => new AudioChainPreset
        {
            SchemaVersion = 2,
            MicGain = 77,
            MicBoost = true,
            TxFilterLow = 150,
            TxFilterHigh = 2850,
            TxEqCaptured = false,
        };

        private bool RestoreLiveChain()
        {
            bool result = false;
            var t = new Thread(() => result = (bool)typeof(FlexBase).GetMethod("RestoreLiveTransmitAudioNow", Private)!.Invoke(_f.Rig, null)!)
            { IsBackground = true };
            t.Start();
            Assert.True(t.Join(10000), "the restore never returned");
            return result;
        }

        [Fact]
        public void A_live_chain_restore_write_past_its_check_does_not_reach_the_replacement_connection()
        {
            // Codex's #861 N2: the restore family never entered the pinned step.
            Interlock("READY", 0);
            Set(_f.Rig, "_liveTxSnapshot", SnapshotWithoutEq());
            var original = _f.Vendor.Radio.CurrentCommandConnection!;
            var reconnectedFirst = ReconnectAtTheWriteOf("transmit set miclevel=77", () => Interlock("READY", 0));
            Assert.False(RestoreLiveChain());
            Assert.True(reconnectedFirst(), "the replacement never completed while the old write waited");
            Assert.Empty(OnAnotherLiveConnection(original, "transmit set"));
            Assert.NotNull(Get(_f.Rig, "_liveTxSnapshot"));
        }

        [Fact]
        public void A_live_chain_restore_write_meeting_a_reported_carrier_at_its_write_is_not_written_and_the_debt_kept()
        {
            Interlock("READY", 0);
            Set(_f.Rig, "_liveTxSnapshot", SnapshotWithoutEq());
            ReportAtTheWriteOf("transmit set miclevel=77", () => Interlock("TRANSMITTING", Foreign));
            Assert.False(RestoreLiveChain());
            Assert.Empty(Commands("transmit set miclevel"));
            Assert.NotNull(Get(_f.Rig, "_liveTxSnapshot"));
        }

        [Fact]
        public void The_same_live_chain_restore_with_nothing_changed_runs_and_discharges()
        {
            Interlock("READY", 0);
            Set(_f.Rig, "_liveTxSnapshot", SnapshotWithoutEq());
            Assert.True(RestoreLiveChain());
            Assert.NotEmpty(Commands("transmit set miclevel"));
            Assert.Null(Get(_f.Rig, "_liveTxSnapshot"));
        }

        [Fact]
        public void The_write_time_gate_is_still_patched_into_the_vendor_transport()
        {
            // MIGRATION.md item 21. A FlexLib upgrade that drops any of these
            // sites turns this red rather than reopening every check-to-write
            // interval above in silence.
            string connection = System.IO.File.ReadAllText(Repo("FlexLib_API/FlexLib/CommandConnection.cs"));
            string tcp = System.IO.File.ReadAllText(Repo("FlexLib_API/FlexLib/TcpCommandCommunication.cs"));
            string tls = System.IO.File.ReadAllText(Repo("FlexLib_API/FlexLib/TlsCommandCommunication.cs"));
            string radio = System.IO.File.ReadAllText(Repo("FlexLib_API/FlexLib/Radio.cs"));
            Assert.Contains("public sealed class CommandWritePin : IDisposable", connection);
            Assert.Contains("protected bool AdmitWrite(CommandConnection? target)", connection);
            Assert.Equal(2, Count(tcp, "if (!AdmitWrite(attempt?.Connection)) return;"));   // Write and WriteAsync
            Assert.Contains("bool sendWhenUnconfirmed = false", connection);
            Assert.Contains("if (!AdmitWrite(session?.Connection)) return;", tls);
            Assert.Contains("CommandWritePin.Current?.Refuse(\"the command connection is not connected\");", radio);
            Assert.Equal(6, Count(connection + tcp + tls + radio, "JJFlex patch: write-time gate (MIGRATION.md item 21)"));
            string migration = System.IO.File.ReadAllText(Repo("MIGRATION.md"));
            Assert.Contains("21. **Command write-time gate", migration);
        }

        /// <summary>The real TCP adapter with one attempt published Connected and a memory stream for its writer: no socket.</summary>
        private static (TcpCommandCommunication Tcp, CommandConnection Connection, System.IO.MemoryStream Bytes) TcpOnMemory()
        {
            var tcp = new TcpCommandCommunication();
            var begin = typeof(TcpCommandCommunication).GetMethod("BeginAttempt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object attempt;
            lock (tcp.ConnectionSync)
                attempt = begin.Invoke(tcp, new object[] { System.Net.IPAddress.Loopback, 4992, 0 })!;
            var bytes = new System.IO.MemoryStream();
            attempt.GetType().GetField("Writer")!.SetValue(attempt, new System.IO.StreamWriter(bytes) { AutoFlush = true });
            var connection = (CommandConnection)attempt.GetType().GetProperty("Connection")!.GetValue(attempt)!;
            typeof(CommandCommunicationBase).GetMethod("PublishConnected", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(tcp, new object[] { connection });
            Assert.True(tcp.IsConnected);
            return (tcp, connection, bytes);
        }

        [Fact]
        public void The_real_TCP_adapters_write_and_write_async_both_ask_the_pin()
        {
            // Codex's F3: WriteAsync was outside the gate. A refusing predicate
            // writes nothing on either path; an allowing one writes on both.
            var (tcp, connection, bytes) = TcpOnMemory();
            using (CommandWritePin.Pin(connection, () => "refused (test)"))
            {
                tcp.Write("C1|xmit 0;");
                tcp.WriteAsync("C2|xmit 0;").GetAwaiter().GetResult();
            }
            Assert.Equal(0, bytes.Length);
            using (CommandWritePin.Pin(connection, () => null))
            {
                tcp.Write("C3|xmit 0;");
                tcp.WriteAsync("C4|xmit 0;").GetAwaiter().GetResult();
            }
            string written = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
            Assert.Contains("C3|xmit 0", written);
            Assert.Contains("C4|xmit 0", written);
        }

        [Fact]
        public void A_pinned_unkey_is_written_when_the_dispatch_gate_cannot_be_had_and_a_restore_is_not()
        {
            // Codex's #596 B11 at the transport: the reader holds its dispatch
            // gate past the bound. An own-carrier unkey still goes; a restore
            // fails closed; an unkey whose answer is "not ours" is refused.
            var (tcp, connection, bytes) = TcpOnMemory();
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var reader = new Thread(() => { lock (tcp.ConnectionSync) { held.Set(); release.Wait(5000); } }) { IsBackground = true };
            reader.Start();
            Assert.True(held.Wait(2000));
            try
            {
                using (var unkey = CommandWritePin.Pin(connection, () => null, sendWhenUnconfirmed: true))
                    tcp.Write("C1|xmit 0;");
                using (var restore = CommandWritePin.Pin(connection, () => null))
                {
                    tcp.Write("C2|mic input MIC;");
                    Assert.NotNull(restore.Refusal);
                }
                using (var foreign = CommandWritePin.Pin(connection, () => "another client's carrier", sendWhenUnconfirmed: true))
                {
                    tcp.Write("C3|xmit 0;");
                    Assert.NotNull(foreign.Refusal);
                }
            }
            finally { release.Set(); }
            string written = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
            Assert.Contains("C1|xmit 0", written);
            Assert.DoesNotContain("C2|", written);
            Assert.DoesNotContain("C3|", written);
        }

        // ── Round 7: an unkey of ours is never refused by contention, and an OFF is never swallowed by a stale cache ──

        /// <summary>Hold the transport's dispatch gate on another thread for <paramref name="ms"/>, as a slow report handler would.</summary>
        private Thread HoldDispatchFor(int ms)
        {
            using var held = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                lock (_f.Vendor.Transport.ConnectionSync) { held.Set(); Thread.Sleep(ms); }
            }) { IsBackground = true };
            holder.Start();
            Assert.True(held.Wait(2000), "the dispatch gate was never held");
            return holder;
        }

        [Fact]
        public void A_sends_release_under_a_busy_report_reader_still_unkeys_and_the_kill_after_it_still_does()
        {
            // Codex's #596 B11 / #861 B2, exactly: our carrier, the reader busy
            // 350 ms, the send's release. It used to be refused at 100 ms with
            // FlexLib's cache already OFF, so a second release and the kill
            // then sent nothing.
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurCarrierUp();
            var holder = HoldDispatchFor(350);
            send.Transmit = false;
            Drained();
            Assert.True(holder.Join(2000));
            Assert.Single(Commands("xmit 0"));

            // And the kill after it: the radio still reports our carrier (its
            // report has not come), and FlexLib's cache reads OFF. It goes.
            Interlock("TRANSMITTING");
            TransmitKillSwitch.DropCarrier(_f.Rig, TransmitKillSwitch.Carrier.Mox);
            Drained();
            Assert.Equal(2, Commands("xmit 0").Length);
        }

        [Fact]
        public void A_release_refused_for_another_clients_carrier_does_not_swallow_the_kill_after_it()
        {
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurCarrierUp();
            ReportAtTheWriteOf("xmit 0", () => Interlock("TRANSMITTING", Foreign));
            send.Transmit = false;                                  // refused AT its write: the carrier passed to another client
            Drained();
            Assert.Empty(Commands("xmit"));
            Assert.True(_f.Vendor.Radio.Mox, "FlexLib's cache was left reading OFF for an OFF that was never sent");
            TransmitKillSwitch.DropCarrier(_f.Rig, TransmitKillSwitch.Carrier.Mox);   // the emergency route, unconditional
            Drained();
            Assert.Single(Commands("xmit 0"));
        }

        [Fact]
        public void The_kill_sends_its_unkey_raw_when_FlexLibs_cache_already_reads_off_but_our_key_is_outstanding()
        {
            Interlock("TRANSMITTING");
            Set(_f.Vendor.Radio, "_mox", false);                   // a skipped or refused setter left the cache OFF
            Set(_f.Rig, "_ownKeyRequested", true);                 // our xmit 1 went out, no xmit 0 after it
            TransmitKillSwitch.DropCarrier(_f.Rig, TransmitKillSwitch.Carrier.Mox);
            Drained();
            Assert.Single(Commands("xmit 0"));
        }

        private void TeardownTuneOff() =>
            typeof(FlexBase).GetMethod("EnqueueTuneWrite", Private, null,
                new[] { typeof(bool), typeof(string), typeof(Action), typeof(bool) }, null)!
                .Invoke(_f.Rig, new object?[] { false, "teardown tune", null, true });

        [Fact]
        public void The_teardown_tune_off_whose_carrier_passes_to_another_client_after_its_check_is_not_written()
        {
            // Codex's #596 B12: the teardown's tune release was outside the pin.
            OurCarrierUp();
            Set(_f.Vendor.Radio, "_txTune", true);
            ReportAtTheWriteOf("transmit tune 0", () => Interlock("TRANSMITTING", Foreign));
            TeardownTuneOff();
            Drained();
            Assert.Empty(Commands("transmit tune"));
            Assert.True(_f.Vendor.Radio.TXTune, "FlexLib's tune cache was left OFF for an OFF that was never sent");
        }

        [Fact]
        public void The_same_teardown_tune_off_with_its_own_carrier_at_the_write_is_written()
        {
            OurCarrierUp();
            Set(_f.Vendor.Radio, "_txTune", true);
            ReportAtTheWriteOf("transmit tune 0", () => Interlock("TRANSMITTING"));
            TeardownTuneOff();
            Drained();
            Assert.Single(Commands("transmit tune 0"));
        }

        [Fact]
        public void The_teardown_tune_off_under_a_busy_report_reader_is_still_written()
        {
            OurCarrierUp();
            Set(_f.Vendor.Radio, "_txTune", true);
            var holder = HoldDispatchFor(350);
            TeardownTuneOff();
            Drained();
            Assert.True(holder.Join(2000));
            Assert.Single(Commands("transmit tune 0"));
        }

        // ── Round 8: forced writes in the vendor radio (MIGRATION.md item 22) ──

        [Fact]
        public void The_forced_writes_are_still_patched_into_the_vendor_radio()
        {
            string radio = System.IO.File.ReadAllText(Repo("FlexLib_API/FlexLib/Radio.cs"));
            Assert.Contains("// JJFlex patch: forced writes (MIGRATION.md item 22).", radio);
            Assert.Contains("private bool SendCommandForced(string command, Func<long> reports, Func<bool> applyCache, string property)", radio);
            Assert.Contains("public bool SendMoxOff() => SendCommandForced(\"xmit 0\"", radio);
            Assert.Contains("public bool SendTuneOff() => SendCommandForced(\"transmit tune 0\"", radio);
            Assert.Contains("public bool SendMicInput(string input) => SendCommandForced(\"mic input \"", radio);
            Assert.Contains("22. **Forced writes for our unkeys and restores", System.IO.File.ReadAllText(Repo("MIGRATION.md")));
            // Round 9 (B15): a write counts only after its writer returned.
            string connection = System.IO.File.ReadAllText(Repo("FlexLib_API/FlexLib/CommandConnection.cs"));
            string tcp = System.IO.File.ReadAllText(Repo("FlexLib_API/FlexLib/TcpCommandCommunication.cs"));
            string tls = System.IO.File.ReadAllText(Repo("FlexLib_API/FlexLib/TlsCommandCommunication.cs"));
            string ssl = System.IO.File.ReadAllText(Repo("FlexLib_API/FlexLib/SslClientTls12.cs"));
            Assert.Contains("protected static void ReportWrite(CommandWritePin? pin, bool ok, string? why = null)", connection);
            Assert.Contains("internal void Admit() => Admitted++;", connection);
            Assert.Equal(2, Count(tcp, "ReportWrite(pin, true);"));
            Assert.Contains("bool ok = session.Client.TryWrite(msg, out string? failure);", tls);
            Assert.Contains("public bool TryWrite(string message, out string? failure)", ssl);
            Assert.Contains("using var own = pin == null && current != null ? CommandWritePin.Pin(current) : null;", radio);
            Assert.Contains("A write counts only when its writer returned", System.IO.File.ReadAllText(Repo("MIGRATION.md")));
            // Round 9 (#861 B3): the cache after a forced write loses to a newer report.
            Assert.Contains("if (reports() == reportsBefore) changed = applyCache();", radio);
            Assert.Contains("if (reportsOurMox) lock (_reportedCacheSync) _moxReports++;", radio);
            Assert.Contains("lock (_reportedCacheSync) { _micInput = value.ToUpper(); _micInputReports++; }", radio);
            Assert.Contains("_txTuneReports++;", radio);
            Assert.Contains("A forced write's cache update loses to a newer report", System.IO.File.ReadAllText(Repo("MIGRATION.md")));
            // Round 10 (#863 F2): the write's success point is reported to the app.
            Assert.Contains("try { OnWritten?.Invoke(); } catch { }", connection);
            Assert.Contains("public Action? OnWritten { get; set; }", connection);
            Assert.Contains("A pinned write says when it was written", System.IO.File.ReadAllText(Repo("MIGRATION.md")));
        }

        [Fact]
        public void A_pinned_write_reports_its_success_point_once_and_a_failed_one_never()
        {
            // Round 10 (#863 F2): the app dates its own unkey at this point.
            int told = 0;
            var connection = _f.Vendor.Radio.CurrentCommandConnection!;
            using (var pin = CommandWritePin.Pin(connection))
            {
                pin.OnWritten = () => told++;
                Assert.True(_f.Vendor.Radio.SendMoxOff());
                Assert.Equal(1, told);
                _f.Vendor.Transport.FailNextWrite = true;
                Assert.False(_f.Vendor.Radio.SendMoxOff());
                Assert.Equal(1, told);
            }
        }

        // ── Round 9, Codex's #861 B3: the cache after a forced write keeps a newer report ──

        /// <summary>Deliver <paramref name="report"/> once, after the transport captured <paramref name="command"/>.</summary>
        private void ReportAfterTheWriteOf(string command, string report)
        {
            bool once = true;
            _f.Vendor.Transport.CommandWritten = c =>
            {
                if (!once || c != command) return;
                once = false;
                _f.Vendor.Status(report);
            };
        }

        [Fact]
        public void A_forced_mic_write_keeps_a_choice_the_radio_reported_while_it_was_written()
        {
            _f.Vendor.Status("transmit mic_selection=PC");
            ReportAfterTheWriteOf("mic input MIC", "transmit mic_selection=BAL");
            Assert.True(_f.Vendor.Radio.SendMicInput("MIC"));
            Assert.Equal("BAL", _f.Vendor.Radio.MicInput);
        }

        [Fact]
        public void A_forced_mic_write_with_nothing_reported_meanwhile_is_cached()
        {
            _f.Vendor.Status("transmit mic_selection=PC");
            Assert.True(_f.Vendor.Radio.SendMicInput("MIC"));
            Assert.Equal("MIC", _f.Vendor.Radio.MicInput);
        }

        [Fact]
        public void A_forced_unkey_keeps_a_carrier_the_radio_reported_while_it_was_written()
        {
            Interlock("TRANSMITTING", 0);
            Assert.True(_f.Vendor.Radio.Mox);
            ReportAfterTheWriteOf("xmit 0", "interlock state=UNKEY_REQUESTED source=SW tx_client_handle=0x00000000");
            Assert.True(_f.Vendor.Radio.SendMoxOff());
            Assert.True(_f.Vendor.Radio.Mox, "our OFF's cache update erased the radio's newer report of its carrier");
            Interlock("READY", 0);
            Assert.False(_f.Vendor.Radio.Mox);
        }

        [Fact]
        public void A_forced_unkey_with_nothing_reported_meanwhile_clears_the_cache()
        {
            Interlock("TRANSMITTING", 0);
            Assert.True(_f.Vendor.Radio.SendMoxOff());
            Assert.False(_f.Vendor.Radio.Mox);
        }

        [Fact]
        public void A_forced_tune_off_keeps_a_tune_the_radio_reported_while_it_was_written()
        {
            _f.Vendor.Status("transmit tune=1");
            Assert.True(_f.Vendor.Radio.TXTune);
            ReportAfterTheWriteOf("transmit tune 0", "transmit tune=1");
            Assert.True(_f.Vendor.Radio.SendTuneOff());
            Assert.True(_f.Vendor.Radio.TXTune);

            _f.Vendor.Transport.CommandWritten = null;
            Assert.True(_f.Vendor.Radio.SendTuneOff());       // the control: nothing reported meanwhile
            Assert.False(_f.Vendor.Radio.TXTune);
        }

        [Fact]
        public void A_forced_unkey_goes_out_with_the_cache_already_off_and_a_refused_one_leaves_the_cache_alone()
        {
            var radio = _f.Vendor.Radio;
            Set(radio, "_mox", false);                              // a stale receive report already cleared it
            Assert.True(radio.SendMoxOff());
            Assert.Single(Commands("xmit 0"));                       // the setter would have sent nothing

            Set(radio, "_mox", true);
            Set(radio, "_txTune", true);
            using (CommandWritePin.Pin(radio.CurrentCommandConnection!, () => "refused (test)"))
            {
                Assert.False(radio.SendMoxOff());
                Assert.False(radio.SendTuneOff());
                Assert.False(radio.SendMicInput("MIC"));
            }
            Assert.Single(Commands("xmit 0"));
            Assert.Empty(Commands("transmit tune"));
            Assert.True(radio.Mox, "a refused OFF changed the cache although nothing was sent");
            Assert.True(radio.TXTune);
            Assert.NotEqual("MIC", radio.MicInput);

            Assert.True(radio.SendTuneOff());
            Assert.False(radio.TXTune);
            Assert.Single(Commands("transmit tune 0"));
        }

        /// <summary>
        /// Codex's #596 B13 / #861 B1 schedule: our key is asked; at its xmit 1's
        /// pre-write point the radio's READY lands and the parser clears
        /// FlexLib's Mox cache; the xmit 1 still goes out. No transmit report has
        /// come when the release is asked.
        /// </summary>
        private void OurKeyWentOutUnderAStaleReceiveReport(Action key)
        {
            Interlock("READY", 0);
            ReportAtTheWriteOf("xmit 1", () =>
            {
                _f.Vendor.Status("interlock state=READY source= tx_client_handle=0x00000000");
                Set(_f.Vendor.Radio, "_mox", false);               // the parser's word: not transmitting
            });
            key();
            Drained();
            Assert.Single(Commands("xmit 1"));
            Assert.False(_f.Vendor.Radio.Mox);
            Assert.True((bool)Get(_f.Rig, "_ownKeyRequested")!);
        }

        [Fact]
        public void A_sends_release_after_a_stale_receive_report_still_unkeys()
        {
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurKeyWentOutUnderAStaleReceiveReport(() => send.Transmit = true);
            send.Transmit = false;
            Drained();
            Assert.Single(Commands("xmit 0"));
            Assert.False((bool)Get(_f.Rig, "_ownKeyRequested")!);   // retired by an OFF that really went
        }

        [Fact]
        public void The_teardowns_own_unkey_after_a_stale_receive_report_still_unkeys()
        {
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurKeyWentOutUnderAStaleReceiveReport(() => send.Transmit = true);
            _f.Rig.UnkeyOwnCarrier("test teardown");
            Drained();
            Assert.Single(Commands("xmit 0"));
        }

        [Fact]
        public void The_kill_after_a_stale_receive_report_still_unkeys()
        {
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurKeyWentOutUnderAStaleReceiveReport(() => send.Transmit = true);
            TransmitKillSwitch.DropCarrier(_f.Rig, TransmitKillSwitch.Carrier.Mox);
            Drained();
            Assert.Single(Commands("xmit 0"));
        }

        /// <summary>Codex's #596 B14: our tune ON goes out while a tune=0 report clears FlexLib's cache.</summary>
        private void OurTuneWentOutUnderAStaleTuneOffReport()
        {
            Interlock("READY", 0);
            ReportAtTheWriteOf("transmit tune 1", () => Set(_f.Vendor.Radio, "_txTune", false));
            _f.Rig.TxTune = true;
            Drained();
            Assert.Single(Commands("transmit tune 1"));
            Assert.False(_f.Vendor.Radio.TXTune);
        }

        [Fact]
        public void The_teardown_tune_off_after_a_stale_tune_off_report_still_goes_out()
        {
            OurTuneWentOutUnderAStaleTuneOffReport();
            TeardownTuneOff();
            Drained();
            Assert.Single(Commands("transmit tune 0"));
        }

        [Fact]
        public void The_emergency_tune_drop_after_a_stale_tune_off_report_still_goes_out()
        {
            OurTuneWentOutUnderAStaleTuneOffReport();
            TransmitKillSwitch.DropCarrier(_f.Rig, TransmitKillSwitch.Carrier.Tune);
            Drained();
            Assert.Single(Commands("transmit tune 0"));
        }

        [Fact]
        public void A_release_refused_at_its_write_leaves_FlexLibs_cache_on_the_radios_word()
        {
            // Codex's F4/T2: the round-7 put-back could overwrite a newer READY.
            // A refused forced OFF never changes the cache, so nothing is put back.
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurCarrierUp();
            ReportAtTheWriteOf("xmit 0", () => Interlock("TRANSMITTING", Foreign));
            send.Transmit = false;
            Drained();
            Assert.Empty(Commands("xmit"));
            Assert.True(_f.Vendor.Radio.Mox);                       // as the radio last said: a carrier is up
            Assert.DoesNotContain("PutBackUnsentFlag", System.IO.File.ReadAllText(Repo("Radios/FlexBase.PinnedWrites.cs")));
        }

        // ── Round 9: a write counts only when its writer returned (Codex's #596 B15) ──

        /// <summary>A stream whose first write throws, as a socket dropping under a writer does; later writes go through.</summary>
        private sealed class FailingOnceStream : System.IO.MemoryStream
        {
            private bool _failed;
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (!_failed) { _failed = true; throw new System.IO.IOException("the socket went (test)"); }
                base.Write(buffer, offset, count);
            }
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (!_failed) { _failed = true; throw new System.IO.IOException("the socket went (test)"); }
                base.Write(buffer);
            }
        }

        [Fact]
        public void The_real_TCP_adapter_counts_a_write_only_when_its_writer_returned()
        {
            var (tcp, connection, _) = TcpOnMemory();
            var attempt = typeof(TcpCommandCommunication).GetField("_attempt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tcp)!;
            var writerField = attempt.GetType().GetField("Writer")!;

            writerField.SetValue(attempt, new System.IO.StreamWriter(new FailingOnceStream()) { AutoFlush = true });
            using (var pin = CommandWritePin.Pin(connection))
            {
                tcp.Write("C1|xmit 0");
                Assert.Equal(1, pin.Admitted);
                Assert.Equal(0, pin.Written);
                Assert.NotNull(pin.Refusal);
            }

            var (tcp2, connection2, _) = TcpOnMemory();
            var attempt2 = typeof(TcpCommandCommunication).GetField("_attempt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tcp2)!;
            attempt2.GetType().GetField("Writer")!.SetValue(attempt2, null);   // no writer
            using (var pin = CommandWritePin.Pin(connection2))
            {
                tcp2.Write("C1|xmit 0");
                Assert.Equal(0, pin.Written);
                Assert.NotNull(pin.Refusal);
            }

            var (tcp3, connection3, bytes3) = TcpOnMemory();
            using (var pin = CommandWritePin.Pin(connection3))
            {
                tcp3.Write("C1|xmit 0");                          // the control: written and counted
                Assert.Equal(1, pin.Written);
                Assert.Null(pin.Refusal);
            }
            Assert.True(bytes3.Length > 0);
        }

        [Fact]
        public void The_TLS_client_says_when_it_could_not_write()
        {
            var client = new SslClientTls12("127.0.0.1", "4992", 0, startPingThread: false, validateCert: false);
            Assert.False(client.TryWrite("C1|xmit 0", out string? failure));     // no writer: never connected
            Assert.NotNull(failure);
        }

        [Fact]
        public void A_sends_release_whose_write_fails_keeps_our_key_owed_and_the_kill_after_it_unkeys()
        {
            // Codex's B15: the OFF admitted but dropped by its writer used to
            // retire our key, so the kill found nothing to send.
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurKeyWentOutUnderAStaleReceiveReport(() => send.Transmit = true);
            _f.Vendor.Transport.FailNextWrite = true;
            send.Transmit = false;
            Drained();
            Assert.Empty(Commands("xmit 0"));
            Assert.True((bool)Get(_f.Rig, "_ownKeyRequested")!, "a failed OFF retired our key");
            TransmitKillSwitch.DropCarrier(_f.Rig, TransmitKillSwitch.Carrier.Mox);
            Drained();
            Assert.Single(Commands("xmit 0"));
            Assert.False((bool)Get(_f.Rig, "_ownKeyRequested")!);
        }

        [Fact]
        public void The_teardowns_tune_off_whose_write_fails_keeps_our_tune_owed_and_the_kill_after_it_sends_it()
        {
            OurTuneWentOutUnderAStaleTuneOffReport();
            _f.Vendor.Transport.FailNextWrite = true;
            TeardownTuneOff();
            Drained();
            Assert.Empty(Commands("transmit tune 0"));
            Assert.True((bool)Get(_f.Rig, "_ownTuneRequested")!, "a failed tune OFF retired our tune");
            TransmitKillSwitch.DropCarrier(_f.Rig, TransmitKillSwitch.Carrier.Tune);
            Drained();
            Assert.Single(Commands("transmit tune 0"));
        }

        [Fact]
        public void An_ordinary_unkey_whose_write_fails_keeps_our_key_owed_and_the_next_one_sends()
        {
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurKeyWentOutUnderAStaleReceiveReport(() => send.Transmit = true);
            _f.Vendor.Transport.FailNextWrite = true;
            _f.Rig.Transmit = false;
            Drained();
            Assert.Empty(Commands("xmit 0"));
            Assert.True((bool)Get(_f.Rig, "_ownKeyRequested")!);
            _f.Rig.Transmit = false;
            Drained();
            Assert.Single(Commands("xmit 0"));
        }

        // ── Round 10, Codex's #596 B16: a failed unkey is never read as receive ──

        /// <summary>The teardown's own unkey-and-confirm, as Disconnect calls it.</summary>
        private bool TeardownUnkeysAndConfirms(int receiveWaitMs = 300) =>
            _f.Rig.UnkeyBeforePuttingBack("test teardown", TimeSpan.FromSeconds(2), receiveWaitMs);

        /// <summary>The mic put-back's own write, for radio and connection now, owing MIC over the PC we left.</summary>
        private string[] MicPutBackNow()
        {
            _f.Vendor.Status("transmit mic_selection=PC");
            var swapType = typeof(FlexBase).GetNestedType("MicInputSwap", BindingFlags.NonPublic)!;
            object swap = Activator.CreateInstance(swapType, BindingFlags.Public | BindingFlags.Instance, null,
                new object?[] { "MIC", _f.Vendor.Radio, 0L, false }, null)!;
            typeof(FlexBase).GetMethod("PutBackNow", Private)!.Invoke(_f.Rig, new object[] { _f.Vendor.Radio, swap, "round ten test", 0L });
            return Commands("mic input MIC");
        }

        /// <summary>Deliver READY from another thread once our xmit 0 has really gone out and been marked.</summary>
        private Thread ReceiveAfterOurOffIsWritten()
        {
            var t = new Thread(() =>
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < until && (Commands("xmit 0").Length == 0 || (bool)Get(_f.Rig, "_ownKeyRequested")!))
                    Thread.Sleep(2);
                Thread.Sleep(20);
                Interlock("READY", 0);
            }) { IsBackground = true };
            t.Start();
            return t;
        }

        [Fact]
        public void A_teardown_whose_unkey_cannot_be_written_puts_nothing_back_under_our_outstanding_key()
        {
            // Codex's B16: a READY from before our xmit 1 read as receive after the
            // teardown's xmit 0 failed, and the mic was put back under our key.
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurKeyWentOutUnderAStaleReceiveReport(() => send.Transmit = true);
            _f.Vendor.Transport.FailEveryWriteOf = "xmit 0";
            Assert.False(TeardownUnkeysAndConfirms(), "the teardown confirmed receive with our own key's off unwritten");
            Assert.Empty(Commands("xmit 0"));
            Assert.True((bool)Get(_f.Rig, "_ownKeyRequested")!);
            string? why = _f.Rig.RestoreRefusedAtWrite(_f.Vendor.Radio, _f.Vendor.Radio.CurrentCommandConnection!);
            Assert.NotNull(why);
            Assert.Contains("its off has not been written", why!);
            Assert.Empty(MicPutBackNow());
            _f.Vendor.Transport.FailEveryWriteOf = null;
        }

        [Fact]
        public void A_teardown_whose_first_unkey_fails_writes_it_again_and_still_waits_for_a_later_receive()
        {
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurKeyWentOutUnderAStaleReceiveReport(() => send.Transmit = true);
            _f.Vendor.Transport.FailNextWrite = true;            // the first xmit 0 only
            Assert.False(TeardownUnkeysAndConfirms(), "the old READY was read as the end of our key");
            Assert.Single(Commands("xmit 0"));                    // the second attempt went out
            Assert.False((bool)Get(_f.Rig, "_ownKeyRequested")!);
            Assert.Contains("has not reported receive since our own key's off went out",
                _f.Rig.RestoreRefusedAtWrite(_f.Vendor.Radio, _f.Vendor.Radio.CurrentCommandConnection!)!);
            Assert.Empty(MicPutBackNow());
        }

        [Fact]
        public void A_teardown_whose_unkey_went_out_confirms_on_a_receive_reported_after_it_and_puts_back()
        {
            // The control: no failure, and the radio's READY after our xmit 0.
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurKeyWentOutUnderAStaleReceiveReport(() => send.Transmit = true);
            var later = ReceiveAfterOurOffIsWritten();
            Assert.True(TeardownUnkeysAndConfirms(3000));
            Assert.True(later.Join(5000));
            Assert.Null(_f.Rig.RestoreRefusedAtWrite(_f.Vendor.Radio, _f.Vendor.Radio.CurrentCommandConnection!));
            Assert.Single(MicPutBackNow());
        }

        [Fact]
        public void A_teardown_after_a_key_that_never_went_out_is_not_held_by_it()
        {
            // Our ON was revoked in the queue: nothing was transmitted, so the
            // radio's receive is enough.
            Interlock("READY", 0);
            _hold = true;
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            send.Transmit = true;
            _f.Rig.RevokeAdmittedTransmitWork("a drop (test)");
            _hold = false;
            Drained();
            Assert.Empty(Commands("xmit 1"));
            Assert.True(_f.Rig.LatestOwnKey!.NeverWent);
            Interlock("READY", 0);
            Assert.True(TeardownUnkeysAndConfirms());
            Assert.Null(_f.Rig.RestoreRefusedAtWrite(_f.Vendor.Radio, _f.Vendor.Radio.CurrentCommandConnection!));
        }

        [Fact]
        public void A_teardown_whose_tune_off_cannot_be_written_puts_nothing_back_under_our_tune()
        {
            // The sibling carrier family: our transmit tune 1 went out under a
            // stale tune=0, the teardown's tune off fails every time.
            OurTuneWentOutUnderAStaleTuneOffReport();
            _f.Vendor.Transport.FailEveryWriteOf = "transmit tune 0";
            Assert.False(TeardownUnkeysAndConfirms());
            Assert.Empty(Commands("transmit tune 0"));
            Assert.Contains("our own tune went out and its off has not been written",
                _f.Rig.RestoreRefusedAtWrite(_f.Vendor.Radio, _f.Vendor.Radio.CurrentCommandConnection!)!);
            Assert.Empty(MicPutBackNow());
            _f.Vendor.Transport.FailEveryWriteOf = null;
        }

        [Fact]
        public void A_teardown_whose_tune_off_went_out_confirms_on_a_receive_reported_after_it()
        {
            OurTuneWentOutUnderAStaleTuneOffReport();
            var later = new Thread(() =>
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < until && (Commands("transmit tune 0").Length == 0 || (bool)Get(_f.Rig, "_ownTuneRequested")!))
                    Thread.Sleep(2);
                Thread.Sleep(20);
                Interlock("READY", 0);
            }) { IsBackground = true };
            later.Start();
            Assert.True(TeardownUnkeysAndConfirms(3000));
            Assert.True(later.Join(5000));
            Assert.Null(_f.Rig.RestoreRefusedAtWrite(_f.Vendor.Radio, _f.Vendor.Radio.CurrentCommandConnection!));
        }

        // ── Round 11, Codex's #863 F1: a successor that never went out keeps
        //    the predecessor's unfinished receive ──

        [Fact]
        public void A_revoked_key_after_our_written_off_does_not_discard_the_wait_for_its_receive()
        {
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurKeyWentOutUnderAStaleReceiveReport(() => send.Transmit = true);
            send.Transmit = false;
            Drained();
            Assert.Single(Commands("xmit 0"));                    // our off really went out
            var connection = _f.Vendor.Radio.CurrentCommandConnection!;
            Assert.Contains("has not reported receive since", _f.Rig.RestoreRefusedAtWrite(_f.Vendor.Radio, connection)!);

            _hold = true;
            send.Transmit = true;                                 // a successor key...
            _f.Rig.RevokeAdmittedTransmitWork("a drop (test)");   // ...revoked in the queue
            _hold = false;
            Drained();
            Assert.True(_f.Rig.LatestOwnKey!.NeverWent);
            Assert.Contains("has not reported receive since", _f.Rig.RestoreRefusedAtWrite(_f.Vendor.Radio, connection)!);
            Assert.Empty(MicPutBackNow());                        // round 10 wrote MIC here

            Interlock("READY", 0);                                // the radio's word after our off
            Assert.Null(_f.Rig.RestoreRefusedAtWrite(_f.Vendor.Radio, connection));
        }

        [Fact]
        public void Codexs_reproduction_a_revoked_successor_after_an_unconfirmed_off_puts_no_mic_back()
        {
            // The ninth #863 re-check's exact schedule: READY before, then our
            // xmit 1 and xmit 0 with no status after either, then a new ON
            // asked and revoked as the teardown revokes it.
            Interlock("READY", 0);
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            send.Transmit = true;
            Drained();
            send.Transmit = false;
            Drained();
            Assert.Equal(new[] { "xmit 1", "xmit 0" }, _f.Vendor.Transport.Commands.Where(c => c.StartsWith("xmit")).ToArray());
            var connection = _f.Vendor.Radio.CurrentCommandConnection!;
            Assert.Empty(MicPutBackNow());                        // the control: no receive since our off
            _hold = true;
            send.Transmit = true;
            _f.Rig.RevokeAdmittedTransmitWork("teardown (test)");
            _hold = false;
            Drained();
            Assert.True(_f.Rig.LatestOwnKey!.NeverWent);
            Assert.Empty(MicPutBackNow());                        // round 10 wrote MIC here
            Interlock("READY", 0);
            // Once the radio says receive, it goes: the direct put-back, and the
            // earlier one kept owed may also be retried by that READY (round 12:
            // asserting exactly one raced that retry).
            Assert.NotEmpty(MicPutBackNow());
        }

        // ── Round 11, Codex's #596 B17: from the moment a key of ours is asked
        //    until it is known never to have gone out, it is possibly keyed ──

        /// <summary>Run the mic put-back on another thread at the moment the transport has written <paramref name="command"/>, before its caller returns.</summary>
        private Func<string[]?> PutBackWhileWriting(string command)
        {
            string[]? during = null;
            bool once = true;
            var recorder = _f.Vendor.Transport.CommandWritten;
            _f.Vendor.Transport.CommandWritten = c =>
            {
                recorder?.Invoke(c);
                if (!once || c != command) return;
                once = false;
                var t = new Thread(() => during = MicPutBackNow()) { IsBackground = true };
                t.Start();
                Assert.True(t.Join(5000), "the put-back never finished");
            };
            return () => during;
        }

        [Fact]
        public void A_mic_put_back_while_our_key_is_being_written_is_held()
        {
            // Codex's B17: an older READY at xmit 1's pre-write point, then the
            // put-back after the bytes went out and before the bookkeeping.
            Interlock("READY", 0);
            ReportAtTheWriteOf("xmit 1", () =>
            {
                _f.Vendor.Status("interlock state=READY source= tx_client_handle=0x00000000");
                Set(_f.Vendor.Radio, "_mox", false);
            });
            var during = PutBackWhileWriting("xmit 1");
            new FlexBaseVoiceMessageRig(_f.Rig).Transmit = true;
            Drained();
            Assert.Single(Commands("xmit 1"));
            Assert.NotNull(during());
            Assert.Empty(during()!);                              // round 10 wrote MIC here
        }

        [Fact]
        public void A_mic_put_back_while_our_tune_is_being_written_is_held()
        {
            Interlock("READY", 0);
            ReportAtTheWriteOf("transmit tune 1", () => Set(_f.Vendor.Radio, "_txTune", false));
            var during = PutBackWhileWriting("transmit tune 1");
            _f.Rig.TxTune = true;
            Drained();
            Assert.Single(Commands("transmit tune 1"));
            Assert.NotNull(during());
            Assert.Empty(during()!);
        }

        [Fact]
        public void A_key_still_queued_holds_the_put_back_until_it_is_known_never_to_have_gone_out()
        {
            Interlock("READY", 0);
            _hold = true;
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            send.Transmit = true;                                 // asked, not yet run
            Assert.Empty(MicPutBackNow());
            _f.Rig.RevokeAdmittedTransmitWork("a drop (test)");
            _hold = false;
            Drained();
            Assert.True(_f.Rig.LatestOwnKey!.NeverWent);
            Assert.Single(MicPutBackNow());                        // nothing went out: the receive is enough
        }

        // ── Round 12, Codex's #596 B18: never gone out only on the queue's own answer ──

        [Fact]
        public void A_key_is_settled_never_gone_out_only_when_the_queue_dropped_it()
        {
            // Codex's B18: the key was settled from one look at MainLoop, the loop
            // started before Enqueue looked again, and a key recorded as never
            // gone out was written. Here the loop starts the moment the key is
            // settled, as in Codex's schedule.
            Interlock("READY", 0);
            var settled = typeof(FlexBase).GetEvent("OwnKeyChanged", Private)!;
            Action startTheLoop = () => _f.Rig.q.MainLoop = true;
            _f.Rig.q.MainLoop = false;
            settled.GetAddMethod(true)!.Invoke(_f.Rig, new object[] { startTheLoop });
            try
            {
                new FlexBaseVoiceMessageRig(_f.Rig).Transmit = true;
                _f.Rig.q.MainLoop = true;
                Drained();
                var key = _f.Rig.LatestOwnKey!;
                Assert.True(key.NeverWent);
                Assert.Empty(Commands("xmit 1"));            // never gone out, and never written
            }
            finally { settled.GetRemoveMethod(true)!.Invoke(_f.Rig, new object[] { startTheLoop }); }
        }

        [Fact]
        public void A_tune_asked_while_the_queue_is_not_running_never_goes_out_and_one_asked_while_it_runs_does()
        {
            Interlock("READY", 0);
            var latestTune = typeof(FlexBase).GetField("_latestOwnTune", Private)!;
            _f.Rig.q.MainLoop = false;
            _f.Rig.TxTune = true;
            Assert.True(((FlexBase.OwnKey)latestTune.GetValue(_f.Rig)!).NeverWent);
            _f.Rig.q.MainLoop = true;
            Drained();
            Assert.Empty(Commands("transmit tune 1"));
            _f.Rig.TxTune = true;
            Drained();
            Assert.Single(Commands("transmit tune 1"));
            Assert.False(((FlexBase.OwnKey)latestTune.GetValue(_f.Rig)!).NeverWent);
        }

        // ── Round 12, Codex's #596 F5: an OFF ends the key it was asked for ──

        [Fact]
        public void An_off_queued_before_a_newer_ask_ends_its_own_key_and_the_receive_after_it_releases_the_restore()
        {
            Interlock("READY", 0);
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            send.Transmit = true;
            Drained();                                       // ON written
            _hold = true;
            send.Transmit = false;                           // OFF queued for that key
            send.Transmit = true;                            // a newer ask, published before the OFF runs
            _f.Rig.RevokeAdmittedTransmitWork("a drop (test)");
            _hold = false;
            Drained();
            Assert.Single(Commands("xmit 0"));
            Assert.True(_f.Rig.LatestOwnKey!.NeverWent);
            Interlock("READY", 0);                           // the radio's word after our OFF
            Assert.Null(_f.Rig.RestoreRefusedAtWrite(_f.Vendor.Radio, _f.Vendor.Radio.CurrentCommandConnection!));
            Assert.NotEmpty(MicPutBackNow());                 // round 11 held this forever
        }

        [Fact]
        public void A_tune_off_queued_before_a_newer_tune_ends_its_own_tune()
        {
            Interlock("READY", 0);
            _f.Rig.TxTune = true;
            Drained();                                       // tune ON written
            _hold = true;
            _f.Rig.TxTune = false;                           // OFF queued for that tune
            _f.Rig.TxTune = true;                            // a newer tune asked
            _f.Rig.RevokeAdmittedTransmitWork("a drop (test)");
            _hold = false;
            Drained();
            Assert.Single(Commands("transmit tune 0"));
            Interlock("READY", 0);
            Assert.Null(_f.Rig.RestoreRefusedAtWrite(_f.Vendor.Radio, _f.Vendor.Radio.CurrentCommandConnection!));
        }

        // ── Integration fixes 2026-11-06 ──

        [Fact]
        public void The_where_it_transmits_memory_is_reset_by_a_new_connection_on_the_same_rig()
        {
            // Fix 1: ForgetFor had no production caller, so a reconnect of the
            // same radio kept the last split as already said.
            var roles = SliceRoles.Of(_f.Rig);
            var r = new TransmitSliceNoticeTests.Reader();
            ICountdownWords words = r.Speech.SayWhole("Transmitting on slice A", Speech.SpeechSubject.TransmitSliceNotice);
            r.Arbiter.OnOutcome(r.LastTicket, r.LastText, Speech.SpeechOutcome.Completed(4, 4, 900));
            TransmitSliceNotice.Keyed(new TransmitSliceNotice.Notice(_f.Rig, "test", roles, false, true, words));
            WaitFor(() => TransmitSliceNotice.LastSpoken(_f.Rig).HasValue, "the split to be remembered");
            _f.Vendor.Transport.Reconnect();               // the same Radio and rig, a new command connection
            Assert.Null(TransmitSliceNotice.LastSpoken(_f.Rig));
        }

        [Fact]
        public void A_withheld_VOX_restore_is_written_on_its_connections_next_receive()
        {
            // Fix 5: withheld at its write, VOX stayed off until the next pause,
            // an operator change or the disconnect.
            _f.AddOwnSlice(0, 14.25, "USB");
            Set(_f.Rig, "_TXVFO", 0);
            Set(_f.Rig, "_IsConnected", true);
            _f.Vendor.Status("transmit vox_enable=1");
            Interlock("READY");
            var lease = _f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
            WaitFor(() => Commands("transmit set vox_enable=0").Length == 1, "VOX off");
            Interlock("TRANSMITTING", Foreign);
            Assert.False(lease.Release(immediate: true));
            Assert.True(_f.Rig.VoxPauses.RestoreOwed(FlexBase.VoxSetting.SimpleVox));
            Interlock("READY");                            // the carrier ends
            WaitFor(() => Commands("transmit set vox_enable=1").Length == 1, "the withheld restore on the receive report");
            Assert.False(_f.Rig.VoxPauses.RestoreOwed(FlexBase.VoxSetting.SimpleVox));
        }

        [Fact]
        public void A_withheld_VOX_restore_is_not_retried_by_a_report_that_is_not_receive()
        {
            _f.AddOwnSlice(0, 14.25, "USB");
            Set(_f.Rig, "_TXVFO", 0);
            Set(_f.Rig, "_IsConnected", true);
            _f.Vendor.Status("transmit vox_enable=1");
            Interlock("READY");
            var lease = _f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
            WaitFor(() => Commands("transmit set vox_enable=0").Length == 1, "VOX off");
            Interlock("TRANSMITTING", Foreign);
            lease.Release(immediate: true);
            Interlock("TX_FAULT", Foreign);
            Thread.Sleep(150);
            Drained();
            Assert.Empty(Commands("transmit set vox_enable=1"));
            Assert.True(_f.Rig.VoxPauses.RestoreOwed(FlexBase.VoxSetting.SimpleVox));
        }

        [Fact]
        public void A_disconnect_does_not_wait_for_a_carrier_the_radios_own_push_to_talk_holds()
        {
            // Fix 7: with a foot switch holding the carrier, the teardown waited
            // its whole receive wait on the UI thread, then put nothing back.
            Interlock("READY", 0);
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            send.Transmit = true;
            Drained();                                       // our ON
            _f.Vendor.Status($"interlock state=TRANSMITTING source=RCA tx_client_handle=0x{RigOnVendorRadio.OurHandle:X8}");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(_f.Rig.UnkeyBeforePuttingBack("test teardown", TimeSpan.FromSeconds(2), 3000));
            watch.Stop();
            Assert.Single(Commands("xmit 0"));               // our own off still went out
            Assert.True(watch.ElapsedMilliseconds < 1500, "the teardown waited " + watch.ElapsedMilliseconds + " ms on a carrier it cannot end");
            Assert.NotNull(_f.Rig.RestoreRefusedAtWrite(_f.Vendor.Radio, _f.Vendor.Radio.CurrentCommandConnection!));
        }

        [Fact]
        public void A_disconnect_still_waits_for_receive_after_our_own_software_carrier()
        {
            // The control: our own (SW) carrier, no receive yet, waits its bound.
            Interlock("READY", 0);
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            send.Transmit = true;
            Drained();
            Interlock("TRANSMITTING");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(_f.Rig.UnkeyBeforePuttingBack("test teardown", TimeSpan.FromSeconds(2), 600));
            Assert.True(watch.ElapsedMilliseconds >= 500);
        }

        [Fact]
        public void An_imports_exemption_covers_only_its_own_power_off_notice_even_when_the_import_fails()
        {
            // Integration fix 2 (Codex's F2): the flag stayed set after a failed
            // import, so a later power-off could be taken for the import's. Now
            // it is taken by the import's own notice and cleared when it returns.
            bool takenInTheNotice = false;
            FlexBase.PowerStatusHandler handler = (s, on) => { if (!on) takenInTheNotice = _f.Rig.TakeImportPowerOff(); };
            _f.Rig.PowerStatus += handler;
            try
            {
                string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jjflex-no-such-dir", "no-such-file.ssdr_cfg");
                try { _f.Rig.ImportProfile(missing); } catch { /* a failed import is the point */ }
            }
            finally { _f.Rig.PowerStatus -= handler; }
            Assert.True(takenInTheNotice);                    // the import's own notice is the import's
            Assert.False(_f.Rig.TakeImportPowerOff());         // and nothing is left for a later power-off
        }

        [Fact]
        public void An_imports_exemption_nobody_took_is_cleared_when_its_notice_returns()
        {
            // No power-off subscriber took it (or the import failed before
            // one did): it must not wait for a later, real power-off.
            string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jjflex-no-such-dir", "no-such-file.ssdr_cfg");
            try { _f.Rig.ImportProfile(missing); } catch { /* a failed import is the point */ }
            Assert.False(_f.Rig.TakeImportPowerOff());
        }

        [Fact]
        public void An_imports_exemption_set_when_a_new_connection_begins_is_cleared()
        {
            typeof(FlexBase).GetField("_importPowerOffPending", Private)!.SetValue(_f.Rig, 1);
            _f.Vendor.Transport.Reconnect();
            Assert.False(_f.Rig.TakeImportPowerOff());
        }

        [Fact]
        public void A_profile_imports_power_off_does_not_run_the_teardown_release_or_unwire_the_drop_handler()
        {
            string main = System.IO.File.ReadAllText(Repo("JJFlexWpf/MainWindow.xaml.cs"));
            int body = main.IndexOf("private void PowerNowOffInternal()", StringComparison.Ordinal);
            Assert.True(body > 0, "PowerNowOffInternal moved");
            string text = main.Substring(body, 2500);
            int importing = text.IndexOf("bool importing = RigControl?.TakeImportPowerOff() == true;", StringComparison.Ordinal);
            int release = text.IndexOf("ReleaseKeyForTeardown(\"the radio powered off\");", StringComparison.Ordinal);
            int unwire = text.IndexOf("UnwireDropHandler();", StringComparison.Ordinal);
            int elseAt = text.IndexOf("else", importing, StringComparison.Ordinal);
            Assert.True(importing > 0 && elseAt > importing && release > elseAt && unwire > elseAt,
                "an import's power-off runs the teardown release or unwires the drop handler");
            string flex = System.IO.File.ReadAllText(Repo("Radios/FlexBase.MainThread.cs"));
            int import = flex.IndexOf("internal void ImportProfile(string name)", StringComparison.Ordinal);
            string importBody = flex.Substring(import, 600);
            Assert.True(importBody.IndexOf("Interlocked.Exchange(ref _importPowerOffPending, 1);", StringComparison.Ordinal)
                < importBody.IndexOf("try { raisePowerEvent(false); }", StringComparison.Ordinal));
            Assert.Contains("finally { System.Threading.Interlocked.Exchange(ref _importPowerOffPending, 0); }", importBody);
        }

        // ── Integration fix 2 (Codex's B1): work accepted but not run at the
        //    loop's end is dropped, never written onto a replacement ──

        [Fact]
        public void Work_accepted_before_a_reconnect_and_not_run_by_the_loops_end_writes_nothing()
        {
            Interlock("READY", 0);
            _f.AddOwnSlice(0, 14.100, "USB");
            Set(_f.Rig, "_RXVFO", 0);
            _hold = true;                                    // the loop is busy: nothing runs
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            send.Transmit = true;                             // a key-on
            _f.Rig.RXFrequency = 14_100_100;                  // an ordinary tune
            _f.Rig.MicSource = "MIC";                         // an ordinary setting
            _f.Rig.HeadphoneGain = 12;
            int before = _f.Vendor.Transport.Commands.Count;
            _f.Vendor.Transport.Reconnect();                  // a replacement connection, Connecting forwarded
            _f.Rig.CloseCommandQueue();                       // the loop ends with them still queued
            Assert.Equal(before, _f.Vendor.Transport.Commands.Count);   // integration fix 1 ran them onto the replacement
            Assert.True(_f.Rig.LatestOwnKey!.NeverWent);      // the key's caller was told
            Assert.Equal(14_100_000UL, _f.Rig.RXFrequency);   // the tune conceded: readers say where the slice is
        }

        // ── Integration fix 2 (Codex's F1): a VOX restore refused by the gate's
        //    timeout is retried with a real write ──

        [Fact]
        public void A_VOX_restore_refused_by_the_gate_timeout_is_written_on_the_next_receive()
        {
            _f.AddOwnSlice(0, 14.25, "USB");
            Set(_f.Rig, "_TXVFO", 0);
            Set(_f.Rig, "_IsConnected", true);
            _f.Vendor.Status("transmit vox_enable=1");
            Interlock("READY");
            var lease = _f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
            WaitFor(() => Commands("transmit set vox_enable=0").Length == 1, "VOX off");
            var holder = HoldDispatchFor(350);
            Assert.False(lease.Release(immediate: true));     // refused at its write: the gate was not had in time
            Assert.True(holder.Join(2000));
            Assert.True(_f.Rig.VoxPauses.RestoreOwed(FlexBase.VoxSetting.SimpleVox));
            Assert.Empty(Commands("transmit set vox_enable=1"));
            Interlock("READY");                              // the next receive retries it
            WaitFor(() => Commands("transmit set vox_enable=1").Length == 1, "the restore really written");
            Assert.False(_f.Rig.VoxPauses.RestoreOwed(FlexBase.VoxSetting.SimpleVox));
        }

        // ── Integration fix 3 (Codex's #596 B3): the operator's own VOX change,
        //    made while another thread's restore holds the app scope at the
        //    transport, voids that restore; no retry undoes it ──

        private (FlexBase.VoxSetting Which, string On, string Off, Func<bool> Cache) VoxOrBreakIn(bool cw)
        {
            _f.AddOwnSlice(0, 14.25, cw ? "CW" : "USB");
            Set(_f.Rig, "_TXVFO", 0);
            Set(_f.Rig, "_IsConnected", true);
            _f.Vendor.Status(cw ? "transmit break_in=1" : "transmit vox_enable=1");
            Interlock("READY");
            return cw
                ? (FlexBase.VoxSetting.CwBreakIn, "cw break_in 1", "cw break_in 0", () => _f.Vendor.Radio.CWBreakIn)
                : (FlexBase.VoxSetting.SimpleVox, "transmit set vox_enable=1", "transmit set vox_enable=0", () => _f.Vendor.Radio.SimpleVOXEnable);
        }

        /// <summary>
        /// Start a restore on its own thread with the dispatch gate held, and
        /// return once it is inside its write: its setter has made FlexLib's
        /// cache read ON, and it waits for the transport inside the app scope.
        /// </summary>
        private Thread RestoreWaitingAtTheGate(VoxPauseOwner.Lease lease, Func<bool> cache)
        {
            var restorer = new Thread(() => lease.Release(immediate: true)) { IsBackground = true };
            restorer.Start();
            WaitFor(cache, "the restore inside its write, waiting for the transport");
            // Positive control: the overlap Codex found is the one reached here.
            Assert.True((int)Get(_f.Rig, "_appInitiatedSettingDepth")! > 0, "the restore holds the rig-wide app scope");
            return restorer;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_operators_VOX_off_during_a_restores_wait_is_not_undone_by_the_next_receive(bool cw)
        {
            // Codex's B3 schedule: the restore exhausts its bounded attempts at a
            // held gate while the operator, on another thread, turns VOX off.
            var (which, on, off, cache) = VoxOrBreakIn(cw);
            var lease = _f.Rig.VoxPauses.Acquire(which)!;
            WaitFor(() => Commands(off).Length == 1, "the pause's off");
            using var release = new ManualResetEventSlim();
            using var held = new ManualResetEventSlim();
            var holder = new Thread(() => { lock (_f.Vendor.Transport.ConnectionSync) { held.Set(); release.Wait(3000); } }) { IsBackground = true };
            holder.Start();
            Assert.True(held.Wait(2000));
            var restorer = RestoreWaitingAtTheGate(lease, cache);
            _f.Rig.Vox = FlexBase.OffOnValues.off;           // the operator, on this thread
            Assert.True(restorer.IsAlive, "the operator's change never waits behind the restore");
            Assert.True(restorer.Join(3000));                  // refused at the gate, not written
            release.Set();
            Assert.True(holder.Join(2000));
            WaitFor(() => Commands(off).Length == 2, "the operator's off really written");
            Assert.False(cache());
            Assert.False(_f.Rig.VoxPauses.RestoreOwed(which)); // the operator's choice voided it
            Assert.False(_f.Rig.VoxPauses.HasWithheld);
            Interlock("READY");                                // the next receive retries nothing
            Thread.Sleep(150);
            Interlock("READY");
            Thread.Sleep(150);
            Assert.Empty(Commands(on));
            Assert.False(cache());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_restore_waiting_at_the_transport_when_the_operator_turns_VOX_off_refuses_at_its_write(bool cw)
        {
            // One step with the restore's own decision: the restore passed its
            // checks before the write, the operator changes it while it waits
            // for the transport, and the gate then frees within its bound. The
            // question asked AT the write sees the operator's change.
            var (which, on, off, cache) = VoxOrBreakIn(cw);
            var lease = _f.Rig.VoxPauses.Acquire(which)!;
            WaitFor(() => Commands(off).Length == 1, "the pause's off");
            using var release = new ManualResetEventSlim();
            using var held = new ManualResetEventSlim();
            var holder = new Thread(() => { lock (_f.Vendor.Transport.ConnectionSync) { held.Set(); release.Wait(3000); } }) { IsBackground = true };
            holder.Start();
            Assert.True(held.Wait(2000));
            var restorer = RestoreWaitingAtTheGate(lease, cache);
            _f.Rig.Vox = FlexBase.OffOnValues.off;           // the operator, on this thread
            release.Set();                                     // the gate frees before the restore's bound
            Assert.True(holder.Join(2000));
            Assert.True(restorer.Join(3000));
            WaitFor(() => Commands(off).Length == 2, "the operator's off really written");
            Assert.Empty(Commands(on));                        // never written after the operator's choice
            Assert.False(cache());
            Assert.False(_f.Rig.VoxPauses.RestoreOwed(which));
            Interlock("READY");
            Thread.Sleep(150);
            Assert.Empty(Commands(on));
        }

        [Fact]
        public void An_app_scoped_VOX_write_on_this_thread_is_still_the_apps_while_another_restore_waits()
        {
            // The narrower question is the CALLER's scope: an app write on a
            // thread that opened one still voids nothing.
            var (which, on, off, cache) = VoxOrBreakIn(false);
            var lease = _f.Rig.VoxPauses.Acquire(which)!;
            WaitFor(() => Commands(off).Length == 1, "the pause's off");
            using (_f.Rig.AppInitiatedSettingChanges()) _f.Rig.Vox = FlexBase.OffOnValues.off;
            Assert.True(lease.Paused);
            Assert.True(lease.Release(immediate: true));
            Assert.Single(Commands(on));
        }

        [Fact]
        public void An_app_scope_answers_for_the_thread_that_opened_it_wherever_it_is_closed()
        {
            var scope = _f.Rig.AppInitiatedSettingChanges();
            Assert.True(_f.Rig.AppScopeOpenOnThisThread);
            bool seenElsewhere = true;
            var other = new Thread(() => { seenElsewhere = _f.Rig.AppScopeOpenOnThisThread; scope.Dispose(); });
            other.Start();
            Assert.True(other.Join(2000));
            Assert.False(seenElsewhere);                  // another thread's scope is not this caller's
            Assert.False(_f.Rig.AppScopeOpenOnThisThread);  // closed on another thread, closed here
        }

        private static int Count(string text, string what)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
            return n;
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
            // Round 6 (Codex's B9/N1): the writer is held PAST the old one-second
            // bound, and the replacement completes first. The transport gate refuses.
            Assert.True(reconnectedFirst(), "the replacement never completed while the old write waited; the schedule would prove nothing");
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
