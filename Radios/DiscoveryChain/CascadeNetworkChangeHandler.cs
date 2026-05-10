using System;
using System.Diagnostics;
using System.Threading.Tasks;
using JJTrace;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// Lifetime owner for the singleton <see cref="NetworkChangeWatchdog"/>.
    /// Wired from <c>ApplicationEvents.vb</c> at app Startup / Shutdown so the
    /// watchdog observes <see cref="System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged"/>
    /// for the entire process lifetime.
    ///
    /// v1 callback behaviour (per TRACK-INSTRUCTIONS.md Sprint 29 Track L):
    /// <list type="bullet">
    ///   <item>Emit a <c>KEY_EVENT: network_change_observed</c> trace line so
    ///   the Sprint 29 trace-persistence manifest (when it lands) can index
    ///   network transitions for forensic correlation.</item>
    ///   <item>No automatic re-cascade. The cache's NLM-style network-identity
    ///   gate (per <c>72cc0edd</c>) already excludes prior-network LAN-IP
    ///   history entries on the next cascade read — invalidation is automatic
    ///   at probe time, no cache mutation needed here.</item>
    /// </list>
    ///
    /// Auto-reconnect on network change is intentionally deferred — it has its
    /// own consent surface (Settings → Connection → Discovery → "Re-search for
    /// my radio when my network changes") and design questions around what to
    /// do if the user is mid-QSO when their laptop roams off-network. Keeping
    /// v1 observe-only lets the watchdog ship with the foundation phase
    /// without those decisions blocking it.
    /// </summary>
    public static class CascadeNetworkChangeHandler
    {
        private static readonly object _sync = new();
        private static NetworkChangeWatchdog _watchdog;

        /// <summary>
        /// Construct + start the watchdog. Idempotent — subsequent calls no-op.
        /// Safe to call before any radio is connected; the callback only logs.
        /// </summary>
        public static void Start()
        {
            lock (_sync)
            {
                if (_watchdog != null) return;
                _watchdog = new NetworkChangeWatchdog(OnNetworkChangedAsync);
                _watchdog.Start();
            }
        }

        /// <summary>
        /// Stop and dispose the watchdog. Idempotent. Called from app shutdown
        /// to release the NetworkAddressChanged subscription cleanly — leaving
        /// it subscribed across process exit can prevent clean termination per
        /// the standard NLM hosting guidance.
        /// </summary>
        public static void Stop()
        {
            NetworkChangeWatchdog toDispose = null;
            lock (_sync)
            {
                if (_watchdog == null) return;
                toDispose = _watchdog;
                _watchdog = null;
            }
            try { toDispose.Dispose(); }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"CascadeNetworkChangeHandler: dispose threw {ex.GetType().Name}: {ex.Message}",
                    TraceLevel.Warning);
            }
        }

        private static Task OnNetworkChangedAsync()
        {
            // KEY_EVENT prefix is the convention the upcoming TraceSessionContext
            // (per memory/project_trace_persistence_design.md) will scan for to
            // populate manifest.json's key_events[]. Until that lands, this is
            // just an Info-level trace line — but the prefix is stable so the
            // future indexer doesn't need to re-tag historical archives.
            string netId = NetworkIdentity.GetCurrentNetworkId();
            string idDescr = string.IsNullOrEmpty(netId) ? "(none)" : netId;
            Tracing.TraceLine(
                $"KEY_EVENT: network_change_observed currentNetworkId={idDescr}",
                TraceLevel.Info);
            return Task.CompletedTask;
        }
    }
}
