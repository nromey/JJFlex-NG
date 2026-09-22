using System;
using System.Collections.Generic;
using System.IO;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// Track G review, section 1 step 9 and section 6 group 7: the live
    /// transmit-audio path's hardening, tested at each queued setter, at the
    /// snapshot's persistence, and at the deferred-apply rule.
    /// </summary>
    public sealed class LiveTransmitAudioGateTests
    {
        // ── the queue gate: validation inside EVERY queued setter, at its run ──

        [Fact]
        public void AQueuedItemRunsWhenTheGateStillPermitsAtItsRun()
        {
            // The positive control.
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests", StationName = "K5TEST" });
            try
            {
                var q = rig.q;
                q.MainLoop = true;
                int ran = 0;
                using (var gate = QueuedWriteGate.Open(() => null))
                {
                    q.Enqueue((FlexBase.FunctionDel)(() => ran++), "setter");
                    Assert.Equal(0, ran);                             // queued, not run
                    RunQueue(q);
                    Assert.Equal(1, ran);
                    Assert.Equal(1, gate.Ran);
                    Assert.Equal(0, gate.Refused);
                    Assert.True(gate.Complete);
                }
            }
            finally { rig.Dispose(); }
        }

        [Fact]
        public void AQueuedItemIsRefusedWhenTheGateChangesBetweenEnqueueAndRun()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests", StationName = "K5TEST" });
            try
            {
                var q = rig.q;
                q.MainLoop = true;
                int ran = 0;
                string answer = null;
                using (var gate = QueuedWriteGate.Open(() => answer))
                {
                    q.Enqueue((FlexBase.FunctionDel)(() => ran++), "MicLevel");
                    q.Enqueue((FlexBase.FunctionDel)(() => ran++), "CompanderOn");
                    answer = "another operator joined";                // the world changed
                    RunQueue(q);
                    Assert.Equal(0, ran);
                    Assert.Equal(2, gate.Refused);
                    Assert.Equal("another operator joined", gate.FirstRefusal);
                    Assert.False(gate.Complete);
                }
            }
            finally { rig.Dispose(); }
        }

        [Fact]
        public void TheGateIsPerThreadAndScoped_ItemsQueuedOutsideItAreUngated()
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests", StationName = "K5TEST" });
            try
            {
                var q = rig.q;
                q.MainLoop = true;
                int ran = 0;
                using (QueuedWriteGate.Open(() => "refuse everything")) { }
                q.Enqueue((FlexBase.FunctionDel)(() => ran++), "after the scope");
                RunQueue(q);
                Assert.Equal(1, ran);
                Assert.Null(QueuedWriteGate.Ambient);
            }
            finally { rig.Dispose(); }
        }

        private static void RunQueue(FlexBase.q_t q)
        {
            while (q.Count > 0)
            {
                var item = q.Dequeue();
                if (item.Item is FlexBase.FunctionDel f) f();
            }
        }

        // ── the snapshot's persistence gates the apply ──

        [Fact]
        public void ASnapshotThatDidNotReachDisk_IsNotADurableCapture()
        {
            var preset = new AudioChainPreset("live") { MicGain = 40 };
            string root = Path.Combine(Path.GetTempPath(), "jjflex-g2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "a file where a directory is needed");
            string impossible = Path.Combine(blocker, "snapshot.xml");
            try
            {
                Assert.False(LiveTxSnapshotStore.Persist(impossible, preset));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void ASnapshotThatReachedDisk_ReadsBackEqual()
        {
            var preset = new AudioChainPreset("live") { MicGain = 40, TxFilterLow = 150, TxFilterHigh = 2900 };
            string path = Path.Combine(Path.GetTempPath(), "jjflex-g2-" + Guid.NewGuid().ToString("N"), "snapshot.xml");
            try
            {
                Assert.True(LiveTxSnapshotStore.Persist(path, preset));
                Assert.True(AudioChainPreset.TryLoad(path, out var back));
                Assert.Equal(40, back.MicGain);
                Assert.Equal(150, back.TxFilterLow);
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(path), true); } catch { } }
        }

        // ── the deferred-apply rule, every condition named ──

        private static DeferredLiveAudioFacts Clean() => new DeferredLiveAudioFacts
        {
            ChosenLocalPreset = "Contest", PendingPreset = "Contest",
            SnapshotChainGeneration = 3, ChainGenerationNow = 3,
            RadioIsOurs = false, ReportedAutosave = false,
        };

        [Fact]
        public void TheCleanCase_Permits()
        {
            Assert.Null(DeferredLiveAudioGate.Refusal(Clean()));
        }

        public static IEnumerable<object[]> Refusals()
        {
            yield return new object[] { "operation ended", (Action<DeferredLiveAudioFacts>)(f => { f.OperationLive = false; f.OperationEndReason = "superseded"; }), "operation ended" };
            yield return new object[] { "other attempt", (Action<DeferredLiveAudioFacts>)(f => f.ApplyAttemptMatches = false), "different connection attempt" };
            yield return new object[] { "not connected", (Action<DeferredLiveAudioFacts>)(f => f.Connected = false), "not connected" };
            yield return new object[] { "hold", (Action<DeferredLiveAudioFacts>)(f => f.HoldArmed = true), "hold" };
            yield return new object[] { "company", (Action<DeferredLiveAudioFacts>)(f => f.StrictRoster = RosterVerdict.OthersPresent), "roster" };
            yield return new object[] { "roster unknown", (Action<DeferredLiveAudioFacts>)(f => f.StrictRoster = RosterVerdict.Unknown), "roster" };
            yield return new object[] { "intent changed", (Action<DeferredLiveAudioFacts>)(f => f.Intent = ProfileGuestIntent.LeaveAlone), "intent" };
            yield return new object[] { "preset choice changed", (Action<DeferredLiveAudioFacts>)(f => f.ChosenLocalPreset = "Ragchew"), "choice changed" };
            yield return new object[] { "payload not held", (Action<DeferredLiveAudioFacts>)(f => f.PendingPayloadHeld = false), "payload" };
            yield return new object[] { "no snapshot", (Action<DeferredLiveAudioFacts>)(f => f.SnapshotHeld = false), "snapshot" };
            yield return new object[] { "snapshot other attempt", (Action<DeferredLiveAudioFacts>)(f => f.SnapshotAttemptMatches = false), "snapshot belongs" };
            yield return new object[] { "chain changed", (Action<DeferredLiveAudioFacts>)(f => f.ChainGenerationNow = 4), "chain changed" };
            yield return new object[] { "unsaved work", (Action<DeferredLiveAudioFacts>)(f => f.OwnerHasUnsavedWork = true), "unsaved" };
            yield return new object[] { "autosave not reported off", (Action<DeferredLiveAudioFacts>)(f => f.ReportedAutosave = null), "autosave" };
            yield return new object[] { "autosave reported on", (Action<DeferredLiveAudioFacts>)(f => f.ReportedAutosave = true), "autosave" };
        }

        [Theory]
        [MemberData(nameof(Refusals))]
        public void EachWithdrawnCondition_Refuses(string what, Action<DeferredLiveAudioFacts> withdraw, string fragment)
        {
            var f = Clean();
            withdraw(f);
            string refusal = DeferredLiveAudioGate.Refusal(f);
            Assert.NotNull(refusal);
            Assert.Contains(fragment, refusal, StringComparison.OrdinalIgnoreCase);
            _ = what;
        }

        [Fact]
        public void OnOurOwnRadio_AutosaveIsTheOperatorsOwn_AndNotRequiredOff()
        {
            var f = Clean();
            f.RadioIsOurs = true;
            f.ReportedAutosave = true;
            Assert.Null(DeferredLiveAudioGate.Refusal(f));
        }
    }
}
