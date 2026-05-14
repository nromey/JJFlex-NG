using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// Rung 1c — N-deep cached LAN IP history. Augments Rung 1a with the last
    /// N IPs we've seen the radio at (LRU-capped at <see cref="RadioConnectionCache.MaxHistoryDepth"/>).
    /// Solves the IP-rotation case (Stream 5 §3.14) where the radio's address
    /// shifts between two reserved DHCP pools, and the multi-network case
    /// (Stream 7 §2.8) where a contest station has home + portable + remote
    /// shack environments — the cached IP for one network is wrong for another,
    /// but a recent IP from the network we're currently on is in the history.
    ///
    /// Skips the first history entry (already tried by Rung 1a as
    /// <c>RadioConnectionCacheEntry.LanIp</c>) and probes the remainder in
    /// parallel via <see cref="Task.WhenAny"/>. First successful TCP connect
    /// to FlexLib's command port wins; remaining probes are cancelled.
    ///
    /// Aggressive per-IP timeout (1.5s default) keeps total wall-clock under
    /// 2s for an empty fan-out, per design memo §3.1c.
    ///
    /// Per docs/planning/design/discovery-fallback-chain-v3.md §3 Rung 1c.
    /// </summary>
    public sealed class CachedLanIpHistoryRung : IDiscoveryRung
    {
        private const int FlexLibCommandPort = 4992;
        private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(1500);

        private readonly TimeSpan _probeTimeout;

        public CachedLanIpHistoryRung(TimeSpan? probeTimeout = null)
        {
            _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
        }

        public string Name => "CachedLanIpHistory";
        public bool Enabled { get; init; } = true;

        public async Task<DiscoveryRungResult> TryAsync(DiscoveryContext ctx, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            if (ctx?.Cache == null || string.IsNullOrEmpty(ctx.Serial))
            {
                return Fail(sw, "no_context");
            }

            var entry = ctx.Cache.Lookup(ctx.Serial);
            if (entry == null)
            {
                return Fail(sw, "no_cache_entry");
            }

            // Skip the first history entry — Rung 1a already tried entry.LanIp,
            // which (under normal operation) is the most-recent history entry.
            // Re-probing it here would just duplicate Rung 1a's effort.
            var historyTail = entry.LanIpHistory?.Skip(1).ToList() ?? new List<HistoricalLanIp>();
            if (historyTail.Count == 0)
            {
                return Fail(sw, "no_history_to_probe");
            }

            // NLM-style network identity gating per Q5 ACK 2026-05-08:
            // prefer history entries captured on the current network. If
            // current identity is unknown OR no entries match, fall back
            // to probing all entries — the gate is an optimization, not a
            // refusal-to-connect (per Q5: "as long as it does not constrict
            // the user's ability to obtain an IP or connect for updating
            // purposes").
            string currentNetId = NetworkIdentity.GetCurrentNetworkId();
            int nlmFiltered = 0;
            List<HistoricalLanIp> probeHistory = historyTail;
            if (!string.IsNullOrEmpty(currentNetId))
            {
                var matching = historyTail
                    .Where(h => string.Equals(h.NetworkIdentityGuid, currentNetId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matching.Count > 0)
                {
                    nlmFiltered = historyTail.Count - matching.Count;
                    probeHistory = matching;
                }
                // else: no entries match current network. Fall through to
                // probing all (the radio may be on a network we haven't seen
                // before, or NLM IDs weren't captured for older entries).
            }

            // Parse + dedup against entry.LanIp (defensive — covers the case where
            // Rung 1a's IP isn't actually at position 0 of the history due to
            // an old-format file upgrade or out-of-band edit).
            var probeIps = new List<IPAddress>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(entry.LanIp)) seen.Add(entry.LanIp);

            foreach (var hist in probeHistory)
            {
                if (string.IsNullOrEmpty(hist?.Ip)) continue;
                if (!seen.Add(hist.Ip)) continue; // dup with entry.LanIp or earlier history
                if (!IPAddress.TryParse(hist.Ip, out var ip)) continue;
                probeIps.Add(ip);
            }

            if (probeIps.Count == 0)
            {
                return Fail(sw, "no_probable_ips");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(_probeTimeout);

            var probeTasks = probeIps
                .Select(ip => ProbeIpAsync(ip, linked.Token))
                .ToList();

            try
            {
                while (probeTasks.Count > 0)
                {
                    var first = await Task.WhenAny(probeTasks).ConfigureAwait(false);
                    probeTasks.Remove(first);

                    var (ok, winningIp) = await first.ConfigureAwait(false);
                    if (!ok) continue;

                    // Cancel the remaining probes; they're now wasted work.
                    linked.Cancel();

                    var radio = Radio.CreateFromIp(
                        entry.Model ?? "",
                        entry.Serial,
                        entry.Nickname ?? "",
                        winningIp,
                        entry.Version ?? "");

                    sw.Stop();
                    return new DiscoveryRungResult
                    {
                        Success = true,
                        OutcomeTag = "success",
                        Radio = radio,
                        Elapsed = sw.Elapsed,
                        DiagnosticDetail = $"won at {winningIp} after {sw.ElapsedMilliseconds}ms; probed {probeIps.Count} historical IPs (nlm_filtered={nlmFiltered})"
                    };
                }
            }
            catch (Exception ex)
            {
                return Fail(sw, "exception", ex.GetType().Name + ": " + ex.Message);
            }

            return Fail(sw, "all_probes_failed", $"probed {probeIps.Count} historical IPs (nlm_filtered={nlmFiltered}), none answered on TCP/{FlexLibCommandPort}");
        }

        /// <summary>
        /// Probe one IP for FlexLib's command port. Returns (success, ip) so
        /// the caller can identify which IP won when Task.WhenAny resolves.
        /// </summary>
        private static async Task<(bool Ok, IPAddress Ip)> ProbeIpAsync(IPAddress ip, CancellationToken ct)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(ip, FlexLibCommandPort, ct).ConfigureAwait(false);
                return (true, ip);
            }
            catch
            {
                // Any failure (timeout, refused, unreachable, exception) is just
                // "this IP doesn't work" — let Task.WhenAny see the next probe.
                return (false, ip);
            }
        }

        private static DiscoveryRungResult Fail(Stopwatch sw, string tag, string detail = "")
        {
            sw.Stop();
            return new DiscoveryRungResult
            {
                Success = false,
                OutcomeTag = tag,
                Elapsed = sw.Elapsed,
                DiagnosticDetail = detail
            };
        }
    }
}
