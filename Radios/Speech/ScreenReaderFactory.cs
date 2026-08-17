using System;
using System.Diagnostics;
using JJTrace;

namespace Radios.Speech
{
    /// <summary>
    /// Chooses and initialises the speech backend.
    ///
    /// Order: Prism first, Tolk as a fallback. Prism never hard-fails startup —
    /// if it cannot initialise for any reason (missing prism.dll, no backend,
    /// init error) the factory disposes it and brings Tolk up instead. An
    /// operator who cannot see the screen must not be left in silence because
    /// a native library moved.
    ///
    /// **The Tolk rung is temporary.** Once Prism is confirmed on real hardware
    /// with NVDA and JAWS, both the fallback and Tolk's four native DLLs per
    /// architecture come out. See <see cref="TolkScreenReader"/>.
    /// </summary>
    public static class ScreenReaderFactory
    {
        /// <summary>
        /// Overrides the backend for one launch, so Prism and Tolk can be
        /// compared on the same machine without a rebuild. Accepts "prism" or
        /// "tolk", case-insensitively; anything else is ignored with a trace.
        /// </summary>
        public const string EnvVar = "JJFLEX_SCREEN_READER_BACKEND";

        public static IScreenReader Create()
        {
            var forced = Environment.GetEnvironmentVariable(EnvVar)?.Trim();

            bool wantTolk = string.Equals(forced, "tolk", StringComparison.OrdinalIgnoreCase);
            bool wantPrism = string.Equals(forced, "prism", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(forced) && !wantTolk && !wantPrism)
            {
                Tracing.TraceLine(
                    $"{EnvVar}='{forced}' not recognised (expected prism or tolk) — using the default order.",
                    TraceLevel.Warning);
            }
            else if (!string.IsNullOrEmpty(forced))
            {
                Tracing.TraceLine($"{EnvVar}={forced} overrides the default backend order.", TraceLevel.Info);
            }

            if (!wantTolk)
            {
                var prism = new PrismScreenReader();
                if (prism.Initialize())
                {
                    Tracing.TraceLine(
                        $"Speech backend: Prism (reader: {prism.DetectedReader ?? "unknown"}, "
                        + $"braille={prism.HasBraille}).",
                        TraceLevel.Info);
                    return prism;
                }
                prism.Dispose();
                if (wantPrism)
                    Tracing.TraceLine("Prism was forced but is unavailable — falling back to Tolk anyway, "
                                      + "because silence is not an acceptable outcome.", TraceLevel.Warning);
                else
                    Tracing.TraceLine("Prism unavailable — falling back to Tolk.", TraceLevel.Warning);
            }

            var tolk = new TolkScreenReader();
            bool ok = tolk.Initialize();
            Tracing.TraceLine(
                $"Speech backend: Tolk (reader: {tolk.DetectedReader ?? "none detected"}, loaded={ok}).",
                TraceLevel.Info);
            return tolk;
        }
    }
}
