#nullable enable
using System;
using System.Diagnostics;
using JJTrace;

namespace Radios.Speech
{
    /// <summary>
    /// Which kind of safety information is asking for the speaking turn.
    /// An explicit priority class, never the presence or absence of a
    /// subject string: Track I's arbiter extension encoded "this is the
    /// highest priority urgent there is" as *the absence of a tag*, which
    /// meant that giving the transmit-cut sentence the owner identity it
    /// needed would silently have demoted it.
    /// </summary>
    internal enum SafetyClass
    {
        /// <summary>A transmit-safety outcome: the reflected-power cut, the PTT timeout, the missing transmit path.</summary>
        TransmitSafety = 0,

        /// <summary>An operator-defined alarm's warning (#566).</summary>
        OperatorAlarm = 1,
    }

    /// <summary>
    /// Why a turn ended. Every one of these is an ACCOUNTED outcome: none of
    /// them is allowed to mean "and therefore the operator heard it".
    /// </summary>
    internal enum SafetyTurnEnd
    {
        /// <summary>The reader reported this exact attempt finished.</summary>
        Completed,

        /// <summary>The reader reported this exact attempt cut, refused or unknown.</summary>
        NotCompleted,

        /// <summary>The handoff never reached a backend, so the turn was never really taken.</summary>
        NeverStarted,

        /// <summary>Nobody can report on this attempt and its bounded occupancy estimate has run out. Delivery UNKNOWN.</summary>
        EstimateElapsed,

        /// <summary>The finite maximum turn was reached. A transport fault, never a success path.</summary>
        MaxTurnReached,

        /// <summary>A transmit-safety attempt took the turn from this one.</summary>
        PreEmptedBySafety,

        /// <summary>The operator asked for quiet.</summary>
        Silenced,
    }

    /// <summary>
    /// The occupancy token: one attempt's claim on the safety speaking turn.
    ///
    /// <para>Its identity is what makes a late callback harmless. A
    /// completion or a cancellation releases the turn ONLY when its ticket is
    /// the ticket this token was bound to — so an alarm's delayed answer,
    /// arriving after a transmit cut has taken the turn, updates its own
    /// attempt's evidence and frees nothing (#611).</para>
    /// </summary>
    internal sealed class SafetyTurn
    {
        /// <summary>Monotonic within a coordinator. Never reused, so "is this the same attempt" is answerable.</summary>
        public long Id { get; init; }

        public SafetyClass Class { get; init; }

        /// <summary>
        /// The episode's owner — the caller's <see cref="SpeechSubject"/>, or
        /// null where the caller declared none. Null is recorded rather than
        /// replaced by a house subject: giving every safety sentence one broad
        /// subject would let unrelated warnings retire each other.
        /// </summary>
        public string? Owner { get; init; }

        /// <summary>Which quiet cohort this attempt belongs to. A turn from an older cohort is never resumed automatically.</summary>
        public long QuietGeneration { get; init; }

        public DateTime ReservedUtc { get; init; }

        /// <summary>The finite maximum turn, from the reservation. Independent of any progress extension.</summary>
        public DateTime HardDeadlineUtc { get; init; }

        /// <summary>The pump ticket, once the handoff has returned. Zero while reserved-but-unbound, and zero forever for an untracked handoff.</summary>
        public long Ticket { get; private set; }

        /// <summary>True once <see cref="SafetyDeliveryCoordinator.Bind"/> has run, whether or not a ticket came back.</summary>
        public bool Bound { get; private set; }

        /// <summary>True when a completion channel will report on this attempt.</summary>
        public bool Tracked { get; private set; }

        /// <summary>
        /// When the turn may be assumed over for an UNTRACKED handoff — a
        /// bounded conservative occupancy estimate, and nothing more. Reaching
        /// it records delivery unknown; it is never evidence of hearing.
        /// </summary>
        public DateTime OccupiedUntilUtc { get; private set; }

        /// <summary>
        /// When the maximum turn was reached and a bounded cancellation of this
        /// attempt was requested; null until then. While set, the turn is still
        /// OCCUPIED: the attempt is being stopped, not yet stopped, and nothing
        /// may start on top of it (#611, Sol's Track K review section 1).
        /// </summary>
        public DateTime? CancelRequestedUtc { get; private set; }

