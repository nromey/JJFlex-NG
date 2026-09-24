#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using JJTrace;

namespace Radios.Facts
{
    /// <summary>What happened when a producer asked for a condition slot.</summary>
    public enum RegistrationOutcome
    {
        /// <summary>A slot exists. Information published against it has somewhere to go.</summary>
        Granted = 0,

        /// <summary>
        /// No slot could be allocated. <b>Reported, never assumed.</b> A newly
        /// configured alarm whose delivery record could not be allocated must
        /// not be described as monitored.
        /// </summary>
        Exhausted = 1,

        /// <summary>The capability offered was revoked or not ours.</summary>
        NotAuthorised = 2,
    }

    /// <summary>The answer to a registration request, including why not.</summary>
    public sealed class RegistrationResult
    {
        internal RegistrationResult(RegistrationOutcome outcome, string slot, string explanation)
        {
            Outcome = outcome;
            Slot = slot;
            Explanation = explanation;
        }

        public RegistrationOutcome Outcome { get; }
        public string Slot { get; }

        /// <summary>Plain words for the operator-facing capacity result. Never only a trace line.</summary>
        public string Explanation { get; }

        public bool Granted => Outcome == RegistrationOutcome.Granted;
    }

    /// <summary>What happened when a producer offered a fact.</summary>
    public enum AdmissionOutcome
    {
        /// <summary>Retained. The handle is live and the information is owed.</summary>
        Accepted = 0,

        /// <summary>
        /// A newer revision of this episode already exists, or this revision was
        /// seen before. Idempotent: nothing regressed and nothing was lost.
        /// </summary>
        AlreadyKnown = 1,

        /// <summary>
        /// The store is full. <b>An explicit, reachable capacity result</b> —
        /// the overflow record records how much detail was lost and over what
        /// range. Never a silent drop and never a trace-only refusal.
        /// </summary>
        CapacityRecorded = 2,

        /// <summary>The capability was revoked, replaced, or belongs to another connection.</summary>
        NotAuthorised = 3,
    }

    /// <summary>The answer to an admission, and the handle when there is one.</summary>
    public sealed class FactAdmission
    {
        internal FactAdmission(AdmissionOutcome outcome, Fact? fact, string explanation)
        {
            Outcome = outcome;
            Fact = fact;
            Explanation = explanation;
        }

        public AdmissionOutcome Outcome { get; }

        /// <summary>The retained fact, or null when none was retained.</summary>
        public Fact? Fact { get; }

        /// <summary>Plain words for the reachable surface.</summary>
        public string Explanation { get; }

        public bool Accepted => Outcome == AdmissionOutcome.Accepted;
    }

    /// <summary>
    /// What was lost when even the bounded store filled. Honest reduced
    /// guarantee, never silence.
    /// </summary>
    public sealed class OverflowRecord
    {
        /// <summary>How many facts could not be retained. Saturates rather than wrapping.</summary>
        public long LostCount { get; private set; }

        /// <summary>The first and last moments covered by the loss.</summary>
        public DateTime? FirstLostUtc { get; private set; }
        public DateTime? LastLostUtc { get; private set; }

        /// <summary>True when the count stopped being exact.</summary>
        public bool Saturated { get; private set; }

        internal void Note(DateTime whenUtc)
        {
            if (LostCount == long.MaxValue) { Saturated = true; return; }
            LostCount++;
            FirstLostUtc ??= whenUtc;
            LastLostUtc = whenUtc;
        }

        public bool Any => LostCount > 0;

        public override string ToString() =>
            Any
                ? LostCount + (Saturated ? " or more" : string.Empty) + " fact(s) could not be retained"
                : "nothing lost";
    }

    /// <summary>How the store's own durability is doing.</summary>
    public enum PersistenceStatus
    {
        /// <summary>Everything mutated has been written.</summary>
        UpToDate = 0,

        /// <summary>A write is outstanding. Recent information may be lost to a crash.</summary>
        Pending = 1,

        /// <summary>The last write failed. Explicitly unsaved, never a durability claim.</summary>
        Failed = 2,

