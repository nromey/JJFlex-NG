using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using JJTrace;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// Background subscription to <see cref="NetworkChange.NetworkAddressChanged"/>.
    /// Detects when the laptop joins a new network and triggers a cascade
    /// re-run automatically — eliminates the "took my laptop from home shack
    /// to portable site, JJ Flex still tries the home IP" failure mode that
    /// Stream 7 §2.7 estimates accounts for 30-50% of "JJF doesn't see my
    /// radio" support cases.
    ///
    /// This is NOT a rung — it lives independently of any user-initiated
    /// discovery action. Its job: notice the environment changed and fire
    /// the cascade again so the user gets back to a working radio without
    /// having to do anything.
    ///
    /// On NetworkAddressChanged:
    /// <list type="number">
    ///   <item>Debounce 2.5s — Windows fires NAC 3-8 times in rapid succession
    ///   during a typical Wi-Fi reconnect; we want one re-cascade per
    ///   network-change burst.</item>
    ///   <item>Compare new network identity against the previous identity.
    ///   If unchanged, no-op (event was a renumber within the same network,
    ///   not a network change).</item>
    ///   <item>Cancel any in-flight cascade — its results are about a stale
    ///   environment.</item>
    ///   <item>Fire the user-supplied re-cascade callback.</item>
    /// </list>
    ///
    /// The actual re-cascade work (running the chain, speaking the result,
    /// updating UI) is the caller's responsibility via the
    /// <see cref="OnNetworkChanged"/> callback. This class only handles the
    /// detection / debouncing / identity-compare layer so it's testable
    /// independent of the cascade orchestrator.
    ///
    /// Configurable per <c>project_flexibility_principle.md</c>: the user
    /// can disable via <see cref="Enabled"/> (Settings → Connection →
    /// Discovery → "Re-search for my radio when my network changes"). Default:
    /// enabled.
    ///
    /// Per docs/planning/design/discovery-fallback-chain-v3.md §4 Background
    /// layer.
    /// </summary>
    public sealed class NetworkChangeWatchdog : IDisposable
    {
        private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(2500);

        private readonly object _sync = new();
        private readonly Func<Task> _onNetworkChanged;
        private CancellationTokenSource _debounceCts;
        private string _lastSeenNetworkId = "";
        private bool _subscribed;

        /// <summary>
        /// Construct with a callback that fires after a debounced network change
        /// has been confirmed. Caller is responsible for cancelling any in-flight
        /// cascade and starting the new one.
        /// </summary>
        public NetworkChangeWatchdog(Func<Task> onNetworkChanged)
        {
            _onNetworkChanged = onNetworkChanged
                ?? throw new ArgumentNullException(nameof(onNetworkChanged));
        }

        /// <summary>
        /// True when the watchdog should respond to network-change events.
        /// User-toggleable per the flexibility principle. Defaults to true;
        /// when set to false, NAC events are observed (still subscribed) but
        /// silently dropped — keeps the subscription warm so re-enabling is
        /// instant.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Begin listening to NetworkAddressChanged. Idempotent — calling Start
        /// twice does not double-subscribe.
        /// </summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_subscribed) return;
                _lastSeenNetworkId = NetworkIdentity.GetCurrentNetworkId();
                NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
                _subscribed = true;
            }
            Tracing.TraceLine(
                $"NetworkChangeWatchdog: started; baseline networkId={DescribeId(_lastSeenNetworkId)}",
                System.Diagnostics.TraceLevel.Info);
        }

        /// <summary>
        /// Unsubscribe. Idempotent. Safe to call from finalizer paths.
        /// </summary>
        public void Stop()
        {
            lock (_sync)
            {
                if (!_subscribed) return;
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                _subscribed = false;
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = null;
            }
            Tracing.TraceLine("NetworkChangeWatchdog: stopped", System.Diagnostics.TraceLevel.Info);
        }

        public void Dispose() => Stop();

        private void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            CancellationTokenSource priorCts;
            CancellationTokenSource newCts = new();
            lock (_sync)
            {
                if (!Enabled) return;
                priorCts = _debounceCts;
                _debounceCts = newCts;
            }

            // Cancel the in-flight debounce (if any) so this event resets the
            // 2.5s timer. Standard debounce: only the LAST event in a burst
            // gets to fire the action.
            try
            {
                priorCts?.Cancel();
                priorCts?.Dispose();
            }
            catch { /* defensive */ }

            _ = DebouncedFireAsync(newCts.Token);
        }

        private async Task DebouncedFireAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(DebounceWindow, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return; // a newer event preempted us
            }

            string priorNetId;
            string currentNetId = NetworkIdentity.GetCurrentNetworkId();
            lock (_sync)
            {
                priorNetId = _lastSeenNetworkId;
                _lastSeenNetworkId = currentNetId;
            }

            // Skip the event when the identity didn't actually change — this
            // is a renumber within the same network (DHCP renewal, IPv6 RA),
            // not a join-new-network event. Cascading on a renumber would be
            // wasted work because cached IPs are still correct.
            if (string.Equals(priorNetId, currentNetId, StringComparison.OrdinalIgnoreCase))
            {
                Tracing.TraceLine(
                    $"NetworkChangeWatchdog: debounce fired but networkId unchanged ({DescribeId(currentNetId)}); skipping re-cascade",
                    System.Diagnostics.TraceLevel.Info);
                return;
            }

            Tracing.TraceLine(
                $"NetworkChangeWatchdog: networkId changed {DescribeId(priorNetId)} -> {DescribeId(currentNetId)}; firing re-cascade",
                System.Diagnostics.TraceLevel.Info);

            try
            {
                await _onNetworkChanged().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"NetworkChangeWatchdog: re-cascade callback threw {ex.GetType().Name}: {ex.Message}",
                    System.Diagnostics.TraceLevel.Error);
            }
        }

        private static string DescribeId(string id) =>
            string.IsNullOrEmpty(id) ? "(none)" : id;
    }
}