        /// <summary>True from the moment the maximum turn asked this attempt to stop until its answer, or the grace, ends the turn.</summary>
        public bool CancelRequested => CancelRequestedUtc.HasValue;

        internal void BindTo(long ticket, bool tracked, DateTime occupiedUntilUtc)
        {
            Ticket = ticket;
            Tracked = tracked;
            Bound = true;
            OccupiedUntilUtc = occupiedUntilUtc;
        }

        internal void NoteCancelRequested(DateTime now) => CancelRequestedUtc = now;

        public override string ToString() =>
            $"turn #{Id} ({Class}, owner '{Owner ?? "(none declared)"}', ticket {Ticket}, quiet gen {QuietGeneration}"
            + (CancelRequested ? ", cancellation requested" : string.Empty) + ")";
    }

    /// <summary>
    /// One scheduler for the safety speaking turn — the transmit-safety
    /// warning, the transmit-safety retry and the operator alarm all take
    /// their turn from here (#611).
    ///
    /// <para><b>Why this type exists at all.</b> Track J gave transmit safety
    /// a protected obligation that survives an ordinary discard; Track I gave
    /// operator alarms an urgent path with a deadline of their own. Each is
    /// defensible alone. Merged as they stood, an alarm's LATE completion —
    /// arriving after a transmit cut had taken over — matched the alarm
    /// scheduler's remembered ticket, collapsed the shared deadline, released
    /// a queued alarm, and that alarm's handoff called the full teardown,
    /// which really does clear protected entries. The sentence that must
    /// survive was erased by a mechanism that never touched it. No radio
    /// fault, no elapsed estimate and no merge conflict were needed.</para>
    ///
    /// <para><b>The rule, in one line:</b> only the CURRENT turn's own answer
    /// ends the current turn.</para>
    ///
    /// <para><b>Not thread-safe on its own, deliberately.</b> Every method is
    /// called with <c>SpeechArbiter</c>'s lock held, which is also the lock
    /// the sink call and the outcome callback are serialised by — so
    /// reserve-then-hand-over-then-bind is one atomic sequence and a callback
    /// arriving before the sink returns cannot be lost. A second lock here
    /// would buy nothing and could invert.</para>
    /// </summary>
    internal sealed class SafetyDeliveryCoordinator
    {
        /// <summary>
        /// The finite maximum automatic turn, T. Repeated progress marks may
        /// keep a reader's own deadline alive indefinitely; they may not
        /// reserve this channel indefinitely.
        ///
        /// <para>Sized against a bounded safety presentation read at a slow
        /// rate, with room to spare: the ledger's own per-utterance ceiling is
        /// <see cref="SpeechArbiter.SalvageCapMs"/> (15 s) and the pump's
        /// longest deadline is <see cref="PacedSpeechDelivery.DeadlineCapMs"/>
        /// (45 s). Twenty seconds sits above the first and below the second,
        /// so T bounds the TURN without pre-empting the transport's own escape
        /// machinery. Exceeding it takes the unknown path, never a success
        /// path: the fact stays owed and delivery is recorded unknown.</para>
        /// </summary>
        internal const int MaxAutomaticTurnMs = 20000;

        /// <summary>
        /// How long after the maximum turn asks a TRACKED attempt to stop the
        /// coordinator waits for that attempt's own answer before it declares
        /// the attempt isolated and transfers the turn anyway.
        ///
        /// <para><b>Why the maximum turn is a handshake and not a release (Sol,
        /// Track K review, 2026-09-23).</b> Reaching T used to clear the turn
        /// and nothing else, so the next waiting alarm saw a free turn and
        /// interrupted the sink on top of a tracked cut that was, by the
        /// pump's own evidence, still sounding — no cancellation first, no
        /// isolation, and a turn "bounded" by a clock that transferred a
        /// running transport to a new speaker. Now reaching T REQUESTS a
        /// bounded cancellation of the attempt that overran, keeps the turn
        /// occupied while that request is outstanding, and transfers only when
        /// the attempt's own answer arrives or this grace runs out — at which
        /// point the attempt has had the pump's full cancel-then-escape path
        /// and is treated as isolated.</para>
        ///
        /// <para>Sized against the transport it is waiting on rather than
        /// chosen: the pump gives a cancelled call <see cref="PacedSpeechDelivery.CancelGraceMs"/>
        /// to come back and then <see cref="PacedSpeechDelivery.EscapeGraceMs"/>
        /// for the escape, after which it abandons the thread and reports.
        /// Half a second over their sum leaves room for the report to travel.
        /// Both halves record delivery UNKNOWN; neither is a success path.</para>
        /// </summary>
        internal const int MaxTurnCancelGraceMs =
            PacedSpeechDelivery.CancelGraceMs + PacedSpeechDelivery.EscapeGraceMs + 500;

