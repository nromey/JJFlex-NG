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
    /// Junk box repair round 4 (#596, #861, #600; Codex's third re-check). The
    /// round-three mechanisms held for the routes they were given; the
    /// re-check found a multi-write item gated once (F1), an incomplete apply
    /// counted as complete (F2), receive evidence outliving its connection
    /// (F4), VOX debt adopted onto another connection of the same radio (B6),
    /// and a send's unkey reaching a carrier on a returned connection (B5).
    /// The real <see cref="FlexBase"/> on the vendored Radio and its fake
    /// transport, with a command loop that can be held; what went out is read
    /// off the transport. No socket, no radio, nothing keys.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RepairFourTeardownTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const uint Foreign = 0x0BADF00D;
        private const string Serial = "0596-0861-0004-0001";

        private readonly RadioConfigStaticsScope _scope = new(nameof(RepairFourTeardownTests));
        private readonly RigOnVendorRadio _f;
        private readonly Thread _loop;
        private volatile bool _stop;
        private volatile bool _hold;

        public RepairFourTeardownTests()
        {
            _f = new RigOnVendorRadio(Serial);
            _f.Vendor.Radio.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Mox")
                    typeof(FlexBase).GetMethod("noteRadioMox", Private)!.Invoke(_f.Rig, new object[] { _f.Vendor.Radio.Mox });
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

        private static (Thread Thread, Func<T> Result) Background<T>(Func<T> work)
        {
            T result = default!;
            var t = new Thread(() => result = work()) { IsBackground = true };
            t.Start();
            return (t, () => result);
        }

        private bool Restore()
        {
            var run = Background(() => (bool)typeof(FlexBase).GetMethod("RestoreLiveTransmitAudioNow", Private)!.Invoke(_f.Rig, null)!);
            Assert.True(run.Thread.Join(8000), "the restore never returned");
            return run.Result();
        }

        private static AudioChainPreset SnapshotWithEq() => new AudioChainPreset
        {
            SchemaVersion = 2,
            MicGain = 77,
            MicBoost = true,
            TxFilterLow = 150,
            TxFilterHigh = 2850,
            TxEqCaptured = true,
            TxEqEnabled = true,
            TxEq32 = 3, TxEq63 = 3, TxEq125 = 3, TxEq250 = 3, TxEq500 = 3,
            TxEq1000 = 3, TxEq2000 = 3, TxEq4000 = 3, TxEq8000 = 3,
        };

        /// <summary>The radio's transmit equaliser, registered as the radio's own reply registers it.</summary>
        private Equalizer AddTxEqualizer()
        {
            var eq = (Equalizer)Activator.CreateInstance(typeof(Equalizer), Private, null,
                new object[] { _f.Vendor.Radio, EqualizerSelect.TX }, null)!;
            var list = (List<Equalizer>)typeof(Radio).GetField("_equalizers", Private)!.GetValue(_f.Vendor.Radio)!;
            lock (list) list.Add(eq);
            Assert.Same(eq, _f.Vendor.Radio.FindEqualizerByEQSelect(EqualizerSelect.TX));
            return eq;
        }

        private void RemoveTxEqualizer()
        {
            var list = (List<Equalizer>)typeof(Radio).GetField("_equalizers", Private)!.GetValue(_f.Vendor.Radio)!;
            lock (list) list.Clear();
        }

        private bool SnapshotFileExists() =>
            (bool)typeof(FlexBase).GetMethod("LiveTxSnapshotFileExists", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { Serial })!;

        private void SaveSnapshotToDisk(AudioChainPreset snapshot)
        {
            bool saved = (bool)typeof(FlexBase).GetMethod("SaveLiveTxSnapshot", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { Serial, snapshot })!;
            Assert.True(saved && SnapshotFileExists(), "the durable snapshot was not written; the test would prove nothing");
        }

        // ── F1: the equaliser's ten writes, each asked ──

        [Fact]
        public void A_carrier_rising_between_the_equalisers_writes_stops_the_rest_and_keeps_the_snapshot()
        {
            Interlock("READY");
            AddTxEqualizer();
            Set(_f.Rig, "_liveTxSnapshot", SnapshotWithEq());
            _f.Vendor.Transport.CommandWritten = c =>
            {
                if (c.StartsWith("eq TXsc 32Hz", StringComparison.Ordinal)) Interlock("TRANSMITTING", Foreign);
            };

            Assert.False(Restore());
            Assert.Single(Commands("eq TXsc"));                    // the first band, and nothing after the carrier
            Assert.NotNull(Get(_f.Rig, "_liveTxSnapshot"));
        }

        [Fact]
        public void The_equaliser_with_the_radio_receiving_throughout_writes_all_ten_and_discharges()
        {
            // The positive control: the same restore reaches every band.
            Interlock("READY");
            AddTxEqualizer();
            Set(_f.Rig, "_liveTxSnapshot", SnapshotWithEq());

            Assert.True(Restore());
            Assert.Equal(10, Commands("eq TXsc").Length);
            Assert.Null(Get(_f.Rig, "_liveTxSnapshot"));
        }

        // ── F2: an equaliser owed and not there ──

        [Fact]
        public void A_snapshot_that_owes_the_equaliser_is_kept_on_disk_when_the_radio_has_none()
        {
            Interlock("READY");
            SaveSnapshotToDisk(SnapshotWithEq());                  // an earlier session's, before this one reported EQ

            Assert.False(Restore());
            Assert.Empty(Commands("eq TXsc"));
            Assert.True(SnapshotFileExists());
        }

        [Fact]
        public void An_equaliser_gone_by_the_time_its_write_runs_leaves_the_restore_incomplete()
        {
            Interlock("READY");
            AddTxEqualizer();
            Set(_f.Rig, "_liveTxSnapshot", SnapshotWithEq());
            _hold = true;
            var run = Background(() => (bool)typeof(FlexBase).GetMethod("RestoreLiveTransmitAudioNow", Private)!.Invoke(_f.Rig, null)!);
            WaitFor(() => _f.Rig.q.Count >= 8, "the restore's writes to queue");
            RemoveTxEqualizer();
            _hold = false;
            Assert.True(run.Thread.Join(8000));

            Assert.False(run.Result());
            Assert.Empty(Commands("eq TXsc"));
            Assert.NotNull(Get(_f.Rig, "_liveTxSnapshot"));
        }

        // ── F4: receive evidence ends with its connection ──

        [Fact]
        public void A_READY_on_a_connection_that_has_ended_confirms_nothing_and_the_restore_keeps_its_debt()
        {
            Interlock("READY");
            Assert.True(_f.Rig.RadioInterlockConfirmsReceive);     // control: a live connection's READY
            Set(_f.Rig, "_liveTxSnapshot", SnapshotWithEq());
            _f.Vendor.Transport.Fall();

            Assert.False(_f.Rig.RadioInterlockConfirmsReceive);
            Assert.Equal("the radio's command connection has ended", _f.Rig.RestoreRefusedAtWrite());
            Assert.False(Restore());
            Assert.NotNull(Get(_f.Rig, "_liveTxSnapshot"));
        }

        [Fact]
        public void A_connection_ending_part_way_through_a_restore_leaves_the_rest_unwritten_and_the_debt_kept()
        {
            Interlock("READY");
            Set(_f.Rig, "_liveTxSnapshot", SnapshotWithEq());
            _f.Vendor.Transport.CommandWritten = c =>
            {
                if (c.StartsWith("transmit set miclevel", StringComparison.Ordinal)) _f.Vendor.Transport.Fall();
            };

            Assert.False(Restore());
            Assert.NotNull(Get(_f.Rig, "_liveTxSnapshot"));
        }

        // ── B6: VOX debt belongs to its connection ──

        private VoxPauseOwner.Lease WithheldVoxRestore()
        {
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
            return lease;
        }

        [Fact]
        public void A_VOX_restore_withheld_on_one_connection_is_never_written_on_the_next_one()
        {
            var old = WithheldVoxRestore();
            _f.Vendor.Transport.Reconnect();                       // the same Radio, a new command connection
            Interlock("READY");
            _f.Vendor.Status("transmit vox_enable=0");

            var fresh = _f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
            Assert.False(_f.Rig.VoxPauses.RestoreOwed(FlexBase.VoxSetting.SimpleVox));
            Assert.False(fresh.Paused);                            // it owes nothing: the old debt did not come with it
            fresh.Release(immediate: true);
            old.Release(immediate: true);
            typeof(FlexBase).GetMethod("DisconnectVoxPauses", Private)!.Invoke(_f.Rig, new object[] { true });
            Drained();

            Assert.Empty(Commands("transmit set vox_enable=1"));
            Assert.Equal(Serial, Get(_f.Rig, "_voxPausedAtDisconnectSerial"));   // remembered for the sentence
        }

        [Fact]
        public void An_old_VOX_lease_released_after_a_reconnect_writes_nothing_even_before_anything_asks_for_the_owner()
        {
            var lease = WithheldVoxRestore();
            Interlock("READY");
            var second = _f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;   // inherits the debt, same connection
            _f.Vendor.Transport.Reconnect();
            Interlock("READY");
            second.Release(immediate: true);                       // the old owner's lease, on the new connection

            Drained();
            Assert.Empty(Commands("transmit set vox_enable=1"));
            _ = lease;
        }

        // ── B5: a send outlived by its connection keys and unkeys nothing ──

        private void OurCarrierUp()
        {
            Interlock("TRANSMITTING");
            Set(_f.Vendor.Radio, "_mox", true);
            Set(_f.Rig, "_Transmit", true);
        }

        [Fact]
        public void A_sends_unkey_after_a_reconnect_does_not_reach_the_carrier_on_the_new_connection()
        {
            var send = new FlexBaseVoiceMessageRig(_f.Rig);         // admitted at the press
            _f.Vendor.Transport.Reconnect();
            Interlock("TRANSMITTING", Foreign);                     // another client's carrier on the returned link
            Set(_f.Vendor.Radio, "_mox", true);

            send.Transmit = false;
            Drained();
            Assert.Empty(Commands("xmit"));
        }

        [Fact]
        public void A_sends_unkey_on_its_own_connection_still_goes_out()
        {
            // The control: the same adapter, nothing changed, unkeys.
            var send = new FlexBaseVoiceMessageRig(_f.Rig);
            OurCarrierUp();
            send.Transmit = false;
            Drained();
            Assert.Single(Commands("xmit 0"));
        }

        [Fact]
        public void A_box_sends_unkey_after_a_reconnect_does_not_reach_the_new_connection_and_on_its_own_it_does()
        {
            var inner = new FlexBaseVoiceMessageRig(_f.Rig);
            using (var box = BoxSendOwnedRig.Own(_f.Rig, inner, "repair four test"))
            {
                _f.Vendor.Transport.Reconnect();
                Interlock("TRANSMITTING", Foreign);
                Set(_f.Vendor.Radio, "_mox", true);
                box.Transmit = false;
                Drained();
                Assert.Empty(Commands("xmit"));
                box.Transmit = true;                                // a key on the new connection is refused too
                Assert.True(box.KeyRefused);
                Drained();
                Assert.Empty(Commands("xmit"));
            }

            using var own = BoxSendOwnedRig.Own(_f.Rig, new FlexBaseVoiceMessageRig(_f.Rig), "repair four control");
            OurCarrierUp();
            own.Transmit = false;
            Drained();
            Assert.Single(Commands("xmit 0"));
        }

        [Fact]
        public void A_send_ended_with_its_connection_gives_the_microphone_back_and_never_unkeys()
        {
            var rig = new RecordingRig();
            var send = VoiceMessageSend.Begin(rig, "test", 0);
            Assert.True(send.WeKeyed);
            send.Tick(100);                                        // it saw its key
            rig.Writes.Clear();

            send.EndWithConnection();
            send.Tick(5000);                                       // no later tick acts
            Assert.True(send.Finished);
            Assert.Equal(VoiceMessageOutcome.Interrupted, send.Outcome);
            Assert.Empty(rig.Writes);
            Assert.True(rig.FileStopped);

            // Control: Stop on a live send does unkey what it keyed.
            var rig2 = new RecordingRig();
            var stopped = VoiceMessageSend.Begin(rig2, "test", 0);
            stopped.Tick(100);
            rig2.Writes.Clear();
            stopped.Stop();
            Assert.Equal(new[] { false }, rig2.Writes);
        }

        private sealed class RecordingRig : IVoiceMessageRig
        {
            private bool _tx;
            public readonly List<bool> Writes = new();
            public bool FileStopped;
            public bool Transmit { get => _tx; set { _tx = value; Writes.Add(value); } }
            public bool FilePlaying => true;
            public bool FileReachedEnd => false;
            public double FileSeconds => 30;
            public void FileStart() { }
            public void FileStartForVox() { }
            public void FileStop() => FileStopped = true;
            public bool VoxOn => false;
        }

        [Fact]
        public void The_drop_and_the_teardown_end_every_send_before_the_key_is_released()
        {
            string main = System.IO.File.ReadAllText(Repo("JJFlexWpf/MainWindow.xaml.cs"));
            int body = main.IndexOf("private void ReleaseKeyForDroppedConnection(bool radioWasKeyed, bool deliberate)", StringComparison.Ordinal);
            Assert.True(body > 0, "ReleaseKeyForDroppedConnection moved");
            int end = main.IndexOf("VoiceMessageSender.EndForConnection(", body, StringComparison.Ordinal);
            int release = main.IndexOf("_pttController.ReleaseForTeardown();", body, StringComparison.Ordinal);
            int check = main.IndexOf("EndCheckForDroppedConnection(", body, StringComparison.Ordinal);
            Assert.True(end > 0 && end < release && end < check, "a send can tick after the drop's release");
        }

        private static string Repo(string relative)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.Path.Combine(dir!.FullName, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }
    }
}
