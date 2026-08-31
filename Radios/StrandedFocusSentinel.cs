#nullable enable

namespace Radios
{
    /// <summary>
    /// Decision policy for the stranded-focus sentinel: the periodic check an
    /// open dialog runs so a keyboard black hole cannot outlive a few seconds.
    ///
    /// <para><b>The case this exists for (2026-08-30, session one).</b> A
    /// dialog left open and unattended — Settings that day; the radio picker
    /// on other days — lost the foreground, and from then on keys reached
    /// nothing and the screen reader had nothing to announce. The application
    /// was healthy the whole time; there was simply no window anywhere taking
    /// input. Nothing in the app ever repaired it, because the only repair we
    /// had (the dialog-close focus-return landing) fires on CLOSE, and this
    /// dialog was not closing. The operator sat in silence for about 195
    /// seconds until an OS-level escape (Alt+Tab) happened to land him
    /// somewhere real. For a blind operator that state is indistinguishable
    /// from a crash — he killed the process on other occurrences of the same
    /// class.</para>
    ///
    /// <para><b>What may be repaired, and what must never be.</b> Only the
    /// two provable black holes: no foreground window anywhere on the
    /// desktop, and a foreground window of OUR OWN process whose thread has
    /// no focus window. A FOREIGN foreground is the operator being somewhere
    /// else — reading email while the picker sits open is a choice, not a
    /// fault — and repairing over it would yank the foreground out from
    /// under him on a timer, which is worse than the outage. That rule is
    /// pinned by tests; anyone loosening it is reintroducing focus theft.</para>
    ///
    /// <para><b>Debounced.</b> Window churn legitimately passes through
    /// no-foreground moments (one window closing into the next opening). Two
    /// consecutive bad observations — about four seconds — separate a
    /// transition from a black hole. The cost of the debounce is four
    /// seconds of outage in the real case, against 195 in the field.</para>
    /// </summary>
    public sealed class StrandedFocusSentinel
    {
        /// <summary>
        /// How often an open dialog looks. Two GetForegroundWindow-class
        /// calls per tick — nothing. Frequent enough that debounced repair
        /// lands within about four seconds; infrequent enough to be inert.
        /// </summary>
        public const int CheckIntervalMs = 2_000;

        /// <summary>
        /// Consecutive bad observations before repair. Two, so a single
        /// mid-transition sample (one window closing into the next) never
        /// triggers; see the class remarks.
        /// </summary>
        public const int ConsecutiveBadObservationsBeforeRepair = 2;

        /// <summary>One tick's worth of looking at the desktop.</summary>
        public enum Observation
        {
            /// <summary>A window of ours has the foreground and its thread
            /// has a focus window — the operator is somewhere real, in us.</summary>
            Healthy,

            /// <summary>Another process owns the foreground. The operator
            /// went somewhere else, or something took it — either way it is
            /// a real window taking his keys, and NEVER ours to steal back
            /// on a timer.</summary>
            ForeignForeground,

            /// <summary>GetForegroundWindow returned nothing: no window
            /// anywhere on the desktop is taking input. Keys go nowhere,
            /// screen readers have nothing to follow. The black hole.</summary>
            NoForegroundAnywhere,

            /// <summary>The foreground is a window of our own process, but
            /// its thread has no focus window — activation stranded on a
            /// shell whose focused control is gone. Ours, so ours to fix.</summary>
            OursWithDeadFocus,
        }

        private int _consecutiveBad;

        /// <summary>
        /// Record one observation; true means "repair now". The counter
        /// resets on every healthy or foreign observation, and after each
        /// repair so a repair that does not take is retried a full debounce
        /// later rather than every tick.
        /// </summary>
        public bool NoteAndDecide(Observation observation)
        {
            switch (observation)
            {
                case Observation.NoForegroundAnywhere:
                case Observation.OursWithDeadFocus:
                    _consecutiveBad++;
                    if (_consecutiveBad >= ConsecutiveBadObservationsBeforeRepair)
                    {
                        _consecutiveBad = 0;
                        return true;
                    }
                    return false;

                default:
                    _consecutiveBad = 0;
                    return false;
            }
        }
    }
}
