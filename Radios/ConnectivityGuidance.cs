#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using JJTrace;
using Radios.SmartLink;

namespace Radios
{
    // ────────────────────────────────────────────────────────────────────
    //  QB Track D (2026-08-07) — connectivity truth & guidance.
    //
    //  Origin story, and the principle this file exists to enforce:
    //  router port numbers were once asserted from memory, the wrong
    //  numbers reached two people's routers, and the app reported none of
    //  the evidence it already had — Don's traces read fwdTcp=False for
    //  hours while humans guessed. Trust what the radio reports, and never
    //  make a human retype a number the app already knows.
    //  (memory/feedback_never_assert_config_values_from_memory.md)
    //
    //  Settled port facts (do not re-derive):
    //  - SmartLink remote path: EXTERNAL ports are user-chosen; INTERNAL
    //    are fixed UDP 4993 / TCP 4994.
    //  - LAN path: TCP 4992 / UDP 4991. Different path, different ports —
    //    never generalize one into the other.
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What kind of failure a connect attempt actually was. Different causes
    /// need different words — "connection failed" alone made people guess.
    /// </summary>
    public enum ConnectFailureClass
    {
        /// <summary>No classification recorded.</summary>
        Unknown,
        /// <summary>The requested radio never appeared in the discovery / SmartLink list.</summary>
        RadioNotFound,
        /// <summary>The SmartLink session itself could not be established (server / network side, sign-in OK).</summary>
        SessionSetupFailed,
        /// <summary>SmartLink rejected our authorization — the one class where a sign-in form is the right medicine.</summary>
        AuthenticationFailed,
        /// <summary>SmartLink accepted the connect request but the radio never reported ready.</summary>
        RemoteHandshakeFailed,
        /// <summary>TCP to the radio's public port was actively refused — the router answered, nothing behind the rule.</summary>
        TransportRefused,
        /// <summary>TCP to the radio's public port timed out — packets never arrived (firewall / ISP / stale address).</summary>
        TransportTimedOut,
        /// <summary>The transport connect failed some other way (unreachable, punch failure, TLS, …).</summary>
        TransportFailed,
        /// <summary>A LAN connect failed.</summary>
        LocalConnectFailed,
    }

    /// <summary>Outcome of a single client-side TCP reachability check.</summary>
    public enum TcpProbeOutcome
    {
        Connected,
        /// <summary>RST came back — something routed us there and said no. Fast (usually well under 200 ms over WAN).</summary>
        Refused,
        /// <summary>No answer at all inside the window — the SYN was dropped somewhere.</summary>
        TimedOut,
        /// <summary>ICMP-level unreachable (no route, host down at the network layer).</summary>
        Unreachable,
        /// <summary>Anything else (DNS, socket error, …). Detail carries the message.</summary>
        Failed,
    }

    /// <summary>Result of <see cref="TcpReachabilityProbe.Classify"/> — outcome plus how long it took.</summary>
    public sealed class TcpProbeResult
    {
        public TcpProbeOutcome Outcome { get; init; }
        public long ElapsedMs { get; init; }
        public string Detail { get; init; } = string.Empty;
    }

    /// <summary>
    /// Client-side TCP SYN classifier. A sub-second refusal and a multi-second
    /// timeout are different diseases: refusal means the router answered and
    /// nothing sits behind the rule; timeout means the packets never arrived
    /// at anything willing to answer. This is OUR observation from the
    /// client's side of the network — it complements (not replaces) the
    /// radio-side test_connection probe SmartLink runs from outside.
    ///
    /// Deliberately a single bare connect + immediate close: one SYN
    /// exchange, no data, no protocol. Safe against a radio that is up (the
    /// firmware sees a connection open and close, which discovery-era
    /// clients do constantly). Only ever aimed at the forwarded public port
    /// of a connect attempt that ALREADY failed — never at a live session,
    /// and never on the hole-punch path (there is no listening public port
    /// to classify there; the punched port only exists mid-orchestration).
    /// </summary>
    public static class TcpReachabilityProbe
    {
        public static TcpProbeResult Classify(IPAddress ip, int port, int timeoutMs = 4000)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient(ip.AddressFamily);
                var connectTask = client.ConnectAsync(ip, port);
                bool completed = connectTask.Wait(timeoutMs);
                sw.Stop();

                if (!completed)
                {
                    return new TcpProbeResult
                    {
                        Outcome = TcpProbeOutcome.TimedOut,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Detail = $"no answer from {ip}:{port} within {timeoutMs}ms",
                    };
                }

                if (connectTask.IsFaulted)
                {
                    var sockEx = FindSocketException(connectTask.Exception);
                    return new TcpProbeResult
                    {
                        Outcome = ClassifySocketError(sockEx),
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Detail = sockEx?.Message ?? connectTask.Exception?.GetBaseException().Message ?? "unknown fault",
                    };
                }

                // Connected — close immediately (the using block sends FIN/RST on dispose).
                return new TcpProbeResult
                {
                    Outcome = TcpProbeOutcome.Connected,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Detail = $"{ip}:{port} accepted the connection",
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                var sockEx = ex as SocketException ?? FindSocketException(ex as AggregateException);
                return new TcpProbeResult
                {
                    Outcome = ClassifySocketError(sockEx),
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Detail = ex.GetBaseException().Message,
                };
            }
        }

