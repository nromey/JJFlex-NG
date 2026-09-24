#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios;
using Radios.Facts;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The combined path, end to end, radio-free: a captured owner event, the
    /// quiet and worsening decision, the retained fact, a request-only attempt,
    /// persistence, a fresh historical load, the issue-aware list, and review of
    /// the installed snapshot — which then survives another restart.
    /// </summary>
    /// <remarks>
    /// This is the test that stops individually green components from leaving
    /// a missing edge between them. It is headless: it proves no physical
    /// protection, no native cancellation, and nothing a screen reader can
    /// reach. The desk half is <c>UndeliveredDetailsSurfaceTests</c>.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class FactSeamTests
    {
        [Fact]
        public void TheWholeSeam_CaptureQuietWorsenAttemptPersistLoadListReview_SyntheticOwner()
        {
            using var dir = new TempFactDir();

            // ── Process one ──
            var one = new FactKit();
            var j1 = new FactJournal(one.Store, dir.Path);
            Assert.True(j1.TakeLease());
            j1.LoadHistory();
            var writer = new FactJournalWriter(one.Store, j1);

            FactSession session = one.Session("SERIAL-SEAM");
            SlotPublisher publisher = one.HotSlot(session);
            var queue = new Queue<Action>();
            var source = new FactSourceAdapter(publisher, queue.Enqueue);

            // Captured before Ctrl, evaluated after it.
            PublicationResult? opened = null;
            source.OnSourceEvent(FactKit.Temp(70m), FactKit.T0, "meter-1", ev =>
                opened = publisher.Open(ev, "condition.hot", FactKit.HotKey,
                    new[] { new MaterialDeclaration("temperature", FactValue.Of(70m)),
                            new MaterialDeclaration("duration", FactValue.Of(3L)) }));
            one.Registry.Quiet.Observe("ctrl");
            queue.Dequeue()();
            EpisodeId id = opened!.Handle!.Id;
            Assert.True(one.Store.Find(id)!.AutomaticPaused);

            // The owner says it got worse, after the quiet.
            source.OnSourceEvent(FactKit.Temp(74m), FactKit.T0, "meter-2", ev =>
                publisher.Worsen(opened.Handle, ev, FactKit.Worse(one.Store.Find(id)!, "w-1", 74m)));
            queue.Dequeue()();
            Assert.True(one.Store.IsEligibleForAutomaticDelivery(one.Store.Find(id)!));

            // A request-only attempt: sent, nothing more is known.
            var plain = new RecordingTransport(one.Registry, "plain", TransportCapability.RequestOnly);
            AttemptRunner.Run(one.Allocate(one.PlanAutomatic(id), plain.Binding), plain.Submit);
            Assert.Equal("PA at 74 degrees for 3 minutes.", plain.Sent.Single());
            Assert.True(one.Store.Find(id)!.IsPending);

            session.End(FactKit.T0, "application closing");
            writer.Dispose();
            Assert.True(writer.FlushOnce());
            Assert.Equal(one.Store.MutationSequence, one.Store.PersistedThrough);
            j1.Dispose();

            // A second, corrupt source sits beside it.
            File.WriteAllText(Path.Combine(dir.Path, "facts-damaged.json"), "{ \"schema\": 2, \"writer\": ");

            // ── Process two ──
            var two = new FactKit();
            var j2 = new FactJournal(two.Store, dir.Path);
            Assert.True(j2.TakeLease());
            LoadReport report = j2.LoadHistory();
            Assert.Equal(1, report.Counts.Accepted);

            FactSnapshot restored = two.Store.Find(id)!;
            Assert.False(restored.IsLive);
            Assert.True(restored.IsPending);
            Assert.Equal(AttemptDisposition.Interrupted, restored.Attempts.Single().Disposition);
            Assert.Contains(restored.Materials, m => m.Kind == MaterialKind.Worsening);

            // The issue-aware list: the history row AND the recovery row.
            using (FactListView view = new FactListPresenter(two.Store).OpenView())
            {
                FactListSnapshot list = view.Snapshot(FactView.Pending);
                Assert.Contains(list.Items, i => i.Issue?.Kind == IssueKind.RecoveryGap);
                ItemSnapshot row = list.Items.Single(i => i.Fact?.Id == id);

                // Review the installed snapshot.
                RenderedDetailSnapshot shown = view.RenderDetail(row)!;
                Assert.True(shown.Historical);
                Assert.True(view.Installed(shown));
                Assert.Equal(ReviewOutcome.Reviewed, view.Review(shown.Token));
            }
            Assert.False(two.Store.Find(id)!.IsPending);
            Assert.True(j2.Write());
            j2.Dispose();

            // ── Process three: the review of imported history survived ──
            var three = new FactKit();
            var j3 = new FactJournal(three.Store, dir.Path);
            j3.TakeLease();
            LoadReport third = j3.LoadHistory();
            j3.Dispose();
            Assert.Equal(1, third.Counts.OverlaysApplied);
            Assert.Single(three.Store.All);                     // B copied nothing; nothing multiplied
            Assert.False(three.Store.Find(id)!.IsPending);
            Assert.Contains(three.Store.Issues, i => i.Kind == IssueKind.RecoveryGap);
        }
    }
}
