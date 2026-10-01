#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JJTrace;

namespace Radios.Facts
{
    /// <summary>Where the store is in loading its saved history.</summary>
    public enum HistoryLoadState
    {
        /// <summary>No journal is attached, so nothing can be said about saved history.</summary>
        NotAttached = 0,

        /// <summary>A journal is loading. A distinct state, never "empty".</summary>
        Loading = 1,

        /// <summary>Loading finished. Whatever it could not account for is an issue row.</summary>
        Loaded = 2,
    }

    /// <summary>
    /// The information owed to the operator, held independently of the
    /// sentence, the radio, the reader and every delivery attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One gate, one ordered stream.</b> Source capture, quiet, permission
    /// changes, lifecycle revocation, receipt claims and the final application
    /// start all take the same short gate and the same strictly increasing
    /// sequence. There is no second counter to disagree with the first. Checks
    /// and mutation happen atomically under it; nothing slow ever runs under it.
    /// </para>
    /// <para>
    /// <b>Everything outside sees snapshots.</b> Find, All, Pending, History and
    /// the projection return immutable views. There is no public setter beside
    /// the checked paths: publication goes through an issued
    /// <see cref="SlotPublisher"/>, presentation through an issued
    /// <see cref="FactPresentation"/>, review through a displayed-detail token,
    /// and history comes back through an internal hydration endpoint that
    /// cannot mint a publisher.
    /// </para>
    /// <para>
    /// <b>Nothing in here waits and nothing in here reads a clock.</b> No
    /// method touches a radio, a delegate, the UI dispatcher, the disk or native
    /// speech under the gate. Every time value in this layer was supplied by a
    /// producer and is used for ordering display and explanation only;
    /// eligibility is decided by owner transitions and sequence comparisons,
    /// and no rule asks how old anything is.
    /// </para>
    /// <para>
    /// <b>It is narrow on purpose.</b> Undelivered detail, the history of
    /// perishable events, and the store's own problems — not a notifications
    /// centre.
    /// </para>
    /// </remarks>
    public sealed partial class FactStore
    {
        internal readonly object Gate = new object();

        private readonly Dictionary<EpisodeId, List<FactRecord>> _byId = new();
        private readonly List<FactRecord> _order = new();
        private readonly Dictionary<string, IssueRecord> _issues = new(StringComparer.Ordinal);
        private IssueRecord? _overflowIssue;

        private long _sequence;
        private long _latestQuiet;
        private readonly List<(long Sequence, string Reason)> _quiets = new();

        private long _nextEpisode, _nextEvent, _nextScope, _nextSlot, _nextAttempt, _nextPlan, _nextGrant,
                     _nextReceipt, _nextView, _nextToken, _nextIssue, _nextReadGrant, _nextBinding;

        private long _mutation;
        private long _persistedThrough;
        private long _projectionRevision;
        private HistoryLoadState _load = HistoryLoadState.NotAttached;
        private bool _journalAttached;

        private long _compactedCovered;
        private long _compactedReviewedOnly;
        private long _compactedForgettableUnpresented;
        private bool _compactedCountsLowerBound;

        private bool _channelFailing;
        private bool _raiseProjection;
        private bool _raisePersistence;

        /// <summary>
        /// The largest sequence this store may issue. Checked, never wrapped:
        /// reaching it stops issuance with a reachable integrity result rather
        /// than wrapping into apparently fresh authority.
        /// </summary>
        internal long SequenceCeiling = long.MaxValue;

        internal FactStore(Guid writerIncarnation, IFactCatalog catalog, IFactRenderer renderer)
        {
            if (writerIncarnation == Guid.Empty) throw new ArgumentException("a store needs a writer incarnation", nameof(writerIncarnation));
            WriterIncarnation = writerIncarnation;
            Catalog = catalog;
            Renderer = renderer;
        }

        /// <summary>
        /// This store's random writer incarnation: its process identity and
        /// the namespace of every identity it allocates. One per lifetime; never
        /// derived from a timestamp.
        /// </summary>
        public Guid WriterIncarnation { get; }

        internal IFactCatalog Catalog { get; }
        internal IFactRenderer Renderer { get; }

