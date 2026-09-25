#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JJTrace;

namespace Radios.Facts
{
    /// <summary>One assertion in a continuity record, with the evidence the occurrence has for it.</summary>
    internal sealed class ContinuityAssertionRecord
    {
        public AssertionRef Root;
        public long Unit;                 // the unit's identity in the episode the record was derived from
        public string Name = string.Empty;
        public FactValue Value;
        public MaterialKind Kind;
        public bool Covered;
        public bool Reviewed;
    }

    /// <summary>
    /// What the store remembers about a logical condition on an established
    /// station across a disconnect — outside the disposable session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed by station identity, condition and contract. <b>Not an authority
    /// token</b>, and it never merges distinct historical episodes. The
    /// definition revision is recorded, not keyed on, so a changed alarm
    /// definition is not mistaken for a new onset — and is not taken as a
    /// supported comparison either.
    /// </para>
    /// <para>
    /// It carries the occurrence's lineage (root, newest episode, revision),
    /// a bounded per-assertion evidence summary, and the receipt consequence.
    /// These are references and summaries, not a second transcript: enough
    /// for a successor to know what was already presented, reviewed and
    /// sounded, and no more.
    /// </para>
    /// </remarks>
    internal sealed class ContinuityRecord
    {
        public string Radio = string.Empty;
        public string Condition = string.Empty;
        public string Contract = string.Empty;
        public int DefinitionRevision;
        public PauseCause Pause;
        public FactObservation Baseline = FactObservation.Empty;
        public EpisodeId? Root;
        public EpisodeId? LastEpisode;
        public long Revision;
        public bool FromPreviousProcess;

        /// <summary>False for a continuity saved by a format that kept no per-assertion evidence.</summary>
        public bool DeliveryEvidenceSupported = true;

        /// <summary>Two saved sources disagreed about this key. Neither candidate is chosen.</summary>
        public bool Conflicted;

        public readonly List<ContinuityAssertionRecord> Assertions = new();

        public ReceiptPolicy ReceiptPolicy;
        public ReceiptState ReceiptState;
        public bool ReceiptConsumed;
        public long ReceiptId;
        public bool ReceiptClosed;

        /// <summary>The occurrence roots this occurrence replaced for its key, oldest first. Succession evidence, bounded.</summary>
        public readonly List<EpisodeId> SupersededRoots = new();

        /// <summary>Every root a saved source has named for this key, so a later successor can be checked against all of them.</summary>
        public readonly List<EpisodeId> SeenRoots = new();

        public long Order;

        public static string KeyOf(string radio, string condition, string contract) =>
            radio + "\u001f" + condition + "\u001f" + contract;

        public string Key => KeyOf(Radio, Condition, Contract);

        /// <summary>A continuation may claim this: it has a root, its evidence is usable, and nothing disputes it.</summary>
        public bool Supported => Root != null && DeliveryEvidenceSupported && !Conflicted;
    }

    public sealed partial class FactStore
    {
        private readonly List<FactOwner> _owners = new();
        private readonly List<FactScope> _scopes = new();
        private readonly List<SlotPublisher> _slots = new();
        private readonly Dictionary<long, FactRecord> _openedByEvent = new();
        private readonly Dictionary<string, ContinuityRecord> _continuity = new(StringComparer.Ordinal);
        private long _continuityOrder;
        private readonly HashSet<string> _continuityEvicted = new(StringComparer.Ordinal);
        private bool _continuityEvictedOverflow;
        private readonly Dictionary<EpisodeId, OccurrenceLineage> _lineages = new();

        // ────────────────────────────────────────────────────────────────
        //  Issuance — reached only through FactAuthorityRegistry
        // ────────────────────────────────────────────────────────────────

        internal FactOwner DeclareOwnerInternal(string name, ConditionContract[] contracts)
        {
            lock (Gate)
            {
                var owner = new FactOwner(this, name, contracts.ToArray());
                _owners.Add(owner);
                return owner;
            }
        }

        internal FactSession OpenSessionInternal(string? radioIdentity, string label)
        {
            lock (Gate)
            {
                var session = new FactSession(this, Checked(ref _nextScope), radioIdentity, label);
                _scopes.Add(session);
                return session;
            }
        }

        internal FactActivity OpenActivityInternal(string label)
        {
            lock (Gate)
            {
                var activity = new FactActivity(this, Checked(ref _nextScope), label);
                _scopes.Add(activity);
                return activity;
            }
        }

        internal RegistrationResult RegisterInternal(FactOwner owner, FactScope scope, ConditionContract contract, ConditionKey condition)
        {
            if (owner == null || scope == null || contract == null || condition == null)
                return new RegistrationResult(RegistrationOutcome.AuthorityMismatch, null, "an argument was missing");

            try
            {
                lock (Gate)
                {
                    if (!ReferenceEquals(owner.Store, this) || !ReferenceEquals(scope.Store, this))
                    {
                        NoteIntegrityLocked("registration", "a registration named an owner or scope another store issued",
                                            owner.Name + " / " + condition);
                        return new RegistrationResult(RegistrationOutcome.AuthorityMismatch, null,
                            "the owner or scope was not issued by this store");
                    }

                    if (!owner.Declares(contract))
                    {
                        NoteIntegrityLocked("registration",
                            "a registration asked for a contract its owner was never declared for",
                            owner.Name + " asked for " + contract);
                        return new RegistrationResult(RegistrationOutcome.AuthorityMismatch, null,
                            owner.Name + " was not declared for " + contract);
                    }

                    if (scope.EndedLocked)
                        return new RegistrationResult(RegistrationOutcome.ScopeEnded, null, "the scope has ended");

                    foreach (SlotPublisher existing in _slots)
                    {
                        if (existing.Released) continue;
                        if (!ReferenceEquals(existing.Owner, owner) || !ReferenceEquals(existing.Scope, scope)) continue;
                        if (!existing.Condition.Equals(condition)) continue;

                        // Idempotent only when the ENTIRE contract agrees.
                        return existing.Contract.SameAs(contract)
                            ? new RegistrationResult(RegistrationOutcome.Granted, existing, "already registered")
                            : new RegistrationResult(RegistrationOutcome.ContractConflict, null,
                                "this owner registered the condition before under a different contract");
                    }

                    int active = _slots.Count(s => !s.Released);
                    if (active >= FactStoreCapacity.MaxCurrentSlots)
                    {
                        // The refusal is a row, in the SAME transition. It
                        // concerns delivery registration only — never
                        // permission to perform a protective stop.
                        string why = Lexicon.Get("facts.capacity.slot_refused", ("count", active));
                        NoteIssueLocked(IssueKind.RegistrationPressure, "slots", "condition registration",
                            "no current slot could be reserved for " + condition + "; it is not monitored",
                            1, ExtentCertainty.Exact, owner.Name + " / " + condition,
                            "reg:" + scope.ScopeId + ":" + owner.Name + ":" + condition);
                        return new RegistrationResult(RegistrationOutcome.Exhausted, null, why);
                    }

                    var publisher = new SlotPublisher(this, owner, scope, contract, condition, Checked(ref _nextSlot));
                    _slots.Add(publisher);
                    return new RegistrationResult(RegistrationOutcome.Granted, publisher, "registered");
                }
            }
            finally
            {
                RaiseSignals();
            }
        }

        internal bool ScopeEnded(FactScope scope)
        {
            lock (Gate) return scope.EndedLocked;
        }

        internal bool PublisherRevoked(SlotPublisher publisher)
        {
            lock (Gate) return publisher.RevokedLocked || publisher.Scope.EndedLocked;
        }

        // ────────────────────────────────────────────────────────────────
        //  Capture — the earliest trusted boundary
        // ────────────────────────────────────────────────────────────────

