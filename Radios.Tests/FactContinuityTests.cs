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
    /// G2: a reconnect is a new observation context, not a new occurrence. It
    /// creates no new obligation for identical information, no onset grant
    /// and no receipt; it carries the predecessor's evidence for exactly the
    /// assertions it shares, its permission at the ORIGINAL causal position
    /// in the same process, and its receipt consequence.
    /// </summary>
    /// <remarks>
    /// Every owner here is SYNTHETIC. The production renderer, a registered
    /// completing transport and the real receipt adapter are used, with a
    /// counting tone boundary. Nothing here proves a person heard anything
    /// or that a screen reader stopped; the adapter callbacks are synthetic
    /// evidence at the store's boundary.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class FactContinuityTests
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

        private static MaterialUnit Unit(FactSnapshot f, string name) => f.Materials.Last(m => m.Name == name);

        public enum Completion
        {
            FullyPresented,
            ReviewedOnly,
            ShortRenderingOmittedDuration,
            ReceiptNeverRequested,
        }

        [Theory]
        [InlineData(Completion.FullyPresented)]
        [InlineData(Completion.ReviewedOnly)]
        [InlineData(Completion.ShortRenderingOmittedDuration)]
        [InlineData(Completion.ReceiptNeverRequested)]
        public void CompletedContinuationDoesNotRearm(Completion how)
        {
            using var dir = new TempFactDir();
            var kit = new FactKit();
            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            int tones = 0;
            var receipts = new ReceiptRequestAdapter(kit.Registry.RegisterReceiptAdapter("r"),
                                                     _ => { tones++; return ToneRequestResult.PlaybackReported; });

            FactSession s1 = kit.Session("SERIAL-C");
            SlotPublisher p1 = kit.HotSlot(s1);
            EpisodeId first = FactKit.OnsetHot(p1, 70m, 3).Handle!.Id;

            switch (how)
            {
                case Completion.FullyPresented:
                case Completion.ReceiptNeverRequested:
                    {
                        AttemptHandle a = kit.Allocate(kit.PlanAutomatic(first, VerbosityLevel.Chatty), tracked.Binding);
                        AttemptRunner.Run(a, tracked.Submit);
                        Assert.Equal("PA at 70 degrees for 3 minutes.", tracked.Sent.Single());
                        a.Report(TransportEvidence.Completed(5));
                        break;
                    }
                case Completion.ShortRenderingOmittedDuration:
                    {
                        AttemptHandle a = kit.Allocate(kit.PlanAutomatic(first, VerbosityLevel.Terse), tracked.Binding);
                        AttemptRunner.Run(a, tracked.Submit);
                        Assert.Equal("PA at 70 degrees.", tracked.Sent.Single());
                        a.Report(TransportEvidence.Completed(5));
                        break;
                    }
                case Completion.ReviewedOnly:
                    using (FactListView view = new FactListPresenter(kit.Store).OpenView())
                    {
                        RenderedDetailSnapshot d = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == first))!;
                        Assert.True(view.Installed(d));
                        Assert.Equal(ReviewOutcome.Reviewed, view.Review(d.Token));
                    }
                    break;
            }
            if (how != Completion.ReceiptNeverRequested)
            {
                Assert.Equal(ReceiptAttemptOutcome.ToneRequested, receipts.RequestFor(first));
                Assert.Equal(1, tones);
            }
            int nativeBefore = tracked.NativeCalls;
            int tonesBefore = tones;

            s1.End(T0, "disconnected");

            // Reopen the same supported continuation, twice.
            EpisodeId? previous = null;
            for (int reconnect = 1; reconnect <= 2; reconnect++)
            {
                FactSession s = kit.Session("SERIAL-C");
                SlotPublisher p = kit.HotSlot(s);
                ContinuityView view = p.Continuity!;
                Assert.NotNull(view.Reference);
                Assert.True(view.DeliveryEvidenceSupported);
                Assert.Equal(first, view.Reference!.Root);

                PublicationResult c = FactKit.ContinueHot(p, 70m, 3);
                Assert.Equal(PublicationOutcome.Accepted, c.Outcome);
                FactSnapshot successor = c.Fact!;
                Assert.Equal(first, successor.LineageRoot);
                Assert.Equal(previous ?? first, successor.PredecessorEpisode);
                Assert.True(successor.IsLive);
                Assert.Empty(successor.Grants.Where(g => g.Origin == GrantOrigin.Occurrence));   // no onset grant

                switch (how)
                {
                    case Completion.FullyPresented:
                    case Completion.ReceiptNeverRequested:
                        // Nothing newly owed, no automatic sentence, and the
                        // evidence is the predecessor's, found through the link.
                        Assert.Empty(successor.Unpresented);
                        Assert.False(successor.IsPending);
                        Assert.True(successor.PresentationComplete);
                        Assert.False(kit.Store.IsEligibleForAutomaticDelivery(successor));
                        Assert.Equal(PreparationOutcome.NotEligible,
                            kit.Presentation.Prepare(successor.Id, PlanRequest.Automatic(VerbosityLevel.Chatty)).Outcome);
                        break;
                    case Completion.ReviewedOnly:
                        // Reviewed carries; it remains reviewed, not presented.
                        Assert.Empty(successor.Unpresented);
                        Assert.False(successor.IsPending);
                        Assert.False(successor.PresentationComplete);
                        Assert.True(successor.DischargedByReviewOnly);
                        Assert.False(kit.Store.IsEligibleForAutomaticDelivery(successor));
                        break;
                    case Completion.ShortRenderingOmittedDuration:
                        // Omitted material stays owed, and in the same process
                        // the ORIGINAL permission continues its retry — that is
                        // inherited permission for the existing debt, not a
                        // grant from the reconnect.
                        Assert.Equal(new[] { Unit(successor, "duration").Id }, successor.Unpresented);
                        Assert.True(successor.IsPending);
                        GrantSnapshot inherited = Assert.Single(successor.Grants);
                        Assert.Equal(GrantOrigin.Inherited, inherited.Origin);
                        Assert.Equal(kit.Store.Find(first)!.Grants.Single().SourceSequence, inherited.SourceSequence);
                        break;
                }

                // No receipt: the occurrence's allowance was spent, or closed
                // unused when its scope ended. A fresh EpisodeId buys no tone.
                Assert.True(successor.Receipt.AllowanceConsumed);
                Assert.Equal(how == Completion.ReceiptNeverRequested, successor.Receipt.Closed);
                Assert.Equal(ReceiptAttemptOutcome.NotClaimed, receipts.RequestFor(successor.Id));
                Assert.Equal(tonesBefore, tones);
                if (how != Completion.ShortRenderingOmittedDuration) Assert.Equal(nativeBefore, tracked.NativeCalls);

                // Both identities remain: the predecessor is history, the
                // successor is the current observation.
                FactSnapshot predecessor = kit.Store.Find(previous ?? first)!;
                Assert.True(predecessor.IsHistorical);
                Assert.False(predecessor.IsLive);
                Assert.True(successor.Validity.IsCurrent);

                previous = successor.Id;
                s.End(T0, "disconnected again");
            }

            // Journal, reload, and continue again across the restart.
            Save(kit, dir.Path);
            var (fresh, report) = Load(dir.Path);
            Assert.Equal(3, report.Counts.Accepted);
            int restartTones = 0;
            var restartReceipts = new ReceiptRequestAdapter(fresh.Registry.RegisterReceiptAdapter("r"),
                                                            _ => { restartTones++; return ToneRequestResult.Requested; });
            var restartTransport = new RecordingTransport(fresh.Registry, "t", TransportCapability.ReportsCompletion);

            SlotPublisher after = fresh.HotSlot(fresh.Session("SERIAL-C"));
            ContinuityView saved = after.Continuity!;
            Assert.True(saved.FromPreviousProcess);
            Assert.NotNull(saved.Reference);
            Assert.Equal(first, saved.Reference!.Root);
            PublicationResult continued = FactKit.ContinueHot(after, 70m, 3);
            Assert.Equal(PublicationOutcome.Accepted, continued.Outcome);
            FactSnapshot restarted = continued.Fact!;
            Assert.Equal(first, restarted.LineageRoot);
            Assert.Empty(restarted.Grants);                                     // no live grant survives a restart
            Assert.False(fresh.Store.IsEligibleForAutomaticDelivery(restarted));
            Assert.Equal(ReceiptAttemptOutcome.NotClaimed, restartReceipts.RequestFor(restarted.Id));
            Assert.Equal(0, restartTones);
            Assert.Equal(0, restartTransport.NativeCalls);
            if (how == Completion.ShortRenderingOmittedDuration)
            {
                Assert.True(restarted.IsPending);                               // the omitted duration is still owed
                Assert.Equal(PauseCause.ContinuityAcrossRestart, restarted.Pause);
            }
            else
            {
                Assert.Empty(restarted.Unpresented);                            // the evidence survived the restart
                Assert.False(restarted.IsPending);
            }
            Assert.True(fresh.Store.Find(first)!.IsHistorical);

            // POSITIVE CONTROL: an evidenced new onset at the same value is a
            // new occurrence — new debt, its own grant, its own tone.
            PublicationResult onset = FactKit.OnsetHot(fresh.HotSlot(fresh.Session("SERIAL-C"), "psu"), 70m, 3);
            Assert.Equal(PublicationOutcome.Accepted, onset.Outcome);
            Assert.Equal(onset.Handle!.Id, onset.Fact!.LineageRoot);
            Assert.True(onset.Fact.IsPending);
            Assert.True(fresh.Store.IsEligibleForAutomaticDelivery(onset.Fact));
            Assert.Equal(ReceiptAttemptOutcome.ToneRequested, restartReceipts.RequestFor(onset.Handle.Id));
            Assert.Equal(1, restartTones);
        }

        [Fact]
        public void ContinuationRetainsCausalPermission()
        {
            // (i) Same process, no Ctrl: the inherited retry reaches the
            // recording adapter under the ORIGINAL grant.
            var kit = new FactKit();
            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            FactSession s1 = kit.Session("SERIAL-R");
            PublicationResult first = FactKit.OnsetHot(kit.HotSlot(s1), 70m, 3);
            long originalPosition = first.Fact!.Grants.Single().SourceSequence;
            s1.End(T0, "disconnected");

            PublicationResult c = FactKit.ContinueHot(kit.HotSlot(kit.Session("SERIAL-R")), 70m, 3);
            Assert.Equal(PublicationOutcome.Accepted, c.Outcome);
            GrantSnapshot inherited = Assert.Single(c.Fact!.Grants);
            Assert.Equal(GrantOrigin.Inherited, inherited.Origin);
            Assert.Equal(originalPosition, inherited.SourceSequence);
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(c.Fact));
            Assert.Equal(AttemptRunOutcome.Requested,
                AttemptRunner.Run(kit.Allocate(kit.PlanAutomatic(c.Handle!.Id), tracked.Binding), tracked.Submit));
            Assert.Equal("PA at 70 degrees for 3 minutes.", tracked.Sent.Single());

            // (ii) Ctrl between detach and open: the inherited position is
            // before the quiet, so the successor is paused by that quiet.
            var kit2 = new FactKit();
            var tracked2 = new RecordingTransport(kit2.Registry, "t", TransportCapability.ReportsCompletion);
            FactSession s2 = kit2.Session("SERIAL-R");
            FactKit.OnsetHot(kit2.HotSlot(s2), 70m, 3);
            s2.End(T0, "disconnected");
            kit2.Registry.Quiet.Observe("ctrl during the gap");
            PublicationResult paused = FactKit.ContinueHot(kit2.HotSlot(kit2.Session("SERIAL-R")), 70m, 3);
            Assert.Equal(PublicationOutcome.Accepted, paused.Outcome);
            Assert.Equal(PauseCause.OperatorQuiet, paused.Fact!.Pause);
            Assert.False(kit2.Store.IsEligibleForAutomaticDelivery(paused.Fact));
            Assert.Equal(PreparationOutcome.NotEligible,
                kit2.Presentation.Prepare(paused.Handle!.Id, PlanRequest.Automatic(VerbosityLevel.Chatty)).Outcome);
            Assert.Equal(0, tracked2.NativeCalls);

            // (iii) A delayed continuation: the reconnect event is captured
            // AFTER the quiet and admitted later. Stamping the inherited
            // permission with the reconnect event's position would make it
            // eligible; the original position does not.
            var kit3 = new FactKit();
            var tracked3 = new RecordingTransport(kit3.Registry, "t", TransportCapability.ReportsCompletion);
            FactSession s3 = kit3.Session("SERIAL-R");
            FactKit.OnsetHot(kit3.HotSlot(s3), 70m, 3);
            s3.End(T0, "disconnected");
            kit3.Registry.Quiet.Observe("ctrl before the reconnect");
            SlotPublisher p3 = kit3.HotSlot(kit3.Session("SERIAL-R"));
            var held = new Queue<Action>();
            var adapter = new FactSourceAdapter(p3, held.Enqueue);
            PublicationResult? delayed = null;
            var declared = new[] { new MaterialDeclaration("temperature", FactValue.Of(70m)), new MaterialDeclaration("duration", FactValue.Of(3L)) };
            adapter.OnSourceEvent(FactKit.Temp(70m), T0, "reconnect-1", ev =>
            {
                ContinuityView view = p3.Continuity!;
                delayed = p3.Open(ev, "condition.hot", FactKit.HotKey, declared, new OpenOptions
                {
                    Continuity = ContinuityClaim.Continuation, Predecessor = view.Reference, Carried = FactKit.Links(view, declared),
                });
            });
            held.Dequeue()();
            Assert.Equal(PublicationOutcome.Accepted, delayed!.Outcome);
            Assert.True(delayed.Fact!.Grants.Single().SourceSequence < kit3.Store.LatestQuietSequence);
            Assert.Equal(PauseCause.OperatorQuiet, delayed.Fact.Pause);
            Assert.Equal(PreparationOutcome.NotEligible,
                kit3.Presentation.Prepare(delayed.Handle!.Id, PlanRequest.Automatic(VerbosityLevel.Chatty)).Outcome);
            Assert.Equal(0, tracked3.NativeCalls);

            // (iv) A saved, UNPAUSED, incomplete occurrence reloaded into a
            // fresh store: no inherited live grant, but an explicit selected
            // resume of the new live continuation works.
            using var dir = new TempFactDir();
            var writer = new FactKit();
            FactSession s4 = writer.Session("SERIAL-R");
            FactKit.OnsetHot(writer.HotSlot(s4), 70m, 3);
            s4.End(T0, "application closing");
            Save(writer, dir.Path);

            var (fresh, _) = Load(dir.Path);
            var tracked4 = new RecordingTransport(fresh.Registry, "t", TransportCapability.ReportsCompletion);
            PublicationResult restarted = FactKit.ContinueHot(fresh.HotSlot(fresh.Session("SERIAL-R")), 70m, 3);
            Assert.Equal(PublicationOutcome.Accepted, restarted.Outcome);
            Assert.Empty(restarted.Fact!.Grants);
            Assert.True(restarted.Fact.IsPending);
            Assert.Equal(PauseCause.ContinuityAcrossRestart, restarted.Fact.Pause);
            Assert.False(fresh.Store.IsEligibleForAutomaticDelivery(restarted.Fact));

            using FactListView list = new FactListPresenter(fresh.Store).OpenView();
            RenderedDetailSnapshot shown = list.RenderDetail(list.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == restarted.Handle!.Id))!;
            Assert.True(list.Installed(shown));
            Assert.Equal(ResumeOutcome.Resumed, list.Resume(shown.Token));
            FactSnapshot resumed = fresh.Store.Find(restarted.Handle!.Id)!;
            Assert.True(fresh.Store.IsEligibleForAutomaticDelivery(resumed));
            Assert.Equal(GrantOrigin.ExplicitResume, resumed.Grants.Single().Origin);
            Assert.Equal(AttemptRunOutcome.Requested,
                AttemptRunner.Run(fresh.Allocate(fresh.PlanAutomatic(resumed.Id), tracked4.Binding), tracked4.Submit));
            Assert.Equal(1, tracked4.NativeCalls);
        }

        [Fact]
        public void ContinuingEvidenceKeepsItsExactTarget()
        {
            // An old plan at the SHORT tier (temperature only), reconnect with
            // duration unchanged and temperature corrected, then the old
            // completion arrives. Only the assertion the old words carried AND
            // the successor shares gains credit.
            var kit = new FactKit();
            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            FactSession s1 = kit.Session("SERIAL-E");
            SlotPublisher p1 = kit.HotSlot(s1);
            EpisodeId first = FactKit.OnsetHot(p1, 70m, 3).Handle!.Id;
            AttemptHandle old = kit.Allocate(kit.PlanAutomatic(first, VerbosityLevel.Terse), tracked.Binding);
            AttemptRunner.Run(old, tracked.Submit);
            Assert.Equal("PA at 70 degrees.", tracked.Sent.Single());
            s1.End(T0, "disconnected");

            SlotPublisher p2 = kit.HotSlot(kit.Session("SERIAL-E"));
            PublicationResult c = FactKit.ContinueHot(p2, 72m, 3);              // temperature 72 is NEW; duration 3 is the same assertion
            Assert.Equal(PublicationOutcome.Accepted, c.Outcome);
            FactSnapshot before = c.Fact!;
            Assert.Null(Unit(before, "temperature").Origin);
            Assert.Equal(new AssertionRef(first, Unit(kit.Store.Find(first)!, "duration").Id), Unit(before, "duration").Root);
            Assert.Equal(3, before.Unpresented.Count);

            Assert.Equal(EvidenceResult.Recorded, old.Report(TransportEvidence.Completed(5)));

            FactSnapshot after = kit.Store.Find(c.Handle!.Id)!;
            Assert.Contains(Unit(after, MaterialUnit.CoreName).Id, after.Covered);   // the occurrence, shared and said
            Assert.Contains(Unit(after, "duration").Id, after.Unpresented);          // omitted by the short words: still owed
            Assert.Contains(Unit(after, "temperature").Id, after.Unpresented);       // a different value: never said
            Assert.DoesNotContain(Unit(after, "temperature").Id, after.Covered);
            Assert.True(after.IsPending);

            // The parallel review case: review the predecessor's installed
            // snapshot (which lists the duration). The shared duration is
            // discharged on the successor; its new temperature is not.
            using (FactListView view = new FactListPresenter(kit.Store).OpenView())
            {
                RenderedDetailSnapshot oldDetail = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == first))!;
                Assert.Contains("3", oldDetail.Text, StringComparison.Ordinal);
                Assert.True(view.Installed(oldDetail));
                Assert.Equal(ReviewOutcome.Reviewed, view.Review(oldDetail.Token));
            }
            FactSnapshot reviewed = kit.Store.Find(c.Handle.Id)!;
            Assert.Contains(Unit(reviewed, "duration").Id, reviewed.Reviewed);
            Assert.Equal(new[] { Unit(reviewed, "temperature").Id }, reviewed.Unpresented);

            // An unknown-cause cancellation reported against the OLD attempt
            // follows the permission lineage into the successor.
            var kit2 = new FactKit();
            var tracked2 = new RecordingTransport(kit2.Registry, "t", TransportCapability.ReportsCompletion);
            FactSession s2 = kit2.Session("SERIAL-E");
            EpisodeId first2 = FactKit.OnsetHot(kit2.HotSlot(s2), 70m, 3).Handle!.Id;
            AttemptHandle old2 = kit2.Allocate(kit2.PlanAutomatic(first2), tracked2.Binding);
            AttemptRunner.Run(old2, tracked2.Submit);
            s2.End(T0, "disconnected");
            PublicationResult c2 = FactKit.ContinueHot(kit2.HotSlot(kit2.Session("SERIAL-E")), 70m, 3);
            Assert.True(kit2.Store.IsEligibleForAutomaticDelivery(c2.Fact!));
            Assert.Equal(EvidenceResult.Recorded, old2.Report(TransportEvidence.Cancelled(5, CancelCause.Unknown)));
            FactSnapshot constrained = kit2.Store.Find(c2.Handle!.Id)!;
            Assert.Equal(PauseCause.UnknownCancellation, constrained.Pause);
            Assert.False(kit2.Store.IsEligibleForAutomaticDelivery(constrained));

            // Disputed evidence on the old attempt credits nothing anywhere.
            var kit3 = new FactKit();
            var tracked3 = new RecordingTransport(kit3.Registry, "t", TransportCapability.ReportsCompletion);
            FactSession s3 = kit3.Session("SERIAL-E");
            EpisodeId first3 = FactKit.OnsetHot(kit3.HotSlot(s3), 70m, 3).Handle!.Id;
            AttemptHandle old3 = kit3.Allocate(kit3.PlanAutomatic(first3), tracked3.Binding);
            AttemptRunner.Run(old3, tracked3.Submit);
            s3.End(T0, "disconnected");
            PublicationResult c3 = FactKit.ContinueHot(kit3.HotSlot(kit3.Session("SERIAL-E")), 70m, 3);
            old3.Report(TransportEvidence.Completed(5));
            Assert.Empty(kit3.Store.Find(c3.Handle!.Id)!.Unpresented);
            Assert.Equal(EvidenceResult.Disputed, old3.Report(TransportEvidence.Cancelled(6, CancelCause.Unknown)));
            Assert.Equal(3, kit3.Store.Find(c3.Handle.Id)!.Unpresented.Count);
            Assert.Empty(kit3.Store.Find(first3)!.Covered);

            // Earlier attempt detail compacted past every bound: the evidence
            // survives in the ledger, and the successor still finds it — also
            // after a journal round trip.
            using var dir = new TempFactDir();
            var kit4 = new FactKit();
            var tracked4 = new RecordingTransport(kit4.Registry, "t", TransportCapability.ReportsCompletion);
            FactSession s4 = kit4.Session("SERIAL-E");
            SlotPublisher p4 = kit4.HotSlot(s4);
            PublicationResult opened4 = FactKit.OnsetHot(p4, 70m, 3);
            EpisodeId first4 = opened4.Handle!.Id;
            AttemptHandle carrier = kit4.Allocate(kit4.PlanAutomatic(first4, VerbosityLevel.Chatty), tracked4.Binding);
            AttemptRunner.Run(carrier, tracked4.Submit);
            carrier.Report(TransportEvidence.Completed(5));                    // covers core, temperature, duration
            // Keep the fact owed (a correction of the temperature) so terse
            // attempts keep being allowed, and push the carrier out of every bound.
            PublicationResult corrected4 = p4.Update(opened4.Handle, FactKit.Capture(p4, FactKit.Temp(71m)),
                FactTransition.Correction(new[] { new MaterialDeclaration("temperature", FactValue.Of(71m)) }),
                kit4.Store.Find(first4)!.Revision);
            Assert.Equal(PublicationOutcome.Accepted, corrected4.Outcome);
            int rounds = FactStoreCapacity.MaxAttemptEvidence + FactStoreCapacity.MaxAttemptTombstones + 5;
            for (int i = 0; i < rounds; i++)
            {
                AttemptHandle a = kit4.Allocate(kit4.PlanAutomatic(first4, VerbosityLevel.Terse), tracked4.Binding);
                AttemptRunner.Run(a, tracked4.Submit);
                Assert.Equal(EvidenceResult.Recorded, a.Report(TransportEvidence.CompletionUnobservable(5)));   // closed, nothing established
            }
            FactSnapshot compacted = kit4.Store.Find(first4)!;
            Assert.DoesNotContain(compacted.Attempts, a => a.Id == carrier.Id);
            Assert.Contains(Unit(compacted, "duration").Id, compacted.Covered);   // only the ledger knows this
            s4.End(T0, "disconnected");

            PublicationResult c4 = FactKit.ContinueHot(kit4.HotSlot(kit4.Session("SERIAL-E")), 71m, 3);
            Assert.Equal(PublicationOutcome.Accepted, c4.Outcome);
            Assert.Contains(Unit(c4.Fact!, "duration").Id, c4.Fact.Covered);
            Assert.Contains(Unit(c4.Fact, "temperature").Id, c4.Fact.Unpresented);   // 71 was corrected in, never said

            Save(kit4, dir.Path);
            var (fresh, _) = Load(dir.Path);
            FactSnapshot restored = fresh.Store.Find(c4.Handle!.Id)!;
            Assert.Contains(Unit(restored, "duration").Id, restored.Covered);
            Assert.Contains(Unit(restored, "temperature").Id, restored.Unpresented);
        }

        [Fact]
        public void WorseningAndUnknownDoNotBecomeOnset()
        {
            var kit = new FactKit();
            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            int tones = 0;
            var receipts = new ReceiptRequestAdapter(kit.Registry.RegisterReceiptAdapter("r"),
                                                     _ => { tones++; return ToneRequestResult.Requested; });

            // Previously presented material, and an earlier receipt.
            FactSession s1 = kit.Session("SERIAL-W");
            EpisodeId first = FactKit.OnsetHot(kit.HotSlot(s1), 70m, 3).Handle!.Id;
            AttemptHandle a = kit.Allocate(kit.PlanAutomatic(first), tracked.Binding);
            AttemptRunner.Run(a, tracked.Submit);
            a.Report(TransportEvidence.Completed(5));
            Assert.Equal(ReceiptAttemptOutcome.ToneRequested, receipts.RequestFor(first));
            Assert.Equal(1, tones);
            s1.End(T0, "disconnected");

            // A supported reconnect worsening: the grant covers the worse
            // material only, no occurrence grant sits beneath it, no tone.
            SlotPublisher p2 = kit.HotSlot(kit.Session("SERIAL-W"));
            ContinuityView view = p2.Continuity!;
            PublicationResult worse = FactKit.ContinueHot(p2, 70m, 3, ContinuityClaim.WorseningOfPrior,
                new WorseningTransition("rw-1", view.BaselineFingerprint,
                    new[] { new MaterialDeclaration("temperature", FactValue.Of(74m)) }, "worse than before the reconnect"),
                observedTemperature: 74m);
            Assert.Equal(PublicationOutcome.Accepted, worse.Outcome);
            FactSnapshot successor = worse.Fact!;
            // The NEW permission is the worsening grant, covering the worse
            // material only. The predecessor's own grant is carried at its
            // original position over the units it already covered; there is
            // no generic occurrence grant for the whole reconnect record.
            GrantSnapshot grant = Assert.Single(successor.Grants, g => g.Origin == GrantOrigin.Worsening);
            Assert.DoesNotContain(successor.Grants, g => g.Origin == GrantOrigin.Occurrence);
            Assert.All(successor.Grants.Where(g => g.Origin != GrantOrigin.Worsening), g => Assert.Equal(GrantOrigin.Inherited, g.Origin));
            MaterialUnit worseUnit = successor.Materials.Single(m => m.Kind == MaterialKind.Worsening);
            Assert.Equal(new[] { worseUnit.Id }, grant.Covers);
            Assert.All(successor.Grants.Where(g => g.Origin == GrantOrigin.Inherited), g => Assert.DoesNotContain(worseUnit.Id, g.Covers));
            Assert.Equal(new[] { worseUnit.Id }, successor.Presentable);
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(successor));
            PresentationPlan plan = kit.PlanAutomatic(successor.Id);
            Assert.Equal("PA at 74 degrees for 3 minutes.", plan.Text);
            Assert.Equal(ReceiptAttemptOutcome.NotClaimed, receipts.RequestFor(successor.Id));
            Assert.Equal(1, tones);

            // Another worsening captured before Ctrl but admitted after: no start.
            CapturedFactEvent late = FactKit.Capture(p2, FactKit.Temp(78m));
            kit.Registry.Quiet.Observe("ctrl");
            Assert.Equal(PublicationOutcome.Accepted,
                p2.Worsen(worse.Handle!, late, FactKit.Worse(kit.Store.Find(successor.Id)!, "rw-2", 78m)).Outcome);
            FactSnapshot held = kit.Store.Find(successor.Id)!;
            Assert.Equal(PauseCause.OperatorQuiet, held.Pause);
            Assert.Equal(PreparationOutcome.NotEligible,
                kit.Presentation.Prepare(successor.Id, PlanRequest.Automatic(VerbosityLevel.Chatty)).Outcome);

            // ── The cases that must retain uncertainty: no grant, no receipt. ──
            static void AssertUnknown(FactKit k, PublicationResult r, PauseCause expected)
            {
                Assert.Equal(PublicationOutcome.Accepted, r.Outcome);
                Assert.Equal(expected, r.Fact!.Pause);
                Assert.Empty(r.Fact.Grants);
                Assert.Equal(ReceiptPolicy.None, r.Fact.Receipt.Policy);
                Assert.True(r.Fact.IsPending);
                Assert.False(k.Store.IsEligibleForAutomaticDelivery(r.Fact));
                Assert.Equal(PreparationOutcome.NotEligible,
                    k.Presentation.Prepare(r.Handle!.Id, PlanRequest.Automatic(VerbosityLevel.Chatty)).Outcome);
            }

            // Missing predecessor: a claimed continuation with no reference.
            var kitM = new FactKit();
            FactSession sM = kitM.Session("SERIAL-M");
            FactKit.OpenHot(kitM.HotSlot(sM), 70m, 3);
            sM.End(T0, "gone");
            AssertUnknown(kitM, FactKit.OpenHot(kitM.HotSlot(kitM.Session("SERIAL-M")), 70m,
                options: new OpenOptions { Continuity = ContinuityClaim.Continuation }), PauseCause.ContinuityUnknown);

            // Explicit unknown.
            AssertUnknown(kitM, FactKit.OpenHot(kitM.HotSlot(kitM.Session("SERIAL-M")), 70m,
                options: new OpenOptions { Continuity = ContinuityClaim.ContinuityUnknown }), PauseCause.ContinuityUnknown);

            // Incompatible definition: the same condition under a new revision.
            SlotPublisher redefined = kitM.Registry.Register(kitM.Hot, kitM.Session("SERIAL-M"), FactKit.Temperature, new ConditionKey("pa", 1)).Publisher!;
            Assert.Equal(0, redefined.Continuity!.DefinitionRevision);
            AssertUnknown(kitM, FactKit.ContinueHot(redefined, 70m, 3), PauseCause.ContinuityUnknown);

            // Unknown station identity: nothing to compare against.
            var kitU = new FactKit();
            AssertUnknown(kitU, FactKit.OpenHot(kitU.HotSlot(kitU.Session(null)), 70m), PauseCause.ContinuityUnknown);
            AssertUnknown(kitU, FactKit.OpenHot(kitU.HotSlot(kitU.Session(null), "psu"), 70m,
                options: new OpenOptions { Continuity = ContinuityClaim.Continuation }), PauseCause.ContinuityUnknown);

            // Evicted evidence: the continuity record was lost to capacity.
            using var lostDir = new TempFactDir();
            var before = new FactKit();
            var stations = new List<string>();
            for (int i = 0; i <= FactStoreCapacity.MaxContinuityRecords; i++)
            {
                string radio = "SERIAL-" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
                FactSession s = before.Session(radio);
                FactKit.OpenHot(before.HotSlot(s), 70m);
                s.End(T0, "gone");
                stations.Add(radio);
            }
            Save(before, lostDir.Path);
            var (afterLoss, _) = Load(lostDir.Path);
            string? lost = null;
            foreach (string radio in stations)
            {
                FactSession probe = afterLoss.Session(radio);
                if (afterLoss.HotSlot(probe).Continuity == null) { lost = radio; break; }
                probe.End(T0, "survived; not needed");           // release its slot
            }
            Assert.NotNull(lost);
            AssertUnknown(afterLoss, FactKit.OpenHot(afterLoss.HotSlot(afterLoss.Session(lost!)), 70m,
                options: new OpenOptions { Continuity = ContinuityClaim.Continuation }), PauseCause.ContinuityLost);

            // Old-format continuity without delivery evidence: a schema-2 file.
            using var v2Dir = new TempFactDir();
            File.WriteAllText(Path.Combine(v2Dir.Path, "facts-0123456789abcdef0123456789abcdef.json"), SchemaTwoShard);
            var (v2, v2Report) = Load(v2Dir.Path);
            Assert.Equal(1, v2Report.Counts.Accepted);
            Assert.Contains(v2Report.Sources, s => s.Status == SourceStatus.Loaded && s.Note != null && s.Note.Contains("schema 2", StringComparison.Ordinal));
            StoreIssueSnapshot limitation = Assert.Single(v2.Store.Issues, i => i.Kind == IssueKind.MigrationGap);
            Assert.Equal(IssueState.Limitation, limitation.State);
            EpisodeId v2Episode = new EpisodeId(Guid.ParseExact("0123456789abcdef0123456789abcdef", "N"), 1);
            FactSnapshot v2Fact = v2.Store.Find(v2Episode)!;
            Assert.True(v2Fact.PresentationComplete);                          // its own evidence is independently valid
            SlotPublisher pV2 = v2.HotSlot(v2.Session("SERIAL-V2"));
            ContinuityView unsupported = pV2.Continuity!;
            Assert.False(unsupported.DeliveryEvidenceSupported);
            Assert.Null(unsupported.Reference);
            Assert.Empty(unsupported.Assertions);
            AssertUnknown(v2, FactKit.ContinueHot(pV2, 70m, 3), PauseCause.ContinuityUnknown);

            // POSITIVE CONTROL: a genuinely evidenced new onset speaks and
            // earns its own tone, on the very station whose continuity was lost.
            int lostTones = 0;
            var lostReceipts = new ReceiptRequestAdapter(afterLoss.Registry.RegisterReceiptAdapter("r"),
                                                         _ => { lostTones++; return ToneRequestResult.Requested; });
            PublicationResult onset = FactKit.OnsetHot(afterLoss.HotSlot(afterLoss.Session(lost!), "pa2"), 70m);
            Assert.Single(onset.Fact!.Grants);
            Assert.Equal(GrantOrigin.Occurrence, onset.Fact.Grants[0].Origin);
            Assert.True(afterLoss.Store.IsEligibleForAutomaticDelivery(onset.Fact));
            Assert.Equal(ReceiptAttemptOutcome.ToneRequested, lostReceipts.RequestFor(onset.Handle!.Id));
            Assert.Equal(1, lostTones);
        }

        [Fact]
        public void AnUnknownCancellationSurvivesAnyNumberOfReconnects()
        {
            // Sol, G2 blocker 2: the constraint was read by walking
            // InheritedFrom with a depth limit of 64, and each same-process
            // continuation added a link. By the 65th continued grant an
            // unknown cancellation on the original permission was invisible,
            // and automatic output could resume with no new permission. The
            // constraint is now a bounded summary the whole lineage shares.
            var kit = new FactKit();
            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            FactSession s0 = kit.Session("SERIAL-N");
            EpisodeId first = FactKit.OnsetHot(kit.HotSlot(s0), 70m, 3).Handle!.Id;
            AttemptHandle original = kit.Allocate(kit.PlanAutomatic(first), tracked.Binding);
            Assert.Equal(AttemptRunOutcome.Requested, AttemptRunner.Run(original, tracked.Submit));   // in flight, never finishes
            s0.End(T0, "disconnected");

            // Well past the old limit, in one process, ending each session
            // before the next continues it — except the last, which stays live.
            const int hops = 64 + 6;
            FactSession? live = null;
            EpisodeId latest = first;
            for (int i = 0; i < hops; i++)
            {
                FactSession next = kit.Session("SERIAL-N");
                PublicationResult c = FactKit.ContinueHot(kit.HotSlot(next), 70m, 3);
                Assert.Equal(PublicationOutcome.Accepted, c.Outcome);
                GrantSnapshot inherited = Assert.Single(c.Fact!.Grants);
                Assert.Equal(GrantOrigin.Inherited, inherited.Origin);
                Assert.NotNull(inherited.InheritedFromId);
                latest = c.Handle!.Id;
                if (i < hops - 1) next.End(T0, "disconnected"); else live = next;
            }
            Assert.NotNull(live);
            Assert.True(kit.Store.All.Count > 64);

            // POSITIVE CONTROL: with nothing reported, the last successor's
            // inherited permission is effective and it is eligible.
            FactSnapshot before = kit.Store.Find(latest)!;
            Assert.True(before.IsLive);
            Assert.Equal(PauseCause.None, before.Pause);
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(before));

            // The ORIGINAL attempt, under the original permission, is now
            // reported cancelled for no known reason — after all those hops.
            Assert.Equal(EvidenceResult.Recorded, original.Report(TransportEvidence.Cancelled(5, CancelCause.Unknown)));

            FactSnapshot after = kit.Store.Find(latest)!;
            Assert.Equal(PauseCause.UnknownCancellation, after.Pause);
            Assert.Equal(PauseCause.UnknownCancellation, Assert.Single(after.Grants).Blocker);
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(after));
            Assert.Equal(PreparationOutcome.NotEligible,
                kit.Presentation.Prepare(latest, PlanRequest.Automatic(VerbosityLevel.Chatty)).Outcome);
            Assert.Equal(1, tracked.NativeCalls);                                    // nothing resumed

            // Every episode in the line reads the same constraint — the first,
            // and one in the middle whose own session ended long ago.
            Assert.Equal(PauseCause.UnknownCancellation, kit.Store.Find(first)!.Grants.Single().Blocker);
            FactSnapshot middle = kit.Store.All.Single(f => f.PredecessorEpisode == first);
            Assert.Equal(PauseCause.UnknownCancellation, middle.Grants.Single().Blocker);

            // And one more continuation, made AFTER the report, is held too.
            live!.End(T0, "disconnected");
            PublicationResult later = FactKit.ContinueHot(kit.HotSlot(kit.Session("SERIAL-N")), 70m, 3);
            Assert.Equal(PublicationOutcome.Accepted, later.Outcome);
            Assert.Equal(PauseCause.UnknownCancellation, later.Fact!.Pause);
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(later.Fact));
        }

        [Fact]
        public void CompleteInventoryDoesNotProveOnset()
        {
            // Astra's first-onset ruling (2026-09-24): a complete inventory
            // establishes that nothing is on record; it cannot establish that
            // the condition began now rather than being already active when
            // the session first observed it. A first observation with
            // NoPriorRecord is retained, listed and held — no grant, no plan,
            // no native call, no receipt — until the owner supplies onset
            // evidence. Whether such a condition SHOULD speak is Noel's open
            // question; this test pins the current contract, not a decision.
            using var dir = new TempFactDir();
            var kit = new FactKit();
            var journal = new FactJournal(kit.Store, dir.Path);
            Assert.True(journal.TakeLease());
            journal.LoadHistory();
            Assert.Equal(HistoryLoadState.Loaded, kit.Store.Load);
            Assert.True(kit.Store.Project(FactView.Pending, null).Predicates.CompleteInventory);   // the inventory IS complete

            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            int tones = 0;
            var receipts = new ReceiptRequestAdapter(kit.Registry.RegisterReceiptAdapter("r"),
                                                     _ => { tones++; return ToneRequestResult.PlaybackReported; });

            FactSession session = kit.Session("SERIAL-FIRST");
            SlotPublisher pa = kit.HotSlot(session);
            PublicationResult found = FactKit.OpenHot(pa, 74m, 3);              // already hot; the owner knows of no record
            Assert.Equal(PublicationOutcome.Accepted, found.Outcome);
            FactSnapshot fact = found.Fact!;
            Assert.True(fact.Validity.IsCurrent);
            Assert.True(fact.IsLive);
            Assert.True(fact.IsPending);                                          // retained, listed, reachable
            Assert.Equal(3, fact.Unpresented.Count);
            Assert.Equal(PauseCause.OnsetNotEstablished, fact.Pause);
            Assert.Empty(fact.Grants);                                            // no occurrence grant
            Assert.Equal(ReceiptPolicy.None, fact.Receipt.Policy);                // no receipt allowance
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(fact));
            Assert.Equal(PreparationOutcome.NotEligible,
                kit.Presentation.Prepare(fact.Id, PlanRequest.Automatic(VerbosityLevel.Chatty)).Outcome);
            Assert.Equal(ReceiptAttemptOutcome.NotClaimed, receipts.RequestFor(fact.Id));
            Assert.Equal(0, tracked.NativeCalls);
            Assert.Equal(0, tones);

            // The row says what is true: held, not paused, not a reconnect.
            Assert.Equal("facts.state.held_onset_not_established", FactListPresenter.StateRole(fact));
            Assert.True(Lexicon.Contains("facts.state.held_onset_not_established"));

            // A later sample changes none of that: still the same information,
            // still no grant.
            Assert.Equal(PublicationOutcome.Accepted,
                pa.Update(found.Handle!, FactKit.Capture(pa, FactKit.Temp(74m)), FactTransition.Sample(), fact.Revision).Outcome);
            FactSnapshot sampled = kit.Store.Find(fact.Id)!;
            Assert.Empty(sampled.Grants);
            Assert.Equal(PauseCause.OnsetNotEstablished, sampled.Pause);
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(sampled));
            Assert.Equal(0, tracked.NativeCalls);
            Assert.Equal(0, tones);

            // POSITIVE CONTROL: an explicitly evidenced onset, captured AFTER a
            // quiet boundary, permits an automatic attempt and exactly one
            // policy-authorized receipt.
            kit.Registry.Quiet.Observe("ctrl before the rise");
            PublicationResult onset = FactKit.OnsetHot(kit.HotSlot(session, "psu"), 74m, 3);
            Assert.Equal(PublicationOutcome.Accepted, onset.Outcome);
            Assert.Equal(GrantOrigin.Occurrence, Assert.Single(onset.Fact!.Grants).Origin);
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(onset.Fact));
            Assert.Equal(AttemptRunOutcome.Requested,
                AttemptRunner.Run(kit.Allocate(kit.PlanAutomatic(onset.Handle!.Id), tracked.Binding), tracked.Submit));
            Assert.Equal(1, tracked.NativeCalls);
            Assert.Equal(ReceiptAttemptOutcome.ToneRequested, receipts.RequestFor(onset.Handle.Id));
            Assert.Equal(ReceiptAttemptOutcome.NotClaimed, receipts.RequestFor(onset.Handle.Id));
            Assert.Equal(1, tones);
            // And the first observation is still held, whatever its neighbour earned.
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(kit.Store.Find(fact.Id)!));
            journal.Dispose();

            // PARTIAL inventory: another instance holds a live shard, so a
            // record for this condition may exist unread. That is unknown
            // continuity — a different truth from an established empty
            // record, and held for a different reason, so fixing one branch
            // cannot quietly weaken the other. Both are silent.
            using var shared = new TempFactDir();
            var other = new FactKit();
            using var holder = new FactJournal(other.Store, shared.Path);
            Assert.True(holder.TakeLease());
            FactKit.OnsetHot(other.HotSlot(other.Session("SERIAL-ELSEWHERE")));
            Assert.True(holder.Write());
            var mine = new FactKit();
            var mj = new FactJournal(mine.Store, shared.Path);
            Assert.True(mj.TakeLease());
            mj.LoadHistory();
            Assert.False(mine.Store.Project(FactView.Pending, null).Predicates.CompleteInventory);
            int partialTones = 0;
            var partialReceipts = new ReceiptRequestAdapter(mine.Registry.RegisterReceiptAdapter("r"),
                                                            _ => { partialTones++; return ToneRequestResult.Requested; });
            PublicationResult partial = FactKit.OpenHot(mine.HotSlot(mine.Session("SERIAL-FIRST")), 74m, 3);
            Assert.Equal(PublicationOutcome.Accepted, partial.Outcome);
            Assert.Equal(PauseCause.ContinuityUnknown, partial.Fact!.Pause);
            Assert.NotEqual(fact.Pause, partial.Fact.Pause);
            Assert.Empty(partial.Fact.Grants);
            Assert.True(partial.Fact.IsPending);
            Assert.False(mine.Store.IsEligibleForAutomaticDelivery(partial.Fact));
            Assert.Equal(ReceiptAttemptOutcome.NotClaimed, partialReceipts.RequestFor(partial.Handle!.Id));
            Assert.Equal(0, partialTones);
            mj.Dispose();
        }

        /// <summary>
        /// A schema-2 shard exactly as Track M2's writer emitted one: a fully
        /// presented occurrence and a continuity entry that records only its
        /// pause and baseline — no per-assertion evidence, no receipt.
        /// </summary>
        private const string SchemaTwoShard = """
        {
          "schema": 2,
          "writer": "0123456789abcdef0123456789abcdef",
          "generation": 1,
          "mutation": 3,
          "compacted": { "covered": 0, "reviewedOnly": 0, "forgettableUnpresented": 0, "lowerBound": false },
          "facts": [
            {
              "episode": { "writer": "0123456789abcdef0123456789abcdef", "ordinal": 1, "origin": "Allocated" },
              "revision": 2,
              "owner": "synthetic temperature owner",
              "contract": { "name": "syn.temperature", "revision": 1 },
              "condition": { "name": "pa", "definitionRevision": 0 },
              "claim": "condition.hot",
              "scope": { "id": 1, "kind": "RadioSession", "radio": "SERIAL-V2" },
              "occurrence": null,
              "priority": "OperatorAlarm",
              "message": {
                "key": "syn.hot", "classification": "Message", "catalogGeneration": 1,
                "delivery": { "shelfLife": "Persistent", "validity": "syn.active", "historyKey": "syn.hot.history", "receipt": "Warning" }
              },
              "validity": { "state": "Ended", "ended": "EndedObservationContext", "unknown": null, "supersededBy": null, "note": "disconnected", "asOfUtc": "2026-09-24T12:00:00.0000000Z" },
              "observedUtc": "2026-09-24T12:00:00.0000000Z",
              "lastEventSequence": 1,
              "observationRevision": 1,
              "materialRevision": 1,
              "current": { "temperature": { "kind": "Decimal", "v": "70" }, "duration": { "kind": "Integer", "v": "3" } },
              "baseline": { "temperature": { "kind": "Decimal", "v": "70" }, "duration": { "kind": "Integer", "v": "3" } },
              "events": [ { "ordinal": 1, "sequence": 1, "source": null, "observedUtc": "2026-09-24T12:00:00.0000000Z", "effect": "opened" } ],
              "eventsDropped": 0,
              "materials": [
                { "id": 1, "name": "core", "value": { "kind": "Text", "v": "syn.hot" }, "kind": "Core", "supersedes": null, "relatesTo": null, "introducedAt": 1 },
                { "id": 2, "name": "temperature", "value": { "kind": "Decimal", "v": "70" }, "kind": "Initial", "supersedes": null, "relatesTo": null, "introducedAt": 1 },
                { "id": 3, "name": "duration", "value": { "kind": "Integer", "v": "3" }, "kind": "Initial", "supersedes": null, "relatesTo": null, "introducedAt": 1 }
              ],
              "nextMaterial": 4,
              "coverage": { "presented": [ 1, 2, 3 ], "reviewed": [] },
              "pause": "None",
              "legacyUnverified": false,
              "attempts": [],
              "compactedAttempts": 1,
              "compactedLowerBound": false,
              "receipt": { "policy": "Warning", "state": "PlaybackReported", "consumed": true, "id": 1 },
              "detail": "",
              "detailTruncated": false
            }
          ],
          "overlays": [],
          "continuity": [
            {
              "radio": "SERIAL-V2", "condition": "pa", "contract": "syn.temperature", "definitionRevision": 0, "pause": "None",
              "baseline": { "temperature": { "kind": "Decimal", "v": "70" }, "duration": { "kind": "Integer", "v": "3" } },
              "lastEpisode": { "writer": "0123456789abcdef0123456789abcdef", "ordinal": 1, "origin": "Allocated" }
            }
          ],
          "issues": []
        }
        """;
    }
}
