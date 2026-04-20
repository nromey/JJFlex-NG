#nullable enable

using System;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 27 Track C / Phase C.1. Verifies
    /// <see cref="NetworkDiagnosticReport.ToMarkdown"/> produces output that:
    /// (a) is always plain-readable as text (no tables per Noel's accessibility
    /// preference), (b) distinguishes yes/no/unknown for each subtest, and
    /// (c) collapses to an error summary when the probe did not complete.
    /// The timestamp and radio serial always appear so the snapshot is
    /// self-contained for bug reports.
    /// </summary>
    public class NetworkDiagnosticReportTests
    {
        private static readonly DateTime FixedTs = new DateTime(2026, 4, 20, 14, 30, 0, DateTimeKind.Utc);

        [Fact]
        public void ProbeCompleted_TrueWhenNoError()
        {
            var r = new NetworkDiagnosticReport
            {
                RadioSerial = "1234-5678",
                TimestampUtc = FixedTs,
                UpnpTcpReachable = true,
            };
            Assert.True(r.ProbeCompleted);
        }

        [Fact]
        public void ProbeCompleted_FalseWhenErrorSet()
        {
            var r = new NetworkDiagnosticReport
            {
                RadioSerial = "1234-5678",
                TimestampUtc = FixedTs,
                ErrorDetail = "Timed out after 30 seconds",
            };
            Assert.False(r.ProbeCompleted);
        }

        [Fact]
        public void ToMarkdown_AllPass_RendersYesForEverySubtest()
        {
            var r = new NetworkDiagnosticReport
            {
                RadioSerial = "1234-5678",
                TimestampUtc = FixedTs,
                UpnpTcpReachable = true,
                UpnpUdpReachable = true,
                ManualForwardTcpReachable = true,
                ManualForwardUdpReachable = true,
                NatSupportsHolePunch = true,
            };
            string md = r.ToMarkdown();
            Assert.Contains("**UPnP TCP port reachable:** yes", md);
            Assert.Contains("**UPnP UDP port reachable:** yes", md);
            Assert.Contains("**TCP port reachable:** yes", md);
            Assert.Contains("**UDP port reachable:** yes", md);
            Assert.Contains("**Hole-punch support:** yes", md);
            Assert.Contains("**Probe status:** completed", md);
        }

        [Fact]
        public void ToMarkdown_AllFail_RendersNoForEverySubtest()
        {
            var r = new NetworkDiagnosticReport
            {
                RadioSerial = "1234-5678",
                TimestampUtc = FixedTs,
                UpnpTcpReachable = false,
                UpnpUdpReachable = false,
                ManualForwardTcpReachable = false,
                ManualForwardUdpReachable = false,
                NatSupportsHolePunch = false,
            };
            string md = r.ToMarkdown();
            Assert.Contains("**UPnP TCP port reachable:** no", md);
            Assert.Contains("**UPnP UDP port reachable:** no", md);
            Assert.Contains("**TCP port reachable:** no", md);
            Assert.Contains("**UDP port reachable:** no", md);
            Assert.Contains("**Hole-punch support:** no", md);
            Assert.Contains("**Probe status:** completed", md);
        }

        [Fact]
        public void ToMarkdown_Mixed_RendersUnknownForNullSubtests()
        {
            var r = new NetworkDiagnosticReport
            {
                RadioSerial = "1234-5678",
                TimestampUtc = FixedTs,
                UpnpTcpReachable = true,
                UpnpUdpReachable = null,
                ManualForwardTcpReachable = false,
                ManualForwardUdpReachable = null,
                NatSupportsHolePunch = null,
            };
            string md = r.ToMarkdown();
            Assert.Contains("**UPnP TCP port reachable:** yes", md);
            Assert.Contains("**UPnP UDP port reachable:** unknown", md);
            Assert.Contains("**TCP port reachable:** no", md);
            Assert.Contains("**UDP port reachable:** unknown", md);
            Assert.Contains("**Hole-punch support:** unknown", md);
        }

        [Fact]
        public void ToMarkdown_ErrorState_ShortFormWithReason()
        {
            var r = new NetworkDiagnosticReport
            {
                RadioSerial = "1234-5678",
                TimestampUtc = FixedTs,
                ErrorDetail = "Timed out after 30 seconds",
            };
            string md = r.ToMarkdown();
            Assert.Contains("**Probe status:** did not complete", md);
            Assert.Contains("**Reason:** Timed out after 30 seconds", md);
            // When the probe didn't complete, the subtest H2 groups should be
            // omitted entirely — rendering them as "unknown" five times would
            // suggest five separate unknowns rather than one missing probe.
            Assert.DoesNotContain("## UPnP", md);
            Assert.DoesNotContain("## Manual port forward", md);
            Assert.DoesNotContain("## NAT", md);
        }

        [Fact]
        public void ToMarkdown_IncludesTimestampAndSerial()
        {
            var r = new NetworkDiagnosticReport
            {
                RadioSerial = "1234-5678",
                TimestampUtc = FixedTs,
            };
            string md = r.ToMarkdown();
            Assert.Contains("2026-04-20 14:30:00 UTC", md);
            Assert.Contains("**Radio serial:** 1234-5678", md);
        }

        [Fact]
        public void ToMarkdown_BlankSerial_RendersAsUnknown()
        {
            var r = new NetworkDiagnosticReport
            {
                RadioSerial = "",
                TimestampUtc = FixedTs,
            };
            string md = r.ToMarkdown();
            Assert.Contains("**Radio serial:** unknown", md);
        }

        [Fact]
        public void ToMarkdown_DoesNotContainMarkdownTable()
        {
            // Accessibility invariant: the output must never include a markdown
            // table syntax ('|...|...|') because screen readers flatten those
            // into unreadable concatenated runs. If a future change introduces
            // a table, this test catches it immediately.
            var r = new NetworkDiagnosticReport
            {
                RadioSerial = "1234-5678",
                TimestampUtc = FixedTs,
                UpnpTcpReachable = true,
                UpnpUdpReachable = false,
                ManualForwardTcpReachable = true,
                ManualForwardUdpReachable = false,
                NatSupportsHolePunch = true,
            };
            string md = r.ToMarkdown();
            Assert.DoesNotContain("| ", md);
            Assert.DoesNotContain(" |", md);
        }
    }
}
