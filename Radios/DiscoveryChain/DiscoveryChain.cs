using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios.DiscoveryChain
{
    /// <summary>
    /// Sequential first-success-wins orchestrator over a list of
    /// <see cref="IDiscoveryRung"/>s. Sequential is the R6 shape; concurrent
    /// execution is Phase 3 of the design memo.
    /// </summary>
    public sealed class DiscoveryChainRunner
    {
        private readonly IReadOnlyList<IDiscoveryRung> _rungs;

        public DiscoveryChainRunner(IEnumerable<IDiscoveryRung> rungs)
        {
            _rungs = rungs?.ToList() ?? new List<IDiscoveryRung>();
        }

        public async Task<DiscoveryChainResult> RunAsync(DiscoveryContext ctx, CancellationToken ct)
        {
            var attempts = new List<DiscoveryRungAttempt>();
            var totalSw = Stopwatch.StartNew();

            foreach (var rung in _rungs)
            {
                if (ct.IsCancellationRequested) break;
                if (!rung.Enabled)
                {
                    attempts.Add(new DiscoveryRungAttempt
                    {
                        RungName = rung.Name,
                        OutcomeTag = "disabled",
                        Elapsed = TimeSpan.Zero
                    });
                    continue;
                }

                Tracing.TraceLine($"DiscoveryChain: trying {rung.Name} for serial={ctx.Serial}", TraceLevel.Info);

                DiscoveryRungResult result;
                try
                {
                    result = await rung.TryAsync(ctx, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"DiscoveryChain: {rung.Name} threw {ex.GetType().Name}: {ex.Message}", TraceLevel.Error);
                    result = new DiscoveryRungResult
                    {
                        OutcomeTag = "exception",
                        DiagnosticDetail = ex.GetType().Name + ": " + ex.Message
                    };
                }

                attempts.Add(new DiscoveryRungAttempt
                {
                    RungName = rung.Name,
                    OutcomeTag = result.OutcomeTag,
                    Elapsed = result.Elapsed,
                    DiagnosticDetail = result.DiagnosticDetail
                });

                // Per-rung outcome at Info regardless of success/failure: when a
                // diagnostic build is the whole point (R5/R6/R7…), the boot trace
                // runs at Info and Verbose lines never make it to disk. Hiding
                // failure tags behind Verbose left R6 unable to surface no_cache /
                // bad_cache_ip / tcp_refused — exactly the data we needed from
                // Don's 2026-05-06 trace. See
                // memory/project_autoconnect_no_ip_dead_end.md.
                Tracing.TraceLine(
                    $"DiscoveryChain: {rung.Name} -> {result.OutcomeTag} in {result.Elapsed.TotalMilliseconds:F0}ms" +
                    (string.IsNullOrEmpty(result.DiagnosticDetail) ? "" : $" ({result.DiagnosticDetail})"),
                    TraceLevel.Info);

                if (result.Success && result.Radio != null)
                {
                    totalSw.Stop();
                    Tracing.TraceLine(
                        $"DiscoveryChain: WON via {rung.Name} after {attempts.Count} rung(s), total {totalSw.ElapsedMilliseconds}ms",
                        TraceLevel.Info);
                    return new DiscoveryChainResult
                    {
                        Success = true,
                        WinningRung = rung.Name,
                        Radio = result.Radio,
                        Attempts = attempts,
                        TotalElapsed = totalSw.Elapsed
                    };
                }
            }

            totalSw.Stop();
            Tracing.TraceLine(
                $"DiscoveryChain: NO RUNG SUCCEEDED ({attempts.Count} tried, total {totalSw.ElapsedMilliseconds}ms)",
                TraceLevel.Warning);
            return new DiscoveryChainResult
            {
                Success = false,
                Attempts = attempts,
                TotalElapsed = totalSw.Elapsed
            };
        }
    }

    public sealed class DiscoveryChainResult
    {
        public bool Success { get; init; }
        public string WinningRung { get; init; } = "";
        public Radio Radio { get; init; }
        public IReadOnlyList<DiscoveryRungAttempt> Attempts { get; init; } = Array.Empty<DiscoveryRungAttempt>();
        public TimeSpan TotalElapsed { get; init; }
    }

    public sealed class DiscoveryRungAttempt
    {
        public string RungName { get; init; } = "";
        public string OutcomeTag { get; init; } = "";
        public TimeSpan Elapsed { get; init; }
        public string DiagnosticDetail { get; init; } = "";
    }
}
