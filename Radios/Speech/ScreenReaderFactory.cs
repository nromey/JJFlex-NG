using System;
using System.Diagnostics;
using JJTrace;

namespace Radios.Speech
{
    /// <summary>
    /// Brings the speech backend up. Prism, or nothing.
    ///
    /// Prism handles the no-screen-reader case itself by acquiring SAPI, so a
    /// failure here means prism.dll is missing or broken — a deployment fault
    /// rather than an environment one. It is traced loudly and reported on
    /// every launch by <see cref="ScreenReaderOutput.TraceBackend"/>.
    /// </summary>
    public static class ScreenReaderFactory
    {
        public static IScreenReader Create()
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

            // Traced at Error deliberately. This is not a degraded mode, it is
            // an application that cannot talk to the person using it.
            Tracing.TraceLine(
                "Speech backend: NONE. Prism could not initialise, so the application "
                + "has no speech and no braille. prism.dll is expected at "
                + "runtimes\\win-{x64,x86}\\native\\prism.dll — check it shipped and "
                + "matches the process architecture.",
                TraceLevel.Error);
            return new NullScreenReader();
        }
    }
}
