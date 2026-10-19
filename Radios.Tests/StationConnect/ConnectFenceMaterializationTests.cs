using System;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// The post-connect fence as the end of initial materialization (#782).
    /// A returning GUI client's restored slices arrive AFTER our own handle
    /// (FlexLib subscribes <c>sub client all</c> before <c>sub slice all</c>)
    /// and BEFORE the radio answers a command sent after the subscriptions.
    /// The fence policy waits for that answer; the own-handle policy it
    /// replaced did not, and this file keeps the duplication it allowed as
    /// the positive control.
    /// </summary>
    public sealed class ConnectFenceMaterializationTests
    {
        private static StationPolicies FencePolicies()
        {
            var p = StationHarness.PositiveControlPolicies();
            p.InitialMaterialization = MaterializationEndsAtConnectFencePolicy.Instance;
            return p;
        }

        /// <summary>A guest on someone else's radio: the refused route, which
        /// allocates client-locally. Two restored slices and the fence arrive
        /// on the coordinator's first three waits, in that order — the wire
        /// order FlexLib's subscription sequence produces.</summary>
        private static StationHarness ReturningGuestWhoseRadioRestoresTwoSlices(StationPolicies policies)
        {
            var h = new StationHarness(policies);
            h.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            h.Port.Capacity = 4;
            h.Port.LegacyTarget = 4;
            h.OurClientAdded();                       // sub client all: our handle, first
            h.RadioHonoursPanafallRequests();         // and it would honour any request we made
            h.Waiter.Then(() => h.OwnSliceArrives())  // sub slice all: the radio's restored slices
                    .Then(() => h.OwnSliceArrives())
                    .Then(() => h.ConnectFence());    // the ping reply, after everything above
            return h;
        }

        // ── the policy itself ─────────────────────────────────────────────

        [Fact]
        public void ThePolicy_IsPendingUntilBothOurHandleAndTheFenceAreIn()
        {
            var h = new StationHarness(FencePolicies());
            var policy = MaterializationEndsAtConnectFencePolicy.Instance;

            Assert.Equal(MaterializationBoundary.Pending, policy.Judge(null));
            Assert.Equal(MaterializationBoundary.Pending, policy.Judge(Evidence(h)));           // neither

            h.ConnectFence();                                                                   // fence, no handle
            Assert.Equal(MaterializationBoundary.Pending, policy.Judge(Evidence(h)));

            h.NewAttempt();
            h.OurClientAdded();                                                                 // handle, no fence
            Assert.Equal(MaterializationBoundary.Pending, policy.Judge(Evidence(h)));

            h.ConnectFence();                                                                   // both
            Assert.Equal(MaterializationBoundary.Ended, policy.Judge(Evidence(h)));
        }

        [Fact]
        public void TheFenceIsPerAttempt_AStaleGenerationIsIgnored_AndTheFirstAnswerCounts()
        {
            var h = new StationHarness(FencePolicies());
            int stale = h.Gen;
            h.NewAttempt();
            h.Profiles.ConnectFenceObserved(stale);
            Assert.Equal(0, h.Profiles.Snapshot().ConnectFenceAtSequence);

            h.ConnectFence();
            long first = h.Profiles.Snapshot().ConnectFenceAtSequence;
            Assert.True(first > 0);
            h.ConnectFence();
            Assert.Equal(first, h.Profiles.Snapshot().ConnectFenceAtSequence);

            h.NewAttempt();
            Assert.Equal(0, h.Profiles.Snapshot().ConnectFenceAtSequence);
        }

        [Fact]
        public void TheDefaultStaysOwnHandle_AndOnlyAReturningConnectGetsTheFence()
        {
            // A fresh mint is the case bench D measured; it keeps the measured
            // boundary and cannot be left with zero slices by a fence that
            // never answers. A connect that presented a stored id may be
            // handed a restored station, and waits for the fence.
            Assert.IsType<MaterializationEndsAtOwnHandlePolicy>(StationPolicies.Defaults().InitialMaterialization);

            var fresh = StationPolicies.ForConnect(presentedStoredClientId: false);
            Assert.Same(StationPolicies.Current, fresh);
            Assert.IsType<MaterializationEndsAtOwnHandlePolicy>(fresh.InitialMaterialization);

            var returning = StationPolicies.ForConnect(presentedStoredClientId: true);
            Assert.NotSame(StationPolicies.Current, returning);
            Assert.IsType<MaterializationEndsAtConnectFencePolicy>(returning.InitialMaterialization);
            Assert.Contains("post-connect fence", returning.Describe());
            // The other three policies are the current ones, untouched.
            Assert.Same(StationPolicies.Current.RosterAuthority, returning.RosterAuthority);
            Assert.Same(StationPolicies.Current.LoadCompletion, returning.LoadCompletion);
            Assert.Same(StationPolicies.Current.GuestSharedWriteAuthority, returning.GuestSharedWriteAuthority);
            // And Current itself was not mutated by asking.
            Assert.IsType<MaterializationEndsAtOwnHandlePolicy>(StationPolicies.Current.InitialMaterialization);
        }

        // ── the duplication the fence closes ──────────────────────────────

        [Fact]
        public void ARestoredLayoutArrivingAfterOurHandle_IsPreservedUnderTheFence_AndNothingIsRequested()
        {
            var h = ReturningGuestWhoseRadioRestoresTwoSlices(FencePolicies());

            var r = h.Run();

            Assert.Equal(0, h.Port.PanafallRequests);
            Assert.Equal(AllocationStop.ExistingLayoutPreserved, r.Allocation.Stop);
            Assert.Equal(2, r.OwnSlicesAtEnd);
            Assert.Equal(2, h.Station.Snapshot().OwnSliceCount);
            Assert.True(r.StationEstablished);
            Assert.Empty(h.Port.GlobalLoadsSent);
        }

        [Fact]
        public void PositiveControl_TheOwnHandlePolicy_RequestsSlicesWhileTheRadioIsStillRestoring()
        {
            // The same radio, the same wire order, the boundary this file
            // retired as the default: it judges "ended" at our handle, reads
            // zero own slices, and asks for more — on top of the two the
            // radio is in the middle of restoring. This is the duplication
            // the own-handle policy's own note warned a stable id would cause.
            var policies = StationHarness.PositiveControlPolicies();
            policies.InitialMaterialization = MaterializationEndsAtOwnHandlePolicy.Instance;
            var h = ReturningGuestWhoseRadioRestoresTwoSlices(policies);

            var r = h.Run();

            Assert.True(h.Port.PanafallRequests >= 1, "the control must reproduce the request made during the restore");
            Assert.NotEqual(AllocationStop.ExistingLayoutPreserved, r.Allocation.Stop);
        }

        // ── fail-closed when the fence never answers ──────────────────────

        [Fact]
        public void AFenceThatNeverAnswers_LeavesMaterializationUnknown_AndAllocatesNothing()
        {
            var h = new StationHarness(FencePolicies());
            h.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            h.OurClientAdded();
            h.RadioHonoursPanafallRequests();
            long before = h.Clock.NowMs;

            var r = h.Run();

            Assert.Equal(0, h.Port.PanafallRequests);
            Assert.Equal(AllocationStop.MaterializationUnknown, r.Allocation.Stop);
            Assert.Contains("post-connect fence", r.Allocation.Note);
            Assert.False(r.StationEstablished);
            // It waited the phase out rather than inventing an answer.
            Assert.True(h.Clock.NowMs - before >= h.Deadlines.StationPhaseMs,
                "the coordinator gave up before the phase bound without a fence; a quiet period is not the boundary");
        }

        // ── Noel's zero-slice regression stays fixed ─────────────────────

        [Fact]
        public void AFreshClientWithNothingRestored_StillGetsItsSlicesOnceTheFenceAnswers()
        {
            // #639's whole report was a fail-closed materialization default
            // refusing every client-local allocation. The fence must not bring
            // that back: a client the radio restored nothing for is allocated
            // to the bound once the fence answers.
            var h = new StationHarness(FencePolicies());
            h.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            h.Port.Capacity = 4;
            h.Port.LegacyTarget = 4;
            h.OurClientAdded();
            h.RadioHonoursPanafallRequests();
            h.Waiter.Then(() => h.ConnectFence());    // answered on the first wait; nothing arrived before it

            var r = h.Run();

            Assert.Equal(StationLayout.SlicesWithNoRememberedLayout, h.Port.PanafallRequests);
            Assert.Equal(AllocationStop.TargetReached, r.Allocation.Stop);
            Assert.Equal(StationLayout.SlicesWithNoRememberedLayout, r.OwnSlicesAtEnd);
            Assert.True(r.StationEstablished);
        }

        [Fact]
        public void AFenceAnsweredBeforeTheStationPhaseBegins_CostsNoWait()
        {
            var h = new StationHarness(FencePolicies());
            h.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            h.Port.Capacity = 4;
            h.OurClientAdded();
            h.ConnectFence();                          // already in before Run, as on a quick LAN connect
            h.RadioHonoursPanafallRequests();

            var r = h.Run();

            Assert.Equal(StationLayout.SlicesWithNoRememberedLayout, h.Port.PanafallRequests);
            Assert.True(r.StationEstablished);
        }

        [Fact]
        public void TheOwnersRefusedIntent_NoelsCase_AllocatesUnderTheFence_AndPreservesARestoredLayout()
        {
            // The route that reported zero slices for weeks: the owner with the
            // UseMyTransmitAudio intent is refused at step 2 and goes straight
            // to the client-local allocation, before the roster or profile
            // reads that would otherwise have given the restore time to land.
            var fresh = new StationHarness(FencePolicies());
            fresh.Port.Facts.Ownership = RadioOwnership.Mine;
            fresh.Port.Facts.Intent = ProfileGuestIntent.UseMyTransmitAudio;
            fresh.Port.Capacity = 4;
            fresh.OurClientAdded();
            fresh.RadioHonoursPanafallRequests();
            fresh.Waiter.Then(() => fresh.ConnectFence());
            var r1 = fresh.Run();
            Assert.Equal(StationLayout.SlicesWithNoRememberedLayout, fresh.Port.PanafallRequests);

            var returning = new StationHarness(FencePolicies());
            returning.Port.Facts.Ownership = RadioOwnership.Mine;
            returning.Port.Facts.Intent = ProfileGuestIntent.UseMyTransmitAudio;
            returning.Port.Capacity = 4;
            returning.OurClientAdded();
            returning.RadioHonoursPanafallRequests();
            returning.Waiter.Then(() => returning.OwnSliceArrives())
                            .Then(() => returning.OwnSliceArrives())
                            .Then(() => returning.ConnectFence());
            var r2 = returning.Run();
            Assert.Equal(0, returning.Port.PanafallRequests);
            Assert.Equal(AllocationStop.ExistingLayoutPreserved, r2.Allocation.Stop);
            Assert.Equal(2, r2.OwnSlicesAtEnd);
        }

        private static MaterializationEvidence Evidence(StationHarness h) => new MaterializationEvidence
        {
            Roster = h.Roster.Snapshot(),
            Station = h.Station.Snapshot(),
            Profiles = h.Profiles.Snapshot(),
        };
    }
}
