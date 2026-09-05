#nullable enable

namespace Radios
{
    /// <summary>
    /// Decision policy for the stranded-focus sentinel: the periodic check an
    /// open dialog runs so a keyboard black hole cannot outlive a few seconds,
    /// and — since Sprint 44 Track Q — so a foreground TAKEN from an idle
    /// operator while a modal of ours is up cannot either.
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
    /// <para><b>The second case (2026-09-02, #529), which the first rule
    /// deliberately did not cover.</b> The Select Radio dialog sat open and
    /// idle; after a while <c>GetForegroundWindow()</c> belonged to another
    /// process entirely. The dialog was visible, enabled and on screen, the
    /// main window correctly disabled behind it, the process responding —
    /// and the keyboard was going to some other application. Measured while
    /// the operator was stuck, and repaired instantly by a single
    /// <c>SetForegroundWindow</c> from outside. The sentinel had been ticking
    /// the whole time and classified every tick as <see cref="Observation.ForeignForeground"/>,
    /// which under the original rule is the operator being elsewhere by
    /// choice. It was not a choice. Windows lets any process take the
    /// foreground once the current one has had no input for the foreground
    /// lock timeout (about 200 seconds by default), which is exactly why the
    /// operator's own report is "if you leave it open too long".</para>
    ///
    /// <para><b>What may be repaired, and what must never be.</b> The two
    /// provable black holes, as before: no foreground window anywhere on the
    /// desktop, and a foreground window of OUR OWN process whose thread has
    /// no focus window. And now one more, with evidence: a foreign foreground
    /// that arrived while the operator was doing NOTHING. The discriminator is
    /// the last input time the OS keeps for the whole session. If the
    /// operator's last keystroke or mouse movement predates the last moment
    /// we held the foreground, nobody Alt-Tabbed, nobody clicked — the
    /// foreground was taken from under them. If any input happened after that
    /// moment, the operator may have gone there on purpose, and reading email
    /// while the picker waits is a choice this sentinel never overrides. That
    /// rule is pinned by tests; anyone loosening it is reintroducing focus
    /// theft.</para>
    ///
    /// <para><b>Conservative on purpose, because taking the foreground is
    /// intrusive.</b> The theft repair requires all of: a MODAL of ours is up
    /// (the operator cannot reach the rest of the application anyway, so
    /// there is nothing of ours they could be using instead); we verifiably
    /// held the foreground earlier in this dialog's life; the foreign window
    /// is not a secure or system prompt (credential pickers, UAC, the secure
    /// desktop — stealing from those is the one actively harmful case, and
    /// harmful precisely to an operator who cannot see what just vanished);
    /// the same debounce as the black holes; and a cap of
    /// <see cref="MaxReclaimsPerIdleStretch"/> reclaims while the operator
    /// stays idle, so a thief that keeps taking it back does not turn into a
    /// tug-of-war announced every four seconds. The cap resets the moment the
    /// operator provides any input.</para>
    ///
    /// <para><b>The reclaim has an off switch, and the decision does not.</b>
    /// <c>AccessibilityConfig.ReclaimStolenForeground</c> (Settings,
    /// Accessibility) can stop the theft repair from acting, because a note
    /// already sent to a tester promises exactly that. It is applied by
    /// <see cref="WithOperatorPreference"/> to a verdict this class has already
    /// reached — nothing below reads it, and the gates behave identically at
    /// either setting. Switched off, an earned reclaim becomes
    /// <see cref="Verdict.ReclaimSuppressedByPreference"/>, which the dialog
    /// writes down and does not act on. The black-hole repairs are not
    /// governed by it at all.</para>
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
        /// triggers; see the class remarks. The theft path uses the same
        /// count: a foreign window that flashes up and goes away on its own
        /// is not a theft.
        /// </summary>
        public const int ConsecutiveBadObservationsBeforeRepair = 2;

        /// <summary>
        /// How many times the foreground is taken back from a thief while the
        /// operator provides no input at all. After this the sentinel stands
        /// down and says so in the trace, until the operator does something.
        /// Two: one for the theft, one for a thief that immediately re-steals,
        /// and no third — a reclaim that has failed twice is not going to
        /// succeed on a timer, and each attempt is an announcement.
        /// </summary>
        public const int MaxReclaimsPerIdleStretch = 2;

        /// <summary>
        /// How long after taking the foreground back the explanation is
        /// spoken. A screen reader flushes its queue when the foreground
        /// window changes and then announces the new window itself, so a
        /// sentence spoken at the moment of the grab is destroyed by the
        /// grab. This wait lets the reader's own "Select Radio dialog"
        /// announcement land first; the explanation then queues behind it.
        /// Same figure as <see cref="ConnectQuietScope.StrandedFocusRescueDelayMs"/>,
        /// for the same reason: long enough for a window transition to settle.
        /// </summary>
        public const int ReclaimAnnounceDelayMs = 750;