        private readonly Action? _requestCancellation;

        private long _nextTurnId;
        private SafetyTurn? _turn;

        /// <param name="requestCancellation">
        /// Cut the attempt that currently holds the reader — the arbiter's
        /// silence-backend action. Called with the arbiter's lock held, from
        /// <see cref="ExpireIfOverdue"/>, exactly once per turn that reaches
        /// the maximum. Null where there is no backend to cut (tests of the
        /// token alone).
        /// </param>
        public SafetyDeliveryCoordinator(Action? requestCancellation = null)
        {
            _requestCancellation = requestCancellation;
        }

        /// <summary>How many times the maximum turn has asked an attempt to stop. Tests.</summary>
        public int CancellationsRequested { get; private set; }

        private void RequestCancellation(SafetyTurn turn, DateTime now)
        {
            turn.NoteCancelRequested(now);
            CancellationsRequested++;
            Tracing.TraceLine(
                $"SafetyDelivery: {turn} reached the {MaxAutomaticTurnMs} ms maximum turn while still sounding; "
                + $"a bounded cancellation is requested and the turn stays OCCUPIED until its own answer arrives or "
                + $"{MaxTurnCancelGraceMs} ms pass. Delivery is UNKNOWN and the fact stays owed.",
                TraceLevel.Warning);
            try { _requestCancellation?.Invoke(); }
            catch (Exception ex)
            {
                Tracing.TraceLine($"SafetyDelivery: the cancellation request threw — {ex.Message}", TraceLevel.Warning);
            }
        }

        /// <summary>A ticket whose outcome arrived before its turn was bound — see <see cref="Bind"/>.</summary>
        private long _outcomeBeforeBindTicket;
        private bool _outcomeBeforeBindCompleted;

        /// <summary>
        /// Advanced by every deliberate silence. An episode admitted under an
        /// older generation is not resurrected by a passing interrupt, a
        /// settle timer, an unrelated completion or a backend rebind; it needs
        /// an explicit replay or a positively correlated owner recovery event
        /// (#182, Astra section 5).
        /// </summary>
        public long QuietGeneration { get; private set; }

        /// <summary>The turn in progress, or null. For the trace and for tests.</summary>
        public SafetyTurn? Current => _turn;

        /// <summary>
        /// True while the speech channel is known to be refusing or absent.
        /// Set by any handoff that did not reach a backend and by a refusal;
        /// the transition back out of it is the only backend-recovery EDGE,
        /// and the only thing that may grant a paused safety slot one further
        /// automatic probe. An ordinary completion on a healthy channel is not
        /// an edge and grants nothing.
        /// </summary>
        public bool BackendKnownBad { get; private set; }

        /// <summary>Note a handoff that did not reach a backend, or a reader refusal.</summary>
        public void NoteBackendFailed() => BackendKnownBad = true;

        /// <summary>
        /// A delivery was really heard. Returns true exactly once per
        /// failure-to-working transition — that is the recovery edge.
        /// Duplicate successes cannot replenish anything.
        /// </summary>
        public bool NoteBackendWorkedAndTakeEdge()
        {
            if (!BackendKnownBad) return false;
            BackendKnownBad = false;
            return true;
        }

