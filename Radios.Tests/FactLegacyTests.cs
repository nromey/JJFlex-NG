#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Radios;
using Radios.Facts;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// G4: a schema-1 record that claimed delivery stays pending as
    /// unverified. The old aggregate flag is kept as the writer's claim — it
    /// never decides pending membership, never becomes coverage, and never
    /// invents an attempt. The one accepted discharge is the old format's own
    /// saved review, which is the operator's instruction rather than a claim
    /// about what was heard.
    /// </summary>
    /// <remarks>
    /// The schema-1 fixtures are the shape Track M wrote. They are loaded
    /// through the production loader into a fresh store and inspected through
    /// the same projection and detail path the dialog uses. Nothing here
    /// proves what the old build actually said to anyone.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class FactLegacyTests
    {
        private static (FactKit Kit, LoadReport Report) Load(string dir)
        {
            var kit = new FactKit();
            var journal = new FactJournal(kit.Store, dir);
            Assert.True(journal.TakeLease());
            LoadReport report = journal.LoadHistory();
            journal.Dispose();
            return (kit, report);
        }

        private static string Record(string occurrence, string undelivered, long reviewed, string shelfLife = "Persistent",
                                     string classification = "Message", string detail = "detail", string receipt = "Requested")
        {
            string life = shelfLife.Length == 0 ? "" : "\"shelfLife\": \"" + shelfLife + "\", \"validityContract\": \"syn.active\", \"historyKey\": \"syn.hot.history\", \"receiptPolicy\": \"Warning\",";
            string flag = undelivered.Length == 0 ? "" : "\"undelivered\": " + undelivered + ",";
            return "{ \"episode\": \"42:7:transmit.reflected:" + occurrence + "\", \"connection\": 7, \"radio\": \"SERIAL-L\", \"slot\": \"transmit.reflected\", "
                   + "\"occurrence\": \"" + occurrence + "\", \"key\": \"syn.hot\", \"classification\": \"" + classification + "\", " + life
                   + " \"validity\": \"Current\", \"observedUtc\": \"2026-09-24T12:00:00.0000000Z\", \"revision\": 1, "
                   + "\"materialRevision\": 1, \"reviewedMaterialRevision\": " + reviewed + ", \"receipt\": \"" + receipt + "\", \"paused\": false, "
                   + flag + " \"attempts\": 2, \"detail\": \"" + detail + "\", \"detailTruncated\": false }";
        }

        private static string Legacy(params string[] records) =>
            "{ \"schema\": 1, \"processIncarnation\": 42, \"facts\": [ " + string.Join(", ", records) + " ], \"overflow\": { \"lost\": 0, \"saturated\": false } }";

        private static FactSnapshot ByDetail(FactStore store, string detail) => store.All.Single(f => f.Detail.StartsWith(detail, StringComparison.Ordinal));

        [Fact]
        public void ClaimedLegacyDeliveryWithoutReviewIsPendingAndHistorical()
        {
            using var dir = new TempFactDir();
            File.WriteAllText(Path.Combine(dir.Path, "facts-legacy.json"), Legacy(
                Record("claimed", "false", 0, detail: "claimed delivered"),
                Record("undelivered", "true", 0, detail: "claimed undelivered"),
                Record("absent", "", 0, detail: "no claim at all"),
                Record("brief", "false", 0, shelfLife: "Forgettable", detail: "forgettable"),
                Record("reviewed", "false", 1, detail: "saved review")));

            var (kit, report) = Load(dir.Path);
            Assert.Contains(report.Sources, s => s.Status == SourceStatus.Legacy && s.Records == 5);
            int tones = 0;
            var receipts = new ReceiptRequestAdapter(kit.Registry.RegisterReceiptAdapter("r"), _ => { tones++; return ToneRequestResult.Requested; });
            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);

            using FactListView view = new FactListPresenter(kit.Store).OpenView();
            FactListSnapshot pending = view.Snapshot(FactView.Pending);
            FactListSnapshot history = view.Snapshot(FactView.History);

            // The claimed-delivered record: pending AND historical, unverified.
            FactSnapshot claimed = ByDetail(kit.Store, "claimed delivered");
            Assert.True(claimed.Legacy);
            Assert.Equal(LegacyDeliveryClaim.ClaimedDelivered, claimed.LegacyClaim);
            Assert.True(claimed.IsPending);
            Assert.True(claimed.IsHistorical);
            Assert.Contains(pending.Items, i => i.Fact?.Id == claimed.Id);
            Assert.Contains(history.Items, i => i.Fact?.Id == claimed.Id);
            Assert.Empty(claimed.Attempts);                                 // no attempts invented
            Assert.Equal(2, claimed.CompactedAttempts);                     // the old count, kept as the old count
            Assert.False(claimed.PresentationComplete);
            Assert.True(claimed.LegacyUnverifiedCoverage);
            Assert.Empty(claimed.Covered);
            Assert.Equal(ReceiptState.Requested, claimed.Receipt.State);    // a request, not completion or hearing
            Assert.True(claimed.Receipt.FromPreviousProcess);
            Assert.Equal("facts.delivery.legacy_claimed_delivered", FactListPresenter.DeliveryRole(claimed));
            RenderedDetailSnapshot detail = view.RenderDetail(pending.Items.Single(i => i.Fact?.Id == claimed.Id))!;
            Assert.Contains("claimed delivered", detail.Text, StringComparison.Ordinal);
            Assert.Contains(Lexicon.Get("facts.detail.legacy_claimed"), detail.Text, StringComparison.Ordinal);
            Assert.True(detail.Historical);

            // Claimed undelivered and absent: also pending, each with its own claim.
            FactSnapshot undelivered = ByDetail(kit.Store, "claimed undelivered");
            Assert.True(undelivered.IsPending);
            Assert.Equal(LegacyDeliveryClaim.ClaimedUndelivered, undelivered.LegacyClaim);
            Assert.Equal("facts.delivery.legacy_claimed_undelivered", FactListPresenter.DeliveryRole(undelivered));
            FactSnapshot absent = ByDetail(kit.Store, "no claim at all");
            Assert.True(absent.IsPending);
            Assert.Equal(LegacyDeliveryClaim.Absent, absent.LegacyClaim);
            Assert.Equal("facts.delivery.legacy_unverified", FactListPresenter.DeliveryRole(absent));

            // A known forgettable record is outside pending debt whatever the flag said.
            FactSnapshot brief = ByDetail(kit.Store, "forgettable");
            Assert.False(brief.IsPending);
            Assert.DoesNotContain(pending.Items, i => i.Fact?.Id == brief.Id);

            // The existing saved-review rule is unchanged: history only, never "delivered".
            FactSnapshot reviewed = ByDetail(kit.Store, "saved review");
            Assert.False(reviewed.IsPending);
            Assert.True(reviewed.DischargedByReviewOnly);
            Assert.False(reviewed.PresentationComplete);
            Assert.DoesNotContain(pending.Items, i => i.Fact?.Id == reviewed.Id);
            Assert.Contains(history.Items, i => i.Fact?.Id == reviewed.Id);

            // Merely loading, displaying or focusing invokes nothing.
            foreach (FactSnapshot f in kit.Store.All)
            {
                Assert.False(kit.Store.IsEligibleForAutomaticDelivery(f));
                Assert.Equal(PreparationOutcome.NotEligible, kit.Presentation.Prepare(f.Id, PlanRequest.Automatic(VerbosityLevel.Chatty)).Outcome);
                Assert.Equal(ReceiptAttemptOutcome.NotClaimed, receipts.RequestFor(f.Id));
            }
            Assert.Equal(0, tones);
            Assert.Equal(0, tracked.NativeCalls);

            // The migration gap is a standing limitation, present as a row.
            StoreIssueSnapshot gap = Assert.Single(kit.Store.Issues, i => i.Kind == IssueKind.MigrationGap);
            Assert.Equal(IssueState.Limitation, gap.State);
            Assert.Contains(pending.Items, i => i.Issue?.Id == gap.Id);
            Assert.False(pending.Predicates.PresentationComplete);
        }

        [Fact]
        public void LegacyReviewDischargesOnlyTheShownInformation()
        {
            using var dir = new TempFactDir();
            string path = Path.Combine(dir.Path, "facts-legacy.json");
            string longDetail = "truncated one " + new string('x', FactStoreCapacity.MaxDetailBytes + 50);
            File.WriteAllText(path, Legacy(
                Record("shown", "false", 0, detail: "shown one"),
                Record("other", "false", 0, detail: "other one"),
                Record("long", "false", 0, detail: longDetail)));
            string hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

            // Writer B loads the legacy history, reviews ONE record through
            // its installed detail, and persists the overlay.
            var b = new FactKit();
            var bj = new FactJournal(b.Store, dir.Path);
            Assert.True(bj.TakeLease());
            bj.LoadHistory();
            FactSnapshot shown = ByDetail(b.Store, "shown one");
            FactSnapshot truncated = ByDetail(b.Store, "truncated one");
            Assert.True(truncated.DetailTruncated);                         // the omission is retained independently
            using (FactListView view = new FactListPresenter(b.Store).OpenView())
            {
                RenderedDetailSnapshot d = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == shown.Id))!;
                Assert.True(view.Installed(d));
                Assert.Equal(ReviewOutcome.Reviewed, view.Review(d.Token));
            }
            Assert.True(bj.Write());
            bj.Dispose();

            // The schema-1 source was never rewritten.
            Assert.Equal(hashBefore, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

            // A fresh store loads both sources.
            var (c, report) = Load(dir.Path);
            Assert.Equal(1, report.Counts.OverlaysApplied);
            FactSnapshot reviewed = c.Store.Find(shown.Id)!;
            Assert.False(reviewed.IsPending);                               // only the represented detail left pending
            Assert.True(reviewed.DischargedByReviewOnly);
            Assert.True(reviewed.LegacyUnverifiedCoverage);                 // reviewed, never "delivered"
            Assert.False(reviewed.PresentationComplete);
            Assert.Equal(LegacyDeliveryClaim.ClaimedDelivered, reviewed.LegacyClaim);
            using (FactListView view = new FactListPresenter(c.Store).OpenView())
            {
                Assert.DoesNotContain(view.Snapshot(FactView.Pending).Items, i => i.Fact?.Id == shown.Id);
                Assert.Contains(view.Snapshot(FactView.History).Items, i => i.Fact?.Id == shown.Id);
            }
            Assert.True(c.Store.Find(ByDetail(b.Store, "other one").Id)!.IsPending);
            FactSnapshot stillTruncated = c.Store.Find(truncated.Id)!;
            Assert.True(stillTruncated.IsPending);
            Assert.True(stillTruncated.DetailTruncated);
            StoreIssueSnapshot gap = Assert.Single(c.Store.Issues, i => i.Kind == IssueKind.MigrationGap);
            Assert.Equal(IssueState.Limitation, gap.State);               // the limitation still qualifies the aggregate claim

            // NEGATIVE CONTROL: reviewing only the migration-gap row reviews
            // no fact. Individual unreviewed records stay pending.
            var (n, _) = Load(dir.Path);
            using (FactListView view = new FactListPresenter(n.Store).OpenView())
            {
                ItemSnapshot row = view.Snapshot(FactView.Pending).Items.Single(i => i.Issue?.Kind == IssueKind.MigrationGap);
                RenderedDetailSnapshot d = view.RenderDetail(row)!;
                Assert.True(view.Installed(d));
                Assert.Equal(ReviewOutcome.Reviewed, view.Review(d.Token));
                FactListSnapshot after = view.Snapshot(FactView.Pending);
                Assert.DoesNotContain(after.Items, i => i.Issue?.Kind == IssueKind.MigrationGap);   // reviewed: leaves the default view
                Assert.Contains(view.Snapshot(FactView.History).Items, i => i.Issue?.Kind == IssueKind.MigrationGap);
                Assert.Equal(2, after.Items.Count(i => i.Kind == ItemKind.Fact && i.Fact!.IsPending));
            }
        }

        [Fact]
        public void LegacyUncertaintySurvivesAnEmptyPendingView()
        {
            using var dir = new TempFactDir();
            File.WriteAllText(Path.Combine(dir.Path, "facts-legacy.json"), Legacy(
                Record("one", "false", 0, detail: "first claimed"),
                Record("two", "false", 0, detail: "second claimed")));

            var kit = new FactKit();
            var journal = new FactJournal(kit.Store, dir.Path);
            Assert.True(journal.TakeLease());
            journal.LoadHistory();

            // Discharge every retained legacy detail through accepted review,
            // and review the limitation row itself.
            using (FactListView view = new FactListPresenter(kit.Store).OpenView())
            {
                foreach (ItemSnapshot item in view.Snapshot(FactView.Pending).Items.ToArray())
                {
                    RenderedDetailSnapshot d = view.RenderDetail(item)!;
                    Assert.True(view.Installed(d));
                    Assert.Equal(ReviewOutcome.Reviewed, view.Review(d.Token));
                }

                FactListSnapshot empty = view.Snapshot(FactView.Pending);
                Assert.Empty(empty.Items);                                   // pending is empty in scope
                Assert.True(empty.Predicates.EmptyPendingInScope);
                Assert.False(empty.Predicates.PresentationComplete);         // and nothing claims delivery
                Assert.False(empty.Predicates.CompleteInventory);
                Assert.True(empty.Predicates.ReviewedNotDelivered >= 2);
                var roles = FactListPresenter.EmptyStateRoles(empty);
                Assert.Contains("facts.window.nothing_pending_unverified", roles);
                Assert.Contains("facts.window.nothing_pending_reviewed", roles);
                Assert.DoesNotContain("facts.window.nothing_pending_recorded", roles);
                Assert.Equal("facts.status.nothing_waiting", FactListPresenter.StatusSummaryRoles(empty).Single().Key);

                // The limitation stays reachable, in history, as a limitation.
                FactListSnapshot history = view.Snapshot(FactView.History);
                ItemSnapshot gap = Assert.Single(history.Items, i => i.Issue?.Kind == IssueKind.MigrationGap);
                Assert.Equal(IssueState.Limitation, gap.Issue!.State);
                Assert.Contains(Lexicon.Get("facts.issue.state.limitation"), gap.RowText, StringComparison.Ordinal);
                Assert.Equal(2, history.Items.Count(i => i.Fact?.Legacy == true));
            }

            // Compaction: fill the store with PRESENTED facts so both reviewed
            // legacy records are compacted out. They count as
            // reviewed-not-delivered, never as presented. (Presented fillers,
            // so that on reload the budget can compact them and the legacy
            // records load again.)
            var filler = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            for (int i = 0; i < FactStoreCapacity.MaxHistoricalRecords + 2; i++)
            {
                FactSession s = kit.Session("SERIAL-" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture));
                EpisodeId id = FactKit.OpenHot(kit.HotSlot(s, "c"), 70m).Handle!.Id;
                AttemptHandle presented = kit.Allocate(kit.PlanAutomatic(id), filler.Binding);
                AttemptRunner.Run(presented, filler.Submit);
                presented.Report(TransportEvidence.Completed(5));
                s.End(FactKit.T0, "filled");
            }
            Assert.Empty(kit.Store.All.Where(f => f.Legacy));
            FactListSnapshot afterCompaction = kit.Store.Project(FactView.Pending, null);
            Assert.False(afterCompaction.Predicates.PresentationComplete);
            Assert.True(afterCompaction.Predicates.ReviewedNotDelivered >= 2);
            Assert.True(journal.Write());
            journal.Dispose();

            // Reload: the reviews survived their records' compaction, the
            // legacy records are still reviewed rather than owed, and the
            // limitation survived with its review.
            var (fresh, report) = Load(dir.Path);
            Assert.Equal(2, report.Counts.OverlaysApplied);
            Assert.All(fresh.Store.All.Where(f => f.Legacy), f =>
            {
                Assert.False(f.IsPending);
                Assert.True(f.DischargedByReviewOnly);
                Assert.False(f.PresentationComplete);
            });
            FactListSnapshot reloaded = fresh.Store.Project(FactView.Pending, null);
            Assert.False(reloaded.Predicates.PresentationComplete);
            Assert.DoesNotContain(reloaded.Items, i => i.Fact?.Legacy == true);
            StoreIssueSnapshot limitation = Assert.Single(fresh.Store.Issues, i => i.Kind == IssueKind.MigrationGap && i.SourceKey.StartsWith("legacy:", StringComparison.Ordinal));
            Assert.Equal(IssueState.Limitation, limitation.State);
            Assert.False(limitation.Unreviewed);                             // the review of the limitation survived the restart
            Assert.Contains(fresh.Store.Project(FactView.History, null).Items, i => i.Issue?.Id == limitation.Id);
            Assert.DoesNotContain("facts.window.nothing_pending_recorded", FactListPresenter.EmptyStateRoles(reloaded));

            // POSITIVE CONTROL: real attributable coverage in this schema is
            // what the presentation-complete predicate accepts.
            using var clean = new TempFactDir();
            var real = new FactKit();
            var rj = new FactJournal(real.Store, clean.Path);
            rj.TakeLease();
            rj.LoadHistory();
            var tracked = new RecordingTransport(real.Registry, "t", TransportCapability.ReportsCompletion);
            EpisodeId heard = FactKit.OpenHot(real.HotSlot(real.Session())).Handle!.Id;
            AttemptHandle a = real.Allocate(real.PlanAutomatic(heard), tracked.Binding);
            AttemptRunner.Run(a, tracked.Submit);
            a.Report(TransportEvidence.Completed(5));
            rj.Write();
            FactListSnapshot delivered = real.Store.Project(FactView.Pending, null);
            Assert.True(delivered.Predicates.PresentationComplete);
            Assert.Equal(new[] { "facts.window.nothing_pending_recorded" }, FactListPresenter.EmptyStateRoles(delivered));
            rj.Dispose();
        }
    }
}
