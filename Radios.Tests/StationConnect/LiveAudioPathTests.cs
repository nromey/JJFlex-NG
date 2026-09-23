using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// The connect live-audio path on a REAL FlexBase with a vendored Radio
    /// (Track G2 re-review, step 9): a confirmed autosave-off sets the
    /// session's cleanup obligation; the step refuses at its write; the
    /// capture needs the whole receipt set; a gated queued setter's own echo
    /// does not invalidate the apply; the post-import apply is queued to the
    /// gated path, never run ungated.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class LiveAudioPathTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(LiveAudioPathTests));
        private readonly List<RigOnVendorRadio> _rigs = new();
        public void Dispose()
        {
            foreach (var r in _rigs) r.Dispose();
            _scope.Dispose();
        }

        private static int _serialCounter;
        private static string UniqueSerial()
        {
            int n = Interlocked.Increment(ref _serialCounter);
            return "9996-" + (1000 + Environment.ProcessId % 9000).ToString("D4") + "-" + (1000 + n).ToString("D4") + "-7236";
        }

        private RigOnVendorRadio NewRig()
        {
            var rig = new RigOnVendorRadio(UniqueSerial());
            _rigs.Add(rig);
            return rig;
        }

        private static readonly MethodInfo RunStep =
            typeof(FlexBase).GetMethod("RunLiveAudioActionChecked", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo AutosaveOffThisSession =
            typeof(FlexBase).GetField("_autosaveTurnedOffThisSession", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo IsConnectedField =
            typeof(FlexBase).GetField("_IsConnected", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static ProfileActionOutcome Run(RigOnVendorRadio r, ProfileActionKind kind, StationOperation op, Func<string> refusal) =>
            (ProfileActionOutcome)RunStep.Invoke(r.Rig, new object[]
            {
                new ProfileAction { Kind = kind, ProfileType = ProfileTypes.tx, ProfileName = "Contest", Because = "test" }, op, refusal,
            })!;

        [Fact]
        public void AConfirmedAutosaveOff_SetsTheSessionsCleanupObligation_AndTheDurableNotice()
        {
            Assert.True(RunStep != null && AutosaveOffThisSession != null, "the live-audio step and the session flag are not where this test reaches them");
            var r = NewRig();
            var op = r.Rig.StationAttempt.BeginOperation("connect");
            Assert.False((bool)AutosaveOffThisSession.GetValue(r.Rig)!);
            // The radio answers the autosave command from its own status,
            // a moment after it is sent, as a radio would.
            var radioAnswers = new Thread(() =>
            {
                for (int i = 0; i < 100 && r.Vendor.Transport.Written.Count == 0; i++) Thread.Sleep(10);
                Thread.Sleep(20);
                r.Vendor.Status("radio auto_save=0");
            });
            radioAnswers.Start();

            var outcome = Run(r, ProfileActionKind.TurnAutosaveOff, op, () => null);
            radioAnswers.Join();

            Assert.Equal(ProfileActionOutcome.Confirmed, outcome);
            Assert.Contains("profile autosave off", r.Vendor.Transport.Commands);
            Assert.True((bool)AutosaveOffThisSession.GetValue(r.Rig)!, "a confirmed autosave-off must set the session flag the clean teardown reads");
            Assert.True(RadioConfig.AutosaveTurnedOffByUsOn(r.Vendor.Radio.Serial));
        }

        [Fact]
        public void AnAutosaveOffTheRadioNeverConfirms_IsFailed_AndSetsNoObligation()
        {
            var r = NewRig();
            var op = r.Rig.StationAttempt.BeginOperation("connect");

            var outcome = Run(r, ProfileActionKind.TurnAutosaveOff, op, () => null);

            Assert.Equal(ProfileActionOutcome.Failed, outcome);
            Assert.False((bool)AutosaveOffThisSession.GetValue(r.Rig)!);
        }

        [Fact]
        public void AStepRefusedAtItsWrite_SendsNothing()
        {
            var r = NewRig();
            var op = r.Rig.StationAttempt.BeginOperation("connect");

            var autosave = Run(r, ProfileActionKind.TurnAutosaveOff, op, () => "another operator joined");
            var capture = Run(r, ProfileActionKind.CaptureLiveTransmitAudio, op, () => "another operator joined");

            Assert.Equal(ProfileActionOutcome.Refused, autosave);
            Assert.Equal(ProfileActionOutcome.Refused, capture);
            Assert.Empty(r.Vendor.Transport.Commands);
            Assert.False((bool)AutosaveOffThisSession.GetValue(r.Rig)!);
        }

        [Fact]
        public void TheCaptureRefusesUntilTheRadioHasReportedEveryChainField_ThenCaptures()
        {
            var r = NewRig();
            var op = r.Rig.StationAttempt.BeginOperation("connect");
            var deadlines = StationDeadlines.Default();

            // Only the mic input reported: the old proxy would have accepted this.
            r.Vendor.Status("transmit mic_selection=MIC");
            Assert.Contains("MicInput", r.Profiles.Snapshot().TxChainFieldsReported);
            long before = Environment.TickCount64;
            var partial = Run(r, ProfileActionKind.CaptureLiveTransmitAudio, op, () => null);
            Assert.Equal(ProfileActionOutcome.Failed, partial);
            Assert.True(Environment.TickCount64 - before >= deadlines.AutosaveAndCaptureMs - 50, "the capture must wait its bound for the missing fields");

            // The whole transmit status, as the radio sends it on subscription.
            // This also pins the vendor's PropertyChanged names against
            // LiveAudioCapture.RequiredFields: G2 listed SBMonitorGain and
            // SBMonitorPan, which FlexLib never raises.
            r.Vendor.Status("transmit mic_level=40 mic_boost=0 mic_bias=0 compander=1 compander_level=50 speech_processor_enable=0 "
                + "speech_processor_level=0 lo=100 hi=2800 sb_monitor=0 mon_gain_sb=50 mon_pan_sb=50 mic_selection=MIC");
            Assert.Empty(LiveAudioCapture.MissingFields(r.Profiles.Snapshot().TxChainFieldsReported));
            IsConnectedField?.SetValue(r.Rig, true);

            var full = Run(r, ProfileActionKind.CaptureLiveTransmitAudio, op, () => null);

            Assert.Equal(ProfileActionOutcome.Confirmed, full);
            Assert.True(r.Rig.HasStrandedLiveTransmitAudioSnapshot || SnapshotHeld(r.Rig), "the capture must hold a snapshot");
        }

        private static bool SnapshotHeld(FlexBase rig) =>
            typeof(FlexBase).GetField("_liveTxSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rig) != null;

        [Fact]
        public void AGatedQueuedSettersOwnEcho_DoesNotAdvanceTheChainGeneration()
        {
            // The deferred apply invalidated itself: its first setter's echo
            // was radio-origin to the feed, the chain generation moved, and
            // the next setter refused (Track G2 re-review, step 9).
            var r = NewRig();
            var q = r.Rig.q;
            q.MainLoop = true;
            long before = r.Profiles.Snapshot().TxChainGeneration;
            using (QueuedWriteGate.Open(() => null))
            {
                q.Enqueue((FlexBase.FunctionDel)(() => r.Vendor.Radio.MicLevel = 33), "MicLevel");
                q.Enqueue((FlexBase.FunctionDel)(() => r.Vendor.Radio.CompanderOn = true), "CompanderOn");
            }
            while (q.Count > 0) { var item = q.Dequeue(); if (item.Item is FlexBase.FunctionDel f) f(); }
            Assert.Equal(33, r.Vendor.Radio.MicLevel);
            Assert.Equal(before, r.Profiles.Snapshot().TxChainGeneration);

            // The positive control: the same setter outside a gate is a
            // radio-origin notification to the feed and advances it.
            r.Vendor.Radio.MicLevel = 34;
            Assert.Equal(before + 1, r.Profiles.Snapshot().TxChainGeneration);
        }

        [Fact]
        public void ThePostImportApply_IsQueuedToTheGatedPath_NotRunUngated()
        {
            // The source pin for the seam (the behaviour needs a local
            // preset store, which the operator's Callouts supply in the app).
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var text = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "Radios", "FlexBase.StationConnect.cs"));
            int method = text.IndexOf("private ProfileActionOutcome RunLiveAudioActionChecked(", StringComparison.Ordinal);
            Assert.True(method > 0);
            string body = text.Substring(method, 5000);
            Assert.Contains("q.Enqueue((FunctionDel)ApplyDeferredGuestTransmitAudio, \"deferred guest transmit audio (post-import)\");", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ApplyLocalTransmitAudioNow(action.ProfileName) ? ProfileActionOutcome.Confirmed", body, StringComparison.Ordinal);
            Assert.Contains("if (confirmed) _autosaveTurnedOffThisSession = true;", body, StringComparison.Ordinal);
        }
    }
}
