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
    /// 11559, 11561, list 11566.
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

            /// <summary>Everything up to the send: the connect-time list, a fresh ask's answer, then the send.</summary>
            public Evidence ArmAtSend()
            {
                Profiles.GlobalListObserved(new[] { "Default", "K5NER" }, ObservationProvenance.RadioReported, Gen);
                Profiles.GlobalListObserved(new[] { "Default", "K5NER" }, ObservationProvenance.RadioReported, Gen);
                ProfileSeqAtSend = Profiles.Sequence;
                StationSeqAtSend = Station.Sequence;
                Before = Station.Snapshot();
                return this;
            }

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
        public void Policy_ASliceThenTheList_IsConfirmed_InEitherOrder()
        {
            var measured = new Evidence().ArmAtSend();
            measured.SliceReady(0);
            measured.ListReported();
            Assert.Equal(LoadCompletion.Confirmed, Policy.Judge(measured.Now()));

            var reversed = new Evidence().ArmAtSend();
            reversed.ListReported();
            reversed.SliceReady(0);
            Assert.Equal(LoadCompletion.Confirmed, Policy.Judge(reversed.Now()));
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
            Assert.Contains("own slice", Policy.Name);
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
        public void ProductionDefaults_TheListBeforeTheSliceIsReady_StillConfirmsOnceTheSliceIs()
        {
            // Over a slower transport FlexLib may declare the slice ready (its
            // panadapter ready, full status in) after the radio's list status
            // has already been parsed. The list observation stays in the
            // snapshot, so the next judgement after the slice is ready confirms.
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            h.Waiter.Then(() => h.RadioReportsGlobalList(h.Port.InventoryNames.ToArray()))
                    .Then(() => h.OwnSliceArrives());

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
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
