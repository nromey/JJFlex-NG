#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.Facts
{
    /// <summary>
    /// The issuer: the one place owners, scopes, slots, transports, receipt
    /// adapters and presentation builders come from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owned by the composition root, and handed only to lifecycle
    /// owners.</b> The consumer-facing <see cref="FactStore"/> exposes immutable
    /// queries and a few narrow endpoints; it has no public issuer, no public
    /// restore and no revoke-by-number. A factory that took any caller's owner
    /// name and returned authority would simply move Track M's defect here, so
    /// nothing in this class accepts a name as a credential: every grant is an
    /// object this registry issued.
    /// </para>
    /// <para>
    /// <b>This is a boundary between application components, not a security
    /// boundary against hostile code in the process.</b> It proves a registered
    /// owner published a supported claim; the owner remains responsible for the
    /// observation and its domain evaluation.
    /// </para>
    /// </remarks>
    public sealed class FactAuthorityRegistry
    {
        private FactAuthorityRegistry(FactStore store)
        {
            Store = store;
            Quiet = new QuietEndpoint(store);
        }

        /// <summary>
        /// Create a store and its issuer together. The writer incarnation is
        /// random and minted once; it is the store's process identity and the
        /// namespace for every identity it allocates.
        /// </summary>
        public static FactAuthorityRegistry Create(IFactCatalog? catalog = null, IFactRenderer? renderer = null)
        {
            var store = new FactStore(Guid.NewGuid(), catalog ?? LexiconFactCatalog.Instance,
                                      renderer ?? LexiconFactRenderer.Instance);
            return new FactAuthorityRegistry(store);
        }

        /// <summary>The consumer-facing store.</summary>
        public FactStore Store { get; }

        /// <summary>Where the operator's quiet is observed. One per store; hand it to the observation hook.</summary>
        public QuietEndpoint Quiet { get; }

        /// <summary>
        /// Declare an owner and the contracts it may publish under. The
        /// returned object IS the owner's token.
        /// </summary>
        public FactOwner DeclareOwner(string name, params ConditionContract[] contracts)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            if (contracts == null || contracts.Length == 0)
                throw new ArgumentException("an owner must declare at least one contract", nameof(contracts));
            return Store.DeclareOwnerInternal(name, contracts);
        }

        /// <summary>Open the observation scope for one concrete radio attachment. Issued before any callback subscribes.</summary>
        public FactSession OpenSession(string? radioIdentity, string label = "") =>
            Store.OpenSessionInternal(radioIdentity, label);

        /// <summary>Open the observation scope for an application activity that is not a radio.</summary>
        public FactActivity OpenActivity(string label) => Store.OpenActivityInternal(label);

        /// <summary>
        /// Allocate the one publishing endpoint for one owner, one scope and
        /// one logical condition, under one contract.
        /// </summary>
        /// <remarks>
        /// Registration reserves current-record capacity BEFORE monitoring
        /// begins. A refusal for capacity is a reachable pressure item in the
        /// same transition, and never a claim that monitoring is active. It
        /// never blocks a protective command.
        /// </remarks>
        public RegistrationResult Register(FactOwner owner, FactScope scope, ConditionContract contract, ConditionKey condition) =>
            Store.RegisterInternal(owner, scope, contract, condition);

        /// <summary>Register a transport adapter and what it is able to establish.</summary>
        public TransportBinding RegisterTransport(string name, TransportCapability capability) =>
            Store.RegisterTransportInternal(name, capability);

        /// <summary>Register the adapter allowed to claim occurrence receipts.</summary>
        public ReceiptEndpoint RegisterReceiptAdapter(string name) => Store.RegisterReceiptInternal(name);

        /// <summary>Register a presentation builder: the only thing that may prepare plans.</summary>
        public FactPresentation RegisterPresentation(string name) => Store.RegisterPresentationInternal(name);
    }

    /// <summary>What happened to a registration request.</summary>
    public enum RegistrationOutcome
    {
        Granted = 0,

        /// <summary>No current slot could be reserved. A reachable pressure item was created in the same transition.</summary>
        Exhausted = 1,

        /// <summary>The owner, scope or contract was not issued by this registry, or the contract was not declared for this owner.</summary>
        AuthorityMismatch = 2,

        /// <summary>The scope has ended.</summary>
        ScopeEnded = 3,

        /// <summary>The same owner registered this condition before under a DIFFERENT contract.</summary>
        ContractConflict = 4,
    }

    /// <summary>The answer to a registration, with the publisher when one was issued.</summary>
    public sealed class RegistrationResult
    {
        internal RegistrationResult(RegistrationOutcome outcome, SlotPublisher? publisher, string explanation)
        {
            Outcome = outcome;
            Publisher = publisher;
            Explanation = explanation;
        }

        public RegistrationOutcome Outcome { get; }
        public SlotPublisher? Publisher { get; }

        /// <summary>Diagnostic words. The operator reaches a capacity refusal through its issue row, not through this.</summary>
        public string Explanation { get; }

        public bool Granted => Outcome == RegistrationOutcome.Granted;
        public override string ToString() => Outcome + ": " + Explanation;
    }

    /// <summary>
    /// The outcome of every publication. Implementation vocabulary, not
    /// operator wording.
    /// </summary>
    public enum PublicationOutcome
    {
        /// <summary>Accepted as current evidence.</summary>
        Accepted = 0,

        /// <summary>
        /// A genuinely earlier event, captured before its scope ended, admitted
        /// as history. It can create no current claim and no permission.
        /// </summary>
        AcceptedAsHistory = 1,

        /// <summary>The exact same event and payload again. Nothing changed.</summary>
        Duplicate = 2,

        /// <summary>Older than evidence already accepted. Recorded in bounded history; nothing regressed.</summary>
        StaleEvidence = 3,

        /// <summary>The scope ended before this event was captured.</summary>
        ScopeEnded = 4,

        /// <summary>
        /// Wrong owner, wrong slot, wrong contract or descriptor, a handle from
        /// another store, or an unpermitted claim. Nothing changed and an
        /// integrity item was aggregated.
        /// </summary>
        AuthorityMismatch = 5,

        /// <summary>The same event or transition identity with different content. Nothing changed; an integrity item records it.</summary>
        EvidenceConflict = 6,

        /// <summary>The observation does not fit the contract's evidence shape.</summary>
        EvidenceRejected = 7,

        /// <summary>The owner's expected predecessor does not match. The owner must reconcile; the store invents no causal chain.</summary>
        PredecessorMismatch = 8,

        /// <summary>No room to retain it. Recorded, reachable, never silent.</summary>
        CapacityRecorded = 9,

        /// <summary>An issuance counter reached its end. Nothing is wrapped into apparently fresh authority.</summary>
        SequenceExhausted = 10,
    }

    /// <summary>The answer to a publication.</summary>
    public sealed class PublicationResult
    {
        internal PublicationResult(PublicationOutcome outcome, EpisodeHandle? handle, FactSnapshot? fact, string explanation)
        {
            Outcome = outcome;
            Handle = handle;
            Fact = fact;
            Explanation = explanation;
        }

        public PublicationOutcome Outcome { get; }

        /// <summary>The episode handle, on an accepted or duplicate open.</summary>
        public EpisodeHandle? Handle { get; }

        /// <summary>The episode as it stands after this publication, when there is one to show.</summary>
        public FactSnapshot? Fact { get; }

        public string Explanation { get; }

        public bool Accepted => Outcome == PublicationOutcome.Accepted || Outcome == PublicationOutcome.AcceptedAsHistory;
        public override string ToString() => Outcome + ": " + Explanation;
    }

    /// <summary>What happened when a source event was captured.</summary>
    public enum CaptureOutcome
    {
        Captured = 0,
        ScopeEnded = 1,
        AuthorityMismatch = 2,
        EvidenceRejected = 3,
        SequenceExhausted = 4,
    }

    /// <summary>The answer to a capture, with the sealed event when one was issued.</summary>
    public sealed class CaptureResult
    {
        internal CaptureResult(CaptureOutcome outcome, CapturedFactEvent? ev, string explanation)
        {
            Outcome = outcome;
            Event = ev;
            Explanation = explanation;
        }

        public CaptureOutcome Outcome { get; }
        public CapturedFactEvent? Event { get; }
        public string Explanation { get; }
        public bool Captured => Outcome == CaptureOutcome.Captured;
    }

    /// <summary>How an owner classifies the first observation of a condition in a new session.</summary>
    /// <remarks>
    /// <para>
    /// <b>A session and an occurrence are different identities.</b> A reconnect
    /// gives the owner a new observation context; it does not create another
    /// occurrence of the condition, another obligation for identical
    /// information, another onset grant, or another receipt. The owner says
    /// what this observation is; the store checks the claim against what it
    /// retained, and reconnect alone supplies no permission of any kind.
    /// </para>
    /// <para>
    /// <b>Absence is not evidence.</b> <see cref="NoPriorRecord"/> describes
    /// the owner's knowledge of the RECORD: it knows of nothing earlier. The
    /// store checks that — no record for the condition on that station,
    /// nothing lost for it, a complete inventory of saved history — and even
    /// when all of it holds, the observation is retained with its onset
    /// unestablished and no automatic permission. A complete record proves
    /// that nothing came before it was written; it cannot prove that the
    /// condition began now rather than being already active when the session
    /// first observed it. When the inventory is partial, or a record may have
    /// been lost, the claim is treated as <see cref="ContinuityUnknown"/> or
    /// as lost continuity instead. A genuinely first occurrence is claimed
    /// through <see cref="NewOccurrence"/> with the owner's positive onset
    /// evidence — an owner-established inactive-to-active transition or an
    /// attributable onset event. The non-empty check on that evidence is
    /// structural; what it means is the registered owner's responsibility.
    /// The one first observation that needs no evidence is an application
    /// activity's, because there is no station it could already have been
    /// true on. Ruled 2026-09-24 (Astra's first-onset design). Whether a
    /// condition found already active at first connect should speak or sound
    /// is an operator-policy question filed with Noel, and would be a distinct
    /// permission if he grants it, never this claim.
    /// </para>
    /// </remarks>
    public enum ContinuityClaim
    {
        /// <summary>
        /// The owner knows of no earlier record. Record knowledge only: the
        /// observation is retained with its onset unestablished and no
        /// automatic permission, or as unknown or lost continuity when the
        /// store cannot even establish the record.
        /// </summary>
        NoPriorRecord = 0,

        /// <summary>
        /// The same condition, carried on. Needs the issued predecessor
        /// reference and, for each declared unit that reuses an earlier
        /// assertion, a checked link. Inherits the predecessor's evidence for
        /// exactly those assertions, its permission at its ORIGINAL causal
        /// position in the same process, and its receipt consequence. Never a
        /// fresh grant, never a fresh tone.
        /// </summary>
        Continuation = 1,

        /// <summary>A new onset, with positive domain evidence supplied. Not inferred from a disconnect.</summary>
        NewOccurrence = 2,

        /// <summary>The owner cannot say. Retains the uncertainty; grants nothing and re-arms nothing.</summary>
        ContinuityUnknown = 3,

        /// <summary>
        /// Worse than a supported baseline from before the reconnect, through
        /// an owner-issued worsening. Continues the occurrence; the grant
        /// covers the worse material only, and there is no second tone.
        /// </summary>
        WorseningOfPrior = 4,
    }

    /// <summary>Optional terms of opening an occurrence.</summary>
    public sealed class OpenOptions
    {
        /// <summary>The owner's own label for this occurrence. Kept for diagnosis; never the identity.</summary>
        public string? OccurrenceLabel { get; init; }

        /// <summary>The owner's domain baseline. Defaults to the event's observation.</summary>
        public FactObservation? Baseline { get; init; }

        public ContinuityClaim Continuity { get; init; } = ContinuityClaim.NoPriorRecord;

        /// <summary>Required for <see cref="ContinuityClaim.NewOccurrence"/>: the positive evidence of onset.</summary>
        public FactObservation? NewOnsetEvidence { get; init; }

        /// <summary>Required for <see cref="ContinuityClaim.WorseningOfPrior"/>.</summary>
        public WorseningTransition? WorseningOfPrior { get; init; }

        /// <summary>
        /// The predecessor this continues, as the store issued it through
        /// <see cref="SlotPublisher.Continuity"/>. Required for
        /// <see cref="ContinuityClaim.Continuation"/> and
        /// <see cref="ContinuityClaim.WorseningOfPrior"/>; a continuation
        /// without one has no supported predecessor and is retained as
        /// unknown continuity. A reference whose revision has moved on is
        /// refused, so the owner reconciles against the current view.
        /// </summary>
        public ContinuityReference? Predecessor { get; init; }

        /// <summary>
        /// Which declared units reuse which predecessor assertions. A unit
        /// with no link is new information: owed, and never automatically
        /// permitted by the reconnect. The occurrence itself is linked by the
        /// store when the message is the same.
        /// </summary>
        public IReadOnlyList<MaterialLink>? Carried { get; init; }

        /// <summary>Free detail text, bounded and truncated with a marker when too long.</summary>
        public string? Detail { get; init; }
    }

    /// <summary>
    /// An issued reference to the continuity a condition has on a station:
    /// the occurrence root, the newest episode that holds it, and the
    /// revision of that record. Compared, never trusted.
    /// </summary>
    public sealed class ContinuityReference
    {
        internal ContinuityReference(FactStore store, EpisodeId root, EpisodeId lastEpisode, long revision)
        {
            Store = store;
            Root = root;
            LastEpisode = lastEpisode;
            Revision = revision;
        }

        internal FactStore Store { get; }

        /// <summary>The first episode of the occurrence. The occurrence's identity.</summary>
        public EpisodeId Root { get; }

        /// <summary>The episode the continuity was derived from — the predecessor a continuation follows.</summary>
        public EpisodeId LastEpisode { get; }

        /// <summary>Advances whenever the continuity changes meaning; a stale reference is refused.</summary>
        public long Revision { get; }

        public override string ToString() => Root + " via " + LastEpisode + " r" + Revision;
    }

    /// <summary>
    /// One assertion the predecessor holds, with the evidence the store has
    /// for it — so a continuing owner can say which of its units are the
    /// same information, and see what that information already earned.
    /// </summary>
    public sealed class ContinuityAssertion
    {
        internal ContinuityAssertion(AssertionRef reference, string name, FactValue value, MaterialKind kind,
                                     bool covered, bool reviewed)
        {
            Reference = reference;
            Name = name;
            Value = value;
            Kind = kind;
            Covered = covered;
            Reviewed = reviewed;
        }

        /// <summary>The assertion's identity across the occurrence.</summary>
        public AssertionRef Reference { get; }
        public string Name { get; }
        public FactValue Value { get; }
        public MaterialKind Kind { get; }

        /// <summary>Attributable, undisputed presentation evidence exists for it.</summary>
        public bool Covered { get; }

        /// <summary>The operator explicitly reviewed it on a displayed snapshot.</summary>
        public bool Reviewed { get; }

        public override string ToString() => Reference + " " + Name + "=" + Value.Invariant
            + (Covered ? " covered" : "") + (Reviewed ? " reviewed" : "");
    }

    /// <summary>A domain transition an owner publishes against an open episode.</summary>
    public sealed class FactTransition
    {
        internal enum TransitionKind
        {
            Sample = 0,
            Correction = 1,
            ObservationUnknown = 2,
            OperationCancelled = 3,
            Resolved = 4,
        }

        private FactTransition(TransitionKind kind, IReadOnlyList<MaterialDeclaration> materials,
                               UnknownReason? unknown, string? note, FactObservation? baseline, string? detail)
        {
            Kind = kind;
            Materials = materials;
            Unknown = unknown;
            Note = note;
            Baseline = baseline;
            Detail = detail;
        }

        internal TransitionKind Kind { get; }
        public IReadOnlyList<MaterialDeclaration> Materials { get; }
        public UnknownReason? Unknown { get; }
        public string? Note { get; }
        public FactObservation? Baseline { get; }
        public string? Detail { get; }

        /// <summary>
        /// A fresh observation of the same condition. <b>Not material</b>:
        /// it updates current values without manufacturing an incident, undoing
        /// a silence or replenishing any allowance. It inherits the existing
        /// grant; it never gets one of its own.
        /// </summary>
        public static FactTransition Sample(FactObservation? baseline = null, string? note = null) =>
            new(TransitionKind.Sample, Array.Empty<MaterialDeclaration>(), null, note, baseline, null);

        /// <summary>
        /// The owner corrects information. Each unit supersedes the latest
        /// unit of the same name and is newly owed. Does not cross a quiet.
        /// </summary>
        public static FactTransition Correction(IEnumerable<MaterialDeclaration> materials, FactObservation? baseline = null,
                                                string? detail = null, string? note = null)
        {
            var list = (materials ?? throw new ArgumentNullException(nameof(materials))).ToArray();
            if (list.Length == 0) throw new ArgumentException("a correction must declare what changed", nameof(materials));
            return new(TransitionKind.Correction, list, null, note, baseline, detail);
        }

        /// <summary>Observation failed. The obligation is retained and current assertions are suspended.</summary>
        public static FactTransition ObservationUnknown(UnknownReason reason, string? note = null) =>
            new(TransitionKind.ObservationUnknown, Array.Empty<MaterialDeclaration>(), reason, note, null, null);

        /// <summary>The operation this fact described was cancelled.</summary>
        public static FactTransition OperationCancelled(string? note = null) =>
            new(TransitionKind.OperationCancelled, Array.Empty<MaterialDeclaration>(), null, note, null, null);

        internal static FactTransition ResolvedInternal(string? note) =>
            new(TransitionKind.Resolved, Array.Empty<MaterialDeclaration>(), null, note, null, null);

        internal string Fingerprint =>
            (int)Kind + "|" + string.Join(";", Materials.Select(m => m.Fingerprint)) + "|" + Unknown + "|"
            + (Baseline?.Fingerprint ?? "") + "|" + (Detail ?? "");
    }

    /// <summary>
    /// An owner-issued worsening: the one automatic crossing of a prior quiet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The speech layer never decides what "worse" means.</b> The owner
    /// that knows what two degrees or a fifth of a volt means evaluates it, and
    /// issues this with its result, its baseline and the new material. Reaching
    /// the store with "material = true" is no longer a substitute.
    /// </para>
    /// <para>
    /// Ruled by Noel, 2026-09-24 07:46 (#617): <i>"if pressing control silences
    /// and the alarm condition gets worse, then we definitely would want to
    /// speak that warning again."</i>
    /// </para>
    /// </remarks>
    public sealed class WorseningTransition
    {
        public WorseningTransition(
            string transitionId, string baselineFingerprint, IEnumerable<MaterialDeclaration> worseMaterial,
            string ownerResult, FactObservation? newBaseline = null, bool alsoCoversEarlier = false)
        {
            if (string.IsNullOrWhiteSpace(transitionId)) throw new ArgumentNullException(nameof(transitionId));
            if (string.IsNullOrWhiteSpace(ownerResult)) throw new ArgumentNullException(nameof(ownerResult));
            TransitionId = transitionId;
            BaselineFingerprint = baselineFingerprint ?? throw new ArgumentNullException(nameof(baselineFingerprint));
            WorseMaterial = (worseMaterial ?? throw new ArgumentNullException(nameof(worseMaterial))).ToArray();
            if (WorseMaterial.Count == 0)
                throw new ArgumentException("a worsening must declare the worse information", nameof(worseMaterial));
            OwnerResult = ownerResult;
            NewBaseline = newBaseline;
            AlsoCoversEarlier = alsoCoversEarlier;
        }

        /// <summary>The owner's identity for this transition. Reused with the same payload is a duplicate; changed is a conflict.</summary>
        public string TransitionId { get; }

        /// <summary>The fingerprint of the baseline this comparison was made against.</summary>
        public string BaselineFingerprint { get; }

        public IReadOnlyList<MaterialDeclaration> WorseMaterial { get; }

        /// <summary>The owner's typed result, recorded as given. The store never compares it.</summary>
        public string OwnerResult { get; }

        /// <summary>The baseline after this transition. Defaults to the triggering event's observation.</summary>
        public FactObservation? NewBaseline { get; }

        /// <summary>The owner says the new rendering covers earlier unpresented material too. The plan still has to carry it.</summary>
        public bool AlsoCoversEarlier { get; }

        internal string Fingerprint =>
            TransitionId + "|" + BaselineFingerprint + "|" + string.Join(";", WorseMaterial.Select(m => m.Fingerprint))
            + "|" + OwnerResult + "|" + (NewBaseline?.Fingerprint ?? "") + "|" + AlsoCoversEarlier;
    }

    /// <summary>
    /// The only publishing endpoint for one owner, one scope and one logical
    /// condition. Issued by registration; never constructed by a producer.
    /// </summary>
    public sealed class SlotPublisher
    {
        internal SlotPublisher(FactStore store, FactOwner owner, FactScope scope, ConditionContract contract,
                               ConditionKey condition, long slotOrdinal)
        {
            Store = store;
            Owner = owner;
            Scope = scope;
            Contract = contract;
            Condition = condition;
            SlotOrdinal = slotOrdinal;
        }

        internal FactStore Store { get; }
        internal FactOwner Owner { get; }
        internal FactScope Scope { get; }
        internal bool RevokedLocked;
        internal bool Released;

        public ConditionContract Contract { get; }
        public ConditionKey Condition { get; }
        public long SlotOrdinal { get; }
        public string OwnerName => Owner.Name;
        public long ScopeId => Scope.ScopeId;
        public string? RadioIdentity => Scope.RadioIdentity;

        /// <summary>True once the scope ended.</summary>
        public bool Revoked => Store.PublisherRevoked(this);

        /// <summary>
        /// Capture a source event at the earliest trusted boundary — BEFORE
        /// posting anything to an evaluator, dispatcher, cue timer or queue.
        /// </summary>
        public CaptureResult Capture(FactObservation observation, DateTime observedUtc, string? sourceEventId = null) =>
            Store.Capture(this, observation, observedUtc, sourceEventId);

        /// <summary>Open a new occurrence against a captured event.</summary>
        public PublicationResult Open(CapturedFactEvent ev, string claim, string messageKey,
                                      IEnumerable<MaterialDeclaration>? materials = null, OpenOptions? options = null) =>
            Store.Open(this, ev, claim, messageKey, materials, options ?? new OpenOptions());

        /// <summary>Publish a domain transition against an episode this publisher opened.</summary>
        public PublicationResult Update(EpisodeHandle episode, CapturedFactEvent ev, FactTransition transition, long expectedRevision) =>
            Store.Update(this, episode, ev, transition, expectedRevision);

        /// <summary>Issue a worsening transition. Only a contract that allows it may.</summary>
        public PublicationResult Worsen(EpisodeHandle episode, CapturedFactEvent ev, WorseningTransition transition) =>
            Store.Worsen(this, episode, ev, transition);

        /// <summary>
        /// Assert the contract's positive resolution. Only this owner, only a
        /// contract that allows it; a disconnect or a withdrawn plan never can.
        /// </summary>
        public PublicationResult Resolve(EpisodeHandle episode, CapturedFactEvent ev, long expectedRevision, string? note = null) =>
            Store.Update(this, episode, ev, FactTransition.ResolvedInternal(note), expectedRevision);

        /// <summary>What the store remembers about this condition from before, for the owner to compare against.</summary>
        public ContinuityView? Continuity => Store.ContinuityFor(this);

        public override string ToString() => Owner.Name + " / " + Condition + " in " + Scope;
    }

    /// <summary>The continuity an owner may consult on a reconnect.</summary>
    /// <remarks>
    /// A bounded evidence summary and a reference — not authority, not a
    /// runnable plan, not a permit. What it offers is exactly what a
    /// continuation may claim: the predecessor and its assertions.
    /// </remarks>
    public sealed class ContinuityView
    {
        internal ContinuityView(ContinuityReference? reference, bool paused, PauseCause cause, FactObservation baseline,
                                int definitionRevision, bool fromPreviousProcess, bool deliveryEvidenceSupported,
                                bool conflicted, IReadOnlyList<ContinuityAssertion> assertions, ReceiptSnapshot receipt)
        {
            Reference = reference;
            Paused = paused;
            Cause = cause;
            Baseline = baseline;
            BaselineFingerprint = baseline.Fingerprint;
            DefinitionRevision = definitionRevision;
            FromPreviousProcess = fromPreviousProcess;
            DeliveryEvidenceSupported = deliveryEvidenceSupported;
            Conflicted = conflicted;
            Assertions = assertions;
            Receipt = receipt;
        }

        /// <summary>
        /// The reference a continuation or a reconnect worsening must supply.
        /// Null when the record cannot support one: a conflicted continuity,
        /// or one saved by a format that kept no delivery evidence.
        /// </summary>
        public ContinuityReference? Reference { get; }

        public bool Paused { get; }
        public PauseCause Cause { get; }
        public FactObservation Baseline { get; }
        public string BaselineFingerprint { get; }
        public int DefinitionRevision { get; }
        public bool FromPreviousProcess { get; }

        /// <summary>
        /// False when the continuity was saved by a format that recorded no
        /// per-assertion evidence. Such a continuity has UNSUPPORTED delivery
        /// continuity — not empty coverage plus an available receipt — and a
        /// continuation against it is retained as unknown.
        /// </summary>
        public bool DeliveryEvidenceSupported { get; }

        /// <summary>Two saved sources disagreed about this continuity. Neither candidate is chosen.</summary>
        public bool Conflicted { get; }

        /// <summary>The predecessor's required assertions, with their evidence. Empty when unsupported or conflicted.</summary>
        public IReadOnlyList<ContinuityAssertion> Assertions { get; }

        /// <summary>The occurrence's receipt consequence: what was spent, and what became of it.</summary>
        public ReceiptSnapshot Receipt { get; }
    }

    /// <summary>
    /// Where an observed operator quiet enters the ordered stream.
    /// </summary>
    /// <remarks>
    /// <b>The hook stamps and returns.</b> Disk, UI, native stop and producer
    /// callbacks happen elsewhere, after the gate is released. It does not scan
    /// every fact before returning: permissions are derived by comparing their
    /// source positions with this one, so a late event is paused even if no
    /// fact existed when Ctrl was pressed. This changes DELIVERY PERMISSION and
    /// nothing else — nothing is acknowledged, cleared, snoozed or made less
    /// true.
    /// </remarks>
    public sealed class QuietEndpoint
    {
        private readonly FactStore _store;

        internal QuietEndpoint(FactStore store) => _store = store;

        /// <summary>Record an observed operator quiet. Returns its position in the ordered stream.</summary>
        public long Observe(string reason) => _store.ObserveQuiet(reason);
    }
}
