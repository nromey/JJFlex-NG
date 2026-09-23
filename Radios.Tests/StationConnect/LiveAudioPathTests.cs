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

        // ── owner initialisation on a real rig: refused at the write sends nothing (Track G3, group 5) ──

        private static readonly MethodInfo OwnerInit =
            typeof(FlexBase).GetMethod("RunOwnerInitialization", BindingFlags.NonPublic | BindingFlags.Instance)!;

        [Fact]
        public void OwnerInitialisation_RefusedAtItsWrite_SendsNothing_PermittedItWritesTnfAndTheKeyer()
        {
            Assert.NotNull(OwnerInit);
            var refused = NewRig();
            var op1 = refused.Rig.StationAttempt.BeginOperation("connect");
            var r1 = (ProfileActionOutcome)OwnerInit.Invoke(refused.Rig, new object[] { op1, (Func<string>)(() => "another operator joined") })!;
            Assert.Equal(ProfileActionOutcome.Refused, r1);
            Assert.Empty(refused.Vendor.Transport.Commands);

            // The positive control: permitted, the whole set goes out
            // through the vendor transport, TNF and the keyer included.
            var permitted = NewRig();
            var op2 = permitted.Rig.StationAttempt.BeginOperation("connect");
            var r2 = (ProfileActionOutcome)OwnerInit.Invoke(permitted.Rig, new object[] { op2, (Func<string>)(() => null) })!;
            Assert.Equal(ProfileActionOutcome.Confirmed, r2);
            var commands = permitted.Vendor.Transport.Commands;
            Assert.Contains(commands, c => c.StartsWith("radio set tnf_enabled=", StringComparison.Ordinal));
            Assert.Contains(commands, c => c.StartsWith("interlock tx1_enabled=", StringComparison.Ordinal));
            // The keyer restore writes only when the operator has a saved
            // setup; this rig has no operator directory, so it is skipped
            // and traced rather than thrown.
            Assert.DoesNotContain(commands, c => c.Contains("break_in_delay", StringComparison.Ordinal));
        }

        // ── a non-owner alone on the radio, TNF off, on the same rig
        //    (refined 2026-09-22 21:37) ──

        private static readonly MethodInfo NonOwnerTnf =
            typeof(FlexBase).GetMethod("RunNonOwnerTnfEnable", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo VendorTnfField =
            typeof(Radio).GetField("_tnfEnabled", BindingFlags.NonPublic | BindingFlags.Instance)!;

        /// <summary>A non-owner alone on the radio, with the guest authority
        /// opened as only a bench may open it in production. The rig's serial
        /// is not declared ours, which is what makes it a non-owner.</summary>
        private RigOnVendorRadio NonOwnerAloneRig()
        {
            var r = NewRig();
            IsConnectedField.SetValue(r.Rig, true);
            r.Rig.WireStationHandlers(r.Vendor.Radio);
            r.Vendor.Radio.UpdateGuiClientsList(new List<GUIClient>
            {
                new GUIClient(RigOnVendorRadio.OurHandle, "our-client-id", "JJFlex", "K5NER", is_local_ptt: true),
            });
            return r;
        }

        [Fact]
        public void ANonOwnerAlone_WithTnfOff_SendsTnfEnabled_AndNothingElseOfTheOwnerInitialisation()
        {
            Assert.NotNull(NonOwnerTnf);
            Assert.NotNull(VendorTnfField);
            var saved = StationPolicies.Current;
            try
            {
                // In production the guest's shared-write authority is Unknown
                // until a bench establishes it, so this path does not run at
                // all; opening it here is the only way to exercise the ruled
                // behaviour, and it is said out loud so a green run is not
                // read as "a non-owner sets TNF today".
                StationPolicies.Current = new StationPolicies { GuestSharedWriteAuthority = RosterAuthorityByLiveMembershipPolicy.Instance };
                var r = NonOwnerAloneRig();
                var op = r.Rig.StationAttempt.BeginOperation("connect");

                var outcome = (ProfileActionOutcome)NonOwnerTnf.Invoke(r.Rig, new object[] { op, (Func<string>)(() => null) })!;

                Assert.Equal(ProfileActionOutcome.Confirmed, outcome);
                Assert.Contains(r.Vendor.Transport.Commands, c => c.StartsWith("radio set tnf_enabled=", StringComparison.Ordinal));
                // MicInput, VOX, CW break-in, TX1 and the keyer are the
                // owner's alone and are not in this path.
                Assert.DoesNotContain(r.Vendor.Transport.Commands, c => c.StartsWith("interlock tx1_enabled=", StringComparison.Ordinal));
            }
            finally { StationPolicies.Current = saved; }
        }

        [Fact]
        public void ANonOwnerAlone_WithTnfAlreadyOn_SendsNothing()
        {
            var saved = StationPolicies.Current;
            try
            {
                StationPolicies.Current = new StationPolicies { GuestSharedWriteAuthority = RosterAuthorityByLiveMembershipPolicy.Instance };
                var r = NonOwnerAloneRig();
                // The vendor's cache set directly: the radio says TNF is on,
                // with no command of ours behind it.
                VendorTnfField.SetValue(r.Vendor.Radio, true);
                var op = r.Rig.StationAttempt.BeginOperation("connect");

                var outcome = (ProfileActionOutcome)NonOwnerTnf.Invoke(r.Rig, new object[] { op, (Func<string>)(() => null) })!;

                Assert.Equal(ProfileActionOutcome.Refused, outcome);
                Assert.DoesNotContain(r.Vendor.Transport.Commands, c => c.StartsWith("radio set tnf_enabled=", StringComparison.Ordinal));
            }
            finally { StationPolicies.Current = saved; }
        }

        [Fact]
        public void ANonOwner_UnderTheProductionGuestAuthority_SendsNothing()
        {
            var r = NonOwnerAloneRig();
            var op = r.Rig.StationAttempt.BeginOperation("connect");

            var outcome = (ProfileActionOutcome)NonOwnerTnf.Invoke(r.Rig, new object[] { op, (Func<string>)(() => null) })!;

            Assert.Equal(ProfileActionOutcome.Refused, outcome);
            Assert.DoesNotContain(r.Vendor.Transport.Commands, c => c.StartsWith("radio set tnf_enabled=", StringComparison.Ordinal));
        }

        // ── abandoning THIS apply must not abandon an earlier one's put-back ──
        //
        // AbandonUnappliedLiveAudio is called from three places — the deferred
        // refusal, an enqueue failure and an all-refused continuation — and
        // until Track G5 each of them deleted the snapshot, every live record
        // and the autosave obligation, whatever else was outstanding. Track G3
        // repaired the sibling AbortLiveAudio and left this one. Astra's
        // review: "A refused later operation must not erase an earlier
        // operation's put-back or turn autosave on over its live settings."

        private static readonly MethodInfo Abandon =
            typeof(FlexBase).GetMethod("AbandonUnappliedLiveAudio", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo SessionRecord =
            typeof(FlexBase).GetField("_profileSessionRecord", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo SnapshotField =
            typeof(FlexBase).GetField("_liveTxSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static List<ProfileSessionRecord> RecordsOf(FlexBase rig) =>
            (List<ProfileSessionRecord>)SessionRecord.GetValue(rig)!;

        [Fact]
        public void AbandoningAnUnappliedApply_KeepsAnEarlierOperationsPutBack_AndItsSnapshot()
        {
            Assert.True(Abandon != null && SessionRecord != null, "the abandon path is not where this test reaches it");
            var r = NewRig();
            // An earlier operation on this connection applied ours and owes a
            // put-back; the snapshot is the owner's own chain, which is the
            // only thing that put-back can read from.
            RecordsOf(r.Rig).Add(new ProfileSessionRecord
            {
                ProfileType = ProfileTypes.tx, LiveTransmitAudio = true, WeLoaded = "Contest",
            });
            SnapshotField.SetValue(r.Rig, new AudioChainPreset("live transmit audio"));
            AutosaveOffThisSession.SetValue(r.Rig, true);

            Abandon.Invoke(r.Rig, null);

            Assert.Contains(RecordsOf(r.Rig), rec => rec.LiveTransmitAudio);
            Assert.True(SnapshotHeld(r.Rig), "the snapshot the earlier put-back reads from must survive");
            Assert.True((bool)AutosaveOffThisSession.GetValue(r.Rig)!,
                "autosave must stay off while our chain is live on the radio: turning it on commits our settings into the owner's profile");
        }

        [Fact]
        public void AbandoningAnUnappliedApply_WithNothingElseOutstanding_DiscardsTheSnapshot()
        {
            // The positive control, and the behaviour that was always right:
            // an apply that never happened owes nothing.
            var r = NewRig();
            SnapshotField.SetValue(r.Rig, new AudioChainPreset("live transmit audio"));

            Abandon.Invoke(r.Rig, null);

            Assert.DoesNotContain(RecordsOf(r.Rig), rec => rec.LiveTransmitAudio);
            Assert.False(SnapshotHeld(r.Rig), "with nothing owed, the snapshot is discarded as it always was");
        }

        [Fact]
        public void AbandoningIsScopedAtEveryEntry_NotOnlyTheFirst()
        {
            // All three callers go through the one helper, so the scoping
            // cannot be true of one entry and false of another. Pinned in the
            // source because the defect was precisely a sibling left behind.
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string text = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "Radios", "FlexBase.cs"));
            int helper = text.IndexOf("private void AbandonUnappliedLiveAudio()", StringComparison.Ordinal);
            Assert.True(helper > 0);
            string body = text.Substring(helper, 1600);
            Assert.Contains("LiveAudioAbortPlan.Decide(", body, StringComparison.Ordinal);
            Assert.Contains("priorRecord", body, StringComparison.Ordinal);
            // Three calls, and no site that reaches past the helper to delete
            // records or the snapshot itself.
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(text, @"AbandonUnappliedLiveAudio\(\);").Count);
        }

        // ── the preset's receipt reaches the operator ──

        [Fact]
        public void TheApplyCarriesThePresetsReceipt_InsteadOfDroppingIt()
        {
            // AudioChainPreset.ApplyTo returns a spoken-ready note about
            // anything it could NOT apply faithfully — a TX equalizer the
            // radio has not reported, or a preset tuned for a different
            // microphone input — and its own summary says callers append it to
            // their announcement. This path produced that note and threw it
            // away, so a half-applied chain was announced as applied.
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string text = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "Radios", "FlexBase.cs"));

            int apply = text.IndexOf("private bool ApplyLocalTransmitAudioPayloadNow(AudioChainPreset payload, string presetName, out string receipt)", StringComparison.Ordinal);
            Assert.True(apply > 0, "the apply must hand its receipt back");
            Assert.Contains("receipt = payload.ApplyTo(this) ?? \"\";", text.Substring(apply, 900), StringComparison.Ordinal);

            int deferred = text.IndexOf("internal void ApplyDeferredGuestTransmitAudio()", StringComparison.Ordinal);
            string body = text.Substring(deferred, text.IndexOf("private void AbandonUnappliedLiveAudio()", StringComparison.Ordinal) - deferred);
            Assert.Contains("out receipt", body, StringComparison.Ordinal);
            // Spoken, not merely traced: the announcement itself carries it.
            Assert.Contains("(\"preset\", pending)) + (receipt.Length == 0 ? \"\" : \" \" + receipt)", body, StringComparison.Ordinal);
        }

        [Fact]
        public void ThePresetsReceiptIsEmptyWhenNothingWasLeftOut_SoNothingIsAppendedForNoReason()
        {
            // The other half: a complete apply says nothing extra. Read
            // assembled — "Your transmit audio is set to Contest." with a
            // trailing space and nothing after it is a defect an operator
            // hears as a pause.
            var preset = new AudioChainPreset("Contest");
            Assert.Equal("", preset.ApplyTo(NewRig().Rig, applyEq: false));
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
