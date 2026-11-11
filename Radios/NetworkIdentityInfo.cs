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
            string serial = rig.ConnectedSerial ?? string.Empty;
            AddIdentityLines(lines, rig.RadioModel, rig.RadioNickname, serial);

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
                lines.Add(PathLine(holePunch: true, rig.RadioPublicTlsPort, rig.RadioPublicUdpPort,
                    rig.RadioPortForwardActive, rig.LastHolePunchPort));
            }
            else
            {
                lines.Add(PathLine(holePunch: false, rig.RadioPublicTlsPort, rig.RadioPublicUdpPort,
                    rig.RadioPortForwardActive, 0));

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

        /// <summary>
        /// The card's lines in the radio picker (#398): the HIGHLIGHTED row,
        /// so a SmartLink radio's advertised ports can be read without
        /// connecting. With no row highlighted, or the connected radio
        /// highlighted, the connected radio's own lines, exactly as the Status
        /// dialog shows them. A row a SmartLink list carries, not connected,
        /// shows what the list says about reaching it, or, when its numbers
        /// have not reached the picker, says it is listed and that they have
        /// not arrived; a row no list carries says SmartLink does not list it.
        /// Nothing here probes anything: the list's numbers are the address
        /// the radio is handing out, which no port probe can stand in for.
        /// </summary>
        /// <remarks>
        /// <b>Whether a list carries the radio is asked separately from its
        /// numbers</b> (repair one, Codex B1). Missing numbers are not
        /// evidence that SmartLink does not list the radio: until the repair a
        /// listed radio that local discovery found first reached here with no
        /// numbers, and the card said SmartLink does not list it while its row
        /// said it was available on SmartLink. The converse holds too: a
        /// row's last SmartLink sighting is kept after the radio leaves the
        /// list, so its numbers are shown only while a list carries it.
        /// </remarks>
        /// <param name="current">The app's current radio, or null.</param>
        /// <param name="rowSerial">The highlighted row's serial, or null when none is highlighted.</param>
        /// <param name="rowName">The highlighted row's radio name.</param>
        /// <param name="rowModel">The highlighted row's model.</param>
        /// <param name="listedOnSmartLink">The row's own SmartLink flag: a SmartLink list carries the radio.</param>
        /// <param name="advertised">What that list says about reaching the radio, or null when the picker holds no numbers for it.</param>
        public static List<string> BuildPickerLines(FlexBase? current, string? rowSerial, string? rowName,
            string? rowModel, bool listedOnSmartLink, SmartLinkAdvertised? advertised)
        {
            if (string.IsNullOrEmpty(rowSerial)) return BuildLines(current);
            if (current != null && current.IsConnected
                && string.Equals(current.ConnectedSerial, rowSerial, StringComparison.OrdinalIgnoreCase))
                return BuildLines(current);

            var lines = new List<string>();
            AddIdentityLines(lines, rowModel ?? "", rowName ?? "", rowSerial!);
            if (!listedOnSmartLink)
            {
                lines.Add(Lexicon.Get("connect.identity.not_listed"));
                return lines;
            }
            if (advertised == null)
            {
                lines.Add(Lexicon.Get("connect.identity.listed_ports_not_arrived"));
                return lines;
            }
            lines.Add(Lexicon.Get("connect.identity.listed_not_connected"));
            lines.Add(PathLine(advertised.RequiresHolePunch, advertised.TlsPort, advertised.UdpPort, advertised.PortForwardOn, 0));
            return lines;
        }

        private static void AddIdentityLines(List<string> lines, string model, string nickname, string serial)
        {
            lines.Add(string.IsNullOrWhiteSpace(nickname)
                ? $"Radio: {model}"
                : $"Radio: {model}, name {nickname}");
            if (!string.IsNullOrWhiteSpace(serial))
                lines.Add($"Serial: {serial}");
        }

        /// <summary>The path line, one wording for the connected radio and a listed one.</summary>
        private static string PathLine(bool holePunch, int tcp, int udp, bool forwardOn, int punchPort)
        {
            if (holePunch)
                return punchPort > 0
                    ? $"Path: hole punch, negotiated on port {punchPort} this session. No forwarded ports are configured at the radio's site."
                    : "Path: hole punch. No forwarded ports are configured at the radio's site.";
            string fwdState = forwardOn ? "on" : "off";
            return $"Path: forwarded ports — external TCP {tcp}, external UDP {udp}. Radio reports port forwarding {fwdState}.";
        }

        private static string AgeSuffix(DateTime timestampUtc)
        {
            var age = DateTime.UtcNow - timestampUtc;
            if (age < TimeSpan.Zero) return string.Empty;
            if (age.TotalMinutes < 1) return " just now";
            int m = (int)age.TotalMinutes;
            if (m < 120) return $" {m} minute{(m == 1 ? "" : "s")} ago";
            int h = (int)age.TotalHours;
            return $" {h} hour{(h == 1 ? "" : "s")} ago";
        }

        private static string YesNo(bool? v) => v switch { true => "yes", false => "no", null => "unknown" };
    }

    /// <summary>
    /// What a SmartLink radio list says about reaching one radio, as it was
    /// received (#398): the public TCP and UDP ports it advertises, whether it
    /// reports port forwarding on, and whether it needs a hole punch. Carried
    /// on a SmartLink sighting (<see cref="FlexBase.RigData.Advertised"/>) so
    /// the radio picker can show it without a connect.
    /// </summary>
    public sealed class SmartLinkAdvertised
    {
        public SmartLinkAdvertised(int tlsPort, int udpPort, bool portForwardOn, bool requiresHolePunch)
        {
            TlsPort = tlsPort;
            UdpPort = udpPort;
            PortForwardOn = portForwardOn;
            RequiresHolePunch = requiresHolePunch;
        }

        public int TlsPort { get; }
        public int UdpPort { get; }
        public bool PortForwardOn { get; }
        public bool RequiresHolePunch { get; }
    }
}
