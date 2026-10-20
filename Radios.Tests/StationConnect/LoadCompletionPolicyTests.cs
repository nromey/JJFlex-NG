using System;
using System.Linq;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// The production load-completion default after junkbox #639 retired the
    /// "unconfirmed until bench B" placeholder (2026-10-20), judged through
    /// the real coordinator with the real production defaults.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was measured, and what these tests encode.</b> Bench B asked
    /// whether the radio emits a distinct "this load has finished" message.
    /// It does not emit one FlexLib surfaces. What every archived trace shows
    /// instead, on the bench 8600 across seven connects from 2026-10-17 to
    /// 2026-10-20 — three-slice profile (2026-10-19 06:00 and 06:35) and
    /// one-slice profile (2026-10-19 17:42 to 19:01, 2026-10-20 04:44 and
    /// 04:45) — is this order, every time: the load is sent; 260 to 480 ms
    /// later the restored slices are reported ready; 2 to 5 ms after the LAST
    /// of them the radio re-reports its global profile list, unasked (FlexLib
    /// asks for the lists once, at connect, and our own fresh ask is answered
    /// before the load goes out). A one-slice profile: load 13083, slice
    /// 13342, list 13347. A three-slice profile: load 11101, slices 11554,
    /// 11559, 11561, list 11566. One of the 2026-10-20 connects was over
    /// SmartLink and showed the same order (load 163634, slice 163910, list
    /// 163912).
    /// </para>
    /// <para>
    /// So the evidence the default accepts is a RADIO-REPORTED global list
    /// observed after the send, plus at least one own slice reported ready
    /// after the send that was not there at the send. Neither alone: the list
    /// without a slice is not a station, and a slice without the list may be
    /// the first of several. Nothing here promotes a quiet timer to proof.
    /// </para>
    /// </remarks>
    public class LoadCompletionPolicyTests
    {
        // ── the swap itself ────────────────────────────────────────────────

        [Fact]
        public void TheProductionDefault_IsNoLongerTheBenchBPlaceholder()
        {
            // The pin with the date and the evidence is beside the other three
            // defaults in RosterEvidenceTests; this one says what the trace line
            // must no longer read.
            var defaults = StationPolicies.Defaults();
            Assert.IsNotType<LoadCompletionUnconfirmedPolicy>(defaults.LoadCompletion);
            Assert.IsType<LoadCompletionByGlobalListReportPolicy>(defaults.LoadCompletion);
            Assert.DoesNotContain("bench B", defaults.LoadCompletion.Name);
            Assert.DoesNotContain("bench B", defaults.Describe());
            // And a returning connect (#782) carries the same completion policy.
            Assert.Same(defaults.LoadCompletion.GetType(), StationPolicies.ForConnect(presentedStoredClientId: true).LoadCompletion.GetType());
        }

        // ── the policy on hand-built evidence ──────────────────────────────

        private sealed class Evidence
        {
            public readonly FakeStationClock Clock = new FakeStationClock();
            public readonly ProfileEvidenceLog Profiles;
            public readonly StationTracker Station;
            public const int Gen = 7;
            public long ProfileSeqAtSend;
            public long StationSeqAtSend;
            public StationSnapshot Before;

            public Evidence()
            {
                Profiles = new ProfileEvidenceLog(Clock);
                Station = new StationTracker(Clock);
                Profiles.Reset(Gen);
                Station.Reset(Gen);
            }

            /// <summary>Everything up to the send: FlexLib's connect-time list
            /// (not our ask), then our fresh ask and its answer, then the send.</summary>
            public Evidence ArmAtSend()
            {
                Profiles.GlobalListObserved(new[] { "Default", "K5NER" }, ObservationProvenance.RadioReported, Gen);
                Profiles.GlobalInventoryAsked(Gen);
                Profiles.GlobalListObserved(new[] { "Default", "K5NER" }, ObservationProvenance.RadioReported, Gen);
                ProfileSeqAtSend = Profiles.Sequence;
                StationSeqAtSend = Station.Sequence;
                Before = Station.Snapshot();
                return this;
            }

            /// <summary>Our fresh ask went out and was NOT answered before the send.</summary>
            public Evidence OurAskOutstanding() { Profiles.GlobalInventoryAsked(Gen); return this; }

            public void SliceReady(int index) => Station.OwnSliceAdded(index, ((char)('A' + index)).ToString(), 0x1A2B3C4D, 0x40000001, Gen);
            public void ListReported(ObservationProvenance provenance = ObservationProvenance.RadioReported) =>
                Profiles.GlobalListObserved(new[] { "Default", "K5NER" }, provenance, Gen);

            public LoadEvidence Now(bool rejected = false) => new LoadEvidence
            {
                WantedName = "K5NER",
                AttemptGeneration = Gen,
                SentAtProfileSequence = ProfileSeqAtSend,
                SentAtStationSequence = StationSeqAtSend,
                StationBefore = Before,
                StationNow = Station.Snapshot(),
                ProfileNow = Profiles.Snapshot(),
                Rejected = rejected,
                RejectionText = rejected ? "profile not found" : "",
                Acknowledged = !rejected,
            };
        }

        private static readonly ILoadCompletionPolicy Policy = LoadCompletionByGlobalListReportPolicy.Instance;

        [Fact]
        public void Policy_NullEvidence_IsUnconfirmed_NotNotProvable()
        {
            // Unconfirmed keeps the coordinator waiting inside the phase;
            // NotProvable would end it at once, which is the placeholder's
            // behaviour and not this policy's.
            Assert.Equal(LoadCompletion.Unconfirmed, Policy.Judge(null));
        }

        [Fact]
        public void Policy_ARejection_IsRejected_WhateverElseArrived()
        {
            var e = new Evidence().ArmAtSend();
            e.SliceReady(0);
            e.ListReported();
            Assert.Equal(LoadCompletion.Rejected, Policy.Judge(e.Now(rejected: true)));
        }

        [Fact]
        public void Policy_NothingAfterTheSend_IsUnconfirmed()
        {
            var e = new Evidence().ArmAtSend();
            var evidence = e.Now();
            Assert.False(evidence.RadioReportedGlobalListAfterSend, "the pre-send lists must not read as after the send");
            Assert.Equal(0, evidence.NewOwnSlicesSinceSend);
            Assert.Equal(LoadCompletion.Unconfirmed, Policy.Judge(evidence));
        }

        [Fact]
        public void Policy_TheListAlone_IsUnconfirmed()
        {
            var e = new Evidence().ArmAtSend();
            e.ListReported();
            Assert.Equal(LoadCompletion.Unconfirmed, Policy.Judge(e.Now()));
        }

        [Fact]
        public void Policy_ASliceAlone_IsUnconfirmed()
        {
            var e = new Evidence().ArmAtSend();
            e.SliceReady(0);
            Assert.Equal(LoadCompletion.Unconfirmed, Policy.Judge(e.Now()));
        }

        [Fact]
        public void Policy_TheSliceThenTheList_IsConfirmed()
        {
            var measured = new Evidence().ArmAtSend();
            measured.SliceReady(0);
            measured.ListReported();
            var evidence = measured.Now();
            Assert.True(evidence.UnsolicitedGlobalListAfterSend);
            Assert.True(evidence.UnsolicitedGlobalListAfterNewestNewOwnSlice);
            Assert.Equal(LoadCompletion.Confirmed, Policy.Judge(evidence));
        }

        [Fact]
        public void Policy_TheListThenTheSlice_IsUnconfirmed_UntilAListFollowsTheNewestSlice()
        {
            // Until repair round one this asserted Confirmed for the reversed
            // order (Codex B1). A list that precedes a slice was not sent after
            // the last restored slice, whatever else it was.
            var e = new Evidence().ArmAtSend();
            e.ListReported();
            e.SliceReady(0);
            Assert.True(e.Now().UnsolicitedGlobalListAfterSend);
            Assert.False(e.Now().UnsolicitedGlobalListAfterNewestNewOwnSlice);
            Assert.Equal(LoadCompletion.Unconfirmed, Policy.Judge(e.Now()));

            e.ListReported();
            Assert.Equal(LoadCompletion.Confirmed, Policy.Judge(e.Now()));
        }

        [Fact]
        public void Policy_ALaterSlice_ReopensTheWait_UntilALaterList()
        {
            // The three-slice restore where the first list arrived too early:
            // slice, list, slice is not done; slice, list, slice, list is.
            var e = new Evidence().ArmAtSend();
            e.SliceReady(0);
            e.ListReported();
            Assert.Equal(LoadCompletion.Confirmed, Policy.Judge(e.Now()));
            e.SliceReady(1);
            Assert.Equal(LoadCompletion.Unconfirmed, Policy.Judge(e.Now()));
            e.SliceReady(2);
            Assert.Equal(LoadCompletion.Unconfirmed, Policy.Judge(e.Now()));
            e.ListReported();
            Assert.Equal(LoadCompletion.Confirmed, Policy.Judge(e.Now()));
            Assert.Equal(3, e.Now().NewOwnSlicesSinceSend);
        }

        [Fact]
        public void Policy_AListThatAnswersOurOutstandingAsk_IsNotTheRadiosReport()
        {
            // Codex B1: our bounded inventory read gave up; the load went out
            // with the ask outstanding; the answer lands after the first slice.
            var e = new Evidence().ArmAtSend().OurAskOutstanding();
            e.SliceReady(0);
            e.ListReported();
            var evidence = e.Now();
            Assert.True(evidence.RadioReportedGlobalListAfterSend, "freshness alone reads it as after the send");
            Assert.False(evidence.UnsolicitedGlobalListAfterSend, "but it answered our ask");
            Assert.True(evidence.ProfileNow.GlobalList.AnswersOurAsk);
            Assert.Equal(0, evidence.ProfileNow.OutstandingGlobalAsks);
            Assert.Equal(LoadCompletion.Unconfirmed, Policy.Judge(evidence));

            // The radio's own re-report, after that, confirms.
            e.ListReported();
            Assert.Equal(LoadCompletion.Confirmed, Policy.Judge(e.Now()));
        }

        [Fact]
        public void Policy_AnAskThatIsNeverAnswered_ConsumesTheRadiosReport_AndStaysUnconfirmed()
        {
            var e = new Evidence().ArmAtSend().OurAskOutstanding();
            e.SliceReady(0);
            e.ListReported();                                   // taken as the ask's answer
            Assert.Equal(LoadCompletion.Unconfirmed, Policy.Judge(e.Now()));
        }

        [Fact]
        public void Policy_AWithdrawnAsk_LeavesNothingOutstanding()
        {
            // The send threw, or the transport refused it: the ask is withdrawn
            // and the next list is the radio's own.
            var e = new Evidence().ArmAtSend();
            e.Profiles.GlobalInventoryAsked(Evidence.Gen);
            e.Profiles.GlobalInventoryAskWithdrawn(Evidence.Gen);
            e.SliceReady(0);
            e.ListReported();
            Assert.Equal(LoadCompletion.Confirmed, Policy.Judge(e.Now()));
        }

        [Fact]
        public void Policy_ALocalEchoOfTheList_IsNotTheRadioReporting()
        {
            // FlexLib raises the same PropertyChanged for a status the radio
            // sent and for a value our own code assigned; only the former is
            // evidence. The production port never assigns the list, so this is
            // the guard's positive control rather than a live path.
            var e = new Evidence().ArmAtSend();
            e.SliceReady(0);
            e.ListReported(ObservationProvenance.LocalEcho);
            Assert.Equal(LoadCompletion.Unconfirmed, Policy.Judge(e.Now()));
        }

        [Fact]
        public void Policy_ASliceThatWasThereAtTheSend_IsNotRestoredEvidence()
        {
            // The identity rule the station tracker already applies: a slice
            // present when the load was armed cannot be what the load produced,
            // and the list re-report does not launder it.
            var e = new Evidence();
            e.SliceReady(0);                                   // before the send
            e.ArmAtSend();
            e.ListReported();
            Assert.Equal(LoadCompletion.Unconfirmed, Policy.Judge(e.Now()));
        }

        [Fact]
        public void Policy_TheNameSaysWhatItRequires_AndNotTheBench()
        {
            Assert.Contains("global profile list", Policy.Name);
            Assert.Contains("unasked", Policy.Name);
            Assert.Contains("newest own slice", Policy.Name);
            Assert.DoesNotContain("bench", Policy.Name);
        }

        // ── the production defaults, through the coordinator ───────────────

        [Fact]
        public void ProductionDefaults_TheRadioDoesWhatTheBenchMeasured_TheRestoreIsConfirmed_AndNothingIsToppedUp()
        {
            // Noel's 2026-10-19 06:57 witness (#639): one slice saved to the
            // radio, reconnect, exactly one slice back — and the placeholder
            // policy announced "Station restore not confirmed". This is that
            // connect, with the measured evidence, under the shipped defaults.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.RadioHonoursPanafallRequests();
            h.Port.OnGlobalLoadSent = _ => h.RadioCompletesTheLoadAsMeasured(slices: 1);

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            Assert.Equal(GlobalRoute.LoadExisting, r.Route);
            Assert.True(r.StationEstablished);
            Assert.True(r.LoadSent);
            Assert.False(r.LoadOutstanding);
            Assert.Equal(1, r.OwnSlicesAtEnd);
            // A restored profile is never topped up: no panafall, whatever the
            // remembered layout or the radio's capacity says.
            Assert.Equal(AllocationStop.RouteForbids, r.Allocation.Stop);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
            Assert.Equal(0, h.Waiter.Waits);
        }

        [Fact]
        public void ProductionDefaults_AThreeSliceProfile_IsConfirmedWithAllThreeCounted()
        {
            // The 2026-10-19 06:00 and 06:35 connects: three slices, then the
            // list. The count the result carries is the whole station, not the
            // first slice.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => h.RadioCompletesTheLoadAsMeasured(slices: 3);

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            Assert.Equal(3, r.OwnSlicesAtEnd);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ProductionDefaults_EvidenceArrivingDuringTheWait_IsRetainedAndConfirms()
        {
            // The real timing: nothing is synchronous with the send. The slice
            // lands on one wait, the list on the next; the coordinator keeps
            // judging as the trackers signal.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.Waiter.Then(() => h.OwnSliceArrives())
                    .Then(() => h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray()));

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            Assert.Equal(2, h.Waiter.Waits);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ProductionDefaults_AListBeforeTheNewestSlice_DoesNotConfirm_TheRadiosOwnListAfterItDoes()
        {
            // Until repair round one (Codex B1, 2026-10-20) this test asserted
            // the OPPOSITE — a list, then a slice, confirmed — and so pinned
            // the permissive order: a list that arrived before the first slice
            // of a multi-slice restore, plus that first slice, confirmed the
            // whole load. The measured order is slices THEN the list, 2 to
            // 5 ms after the last of them, so a list that precedes a slice was
            // not the end of the load; the wait re-opens and only a list after
            // the newest slice closes it.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            int confirmedAfterWait = -1;
            h.Waiter.Then(() => h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray()))   // a list nobody of ours asked for
                    .Then(() => h.OwnSliceArrives())                                          // the first restored slice
                    .Then(() => h.OwnSliceArrives())                                          // the second
                    .Then(() => { h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray()); confirmedAfterWait = h.Waiter.Waits; });

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            Assert.Equal(2, r.OwnSlicesAtEnd);
            Assert.Equal(4, confirmedAfterWait);
            Assert.Equal(4, h.Waiter.Waits);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ProductionDefaults_ALateAnswerToOurOwnOutstandingAsk_IsNotTheRadiosReport_AndTheLoadStaysUnconfirmed()
        {
            // Codex B1, 2026-10-20: the coordinator's bounded inventory read
            // gave up on cached names and the load went out with our
            // "profile global info" unanswered. The late answer arrives after
            // the send, after the first restored slice, as a radio-reported
            // list — exactly the shape of the post-load re-report. It is
            // inventory, not the load finishing, and must not confirm.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.OurInventoryAskIsStillOutstanding();
            h.Port.OnGlobalLoadSent = _ => h.OwnSliceArrives();
            h.Waiter.Then(() => h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray()));  // the late answer

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.True(r.LoadOutstanding);
            Assert.False(r.StationEstablished);
            Assert.Equal(1, r.OwnSlicesAtEnd);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ProductionDefaults_AfterTheLateAnswerIsConsumed_TheRadiosOwnListStillConfirms()
        {
            // The positive control for the ask accounting: the late answer is
            // consumed as the answer, and the radio's own re-report after the
            // last slice — the measured evidence — confirms as before.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.OurInventoryAskIsStillOutstanding();
            h.Waiter.Then(() => h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray()))   // the late answer, consumed
                    .Then(() => h.RadioCompletesTheLoadAsMeasured(slices: 3));

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            Assert.Equal(3, r.OwnSlicesAtEnd);
            Assert.Equal(2, h.Waiter.Waits);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ProductionDefaults_AnAskTheRadioNeverAnswers_HoldsTheLoadUnconfirmed_FailClosed()
        {
            // Our ask is outstanding and the radio never answers it; the load's
            // own re-report is then taken as the answer and nothing confirms.
            // Honest, and the safe direction: the alternative is guessing which
            // list was which.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.OurInventoryAskIsStillOutstanding();
            h.Port.OnGlobalLoadSent = _ => h.RadioCompletesTheLoadAsMeasured(slices: 1);

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.True(r.LoadOutstanding);
            Assert.Equal(1, r.OwnSlicesAtEnd);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ProductionDefaults_SlicesArriveButTheRadioNeverReListsItsProfiles_StaysUnconfirmedAtTheDeadline_NoTopUp()
        {
            // The half of the evidence that is NOT measured on another radio or
            // firmware. A slice alone might be the first of several, so the
            // coordinator waits out the station phase and ends honestly
            // Unconfirmed: the slices that came are kept, nothing is filled in
            // around them, and nothing downstream runs. This is the case the
            // bench half on a 6300 over SmartLink would settle.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.RadioHonoursPanafallRequests();
            h.Port.OnGlobalLoadSent = _ => h.OwnSliceArrives();
            long before = h.Clock.NowMs;

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.False(r.StationEstablished);
            Assert.True(r.LoadOutstanding);
            Assert.Contains("without completion evidence", r.Reason);
            Assert.Equal(AllocationStop.LoadOutstanding, r.Allocation.Stop);
            Assert.Equal(1, r.OwnSlicesAtEnd);                 // kept whatever came back
            Assert.True(h.Clock.NowMs - before >= h.Deadlines.StationPhaseMs, "ended before the phase deadline");
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ProductionDefaults_TheListAloneIsNotAStation()
        {
            // The radio re-lists its profiles but no slice of ours is ever
            // reported ready. A saved zero-slice profile would look like this,
            // and so would a load whose slices never materialized; neither is
            // a confirmed station.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray());

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.True(r.LoadOutstanding);
            Assert.Equal(0, r.OwnSlicesAtEnd);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ProductionDefaults_AListReportedBeforeTheSend_IsNotCompletionEvidence()
        {
            // The connect itself reports the list twice before the load goes
            // out (FlexLib's connect-time ask, then our fresh ask). Neither
            // may be mistaken for the re-report that follows the load.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();                              // one list report
            h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray()); // and another, still before the send
            h.Port.OnGlobalLoadSent = _ => h.OwnSliceArrives();

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.True(r.LoadOutstanding);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ProductionDefaults_ARejection_StillEndsFailed()
        {
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.Port.RejectNextLoad = "profile not found";

            var r = h.Run();

            Assert.Equal(StationOutcome.Failed, r.Outcome);
            Assert.Contains("rejected", r.Reason);
            Assert.False(r.LoadOutstanding);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ProductionDefaults_TheOperatorRequestedLoad_IsConfirmedByTheSameEvidence()
        {
            // The owner said yes to "Load your profile?" after company left.
            // Until #639 this path ALWAYS ended "Your profile load was sent"
            // because the placeholder could confirm nothing; the same measured
            // evidence now confirms it, and nothing is topped up.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => h.RadioCompletesTheLoadAsMeasured(slices: 2);

            var r = h.Coordinator().RunOperatorRequestedLoad();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            Assert.True(r.StationEstablished);
            Assert.Equal(2, r.OwnSlicesAtEnd);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ProductionDefaults_APostImportReentry_AfterAConfirmedRestore_FindsTheExistingStation()
        {
            // The barrier a previous operation's OUTSTANDING load raises no
            // longer applies to a confirmed one: the re-entry sees the wanted
            // name selected and the station present, and sends nothing more.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => h.RadioCompletesTheLoadAsMeasured(slices: 1);
            var first = h.Run();
            Assert.Equal(StationOutcome.RestoredConfirmed, first.Outcome);
            h.Port.Selection = h.Port.Facts.WantedGlobal;

            var second = h.Coordinator(previous: first).Run();

            Assert.Equal(StationOutcome.ExistingStationConfirmed, second.Outcome);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }
    }
}
