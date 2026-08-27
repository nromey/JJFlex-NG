using System;

namespace Radios
{
    /// <summary>
    /// Decides WHEN the speech channel should be re-bound because the operator
    /// changed screen readers under a running application (#283).
    ///
    /// Pure policy: observations in, a decision out. No timers, no Win32, no
    /// speech. The host (<see cref="ScreenReaderOutput"/>) owns the tick and
    /// performs the rebind; this class only says whether it is time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fault this exists for.</b> The speech backend is bound once at
    /// startup. Prism's own availability enumerator can move it later, but only
    /// on the RISING edge of a reader becoming available, and that edge is
    /// discarded while a controller reader is already held. Start JAWS while
    /// NVDA is still running and the rise is thrown away; let NVDA exit a
    /// moment later and the binding is marked dead with nothing left to
    /// re-trigger it, because JAWS is already available and will not rise
    /// again. The application then talks to a reader that has gone, forever —
    /// exactly what the operator measured on 2026-08-26, and exactly why
    /// relaunching with JAWS already up worked perfectly.
    /// </para>
    /// <para>
    /// <b>Why a watchdog rather than an event.</b> The event we would need is
    /// the one that was lost. A poll cannot miss an edge because it does not
    /// look at edges: it compares the reader that is actually running against
    /// the reader we are bound to, and any disagreement that persists is a
    /// rebind, no matter which sequence of appearances and disappearances
    /// produced it. That covers reader-to-reader, reader-to-none-to-reader and
    /// none-to-reader identically, which is the point — the direction of the
    /// swap was never supposed to matter, and it did.
    /// </para>
    /// <para>
    /// <b>Settling, and why the two cases differ.</b> A reader that has gone
    /// away is very often a reader that is coming back: NVDA restarts are
    /// routine, and during one the machine genuinely has no reader for a couple
    /// of seconds. Acting on that instantly would rebind to a synthesiser and
    /// then rebind back, so "no reader" needs a longer run of agreement than "a
    /// different reader", which is unambiguous the moment it is seen. Both runs
    /// also give Prism's own recovery the first chance to fix things by itself;
    /// when it does, the next observation agrees and this policy stands down
    /// without having acted.
    /// </para>
    /// <para>
    /// <b>The probe may not work, and that is not the same as no reader.</b>
    /// A failed probe reports <see cref="Decision.StandDown"/>, never a change.
    /// "I looked and saw nothing" and "I cannot look" are different claims, and
    /// treating them alike would tear a working binding down on a machine where
    /// the observation was simply unavailable.
    /// </para>
    /// </remarks>
    public sealed class ScreenReaderWatch
    {
        /// <summary>What the host should do with this observation.</summary>
        public enum Decision
        {
            /// <summary>Nothing to do — the binding matches what is running.</summary>
            Hold,

            /// <summary>
            /// The probe could not be trusted this tick. Explicitly NOT a
            /// change: an unusable instrument reports nothing, not absence.
            /// </summary>
            StandDown,

            /// <summary>
            /// A disagreement has now persisted long enough. Flush pending
            /// speech and re-bind the backend.
            /// </summary>
            Rebind,
        }

        /// <summary>
        /// Consecutive agreeing observations before a DIFFERENT named reader is
        /// acted on. A named reader is unambiguous, so this only has to outlast
        /// Prism's own two-sample availability debounce and its re-acquire
        /// attempt, giving the mechanism that is supposed to handle this the
        /// first chance to do so.
        /// </summary>
        public const int ReaderSettleTicks = 3;

        /// <summary>
        /// Consecutive agreeing observations before "no reader at all" is acted
        /// on. Longer than <see cref="ReaderSettleTicks"/> on purpose: a reader
        /// that vanished is usually a reader restarting, and a rebind to a
        /// synthesiser followed by a rebind back is worse for the operator than
        /// two extra seconds of waiting.
        /// </summary>
        public const int NoReaderSettleTicks = 5;