        /// <summary>Some records could not be read back and are named as a recovery gap.</summary>
        RecoveryGap = 3,
    }

    /// <summary>
    /// The information owed to the operator, held independently of the
    /// sentence, the radio, the reader and every delivery attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Application lifetime.</b> It starts before speech is initialised and
    /// outlives every dialog, controller and reader binding, because a store
    /// that dies with the thing that failed cannot tell anyone it failed.
    /// </para>
    /// <para>
    /// <b>It is narrow on purpose.</b> Undelivered detail, and the history of
    /// perishable events. It is not a notifications centre: a persistent
    /// condition that WAS successfully presented belongs in the live status and
    /// alarm surfaces, not in a growing list nobody reads. That narrowness is
    /// what keeps the list worth opening.
    /// </para>
    /// <para>
    /// <b>Nothing in here waits.</b> No method touches a radio, a delegate, the
    /// UI dispatcher, the disk, or native speech. Admission is a short
    /// in-memory update; durability is somebody else's asynchronous problem and
    /// is reported rather than promised. <b>A protective stop must never wait
    /// for store capacity, disk or speech.</b>
    /// </para>
    /// <para>
    /// <b>And nothing in here counts seconds.</b> There is no age-based purge,
    /// no expiry and no TTL. Times order events and explain observations; the
    /// condition decides whether a rendering is still justified.
    /// </para>
    /// </remarks>
    public sealed class FactStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Fact> _facts = new(StringComparer.Ordinal);
        private readonly List<Fact> _order = new();
        private readonly HashSet<string> _slots = new(StringComparer.Ordinal);

        private long _ingestionSequence;
        private long _quietGeneration;
        private long _nextAttemptId;
        private bool _channelFailing;

        public FactStore(long processIncarnation)
        {
            ProcessIncarnation = processIncarnation;
        }

        /// <summary>This run of the application.</summary>
        public long ProcessIncarnation { get; }

        /// <summary>What was lost to capacity, if anything.</summary>
        public OverflowRecord Overflow { get; } = new OverflowRecord();

        /// <summary>How durability is doing. Reported, never promised.</summary>
        public PersistenceStatus Persistence { get; private set; } = PersistenceStatus.UpToDate;

        /// <summary>Why, when it is not up to date.</summary>
        public string? PersistenceNote { get; private set; }

        /// <summary>
        /// Raised when the retained set changed, so a journal can coalesce to
        /// the newest complete snapshot rather than queueing every sample.
        /// </summary>
        /// <remarks>
        /// Deliberately a bare signal carrying no data: a handler that needs the
        /// contents takes them itself, under its own lock, at its own pace, so
        /// nothing slow is ever called with the store's gate held.
        /// </remarks>
        public event Action? Dirty;

        /// <summary>
        /// The one sequencer shared by fact ingestion and the quiet barrier.
        /// </summary>
        /// <remarks>
        /// <b>Stamp at the source, before any slow stage.</b> An event stamped
        /// after a cue, a journal write or a dispatcher hop can arrive on the
        /// wrong side of a barrier the operator has already crossed, and then
        /// speak into a silence he asked for. Timestamps cannot order two
        /// delayed queues; one counter can.
        /// </remarks>
        public long NextIngestionStamp()
        {
            lock (_gate) return ++_ingestionSequence;
        }

        /// <summary>The current quiet cohort. Advanced by every deliberate silence.</summary>
        public long QuietGeneration
        {
            get { lock (_gate) return _quietGeneration; }
        }