        /// <summary>One tick's worth of looking at the desktop.</summary>
        public enum Observation
        {
            /// <summary>A window of ours has the foreground and its thread
            /// has a focus window — the operator is somewhere real, in us.</summary>
            Healthy,

            /// <summary>Another process owns the foreground. Either the
            /// operator went there, or something took it from them; the
            /// input evidence in a <see cref="Sample"/> is what tells the
            /// two apart, and without that evidence this is NEVER repaired
            /// over.</summary>
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

        /// <summary>What one tick concluded.</summary>
        public enum Verdict
        {
            /// <summary>Leave everything alone.</summary>
            Nothing,

            /// <summary>A black hole has persisted through the debounce:
            /// reactivate this dialog, silently — the reader announces the
            /// window it lands on.</summary>
            ReactivateOverBlackHole,

            /// <summary>Another process took the foreground from an idle
            /// operator while a modal of ours was up: take it back and SAY
            /// SO, because a silent recovery leaves the outage unexplained
            /// and a silent outage reads as a crash.</summary>
            ReclaimFromForeignThief,

            /// <summary>The thief keeps taking it and the operator has done
            /// nothing since the last reclaim: stop fighting, record it, and
            /// wait for the operator. Returned once per idle stretch.</summary>
            StandDownThiefPersists,

            /// <summary>A reclaim was earned on the evidence and the operator
            /// has the watchdog switched off. Take nothing; say nothing to the
            /// operator; write it down. NEVER returned by
            /// <see cref="Decide"/> — only by
            /// <see cref="WithOperatorPreference"/>.</summary>
            ReclaimSuppressedByPreference,
        }

        /// <summary>
        /// Apply the operator's off switch to a verdict <see cref="Decide"/>
        /// has already reached.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>After the decision, never inside it.</b> The six gates, the
        /// debounce and the reclaim cap were measured, and the idle-comparison
        /// gate was verified at the radio on 2026-09-05. A preference read
        /// inside <see cref="DecideForeign"/> would have changed what the
        /// sentinel CONCLUDES; this changes only what the dialog DOES about it.
        /// That is what makes the disabled trace worth reading: it is the same
        /// conclusion, from the same evidence, with the action withheld.
        /// </para>
        /// <para>
        /// <b>Only the reclaim is governed.</b> A black hole is not "you were
        /// doing something else" — see
        /// <c>AccessibilityConfig.ReclaimStolenForeground</c> — so
        /// <see cref="Verdict.ReactivateOverBlackHole"/> passes through
        /// untouched at any setting.
        /// </para>
        /// <para>
        /// <b>The stand-down still comes through when it is off.</b> It marks
        /// the point at which a watchdog that HAD been running would have given
        /// up, so it belongs in the counterfactual record; the caller says in
        /// its own words that nothing was actually reclaimed. Note the
        /// counterfactual is exact for the first would-be reclaim and an
        /// approximation after it: a reclaim that really fired would have moved
        /// the foreground, so the following ticks would have seen a different
        /// desktop. What the trace claims is "a reclaim was earned here", which
        /// is true every time it is written.
        /// </para>
        /// </remarks>
        /// <param name="verdict">What <see cref="Decide"/> concluded.</param>
        /// <param name="reclaimEnabled">The operator's setting.</param>
        public static Verdict WithOperatorPreference(Verdict verdict, bool reclaimEnabled)
        {
            if (reclaimEnabled) return verdict;
            return verdict == Verdict.ReclaimFromForeignThief
                ? Verdict.ReclaimSuppressedByPreference
                : verdict;
        }

        /// <summary>
        /// Everything one tick knows. The observation alone is enough for the
        /// black-hole rules; the theft rule needs the rest, and a sample
        /// built without input evidence (<see cref="InputEvidenceKnown"/>
        /// false) can never conclude theft — which is how the original
        /// boolean API keeps its original meaning.
        /// </summary>
        /// <param name="NowMs">A monotonic clock, milliseconds.</param>
        /// <param name="LastInputMs">When the operator last pressed a key or
        /// moved the mouse, anywhere on the desktop, on the same clock as
        /// <paramref name="NowMs"/>.</param>
        /// <param name="InputEvidenceKnown">False when the OS could not say
        /// when the last input was; the theft rule then never fires.</param>
        /// <param name="OurModalIsUp">True when this dialog is modal — its
        /// owner window is disabled — so the operator has nothing else of
        /// ours to be using.</param>
        /// <param name="ForeignIsProtected">True when the foreign foreground
        /// is a window the sentinel must never take from: a credential
        /// picker, UAC, the secure desktop, or a sign-in flow of our own.</param>
        public readonly record struct Sample(
            Observation Observation,
            long NowMs,
            long LastInputMs,
            bool InputEvidenceKnown,
            bool OurModalIsUp,
            bool ForeignIsProtected)
        {
            /// <summary>The original API's view: observation only.</summary>
            public static Sample WithoutInputEvidence(Observation observation)
                => new(observation, 0, 0, false, false, false);
        }

