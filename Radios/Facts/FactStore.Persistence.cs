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
        public List<FactRecord> Facts = new();
        public List<OverlayImage> Overlays = new();
        public List<ContinuityRecord> Continuity = new();
        public List<IssueRecord> Issues = new();
        public long CompactedCovered, CompactedReviewedOnly, CompactedForgettable;
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
                        if (record.ReviewedLocal.Count > 0)
                            image.Overlays.Add(new OverlayImage
                            {
                                Episode = record.Id,
                                ContentFingerprint = record.ContentFingerprint(),
                                Reviewed = record.ReviewedLocal.OrderBy(x => x).ToArray(),
                            });
                        continue;
                    }
                    image.Facts.Add(CloneForImage(record));
                    if (record.RadioIdentity != null) image.Continuity.Add(DeriveContinuity(record, false));
                }

                foreach (ContinuityRecord entry in _continuity.Values)
                    if (!image.Continuity.Any(c => c.Key == entry.Key)) image.Continuity.Add(entry);

                foreach (IssueRecord issue in AllIssuesLocked()) image.Issues.Add(CloneIssue(issue));
                return image;
            }
        }

        private FactRecord CloneForImage(FactRecord r)
        {
            (_, PauseCause pause) = FactPermission.Evaluate(r, r.Unpresented(), _latestQuiet);
            var c = new FactRecord
            {
                Id = r.Id, OwnerName = r.OwnerName, ContractName = r.ContractName, ContractRevision = r.ContractRevision,
                Condition = r.Condition, Claim = r.Claim, ScopeId = r.ScopeId, ScopeKind = r.ScopeKind,
                RadioIdentity = r.RadioIdentity, OccurrenceLabel = r.OccurrenceLabel, Priority = r.Priority,
                MessageKey = r.MessageKey, Classification = r.Classification, Delivery = r.Delivery,
                CatalogGeneration = r.CatalogGeneration, Validity = r.Validity, ObservedUtc = r.ObservedUtc,
                LastEventSequence = r.LastEventSequence, Revision = r.Revision, ObservationRevision = r.ObservationRevision,
                MaterialRevision = r.MaterialRevision, CurrentValues = r.CurrentValues, Baseline = r.Baseline,
                HistoryDropped = r.HistoryDropped, NextMaterialId = r.NextMaterialId,
                LegacyUnverifiedOwed = r.LegacyUnverifiedOwed, RestoredPause = pause,
                CompactedAttempts = r.CompactedAttempts, CompactedAttemptsLowerBound = r.CompactedAttemptsLowerBound,
                Detail = r.Detail, DetailTruncated = r.DetailTruncated,
                Receipt = new ReceiptRecord
                {
                    Policy = r.Receipt.Policy, State = r.Receipt.State, Consumed = r.Receipt.Consumed,
                    ReceiptId = r.Receipt.ReceiptId,
                },
            };
            c.Events.AddRange(r.Events.Select(e => new EventRecord
            {
                EventOrdinal = e.EventOrdinal, Sequence = e.Sequence, SourceEventId = e.SourceEventId,
                ObservedUtc = e.ObservedUtc, Effect = e.Effect,
            }));
            c.Materials.AddRange(r.Materials);
            c.CoveredLedger.UnionWith(r.Covered());
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

                        _compactedCovered = Saturate(_compactedCovered, source.CompactedCovered, out _);
                        _compactedReviewedOnly = Saturate(_compactedReviewedOnly, source.CompactedReviewedOnly, out _);
                        _compactedForgettableUnpresented = Saturate(_compactedForgettableUnpresented, source.CompactedForgettable, out _);
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

        private void MergeContinuityLocked(ContinuityRecord entry)
        {
            entry.FromPreviousProcess = true;
            if (_continuity.TryGetValue(entry.Key, out ContinuityRecord? existing))
            {
                // A pause anywhere wins; disagreeing baselines become unknown,
                // so no worsening can be measured against a guess.
                existing.Pause = existing.Pause != PauseCause.None ? existing.Pause : entry.Pause;
                if (!existing.Baseline.Equals(entry.Baseline)) existing.Baseline = FactObservation.Empty;
                return;
            }
            PutContinuityLocked(entry);
        }

        private void MergeIssueLocked(IssueRecord incoming)
        {
            incoming.FromPreviousProcess = true;
            string key = (int)incoming.Kind + "|" + incoming.SourceKey;
            if (!_issues.TryGetValue(key, out IssueRecord? existing))
            {
                if (_issues.Count >= FactStoreCapacity.MaxIssues)
                {
                    NoteIssueLocked(incoming.Kind, incoming.SourceKey, incoming.Source, incoming.Reason,
                                    Math.Max(1, incoming.Count), incoming.Extent, null, null, persist: false);
                    return;
                }
                incoming.Id = Checked(ref _nextIssue);
                _issues[key] = incoming;
                return;
            }

            int before = existing.Seen.Count;
            existing.Seen.UnionWith(incoming.Seen);
            bool grew = existing.Seen.Count > before || incoming.Count > existing.Count;
            bool bothReviewed = existing.ReviewedRevision >= existing.Revision && incoming.ReviewedRevision >= incoming.Revision;
            existing.Count = Math.Max(Math.Max(existing.Count, incoming.Count), existing.Seen.Count);
            if (incoming.Extent != ExtentCertainty.Exact || incoming.SeenOverflowed) existing.Extent = ExtentCertainty.LowerBound;
            existing.Revision = Math.Max(existing.Revision, incoming.Revision) + (grew ? 1 : 0);
            // Reviewed only if every copy had been reviewed through its own
            // information. A merge never marks unreviewed information reviewed.
            existing.ReviewedRevision = bothReviewed && !grew ? existing.Revision : Math.Min(existing.ReviewedRevision, incoming.ReviewedRevision);
            if (incoming.State == IssueState.Active) existing.State = IssueState.Active;
            foreach (string e in incoming.Exemplars) if (!existing.Exemplars.Contains(e)) AddExemplar(existing, e);
        }
    }
}
