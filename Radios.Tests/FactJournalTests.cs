#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Radios;
using Radios.Facts;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// C-series: history survives a restart with its identity and evidence,
    /// and without any of its authority.
    /// </summary>
    /// <remarks>
    /// Every journal here lives in an absolute temporary directory. Nothing
    /// touches the real settings folder. Every test reads ACTUAL journal bytes
    /// back through the production loader into a fresh store — a round trip
    /// through an in-memory helper would prove nothing about the file.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class FactJournalTests
    {
        private static readonly DateTime T0 = FactKit.T0;

        private static void Save(FactKit kit, string dir)
        {
            using var journal = new FactJournal(kit.Store, dir);
            Assert.True(journal.TakeLease());
            Assert.True(journal.Write());
        }

        private static (FactKit Kit, LoadReport Report) Load(string dir)
        {
            var kit = new FactKit();
            var journal = new FactJournal(kit.Store, dir);
            Assert.True(journal.TakeLease());
            LoadReport report = journal.LoadHistory();
            journal.Dispose();
            return (kit, report);
        }

        [Fact]
        public void C1_TwoReleasedWritersRestoreWithoutAliasing()
        {
            using var dir = new TempFactDir();

            // Two legitimate writers with IDENTICAL local session, slot and
            // occurrence values.
            var a = new FactKit();
            var b = new FactKit();
            var label = new OpenOptions { OccurrenceLabel = "occ-1" };
            EpisodeId idA = FactKit.OpenHot(a.HotSlot(a.Session("SERIAL-1")), 70m, options: label).Handle!.Id;
            EpisodeId idB = FactKit.OpenHot(b.HotSlot(b.Session("SERIAL-1")), 70m, options: label).Handle!.Id;
            Assert.Equal(idA.Ordinal, idB.Ordinal);   // the local counters really are equal
            Save(a, dir.Path);
            Save(b, dir.Path);
            Assert.Equal(2, dir.Shards().Length);

            var (fresh, report) = Load(dir.Path);

            Assert.Equal(2, report.Counts.Accepted);
            Assert.Equal(0, report.Counts.Duplicates);
            Assert.Equal(0, report.Counts.Conflicts);
            FactSnapshot restoredA = fresh.Store.Find(idA)!;
            FactSnapshot restoredB = fresh.Store.Find(idB)!;
            Assert.NotEqual(restoredA.Id, restoredB.Id);
            Assert.Equal(a.Store.WriterIncarnation, restoredA.Id.Writer);   // origin identity, not the loader's
            Assert.Equal(b.Store.WriterIncarnation, restoredB.Id.Writer);

            // Both are selectable history in the projection the surface uses.
            using FactListView view = new FactListPresenter(fresh.Store).OpenView();
            FactListSnapshot list = view.Snapshot(FactView.Pending);
            Assert.Contains(list.Items, i => i.Fact?.Id == idA);
            Assert.Contains(list.Items, i => i.Fact?.Id == idB);
            Assert.NotNull(view.RenderDetail(list.Items.First(i => i.Fact?.Id == idA)));

            // Loading again is idempotent: duplicates are counted as such,
            // never as a second occurrence and never as silent success.
            var again = new FactJournal(fresh.Store, dir.Path);
            LoadReport second = again.LoadHistory();
            again.Dispose();
            Assert.Equal(0, second.Counts.Accepted);
            Assert.Equal(2, second.Counts.Duplicates);
            Assert.Equal(2, fresh.Store.All.Count);

            // POSITIVE one-record control: one writer alone loads one record.
            using var solo = new TempFactDir();
            Save(a, solo.Path);
            var (only, soloReport) = Load(solo.Path);
            Assert.Equal(1, soloReport.Counts.Accepted);
            Assert.Single(only.Store.All);
        }

        [Fact]
        public void C2_RoundTripPreservesHistoricalMeaning()
        {
            using var dir = new TempFactDir();
            var kit = new FactKit();
            FactSession session = kit.Session("SERIAL-1");
            var tracked = new RecordingTransport(kit.Registry, "tracked",
                TransportCapability.ReportsCompletion | TransportCapability.ReportsProgress | TransportCapability.ReportsAcceptance);
            var plain = new RecordingTransport(kit.Registry, "plain", TransportCapability.RequestOnly);
            int tones = 0;
            var receipts = new ReceiptRequestAdapter(kit.Registry.RegisterReceiptAdapter("r"), _ => { tones++; return ToneRequestResult.PlaybackReported; });

            // Fully covered.
            EpisodeId full = FactKit.OpenHot(kit.HotSlot(session, "full")).Handle!.Id;
            AttemptHandle fa = kit.Allocate(kit.PlanAutomatic(full), tracked.Binding);
            AttemptRunner.Run(fa, tracked.Submit);
            fa.Report(TransportEvidence.Completed(5));
            receipts.RequestFor(full);

            // Short tier completed: the duration stays owed.
            EpisodeId shortTier = FactKit.OpenHot(kit.HotSlot(session, "short")).Handle!.Id;
            AttemptHandle sa = kit.Allocate(kit.PlanAutomatic(shortTier, VerbosityLevel.Terse), tracked.Binding);
            AttemptRunner.Run(sa, tracked.Submit);
            sa.Report(TransportEvidence.Completed(5));

            // Partial, requested-only, unknown-cancelled.
            EpisodeId partial = FactKit.OpenHot(kit.HotSlot(session, "partial")).Handle!.Id;
            AttemptHandle pa = kit.Allocate(kit.PlanAutomatic(partial), tracked.Binding);
            AttemptRunner.Run(pa, tracked.Submit);
            pa.Report(TransportEvidence.Progress(5, new[] { "temperature" }));

            EpisodeId requested = FactKit.OpenHot(kit.HotSlot(session, "requested")).Handle!.Id;
            AttemptRunner.Run(kit.Allocate(kit.PlanAutomatic(requested), plain.Binding), plain.Submit);

            EpisodeId cancelled = FactKit.OpenHot(kit.HotSlot(session, "cancelled")).Handle!.Id;
            AttemptHandle ca = kit.Allocate(kit.PlanAutomatic(cancelled), tracked.Binding);
            AttemptRunner.Run(ca, tracked.Submit);
            ca.Report(TransportEvidence.Cancelled(5, CancelCause.Unknown));

            // Reviewed-only, through a displayed snapshot.
            EpisodeId reviewed = FactKit.OpenHot(kit.HotSlot(session, "reviewed")).Handle!.Id;
            using (FactListView view = new FactListPresenter(kit.Store).OpenView())
            {
                RenderedDetailSnapshot d = view.RenderDetail(view.Snapshot(FactView.Pending).Items.First(i => i.Fact?.Id == reviewed))!;
                view.Installed(d);
                Assert.Equal(ReviewOutcome.Reviewed, view.Review(d.Token));
            }

            // Perishable, forgettable, unclassified, truncated.
            SlotPublisher notes = kit.NotesSlot(session);
            EpisodeId perishable = FactKit.OpenNote(notes, FactKit.CutKey).Handle!.Id;
            EpisodeId forgettable = FactKit.OpenNote(kit.NotesSlot(session, "n2"), FactKit.BriefKey).Handle!.Id;
            EpisodeId unclassified = FactKit.OpenNote(kit.NotesSlot(session, "n3"), FactKit.MysteryKey).Handle!.Id;
            EpisodeId truncated = FactKit.OpenNote(kit.NotesSlot(session, "n4"), FactKit.CutKey,
                detail: new string('x', FactStoreCapacity.MaxDetailBytes + 50)).Handle!.Id;

            kit.Registry.Quiet.Observe("ctrl");
            var before = kit.Store.All.ToDictionary(f => f.Id);
            Save(kit, dir.Path);

            var (fresh, report) = Load(dir.Path);
            Assert.Equal(before.Count, report.Counts.Accepted);
            var claimer = fresh.Registry.RegisterReceiptAdapter("r");

            foreach (FactSnapshot was in before.Values)
            {
                FactSnapshot now = fresh.Store.Find(was.Id)!;
                Assert.True(now.RestoredFromDisk);
                Assert.False(now.IsLive);
                Assert.Equal(was.MessageKey, now.MessageKey);
                Assert.Equal(was.Classification, now.Classification);
                Assert.Equal(was.Delivery, now.Delivery);
                Assert.Equal(was.Claim, now.Claim);
                Assert.Equal(was.OwnerName, now.OwnerName);
                Assert.Equal(was.RadioIdentity, now.RadioIdentity);
                Assert.Equal(was.Materials.Select(m => m.Id + m.Name + m.Value.Invariant),
                             now.Materials.Select(m => m.Id + m.Name + m.Value.Invariant));
                Assert.Equal(was.Covered.OrderBy(x => x), now.Covered.OrderBy(x => x));
                Assert.Equal(was.Reviewed.OrderBy(x => x), now.Reviewed.OrderBy(x => x));
                Assert.Equal(was.Unpresented.OrderBy(x => x), now.Unpresented.OrderBy(x => x));
                Assert.Equal(was.IsPending, now.IsPending);
                Assert.Equal(was.Receipt.State, now.Receipt.State);
                Assert.Equal(was.Receipt.AllowanceConsumed, now.Receipt.AllowanceConsumed);
                Assert.True(now.Receipt.FromPreviousProcess);
                // An attempt still open when the image was taken loads as
                // interrupted-session evidence, completion unconfirmed. A
                // request-only attempt is open by definition until closed.
                Assert.Equal(was.Attempts.Select(x => x.Disposition is AttemptDisposition.RequestedOnly
                                 or AttemptDisposition.InFlight or AttemptDisposition.Allocated
                                     ? AttemptDisposition.Interrupted : x.Disposition),
                             now.Attempts.Select(x => x.Disposition));
                Assert.Equal(was.Detail, now.Detail);
                Assert.Equal(was.DetailTruncated, now.DetailTruncated);
                Assert.Equal(was.Pause == PauseCause.None ? PauseCause.NoLivePermission : was.Pause,
                             now.Pause == PauseCause.None ? PauseCause.NoLivePermission : now.Pause);

                // No restored automatic permission, and no receipt permit.
                Assert.False(fresh.Store.IsEligibleForAutomaticDelivery(now));
                Assert.NotEqual(ReceiptClaimOutcome.Granted, claimer.TryClaim(now.Id).Outcome);
            }

            // The specific meanings the old loader destroyed.
            Assert.False(fresh.Store.Find(full)!.IsPending);           // genuine full coverage stays discharged
            Assert.True(fresh.Store.Find(full)!.PresentationComplete);
            Assert.False(fresh.Store.Find(reviewed)!.IsPending);        // genuine shown review stays discharged
            Assert.True(fresh.Store.Find(reviewed)!.DischargedByReviewOnly);
            Assert.True(fresh.Store.Find(shortTier)!.IsPending);        // a short sentence left the duration owed
            Assert.True(fresh.Store.Find(requested)!.IsPending);        // requested-only proves nothing
            Assert.True(fresh.Store.Find(partial)!.IsPending);
            Assert.Equal(PauseCause.UnknownCancellation, fresh.Store.Find(cancelled)!.Pause);
            Assert.Equal(PauseCause.OperatorQuiet, fresh.Store.Find(requested)!.Pause);
            Assert.Equal(ReceiptState.PlaybackReported, fresh.Store.Find(full)!.Receipt.State);
            Assert.Equal(DeliveryClassification.Unclassified, fresh.Store.Find(unclassified)!.Classification);
            Assert.False(fresh.Store.Find(forgettable)!.IsPending);     // a forgettable miss is not retained debt
            Assert.True(fresh.Store.Find(truncated)!.DetailTruncated);
            Assert.Contains(fresh.Store.History(), f => f.Id == perishable);   // perishable history stays reachable
        }

        [Fact]
        public void C3_DuplicateSuccessorAndConflictHaveDifferentVisibleResults()
        {
            using var dir = new TempFactDir();
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session("SERIAL-1"));
            PublicationResult opened = FactKit.OpenHot(publisher);
            EpisodeId id = opened.Handle!.Id;

            var journal = new FactJournal(kit.Store, dir.Path);
            Assert.True(journal.TakeLease());
            Assert.True(journal.Write());                                  // generation 1
            string gen1 = File.ReadAllText(journal.ShardPath);
            publisher.Update(opened.Handle, FactKit.Capture(publisher, FactKit.Temp(71m)), FactTransition.Sample(), opened.Fact!.Revision);
            Assert.True(journal.Write());                                  // generation 2
            string gen2 = File.ReadAllText(journal.ShardPath);
            journal.Dispose();

            // An exact copy of the newest file, and an older copy.
            File.WriteAllText(Path.Combine(dir.Path, "facts-copy.json"), gen2);
            File.WriteAllText(Path.Combine(dir.Path, "facts-older.json"), gen1);

            var (fresh, report) = Load(dir.Path);
            Assert.Equal(1, report.Counts.Accepted);
            Assert.Equal(1, report.Counts.Duplicates);
            Assert.Equal(1, report.Counts.Superseded);
            Assert.Equal(0, report.Counts.Conflicts);
            Assert.Single(fresh.Store.All);
            Assert.Equal(kit.Store.Find(id)!.Revision, fresh.Store.Find(id)!.Revision);   // the successor was selected
            Assert.DoesNotContain(fresh.Store.Issues, i => i.Kind == IssueKind.IdentityConflict);

            // Same identity, same generation, different content: a conflict.
            Assert.Contains("\"detail\": \"\"", gen2);
            File.WriteAllText(Path.Combine(dir.Path, "facts-tampered.json"),
                gen2.Replace("\"detail\": \"\"", "\"detail\": \"a different account\""));
            var (conflicted, conflictReport) = Load(dir.Path);
            Assert.Equal(1, conflictReport.Counts.Conflicts);

            // Both variants kept, neither discharging the other, under a row.
            IReadOnlyList<FactSnapshot> variants = conflicted.Store.Variants(id);
            Assert.Equal(2, variants.Count);
            Assert.All(variants, v => Assert.True(v.ConflictVariant));
            StoreIssueSnapshot issue = Assert.Single(conflicted.Store.Issues, i => i.Kind == IssueKind.IdentityConflict);

            using FactListView view = new FactListPresenter(conflicted.Store).OpenView();
            FactListSnapshot list = view.Snapshot(FactView.Pending);
            ItemSnapshot row = Assert.Single(list.Items, i => i.Issue?.Id == issue.Id);
            RenderedDetailSnapshot detail = view.RenderDetail(row)!;
            Assert.False(string.IsNullOrWhiteSpace(detail.Text));
            Assert.False(Lexicon.LooksLikeKey(detail.Text.Split(Environment.NewLine)[0]));
            Assert.Equal(2, list.Items.Count(i => i.Fact?.Id == id));
        }

        [Fact]
        public void C4_LegacyAndRecoveryDoNotInventEvidence()
        {
            using var dir = new TempFactDir();

            // A real schema-1 shape, as Track M wrote it.
            const string legacy = """
            {
              "schema": 1,
              "processIncarnation": 42,
              "facts": [
                { "episode": "42:7:transmit.reflected:occ-1", "connection": 7, "radio": "SERIAL-L", "slot": "transmit.reflected",
                  "occurrence": "occ-1", "key": "syn.hot", "classification": "Message", "shelfLife": "Persistent",
                  "validityContract": "syn.active", "historyKey": "syn.hot.history", "receiptPolicy": "Warning",
                  "validity": "Current", "observedUtc": "2026-09-24T12:00:00.0000000Z", "revision": 3,
                  "materialRevision": 1, "reviewedMaterialRevision": 1, "receipt": "Requested", "paused": false,
                  "undelivered": true, "attempts": 3, "detail": "reviewed one", "detailTruncated": false },
                { "episode": "42:7:transmit.reflected:occ-2", "connection": 7, "radio": "SERIAL-L", "slot": "transmit.reflected",
                  "occurrence": "occ-2", "key": "syn.hot", "classification": "Unclassified",
                  "validity": "Current", "observedUtc": "2026-09-24T12:01:00.0000000Z", "revision": 1,
                  "materialRevision": 1, "reviewedMaterialRevision": 0, "receipt": "NotRequested", "paused": true,
                  "undelivered": true, "attempts": 0, "detail": "paused one", "detailTruncated": false }
              ],
              "overflow": { "lost": 0, "saturated": false }
            }
            """;
            File.WriteAllText(Path.Combine(dir.Path, "facts-legacy.json"), legacy);

            var (first, report) = Load(dir.Path);
            Assert.Contains(report.Sources, s => s.Status == SourceStatus.Legacy && s.Records == 2);
            Assert.Contains(first.Store.Issues, i => i.Kind == IssueKind.MigrationGap);

            FactSnapshot reviewedOne = first.Store.All.Single(f => f.Detail == "reviewed one");
            FactSnapshot pausedOne = first.Store.All.Single(f => f.Detail == "paused one");
            Assert.All(new[] { reviewedOne, pausedOne }, f =>
            {
                Assert.True(f.Legacy);
                Assert.Equal(EpisodeOrigin.Legacy, f.Id.Origin);
                Assert.Empty(f.Attempts);                       // no attempt rows manufactured
                Assert.False(f.PresentationComplete);           // an aggregate claim is not verified coverage
                Assert.False(f.Validity.IsCurrent);             // a saved "Current" is not current now
            });
            Assert.Equal(3, reviewedOne.CompactedAttempts);
            Assert.Equal(ReceiptState.Requested, reviewedOne.Receipt.State);
            Assert.True(reviewedOne.Receipt.FromPreviousProcess);
            Assert.False(reviewedOne.IsPending);                // its saved review is preserved
            Assert.True(pausedOne.IsPending);                    // unverified, unreviewed: still owed
            Assert.Equal(PauseCause.LegacyUnknownCause, pausedOne.Pause);

            // Stable attribution across loads — never process zero, never the loader.
            var (second, _) = Load(dir.Path);
            Assert.Equal(first.Store.All.Select(f => f.Id).OrderBy(i => i.Ordinal),
                         second.Store.All.Select(f => f.Id).OrderBy(i => i.Ordinal));

            // Recovery: a corrupt primary with a valid last-good.
            using var rec = new TempFactDir();
            var writer = new FactKit();
            SlotPublisher p = writer.HotSlot(writer.Session("SERIAL-R"));
            PublicationResult o = FactKit.OpenHot(p);
            var j = new FactJournal(writer.Store, rec.Path);
            j.TakeLease();
            j.Write();
            p.Update(o.Handle!, FactKit.Capture(p, FactKit.Temp(71m)), FactTransition.Sample(), o.Fact!.Revision);
            j.Write();
            string primary = j.ShardPath;
            j.Dispose();
            File.WriteAllText(primary, "{ \"schema\": 2, \"writer\": \"torn");

            // An unsupported schema, an unreadable source, and a live shard.
            File.WriteAllText(Path.Combine(rec.Path, "facts-future.json"), "{ \"schema\": 99, \"facts\": [] }");
            string lockedPath = Path.Combine(rec.Path, "facts-unreadable.json");
            File.WriteAllText(lockedPath, "{}");
            using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var live = new FactKit();
            FactKit.OpenHot(live.HotSlot(live.Session("SERIAL-LIVE")));
            using var holder = new FactJournal(live.Store, rec.Path);
            holder.TakeLease();
            holder.Write();

            var (recovered, recReport) = Load(rec.Path);
            Assert.Contains(recReport.Sources, s => s.Status == SourceStatus.RecoveredFromLastGood && s.Records == 1);
            Assert.Contains(recReport.Sources, s => s.Status == SourceStatus.Unsupported);
            Assert.Contains(recReport.Sources, s => s.Status == SourceStatus.Inaccessible);
            Assert.Contains(recReport.Sources, s => s.Status == SourceStatus.SkippedLive);
            Assert.True(recReport.Partial);
            Assert.Single(recovered.Store.All);                                  // the last-good record, usable history
            Assert.Contains(recovered.Store.Issues, i => i.Kind == IssueKind.RecoveryGap);
            Assert.Contains(recovered.Store.Issues, i => i.Kind == IssueKind.IncompleteInventory && i.SourceKey.StartsWith("live:", StringComparison.Ordinal));
            Assert.Contains(recovered.Store.Issues, i => i.Kind == IssueKind.IncompleteInventory && i.SourceKey.StartsWith("unsupported:", StringComparison.Ordinal));
            Assert.False(recovered.Store.Project(FactView.Pending, null).Predicates.CompleteInventory);

            // POSITIVE CONTROL: a clean source loads normally with no issues.
            using var clean = new TempFactDir();
            Save(writer, clean.Path);
            var (ok, okReport) = Load(clean.Path);
            Assert.All(okReport.Sources, s => Assert.Equal(SourceStatus.Loaded, s.Status));
            Assert.Empty(ok.Store.Issues);
            Assert.True(ok.Store.Project(FactView.Pending, null).Predicates.CompleteInventory);
        }

        [Fact]
        public void C5_SnapshotCommitAcknowledgesOnlyItsRevision()
        {
            using var dir = new TempFactDir();
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session("SERIAL-1"));
            PublicationResult opened = FactKit.OpenHot(publisher);

            var journal = new FactJournal(kit.Store, dir.Path);
            Assert.True(journal.TakeLease());

            // Snapshot N is captured; the store moves to N+1 before N commits.
            StoreImage imageN = kit.Store.CaptureImage();
            long n = imageN.Mutation;
            publisher.Update(opened.Handle!, FactKit.Capture(publisher, FactKit.Temp(75m)), FactTransition.Sample(), opened.Fact!.Revision);
            Assert.True(kit.Store.MutationSequence > n);
            Assert.True(journal.Write(imageN));

            Assert.Equal(n, kit.Store.PersistedThrough);
            Assert.False(kit.Store.Project(FactView.Pending, null).Predicates.SavedThroughCurrent);

            // The file really is a coherent N, not a mixture.
            var coherent = new FactKit();
            LoadedSource parsed = FactJournalFormat.Parse(File.ReadAllText(journal.ShardPath), "n", out List<string> rejected);
            Assert.Empty(rejected);
            Assert.Equal(opened.Fact.Revision, parsed.Facts.Single().Revision);

            // A later write catches up.
            Assert.True(journal.Write());
            Assert.Equal(kit.Store.MutationSequence, kit.Store.PersistedThrough);
            Assert.True(kit.Store.Project(FactView.Pending, null).Predicates.SavedThroughCurrent);
            journal.Dispose();

            // Without the lease: refused, and no false success.
            var unleased = new FactKit();
            FactKit.OpenHot(unleased.HotSlot(unleased.Session()));
            using var noLease = new FactJournal(unleased.Store, dir.Path);
            Assert.False(noLease.Write());
            Assert.Equal(0, unleased.Store.PersistedThrough);
            Assert.Contains(unleased.Store.Issues, i => i.Kind == IssueKind.PersistenceFailure);

            // Read while another writer holds its shard: partial, not success.
            using var held = new FactJournal(unleased.Store, dir.Path);
            Assert.True(held.TakeLease());
            Assert.True(held.Write());
            var (reader, report) = Load(dir.Path);
            Assert.Contains(report.Sources, s => s.Status == SourceStatus.SkippedLive);
            Assert.False(reader.Store.Project(FactView.Pending, null).Predicates.CompleteInventory);
        }

        [Fact]
        public void C6_CompactionAndImportedReviewSurviveAnotherRestart()
        {
            using var dir = new TempFactDir();

            // Attempt evidence beyond every per-fact and tombstone bound, with
            // confirmed coverage kept in the durable ledger.
            var busy = new FactKit();
            SlotPublisher hot = busy.HotSlot(busy.Session("SERIAL-BUSY"));
            // A third unit no wording carries keeps the fact owed and eligible
            // through every round.
            CapturedFactEvent opening = FactKit.Capture(hot, FactKit.Temp(70m));
            EpisodeId busyId = hot.Open(opening, "condition.hot", FactKit.HotKey, new[]
            {
                new MaterialDeclaration("temperature", FactValue.Of(70m)),
                new MaterialDeclaration("duration", FactValue.Of(3L)),
                new MaterialDeclaration("zone", FactValue.Of("north")),
            }).Handle!.Id;
            var tracked = new RecordingTransport(busy.Registry, "tracked",
                TransportCapability.ReportsCompletion | TransportCapability.ReportsProgress);

            // The FIRST attempt is the only one that ever carries the duration:
            // attributable progress for exactly that clause.
            AttemptHandle first = busy.Allocate(busy.PlanAutomatic(busyId, VerbosityLevel.Chatty), tracked.Binding);
            AttemptRunner.Run(first, tracked.Submit);
            first.Report(TransportEvidence.Progress(5, new[] { "duration" }));
            first.Report(TransportEvidence.CompletionUnobservable(6));   // closed, so it can be compacted

            // Then enough terse attempts to push it out of the per-fact bound
            // AND out of the tombstones, so only the durable ledger remembers.
            int rounds = FactStoreCapacity.MaxAttemptEvidence + FactStoreCapacity.MaxAttemptTombstones + 10;
            for (int i = 0; i < rounds; i++)
            {
                AttemptHandle a = busy.Allocate(busy.PlanAutomatic(busyId, VerbosityLevel.Terse), tracked.Binding);
                AttemptRunner.Run(a, tracked.Submit);
                a.Report(TransportEvidence.Completed(5));
            }
            FactSnapshot compacted = busy.Store.Find(busyId)!;
            Assert.True(compacted.Attempts.Count <= FactStoreCapacity.MaxAttemptEvidence);
            Assert.True(compacted.CompactedAttempts >= rounds - FactStoreCapacity.MaxAttemptEvidence);
            Assert.DoesNotContain(compacted.Attempts, a => a.Id == first.Id);
            MaterialUnit temperature = compacted.Materials.Single(m => m.Name == "temperature");
            MaterialUnit duration = compacted.Materials.Single(m => m.Name == "duration");
            MaterialUnit zone = compacted.Materials.Single(m => m.Name == "zone");
            Assert.Contains(temperature.Id, compacted.Covered);
            Assert.Contains(duration.Id, compacted.Covered);          // only the ledger still knows this
            Assert.Contains(zone.Id, compacted.Unpresented);

            // Writer A: an owed fact with more units than one detail shows.
            var a1 = new FactKit();
            SlotPublisher notes = a1.NotesSlot(a1.Session("SERIAL-A"));
            var many = Enumerable.Range(1, FactListPresenter.MaxDetailMaterialLines + 3)
                                 .Select(i => new MaterialDeclaration("extra" + i, FactValue.Of((long)i))).ToArray();
            EpisodeId owed = FactKit.OpenNote(notes, FactKit.CutKey, materials: many).Handle!.Id;
            Save(a1, dir.Path);
            Save(busy, dir.Path);

            // Writer B imports A's history and reviews it through a displayed snapshot.
            var b = new FactKit();
            var bj = new FactJournal(b.Store, dir.Path);
            Assert.True(bj.TakeLease());
            bj.LoadHistory();
            using (FactListView view = new FactListPresenter(b.Store).OpenView())
            {
                RenderedDetailSnapshot detail = view.RenderDetail(view.Snapshot(FactView.Pending).Items.First(i => i.Fact?.Id == owed))!;
                Assert.True(detail.OmittedRequiredDetail);
                view.Installed(detail);
                Assert.Equal(ReviewOutcome.Reviewed, view.Review(detail.Token));
            }
            Assert.True(bj.Write());
            bj.Dispose();

            // A third store loads both released origins.
            var (c, report) = Load(dir.Path);
            Assert.Equal(2, report.Counts.Accepted);            // A's fact and the busy fact; B copied nothing
            Assert.Equal(1, report.Counts.OverlaysApplied);
            Assert.Equal(2, c.Store.All.Count);                  // counts do not multiply

            FactSnapshot owedNow = c.Store.Find(owed)!;
            Assert.True(owedNow.IsPending);                      // the units the detail never showed stay owed
            Assert.Equal(3, owedNow.Unpresented.Count);
            Assert.True(owedNow.Reviewed.Count >= FactListPresenter.MaxDetailMaterialLines);

            FactSnapshot busyNow = c.Store.Find(busyId)!;
            Assert.Contains(temperature.Id, busyNow.Covered);
            Assert.Contains(duration.Id, busyNow.Covered);            // the compacted coverage survived a restart
            Assert.Contains(zone.Id, busyNow.Unpresented);
            Assert.True(busyNow.CompactedAttempts >= compacted.CompactedAttempts);
        }

        [Fact]
        public void FactJournalCapacity_TheFullestBoundedImageFitsTheDiskAllowance()
        {
            // The measurement MaxJournalBytes is sized from: every bound at
            // once. If this fails, a bound grew — change it deliberately.
            var kit = new FactKit();
            var transport = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion | TransportCapability.ReportsProgress);
            var extras = Enumerable.Range(1, FactStoreCapacity.MaxMaterialUnitsPerFact - 1)
                                   .Select(i => new MaterialDeclaration("m" + i, FactValue.Of(new string('v', 40)))).ToArray();
            string detail = new string('d', FactStoreCapacity.MaxDetailBytes);
            for (int f = 0; f < FactStoreCapacity.MaxHistoricalRecords; f++)
            {
                FactSession session = kit.Session("SERIAL-" + f);
                SlotPublisher p = kit.NotesSlot(session, "c" + f);
                PublicationResult opened = FactKit.OpenNote(p, FactKit.CutKey, detail, extras);
                Assert.Equal(PublicationOutcome.Accepted, opened.Outcome);
                if (f % 16 == 0)
                {
                    for (int a = 0; a < FactStoreCapacity.MaxAttemptEvidence; a++)
                    {
                        AttemptHandle h = kit.Allocate(kit.PlanAutomatic(opened.Handle!.Id), transport.Binding);
                        AttemptRunner.Run(h, transport.Submit);
                        h.Report(TransportEvidence.Progress(3, new[] { "core" }));
                    }
                }
                // Release the slot so registration capacity is not what this measures.
                session.End(FactKit.T0, "measured");
            }
            Assert.Equal(FactStoreCapacity.MaxHistoricalRecords, kit.Store.All.Count);
            string json = FactJournalFormat.Render(kit.Store.CaptureImage(), 1);
            int bytes = Encoding.UTF8.GetByteCount(json);
            Assert.True(bytes < FactStoreCapacity.MaxJournalBytes,
                "the fullest bounded image is " + bytes + " bytes, over the " + FactStoreCapacity.MaxJournalBytes + " allowance");
        }
    }
}
