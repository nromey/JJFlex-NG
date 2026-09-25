#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.Facts
{
    /// <summary>A review overlay this writer holds for another writer's episode.</summary>
    internal sealed class OverlayImage
    {
        public EpisodeId Episode;
        public string ContentFingerprint = string.Empty;
        public long[] Reviewed = Array.Empty<long>();
    }

    /// <summary>
    /// One immutable image of the store, captured under the gate with the
    /// mutation sequence it contains. Rendered and written after the gate is
    /// released; copying a list of live records would not be a consistent
    /// snapshot.
    /// </summary>
    internal sealed class StoreImage
    {
        public Guid Writer;
        public long Mutation;
        public List<FactRecord> Facts = new();
        public List<OverlayImage> Overlays = new();
        public List<ContinuityRecord> Continuity = new();
        public List<IssueRecord> Issues = new();
        public long CompactedCovered, CompactedReviewedOnly, CompactedForgettable;
        public bool CompactedLowerBound;

        public bool IsEmpty => Facts.Count == 0 && Overlays.Count == 0 && Continuity.Count == 0 && Issues.Count == 0
                               && CompactedCovered == 0 && CompactedReviewedOnly == 0 && CompactedForgettable == 0;
    }

    /// <summary>Everything one readable source contributed.</summary>
    internal sealed class LoadedSource
    {
        public string Name = string.Empty;
        public Guid HeaderWriter;
        public long Generation;
        public bool Legacy;
        public int SchemaVersion;
        public List<FactRecord> Facts = new();
        public List<OverlayImage> Overlays = new();
        public List<ContinuityRecord> Continuity = new();
        public List<IssueRecord> Issues = new();
        public long CompactedCovered, CompactedReviewedOnly, CompactedForgettable;
        public bool CompactedLowerBound;

        /// <summary>
        /// A schema-2 payload: its records are valid history, but its
        /// continuity kept no per-assertion evidence, so a reconnect cannot
        /// continue anything from it — unsupported, not empty.
        /// </summary>
        public bool DeliveryContinuityUnsupported => !Legacy && SchemaVersion < FactJournal.SchemaVersion;
    }

    /// <summary>What hydration did, counted separately — a silently ignored restore is never counted as success.</summary>
    public sealed class HydrationCounts
    {
        public int Accepted { get; internal set; }
        public int Duplicates { get; internal set; }
        public int Superseded { get; internal set; }
        public int Conflicts { get; internal set; }
        public int OverBudget { get; internal set; }
        public int OverlaysApplied { get; internal set; }
        public int OverlaysUnmatched { get; internal set; }

        public override string ToString() =>
            Accepted + " accepted, " + Duplicates + " duplicate, " + Superseded + " superseded, " + Conflicts
            + " conflicting, " + OverBudget + " over budget; overlays " + OverlaysApplied + " applied, "
            + OverlaysUnmatched + " unmatched";
    }

    public sealed partial class FactStore
    {
        /// <summary>
        /// Review overlays for imported records that were since compacted out
        /// of this store. The review must survive the next restart even though
        /// the record it names is gone from memory: the origin file still holds
        /// the record, and without the overlay it would come back owed.
        /// </summary>
        private readonly List<OverlayImage> _compactedOverlays = new();

        /// <summary>
        /// Capture one immutable image and the mutation sequence it contains.
        /// Only this writer's own facts go in; imported history is represented
        /// by review overlays, never re-exported as if this writer owned it.
        /// </summary>
        internal StoreImage CaptureImage()
        {
            lock (Gate)
            {
                var image = new StoreImage
                {
                    Writer = WriterIncarnation,
                    Mutation = _mutation,
                    CompactedCovered = _compactedCovered,
                    CompactedReviewedOnly = _compactedReviewedOnly,
                    CompactedForgettable = _compactedForgettableUnpresented,
                    CompactedLowerBound = _compactedCountsLowerBound,
                };

                foreach (FactRecord record in _order)
                {
                    if (record.Restored)
                    {
                        if (record.ReviewedLocal.Count > 0) image.Overlays.Add(OverlayOf(record));
                        continue;
                    }
                    image.Facts.Add(CloneForImage(record));
                }
                image.Overlays.AddRange(_compactedOverlays);

                // One continuity entry per key: the NEWEST episode this process
                // holds for it, otherwise what the table remembers.
                var keys = new HashSet<string>(StringComparer.Ordinal);
                for (int i = _order.Count - 1; i >= 0; i--)
                {
                    FactRecord record = _order[i];
                    if (record.Restored || record.RadioIdentity == null) continue;
                    ContinuityRecord entry = DeriveContinuity(record, fromPreviousProcess: false);
                    if (keys.Add(entry.Key)) image.Continuity.Add(entry);
                }
                foreach (ContinuityRecord entry in _continuity.Values)
                    if (keys.Add(entry.Key)) image.Continuity.Add(entry);

                foreach (IssueRecord issue in AllIssuesLocked()) image.Issues.Add(CloneIssue(issue));
                return image;
            }
        }

        private static OverlayImage OverlayOf(FactRecord record) => new OverlayImage
        {
            Episode = record.Id,
            ContentFingerprint = record.ContentFingerprint(),
            Reviewed = record.ReviewedLocal.OrderBy(x => x).ToArray(),
        };

        /// <summary>
        /// A reviewed imported record is leaving memory. Keep its overlay so
        /// the review outlives the record here, bounded; past the bound the
        /// loss is a row, never a silent return to owed.
        /// </summary>
        private void RetainOverlayLocked(FactRecord record)
        {
            if (!record.Restored || record.ReviewedLocal.Count == 0) return;
            if (_compactedOverlays.Count >= FactStoreCapacity.MaxHistoricalRecords)
            {
                NoteIssueLocked(IssueKind.DetailLoss, "overlays", "reviews of imported history",
                    "a review of imported history could not be kept after its record was compacted; it may come back as owed",
                    1, ExtentCertainty.Exact, record.Id.ToString(), "overlay:" + record.Id);
                return;
            }
            _compactedOverlays.Add(OverlayOf(record));
        }

        private FactRecord CloneForImage(FactRecord r)
        {
            (_, PauseCause pause) = FactPermission.Evaluate(r, r.Unpresented(), _latestQuiet);
            var c = new FactRecord
            {
                Id = r.Id, OwnerName = r.OwnerName, ContractName = r.ContractName, ContractRevision = r.ContractRevision,
                Condition = r.Condition, Claim = r.Claim, ScopeId = r.ScopeId, ScopeKind = r.ScopeKind,
                RadioIdentity = r.RadioIdentity, OccurrenceLabel = r.OccurrenceLabel, Priority = r.Priority,
                LineageRoot = r.LineageRoot, PredecessorEpisode = r.PredecessorEpisode,
                MessageKey = r.MessageKey, Classification = r.Classification, Delivery = r.Delivery,
                CatalogGeneration = r.CatalogGeneration, Validity = r.Validity, ObservedUtc = r.ObservedUtc,
                LastEventSequence = r.LastEventSequence, Revision = r.Revision, ObservationRevision = r.ObservationRevision,
                MaterialRevision = r.MaterialRevision, CurrentValues = r.CurrentValues, Baseline = r.Baseline,
                HistoryDropped = r.HistoryDropped, NextMaterialId = r.NextMaterialId,
                RestoredPause = pause,
                CompactedAttempts = r.CompactedAttempts, CompactedAttemptsLowerBound = r.CompactedAttemptsLowerBound,
                Detail = r.Detail, DetailTruncated = r.DetailTruncated,
                Receipt = new ReceiptRecord
                {
                    Policy = r.Receipt.Policy, State = r.Receipt.State, Consumed = r.Receipt.Consumed,
                    ReceiptId = r.Receipt.ReceiptId, Closed = r.Receipt.Closed,
                },
            };
            c.Events.AddRange(r.Events.Select(e => new EventRecord
            {
                EventOrdinal = e.EventOrdinal, Sequence = e.Sequence, SourceEventId = e.SourceEventId,
                ObservedUtc = e.ObservedUtc, Effect = e.Effect,
            }));
            c.Materials.AddRange(r.Materials);
            // This record's OWN presentation evidence. What it earned through
            // its occurrence is found again on reload through the links and
            // the continuity summary, attributed to the episode that earned it.
            c.CoveredLedger.UnionWith(r.DirectCovered());
            c.ReviewedLocal.UnionWith(r.ReviewedLocal);
            foreach (AttemptRecord a in r.Attempts.Concat(r.RetiredAttempts)) c.Attempts.Add(CloneAttempt(a, c));
            return c;
        }

        private static AttemptRecord CloneAttempt(AttemptRecord a, FactRecord owner)
        {
            var c = new AttemptRecord
            {
                Id = a.Id, Fact = owner, PlanId = a.PlanId, Kind = a.Kind, HistoricalPlan = a.HistoricalPlan,
                Rendering = a.Rendering, GrantId = a.GrantId, BindingName = a.BindingName ?? a.Binding?.Name,
                Ticket = a.Ticket, Consumed = a.Consumed, Authorized = a.Authorized, NotStarted = a.NotStarted,
                FromPreviousProcess = a.FromPreviousProcess,
                // An attempt allocated, starting or in flight when the image
                // was taken is interrupted-session evidence if this image is
                // ever what a later process reads.
                Interrupted = a.Interrupted || (!a.IsTerminal && a.NotStarted == null),
                CancellationRequested = a.CancellationRequested, CancelOperation = a.CancelOperation,
            };
            foreach (var pair in a.PlanMaterial) c.PlanMaterial[pair.Key] = pair.Value;
            foreach (var pair in a.Evidence) c.Evidence.Add(pair.Key, pair.Value);
            return c;
        }

        private static IssueRecord CloneIssue(IssueRecord i)
        {
            var c = new IssueRecord
            {
                Id = i.Id, Kind = i.Kind, SourceKey = i.SourceKey, Source = i.Source, Reason = i.Reason,
                Revision = i.Revision, ReviewedRevision = i.ReviewedRevision, State = i.State, Count = i.Count,
                Extent = i.Extent, ExemplarsOverflowed = i.ExemplarsOverflowed, SeenOverflowed = i.SeenOverflowed,
                FromPreviousProcess = i.FromPreviousProcess,
            };
            c.Exemplars.AddRange(i.Exemplars);
            c.Seen.UnionWith(i.Seen);
            return c;
        }

        // ────────────────────────────────────────────────────────────────
        //  Hydration — history in, never authority
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Put every readable source's history into the store, resolving
        /// identity collisions by their evidence. The one endpoint the journal
        /// loader uses; it can mint no publisher, no grant and no permit.
        /// </summary>
        internal HydrationCounts Hydrate(IReadOnlyList<LoadedSource> sources)
        {
            var counts = new HydrationCounts();
            try
            {
                lock (Gate)
                {
                    foreach (LoadedSource source in sources)
                    {
                        // Owed history is taken first, so a load budget spends
                        // itself on what is still owed.
                        foreach (FactRecord record in source.Facts.OrderByDescending(f => f.HasRetainedDebt()))
                            HydrateOneLocked(record, counts);

                        foreach (ContinuityRecord entry in source.Continuity) MergeContinuityLocked(entry);
                        foreach (IssueRecord issue in source.Issues) MergeIssueLocked(issue);

                        // The counts and their certainty travel together: a
                        // count that saturates here, or was a lower bound
                        // where it was written, is a lower bound here.
                        bool sat;
                        _compactedCovered = Saturate(_compactedCovered, source.CompactedCovered, out sat);
                        if (sat) _compactedCountsLowerBound = true;
                        _compactedReviewedOnly = Saturate(_compactedReviewedOnly, source.CompactedReviewedOnly, out sat);
                        if (sat) _compactedCountsLowerBound = true;
                        _compactedForgettableUnpresented = Saturate(_compactedForgettableUnpresented, source.CompactedForgettable, out sat);
                        if (sat) _compactedCountsLowerBound = true;
                        if (source.CompactedLowerBound) _compactedCountsLowerBound = true;
                    }

                    // Overlays last, so they can find whichever variant they reviewed.
                    foreach (LoadedSource source in sources)
                        foreach (OverlayImage overlay in source.Overlays) ApplyOverlayLocked(overlay, counts);

                    TouchLocked(persist: false);
                }
            }
            finally
            {
                RaiseSignals();
            }
            return counts;
        }

        private void HydrateOneLocked(FactRecord incoming, HydrationCounts counts)
        {
            incoming.Restored = true;
            incoming.Publisher = null;
            incoming.Scope = null;
            incoming.Grants.Clear();
            incoming.ContinuityPause = PauseCause.None;
            incoming.Receipt.FromPreviousProcess = true;
            incoming.Receipt.OutstandingPermit = null;
            foreach (AttemptRecord a in incoming.Attempts) a.FromPreviousProcess = true;
            HistoricalOrigin origin = incoming.Origin ??= new HistoricalOrigin();

            if (_byId.TryGetValue(incoming.Id, out List<FactRecord>? existing) && existing.Count > 0)
            {
                foreach (FactRecord variant in existing)
                {
                    if (variant.Origin?.RecordFingerprint == origin.RecordFingerprint)
                    {
                        // The same record, perhaps copied into another file.
                        // Not a second occurrence.
                        counts.Duplicates++;
                        return;
                    }
                }

                FactRecord current = existing[0];
                HistoricalOrigin prior = current.Origin ?? new HistoricalOrigin();
                bool sameLineage = existing.Count == 1 && current.Origin != null && !current.Origin.ConflictVariant
                                   && prior.HeaderWriter == origin.HeaderWriter
                                   && origin.HeaderWriter == incoming.Id.Writer
                                   && prior.Generation != origin.Generation;
                if (sameLineage)
                {
                    bool incomingNewer = origin.Generation > prior.Generation && incoming.Revision > current.Revision;
                    bool currentNewer = prior.Generation > origin.Generation && current.Revision > incoming.Revision;
                    if (incomingNewer)
                    {
                        // An established successor from the exclusive origin
                        // writer replaces its older snapshot.
                        int at = _order.IndexOf(current);
                        _order[at] = incoming;
                        existing[0] = incoming;
                        current.Lineage?.Members.Remove(current);
                        AttachLineageLocked(incoming);
                        counts.Superseded++;
                        return;
                    }
                    if (currentNewer)
                    {
                        counts.Superseded++;
                        return;
                    }
                }

                // An older snapshot of a lineage one of the variants already
                // succeeds is not a new variant, whatever order the files
                // were read in.
                foreach (FactRecord variant in existing)
                {
                    HistoricalOrigin? v = variant.Origin;
                    if (v != null && v.HeaderWriter == origin.HeaderWriter && origin.HeaderWriter == incoming.Id.Writer
                        && v.Generation > origin.Generation && variant.Revision > incoming.Revision)
                    {
                        counts.Superseded++;
                        return;
                    }
                }

                // Incompatible origin binding, equal revision with different
                // content, or an unprovable lineage: keep EVERY variant, under
                // a conflict item. Nothing chooses first or last writer, and
                // neither variant discharges the other.
                foreach (FactRecord variant in existing) (variant.Origin ??= new HistoricalOrigin()).ConflictVariant = true;
                origin.ConflictVariant = true;
                origin.VariantIndex = existing.Count;
                if (!RoomForHistoryLocked(incoming, counts)) return;
                existing.Add(incoming);
                _order.Add(incoming);
                AttachLineageLocked(incoming);
                counts.Conflicts++;
                NoteIssueLocked(IssueKind.IdentityConflict, "identity:" + incoming.Id, incoming.Id.ToString(),
                    "two saved records claim the same identity with different content; both are kept",
                    1, ExtentCertainty.Exact, origin.Source + " (" + origin.RecordFingerprint + ")",
                    "conflict:" + incoming.Id + ":" + origin.RecordFingerprint, persist: false);
                return;
            }

            if (!RoomForHistoryLocked(incoming, counts)) return;

            // A writer only ever saves its own episodes. A record in one
            // writer's file claiming another writer's identity has an
            // incompatible origin binding even when nothing else claims it.
            if (!origin.Legacy && origin.HeaderWriter != incoming.Id.Writer)
            {
                origin.ConflictVariant = true;
                NoteIssueLocked(IssueKind.IdentityConflict, "identity:" + incoming.Id, incoming.Id.ToString(),
                    "a saved record claims to belong to a different writer than the file it is in",
                    1, ExtentCertainty.Exact, origin.Source + " (" + origin.RecordFingerprint + ")",
                    "binding:" + incoming.Id + ":" + origin.RecordFingerprint, persist: false);
                counts.Conflicts++;
            }
            else
            {
                counts.Accepted++;
            }
            AddRecordLocked(incoming);
            AttachLineageLocked(incoming);
        }

        private bool RoomForHistoryLocked(FactRecord incoming, HydrationCounts counts)
        {
            if (_order.Count < FactStoreCapacity.MaxHistoricalRecords || CompactLocked()) return true;
            counts.OverBudget++;
            NoteIssueLocked(IssueKind.IncompleteInventory, "load-budget", "saved history",
                "more saved history exists than this store can hold; the source files are kept for later recovery",
                1, ExtentCertainty.Exact, incoming.Origin?.Source, "budget:" + incoming.Id + ":" + incoming.Origin?.RecordFingerprint,
                persist: false);
            return false;
        }

        private void ApplyOverlayLocked(OverlayImage overlay, HydrationCounts counts)
        {
            if (!_byId.TryGetValue(overlay.Episode, out List<FactRecord>? variants))
            {
                counts.OverlaysUnmatched++;
                return;
            }

            bool matched = false;
            foreach (FactRecord record in variants)
            {
                // Combined by referenced information identity — and only onto
                // the exact content that was reviewed. A review overlay cannot
                // heal a conflicting payload.
                if (record.ContentFingerprint() != overlay.ContentFingerprint) continue;
                var ids = new HashSet<long>(record.Materials.Select(m => m.Id));
                foreach (long id in overlay.Reviewed) if (ids.Contains(id)) record.ReviewedImported.Add(id);
                matched = true;
            }
            if (matched) counts.OverlaysApplied++;
            else counts.OverlaysUnmatched++;
        }

        /// <summary>
        /// Two sources naming the same station and condition: the same
        /// occurrence combines by exact lineage; an established successor
        /// replaces what it superseded; anything else is a conflict, and a
        /// conflicted continuity supports no continuation. Nothing chooses an
        /// unpaused candidate or unions unrelated coverage.
        /// </summary>
        private void MergeContinuityLocked(ContinuityRecord entry)
        {
            entry.FromPreviousProcess = true;
            if (!_continuity.TryGetValue(entry.Key, out ContinuityRecord? existing))
            {
                PutContinuityLocked(entry);
                return;
            }

            bool sameOccurrence = existing.Root != null && existing.Root == entry.Root;
            if (sameOccurrence && !existing.Conflicted)
            {
                if (entry.Revision > existing.Revision)
                {
                    existing.LastEpisode = entry.LastEpisode;
                    existing.Revision = entry.Revision;
                    existing.Baseline = entry.Baseline;
                    existing.DefinitionRevision = entry.DefinitionRevision;
                    existing.ReceiptState = entry.ReceiptState;
                    existing.ReceiptId = entry.ReceiptId;
                    existing.ReceiptPolicy = entry.ReceiptPolicy;
                }
                else if (!existing.Baseline.Equals(entry.Baseline))
                {
                    // Disagreeing baselines become unknown, so no worsening can
                    // be measured against a guess.
                    existing.Baseline = FactObservation.Empty;
                }
                // A pause anywhere wins; a spent receipt stays spent.
                existing.Pause = existing.Pause != PauseCause.None ? existing.Pause : entry.Pause;
                existing.ReceiptConsumed |= entry.ReceiptConsumed;
                existing.ReceiptClosed |= entry.ReceiptClosed;
                existing.DeliveryEvidenceSupported &= entry.DeliveryEvidenceSupported;
                foreach (ContinuityAssertionRecord a in entry.Assertions)
                {
                    ContinuityAssertionRecord? have = existing.Assertions.FirstOrDefault(x => x.Root == a.Root);
                    if (have == null) existing.Assertions.Add(a);
                    else { have.Covered |= a.Covered; have.Reviewed |= a.Reviewed; }
                }
                foreach (EpisodeId r in entry.SupersededRoots) if (!existing.SupersededRoots.Contains(r)) existing.SupersededRoots.Add(r);
                PutContinuityLocked(existing);
                return;
            }

            // Different occurrences for one key: established succession
            // decides, and nothing else does.
            if (entry.Root != null && existing.Root != null)
            {
                bool entrySupersedes = entry.SupersededRoots.Contains(existing.Root.Value)
                                       && existing.SeenRoots.All(r => r == entry.Root || entry.SupersededRoots.Contains(r));
                bool existingSupersedes = existing.SupersededRoots.Contains(entry.Root.Value);
                if (entrySupersedes)
                {
                    foreach (EpisodeId r in existing.SeenRoots) if (!entry.SeenRoots.Contains(r)) entry.SeenRoots.Add(r);
                    if (existing.Root is EpisodeId gone) PruneLineageLocked(gone);
                    PutContinuityLocked(entry);
                    return;
                }
                if (existingSupersedes)
                {
                    if (!existing.SeenRoots.Contains(entry.Root.Value)) existing.SeenRoots.Add(entry.Root.Value);
                    return;
                }
            }

            existing.Conflicted = true;
            existing.DeliveryEvidenceSupported = false;
            existing.Assertions.Clear();
            existing.Pause = existing.Pause != PauseCause.None ? existing.Pause : entry.Pause;
            if (!existing.Baseline.Equals(entry.Baseline)) existing.Baseline = FactObservation.Empty;
            if (entry.Root is EpisodeId other && !existing.SeenRoots.Contains(other)) existing.SeenRoots.Add(other);
            NoteIssueLocked(IssueKind.IdentityConflict, "continuity:" + entry.Key, entry.Radio + " / " + entry.Condition,
                "two saved records disagree about which occurrence this condition continues; a reconnect cannot continue either",
                1, ExtentCertainty.Exact, entry.Radio + " / " + entry.Condition,
                "cont-conflict:" + entry.Key + ":" + entry.Root, persist: false);
        }

        /// <summary>
        /// The same problem observed twice — by two writers, or by this
        /// loader and a saved copy of its own earlier observation. Three
        /// cases, and only two of them keep an exact count:
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The same information:</b> equal, non-empty deduplication keys
        /// and an equal count are one observation described twice. Nothing
        /// changes, and a review of either copy was a review of it — the
        /// loader re-noting a migration gap it saved as reviewed must not
        /// un-review it.
        /// </para>
        /// <para>
        /// <b>Complete evidence on both sides:</b> every observation keyed,
        /// none overflowed, both exact — so the union of keys IS the set of
        /// distinct observations, and its size is the count.
        /// </para>
        /// <para>
        /// <b>Anything else is a lower bound.</b> Two writers that each saw
        /// ten losses and kept four keys apiece did not see the same ten; the
        /// larger count is all that can be said, and it is said as at least.
        /// A merge never marks unreviewed information reviewed.
        /// </para>
        /// </remarks>
        private void MergeIssueLocked(IssueRecord incoming)
        {
            incoming.FromPreviousProcess = true;
            string key = (int)incoming.Kind + "|" + incoming.SourceKey;
            if (!_issues.TryGetValue(key, out IssueRecord? existing))
            {
                if (_issues.Count >= FactStoreCapacity.MaxIssues)
                {
                    NoteIssueLocked(incoming.Kind, incoming.SourceKey, incoming.Source, incoming.Reason,
                                    Math.Max(1, incoming.Count), incoming.Extent, null, null, persist: false, state: incoming.State);
                    return;
                }
                incoming.Id = Checked(ref _nextIssue);
                _issues[key] = incoming;
                return;
            }

            bool existingReviewed = existing.ReviewedRevision >= existing.Revision && existing.ReviewedRevision > 0;
            bool incomingReviewed = incoming.ReviewedRevision >= incoming.Revision && incoming.ReviewedRevision > 0;
            bool sameInformation = existing.Seen.Count > 0 && incoming.Seen.SetEquals(existing.Seen)
                                   && incoming.Count == existing.Count && !existing.SeenOverflowed && !incoming.SeenOverflowed;

            if (sameInformation)
            {
                if (incoming.Extent == ExtentCertainty.Unknown) existing.Extent = ExtentCertainty.Unknown;
                else if (incoming.Extent == ExtentCertainty.LowerBound && existing.Extent == ExtentCertainty.Exact) existing.Extent = ExtentCertainty.LowerBound;
                existing.Revision = Math.Max(existing.Revision, incoming.Revision);
                existing.ReviewedRevision = existingReviewed || incomingReviewed ? existing.Revision : Math.Min(existing.ReviewedRevision, incoming.ReviewedRevision);
                if (incoming.State == IssueState.Active) existing.State = IssueState.Active;
                else if (incoming.State == IssueState.Limitation && existing.State == IssueState.ResolvedWithHistory) existing.State = IssueState.Limitation;
                foreach (string e in incoming.Exemplars) if (!existing.Exemplars.Contains(e)) AddExemplar(existing, e);
                return;
            }

            bool existingComplete = existing.Extent == ExtentCertainty.Exact && !existing.SeenOverflowed
                                    && existing.Seen.Count == existing.Count;
            bool incomingComplete = incoming.Extent == ExtentCertainty.Exact && !incoming.SeenOverflowed
                                    && incoming.Seen.Count == incoming.Count;

            existing.Seen.UnionWith(incoming.Seen);
            if (incoming.SeenOverflowed) existing.SeenOverflowed = true;
            bool bothReviewed = existingReviewed && incomingReviewed;

            if (existingComplete && incomingComplete)
            {
                existing.Count = existing.Seen.Count;
                existing.Extent = ExtentCertainty.Exact;
            }
            else
            {
                existing.Count = Math.Max(Math.Max(existing.Count, incoming.Count), existing.Seen.Count);
                existing.Extent = existing.Extent == ExtentCertainty.Unknown || incoming.Extent == ExtentCertainty.Unknown
                    ? ExtentCertainty.Unknown
                    : ExtentCertainty.LowerBound;
            }

            // Different information, combined: new to whoever reviewed only
            // one side of it.
            existing.Revision = Math.Max(existing.Revision, incoming.Revision) + 1;
            existing.ReviewedRevision = bothReviewed ? existing.Revision - 1 : Math.Min(existing.ReviewedRevision, incoming.ReviewedRevision);
            if (incoming.State == IssueState.Active) existing.State = IssueState.Active;
            else if (incoming.State == IssueState.Limitation && existing.State == IssueState.ResolvedWithHistory) existing.State = IssueState.Limitation;
            foreach (string e in incoming.Exemplars) if (!existing.Exemplars.Contains(e)) AddExemplar(existing, e);
        }
    }
}
