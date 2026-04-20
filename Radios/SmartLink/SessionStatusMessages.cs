#nullable enable

using System;
using Radios;

namespace Radios.SmartLink
{
    /// <summary>
    /// Single source of truth for human-readable messages derived from
    /// <see cref="SessionStatus"/> plus (Sprint 27 Track D) the most recent
    /// <see cref="NetworkDiagnosticReport"/> and the account's
    /// <see cref="SmartLinkConnectionMode"/>. Messages are intentionally
    /// user-facing — no stack traces, no technical jargon, no codes.
    ///
    /// <para>
    /// All consumers (screen-reader announcements, status bar binding,
    /// diagnostic panel labels, tooltip rendering) call into this class so
    /// user-visible phrasing stays consistent.
    /// </para>
    /// </summary>
    public static class SessionStatusMessages
    {
        /// <summary>
        /// Human-readable status line for display / screen-reader announcement.
        /// Short — fits in a status bar cell and reads well when announced
        /// inline by a screen reader.
        /// </summary>
        public static string ForStatus(SessionStatus status, int reconnectAttemptCount = 0, Exception? lastError = null)
        {
            return status switch
            {
                SessionStatus.Disconnected => "Disconnected",
                SessionStatus.Connecting => "Connecting to SmartLink…",
                SessionStatus.Connected => "Connected",
                SessionStatus.Reconnecting => reconnectAttemptCount > 0
                    ? $"Reconnecting (attempt {reconnectAttemptCount})"
                    : "Reconnecting…",
                SessionStatus.AuthorizationExpired => "Authorization expired — please sign in again",
                SessionStatus.ShutDown => "Session closed",
                _ => status.ToString(),
            };
        }

        /// <summary>
        /// Longer explanation suitable for a tooltip or details panel. Sprint 26
        /// returns the same text as <see cref="ForStatus"/> plus the exception
        /// message if the status is Reconnecting. Sprint 27 Track D prefers
        /// <see cref="ForStatusRich"/> which folds in NetworkTest context.
        /// </summary>
        public static string ForStatusDetailed(SessionStatus status, int reconnectAttemptCount = 0, Exception? lastError = null)
        {
            string baseLine = ForStatus(status, reconnectAttemptCount, lastError);
            if (lastError != null && status == SessionStatus.Reconnecting)
            {
                return $"{baseLine}. Last error: {lastError.Message}";
            }
            return baseLine;
        }

        /// <summary>
        /// Sprint 27 Track D — NetworkTest-aware status line. Builds on
        /// <see cref="ForStatus"/> and appends a failure-class-appropriate
        /// overlay when the status is a disconnection family and we have
        /// positive evidence about what's broken. The <paramref name="verbose"/>
        /// flag, off by default, returns a longer form with the raw error
        /// message appended — useful for advanced users who want the raw
        /// signal without opening the trace file.
        /// </summary>
        /// <param name="status">Current session status.</param>
        /// <param name="reconnectAttemptCount">Attempts since last success.</param>
        /// <param name="lastError">Last connect-path exception, if any.</param>
        /// <param name="networkReport">
        /// Most recent NetworkTest report for this session, or null if none
        /// has run. Drives the overlay selection for disconnection states.
        /// </param>
        /// <param name="mode">Account's <see cref="SmartLinkConnectionMode"/>. Affects overlay priority — Tier-2 UPnP-fail overlays only appear when UPnP was selected.</param>
        /// <param name="verbose">When true, appends raw error details.</param>
        public static string ForStatusRich(
            SessionStatus status,
            int reconnectAttemptCount = 0,
            Exception? lastError = null,
            NetworkDiagnosticReport? networkReport = null,
            SmartLinkConnectionMode mode = SmartLinkConnectionMode.ManualPortForwardOnly,
            bool verbose = false)
        {
            string baseLine = ForStatus(status, reconnectAttemptCount, lastError);

            // Overlay only for failure-family states; successful / in-flight
            // states read more cleanly without a NetworkTest post-mortem.
            if (status == SessionStatus.AuthorizationExpired)
            {
                if (networkReport != null && networkReport.ProbeCompleted
                    && (networkReport.ManualForwardTcpReachable == true || networkReport.UpnpTcpReachable == true))
                {
                    return $"{baseLine}. Network appears healthy — authentication is the only issue.";
                }
                return baseLine;
            }

            if (status != SessionStatus.Reconnecting && status != SessionStatus.Disconnected)
            {
                return baseLine;
            }

            string overlay = ResolveDisconnectOverlay(mode, networkReport, verbose, lastError);
            return string.IsNullOrEmpty(overlay) ? baseLine : $"{baseLine}. {overlay}";
        }

        /// <summary>
        /// Sprint 27 Track D — map a (status, report, mode) triple to the
        /// filename of the help doc that best explains the current state.
        /// Filenames are bare ("tier1-manual-port.md" etc.); callers prepend
        /// <c>help\networking\</c> and the app directory. Returns null when
        /// no help doc is applicable (e.g. status is Connected).
        /// </summary>
        public static string? HelpDocFor(
            SessionStatus status,
            NetworkDiagnosticReport? report,
            SmartLinkConnectionMode mode)
        {
            if (status == SessionStatus.Connected || status == SessionStatus.Connecting) return null;

            if (report != null && !report.ProbeCompleted) return "diagnostics.md";

            if (mode == SmartLinkConnectionMode.AutomaticHolePunch && report?.NatSupportsHolePunch == false)
                return "tier1-manual-port.md";

            if (mode >= SmartLinkConnectionMode.ManualPlusUpnp && report?.UpnpTcpReachable == false)
                return "tier2-upnp.md";

            if (report?.ManualForwardTcpReachable == false) return "tier1-manual-port.md";

            return "diagnostics.md";
        }

        private static string ResolveDisconnectOverlay(
            SmartLinkConnectionMode mode,
            NetworkDiagnosticReport? report,
            bool verbose,
            Exception? lastError)
        {
            if (report == null)
            {
                return verbose && lastError != null ? $"Last error: {lastError.Message}" : string.Empty;
            }

            if (!report.ProbeCompleted)
            {
                return verbose
                    ? $"Network probe did not complete: {report.ErrorDetail}. Check your internet."
                    : "Network appears unreachable — check your internet.";
            }

            // Probe completed. Pick the most specific overlay, highest priority first.
            if (mode == SmartLinkConnectionMode.AutomaticHolePunch && report.NatSupportsHolePunch == false)
            {
                return "Your NAT appears symmetric — hole-punch (Tier 3) may fail. Manual port forwarding is the reliable path.";
            }

            if (mode >= SmartLinkConnectionMode.ManualPlusUpnp
                && report.UpnpTcpReachable == false && report.UpnpUdpReachable == false)
            {
                return "UPnP mapping didn't take — falling back to your manual port forward.";
            }

            if (report.ManualForwardTcpReachable == true || report.UpnpTcpReachable == true)
            {
                return "Your local network is fine — this is usually a Flex-side issue; retrying automatically.";
            }

            return "Network paths to the radio aren't reachable — check your router's port forward.";
        }
    }
}
