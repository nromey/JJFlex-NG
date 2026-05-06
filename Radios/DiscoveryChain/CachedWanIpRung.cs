using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// Rung 1b — cached SmartLink target. For SmartLink-configured users we
    /// remember the radio's external connection metadata (public IP, TLS port,
    /// hole-punch requirement, port-forward state) from the most recent
    /// successful WAN connect. On the next connect attempt we reconstitute a
    /// <see cref="Radio"/> from cache and skip the round-trip wait for
    /// <c>radio list</c> from SmartLink.
    ///
    /// Per <c>docs/planning/research/smartlink-auth-direct-connect-feasibility.md</c>
    /// this is the constrained-case shape: we do NOT skip the SmartLink server
    /// query for the per-attempt <c>wan validate handle=</c>. The caller's
    /// remote-connect path (<c>FlexBase.sendRemoteConnect</c>) still issues
    /// <c>application connect</c> against the SmartLink server and feeds the
    /// returned handle into <c>Radio.WANConnectionHandle</c> before
    /// <c>Radio.Connect()</c>. The win is purely the elided radio-list refresh.
    ///
    /// Privacy: cached WAN IPs stay LOCAL ONLY. Never write them into trace
    /// exports, crash reports, or Data Provider sync. See
    /// <c>project_no_silent_phone_home.md</c>.
    /// </summary>
    public sealed class CachedWanIpRung : IDiscoveryRung
    {
        private readonly Func<bool> _isSmartLinkSessionActive;

        public CachedWanIpRung(Func<bool> isSmartLinkSessionActive)
        {
            _isSmartLinkSessionActive = isSmartLinkSessionActive
                ?? throw new ArgumentNullException(nameof(isSmartLinkSessionActive));
        }

        public string Name => "CachedWanIp";
        public bool Enabled { get; init; } = true;

        public Task<DiscoveryRungResult> TryAsync(DiscoveryContext ctx, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            if (ctx?.Cache == null || string.IsNullOrEmpty(ctx.Serial))
                return Task.FromResult(Fail(sw, "no_context"));

            // Rung 1b only applies to SmartLink-configured radios. For pure-LAN
            // setups, Rung 1a already handled the cache hit; falling through is
            // correct.
            if (!ctx.IsRemoteHint)
                return Task.FromResult(Fail(sw, "not_remote"));

            var entry = ctx.Cache.Lookup(ctx.Serial);
            if (entry == null || string.IsNullOrEmpty(entry.WanIp))
                return Task.FromResult(Fail(sw, "no_cache"));

            if (!IPAddress.TryParse(entry.WanIp, out var ip))
                return Task.FromResult(Fail(sw, "bad_cache_ip"));

            if (entry.PublicTlsPort <= 0)
                return Task.FromResult(Fail(sw, "no_cached_port"));

            // Per the research memo §4: we still need the SmartLink server
            // round-trip for the per-attempt handle. If SmartLink isn't up
            // yet, the caller's remote-connect path will run setupRemote()
            // first; we trust that the chain is being invoked AFTER the
            // SmartLink session is ready (the caller's job to sequence).
            // We do a soft check here purely to pick a sensible OutcomeTag.
            if (!_isSmartLinkSessionActive())
                return Task.FromResult(Fail(sw, "no_session"));

            var radio = Radio.CreateFromIpWan(
                entry.Model ?? "",
                entry.Serial,
                entry.Nickname ?? "",
                ip,
                entry.Version ?? "",
                entry.PublicTlsPort,
                entry.RequiresHolePunch,
                entry.IsPortForwardOn);

            sw.Stop();
            return Task.FromResult(new DiscoveryRungResult
            {
                Success = true,
                OutcomeTag = "success",
                Radio = radio,
                Elapsed = sw.Elapsed,
                // Diagnostic detail intentionally omits the WAN IP — local
                // trace is OK with it but the field is a redaction candidate
                // when support packages ship.
                DiagnosticDetail = $"port={entry.PublicTlsPort} holePunch={entry.RequiresHolePunch}"
            });
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
