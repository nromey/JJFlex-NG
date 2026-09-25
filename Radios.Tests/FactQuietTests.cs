#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.Facts;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// B-series: quiet is ordered at capture, and only an owner-issued
    /// worsening crosses it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every worsening here comes from a SYNTHETIC owner through a registered
    /// contract. These tests prove the store honours an owner-issued transition
    /// and its ordering; they do not and cannot prove any real alarm's 2 C or
    /// 0.2 V threshold, which belongs to the alarm owner.
    /// </para>
    /// <para>
    /// Ruled by Noel 2026-09-24 07:46 (#617): Ctrl silences, and a worsening
    /// condition speaks again.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public class FactQuietTests
    {
        private static readonly DateTime T0 = FactKit.T0;

        [Fact]
        public void B1_CapturedBeforeQuietAdmittedAfterQuietStaysPaused_ThroughTheProductionSourceAdapter()
        {
            var kit = new FactKit();
            FactSession session = kit.Session();
            SlotPublisher publisher = kit.HotSlot(session);
            var transport = new RecordingTransport(kit.Registry, "t", TransportCapability.ReportsCompletion);
            int tones = 0;
            var receipts = new ReceiptRequestAdapter(kit.Registry.RegisterReceiptAdapter("r"), _ => { tones++; return ToneRequestResult.Requested; });

            // The adapter's first handoff is a queue we hold.
            var held = new Queue<Action>();
            var adapter = new FactSourceAdapter(publisher, held.Enqueue);
            EpisodeHandle? opened = null;
            CaptureResult captured = adapter.OnSourceEvent(FactKit.Temp(70m), T0, "src-1", ev =>
                opened = publisher.Open(ev, "condition.hot", FactKit.HotKey,
                    new[] { new MaterialDeclaration("temperature", FactValue.Of(70m)) }).Handle);
            Assert.True(captured.Captured);
            Assert.Null(opened);   // nothing admitted yet: the evaluation is still queued

            // Ctrl, while the evaluation waits.
            kit.Registry.Quiet.Observe("operator pressed Ctrl");

            // Now deliver it.
            held.Dequeue()();
            FactSnapshot fact = kit.Store.Find(opened!.Id)!;

            Assert.True(fact.IsPending);
            Assert.True(fact.AutomaticPaused);
            Assert.Equal(PauseCause.OperatorQuiet, fact.Pause);
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(fact));
            Assert.Equal(PreparationOutcome.NotEligible,
                kit.Presentation.Prepare(fact.Id, PlanRequest.Automatic(VerbosityLevel.Chatty)).Outcome);
            Assert.Equal(ReceiptAttemptOutcome.NotClaimed, receipts.RequestFor(fact.Id));
            Assert.Equal(0, transport.NativeCalls);
            Assert.Equal(0, tones);

            // POSITIVE CONTROL: a genuinely new event captured AFTER the quiet.
            SlotPublisher other = kit.HotSlot(session, "psu");
            var later = new FactSourceAdapter(other, held.Enqueue);
            EpisodeHandle? second = null;
            later.OnSourceEvent(FactKit.Temp(80m), T0, "src-2", ev =>
                second = other.Open(ev, "condition.hot", FactKit.HotKey,
                    new[] { new MaterialDeclaration("temperature", FactValue.Of(80m)) }).Handle);
            held.Dequeue()();

            FactSnapshot fresh = kit.Store.Find(second!.Id)!;
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(fresh));
            Assert.Equal(AttemptRunOutcome.Requested,
                AttemptRunner.Run(kit.Allocate(kit.PlanAutomatic(fresh.Id), transport.Binding), transport.Submit));
            Assert.Equal(1, transport.NativeCalls);
            Assert.Equal(ReceiptAttemptOutcome.ToneRequested, receipts.RequestFor(fresh.Id));
            Assert.Equal(1, tones);
        }

        [Fact]
        public void B2_OnlyEvidencedWorseningCrossesQuiet_SyntheticOwner()
        {
            var kit = new FactKit();
            FactSession session = kit.Session();
            SlotPublisher publisher = kit.HotSlot(session);
            SlotPublisher bystanderSlot = kit.HotSlot(session, "psu");
            int tones = 0;
            var receipts = new ReceiptRequestAdapter(kit.Registry.RegisterReceiptAdapter("r"), _ => { tones++; return ToneRequestResult.Requested; });

            PublicationResult opened = FactKit.OpenHot(publisher, 70m);
            PublicationResult bystander = FactKit.OpenHot(bystanderSlot, 60m);
            EpisodeHandle handle = opened.Handle!;
            Assert.Equal(ReceiptAttemptOutcome.ToneRequested, receipts.RequestFor(handle.Id));

            kit.Registry.Quiet.Observe("ctrl");
            Assert.True(kit.Store.Find(handle.Id)!.AutomaticPaused);

            long Rev() => kit.Store.Find(handle.Id)!.Revision;
            void StillPaused(string after)
            {
                FactSnapshot f = kit.Store.Find(handle.Id)!;
                Assert.True(f.AutomaticPaused, "re-armed by " + after);
                Assert.False(kit.Store.IsEligibleForAutomaticDelivery(f), "eligible after " + after);
            }

            publisher.Update(handle, FactKit.Capture(publisher, FactKit.Temp(70m)), FactTransition.Sample(), Rev());
            StillPaused("a repeated sample");
            publisher.Update(handle, FactKit.Capture(publisher, FactKit.Temp(71m)), FactTransition.Sample(), Rev());
            StillPaused("a non-material sample");
            publisher.Update(handle, FactKit.Capture(publisher, FactKit.Temp(70.5m)),
                FactTransition.Correction(new[] { new MaterialDeclaration("temperature", FactValue.Of(70.5m)) }), Rev());
            StillPaused("a material correction");
            publisher.Update(handle, FactKit.Capture(publisher, FactKit.Temp(70.5m)), FactTransition.Sample(note: "reminder"), Rev());
            StillPaused("a normal reminder");
            kit.Store.NoteChannelHealth(false);
            kit.Store.NoteChannelHealth(true);
            StillPaused("a backend recovery");
            using (FactListView view = new FactListPresenter(kit.Store).OpenView())
            {
                FactListSnapshot list = view.Snapshot(FactView.Pending);
                RenderedDetailSnapshot detail = view.RenderDetail(list.Items.First(i => i.Fact?.Id == handle.Id))!;
                view.Installed(detail);
            }
            StillPaused("a view arriving");

            // The owner's evaluator decides it is worse and says so.
            FactSnapshot before = kit.Store.Find(handle.Id)!;
            PublicationResult worse = publisher.Worsen(handle, FactKit.Capture(publisher, FactKit.Temp(73m)),
                FactKit.Worse(before, "w-1", 73m));
            Assert.Equal(PublicationOutcome.Accepted, worse.Outcome);

            FactSnapshot after = kit.Store.Find(handle.Id)!;
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(after));
            MaterialUnit worseUnit = after.Materials.Last();
            Assert.Equal(MaterialKind.Worsening, worseUnit.Kind);
            Assert.Equal(73m, worseUnit.Value.AsDecimal);

            // The permission is for the exact new material: the presentable
            // set now carries the new unit, and the earlier temperature unit
            // is still owed, not resumed.
            Assert.Contains(worseUnit.Id, after.Presentable);
            MaterialUnit earlier = after.Materials.First(u => u.Kind == MaterialKind.Correction);
            Assert.Contains(earlier.Id, after.Unpresented);
            Assert.DoesNotContain(earlier.Id, after.Presentable);

            // No global resume: the bystander stays paused.
            Assert.True(kit.Store.Find(bystander.Handle!.Id)!.AutomaticPaused);

            // And no second occurrence receipt.
            Assert.Equal(ReceiptAttemptOutcome.NotClaimed, receipts.RequestFor(handle.Id));
            Assert.Equal(1, tones);
        }

        [Fact]
        public void B3_ASecondQuietWinsOverDelayedWorsening_SyntheticOwner()
        {
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session());
            PublicationResult opened = FactKit.OpenHot(publisher);
            EpisodeHandle handle = opened.Handle!;

            kit.Registry.Quiet.Observe("Q1");
            CapturedFactEvent worseAfterQ1 = FactKit.Capture(publisher, FactKit.Temp(73m));
            kit.Registry.Quiet.Observe("Q2");   // evaluation was held; the operator pressed Ctrl again

            FactSnapshot baseline = kit.Store.Find(handle.Id)!;
            PublicationResult delayed = publisher.Worsen(handle, worseAfterQ1, FactKit.Worse(baseline, "w-1", 73m));

            // The information appears; no automatic start is authorised.
            Assert.Equal(PublicationOutcome.Accepted, delayed.Outcome);
            FactSnapshot held = kit.Store.Find(handle.Id)!;
            Assert.Contains(held.Materials, u => u.Kind == MaterialKind.Worsening);
            Assert.True(held.AutomaticPaused);
            Assert.Equal(PauseCause.OperatorQuiet, held.Pause);
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(held));

            // A duplicate of that worsening creates no further grant.
            Assert.Equal(PublicationOutcome.Duplicate,
                publisher.Worsen(handle, worseAfterQ1, FactKit.Worse(baseline, "w-1", 73m)).Outcome);
            Assert.True(kit.Store.Find(handle.Id)!.AutomaticPaused);

            // POSITIVE CONTROL: a legitimate worsening captured after Q2.
            PublicationResult fresh = publisher.Worsen(handle, FactKit.Capture(publisher, FactKit.Temp(76m)),
                FactKit.Worse(kit.Store.Find(handle.Id)!, "w-2", 76m));
            Assert.Equal(PublicationOutcome.Accepted, fresh.Outcome);
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(kit.Store.Find(handle.Id)!));
        }

        [Fact]
        public void B4_ReconnectIsNotNewOnset_InProcessAndAfterJournalReload_SyntheticOwner()
        {
            using var dir = new TempFactDir();
            var kit = new FactKit();

            FactSession first = kit.Session("SERIAL-1");
            PublicationResult opened = FactKit.OpenHot(kit.HotSlot(first), 70m);
            kit.Registry.Quiet.Observe("ctrl");
            first.End(T0, "disconnected");

            // Unchanged first observation, classified a continuation: the
            // predecessor's permission is carried at its ORIGINAL position,
            // which is before the operator's quiet — so the successor is
            // paused for the reason the predecessor was, his own Ctrl.
            SlotPublisher again = kit.HotSlot(kit.Session("SERIAL-1"));
            Assert.True(again.Continuity!.Paused);
            PublicationResult continued = FactKit.ContinueHot(again, 70m);
            Assert.Equal(PublicationOutcome.Accepted, continued.Outcome);
            Assert.Equal(PauseCause.OperatorQuiet, continued.Fact!.Pause);
            Assert.False(kit.Store.IsEligibleForAutomaticDelivery(continued.Fact));

            // The owner cannot say: the uncertainty is kept, not re-armed.
            SlotPublisher unsure = kit.HotSlot(kit.Session("SERIAL-1"));
            PublicationResult unknown = FactKit.OpenHot(unsure, 70m,
                options: new OpenOptions { Continuity = ContinuityClaim.ContinuityUnknown });
            Assert.Equal(PauseCause.ContinuityUnknown, unknown.Fact!.Pause);

            // A new onset needs positive evidence, never a disconnect.
            SlotPublisher onset = kit.HotSlot(kit.Session("SERIAL-1"));
            Assert.Equal(PublicationOutcome.EvidenceRejected,
                FactKit.OpenHot(onset, 70m, options: new OpenOptions { Continuity = ContinuityClaim.NewOccurrence }).Outcome);

            // A supported worse case: permission for the worse material only.
            SlotPublisher worse = kit.HotSlot(kit.Session("SERIAL-1"));
            ContinuityView prior = worse.Continuity!;
            PublicationResult worsened = FactKit.ContinueHot(worse, 70m, claim: ContinuityClaim.WorseningOfPrior,
                worsening: new WorseningTransition("reconnect-w", prior.BaselineFingerprint,
                    new[] { new MaterialDeclaration("temperature", FactValue.Of(74m)) }, "worse than before the reconnect"));
            Assert.Equal(PublicationOutcome.Accepted, worsened.Outcome);
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(worsened.Fact!));

            // The old episode never became live again.
            Assert.False(kit.Store.Find(opened.Handle!.Id)!.IsLive);

            // After a restart, the saved continuity still constrains permission.
            var saved = new FactKit();
            FactSession s = saved.Session("SERIAL-9");
            FactKit.OpenHot(saved.HotSlot(s), 70m);
            saved.Registry.Quiet.Observe("ctrl");
            s.End(T0, "application closing");
            using (var journal = new FactJournal(saved.Store, dir.Path))
            {
                Assert.True(journal.TakeLease());
                Assert.True(journal.Write());
            }

            var restarted = new FactKit();
            using var reader = new FactJournal(restarted.Store, dir.Path);
            reader.TakeLease();
            reader.LoadHistory();
            Assert.All(restarted.Store.All, f => Assert.False(f.IsLive));

            SlotPublisher afterRestart = restarted.HotSlot(restarted.Session("SERIAL-9"));
            Assert.True(afterRestart.Continuity!.FromPreviousProcess);
            PublicationResult resumedSession = FactKit.ContinueHot(afterRestart, 70m);
            Assert.Equal(PublicationOutcome.Accepted, resumedSession.Outcome);
            Assert.Equal(PauseCause.ContinuityInherited, resumedSession.Fact!.Pause);
            Assert.False(restarted.Store.IsEligibleForAutomaticDelivery(resumedSession.Fact));

            // POSITIVE CONTROL: an EVIDENCED new onset on another radio speaks.
            // This control used to open an unrelated station as a continuation
            // with no predecessor and expect eligibility — which would have
            // pinned "no record means permission", the error the unknown
            // branch exists to prevent. Absence is not evidence; an onset is.
            PublicationResult unrelated = FactKit.OnsetHot(restarted.HotSlot(restarted.Session("SERIAL-NEW")), 70m);
            Assert.True(restarted.Store.IsEligibleForAutomaticDelivery(unrelated.Fact!));

            // And the same station with NO evidence and no predecessor is
            // retained as unknown, not spoken.
            PublicationResult bare = FactKit.OpenHot(restarted.HotSlot(restarted.Session("SERIAL-NEW-2")), 70m,
                options: new OpenOptions { Continuity = ContinuityClaim.Continuation });
            Assert.Equal(PauseCause.ContinuityUnknown, bare.Fact!.Pause);
            Assert.False(restarted.Store.IsEligibleForAutomaticDelivery(bare.Fact));
        }

        [Fact]
        public void B4_LostContinuityRequiresEvidenceOrAnExplicitResume()
        {
            using var dir = new TempFactDir();

            // One run records more quieted conditions than the continuity
            // table can hold across a restart.
            var before = new FactKit();
            var sessions = new List<FactSession>();
            for (int i = 0; i <= FactStoreCapacity.MaxContinuityRecords; i++)
            {
                FactSession s = before.Session("SERIAL-" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture));
                FactKit.OpenHot(before.HotSlot(s), 70m);
                before.Registry.Quiet.Observe("ctrl");
                s.End(T0, "gone");
                sessions.Add(s);
            }
            using (var j = new FactJournal(before.Store, dir.Path))
            {
                j.TakeLease();
                j.Write();
            }

            var after = new FactKit();
            var reader = new FactJournal(after.Store, dir.Path);
            reader.TakeLease();
            reader.LoadHistory();
            reader.Dispose();
            Assert.Contains(after.Store.Issues, i => i.Kind == IssueKind.DetailLoss && i.SourceKey == "continuity");

            // Exactly one station's baseline was lost. It is not assumed clear.
            var lostOnes = new List<PauseCause>();
            foreach (FactSession s in sessions)
            {
                FactSession probe = after.Session(s.RadioIdentity);
                SlotPublisher p = after.HotSlot(probe);
                if (p.Continuity != null)
                {
                    probe.End(T0, "survived; not needed");   // this one kept its baseline
                    continue;
                }
                lostOnes.Add(FactKit.OpenHot(p, 70m, options: new OpenOptions { Continuity = ContinuityClaim.Continuation }).Fact!.Pause);
            }
            Assert.Equal(new[] { PauseCause.ContinuityLost }, lostOnes);

            // POSITIVE CONTROL: a station that never had a record is not "lost"
            // — its first observation, corroborated by the store, speaks. (It
            // used to be opened as a continuation with no predecessor, which
            // would pin missing-record-as-permission; Astra's G2 corrected it.)
            PublicationResult unseen = FactKit.OpenHot(after.HotSlot(after.Session("SERIAL-NEVER")), 70m);
            Assert.NotEqual(PauseCause.ContinuityLost, unseen.Fact!.Pause);
            Assert.True(after.Store.IsEligibleForAutomaticDelivery(unseen.Fact));

            // And positive onset evidence is what re-arms a lost one.
            string lostRadio = sessions.Select(s => s.RadioIdentity!).First(r =>
                after.Store.All.Any(f => f.RadioIdentity == r && f.IsLive && f.Pause == PauseCause.ContinuityLost));
            PublicationResult evidenced = FactKit.OpenHot(after.HotSlot(after.Session(lostRadio), "pa"), 70m,
                options: new OpenOptions
                {
                    Continuity = ContinuityClaim.NewOccurrence,
                    NewOnsetEvidence = FactObservation.Of(("onset", FactValue.Of("sensor reported a fresh rise"))),
                });
            Assert.True(after.Store.IsEligibleForAutomaticDelivery(evidenced.Fact!));
        }

        [Fact]
        public void B5_QuietAndFinalStartAreOrdered()
        {
            var kit = new FactKit();
            SlotPublisher publisher = kit.HotSlot(kit.Session());
            var transport = new RecordingTransport(kit.Registry, "t", TransportCapability.RequestOnly);

            // Quiet wins first: zero native requests, a retained reason.
            PublicationResult one = FactKit.OpenHot(publisher);
            AttemptHandle blocked = kit.Allocate(kit.PlanAutomatic(one.Handle!.Id), transport.Binding);
            kit.Registry.Quiet.Observe("ctrl");
            Assert.Equal(AttemptRunOutcome.NotStarted, AttemptRunner.Run(blocked, transport.Submit));
            Assert.Equal(0, transport.NativeCalls);
            AttemptSnapshot notStarted = kit.Store.Find(one.Handle.Id)!.Attempts.Single();
            Assert.Equal(AttemptDisposition.NotStarted, notStarted.Disposition);
            Assert.Equal(NotStartedReason.QuietAfterPermission, notStarted.NotStarted);

            // Start wins first: in flight, and a later quiet records
            // cancellation REQUESTED — never a claim that sound stopped.
            SlotPublisher other = kit.HotSlot(kit.Session("SERIAL-2"));
            PublicationResult two = FactKit.OpenHot(other);
            AttemptHandle running = kit.Allocate(kit.PlanAutomatic(two.Handle!.Id), transport.Binding);
            AttemptRunner.Run(running, (plan, attempt) =>
            {
                kit.Registry.Quiet.Observe("ctrl during the native call");
                return transport.Submit(plan, attempt);
            });
            AttemptSnapshot inFlight = kit.Store.Find(two.Handle.Id)!.Attempts.Single();
            Assert.Equal(1, transport.NativeCalls);
            Assert.True(inFlight.CancellationRequested);
            Assert.Equal(AttemptDisposition.RequestedOnly, inFlight.Disposition);
            Assert.False(inFlight.CompletedFully);

            // A selected READ crossing a newer quiet does not start.
            SlotPublisher third = kit.HotSlot(kit.Session("SERIAL-3"));
            PublicationResult three = FactKit.OpenHot(third);
            using FactListView view = new FactListPresenter(kit.Store).OpenView();
            RenderedDetailSnapshot shown = view.RenderDetail(view.Snapshot(FactView.Pending).Items.First(i => i.Fact?.Id == three.Handle!.Id))!;
            Assert.True(view.Installed(shown));
            SelectedReadGrant read = view.RequestRead(shown.Token, out _)!;
            kit.Registry.Quiet.Observe("ctrl after asking to read");
            PresentationPlan readPlan = kit.Presentation.Prepare(three.Handle!.Id, PlanRequest.SelectedRead(read, VerbosityLevel.Chatty)).Plan!;
            AttemptHandle readAttempt = kit.Allocate(readPlan, transport.Binding);
            Assert.Equal(NotStartedReason.QuietAfterPermission, readAttempt.TryCommitStart().Reason);

            // A selected RESUME crossing a newer quiet is overtaken.
            ResumeRequest resume = view.BeginResume(shown.Token, out ResumeOutcome begun)!;
            Assert.Equal(ResumeOutcome.Resumed, begun);
            kit.Registry.Quiet.Observe("ctrl after choosing resume");
            Assert.Equal(ResumeOutcome.OvertakenByQuiet, view.CommitResume(resume));
            Assert.True(kit.Store.Find(three.Handle.Id)!.AutomaticPaused);

            // POSITIVE CONTROL: a resume with no quiet in between releases
            // exactly the displayed information.
            Assert.Equal(ResumeOutcome.Resumed, view.Resume(shown.Token));
            Assert.True(kit.Store.IsEligibleForAutomaticDelivery(kit.Store.Find(three.Handle.Id)!));
        }
    }
}