        private int _consecutiveBad;
        private int _consecutiveTheft;

        /// <summary>The last tick at which the foreground was ours; -1 until
        /// it has been, which is what lets "taken from us" be distinguished
        /// from "never had it".</summary>
        private long _lastOursMs = -1;

        private int _reclaimsThisStretch;
        private long _lastReclaimMs = -1;
        private bool _standDownReported;

        /// <summary>
        /// The last tick at which the foreground was verifiably ours, on the
        /// sample clock, or -1 if it never has been. Exposed for the trace
        /// line that explains a reclaim.
        /// </summary>
        public long LastOursMs => _lastOursMs;

        /// <summary>
        /// Record one observation with no input evidence; true means "repair
        /// the black hole now". The counter resets on every healthy or
        /// foreign observation, and after each repair so a repair that does
        /// not take is retried a full debounce later rather than every tick.
        /// A foreign foreground is never repaired through this API.
        /// </summary>
        public bool NoteAndDecide(Observation observation)
            => Decide(Sample.WithoutInputEvidence(observation)) == Verdict.ReactivateOverBlackHole;

        /// <summary>
        /// Record one full sample and decide. See the class remarks for the
        /// rules; see the tests for the table.
        /// </summary>
        public Verdict Decide(in Sample s)
        {
            switch (s.Observation)
            {
                case Observation.NoForegroundAnywhere:
                case Observation.OursWithDeadFocus:
                    _consecutiveTheft = 0;
                    // Dead focus on our own foreground is still OUR foreground:
                    // if a thief takes it next, the operator was idle in us.
                    if (s.Observation == Observation.OursWithDeadFocus) NoteOurs(s);
                    _consecutiveBad++;
                    if (_consecutiveBad >= ConsecutiveBadObservationsBeforeRepair)
                    {
                        _consecutiveBad = 0;
                        return Verdict.ReactivateOverBlackHole;
                    }
                    return Verdict.Nothing;

                case Observation.ForeignForeground:
                    // Black-hole evidence is stale the moment a real window
                    // has the foreground, whoever owns it.
                    _consecutiveBad = 0;
                    return DecideForeign(s);

                default:
                    _consecutiveBad = 0;
                    _consecutiveTheft = 0;
                    NoteOurs(s);
                    return Verdict.Nothing;
            }
        }

        private void NoteOurs(in Sample s)
        {
            if (s.InputEvidenceKnown) _lastOursMs = s.NowMs;
        }

        private Verdict DecideForeign(in Sample s)
        {
            // Every gate is a reason NOT to act, and each one is a case where
            // acting would be worse than the outage. Order does not matter;
            // all must pass.
            if (!s.InputEvidenceKnown        // cannot tell chosen from taken
                || !s.OurModalIsUp           // the operator may be using the rest of us
                || s.ForeignIsProtected      // a prompt we must never take from
                || _lastOursMs < 0)          // never had it, so it was not taken
            {
                _consecutiveTheft = 0;
                return Verdict.Nothing;
            }

            // THE RULE. Input after the last moment we held the foreground
            // means the operator may have gone there — an Alt+Tab, a click, a
            // keystroke in the other window — and we stand down for as long
            // as that stays true. Only input that PREDATES our last tenure
            // proves the transition happened over an idle operator.
            if (s.LastInputMs > _lastOursMs)
            {
                _consecutiveTheft = 0;
                return Verdict.Nothing;
            }

            // Any input since the last reclaim opens a new idle stretch: the
            // cap is per stretch, not per dialog.
            if (_reclaimsThisStretch > 0 && s.LastInputMs > _lastReclaimMs)
            {
                _reclaimsThisStretch = 0;
                _standDownReported = false;
            }

            _consecutiveTheft++;
            if (_consecutiveTheft < ConsecutiveBadObservationsBeforeRepair)
                return Verdict.Nothing;
            _consecutiveTheft = 0;

            if (_reclaimsThisStretch >= MaxReclaimsPerIdleStretch)
            {
                if (_standDownReported) return Verdict.Nothing;
                _standDownReported = true;
                return Verdict.StandDownThiefPersists;
            }

            _reclaimsThisStretch++;
            _lastReclaimMs = s.NowMs;
            return Verdict.ReclaimFromForeignThief;
        }
    }
}
