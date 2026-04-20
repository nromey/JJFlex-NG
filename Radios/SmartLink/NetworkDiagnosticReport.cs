#nullable enable

using System;
using System.Text;

namespace Radios.SmartLink
{
    /// <summary>
    /// Sprint 27 Track C — structured result of a SmartLink NetworkTest probe.
    /// Faithful rendering of FlexLib's <c>WanTestConnectionResults</c> surface
    /// (five booleans) with a timestamp + radio identity + an optional
    /// <see cref="ErrorDetail"/> slot for probes that failed to complete.
    ///
    /// <para>
    /// All subtest fields are nullable: <c>null</c> means "not probed / unknown",
    /// <c>true</c> means "pass", <c>false</c> means "fail". This lets the type
    /// represent partial probes as well as complete probes where the underlying
    /// SmartLink response omitted a field. <see cref="ProbeCompleted"/> is true
    /// when no <see cref="ErrorDetail"/> was recorded — i.e. SmartLink returned
    /// a response within our timeout window.
    /// </para>
    ///
    /// <para>
    /// This type is FlexLib-free by design; the wrapper that consumes
    /// FlexLib's <c>WanTestConnectionResults</c> does the translation. Keeps
    /// <see cref="Radios.SmartLink"/>'s public DTOs portable.
    /// </para>
    /// </summary>
    public sealed class NetworkDiagnosticReport
    {
        /// <summary>Serial number of the radio the probe was run against.</summary>
        public string RadioSerial { get; init; } = string.Empty;

        /// <summary>When the probe completed (or the timeout fired). UTC.</summary>
        public DateTime TimestampUtc { get; init; }

        /// <summary>UPnP TCP port-forward reachability, or null if not probed.</summary>
        public bool? UpnpTcpReachable { get; init; }

        /// <summary>UPnP UDP port-forward reachability, or null if not probed.</summary>
        public bool? UpnpUdpReachable { get; init; }

        /// <summary>Manually-forwarded TCP port reachability, or null if not probed.</summary>
        public bool? ManualForwardTcpReachable { get; init; }

        /// <summary>Manually-forwarded UDP port reachability, or null if not probed.</summary>
        public bool? ManualForwardUdpReachable { get; init; }

        /// <summary>
        /// Whether the NAT preserves source ports well enough for UDP hole-punch
        /// (Tier 3). Null if not probed. FlexLib field name is
        /// <c>nat_supports_hole_punch</c>; the audit note is that this is a
        /// port-preservation probe, not a full NAT-type classifier.
        /// </summary>
        public bool? NatSupportsHolePunch { get; init; }

        /// <summary>
        /// Non-null when the probe did NOT produce a <c>WanTestConnectionResults</c>
        /// payload — e.g. "Timed out after 30 seconds", "SmartLink connection
        /// was lost during probe", etc. All subtest fields will be null in this
        /// case. See <see cref="ProbeCompleted"/> for a convenience check.
        /// </summary>
        public string? ErrorDetail { get; init; }

        /// <summary>
        /// True when SmartLink returned a response and the probe populated its
        /// subtest fields. False when the probe timed out or errored out —
        /// check <see cref="ErrorDetail"/> for the reason.
        /// </summary>
        public bool ProbeCompleted => ErrorDetail == null;

        /// <summary>
        /// Sprint 27 Track C (with ToMarkdown format per Track D's spec).
        /// Renders a plain-readable markdown document summarizing the probe
        /// result. Format is deliberately table-free (Noel's accessibility
        /// preference): bulleted lists under H2 group headers. Readable as
        /// plain text even without a markdown renderer; parses cleanly when
        /// pasted into anything that does render markdown (GitHub issues,
        /// forum posts, email clients, etc.).
        /// </summary>
        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# JJ Flex Network Diagnostic");
            sb.AppendLine();
            sb.AppendLine($"- **Timestamp:** {TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"- **Radio serial:** {RenderString(RadioSerial)}");
            sb.AppendLine($"- **Probe status:** {(ProbeCompleted ? "completed" : "did not complete")}");
            if (!ProbeCompleted)
            {
                sb.AppendLine($"- **Reason:** {ErrorDetail}");
                return sb.ToString();
            }

            sb.AppendLine();
            sb.AppendLine("## UPnP");
            sb.AppendLine($"- **UPnP TCP port reachable:** {RenderBool(UpnpTcpReachable)}");
            sb.AppendLine($"- **UPnP UDP port reachable:** {RenderBool(UpnpUdpReachable)}");

            sb.AppendLine();
            sb.AppendLine("## Manual port forward");
            sb.AppendLine($"- **TCP port reachable:** {RenderBool(ManualForwardTcpReachable)}");
            sb.AppendLine($"- **UDP port reachable:** {RenderBool(ManualForwardUdpReachable)}");

            sb.AppendLine();
            sb.AppendLine("## NAT");
            sb.AppendLine($"- **Hole-punch support:** {RenderBool(NatSupportsHolePunch)}");

            return sb.ToString();
        }

        private static string RenderBool(bool? value) => value switch
        {
            true => "yes",
            false => "no",
            null => "unknown",
        };

        private static string RenderString(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }
}