        /// <summary>
        /// Raised, outside the gate, whenever anything the surface shows may
        /// have changed. A bare signal: a handler takes a fresh projection
        /// itself. Handlers must be cheap and must not block.
        /// </summary>
        public event Action? ProjectionChanged;

        /// <summary>
        /// Raised, outside the gate, when a mutation needing persistence
        /// happened. Persistence-status changes do not raise it, so a writer
        /// cannot talk itself into an endless loop.
        /// </summary>
        public event Action? PersistenceNeeded;

        // ────────────────────────────────────────────────────────────────
        //  The ordered stream
        // ────────────────────────────────────────────────────────────────

        /// <summary>Take the next position. Under the gate. Null when exhausted.</summary>
        internal long? NextSequenceLocked()
        {
            if (_sequence >= SequenceCeiling)
            {
                NoteIssueLocked(IssueKind.IntegrityRefusal, "sequence", "this store",
                                "the ordered sequence reached its end; no further events, quiets or starts can be ordered",
                                1, ExtentCertainty.Exact, "sequence exhausted", "sequence-exhausted");
                return null;
            }
            return ++_sequence;
        }

        /// <summary>The position of the most recent observed operator quiet, or zero.</summary>
        public long LatestQuietSequence
        {
            get { lock (Gate) return _latestQuiet; }
        }

        /// <summary>The current position of the ordered stream.</summary>
        public long CurrentSequence
        {
            get { lock (Gate) return _sequence; }
        }

        internal long Checked(ref long counter)
        {
            if (counter == long.MaxValue) throw new InvalidOperationException("an identity counter reached its end");
            return ++counter;
        }

        // ────────────────────────────────────────────────────────────────
        //  Signals, raised after the gate is released
        // ────────────────────────────────────────────────────────────────

        /// <summary>Under the gate: the projection changed, and optionally something needs saving.</summary>
        internal void TouchLocked(bool persist)
        {
            _projectionRevision++;
            _raiseProjection = true;
            if (persist)
            {
                _mutation++;
                _raisePersistence = true;
            }
        }

        /// <summary>After the gate: raise whatever the last operation owes.</summary>
        internal void RaiseSignals()
        {
            bool projection, persistence;
            lock (Gate)
            {
                projection = _raiseProjection;
                persistence = _raisePersistence;
                _raiseProjection = false;
                _raisePersistence = false;
            }
            if (persistence) PersistenceNeeded?.Invoke();
            if (projection) ProjectionChanged?.Invoke();
        }

        // ────────────────────────────────────────────────────────────────
        //  Queries — immutable snapshots only
        // ────────────────────────────────────────────────────────────────

        /// <summary>One episode, or null. For a conflicted identity this is the first variant.</summary>
        public FactSnapshot? Find(EpisodeId id)
        {
            lock (Gate)
            {
                return _byId.TryGetValue(id, out List<FactRecord>? list) && list.Count > 0
                    ? list[0].Freeze(_latestQuiet, _projectionRevision)
                    : null;
            }
        }

        /// <summary>Every variant of one identity: more than one only after an identity conflict on load.</summary>
        public IReadOnlyList<FactSnapshot> Variants(EpisodeId id)
        {
            lock (Gate)
            {
                return _byId.TryGetValue(id, out List<FactRecord>? list)
                    ? list.Select(r => r.Freeze(_latestQuiet, _projectionRevision)).ToArray()
                    : Array.Empty<FactSnapshot>();
            }
        }

        /// <summary>Every retained fact, oldest first.</summary>
        public IReadOnlyList<FactSnapshot> All
        {
            get { lock (Gate) return _order.Select(r => r.Freeze(_latestQuiet, _projectionRevision)).ToArray(); }
        }

        /// <summary>Owed facts: a retained debt nobody presented or reviewed.</summary>
        public IReadOnlyList<FactSnapshot> Pending(string? stationFilter = null)
        {
            lock (Gate)
            {
                return _order.Where(r => r.HasRetainedDebt() && Matches(r, stationFilter))
                             .Select(r => r.Freeze(_latestQuiet, _projectionRevision)).ToArray();
            }
        }