        /// <summary>
        /// Retire the current turn if it has outlived its bounded occupancy or
        /// the maximum turn T. Both record delivery UNKNOWN. Call at the top
        /// of anything that asks whether the turn is free.
        /// </summary>
        public void ExpireIfOverdue(DateTime now)
        {
            var turn = _turn;
            if (turn == null) return;

            if (now >= turn.HardDeadlineUtc)
            {
                if (turn.Bound && turn.Tracked)
                {
                    // The escape handshake. A tracked attempt has a reporter,
                    // so it can be ASKED to stop and will say when it has:
                    // request once, stay occupied, and let TakeOutcome end the
                    // turn on the attempt's own answer. Only when the pump's
                    // whole cancel-then-escape path has had its time is the
                    // attempt treated as isolated and the turn transferred.
                    if (!turn.CancelRequested)
                    {
                        RequestCancellation(turn, now);
                        return;
                    }
                    if (now < turn.CancelRequestedUtc!.Value.AddMilliseconds(MaxTurnCancelGraceMs)) return;

                    EndLocked(turn, SafetyTurnEnd.MaxTurnReached,
                        $"the maximum turn was reached, cancellation was requested {(int)(now - turn.CancelRequestedUtc.Value).TotalMilliseconds} ms ago "
                        + "and no answer came, so the attempt is treated as isolated; delivery is UNKNOWN and the fact stays owed");
                    return;
                }

                // Nobody can report on this attempt, so there is no answer to
                // wait for. Cut the backend so nothing is still sounding when
                // the next speaker starts, then transfer.
                RequestCancellation(turn, now);
                EndLocked(turn, SafetyTurnEnd.MaxTurnReached,
                    $"the {MaxAutomaticTurnMs} ms maximum turn was reached with no reporter to answer for the attempt; "
                    + "the backend was cut; delivery is UNKNOWN and the fact stays owed");
                return;
            }

            // An untracked handoff has no reporter. Its estimate bounds how
            // long we ASSUME it occupies the channel — it does not become
            // evidence that anything was heard.
            if (turn.Bound && !turn.Tracked && now >= turn.OccupiedUntilUtc)
            {
                EndLocked(turn, SafetyTurnEnd.EstimateElapsed,
                    "its bounded occupancy estimate elapsed; nobody can report on it, so delivery is UNKNOWN");
            }
        }

        /// <summary>True when any safety attempt holds the turn.</summary>
        public bool IsBusy(DateTime now)
        {
            ExpireIfOverdue(now);
            return _turn != null;
        }

        /// <summary>True when a TRANSMIT-SAFETY attempt holds the turn. An alarm may never take it.</summary>
        public bool SafetyHoldsTheTurn(DateTime now)
        {
            ExpireIfOverdue(now);
            return _turn != null && _turn.Class == SafetyClass.TransmitSafety;
        }

        /// <summary>When the current turn is expected to end, for arming a waiter. Null when it is free.</summary>
        public DateTime? EndsAtUtc
        {
            get
            {
                var turn = _turn;
                if (turn == null) return null;
                if (turn.CancelRequested) return turn.CancelRequestedUtc!.Value.AddMilliseconds(MaxTurnCancelGraceMs);
                if (!turn.Bound || turn.Tracked) return turn.HardDeadlineUtc;
                return turn.OccupiedUntilUtc < turn.HardDeadlineUtc ? turn.OccupiedUntilUtc : turn.HardDeadlineUtc;
            }
        }

        /// <summary>
        /// Take the turn for a transmit-safety attempt. Always succeeds: a
        /// transmit-safety outcome outranks an alarm that is sounding, and
        /// outranks an older safety attempt because the newer episode is the
        /// one that is true. The pre-empted attempt's own record is NOT
        /// touched here — it lives in the arbiter's ledger, still owed.
        /// </summary>
        public SafetyTurn ReserveForSafety(string? owner, DateTime now)
        {
            var held = _turn;
            if (held != null)
            {
                EndLocked(held, SafetyTurnEnd.PreEmptedBySafety,
                    "a transmit-safety announcement took the turn; this attempt's information is still owed");
            }
            return StartLocked(SafetyClass.TransmitSafety, owner, now);
        }

        /// <summary>
        /// Take the turn for an operator alarm, or return null when something
        /// else holds it and the alarm must wait. An alarm never pre-empts.
        /// </summary>
        public SafetyTurn? TryReserveForAlarm(string? owner, DateTime now)
        {
            if (IsBusy(now)) return null;
            return StartLocked(SafetyClass.OperatorAlarm, owner, now);
        }