        private static SocketException? FindSocketException(AggregateException? agg)
        {
            if (agg == null) return null;
            foreach (var inner in agg.Flatten().InnerExceptions)
            {
                if (inner is SocketException se) return se;
                if (inner.InnerException is SocketException se2) return se2;
            }
            return null;
        }

        private static TcpProbeOutcome ClassifySocketError(SocketException? se)
        {
            if (se == null) return TcpProbeOutcome.Failed;
            return se.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => TcpProbeOutcome.Refused,
                SocketError.TimedOut => TcpProbeOutcome.TimedOut,
                SocketError.HostUnreachable => TcpProbeOutcome.Unreachable,
                SocketError.NetworkUnreachable => TcpProbeOutcome.Unreachable,
                SocketError.ConnectionReset => TcpProbeOutcome.Refused,
                _ => TcpProbeOutcome.Failed,
            };
        }
    }

    /// <summary>
    /// Builds the exact router rule a user's router needs, verbatim, from
    /// radio-reported values only. Speakable, copyable, and nobody's memory
    /// gets a vote: external ports come from what the radio advertises
    /// (public_tls_port / public_udp_port in the SmartLink radio list), the
    /// LAN address comes from discovery (the connection cache), and the
    /// internal ports are the two fixed SmartLink listeners.
    /// </summary>
    public static class RouterRuleAdvisor
    {
        /// <summary>
        /// The fixed internal TCP port the radio listens on for SmartLink
        /// remote connections. Per FlexRadio's own port-forwarding setup
        /// article and working field experience. NOT the LAN command port
        /// (that is 4992, a different path entirely).
        /// </summary>
        public const int SmartLinkInternalTcpPort = 4994;

        /// <summary>
        /// The fixed internal UDP port the radio listens on for SmartLink
        /// remote audio/data. NOT the LAN VITA port (that is 4991).
        /// </summary>
        public const int SmartLinkInternalUdpPort = 4993;

        /// <summary>
        /// Compose the router rule sentence. Returns null when the radio has
        /// not advertised usable external ports (nothing honest to say).
        /// <paramref name="lanIp"/> may be null/empty when the radio's LAN
        /// address is unknown from here (e.g. remote radio never seen on
        /// this machine's LAN) — the text degrades to naming the address
        /// generically rather than inventing one.
        /// </summary>
        public static string? BuildRouterRuleText(int externalTcpPort, int externalUdpPort, string? lanIp)
        {
            if (externalTcpPort <= 0 && externalUdpPort <= 0) return null;

            string target = string.IsNullOrWhiteSpace(lanIp)
                ? "the radio's LAN address"
                : lanIp;

            var parts = new List<string>();
            if (externalTcpPort > 0)
                parts.Add($"forward external TCP port {externalTcpPort} to {target}, internal port {SmartLinkInternalTcpPort}");
            if (externalUdpPort > 0)
                parts.Add($"forward external UDP port {externalUdpPort} to {target}, internal port {SmartLinkInternalUdpPort}");

            return "Router rule needed: " + string.Join("; and ", parts) + ".";
        }
    }

    /// <summary>
    /// Everything the app knows about why a connect attempt failed, composed
    /// into one place so every caller speaks the same evidence instead of a
    /// bare "connection failed". SpokenSummary is the sentence(s) a screen
    /// reader says; DetailLines are the arrow-readable expansion for
    /// dialogs and reports; RouterRuleText is the verbatim rule when the
    /// evidence points at the router.
    /// </summary>
    public sealed class ConnectFailureReport
    {
        public ConnectFailureClass Class { get; init; } = ConnectFailureClass.Unknown;

        /// <summary>One or two sentences, complete on their own. Never "see the message" — this IS the message.</summary>
        public string SpokenSummary { get; init; } = string.Empty;

        /// <summary>Optional verbatim router rule, appended to speech when present.</summary>
        public string? RouterRuleText { get; init; }

        /// <summary>Arrow-readable evidence lines (probe results, timings, ports).</summary>
        public List<string> DetailLines { get; init; } = new();

        /// <summary>The SmartLink test_connection report consulted, if any.</summary>
        public NetworkDiagnosticReport? ProbeReport { get; init; }

        /// <summary>The client-side TCP classification run, if any.</summary>
        public TcpProbeResult? TcpProbe { get; init; }

        /// <summary>The full sentence for speech: summary plus router rule when one applies.</summary>
        public string ComposeSpeech()
        {
            return string.IsNullOrEmpty(RouterRuleText)
                ? SpokenSummary
                : $"{SpokenSummary} {RouterRuleText}";
        }

        /// <summary>Trace every composed report so field traces carry the same story the user heard.</summary>
        public void Trace()
        {
            Tracing.TraceLine($"ConnectFailureReport: class={Class} spoken=\"{SpokenSummary}\"", TraceLevel.Warning);
            if (!string.IsNullOrEmpty(RouterRuleText))
                Tracing.TraceLine($"ConnectFailureReport: rule=\"{RouterRuleText}\"", TraceLevel.Warning);
            foreach (var line in DetailLines)
                Tracing.TraceLine($"ConnectFailureReport: detail: {line}", TraceLevel.Info);
        }
    }
}
