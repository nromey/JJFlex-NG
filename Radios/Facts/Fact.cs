#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Radios.Facts
{
    /// <summary>
    /// Why automatic presentation of a fact's owed information is not
    /// currently permitted. Each cause is distinct because each has a different
    /// release, and a surface that says "paused because you asked for quiet"
    /// must only say it when the operator did.
    /// </summary>
    public enum PauseCause
    {
        /// <summary>Not paused — or nothing is owed.</summary>
        None = 0,

        /// <summary>An operator quiet was observed after the permission was granted.</summary>
        OperatorQuiet = 1,

        /// <summary>An attempt was cancelled and nobody can say why.</summary>
        UnknownCancellation = 2,

        /// <summary>
        /// A continuation after the program was restarted, of a condition that
        /// had been paused before it closed. The earlier permission is history;
        /// the pause it carried is what the record still says.
        /// </summary>
        ContinuityInherited = 3,

        /// <summary>
        /// A reconnect's owner could not say whether this continues an earlier
        /// condition — or claimed a continuation the store could not support.
        /// The uncertainty is kept; nothing is granted and nothing re-armed.
        /// </summary>
        ContinuityUnknown = 4,

        /// <summary>The continuity record this would be compared with was lost to capacity.</summary>
        ContinuityLost = 5,

        /// <summary>
        /// There is no live owner to grant permission: restored history, or
        /// evidence admitted after its scope ended.
        /// </summary>
        NoLivePermission = 6,

        /// <summary>A schema-1 record said "paused" and never recorded why.</summary>
        LegacyUnknownCause = 7,

        /// <summary>
        /// A continuation after the program was restarted, of a condition that
        /// was NOT paused when it closed. Its permission did not survive the
        /// restart — a grant is live authority, and live authority is not
        /// restored — so nothing speaks it until a supported worsening, a new
        /// evidenced onset, or the operator's explicit resume.
        /// </summary>
        ContinuityAcrossRestart = 8,
    }

    /// <summary>Where a grant of automatic permission came from.</summary>
    public enum GrantOrigin
    {
        Occurrence = 0,
        Worsening = 1,
        ExplicitResume = 2,

        /// <summary>
        /// Carried into a successor episode from its predecessor's grant, in
        /// the same process, at the ORIGINAL causal position. A reconnect
        /// rebinds eligibility to the new live publisher; it does not renew
        /// it.
        /// </summary>
        Inherited = 3,
    }

    /// <summary>
    /// What a schema-1 record's aggregate delivery field said. Kept as the
    /// writer's claim, never as verified coverage and never as a reason to
    /// leave the pending list.
    /// </summary>
    public enum LegacyDeliveryClaim
    {
        /// <summary>The field was absent or unreadable.</summary>
        Absent = 0,

        /// <summary>The old format said it was delivered. It did not say which retained details were.</summary>
        ClaimedDelivered = 1,

        /// <summary>The old format said it was not delivered.</summary>
        ClaimedUndelivered = 2,
    }

    /// <summary>
    /// One grant of automatic permission: which event or action granted it,
    /// its position in the ordered stream, and exactly which information it
    /// covers.
    /// </summary>
    /// <remarks>
    /// <b>Effective only while its source position is after the latest quiet</b>
    /// — derived at the moment anyone asks, never materialised as a Boolean
    /// that could go stale. A late event cannot earn fresh permission because
    /// its grant inherits the event's captured position, and a successor
    /// episode's inherited grant keeps its predecessor's position for the same
    /// reason. An unknown-cause cancellation reported against the predecessor's
    /// attempt follows the lineage: the constraint is read through
    /// <see cref="InheritedFrom"/>, never copied once at reconnect.
    /// </remarks>
    internal sealed class AutomaticGrant
    {
        public long Id;
        public long SourceSequence;
        public GrantOrigin Origin;
        public readonly HashSet<long> Covers = new();
        public AttemptId? UnknownCancelledBy;
        public long? UnknownCancelledAtSequence;

        /// <summary>The predecessor's grant this one was carried from, when inherited.</summary>
        public AutomaticGrant? InheritedFrom;

        /// <summary>The unknown-cause cancellation constraint, read through the whole lineage.</summary>
        public AttemptId? EffectiveUnknownCancellation
        {
            get
            {
                AutomaticGrant? g = this;
                int guard = 0;
                while (g != null && guard++ < 64)
                {
                    if (g.UnknownCancelledBy != null) return g.UnknownCancelledBy;
                    g = g.InheritedFrom;
                }
                return null;
            }
        }
    }

    /// <summary>One bounded history line about an event the episode received.</summary>
    internal sealed class EventRecord
    {
        public long EventOrdinal;
        public long Sequence;
        public string? SourceEventId;
        public DateTime ObservedUtc;
        public string Effect = string.Empty;
    }

    /// <summary>
    /// The receipt allowance and evidence for one occurrence. Shared by every
    /// episode of the occurrence in the same process: a reconnect does not
    /// buy another tone.
    /// </summary>
    internal sealed class ReceiptRecord
    {
        public ReceiptPolicy Policy;
        public ReceiptState State = ReceiptState.NotRequested;
        public bool Consumed;
        public long ReceiptId;
        public ReceiptPermit? OutstandingPermit;
        public bool FromPreviousProcess;

        /// <summary>
        /// The occurrence's observation scope ended before any request was
        /// made, so the allowance closed without one. Not "requested", not
        /// "played": closed. A continuation never reissues it.
        /// </summary>
        public bool Closed;

        public ReceiptSnapshot Freeze() => new ReceiptSnapshot(Policy, State, Consumed, ReceiptId, FromPreviousProcess, Closed);
    }

    /// <summary>Where a restored record came from.</summary>
    internal sealed class HistoricalOrigin
    {
        public string Source = string.Empty;
        public long Generation;
        public Guid HeaderWriter;
        public bool Legacy;
        public bool ConflictVariant;
        public int VariantIndex;
        public string RecordFingerprint = string.Empty;
    }

    /// <summary>
    /// The bounded evidence an occurrence keeps for an assertion whose
    /// episode record has been compacted or was never loaded.
    /// </summary>
    internal sealed class AssertionEvidence
    {
        public string Name = string.Empty;
        public FactValue Value;
        public MaterialKind Kind;
        public bool Covered;
        public bool Reviewed;
    }

    /// <summary>
    /// One logical occurrence across every episode that observed it: the
    /// place evidence for a continuing assertion is resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A session episode and a logical occurrence are different
    /// identities.</b> The first episode's identity is the occurrence root;
    /// later episodes that continue it are members. An assertion is owed once
    /// across the occurrence: a completion for the predecessor's plan or a
    /// review of its displayed detail credits the assertion for every member
    /// that carries it, found through the units' <see cref="MaterialUnit.Root"/>
    /// rather than copied at the moment of reconnect. Evidence from a
    /// conflicted variant credits nothing.
    /// </para>
    /// <para>
    /// <b>Compaction cannot strand a reference.</b> When a member is compacted
    /// its per-assertion evidence folds into <see cref="Compacted"/>, and a
    /// continuity record saved to disk carries the same summary, so a
    /// successor still finds what its predecessor earned.
    /// </para>
    /// </remarks>
    internal sealed class OccurrenceLineage
    {
        public EpisodeId Root;
        public long Revision;
        public readonly List<FactRecord> Members = new();
        public readonly Dictionary<AssertionRef, AssertionEvidence> Compacted = new();

        /// <summary>
        /// The occurrence roots this one replaced for its key, oldest first
        /// and bounded — so two saved sources naming different occurrences for
        /// one condition can be ordered by established succession rather than
        /// by guess, and unordered candidates stay unknown.
        /// </summary>
        public readonly List<EpisodeId> SupersededRoots = new();

        public const int MaxSupersededRoots = 16;

        public void Supersede(EpisodeId root, IEnumerable<EpisodeId> itsChain)
        {
            foreach (EpisodeId r in itsChain) if (!SupersededRoots.Contains(r)) SupersededRoots.Add(r);
            if (!SupersededRoots.Contains(root)) SupersededRoots.Add(root);
            while (SupersededRoots.Count > MaxSupersededRoots) SupersededRoots.RemoveAt(0);
        }

        public bool CoveredElsewhere(FactRecord self, AssertionRef root)
        {
            foreach (FactRecord m in Members)
            {
                if (ReferenceEquals(m, self) || m.Origin?.ConflictVariant == true) continue;
                if (m.DirectlyCovers(root)) return true;
            }
            return Compacted.TryGetValue(root, out AssertionEvidence? e) && e.Covered;
        }

        public bool ReviewedElsewhere(FactRecord self, AssertionRef root)
        {
            foreach (FactRecord m in Members)
            {
                if (ReferenceEquals(m, self) || m.Origin?.ConflictVariant == true) continue;
                if (m.DirectlyReviewed(root)) return true;
            }
            return Compacted.TryGetValue(root, out AssertionEvidence? e) && e.Reviewed;
        }

        /// <summary>Fold a member's own evidence into the durable summary before it is dropped.</summary>
        public void Absorb(FactRecord member)
        {
            HashSet<long> covered = member.DirectCovered();
            HashSet<long> reviewed = member.DirectReviewed();
            foreach (MaterialUnit unit in member.Required())
                Absorb(unit.Root, unit.Name, unit.Value, unit.Kind, covered.Contains(unit.Id), reviewed.Contains(unit.Id));
        }

        public void Absorb(AssertionRef root, string name, FactValue value, MaterialKind kind, bool covered, bool reviewed)
        {
            if (!Compacted.TryGetValue(root, out AssertionEvidence? e))
                Compacted[root] = e = new AssertionEvidence { Name = name, Value = value, Kind = kind };
            e.Covered |= covered;
            e.Reviewed |= reviewed;
        }
    }

    /// <summary>
    /// One attempt, as the store holds it. Mutable only under the store's gate.
    /// </summary>
    internal sealed class AttemptRecord
    {
        public AttemptId Id;
        public FactRecord Fact = null!;
        public PresentationPlan? Plan;              // null for a previous process's attempt
        public long PlanId;
        public PlanRequestKind Kind;
        public bool HistoricalPlan;
        public string Rendering = string.Empty;
        public readonly Dictionary<long, string> PlanMaterial = new();
        public long? GrantId;
        public TransportBinding? Binding;
        public string? BindingName;
        public string? Ticket;
        public bool Consumed;
        public bool Authorized;
        public NotStartedReason? NotStarted;
        public bool FromPreviousProcess;
        public bool Interrupted;
        public readonly SortedList<long, TransportEvidence> Evidence = new();
        public bool CancellationRequested;
        public string? CancelOperation;

        public bool IsTerminal
        {
            get
            {
                if (NotStarted != null || Interrupted) return true;
                foreach (TransportEvidence e in Evidence.Values)
                {
                    if (e.Kind is EvidenceKind.Completed or EvidenceKind.Cancelled or EvidenceKind.BackendRefused
                        or EvidenceKind.RequestThrew or EvidenceKind.CompletionUnobservable)
                        return true;
                }
                return false;
            }
        }

        public bool Has(EvidenceKind kind)
        {
            foreach (TransportEvidence e in Evidence.Values) if (e.Kind == kind) return true;
            return false;
        }

        public bool Disputed =>
            Has(EvidenceKind.Completed) && (Has(EvidenceKind.BackendRefused) || Has(EvidenceKind.Cancelled));

        public AttemptDisposition Disposition
        {
            get
            {
                if (NotStarted != null) return AttemptDisposition.NotStarted;
                if (Disputed) return AttemptDisposition.Disputed;
                if (Has(EvidenceKind.Completed)) return AttemptDisposition.Completed;
                if (Has(EvidenceKind.Cancelled))
                {
                    TransportEvidence? last = null;
                    foreach (TransportEvidence e in Evidence.Values) if (e.Kind == EvidenceKind.Cancelled) last = e;
                    return last!.Cause == CancelCause.OurOperation
                        ? AttemptDisposition.CancelledByUs
                        : AttemptDisposition.CancelledUnknownCause;
                }
                if (Has(EvidenceKind.BackendRefused)) return AttemptDisposition.Refused;
                if (Has(EvidenceKind.RequestThrew)) return AttemptDisposition.RequestFailed;
                if (Has(EvidenceKind.Progress)) return AttemptDisposition.Partial;
                if (Interrupted) return AttemptDisposition.Interrupted;
                if (Has(EvidenceKind.CompletionUnobservable)) return AttemptDisposition.Unobservable;
                if (Has(EvidenceKind.RequestIssued) || Has(EvidenceKind.BackendAccepted)) return AttemptDisposition.RequestedOnly;
                return Authorized ? AttemptDisposition.InFlight : AttemptDisposition.Allocated;
            }
        }

        /// <summary>
        /// The intersection of what the plan carried and what the adapter
        /// positively established. Nothing when the evidence is disputed.
        /// </summary>
        public HashSet<long> EstablishedCoverage()
        {
            var covered = new HashSet<long>();
            if (Disputed) return covered;
            if (Has(EvidenceKind.Completed))
            {
                covered.UnionWith(PlanMaterial.Keys);
                return covered;
            }
            foreach (TransportEvidence e in Evidence.Values)
            {
                if (e.Kind != EvidenceKind.Progress) continue;
                foreach (var pair in PlanMaterial)
                    foreach (string segment in e.Segments)
                        if (string.Equals(segment, pair.Value, StringComparison.Ordinal)) covered.Add(pair.Key);
            }
            return covered;
        }

        public AttemptSnapshot Freeze() => new AttemptSnapshot(
            Id, PlanId, Kind, HistoricalPlan, Rendering,
            PlanMaterial.Keys.ToArray(), EstablishedCoverage().ToArray(),
            Disposition, NotStarted, CancellationRequested, CancelOperation,
            Evidence.Values.ToArray(), BindingName ?? Binding?.Name, FromPreviousProcess);
    }

    /// <summary>
    /// One retained episode as the store holds it. <b>Internal and mutable
    /// only under the store's gate</b>; everything outside sees a
    /// <see cref="FactSnapshot"/>.
    /// </summary>
    internal sealed class FactRecord
    {
        // ── identity: fixed at open, never rewritten ──
        public EpisodeId Id;
        public string OwnerName = string.Empty;
        public string ContractName = string.Empty;
        public int ContractRevision;
        public ConditionKey Condition = null!;
        public string Claim = string.Empty;
        public long ScopeId;
        public FactScopeKind ScopeKind;
        public string? RadioIdentity;
        public string? OccurrenceLabel;
        public DeliveryPriority Priority;

        // ── occurrence lineage: the identity across episodes ──
        public EpisodeId LineageRoot;
        public EpisodeId? PredecessorEpisode;
        public OccurrenceLineage? Lineage;                          // the live link; null only before the store attached it

        // ── live authority: null on anything restored ──
        public SlotPublisher? Publisher;
        public FactScope? Scope;

        // ── classification, pinned at admission ──
        public string MessageKey = string.Empty;
        public DeliveryClassification Classification;
        public DeliveryDescriptor? Delivery;
        public long CatalogGeneration;

        // ── observation ──
        public ValiditySnapshot Validity = null!;
        public DateTime ObservedUtc;
        public long LastEventSequence;
        public long Revision;
        public long ObservationRevision;
        public long MaterialRevision;
        public FactObservation CurrentValues = FactObservation.Empty;
        public FactObservation Baseline = FactObservation.Empty;
        public readonly List<EventRecord> Events = new();
        public readonly Dictionary<long, string> AppliedEvents = new();
        public readonly Dictionary<string, string> WorseningTransitions = new(StringComparer.Ordinal);
        public long HistoryDropped;

        // ── material ──
        public readonly List<MaterialUnit> Materials = new();
        public long NextMaterialId = 1;
        public readonly HashSet<long> CoveredLedger = new();      // durable coverage of compacted attempts
        public readonly HashSet<long> ReviewedLocal = new();
        public readonly HashSet<long> ReviewedImported = new();
        public LegacyDeliveryClaim LegacyClaim;                    // schema-1 aggregate claim, never upgraded

        // ── permission ──
        public readonly List<AutomaticGrant> Grants = new();
        public PauseCause RestoredPause;

        /// <summary>
        /// Why a live episode with no grant is not automatically permitted:
        /// the continuity outcome of its reconnect. None for an episode whose
        /// permission is decided by its grants.
        /// </summary>
        public PauseCause ContinuityPause;

        // ── attempts ──
        public readonly List<AttemptRecord> Attempts = new();
        public readonly List<AttemptRecord> RetiredAttempts = new();   // tombstones still able to attribute late evidence
        public long CompactedAttempts;
        public bool CompactedAttemptsLowerBound;

        // ── receipt: shared across the occurrence's episodes ──
        public ReceiptRecord Receipt = new();

        // ── detail ──
        public string Detail = string.Empty;
        public bool DetailTruncated;

        // ── history ──
        public bool Restored;
        public HistoricalOrigin? Origin;

        public bool IsForgettable => Delivery != null && Delivery.ShelfLife == ShelfLife.Forgettable;

        /// <summary>Live: an issued owner in a scope that has not ended, and not restored.</summary>
        public bool IsLive => !Restored && Publisher != null && Scope != null && !Scope.EndedLocked && !Publisher.RevokedLocked;

        public IEnumerable<MaterialUnit> Required()
        {
            var superseded = new HashSet<long>();
            foreach (MaterialUnit unit in Materials) if (unit.Supersedes is long s) superseded.Add(s);
            foreach (MaterialUnit unit in Materials) if (!superseded.Contains(unit.Id)) yield return unit;
        }

        /// <summary>This record's own presentation evidence: its attempts, tombstones and durable ledger.</summary>
        public HashSet<long> DirectCovered()
        {
            var covered = new HashSet<long>(CoveredLedger);
            foreach (AttemptRecord attempt in Attempts) covered.UnionWith(attempt.EstablishedCoverage());
            foreach (AttemptRecord attempt in RetiredAttempts) covered.UnionWith(attempt.EstablishedCoverage());
            return covered;
        }

        public HashSet<long> DirectReviewed()
        {
            var reviewed = new HashSet<long>(ReviewedLocal);
            reviewed.UnionWith(ReviewedImported);
            return reviewed;
        }

        public bool DirectlyCovers(AssertionRef root)
        {
            HashSet<long> covered = DirectCovered();
            foreach (MaterialUnit unit in Materials) if (unit.Root == root && covered.Contains(unit.Id)) return true;
            return false;
        }

        public bool DirectlyReviewed(AssertionRef root)
        {
            HashSet<long> reviewed = DirectReviewed();
            foreach (MaterialUnit unit in Materials) if (unit.Root == root && reviewed.Contains(unit.Id)) return true;
            return false;
        }

        /// <summary>
        /// Units with attributable, undisputed presentation evidence — this
        /// record's own, or another episode's for the same assertion, found
        /// through the occurrence.
        /// </summary>
        public HashSet<long> Covered()
        {
            HashSet<long> covered = DirectCovered();
            if (Lineage != null)
                foreach (MaterialUnit unit in Materials)
                    if (!covered.Contains(unit.Id) && Lineage.CoveredElsewhere(this, unit.Root)) covered.Add(unit.Id);
            return covered;
        }

        public HashSet<long> Reviewed()
        {
            HashSet<long> reviewed = DirectReviewed();
            if (Lineage != null)
                foreach (MaterialUnit unit in Materials)
                    if (!reviewed.Contains(unit.Id) && Lineage.ReviewedElsewhere(this, unit.Root)) reviewed.Add(unit.Id);
            return reviewed;
        }

        /// <summary>Required information with neither confirmed presentation nor explicit review.</summary>
        public HashSet<long> Unpresented()
        {
            HashSet<long> covered = Covered();
            HashSet<long> reviewed = Reviewed();
            var owed = new HashSet<long>();
            foreach (MaterialUnit unit in Required())
                if (!covered.Contains(unit.Id) && !reviewed.Contains(unit.Id)) owed.Add(unit.Id);
            return owed;
        }

        /// <summary>
        /// The unpresented units a CURRENT rendering could carry: for each
        /// clause name, only the newest required unit of that name, plus the
        /// occurrence itself.
        /// </summary>
        /// <remarks>
        /// A rendering fills a placeholder with one value. When a worsening
        /// has added a newer unit of the same clause, the older unit's exact
        /// value is not in the words and never will be, so it stays owed for
        /// the list and for review, but it is not something automatic speech
        /// can discharge, and it must not keep automatic speech going forever.
        /// </remarks>
        public HashSet<long> PresentableUnpresented(HashSet<long> unpresented)
        {
            var latest = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (MaterialUnit unit in Required())
                if (!latest.TryGetValue(unit.Name, out long have) || unit.Id > have) latest[unit.Name] = unit.Id;
            var presentable = new HashSet<long>();
            foreach (long id in latest.Values) if (unpresented.Contains(id)) presentable.Add(id);
            return presentable;
        }

        /// <summary>
        /// True when this fact carries a retained delivery debt. Forgettable
        /// information never does. A schema-1 record's aggregate delivery
        /// claim is NOT sufficient coverage: it is owed until its retained
        /// detail is explicitly reviewed, whatever the old flag said.
        /// </summary>
        public bool HasRetainedDebt()
        {
            if (IsForgettable) return false;
            return Unpresented().Count > 0;
        }

        public MaterialUnit? LatestNamed(string name)
        {
            for (int i = Materials.Count - 1; i >= 0; i--)
                if (string.Equals(Materials[i].Name, name, StringComparison.Ordinal)) return Materials[i];
            return null;
        }

        public MaterialUnit AddMaterial(string name, FactValue value, MaterialKind kind, long? supersedes, long? relatesTo,
                                        AssertionRef? origin = null, AssertionRef? root = null)
        {
            long id = NextMaterialId++;
            var unit = new MaterialUnit(id, name, value, kind, supersedes, relatesTo, Revision,
                                        origin, root ?? new AssertionRef(Id, id));
            Materials.Add(unit);
            return unit;
        }

        public void NoteEvent(CapturedFactEvent ev, string effect)
        {
            Events.Add(new EventRecord
            {
                EventOrdinal = ev.EventOrdinal, Sequence = ev.Sequence, SourceEventId = ev.SourceEventId,
                ObservedUtc = ev.ObservedUtc, Effect = effect,
            });
            while (Events.Count > FactStoreCapacity.MaxEventHistoryPerFact)
            {
                Events.RemoveAt(0);
                if (HistoryDropped < long.MaxValue) HistoryDropped++;
            }
        }

        public void ApplyDetail(string? detail)
        {
            string text = detail ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(text) <= FactStoreCapacity.MaxDetailBytes)
            {
                Detail = text;
                DetailTruncated = false;
                return;
            }

            // Truncate, and SAY SO. A silently shortened detail reads as the
            // whole of what was known.
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int cut = FactStoreCapacity.MaxDetailBytes;
            while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
            Detail = Encoding.UTF8.GetString(bytes, 0, cut);
            DetailTruncated = true;
        }

        /// <summary>
        /// A fingerprint of what this record says, for binding a displayed
        /// snapshot to the content it showed. Detects mismatch; proves nothing
        /// about meaning.
        /// </summary>
        public string ContentFingerprint()
        {
            var sb = new StringBuilder();
            sb.Append(Id).Append('|').Append(MessageKey).Append('|').Append((int)Classification).Append('|')
              .Append(Validity.State).Append(Validity.Ended).Append(Validity.Unknown).Append('|')
              .Append(Detail).Append('|').Append(Restored).Append('|').Append(CurrentValues.Fingerprint);
            foreach (MaterialUnit unit in Materials)
                sb.Append('|').Append(unit.Id).Append(':').Append(unit.Name).Append('=').Append(unit.Value.Invariant)
                  .Append(':').Append(unit.Supersedes);
            return FactHash.Of(sb.ToString());
        }

        /// <summary>
        /// A fingerprint of the MATERIAL a rendering is made from — the
        /// information, its values and its detail — without the validity
        /// state. A selected read is bound to this: the same material may be
        /// read honestly as history after its premise ended, but material
        /// that changed is something the operator has not seen.
        /// </summary>
        public string MaterialFingerprint()
        {
            var sb = new StringBuilder();
            sb.Append(Id).Append('|').Append(MessageKey).Append('|').Append((int)Classification).Append('|')
              .Append(Detail).Append('|').Append(CurrentValues.Fingerprint);
            foreach (MaterialUnit unit in Materials)
                sb.Append('|').Append(unit.Id).Append(':').Append(unit.Name).Append('=').Append(unit.Value.Invariant)
                  .Append(':').Append(unit.Supersedes);
            return FactHash.Of(sb.ToString());
        }

        public FactSnapshot Freeze(long latestQuiet, long projectionRevision)
        {
            HashSet<long> covered = Covered();
            HashSet<long> reviewed = Reviewed();
            HashSet<long> unpresented = Unpresented();
            var required = Required().Select(u => u.Id).ToArray();

            HashSet<long> presentable = PresentableUnpresented(unpresented);
            (bool permitted, PauseCause pause) = FactPermission.Evaluate(this, presentable, latestQuiet);

            var grants = new GrantSnapshot[Grants.Count];
            for (int i = 0; i < Grants.Count; i++)
            {
                AutomaticGrant g = Grants[i];
                grants[i] = new GrantSnapshot(g.Id, g.Origin, g.SourceSequence, g.Covers.ToArray(),
                                              FactPermission.Blocker(g, latestQuiet), g.InheritedFrom?.Id);
            }

            return new FactSnapshot(
                id: Id,
                ownerName: OwnerName,
                contractName: ContractName,
                contractRevision: ContractRevision,
                condition: Condition,
                claim: Claim,
                scopeId: ScopeId,
                scopeKind: ScopeKind,
                radioIdentity: RadioIdentity,
                occurrenceLabel: OccurrenceLabel,
                priority: Priority,
                lineageRoot: LineageRoot,
                predecessorEpisode: PredecessorEpisode,
                messageKey: MessageKey,
                classification: Classification,
                delivery: Delivery,
                catalogGeneration: CatalogGeneration,
                validity: Validity,
                observedUtc: ObservedUtc,
                revision: Revision,
                observationRevision: ObservationRevision,
                materialRevision: MaterialRevision,
                currentValues: CurrentValues,
                baseline: Baseline,
                materials: Materials.ToArray(),
                required: required,
                covered: covered.ToArray(),
                reviewed: reviewed.ToArray(),
                unpresented: unpresented.ToArray(),
                presentable: presentable.ToArray(),
                hasRetainedDebt: HasRetainedDebt(),
                pause: pause,
                permitted: permitted,
                grants: grants,
                attempts: Attempts.Select(a => a.Freeze()).ToArray(),
                compactedAttempts: CompactedAttempts,
                compactedAttemptsLowerBound: CompactedAttemptsLowerBound,
                receipt: Receipt.Freeze(),
                detail: Detail,
                detailTruncated: DetailTruncated,
                isLive: IsLive,
                restored: Restored,
                restoredFrom: Origin?.Source,
                legacy: Origin?.Legacy ?? false,
                legacyClaim: LegacyClaim,
                conflictVariant: Origin?.ConflictVariant ?? false,
                variantIndex: Origin?.VariantIndex ?? 0,
                contentFingerprint: ContentFingerprint(),
                materialFingerprint: MaterialFingerprint(),
                projectionRevision: projectionRevision);
        }
    }

    /// <summary>
    /// The permission rule in one place, so the snapshot, the eligibility
    /// question and the final start gate all ask it the same way.
    /// </summary>
    internal static class FactPermission
    {
        /// <summary>
        /// Is automatic presentation of this record's owed information
        /// permitted right now, and if not, why not?
        /// </summary>
        public static (bool Permitted, PauseCause Pause) Evaluate(FactRecord record, HashSet<long> unpresented, long latestQuiet)
        {
            if (unpresented.Count == 0) return (false, PauseCause.None);
            if (!record.IsLive)
                return (false, record.RestoredPause != PauseCause.None ? record.RestoredPause : PauseCause.NoLivePermission);

            AutomaticGrant? blocking = null;
            PauseCause cause = record.ContinuityPause != PauseCause.None ? record.ContinuityPause : PauseCause.NoLivePermission;
            for (int i = record.Grants.Count - 1; i >= 0; i--)
            {
                AutomaticGrant grant = record.Grants[i];
                if (!grant.Covers.Overlaps(unpresented)) continue;
                PauseCause why = Blocker(grant, latestQuiet);
                if (why == PauseCause.None) return (true, PauseCause.None);
                if (blocking == null) { blocking = grant; cause = why; }
            }
            return (false, cause);
        }

        /// <summary>What stops this one grant, or None when it is effective.</summary>
        public static PauseCause Blocker(AutomaticGrant grant, long latestQuiet)
        {
            if (grant.EffectiveUnknownCancellation != null) return PauseCause.UnknownCancellation;
            if (grant.SourceSequence <= latestQuiet) return PauseCause.OperatorQuiet;
            return PauseCause.None;
        }

        public static bool Effective(AutomaticGrant grant, long latestQuiet) =>
            Blocker(grant, latestQuiet) == PauseCause.None;
    }

    /// <summary>An immutable view of one grant of automatic permission.</summary>
    public sealed class GrantSnapshot
    {
        internal GrantSnapshot(long id, GrantOrigin origin, long sourceSequence, IReadOnlyCollection<long> covers,
                               PauseCause blocker, long? inheritedFromId)
        {
            Id = id;
            Origin = origin;
            SourceSequence = sourceSequence;
            Covers = covers;
            Blocker = blocker;
            InheritedFromId = inheritedFromId;
        }

        public long Id { get; }
        public GrantOrigin Origin { get; }

        /// <summary>The causal position of the event or action that granted it. Inherited grants keep their predecessor's.</summary>
        public long SourceSequence { get; }

        /// <summary>Exactly which material units it permits.</summary>
        public IReadOnlyCollection<long> Covers { get; }

        /// <summary>What stops it right now, or None when it is effective.</summary>
        public PauseCause Blocker { get; }

        /// <summary>The predecessor grant this one was carried from, when inherited.</summary>
        public long? InheritedFromId { get; }

        public bool Effective => Blocker == PauseCause.None;

        public override string ToString() => Origin + " grant " + Id + " at " + SourceSequence + (Effective ? "" : " (" + Blocker + ")");
    }

    /// <summary>
    /// An immutable view of one episode. <b>This is what everything outside
    /// the store sees</b> — the dialog, the journal, the presenter and every
    /// test. It cannot be used to change anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fact is not the sentence.</b> It survives the wording, the
    /// selected radio, the reader binding and every delivery attempt; the
    /// sentence is rendered from its descriptor and material at the moment a
    /// plan is prepared.
    /// </para>
    /// </remarks>
    public sealed class FactSnapshot
    {
        internal FactSnapshot(
            EpisodeId id, string ownerName, string contractName, int contractRevision, ConditionKey condition,
            string claim, long scopeId, FactScopeKind scopeKind, string? radioIdentity, string? occurrenceLabel,
            DeliveryPriority priority, EpisodeId lineageRoot, EpisodeId? predecessorEpisode, string messageKey,
            DeliveryClassification classification, DeliveryDescriptor? delivery, long catalogGeneration,
            ValiditySnapshot validity, DateTime observedUtc, long revision, long observationRevision,
            long materialRevision, FactObservation currentValues, FactObservation baseline,
            IReadOnlyList<MaterialUnit> materials, IReadOnlyCollection<long> required,
            IReadOnlyCollection<long> covered, IReadOnlyCollection<long> reviewed, IReadOnlyCollection<long> unpresented,
            IReadOnlyCollection<long> presentable, bool hasRetainedDebt, PauseCause pause, bool permitted,
            IReadOnlyList<GrantSnapshot> grants, IReadOnlyList<AttemptSnapshot> attempts,
            long compactedAttempts, bool compactedAttemptsLowerBound, ReceiptSnapshot receipt, string detail,
            bool detailTruncated, bool isLive, bool restored, string? restoredFrom, bool legacy,
            LegacyDeliveryClaim legacyClaim, bool conflictVariant, int variantIndex, string contentFingerprint,
            string materialFingerprint, long projectionRevision)
        {
            Id = id;
            OwnerName = ownerName;
            ContractName = contractName;
            ContractRevision = contractRevision;
            Condition = condition;
            Claim = claim;
            ScopeId = scopeId;
            ScopeKind = scopeKind;
            RadioIdentity = radioIdentity;
            OccurrenceLabel = occurrenceLabel;
            Priority = priority;
            LineageRoot = lineageRoot;
            PredecessorEpisode = predecessorEpisode;
            MessageKey = messageKey;
            Classification = classification;
            Delivery = delivery;
            CatalogGeneration = catalogGeneration;
            Validity = validity;
            ObservedUtc = observedUtc;
            Revision = revision;
            ObservationRevision = observationRevision;
            MaterialRevision = materialRevision;
            CurrentValues = currentValues;
            Baseline = baseline;
            Materials = materials;
            Required = required;
            Covered = covered;
            Reviewed = reviewed;
            Unpresented = unpresented;
            Presentable = presentable;
            HasUndeliveredDetail = hasRetainedDebt;
            Pause = pause;
            PermittedNow = permitted;
            Grants = grants;
            Attempts = attempts;
            CompactedAttempts = compactedAttempts;
            CompactedAttemptsLowerBound = compactedAttemptsLowerBound;
            Receipt = receipt;
            Detail = detail;
            DetailTruncated = detailTruncated;
            IsLive = isLive;
            RestoredFromDisk = restored;
            RestoredFrom = restoredFrom;
            Legacy = legacy;
            LegacyClaim = legacyClaim;
            ConflictVariant = conflictVariant;
            VariantIndex = variantIndex;
            ContentFingerprint = contentFingerprint;
            MaterialFingerprint = materialFingerprint;
            ProjectionRevision = projectionRevision;
        }

        public EpisodeId Id { get; }
        public string OwnerName { get; }
        public string ContractName { get; }
        public int ContractRevision { get; }
        public ConditionKey Condition { get; }

        /// <summary>The claim this owner made, from the claims its contract allows.</summary>
        public string Claim { get; }

        public long ScopeId { get; }
        public FactScopeKind ScopeKind { get; }
        public string? RadioIdentity { get; }
        public string? OccurrenceLabel { get; }

        /// <summary>From the contract. A message key cannot raise it.</summary>
        public DeliveryPriority Priority { get; }

        /// <summary>
        /// The occurrence this episode belongs to: the first episode that
        /// observed it. Equal to <see cref="Id"/> for a first episode.
        /// </summary>
        public EpisodeId LineageRoot { get; }

        /// <summary>The episode this one continues across a reconnect, or null.</summary>
        public EpisodeId? PredecessorEpisode { get; }

        /// <summary>The lexicon key: the identity of the wording, never the wording.</summary>
        public string MessageKey { get; }

        /// <summary>Pinned at admission. A later catalogue cannot reinterpret stored evidence.</summary>
        public DeliveryClassification Classification { get; }
        public DeliveryDescriptor? Delivery { get; }
        public long CatalogGeneration { get; }

        public ValiditySnapshot Validity { get; }
        public DateTime ObservedUtc { get; }

        /// <summary>Strictly increasing on every accepted update, including a sample that changes nothing owed.</summary>
        public long Revision { get; }

        /// <summary>Advances on every new observation, so a plan can tell its exact values went stale.</summary>
        public long ObservationRevision { get; }

        /// <summary>Advances only when owed information changed.</summary>
        public long MaterialRevision { get; }

        public FactObservation CurrentValues { get; }

        /// <summary>The owner's latest domain baseline — what a later worsening is compared against, by the owner.</summary>
        public FactObservation Baseline { get; }

        /// <summary>Every unit of information, including superseded ones.</summary>
        public IReadOnlyList<MaterialUnit> Materials { get; }

        /// <summary>Units nothing has superseded.</summary>
        public IReadOnlyCollection<long> Required { get; }

        /// <summary>Units with attributable, undisputed presentation evidence — this episode's or a predecessor's, for the same assertion.</summary>
        public IReadOnlyCollection<long> Covered { get; }

        /// <summary>Units the operator explicitly reviewed on a displayed snapshot, in this episode or for the same assertion elsewhere.</summary>
        public IReadOnlyCollection<long> Reviewed { get; }

        /// <summary>Required units with neither presentation nor review.</summary>
        public IReadOnlyCollection<long> Unpresented { get; }

        /// <summary>
        /// The unpresented units a current rendering could carry: the newest
        /// unit of each clause, and the occurrence. Automatic permission is
        /// judged over these.
        /// </summary>
        public IReadOnlyCollection<long> Presentable { get; }

        /// <summary>
        /// A retained delivery debt: something owed that nobody presented or
        /// reviewed. <b>Always false for forgettable information</b>, which
        /// creates no continuing debt — though it can still be unpresented.
        /// </summary>
        public bool HasUndeliveredDetail { get; }

        /// <summary>Why automatic presentation is not permitted now, or None.</summary>
        public PauseCause Pause { get; }

        /// <summary>An effective grant covers some unpresented information right now.</summary>
        public bool PermittedNow { get; }

        /// <summary>
        /// True when something is unpresented and automatic presentation of it
        /// is not permitted. Includes history with no live owner.
        /// </summary>
        public bool AutomaticPaused => Presentable.Count > 0 && !PermittedNow;

        /// <summary>Every grant of automatic permission this episode holds, in the order they were made.</summary>
        public IReadOnlyList<GrantSnapshot> Grants { get; }

        public IReadOnlyList<AttemptSnapshot> Attempts { get; }

        /// <summary>Attempts coalesced into the durable coverage ledger. Never decreases.</summary>
        public long CompactedAttempts { get; }

        /// <summary>The compacted count saturated or came from a lossy source, so it is a lower bound.</summary>
        public bool CompactedAttemptsLowerBound { get; }

        public ReceiptSnapshot Receipt { get; }

        public string Detail { get; }
        public bool DetailTruncated { get; }

        /// <summary>An issued owner in a scope that has not ended. Restored history never is.</summary>
        public bool IsLive { get; }

        public bool RestoredFromDisk { get; }
        public string? RestoredFrom { get; }

        /// <summary>From a schema-1 file: legacy evidence, never proof the current contract existed.</summary>
        public bool Legacy { get; }

        /// <summary>What the schema-1 aggregate field said. A claim, kept; never verified coverage.</summary>
        public LegacyDeliveryClaim LegacyClaim { get; }

        /// <summary>
        /// A schema-1 record: whatever its aggregate field claimed, nothing
        /// establishes which retained details were presented.
        /// </summary>
        public bool LegacyUnverifiedCoverage => Legacy;

        /// <summary>One of several incompatible variants of the same identity, preserved side by side.</summary>
        public bool ConflictVariant { get; }
        public int VariantIndex { get; }

        public string ContentFingerprint { get; }

        /// <summary>The material this record's rendering is made from, without its validity state.</summary>
        public string MaterialFingerprint { get; }

        public long ProjectionRevision { get; }

        /// <summary>On the operator's default list: a retained debt.</summary>
        public bool IsPending => HasUndeliveredDetail;

        /// <summary>Its premise ended, or it came from a previous process.</summary>
        public bool IsHistorical =>
            RestoredFromDisk || Validity.State == ValidityState.Ended || Validity.State == ValidityState.Superseded;

        /// <summary>Every required unit has attributable, undisputed presentation evidence.</summary>
        public bool PresentationComplete
        {
            get
            {
                if (LegacyUnverifiedCoverage) return false;
                var covered = new HashSet<long>(Covered);
                foreach (long id in Required) if (!covered.Contains(id)) return false;
                return true;
            }
        }

        /// <summary>At least one required unit was discharged by review rather than presentation.</summary>
        public bool DischargedByReviewOnly
        {
            get
            {
                var covered = new HashSet<long>(Covered);
                var reviewed = new HashSet<long>(Reviewed);
                foreach (long id in Required) if (!covered.Contains(id) && reviewed.Contains(id)) return true;
                return false;
            }
        }

        public MaterialUnit? Unit(long id)
        {
            foreach (MaterialUnit unit in Materials) if (unit.Id == id) return unit;
            return null;
        }

        public override string ToString() =>
            Id + " [" + MessageKey + ", " + Validity + (IsPending ? ", pending" : string.Empty) + "]";
    }
}
