using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// The three-case owner connect ruled 2026-09-22 (#590). Case 1 (nobody
    /// else on: send the load once, say honestly, never top up) is
    /// ProductionDefaults_NobodyElseOn_* in StationCoordinatorTests. Here:
    /// case 2 (someone else on: no load over them; the owner's frequencies
    /// on free slices, per-client, each confirmed by the slice's own report)
    /// and case 3 (they leave: the load at the operator's request only).
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class OwnerConnectCasesTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(OwnerConnectCasesTests));
        public void Dispose() => _scope.Dispose();

        private static StationLayout TwoSlices() => new StationLayout
        {
            Slices = { new SliceLayoutEntry(14_250_000, "USB"), new SliceLayoutEntry(7_150_000, "LSB") },
            ProfileName = "K5NER",
        };

        private static StationHarness OwnerWithCompany(StationPolicies policies = null)
        {
            var h = new StationHarness(policies);
            h.ArrangeOwnerReconnect();
            h.OtherClientAdded();
            h.Port.Capacity = 2;
            h.Port.LegacyTarget = 4;
            h.Port.OwnerLayout = TwoSlices();
            h.RadioHonoursPanafallRequests();
            return h;
        }

        // ── case 2 ──

        [Fact]
        public void OwnerWithCompany_NoLoadOverThem_FrequenciesOnTheFreeSlices_EachConfirmedByTheSlice()
        {
            // The positive control for case 2.
            var h = OwnerWithCompany();

            var r = h.Run();

            Assert.Equal(StationOutcome.PolicySkipped, r.Outcome);
            Assert.True(r.OwnerRefusedForCompany);
            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.Equal(2, h.Port.PanafallRequests);
            Assert.Equal(new[] { (0, 14_250_000L, "USB"), (1, 7_150_000L, "LSB") }, h.Port.TunesSent);
            Assert.Equal(PlacementStop.Completed, r.Placement.Stop);
            Assert.Equal(2, r.Placement.Placed);
            Assert.True(r.StationEstablished);
        }

        [Fact]
        public void OwnerWithCompany_NoLayoutKnown_TunesNothing_AndSaysSo()
        {
            var h = OwnerWithCompany();
            h.Port.OwnerLayout = null;

            var r = h.Run();

            Assert.Empty(h.Port.TunesSent);
            Assert.Equal(PlacementStop.NoLayoutKnown, r.Placement.Stop);
            Assert.Equal(2, r.OwnSlicesAtEnd);               // the free slices are still there
        }

        [Fact]
        public void OwnerWithCompany_ATuneTheSliceDoesNotReport_IsUnconfirmed_AndStopsThePlacement()
        {
            var h = OwnerWithCompany();
            h.Port.OnTuneSent = (index, hz, mode) => { };   // the slice never reports the tune

            var r = h.Run();

            Assert.Single(h.Port.TunesSent);
            Assert.Equal(PlacementStop.Unconfirmed, r.Placement.Stop);
            Assert.Equal(0, r.Placement.Placed);
            Assert.Equal(1, r.Placement.Sent);
        }

        [Fact]
        public void OwnerWithCompany_OnlyOneFreeSlice_PlacesOnlyTheFirstFrequency()
        {
            var h = OwnerWithCompany();
            h.Port.Capacity = 1;

            var r = h.Run();

            Assert.Equal(new[] { (0, 14_250_000L, "USB") }, h.Port.TunesSent);
            Assert.Equal(1, r.Placement.Wanted);
            Assert.Equal(1, r.Placement.Placed);
        }

        [Fact]
        public void OwnerWithCompany_UnderTheProductionDefaults_PlacesNothing_BecauseNoSliceIsAllocated()
        {
            // D is closed: no fresh allocation, so nothing to place on. The
            // load is still not sent over the other operator.
            var h = OwnerWithCompany(StationPolicies.Defaults());

            var r = h.Run();

            Assert.True(r.OwnerRefusedForCompany);
            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.Equal(0, h.Port.PanafallRequests);
            Assert.Empty(h.Port.TunesSent);
            Assert.Equal(PlacementStop.NoSlices, r.Placement.Stop);
        }

        [Fact]
        public void AGuestWithCompany_GetsReceiveSlices_AndNoFrequencyCoordinationOfAnyKind()
        {
            // Pre-Connect, a guest gets receive slices on free capacity and
            // touches nothing shared. Nothing more.
            var h = OwnerWithCompany();
            h.Port.Facts.Ownership = RadioOwnership.SomeoneElses;

            var r = h.Run();

            Assert.False(r.OwnerRefusedForCompany);
            Assert.Empty(h.Port.GlobalLoadsSent);
            Assert.Equal(2, h.Port.PanafallRequests);
            Assert.Empty(h.Port.TunesSent);
            Assert.Equal(PlacementStop.NotAttempted, r.Placement.Stop);
        }

        [Fact]
        public void HeldPlacement_HoldArmedBeforeRelease_IsNotSent()
        {
            var h = OwnerWithCompany();
            // Hold only the placement dispatches: the loop starts holding
            // after the second (last) panafall request has been answered.
            h.Port.OnPanafallRequested = () =>
            {
                h.OwnSliceArrives();
                if (h.Port.Capacity > 0) h.Port.Capacity--;
                if (h.Port.PanafallRequests == 2) h.Port.HoldDispatch = true;
            };
            h.Waiter.Then(() => { h.Port.Facts.HoldArmed = true; h.Port.ReleaseHeld(); });

            var r = h.Run();

            Assert.Empty(h.Port.TunesSent);
            Assert.NotEqual(PlacementStop.Completed, r.Placement.Stop);
        }

        // ── case 3 ──

        [Fact]
        public void AfterTheOtherOperatorLeaves_TheRequestedLoad_IsSentOnce_AndNeverToppedUp()
        {
            var h = OwnerWithCompany();
            var connect = h.Run();
            Assert.True(connect.OwnerRefusedForCompany);
            Assert.Empty(h.Port.GlobalLoadsSent);

            h.ClientRemoved(StationHarness.OtherHandle);       // the radio's own status: they left
            h.Port.OnGlobalLoadSent = _ => h.DeliverGenuineCompletion();
            int panafallsBefore = h.Port.PanafallRequests;

            var r = h.Coordinator(previous: connect).RunOperatorRequestedLoad();

            Assert.Equal(StationOutcome.RestoredConfirmed, r.Outcome);
            Assert.Equal(new[] { "K5NER" }, h.Port.GlobalLoadsSent);
            Assert.Equal(panafallsBefore, h.Port.PanafallRequests);
            Assert.Equal(AllocationStop.RouteForbids, r.Allocation.Stop);
        }

        [Fact]
        public void TheRequestedLoad_DoesNotTakeTheExistingStationShortcut()
        {
            // The name is selected and our slices are present (the placement
            // put them there). An explicit request is a request.
            var h = OwnerWithCompany();
            var connect = h.Run();
            h.ClientRemoved(StationHarness.OtherHandle);
            h.Port.OnGlobalLoadSent = _ => h.DeliverGenuineCompletion();

            var r = h.Coordinator(previous: connect).RunOperatorRequestedLoad();

            Assert.Single(h.Port.GlobalLoadsSent);
            Assert.Equal(GlobalRoute.LoadExisting, r.Route);
        }

        [Fact]
        public void TheRequestedLoad_IsRefusedWhileTheyAreStillOn_OrUnderTheHold()
        {
            var still = OwnerWithCompany();
            var c1 = still.Run();
            var r1 = still.Coordinator(previous: c1).RunOperatorRequestedLoad();
            Assert.Equal(StationOutcome.PolicySkipped, r1.Outcome);
            Assert.Empty(still.Port.GlobalLoadsSent);

            var held = OwnerWithCompany();
            var c2 = held.Run();
            held.ClientRemoved(StationHarness.OtherHandle);
            held.Port.Facts.HoldArmed = true;
            var r2 = held.Coordinator(previous: c2).RunOperatorRequestedLoad();
            Assert.Equal(StationOutcome.PolicySkipped, r2.Outcome);
            Assert.Empty(held.Port.GlobalLoadsSent);
        }

        [Fact]
        public void TheRequestedLoad_UnderTheProductionDefaults_IsSentAndLeftHonestlyUnconfirmed()
        {
            var h = OwnerWithCompany(StationPolicies.Defaults());
            var connect = h.Run();
            h.ClientRemoved(StationHarness.OtherHandle);
            h.Port.OnGlobalLoadSent = _ => h.DeliverGenuineCompletion();

            var r = h.Coordinator(previous: connect).RunOperatorRequestedLoad();

            Assert.Equal(StationOutcome.Unconfirmed, r.Outcome);
            Assert.True(r.LoadSent);
            Assert.Single(h.Port.GlobalLoadsSent);
            Assert.Equal(0, h.Port.PanafallRequests);
        }

        // ── the tune evidence ──

        [Fact]
        public void ATuneIsConfirmedOnlyForTheSameSlice_AfterTheArm_AtTheFrequency()
        {
            var h = new StationHarness();
            h.OurClientAdded();
            int a = h.OwnSliceArrives();
            int b = h.OwnSliceArrives();
            long armed = h.Station.Sequence;
            h.RadioReportsTune(b, 7_150_000, "LSB");

            var s = h.Station.Snapshot();
            Assert.True(s.TunedSince(armed, b, 7_150_000));
            Assert.True(s.TunedSince(armed, b, 7_150_001));  // within one hertz
            Assert.False(s.TunedSince(armed, a, 7_150_000));
            Assert.False(s.TunedSince(armed, b, 14_250_000));
            Assert.False(s.TunedSince(h.Station.Sequence, b, 7_150_000)); // not after a later arm
            h.Station.OwnSliceTuned(9, 1, "AM", h.Gen);          // unknown index: ignored
            Assert.Equal(2, h.Station.Snapshot().OwnSliceCount);
        }

        // ── the sentences exist and are keyed ──

        [Fact]
        public void TheCompanySentencesExist_AndTheOfferIsAJJFlexDialogWithNoAsCancel()
        {
            foreach (var key in new[]
            {
                "settings.profile_station.company.frequencies_placed", "settings.profile_station.company.no_layout_known",
                "settings.profile_station.company.nothing_placed", "settings.profile_station.company.brief",
                "settings.profile_station.requested.loaded", "settings.profile_station.requested.sent_unconfirmed",
                "settings.profile_station.requested.not_sent", "settings.profile_station.offer.title",
                "settings.profile_station.offer.message", "settings.profile_station.offer.warning",
                "settings.profile_station.offer.question", "settings.profile_station.offer.yes", "settings.profile_station.offer.no",
            })
            {
                Assert.False(string.IsNullOrWhiteSpace(Lexicon.Get(key)), key);
            }
            Assert.Equal("profile-station-company", Speech.SpeechSubject.ProfileStationCompany);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var menu = File.ReadAllText(Path.Combine(dir.FullName, "JJFlexWpf", "NativeMenuBar.cs"));
            Assert.Contains("rig.OwnerProfileLoadOffered += OnOwnerProfileLoadOffered;", menu, StringComparison.Ordinal);
            Assert.Contains("new Dialogs.ConfirmActionDialog(", menu.Substring(menu.IndexOf("private void OnOwnerProfileLoadOffered()", StringComparison.Ordinal)), StringComparison.Ordinal);
            var xaml = File.ReadAllText(Path.Combine(dir.FullName, "JJFlexWpf", "Dialogs", "ConfirmActionDialog.xaml"));
            Assert.Contains("IsCancel=\"True\"", xaml, StringComparison.Ordinal);   // Escape closes it as No
        }
    }
}