        internal CaptureResult Capture(SlotPublisher publisher, FactObservation observation, DateTime observedUtc, string? sourceEventId)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(publisher.Store, this))
                    return new CaptureResult(CaptureOutcome.AuthorityMismatch, null, "another store's publisher");

                // A callback registered against an old connection cannot fetch
                // today's session to create an event. Its publisher ended with
                // its scope, and the refusal is here.
                if (publisher.RevokedLocked || publisher.Scope.EndedLocked)
                    return new CaptureResult(CaptureOutcome.ScopeEnded, null, "the scope has ended");

                if (!publisher.Contract.Accepts(observation ?? FactObservation.Empty, out string? why))
                    return new CaptureResult(CaptureOutcome.EvidenceRejected, null, why ?? "evidence rejected");

                long? sequence = NextSequenceLocked();
                if (sequence == null)
                    return new CaptureResult(CaptureOutcome.SequenceExhausted, null, "the ordered sequence is exhausted");

                var ev = new CapturedFactEvent(publisher, Checked(ref _nextEvent), sequence.Value, _latestQuiet,
                                               sourceEventId, observation ?? FactObservation.Empty, observedUtc);
                return new CaptureResult(CaptureOutcome.Captured, ev, "captured");
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Open
        // ────────────────────────────────────────────────────────────────

        internal PublicationResult Open(SlotPublisher publisher, CapturedFactEvent ev, string claim, string messageKey,
                                        IEnumerable<MaterialDeclaration>? materials, OpenOptions options)
        {
            MaterialDeclaration[] declared = (materials ?? Array.Empty<MaterialDeclaration>()).ToArray();
            try
            {
                lock (Gate) return OpenLocked(publisher, ev, claim, messageKey, declared, options);
            }
            finally
            {
                RaiseSignals();
            }
        }

        /// <summary>
        /// What a reconnect's first observation turned out to be, once the
        /// owner's claim was checked against what the store retained.
        /// </summary>
        private sealed class ContinuityDecision
        {
            /// <summary>
            /// An evidenced new onset, or an application activity's first
            /// observation: a fresh occurrence grant and a fresh receipt. A
            /// radio condition's first observation with nothing on record is
            /// NOT this — see <see cref="PauseCause.OnsetNotEstablished"/>.
            /// </summary>
            public bool FreshOccurrence;

            /// <summary>The continuity this observation continues, when it does.</summary>
            public ContinuityRecord? Prior;

            /// <summary>The continuity a fresh occurrence replaced for its key, when there was one.</summary>
            public ContinuityRecord? Replaced;

            /// <summary>The predecessor episode as this process holds it, live or restored; null when only the table knows it.</summary>
            public FactRecord? Predecessor;

            /// <summary>Declared unit name to the predecessor assertion it reuses. Checked, never assumed.</summary>
            public readonly Dictionary<string, ContinuityAssertionRecord> Links = new(StringComparer.Ordinal);

            /// <summary>Why the successor is not automatically permitted when no grant says otherwise.</summary>
            public PauseCause Pause;

            public bool Continues => Prior != null;

            /// <summary>Same process: permission rebinds at its original position and the receipt is the same object.</summary>
            public bool SameProcess => Predecessor != null && !Predecessor.Restored;
        }

