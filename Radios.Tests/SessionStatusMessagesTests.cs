#nullable enable

using System;
using Radios;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 27 Track D / Phase D.1. Verifies <see cref="SessionStatusMessages"/>
    /// resolves the right failure-class overlay for each (status, report, mode)
    /// combination listed in the sprint plan. Also pins
    /// <see cref="SessionStatusMessages.HelpDocFor"/> outputs so Track D.3's
    /// UI buttons can trust the filename mapping.
    /// </summary>
    public class SessionStatusMessagesTests
    {
        private static NetworkDiagnosticReport ReportWith(
            bool? upnpTcp = null, bool? upnpUdp = null,
            bool? manualTcp = null, bool? manualUdp = null,
            bool? holePunch = null,
            string? errorDetail = null)
        {
            return new NetworkDiagnosticReport
            {
                RadioSerial = "1234-5678",
                TimestampUtc = new DateTime(2026, 4, 20, 14, 0, 0, DateTimeKind.Utc),
                UpnpTcpReachable = upnpTcp,
                UpnpUdpReachable = upnpUdp,
                ManualForwardTcpReachable = manualTcp,
                ManualForwardUdpReachable = manualUdp,
                NatSupportsHolePunch = holePunch,
                ErrorDetail = errorDetail,
            };
        }

        // --- ForStatus (existing behavior — regression-guard) ---

        [Fact]
        public void ForStatus_Connected_Short()
        {
            Assert.Equal("Connected", SessionStatusMessages.ForStatus(SessionStatus.Connected));
        }

        [Fact]
        public void ForStatus_AuthExpired_IsSelfExplanatory()
        {
            Assert.Contains("Authorization expired", SessionStatusMessages.ForStatus(SessionStatus.AuthorizationExpired));
        }

        // --- ForStatusRich overlays ---

        [Fact]
        public void ForStatusRich_Connected_NoOverlay()
        {
            // Positive states don't get a failure post-mortem even if a
            // NetworkTest report is available.
            string msg = SessionStatusMessages.ForStatusRich(
                SessionStatus.Connected,
                networkReport: ReportWith(manualTcp: true, manualUdp: true));
            Assert.Equal("Connected", msg);
        }

        [Fact]
        public void ForStatusRich_Reconnecting_NoReport_ShortForm()
        {
            // No probe data + non-verbose = base message only, no overlay.
            string msg = SessionStatusMessages.ForStatusRich(
                SessionStatus.Reconnecting, reconnectAttemptCount: 2);
            Assert.Contains("Reconnecting", msg);
            Assert.DoesNotContain("Last error", msg);
        }

        [Fact]
        public void ForStatusRich_Reconnecting_Verbose_AppendsErrorMessage()
        {
            string msg = SessionStatusMessages.ForStatusRich(
                SessionStatus.Reconnecting,
                reconnectAttemptCount: 2,
                lastError: new InvalidOperationException("TLS handshake failed"),
                verbose: true);
            Assert.Contains("TLS handshake failed", msg);
        }

        [Fact]
        public void ForStatusRich_Disconnected_ProbeTimedOut_SaysUnreachable()
        {
            string msg = SessionStatusMessages.ForStatusRich(
                SessionStatus.Disconnected,
                networkReport: ReportWith(errorDetail: "Timed out after 30 seconds"));
            Assert.Contains("unreachable", msg);
            Assert.Contains("check your internet", msg);
        }

        // The tiers work, Track C: these two pinned the untrue overlays ("Your
        // NAT appears symmetric ... Manual port forwarding is the reliable
        // path" and "UPnP mapping didn't take — falling back to your manual
        // port forward"). The replacements say what the radio's site measured,
        // never "your NAT" and never a fallback JJ Flexible does not do.

        [Fact]
        public void ForStatusRich_Disconnected_NoPunchAtTheRadiosSiteWithTier3_SaysSo()
        {
            string msg = SessionStatusMessages.ForStatusRich(
                SessionStatus.Disconnected,
                networkReport: ReportWith(
                    manualTcp: true, manualUdp: true,
                    upnpTcp: true, upnpUdp: true,
                    holePunch: false),
                mode: SmartLinkConnectionMode.AutomaticHolePunch);
            Assert.Contains(Lexicon.Get("connect.session_overlay.radio_site_no_punch"), msg);
            Assert.DoesNotContain("symmetric", msg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Your NAT", msg, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ForStatusRich_Disconnected_UpnpUnreachableWithTier2_SaysSoAndClaimsNoFallback()
        {
            string msg = SessionStatusMessages.ForStatusRich(
                SessionStatus.Reconnecting,
                networkReport: ReportWith(
                    manualTcp: true, manualUdp: true,
                    upnpTcp: false, upnpUdp: false),
                mode: SmartLinkConnectionMode.ManualPlusUpnp);
            Assert.Contains(Lexicon.Get("connect.session_overlay.radio_site_upnp_unreachable"), msg);
            Assert.DoesNotContain("UPnP mapping didn't take", msg);
            Assert.DoesNotContain("falling back", msg, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ForStatusRich_WithTheRadiosForwardingOn_NeitherTheUpnpNorThePunchOverlayApplies()
        {
            // A connect dials the forwarded ports first when the radio's
            // forwarding is on, at every tier.
            string punch = SessionStatusMessages.ForStatusRich(SessionStatus.Reconnecting,
                networkReport: ReportWith(manualTcp: true, manualUdp: true, holePunch: false),
                mode: SmartLinkConnectionMode.AutomaticHolePunch, radioForwardingOn: true);
            string upnp = SessionStatusMessages.ForStatusRich(SessionStatus.Reconnecting,
                networkReport: ReportWith(manualTcp: true, manualUdp: true, upnpTcp: false, upnpUdp: false),
                mode: SmartLinkConnectionMode.ManualPlusUpnp, radioForwardingOn: true);
            Assert.DoesNotContain(Lexicon.Get("connect.session_overlay.radio_site_no_punch"), punch);
            Assert.DoesNotContain(Lexicon.Get("connect.session_overlay.radio_site_upnp_unreachable"), upnp);
            // Forwarding known off keeps them.
            Assert.Contains(Lexicon.Get("connect.session_overlay.radio_site_no_punch"),
                SessionStatusMessages.ForStatusRich(SessionStatus.Reconnecting,
                    networkReport: ReportWith(holePunch: false),
                    mode: SmartLinkConnectionMode.AutomaticHolePunch, radioForwardingOn: false));
        }

        [Fact]
        public void ForStatusRich_Tier1_NeverSpeaksTheUpnpOrPunchOverlay()
        {
            string msg = SessionStatusMessages.ForStatusRich(SessionStatus.Reconnecting,
                networkReport: ReportWith(upnpTcp: false, upnpUdp: false, holePunch: false),
                mode: SmartLinkConnectionMode.ManualPortForwardOnly, radioForwardingOn: false);
            Assert.DoesNotContain(Lexicon.Get("connect.session_overlay.radio_site_no_punch"), msg);
            Assert.DoesNotContain(Lexicon.Get("connect.session_overlay.radio_site_upnp_unreachable"), msg);
        }

        [Fact]
        public void ForStatusRich_Disconnected_LanFine_SaysFlexSideIssue()
        {
            // Manual forward is reachable, but we're reconnecting — so the
            // local network isn't the problem; SmartLink's side is.
            string msg = SessionStatusMessages.ForStatusRich(
                SessionStatus.Reconnecting,
                networkReport: ReportWith(manualTcp: true, manualUdp: true),
                mode: SmartLinkConnectionMode.ManualPortForwardOnly);
            Assert.Contains("local network is fine", msg);
            Assert.Contains("Flex-side", msg);
        }

        [Fact]
        public void ForStatusRich_AuthExpired_AppendsHealthyNetworkContextWhenApplicable()
        {
            string msg = SessionStatusMessages.ForStatusRich(
                SessionStatus.AuthorizationExpired,
                networkReport: ReportWith(manualTcp: true, manualUdp: true));
            Assert.Contains("Authorization expired", msg);
            Assert.Contains("Network appears healthy", msg);
        }

        [Fact]
        public void ForStatusRich_AuthExpired_NoReport_LeavesBaseMessage()
        {
            string msg = SessionStatusMessages.ForStatusRich(SessionStatus.AuthorizationExpired);
            Assert.Contains("Authorization expired", msg);
            Assert.DoesNotContain("Network appears healthy", msg);
        }

        // --- HelpDocFor mapping ---

        [Fact]
        public void HelpDocFor_ConnectedOrConnecting_ReturnsNull()
        {
            Assert.Null(SessionStatusMessages.HelpDocFor(SessionStatus.Connected, null, SmartLinkConnectionMode.ManualPortForwardOnly));
            Assert.Null(SessionStatusMessages.HelpDocFor(SessionStatus.Connecting, null, SmartLinkConnectionMode.ManualPortForwardOnly));
        }

        [Fact]
        public void HelpDocFor_ProbeIncomplete_ReturnsDiagnostics()
        {
            Assert.Equal(SessionStatusMessages.HelpKeyNetworkDiagnostics, SessionStatusMessages.HelpDocFor(
                SessionStatus.Disconnected,
                ReportWith(errorDetail: "timeout"),
                SmartLinkConnectionMode.ManualPortForwardOnly));
        }

        [Fact]
        public void HelpDocFor_SymmetricNatWithTier3_ReturnsTier1Help()
        {
            Assert.Equal(SessionStatusMessages.HelpKeyManualPortForwarding, SessionStatusMessages.HelpDocFor(
                SessionStatus.Reconnecting,
                ReportWith(holePunch: false),
                SmartLinkConnectionMode.AutomaticHolePunch));
        }

        [Fact]
        public void HelpDocFor_UpnpFailWithTier2_ReturnsTier2Help()
        {
            Assert.Equal(SessionStatusMessages.HelpKeyUpnp, SessionStatusMessages.HelpDocFor(
                SessionStatus.Reconnecting,
                ReportWith(upnpTcp: false),
                SmartLinkConnectionMode.ManualPlusUpnp));
        }

        [Fact]
        public void HelpDocFor_ManualForwardDown_ReturnsTier1Help()
        {
            Assert.Equal(SessionStatusMessages.HelpKeyManualPortForwarding, SessionStatusMessages.HelpDocFor(
                SessionStatus.Reconnecting,
                ReportWith(manualTcp: false, upnpTcp: true, upnpUdp: true),
                SmartLinkConnectionMode.ManualPortForwardOnly));
        }

        [Fact]
        public void HelpDocFor_GenericFailure_ReturnsDiagnostics()
        {
            Assert.Equal(SessionStatusMessages.HelpKeyNetworkDiagnostics, SessionStatusMessages.HelpDocFor(
                SessionStatus.Disconnected,
                report: null,
                mode: SmartLinkConnectionMode.ManualPortForwardOnly));
        }

        // --- HelpDocFor following the tiers (the tiers work, Track C) ---

        [Fact]
        public void HelpDocFor_ForwardingOn_JudgesTheForwardedPortsAtEveryTier()
        {
            foreach (SmartLinkConnectionMode tier in Enum.GetValues(typeof(SmartLinkConnectionMode)))
            {
                Assert.Equal(SessionStatusMessages.HelpKeyManualPortForwarding, SessionStatusMessages.HelpDocFor(
                    SessionStatus.Reconnecting, ReportWith(manualTcp: true, manualUdp: false), tier, radioForwardingOn: true));
                // A failed UPnP or punch result is not the page: those are not used with forwarding on.
                Assert.Equal(SessionStatusMessages.HelpKeyNetworkDiagnostics, SessionStatusMessages.HelpDocFor(
                    SessionStatus.Reconnecting, ReportWith(manualTcp: true, manualUdp: true, upnpTcp: false, holePunch: false), tier, radioForwardingOn: true));
            }
        }

        [Fact]
        public void HelpDocFor_Tier1WithForwardingOff_PointsAtForwarding_TheOnlyMethodTier1Allows()
        {
            Assert.Equal(SessionStatusMessages.HelpKeyManualPortForwarding, SessionStatusMessages.HelpDocFor(
                SessionStatus.Reconnecting, ReportWith(upnpTcp: true, upnpUdp: true), SmartLinkConnectionMode.ManualPortForwardOnly, radioForwardingOn: false));
            // Tier 1 never points at the UPnP page.
            Assert.NotEqual(SessionStatusMessages.HelpKeyUpnp, SessionStatusMessages.HelpDocFor(
                SessionStatus.Reconnecting, ReportWith(upnpTcp: false), SmartLinkConnectionMode.ManualPortForwardOnly, radioForwardingOn: false));
        }

        [Fact]
        public void HelpDocFor_ReturnsOnlyThePagesTheHelpBuilds_ForEveryInput()
        {
            var keys = new[] { null, SessionStatusMessages.HelpKeyNetworkDiagnostics,
                SessionStatusMessages.HelpKeyManualPortForwarding, SessionStatusMessages.HelpKeyUpnp };
            bool?[] tri = { null, false, true };
            int checkedCount = 0;
            foreach (SessionStatus status in Enum.GetValues(typeof(SessionStatus)))
            foreach (SmartLinkConnectionMode tier in Enum.GetValues(typeof(SmartLinkConnectionMode)))
            foreach (bool? fwd in tri)
            foreach (bool? mTcp in tri)
            foreach (bool? uTcp in tri)
            foreach (bool? nat in tri)
            {
                foreach (var report in new[] { null, ReportWith(manualTcp: mTcp, upnpTcp: uTcp, holePunch: nat), ReportWith(errorDetail: "x") })
                {
                    Assert.Contains(SessionStatusMessages.HelpDocFor(status, report, tier, fwd), keys);
                    checkedCount++;
                }
            }
            Assert.True(checkedCount > 1000);
        }

        [Fact]
        public void HelpDocFor_WithForwardingUnknown_IsTheThreeArgumentAnswer()
        {
            bool?[] tri = { null, false, true };
            foreach (SmartLinkConnectionMode tier in Enum.GetValues(typeof(SmartLinkConnectionMode)))
            foreach (bool? mTcp in tri)
            foreach (bool? uTcp in tri)
            foreach (bool? nat in tri)
            {
                var report = ReportWith(manualTcp: mTcp, upnpTcp: uTcp, holePunch: nat);
                Assert.Equal(SessionStatusMessages.HelpDocFor(SessionStatus.Reconnecting, report, tier),
                    SessionStatusMessages.HelpDocFor(SessionStatus.Reconnecting, report, tier, radioForwardingOn: null));
            }
        }

        // --- The overlay's report is the radio's own, from its own account ---

        [Fact]
        public void The_overlay_uses_a_report_only_from_the_session_that_brokers_the_radio()
        {
            var mine = ReportWith(manualTcp: false);
            var asked = new System.Collections.Generic.List<string>();
            NetworkDiagnosticReport? Last(string s) { asked.Add(s); return mine; }

            var brokered = new DiagnosticPairing(DiagnosticPairingKind.Paired, "Owner@x", "test");
            Assert.Same(mine, SessionOverlayPairing.ReportFor("owner@x", "S-1", brokered, Last));
            Assert.Equal(new[] { "S-1" }, asked);

            // A reconnecting session is not signed in, and is still the radio's own.
            var reconnecting = new DiagnosticPairing(DiagnosticPairingKind.AccountNotSignedIn, "owner@x", "test");
            Assert.Same(mine, SessionOverlayPairing.ReportFor("owner@x", "S-1", reconnecting, Last));

            // Another account's session, or a radio nothing attributes: no report at all.
            Assert.Null(SessionOverlayPairing.ReportFor("other@x", "S-1", brokered, Last));
            Assert.Null(SessionOverlayPairing.ReportFor("owner@x", "S-1", DiagnosticPairing.None, Last));
            Assert.Null(SessionOverlayPairing.ReportFor("owner@x", "", brokered, Last));
            Assert.Null(SessionOverlayPairing.ReportFor(null, "S-1", brokered, Last));
            Assert.Equal(2, asked.Count);
        }
    }
}