        /// <summary>History: perishable events, ended premises, and everything restored.</summary>
        public IReadOnlyList<FactSnapshot> History(string? stationFilter = null)
        {
            lock (Gate)
            {
                return _order.Where(r => IsHistoryRow(r) && Matches(r, stationFilter))
                             .Select(r => r.Freeze(_latestQuiet, _projectionRevision)).ToArray();
            }
        }

        /// <summary>Every retained store issue, active or resolved.</summary>
        public IReadOnlyList<StoreIssueSnapshot> Issues
        {
            get { lock (Gate) return AllIssuesLocked().Select(i => i.Freeze(_projectionRevision)).ToArray(); }
        }

        /// <summary>How many facts are owed. For the Status count; the surface uses the full projection.</summary>
        public int PendingCount
        {
            get { lock (Gate) return _order.Count(r => r.HasRetainedDebt()); }
        }

        public long ProjectionRevision
        {
            get { lock (Gate) return _projectionRevision; }
        }

        /// <summary>The newest mutation needing persistence.</summary>
        public long MutationSequence
        {
            get { lock (Gate) return _mutation; }
        }

        /// <summary>The newest mutation a committed journal image contains.</summary>
        public long PersistedThrough
        {
            get { lock (Gate) return _persistedThrough; }
        }

        /// <summary>
        /// The counts of compacted history are lower bounds: a count saturated,
        /// or a loaded source said its own were. Round-tripped with the counts,
        /// because a count whose certainty was dropped on load would read as
        /// exact.
        /// </summary>
        public bool CompactedCountsLowerBound
        {
            get { lock (Gate) return _compactedCountsLowerBound; }
        }

        public HistoryLoadState Load
        {
            get { lock (Gate) return _load; }
        }

        private static bool Matches(FactRecord r, string? station) =>
            station == null || string.Equals(r.RadioIdentity, station, StringComparison.Ordinal);

        private static bool IsHistoryRow(FactRecord r) =>
            r.Restored
            || (r.Delivery != null && r.Delivery.ShelfLife == ShelfLife.Perishable)
            || r.Validity.State == ValidityState.Ended
            || r.Validity.State == ValidityState.Superseded;

        // ────────────────────────────────────────────────────────────────
        //  Eligibility — what may be offered for an automatic attempt
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// May this fact be offered for another AUTOMATIC presentation?
        /// </summary>
        /// <remarks>
        /// <para>
        /// The scheduler asks this; it does not decide it. The final start gate
        /// asks the same question again, under the gate, against the live
        /// record — this answer is about a snapshot and can go stale.
        /// </para>
        /// <para>
        /// <b>The staleness rule, stated as an invariant because a source guard
        /// cannot prove it:</b> nothing here compares a time. Whether saying
        /// something is still justified is the owner's validity transition;
        /// whether it may be said now is a comparison of sequence positions.
        /// </para>
        /// </remarks>
        public bool IsEligibleForAutomaticDelivery(FactSnapshot fact)
        {
            if (fact == null) return false;

            // ── DECISION POINT ONE — RULED ─────────────────────────────────
            // Noel, 2026-09-24 07:46 (#617): "if pressing control silences and
            // the alarm condition gets worse, then we definitely would want to
            // speak that warning again."
            //
            // So a quiet lasts until the condition's OWNER issues a worsening,
            // which carries its own grant (see FactStore.Worsen). A reconnect,
            // a window opening, a backend recovering and time passing release
            // nothing. AutomaticPaused is derived from grants and the latest
            // quiet position, never stored as a flag that could go stale.
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

            // History has no live owner to justify a present-tense rendering.
            if (!fact.IsLive) return false;

            // A rendering that is not justified is not offered. Unknown
            // suspends the current wording immediately, without claiming
            // anything resolved.
            if (!fact.Validity.IsCurrent) return false;

            // Forgettable gets exactly one eligible presentation and no
            // retained debt. It is eligible for that first one — which it
            // never was before, because eligibility used to end on "has a
            // retained debt" and forgettable information has none by design.
            if (fact.Delivery!.ShelfLife == ShelfLife.Forgettable)
                return fact.Attempts.Count == 0 && fact.Unpresented.Count > 0;

            return fact.HasUndeliveredDetail && fact.PermittedNow;
        }