        private PublicationResult OpenLocked(SlotPublisher publisher, CapturedFactEvent ev, string claim, string messageKey,
                                             MaterialDeclaration[] declared, OpenOptions options)
        {
            if (ev == null || !ReferenceEquals(ev.Publisher, publisher) || !ReferenceEquals(publisher.Store, this))
            {
                NoteIntegrityLocked("authority", "an occurrence was opened with an event another slot captured",
                                    publisher.Owner.Name + " / " + publisher.Condition);
                return Refused(PublicationOutcome.AuthorityMismatch, "the event was not captured by this publisher");
            }

            ConditionContract contract = publisher.Contract;
            if (!contract.AllowsClaim(claim) || !contract.AllowsMessage(messageKey))
            {
                // A producer cannot borrow another condition's message, claim a
                // stop result from a command request, or buy urgent priority
                // with a key.
                NoteIntegrityLocked("authority",
                    "an owner made a claim or used a message its contract does not allow",
                    publisher.Owner.Name + ": " + claim + " / " + messageKey,
                    "claim:" + ev.EventOrdinal);
                return Refused(PublicationOutcome.AuthorityMismatch,
                    contract + " does not allow claim '" + claim + "' with message '" + messageKey + "'");
            }

            if (declared.Length + 1 > FactStoreCapacity.MaxMaterialUnitsPerFact)
                return Refused(PublicationOutcome.EvidenceRejected,
                    "an occurrence may declare at most " + (FactStoreCapacity.MaxMaterialUnitsPerFact - 1) + " units of information");

            string fingerprint = OpenFingerprint(claim, messageKey, declared, options);
            if (_openedByEvent.TryGetValue(ev.EventOrdinal, out FactRecord? already))
            {
                if (already.AppliedEvents.TryGetValue(ev.EventOrdinal, out string? was) && was == fingerprint)
                    return new PublicationResult(PublicationOutcome.Duplicate, new EpisodeHandle(publisher, already.Id),
                                                 FreezeLocked(already), "this occurrence was already opened by this event");

                NoteIntegrityLocked("conflict", "one event opened two different occurrences",
                                    publisher.Owner.Name + " event " + ev.EventOrdinal, "open-conflict:" + ev.EventOrdinal);
                return Refused(PublicationOutcome.EvidenceConflict, "this event already opened a different occurrence");
            }

            // After retirement: only an event captured BEFORE the scope ended
            // may still contribute, and only as history.
            bool historical = false;
            if (publisher.RevokedLocked || publisher.Scope.EndedLocked)
            {
                if (ev.Sequence >= publisher.Scope.EndedAtSequenceLocked)
                    return Refused(PublicationOutcome.ScopeEnded, "the scope ended before this event was captured");
                historical = true;
            }

            // Continuity: a new session's first observation must not become a
            // fresh warning, a fresh obligation or a fresh tone merely because
            // the session changed — and absence of a record is not evidence.
            var decision = new ContinuityDecision();
            if (!historical)
            {
                PublicationResult? refusal = DecideContinuityLocked(publisher, contract, messageKey, declared, options, decision);
                if (refusal != null) return refusal;
            }

            // Capacity: the reserved current slot is separate from history, so
            // a full history never stops a registered slot's current record.
            if (_order.Count >= FactStoreCapacity.MaxHistoricalRecords && !CompactLocked())
            {
                bool slotHasCurrent = _order.Any(r => ReferenceEquals(r.Publisher, publisher) && r.IsLive && r.Validity.IsCurrent);
                if (historical || slotHasCurrent)
                {
                    NoteIssueLocked(IssueKind.RetentionPressure, "retention", "retained facts",
                        "the store was full and nothing could be compacted, so an occurrence was counted rather than kept",
                        1, ExtentCertainty.Exact, publisher.Owner.Name + " / " + messageKey, "evt:" + ev.EventOrdinal);
                    return new PublicationResult(PublicationOutcome.CapacityRecorded, null, null,
                        Lexicon.Get("facts.capacity.overflow"));
                }
            }

            if (_nextEpisode == long.MaxValue)
                return Refused(PublicationOutcome.SequenceExhausted, "episode ordinals are exhausted");

            LexiconMessage message = Catalog.Lookup(messageKey);
            var record = new FactRecord
            {
                Id = new EpisodeId(WriterIncarnation, ++_nextEpisode),
                OwnerName = publisher.Owner.Name,
                ContractName = contract.Name,
                ContractRevision = contract.Revision,
                Condition = publisher.Condition,
                Claim = claim,
                ScopeId = publisher.Scope.ScopeId,
                ScopeKind = publisher.Scope.Kind,
                RadioIdentity = publisher.Scope.RadioIdentity,
                OccurrenceLabel = options.OccurrenceLabel,
                Priority = contract.Priority,
                Publisher = publisher,
                Scope = publisher.Scope,
                MessageKey = messageKey,
                Classification = message.Classification,
                Delivery = message.Delivery,
                CatalogGeneration = message.CatalogGeneration,
                Validity = historical
                    ? ValiditySnapshot.End(EndedKind.EndedObservationContext, ev.ObservedUtc,
                        "admitted after its observation context ended")
                    : ValiditySnapshot.Establish(ev.ObservedUtc),
                ObservedUtc = ev.ObservedUtc,
                LastEventSequence = ev.Sequence,
                Revision = 1,
                ObservationRevision = 1,
                MaterialRevision = 1,
                CurrentValues = ev.Observation,
                Baseline = options.Baseline ?? ev.Observation,
                ContinuityPause = decision.Pause,
            };
            record.LineageRoot = decision.Continues ? decision.Prior!.Root!.Value : record.Id;
            record.PredecessorEpisode = decision.Continues ? decision.Prior!.LastEpisode : null;
            record.ApplyDetail(options.Detail);

            // Material: each declared unit is either the SAME assertion the
            // predecessor held — linked, so its evidence is found through the
            // occurrence — or new information, owed and unpermitted. The
            // occurrence core links itself when the message is the same.
            AddLinkedMaterial(record, MaterialUnit.CoreName, FactValue.Of(messageKey), MaterialKind.Core, decision);
            foreach (MaterialDeclaration d in declared)
                AddLinkedMaterial(record, d.Name, d.Value, MaterialKind.Initial, decision);

            // Permission. An evidenced onset (or an activity's first
            // observation) earns its own grant from its own event. A continuation in
            // the same process rebinds the predecessor's grants at their
            // ORIGINAL positions, over exactly the carried units. Everything
            // else — unknown, lost, a continuation across a restart — gets no
            // grant at all.
            if (!historical && decision.FreshOccurrence)
            {
                var grant = new AutomaticGrant
                {
                    Id = Checked(ref _nextGrant),
                    SourceSequence = ev.Sequence,
                    Origin = GrantOrigin.Occurrence,
                };
                foreach (MaterialUnit unit in record.Materials) grant.Covers.Add(unit.Id);
                record.Grants.Add(grant);
            }
            else if (!historical && decision.SameProcess)
            {
                InheritGrantsLocked(record, decision.Predecessor!);
            }

            // Receipt: one allowance per OCCURRENCE. A continuation shares the
            // occurrence's — the same object in this process, its recorded
            // consequence across a restart — and an unknown or lost
            // continuity has none.
            ReceiptPolicy policy = message.Classification == DeliveryClassification.Message && message.Delivery != null
                ? message.Delivery.Receipt
                : ReceiptPolicy.None;
            if (historical || !(decision.FreshOccurrence || decision.Continues))
            {
                record.Receipt = new ReceiptRecord { Policy = ReceiptPolicy.None };
            }
            else if (decision.FreshOccurrence)
            {
                record.Receipt = new ReceiptRecord
                {
                    Policy = policy,
                    ReceiptId = policy != ReceiptPolicy.None ? Checked(ref _nextReceipt) : 0,
                };
            }
            else if (decision.SameProcess)
            {
                record.Receipt = decision.Predecessor!.Receipt;
            }
            else
            {
                ContinuityRecord prior = decision.Prior!;
                record.Receipt = new ReceiptRecord
                {
                    Policy = prior.ReceiptPolicy,
                    State = prior.ReceiptState,
                    Consumed = true,
                    ReceiptId = prior.ReceiptId,
                    FromPreviousProcess = true,
                    Closed = prior.ReceiptClosed,
                };
            }

            record.AppliedEvents[ev.EventOrdinal] = fingerprint;
            record.NoteEvent(ev, historical ? "opened as history" : decision.Continues ? "opened as a continuation" : "opened");
            _openedByEvent[ev.EventOrdinal] = record;
            AddRecordLocked(record);
            AttachLineageLocked(record);
            if (decision.Continues) record.Lineage!.Revision++;
            else if (decision.Replaced is ContinuityRecord replaced && replaced.Root is EpisodeId replacedRoot)
            {
                // A new occurrence for a key the store already knew: record the
                // succession, so two saved sources naming different
                // occurrences can be ordered by evidence rather than by guess.
                record.Lineage!.Supersede(replacedRoot, replaced.SupersededRoots);
            }

            // A worsening against a baseline from before the reconnect: new
            // material, its own grant from the triggering event's position —
            // and NO generic occurrence grant beneath it.
            if (!historical && options.Continuity == ContinuityClaim.WorseningOfPrior && options.WorseningOfPrior != null)
                ApplyWorseningLocked(record, ev, options.WorseningOfPrior);

            TouchLocked(persist: true);
            return new PublicationResult(historical ? PublicationOutcome.AcceptedAsHistory : PublicationOutcome.Accepted,
                                         new EpisodeHandle(publisher, record.Id), FreezeLocked(record),
                                         historical ? "retained as history" : decision.Continues ? "retained as a continuation" : "retained");
        }

        private static void AddLinkedMaterial(FactRecord record, string name, FactValue value, MaterialKind kind,
                                              ContinuityDecision decision)
        {
            if (decision.Links.TryGetValue(name, out ContinuityAssertionRecord? linked) && linked.Value == value)
            {
                var origin = new AssertionRef(decision.Prior!.LastEpisode!.Value, linked.Unit);
                record.AddMaterial(name, value, kind, null, null, origin, linked.Root);
            }
            else
            {
                record.AddMaterial(name, value, kind, null, null);
            }
        }

        /// <summary>
        /// Rebind the predecessor's grants to the successor, at their original
        /// causal positions, over exactly the units the successor carries from
        /// them. A Ctrl observed during the gap therefore still blocks them,
        /// and an unknown-cause cancellation reported against the predecessor
        /// follows through the lineage.
        /// </summary>
        private void InheritGrantsLocked(FactRecord successor, FactRecord predecessor)
        {
            foreach (AutomaticGrant pg in predecessor.Grants)
            {
                var carried = new HashSet<long>();
                foreach (MaterialUnit unit in successor.Materials)
                    if (unit.Origin is AssertionRef o && o.Episode == predecessor.Id && pg.Covers.Contains(o.Unit))
                        carried.Add(unit.Id);
                if (carried.Count == 0) continue;

                var grant = new AutomaticGrant
                {
                    Id = Checked(ref _nextGrant),
                    SourceSequence = pg.SourceSequence,
                    Origin = GrantOrigin.Inherited,
                    InheritedFrom = pg,
                };
                grant.Covers.UnionWith(carried);
                successor.Grants.Add(grant);
            }
        }

