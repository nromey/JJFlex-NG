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

        // ── the offer is bound to the attempt it was made on (Track G3, group 6) ──

        [Fact]
        public void AnOfferFromAnEarlierAttempt_IsRefused_AndACurrentOneIsNot()
        {
            var r = NewRig();
            var serial = r.Vendor.Radio.Serial;
            var stale = new OwnerLoadOffer(r.Rig.StationAttempt.Generation, serial);
            r.Rig.BeginStationAttempt(r.Vendor.Radio, "RetryConnect");     // the prompt was open across this

            var refused = r.Rig.LoadOwnerGlobalProfileOnRequest(stale);

            Assert.NotNull(refused);
            Assert.Equal(StationOutcome.Cancelled, refused.Outcome);
            Assert.Contains("earlier connection", refused.Reason);
            Assert.Empty(r.Vendor.Transport.Commands);

            // The positive control: the current attempt's offer reaches the
            // coordinator (which then refuses on this rig's facts, not on
            // the offer), and still sends nothing.
            var current = new OwnerLoadOffer(r.Rig.StationAttempt.Generation, serial);
            var reached = r.Rig.LoadOwnerGlobalProfileOnRequest(current);
            Assert.NotNull(reached);
            Assert.DoesNotContain("earlier connection", reached.Reason);
            Assert.NotEqual(StationOutcome.Cancelled, reached.Outcome);
            Assert.Empty(r.Vendor.Transport.Commands);
        }

        // ── discovery-first departure, then the radio's disconnected status (Track G3, group 6) ──

        private static GUIClient Mine() => new GUIClient(RigOnVendorRadio.OurHandle, "our-client-id", "JJFlex", "K5NER", is_local_ptt: true);
        private static GUIClient Other() => new GUIClient(0x5E6F7A8B, "other-id", "SmartSDR", "W1AW", is_local_ptt: false);

        [Fact]
        public void ADiscoveryFirstDeparture_ThenTheDisconnectedStatus_LeavesTheOtherPresent_Conservatively()
        {
            // The vendor removes the client from Radio.GuiClients on the
            // discovery sweep, so the later "client ... disconnected" status
            // finds nothing and raises no removal. The tracker keeps the
            // discovery-only disappearance as PRESENT (ruled 2026-09-22).
            // Conservative, not unsafe: pinned here as the intended shape so
            // it is deliberate rather than accidental (re-review section 3).
            var r = NewRig();
            r.Rig.WireStationHandlers(r.Vendor.Radio);
            r.Vendor.Radio.UpdateGuiClientsList(new List<GUIClient> { Mine(), Other() });
            Assert.Equal(RosterVerdict.OthersPresent, RosterGuard.Evaluate(RosterOf(r.Rig).Snapshot()).Verdict);

            r.Vendor.Radio.UpdateGuiClientsList(new List<GUIClient> { Mine() });            // discovery no longer lists them
            var afterDiscovery = RosterGuard.Evaluate(RosterOf(r.Rig).Snapshot());
            Assert.Equal(RosterVerdict.OthersPresent, afterDiscovery.Verdict);
            Assert.Contains("discovery", afterDiscovery.Reason);

            r.Vendor.Status("client 0x5E6F7A8B disconnected forced=0");                     // the radio's own status, too late
            var afterStatus = RosterGuard.Evaluate(RosterOf(r.Rig).Snapshot());
            Assert.Equal(RosterVerdict.OthersPresent, afterStatus.Verdict);
            Assert.Contains("discovery", afterStatus.Reason);
            Assert.NotEqual(RosterVerdict.OnlyUs, r.Rig.RosterJudgementForAutomaticWrite().Verdict);
        }

        [Fact]
        public void TheDisconnectedStatusFirst_IsALeave_AndTheRosterIsOnlyUs()
        {
            // The positive control for the sequence above: the same rig,
            // the radio's own status arriving while the vendor still holds
            // the record, reaches RemoveGUIClient and the tracker's real
            // removal.
            var r = NewRig();
            r.Rig.WireStationHandlers(r.Vendor.Radio);
            r.Vendor.Radio.UpdateGuiClientsList(new List<GUIClient> { Mine(), Other() });
            Assert.Equal(RosterVerdict.OthersPresent, RosterGuard.Evaluate(RosterOf(r.Rig).Snapshot()).Verdict);

            r.Vendor.Status("client 0x5E6F7A8B disconnected forced=0");

            Assert.Equal(RosterVerdict.OnlyUs, RosterGuard.Evaluate(RosterOf(r.Rig).Snapshot()).Verdict);
            Assert.Equal(RosterVerdict.OnlyUs, r.Rig.RosterJudgementForAutomaticWrite().Verdict);
        }

        // ── deferred work carries the operation that queued it ──

        private static readonly FieldInfo PendingOperationField =
            typeof(FlexBase).GetField("_pendingLiveTxApplyOperation", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo PendingPresetField =
            typeof(FlexBase).GetField("_pendingLiveTxApplyPreset", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo PendingPayloadField =
            typeof(FlexBase).GetField("_pendingLiveTxApplyPayload", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo TakeBatch =
            typeof(FlexBase).GetMethod("TakePendingLiveAudioBatch", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo DeferredRefusal =
            typeof(FlexBase).GetMethod("DeferredLiveAudioRefusal", BindingFlags.NonPublic | BindingFlags.Instance)!;

        /// <summary>Queue an apply the way the connect does, then take it off
        /// the fields the way the command loop does. What comes back is the
        /// batch identity every gate for that apply is judged against.</summary>
        private static object QueueAndTakeBatch(FlexBase rig, StationOperation under)
        {
            PendingPresetField.SetValue(rig, "Contest");
            PendingPayloadField.SetValue(rig, new AudioChainPreset("Contest"));
            PendingOperationField.SetValue(rig, under);
            return TakeBatch.Invoke(rig, null)!;
        }

        private static string Refusal(FlexBase rig, object batch) =>
            (string)DeferredRefusal.Invoke(rig, new[] { batch })!;

        [Fact]
        public void TheDeferredApply_RefusesWhenTheOperationThatQueuedItEnded_EvenThoughTheCurrentOneIsLive()
        {
            var r = NewRig();
            Assert.True(PendingOperationField != null && DeferredRefusal != null, "the deferred apply's operation token is not where this test reaches it");
            var connect = r.Rig.StationAttempt.BeginOperation("station establishment on connect");
            var batch = QueueAndTakeBatch(r.Rig, connect);            // deferred under the connect

            var postImport = r.Rig.StationAttempt.BeginOperation("post-import station re-establishment");
            Assert.True(postImport.IsLive);                            // the CURRENT operation is live
            Assert.True(r.Rig.StationAttempt.IsLive);

            string refusal = Refusal(r.Rig, batch);

            Assert.NotNull(refusal);
            Assert.Contains("operation ended", refusal);
            Assert.Contains("superseded", refusal);
        }

        [Fact]
        public void TheDeferredApply_IsJudgedAgainstItsOwnBatch_EvenAfterANewerApplyOverwritesTheField()
        {
            // The defect Astra found, and the one the old test could not see
            // because it only ENDED the old operation and left the field
            // pointing at it: the post-import entry records a NEW pending
            // apply while the first apply's setters are still queued behind
            // it. Reading the field at the setter asked about the newer
            // operation, which is live — so the old batch's setters ran with
            // the new batch's permission. The identity travels with the batch
            // now, so the field can say anything it likes.
            var r = NewRig();
            var connect = r.Rig.StationAttempt.BeginOperation("station establishment on connect");
            var firstBatch = QueueAndTakeBatch(r.Rig, connect);

            var postImport = r.Rig.StationAttempt.BeginOperation("post-import station re-establishment");
            PendingPresetField.SetValue(r.Rig, "Contest");             // the SAME preset, so the name cannot tell them apart
            PendingPayloadField.SetValue(r.Rig, new AudioChainPreset("Contest"));
            PendingOperationField.SetValue(r.Rig, postImport);         // the newer operation, live

            string refusal = Refusal(r.Rig, firstBatch);

            Assert.NotNull(refusal);
            Assert.Contains("operation ended", refusal);
        }

        [Fact]
        public void TakingABatch_ClearsThePendingFields_SoNothingCanRunTwice()
        {
            var r = NewRig();
            var connect = r.Rig.StationAttempt.BeginOperation("station establishment on connect");
            QueueAndTakeBatch(r.Rig, connect);

            Assert.True(string.IsNullOrEmpty((string)PendingPresetField.GetValue(r.Rig)));
            Assert.Null(PendingPayloadField.GetValue(r.Rig));
            Assert.Null(PendingOperationField.GetValue(r.Rig));
            Assert.Null(TakeBatch.Invoke(r.Rig, null));
        }

        [Fact]
        public void TheDeferredApply_UnderItsOwnLiveOperation_IsNotRefusedForTheOperation()
        {
            // The positive control: the same rig, the deferred apply's own
            // operation still current. Whatever else refuses (this rig is
            // not connected), it is not the operation.
            var r = NewRig();
            var connect = r.Rig.StationAttempt.BeginOperation("station establishment on connect");
            var batch = QueueAndTakeBatch(r.Rig, connect);

            string refusal = Refusal(r.Rig, batch);

            Assert.DoesNotContain("operation ended", refusal ?? "");
            Assert.DoesNotContain("originating operation", refusal ?? "");
        }

        [Fact]
        public void TheDeferredApply_WithNoRecordedOperation_Refuses()
        {
            var r = NewRig();
            r.Rig.StationAttempt.BeginOperation("station establishment on connect");
            var batch = QueueAndTakeBatch(r.Rig, null);

            string refusal = Refusal(r.Rig, batch);

            Assert.Contains("no originating operation", refusal);
        }
    }
}
