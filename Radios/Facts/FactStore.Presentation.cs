#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.Facts
{
    /// <summary>One open view on the surface, and the tokens it issued.</summary>
    internal sealed class ViewState
    {
        public long Id;
        public bool Closed;
        public DisplayToken? Active;
        public readonly List<DisplayToken> Issued = new();
    }

    public sealed partial class FactStore
    {
        private readonly Dictionary<AttemptId, AttemptRecord> _attempts = new();
        private readonly Queue<AttemptRecord> _retired = new();
        private readonly Dictionary<long, ViewState> _views = new();
        private readonly List<TransportBinding> _bindings = new();

        internal TransportBinding RegisterTransportInternal(string name, TransportCapability capability)
        {
            lock (Gate)
            {
                var binding = new TransportBinding(this, Checked(ref _nextBinding), name ?? string.Empty, capability);
                _bindings.Add(binding);
                return binding;
            }
        }

        internal ReceiptEndpoint RegisterReceiptInternal(string name) => new ReceiptEndpoint(this, name ?? string.Empty);

        internal FactPresentation RegisterPresentationInternal(string name) => new FactPresentation(this, name ?? string.Empty);

        private FactRecord? RecordLocked(EpisodeId id, int variant)
        {
            return _byId.TryGetValue(id, out List<FactRecord>? list) && variant >= 0 && variant < list.Count
                ? list[variant]
                : null;
        }

        // ────────────────────────────────────────────────────────────────
        //  Plans
        // ────────────────────────────────────────────────────────────────

        internal PlanPreparation PreparePlan(FactPresentation presentation, EpisodeId id, int variant, PlanRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            FactSnapshot snapshot;
            bool historical;
            long? grantId = null;
            long planId;

            lock (Gate)
            {
                if (!ReferenceEquals(presentation.Store, this))
                    return new PlanPreparation(PreparationOutcome.NotFound, null, null, null, "another store's builder");

                FactRecord? record = RecordLocked(id, variant);
                if (record == null)
                    return new PlanPreparation(PreparationOutcome.NotFound, null, null, null, "no such episode");

                snapshot = FreezeLocked(record);

                if (request.Kind == PlanRequestKind.SelectedRead)
                {
                    SelectedReadGrant? read = request.Read;
                    if (read == null || !ReferenceEquals(read.Store, this) || read.ConsumedLocked
                        || read.Episode != id || read.Variant != variant)
                        return new PlanPreparation(PreparationOutcome.ReadGrantInvalid, null, null, snapshot,
                                                   "the read permission does not name this record, or was used");

                    // Bound to the material the operator was shown. If it
                    // moved on between the selection and now, a plan would say
                    // something unseen: refuse, and let the surface show the
                    // newer detail for him to select again. The grant names
                    // its episode, so a successor is never substituted; the
                    // same material may still be read honestly as history.
                    if (record.MaterialFingerprint() != read.MaterialFingerprint)
                        return new PlanPreparation(PreparationOutcome.ShownSnapshotChanged, null, null, snapshot,
                                                   "the record's information changed since it was shown");
                    historical = !record.IsLive || !record.Validity.IsCurrent;
                }
                else
                {
                    historical = false;
                    if (record.Classification != DeliveryClassification.Message)
                        return Unresolved(record.Classification == DeliveryClassification.TextOnly
                            ? UnresolvedPolicyReason.TextOnly : UnresolvedPolicyReason.Unclassified, snapshot);

                    if (!IsEligibleForAutomaticDelivery(snapshot))
                        return new PlanPreparation(PreparationOutcome.NotEligible, null, null, snapshot,
                                                   "not eligible for automatic presentation: " + snapshot.Pause);

                    HashSet<long> presentable = record.PresentableUnpresented(record.Unpresented());
                    AutomaticGrant? grant = record.Grants
                        .Where(g => FactPermission.Effective(g, _latestQuiet) && g.Covers.Overlaps(presentable))
                        .OrderBy(g => g.SourceSequence).LastOrDefault();
                    if (grant == null && !record.IsForgettable)
                        return new PlanPreparation(PreparationOutcome.NotEligible, null, null, snapshot, "no effective grant");
                    grantId = grant?.Id
                              ?? record.Grants.Where(g => FactPermission.Effective(g, _latestQuiet))
                                              .OrderBy(g => g.SourceSequence).LastOrDefault()?.Id;
                    if (grantId == null)
                        return new PlanPreparation(PreparationOutcome.NotEligible, null, null, snapshot, "no effective grant");
                }

                if (request.Kind == PlanRequestKind.SelectedRead && record.Classification != DeliveryClassification.Message)
                    return Unresolved(record.Classification == DeliveryClassification.TextOnly
                        ? UnresolvedPolicyReason.TextOnly : UnresolvedPolicyReason.Unclassified, snapshot);

                planId = Checked(ref _nextPlan);
            }

            // Rendering happens OUTSIDE the gate: it reads the catalogue, and
            // nothing slow runs while the ordered stream is held. The final
            // start gate re-checks everything this plan relies on.
            FactRendering? rendering = Renderer.Render(snapshot, request.Tier, historical, Catalog);
            if (rendering == null)
                return Unresolved(historical ? UnresolvedPolicyReason.MissingHistoryRendering
                                             : UnresolvedPolicyReason.MissingRendering, snapshot);

            var (coverage, values) = PlanCoverage(snapshot, rendering);

            // A selected read may only carry information the shown detail
            // represented. The fingerprint check above makes this hold; it is
            // checked anyway, because the words are what get said.
            if (request.Kind == PlanRequestKind.SelectedRead)
            {
                var represented = new HashSet<long>(request.Read!.Represented);
                foreach (long unit in coverage.Keys)
                    if (!represented.Contains(unit))
                        return new PlanPreparation(PreparationOutcome.ShownSnapshotChanged, null, null, snapshot,
                                                   "the words would carry information the shown detail did not");
            }

            var plan = new PresentationPlan(
                this, planId, id, variant, request.Kind, historical, coverage, values,
                snapshot.Revision, snapshot.ObservationRevision, snapshot.OwnerName, snapshot.ScopeId,
                snapshot.ContractName, snapshot.ContractRevision, rendering, request.Tier, snapshot.Priority,
                grantId, request.Read, snapshot.MaterialFingerprint);
            return new PlanPreparation(PreparationOutcome.Prepared, plan, null, snapshot, "prepared");
        }

        /// <summary>
        /// What a rendering carries: for each clause name it conveys, the
        /// NEWEST required unit of that name (never an older unit whose exact
        /// value is not in the words), with the value the words actually used.
        /// </summary>
        internal static (Dictionary<long, string> Coverage, Dictionary<long, FactValue> Values) PlanCoverage(
            FactSnapshot snapshot, FactRendering rendering)
        {
            var coverage = new Dictionary<long, string>();
            var values = new Dictionary<long, FactValue>();
            var required = new HashSet<long>(snapshot.Required);
            var latest = new Dictionary<string, MaterialUnit>(StringComparer.Ordinal);
            foreach (MaterialUnit unit in snapshot.Materials)
            {
                if (!required.Contains(unit.Id)) continue;
                if (!latest.TryGetValue(unit.Name, out MaterialUnit? have) || unit.Id > have.Id) latest[unit.Name] = unit;
            }
            var rendered = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach ((string name, object? value) in rendering.Arguments) rendered[name] = value;

            foreach (var pair in latest)
            {
                if (!rendering.ConveyedNames.Contains(pair.Key)) continue;
                coverage[pair.Value.Id] = pair.Key;
                // The value the words used (a non-material newer sample's,
                // where there was one), so the plan never claims an older
                // exact value was the one presented.
                values[pair.Value.Id] = rendered.TryGetValue(pair.Key, out object? used) && used != null
                    ? ValueOf(used, pair.Value.Value)
                    : pair.Value.Value;
            }
            return (coverage, values);
        }

        private static FactValue ValueOf(object used, FactValue fallback) => used switch
        {
            long l => FactValue.Of(l),
            decimal d => FactValue.Of(d),
            bool b => FactValue.Of(b),
            string s => FactValue.Of(s),
            _ => fallback,
        };

        private static PlanPreparation Unresolved(UnresolvedPolicyReason reason, FactSnapshot snapshot) =>
            new PlanPreparation(PreparationOutcome.Unresolved, null, reason, snapshot,
                "presentation metadata is unresolved (" + reason + "); the fact is kept and reachable");

        // ────────────────────────────────────────────────────────────────
        //  Attempts
        // ────────────────────────────────────────────────────────────────

        internal AttemptAllocation AllocateAttempt(FactPresentation presentation, PresentationPlan plan, TransportBinding binding)
        {
            try
            {
                lock (Gate)
                {
                    if (plan == null || binding == null || !ReferenceEquals(presentation.Store, this)
                        || !ReferenceEquals(plan.Store, this) || !ReferenceEquals(binding.Store, this))
                        return new AttemptAllocation(AllocationOutcome.ForeignPlanOrBinding, null,
                                                     "the plan or binding was not issued by this store");

                    FactRecord? record = RecordLocked(plan.Episode, plan.Variant);
                    if (record == null)
                        return new AttemptAllocation(AllocationOutcome.ForeignPlanOrBinding, null, "the episode is gone");

                    if (plan.Kind == PlanRequestKind.Automatic)
                    {
                        AttemptRecord? pending = record.Attempts.FirstOrDefault(a =>
                            a.Kind == PlanRequestKind.Automatic && a.GrantId == plan.GrantId && !a.Consumed);
                        if (pending != null)
                            return new AttemptAllocation(AllocationOutcome.AlreadyPending,
                                new AttemptHandle(this, pending.Id, pending.Plan!, pending.Binding!, duplicate: true),
                                "an unconsumed allocation already exists for this grant");
                    }
                    else
                    {
                        if (plan.Read == null || plan.Read.ConsumedLocked)
                            return new AttemptAllocation(AllocationOutcome.ReadGrantConsumed, null,
                                                         "that read permission was already used");
                        plan.Read.ConsumedLocked = true;
                    }

                    if (record.Attempts.Count(a => !a.IsTerminal) >= FactStoreCapacity.MaxAttemptEvidence)
                        return new AttemptAllocation(AllocationOutcome.AttemptCapacity, null,
                                                     "too many attempts are still open for this episode");

                    var attempt = new AttemptRecord
                    {
                        Id = new AttemptId(WriterIncarnation, Checked(ref _nextAttempt)),
                        Fact = record,
                        Plan = plan,
                        PlanId = plan.PlanId,
                        Kind = plan.Kind,
                        HistoricalPlan = plan.Historical,
                        Rendering = plan.RenderingIdentity,
                        GrantId = plan.GrantId,
                        Binding = binding,
                        BindingName = binding.Name,
                    };
                    foreach (var pair in plan.Coverage) attempt.PlanMaterial[pair.Key] = pair.Value;

                    record.Attempts.Add(attempt);
                    _attempts[attempt.Id] = attempt;
                    BoundAttemptsLocked(record);
                    TouchLocked(persist: true);
                    return new AttemptAllocation(AllocationOutcome.Allocated,
                        new AttemptHandle(this, attempt.Id, plan, binding, duplicate: false), "allocated");
                }
            }
            finally
            {
                RaiseSignals();
            }
        }

        /// <summary>Keep an episode's retained attempt evidence bounded, retiring the oldest terminal ones to tombstones.</summary>
        private void BoundAttemptsLocked(FactRecord record)
        {
            while (record.Attempts.Count > FactStoreCapacity.MaxAttemptEvidence)
            {
                AttemptRecord? oldest = record.Attempts.FirstOrDefault(a => a.IsTerminal);
                if (oldest == null) return;
                record.Attempts.Remove(oldest);
                record.RetiredAttempts.Add(oldest);
                record.CompactedAttempts = Saturate(record.CompactedAttempts, 1, out bool sat);
                if (sat) record.CompactedAttemptsLowerBound = true;
                RetireAttemptLocked(oldest);
            }
        }

        /// <summary>
        /// Keep a bounded tombstone so late evidence can still be attributed.
        /// When a tombstone is finally evicted its coverage is folded into the
        /// durable ledger, and any later evidence for it is uncorrelatable —
        /// never guessed from episode text, and the ID is never reused.
        /// </summary>
        private void RetireAttemptLocked(AttemptRecord attempt)
        {
            _retired.Enqueue(attempt);
            while (_retired.Count > FactStoreCapacity.MaxAttemptTombstones)
            {
                AttemptRecord evicted = _retired.Dequeue();
                _attempts.Remove(evicted.Id);
                evicted.Fact.RetiredAttempts.Remove(evicted);
                evicted.Fact.CoveredLedger.UnionWith(evicted.EstablishedCoverage());
            }
        }

        internal StartDecision TryCommitStart(AttemptHandle handle)
        {
            try
            {
                lock (Gate)
                {
                    if (!ReferenceEquals(handle.Store, this) || !_attempts.TryGetValue(handle.Id, out AttemptRecord? attempt))
                        return new StartDecision(false, NotStartedReason.AlreadyConsumed);

                    if (attempt.Consumed) return new StartDecision(false, NotStartedReason.AlreadyConsumed);
                    attempt.Consumed = true;

                    NotStartedReason? reason = StartBlockerLocked(attempt);
                    if (reason != null)
                    {
                        attempt.NotStarted = reason;
                        TouchLocked(persist: true);
                        return new StartDecision(false, reason);
                    }

                    attempt.Authorized = true;
                    TouchLocked(persist: true);
                    return new StartDecision(true, null);
                }
            }
            finally
            {
                RaiseSignals();
            }
        }

        /// <summary>
        /// Everything the plan relied on, checked again at the last moment,
        /// against the live record and the latest quiet — the boundary that
        /// makes "Q wins first, nothing starts" true.
        /// </summary>
        private NotStartedReason? StartBlockerLocked(AttemptRecord attempt)
        {
            PresentationPlan plan = attempt.Plan!;
            FactRecord record = attempt.Fact;

            if (plan.Kind == PlanRequestKind.Automatic)
            {
                if (!record.IsLive) return NotStartedReason.ScopeEnded;
                if (!record.Validity.IsCurrent) return NotStartedReason.NotCurrent;
                AutomaticGrant? grant = record.Grants.FirstOrDefault(g => g.Id == plan.GrantId);
                if (grant == null) return NotStartedReason.QuietAfterPermission;
                if (grant.EffectiveUnknownCancellation != null) return NotStartedReason.PausedUnknownCause;
                if (grant.SourceSequence <= _latestQuiet) return NotStartedReason.QuietAfterPermission;
            }
            else
            {
                if (plan.Read == null || plan.Read.ActionSequence <= _latestQuiet)
                    return NotStartedReason.QuietAfterPermission;
                // The material the plan was rendered from must still be the
                // record's material. The shown snapshot is what was selected.
                if (record.MaterialFingerprint() != plan.FactFingerprint)
                    return plan.Historical ? NotStartedReason.HistoricalRecordChanged : NotStartedReason.Superseded;
                if (plan.Historical)
                    return Catalog.Generation != plan.CatalogGeneration ? NotStartedReason.CatalogChanged : null;
                if (!record.IsLive) return NotStartedReason.ScopeEnded;
                if (!record.Validity.IsCurrent) return NotStartedReason.NotCurrent;
            }

            // Newer exact values: a sample the domain called non-material still
            // makes these words wrong if it changed a value they carry.
            if (record.ObservationRevision != plan.ObservationRevision)
            {
                foreach ((string name, object? value) in plan.Arguments)
                {
                    FactValue? now = record.CurrentValues.TryGet(name, out FactValue v) ? v : record.LatestNamed(name)?.Value;
                    if (now == null || !Equals(now.Value.AsArgument, value)) return NotStartedReason.Superseded;
                }
            }

            var required = new HashSet<long>(record.Required().Select(u => u.Id));
            foreach (long id in plan.Coverage.Keys)
                if (!required.Contains(id)) return NotStartedReason.Superseded;

            if (Catalog.Generation != plan.CatalogGeneration) return NotStartedReason.CatalogChanged;
            return null;
        }

        internal bool BindTicket(AttemptHandle handle, string ticket)
        {
            lock (Gate)
            {
                if (string.IsNullOrEmpty(ticket) || !ReferenceEquals(handle.Store, this)) return false;
                if (!_attempts.TryGetValue(handle.Id, out AttemptRecord? attempt)) return false;
                if (attempt.Ticket != null) return attempt.Ticket == ticket;
                attempt.Ticket = ticket;
                return true;
            }
        }

        internal EvidenceResult ReportEvidence(TransportBinding binding, AttemptId id, TransportEvidence evidence)
        {
            try
            {
                lock (Gate)
                {
                    if (binding == null || evidence == null || !ReferenceEquals(binding.Store, this))
                        return EvidenceResult.Foreign;
                    if (id.Writer != WriterIncarnation) return EvidenceResult.Foreign;
                    if (!_attempts.TryGetValue(id, out AttemptRecord? attempt)) return EvidenceResult.Uncorrelatable;
                    if (!ReferenceEquals(attempt.Binding, binding)) return EvidenceResult.Foreign;
                    if (evidence.Ticket != null && attempt.Ticket != null && evidence.Ticket != attempt.Ticket)
                        return EvidenceResult.Foreign;

                    TransportCapability needs = evidence.Requires;
                    if (needs != TransportCapability.RequestOnly && (binding.Capability & needs) != needs)
                    {
                        NoteIntegrityLocked("evidence",
                            "a transport reported evidence its registered capability does not cover",
                            binding.Name + ": " + evidence.Kind, "cap:" + id + ":" + evidence.AdapterSequence);
                        return EvidenceResult.NotPermitted;
                    }

                    if (!attempt.Authorized) return EvidenceResult.NotStarted;

                    if (attempt.Evidence.TryGetValue(evidence.AdapterSequence, out TransportEvidence? existing))
                    {
                        if (existing.Fingerprint == evidence.Fingerprint) return EvidenceResult.Duplicate;
                        NoteIntegrityLocked("evidence", "an adapter reported two different things at one sequence",
                                            binding.Name + " " + id, "seq:" + id + ":" + evidence.AdapterSequence);
                        return EvidenceResult.Conflict;
                    }

                    bool wasDisputed = attempt.Disputed;
                    attempt.Evidence.Add(evidence.AdapterSequence, evidence);
                    if (evidence.Kind == EvidenceKind.CancellationRequested)
                    {
                        attempt.CancellationRequested = true;
                        attempt.CancelOperation = evidence.Operation;
                    }

                    // An unknown-cause cancellation pauses the permission
                    // involved, with its own cause — never mistaken for the
                    // operator's quiet, never released by a recovery, and
                    // written to the lineage the permission shares with every
                    // grant inherited from it, however many reconnects later.
                    if (evidence.Kind == EvidenceKind.Cancelled && evidence.Cause == CancelCause.Unknown
                        && attempt.Kind == PlanRequestKind.Automatic)
                    {
                        AutomaticGrant? grant = attempt.Fact.Grants.FirstOrDefault(g => g.Id == attempt.GrantId);
                        if (grant != null && grant.Constraint.UnknownCancelledBy == null)
                        {
                            grant.Constraint.UnknownCancelledBy = attempt.Id;
                            grant.Constraint.UnknownCancelledAtSequence = _sequence;
                        }
                    }

                    TouchLocked(persist: true);

                    if (!wasDisputed && attempt.Disputed)
                    {
                        NoteIntegrityLocked("evidence",
                            "an attempt reported contradictory outcomes, so it discharges nothing",
                            binding.Name + " " + id, "disputed:" + id);
                        return EvidenceResult.Disputed;
                    }
                    return EvidenceResult.Recorded;
                }
            }
            finally
            {
                RaiseSignals();
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Receipts
        // ────────────────────────────────────────────────────────────────

        internal ReceiptClaim ClaimReceipt(ReceiptEndpoint endpoint, EpisodeId id)
        {
            try
            {
                lock (Gate)
                {
                    if (endpoint == null || !ReferenceEquals(endpoint.Store, this))
                        return new ReceiptClaim(ReceiptClaimOutcome.Foreign, null);
                    FactRecord? record = RecordLocked(id, 0);
                    if (record == null) return new ReceiptClaim(ReceiptClaimOutcome.NotFound, null);
                    if (!record.IsLive || record.Receipt.FromPreviousProcess)
                        return new ReceiptClaim(ReceiptClaimOutcome.NotLive, null);
                    if (record.Receipt.Policy == ReceiptPolicy.None)
                        return new ReceiptClaim(ReceiptClaimOutcome.NoAllowance, null);
                    if (record.Receipt.Consumed || record.Receipt.OutstandingPermit != null)
                        return new ReceiptClaim(ReceiptClaimOutcome.AlreadyClaimed, null);

                    if (OccurrencePausedLocked(record))
                    {
                        // A pre-Ctrl event cannot emit a late cue. The
                        // allowance is spent: its moment has passed.
                        record.Receipt.Consumed = true;
                        record.Receipt.State = ReceiptState.Withheld;
                        TouchLocked(persist: true);
                        return new ReceiptClaim(ReceiptClaimOutcome.Withheld, null);
                    }

                    var permit = new ReceiptPermit(this, endpoint, record.Receipt.ReceiptId, record.Id, record.Receipt.Policy);
                    record.Receipt.OutstandingPermit = permit;
                    record.Receipt.State = ReceiptState.Claimed;
                    TouchLocked(persist: true);
                    return new ReceiptClaim(ReceiptClaimOutcome.Granted, permit);
                }
            }
            finally
            {
                RaiseSignals();
            }
        }

        private bool OccurrencePausedLocked(FactRecord record)
        {
            AutomaticGrant? occurrence = record.Grants.FirstOrDefault(g => g.Origin == GrantOrigin.Occurrence);
            return occurrence == null || !FactPermission.Effective(occurrence, _latestQuiet);
        }

        internal ReceiptConsumeOutcome ConsumeReceipt(ReceiptPermit permit)
        {
            try
            {
                lock (Gate)
                {
                    if (!ReferenceEquals(permit.Store, this) || permit.UsedLocked) return ReceiptConsumeOutcome.AlreadyUsed;
                    permit.UsedLocked = true;

                    FactRecord? record = RecordLocked(permit.Episode, 0);
                    if (record == null || !ReferenceEquals(record.Receipt.OutstandingPermit, permit))
                        return ReceiptConsumeOutcome.AlreadyUsed;

                    record.Receipt.Consumed = true;
                    if (!record.IsLive || OccurrencePausedLocked(record))
                    {
                        record.Receipt.State = ReceiptState.Withheld;
                        TouchLocked(persist: true);
                        return ReceiptConsumeOutcome.Withheld;
                    }

                    // The boundary is crossed. Until the adapter says more,
                    // what happened is unknown — and it stays unknown across a
                    // crash, rather than becoming a replay.
                    record.Receipt.State = ReceiptState.OutcomeUnknown;
                    TouchLocked(persist: true);
                    return ReceiptConsumeOutcome.Proceed;
                }
            }
            finally
            {
                RaiseSignals();
            }
        }

        internal void ReportReceipt(ReceiptPermit permit, ToneRequestResult result)
        {
            try
            {
                lock (Gate)
                {
                    if (!ReferenceEquals(permit.Store, this) || !permit.UsedLocked) return;
                    FactRecord? record = RecordLocked(permit.Episode, 0);
                    if (record == null || !ReferenceEquals(record.Receipt.OutstandingPermit, permit)) return;

                    ReceiptState current = record.Receipt.State;
                    if (current is ReceiptState.Withheld or ReceiptState.Unavailable or ReceiptState.Suppressed) return;

                    ReceiptState next = result switch
                    {
                        ToneRequestResult.PlaybackReported => ReceiptState.PlaybackReported,
                        ToneRequestResult.Unavailable => ReceiptState.Unavailable,
                        ToneRequestResult.Suppressed => ReceiptState.Suppressed,
                        _ => ReceiptState.Requested,
                    };
                    // Never regress a stronger observation.
                    if (current == ReceiptState.PlaybackReported && next == ReceiptState.Requested) return;
                    record.Receipt.State = next;
                    TouchLocked(persist: true);
                }
            }
            finally
            {
                RaiseSignals();
            }
        }

        /// <summary>
        /// The boundary was crossed and nothing more is known. The state set
        /// at consumption already says exactly that, so this records nothing
        /// new — and, deliberately, it can never reset the allowance.
        /// </summary>
        internal void ReportReceiptUnknown(ReceiptPermit permit)
        {
            lock (Gate)
            {
                FactRecord? record = RecordLocked(permit.Episode, 0);
                if (record == null || !ReferenceEquals(record.Receipt.OutstandingPermit, permit)) return;
                record.Receipt.Consumed = true;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Views, displayed-snapshot review, resume and selected read
        // ────────────────────────────────────────────────────────────────

        internal long OpenView()
        {
            lock (Gate)
            {
                var view = new ViewState { Id = Checked(ref _nextView) };
                _views[view.Id] = view;
                return view.Id;
            }
        }

        /// <summary>Close a view. Every token it issued stops resolving.</summary>
        internal void CloseView(long viewId)
        {
            lock (Gate)
            {
                if (_views.TryGetValue(viewId, out ViewState? view))
                {
                    view.Closed = true;
                    view.Active = null;
                    view.Issued.Clear();
                    _views.Remove(viewId);
                }
            }
        }

        internal DisplayToken? IssueToken(long viewId, string itemId, EpisodeId? episode, int variant, long? issueId,
                                          IReadOnlyCollection<long> represented, long issueRevision, string fingerprint,
                                          string materialFingerprint, long projectionRevision)
        {
            lock (Gate)
            {
                if (!_views.TryGetValue(viewId, out ViewState? view) || view.Closed) return null;
                var token = new DisplayToken(this, viewId, Checked(ref _nextToken), itemId, episode, variant, issueId,
                                             represented.ToArray(), issueRevision, fingerprint, materialFingerprint,
                                             projectionRevision);
                view.Issued.Add(token);
                while (view.Issued.Count > FactStoreCapacity.MaxTokensPerView)
                {
                    DisplayToken dropped = view.Issued[0];
                    view.Issued.RemoveAt(0);
                    if (ReferenceEquals(view.Active, dropped)) view.Active = null;
                }
                return token;
            }
        }

        /// <summary>The UI installed this token's text into the read-only control. Only now is it the shown token.</summary>
        internal bool InstallToken(DisplayToken token)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(token.Store, this)) return false;
                if (!_views.TryGetValue(token.ViewId, out ViewState? view) || view.Closed) return false;
                if (!view.Issued.Contains(token)) return false;
                view.Active = token;
                return true;
            }
        }

        private int CheckTokenLocked(DisplayToken? token, out ViewState? view)
        {
            view = null;
            if (token == null || !ReferenceEquals(token.Store, this)) return 1;   // foreign
            if (!_views.TryGetValue(token.ViewId, out view) || view.Closed) return 2;   // closed
            if (!view.Issued.Contains(token)) return 1;
            if (!ReferenceEquals(view.Active, token)) return 3;   // not installed as shown
            return 0;
        }

        internal ReviewOutcome Review(DisplayToken token)
        {
            try
            {
                lock (Gate)
                {
                    switch (CheckTokenLocked(token, out _))
                    {
                        case 1: return ReviewOutcome.Foreign;
                        case 2: return ReviewOutcome.ViewClosed;
                        case 3: return ReviewOutcome.NotInstalled;
                    }

                    if (token.IssueId is long issueId)
                    {
                        IssueRecord? issue = AllIssuesLocked().FirstOrDefault(i => i.Id == issueId);
                        if (issue == null) return ReviewOutcome.StaleTarget;
                        if (token.IssueRevision <= issue.ReviewedRevision) return ReviewOutcome.NothingNew;
                        // Records that THIS revision's information was
                        // reviewed. Repairs nothing; an active fault stays
                        // active and reachable.
                        issue.ReviewedRevision = Math.Min(token.IssueRevision, issue.Revision);
                        TouchLocked(persist: true);
                        return ReviewOutcome.Reviewed;
                    }

                    FactRecord? record = token.Episode is EpisodeId id ? RecordLocked(id, token.Variant) : null;
                    if (record == null) return ReviewOutcome.StaleTarget;

                    var ids = new HashSet<long>(record.Materials.Select(m => m.Id));
                    if (!token.Represented.All(ids.Contains)) return ReviewOutcome.StaleTarget;

                    var required = new HashSet<long>(record.Required().Select(u => u.Id));
                    bool any = false;
                    foreach (long unit in token.Represented)
                    {
                        if (!required.Contains(unit)) continue;
                        if (record.ReviewedLocal.Add(unit)) any = true;
                    }
                    if (!any) return ReviewOutcome.NothingNew;
                    TouchLocked(persist: true);
                    return ReviewOutcome.Reviewed;
                }
            }
            finally
            {
                RaiseSignals();
            }
        }

        internal ResumeRequest? BeginResume(DisplayToken token, out ResumeOutcome outcome)
        {
            lock (Gate)
            {
                switch (CheckTokenLocked(token, out _))
                {
                    case 1: outcome = ResumeOutcome.Foreign; return null;
                    case 2: outcome = ResumeOutcome.ViewClosed; return null;
                    case 3: outcome = ResumeOutcome.NotInstalled; return null;
                }

                FactRecord? record = token.Episode is EpisodeId id ? RecordLocked(id, token.Variant) : null;
                if (record == null) { outcome = ResumeOutcome.StaleTarget; return null; }

                // Unavailable for restored history: its owner is gone, and a
                // current fact elsewhere is not silently substituted for it.
                if (!record.IsLive) { outcome = ResumeOutcome.Unavailable; return null; }

                HashSet<long> unpresented = record.Unpresented();
                var target = token.Represented.Where(unpresented.Contains).ToArray();
                (bool permitted, _) = FactPermission.Evaluate(record, new HashSet<long>(target), _latestQuiet);
                if (target.Length == 0 || permitted) { outcome = ResumeOutcome.NothingPaused; return null; }

                long? sequence = NextSequenceLocked();
                if (sequence == null) { outcome = ResumeOutcome.StaleTarget; return null; }
                outcome = ResumeOutcome.Resumed;
                return new ResumeRequest(token, sequence.Value);
            }
        }

        internal ResumeOutcome CommitResume(ResumeRequest request)
        {
            try
            {
                lock (Gate)
                {
                    DisplayToken token = request.Token;
                    switch (CheckTokenLocked(token, out _))
                    {
                        case 1: return ResumeOutcome.Foreign;
                        case 2: return ResumeOutcome.ViewClosed;
                        case 3: return ResumeOutcome.NotInstalled;
                    }
                    FactRecord? record = token.Episode is EpisodeId id ? RecordLocked(id, token.Variant) : null;
                    if (record == null) return ResumeOutcome.StaleTarget;
                    if (!record.IsLive) return ResumeOutcome.Unavailable;

                    // A later Ctrl wins. The operator's resume cannot bypass a
                    // quiet observed after he chose it.
                    if (request.ActionSequence <= _latestQuiet) return ResumeOutcome.OvertakenByQuiet;

                    HashSet<long> unpresented = record.Unpresented();
                    var grant = new AutomaticGrant
                    {
                        Id = Checked(ref _nextGrant),
                        SourceSequence = request.ActionSequence,
                        Origin = GrantOrigin.ExplicitResume,
                    };
                    // Only the information actually displayed. Newer material
                    // the operator never saw stays paused.
                    foreach (long unit in token.Represented) if (unpresented.Contains(unit)) grant.Covers.Add(unit);
                    if (grant.Covers.Count == 0) return ResumeOutcome.NothingPaused;
                    record.Grants.Add(grant);
                    TouchLocked(persist: true);
                    return ResumeOutcome.Resumed;
                }
            }
            finally
            {
                RaiseSignals();
            }
        }

        internal SelectedReadGrant? RequestRead(DisplayToken token, out ReviewOutcome outcome)
        {
            lock (Gate)
            {
                switch (CheckTokenLocked(token, out _))
                {
                    case 1: outcome = ReviewOutcome.Foreign; return null;
                    case 2: outcome = ReviewOutcome.ViewClosed; return null;
                    case 3: outcome = ReviewOutcome.NotInstalled; return null;
                }
                FactRecord? record = token.Episode is EpisodeId id ? RecordLocked(id, token.Variant) : null;
                if (record == null) { outcome = ReviewOutcome.StaleTarget; return null; }
                long? sequence = NextSequenceLocked();
                if (sequence == null) { outcome = ReviewOutcome.StaleTarget; return null; }
                outcome = ReviewOutcome.Reviewed;
                return new SelectedReadGrant(this, Checked(ref _nextReadGrant), record.Id, token.Variant, sequence.Value,
                                             token.Represented, token.MaterialFingerprint);
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  The projection the surface consumes
        // ────────────────────────────────────────────────────────────────

        internal FactListSnapshot Project(FactView view, string? stationFilter)
        {
            lock (Gate)
            {
                var items = new List<ItemSnapshot>();
                var issues = AllIssuesLocked().Select(i => i.Freeze(_projectionRevision)).ToArray();

                // Store issues are never hidden by a radio filter.
                foreach (StoreIssueSnapshot issue in issues)
                    if (view == FactView.History || issue.Outstanding)
                        items.Add(new ItemSnapshot(ItemSnapshot.IdFor(issue.Id), issue));

                int excluded = 0, pendingFacts = 0, reviewedOnly = 0, forgettableMissed = 0, continuedCoverage = 0;
                bool allPresented = true;
                foreach (var pair in _byId)
                {
                    for (int v = 0; v < pair.Value.Count; v++)
                    {
                        FactRecord record = pair.Value[v];
                        FactSnapshot fact = record.Freeze(_latestQuiet, _projectionRevision);
                        bool inScope = Matches(record, stationFilter);

                        if (!inScope)
                        {
                            if (fact.IsPending) excluded++;
                            continue;
                        }

                        if (fact.IsPending) pendingFacts++;
                        if (fact.DischargedByReviewOnly) reviewedOnly++;
                        if (record.IsForgettable)
                        {
                            if (fact.Unpresented.Count > 0) { forgettableMissed++; allPresented = false; }
                        }
                        else if (!fact.PresentationComplete)
                        {
                            allPresented = false;
                        }
                        else if (!record.PresentedByItself())
                        {
                            // Complete through the occurrence: an earlier
                            // episode was read out, this one never was.
                            continuedCoverage++;
                        }

                        bool show = view == FactView.Pending ? fact.IsPending : IsHistoryRow(record);
                        if (show) items.Add(new ItemSnapshot(ItemSnapshot.IdFor(record.Id, v), fact));
                    }
                }

                // Keep facts in the order they were retained, after the issues.
                var ordered = items.Where(i => i.Kind == ItemKind.Issue).ToList();
                var factItems = items.Where(i => i.Kind == ItemKind.Fact).ToDictionary(i => i.ItemId);
                foreach (FactRecord record in _order)
                {
                    int variant = _byId.TryGetValue(record.Id, out List<FactRecord>? list) ? list.IndexOf(record) : 0;
                    if (factItems.TryGetValue(ItemSnapshot.IdFor(record.Id, variant), out ItemSnapshot? item))
                        ordered.Add(item);
                }

                if (stationFilter == null)
                {
                    reviewedOnly += (int)Math.Min(int.MaxValue, _compactedReviewedOnly);
                    forgettableMissed += (int)Math.Min(int.MaxValue, _compactedForgettableUnpresented);
                }

                int outstandingIssues = issues.Count(i => i.Outstanding);
                bool loss = issues.Any(i => i.IsLoss && i.Outstanding);
                bool completeInventory = _load == HistoryLoadState.Loaded && !issues.Any(i => i.AffectsInventory);
                bool saved = _journalAttached && _persistedThrough >= _mutation
                             && !issues.Any(i => i.Kind == IssueKind.PersistenceFailure && i.State == IssueState.Active);
                bool presentationComplete = allPresented && reviewedOnly == 0 && forgettableMissed == 0
                                            && !issues.Any(i => i.IsLoss && i.Kind != IssueKind.DetailLoss)
                                            && _compactedReviewedOnly == 0 && _compactedForgettableUnpresented == 0;
                bool empty = pendingFacts == 0 && outstandingIssues == 0 && !loss && _load != HistoryLoadState.Loading;

                var predicates = new FactListPredicates(
                    loading: _load == HistoryLoadState.Loading,
                    journalAttached: _journalAttached,
                    filtered: stationFilter != null,
                    excludedOutstanding: excluded,
                    pendingFactsInScope: pendingFacts,
                    outstandingIssues: outstandingIssues,
                    emptyPendingInScope: empty,
                    completeInventory: completeInventory,
                    savedThroughCurrent: saved,
                    presentationComplete: presentationComplete,
                    presentedInEarlierEpisode: continuedCoverage,
                    reviewedNotDelivered: reviewedOnly,
                    forgettableUnpresented: forgettableMissed,
                    unaccountedLoss: loss);

                return new FactListSnapshot(_projectionRevision, view, stationFilter, ordered, predicates);
            }
        }

        internal FactSnapshot? FindVariant(EpisodeId id, int variant)
        {
            lock (Gate)
            {
                FactRecord? record = RecordLocked(id, variant);
                return record == null ? null : FreezeLocked(record);
            }
        }

        internal StoreIssueSnapshot? FindIssue(long id)
        {
            lock (Gate) return AllIssuesLocked().FirstOrDefault(i => i.Id == id)?.Freeze(_projectionRevision);
        }
    }
}
