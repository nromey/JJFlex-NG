using System;
using System.Threading;

namespace Radios.StationConnect
{
    /// <summary>
    /// One connection attempt: the radio, its serial, the generation number
    /// every observation is stamped with, and a cancellation that invalidates
    /// all of it at once.
    /// </summary>
    /// <remarks>
    /// <para><b>Serial alone is not enough</b> (design section 3). A reconnect
    /// to the same radio has the same serial, so a callback from the previous
    /// connection would pass a serial check and complete the wrong wait. The
    /// generation is the thing a late callback cannot fake: it was minted for
    /// the attempt it belongs to and nothing else.</para>
    /// <para>Disconnect, radio replacement and operator cancel all call
    /// <see cref="Cancel"/>. Every wait in the coordinator checks
    /// <see cref="IsLive"/>; a cancelled attempt ends with
    /// <see cref="StationOutcome.Cancelled"/> and never sends another
    /// command.</para>
    /// </remarks>
    public sealed class ConnectionAttempt
    {
        private static int _nextGeneration;
        private int _cancelled;

        public ConnectionAttempt(string serial)
        {
            Serial = serial ?? "";
            Generation = Interlocked.Increment(ref _nextGeneration);
        }

        /// <summary>Process-unique, monotonic. Never reused.</summary>
        public int Generation { get; }

        public string Serial { get; }

        /// <summary>Why the attempt was cancelled, for the trace. Null while live.</summary>
        public string CancelReason { get; private set; }

        public bool IsLive => Volatile.Read(ref _cancelled) == 0;

        public bool IsCancelled => !IsLive;

        /// <summary>
        /// Invalidate the attempt. Idempotent; the first reason wins.
        /// Signals <see cref="Wake"/> so a coordinator blocked in a wait sees
        /// the cancellation at once rather than at its next tick.
        /// </summary>
        public void Cancel(string reason)
        {
            if (Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                CancelReason = reason ?? "cancelled";
            }
            Wake?.Invoke();
        }

        /// <summary>Raised whenever anything the coordinator may be waiting on
        /// has changed. Observations invoke it after publishing; the
        /// coordinator's waiter listens.</summary>
        public event Action Wake;

        /// <summary>Publish "something changed" to whoever is waiting.</summary>
        public void Signal() => Wake?.Invoke();

        /// <summary>True when an observation stamped with
        /// <paramref name="generation"/> belongs to this attempt.</summary>
        public bool Owns(int generation) => generation == Generation;

        public override string ToString() =>
            "attempt " + Generation + " on " + Serial + (IsLive ? "" : " (cancelled: " + CancelReason + ")");
    }
}