        /// <summary>
        /// Check the owner's continuity claim against what the store retained.
        /// Returns a refusal for a claim the owner must reconcile; otherwise
        /// fills the decision, treating every unsupported claim as unknown
        /// continuity rather than as permission.
        /// </summary>
        private PublicationResult? DecideContinuityLocked(SlotPublisher publisher, ConditionContract contract, string messageKey,
                                                          MaterialDeclaration[] declared, OpenOptions options,
                                                          ContinuityDecision decision)
        {
            string? radio = publisher.Scope.RadioIdentity;
            bool activity = publisher.Scope.Kind == FactScopeKind.Activity;
            string condition = publisher.Condition.Condition;
            ContinuityRecord? prior = radio == null ? null : ContinuityLocked(radio, condition, contract.Name, exclude: null);
            bool lost = radio != null && prior == null
                        && ContinuityWasLostLocked(ContinuityRecord.KeyOf(radio, condition, contract.Name));

            switch (options.Continuity)
            {
                case ContinuityClaim.NewOccurrence:
                    if (options.NewOnsetEvidence == null || options.NewOnsetEvidence.IsEmpty)
                        return Refused(PublicationOutcome.EvidenceRejected,
                            "a new onset needs positive evidence; a disconnect is not evidence the condition cleared");
                    // A new occurrence root, whatever the store held: old
                    // presentation and review do not discharge a new event,
                    // even at identical values.
                    decision.FreshOccurrence = true;
                    decision.Replaced = prior;
                    return null;

                case ContinuityClaim.NoPriorRecord:
                    if (activity)
                    {
                        // An application activity has no station to continue
                        // across; its first observation is its first.
                        decision.FreshOccurrence = true;
                        return null;
                    }
                    if (radio == null) { decision.Pause = PauseCause.ContinuityUnknown; return null; }
                    if (prior != null) { decision.Pause = PauseCause.ContinuityUnknown; return null; }
                    if (lost) { decision.Pause = PauseCause.ContinuityLost; return null; }
                    if (InventoryPartialLocked())
                    {
                        // A record for this condition may exist unread. Absence
                        // is not evidence; the owner's onset path is.
                        decision.Pause = PauseCause.ContinuityUnknown;
                        return null;
                    }
                    // A complete inventory establishes that nothing is on
                    // record. It does not establish that the condition began
                    // now rather than being already active when this session
                    // first observed it: a fresh onset and a PA that was hot
                    // before the connection both produce an empty history
                    // followed by the same hot reading. So no grant and no
                    // receipt — the owner's onset path (NewOccurrence, with
                    // positive evidence) is what a first occurrence needs.
                    // Ruled by Astra's first-onset design, 2026-09-24. Whether
                    // a condition found already active at first connect should
                    // speak or sound is Noel's question, filed and open; when
                    // he rules, that is a distinct permission, not this branch.
                    decision.Pause = PauseCause.OnsetNotEstablished;
                    return null;

                case ContinuityClaim.ContinuityUnknown:
                    decision.Pause = lost ? PauseCause.ContinuityLost : PauseCause.ContinuityUnknown;
                    return null;

                case ContinuityClaim.Continuation:
                    {
                        if (radio == null) { decision.Pause = PauseCause.ContinuityUnknown; return null; }
                        if (prior == null) { decision.Pause = lost ? PauseCause.ContinuityLost : PauseCause.ContinuityUnknown; return null; }
                        if (!prior.Supported || options.Predecessor == null)
                        {
                            // A claimed continuation with no supported
                            // predecessor: retain the observation and the
                            // uncertainty; grant nothing.
                            decision.Pause = PauseCause.ContinuityUnknown;
                            return null;
                        }
                        PublicationResult? mismatch = CheckPredecessorLocked(prior, options.Predecessor);
                        if (mismatch != null) return mismatch;
                        if (publisher.Condition.DefinitionRevision != prior.DefinitionRevision)
                        {
                            // A changed definition is not a new onset, and it
                            // is not a supported comparison either.
                            decision.Pause = PauseCause.ContinuityUnknown;
                            return null;
                        }
                        PublicationResult? bad = CheckLinksLocked(prior, messageKey, declared, options.Carried, decision);
                        if (bad != null) return bad;
                        return Continue(prior, decision);
                    }

                case ContinuityClaim.WorseningOfPrior:
                    {
                        if (!contract.MayReportWorsening)
                        {
                            NoteIntegrityLocked("authority", "a worsening came from a contract that may not report one",
                                                publisher.Owner.Name + " / " + contract);
                            return Refused(PublicationOutcome.AuthorityMismatch, contract + " may not report a worsening");
                        }
                        if (radio == null || prior == null || !prior.Supported || options.Predecessor == null || options.WorseningOfPrior == null)
                            return Refused(PublicationOutcome.PredecessorMismatch,
                                "there is no supported baseline from before the reconnect to be worse than");
                        PublicationResult? mismatch = CheckPredecessorLocked(prior, options.Predecessor);
                        if (mismatch != null) return mismatch;
                        if (publisher.Condition.DefinitionRevision != prior.DefinitionRevision)
                            return Refused(PublicationOutcome.PredecessorMismatch,
                                "the condition's definition changed since the baseline was recorded, so the comparison is unsupported");
                        if (options.WorseningOfPrior.BaselineFingerprint != prior.Baseline.Fingerprint)
                            return Refused(PublicationOutcome.PredecessorMismatch,
                                "the worsening was evaluated against a different baseline than the one recorded");
                        PublicationResult? bad = CheckLinksLocked(prior, messageKey, declared, options.Carried, decision);
                        if (bad != null) return bad;
                        return Continue(prior, decision);
                    }
            }
            decision.Pause = PauseCause.ContinuityUnknown;
            return null;
        }

        private PublicationResult? Continue(ContinuityRecord prior, ContinuityDecision decision)
        {
            decision.Prior = prior;
            decision.Predecessor = prior.LastEpisode is EpisodeId last ? RecordLocked(last, 0) : null;
            if (decision.Predecessor != null && decision.Predecessor.Origin?.ConflictVariant == true) decision.Predecessor = null;
            if (!decision.SameProcess)
            {
                // Across a restart the grant is history. The record says what
                // it was: paused, or simply not re-granted.
                decision.Pause = prior.Pause != PauseCause.None ? PauseCause.ContinuityInherited : PauseCause.ContinuityAcrossRestart;
            }
            return null;
        }

        private PublicationResult? CheckPredecessorLocked(ContinuityRecord prior, ContinuityReference reference)
        {
            if (!ReferenceEquals(reference.Store, this))
                return Refused(PublicationOutcome.AuthorityMismatch, "the predecessor reference was issued by another store");
            if (reference.Root != prior.Root || reference.LastEpisode != prior.LastEpisode || reference.Revision != prior.Revision)
                return Refused(PublicationOutcome.PredecessorMismatch,
                    "the predecessor reference names " + reference + ", the continuity is at " + prior.Root + " via "
                    + prior.LastEpisode + " r" + prior.Revision + "; the owner must reconcile against the current view");
            return null;
        }

        /// <summary>
        /// Check every declared link: the unit must be declared, with exactly
        /// the assertion's value, against an assertion the continuity offers.
        /// An unproved equivalence is refused outright — it transfers nothing,
        /// and pretending it does would manufacture coverage.
        /// </summary>
        private static PublicationResult? CheckLinksLocked(ContinuityRecord prior, string messageKey, MaterialDeclaration[] declared,
                                                           IReadOnlyList<MaterialLink>? carried, ContinuityDecision decision)
        {
            foreach (MaterialLink link in carried ?? Array.Empty<MaterialLink>())
            {
                if (link.Name == MaterialUnit.CoreName)
                    return Refused(PublicationOutcome.EvidenceRejected, "the occurrence core is linked by the store, not declared");
                MaterialDeclaration? d = declared.FirstOrDefault(x => x.Name == link.Name);
                if (d == null)
                    return Refused(PublicationOutcome.EvidenceRejected, "'" + link.Name + "' is linked but was not declared");
                ContinuityAssertionRecord? a = prior.Assertions.FirstOrDefault(x => x.Root == link.Assertion);
                if (a == null)
                    return Refused(PublicationOutcome.EvidenceRejected,
                        "'" + link.Name + "' names an assertion the continuity does not offer: " + link.Assertion);
                if (a.Name != d.Name || a.Value != d.Value)
                    return Refused(PublicationOutcome.EvidenceRejected,
                        "'" + link.Name + "' is declared as " + d.Value.Invariant + " but the assertion it claims to be holds "
                        + a.Name + "=" + a.Value.Invariant + "; an unproved equivalence transfers nothing");
                if (decision.Links.ContainsKey(link.Name))
                    return Refused(PublicationOutcome.EvidenceRejected, "'" + link.Name + "' is linked twice");
                decision.Links[link.Name] = a;
            }

            ContinuityAssertionRecord? core = prior.Assertions.FirstOrDefault(x =>
                x.Name == MaterialUnit.CoreName && x.Value == FactValue.Of(messageKey));
            if (core != null) decision.Links[MaterialUnit.CoreName] = core;
            return null;
        }

