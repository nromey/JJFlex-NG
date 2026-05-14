using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// Rung 4 — SmartLink-as-LAN-fallback. When all prior LAN-side rungs
    /// returned no result AND the user has an active SmartLink session,
    /// fall through to a SmartLink connect. Slower (cloud round-trip) but
    /// works when the LAN is misbehaving — matches the design memo's
    /// purpose statement.
    ///
    /// Phase 2 MVP shape: this rung only fires when a SmartLink session is
    /// already active (per <c>FlexBase.IsSmartLinkSessionActive</c>). The
    /// auto-open-SmartLink-from-rung path requires touching the WPF auth
    /// flow (<c>AuthFormWebView2.cs</c>) and is deferred to a Phase 2.x
    /// follow-up commit.
    ///
    /// The "SmartLink-as-fallback-row UX" from S4 Pattern A — populating
    /// the radio chooser ListBox with SmartLink radios immediately at
    /// cascade start so LAN-discovered radios overwrite SmartLink rows as
    /// they resolve — is UI integration work that lives in the radio
    /// chooser (not in this rung). Tracked separately as a UI commit.
    ///
    /// Per docs/planning/design/discovery-fallback-chain-v3.md §3 Rung 4.
    /// LOCKED from round 2; v3 ACK 2026-05-08 Pattern A.
    /// </summary>
    public sealed class SmartLinkFallbackRung : IDiscoveryRung
    {
        private readonly Func<bool> _isSmartLinkSessionActive;
        private readonly Func<string, Radio> _smartLinkRadioByserialLookup;

        /// <param name="isSmartLinkSessionActive">
        /// Callback returning whether SmartLink is currently authenticated and
        /// session is up. Provided by FlexBase (same callback as
        /// <see cref="CachedWanIpRung"/>'s constructor argument).
        /// </param>
        /// <param name="smartLinkRadioByserialLookup">
        /// Callback returning the SmartLink-known Radio handle for the given
        /// serial, or null if the SmartLink coordinator doesn't know that
        /// radio (or doesn't currently have it as online). Provided by the
        /// SmartLink layer in FlexBase.
        /// </param>
        public SmartLinkFallbackRung(
            Func<bool> isSmartLinkSessionActive,
            Func<string, Radio> smartLinkRadioByserialLookup)
        {
            _isSmartLinkSessionActive = isSmartLinkSessionActive
                ?? throw new ArgumentNullException(nameof(isSmartLinkSessionActive));
            _smartLinkRadioByserialLookup = smartLinkRadioByserialLookup
                ?? throw new ArgumentNullException(nameof(smartLinkRadioByserialLookup));
        }

        public string Name => "SmartLinkFallback";
        public bool Enabled { get; init; } = true;

        public Task<DiscoveryRungResult> TryAsync(DiscoveryContext ctx, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();

            if (ctx == null || string.IsNullOrEmpty(ctx.Serial))
                return Task.FromResult(Fail(sw, "no_context"));

            // Phase 2 gate: only fires when SmartLink is already authenticated
            // and the session is up. The auto-open-SmartLink path is a Phase
            // 2.x follow-up.
            if (!_isSmartLinkSessionActive())
                return Task.FromResult(Fail(sw, "no_smartlink_session",
                    "SmartLink session not active; cannot trigger fallback connect"));

            // Ask the SmartLink layer for a Radio handle keyed by serial.
            // FlexBase's callback consults the SmartLink coordinator's most-
            // recent radio list. Returns null if SmartLink doesn't know this
            // serial OR if the radio is currently offline per SmartLink's
            // own bookkeeping.
            Radio radio;
            try
            {
                radio = _smartLinkRadioByserialLookup(ctx.Serial);
            }
            catch (Exception ex)
            {
                return Task.FromResult(Fail(sw, "exception",
                    ex.GetType().Name + ": " + ex.Message));
            }

            if (radio == null)
                return Task.FromResult(Fail(sw, "smartlink_no_radios",
                    $"SmartLink coordinator didn't return a radio for serial {ctx.Serial}"));

            sw.Stop();
            return Task.FromResult(new DiscoveryRungResult
            {
                Success = true,
                OutcomeTag = "success",
                Radio = radio,
                Elapsed = sw.Elapsed,
                DiagnosticDetail = $"resolved via SmartLink in {sw.ElapsedMilliseconds}ms"
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
