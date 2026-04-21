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

        [Fact]
        public void ForStatusRich_Disconnected_SymmetricNatWithTier3_SaysSymmetric()
        {
            string msg = SessionStatusMessages.ForStatusRich(
                SessionStatus.Disconnected,
                networkReport: ReportWith(
                    manualTcp: true, manualUdp: true,
                    upnpTcp: true, upnpUdp: true,
                    holePunch: false),
                mode: SmartLinkConnectionMode.AutomaticHolePunch);
            Assert.Contains("symmetric", msg);
            Assert.Contains("Manual port forwarding is the reliable path", msg);
        }

        [Fact]
        public void ForStatusRich_Disconnected_UpnpFailWithTier2_SaysUpnpFellBack()
        {
            string msg = SessionStatusMessages.ForStatusRich(
                SessionStatus.Reconnecting,
                networkReport: ReportWith(
                    manualTcp: true, manualUdp: true,
                    upnpTcp: false, upnpUdp: false),
                mode: SmartLinkConnectionMode.ManualPlusUpnp);
            Assert.Contains("UPnP mapping didn't take", msg);
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
            Assert.Equal("networking-diagnostics.md", SessionStatusMessages.HelpDocFor(
                SessionStatus.Disconnected,
                ReportWith(errorDetail: "timeout"),
                SmartLinkConnectionMode.ManualPortForwardOnly));
        }

        [Fact]
        public void HelpDocFor_SymmetricNatWithTier3_ReturnsTier1Help()
        {
            Assert.Equal("networking-tier1-manual-port.md", SessionStatusMessages.HelpDocFor(
                SessionStatus.Reconnecting,
                ReportWith(holePunch: false),
                SmartLinkConnectionMode.AutomaticHolePunch));
        }

        [Fact]
        public void HelpDocFor_UpnpFailWithTier2_ReturnsTier2Help()
        {
            Assert.Equal("networking-tier2-upnp.md", SessionStatusMessages.HelpDocFor(
                SessionStatus.Reconnecting,
                ReportWith(upnpTcp: false),
                SmartLinkConnectionMode.ManualPlusUpnp));
        }

        [Fact]
        public void HelpDocFor_ManualForwardDown_ReturnsTier1Help()
        {
            Assert.Equal("networking-tier1-manual-port.md", SessionStatusMessages.HelpDocFor(
                SessionStatus.Reconnecting,
                ReportWith(manualTcp: false, upnpTcp: true, upnpUdp: true),
                SmartLinkConnectionMode.ManualPortForwardOnly));
        }

        [Fact]
        public void HelpDocFor_GenericFailure_ReturnsDiagnostics()
        {
            Assert.Equal("networking-diagnostics.md", SessionStatusMessages.HelpDocFor(
                SessionStatus.Disconnected,
                report: null,
                mode: SmartLinkConnectionMode.ManualPortForwardOnly));
        }
    }
}