        private SafetyTurn StartLocked(SafetyClass cls, string? owner, DateTime now)
        {
            var turn = new SafetyTurn
            {
                Id = ++_nextTurnId,
                Class = cls,
                Owner = owner,
                QuietGeneration = QuietGeneration,
                ReservedUtc = now,
                HardDeadlineUtc = now.AddMilliseconds(MaxAutomaticTurnMs),
            };
            _turn = turn;
            _outcomeBeforeBindTicket = 0;
            _outcomeBeforeBindCompleted = false;
            return turn;
        }

        /// <summary>
        /// Bind the handoff's ticket to the turn it was reserved for, and
        /// apply any outcome that arrived before the sink returned.
        ///
        /// <para>The reservation is made BEFORE the sink is called so there is
        /// no window in which an attempt is sounding and nothing owns the
        /// turn. Binding after is what closes the other half: an outcome
        /// callback that beats the sink's own return is stashed by
        /// <see cref="TakeOutcome"/> and applied here, so it can neither be
        /// lost nor leave a turn occupied by an attempt that is already
        /// over.</para>
        /// </summary>
        public void Bind(SafetyTurn turn, SpeechHandoff handoff, int estimateMs, DateTime now)
        {
            if (!ReferenceEquals(turn, _turn))
            {
                // Something took the turn between reserve and bind. That is
                // legitimate — a transmit cut pre-empts — and the attempt just
                // handed over is no longer the one that owns the channel.
                Tracing.TraceLine(
                    $"SafetyDelivery: {turn} was overtaken before its handoff was bound; it does not own the turn",
                    TraceLevel.Info);
                return;
            }

            int occupancyMs = Math.Min(Math.Max(estimateMs, 0), MaxAutomaticTurnMs);
            turn.BindTo(handoff.Ticket, handoff.Tracked, now.AddMilliseconds(occupancyMs));

            if (_outcomeBeforeBindTicket != 0 && _outcomeBeforeBindTicket == handoff.Ticket)
            {
                bool completed = _outcomeBeforeBindCompleted;
                _outcomeBeforeBindTicket = 0;
                _outcomeBeforeBindCompleted = false;
                Tracing.TraceLine(
                    $"SafetyDelivery: the reader answered {turn} before the handoff returned; "
                    + "the answer is applied rather than lost",
                    TraceLevel.Info);
                EndLocked(turn, completed ? SafetyTurnEnd.Completed : SafetyTurnEnd.NotCompleted,
                    "its own answer arrived before the handoff returned");
            }
        }

        /// <summary>
        /// The handoff never reached a backend. The turn was never really
        /// taken, so it is released at once — but nothing about the FACT
        /// changes, and the caller retains it.
        /// </summary>
        public void Abandon(SafetyTurn turn, string why)
        {
            NoteBackendFailed();
            if (!ReferenceEquals(turn, _turn)) return;
            EndLocked(turn, SafetyTurnEnd.NeverStarted, why);
        }

        /// <summary>
        /// An outcome arrived for <paramref name="ticket"/>. Returns the turn
        /// it ended, or null when this outcome has no authority over the
        /// current turn.
        ///
        /// <para><b>This is the #611 gate.</b> An alarm whose completion was
        /// delayed on its way to the arbiter comes back long after a transmit
        /// cut took the turn. Track I matched such an answer against a
        /// remembered "last alarm" ticket and let it collapse the shared
        /// deadline. Here it matches nothing current, releases nothing, and
        /// says so.</para>
        /// </summary>
        public SafetyTurn? TakeOutcome(long ticket, bool completed, DateTime now)
        {
            var turn = _turn;
            if (turn == null || ticket == 0) return null;

            if (!turn.Bound)
            {
                // The callback beat the sink's own return. Stash it; Bind
                // applies it the moment the ticket is known.
                _outcomeBeforeBindTicket = ticket;
                _outcomeBeforeBindCompleted = completed;
                return null;
            }

            if (turn.Ticket != ticket)
            {
                Tracing.TraceLine(
                    $"SafetyDelivery: delivery #{ticket} answered for an attempt that no longer owns the turn "
                    + $"({turn} holds it); it updates its own attempt's evidence and releases nothing",
                    TraceLevel.Info);
                return null;
            }

            if (turn.CancelRequested && !completed)
            {
                // The answer to the maximum-turn cancellation: the attempt
                // stopped, as asked. This is the handshake completing, and it
                // is accounted as the limit being reached, never as the reader
                // having said the words.
                EndLocked(turn, SafetyTurnEnd.MaxTurnReached,
                    "the attempt stopped on the maximum-turn cancellation request; delivery is UNKNOWN and the fact stays owed");
                return turn;
            }

            EndLocked(turn, completed ? SafetyTurnEnd.Completed : SafetyTurnEnd.NotCompleted,
                completed ? "the reader reported this attempt finished" : "the reader reported this attempt did not finish");
            return turn;
        }