        // ────────────────────────────────────────────────────────────────
        //  Registration — before monitoring starts, never after
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reserve a current slot for a condition, before anything is monitored
        /// against it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The ninth warning must have a slot before it is spoken.</b> A
        /// warning presented while only a trace knows its retention failed is
        /// the defect this whole store exists to end, and the fix is ordering:
        /// register first, monitor second, so the refusal happens at
        /// configuration time where somebody can see it.
        /// </para>
        /// <para>
        /// Refusing a slot concerns DELIVERY REGISTRATION only. It is not
        /// permission to perform a protective stop and must never be read as
        /// one.
        /// </para>
        /// </remarks>
        public RegistrationResult RegisterSlot(ProducerCapability capability, string conditionSlot)
        {
            if (capability == null || capability.Revoked)
            {
                return new RegistrationResult(RegistrationOutcome.NotAuthorised, conditionSlot,
                    "the producer's capability has been revoked, so nothing may be registered against it");
            }

            lock (_gate)
            {
                if (_slots.Contains(conditionSlot))
                {
                    return new RegistrationResult(RegistrationOutcome.Granted, conditionSlot,
                        "already registered");
                }

                if (_slots.Count >= FactStoreCapacity.MaxCurrentSlots)
                {
                    string why =
                        "There is no room to keep track of this condition: " + _slots.Count +
                        " are already registered, which is the limit. It will not be monitored, " +
                        "and nothing it might have reported would be kept.";
                    Tracing.TraceLine("FactStore: slot registration refused for '" + conditionSlot +
                        "' — " + why, TraceLevel.Warning);
                    return new RegistrationResult(RegistrationOutcome.Exhausted, conditionSlot, why);
                }

                _slots.Add(conditionSlot);
                return new RegistrationResult(RegistrationOutcome.Granted, conditionSlot, "registered");
            }
        }

        /// <summary>Every registered slot, for the diagnostic view.</summary>
        public IReadOnlyCollection<string> RegisteredSlots
        {
            get { lock (_gate) return new List<string>(_slots); }
        }

        // ────────────────────────────────────────────────────────────────
        //  Admission — before the tone, before the sentence
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Take ownership of a fact.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This happens BEFORE the earcon is requested and before any
        /// speech, and neither is conditioned on the other.</b> That ordering
        /// is what makes the tone reliable: a warning whose sentence cannot be
        /// spoken still makes its noise, and a fact whose tone device is broken
        /// is still owed. Acceptance depends on nothing outside this method.
        /// </para>
        /// <para>
        /// <b>An unclassified message is still admitted.</b> See
        /// <see cref="IsEligibleForAutomaticDelivery"/> for what it may not do.
        /// Throwing away a safety event because its formatting metadata was
        /// missing would be the worse failure by a wide margin.
        /// </para>
        /// </remarks>
        public FactAdmission Admit(
            ProducerCapability capability,
            FactIdentity identity,
            LexiconMessage message,
            ValiditySnapshot validity,
            FactProvenance provenance,
            string? detail = null)
        {
            if (capability == null || capability.Revoked)
            {
                return new FactAdmission(AdmissionOutcome.NotAuthorised, null,
                    "the producer's capability has been revoked; this report is not admitted as current");
            }

            if (capability.ConnectionIncarnation != identity.ConnectionIncarnation)
            {
                // A stale publisher cannot mutate a live fact. The connection
                // captured when the callback was subscribed is the one that
                // counts, not whichever is current when it finally runs.
                return new FactAdmission(AdmissionOutcome.NotAuthorised, null,
                    "this report came from an older connection than the capability it used");
            }

            lock (_gate)
            {
                if (_facts.TryGetValue(identity.EpisodeId, out Fact? existing))
                {
                    return new FactAdmission(AdmissionOutcome.AlreadyKnown, existing,
                        "this occurrence is already retained; repeating it changes nothing");
                }

                if (_order.Count >= FactStoreCapacity.MaxHistoricalRecords && !Compact())
                {
                    Overflow.Note(provenance.ObservedUtc);
                    Tracing.TraceLine(
                        "FactStore: at capacity and nothing could be compacted; '" + message.Key +
                        "' is recorded in the overflow record rather than retained in full",
                        TraceLevel.Warning);
                    return new FactAdmission(AdmissionOutcome.CapacityRecorded, null,
                        "There was no room to keep the details of this one. It is counted in the " +
                        "record of what was lost, with the time it happened.");
                }

                var fact = new Fact(
                    identity, message.Key, message.Delivery, message.Classification,
                    validity, provenance, _quietGeneration)
                {
                    CatalogGeneration = message.CatalogGeneration,
                };
                fact.ApplyDetail(detail);

                _facts[identity.EpisodeId] = fact;
                _order.Add(fact);
                MarkDirtyLocked();

                return new FactAdmission(AdmissionOutcome.Accepted, fact, "retained");
            }
        }

