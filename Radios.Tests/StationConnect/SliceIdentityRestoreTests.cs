using System;
using System.Collections.Generic;
using System.IO;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// Track G review, section 8, second bullet: the RX/TX identity capture
    /// is scoped to the client-local allocation that needs it, decided by
    /// object identity, and never replayed over a restored layout.
    /// </summary>
    public sealed class SliceIdentityRestoreTests
    {
        private sealed class FakeSlice { public int Index; public FakeSlice(int i) { Index = i; } }

        [Fact]
        public void ASliceStillPresentAfterTheAllocation_IsRestoredAtItsNewPosition()
        {
            // The positive control: the allocation inserted a slice ahead of it.
            var rx = new FakeSlice(2);
            var inserted = new FakeSlice(0);
            var d = SliceIdentityRestore.Decide(rx, rx, new object[] { inserted, rx }, allocationCancelled: false);
            Assert.True(d.RestoreRx);
            Assert.Equal(1, d.RxPosition);
            Assert.Equal(1, d.TxPosition);
        }

        [Fact]
        public void ARemovedSlice_IsNotRestored()
        {
            var rx = new FakeSlice(1);
            var d = SliceIdentityRestore.Decide(rx, null, new object[] { new FakeSlice(0) }, allocationCancelled: false);
            Assert.False(d.RestoreRx);
            Assert.False(d.RestoreTx);
            Assert.Contains("no longer a member", d.Reason);
        }

        [Fact]
        public void ASameIndexReplacement_IsADifferentSlice_AndIsNotRestored()
        {
            // SliceToVFO matched by numeric index; the old object would have
            // selected the newcomer that reused its index.
            var old = new FakeSlice(1);
            var replacement = new FakeSlice(1);
            var d = SliceIdentityRestore.Decide(old, old, new object[] { new FakeSlice(0), replacement }, allocationCancelled: false);
            Assert.False(d.RestoreRx);
            Assert.False(d.RestoreTx);
        }

        [Fact]
        public void ACancelledAllocation_ReplaysNothing()
        {
            var rx = new FakeSlice(1);
            var d = SliceIdentityRestore.Decide(rx, rx, new object[] { rx }, allocationCancelled: true);
            Assert.False(d.RestoreRx);
            Assert.Contains("cancelled", d.Reason);
        }

        // ── the coordinator opens the scope only around a client-local allocation ──

        [Fact]
        public void TheScopeIsOpenedOnlyAroundTheClientLocalAllocation_NeverAroundARestore()
        {
            var restored = new StationHarness();
            restored.ArrangeOwnerReconnect();
            restored.Port.OnGlobalLoadSent = _ => restored.DeliverGenuineCompletion();
            Assert.Equal(StationOutcome.RestoredConfirmed, restored.Run().Outcome);
            Assert.Equal(0, restored.Port.AllocationScopesBegun);
            Assert.Equal(0, restored.Port.AllocationScopesEnded);

            var guest = new StationHarness();
            guest.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            guest.Port.Capacity = 2; guest.Port.LegacyTarget = 2;
            guest.OurClientAdded();
            guest.RadioHonoursPanafallRequests();
            var r = guest.Run();
            Assert.Equal(2, r.Allocation.Obtained);
            Assert.Equal(1, guest.Port.AllocationScopesBegun);
            Assert.Equal(1, guest.Port.AllocationScopesEnded);
            Assert.Equal(AllocationStop.TargetReached, guest.Port.LastAllocationEnded.Stop);
        }

        [Fact]
        public void ACancelledAllocation_EndsItsScopeAsCancelled()
        {
            var h = new StationHarness();
            h.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            h.Port.Capacity = 4; h.Port.LegacyTarget = 4;
            h.OurClientAdded();
            int honoured = 0;
            h.Port.OnPanafallRequested = () => { if (honoured++ == 0) h.OwnSliceArrives(); else h.Attempt.Cancel("cancel"); };

            var r = h.Run();

            Assert.Equal(StationOutcome.Cancelled, r.Outcome);
            Assert.Equal(1, h.Port.AllocationScopesEnded);
            Assert.Equal(AllocationStop.Cancelled, h.Port.LastAllocationEnded.Stop);
        }

        // ── the production adapter: capture at Begin, restore by identity at End, nowhere else ──

        [Fact]
        public void TheProductionCaptureLivesInTheAllocationScope_NotAroundTheWholePhase()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var text = File.ReadAllText(Path.Combine(dir.FullName, "Radios", "FlexBase.StationConnect.cs"));

            int establish = text.IndexOf("internal StationResult EstablishStationOnConnect(bool postImport)", StringComparison.Ordinal);
            int post = text.IndexOf("private PostStationResult RunPostStationPhase(", StringComparison.Ordinal);
            string establishBody = text.Substring(establish, post - establish);
            Assert.DoesNotContain("VFOToSlice(RXVFO)", establishBody, StringComparison.Ordinal);
            Assert.DoesNotContain("oldRXSlice", establishBody, StringComparison.Ordinal);

            int begin = text.IndexOf("public void BeginClientLocalAllocation()", StringComparison.Ordinal);
            int end = text.IndexOf("public void EndClientLocalAllocation(AllocationResult allocation)", StringComparison.Ordinal);
            Assert.True(begin > 0 && end > begin);
            string endBody = text.Substring(end, 1200);
            Assert.Contains("SliceIdentityRestore.Decide(", endBody, StringComparison.Ordinal);
        }
    }
}
