#nullable enable
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Check your audio #835, fix round 2026-10-30, track Sends. Behaviour is
    /// driven where it lives in this assembly (the lock's timeout clock, the
    /// monitor waiting for the unkey on a real FlexBase over a vendored radio);
    /// the UI assembly's wiring is pinned from source and stated as pins, the
    /// way <see cref="SendFixRoundTests"/> does.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SendsRound1030Tests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(SendsRound1030Tests));
        public void Dispose() => _scope.Dispose();

        // ── Item 4: a recording riding the lock is not cut at its timeout ──────

        /// <summary>
        /// Noel, 2026-10-30 00:00: the send's own ten-minute limit governs a
        /// recording riding the lock, not the lock's three minutes. The clock
        /// stands still while the recording goes out and carries on from where
        /// it stood: the operator's own time by hand is counted exactly as before.
        /// </summary>
        [Fact]
        public void The_lock_clock_stands_still_while_a_recording_rides_it_and_carries_on_after()
        {
            var clock = new LockTimeoutClock();
            clock.Start(0);
            Assert.Equal(60, clock.ElapsedSeconds(60_000), 3);       // a minute by hand

            Assert.True(clock.BeginSendRide(60_000));
            Assert.True(clock.SendRiding);
            Assert.Equal(60, clock.ElapsedSeconds(600_000), 3);      // nine minutes of bulletin count for nothing

            Assert.True(clock.EndSendRide(600_000));
            Assert.False(clock.SendRiding);
            Assert.Equal(60, clock.ElapsedSeconds(600_000), 3);      // no fresh timeout either
            Assert.Equal(90, clock.ElapsedSeconds(630_000), 3);      // and the operator's time runs again
        }

        [Fact]
        public void Every_ride_is_left_out_and_a_ride_cannot_begin_or_end_twice()
        {
            var clock = new LockTimeoutClock();
            clock.Start(1_000);
            Assert.True(clock.BeginSendRide(11_000));
            Assert.False(clock.BeginSendRide(12_000));
            Assert.True(clock.EndSendRide(41_000));                  // 30 s ridden
            Assert.False(clock.EndSendRide(42_000));
            Assert.True(clock.BeginSendRide(51_000));                // paused at ten minutes, carried on
            Assert.True(clock.EndSendRide(71_000));                  // 20 s ridden
            Assert.Equal(20, clock.ElapsedSeconds(71_000), 3);       // 70 s of lock, 50 s of it riding
        }

        /// <summary>
        /// A recording already riding a held push to talk when the operator
        /// locks rides the lock from the moment it began; and an unlock leaves
        /// the ride for the send to end, so a lock begun later still sees it.
        /// </summary>
        [Fact]
        public void A_ride_that_began_before_the_lock_rides_it_from_the_lock_and_survives_an_unlock()
        {
            var clock = new LockTimeoutClock();
            Assert.Equal(0, clock.ElapsedSeconds(5_000));            // not locked
            Assert.True(clock.BeginSendRide(5_000));
            clock.Start(20_000);
            Assert.Equal(0, clock.ElapsedSeconds(80_000), 3);
            clock.Reset();
            Assert.True(clock.SendRiding);
            clock.Start(100_000);
            Assert.Equal(0, clock.ElapsedSeconds(130_000), 3);
            Assert.True(clock.EndSendRide(130_000));
            Assert.Equal(10, clock.ElapsedSeconds(140_000), 3);
        }

        /// <summary>
        /// Pins (the controller and the send host live in the UI assembly):
        /// the warning ladder stands down while a recording rides, the
        /// fifteen-minute hard kill is untouched by the ride, and every send
        /// riding the operator's transmission tells the controller when it
        /// begins and when it ends.
        /// </summary>
        [Fact]
        public void The_controller_stands_its_ladder_down_for_a_riding_recording_and_the_hard_kill_stays()
        {
            string ctl = SendFixRoundTests.ReadSource("JJFlexWpf/PttSafetyController.cs");
            string tick = SendFixRoundTests.Body(ctl, "private void WarningTimerTick(");
            int ride = tick.IndexOf("if (_lockClock.SendRiding)", StringComparison.Ordinal);
            int timeout = tick.IndexOf("EnterHardKillFromTimeout()", StringComparison.Ordinal);
            Assert.True(ride >= 0 && timeout > ride, "the ladder stands down for a riding recording before it can time out");
            Assert.Contains("_lockClock.ElapsedSeconds(", tick);
            Assert.DoesNotContain("_lockStartTime", ctl);

            string hardKill = SendFixRoundTests.Body(ctl, "private void StartHardKillTimer()");
            Assert.DoesNotContain("_lockClock", hardKill);
            Assert.DoesNotContain("SendRiding", hardKill);

            string sender = SendFixRoundTests.ReadSource("JJFlexWpf/VoiceMessageSender.cs");
            string launch = SendFixRoundTests.Body(sender, "private static void Launch(");
            Assert.Contains("if (!_send.WeKeyed && !_send.ViaVox && !_send.Boxed && ptt != null)", launch);
            Assert.Contains("ptt.BeginRecordingSendRide(Name(info), () => ReferenceEquals(_send, riding) && !riding.Finished);", launch);
            string settle = SendFixRoundTests.Body(sender, "private static void Settle()");
            Assert.Contains("_riddenBy.EndRecordingSendRide(", settle);
        }

        // ── Item 2: after a takeover the monitor waits for the unkey ────────────

        private sealed class Lease : IDisposable
        {
            private int _disposed;
            public bool Disposed => Volatile.Read(ref _disposed) != 0;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        }

        private static void SetTransmit(FlexBase rig, bool on) =>
            typeof(FlexBase).GetField("_Transmit", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(rig, on);

        private static async Task<bool> Within(Func<bool> condition, int ms)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            return condition();
        }

        /// <summary>
        /// Noel, 2026-10-29 23:57: after a push-to-talk takeover of a box send,
        /// the monitor goes back off when the operator UNKEYS, not at the
        /// takeover. The hold is let go only when the radio reports receive.
        /// </summary>
        [Fact]
        public async Task A_taken_over_sends_monitor_hold_ends_only_when_the_radio_reports_receive()
        {
            using var f = new RigOnVendorRadio("sends-1030-monitor");
            var hold = new Lease();
            SetTransmit(f.Rig, true);

            f.Rig.ReleaseMonitorHoldAtUnkey(hold, "the computer recording bench");
            Assert.False(await Within(() => hold.Disposed, 300), "still transmitting: the monitor stays as the send left it");

            SetTransmit(f.Rig, false);
            Assert.True(await Within(() => hold.Disposed, 2_000), "the operator unkeyed: the send's hold ends");
        }

        [Fact]
        public async Task Not_transmitting_or_not_connected_the_hold_ends_at_once()
        {
            using var f = new RigOnVendorRadio("sends-1030-monitor-now");
            var idle = new Lease();
            f.Rig.ReleaseMonitorHoldAtUnkey(idle, "a send");
            Assert.True(idle.Disposed, "receiving already: nothing to wait for");

            var gone = new Lease();
            SetTransmit(f.Rig, true);
            f.Rig.ReleaseMonitorHoldAtUnkey(gone, "a send");
            Assert.False(gone.Disposed);
            typeof(Flex.Smoothlake.FlexLib.Radio).GetProperty(nameof(Flex.Smoothlake.FlexLib.Radio.Connected))!
                .GetSetMethod(nonPublic: true)!.Invoke(f.Vendor.Radio, new object[] { false });
            Assert.True(await Within(() => gone.Disposed, 2_000),
                "the connection went away: the hold ends, and the monitor owner records the put-back as before");
            SetTransmit(f.Rig, false);
        }

        // ── Codex round 1030, B5: "receive" is the radio's word, not our cache ──

        private static void PlantNewConnection(RigOnVendorRadio f) =>
            typeof(Flex.Smoothlake.FlexLib.Radio).GetField("_commandCommunication", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(f.Vendor.Radio, new FakeCommandTransport());

        /// <summary>
        /// Codex's schedule (round 1030, B5): after a hardware takeover the
        /// software key is dropped, so our cached Transmit is already false
        /// while the radio's interlock still reports TRANSMITTING. The hold
        /// must stay until the radio itself reports receive, and go once it
        /// does (Noel, 2026-10-29 23:57: wait until unkey).
        /// </summary>
        [Fact]
        public async Task Cached_receive_is_not_enough_the_hold_waits_for_the_interlock_to_leave_transmit()
        {
            using var f = new RigOnVendorRadio("sends-1030-monitor-interlock");
            SetTransmit(f.Rig, false);
            f.Vendor.Status("interlock state=TRANSMITTING source=RCA");
            Assert.True(f.Rig.RadioInterlockReportsTransmit, "positive control: the fixture's interlock report reached the radio");

            var hold = new Lease();
            f.Rig.ReleaseMonitorHoldAtUnkey(hold, "the computer recording bench");
            Assert.False(await Within(() => hold.Disposed, 300),
                "cached Transmit is false but the radio still transmits: the monitor stays as the send left it");

            f.Vendor.Status("interlock state=UNKEY_REQUESTED source=RCA");
            Assert.False(await Within(() => hold.Disposed, 200), "on its way out of transmit is still transmit");

            f.Vendor.Status("interlock state=READY");
            Assert.True(await Within(() => hold.Disposed, 2_000), "the radio reports receive: the send's hold ends");
        }

        /// <summary>
        /// The other half: the radio's interlock reads ready but our own key is
        /// still down. Both must say receive before the hold goes.
        /// </summary>
        [Fact]
        public async Task Interlock_ready_while_our_cache_still_transmits_the_hold_waits_for_both()
        {
            using var f = new RigOnVendorRadio("sends-1030-monitor-both");
            f.Vendor.Status("interlock state=READY");
            SetTransmit(f.Rig, true);
            var hold = new Lease();
            f.Rig.ReleaseMonitorHoldAtUnkey(hold, "a send");
            Assert.False(await Within(() => hold.Disposed, 300));
            SetTransmit(f.Rig, false);
            Assert.True(await Within(() => hold.Disposed, 2_000));
        }

        /// <summary>
        /// Pinned to the connection it was taken on: a new connection is the
        /// old one gone, so the hold ends (the monitor owner records the
        /// put-back as for any lost connection), even though the radio object
        /// still reads connected and the interlock still reads transmitting.
        /// </summary>
        [Fact]
        public async Task A_replaced_connection_ends_the_wait_as_a_disconnect_does()
        {
            using var f = new RigOnVendorRadio("sends-1030-monitor-reconnect");
            SetTransmit(f.Rig, false);
            f.Vendor.Status("interlock state=TRANSMITTING source=RCA");
            var hold = new Lease();
            f.Rig.ReleaseMonitorHoldAtUnkey(hold, "a send");
            Assert.False(await Within(() => hold.Disposed, 300));

            PlantNewConnection(f);
            Assert.True(await Within(() => hold.Disposed, 2_000), "the connection the hold was taken on is gone");
        }

        /// <summary>
        /// B4's connection identity, which the speaker playback's carry-on
        /// compares across its wait (the UI test stands it in): stable while
        /// the connection lasts, a different object on a new connection, and
        /// null with none.
        /// </summary>
        [Fact]
        public void The_live_connection_identity_changes_with_the_connection_and_is_null_without_one()
        {
            using var f = new RigOnVendorRadio("sends-1030-connection-identity");
            object? first = f.Rig.LiveConnectionIdentity;
            Assert.NotNull(first);
            Assert.Same(first, f.Rig.LiveConnectionIdentity);

            PlantNewConnection(f);
            object? second = f.Rig.LiveConnectionIdentity;
            Assert.NotNull(second);
            Assert.NotSame(first, second);

            typeof(Flex.Smoothlake.FlexLib.Radio).GetProperty(nameof(Flex.Smoothlake.FlexLib.Radio.Connected))!
                .GetSetMethod(nonPublic: true)!.Invoke(f.Vendor.Radio, new object[] { false });
            Assert.Null(f.Rig.LiveConnectionIdentity);
        }

        /// <summary>
        /// Pin: the send host hands a taken-over piece's monitor to the wait,
        /// and every other ending lets it go at once as before.
        /// </summary>
        [Fact]
        public void The_send_host_keeps_the_monitor_until_the_unkey_only_after_a_takeover()
        {
            string sender = SendFixRoundTests.ReadSource("JJFlexWpf/VoiceMessageSender.cs");
            string settle = SendFixRoundTests.Body(sender, "private static void Settle()");
            Assert.Contains("ReleaseHolds(info, takenOver: send?.Outcome == VoiceMessageOutcome.TakenOver);", settle);
            string release = SendFixRoundTests.Body(sender, "private static void ReleaseHolds(");
            Assert.Contains("if (takenOver && info.Rig != null) holds.ReleaseAtUnkey(info.Rig, Name(info));", release);
            Assert.Contains("else holds.Dispose();", release);
            string atUnkey = SendFixRoundTests.Body(sender, "public void ReleaseAtUnkey(");
            Assert.Contains("rig.ReleaseMonitorHoldAtUnkey(monitor, who);", atUnkey);
            Assert.Contains("_pttWatch", atUnkey);
        }

        // ── Item 1: Enter in Recorded QSOs, keyed, sends ────────────────────────

        /// <summary>
        /// Pin: Enter's send uses the route the press read, the same one Play
        /// from computer reads, rather than a route of its own.
        /// </summary>
        [Fact]
        public void Enter_sends_on_the_route_Play_from_computer_reads()
        {
            string dialog = SendFixRoundTests.ReadSource("JJFlexWpf/Dialogs/RecordedQsosDialog.cs");
            string choose = SendFixRoundTests.Body(dialog, "internal static EnterAct ChooseEnter(");
            Assert.Contains("if (route is SendRoute.Box or SendRoute.JoinTransmission) return EnterAct.Send;", choose);
            string send = SendFixRoundTests.Body(dialog, "private void SendSelected(");
            Assert.Contains("doors.SendComputerRecording(_rig!, q.Path, route);", send);
            Assert.DoesNotContain("SendRoute.Box);", send);

            string controls = SendFixRoundTests.ReadSource("JJFlexWpf/RecorderControls.cs");
            string play = SendFixRoundTests.Body(controls, "public static async Task<RecorderOutcome> ToggleComputerPlayAsync(");
            Assert.Contains("if (route != SendRoute.AsBefore)", play);
        }
    }
}
