#nullable enable

using System;
using System.Collections.Generic;

namespace Radios
{
    /// <summary>
    /// QB Track D (item 5) — the read side of the network identity card.
    ///
    /// One builder produces the identity lines every surface shows: the
    /// <c>NetworkIdentityCard</c> WPF control (Status dialog today, the radio
    /// picker detail area when Track E adopts it), and anywhere else that
    /// wants to answer "who and where is this radio" without re-deriving
    /// anything. Lines are plain sentences in a fixed order — arrow-readable
    /// in a ListBox, pasteable as text, no tables, no visual-only markers.
    ///
    /// Every value is radio-reported or app-observed. Nothing here is typed
    /// from memory; that is the whole point
    /// (memory/feedback_never_assert_config_values_from_memory.md).
    /// </summary>
    public static class NetworkIdentityInfo
    {
        /// <summary>
        /// Build the identity lines for the given rig. Safe in every state:
        /// null rig and disconnected rig produce an explanatory line rather
        /// than nothing, because a card that silently vanishes teaches the
        /// user nothing.
        /// </summary>
        public static List<string> BuildLines(FlexBase? rig)
        {
            var lines = new List<string>();

            if (rig == null || !rig.IsConnected)
            {
                lines.Add("No radio connected.");
                lines.Add("Connect to a radio to see its network identity here.");
                return lines;
            }

            // ── Identity ──
            string model = rig.RadioModel;
            string nickname = rig.RadioNickname;
            lines.Add(string.IsNullOrWhiteSpace(nickname)
                ? $"Radio: {model}"
                : $"Radio: {model}, name {nickname}");

            string serial = rig.ConnectedSerial ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(serial))
                lines.Add($"Serial: {serial}");

            string firmware = rig.RadioFirmwareVersion;
            if (!string.IsNullOrWhiteSpace(firmware))
                lines.Add($"Firmware: {firmware}");

            // ── Where and how we reach it ──
            if (!rig.IsWanConnection)
            {
                BuildLocalLines(rig, lines);
            }
            else
            {
                BuildSmartLinkLines(rig, serial, lines);
            }

            return lines;
        }

        private static void BuildLocalLines(FlexBase rig, List<string> lines)
        {
            string ip = rig.CurrentRadioIP?.ToString() ?? "an unknown address";
            lines.Add($"Connection: local network, radio at {ip}.");

            var staticIp = rig.CurrentStaticIP;
            if (staticIp != null)
            {
                string gw = rig.CurrentStaticGateway?.ToString() ?? "unknown";
                string mask = rig.CurrentStaticNetmask?.ToString() ?? "unknown";
                lines.Add($"Address mode: fixed at {staticIp}, gateway {gw}, netmask {mask}.");
            }
            else
            {
                lines.Add("Address mode: automatic — the router assigns the address, which can change after a power cut.");
            }
        }

        private static void BuildSmartLinkLines(FlexBase rig, string serial, List<string> lines)
        {
            lines.Add("Connection: remote, via SmartLink.");

            string account = rig.CurrentSmartLinkEmail;
            if (!string.IsNullOrWhiteSpace(account))
                lines.Add($"SmartLink account: {account}.");

            // Over SmartLink, the address we dial is the radio's PUBLIC face.
            string publicIp = rig.CurrentRadioIP?.ToString() ?? "unknown";
            lines.Add($"Radio's public address: {publicIp}.");

            if (rig.RadioRequiresHolePunch)
            {
                int punchPort = rig.LastHolePunchPort;
                lines.Add(punchPort > 0
                    ? $"Path: hole punch, negotiated on port {punchPort} this session. No forwarded ports are configured at the radio's site."
                    : "Path: hole punch. No forwarded ports are configured at the radio's site.");
            }
            else
            {
                int tcp = rig.RadioPublicTlsPort;
                int udp = rig.RadioPublicUdpPort;
                string fwdState = rig.RadioPortForwardActive ? "on" : "off";
                lines.Add($"Path: forwarded ports — external TCP {tcp}, external UDP {udp}. Radio reports port forwarding {fwdState}.");

                // The verbatim rule, so nobody ever has to reconstruct it.
                string? rule = rig.BuildRouterRuleText();
                if (!string.IsNullOrEmpty(rule))
                    lines.Add(rule);
            }

            string? lanIp = rig.CachedLanIpFor(serial);
            if (lanIp != null)
                lines.Add($"Radio's LAN address, last seen from this computer: {lanIp}.");

            // Reachability — SmartLink's own outside-in test, cache only.
            // Never trigger a probe from a read surface: on a hole-punched
            // session the radio-side probe endangers the live connection
            // (the f842e93f gate), and an identity card must be safe to open
            // in every state.
            var probe = rig.LastNetworkReportFor(serial);
            if (probe == null)
            {
                lines.Add("Reachability from the internet: not tested this session.");
            }
            else if (!probe.ProbeCompleted)
            {
                lines.Add($"Reachability from the internet: last test did not complete ({probe.ErrorDetail}).");
            }
            else
            {
                lines.Add("Reachability from the internet, per SmartLink's test"
                    + AgeSuffix(probe.TimestampUtc) + ": "
                    + $"forwarded TCP {YesNo(probe.ManualForwardTcpReachable)}, "
                    + $"forwarded UDP {YesNo(probe.ManualForwardUdpReachable)}, "
                    + $"UPnP TCP {YesNo(probe.UpnpTcpReachable)}, "
                    + $"UPnP UDP {YesNo(probe.UpnpUdpReachable)}, "
                    + $"hole-punch support {YesNo(probe.NatSupportsHolePunch)}.");
            }
        }

        private static string AgeSuffix(DateTime timestampUtc)
        {
            var age = DateTime.UtcNow - timestampUtc;
            if (age < TimeSpan.Zero) return string.Empty;
            if (age.TotalMinutes < 1) return " just now";
            if (age.TotalMinutes < 120) return $" {(int)age.TotalMinutes} minutes ago";
            return $" {(int)age.TotalHours} hours ago";
        }

        private static string YesNo(bool? v) => v switch { true => "yes", false => "no", null => "unknown" };
    }
}