        /// <summary>
        /// Saved history may exist unread: a live shard skipped, a source that
        /// would not read, a conflict, a load still in progress, or the
        /// memory of lost continuity itself overflowed. A migration
        /// limitation is not this: its records were read, with limited
        /// evidence, and a legacy continuity is looked up and found unsupported
        /// rather than missing.
        /// </summary>
        private bool InventoryPartialLocked()
        {
            if (_load == HistoryLoadState.Loading || _continuityEvictedOverflow) return true;
            foreach (IssueRecord issue in AllIssuesLocked())
                if (issue.State != IssueState.ResolvedWithHistory
                    && issue.Kind is IssueKind.RecoveryGap or IssueKind.IdentityConflict or IssueKind.IncompleteInventory)
                    return true;
            return false;
        }

        private static string OpenFingerprint(string claim, string key, MaterialDeclaration[] declared, OpenOptions o) =>
            FactHash.Of(claim + "|" + key + "|" + string.Join(";", declared.Select(d => d.Fingerprint)) + "|"
                        + o.OccurrenceLabel + "|" + (o.Baseline?.Fingerprint ?? "") + "|" + o.Continuity + "|"
                        + (o.NewOnsetEvidence?.Fingerprint ?? "") + "|" + (o.WorseningOfPrior?.Fingerprint ?? "")
                        + "|" + (o.Predecessor?.ToString() ?? "") + "|"
                        + string.Join(";", (o.Carried ?? Array.Empty<MaterialLink>()).Select(l => l.Fingerprint))
                        + "|" + o.Detail);

        // ────────────────────────────────────────────────────────────────
        //  Update and resolve
        // ────────────────────────────────────────────────────────────────

        internal PublicationResult Update(SlotPublisher publisher, EpisodeHandle episode, CapturedFactEvent ev,
                                          FactTransition transition, long expectedRevision)
        {
            try
            {
                lock (Gate) return UpdateLocked(publisher, episode, ev, transition, expectedRevision);
            }
            finally
            {
                RaiseSignals();
            }
        }

        private FactRecord? CheckEpisodeLocked(SlotPublisher publisher, EpisodeHandle? episode, CapturedFactEvent? ev,
                                               out PublicationResult? refusal)
        {
            refusal = null;
            if (episode == null || ev == null
                || !ReferenceEquals(episode.Publisher, publisher)
                || !ReferenceEquals(ev.Publisher, publisher)
                || !ReferenceEquals(publisher.Store, this))
            {
                // Wrong owner, wrong slot, another store: the same refusal,
                // and nothing about the target changes.
                NoteIntegrityLocked("authority", "a publisher tried to change an episode or use an event it did not issue",
                                    publisher.Owner.Name + " / " + publisher.Condition);
                refusal = Refused(PublicationOutcome.AuthorityMismatch, "this publisher did not open that episode");
                return null;
            }

            if (!_byId.TryGetValue(episode.Id, out List<FactRecord>? list) || list.Count == 0
                || !ReferenceEquals(list[0].Publisher, publisher))
            {
                refusal = Refused(PublicationOutcome.AuthorityMismatch, "that episode is not held by this publisher");
                return null;
            }
            return list[0];
        }

        private PublicationResult UpdateLocked(SlotPublisher publisher, EpisodeHandle episode, CapturedFactEvent ev,
                                               FactTransition transition, long expectedRevision)
        {
            FactRecord? record = CheckEpisodeLocked(publisher, episode, ev, out PublicationResult? refusal);
            if (record == null) return refusal!;

            if (transition.Kind == FactTransition.TransitionKind.Resolved && !publisher.Contract.MayResolve)
            {
                NoteIntegrityLocked("authority", "a positive resolution came from a contract that may not assert one",
                                    publisher.Owner.Name + " / " + publisher.Contract);
                return Refused(PublicationOutcome.AuthorityMismatch, publisher.Contract + " may not assert resolution");
            }

            string fingerprint = transition.Fingerprint;
            if (record.AppliedEvents.TryGetValue(ev.EventOrdinal, out string? seen))
            {
                if (seen == fingerprint)
                    return new PublicationResult(PublicationOutcome.Duplicate, episode, FreezeLocked(record), "already applied");
                NoteIntegrityLocked("conflict", "one event arrived twice with different content",
                                    record.OwnerName + " event " + ev.EventOrdinal, "upd-conflict:" + ev.EventOrdinal);
                return Refused(PublicationOutcome.EvidenceConflict, "the same event with different content");
            }

            if (publisher.RevokedLocked || publisher.Scope.EndedLocked)
            {
                if (ev.Sequence >= publisher.Scope.EndedAtSequenceLocked)
                    return Refused(PublicationOutcome.ScopeEnded, "the scope ended before this event was captured");

                // A genuinely earlier event: explicitly historical evidence.
                // It cannot create a current claim or any permission.
                if (transition.Kind == FactTransition.TransitionKind.Correction)
                    foreach (MaterialDeclaration d in transition.Materials) SupersedeAndAdd(record, d, MaterialKind.Correction);
                record.AppliedEvents[ev.EventOrdinal] = fingerprint;
                record.NoteEvent(ev, "historical evidence after the scope ended");
                record.Revision++;
                TouchLocked(persist: true);
                return new PublicationResult(PublicationOutcome.AcceptedAsHistory, episode, FreezeLocked(record),
                                             "recorded as history");
            }

            if (ev.Sequence < record.LastEventSequence)
            {
                // Older evidence cannot regress current validity or grant
                // anything. Kept in bounded history.
                record.AppliedEvents[ev.EventOrdinal] = fingerprint;
                record.NoteEvent(ev, "stale evidence, not applied");
                TouchLocked(persist: true);
                return new PublicationResult(PublicationOutcome.StaleEvidence, episode, FreezeLocked(record),
                                             "older than evidence already accepted");
            }

            if (record.Materials.Count + transition.Materials.Count > FactStoreCapacity.MaxMaterialUnitsPerFact)
            {
                NoteIssueLocked(IssueKind.RetentionPressure, "material", record.Id.ToString(),
                    "a correction would exceed the information one fact may hold, so it was counted rather than kept",
                    1, ExtentCertainty.Exact, record.OwnerName, "material:" + ev.EventOrdinal);
                return Refused(PublicationOutcome.CapacityRecorded, "this fact holds all the information it can");
            }

            if (expectedRevision != record.Revision)
                return Refused(PublicationOutcome.PredecessorMismatch,
                    "expected revision " + expectedRevision + ", the episode is at " + record.Revision
                    + "; the owner must reconcile");

            switch (transition.Kind)
            {
                case FactTransition.TransitionKind.Sample:
                    // Not material. Inherits the existing grant; never gets one.
                    record.CurrentValues = ev.Observation;
                    record.ObservationRevision++;
                    record.Validity = ValiditySnapshot.Establish(ev.ObservedUtc, transition.Note);
                    if (transition.Baseline != null) record.Baseline = transition.Baseline;
                    break;

                case FactTransition.TransitionKind.Correction:
                    record.CurrentValues = ev.Observation;
                    record.ObservationRevision++;
                    record.MaterialRevision++;
                    var added = new List<long>();
                    foreach (MaterialDeclaration d in transition.Materials)
                        added.Add(SupersedeAndAdd(record, d, MaterialKind.Correction).Id);
                    // A correction does not cross a quiet: its material joins
                    // the newest grant, whose own position decides whether it
                    // is effective.
                    AutomaticGrant? latest = record.Grants.OrderBy(g => g.SourceSequence).LastOrDefault();
                    if (latest != null) foreach (long id in added) latest.Covers.Add(id);
                    record.Validity = ValiditySnapshot.Establish(ev.ObservedUtc, transition.Note);
                    record.Baseline = transition.Baseline ?? ev.Observation;
                    if (transition.Detail != null) record.ApplyDetail(transition.Detail);
                    break;

                case FactTransition.TransitionKind.ObservationUnknown:
                    record.Validity = ValiditySnapshot.NotKnown(transition.Unknown ?? UnknownReason.ObservationFailed,
                                                                ev.ObservedUtc, transition.Note);
                    break;

                case FactTransition.TransitionKind.OperationCancelled:
                    record.Validity = ValiditySnapshot.End(EndedKind.CancelledOperation, ev.ObservedUtc, transition.Note);
                    break;

                case FactTransition.TransitionKind.Resolved:
                    record.Validity = ValiditySnapshot.End(EndedKind.ResolvedCondition, ev.ObservedUtc, transition.Note);
                    break;
            }

            record.Revision++;
            record.LastEventSequence = ev.Sequence;
            record.ObservedUtc = ev.ObservedUtc;
            RememberApplied(record, ev.EventOrdinal, fingerprint);
            record.NoteEvent(ev, transition.Kind.ToString());
            TouchLocked(persist: true);
            return new PublicationResult(PublicationOutcome.Accepted, episode, FreezeLocked(record), "applied");
        }