        /// <summary>
        /// Update an episode's validity. Only the capability that issued it may
        /// do this, and only forwards.
        /// </summary>
        /// <param name="material">
        /// True when the INFORMATION changed — a correction or a material
        /// outcome. False for a fresh sample of the same thing, which must not
        /// manufacture an incident, undo a silence or replenish an allowance.
        /// </param>
        public bool UpdateValidity(
            ProducerCapability capability,
            string episodeId,
            ValiditySnapshot snapshot,
            FactProvenance provenance,
            bool material = false)
        {
            if (capability == null || capability.Revoked) return false;

            lock (_gate)
            {
                if (!_facts.TryGetValue(episodeId, out Fact? fact)) return false;
                if (fact.Identity.ConnectionIncarnation != capability.ConnectionIncarnation) return false;

                // Older provenance cannot regress the store. A late callback
                // may append evidence to history; it cannot reopen current
                // truth.
                if (provenance.IngestionSequence < fact.Provenance.IngestionSequence) return false;

                fact.ApplyValidity(snapshot, provenance, material);
                MarkDirtyLocked();
                return true;
            }
        }

        /// <summary>
        /// Revoke every current rendering that depended on a connection —
        /// at ingestion, before any UI or delivery work is posted.
        /// </summary>
        /// <remarks>
        /// <b>Disconnect is an ENDED OBSERVATION CONTEXT, not a resolved
        /// condition.</b> After it, what was last established is shown and
        /// current state is left unknown. A successful later attach never
        /// rehabilitates an old token.
        /// </remarks>
        public int RevokeConnection(long connectionIncarnation, DateTime nowUtc, string why)
        {
            lock (_gate)
            {
                int touched = 0;
                foreach (Fact fact in _order)
                {
                    if (fact.Identity.ConnectionIncarnation != connectionIncarnation) continue;
                    if (!fact.Validity.IsCurrent) continue;
                    fact.ApplyValidity(
                        ValiditySnapshot.End(EndedKind.EndedObservationContext, nowUtc, why),
                        fact.Provenance, material: false);
                    touched++;
                }
                if (touched > 0) MarkDirtyLocked();
                return touched;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Delivery and receipt evidence
        // ────────────────────────────────────────────────────────────────

        /// <summary>Record what became of one attempt.</summary>
        public void RecordAttempt(
            string episodeId, DeliveryState state, DateTime atUtc,
            bool coveredRequiredDetail, string? note = null)
        {
            lock (_gate)
            {
                if (!_facts.TryGetValue(episodeId, out Fact? fact)) return;
                fact.RecordAttempt(new DeliveryAttempt(
                    ++_nextAttemptId, fact.MaterialRevision, state, atUtc, note, coveredRequiredDetail));

                // An unknown-cause end pauses automatic delivery rather than
                // retiring anything. A zero-mark cancellation from an external
                // cause is not proof that no presentation began, and proves
                // nothing about why it stopped.
                if (state == DeliveryState.UnknownCompletion)
                {
                    fact.Pause("the last attempt ended and nobody can say why or how much was said");
                }

                MarkDirtyLocked();
            }
        }

        /// <summary>
        /// Note the receipt for this occurrence. The token is issued once;
        /// <b>a retry does not replay the receipt</b>, so a failing warning
        /// does not become a beeping one.
        /// </summary>
        public void RecordReceipt(string episodeId, ReceiptState state, string? token = null)
        {
            lock (_gate)
            {
                if (!_facts.TryGetValue(episodeId, out Fact? fact)) return;
                fact.RecordReceipt(state, token ?? episodeId);
                MarkDirtyLocked();
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Quiet, review and resume
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The operator asked for quiet. Advance the barrier and pause the
        /// cohort admitted before it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This changes DELIVERY PERMISSION and nothing else.</b> Nothing is
        /// acknowledged, no condition is disabled, no alarm is snoozed and no
        /// fact becomes less true. Every paused item stays on the list, which
        /// is the entire compensation for it going quiet.
        /// </para>
        /// <para>
        /// Events captured before the barrier but delivered after it inherit
        /// their captured quiet generation — arriving late cannot earn fresh
        /// permission.
        /// </para>
        /// </remarks>
        public long AdvanceQuietBarrier(string reason)
        {
            lock (_gate)
            {
                _quietGeneration++;
                foreach (Fact fact in _order)
                {
                    if (fact.AdmittedAtQuietGeneration < _quietGeneration && !fact.Reviewed)
                        fact.Pause(reason);
                }
                MarkDirtyLocked();
                Tracing.TraceLine(
                    "FactStore: quiet barrier advanced to generation " + _quietGeneration + " — " + reason
                    + ". Nothing is acknowledged and nothing is cleared; the paused information stays "
                    + "on the list.",
                    TraceLevel.Info);
                return _quietGeneration;
            }
        }

        /// <summary>
        /// The operator explicitly reviewed what is on screen, for the material
        /// revision he was shown.
        /// </summary>
        /// <remarks>
        /// <b>Scoped to the revision SHOWN.</b> It cannot acknowledge a newer
        /// revision that arrived behind the selected row while he was reading
        /// it. Reviewing a store record never acknowledges, snoozes or disables
        /// an operator alarm — that belongs to the alarm's own control.
        /// </remarks>
        public bool MarkReviewed(string episodeId, long materialRevisionShown)
        {
            lock (_gate)
            {
                if (!_facts.TryGetValue(episodeId, out Fact? fact)) return false;
                if (materialRevisionShown < fact.MaterialRevision) return false;
                fact.MarkReviewed(materialRevisionShown);
                MarkDirtyLocked();
                return true;
            }
        }

        /// <summary>
        /// Resume automatic delivery for ONE selected record.
        /// </summary>
        /// <remarks>
        /// <b>One at a time, deliberately.</b> One successful read cannot
        /// silently resume every paused item — that would turn a single
        /// deliberate action into permission the operator never gave.
        /// </remarks>
        public bool ResumeAutomatic(string episodeId)
        {
            lock (_gate)
            {
                if (!_facts.TryGetValue(episodeId, out Fact? fact)) return false;
                fact.Resume();
                MarkDirtyLocked();
                return true;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Eligibility — what may be offered for an automatic attempt
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// May this fact be offered for another AUTOMATIC presentation?
        /// </summary>
        /// <remarks>
        /// <para>
        /// The scheduler asks this; it does not decide it. An explicit read
        /// requested by the operator goes through
        /// <see cref="MayReadOnRequest"/> instead and is subject to almost none
        /// of this — asking for something is its own permission.
        /// </para>
        /// <para>
        /// <b>No clause here is a clock.</b> Nothing below asks how old
        /// anything is.
        /// </para>
        /// </remarks>
        public bool IsEligibleForAutomaticDelivery(Fact fact)
        {
            if (fact == null) return false;

            // ── DECISION POINT ONE — OPEN QUESTION WITH NOEL ──────────────
            // "Should a Ctrl-silenced persistent condition resume speaking on
            // its own once the condition is still true and the channel is
            // healthy?"
            //
            // The design's answer, built here: NO. A pause is released by an
            // explicit read or resume for the selected record, and by nothing
            // else — not a reconnect, not a window opening, not the backend
            // recovering, and not time passing. A positively established NEW
            // hazard occurrence still gets its own warning, so the
            // safety-relevant half is protected; a condition that was silenced
            // stops volunteering and stays reachable on the list.
            //
            // If Noel rules the other way, this is the line that changes.
            if (fact.AutomaticPaused) return false;

            // ── DECISION POINT TWO — OPEN QUESTION WITH NOEL ──────────────
            // "What should an unclassified key do if one reaches a running
            // build?"
            //
            // The design's answer, built here: it is SILENT and the fact is
            // KEPT. Automatic presentation and retries are blocked because
            // nothing knows how long the information stays worth saying; the
            // fact keeps its slot, keeps its receipt, and is readable on the
            // list with wording that says the presentation information is
            // missing. It must never speak the raw key as the safety
            // explanation, and it must never be discarded.
            //
            // A text-only entry is refused here too, and for a different
            // reason: somebody affirmatively said that string is not a message,
            // so treating it as one would override a decision rather than fill
            // a gap.
            //
            // If Noel rules the other way, this is the line that changes.
            if (fact.Classification != DeliveryClassification.Message) return false;

            // A rendering that is not justified is not offered. Unknown
            // suspends the current wording immediately, without claiming
            // anything resolved.
            if (!fact.Validity.IsCurrent) return false;

            // Forgettable gets one eligible presentation. A miss is a miss, and
            // an obsolete first hearing is still obsolete.
            if (fact.Delivery!.ShelfLife == ShelfLife.Forgettable && fact.Attempts.Count > 0) return false;

            if (fact.Reviewed) return false;

            return fact.HasUndeliveredDetail;
        }

        /// <summary>
        /// May this fact be read out because the operator asked for this
        /// record?
        /// </summary>
        /// <remarks>
        /// <para>
        /// Asking is its own permission, so quiet and the automatic burst rule
        /// do not apply. What still applies is truth: the plan is revalidated
        /// immediately before dispatch, and a fact whose premise has ended is
        /// read as HISTORY rather than replayed as a present-tense assurance.
        /// </para>
        /// <para>
        /// Reading never transmits and never re-runs the original action.
        /// </para>
        /// </remarks>
        public bool MayReadOnRequest(Fact fact) => fact != null;

        /// <summary>
        /// True when this attempt would be the third or later repeat in a
        /// burst, so a waiting explicit request should go first.
        /// </summary>
        /// <remarks>
        /// <b>Fairness, not a lifetime cap.</b> See
        /// <see cref="FactStoreCapacity.AutomaticBurstAttempts"/> — answering
        /// true here yields the slot; it never retires the fact.
        /// </remarks>
        public bool ShouldYieldToWaitingRequest(Fact fact)
            => fact != null && fact.Attempts.Count >= FactStoreCapacity.AutomaticBurstAttempts;

        // ────────────────────────────────────────────────────────────────
        //  The reachable projections
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The operator's default list: information whose required detail has
        /// no confirmed complete presentation and has not been reviewed.
        /// </summary>
        /// <remarks>
        /// Includes deliberately paused items. Excludes a persistent condition
        /// that WAS fully presented — that belongs in the live status and alarm
        /// surfaces, and putting it here too is how a narrow list becomes a
        /// notifications centre nobody reads.
        /// </remarks>
        public IReadOnlyList<Fact> Pending(string? stationFilter = null)
        {
            lock (_gate)
            {
                var list = new List<Fact>();
                foreach (Fact fact in _order)
                    if (fact.IsPending && MatchesStation(fact, stationFilter)) list.Add(fact);

                // A filter that hides the only pending item is a filter that
                // lies about the state of the station.
                if (list.Count == 0 && stationFilter != null)
                {
                    foreach (Fact fact in _order) if (fact.IsPending) list.Add(fact);
                }
                return list;
            }
        }

        /// <summary>
        /// The bounded history view: perishable events, including ones whose
        /// speech completed, plus anything whose premise has ended.
        /// </summary>
        public IReadOnlyList<Fact> History(string? stationFilter = null)
        {
            lock (_gate)
            {
                var list = new List<Fact>();
                foreach (Fact fact in _order)
                {
                    bool perishable = fact.Delivery != null && fact.Delivery.ShelfLife == ShelfLife.Perishable;
                    if (!perishable && !fact.IsHistorical) continue;
                    if (!MatchesStation(fact, stationFilter)) continue;
                    list.Add(fact);
                }
                return list;
            }
        }

        /// <summary>Every retained fact, newest last.</summary>
        public IReadOnlyList<Fact> All
        {
            get { lock (_gate) return new List<Fact>(_order); }
        }

        /// <summary>One fact by episode, or null.</summary>
        public Fact? Find(string episodeId)
        {
            lock (_gate) return _facts.TryGetValue(episodeId, out Fact? fact) ? fact : null;
        }

        /// <summary>How many items are on the default list. For the Status summary.</summary>
        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    int n = 0;
                    foreach (Fact fact in _order) if (fact.IsPending) n++;
                    return n;
                }
            }
        }

        private static bool MatchesStation(Fact fact, string? station)
            => station == null || string.Equals(fact.Identity.RadioIdentity, station, StringComparison.Ordinal);

        // ────────────────────────────────────────────────────────────────
        //  Delivery channel health
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Note whether the delivery channel is working, and say whether this
        /// call is the TRANSITION into failure.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Returns true exactly once per working-to-failing edge, so the
        /// aggregated notice is raised once rather than once per fact. The
        /// notice must not use the failing speech bridge, must not steal the
        /// emergency-stop route, must be Escape-closable and must leave a way
        /// back to where the operator was.
        /// </para>
        /// <para>
        /// <b>Deliberate suppression is not a failure.</b> Raising a notice
        /// because the operator asked for silence would defeat the instruction
        /// through another channel, so quiet never reaches this method.
        /// </para>
        /// </remarks>
        public bool NoteChannelHealth(bool healthy)
        {
            lock (_gate)
            {
                if (healthy)
                {
                    _channelFailing = false;
                    return false;
                }
                if (_channelFailing) return false;
                _channelFailing = true;
                return true;
            }
        }

        /// <summary>True while the delivery channel is known to be failing.</summary>
        public bool ChannelFailing
        {
            get { lock (_gate) return _channelFailing; }
        }

        // ────────────────────────────────────────────────────────────────
        //  Persistence status — reported, never promised
        // ────────────────────────────────────────────────────────────────

        /// <summary>Called by the journal. The store itself never touches disk.</summary>
        public void NotePersistence(PersistenceStatus status, string? note = null)
        {
            lock (_gate)
            {
                Persistence = status;
                PersistenceNote = note;
            }
        }

        /// <summary>
        /// Put a record back after a restart, as HISTORY.
        /// </summary>
        /// <remarks>
        /// <b>A restored record never speaks on its own.</b> It carries no live
        /// capability, its connection is gone, and its current state is
        /// unknown. A new connection creates new authority and may relate to an
        /// older condition without retroactively confirming it.
        /// </remarks>
        public void Restore(Fact fact)
        {
            if (fact == null) return;
            lock (_gate)
            {
                fact.RestoredFromDisk = true;
                if (_facts.ContainsKey(fact.Identity.EpisodeId)) return;
                _facts[fact.Identity.EpisodeId] = fact;
                _order.Add(fact);
            }
        }

        /// <summary>
        /// Drop the oldest REVIEWED, completed historical record to make room.
        /// Returns false when nothing may be dropped.
        /// </summary>
        /// <remarks>
        /// <b>Compaction can never decrement the unpresented count as though
        /// delivery occurred.</b> Nothing pending is touched; when only pending
        /// records remain, this refuses and the caller records overflow
        /// honestly instead.
        /// </remarks>
        private bool Compact()
        {
            for (int i = 0; i < _order.Count; i++)
            {
                Fact fact = _order[i];
                if (fact.IsPending) continue;
                if (!fact.Reviewed && fact.HasUndeliveredDetail) continue;
                _order.RemoveAt(i);
                _facts.Remove(fact.Identity.EpisodeId);
                return true;
            }
            return false;
        }

        private void MarkDirtyLocked()
        {
            if (Persistence == PersistenceStatus.UpToDate) Persistence = PersistenceStatus.Pending;

            // Raised outside the gate by the caller's continuation, not here:
            // invoking a handler under the store's lock is how a fast path
            // acquires somebody else's slow one.
            Action? handler = Dirty;
            if (handler != null) System.Threading.ThreadPool.QueueUserWorkItem(_ => handler());
        }
    }
}
