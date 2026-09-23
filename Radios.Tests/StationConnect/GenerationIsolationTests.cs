using System;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// Track G review, section 1 step 1: a callback is stamped with the
    /// attempt its SUBSCRIPTION was wired for, never with whatever attempt is
    /// current when it runs; a post-import re-entry is its own operation; a
    /// load an earlier operation left outstanding is a barrier for the next.
    /// The feed shape here is the production one — a closure holding an
    /// <see cref="ObservationBinding"/> and calling the tracker with
    /// <c>binding.Generation</c>, which is exactly what the FlexBase
    /// handlers do — so the test reaches the adapter's contract without a
    /// FlexLib Radio.
    /// </summary>
    public sealed class GenerationIsolationTests
    {
        private static readonly object RadioA = new object();
        private static readonly object RadioB = new object();

        private static RosterEntry Other() =>
            new RosterEntry(StationHarness.OtherHandle, "other-id", false, "W1AW", "SmartSDR");

        [Fact]
        public void ACallbackThroughTheCurrentBinding_IsAccepted()
        {
            // The positive control for everything below.
            var h = new StationHarness();
            var binding = new ObservationBinding(RadioA, h.Attempt);
            Action feed = () => h.Roster.ClientAdded(Other(), binding.Generation);

            feed();

            Assert.Single(h.Roster.Snapshot().Entries);
        }

        [Fact]
        public void ACallbackWiredOnThePreviousRadioObject_CarriesItsOwnDeadAttempt_AndIsRejected()
        {
            // Discovery keeps updating the radio object we left, and FlexLib
            // never unwires our handlers from it. The closure wired on that
            // object holds ITS binding; a new connect to a different object
            // mints a new attempt and the old binding is not rebound.
            var h = new StationHarness();
            var oldBinding = new ObservationBinding(RadioA, h.Attempt);
            Action heldCallback = () => h.Roster.ClientAdded(Other(), oldBinding.Generation);

            h.NewAttempt();                                   // connect to radio B
            var newBinding = new ObservationBinding(RadioB, h.Attempt);
            Assert.NotEqual(oldBinding.Generation, newBinding.Generation);

            heldCallback();                                   // the old object speaks

            Assert.Empty(h.Roster.Snapshot().Entries);
            Assert.Equal(RosterVerdict.Unknown, RosterGuard.Evaluate(h.Roster.Snapshot()).Verdict);
        }

        [Fact]
        public void ABindingIsImmutable_AHeldCallbackAcrossARetry_StampsTheAttemptItWasWiredFor()
        {
            // A retry on the same radio object mints a NEW binding and new
            // closures (see LifecycleIsolationTests on a real FlexBase); the
            // old closure, still running, holds the old binding and is
            // rejected. Nothing re-points a binding, so there is no window
            // in which a callback in flight reads the new generation.
            var h = new StationHarness();
            var oldBinding = new ObservationBinding(RadioA, h.Attempt);
            Action heldCallback = () => h.Roster.ClientAdded(Other(), oldBinding.Generation);
            int wiredFor = oldBinding.Generation;

            h.NewAttempt();                                   // the retry
            var newBinding = new ObservationBinding(RadioA, h.Attempt);

            heldCallback();                                   // the old closure finishes now
            Assert.Equal(wiredFor, oldBinding.Generation);    // it still says what it was wired for
            Assert.Empty(h.Roster.Snapshot().Entries);        // and is rejected
            Assert.Null(typeof(ObservationBinding).GetMethod("Rebind"));

            h.Roster.ClientAdded(Other(), newBinding.Generation);
            Assert.Single(h.Roster.Snapshot().Entries);       // the new closure counts
        }

        [Fact]
        public void AHeldCompletionCallback_AcrossAReconnectAndAnImport_CompletesNoNewOperation()
        {
            // The review's named experiment: hold a real adapter-shaped
            // callback across a reconnect AND an import, release it, and
            // assert no new operation consumes it.
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            var oldBinding = new ObservationBinding(RadioA, h.Attempt);
            Action heldCompletion = () =>
            {
                h.Station.OwnSliceAdded(0, "A", StationHarness.OurHandle, 0x40000001, oldBinding.Generation);
                h.Profiles.RadioEndBoundary("profile-load-complete", oldBinding.Generation);
            };

            // Reconnect: a new attempt on a different radio object.
            h.NewAttempt();
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => heldCompletion();
            var first = h.Run();
            Assert.Equal(StationOutcome.Unconfirmed, first.Outcome);
            Assert.Equal(0, first.OwnSlicesAtEnd);

            // Import: a second operation on the SAME connection. The held
            // callback is released again; it still belongs to nothing.
            h.Port.GlobalLoadsSent.Clear();
            var second = h.Coordinator(previous: first).Run();
            heldCompletion();
            Assert.NotEqual(StationOutcome.RestoredConfirmed, second.Outcome);
            Assert.Equal(0, h.Station.Snapshot().OwnSliceCount);
        }

        // ------------------------------------------------------------------
        // Operations: the unit below the connection
        // ------------------------------------------------------------------

        [Fact]
        public void BeginningANewOperation_EndsThePreviousOne_AndKeepsTheAttemptLive()
        {
            var attempt = new ConnectionAttempt("1234");
            var first = attempt.BeginOperation("connect");
            Assert.True(first.IsLive);
            Assert.Equal(1, first.Generation);

            var second = attempt.BeginOperation("post-import");

            Assert.False(first.IsLive);
            Assert.Contains("superseded", first.WhyNotLive);
            Assert.True(second.IsLive);
            Assert.Equal(2, second.Generation);
            Assert.True(attempt.IsLive);
            Assert.Same(second, attempt.CurrentOperation);
        }

        [Fact]
        public void CancellingTheAttempt_EndsEveryOperationOnIt()
        {
            var attempt = new ConnectionAttempt("1234");
            var op = attempt.BeginOperation("connect");
            attempt.Cancel("disconnect");
            Assert.False(op.IsLive);
            Assert.Contains("disconnect", op.WhyNotLive);
        }

        [Fact]
        public void HeldLoad_QueuedByAnOperationThatIsSupersededBeforeRelease_SendsNothing()
        {
            // The post-import entry begins a new operation while the connect's
            // queued load is still waiting on the command loop. The old
            // delegate must refuse when it finally runs.
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.HoldDispatch = true;
            h.Waiter.Then(() => { h.Attempt.BeginOperation("post-import re-entry"); h.Port.ReleaseHeld(); });

            var r = h.Run();

            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.False(r.LoadSent);
            Assert.Equal(StationOutcome.Cancelled, r.Outcome);
        }

        [Fact]
        public void APostImportReentry_WithAnOutstandingLoad_SendsNoSecondLoad_AndAllocatesNothing()
        {
            // Operation 1 sends a load the default policy cannot confirm.
            var policies = StationHarness.PositiveControlPolicies();
            policies.LoadCompletion = LoadCompletionUnconfirmedPolicy.Instance;
            var h = new StationHarness(policies);
            h.ArrangeOwnerReconnect();
            h.RadioHonoursPanafallRequests();
            var first = h.Run();
            Assert.Equal(StationOutcome.Unconfirmed, first.Outcome);
            Assert.True(first.LoadOutstanding);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");

            // Operation 2 on the same connection: the barrier persists.
            var second = h.Coordinator(previous: first).Run();

            Assert.Equal(StationOutcome.Unconfirmed, second.Outcome);
            Assert.True(second.LoadOutstanding);
            Assert.Contains("still outstanding", second.Reason);
            Assert.Equal(AllocationStop.LoadOutstanding, second.Allocation.Stop);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");   // still exactly the one
            Assert.Equal(2, second.OperationGeneration);
        }

        [Fact]
        public void APostImportReentry_AfterAConfirmedRestore_ProceedsNormally()
        {
            // The positive control: a confirmed previous operation is no
            // barrier. With the name selected and the restored slice present,
            // the re-entry finds an existing station and sends nothing more.
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.OnGlobalLoadSent = _ => h.DeliverGenuineCompletion();
            var first = h.Run();
            Assert.Equal(StationOutcome.RestoredConfirmed, first.Outcome);

            var second = h.Coordinator(previous: first).Run();

            Assert.Equal(StationOutcome.ExistingStationConfirmed, second.Outcome);
            h.AssertExactlyOneLoadAndNothingElse("K5NER");
        }

        [Fact]
        public void ANewConnection_IsNotBarredByThePreviousConnectionsOutstandingLoad()
        {
            var policies = StationHarness.PositiveControlPolicies();
            policies.LoadCompletion = LoadCompletionUnconfirmedPolicy.Instance;
            var h = new StationHarness(policies);
            h.ArrangeOwnerReconnect();
            var first = h.Run();
            Assert.True(first.LoadOutstanding);

            h.NewAttempt();                                   // a new connection starts clean
            h.ArrangeOwnerReconnect();
            var second = h.Coordinator(previous: first).Run();

            Assert.True(second.LoadSent);
            Assert.Equal(2, h.Port.GlobalLoadsSent.Count);
        }

        // ------------------------------------------------------------------
        // The dispatched delegate outliving its phase (step 5)
        // ------------------------------------------------------------------

        [Fact]
        public void HeldLoad_ReleasedAfterTheStationPhaseDeadline_SendsNothing()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.Port.HoldDispatch = true;
            // The command loop finally runs the delegate after the whole
            // station phase has elapsed.
            h.Waiter.Then(() => { h.Clock.Advance(h.Deadlines.StationPhaseMs + 1); h.Port.ReleaseHeld(); });

            var r = h.Run();

            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.False(r.LoadSent);
            Assert.NotEqual(StationOutcome.RestoredConfirmed, r.Outcome);
        }

        [Fact]
        public void HeldLoad_AGlobalRestorePointAppearsBeforeRelease_SendsNothing()
        {
            var h = new StationHarness();
            h.ArrangeOwnerReconnect();
            h.RadioHonoursPanafallRequests();
            h.Port.HoldDispatch = true;
            h.Waiter.Then(() =>
            {
                h.RadioReportsGlobalList("Default", "K5NER", ProfileRestorePoints.NameFor(ProfileTypes.global));
                h.Port.ReleaseHeld();
            });

            var r = h.Run();

            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.Contains("restore point", r.Reason);
        }
    }
}
