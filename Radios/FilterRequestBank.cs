namespace Radios
{
    /// <summary>
    /// One filter side's low/high pair as a layer holds it while writes are in
    /// flight (#747, round eight of the 2026-11-06 junk box). The pair is the
    /// NEWEST REQUEST: a key press takes it at once, so a held key keeps
    /// stepping. A completed write, reported later on the command loop, is
    /// adopted only if it answers the newest request. An older completion
    /// arriving after a newer request is ignored, so it can never roll the
    /// pair back past what the operator has since asked for.
    /// </summary>
    /// <remarks>
    /// <para>Found by Codex (re-verify 4, F2): with two writes queued, the
    /// first completion overwrote the optimistic pair, the next press stepped
    /// from that older value, and three +50 presses from 100 ended at 200.</para>
    /// <para>Every read and write is under one lock: key presses read and
    /// request on the UI thread, completions arrive on the command loop.</para>
    /// </remarks>
    public sealed class FilterRequestBank
    {
        private readonly object _gate = new object();
        private int _low, _high, _entryLow, _entryHigh;
        private bool _touched;
        private long _newest;

        public int Low { get { lock (_gate) return _low; } }
        public int High { get { lock (_gate) return _high; } }
        public int Width { get { lock (_gate) return _high - _low; } }
        public int EntryLow { get { lock (_gate) return _entryLow; } }
        public int EntryHigh { get { lock (_gate) return _entryHigh; } }
        public bool Touched { get { lock (_gate) return _touched; } }

        /// <summary>Both edges at once, from one moment.</summary>
        public (int Low, int High) Pair { get { lock (_gate) return (_low, _high); } }

        /// <summary>Seed from the radio at entry: the pair and the pair Escape puts back.</summary>
        public void Seed(int low, int high)
        {
            lock (_gate)
            {
                _low = _entryLow = low;
                _high = _entryHigh = high;
                _touched = false;
                _newest++;   // anything still in flight from before the seed is old
            }
        }

        /// <summary>
        /// A new request: the pair becomes it at once, and the returned ticket
        /// identifies it to <see cref="Complete"/>.
        /// </summary>
        public long Request(int low, int high)
        {
            lock (_gate)
            {
                _low = low;
                _high = high;
                _touched = true;
                return ++_newest;
            }
        }

        /// <summary>
        /// A write with no completion to wait for (cancel's restore): the pair
        /// becomes it, and anything still in flight is older than it.
        /// </summary>
        public void Set(int low, int high) => Request(low, high);

        /// <summary>
        /// A write has completed with these edges. Adopted only when it answers
        /// the newest request; returns whether it was.
        /// </summary>
        public bool Complete(long ticket, int low, int high)
        {
            lock (_gate)
            {
                if (ticket != _newest) return false;
                _low = low;
                _high = high;
                return true;
            }
        }
    }
}
