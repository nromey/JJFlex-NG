#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JJTrace;

namespace Radios.Facts
{
    /// <summary>
    /// What the store remembers about a logical condition on an established
    /// station across a disconnect — outside the disposable session.
    /// </summary>
    /// <remarks>
    /// Keyed by station identity, condition and contract. <b>Not an authority
    /// token</b>, and it never merges distinct historical episodes. The
    /// definition revision is recorded, not keyed on, so a changed alarm
    /// definition is not mistaken for a new onset.
    /// </remarks>
    internal sealed class ContinuityRecord
    {
        public string Radio = string.Empty;
        public string Condition = string.Empty;
        public string Contract = string.Empty;
        public int DefinitionRevision;
        public PauseCause Pause;
        public FactObservation Baseline = FactObservation.Empty;
        public EpisodeId? LastEpisode;
        public bool FromPreviousProcess;
        public long Order;

        public static string KeyOf(string radio, string condition, string contract) =>
            radio + "\u001f" + condition + "\u001f" + contract;

        public string Key => KeyOf(Radio, Condition, Contract);
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
            // fresh warning merely because the session changed.
            PauseCause inherited = PauseCause.None;
            ContinuityRecord? prior = null;
            if (!historical && publisher.Scope.RadioIdentity != null)
            {
                prior = ContinuityLocked(publisher.Scope.RadioIdentity, publisher.Condition.Condition, contract.Name, exclude: null);
                bool lostBaseline = prior == null && ContinuityWasLostLocked(
                    ContinuityRecord.KeyOf(publisher.Scope.RadioIdentity, publisher.Condition.Condition, contract.Name));
                switch (options.Continuity)
                {
                    case ContinuityClaim.NoPriorRecord:
                    case ContinuityClaim.ContinuityUnknown:
                        if (prior != null && prior.Pause != PauseCause.None) inherited = PauseCause.ContinuityUnknown;
                        else if (lostBaseline) inherited = PauseCause.ContinuityLost;
                        break;
                    case ContinuityClaim.Continuation:
                        if (prior != null && prior.Pause != PauseCause.None) inherited = PauseCause.ContinuityInherited;
                        else if (lostBaseline) inherited = PauseCause.ContinuityLost;
                        break;
                    case ContinuityClaim.NewOccurrence:
                        if (options.NewOnsetEvidence == null || options.NewOnsetEvidence.IsEmpty)
                            return Refused(PublicationOutcome.EvidenceRejected,
                                "a new onset needs positive evidence; a disconnect is not evidence the condition cleared");
                        break;
                    case ContinuityClaim.WorseningOfPrior:
                        if (!contract.MayReportWorsening)
                        {
                            NoteIntegrityLocked("authority", "a worsening came from a contract that may not report one",
                                                publisher.Owner.Name + " / " + contract);
                            return Refused(PublicationOutcome.AuthorityMismatch, contract + " may not report a worsening");
                        }
                        if (prior == null || options.WorseningOfPrior == null)
                            return Refused(PublicationOutcome.PredecessorMismatch,
                                "there is no supported baseline from before the reconnect to be worse than");
                        if (options.WorseningOfPrior.BaselineFingerprint != prior.Baseline.Fingerprint)
                            return Refused(PublicationOutcome.PredecessorMismatch,
                                "the worsening was evaluated against a different baseline than the one recorded");
                        if (prior.Pause != PauseCause.None) inherited = PauseCause.ContinuityInherited;
                        break;
                }
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
            };
            record.ApplyDetail(options.Detail);

            record.AddMaterial(MaterialUnit.CoreName, FactValue.Of(messageKey), MaterialKind.Core, null, null);
            foreach (MaterialDeclaration d in declared)
                record.AddMaterial(d.Name, d.Value, MaterialKind.Initial, null, null);

            if (!historical)
            {
                var grant = new AutomaticGrant
                {
                    Id = Checked(ref _nextGrant),
                    SourceSequence = ev.Sequence,
                    Origin = GrantOrigin.Occurrence,
                    InheritedPause = inherited,
                };
                foreach (MaterialUnit unit in record.Materials) grant.Covers.Add(unit.Id);
                record.Grants.Add(grant);
            }

            ReceiptPolicy policy = message.Classification == DeliveryClassification.Message && message.Delivery != null
                ? message.Delivery.Receipt
                : ReceiptPolicy.None;
            record.Receipt = new ReceiptRecord
            {
                Policy = historical ? ReceiptPolicy.None : policy,
                ReceiptId = !historical && policy != ReceiptPolicy.None ? Checked(ref _nextReceipt) : 0,
            };

            record.AppliedEvents[ev.EventOrdinal] = fingerprint;
            record.NoteEvent(ev, historical ? "opened as history" : "opened");
            _openedByEvent[ev.EventOrdinal] = record;
            AddRecordLocked(record);

            // A worsening against a baseline from before the reconnect: new
            // material, its own grant from the triggering event's position.
            if (!historical && options.Continuity == ContinuityClaim.WorseningOfPrior && options.WorseningOfPrior != null)
                ApplyWorseningLocked(record, ev, options.WorseningOfPrior);

            TouchLocked(persist: true);
            return new PublicationResult(historical ? PublicationOutcome.AcceptedAsHistory : PublicationOutcome.Accepted,
                                         new EpisodeHandle(publisher, record.Id), FreezeLocked(record),
                                         historical ? "retained as history" : "retained");
        }

        private static string OpenFingerprint(string claim, string key, MaterialDeclaration[] declared, OpenOptions o) =>
            FactHash.Of(claim + "|" + key + "|" + string.Join(";", declared.Select(d => d.Fingerprint)) + "|"
                        + o.OccurrenceLabel + "|" + (o.Baseline?.Fingerprint ?? "") + "|" + o.Continuity + "|"
                        + (o.NewOnsetEvidence?.Fingerprint ?? "") + "|" + (o.WorseningOfPrior?.Fingerprint ?? "")
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
                return record == null
                    ? null
                    : new ContinuityView(record.Pause != PauseCause.None, record.Pause, record.Baseline,
                                         record.DefinitionRevision, record.FromPreviousProcess);
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

        /// <summary>Was this record's newest permission silenced, and by what?</summary>
        private ContinuityRecord DeriveContinuity(FactRecord r, bool fromPreviousProcess)
        {
            AutomaticGrant? latest = r.Grants.OrderBy(g => g.SourceSequence).LastOrDefault();
            PauseCause pause = latest == null ? PauseCause.None : FactPermission.Blocker(latest, _latestQuiet);
            return new ContinuityRecord
            {
                Radio = r.RadioIdentity ?? string.Empty,
                Condition = r.Condition.Condition,
                Contract = r.ContractName,
                DefinitionRevision = r.Condition.DefinitionRevision,
                Pause = pause,
                Baseline = r.Baseline,
                LastEpisode = r.Id,
                FromPreviousProcess = fromPreviousProcess,
            };
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
            while (_continuity.Count > FactStoreCapacity.MaxContinuityRecords)
            {
                // Losing a baseline is exposed, and afterwards an owner needs
                // fresh positive onset evidence or an explicit resume. Absence
                // of a record can never be read as a clear condition.
                ContinuityRecord oldest = _continuity.Values.OrderBy(c => c.Order).First();
                _continuity.Remove(oldest.Key);
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
        /// delivery occurred.
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
