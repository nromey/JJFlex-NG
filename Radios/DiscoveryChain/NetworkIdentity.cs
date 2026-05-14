using System;
using System.Linq;
using System.Net.NetworkInformation;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// Phase 1 network-identity helper. Returns a stable-ish string identifying
    /// the current LAN that JJ Flex is reaching the world through. Used to gate
    /// cached-IP rungs (Rung 1a, 1c, future 1.5a/b/6/7) so we don't probe
    /// stale IPs from a foreign network — per Q5 ACK 2026-05-08:
    ///
    ///   "Strict gating is fine as long as it does not constrict the user's
    ///    ability to obtain an IP or connect for updating purposes."
    ///
    /// The Phase 1 implementation returns the GUID of the active adapter that
    /// owns the default gateway. This is a SUBSET of full NLM identity — full
    /// NLM (via INetworkListManager) persists identity across DHCP changes
    /// using gateway-MAC fingerprint, which adapter-GUID alone misses. The
    /// adapter-GUID approach catches the common case (user switched from
    /// home WiFi to coffee shop) while staying dependency-free; full NLM via
    /// COM can be a Phase 2 refinement if a tester surfaces the
    /// gateway-MAC-changes-but-network-is-the-same case.
    ///
    /// Returns empty string when no clearly-active adapter is detectable.
    /// Empty string is the signal "skip the gate, probe everything" so the
    /// cascade falls through to next-rung rather than blocking on unknown
    /// identity (per Q5's anti-constriction principle).
    ///
    /// Per docs/planning/design/discovery-fallback-chain-v3.md §5.3 NLM-based
    /// network identity gating.
    /// </summary>
    public static class NetworkIdentity
    {
        /// <summary>
        /// Returns the network-identity string for the LAN we're currently
        /// reaching the gateway through. Best-effort; never throws.
        /// </summary>
        public static string GetCurrentNetworkId()
        {
            try
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                NetworkInterface candidate = interfaces
                    .Where(IsCandidateAdapter)
                    .Select(nic => new
                    {
                        Nic = nic,
                        DefaultGateway = nic.GetIPProperties().GatewayAddresses
                            .Select(g => g.Address)
                            .FirstOrDefault(a => a != null && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    })
                    .Where(x => x.DefaultGateway != null)
                    .OrderByDescending(x => x.Nic.Speed) // prefer fastest interface when multiple have gateways
                    .Select(x => x.Nic)
                    .FirstOrDefault();

                if (candidate == null) return string.Empty;

                // NetworkInterface.Id is the adapter GUID on Windows, a stable string
                // for the lifetime of the adapter (it does NOT change across reboots
                // or DHCP renewals). Different adapter (e.g. WiFi vs Ethernet) gives
                // a different Id, which is exactly the gate we want for "did the
                // user move networks."
                return candidate.Id ?? string.Empty;
            }
            catch
            {
                // Any failure → empty string → cascade does not gate, probes everything.
                return string.Empty;
            }
        }

        /// <summary>
        /// Two network identities match if they're equal (case-insensitive) AND
        /// at least one is non-empty. Empty-vs-empty returns false because we
        /// can't claim a positive match without evidence.
        /// </summary>
        public static bool MatchesCurrent(string capturedId)
        {
            if (string.IsNullOrEmpty(capturedId)) return false;
            string current = GetCurrentNetworkId();
            if (string.IsNullOrEmpty(current)) return false;
            return string.Equals(capturedId, current, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCandidateAdapter(NetworkInterface nic)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) return false;
            // Exclude obvious non-LAN interfaces. Loopback is the only one we
            // can rule out from NetworkInterfaceType cleanly without depending
            // on the adapter description string. Hyper-V / WSL2 / VPN virtual
            // adapters CAN look like Ethernet — they get filtered later in the
            // ARP/probe step (Rung 1.5a). Here we just need a reasonable
            // default-gateway candidate.
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) return false;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return false;
            return true;
        }
    }
}