        /// <summary>
        /// May this fact be read because the operator asked for this record?
        /// Asking is its own permission; truth is still checked at the final
        /// start boundary, and ended premises are read as history.
        /// </summary>
        public bool MayReadOnRequest(FactSnapshot fact) => fact != null;

        /// <summary>
        /// True when this attempt would be the third or later automatic repeat
        /// in a burst, so a waiting explicit request should go first.
        /// Fairness, never a lifetime cap.
        /// </summary>
        public bool ShouldYieldToWaitingRequest(FactSnapshot fact) =>
            fact != null
            && fact.Attempts.Count(a => a.Kind == PlanRequestKind.Automatic) >= FactStoreCapacity.AutomaticBurstAttempts;

        // ────────────────────────────────────────────────────────────────
        //  Delivery channel health
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Note whether the delivery channel is working, and say whether this
        /// call is the TRANSITION into failure — once per edge, not per fact.
        /// Deliberate quiet is not a failure and never reaches this.
        /// </summary>
        public bool NoteChannelHealth(bool healthy)
        {
            lock (Gate)
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

        public bool ChannelFailing
        {
            get { lock (Gate) return _channelFailing; }
        }

        // ────────────────────────────────────────────────────────────────
        //  Store issues — reserved, bounded, reachable
        // ────────────────────────────────────────────────────────────────

        private IEnumerable<IssueRecord> AllIssuesLocked()
        {
            foreach (IssueRecord issue in _issues.Values.OrderBy(i => i.Id)) yield return issue;
            if (_overflowIssue != null) yield return _overflowIssue;
        }

        /// <summary>
        /// Record a store problem. Creates or updates its row, deduplicates a
        /// repeated observation of the same thing, and advances the issue's
        /// revision only when there is genuinely new information. Always under
        /// the gate, and always in the same transition as the refusal or loss
        /// it reports.
        /// </summary>
        internal IssueRecord NoteIssueLocked(
            IssueKind kind, string sourceKey, string source, string reason, long count,
            ExtentCertainty extent, string? exemplar, string? dedupeKey, bool persist = true,
            IssueState state = IssueState.Active)
        {
            string key = (int)kind + "|" + sourceKey;
            if (!_issues.TryGetValue(key, out IssueRecord? issue))
            {
                if (_issues.Count >= FactStoreCapacity.MaxIssues)
                {
                    // The reserved final summary. It exists outside the space
                    // whose exhaustion it reports, so it can always be written.
                    issue = _overflowIssue ??= new IssueRecord
                    {
                        Id = Checked(ref _nextIssue),
                        Kind = IssueKind.IssueOverflow,
                        SourceKey = "issues",
                        Source = "this store",
                        Reason = "the table of store problems is full; further problems are counted here",
                        Revision = 0,
                    };
                    issue.Count = Saturate(issue.Count, 1, out bool saturated);
                    issue.Extent = saturated ? ExtentCertainty.LowerBound : ExtentCertainty.Exact;
                    AddExemplar(issue, kind + ": " + source);
                    issue.Revision++;
                    issue.State = IssueState.Active;
                    TouchLocked(persist);
                    return issue;
                }

                issue = new IssueRecord
                {
                    Id = Checked(ref _nextIssue),
                    Kind = kind,
                    SourceKey = sourceKey,
                    Source = source,
                    Reason = reason,
                    Revision = 0,
                };
                _issues[key] = issue;
            }

            if (dedupeKey != null)
            {
                if (issue.Seen.Contains(dedupeKey))
                {
                    // The same corrupt generation, the same refused event: not
                    // new loss, so no new revision. Unless it had been
                    // resolved, in which case it is active again.
                    if (issue.State == IssueState.ResolvedWithHistory)
                    {
                        issue.State = state;
                        TouchLocked(persist);
                    }
                    return issue;
                }
                if (issue.Seen.Count < FactStoreCapacity.MaxIssueDedupeKeys) issue.Seen.Add(dedupeKey);
                else issue.SeenOverflowed = true;
            }

            issue.Count = Saturate(issue.Count, count, out bool sat);
            if (sat || issue.SeenOverflowed || extent == ExtentCertainty.LowerBound)
                issue.Extent = issue.Extent == ExtentCertainty.Unknown ? ExtentCertainty.Unknown : ExtentCertainty.LowerBound;
            if (extent == ExtentCertainty.Unknown) issue.Extent = ExtentCertainty.Unknown;
            issue.Reason = reason;
            if (exemplar != null) AddExemplar(issue, exemplar);
            issue.State = state;
            issue.Revision++;
            TouchLocked(persist);

            Tracing.TraceLine("FactStore: " + kind + " (" + source + ") — " + reason, TraceLevel.Warning);
            return issue;
        }

        /// <summary>
        /// A problem is no longer true. It stays in history; resolution is not
        /// new unreviewed information, so it can leave the default view once
        /// its last information was reviewed.
        /// </summary>
        internal void ResolveIssueLocked(IssueKind kind, string sourceKey)
        {
            if (_issues.TryGetValue((int)kind + "|" + sourceKey, out IssueRecord? issue) && issue.State == IssueState.Active)
            {
                issue.State = IssueState.ResolvedWithHistory;
                TouchLocked(persist: true);
            }
        }

        internal void NoteIntegrityLocked(string sourceKey, string what, string exemplar, string? dedupeKey = null) =>
            NoteIssueLocked(IssueKind.IntegrityRefusal, sourceKey, "publication", what, 1,
                            ExtentCertainty.Exact, exemplar, dedupeKey);

        private static void AddExemplar(IssueRecord issue, string exemplar)
        {
            if (issue.Exemplars.Count < FactStoreCapacity.MaxIssueExemplars) issue.Exemplars.Add(exemplar);
            else issue.ExemplarsOverflowed = true;
        }

        internal static long Saturate(long value, long add, out bool saturated)
        {
            saturated = false;
            if (add <= 0) return value;
            if (value > long.MaxValue - add)
            {
                saturated = true;
                return long.MaxValue;
            }
            return value + add;
        }

        // ────────────────────────────────────────────────────────────────
        //  Persistence status — reported, never promised
        // ────────────────────────────────────────────────────────────────

        internal void SetLoadState(HistoryLoadState state)
        {
            lock (Gate)
            {
                if (_load == state) return;
                _load = state;
                TouchLocked(persist: false);
            }
            RaiseSignals();
        }

        /// <summary>
        /// A journal image containing mutations up to <paramref name="throughMutation"/>
        /// committed. Acknowledges exactly that — a store already at N+1 stays
        /// unsaved through the newer mutation.
        /// </summary>
        internal void NotePersisted(long throughMutation, string ownShardKey)
        {
            lock (Gate)
            {
                if (throughMutation > _persistedThrough) _persistedThrough = throughMutation;
                // A successful write of THIS shard resolves THIS shard's
                // failure. It cannot resolve another source's problem.
                ResolveIssueLocked(IssueKind.PersistenceFailure, ownShardKey);
                _projectionRevision++;
                _raiseProjection = true;
            }
            RaiseSignals();
        }

        internal void NotePersistFailure(string ownShardKey, string shardName, string reason)
        {
            lock (Gate)
            {
                NoteIssueLocked(IssueKind.PersistenceFailure, ownShardKey, shardName, reason, 1,
                                ExtentCertainty.Unknown, reason, "fail:" + reason);
            }
            RaiseSignals();
        }

        internal void NoteIssue(IssueKind kind, string sourceKey, string source, string reason, long count,
                                ExtentCertainty extent, string? exemplar, string? dedupeKey,
                                IssueState state = IssueState.Active)
        {
            lock (Gate) NoteIssueLocked(kind, sourceKey, source, reason, count, extent, exemplar, dedupeKey, state: state);
            RaiseSignals();
        }

        internal void ResolveIssue(IssueKind kind, string sourceKey)
        {
            lock (Gate) ResolveIssueLocked(kind, sourceKey);
            RaiseSignals();
        }

        /// <summary>A journal took its lease: from here on, saved-through-current can be true.</summary>
        internal void NoteJournalAttached()
        {
            lock (Gate)
            {
                if (_journalAttached) return;
                _journalAttached = true;
                TouchLocked(persist: false);
            }
            RaiseSignals();
        }

        internal bool JournalAttachedLocked => _journalAttached;
    }
}
