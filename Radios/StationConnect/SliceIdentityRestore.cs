using System;
using System.Collections.Generic;

namespace Radios.StationConnect
{
    /// <summary>
    /// Whether a receive or transmit slice captured BEFORE a client-local
    /// allocation may be reselected AFTER it. Only the same object, still a
    /// member of the current list, qualifies: a removed slice yields nothing,
    /// and a different slice that reused the index is not the operator's
    /// choice. Track G had moved this capture across the whole station phase
    /// and restored by numeric index regardless of route or cancellation
    /// (review section 8, second bullet); it is scoped to the allocation now
    /// and decided by identity.
    /// </summary>
    public static class SliceIdentityRestore
    {
        public sealed class Decision
        {
            public int RxPosition = -1;
            public int TxPosition = -1;
            public bool RestoreRx => RxPosition >= 0;
            public bool RestoreTx => TxPosition >= 0;
            public string Reason = "";
        }

        /// <summary>
        /// <paramref name="capturedRx"/> and <paramref name="capturedTx"/>
        /// are the slice OBJECTS captured before the allocation (null when
        /// there was none); <paramref name="current"/> is the client's slice
        /// list now, in position order.
        /// </summary>
        public static Decision Decide(object capturedRx, object capturedTx, IReadOnlyList<object> current, bool allocationCancelled)
        {
            var d = new Decision();
            if (allocationCancelled)
            {
                d.Reason = "the allocation was cancelled; nothing is replayed over whatever the radio did";
                return d;
            }
            if (current == null) { d.Reason = "no current slice list"; return d; }
            d.RxPosition = PositionOf(capturedRx, current);
            d.TxPosition = PositionOf(capturedTx, current);
            d.Reason = (capturedRx == null ? "no receive slice was captured" : d.RestoreRx ? "receive slice still present" : "the captured receive slice is no longer a member (removed, or its index reused by another)")
                + "; " + (capturedTx == null ? "no transmit slice was captured" : d.RestoreTx ? "transmit slice still present" : "the captured transmit slice is no longer a member");
            return d;
        }

        private static int PositionOf(object captured, IReadOnlyList<object> current)
        {
            if (captured == null) return -1;
            for (int i = 0; i < current.Count; i++)
            {
                if (ReferenceEquals(current[i], captured)) return i;
            }
            return -1;
        }
    }
}
