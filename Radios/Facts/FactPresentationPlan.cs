#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.Facts
{
    // ────────────────────────────────────────────────────────────────────
    //  The catalogue and the renderer
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Where a store looks up a message's descriptor and words.</summary>
    public interface IFactCatalog
    {
        /// <summary>The typed lookup: key, entry and the catalogue generation it came from.</summary>
        LexiconMessage Lookup(string key);

        /// <summary>The generation in force now. A wording reload advances it and invalidates unstarted plans.</summary>
        long Generation { get; }
    }

    /// <summary>The application's catalogue: the shipped lexicon and its overlays.</summary>
    public sealed class LexiconFactCatalog : IFactCatalog
    {
        public static LexiconFactCatalog Instance { get; } = new LexiconFactCatalog();

        private LexiconFactCatalog() { }

        public LexiconMessage Lookup(string key) => Lexicon.Message(key);
        public long Generation => Lexicon.CatalogGeneration;
    }

    /// <summary>One rendering of a fact, and exactly which clauses its words carry.</summary>
    public sealed class FactRendering
    {
        public FactRendering(string text, IReadOnlyList<(string Name, object? Value)> arguments,
                             IReadOnlyCollection<string> conveyedNames, string renderingIdentity, long catalogGeneration)
        {
            Text = text;
            Arguments = arguments;
            ConveyedNames = conveyedNames;
            RenderingIdentity = renderingIdentity;
            CatalogGeneration = catalogGeneration;
        }

        public string Text { get; }
        public IReadOnlyList<(string Name, object? Value)> Arguments { get; }

        /// <summary>The material clause names this rendering conveys. The renderer declares it; a sender cannot.</summary>
        public IReadOnlyCollection<string> ConveyedNames { get; }

        public string RenderingIdentity { get; }
        public long CatalogGeneration { get; }
    }

    /// <summary>
    /// A registered renderer: turns an immutable fact snapshot into words at a
    /// tier, and declares which clauses those words convey.
    /// </summary>
    /// <remarks>
    /// <b>Coverage comes from here and nowhere else.</b> The sender of an attempt
    /// cannot pass "covered" after the fact; the plan's coverage is this
    /// renderer's declared information-to-clause mapping, checked against the
    /// rendering actually selected for the tier. A token cannot prove arbitrary
    /// words express a fact — there is a reviewed renderer contract behind the
    /// mapping, and tests read the material the production renderer sent.
    /// </remarks>
    public interface IFactRenderer
    {
        /// <summary>Render, or null when no approved rendering exists for this request.</summary>
        FactRendering? Render(FactSnapshot fact, VerbosityLevel tier, bool historical, IFactCatalog catalog);
    }

    /// <summary>
    /// The production renderer: the lexicon entry's text at the tier, with
    /// placeholders filled from the fact's material and current values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A clause is conveyed exactly when the resolved tier's text contains
    /// its <c>{name}</c> placeholder.</b> So a terse tier that leaves out a
    /// duration produces a smaller coverage set, and completing it cannot
    /// discharge the duration. The occurrence itself (the core clause) is
    /// conveyed by any rendering of the message.
    /// </para>
    /// <para>
    /// A current rendering fills from the latest observation, so a persistent
    /// condition's old exact number is not presented as timeless. A historical
    /// rendering fills from what was established then, through the entry's own
    /// history key, and never replays a present-tense assurance.
    /// </para>
    /// </remarks>
    public sealed class LexiconFactRenderer : IFactRenderer
    {
        public static LexiconFactRenderer Instance { get; } = new LexiconFactRenderer();

        public FactRendering? Render(FactSnapshot fact, VerbosityLevel tier, bool historical, IFactCatalog catalog)
        {
            if (fact == null || catalog == null) return null;

            string? key = historical ? fact.Delivery?.HistoryKey : fact.MessageKey;
            if (string.IsNullOrEmpty(key)) return null;

            LexiconMessage message = catalog.Lookup(key!);
            string? text = message.Entry?.Resolve(tier);
            if (text == null) return null;

            // Latest value per clause name from the unsuperseded material,
            // then — for a CURRENT rendering only — the latest observation.
            var values = new Dictionary<string, FactValue>(StringComparer.Ordinal);
            var required = new HashSet<long>(fact.Required);
            foreach (MaterialUnit unit in fact.Materials)
                if (required.Contains(unit.Id)) values[unit.Name] = unit.Value;
            if (!historical)
                foreach (var pair in fact.CurrentValues.Values) values[pair.Key] = pair.Value;

            var arguments = new List<(string Name, object? Value)>();
            var conveyed = new HashSet<string>(StringComparer.Ordinal) { MaterialUnit.CoreName };
            foreach (var pair in values)
            {
                if (text.IndexOf("{" + pair.Key + "}", StringComparison.Ordinal) < 0) continue;
                arguments.Add((pair.Key, pair.Value.AsArgument));
                conveyed.Add(pair.Key);
            }

            string filled = Lexicon.Fill(text, arguments.ToArray());
            return new FactRendering(
                filled, arguments, conveyed,
                key + "@" + tier.ToString().ToLowerInvariant() + (historical ? "/history" : ""),
                message.CatalogGeneration);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Plans
    // ────────────────────────────────────────────────────────────────────

    /// <summary>What a plan is asked for.</summary>
    public sealed class PlanRequest
    {
        private PlanRequest(PlanRequestKind kind, VerbosityLevel tier, SelectedReadGrant? read)
        {
            Kind = kind;
            Tier = tier;
            Read = read;
        }

        public PlanRequestKind Kind { get; }
        public VerbosityLevel Tier { get; }
        public SelectedReadGrant? Read { get; }

        public static PlanRequest Automatic(VerbosityLevel tier) => new(PlanRequestKind.Automatic, tier, null);

        public static PlanRequest SelectedRead(SelectedReadGrant grant, VerbosityLevel tier) =>
            new(PlanRequestKind.SelectedRead, tier, grant ?? throw new ArgumentNullException(nameof(grant)));
    }

    /// <summary>Why preparation could not produce a plan because presentation metadata is missing.</summary>
    /// <remarks>
    /// <b>The unclassified-key decision is NOT made here.</b> Noel has ruled that
    /// an unclassified key should speak and the exact reading is still with him.
    /// This result carries what either eventual reading needs — the episode, its
    /// key, its classification and its owed material — so an approved one-shot
    /// or fallback can be selected later without changing how a plan binds.
    /// </remarks>
    public enum UnresolvedPolicyReason
    {
        Unclassified = 0,
        TextOnly = 1,
        MissingRendering = 2,
        MissingHistoryRendering = 3,
    }

    public enum PreparationOutcome
    {
        Prepared = 0,
        Unresolved = 1,
        NotEligible = 2,
        NotFound = 3,
        ReadGrantInvalid = 4,

        /// <summary>
        /// The record's material is no longer what the operator was shown when
        /// the read was requested, so a plan would say something unseen. The
        /// surface shows the newer detail and the operator selects again;
        /// nothing is substituted for the snapshot he chose.
        /// </summary>
        ShownSnapshotChanged = 5,
    }

    /// <summary>The result of preparing a plan.</summary>
    public sealed class PlanPreparation
    {
        internal PlanPreparation(PreparationOutcome outcome, PresentationPlan? plan, UnresolvedPolicyReason? unresolved,
                                 FactSnapshot? fact, string explanation)
        {
            Outcome = outcome;
            Plan = plan;
            Unresolved = unresolved;
            Fact = fact;
            Explanation = explanation;
        }

        public PreparationOutcome Outcome { get; }
        public PresentationPlan? Plan { get; }
        public UnresolvedPolicyReason? Unresolved { get; }
        public FactSnapshot? Fact { get; }
        public string Explanation { get; }
        public bool Prepared => Outcome == PreparationOutcome.Prepared;
    }

    /// <summary>
    /// An immutable presentation plan: exactly what will be said, from which
    /// snapshot, covering which information, under which permission.
    /// </summary>
    /// <remarks>
    /// Preparing a plan reserves no speaking turn and records no completion. A
    /// completion can only ever discharge the material this plan carried — a
    /// correction published before the callback stays owed, because its
    /// material has a different identity.
    /// </remarks>
    public sealed class PresentationPlan
    {
        internal PresentationPlan(
            FactStore store, long planId, EpisodeId episode, int variant, PlanRequestKind kind, bool historical,
            IReadOnlyDictionary<long, string> coverage, IReadOnlyDictionary<long, FactValue> materialValues,
            long factRevision, long observationRevision, string ownerName, long scopeId, string contractName,
            int contractRevision, FactRendering rendering, VerbosityLevel tier, DeliveryPriority priority,
            long? grantId, SelectedReadGrant? read, string factFingerprint)
        {
            Store = store;
            PlanId = planId;
            Episode = episode;
            Variant = variant;
            Kind = kind;
            Historical = historical;
            Coverage = coverage;
            MaterialValues = materialValues;
            FactRevision = factRevision;
            ObservationRevision = observationRevision;
            OwnerName = ownerName;
            ScopeId = scopeId;
            ContractName = contractName;
            ContractRevision = contractRevision;
            Text = rendering.Text;
            Arguments = rendering.Arguments;
            RenderingIdentity = rendering.RenderingIdentity;
            CatalogGeneration = rendering.CatalogGeneration;
            Tier = tier;
            Priority = priority;
            GrantId = grantId;
            Read = read;
            FactFingerprint = factFingerprint;
            ContentFingerprint = FactHash.Of(Text + "|" + string.Join(",", coverage.Keys.OrderBy(k => k)) + "|" + RenderingIdentity);
        }

        internal FactStore Store { get; }
        internal SelectedReadGrant? Read { get; }

        /// <summary>The material fingerprint of the snapshot this plan was rendered from.</summary>
        internal string FactFingerprint { get; }

        public long PlanId { get; }
        public EpisodeId Episode { get; }
        public int Variant { get; }
        public PlanRequestKind Kind { get; }

        /// <summary>A historical plan binds historical claims; it does not pretend a saved owner is alive.</summary>
        public bool Historical { get; }

        /// <summary>The material identities this plan's words carry, with their clause names.</summary>
        public IReadOnlyDictionary<long, string> Coverage { get; }

        /// <summary>The exact values the words were rendered from.</summary>
        public IReadOnlyDictionary<long, FactValue> MaterialValues { get; }

        public long FactRevision { get; }
        public long ObservationRevision { get; }
        public string OwnerName { get; }
        public long ScopeId { get; }
        public string ContractName { get; }
        public int ContractRevision { get; }

        /// <summary>The final words. What a transport sends, and what tests read.</summary>
        public string Text { get; }
        public IReadOnlyList<(string Name, object? Value)> Arguments { get; }
        public string RenderingIdentity { get; }
        public long CatalogGeneration { get; }
        public VerbosityLevel Tier { get; }
        public DeliveryPriority Priority { get; }

        /// <summary>The automatic grant this plan relies on, when automatic.</summary>
        public long? GrantId { get; }

        /// <summary>Detects a mismatch in persistence or callback correlation. Not proof of semantic truth.</summary>
        public string ContentFingerprint { get; }

        public override string ToString() => "plan " + PlanId + " for " + Episode + ": " + Text;
    }

    /// <summary>
    /// A single-use permission to read one selected record, granted by an
    /// explicit operator action and positioned in the ordered stream when it
    /// was taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It authorises ONE attributable attempt at that snapshot. It does not
    /// resume automatic speech, and a newer quiet observed before its attempt
    /// starts wins.
    /// </para>
    /// <para>
    /// <b>It is bound to the material the operator was shown.</b> Preparing a
    /// plan compares the record's material fingerprint with the one this
    /// grant carries; if the material moved on between the selection and the
    /// preparation, the read is refused rather than saying something unseen.
    /// The binding survives a reconnect: the grant names its episode, and a
    /// successor episode is never substituted for it.
    /// </para>
    /// </remarks>
    public sealed class SelectedReadGrant
    {
        internal SelectedReadGrant(FactStore store, long id, EpisodeId episode, int variant, long actionSequence,
                                   IReadOnlyCollection<long> represented, string materialFingerprint)
        {
            Store = store;
            Id = id;
            Episode = episode;
            Variant = variant;
            ActionSequence = actionSequence;
            Represented = represented;
            MaterialFingerprint = materialFingerprint;
        }

        internal FactStore Store { get; }
        internal bool ConsumedLocked;
        internal IReadOnlyCollection<long> Represented { get; }
        internal string MaterialFingerprint { get; }

        public long Id { get; }
        public EpisodeId Episode { get; }
        public int Variant { get; }
        public long ActionSequence { get; }
    }

    /// <summary>
    /// The presentation builder's endpoint. Only a registered builder can
    /// prepare plans and allocate attempts; producers and the review surface
    /// cannot.
    /// </summary>
    public sealed class FactPresentation
    {
        internal FactPresentation(FactStore store, string name)
        {
            Store = store;
            Name = name;
        }

        internal FactStore Store { get; }
        public string Name { get; }

        /// <summary>Prepare an immutable plan from the current snapshot and the approved catalogue.</summary>
        public PlanPreparation Prepare(EpisodeId episode, PlanRequest request) => Store.PreparePlan(this, episode, 0, request);

        /// <summary>Prepare a plan for one variant of a conflicted identity (selected reads only).</summary>
        public PlanPreparation Prepare(EpisodeId episode, int variant, PlanRequest request) =>
            Store.PreparePlan(this, episode, variant, request);

        /// <summary>
        /// Allocate an attempt for a plan on a registered transport binding.
        /// At most one unconsumed automatic allocation exists per grant; a
        /// concurrent duplicate gets the same pending allocation back, not a
        /// second runnable payload.
        /// </summary>
        public AttemptAllocation AllocateAttempt(PresentationPlan plan, TransportBinding binding) =>
            Store.AllocateAttempt(this, plan, binding);
    }

    public enum AllocationOutcome
    {
        Allocated = 0,
        AlreadyPending = 1,
        ForeignPlanOrBinding = 2,
        ReadGrantConsumed = 3,
        AttemptCapacity = 4,
    }

    public sealed class AttemptAllocation
    {
        internal AttemptAllocation(AllocationOutcome outcome, AttemptHandle? attempt, string explanation)
        {
            Outcome = outcome;
            Attempt = attempt;
            Explanation = explanation;
        }

        public AllocationOutcome Outcome { get; }
        public AttemptHandle? Attempt { get; }
        public string Explanation { get; }
    }

    /// <summary>A registered transport adapter's binding: reader-session identity and what it can establish.</summary>
    /// <remarks>
    /// Replacing a reader registers a NEW binding. The old binding can still
    /// report attributable evidence about its own attempts; it can never touch
    /// the new one's.
    /// </remarks>
    public sealed class TransportBinding
    {
        internal TransportBinding(FactStore store, long id, string name, TransportCapability capability)
        {
            Store = store;
            BindingId = id;
            Name = name;
            Capability = capability;
        }

        internal FactStore Store { get; }
        public long BindingId { get; }
        public string Name { get; }
        public TransportCapability Capability { get; }

        /// <summary>
        /// Report evidence through the already-issued attempt identity, for an
        /// adapter that has no early ticket.
        /// </summary>
        public EvidenceResult Report(AttemptId attempt, TransportEvidence evidence) =>
            Store.ReportEvidence(this, attempt, evidence);

        public override string ToString() => Name + " (" + Capability + ")";
    }

    /// <summary>What the final start gate decided.</summary>
    public sealed class StartDecision
    {
        internal StartDecision(bool authorized, NotStartedReason? reason)
        {
            Authorized = authorized;
            Reason = reason;
        }

        /// <summary>
        /// The attempt is in flight from the application's perspective. <b>Not</b>
        /// proof that native sound started, nor that a later stop will silence it.
        /// </summary>
        public bool Authorized { get; }
        public NotStartedReason? Reason { get; }

        public override string ToString() => Authorized ? "authorised" : "not started: " + Reason;
    }

    public enum EvidenceResult
    {
        Recorded = 0,
        Duplicate = 1,

        /// <summary>Wrong binding, wrong ticket, another store or another attempt. Nothing changed.</summary>
        Foreign = 2,

        /// <summary>The adapter's registered capability does not cover this kind of evidence.</summary>
        NotPermitted = 3,

        /// <summary>The attempt never passed the final start gate, so there is nothing for this to be about.</summary>
        NotStarted = 4,

        /// <summary>The attempt's record has been evicted. The relevant debt stays unknown; nothing is guessed.</summary>
        Uncorrelatable = 5,

        /// <summary>The same adapter sequence with different content.</summary>
        Conflict = 6,

        /// <summary>Recorded, and it contradicts earlier terminal evidence. Disputed coverage discharges nothing.</summary>
        Disputed = 7,
    }

    /// <summary>
    /// One allocated attempt, and the outcome sink restricted to it.
    /// </summary>
    public sealed class AttemptHandle
    {
        internal AttemptHandle(FactStore store, AttemptId id, PresentationPlan plan, TransportBinding binding, bool duplicate)
        {
            Store = store;
            Id = id;
            Plan = plan;
            Binding = binding;
            IsDuplicateAllocation = duplicate;
        }

        internal FactStore Store { get; }
        public AttemptId Id { get; }
        public PresentationPlan Plan { get; }
        public TransportBinding Binding { get; }

        /// <summary>This handle was returned for an allocation that already existed; it carries no second runnable payload.</summary>
        public bool IsDuplicateAllocation { get; }

        /// <summary>
        /// The final application start boundary. Consumes the allocation
        /// exactly once, under the same gate that orders quiet and lifecycle.
        /// </summary>
        public StartDecision TryCommitStart() => Store.TryCommitStart(this);

        /// <summary>Bind the backend ticket, before native work. Immutable once bound.</summary>
        public bool BindTicket(string ticket) => Store.BindTicket(this, ticket);

        /// <summary>Report typed evidence about THIS attempt.</summary>
        public EvidenceResult Report(TransportEvidence evidence) => Store.ReportEvidence(Binding, Id, evidence);

        public override string ToString() => Id.ToString();
    }

    public enum AttemptRunOutcome
    {
        NotStarted = 0,
        Requested = 1,
        RequestThrew = 2,
    }

    /// <summary>
    /// The order an adapter must keep: the final start gate, then native work,
    /// then the request evidence — with any callback routed through the
    /// already-issued attempt.
    /// </summary>
    /// <remarks>
    /// <b>Successful method return proves only what that API promises.</b> The
    /// runner records "request issued", never "accepted" and never "completed";
    /// an adapter that can establish more reports it through the attempt with
    /// its own sequence numbers. The runner's own request evidence takes
    /// adapter sequence zero, because the request boundary is by definition
    /// first — so a completion reported synchronously during submission is not
    /// downgraded by the request-return that follows it.
    /// </remarks>
    public static class AttemptRunner
    {
        public static AttemptRunOutcome Run(AttemptHandle attempt, Func<PresentationPlan, AttemptHandle, string?> submitNative)
        {
            if (attempt == null) throw new ArgumentNullException(nameof(attempt));
            if (submitNative == null) throw new ArgumentNullException(nameof(submitNative));

            StartDecision decision = attempt.TryCommitStart();
            if (!decision.Authorized) return AttemptRunOutcome.NotStarted;

            string? ticket;
            try
            {
                ticket = submitNative(attempt.Plan, attempt);
            }
            catch (Exception ex)
            {
                attempt.Report(TransportEvidence.RequestThrew(0, ex.GetType().Name + ": " + ex.Message));
                return AttemptRunOutcome.RequestThrew;
            }

            if (ticket != null) attempt.BindTicket(ticket);
            attempt.Report(TransportEvidence.RequestIssued(0, ticket));
            return AttemptRunOutcome.Requested;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Receipts
    // ────────────────────────────────────────────────────────────────────

    public enum ReceiptClaimOutcome
    {
        Granted = 0,

        /// <summary>The allowance was already claimed or spent. No second request permit exists.</summary>
        AlreadyClaimed = 1,

        /// <summary>The message's policy is none, or it has no classification to carry one.</summary>
        NoAllowance = 2,

        /// <summary>History from an ended scope or a previous process. Never replayed.</summary>
        NotLive = 3,

        /// <summary>The occurrence's own permission is paused — a quiet came after it, or continuity inherited one.</summary>
        Withheld = 4,

        NotFound = 5,
        Foreign = 6,
    }

    public sealed class ReceiptClaim
    {
        internal ReceiptClaim(ReceiptClaimOutcome outcome, ReceiptPermit? permit)
        {
            Outcome = outcome;
            Permit = permit;
        }

        public ReceiptClaimOutcome Outcome { get; }
        public ReceiptPermit? Permit { get; }
    }

    /// <summary>The adapter allowed to claim occurrence receipts.</summary>
    public sealed class ReceiptEndpoint
    {
        internal ReceiptEndpoint(FactStore store, string name)
        {
            Store = store;
            Name = name;
        }

        internal FactStore Store { get; }
        public string Name { get; }

        /// <summary>
        /// Atomically change the occurrence's unconsumed allowance to claimed,
        /// and return its single-use request permit.
        /// </summary>
        public ReceiptClaim TryClaim(EpisodeId episode) => Store.ClaimReceipt(this, episode);
    }

    public enum ReceiptConsumeOutcome
    {
        /// <summary>Proceed to the tone request. The allowance is now spent.</summary>
        Proceed = 0,

        /// <summary>Refused at the boundary: a quiet or a scope end came first. No tone may be requested.</summary>
        Withheld = 1,

        /// <summary>This permit was already used.</summary>
        AlreadyUsed = 2,
    }

    /// <summary>
    /// A single-use permission to request ONE tone for ONE occurrence.
    /// </summary>
    /// <remarks>
    /// Consumed at the request boundary BEFORE any tone work — never by
    /// playing first and recording afterwards. The attainable guarantee is at
    /// most one application request per occurrence, with possibly zero after an
    /// interruption; exactly-once physical playback across a crash is not
    /// promised.
    /// </remarks>
    public sealed class ReceiptPermit
    {
        internal ReceiptPermit(FactStore store, ReceiptEndpoint endpoint, long receiptId, EpisodeId episode, ReceiptPolicy policy)
        {
            Store = store;
            Endpoint = endpoint;
            ReceiptId = receiptId;
            Episode = episode;
            Policy = policy;
        }

        internal FactStore Store { get; }
        internal ReceiptEndpoint Endpoint { get; }
        internal bool UsedLocked;

        public long ReceiptId { get; }
        public EpisodeId Episode { get; }
        public ReceiptPolicy Policy { get; }

        /// <summary>Recheck scope and quiet, then spend the allowance. Call immediately before the tone request.</summary>
        public ReceiptConsumeOutcome TryConsume() => Store.ConsumeReceipt(this);

        /// <summary>Report what the tone request boundary said.</summary>
        public void Report(ToneRequestResult result) => Store.ReportReceipt(this, result);

        /// <summary>The request boundary was crossed and nothing more is known.</summary>
        public void ReportUnknown() => Store.ReportReceiptUnknown(this);
    }

    public enum ReceiptAttemptOutcome
    {
        ToneRequested = 0,
        NotClaimed = 1,
        Withheld = 2,
        ToneRequestFailed = 3,
    }

    /// <summary>
    /// The production receipt adapter: claim, consume at the boundary, request
    /// the tone through the lowest injected call, report.
    /// </summary>
    /// <remarks>
    /// <b>The tone request is injected, not bound here.</b> Binding a policy to
    /// an actual sound belongs to the layer that owns sounds; this adapter owns
    /// only the ORDER, which is the part that decides whether a retry can make a
    /// second noise.
    /// </remarks>
    public sealed class ReceiptRequestAdapter
    {
        private readonly ReceiptEndpoint _endpoint;
        private readonly Func<ReceiptPolicy, ToneRequestResult> _requestTone;

        public ReceiptRequestAdapter(ReceiptEndpoint endpoint, Func<ReceiptPolicy, ToneRequestResult> requestTone)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _requestTone = requestTone ?? throw new ArgumentNullException(nameof(requestTone));
        }

        /// <summary>Claim and fire in one go.</summary>
        public ReceiptAttemptOutcome RequestFor(EpisodeId episode)
        {
            ReceiptClaim claim = _endpoint.TryClaim(episode);
            if (claim.Permit == null) return ReceiptAttemptOutcome.NotClaimed;
            return Fire(claim.Permit);
        }

        /// <summary>Use a permit claimed earlier — for a cue that waits for a lead-in before it sounds.</summary>
        public ReceiptAttemptOutcome Fire(ReceiptPermit permit)
        {
            if (permit == null) throw new ArgumentNullException(nameof(permit));
            if (permit.TryConsume() != ReceiptConsumeOutcome.Proceed) return ReceiptAttemptOutcome.Withheld;

            ToneRequestResult result;
            try
            {
                result = _requestTone(permit.Policy);
            }
            catch (Exception)
            {
                // The boundary was crossed; what the player did is unknown.
                // Uncertainty never resets the allowance.
                permit.ReportUnknown();
                return ReceiptAttemptOutcome.ToneRequestFailed;
            }

            permit.Report(result);
            return ReceiptAttemptOutcome.ToneRequested;
        }
    }
}
