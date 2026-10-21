using System.Threading;

namespace Radios.StationConnect
{
    /// <summary>
    /// One process-wide, monotonic ordinal stamped on every station-connect
    /// observation that a policy may need to ORDER against an observation
    /// from another log: an own slice (the station tracker) against a global
    /// profile list (the profile evidence log). Each log has its own sequence,
    /// so neither can say which of two observations came first; the clock's
    /// milliseconds can tie. This can not.
    /// </summary>
    /// <remarks>
    /// Added for #639 (2026-10-20): the load-completion default requires the
    /// radio's post-load list re-report to have arrived AFTER the newest slice
    /// the load restored, which is a question across the two logs. A later
    /// slice then re-opens the wait for a later list, which is the measured
    /// order (slices, then the list 2 to 5 ms after the last of them). The
    /// ordinal says nothing about generations or attempts; the logs' own
    /// generation checks still do that.
    /// </remarks>
    public static class ObservationOrdinal
    {
        private static long _next;

        /// <summary>The next ordinal; strictly greater than every one handed out before it.</summary>
        public static long Next() => Interlocked.Increment(ref _next);
    }
}
