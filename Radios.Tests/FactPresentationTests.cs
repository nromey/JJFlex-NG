#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Radios;
using Radios.Facts;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// D-series: completion can only discharge what an allocated plan carried,
    /// as reported by an adapter able to report it; a receipt permit gates the
    /// actual tone request.
    /// </summary>
    /// <remarks>
    /// These read the text the PRODUCTION renderer put in the plan, and count
    /// calls at the lowest injected boundary. They say nothing about a real
    /// screen reader stopping, and nothing about a sound reaching anyone.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class FactPresentationTests
    {
        private static MaterialUnit Unit(FactSnapshot f, string name) => f.Materials.Last(m => m.Name == name);

        [Fact]
        public void D1_CompletionCoversOnlyTheAllocatedPayload()
        {
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session());
            var tracked = new RecordingTransport(kit.Registry, "tracked", TransportCapability.ReportsCompletion);
            EpisodeId id = FactKit.OnsetHot(publisher, 70m, 3).Handle!.Id;
            FactSnapshot opened = kit.Store.Find(id)!;

            // The short tier: its words leave the duration out.
            PresentationPlan terse = kit.PlanAutomatic(id, VerbosityLevel.Terse);
            Assert.Equal("PA at 70 degrees.", terse.Text);
            Assert.Contains(Unit(opened, "temperature").Id, terse.Coverage.Keys);
            Assert.DoesNotContain(Unit(opened, "duration").Id, terse.Coverage.Keys);
            AttemptHandle shortAttempt = kit.Allocate(terse, tracked.Binding);
            AttemptRunner.Run(shortAttempt, tracked.Submit);
            shortAttempt.Report(TransportEvidence.Completed(5));

            FactSnapshot afterShort = kit.Store.Find(id)!;
            Assert.Equal("PA at 70 degrees.", tracked.Sent.Single());
            Assert.Contains(Unit(opened, "duration").Id, afterShort.Unpresented);
            Assert.True(afterShort.IsPending);

            // The full tier carries it, and completing it discharges exactly that.
            PresentationPlan chatty = kit.PlanAutomatic(id, VerbosityLevel.Chatty);
            Assert.Equal("PA at 70 degrees for 3 minutes.", chatty.Text);
            AttemptHandle fullAttempt = kit.Allocate(chatty, tracked.Binding);
            AttemptRunner.Run(fullAttempt, tracked.Submit);
            fullAttempt.Report(TransportEvidence.Completed(5));
            FactSnapshot afterFull = kit.Store.Find(id)!;
            Assert.Empty(afterFull.Unpresented);
            Assert.False(afterFull.IsPending);
            Assert.Equal(afterFull.Required.OrderBy(x => x), afterFull.Covered.OrderBy(x => x));

            // A correction published before the old callback stays owed.
            SlotPublisher publisher2 = kit.HotSlot(kit.Session("SERIAL-2"));
            PublicationResult openedSecond = FactKit.OnsetHot(publisher2, 70m, 3);
            EpisodeId second = openedSecond.Handle!.Id;
            AttemptHandle before = kit.Allocate(kit.PlanAutomatic(second), tracked.Binding);
            AttemptRunner.Run(before, tracked.Submit);
            publisher2.Update(openedSecond.Handle, FactKit.Capture(publisher2, FactKit.Temp(71m)),
                FactTransition.Correction(new[] { new MaterialDeclaration("temperature", FactValue.Of(71m)) }),
                kit.Store.Find(second)!.Revision);
            before.Report(TransportEvidence.Completed(5));

            FactSnapshot corrected = kit.Store.Find(second)!;
            MaterialUnit newer = Unit(corrected, "temperature");
            Assert.Equal(71m, newer.Value.AsDecimal);
            Assert.Contains(newer.Id, corrected.Unpresented);
            Assert.True(corrected.IsPending);
        }

        [Fact]
        public void D2_RequestOnlyNeverBecomesCompletion()
        {
            var kit = new FactKit();
            var requestOnly = new RecordingTransport(kit.Registry, "plain", TransportCapability.RequestOnly);
            EpisodeId id = FactKit.OnsetHot(kit.HotSlot(kit.Session())).Handle!.Id;

            AttemptHandle attempt = kit.Allocate(kit.PlanAutomatic(id), requestOnly.Binding);
            Assert.Equal(AttemptRunOutcome.Requested, AttemptRunner.Run(attempt, requestOnly.Submit));

            FactSnapshot f = kit.Store.Find(id)!;
            AttemptSnapshot a = f.Attempts.Single();
            Assert.Equal(AttemptDisposition.RequestedOnly, a.Disposition);
            Assert.Single(a.Evidence, e => e.Kind == EvidenceKind.RequestIssued);
            Assert.Empty(a.EstablishedCoverage);
            Assert.True(f.IsPending);

            // No fabricated Ctrl cause, no turn release: the fact is still owed
            // and still permitted, and nothing claims the reader stopped.
            Assert.Equal(PauseCause.None, f.Pause);
            Assert.True(f.PermittedNow);

            // A request-only adapter cannot claim completion by choosing the enum.
            Assert.Equal(EvidenceResult.NotPermitted, attempt.Report(TransportEvidence.Completed(9)));
            Assert.True(kit.Store.Find(id)!.IsPending);

            // A throwing request: the exact failure, no coverage.
            EpisodeId other = FactKit.OnsetHot(kit.HotSlot(kit.Session("SERIAL-2"))).Handle!.Id;
            AttemptHandle throws = kit.Allocate(kit.PlanAutomatic(other), requestOnly.Binding);
            Assert.Equal(AttemptRunOutcome.RequestThrew,
                AttemptRunner.Run(throws, (_, _) => throw new InvalidOperationException("reader gone")));
            AttemptSnapshot failed = kit.Store.Find(other)!.Attempts.Single();
            Assert.Equal(AttemptDisposition.RequestFailed, failed.Disposition);
            Assert.Empty(failed.EstablishedCoverage);

            // POSITIVE CONTROL: a positively completing adapter discharges its plan.
            var tracked = new RecordingTransport(kit.Registry, "tracked", TransportCapability.ReportsCompletion);
            AttemptHandle good = kit.Allocate(kit.PlanAutomatic(id), tracked.Binding);
            AttemptRunner.Run(good, tracked.Submit);
            Assert.Equal(EvidenceResult.Recorded, good.Report(TransportEvidence.Completed(9)));
            Assert.False(kit.Store.Find(id)!.IsPending);
        }

        [Fact]
        public void D3_AttemptAttributionSurvivesReentryAndReplacement()
        {
            var kit = new FactKit();
            var reader1 = new RecordingTransport(kit.Registry, "reader 1", TransportCapability.ReportsCompletion);
            var impostor = new RecordingTransport(kit.Registry, "impostor", TransportCapability.ReportsCompletion);
            SlotPublisher publisher = kit.HotSlot(kit.Session());
            PublicationResult opened = FactKit.OnsetHot(publisher);
            EpisodeId id = opened.Handle!.Id;

            // Completion reported synchronously INSIDE submission, then again.
            AttemptHandle attempt = kit.Allocate(kit.PlanAutomatic(id, VerbosityLevel.Terse), reader1.Binding);
            EvidenceResult inside = EvidenceResult.Foreign, again = EvidenceResult.Foreign;
            AttemptRunner.Run(attempt, (plan, handle) =>
            {
                handle.BindTicket("t-1");
                inside = handle.Report(TransportEvidence.Completed(5, "t-1"));
                again = handle.Report(TransportEvidence.Completed(5, "t-1"));
                return "t-1";
            });
            Assert.Equal(EvidenceResult.Recorded, inside);
            Assert.Equal(EvidenceResult.Duplicate, again);
            // The request-return that followed did not downgrade it.
            Assert.Equal(AttemptDisposition.Completed, kit.Store.Find(id)!.Attempts.Single().Disposition);

            // Foreign ticket, foreign binding: nothing changes.
            Assert.Equal(EvidenceResult.Foreign, attempt.Report(TransportEvidence.Cancelled(7, CancelCause.Unknown, ticket: "t-2")));
            Assert.Equal(EvidenceResult.Foreign, impostor.Binding.Report(attempt.Id, TransportEvidence.Cancelled(7, CancelCause.Unknown)));
            Assert.Equal(AttemptDisposition.Completed, kit.Store.Find(id)!.Attempts.Single().Disposition);

            // The reader is replaced; newer material is presented on the new
            // binding; an old valid completion then arrives for an old attempt.
            AttemptHandle oldPending = kit.Allocate(kit.PlanAutomatic(id, VerbosityLevel.Chatty), reader1.Binding);
            AttemptRunner.Run(oldPending, reader1.Submit);
            var reader2 = new RecordingTransport(kit.Registry, "reader 2", TransportCapability.ReportsCompletion);
            FactSnapshot now = kit.Store.Find(id)!;
            publisher.Worsen(opened.Handle, FactKit.Capture(publisher, FactKit.Temp(74m)), FactKit.Worse(now, "w-1", 74m));
            AttemptHandle fresh = kit.Allocate(kit.PlanAutomatic(id, VerbosityLevel.Chatty), reader2.Binding);
            AttemptRunner.Run(fresh, reader2.Submit);

            Assert.Equal(EvidenceResult.Recorded, oldPending.Report(TransportEvidence.Completed(5)));
            FactSnapshot afterLate = kit.Store.Find(id)!;
            MaterialUnit worse = afterLate.Materials.Last(m => m.Kind == MaterialKind.Worsening);
            Assert.Contains(worse.Id, afterLate.Unpresented);   // the late completion carried the OLD words
            Assert.Equal(AttemptDisposition.RequestedOnly,
                afterLate.Attempts.Single(a => a.Id == fresh.Id).Disposition);   // the newer attempt is untouched

            // Cancellation requested, then a valid completion: both kept.
            EpisodeId other = FactKit.OnsetHot(kit.HotSlot(kit.Session("SERIAL-2"))).Handle!.Id;
            AttemptHandle both = kit.Allocate(kit.PlanAutomatic(other), reader2.Binding);
            AttemptRunner.Run(both, reader2.Submit);
            both.Report(TransportEvidence.CancellationRequested(3, "quiet", "operator pressed Ctrl"));
            both.Report(TransportEvidence.Completed(4));
            AttemptSnapshot bothSnap = kit.Store.Find(other)!.Attempts.Single();
            Assert.True(bothSnap.CancellationRequested);
            Assert.Equal(AttemptDisposition.Completed, bothSnap.Disposition);

            // A contradictory terminal pair: disputed, discharges nothing.
            EpisodeId third = FactKit.OnsetHot(kit.HotSlot(kit.Session("SERIAL-3"))).Handle!.Id;
            AttemptHandle contradicted = kit.Allocate(kit.PlanAutomatic(third), reader2.Binding);
            AttemptRunner.Run(contradicted, reader2.Submit);
            Assert.Equal(EvidenceResult.Recorded, contradicted.Report(TransportEvidence.Completed(3)));
            Assert.Equal(EvidenceResult.Disputed, contradicted.Report(TransportEvidence.Cancelled(4, CancelCause.Unknown)));
            FactSnapshot disputed = kit.Store.Find(third)!;
            Assert.Equal(AttemptDisposition.Disputed, disputed.Attempts.Single().Disposition);
            Assert.True(disputed.IsPending);
            Assert.Empty(disputed.Covered);
        }

        [Fact]
        public void D4_ReceiptGateLimitsActualRequests()
        {
            using var dir = new TempFactDir();
            var kit = new FactKit();
            int tones = 0;
            var adapter = new ReceiptRequestAdapter(kit.Registry.RegisterReceiptAdapter("tones"),
                                                    _ => { Interlocked.Increment(ref tones); return ToneRequestResult.Requested; });
            SlotPublisher publisher = kit.HotSlot(kit.Session());
            CapturedFactEvent ev = FactKit.Capture(publisher, FactKit.Temp(70m));
            var materials = new[] { new MaterialDeclaration("temperature", FactValue.Of(70m)) };
            OpenOptions onset = FactKit.Onset();
            PublicationResult opened = publisher.Open(ev, "condition.hot", FactKit.HotKey, materials, onset);
            EpisodeId id = opened.Handle!.Id;

            // Two callers race for one occurrence's tone.
            using var start = new Barrier(2);
            var outcomes = new ReceiptAttemptOutcome[2];
            var racers = Enumerable.Range(0, 2).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                outcomes[i] = adapter.RequestFor(id);
            })).ToArray();
            foreach (Thread t in racers) t.Start();
            foreach (Thread t in racers) t.Join();
            Assert.Equal(1, tones);
            Assert.Single(outcomes, o => o == ReceiptAttemptOutcome.ToneRequested);

            // Repeated admission, a speech retry, another request: still one.
            Assert.Equal(PublicationOutcome.Duplicate, publisher.Open(ev, "condition.hot", FactKit.HotKey, materials, onset).Outcome);
            Assert.Equal(ReceiptAttemptOutcome.NotClaimed, adapter.RequestFor(id));
            Assert.Equal(1, tones);

            // A genuinely new occurrence gets its own.
            EpisodeId other = FactKit.OnsetHot(kit.HotSlot(kit.Session("SERIAL-2"))).Handle!.Id;
            Assert.Equal(ReceiptAttemptOutcome.ToneRequested, adapter.RequestFor(other));
            Assert.Equal(2, tones);

            // A permit claimed before Ctrl, used after it: the stale cue is refused.
            EpisodeId cued = FactKit.OnsetHot(kit.HotSlot(kit.Session("SERIAL-3"))).Handle!.Id;
            ReceiptEndpoint endpoint = kit.Registry.RegisterReceiptAdapter("cue timer");
            ReceiptPermit held = endpoint.TryClaim(cued).Permit!;
            kit.Registry.Quiet.Observe("ctrl during the lead-in");
            Assert.Equal(ReceiptAttemptOutcome.Withheld, adapter.Fire(held));
            Assert.Equal(2, tones);
            Assert.Equal(ReceiptState.Withheld, kit.Store.Find(cued)!.Receipt.State);

            // Claimed, then the process ends before the request: no replay after restart.
            var crash = new FactKit();
            EpisodeId interrupted = FactKit.OnsetHot(crash.HotSlot(crash.Session("SERIAL-4"))).Handle!.Id;
            Assert.NotNull(crash.Registry.RegisterReceiptAdapter("r").TryClaim(interrupted).Permit);
            using (var journal = new FactJournal(crash.Store, dir.Path))
            {
                journal.TakeLease();
                journal.Write();
            }
            var restarted = new FactKit();
            var reader = new FactJournal(restarted.Store, dir.Path);
            reader.TakeLease();
            reader.LoadHistory();
            reader.Dispose();
            int replayTones = 0;
            var replay = new ReceiptRequestAdapter(restarted.Registry.RegisterReceiptAdapter("tones"), _ => { replayTones++; return ToneRequestResult.Requested; });
            FactSnapshot loaded = restarted.Store.Find(interrupted)!;
            Assert.Equal(ReceiptState.OutcomeUnknown, loaded.Receipt.State);
            Assert.True(loaded.Receipt.FromPreviousProcess);
            Assert.Equal(ReceiptAttemptOutcome.NotClaimed, replay.RequestFor(interrupted));
            Assert.Equal(0, replayTones);
        }

        [Fact]
        public void D5_ASelectedReadIsBoundToTheShownSnapshot()
        {
            // Sol, D: RequestRead captured the shown material, PreparePlan
            // rendered the current fact without comparing. Now it compares.
            var kit = new FactKit();
            var tracked = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            FactSession session = kit.Session("SERIAL-D");
            SlotPublisher publisher = kit.HotSlot(session);
            PublicationResult opened = FactKit.OpenHot(publisher, 70m, 3);
            EpisodeId id = opened.Handle!.Id;
            using FactListView view = new FactListPresenter(kit.Store).OpenView();

            // Selected, then a NON-MATERIAL sample changes the value the words
            // would use, before the plan is prepared. The unit set is the same;
            // only the material fingerprint can tell the words moved.
            RenderedDetailSnapshot shown = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == id))!;
            Assert.True(view.Installed(shown));
            SelectedReadGrant read = view.RequestRead(shown.Token, out _)!;
            publisher.Update(opened.Handle, FactKit.Capture(publisher, FactKit.Temp(71m)), FactTransition.Sample(), kit.Store.Find(id)!.Revision);
            PlanPreparation moved = kit.Presentation.Prepare(id, PlanRequest.SelectedRead(read, VerbosityLevel.Chatty));
            Assert.Equal(PreparationOutcome.ShownSnapshotChanged, moved.Outcome);
            Assert.Null(moved.Plan);

            // Selected again, then the MATERIAL changes before the plan is prepared.
            RenderedDetailSnapshot shown71 = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == id))!;
            Assert.True(view.Installed(shown71));
            SelectedReadGrant read71 = view.RequestRead(shown71.Token, out _)!;
            publisher.Update(opened.Handle, FactKit.Capture(publisher, FactKit.Temp(72m)),
                FactTransition.Correction(new[] { new MaterialDeclaration("temperature", FactValue.Of(72m)) }),
                kit.Store.Find(id)!.Revision);
            PlanPreparation stale = kit.Presentation.Prepare(id, PlanRequest.SelectedRead(read71, VerbosityLevel.Chatty));
            Assert.Equal(PreparationOutcome.ShownSnapshotChanged, stale.Outcome);
            Assert.Null(stale.Plan);
            Assert.Equal(0, tracked.NativeCalls);

            // POSITIVE CONTROL: a fresh token on the refreshed detail reads,
            // and the words are the value that was shown.
            RenderedDetailSnapshot refreshed = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == id))!;
            Assert.Contains("72", refreshed.Text, StringComparison.Ordinal);
            Assert.True(view.Installed(refreshed));
            SelectedReadGrant current = view.RequestRead(refreshed.Token, out _)!;
            PlanPreparation ok = kit.Presentation.Prepare(id, PlanRequest.SelectedRead(current, VerbosityLevel.Chatty));
            Assert.True(ok.Prepared);
            Assert.Equal("PA at 72 degrees for 3 minutes.", ok.Plan!.Text);
            Assert.Equal(AttemptRunOutcome.Requested, AttemptRunner.Run(kit.Allocate(ok.Plan, tracked.Binding), tracked.Submit));

            // A non-material sample after preparation is still caught at the
            // final start gate: the words would be wrong.
            RenderedDetailSnapshot again = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == id))!;
            view.Installed(again);
            SelectedReadGrant later = view.RequestRead(again.Token, out _)!;
            PresentationPlan prepared = kit.Presentation.Prepare(id, PlanRequest.SelectedRead(later, VerbosityLevel.Chatty)).Plan!;
            publisher.Update(opened.Handle, FactKit.Capture(publisher, FactKit.Temp(73m)), FactTransition.Sample(), kit.Store.Find(id)!.Revision);
            Assert.Equal(NotStartedReason.Superseded, kit.Allocate(prepared, tracked.Binding).TryCommitStart().Reason);

            // With G2: a token bound to the predecessor stays bound to it
            // across a reconnect. The successor is never substituted; the same
            // material is read honestly as history.
            RenderedDetailSnapshot beforeReconnect = view.RenderDetail(view.Snapshot(FactView.Pending).Items.Single(i => i.Fact?.Id == id))!;
            view.Installed(beforeReconnect);
            SelectedReadGrant bound = view.RequestRead(beforeReconnect.Token, out _)!;
            session.End(FactKit.T0, "disconnected");
            PublicationResult successor = FactKit.ContinueHot(kit.HotSlot(kit.Session("SERIAL-D")), 72m, 3);
            Assert.Equal(PublicationOutcome.Accepted, successor.Outcome);
            PlanPreparation historical = kit.Presentation.Prepare(id, PlanRequest.SelectedRead(bound, VerbosityLevel.Chatty));
            Assert.True(historical.Prepared);
            Assert.Equal(id, historical.Plan!.Episode);
            Assert.NotEqual(successor.Handle!.Id, historical.Plan.Episode);
            Assert.True(historical.Plan.Historical);
            Assert.Equal("Earlier the PA was at 72 degrees.", historical.Plan.Text);
        }

        [Fact]
        public void D_UnclassifiedPreparationReturnsATypedUnresolvedResultAndKeepsTheFact()
        {
            // The unclassified-key decision is still with Noel. The binding
            // model carries either reading: a typed result naming why, with the
            // fact and its owed material, and the fact kept and reachable.
            var kit = new FactKit();
            SlotPublisher notes = kit.NotesSlot(kit.Session());
            EpisodeId mystery = FactKit.OpenNote(notes, FactKit.MysteryKey).Handle!.Id;
            EpisodeId label = FactKit.OpenNote(kit.NotesSlot(kit.Session("SERIAL-2")), FactKit.LabelKey).Handle!.Id;

            PlanPreparation unclassified = kit.Presentation.Prepare(mystery, PlanRequest.Automatic(VerbosityLevel.Chatty));
            Assert.Equal(PreparationOutcome.Unresolved, unclassified.Outcome);
            Assert.Equal(UnresolvedPolicyReason.Unclassified, unclassified.Unresolved);
            Assert.NotNull(unclassified.Fact);
            Assert.NotEmpty(unclassified.Fact!.Unpresented);

            Assert.Equal(UnresolvedPolicyReason.TextOnly,
                kit.Presentation.Prepare(label, PlanRequest.Automatic(VerbosityLevel.Chatty)).Unresolved);

            Assert.True(kit.Store.Find(mystery)!.IsPending);
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(kit.Store.Find(mystery)!));

            // POSITIVE CONTROL: a classified message on the same slot prepares.
            EpisodeId cut = FactKit.OnsetNote(kit.NotesSlot(kit.Session("SERIAL-3")), FactKit.CutKey).Handle!.Id;
            Assert.True(kit.Presentation.Prepare(cut, PlanRequest.Automatic(VerbosityLevel.Chatty)).Prepared);
        }
    }
}