        /// <summary>
        /// What the application currently believes it is speaking through, and
        /// whether that belief is a controller reader (as opposed to UI
        /// Automation or a raw synthesiser).
        /// </summary>
        private string? _boundReader;
        private bool _boundIsControllerReader;

        /// <summary>The candidate currently accumulating agreement, and how much it has.</summary>
        private string? _candidate;
        private bool _candidateIsNone;
        private int _agreeingTicks;

        /// <summary>
        /// True between emitting a Rebind and the host reporting what it landed
        /// on. Without it a slow rebind would be asked for again on the very
        /// next tick, and again, while the first one was still running.
        /// </summary>
        private bool _awaitingRebind;

        /// <summary>
        /// The host tells us what it is bound to — at startup, after its own
        /// rebind, and whenever the backend moves underneath us (Prism's
        /// recovery raises that). Clears any run in progress: whatever
        /// disagreement was accumulating was about the OLD binding.
        /// </summary>
        public void NoteBound(string? reader, bool isControllerReader)
        {
            _boundReader = reader;
            _boundIsControllerReader = isControllerReader;
            _candidate = null;
            _candidateIsNone = false;
            _agreeingTicks = 0;
            _awaitingRebind = false;
        }

        /// <summary>How many consecutive ticks the current candidate has agreed. For tracing.</summary>
        public int AgreeingTicks => _agreeingTicks;

        /// <summary>
        /// The reader the last <see cref="Observe"/> saw running, or null when
        /// it saw none. For tracing, so a capture can say what the watchdog was
        /// looking at rather than only what it decided.
        /// </summary>
        public string? LastObserved { get; private set; }

        /// <summary>
        /// One tick of the watchdog.
        /// </summary>
        /// <param name="observedReader">
        /// The controller screen reader actually running, or null for none.
        /// </param>
        /// <param name="probeHealthy">
        /// Whether the observation can be trusted at all — see
        /// <see cref="ScreenReaderPresence.ProbeWorks"/>. False forces
        /// <see cref="Decision.StandDown"/>.
        /// </param>
        public Decision Observe(string? observedReader, bool probeHealthy)
        {
            LastObserved = observedReader;

            if (!probeHealthy)
            {
                // Not evidence of anything. Drop any run in progress rather
                // than letting an unreadable instrument accumulate a case.
                _candidate = null;
                _candidateIsNone = false;
                _agreeingTicks = 0;
                return Decision.StandDown;
            }

            if (_awaitingRebind) return Decision.Hold;

            bool observedNone = string.IsNullOrWhiteSpace(observedReader);
            bool agrees = observedNone
                ? !_boundIsControllerReader
                : (_boundIsControllerReader && SameReader(_boundReader, observedReader));

            if (agrees)
            {
                _candidate = null;
                _candidateIsNone = false;
                _agreeingTicks = 0;
                return Decision.Hold;
            }

            // A disagreement. Is it the same disagreement as last tick?
            bool sameCandidate = observedNone
                ? _candidateIsNone
                : (!_candidateIsNone && SameReader(_candidate, observedReader));

            if (sameCandidate && _agreeingTicks > 0)
            {
                _agreeingTicks++;
            }
            else
            {
                _candidate = observedReader;
                _candidateIsNone = observedNone;
                _agreeingTicks = 1;
            }

            int needed = observedNone ? NoReaderSettleTicks : ReaderSettleTicks;
            if (_agreeingTicks < needed) return Decision.Hold;

            _awaitingRebind = true;
            return Decision.Rebind;
        }

        /// <summary>
        /// Whether two reader names describe the same reader.
        ///
        /// Containment either way, case-insensitively, because the name we are
        /// bound to comes from Prism's backend ("JAWS") while a probe may see a
        /// longer product name, and the two must not read as a swap. Matching
        /// too eagerly here costs nothing — it means holding a binding that was
        /// already right; matching too strictly would rebind in a loop.
        /// </summary>
        internal static bool SameReader(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            a = a!.Trim();
            b = b!.Trim();
            return a.Contains(b, StringComparison.OrdinalIgnoreCase)
                || b.Contains(a, StringComparison.OrdinalIgnoreCase);
        }
    }
}
