using System;
using System.Collections.Generic;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The network test belongs to the radio's own brokering account, never to
    /// whatever session is active (the tiers plan, section 5; settled for
    /// Track C); its gate keeps testing without a connected radio switched off;
    /// and a window following a radio's connect record re-reads it rather
    /// than trusting the order of the store's callbacks.
    /// </summary>
    public sealed class NetworkDiagnosticPairingTests
    {
        private static bool AllSignedIn(string _) => true;

        // ── Which account ───────────────────────────────────────────────

        [Fact]
        public void The_account_comes_from_the_brokering_facts_in_their_order()
        {
            Assert.Equal("plan@x", NetworkDiagnosticPairing.Resolve("plan@x", "owner@x", "list@x", "last@x", AllSignedIn).Account);
            Assert.Equal("owner@x", NetworkDiagnosticPairing.Resolve(null, "owner@x", "list@x", "last@x", AllSignedIn).Account);
            Assert.Equal("list@x", NetworkDiagnosticPairing.Resolve("", "", "list@x", "last@x", AllSignedIn).Account);
            Assert.Equal("last@x", NetworkDiagnosticPairing.Resolve(null, null, null, "last@x", AllSignedIn).Account);
        }

        [Fact]
        public void With_nothing_naming_the_account_there_is_no_pairing_and_no_fallback()
        {
            // The connect lookup's last step is "else the active session"; the
            // diagnostic has no such step, so even a signed-in world answers None.
            var p = NetworkDiagnosticPairing.Resolve(null, "", " ", null, AllSignedIn);
            Assert.Equal(DiagnosticPairingKind.NoBrokeringAccount, p.Kind);
            Assert.Equal("", p.Account);
            Assert.False(p.IsPaired);
        }

        [Fact]
        public void A_known_account_that_is_not_signed_in_is_said_and_never_swapped_for_another()
        {
            var asked = new List<string>();
            var p = NetworkDiagnosticPairing.Resolve(null, "owner@x", "list@x", null, a => { asked.Add(a); return false; });
            Assert.Equal(DiagnosticPairingKind.AccountNotSignedIn, p.Kind);
            Assert.Equal("owner@x", p.Account);
            // Only the brokering account is asked about: a signed-in other account is not tried.
            Assert.Equal(new[] { "owner@x" }, asked);
        }

        [Fact]
        public void A_signed_in_account_is_paired()
        {
            var p = NetworkDiagnosticPairing.Resolve(null, "owner@x", null, null, a => a == "owner@x");
            Assert.True(p.IsPaired);
        }

        // ── The gate ────────────────────────────────────────────────────

        [Fact]
        public void Testing_without_a_connected_radio_is_built_but_switched_off()
        {
            Assert.False(NetworkTestGate.OfferedWithoutAConnectedRadio,
                "Testing a radio with none of ours connected waits on Noel's ruling (the probe is radio-wide).");
            Assert.Equal(NetworkTestGateAnswer.NeedsAConnectedRadio, NetworkTestGate.Decide(radioConnected: false, "S-1"));
            Assert.Equal(NetworkTestGateAnswer.Run, NetworkTestGate.Decide(radioConnected: true, "S-1"));
        }

        [Theory]
        [InlineData(true, "S-1", false, NetworkTestGateAnswer.Run)]
        [InlineData(true, "S-1", true, NetworkTestGateAnswer.Run)]
        [InlineData(false, "S-1", false, NetworkTestGateAnswer.NeedsAConnectedRadio)]
        [InlineData(false, "S-1", true, NetworkTestGateAnswer.Run)]
        [InlineData(false, "", true, NetworkTestGateAnswer.NoRadio)]
        [InlineData(true, null, false, NetworkTestGateAnswer.NoRadio)]
        public void The_gate_built_for_the_day_it_is_switched_on(bool connected, string serial, bool offered, NetworkTestGateAnswer expected)
        {
            Assert.Equal(expected, NetworkTestGate.Decide(connected, serial, offered));
        }

        // ── Following the record, out of order ──────────────────────────

        private static readonly RadioPortReport Ports = new(
            new AdvertisedPortPair(PortPairSource.Manual, 4994, 4993, true),
            new AdvertisedPortPair(PortPairSource.Upnp, -1, -1, false), false, true);

        private static SmartLinkConnectRecord Begin(string serial)
        {
            var input = new SmartLinkPolicyInput(SmartLinkConnectionMode.ManualPortForwardOnly, true, RadioConnectionPreference.Auto, false, Ports);
            return SmartLinkConnectRecord.Begin(serial, "owner@x", input, SmartLinkMethodPolicy.Decide(input), DateTime.UtcNow);
        }

        private static string UniqueSerial() => "DIAG-" + Guid.NewGuid().ToString("N");

        [Fact]
        public void A_late_callback_about_an_older_connect_shows_the_newest_record()
        {
            string serial = UniqueSerial();
            var older = Begin(serial);
            var newer = Begin(serial);
            Assert.True(SmartLinkConnectRecords.Store(newer));

            var shown = new List<SmartLinkConnectRecord>();
            using var follower = new ConnectRecordFollower(() => serial, run => run(), r => shown.Add(r), subscribe: false);
            // The older connect's callback arrives last, as the store's
            // callbacks can, on a different thread from the newer one's.
            follower.OnChanged(older);

            Assert.Single(shown);
            Assert.Same(newer, shown[0]);
        }

        [Fact]
        public void The_record_is_read_when_the_window_runs_the_refresh_not_when_the_callback_came()
        {
            string serial = UniqueSerial();
            var first = Begin(serial);
            Assert.True(SmartLinkConnectRecords.Store(first));

            var posted = new List<Action>();
            var shown = new List<SmartLinkConnectRecord>();
            using var follower = new ConnectRecordFollower(() => serial, posted.Add, r => shown.Add(r), subscribe: false);
            follower.OnChanged(first);

            // A newer connect lands before the window's thread gets to run it.
            var second = Begin(serial).WithAttempt(SmartLinkAttemptRecord.ForPorts(SmartLinkMethod.ManualPorts, Ports.Manual));
            Assert.True(SmartLinkConnectRecords.Store(second));
            Assert.Single(posted);
            posted[0]();

            Assert.Same(second, shown[0]);
        }

        [Fact]
        public void Another_radios_record_does_not_refresh_this_one_and_a_closed_window_hears_nothing()
        {
            string serial = UniqueSerial();
            var posted = new List<Action>();
            var follower = new ConnectRecordFollower(() => serial, posted.Add, _ => { }, subscribe: false);
            follower.OnChanged(Begin(UniqueSerial()));
            Assert.Empty(posted);

            follower.OnChanged(Begin(serial));
            Assert.Single(posted);
            follower.Dispose();
            follower.OnChanged(Begin(serial));
            Assert.Single(posted);
        }

        [Fact]
        public void The_follower_hears_the_real_store()
        {
            string serial = UniqueSerial();
            var shown = new List<SmartLinkConnectRecord>();
            using (var follower = new ConnectRecordFollower(() => serial, run => run(), r => shown.Add(r)))
            {
                var rec = Begin(serial);
                Assert.True(SmartLinkConnectRecords.Store(rec));
                Assert.Same(rec, Assert.Single(shown));
            }
            // Disposed: unsubscribed from the store.
            Assert.True(SmartLinkConnectRecords.Store(Begin(serial)));
            Assert.Single(shown);
        }
    }
}
