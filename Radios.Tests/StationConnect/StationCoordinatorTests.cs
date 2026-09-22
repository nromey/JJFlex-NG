using System;
using System.Linq;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// The station-first connect coordinator against the production trackers,
    /// guard and policies, with a fake monotonic clock and a port that can
    /// hold a dispatched delegate. Design section 6, groups 3, 4, 6 and 8.
    /// </summary>
    /// <remarks>
    /// <para>Every refusal test here has a positive control: the same
    /// sequence with the blocking condition removed and valid completion
    /// evidence supplied, which must send EXACTLY the intended command. A
    /// refusal test with no positive control proves only that nothing
    /// happens.</para>
    /// <para>Every assertion names the expected action AND zero of each
    /// competing action. A test that a load "happened" passes the original
    /// defect.</para>
    /// </remarks>
    public sealed class StationCoordinatorTests
    {
        // ==================================================================
        // THE POSITIVE CONTROL, FIRST. Without it every refusal below passes
        // on a coordinator that does nothing.
        // ==================================================================

        [Fact]
        public void OwnerReconnect_WithCompletionEvidence_SendsExactlyOneLoad_AndNoAllocation()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => h.DeliverGenuineCompletion();

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            Assert.Equal(GlobalRoute.LoadExisting, r.Route);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
            Assert.True(r.LoadSent);
            Assert.False(r.LoadOutstanding);
            Assert.False(r.CreationArmed);
            Assert.Equal(AllocationStop.RouteForbids, r.Allocation.Stop);
        }

        // ==================================================================
        // The defaults: fail closed, and cost the connect nothing but the refusal
        // ==================================================================

        [Fact]
        public void DefaultPolicies_RefuseTheOwnersAutomaticLoad_AndDoNotWaitOutTheBound()
        {
            var h = new StationHarness(StationPolicies.Defaults());
            h.ArrangeOwnerReconnect();
            long before = h.Clock.NowMs;

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.Equal(GlobalRoute.Refused, r.Route);
            Assert.Equal(RosterVerdict.Unknown, r.RosterAtDecision.Verdict);
            Assert.Contains("authority is not established", r.RosterAtDecision.Reason);
            h.AssertNothingWasSent();
            // Unknown by policy is Unknown for good: no five-second wait.
            Assert.True(h.Clock.NowMs - before < h.Deadlines.RosterSettleMs, "the coordinator waited for an answer that could not change");
            // ...and no fresh allocation either: materialization is unknown by default.
            Assert.Equal(AllocationStop.MaterializationUnknown, r.Allocation.Stop);
        }

        [Fact]
        public void DefaultCompletionPolicy_LeavesASentLoadUnconfirmed_WithNoDefaultFill()
        {
            var policies = StationHarness.PositiveControlPolicies();
            policies.LoadCompletion = LoadCompletionUnconfirmedPolicy.Instance;
            var h = new StationHarness(policies);
            h.ArrangeOwnerReconnect();
            // The radio does everything right — and it still cannot be proven.
            h.Port.OnGlobalLoadSent = _ => h.DeliverGenuineCompletion();

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.True(r.LoadSent);
            Assert.True(r.LoadOutstanding);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
            Assert.Equal(AllocationStop.LoadOutstanding, r.Allocation.Stop);
            Assert.False(r.PermitsScratchSetup);
        }

        // ==================================================================
        // Step 2: hold before intent, ownership before anything shared
        // ==================================================================

        [Theory]
        [InlineData(true, RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack, "hold")]
        [InlineData(false, RadioOwnership.Mine, ProfileGuestIntent.NotAnswered, "not answered")]
        [InlineData(false, RadioOwnership.Mine, ProfileGuestIntent.LeaveAlone, "leave")]
        [InlineData(false, RadioOwnership.Mine, ProfileGuestIntent.UseMyTransmitAudio, "transmit audio only")]
        [InlineData(false, RadioOwnership.Unset, ProfileGuestIntent.LoadMineAndPutBack, "not the declared owner")]
        [InlineData(false, RadioOwnership.SomeoneElses, ProfileGuestIntent.LoadMineAndPutBack, "not the declared owner")]
        public void NoAutomaticStewardship_SendsNoLoad_ArmsNoCreation(
            bool hold, RadioOwnership ownership, ProfileGuestIntent intent, string reasonFragment)
        {
            var h = new StationHarness();
            h.Port.Facts.HoldArmed = hold;
            h.Port.Facts.Ownership = ownership;
            h.Port.Facts.Intent = intent;
            h.ArrangeOwnerReconnect();
            h.RadioHonoursPanafallRequests();

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.Equal(GlobalRoute.Refused, r.Route);
            Assert.Contains(reasonFragment, r.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.False(r.CreationArmed);
            Assert.Equal("", r.PendingCreateName);
            // The global situation was never even read: hold before intent,
            // and both before any radio round trip.
            Assert.Equal(0, h.Port.GlobalSituationReads);
        }

        [Fact]
        public void AGuestConnection_GetsClientLocalReceiveResources_AndNothingShared()
        {
            // The #590 ruling's other half: a non-owner is refused the load
            // and gets a bounded fresh allocation when materialization has
            // ended. Client-local, bounded, and the only thing that runs.
            var h = new StationHarness();
            h.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            h.Port.Facts.Intent = ProfileGuestIntent.LoadMineAndPutBack;
            h.Port.Capacity = 2;
            h.Port.LegacyTarget = 4;
            h.OurClientAdded();
            h.RadioHonoursPanafallRequests();

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.Equal(2, h.Port.PanafallRequests);       // reconciled to capacity, not the legacy 4
            Assert.Equal(AllocationStop.TargetReached, r.Allocation.Stop);
            Assert.Equal(2, r.OwnSlicesAtEnd);
            Assert.True(r.StationEstablished);
            Assert.False(r.StationConfirmed);
            Assert.False(r.CreationArmed);
        }

        // ==================================================================
        // Step 3: the live roster (#577), as an input the coordinator waits on
        // ==================================================================

        [Fact]
        public void AnotherClientPresentAtDecision_RefusesTheOwnersLoad_AllocatesOnlyClientLocally()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.OtherClientAdded();
            h.RadioHonoursPanafallRequests();

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.Equal(RosterVerdict.OthersPresent, r.RosterAtDecision.Verdict);
            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.False(r.LoadSent);
            // The refused route's only action is the bounded client-local
            // allocation (design step 5, route 4).
            Assert.Equal(AllocationStop.TargetReached, r.Allocation.Stop);
        }

        [Fact]
        public void ARefusedRoute_WhoseAllocationIsDenied_IsFailedWithNoLoad()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.OtherClientAdded();
            // The radio never answers the panafall request.

            var r = h.Run();

            Assert.Equal(StationOutcome.Failed, r.Outcome);
            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.Equal(1, h.Port.PanafallRequests);
            Assert.Equal(AllocationStop.Timeout, r.Allocation.Stop);
        }

        [Fact]
        public void AnotherClientWhoseClientIdIsEmpty_IsStillAnotherClient()
        {
            // "Do not discard a different handle because its client ID is
            // empty" (design step 3). The naming-collision workaround ignores
            // such records; the roster guard must not.
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.OtherClientAdded(clientId: "", station: "");
            h.RadioHonoursPanafallRequests();

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.Equal(RosterVerdict.OthersPresent, r.RosterAtDecision.Verdict);
            Assert.Empty(h.Port.GlobalLoadsSent);
        }

        [Fact]
        public void OwnHandleNotYetEstablished_WaitsInsideTheBound_ThenProceedsWhenItArrives()
        {
            var h = new StationHarness();
            h.RadioReportsGlobalList("Default", "K5NER");
            h.Port.Selection = "K5NER";
            h.Port.OnGlobalLoadSent = _ => h.DeliverGenuineCompletion();
            // Our client is added on the second wait.
            h.Waiter.Then(() => h.Clock.Advance(100)).Then(() => h.OurClientAdded());

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void OwnHandleNeverEstablished_RefusesAtTheRosterBound_SendsNothing()
        {
            var h = new StationHarness();
            h.RadioReportsGlobalList("Default", "K5NER");
            long before = h.Clock.NowMs;

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.Equal(RosterVerdict.Unknown, r.RosterAtDecision.Verdict);
            Assert.Contains("still unknown at the bound", r.RosterAtDecision.Reason);
            // The roster bound was spent; the materialization policy (which
            // also keys on our handle) then said Pending until the phase
            // ended. Everything stayed inside the one station-phase ceiling.
            Assert.True(h.Clock.NowMs - before >= h.Deadlines.RosterSettleMs);
            Assert.True(h.Clock.NowMs - before <= h.Deadlines.StationPhaseMs + 25);
            Assert.Equal(AllocationStop.MaterializationUnknown, r.Allocation.Stop);
            h.AssertNothingWasSent();
        }

        [Fact]
        public void AnUnconfirmedRemovalOfAnotherClient_RefusesUntilTheRadioSpeaksAgain()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.OtherClientAdded();
            h.ClientRemoved(StationHarness.OtherHandle);
            h.RadioHonoursPanafallRequests();
            // Nothing else arrives: the removal stays ambiguous.

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.Equal(RosterVerdict.Unknown, r.RosterAtDecision.Verdict);
            Assert.Contains("removed", r.RosterAtDecision.Reason);
            Assert.Empty(h.Port.GlobalLoadsSent);
        }

        [Fact]
        public void AnUnconfirmedRemoval_ClearedByARadioStatusUpdate_ThenLoads()
        {
            // The positive control for the test above: the same removal, then
            // the radio's own status speaks (an identity-bearing update of our
            // record), and the load goes out exactly once.
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.OtherClientAdded();
            h.ClientRemoved(StationHarness.OtherHandle);
            h.Port.OnGlobalLoadSent = _ => h.DeliverGenuineCompletion();
            h.Waiter.Then(() => h.Roster.ClientUpdated(
                new RosterEntry(StationHarness.OurHandle, "our-client-id", true, "K5NER", "JJFlex"), h.Gen));

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        // ==================================================================
        // Group 3: the actual command boundary. Pause the queued load, change
        // the world, release, assert ZERO sends.
        // ==================================================================

        private static StationHarness HeldLoad()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.RadioHonoursPanafallRequests();
            h.Port.HoldDispatch = true;
            return h;
        }

        [Fact]
        public void HeldLoad_ReleasedUnchanged_SendsExactlyOnce()
        {
            // The positive control for every dispatch-race test below.
            var h = HeldLoad();
            h.Port.OnGlobalLoadSent = _ => h.DeliverGenuineCompletion();
            h.Waiter.Then(() => h.Port.ReleaseHeld());

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void HeldLoad_AnotherClientJoinsBeforeRelease_SendsNothing()
        {
            var h = HeldLoad();
            h.Waiter.Then(() => { h.OtherClientAdded(); h.Port.ReleaseHeld(); });

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.Contains("roster at dispatch", r.Reason);
            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.False(r.LoadSent);
        }

        [Fact]
        public void HeldLoad_HoldArmedBeforeRelease_SendsNothing()
        {
            var h = HeldLoad();
            h.Waiter.Then(() => { h.Port.Facts.HoldArmed = true; h.Port.ReleaseHeld(); });

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.Contains("policy changed", r.Reason);
            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.False(r.LoadSent);
        }

        [Theory]
        [InlineData(ProfileGuestIntent.NotAnswered)]
        [InlineData(ProfileGuestIntent.LeaveAlone)]
        public void HeldLoad_IntentChangedBeforeRelease_SendsNothing(ProfileGuestIntent newIntent)
        {
            var h = HeldLoad();
            h.Waiter.Then(() => { h.Port.Facts.Intent = newIntent; h.Port.ReleaseHeld(); });

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.Empty(h.Port.GlobalLoadsSent);
        }

        [Fact]
        public void HeldLoad_WantedNameChangedBeforeRelease_SendsNothing_AndDoesNotChaseTheNewName()
        {
            var h = HeldLoad();
            h.Waiter.Then(() => { h.Port.Facts.WantedGlobal = "SO2RDefault"; h.Port.ReleaseHeld(); });

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.Empty(h.Port.GlobalLoadsSent);
        }

        [Fact]
        public void HeldLoad_TargetProfileRemovedFromInventoryBeforeRelease_SendsNothing()
        {
            var h = HeldLoad();
            h.Waiter.Then(() => { h.RadioReportsGlobalList("Default"); h.Port.ReleaseHeld(); });

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.Contains("not in the radio's reported inventory at dispatch", r.Reason);
            Assert.Empty(h.Port.GlobalLoadsSent);
        }

        [Fact]
        public void HeldLoad_ConnectionReplacedBeforeRelease_SendsNothing_EndsCancelled()
        {
            var h = HeldLoad();
            var first = h.Attempt;
            h.Waiter.Then(() => { first.Cancel("radio replaced"); h.Port.ReleaseHeld(); });

            var r = h.Run();

            Assert.Equal(StationOutcome.Cancelled, r.Outcome);
            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.Equal(0, h.Port.PanafallRequests);
        }

        [Fact]
        public void CancellationImmediatelyAfterSend_OneSend_NoDownstreamAction_UncertainResult()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => h.Attempt.Cancel("operator cancelled");

            var r = h.Run();

            Assert.Equal(StationOutcome.Cancelled, r.Outcome);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
            Assert.True(r.LoadSent);
            Assert.True(r.LoadOutstanding);
            Assert.False(r.CreationArmed);
        }

        // ==================================================================
        // Group 4: station event permutations — what does NOT complete
        // ==================================================================

        [Fact]
        public void InventoryMembershipAlone_DoesNotCompleteTheBarrier_OrStartAllocation()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => h.RadioReportsGlobalList("Default", "K5NER");

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.True(r.LoadOutstanding);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void LocalSelectionEchoAlone_DoesNotCompleteTheBarrier()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = name => h.LocalEchoSelection(name);

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void PersistenceLoadedAlone_DoesNotCompleteTheBarrier()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => h.PersistenceLoaded();

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void OneEarlySliceAlone_DoesNotCompleteTheBarrier()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => h.OwnSliceArrives();

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.Equal(1, r.OwnSlicesAtEnd);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ARadioReportedSelectionAlone_DoesNotCompleteTheBarrier()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = name => h.RadioReportsGlobalSelection(name);

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void GenuineCompletionDeliveredDuringDispatch_IsRetained()
        {
            // The evidence arrives synchronously inside the send, before the
            // coordinator's wait begins. Arming before the send is what keeps
            // it; #579's lost-event race was exactly this window.
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => h.DeliverGenuineCompletion();

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            Assert.Equal(0, h.Waiter.Waits); // retained without a single wait
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void EvidenceStampedWithTheOldGeneration_CannotCompleteTheNewOperation()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            int oldGen = h.Gen - 1;
            h.Port.OnGlobalLoadSent = _ =>
            {
                h.Station.OwnSliceAdded(0, "A", StationHarness.OurHandle, 0x40000001, oldGen);
                h.Profiles.RadioEndBoundary("profile-load-complete", oldGen);
            };

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.Equal(0, r.OwnSlicesAtEnd);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ASliceThatWasAlreadyThereBeforeTheSend_IsNotRestoredEvidence()
        {
            // Switching from a several-slice profile to a one-slice profile:
            // old slices must not masquerade as newly restored ones.
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.OwnSliceArrives(); // present before the load; the name still matches, so the planner says load
            h.Port.Selection = "Default"; // so the planner does not call it AlreadyLoaded
            h.Port.OnGlobalLoadSent = _ => h.RadioEndBoundary();

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void Rejection_EndsFailed_WithNoRetryAndNoDefaultFill()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.RejectNextLoad = "profile not found";

            var r = h.Run();

            Assert.Equal(StationOutcome.Failed, r.Outcome);
            Assert.Contains("rejected", r.Reason);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
            Assert.False(r.LoadOutstanding);
        }

        [Fact]
        public void LossOfConnectionDuringTheWait_EndsCancelled_KeepsTheUncertainty()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Waiter.Then(() => h.Attempt.Cancel("connection dropped"));

            var r = h.Run();

            Assert.Equal(StationOutcome.Cancelled, r.Outcome);
            Assert.True(r.LoadOutstanding);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ALongGap_EndsUnconfirmedAtTheStationPhaseDeadline_NoImplicitRetry()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            long before = h.Clock.NowMs;

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.True(r.LoadOutstanding);
            Assert.True(h.Clock.NowMs - before >= h.Deadlines.StationPhaseMs, "the wait ended before the phase deadline");
            Assert.True(h.Clock.NowMs - before < h.Deadlines.StationPhaseMs + 1000, "the wait ran past the phase deadline");
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void DuplicateCompletionEvents_ProduceOneOutcome_AndOneSend()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => { h.DeliverGenuineCompletion(); h.RadioEndBoundary(); h.RadioEndBoundary(); };

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void LateSuccessAfterTimeout_DoesNotSilentlyResumeTheCancelledPlan()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            var coordinator = h.Coordinator();
            var r = coordinator.Run();
            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);

            // The completion arrives late. Nothing re-runs; the result stands;
            // no second load and no allocation are sent.
            h.DeliverGenuineCompletion();

            Assert.Equal(StationOutcome.Unconfirmed, coordinator.Result.Outcome);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void InvalidationAfterSend_HoldArmed_EndsUnconfirmed_NoRollbackNoSecondLoad()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Waiter.Then(() => h.Port.Facts.HoldArmed = true);

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.Contains("permission changed after the load was sent", r.Reason);
            Assert.True(r.LoadOutstanding);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        // ==================================================================
        // Group 6: allocation and the missing name (#587, #588, #578)
        // ==================================================================

        [Fact]
        public void AConfirmedOneSliceRestore_OnAFourSliceRadio_ProducesZeroExtraRequests()
        {
            // Tonight's bug (#587): two saved slices restored, two padded on
            // top, and per-slice commands landed on the wrong slice.
            var h = new StationHarness();
            h.Port.Capacity = 3;
            h.Port.LegacyTarget = 4;
            h.ArrangeOwnerReconnect();
            h.RadioHonoursPanafallRequests();
            h.Port.OnGlobalLoadSent = _ => h.DeliverGenuineCompletion(); // ONE slice

            var r = h.Run();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            Assert.Equal(1, r.OwnSlicesAtEnd);
            Assert.Equal(0, h.Port.PanafallRequests);
            Assert.Equal(AllocationStop.RouteForbids, r.Allocation.Stop);
        }

        [Fact]
        public void AMissingOwnedPerRadioGlobal_NoPhantomLoad_BoundedAllocation_CreationArmedForTheEffectiveName()
        {
            var h = new StationHarness();
            // The per-radio choice differs from the operator default and is
            // absent from the radio. #578's fallback searched the operator
            // defaults and would have missed this name.
            h.Port.Facts.WantedGlobal = "K5NER-8600";
            h.Port.InventoryNames = new System.Collections.Generic.List<string> { "Default", "K5NER" };
            h.Port.Selection = "Default";
            h.Port.Capacity = 2;
            h.Port.LegacyTarget = 4;
            h.OurClientAdded();
            h.RadioReportsGlobalList("Default", "K5NER");
            h.RadioHonoursPanafallRequests();

            var r = h.Run();

            Assert.Equal(StationOutcome.FreshStationConfirmed, r.Outcome);
            Assert.Equal(GlobalRoute.MissingOwnedGlobal, r.Route);
            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.False(r.LoadSent);
            Assert.Equal(2, h.Port.PanafallRequests);
            Assert.Equal(AllocationStop.TargetReached, r.Allocation.Stop);
            Assert.True(r.CreationArmed);
            Assert.Equal("K5NER-8600", r.PendingCreateName);
        }

        [Theory]
        [InlineData(RadioOwnership.Unset, ProfileGuestIntent.LoadMineAndPutBack, false, true)]
        [InlineData(RadioOwnership.SomeoneElses, ProfileGuestIntent.LoadMineAndPutBack, false, true)]
        [InlineData(RadioOwnership.Mine, ProfileGuestIntent.NotAnswered, false, true)]
        [InlineData(RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack, true, true)]
        [InlineData(RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack, false, false)]
        public void NonOwned_NotAnswered_Held_OrUnknownInventory_NeverArmsCreation(
            RadioOwnership ownership, ProfileGuestIntent intent, bool hold, bool inventoryReported)
        {
            var h = new StationHarness();
            h.Port.Facts.WantedGlobal = "K5NER-8600";
            h.Port.Facts.Ownership = ownership;
            h.Port.Facts.Intent = intent;
            h.Port.Facts.HoldArmed = hold;
            h.Port.InventoryReported = inventoryReported;
            h.Port.InventoryNames = new System.Collections.Generic.List<string> { "Default" };
            h.OurClientAdded();
            if (inventoryReported) h.RadioReportsGlobalList("Default");
            h.RadioHonoursPanafallRequests();

            var r = h.Run();

            Assert.False(r.CreationArmed);
            Assert.Equal("", r.PendingCreateName);
            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.NotEqual(StationOutcome.FreshStationConfirmed, r.Outcome);
        }

        [Fact]
        public void ResourceDenial_StopsAfterTheOneBoundedRequest_NotTheCountBasedLoop()
        {
            // #588: the radio never allocates. The old loop retried every two
            // seconds forever; this stops after one bounded request.
            var h = new StationHarness();
            h.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            h.Port.Capacity = 4;
            h.Port.LegacyTarget = 4;
            h.OurClientAdded();
            // The radio says nothing back.
            long before = h.Clock.NowMs;

            var r = h.Run();

            Assert.Equal(1, h.Port.PanafallRequests);
            Assert.Equal(AllocationStop.Timeout, r.Allocation.Stop);
            Assert.Equal(StationOutcome.Failed, r.Outcome);
            Assert.Equal(0, r.OwnSlicesAtEnd);
            Assert.True(h.Clock.NowMs - before < h.Deadlines.StationPhaseMs, "the allocator ran out the whole phase instead of stopping");
        }

        [Fact]
        public void PartialAllocationThenDenial_KeepsWhatWasObtained_ReportsFailed_ArmsNoCreation()
        {
            var h = new StationHarness();
            h.Port.Facts.WantedGlobal = "K5NER-8600";
            h.Port.InventoryNames = new System.Collections.Generic.List<string> { "Default" };
            h.Port.Capacity = 4;
            h.Port.LegacyTarget = 4;
            h.OurClientAdded();
            h.RadioReportsGlobalList("Default");
            int honoured = 0;
            h.Port.OnPanafallRequested = () => { if (honoured++ < 2) h.OwnSliceArrives(); };

            var r = h.Run();

            Assert.Equal(3, h.Port.PanafallRequests); // two honoured, one denied, then stop
            Assert.Equal(2, r.OwnSlicesAtEnd);
            Assert.Equal(AllocationStop.Timeout, r.Allocation.Stop);
            Assert.Equal(StationOutcome.Failed, r.Outcome);
            Assert.False(r.CreationArmed);
        }

        [Fact]
        public void CapacityExhausted_StopsCleanly_AndCountsAsEstablished()
        {
            var h = new StationHarness();
            h.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            h.Port.Capacity = 1;
            h.Port.LegacyTarget = 4;
            h.OurClientAdded();
            h.RadioHonoursPanafallRequests();

            var r = h.Run();

            Assert.Equal(1, h.Port.PanafallRequests);
            Assert.Equal(AllocationStop.TargetReached, r.Allocation.Stop);
            Assert.True(r.StationEstablished);
        }

        [Fact]
        public void ALateRestorationSlice_CannotBeMisattributedToANewAllocation()
        {
            // A slice already present before the request was armed is not the
            // requested one. The request's own slice must arrive.
            var h = new StationHarness();
            h.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            h.Port.Capacity = 1;
            h.Port.LegacyTarget = 2;
            h.OurClientAdded();
            h.OwnSliceArrives(); // present before allocation begins
            h.Port.OnPanafallRequested = () => { }; // the radio never answers the request

            var r = h.Run();

            Assert.Equal(1, h.Port.PanafallRequests);
            Assert.Equal(AllocationStop.Timeout, r.Allocation.Stop);
            Assert.Equal(1, r.OwnSlicesAtEnd);
        }

        [Fact]
        public void MaterializationUnknown_ForbidsFreshAllocation_AndArmsNoCreationOnTheMissingRoute()
        {
            var policies = StationHarness.PositiveControlPolicies();
            policies.InitialMaterialization = MaterializationUnknownPolicy.Instance;
            var h = new StationHarness(policies);
            h.Port.Facts.WantedGlobal = "K5NER-8600";
            h.Port.InventoryNames = new System.Collections.Generic.List<string> { "Default" };
            h.OurClientAdded();
            h.RadioReportsGlobalList("Default");
            h.RadioHonoursPanafallRequests();

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.Equal(GlobalRoute.MissingOwnedGlobal, r.Route);
            Assert.Equal(0, h.Port.PanafallRequests);
            Assert.Equal(AllocationStop.MaterializationUnknown, r.Allocation.Stop);
            Assert.False(r.CreationArmed);
            Assert.Equal("", r.PendingCreateName);
        }

        // ==================================================================
        // Route 1: existing, established station
        // ==================================================================

        [Fact]
        public void MatchingNameWithAPresentStation_AndMaterializationEnded_IsExistingStationConfirmed_NothingSent()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.OwnSliceArrives();

            var r = h.Run();

            Assert.Equal(StationOutcome.ExistingStationConfirmed, r.Outcome);
            Assert.Equal(GlobalRoute.ExistingStation, r.Route);
            h.AssertNothingWasSent();
        }

        [Fact]
        public void MatchingNameWithAPresentStation_ButMaterializationUnknown_IsUnconfirmed_NothingSent()
        {
            var policies = StationHarness.PositiveControlPolicies();
            policies.InitialMaterialization = MaterializationUnknownPolicy.Instance;
            var h = new StationHarness(policies);
            h.ArrangeOwnerReconnect();
            h.OwnSliceArrives();

            var r = h.Run();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.Equal(GlobalRoute.ExistingStation, r.Route);
            h.AssertNothingWasSent();
        }

        [Fact]
        public void MatchingNameWithNoStation_IsAName_AndTheLoadIsSent()
        {
            // #563 through the coordinator, not just the planner.
            var h = new StationHarness();
            h.ArrangeOwnerReconnect(); // selection == wanted, zero own slices
            h.Port.OnGlobalLoadSent = _ => h.DeliverGenuineCompletion();

            var r = h.Run();

            Assert.Equal(GlobalRoute.LoadExisting, r.Route);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        // ==================================================================
        // Group 8: lifecycle
        // ==================================================================

        [Fact]
        public void CancelDuringTheRosterWait_EndsCancelled_SendsNothing()
        {
            var h = new StationHarness();
            h.RadioReportsGlobalList("Default", "K5NER");
            h.Waiter.Then(() => h.Attempt.Cancel("cancelled during roster wait"));

            var r = h.Run();

            Assert.Equal(StationOutcome.Cancelled, r.Outcome);
            h.AssertNothingWasSent();
        }

        [Fact]
        public void CancelDuringAllocation_EndsCancelled_KeepsWhatWasObtained()
        {
            var h = new StationHarness();
            h.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            h.Port.Capacity = 4;
            h.Port.LegacyTarget = 4;
            h.OurClientAdded();
            int honoured = 0;
            h.Port.OnPanafallRequested = () => { if (honoured++ == 0) h.OwnSliceArrives(); else h.Attempt.Cancel("cancel"); };

            var r = h.Run();

            Assert.Equal(StationOutcome.Cancelled, r.Outcome);
            Assert.Equal(1, r.OwnSlicesAtEnd);
            Assert.Equal(2, h.Port.PanafallRequests);
        }

        [Fact]
        public void TheNextAttemptStartsClean_AndOldObservationsAreIgnored()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.OwnSliceArrives();
            int oldGen = h.Gen;

            h.NewAttempt();

            Assert.Equal(0, h.Station.Snapshot().OwnSliceCount);
            Assert.False(h.Roster.Snapshot().OwnHandleKnown);
            Assert.Null(h.Profiles.Snapshot().GlobalList);

            // A callback from the old attempt lands after the reset.
            h.Station.OwnSliceAdded(7, "H", StationHarness.OurHandle, 1, oldGen);
            h.Roster.ClientAdded(new RosterEntry(StationHarness.OurHandle, "x", true, "", ""), oldGen);
            h.Profiles.GlobalListObserved(new[] { "K5NER" }, ObservationProvenance.RadioReported, oldGen);

            Assert.Equal(0, h.Station.Snapshot().OwnSliceCount);
            Assert.Empty(h.Roster.Snapshot().Entries);
            Assert.Null(h.Profiles.Snapshot().GlobalList);
        }

        [Fact]
        public void NoOutcomePermitsAScratchSetup()
        {
            foreach (StationOutcome outcome in Enum.GetValues(typeof(StationOutcome)))
            {
                Assert.False(new StationResult { Outcome = outcome }.PermitsScratchSetup);
            }
        }

        [Fact]
        public void OnlyTheThreeConfirmedOutcomesAreConfirmed()
        {
            var confirmed = Enum.GetValues(typeof(StationOutcome)).Cast<StationOutcome>()
                .Where(o => new StationResult { Outcome = o }.StationConfirmed).ToList();
            Assert.Equal(new[]
            {
                StationOutcome.ExistingStationConfirmed,
                StationOutcome.RestoredConfirmed,
                StationOutcome.FreshStationConfirmed,
            }, confirmed);
        }
    }
}