        /// <summary>
        /// The operator asked for quiet. Advance the cohort barrier and drop
        /// the turn. Nothing is acknowledged, nothing is erased, and no
        /// condition is disabled — this touches the DELIVERY, never the fact
        /// (#182).
        /// </summary>
        public void Silence(DateTime now)
        {
            var turn = _turn;
            if (turn != null)
                EndLocked(turn, SafetyTurnEnd.Silenced,
                    "the operator silenced speech; the information is still owed and is not acknowledged");
            QuietGeneration++;
            _outcomeBeforeBindTicket = 0;
            _outcomeBeforeBindCompleted = false;
            Tracing.TraceLine(
                $"SafetyDelivery: quiet cohort advanced to generation {QuietGeneration}; every safety episode "
                + "admitted before it waits for an explicit replay or a known owner recovery event",
                TraceLevel.Info);
        }

        /// <summary>
        /// An ORDINARY interrupt reached the reader and cut what it was
        /// saying. A tracked attempt answers for itself through the pump and
        /// is left to; an untracked one has no reporter and would otherwise be
        /// believed to occupy the turn until its estimate ran out, after the
        /// reader had in fact stopped. Its turn ends here as NotCompleted; the
        /// attempt's own record in the arbiter's ledger is untouched and still
        /// owed (Astra's Track IJK review, blocker 1).
        /// </summary>
        public void NoteOrdinaryInterruptCutTheReader(DateTime now)
        {
            var turn = _turn;
            if (turn == null || !turn.Bound || turn.Tracked) return;
            EndLocked(turn, SafetyTurnEnd.NotCompleted,
                "an ordinary interrupt cut the reader; nobody can report on this attempt, and its information is still owed");
        }

        /// <summary>
        /// Give the turn back without anything having gone wrong with the
        /// channel: the attempt turned out to have nothing to say. Distinct
        /// from <see cref="Abandon"/>, which also records the backend as
        /// failing and would otherwise manufacture a recovery edge out of a
        /// condition that simply cleared.
        /// </summary>
        public void ReleaseUnused(SafetyTurn turn, string why)
        {
            if (!ReferenceEquals(turn, _turn)) return;
            EndLocked(turn, SafetyTurnEnd.NeverStarted, why);
        }

        /// <summary>Shutdown and the test reset. Not a silence: it claims nothing about the operator.</summary>
        public void Reset()
        {
            _turn = null;
            _outcomeBeforeBindTicket = 0;
            _outcomeBeforeBindCompleted = false;
            BackendKnownBad = false;
        }

        /// <summary>True when this episode belongs to the CURRENT quiet cohort — the only one automatic speech may serve.</summary>
        public bool InCurrentCohort(long admittedAtQuietGeneration) =>
            admittedAtQuietGeneration == QuietGeneration;

        private void EndLocked(SafetyTurn turn, SafetyTurnEnd end, string why)
        {
            if (ReferenceEquals(turn, _turn)) _turn = null;
            Tracing.TraceLine(
                $"SafetyDelivery: {turn} ended — {end}: {why}",
                end is SafetyTurnEnd.MaxTurnReached or SafetyTurnEnd.NeverStarted
                    ? TraceLevel.Warning
                    : TraceLevel.Info);
        }
    }
}
