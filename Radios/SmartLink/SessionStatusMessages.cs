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
        /// <param name="mode">The tier of the account whose session this is. Affects overlay priority — the UPnP overlay only appears where the tier allows the radio's UPnP ports.</param>
        /// <param name="verbose">When true, appends raw error details.</param>
        /// <param name="radioForwardingOn">The radio's own forwarding flag from the radio list, or null when unknown. With forwarding on, a connect uses the forwarded ports first, so neither the UPnP nor the hole-punch overlay applies (the tiers work, Track C).</param>
        /// <remarks>
        /// <para><b>The report must be the radio's own, from its own brokering
        /// account's session</b> (the tiers work, Track C): the caller pairs it
        /// (<c>FlexBase.SessionOverlayInputs</c>). Until then the overlay was
        /// built from the active session's newest report for ANY radio and the
        /// current account's tier.</para>
        /// <para>Two overlays were untrue and are replaced (approved words, in
        /// connect.json): "UPnP mapping didn't take — falling back to your
        /// manual port forward." (JJ Flexible maps nothing and falls back to
        /// nothing), and "Your NAT appears symmetric — hole-punch (Tier 3) may
        /// fail..." (SmartLink's test measures the RADIO's site, not the
        /// operator's NAT, and its flag is not a NAT-type classifier).</para>
        /// </remarks>
        public static string ForStatusRich(
            SessionStatus status,
            int reconnectAttemptCount = 0,
            Exception? lastError = null,
            NetworkDiagnosticReport? networkReport = null,
            SmartLinkConnectionMode mode = SmartLinkConnectionMode.ManualPortForwardOnly,
            bool verbose = false,
            bool? radioForwardingOn = null)
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

            string overlay = ResolveDisconnectOverlay(mode, networkReport, verbose, lastError, radioForwardingOn);
            return string.IsNullOrEmpty(overlay) ? baseLine : $"{baseLine}. {overlay}";
        }

        /// <summary>Help key for the Network Diagnostics page.</summary>
        public const string HelpKeyNetworkDiagnostics = "NetworkDiagnostics";

        /// <summary>Help key for the Manual Port Forwarding page.</summary>
        public const string HelpKeyManualPortForwarding = "NetworkManualPortForwarding";

        /// <summary>Help key for the UPnP page.</summary>
        public const string HelpKeyUpnp = "NetworkUpnp";

        /// <summary>
        /// Sprint 27 Track D — map a (status, report, mode) triple to the help
        /// page that best explains the current state. Returns a help KEY, one of
        /// the three constants above, which <c>JJFlexWpf.HelpLauncher.ShowHelp</c>
        /// opens in the help like any F1 page. Until the 2026-10-27 help rework
        /// (#826) it returned a Markdown filename that the Settings Network tab
        /// opened loose from a help folder beside the exe; those copies no longer
        /// ship. Returns null when no help page is applicable (e.g. status is
        /// Connected).
        /// </summary>
        public static string? HelpDocFor(
            SessionStatus status,
            NetworkDiagnosticReport? report,
            SmartLinkConnectionMode mode) => HelpDocFor(status, report, mode, radioForwardingOn: null);

        /// <summary>
        /// As above, following the tiers (the tiers work, Track C):
        /// <paramref name="mode"/> is the tier of the account that brokers the
        /// radio, <paramref name="report"/> that radio's own last test from
        /// that account's session, and <paramref name="radioForwardingOn"/> the
        /// radio's own forwarding flag (null when unknown). Returns only the
        /// three keys above, which the help already builds; it never names a
        /// page that does not exist.
        /// </summary>
        /// <remarks>
        /// With the radio's forwarding known ON, a connect dials the forwarded
        /// ports first at every tier, so the page is the forwarding page when
        /// a forwarded port failed and the diagnostics page otherwise; the
        /// UPnP and hole-punch pages do not apply. Otherwise the long-standing
        /// order holds, each step limited to a tier that allows it, and Tier 1
        /// with forwarding known OFF points at forwarding, the only method
        /// Tier 1 allows.
        /// </remarks>
        public static string? HelpDocFor(
            SessionStatus status,
            NetworkDiagnosticReport? report,
            SmartLinkConnectionMode mode,
            bool? radioForwardingOn)
        {
            if (status == SessionStatus.Connected || status == SessionStatus.Connecting) return null;

            if (report != null && !report.ProbeCompleted) return HelpKeyNetworkDiagnostics;

            if (radioForwardingOn == true)
            {
                return report?.ManualForwardTcpReachable == false || report?.ManualForwardUdpReachable == false
                    ? HelpKeyManualPortForwarding
                    : HelpKeyNetworkDiagnostics;
            }

            if (mode == SmartLinkConnectionMode.AutomaticHolePunch && report?.NatSupportsHolePunch == false)
                return HelpKeyManualPortForwarding;

            if (mode >= SmartLinkConnectionMode.ManualPlusUpnp && report?.UpnpTcpReachable == false)
                return HelpKeyUpnp;

            if (report?.ManualForwardTcpReachable == false) return HelpKeyManualPortForwarding;

            if (mode == SmartLinkConnectionMode.ManualPortForwardOnly && radioForwardingOn == false)
                return HelpKeyManualPortForwarding;

            return HelpKeyNetworkDiagnostics;
        }

        private static string ResolveDisconnectOverlay(
            SmartLinkConnectionMode mode,
            NetworkDiagnosticReport? report,
            bool verbose,
            Exception? lastError,
            bool? radioForwardingOn)
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
            // The two below were untrue and are replaced (the tiers work,
            // Track C; approved words). Neither applies with the radio's
            // forwarding known on: a connect then dials the forwarded ports
            // first, whatever the tier.
            if (mode == SmartLinkConnectionMode.AutomaticHolePunch && radioForwardingOn != true
                && report.NatSupportsHolePunch == false)
            {
                return Lexicon.Get("connect.session_overlay.radio_site_no_punch");
            }

            if (mode >= SmartLinkConnectionMode.ManualPlusUpnp && radioForwardingOn != true
                && report.UpnpTcpReachable == false && report.UpnpUdpReachable == false)
            {
                return Lexicon.Get("connect.session_overlay.radio_site_upnp_unreachable");
            }

            if (report.ManualForwardTcpReachable == true || report.UpnpTcpReachable == true)
            {
                return "Your local network is fine — this is usually a Flex-side issue; retrying automatically.";
            }

            return "Network paths to the radio aren't reachable — check your router's port forward.";
        }
    }
}
