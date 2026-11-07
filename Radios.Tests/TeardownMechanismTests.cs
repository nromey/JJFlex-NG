using System;
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
    /// Junk box repair round 3 (#596, #861, #600; Codex's second re-check). Round
    /// two applied its rules where the named blocker was, and the re-check found
    /// each broken one route over. These pin the MECHANISMS: one question at
    /// every restoration write, a debt discharged only when its writes ran,
    /// receive meaning a report received, and the check and the send as one
    /// step. The real <see cref="FlexBase"/> on the vendored Radio and its fake
    /// transport, with a command loop that can be HELD, so the radio's state can
    /// change between a write being queued and it going out. What went out is
    /// read off the transport. No socket, no radio, nothing keys.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TeardownMechanismTests : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const uint Foreign = 0x0BADF00D;

        private readonly RadioConfigStaticsScope _scope = new(nameof(TeardownMechanismTests));
        private readonly RigOnVendorRadio _f;
        private readonly Thread _loop;
        private volatile bool _stop;
        private volatile bool _hold;

        public TeardownMechanismTests()
        {
            _f = new RigOnVendorRadio("teardown-mechanism");
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
                        // The production dispatch, not a copy of it.
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

        /// <summary>Run <paramref name="work"/> on its own thread, as the teardown does on the closing thread.</summary>
        private static (Thread Thread, Func<T> Result) Background<T>(Func<T> work)
        {
            T result = default!;
            var t = new Thread(() => result = work()) { IsBackground = true };
            t.Start();
            return (t, () => result);
        }

        private AudioChainPreset Snapshot() => new AudioChainPreset
        {
            MicGain = 77,
            MicBoost = true,
            MicBias = true,
            TxFilterLow = 150,
            TxFilterHigh = 2850,
        };

        // ── Rule 1 and 2: the live transmit-audio restore (#596 N3, #861 R1) ──

        [Fact]
        public void A_carrier_rising_while_the_live_audio_restore_waits_in_the_queue_writes_nothing_and_keeps_the_snapshot()
        {
            // Codex's reproduction: READY when the restore is admitted, a dozen
            // setters queued, then a carrier reported before the queue runs.
            Interlock("READY");
            Set(_f.Rig, "_liveTxSnapshot", Snapshot());
            _hold = true;
            var restore = Background(() => (bool)typeof(FlexBase).GetMethod("RestoreLiveTransmitAudioNow", Private)!.Invoke(_f.Rig, null)!);
            WaitFor(() => _f.Rig.q.Count >= 10, "the restore's setters to queue");
            Interlock("TRANSMITTING", Foreign);
            _hold = false;
            restore.Thread.Join(5000);

            Assert.False(restore.Result());
            Assert.Empty(Commands("transmit set miclevel"));
            Assert.Empty(Commands("mic boost"));
            Assert.Empty(Commands("mic bias"));
            Assert.Empty(Commands("transmit set filter"));
            Assert.NotNull(Get(_f.Rig, "_liveTxSnapshot"));   // the debt is kept
        }

        [Fact]
        public void The_live_audio_restore_with_the_radio_receiving_writes_its_chain_and_discharges_the_snapshot()
        {
            // The positive control: the same restore, receive throughout.
            Interlock("READY");
            Set(_f.Rig, "_liveTxSnapshot", Snapshot());
            var restore = Background(() => (bool)typeof(FlexBase).GetMethod("RestoreLiveTransmitAudioNow", Private)!.Invoke(_f.Rig, null)!);
            restore.Thread.Join(5000);

            Assert.True(restore.Result());
            Assert.Contains(_f.Vendor.Transport.Commands, c => c.Contains("miclevel=77"));
            Assert.Null(Get(_f.Rig, "_liveTxSnapshot"));
        }

        [Fact]
        public void A_selection_withheld_at_its_write_is_a_step_that_did_not_run()
        {
            // The profile half: a step counts only when its writes ran, so a
            // plan stops before the restore point the selection stood on is
            // deleted (Codex's debt-discharge finding).
            Interlock("READY");
            Set(_f.Rig, "_teardownRestoring", true);
            _hold = true;
            var step = Background(() => _f.Rig.RunRestoreStepAtTheWrite("the global profile", () =>
            {
                typeof(FlexBase).GetMethod("ApplyProfileSelection", Private)!.Invoke(_f.Rig,
                    new object[] { _f.Vendor.Radio, ProfileTypes.global, "Owner's Global", false });
                return true;
            }, TimeSpan.FromSeconds(2)));
            WaitFor(() => _f.Rig.q.Count >= 1, "the selection to queue");
            Interlock("TRANSMITTING", Foreign);
            _hold = false;
            step.Thread.Join(5000);

            Assert.Equal(FlexBase.RestoreStep.Withheld, step.Result());
            Assert.Empty(Commands("profile global load"));

            Interlock("READY");                      // control: receive, the same step runs
            var again = Background(() => _f.Rig.RunRestoreStepAtTheWrite("the global profile", () =>
            {
                typeof(FlexBase).GetMethod("ApplyProfileSelection", Private)!.Invoke(_f.Rig,
                    new object[] { _f.Vendor.Radio, ProfileTypes.global, "Owner's Global", false });
                return true;
            }, TimeSpan.FromSeconds(2)));
            again.Thread.Join(5000);
            Assert.Equal(FlexBase.RestoreStep.Ran, again.Result());
            Assert.NotEmpty(Commands("profile global load"));
            Set(_f.Rig, "_teardownRestoring", false);
        }

        [Fact]
        public void Gated_work_that_withholds_itself_counts_as_refused_never_as_run()
        {
            var gate = QueuedWriteGate.Open(() => null);
            Action withheld, ran;
            try
            {
                withheld = gate.Wrap(() => QueuedWriteGate.WithholdRunning("its own check said no"), "a", null);
                ran = gate.Wrap(() => { }, "b", null);
            }
            finally { gate.Dispose(); }
            withheld();
            ran();
            Assert.Equal(1, gate.Refused);
            Assert.Equal(1, gate.Ran);
            Assert.False(QueuedWriteGate.WithholdRunning("nothing gated is running"));
        }

        // ── Rule 1 and 5: VOX's teardown release (#861 R1) ──

        [Fact]
        public void A_VOX_restore_under_a_carrier_writes_nothing_and_stays_owed_until_the_disconnect_can_write_it()
        {
            // Codex's reproduction: a real SimpleVOX pause, OFF drained, READY,
            // then a carrier reported and the lease released immediately, as
            // the reflected-power cut's hold does at teardown.
            _f.AddOwnSlice(0, 14.25, "USB");
            Set(_f.Rig, "_TXVFO", 0);
            Set(_f.Rig, "_IsConnected", true);
            _f.Vendor.Status("transmit vox_enable=1");
            Interlock("READY");
            var lease = _f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
            WaitFor(() => Commands("transmit set vox_enable=0").Length == 1, "VOX off");
            Interlock("TRANSMITTING", Foreign);

            Assert.False(lease.Release(immediate: true));
            Assert.Empty(Commands("transmit set vox_enable=1"));
            Assert.True(_f.Rig.VoxPauses.RestoreOwed(FlexBase.VoxSetting.SimpleVox));

            Interlock("READY");                      // the carrier ends: the receive retries it (integration fix)
            WaitFor(() => Commands("transmit set vox_enable=1").Length == 1, "the restore on the receive report");
            typeof(FlexBase).GetMethod("DisconnectVoxPauses", Private)!.Invoke(_f.Rig, new object[] { true });
            Assert.Single(Commands("transmit set vox_enable=1"));   // and the disconnect does not write it twice
        }

        [Fact]
        public void A_VOX_restore_still_refused_at_the_disconnect_is_remembered_for_the_next_connect()
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
            typeof(FlexBase).GetMethod("DisconnectVoxPauses", Private)!.Invoke(_f.Rig, new object[] { true });

            Assert.Empty(Commands("transmit set vox_enable=1"));
            Assert.Equal("teardown-mechanism", Get(_f.Rig, "_voxPausedAtDisconnectSerial"));
        }

        [Fact]
        public void A_later_pause_inherits_a_withheld_VOX_restore()
        {
            _f.AddOwnSlice(0, 14.25, "USB");
            Set(_f.Rig, "_TXVFO", 0);
            Set(_f.Rig, "_IsConnected", true);
            _f.Vendor.Status("transmit vox_enable=1");
            Interlock("READY");
            var first = _f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
            WaitFor(() => Commands("transmit set vox_enable=0").Length == 1, "VOX off");
            Interlock("TRANSMITTING", Foreign);
            first.Release(immediate: true);          // withheld: VOX stays off, owed
            // The second pause takes the debt while the carrier is still up
            // (integration fix: a READY before it would now retry the restore).
            var second = _f.Rig.VoxPauses.Acquire(FlexBase.VoxSetting.SimpleVox)!;
            Assert.False(_f.Rig.VoxPauses.RestoreOwed(FlexBase.VoxSetting.SimpleVox));   // the pause has it now
            Interlock("READY");
            Assert.True(second.Release(immediate: true));
            Assert.Single(Commands("transmit set vox_enable=1"));
        }

        // ── #596 N4: firmware CW at teardown ──

        private void CwHandedToTheRadio()
        {
            Set(_f.Rig, "cwx", _f.Vendor.Radio.GetCWX());
            Set(_f.Rig, "_Transmit", true);
            Assert.True(_f.Rig.SendCW("CQ TEST"));
            WaitFor(() => Commands("cwx send").Length == 1, "the CW to reach the radio");
            Set(_f.Rig, "_Transmit", false);         // the key report has not come; the radio still says READY
        }

        [Fact]
        public void CW_handed_to_the_radio_is_cleared_at_teardown_even_while_the_radio_reports_receive()
        {
            Interlock("READY");
            CwHandedToTheRadio();
            Assert.True(_f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(500), 100));
            Assert.Single(Commands("cwx clear"));
        }

        [Fact]
        public void The_CW_queue_is_not_cleared_under_another_clients_carrier()
        {
            Interlock("READY");
            CwHandedToTheRadio();
            Interlock("TRANSMITTING", Foreign);
            _f.Rig.UnkeyBeforePuttingBack("test", TimeSpan.FromMilliseconds(500), 50);
            Assert.Empty(Commands("cwx clear"));
        }

        // ── The teardown's tune-off, with the tune really on (Codex's test-strength note) ──

        [Fact]
        public void The_teardown_turns_our_own_tune_carrier_off_and_leaves_another_clients_alone()
        {
            // FlexLib skips an equal-value write, so the negative case means
            // nothing unless the tune cache really reads ON first.
            Interlock("TRANSMITTING");
            _f.Vendor.Status("transmit tune=1");
            Assert.True(_f.Vendor.Radio.TXTune);
            _f.Rig.UnkeyBeforePuttingBack("ours", TimeSpan.FromMilliseconds(500), 50);
            Assert.Single(Commands("transmit tune 0"));

            _f.Vendor.Transport.Written.Clear();
            Interlock("TRANSMITTING", Foreign);
            _f.Vendor.Status("transmit tune=1");
            Assert.True(_f.Vendor.Radio.TXTune);
            _f.Rig.UnkeyBeforePuttingBack("theirs", TimeSpan.FromMilliseconds(500), 50);
            Assert.Empty(Commands("transmit tune"));
        }

        // ── Rule 4: the check and the send are one step ──

        [Fact]
        public void A_revocation_waits_for_a_key_on_already_past_its_check_and_nothing_admitted_before_it_sends_after()
        {
            Interlock("READY");
            using var inWrite = new ManualResetEventSlim();
            using var letGo = new ManualResetEventSlim();
            _f.Vendor.Transport.CommandWritten = c =>
            {
                if (c == "xmit 1" && !letGo.IsSet) { inWrite.Set(); letGo.Wait(5000); }
            };
            _f.Rig.Transmit = true;                  // admitted, and its write is now held mid-send
            Assert.True(inWrite.Wait(5000), "the key-on never reached its write");

            var revoke = Background(() => { _f.Rig.RevokeAdmittedTransmitWork("test"); return true; });
            Assert.False(revoke.Thread.Join(200), "the revocation returned while an admitted send was part-way out");
            letGo.Set();
            Assert.True(revoke.Thread.Join(5000));

            // And a key-on admitted before the revocation, reaching the loop after it, sends nothing.
            _f.Vendor.Transport.CommandWritten = null;
            _hold = true;
            _f.Rig.Transmit = true;
            _f.Rig.RevokeAdmittedTransmitWork("test again");
            _hold = false;
            WaitFor(() => _f.Rig.q.Count == 0, "the queue to drain");
            Thread.Sleep(50);
            Assert.Single(Commands("xmit 1"));
        }

        [Fact]
        public void CW_queued_before_a_drop_is_dropped_by_the_production_dispatch_and_CW_after_it_is_sent()
        {
            Interlock("READY");
            Set(_f.Rig, "cwx", _f.Vendor.Radio.GetCWX());
            Set(_f.Rig, "_Transmit", true);
            _hold = true;
            Assert.True(_f.Rig.SendCW("CQ OLD"));
            Assert.True(_f.Rig.SendCW('K'));
            _f.Rig.RevokeAdmittedTransmitWork("the connection fell");
            _hold = false;
            WaitFor(() => _f.Rig.q.Count == 0, "the queue to drain");
            Assert.Empty(Commands("cwx send"));

            Assert.True(_f.Rig.SendCW("CQ NEW"));     // control: fresh work after the fall is sent
            WaitFor(() => Commands("cwx send").Length == 1, "the fresh CW");
            Set(_f.Rig, "_Transmit", false);
        }

        // ── Rule 3: receive means a report received (#861 R3) ──

        [Fact]
        public void A_radio_that_has_reported_nothing_does_not_confirm_receive_and_its_mic_put_back_is_withheld()
        {
            // Codex's reproduction: no interlock status at all, and FlexLib's
            // cache reads Ready. Built without the fixture's connect-time READY.
            var vendor = new VendorRadioFixture("never-reported");
            Assert.True(InterlockReports.Watch(vendor.Radio));
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
            try
            {
                Set(rig, "theRadio", vendor.Radio);
                Assert.Equal(InterlockState.Ready, vendor.Radio.InterlockState);
                Assert.False(rig.RadioInterlockConfirmsReceive);
                Assert.NotNull(rig.RestoreRefusedAtWrite(vendor.Radio, vendor.Radio.CurrentCommandConnection!));

                vendor.Status("interlock state=READY");   // the radio says so: confirmed
                Assert.True(rig.RadioInterlockConfirmsReceive);
                Assert.Null(rig.RestoreRefusedAtWrite(vendor.Radio, vendor.Radio.CurrentCommandConnection!));

                vendor.Status("interlock state=SOMETHING_NEW");   // FlexLib keeps Ready; we do not
                Assert.Equal(InterlockState.Ready, vendor.Radio.InterlockState);
                Assert.False(rig.RadioInterlockConfirmsReceive);
            }
            finally
            {
                Set(rig, "theRadio", null);
                try { rig.Dispose(); } catch { }
            }
        }

        [Fact]
        public void The_report_watch_reaches_the_transport_of_a_radio_built_the_way_FlexLib_builds_one()
        {
            // The one reflection: a FlexLib upgrade that moves the field must
            // fail here, not withhold every put-back in the field.
            var radio = (Radio)Activator.CreateInstance(typeof(Radio), Private, null, new object[] { false }, null)!;
            Assert.True(InterlockReports.Watch(radio));
            Assert.Null(InterlockReports.LastOn(radio.CurrentCommandConnection));
        }

        [Fact]
        public void A_report_from_an_earlier_connection_says_nothing_about_this_one()
        {
            Interlock("READY");
            Assert.True(_f.Rig.RadioInterlockConfirmsReceive);
            var fresh = new FakeCommandTransport();   // a reconnect: a new connection
            typeof(Radio).GetField("_commandCommunication", Private)!.SetValue(_f.Vendor.Radio, fresh);
            Assert.False(_f.Rig.RadioInterlockConfirmsReceive);
        }

        // ── Rule 5: every route by which the connection ends unkeys through one scoped path ──

        private static string Repo(string relative)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, relative));
        }

        private static int Count(string s, string what)
        {
            int n = 0, at = 0;
            while ((at = s.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
            return n;
        }

        [Fact]
        public void A_check_ended_because_the_connection_ends_unkeys_only_its_own_carrier()
        {
            // #596 N1 (Codex's second re-check): on a drop, ending an active
            // check ran BEFORE the controller's own-carrier release and queued
            // the ordinary xmit 0. Every connection-ending route now passes
            // connectionEnding, and that branch releases through the scoped path.
            string check = Repo("JJFlexWpf/Dialogs/CheckYourAudioDialog.Check.cs");
            int force = check.IndexOf("public void ForceEnd(string? reason, bool connectionEnding = false)", StringComparison.Ordinal);
            int keyed = check.IndexOf("if (OwnKeyHeld)", force, StringComparison.Ordinal);
            string release = check.Substring(keyed, 900);
            Assert.Contains("if (connectionEnding) _ptt.ReleaseForTeardown();", release);
            Assert.Contains("else _ptt.EscapeUnlock();", release);
            int gone = check.IndexOf("audio.check.radio_disconnected", StringComparison.Ordinal);
            Assert.True(gone > 0);
            Assert.Contains("connectionEnding: true);", check.Substring(gone, 60));

            string dialog = Repo("JJFlexWpf/Dialogs/CheckYourAudioDialog.cs");
            Assert.Contains("ForceEnd(\"Radio connection dropped, audio check ended\", connectionEnding: true);", dialog);
            Assert.Contains("ForceEnd(\"Radio disconnected, audio check ended\", connectionEnding: true);", dialog);
            int changed = dialog.IndexOf("\"Radio changed, audio check ended\",", StringComparison.Ordinal);
            Assert.True(changed > 0);
            Assert.Contains("connectionEnding: true);", dialog.Substring(changed, 120));
            // The window closing and Escape keep the ordinary unkey.
            Assert.Contains("_session?.ForceEnd(\"Audio check ended\");", dialog);
            Assert.Contains("_session?.ForceEnd(null);", dialog);
        }

        [Fact]
        public void Homes_lock_key_up_records_the_stream_the_drop_reads()
        {
            // Codex's N2 test-strength note: the replay tests call
            // NoteLockSpaceUp directly, so this pins that the real key-up does.
            string main = Repo("JJFlexWpf/MainWindow.xaml.cs");
            int up = main.IndexOf("private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)", StringComparison.Ordinal);
            int next = main.IndexOf("    private ", up + 10, StringComparison.Ordinal);
            Assert.True(up > 0 && next > up);
            Assert.Equal(1, Count(main.Substring(up, next - up), "NoteLockSpaceUp(Environment.TickCount64);"));
        }

        [Fact]
        public void The_connect_path_watches_the_radio_before_it_connects()
        {
            string src = FlexBasePartials.JoinedText();
            int find = src.IndexOf("theRadio = findRadioForConnect(serial, preferWanPath);", StringComparison.Ordinal);
            int watch = src.IndexOf("InterlockReports.Watch(theRadio);", find, StringComparison.Ordinal);
            int connect = src.IndexOf("theRadio.Connect(", find, StringComparison.Ordinal);
            Assert.True(find > 0 && watch > find && connect > watch, "the connect does not watch the radio's reports before it connects");
        }
    }
}
