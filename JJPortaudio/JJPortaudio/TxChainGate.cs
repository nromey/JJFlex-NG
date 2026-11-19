using System.Threading;

namespace JJPortaudio
{
    /// <summary>
    /// The one way into a shared transmit chain (the source mux, the
    /// conditioner, the loudness meter, the encoder and the send) for every
    /// producer that feeds it: each capture callback of each stream, for the
    /// stream's whole life, and the self-clocked source. Sprint 49 Track H2
    /// split, repair 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is not part of a capture hand-over.</b> Repair 1 kept two
    /// capture streams out of the chain with a flag on the hand-over object a
    /// swap creates. Codex's re-check of <c>ab5ba9a8</c> showed that flag was
    /// installed too late: a callback reads its stream's hand-over once, at its
    /// start, so one already inside the chain when the swap published a new
    /// hand-over held either no flag (the first swap) or the previous swap's
    /// (a later one). A forced take-over then let the replacement's callback in
    /// beside it. A guard created per swap can never cover a callback that began
    /// before the swap, so the exclusion lives on the CHAIN instead: one of
    /// these per chain, put on each producer's pipeline when it is opened,
    /// before it can produce anything, and never changed while it runs.
    /// </para>
    /// <para>
    /// <b>It never waits.</b> An audio callback that finds the chain taken
    /// feeds nothing for that buffer and returns. Nothing on the key path takes
    /// it, so a key-down or an unkey is never held behind a callback.
    /// </para>
    /// <para>
    /// <b>What that costs, honestly.</b> A producer that stalls inside the chain
    /// keeps every other producer out for as long as it stalls, which may be
    /// many buffers: the chain is never entered twice, and the price of that is
    /// a gap as long as the stall. See <see cref="Refusals"/>.
    /// </para>
    /// <para>No allocation: one compare-and-swap in, one write out.</para>
    /// </remarks>
    public sealed class TxChainGate
    {
        private int _busy;
        private long _refusals;

        /// <summary>Take the chain if nobody holds it. False means another producer is inside: feed nothing.</summary>
        public bool TryEnter()
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0) return true;
            Interlocked.Increment(ref _refusals);
            return false;
        }

        /// <summary>Give the chain back. Only for a producer whose <see cref="TryEnter"/> returned true.</summary>
        public void Leave() => Volatile.Write(ref _busy, 0);

        /// <summary>True while a producer is inside the chain.</summary>
        public bool Held => Volatile.Read(ref _busy) != 0;

        /// <summary>Buffers (or self-clocked pumps) that found the chain taken and fed nothing, since this gate was made.</summary>
        public long Refusals => Interlocked.Read(ref _refusals);
    }
}
