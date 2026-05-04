using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// Rung 1a — direct TCP probe to a previously-known LAN IP for the target
    /// radio. If the cache has an IP and the radio answers on FlexLib's command
    /// port (4992), we hand back a <see cref="Radio"/> built via the
    /// <c>Radio.CreateFromIp</c> vendor-patch factory and skip UDP discovery
    /// entirely. See <c>docs/planning/design/discovery-fallback-chain.md</c>.
    /// </summary>
    public sealed class CachedLanIpRung : IDiscoveryRung
    {
        private const int FlexLibCommandPort = 4992;
        private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(750);

        private readonly TimeSpan _probeTimeout;

        public CachedLanIpRung(TimeSpan? probeTimeout = null)
        {
            _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
        }

        public string Name => "CachedLanIp";
        public bool Enabled { get; init; } = true;

        public async Task<DiscoveryRungResult> TryAsync(DiscoveryContext ctx, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            if (ctx?.Cache == null || string.IsNullOrEmpty(ctx.Serial))
            {
                return Fail(sw, "no_context");
            }

            var entry = ctx.Cache.Lookup(ctx.Serial);
            if (entry == null || string.IsNullOrEmpty(entry.LanIp))
            {
                return Fail(sw, "no_cache");
            }

            if (!IPAddress.TryParse(entry.LanIp, out var ip))
            {
                return Fail(sw, "bad_cache_ip", entry.LanIp);
            }

            try
            {
                using var tcp = new TcpClient();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(_probeTimeout);

                await tcp.ConnectAsync(ip, FlexLibCommandPort, linked.Token).ConfigureAwait(false);

                // TCP accepted. We don't do a full FlexLib protocol handshake here —
                // the caller's Radio.Connect() will do that and fall through if the
                // radio doesn't speak FlexLib (e.g., port collision). For the common
                // case (the cached IP is still our radio), this is the win.
                var radio = Radio.CreateFromIp(
                    entry.Model ?? "",
                    entry.Serial,
                    entry.Nickname ?? "",
                    ip,
                    entry.Version ?? "");

                sw.Stop();
                return new DiscoveryRungResult
                {
                    Success = true,
                    OutcomeTag = "success",
                    Radio = radio,
                    Elapsed = sw.Elapsed
                };
            }
            catch (OperationCanceledException)
            {
                return Fail(sw, "tcp_timeout");
            }
            catch (SocketException ex)
            {
                return Fail(sw, "tcp_refused", ex.SocketErrorCode.ToString());
            }
            catch (Exception ex)
            {
                return Fail(sw, "exception", ex.GetType().Name + ": " + ex.Message);
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
