using System.Collections.Generic;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// One provider in the third-party config scrape rung (Rung 1.7). Each
    /// provider knows how to look at one ham-radio app's config files and
    /// extract candidate Flex-radio IPs. The Rung 1.7 dispatcher iterates
    /// providers in priority order (SmartSDR-first, then alphabetical
    /// fallback per Q12 ACK 2026-05-08), aggregates results, dedups, and
    /// runs identity verification in parallel.
    ///
    /// Per docs/planning/design/discovery-fallback-chain-v3.md §3 Rung 1.7.
    /// </summary>
    public interface IThirdPartyConfigProvider
    {
        /// <summary>
        /// Stable provider name used in trace output and chooser-attribution
        /// UX (Q13 ACK: "Found via SmartSDR config" / "Found via WSJT-X").
        /// Examples: "SmartSDR", "PowerSDR", "WSJT-X", "N1MM Logger+".
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Per-user toggle (Q4 ACK: per-app toggles for granular control).
        /// Defaults to <c>true</c>; user can disable specific providers in
        /// Settings → Connection → Discovery → Third-party config sources.
        /// </summary>
        bool Enabled { get; }

        /// <summary>
        /// Inspect this provider's config files and return any Flex-radio IP
        /// candidates found. Implementations must:
        /// <list type="bullet">
        ///   <item>Open files with <c>FileShare.ReadWrite</c> so running apps
        ///   don't lock the read.</item>
        ///   <item>Parse defensively — schema-aware first, IPv4 regex fallback.</item>
        ///   <item>Filter loopback (<c>127/8</c>) and obviously non-LAN
        ///   addresses (<c>0.0.0.0</c>, multicast, broadcast).</item>
        ///   <item>Never throw; return empty list on any error and let the
        ///   dispatcher record the outcome via <see cref="OutcomeTag"/> on the
        ///   last call.</item>
        /// </list>
        /// </summary>
        List<ThirdPartyIpCandidate> Discover();

        /// <summary>
        /// Last call's outcome tag, populated by <see cref="Discover"/>:
        /// <c>found</c> / <c>no_files</c> / <c>no_ips</c> / <c>parse_error</c>
        /// / <c>disabled</c>. Used by the dispatcher for trace.
        /// </summary>
        string OutcomeTag { get; }
    }

    /// <summary>
    /// One IP candidate from a third-party config scrape, with provenance.
    /// The <see cref="Source"/> field drives chooser-attribution UX per Q13
    /// ("Found via SmartSDR config").
    /// </summary>
    public sealed class ThirdPartyIpCandidate
    {
        public string Ip { get; init; } = "";
        public string Source { get; init; } = "";
        public string SourceDetail { get; init; } = "";
    }
}