        private static MaterialUnit SupersedeAndAdd(FactRecord record, MaterialDeclaration d, MaterialKind kind)
        {
            MaterialUnit? previous = record.LatestNamed(d.Name);
            return record.AddMaterial(d.Name, d.Value, kind, previous?.Id, null);
        }

        private static void RememberApplied(FactRecord record, long ordinal, string fingerprint)
        {
            record.AppliedEvents[ordinal] = fingerprint;
            while (record.AppliedEvents.Count > FactStoreCapacity.MaxAppliedEventsPerFact)
            {
                long oldest = record.AppliedEvents.Keys.Min();
                record.AppliedEvents.Remove(oldest);
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Worsening — the one automatic crossing of a quiet
        // ────────────────────────────────────────────────────────────────

        internal PublicationResult Worsen(SlotPublisher publisher, EpisodeHandle episode, CapturedFactEvent ev,
                                          WorseningTransition transition)
        {
            try
            {
                lock (Gate) return WorsenLocked(publisher, episode, ev, transition);
            }
            finally
            {
                RaiseSignals();
            }
        }

        private PublicationResult WorsenLocked(SlotPublisher publisher, EpisodeHandle episode, CapturedFactEvent ev,
                                               WorseningTransition transition)
        {
            FactRecord? record = CheckEpisodeLocked(publisher, episode, ev, out PublicationResult? refusal);
            if (record == null) return refusal!;

            if (!publisher.Contract.MayReportWorsening)
            {
                NoteIntegrityLocked("authority", "a worsening came from a contract that may not report one",
                                    publisher.Owner.Name + " / " + publisher.Contract);
                return Refused(PublicationOutcome.AuthorityMismatch, publisher.Contract + " may not report a worsening");
            }

            string fingerprint = transition.Fingerprint;
            if (record.WorseningTransitions.TryGetValue(transition.TransitionId, out string? was))
            {
                if (was == fingerprint)
                    return new PublicationResult(PublicationOutcome.Duplicate, episode, FreezeLocked(record),
                                                 "this worsening was already issued");
                NoteIntegrityLocked("conflict", "a worsening transition was reused with different content",
                                    record.OwnerName + " " + transition.TransitionId, "worse-conflict:" + transition.TransitionId);
                return Refused(PublicationOutcome.EvidenceConflict, "the same transition with different content");
            }
            if (record.AppliedEvents.TryGetValue(ev.EventOrdinal, out string? seen) && seen != fingerprint)
            {
                NoteIntegrityLocked("conflict", "one event arrived twice with different content",
                                    record.OwnerName + " event " + ev.EventOrdinal, "upd-conflict:" + ev.EventOrdinal);
                return Refused(PublicationOutcome.EvidenceConflict, "the same event with different content");
            }

            if (publisher.RevokedLocked || publisher.Scope.EndedLocked)
            {
                if (ev.Sequence >= publisher.Scope.EndedAtSequenceLocked)
                    return Refused(PublicationOutcome.ScopeEnded, "the scope ended before this event was captured");
                foreach (MaterialDeclaration d in transition.WorseMaterial)
                    record.AddMaterial(d.Name, d.Value, MaterialKind.Worsening, null, record.LatestNamed(d.Name)?.Id);
                record.WorseningTransitions[transition.TransitionId] = fingerprint;
                record.NoteEvent(ev, "historical worsening after the scope ended");
                record.Revision++;
                TouchLocked(persist: true);
                return new PublicationResult(PublicationOutcome.AcceptedAsHistory, episode, FreezeLocked(record),
                                             "recorded as history; no permission");
            }

            if (record.Materials.Count + transition.WorseMaterial.Count > FactStoreCapacity.MaxMaterialUnitsPerFact)
            {
                NoteIssueLocked(IssueKind.RetentionPressure, "material", record.Id.ToString(),
                    "a worsening would exceed the information one fact may hold, so it was counted rather than kept",
                    1, ExtentCertainty.Exact, record.OwnerName, "material:" + ev.EventOrdinal);
                return Refused(PublicationOutcome.CapacityRecorded, "this fact holds all the information it can");
            }

            if (transition.BaselineFingerprint != record.Baseline.Fingerprint)
                return Refused(PublicationOutcome.PredecessorMismatch,
                    "the worsening was evaluated against a baseline this episode does not hold");

            if (ev.Sequence < record.LastEventSequence)
                return Refused(PublicationOutcome.PredecessorMismatch,
                    "a later event was already accepted; the owner must reconcile its lineage");

            ApplyWorseningLocked(record, ev, transition);
            TouchLocked(persist: true);
            return new PublicationResult(PublicationOutcome.Accepted, episode, FreezeLocked(record), "worsening accepted");
        }

        /// <summary>
        /// Apply an accepted worsening: the material is always recorded; the
        /// grant is created from the TRIGGERING event's position, so a second
        /// quiet captured before it was admitted still wins.
        /// </summary>
        private void ApplyWorseningLocked(FactRecord record, CapturedFactEvent ev, WorseningTransition transition)
        {
            HashSet<long> earlierUnpresented = record.Unpresented();
            var grant = new AutomaticGrant
            {
                Id = Checked(ref _nextGrant),
                SourceSequence = ev.Sequence,
                Origin = GrantOrigin.Worsening,
            };
            foreach (MaterialDeclaration d in transition.WorseMaterial)
            {
                MaterialUnit related = record.LatestNamed(d.Name) ?? record.Materials[0];
                grant.Covers.Add(record.AddMaterial(d.Name, d.Value, MaterialKind.Worsening, null, related.Id).Id);
            }
            if (transition.AlsoCoversEarlier) grant.Covers.UnionWith(earlierUnpresented);
            record.Grants.Add(grant);

            record.MaterialRevision++;
            record.Baseline = transition.NewBaseline ?? ev.Observation;
            record.CurrentValues = ev.Observation;
            record.ObservationRevision++;
            record.Validity = ValiditySnapshot.Establish(ev.ObservedUtc, transition.OwnerResult);
            record.Revision++;
            record.LastEventSequence = Math.Max(record.LastEventSequence, ev.Sequence);
            record.ObservedUtc = ev.ObservedUtc;
            record.WorseningTransitions[transition.TransitionId] = transition.Fingerprint;
            RememberApplied(record, ev.EventOrdinal, transition.Fingerprint);
            record.NoteEvent(ev, "worsening: " + transition.OwnerResult);
        }

        // ────────────────────────────────────────────────────────────────
        //  Lifecycle — ending a scope
        // ────────────────────────────────────────────────────────────────

        internal int EndScope(FactScope scope, DateTime asOfUtc, string why)
        {
            int touched = 0;
            try
            {
                lock (Gate)
                {
                    if (!ReferenceEquals(scope.Store, this) || scope.EndedLocked) return 0;

                    // Ending must never fail, even with the sequence exhausted.
                    scope.EndedAtSequenceLocked = _sequence >= SequenceCeiling ? long.MaxValue : ++_sequence;
                    scope.EndedLocked = true;

                    foreach (SlotPublisher publisher in _slots)
                    {
                        if (!ReferenceEquals(publisher.Scope, scope)) continue;
                        publisher.RevokedLocked = true;
                        publisher.Released = true;
                    }

                    foreach (FactRecord record in _order)
                    {
                        if (!ReferenceEquals(record.Scope, scope)) continue;

                        // A receipt not requested before its observation scope
                        // ended closes without a request. Not "requested", not
                        // "played" — and a continuation never reissues it.
                        CloseReceiptIfUnusedLocked(record);
                        if (record.Lineage != null) record.Lineage.Revision++;
                        WriteContinuityLocked(record, fromPreviousProcess: false);

                        if (!record.Validity.IsCurrent) continue;
                        record.Validity = ValiditySnapshot.End(EndedKind.EndedObservationContext, asOfUtc, why);
                        record.Revision++;
                        touched++;
                    }

                    TouchLocked(persist: true);
                }
            }
            finally
            {
                RaiseSignals();
            }

            Tracing.TraceLine(
                "FactStore: " + scope + " ended — " + why + "; " + touched + " current rendering(s) became history. "
                + "What was last established is still readable and current state is unknown, which is not the same as clear.",
                TraceLevel.Info);
            return touched;
        }

        private void CloseReceiptIfUnusedLocked(FactRecord record)
        {
            ReceiptRecord receipt = record.Receipt;
            if (receipt.Policy == ReceiptPolicy.None || receipt.Consumed || receipt.OutstandingPermit != null) return;
            // The occurrence may still be observed by a live successor that
            // shares this receipt; only an occurrence nobody observes closes.
            if (record.Lineage != null)
                foreach (FactRecord member in record.Lineage.Members)
                    if (!ReferenceEquals(member, record) && ReferenceEquals(member.Receipt, receipt) && member.IsLive) return;
            receipt.Consumed = true;
            receipt.Closed = true;
        }

        // ────────────────────────────────────────────────────────────────
        //  Quiet
        // ────────────────────────────────────────────────────────────────

        internal long ObserveQuiet(string reason)
        {
            long position;
            try
            {
                lock (Gate)
                {
                    long? sequence = NextSequenceLocked();

                    // If the sequence is exhausted the quiet must still win:
                    // it takes the ceiling, which every issued grant is below.
                    position = sequence ?? SequenceCeiling;
                    _latestQuiet = position;
                    _quiets.Add((position, reason ?? string.Empty));
                    while (_quiets.Count > FactStoreCapacity.MaxQuietHistory) _quiets.RemoveAt(0);

                    // An attempt authorised before this quiet is in flight from
                    // the application's side. Record that cancellation was
                    // REQUESTED against it — never that sound stopped.
                    foreach (FactRecord record in _order)
                    {
                        foreach (AttemptRecord attempt in record.Attempts)
                        {
                            if (!attempt.Authorized || attempt.IsTerminal) continue;
                            attempt.CancellationRequested = true;
                            attempt.CancelOperation = "operator quiet at " + position;
                        }
                    }

                    TouchLocked(persist: true);
                }
            }
            finally
            {
                RaiseSignals();
            }

            Tracing.TraceLine(
                "FactStore: operator quiet observed at position " + position + " — " + reason
                + ". Nothing is acknowledged or cleared; paused information stays on the list.",
                TraceLevel.Info);
            return position;
        }

        // ────────────────────────────────────────────────────────────────
        //  Continuity
        // ────────────────────────────────────────────────────────────────

        internal ContinuityView? ContinuityFor(SlotPublisher publisher)
        {
            lock (Gate)
            {
                string? radio = publisher.Scope.RadioIdentity;
                if (radio == null) return null;
                ContinuityRecord? record = ContinuityLocked(radio, publisher.Condition.Condition, publisher.Contract.Name, exclude: publisher);
                if (record == null) return null;

                ContinuityReference? reference = record.Supported
                    ? new ContinuityReference(this, record.Root!.Value, record.LastEpisode!.Value, record.Revision)
                    : null;
                var assertions = record.Supported
                    ? record.Assertions.Select(a => new ContinuityAssertion(a.Root, a.Name, a.Value, a.Kind, a.Covered, a.Reviewed)).ToArray()
                    : Array.Empty<ContinuityAssertion>();
                return new ContinuityView(reference, record.Pause != PauseCause.None, record.Pause, record.Baseline,
                                          record.DefinitionRevision, record.FromPreviousProcess,
                                          record.DeliveryEvidenceSupported, record.Conflicted, assertions,
                                          new ReceiptSnapshot(record.ReceiptPolicy, record.ReceiptState, record.ReceiptConsumed,
                                                              record.ReceiptId, record.FromPreviousProcess, record.ReceiptClosed));
            }
        }

        /// <summary>
        /// The continuity for a condition on a station: this process's newest
        /// earlier record for it if there is one, otherwise the table.
        /// </summary>
        private ContinuityRecord? ContinuityLocked(string radio, string condition, string contract, SlotPublisher? exclude)
        {
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                FactRecord r = _order[i];
                if (r.Restored || r.RadioIdentity != radio) continue;
                if (r.Condition.Condition != condition || r.ContractName != contract) continue;
                if (exclude != null && ReferenceEquals(r.Publisher, exclude)) continue;
                return DeriveContinuity(r, fromPreviousProcess: false);
            }
            return _continuity.TryGetValue(ContinuityRecord.KeyOf(radio, condition, contract), out ContinuityRecord? table)
                ? table
                : null;
        }

        /// <summary>The occurrence's continuity as this episode leaves it: pause, baseline, lineage, evidence and receipt.</summary>
        private ContinuityRecord DeriveContinuity(FactRecord r, bool fromPreviousProcess)
        {
            AutomaticGrant? latest = r.Grants.OrderBy(g => g.SourceSequence).LastOrDefault();
            PauseCause pause = latest != null ? FactPermission.Blocker(latest, _latestQuiet)
                               : r.ContinuityPause != PauseCause.None ? r.ContinuityPause
                               : PauseCause.None;
            var entry = new ContinuityRecord
            {
                Radio = r.RadioIdentity ?? string.Empty,
                Condition = r.Condition.Condition,
                Contract = r.ContractName,
                DefinitionRevision = r.Condition.DefinitionRevision,
                Pause = pause,
                Baseline = r.Baseline,
                Root = r.LineageRoot,
                LastEpisode = r.Id,
                Revision = r.Lineage?.Revision ?? 0,
                FromPreviousProcess = fromPreviousProcess,
                ReceiptPolicy = r.Receipt.Policy,
                ReceiptState = r.Receipt.State,
                ReceiptConsumed = r.Receipt.Consumed,
                ReceiptId = r.Receipt.ReceiptId,
                ReceiptClosed = r.Receipt.Closed,
            };
            if (r.Lineage != null) entry.SupersededRoots.AddRange(r.Lineage.SupersededRoots);
            entry.SeenRoots.Add(r.LineageRoot);
            HashSet<long> covered = r.Covered();
            HashSet<long> reviewed = r.Reviewed();
            foreach (MaterialUnit unit in r.Required())
            {
                entry.Assertions.Add(new ContinuityAssertionRecord
                {
                    Root = unit.Root, Unit = unit.Id, Name = unit.Name, Value = unit.Value, Kind = unit.Kind,
                    Covered = covered.Contains(unit.Id), Reviewed = reviewed.Contains(unit.Id),
                });
            }
            return entry;
        }

        private void WriteContinuityLocked(FactRecord record, bool fromPreviousProcess)
        {
            if (record.RadioIdentity == null) return;
            ContinuityRecord entry = DeriveContinuity(record, fromPreviousProcess);
            PutContinuityLocked(entry);
        }

        private bool ContinuityWasLostLocked(string key) =>
            _continuityEvicted.Contains(key) || _continuityEvictedOverflow;

        internal void PutContinuityLocked(ContinuityRecord entry)
        {
            _continuityEvicted.Remove(entry.Key);
            entry.Order = ++_continuityOrder;
            _continuity[entry.Key] = entry;

            // The table's evidence summary is also the occurrence's durable
            // memory, so a successor finds it even when the episode records
            // that earned it are gone.
            if (entry.Root is EpisodeId root && !entry.SeenRoots.Contains(root)) entry.SeenRoots.Add(root);
            if (entry.Supported)
            {
                OccurrenceLineage lineage = LineageLocked(entry.Root!.Value);
                foreach (ContinuityAssertionRecord a in entry.Assertions)
                    lineage.Absorb(a.Root, a.Name, a.Value, a.Kind, a.Covered, a.Reviewed);
                if (entry.Revision > lineage.Revision) lineage.Revision = entry.Revision;
                foreach (EpisodeId r in entry.SupersededRoots)
                    if (!lineage.SupersededRoots.Contains(r)) lineage.SupersededRoots.Add(r);
                while (lineage.SupersededRoots.Count > OccurrenceLineage.MaxSupersededRoots) lineage.SupersededRoots.RemoveAt(0);
            }

            while (_continuity.Count > FactStoreCapacity.MaxContinuityRecords)
            {
                // Losing a baseline is exposed, and afterwards an owner needs
                // fresh positive onset evidence or an explicit resume. Absence
                // of a record can never be read as a clear condition.
                ContinuityRecord oldest = _continuity.Values.OrderBy(c => c.Order).First();
                _continuity.Remove(oldest.Key);
                if (oldest.Root is EpisodeId evictedRoot) PruneLineageLocked(evictedRoot);
                // Remember WHICH key lost its baseline, so only that condition
                // on that station needs fresh evidence. Past this bound the
                // memory of losses is itself lost, and then every unknown key
                // is treated as possibly lost — said, not hidden.
                if (_continuityEvicted.Count < FactStoreCapacity.MaxContinuityRecords * 4) _continuityEvicted.Add(oldest.Key);
                else _continuityEvictedOverflow = true;
                NoteIssueLocked(IssueKind.DetailLoss, "continuity", "reconnect continuity",
                    "a continuity record was dropped for lack of room; a reconnect cannot tell a continuation from a new onset for it",
                    1, ExtentCertainty.Exact, oldest.Radio + " / " + oldest.Condition, "cont:" + oldest.Key + ":" + oldest.Order);
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Lineage — where a continuing assertion's evidence is resolved
        // ────────────────────────────────────────────────────────────────

        private OccurrenceLineage LineageLocked(EpisodeId root)
        {
            if (!_lineages.TryGetValue(root, out OccurrenceLineage? lineage))
                _lineages[root] = lineage = new OccurrenceLineage { Root = root };
            return lineage;
        }

        internal void AttachLineageLocked(FactRecord record)
        {
            OccurrenceLineage lineage = LineageLocked(record.LineageRoot);
            record.Lineage = lineage;
            if (!lineage.Members.Contains(record)) lineage.Members.Add(record);
        }

        private void DetachLineageLocked(FactRecord record)
        {
            OccurrenceLineage? lineage = record.Lineage;
            if (lineage == null) return;
            lineage.Absorb(record);
            lineage.Members.Remove(record);
            PruneLineageLocked(lineage.Root);
        }

        /// <summary>Drop a lineage nothing refers to: no member record and no continuity entry.</summary>
        private void PruneLineageLocked(EpisodeId root)
        {
            if (!_lineages.TryGetValue(root, out OccurrenceLineage? lineage)) return;
            if (lineage.Members.Count > 0) return;
            foreach (ContinuityRecord entry in _continuity.Values) if (entry.Root == root) return;
            _lineages.Remove(root);
        }

        // ────────────────────────────────────────────────────────────────
        //  Records and capacity
        // ────────────────────────────────────────────────────────────────

        internal void AddRecordLocked(FactRecord record)
        {
            if (!_byId.TryGetValue(record.Id, out List<FactRecord>? list))
            {
                list = new List<FactRecord>();
                _byId[record.Id] = list;
            }
            list.Add(record);
            _order.Add(record);
        }

        /// <summary>
        /// Drop the oldest discharged historical record to make room. Nothing
        /// owed and nothing live-current is ever touched. Compaction records
        /// its loss of detail; it never decrements an obligation as though
        /// delivery occurred — and it folds the record's evidence into its
        /// occurrence first, so a continuation still finds it.
        /// </summary>
        private bool CompactLocked()
        {
            for (int i = 0; i < _order.Count; i++)
            {
                FactRecord r = _order[i];
                if (r.HasRetainedDebt()) continue;
                if (r.IsLive && r.Validity.IsCurrent) continue;
                if (r.Attempts.Any(a => !a.IsTerminal)) continue;
                if (r.Receipt.OutstandingPermit != null && !r.Receipt.Consumed) continue;

                FactSnapshot s = r.Freeze(_latestQuiet, _projectionRevision);
                bool forgettableMissed = r.IsForgettable && s.Unpresented.Count > 0;
                bool counted;
                if (forgettableMissed) _compactedForgettableUnpresented = Saturate(_compactedForgettableUnpresented, 1, out counted);
                else if (s.PresentationComplete) _compactedCovered = Saturate(_compactedCovered, 1, out counted);
                else _compactedReviewedOnly = Saturate(_compactedReviewedOnly, 1, out counted);
                if (counted) _compactedCountsLowerBound = true;

                _order.RemoveAt(i);
                if (_byId.TryGetValue(r.Id, out List<FactRecord>? list))
                {
                    list.Remove(r);
                    if (list.Count == 0) _byId.Remove(r.Id);
                }
                foreach (long ordinal in r.AppliedEvents.Keys) _openedByEvent.Remove(ordinal);
                foreach (AttemptRecord attempt in r.Attempts) RetireAttemptLocked(attempt);
                RetainOverlayLocked(r);
                DetachLineageLocked(r);

                IssueRecord issue = NoteIssueLocked(IssueKind.DetailLoss, "compaction", "discharged history",
                    "older history whose information was already presented or reviewed was compacted to make room",
                    1, _compactedCountsLowerBound ? ExtentCertainty.LowerBound : ExtentCertainty.Exact,
                    null, "compact:" + r.Id);
                // Routine: it reports discharged detail, so it is history, not
                // an outstanding problem.
                issue.State = IssueState.ResolvedWithHistory;
                issue.ReviewedRevision = issue.Revision;
                return true;
            }
            return false;
        }

        internal FactSnapshot FreezeLocked(FactRecord record) => record.Freeze(_latestQuiet, _projectionRevision);

        private static PublicationResult Refused(PublicationOutcome outcome, string why) =>
            new PublicationResult(outcome, null, null, why);
    }
}
