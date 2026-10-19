#define CWMonitor
using System;
using System.Collections.Generic;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// FlexBase, the radio's static IP and DHCP network settings.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        #region Static IP / DHCP

        /// <summary>
        /// Result of validating a proposed static-network configuration.
        /// </summary>
        public sealed class StaticIpCheck
        {
            public bool CanProceed { get; set; }
            public string BlockReason { get; set; } = string.Empty;
            public List<string> Warnings { get; } = new List<string>();
        }

        /// <summary>Current static IP the radio reports, or null when on DHCP.</summary>
        public System.Net.IPAddress CurrentStaticIP => theRadio?.StaticIP;

        /// <summary>Current static gateway the radio reports, or null.</summary>
        public System.Net.IPAddress CurrentStaticGateway => theRadio?.StaticGateway;

        /// <summary>Current static netmask the radio reports, or null.</summary>
        public System.Net.IPAddress CurrentStaticNetmask => theRadio?.StaticNetmask;

        /// <summary>The address we are actually talking to the radio on right now.</summary>
        public System.Net.IPAddress CurrentRadioIP => theRadio?.IP;

        /// <summary>
        /// Validate a proposed static-network configuration without applying it.
        ///
        /// This matters more than a normal settings validation. A wrong value here
        /// doesn't produce an error — it makes the radio unreachable at the next
        /// reboot, and the only recovery is physical access to the front panel. For a
        /// radio at a remote site that means someone has to travel. So we check the
        /// arithmetic properly and warn loudly when the proposed address wouldn't be
        /// reachable from where we're standing.
        /// </summary>
        public StaticIpCheck PreflightStaticIp(string ip, string gateway, string netmask)
        {
            var check = new StaticIpCheck();

            if (theRadio == null || !IsConnected)
            {
                check.BlockReason = "No radio is connected.";
                return check;
            }

            if (!System.Net.IPAddress.TryParse(ip, out var ipAddr)
                || ipAddr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                check.BlockReason = "The IP address is not a valid IPv4 address.";
                return check;
            }
            if (!System.Net.IPAddress.TryParse(gateway, out var gwAddr)
                || gwAddr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                check.BlockReason = "The gateway is not a valid IPv4 address.";
                return check;
            }
            if (!System.Net.IPAddress.TryParse(netmask, out var maskAddr)
                || maskAddr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                check.BlockReason = "The subnet mask is not a valid IPv4 address.";
                return check;
            }

            uint ipV = ToUInt(ipAddr), gwV = ToUInt(gwAddr), maskV = ToUInt(maskAddr);

            // A netmask must be a contiguous run of 1 bits. 255.255.0.255 is a
            // classic typo that parses fine and breaks routing.
            uint inverted = ~maskV;
            if ((inverted & (inverted + 1)) != 0)
            {
                check.BlockReason = "The subnet mask is not valid — the one bits must be contiguous (for example 255.255.255.0).";
                return check;
            }
            if (maskV == 0xFFFFFFFF || maskV == 0)
            {
                check.BlockReason = "The subnet mask must leave room for hosts (for example 255.255.255.0).";
                return check;
            }

            uint network = ipV & maskV;
            uint broadcast = network | ~maskV;

            if (ipV == network)
            {
                check.BlockReason = "That IP address is the network address for this subnet and cannot be assigned to the radio.";
                return check;
            }
            if (ipV == broadcast)
            {
                check.BlockReason = "That IP address is the broadcast address for this subnet and cannot be assigned to the radio.";
                return check;
            }
            if ((gwV & maskV) != network)
            {
                check.BlockReason = "The gateway is not on the same subnet as the IP address. The radio would have no route off its network.";
                return check;
            }
            if (gwV == ipV)
            {
                check.BlockReason = "The gateway cannot be the same address as the radio.";
                return check;
            }

            // The lock-yourself-out check: if the proposed address isn't on the same
            // subnet we're currently reaching the radio on, this is very likely a typo
            // — and if it isn't, whoever applies it needs to know they're changing
            // networks deliberately.
            var currentIp = theRadio.IP;
            if (currentIp != null && currentIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                uint curV = ToUInt(currentIp);
                if ((curV & maskV) != network)
                {
                    check.Warnings.Add(
                        $"The radio is reachable at {currentIp} today, which is on a different subnet than {ip}. " +
                        "If that is not deliberate, the radio will be unreachable after it restarts and will need " +
                        "someone at the radio to fix it.");
                }
                else if (curV != ipV)
                {
                    check.Warnings.Add(
                        $"The radio's address will change from {currentIp} to {ip}. You will need to reconnect after it restarts.");
                }
            }

            var others = OtherConnectedStations;
            if (others.Count > 0)
                check.Warnings.Add("Other stations are connected and will need to reconnect: " + string.Join(", ", others));
            // Only clients the radio has confirmed are named above; the ones a
            // list or a broadcast reported are named here instead, as possibly
            // affected, and never in both (#634, Track L7).
            var caveat = UnconfirmedCompanyCaveat;
            if (caveat != null) check.Warnings.Add(caveat);

            check.CanProceed = true;
            return check;
        }

        private static uint ToUInt(System.Net.IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        /// <summary>
        /// A proposed static configuration built from what the radio is using right
        /// now. Backs the "use the current address" button.
        /// </summary>
        public sealed class SuggestedStaticConfig
        {
            public bool Available { get; set; }
            public string Reason { get; set; } = string.Empty;
            public string Ip { get; set; } = string.Empty;
            public string Gateway { get; set; } = string.Empty;
            public string Netmask { get; set; } = string.Empty;
            public List<string> Warnings { get; } = new List<string>();
        }

        /// <summary>
        /// Build a static configuration from the address the radio is currently
        /// using. This is the safest way to go static — you're pinning an address
        /// that demonstrably works on that network right now.
        ///
        /// Refuses over SmartLink. On a WAN connection <c>theRadio.IP</c> is the
        /// address we reach the radio *through*, not the radio's address on its own
        /// LAN. Pinning that as a static IP would make the radio unreachable and need
        /// physical access to undo — the exact outcome this whole feature exists to
        /// avoid.
        ///
        /// Gateway and netmask come from the radio when it reports them, otherwise
        /// they're inferred from whichever local adapter shares a subnet with the
        /// radio. Inference is flagged in Warnings so the user can sanity-check it.
        /// </summary>
        public SuggestedStaticConfig SuggestStaticFromCurrent()
        {
            var s = new SuggestedStaticConfig();

            if (theRadio == null || !IsConnected)
            {
                s.Reason = "No radio is connected.";
                return s;
            }

            if (theRadio.IsWan)
            {
                s.Reason =
                    "You are connected over SmartLink. The address JJ Flex sees is not the radio's address " +
                    "on its own network, so it cannot be used as a static IP. Connect on the same local " +
                    "network as the radio to use this.";
                return s;
            }

            var ip = theRadio.IP;
            if (ip == null || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                s.Reason = "The radio's current IPv4 address is not available.";
                return s;
            }
            s.Ip = ip.ToString();

            // Prefer whatever the radio itself reports.
            if (theRadio.StaticNetmask != null) s.Netmask = theRadio.StaticNetmask.ToString();
            if (theRadio.StaticGateway != null) s.Gateway = theRadio.StaticGateway.ToString();

            // Otherwise infer from the local adapter that shares the radio's subnet.
            if (string.IsNullOrEmpty(s.Netmask) || string.IsNullOrEmpty(s.Gateway))
            {
                try
                {
                    uint radioV = ToUInt(ip);
                    foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                        var props = nic.GetIPProperties();
                        foreach (var ua in props.UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                            var mask = ua.IPv4Mask;
                            if (mask == null || ToUInt(mask) == 0) continue;
                            uint maskV = ToUInt(mask);
                            if ((ToUInt(ua.Address) & maskV) != (radioV & maskV)) continue;

                            if (string.IsNullOrEmpty(s.Netmask))
                            {
                                s.Netmask = mask.ToString();
                                s.Warnings.Add($"The subnet mask {s.Netmask} was taken from this computer's network settings, not from the radio.");
                            }
                            if (string.IsNullOrEmpty(s.Gateway))
                            {
                                foreach (var gw in props.GatewayAddresses)
                                {
                                    if (gw?.Address == null) continue;
                                    if (gw.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                                    if (ToUInt(gw.Address) == 0) continue;
                                    s.Gateway = gw.Address.ToString();
                                    s.Warnings.Add($"The gateway {s.Gateway} was taken from this computer's network settings, not from the radio.");
                                    break;
                                }
                            }
                            break;
                        }
                        if (!string.IsNullOrEmpty(s.Netmask) && !string.IsNullOrEmpty(s.Gateway)) break;
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"SuggestStaticFromCurrent: adapter probe failed: {ex.Message}", TraceLevel.Error);
                }
            }

            if (string.IsNullOrEmpty(s.Netmask) || string.IsNullOrEmpty(s.Gateway))
            {
                s.Reason =
                    "The radio's current address is " + s.Ip + ", but the gateway and subnet mask could not be " +
                    "determined automatically. Enter them by hand.";
                // Still hand back the IP so the field can be filled in.
                s.Available = false;
                return s;
            }

            s.Warnings.Add(
                "This address was assigned by DHCP. Pinning it as static works, but the router could later hand " +
                "the same address to another device. A DHCP reservation on the router is the tidier fix where that is possible.");

            s.Available = true;
            return s;
        }

        /// <summary>
        /// Apply a static network configuration. Call <see cref="PreflightStaticIp"/>
        /// first and only call this when it reports CanProceed.
        ///
        /// FlexLib reports the outcome through the radio's StaticIPSetSuccessful /
        /// StaticIPSetFailed events rather than a return value, so callers pass
        /// handlers. Both are unsubscribed after the first fire.
        /// </summary>
        public bool ApplyStaticIp(string ip, string gateway, string netmask, Action onSuccess, Action onFailure)
        {
            if (theRadio == null || !IsConnected) return false;
            if (GuardRefuses("settings.guard.action.network")) return false;

            try
            {
                var r = theRadio;
                EventHandler okHandler = null, failHandler = null;

                okHandler = (s, e) =>
                {
                    r.StaticIPSetSuccessful -= okHandler;
                    r.StaticIPSetFailed -= failHandler;
                    Tracing.TraceLine($"ApplyStaticIp: radio accepted {ip}/{netmask} gw {gateway}", TraceLevel.Info);
                    onSuccess?.Invoke();
                };
                failHandler = (s, e) =>
                {
                    r.StaticIPSetSuccessful -= okHandler;
                    r.StaticIPSetFailed -= failHandler;
                    Tracing.TraceLine("ApplyStaticIp: radio rejected the static network parameters", TraceLevel.Error);
                    onFailure?.Invoke();
                };

                r.StaticIPSetSuccessful += okHandler;
                r.StaticIPSetFailed += failHandler;

                r.StaticIP = System.Net.IPAddress.Parse(ip);
                r.StaticGateway = System.Net.IPAddress.Parse(gateway);
                r.StaticNetmask = System.Net.IPAddress.Parse(netmask);
                r.SetStaticNetworkParams();
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ApplyStaticIp: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Put the radio back on DHCP. Same event-based outcome reporting as
        /// <see cref="ApplyStaticIp"/>.
        /// </summary>
        public bool RevertToDhcp(Action onSuccess, Action onFailure)
        {
            if (theRadio == null || !IsConnected) return false;
            if (GuardRefuses("settings.guard.action.network")) return false;

            try
            {
                var r = theRadio;
                EventHandler okHandler = null, failHandler = null;

                okHandler = (s, e) =>
                {
                    r.DHCPSetSuccessful -= okHandler;
                    r.DHCPSetFailed -= failHandler;
                    Tracing.TraceLine("RevertToDhcp: radio accepted DHCP", TraceLevel.Info);
                    onSuccess?.Invoke();
                };
                failHandler = (s, e) =>
                {
                    r.DHCPSetSuccessful -= okHandler;
                    r.DHCPSetFailed -= failHandler;
                    Tracing.TraceLine("RevertToDhcp: radio rejected the DHCP reset", TraceLevel.Error);
                    onFailure?.Invoke();
                };

                r.DHCPSetSuccessful += okHandler;
                r.DHCPSetFailed += failHandler;
                r.SetNetworkToDCHP();
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"RevertToDhcp: {ex.Message}", TraceLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Whether the radio refuses connections from non-private source addresses.
        /// Surfaced because Tailscale hands out CGNAT addresses (100.64.0.0/10), which
        /// are NOT RFC1918 — with this enabled a tailnet-attached client can be
        /// silently refused.
        /// </summary>
        public bool EnforcePrivateIPConnections
        {
            get
            {
                try { return theRadio != null && theRadio.EnforcePrivateIPConnections; }
                catch { return false; }
            }
            set
            {
                try
                {
                    if (theRadio == null) return;
                    if (GuardRefuses("settings.guard.action.private_ip")) return;
                    theRadio.EnforcePrivateIPConnections = value;
                    Tracing.TraceLine($"EnforcePrivateIPConnections set to {value}", TraceLevel.Info);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"EnforcePrivateIPConnections set failed: {ex.Message}", TraceLevel.Error);
                }
            }
        }

        #endregion
    }
}
