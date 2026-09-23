using System;
using System.Collections.Generic;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// Lifecycle and dispatch isolation on a REAL FlexBase with a vendored
    /// Radio (Track G2 re-review, section 1 step 1 and section 2): a retry
    /// on the same radio object replaces the closures rather than re-pointing
    /// the binding; a closure whose binding is no longer current drops its
    /// callback whole, legacy state included; deferred work checks the
    /// operation that queued it, not whichever is current.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class LifecycleIsolationTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(LifecycleIsolationTests));
        private readonly List<RigOnVendorRadio> _rigs = new();
        public void Dispose()
        {
            foreach (var r in _rigs) r.Dispose();
            _scope.Dispose();
        }

        private static int _serialCounter;
        private static string UniqueSerial()
        {
            int n = System.Threading.Interlocked.Increment(ref _serialCounter);
            return "9997-" + (1000 + Environment.ProcessId % 9000).ToString("D4") + "-" + (1000 + n).ToString("D4") + "-7236";
        }

        private RigOnVendorRadio NewRig()
        {
            var rig = new RigOnVendorRadio(UniqueSerial());
            _rigs.Add(rig);
            return rig;
        }

        private static readonly MethodInfo ObserveClientAdded =
            typeof(FlexBase).GetMethod("ObserveClientAdded", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo RosterTrackerField =
            typeof(FlexBase).GetField("_rosterTracker", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static RosterTracker RosterOf(FlexBase rig) => (RosterTracker)RosterTrackerField.GetValue(rig)!;

        [Fact]
        public void ARetryOnTheSameRadio_ReplacesTheClosures_AndAHeldOldCallbackIsRejected()
        {
            var r = NewRig();
            Assert.True(ObserveClientAdded != null && RosterTrackerField != null, "the feed and tracker are not where this test reaches them");
            r.Rig.WireStationHandlers(r.Vendor.Radio);
            Assert.Equal(1, r.Rig.StationWiringCount);
            var oldBinding = r.Rig.BindingFor(r.Vendor.Radio);
            int legacyHandlerRuns = 0;
            r.Rig.GuiClientChanged += () => legacyHandlerRuns++;

            r.Rig.BeginStationAttempt(r.Vendor.Radio, "RetryConnect");   // the retry: same object, new attempt

            var newBinding = r.Rig.BindingFor(r.Vendor.Radio);
            Assert.NotSame(oldBinding, newBinding);
            Assert.True(oldBinding.Attempt.IsCancelled);
            Assert.NotEqual(oldBinding.Generation, newBinding.Generation);
            Assert.Equal(1, r.Rig.StationWiringCount);                    // one live wiring, not two

            // The radio raises an add. Exactly ONE closure handles it (the
            // old one was unwired), and it counts for the new attempt.
            var other = new GUIClient(0x5E6F7A8B, "other-id", "SmartSDR", "W1AW", is_local_ptt: false);
            r.Vendor.Radio.UpdateGuiClientsList(new List<GUIClient> { other });
            Assert.Equal(1, legacyHandlerRuns);
            Assert.Single(RosterOf(r.Rig).Snapshot().Entries);

            // A callback that started under the OLD subscription finishes
            // now: it stamps the old generation and is rejected.
            var late = new GUIClient(0x77777777, "late-id", "SmartSDR", "K1LATE", is_local_ptt: false);
            ObserveClientAdded.Invoke(r.Rig, new object[] { oldBinding, late, false });
            Assert.Single(RosterOf(r.Rig).Snapshot().Entries);
        }

        [Fact]
        public void ACallbackFromARadioObjectWeHaveLeft_IsDroppedWhole_LegacyStateIncluded()
        {
            // Connect to A, then begin an attempt on B (a new radio object).
            // Discovery goes on raising events on A through the closures
            // still wired there; until Track G3 the feed rejected the
            // observation but the handler body ran and mutated legacy state.
            var r = NewRig();
            r.Rig.WireStationHandlers(r.Vendor.Radio);
            int legacyHandlerRuns = 0;
            r.Rig.GuiClientChanged += () => legacyHandlerRuns++;
            var radioB = new VendorRadioFixture(UniqueSerial()).Radio;

            r.Rig.BeginStationAttempt(radioB, "Connect to another radio");

            var other = new GUIClient(0x5E6F7A8B, "other-id", "SmartSDR", "W1AW", is_local_ptt: false);
            r.Vendor.Radio.UpdateGuiClientsList(new List<GUIClient> { other });   // A speaks

            Assert.Equal(0, legacyHandlerRuns);
            Assert.Empty(RosterOf(r.Rig).Snapshot().Entries);
            // The add, and the vendor's PropertyChanged notifications that
            // accompany it, were all dropped by A's stale closures.
            Assert.True(r.Rig.StaleCallbacksDropped >= 1);

            // The positive control: wired on B, B's add is handled.
            r.Rig.WireStationHandlers(radioB);
            radioB.UpdateGuiClientsList(new List<GUIClient> { other });
            Assert.Equal(1, legacyHandlerRuns);
            Assert.Single(RosterOf(r.Rig).Snapshot().Entries);
        }

        // ── deferred work carries the operation that queued it ──

        private static readonly FieldInfo PendingOperationField =
            typeof(FlexBase).GetField("_pendingLiveTxApplyOperation", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo DeferredRefusal =
            typeof(FlexBase).GetMethod("DeferredLiveAudioRefusal", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static string Refusal(FlexBase rig) =>
            (string)DeferredRefusal.Invoke(rig, new object[] { "Contest", new AudioChainPreset("Contest") })!;

        [Fact]
        public void TheDeferredApply_RefusesWhenTheOperationThatQueuedItEnded_EvenThoughTheCurrentOneIsLive()
        {
            var r = NewRig();
            Assert.True(PendingOperationField != null && DeferredRefusal != null, "the deferred apply's operation token is not where this test reaches it");
            var connect = r.Rig.StationAttempt.BeginOperation("station establishment on connect");
            PendingOperationField.SetValue(r.Rig, connect);           // deferred under the connect

            var postImport = r.Rig.StationAttempt.BeginOperation("post-import station re-establishment");
            Assert.True(postImport.IsLive);                            // the CURRENT operation is live
            Assert.True(r.Rig.StationAttempt.IsLive);

            string refusal = Refusal(r.Rig);

            Assert.NotNull(refusal);
            Assert.Contains("operation ended", refusal);
            Assert.Contains("superseded", refusal);
        }

        [Fact]
        public void TheDeferredApply_UnderItsOwnLiveOperation_IsNotRefusedForTheOperation()
        {
            // The positive control: the same rig, the deferred apply's own
            // operation still current. Whatever else refuses (this rig is
            // not connected), it is not the operation.
            var r = NewRig();
            var connect = r.Rig.StationAttempt.BeginOperation("station establishment on connect");
            PendingOperationField.SetValue(r.Rig, connect);

            string refusal = Refusal(r.Rig);

            Assert.DoesNotContain("operation ended", refusal ?? "");
            Assert.DoesNotContain("originating operation", refusal ?? "");
        }

        [Fact]
        public void TheDeferredApply_WithNoRecordedOperation_Refuses()
        {
            var r = NewRig();
            r.Rig.StationAttempt.BeginOperation("station establishment on connect");
            PendingOperationField.SetValue(r.Rig, null);

            string refusal = Refusal(r.Rig);

            Assert.Contains("no originating operation", refusal);
        }
    }
}
