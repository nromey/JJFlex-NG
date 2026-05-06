using System;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// One step in the discovery fallback chain. Each rung tries a different
    /// path to obtain a working <see cref="Radio"/> handle for a known serial;
    /// the first rung to return success wins. See
    /// <c>docs/planning/design/discovery-fallback-chain.md</c>.
    /// </summary>
    public interface IDiscoveryRung
    {
        /// <summary>
        /// Stable identifier used in trace output and config. Examples:
        /// "CachedLanIp", "CachedWanIp", "SubnetProbe", "UdpDiscovery".
        /// </summary>
        string Name { get; }

        /// <summary>
        /// User-configurable per <c>project_flexibility_principle.md</c>.
        /// Defaults are conservative (all rungs enabled) but the user can
        /// disable specific rungs or force "manual IP only."
        /// </summary>
        bool Enabled { get; }

        Task<DiscoveryRungResult> TryAsync(DiscoveryContext ctx, CancellationToken ct);
    }

    /// <summary>
    /// Inputs a rung needs to try its path.
    /// </summary>
    public sealed class DiscoveryContext
    {
        public string Serial { get; init; } = "";
        public string ConfigDirectory { get; init; } = "";
        public string OperatorName { get; init; } = "";
        public bool IsRemoteHint { get; init; }
        public RadioConnectionCache Cache { get; init; }
    }

    public sealed class DiscoveryRungResult
    {
        public bool Success { get; init; }

        /// <summary>
        /// Stable tag for trace + diagnostics. Per-rung vocabulary, e.g.
        /// "success", "no_cache", "tcp_timeout", "tcp_refused", "exception".
        /// </summary>
        public string OutcomeTag { get; init; } = "";

        public Radio Radio { get; init; }
        public TimeSpan Elapsed { get; init; }
        public string DiagnosticDetail { get; init; } = "";
    }
}
