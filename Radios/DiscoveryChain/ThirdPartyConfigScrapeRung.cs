using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using Radios.DiscoveryChain.Providers;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// Rung 1.7 — third-party config scrape. Single rung that walks all
    /// configured providers in priority order (SmartSDR-first, then
    /// alphabetical fallback per Q12 ACK 2026-05-08), aggregates candidate
    /// IPs, dedups, and runs identity verification in parallel via TCP probe
    /// to FlexLib command port 4992.
    ///
    /// Stream 3 hit-rate evidence: SmartSDR alone covers ~70% of new-user
    /// installs. Cumulative across all providers: high. Coverage of the
    /// "fresh JJF install on a machine that already has SmartSDR or other
    /// ham apps that know the radio's IP."
    ///
    /// Phase 2 ships with the SmartSDR provider only (see
    /// <see cref="DefaultProviders"/>). Subsequent commits add PowerSDR,
    /// WSJT-X, N1MM Logger+, Log4OM, and the rest of the prioritized walk
    /// per design memo §3 Rung 1.7.
    ///
    /// Per docs/planning/design/discovery-fallback-chain-v3.md §3 Rung 1.7.
    /// Q4 ACK: on-by-default with first-run disclosure + per-app toggles.
    /// </summary>
    public sealed class ThirdPartyConfigScrapeRung : IDiscoveryRung
    {
        private const int FlexLibCommandPort = 4992;
        private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(1500);

        private readonly IReadOnlyList<IThirdPartyConfigProvider> _providers;
        private readonly TimeSpan _probeTimeout;

        public ThirdPartyConfigScrapeRung(
            IEnumerable<IThirdPartyConfigProvider> providers = null,
            TimeSpan? probeTimeout = null)
        {
            _providers = (providers ?? DefaultProviders()).ToList();
            _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
        }

        /// <summary>
        /// Default provider list, ordered per Q12 ACK: SmartSDR-first, then
        /// alphabetical fallback. As of Phase 2 initial ship, only SmartSDR
        /// is implemented; the alphabetical-fallback section grows as
        /// subsequent providers are added.
        /// </summary>
        public static IEnumerable<IThirdPartyConfigProvider> DefaultProviders()
        {
            // Priority 1: SmartSDR family — highest hit-rate per Stream 3.
            yield return new SmartSdrConfigProvider();

            // Priority 2+ (alphabetical): PowerSDR, WSJT-X, N1MM, Log4OM,
            // fldigi, etc. Empty in Phase 2 initial ship; growing.
        }

        public string Name => "ThirdPartyConfigScrape";
        public bool Enabled { get; init; } = true;

        public async Task<DiscoveryRungResult> TryAsync(DiscoveryContext ctx, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            // Aggregate candidates from every provider. Each provider records
            // its own outcome via OutcomeTag for trace.
            var allCandidates = new List<ThirdPartyIpCandidate>();
            int providersChecked = 0;
            int providersWithResults = 0;

            foreach (var provider in _providers)
            {
                if (ct.IsCancellationRequested) break;
                providersChecked++;

                List<ThirdPartyIpCandidate> found;
                try
                {
                    found = provider.Discover() ?? new List<ThirdPartyIpCandidate>();
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine(
                        $"ThirdPartyConfigScrapeRung: {provider.Name} threw {ex.GetType().Name}: {ex.Message}",
                        TraceLevel.Warning);
                    continue;
                }

                Tracing.TraceLine(
                    $"ThirdPartyConfigScrapeRung: {provider.Name} -> {provider.OutcomeTag} ({found.Count} candidates)",
                    TraceLevel.Info);

                if (found.Count > 0)
                {
                    providersWithResults++;
                    allCandidates.AddRange(found);
                }
            }

            // Dedup by IP across providers, keeping first attribution.
            var dedupedCandidates = allCandidates
                .GroupBy(c => c.Ip, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (dedupedCandidates.Count == 0)
            {
                return Fail(sw, "no_ips_extracted",
                    $"providers={providersChecked}, with_results={providersWithResults}, total_candidates=0");
            }

            // Parse + filter to valid IPAddress objects.
            var probeIps = new List<(IPAddress Ip, ThirdPartyIpCandidate Source)>();
            foreach (var cand in dedupedCandidates)
            {
                if (IPAddress.TryParse(cand.Ip, out var addr))
                {
                    probeIps.Add((addr, cand));
                }
            }

            if (probeIps.Count == 0)
            {
                return Fail(sw, "no_parseable_ips",
                    $"candidates={dedupedCandidates.Count} but none parsed");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(_probeTimeout);

            var probeTasks = probeIps
                .Select(p => ProbeIpAsync(p.Ip, p.Source, linked.Token))
                .ToList();

            try
            {
                while (probeTasks.Count > 0)
                {
                    var first = await Task.WhenAny(probeTasks).ConfigureAwait(false);
                    probeTasks.Remove(first);

                    var (ok, winningIp, source) = await first.ConfigureAwait(false);
                    if (!ok) continue;

                    linked.Cancel();

                    // Cache lookup gives us model/nickname/version when available
                    // — the SmartSDR config rarely has all three, so we typically
                    // pass empties. Connect() will fill in via FlexLib handshake.
                    var entry = ctx.Cache?.Lookup(ctx.Serial);
                    var radio = Radio.CreateFromIp(
                        entry?.Model ?? "",
                        ctx.Serial ?? "",
                        entry?.Nickname ?? "",
                        winningIp,
                        entry?.Version ?? "");

                    sw.Stop();
                    return new DiscoveryRungResult
                    {
                        Success = true,
                        OutcomeTag = "success",
                        Radio = radio,
                        Elapsed = sw.Elapsed,
                        DiagnosticDetail =
                            $"won at {winningIp} via {source.Source} ({source.SourceDetail}) " +
                            $"after {sw.ElapsedMilliseconds}ms; " +
                            $"providers={providersChecked}, with_results={providersWithResults}, candidates={probeIps.Count}"
                    };
                }
            }
            catch (Exception ex)
            {
                return Fail(sw, "exception", ex.GetType().Name + ": " + ex.Message);
            }

            return Fail(sw, "all_probes_failed",
                $"providers={providersChecked}, candidates={probeIps.Count}, " +
                $"none answered on TCP/{FlexLibCommandPort}");
        }

        private static async Task<(bool Ok, IPAddress Ip, ThirdPartyIpCandidate Source)> ProbeIpAsync(
            IPAddress ip,
            ThirdPartyIpCandidate source,
            CancellationToken ct)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(ip, FlexLibCommandPort, ct).ConfigureAwait(false);
                return (true, ip, source);
            }
            catch
            {
                return (false, ip, source);
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
