using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// A radio's transmit and microphone profile state behind the
    /// orchestrator's narrow port. The test changes it in response to a send,
    /// which is how "applying the transmit profile changes the microphone
    /// selection" is exercised without a radio. Every command and every read
    /// is recorded in ORDER, so sequencing is an assertion, not a source pin.
    /// </summary>
    internal sealed class FakePostStationPort : IPostStationPort
    {
        public sealed class TypeState
        {
            public List<string> Names = new List<string>();
            /// <summary>null = unreadable; "" = the radio reported none loaded.</summary>
            public string Selection = "";
            public bool Reported = true;
            public bool Unsaved;
        }

        public readonly FakeStationClock Clock;
        public readonly ProfileEvidenceLog Evidence;
        public readonly int Gen;

        public StationPolicyFacts Facts = new StationPolicyFacts
        {
            Connected = true, Ownership = RadioOwnership.Mine, Intent = ProfileGuestIntent.LoadMineAndPutBack,
            WantedGlobal = "K5NER", WantedTx = "K5NER-TX", WantedMic = "K5NER-MIC", Serial = "1234-5678-9012-3456",
        };
        public RosterVerdict Roster = RosterVerdict.OnlyUs;
        /// <summary>The GUEST'S authority verdict. Unknown by default, as
        /// production's StationPolicies.GuestSharedWriteAuthority is; a test
        /// of the guest route sets OnlyUs as its positive control.</summary>
        public RosterVerdict GuestRoster = RosterVerdict.Unknown;
        public bool? RadioAutosave = false;
        public string LocalTxProfile = "";
        public bool LocalTxProfileExists;

        public readonly Dictionary<ProfileTypes, TypeState> State = new Dictionary<ProfileTypes, TypeState>
        {
            { ProfileTypes.tx, new TypeState { Names = { "Default", "K5NER-TX" }, Selection = "Default" } },
            { ProfileTypes.mic, new TypeState { Names = { "Default", "K5NER-MIC" }, Selection = "Default" } },
        };
        public readonly Dictionary<ProfileTypes, string> Wanted = new Dictionary<ProfileTypes, string>
        {
            { ProfileTypes.global, "K5NER" }, { ProfileTypes.tx, "K5NER-TX" }, { ProfileTypes.mic, "K5NER-MIC" },
        };

        /// <summary>Everything, in order: "read tx", "send tx K5NER-TX", "owner-init", "live TurnAutosaveOff", "conclude repair=True".</summary>
        public readonly List<string> Log = new List<string>();
        public readonly List<(ProfileTypes type, string name)> Sent = new List<(ProfileTypes, string)>();
        public readonly List<ProfileActionKind> LiveAudioRun = new List<ProfileActionKind>();
        public readonly List<ProfileSessionRecord> Recorded = new List<ProfileSessionRecord>();
        public PostStationResult Concluded;

        /// <summary>Called when a selection is actually sent. The default
        /// makes the radio report that selection at once; a test replaces it
        /// to withhold the report or to change the other type's state.</summary>
        public Action<ProfileTypes, string> OnSelectionSent;

        /// <summary>Outcome for each live-audio kind; Confirmed by default.</summary>
        public readonly Dictionary<ProfileActionKind, ProfileActionOutcome> LiveAudioOutcomes = new Dictionary<ProfileActionKind, ProfileActionOutcome>();

        public bool HoldDispatch;
        public readonly List<Action> Held = new List<Action>();

        public FakePostStationPort(FakeStationClock clock)
        {
            Clock = clock;
            Evidence = new ProfileEvidenceLog(clock);
            Gen = 42;
            Evidence.Reset(Gen);
            OnSelectionSent = (type, name) => RadioReportsSelection(type, name);
        }

        public void RadioReportsSelection(ProfileTypes type, string name)
        {
            State[type].Selection = name;
            Evidence.SelectionObserved(type, name, ObservationProvenance.RadioReported, Gen);
        }

        public int ReadsOf(ProfileTypes type) => Log.Count(l => l == "read " + type);
        public int IndexOfFirst(string entry) => Log.IndexOf(entry);

        public StationPolicyFacts ReadPolicyFacts() => new StationPolicyFacts
        {
            Connected = Facts.Connected, HoldArmed = Facts.HoldArmed, Ownership = Facts.Ownership,
            Intent = Facts.Intent, WantedGlobal = Facts.WantedGlobal, Serial = Facts.Serial,
            WantedTx = Facts.WantedTx, WantedMic = Facts.WantedMic,
            UnsavedTx = Facts.UnsavedTx, UnsavedMic = Facts.UnsavedMic,
        };

        public RosterJudgement RosterForAutomaticWrite() => new RosterJudgement(Roster, "fake owner roster", 1);
        public RosterJudgement RosterForGuestSharedWrite() => new RosterJudgement(GuestRoster, "fake guest roster", 1);

        public ProfileSituation ReadBaseSituation() => new ProfileSituation
        {
            Connected = Facts.Connected, ChangeNothingArmed = Facts.HoldArmed, Ownership = Facts.Ownership,
            Intent = Facts.Intent, RadioAutosave = RadioAutosave,
            LocalTransmitAudioProfile = LocalTxProfile, LocalTransmitAudioProfileExists = LocalTxProfileExists,
        };

        public ProfileTypeState ReadType(ProfileTypes type, int timeoutMs)
        {
            Log.Add("read " + type);
            var st = State[type];
            return new ProfileTypeState
            {
                ProfileType = type, Reported = st.Reported, Names = st.Names.ToList(),
                Selection = st.Selection, UnsavedChanges = st.Unsaved,
                Wanted = Wanted.TryGetValue(type, out var w) ? w : "",
            };
        }

        /// <summary>Whether the radio replies to the load command (code 0).
        /// The reply is the radio's acceptance of the command; the status
        /// report that follows a CHANGED selection is delivered separately
        /// by OnSelectionSent, and an unchanged one is never reported at all
        /// (the vendor's equal-value skip).</summary>
        public bool RadioAcknowledgesSelections = true;
        /// <summary>When set, the radio rejects the next load with this text.</summary>
        public string RejectNextSelectionWith;
        public readonly List<CommandReply> RepliesDelivered = new List<CommandReply>();

        public ProfileActionOutcome SendSelection(ProfileAction action, Func<string> refusalAtSend, Action<CommandReply> onReply)
        {
            ProfileActionOutcome outcome = ProfileActionOutcome.Queued;
            Action work = () =>
            {
                string refusal = refusalAtSend();
                if (refusal != null) { Log.Add("refused " + action.ProfileType + " " + refusal); outcome = ProfileActionOutcome.Refused; return; }
                Log.Add("send " + action.ProfileType + " " + action.ProfileName);
                Sent.Add((action.ProfileType, action.ProfileName));
                outcome = ProfileActionOutcome.Sent;
                string cmd = "profile " + action.ProfileType + " load \"" + action.ProfileName + "\"";
                CommandReply reply = null;
                if (RejectNextSelectionWith != null)
                {
                    reply = new CommandReply(cmd, 0x50000001, RejectNextSelectionWith);
                    RejectNextSelectionWith = null;
                }
                else if (RadioAcknowledgesSelections) reply = CommandReply.Ok(cmd);
                if (reply != null) { RepliesDelivered.Add(reply); onReply?.Invoke(reply); }
                OnSelectionSent?.Invoke(action.ProfileType, action.ProfileName);
            };
            if (HoldDispatch)
            {
                // The production port waits, bounded, for a queued delegate
                // to run; this models that wait through the scripted waiter
                // so a test can change the world before releasing it.
                Held.Add(work);
                int spins = 0;
                while (Held.Contains(work) && spins++ < 200) WhileHeld?.Invoke();
                return Held.Contains(work) ? ProfileActionOutcome.Queued : outcome;
            }
            work();
            return outcome;
        }

        /// <summary>Runs while a held delegate has not been released; a test
        /// wires it to the scripted waiter.</summary>
        public Action WhileHeld;

        public void ReleaseHeld()
        {
            HoldDispatch = false;
            var items = Held.ToList();
            Held.Clear();
            foreach (var w in items) w();
        }

        public SelectionObservation LatestReportedSelection(ProfileTypes type) => Evidence.Snapshot().ReportedSelectionOf(type);
        public long ProfileSequence => Evidence.Sequence;

        /// <summary>Runs before the owner initialisation's recheck.</summary>
        public Action BeforeOwnerInit;

        public ProfileActionOutcome RunOwnerInitialization(Func<string> refusalAtWrite)
        {
            BeforeOwnerInit?.Invoke();
            string refusal = refusalAtWrite();
            if (refusal != null) { Log.Add("owner-init refused " + refusal); return ProfileActionOutcome.Refused; }
            Log.Add("owner-init");
            return ProfileActionOutcome.Confirmed;
        }

        /// <summary>What the radio reports TNF to be, for the non-owner path.</summary>
        public bool TnfEnabled;

        /// <summary>Mirrors the production port: the orchestrator's recheck
        /// first, then the real gate against the facts, the GUEST'S roster
        /// authority and the radio's TNF state, all at the write.</summary>
        public ProfileActionOutcome RunNonOwnerTnfEnable(Func<string> refusalAtWrite)
        {
            string refusal = refusalAtWrite();
            if (refusal == null)
                refusal = NonOwnerTnfGate.Refusal(ReadPolicyFacts(), RosterForGuestSharedWrite(), TnfEnabled);
            if (refusal != null) { Log.Add("non-owner-tnf refused " + refusal); return ProfileActionOutcome.Refused; }
            TnfEnabled = true;
            Log.Add("non-owner-tnf");
            return ProfileActionOutcome.Confirmed;
        }

        /// <summary>Runs before a live-audio step's recheck, so a test can
        /// change the world between the plan and the step's write.</summary>
        public Action<ProfileActionKind> BeforeLiveAudioStep;

        public ProfileActionOutcome RunLiveAudioAction(ProfileAction action, Func<string> refusalAtSend)
        {
            BeforeLiveAudioStep?.Invoke(action.Kind);
            string refusal = refusalAtSend();
            if (refusal != null)
            {
                Log.Add("live " + action.Kind + " refused " + refusal);
                return ProfileActionOutcome.Refused;
            }
            Log.Add("live " + action.Kind);
            LiveAudioRun.Add(action.Kind);
            return LiveAudioOutcomes.TryGetValue(action.Kind, out var o) ? o : ProfileActionOutcome.Confirmed;
        }

        public void AbortLiveAudio(bool autosaveWasTurnedOff) => Log.Add("abort-live-audio autosave=" + autosaveWasTurnedOff);
        public void RecordSession(IEnumerable<ProfileSessionRecord> records) => Recorded.AddRange(records);
        public void Conclude(PostStationResult result) { Concluded = result; Log.Add("conclude repair=" + result.MayRepairMicrophone); }
        public void Trace(string line, bool warn) { }
    }

    /// <summary>
    /// The review's stronger third mutation test: the production post-station
    /// ORDERING, driven through a fake port with the fake clock. Group 5 of
    /// the design's tests. The named mutation — read both types up front and
    /// plan once — must fail the two dependency tests.
    /// </summary>
    public sealed class PostStationOrchestratorTests
    {
        private static StationResult Established(int slices = 2) => new StationResult
        {
            Outcome = StationOutcome.RestoredConfirmed, Route = GlobalRoute.LoadExisting, OwnSlicesAtEnd = slices,
        };

        private static StationResult Unconfirmed() => new StationResult
        {
            Outcome = StationOutcome.Unconfirmed, Route = GlobalRoute.LoadExisting, LoadSent = true, LoadOutstanding = true, OwnSlicesAtEnd = 1,
        };

        private sealed class Rig
        {
            public readonly FakeStationClock Clock = new FakeStationClock();
            public readonly FakePostStationPort Port;
            public readonly ScriptedWaiter Waiter;
            public readonly ConnectionAttempt Attempt = new ConnectionAttempt("1234-5678-9012-3456");
            public readonly StationOperation Operation;
            public Rig()
            {
                Port = new FakePostStationPort(Clock);
                Waiter = new ScriptedWaiter(Clock);
                Port.WhileHeld = () => Waiter.Wait(25);
                Operation = Attempt.BeginOperation("connect");
            }
            public PostStationResult Run(StationResult station) =>
                new PostStationOrchestrator(Port, Clock, Waiter, StationDeadlines.Default()).Run(station, Operation);
        }

        // ── the positive control ──

        [Fact]
        public void BothTypesNeedLoading_EachIsSentAndConfirmedInTurn_OwnerInitFirst_AssessmentLast()
        {
            var r = new Rig();

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Completed, result.Outcome);
            Assert.Equal(new[] { (ProfileTypes.tx, "K5NER-TX"), (ProfileTypes.mic, "K5NER-MIC") }, r.Port.Sent);
            Assert.Equal(new[] { ProfileTypes.tx, ProfileTypes.mic }, result.ConfirmedSelections);
            Assert.True(result.OwnerInitialisationRan);
            Assert.True(result.MayRepairMicrophone);
            // Order: owner-init, read tx, send tx, read mic, send mic, conclude.
            Assert.Equal(new[] { "owner-init", "read tx", "send tx K5NER-TX", "read mic", "send mic K5NER-MIC", "conclude repair=True" }, r.Port.Log);
            Assert.Empty(r.Port.Recorded);                     // the owner's radio: nothing to put back
        }

        // ── the dependency: microphone is decided AFTER the confirmed transmit effect ──

        [Fact]
        public void TransmitApplicationChangesTheMicrophone_TheMicrophoneIsDecidedFromAFreshReadAfterThatEffect()
        {
            // Before the phase the microphone already matches. Loading the
            // transmit profile carries a microphone with it, and the radio
            // reports the microphone changed. A plan made from a pre-read
            // would say "already loaded" and leave the wrong microphone.
            var r = new Rig();
            r.Port.State[ProfileTypes.mic].Selection = "K5NER-MIC";
            r.Port.OnSelectionSent = (type, name) =>
            {
                r.Port.RadioReportsSelection(type, name);
                if (type == ProfileTypes.tx) r.Port.RadioReportsSelection(ProfileTypes.mic, "Default");
            };

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Completed, result.Outcome);
            Assert.Equal(new[] { (ProfileTypes.tx, "K5NER-TX"), (ProfileTypes.mic, "K5NER-MIC") }, r.Port.Sent);
            Assert.True(r.Port.IndexOfFirst("read mic") > r.Port.IndexOfFirst("send tx K5NER-TX"),
                "the microphone must be read after the transmit effect, not before it");
        }

        [Fact]
        public void TransmitApplicationSetsTheMicrophoneRight_NoObsoleteMicrophoneWrite()
        {
            // The inverse: both need loading by a pre-read, but the transmit
            // load's effect leaves the microphone already right. Exactly one
            // load.
            var r = new Rig();
            r.Port.OnSelectionSent = (type, name) =>
            {
                r.Port.RadioReportsSelection(type, name);
                if (type == ProfileTypes.tx) r.Port.RadioReportsSelection(ProfileTypes.mic, "K5NER-MIC");
            };

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Completed, result.Outcome);
            Assert.Equal(new[] { (ProfileTypes.tx, "K5NER-TX") }, r.Port.Sent);
            Assert.Equal(1, r.Port.ReadsOf(ProfileTypes.mic));
            Assert.True(result.Plan.Skipped(ProfileTypes.mic, ProfileSkipReason.AlreadyLoaded));
        }

        // ── stop rules ──

        [Fact]
        public void AnUnconfirmedTransmitSelection_StopsBeforeTheMicrophone_AndWithholdsTheRepair()
        {
            var r = new Rig();
            r.Port.OnSelectionSent = (type, name) => { }; // the radio never reports it
            long before = r.Clock.NowMs;

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Unconfirmed, result.Outcome);
            Assert.Equal(new[] { (ProfileTypes.tx, "K5NER-TX") }, r.Port.Sent);
            Assert.Equal(0, r.Port.ReadsOf(ProfileTypes.mic));
            Assert.Empty(result.ConfirmedSelections);
            Assert.False(result.MayRepairMicrophone);
            Assert.True(r.Clock.NowMs - before >= StationDeadlines.Default().TxMicEffectMs);
            Assert.True(r.Clock.NowMs - before < StationDeadlines.Default().PostStationPhaseMs);
            Assert.Equal("conclude repair=False", r.Port.Log.Last());
        }

        // ── the confirmation is the reply AND the report (Track G3, group 1) ──

        [Fact]
        public void AnAcknowledgedLoad_TheRadioNeverReportsSelected_IsUnconfirmed()
        {
            var r = new Rig();
            r.Port.OnSelectionSent = (type, name) => { };       // acknowledged (the fake's default), never reported

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Unconfirmed, result.Outcome);
            Assert.Single(r.Port.RepliesDelivered);
            Assert.Contains("acknowledged the load but did not report it selected", result.Reason);
        }

        [Fact]
        public void AReportedSelection_TheRadioNeverAcknowledges_IsUnconfirmed()
        {
            var r = new Rig();
            r.Port.RadioAcknowledgesSelections = false;        // the status arrives; the reply never does

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Unconfirmed, result.Outcome);
            Assert.Empty(result.ConfirmedSelections);
            Assert.Contains("did not acknowledge", result.Reason);
        }

        [Fact]
        public void ARejectedLoad_StopsThePhase_WithTheRadiosText()
        {
            var r = new Rig();
            r.Port.RejectNextSelectionWith = "profile not found";
            r.Port.OnSelectionSent = (type, name) => { };

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Stopped, result.Outcome);
            Assert.Contains("rejected by the radio", result.Reason);
            Assert.Contains("profile not found", result.Reason);
            Assert.Equal(0, r.Port.ReadsOf(ProfileTypes.mic));
            Assert.False(result.MayRepairMicrophone);
        }

        [Fact]
        public void ALocalEchoOfOurOwnSetter_DoesNotConfirmTheSelection()
        {
            var r = new Rig();
            r.Port.OnSelectionSent = (type, name) =>
                r.Port.Evidence.SelectionObserved(type, name, ObservationProvenance.LocalEcho, r.Port.Gen);

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Unconfirmed, result.Outcome);
            Assert.Empty(result.ConfirmedSelections);
        }

        [Fact]
        public void ARefusedTransmitSelection_StopsBeforeTheMicrophone()
        {
            var r = new Rig();
            r.Port.HoldDispatch = true;
            r.Waiter.Then(() => { r.Port.Facts.HoldArmed = true; r.Port.ReleaseHeld(); });

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Stopped, result.Outcome);
            Assert.Empty(r.Port.Sent);
            Assert.Equal(0, r.Port.ReadsOf(ProfileTypes.mic));
            Assert.False(result.MayRepairMicrophone);
        }

        [Fact]
        public void AHeldSelection_ReleasedAfterAnotherClientJoins_IsRefused()
        {
            var r = new Rig();
            r.Port.HoldDispatch = true;
            r.Waiter.Then(() => { r.Port.Roster = RosterVerdict.OthersPresent; r.Port.ReleaseHeld(); });

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Stopped, result.Outcome);
            Assert.Empty(r.Port.Sent);
            Assert.Contains(r.Port.Log, l => l.StartsWith("refused tx", StringComparison.Ordinal));
        }

        [Fact]
        public void AHeldSelection_ReleasedAfterTheWantedNameForThatTypeChanged_IsRefused()
        {
            // The per-type wanted name is part of the send-time set (Track
            // G3, group 2): the plan said K5NER-TX; by dispatch the operator
            // chose another. Neither the old nor the new is sent.
            var r = new Rig();
            r.Port.HoldDispatch = true;
            r.Waiter.Then(() => { r.Port.Facts.WantedTx = "Ragchew-TX"; r.Port.ReleaseHeld(); });

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Stopped, result.Outcome);
            Assert.Empty(r.Port.Sent);
            Assert.Contains(r.Port.Log, l => l.StartsWith("refused tx", StringComparison.Ordinal) && l.Contains("wanted transmit profile changed"));
        }

        [Fact]
        public void AHeldSelection_ReleasedAfterTheRadioReportsUnsavedWork_IsRefused()
        {
            var r = new Rig();
            r.Port.HoldDispatch = true;
            r.Waiter.Then(() => { r.Port.Facts.UnsavedTx = true; r.Port.ReleaseHeld(); });

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Stopped, result.Outcome);
            Assert.Empty(r.Port.Sent);
            Assert.Contains(r.Port.Log, l => l.StartsWith("refused tx", StringComparison.Ordinal) && l.Contains("unsaved"));
        }

        [Fact]
        public void AHeldSelection_ReleasedUnchanged_IsSent()
        {
            // The positive control for the two held cases.
            var r = new Rig();
            r.Port.HoldDispatch = true;
            r.Waiter.Then(() => r.Port.ReleaseHeld());

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Completed, result.Outcome);
            Assert.Equal(2, r.Port.Sent.Count);
        }

        [Theory]
        [InlineData(StationOutcome.Unconfirmed)]
        [InlineData(StationOutcome.Failed)]
        [InlineData(StationOutcome.Cancelled)]
        public void AnUncertainStation_RunsNoDependentCommand_NoOwnerInit_NoCapture_NoRepair(StationOutcome outcome)
        {
            var r = new Rig();
            r.Port.Facts.Intent = ProfileGuestIntent.UseMyTransmitAudio;
            r.Port.LocalTxProfile = "Contest"; r.Port.LocalTxProfileExists = true;
            var station = Unconfirmed(); station.Outcome = outcome;

            var result = r.Run(station);

            Assert.Equal(PostStationOutcome.NotEstablished, result.Outcome);
            Assert.Empty(r.Port.Sent);
            Assert.Empty(r.Port.LiveAudioRun);
            Assert.DoesNotContain("owner-init", r.Port.Log);
            Assert.False(result.MayRepairMicrophone);
            Assert.Equal("conclude repair=False", r.Port.Log.Last());
        }

        // ── reported-empty is not unreported ──

        [Fact]
        public void AReportedEmptyMicrophoneSelection_IsLoaded_AnUnreadableOneIsNot()
        {
            var reported = new Rig();
            reported.Port.State[ProfileTypes.mic].Selection = "";
            var a = reported.Run(Established());
            Assert.Contains((ProfileTypes.mic, "K5NER-MIC"), reported.Port.Sent);
            Assert.Equal(PostStationOutcome.Completed, a.Outcome);

            var unreadable = new Rig();
            unreadable.Port.State[ProfileTypes.mic].Selection = null;
            var b = unreadable.Run(Established());
            Assert.DoesNotContain(unreadable.Port.Sent, s => s.type == ProfileTypes.mic);
            Assert.True(b.Plan.Skipped(ProfileTypes.mic, ProfileSkipReason.SelectionUnreadable));
        }

        // ── the owner initialisation gate ──

        [Theory]
        [InlineData("guest", RadioOwnership.SomeoneElses, ProfileGuestIntent.LoadMineAndPutBack, false, RosterVerdict.OnlyUs)]
        [InlineData("company", RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack, false, RosterVerdict.OthersPresent)]
        [InlineData("roster unknown", RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack, false, RosterVerdict.Unknown)]
        [InlineData("hold", RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack, true, RosterVerdict.OnlyUs)]
        [InlineData("not answered", RadioOwnership.Mine, ProfileGuestIntent.NotAnswered, false, RosterVerdict.OnlyUs)]
        public void OwnerInitialisation_RunsOnlyUnderTheFullGate(
            string what, RadioOwnership ownership, ProfileGuestIntent intent, bool hold, RosterVerdict roster)
        {
            var r = new Rig();
            r.Port.Facts.Ownership = ownership;
            r.Port.Facts.Intent = intent;
            r.Port.Facts.HoldArmed = hold;
            r.Port.Roster = roster;
            var station = Established();
            station.Outcome = StationOutcome.PolicySkipped;
            station.Allocation.Stop = AllocationStop.TargetReached;
            station.Allocation.OwnSlicesAtEnd = 2;

            var result = r.Run(station);

            Assert.False(result.OwnerInitialisationRan, what);
            Assert.DoesNotContain("owner-init", r.Port.Log);
            Assert.Empty(r.Port.Sent);
        }

        // ── the non-owner's TNF, refined 2026-09-22 21:37 ──

        private static Rig NonOwnerAlone()
        {
            var r = new Rig();
            r.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            r.Port.GuestRoster = RosterVerdict.OnlyUs;      // a bench has opened the guest authority
            return r;
        }

        private static StationResult SkippedStation()
        {
            var station = Established();
            station.Outcome = StationOutcome.PolicySkipped;
            station.Allocation.Stop = AllocationStop.TargetReached;
            station.Allocation.OwnSlicesAtEnd = 2;
            return station;
        }

        [Fact]
        public void ANonOwnerAlone_WithTnfOff_TurnsItOn_AndNothingElseOfTheInitialisationRuns()
        {
            var r = NonOwnerAlone();
            r.Port.TnfEnabled = false;

            var result = r.Run(SkippedStation());

            Assert.True(result.NonOwnerTnfEnabled);
            Assert.Contains("non-owner-tnf", r.Port.Log);
            Assert.True(r.Port.TnfEnabled);
            // The owner initialisation is not theirs: no MicInput, no VOX, no
            // TX1, no keyer, and no profile selection either.
            Assert.False(result.OwnerInitialisationRan);
            Assert.DoesNotContain("owner-init", r.Port.Log);
            Assert.Empty(r.Port.Sent);
        }

        [Fact]
        public void ANonOwnerAlone_WithTnfAlreadyOn_LeavesIt()
        {
            var r = NonOwnerAlone();
            r.Port.TnfEnabled = true;

            var result = r.Run(SkippedStation());

            Assert.False(result.NonOwnerTnfEnabled);
            Assert.Contains(r.Port.Log, l => l.StartsWith("non-owner-tnf refused", StringComparison.Ordinal));
        }

        [Fact]
        public void ANonOwnerWithTheOwnerConnected_LeavesTnfAlone()
        {
            var r = NonOwnerAlone();
            r.Port.GuestRoster = RosterVerdict.OthersPresent;
            r.Port.TnfEnabled = false;

            var result = r.Run(SkippedStation());

            Assert.False(result.NonOwnerTnfEnabled);
            Assert.False(r.Port.TnfEnabled);
        }

        [Fact]
        public void UnderTheProductionGuestAuthority_TheNonOwnerTnfPathDoesNotRun()
        {
            // GuestRoster is Unknown by default in the fake, as it is in
            // production: built to the ruling, closed until a bench opens it.
            var r = new Rig();
            r.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            r.Port.TnfEnabled = false;

            var result = r.Run(SkippedStation());

            Assert.False(result.NonOwnerTnfEnabled);
            Assert.False(r.Port.TnfEnabled);
        }

        [Fact]
        public void TheOwnersConnect_SetsTnfThroughTheOwnerInitialisation_NotTheNonOwnerPath()
        {
            var r = new Rig();                                  // Mine, OnlyUs, opted in

            var result = r.Run(Established());

            Assert.True(result.OwnerInitialisationRan);
            Assert.False(result.NonOwnerTnfEnabled);
            Assert.DoesNotContain(r.Port.Log, l => l.StartsWith("non-owner-tnf", StringComparison.Ordinal));
        }

        // ── the live transmit-audio path, under the STRICT roster test (part three) ──

        [Fact]
        public void TheSameOneHandleRoster_LetsTheOwnerLoad_AndKeepsTheGuestRouteClosed()
        {
            // Track G2 re-review, section 4: the owner's ruled live-membership
            // authority must not become the guest's. One snapshot, our handle
            // alone and identity-bearing: Mine may send; UseMyTransmitAudio
            // may not, because the GUEST'S authority is still Unknown.
            var owner = new Rig();                                   // Roster OnlyUs, GuestRoster Unknown (the defaults)
            var ownerResult = owner.Run(Established());
            Assert.Equal(PostStationOutcome.Completed, ownerResult.Outcome);
            Assert.True(ownerResult.OwnerInitialisationRan);
            Assert.Equal(2, owner.Port.Sent.Count);

            var guest = new Rig();
            guest.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            guest.Port.Facts.Intent = ProfileGuestIntent.UseMyTransmitAudio;
            guest.Port.LocalTxProfile = "Contest"; guest.Port.LocalTxProfileExists = true;
            guest.Port.RadioAutosave = true;
            var station = Established(); station.Outcome = StationOutcome.PolicySkipped;
            station.Allocation.Stop = AllocationStop.TargetReached; station.Allocation.OwnSlicesAtEnd = 2;

            var guestResult = guest.Run(station);

            Assert.Equal(RosterVerdict.OnlyUs, guest.Port.RosterForAutomaticWrite().Verdict);   // the same snapshot says only us
            Assert.Empty(guest.Port.LiveAudioRun);                                               // and the guest route stays closed
            Assert.Empty(guest.Port.Sent);
            Assert.False(guestResult.LiveAudioDeferred);
            Assert.False(guestResult.LiveAudioApplied);
            // Closed at the ROUTE, not merely refused step by step: no live
            // step was even attempted, and nothing aborted.
            Assert.False(guestResult.LiveAudioAborted);
            Assert.DoesNotContain(guest.Port.Log, l => l.StartsWith("live ", StringComparison.Ordinal));
        }

        [Fact]
        public void TheProductionDefaults_KeepTheGuestSharedWriteAuthorityUnknown()
        {
            var defaults = StationPolicies.Defaults();
            Assert.Same(RosterAuthorityUnknownPolicy.Instance, defaults.GuestSharedWriteAuthority);
            Assert.Same(RosterAuthorityByLiveMembershipPolicy.Instance, defaults.RosterAuthority);
            Assert.Contains("guest shared write", defaults.Describe());
        }

        [Fact]
        public void OwnerInitialisation_RefusedAtItsWrite_StopsThePhase_BeforeAnySelection()
        {
            var r = new Rig();
            r.Port.BeforeOwnerInit = () => r.Port.Roster = RosterVerdict.OthersPresent;

            var result = r.Run(Established());

            Assert.Equal(PostStationOutcome.Stopped, result.Outcome);
            Assert.False(result.OwnerInitialisationRan);
            Assert.Empty(r.Port.Sent);
            Assert.Contains(r.Port.Log, l => l.StartsWith("owner-init refused", StringComparison.Ordinal));
            Assert.False(result.MayRepairMicrophone);
        }

        [Fact]
        public void TheLiveAudioRoute_RunsUnderTheStrictRoster_AutosaveThenCaptureThenApply()
        {
            var r = new Rig();
            r.Port.GuestRoster = RosterVerdict.OnlyUs;                 // the positive control's authority, not production's
            r.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            r.Port.Facts.Intent = ProfileGuestIntent.UseMyTransmitAudio;
            r.Port.LocalTxProfile = "Contest"; r.Port.LocalTxProfileExists = true;
            r.Port.RadioAutosave = true;
            r.Port.LiveAudioOutcomes[ProfileActionKind.ApplyLocalTransmitAudio] = ProfileActionOutcome.Deferred;
            var station = Established(); station.Outcome = StationOutcome.PolicySkipped;
            station.Allocation.Stop = AllocationStop.TargetReached; station.Allocation.OwnSlicesAtEnd = 2;

            var result = r.Run(station);

            Assert.Equal(new[] { ProfileActionKind.TurnAutosaveOff, ProfileActionKind.CaptureLiveTransmitAudio, ProfileActionKind.ApplyLocalTransmitAudio },
                r.Port.LiveAudioRun);
            Assert.True(result.LiveAudioDeferred);
            Assert.Empty(r.Port.Sent);                        // a guest sends no named selection
            Assert.Empty(r.Port.Recorded);                    // deferred: the record is made when it really runs
            Assert.False(result.MayRepairMicrophone);
        }

        [Theory]
        [InlineData(RosterVerdict.OthersPresent)]
        [InlineData(RosterVerdict.Unknown)]
        public void TheLiveAudioRoute_IsClosedUnlessTheStrictRosterSaysOnlyUs(RosterVerdict roster)
        {
            var r = new Rig();
            r.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            r.Port.Facts.Intent = ProfileGuestIntent.UseMyTransmitAudio;
            r.Port.LocalTxProfile = "Contest"; r.Port.LocalTxProfileExists = true;
            r.Port.RadioAutosave = true;
            r.Port.GuestRoster = roster;
            var station = Established(); station.Outcome = StationOutcome.PolicySkipped;
            station.Allocation.Stop = AllocationStop.TargetReached; station.Allocation.OwnSlicesAtEnd = 2;

            var result = r.Run(station);

            Assert.Empty(r.Port.LiveAudioRun);
            Assert.False(result.LiveAudioDeferred);
            Assert.False(result.LiveAudioApplied);
            Assert.False(result.LiveAudioAborted);                                     // closed at the route, nothing attempted
            Assert.DoesNotContain(r.Port.Log, l => l.StartsWith("live ", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(ProfileActionKind.TurnAutosaveOff, "hold")]
        [InlineData(ProfileActionKind.TurnAutosaveOff, "join")]
        [InlineData(ProfileActionKind.CaptureLiveTransmitAudio, "intent")]
        [InlineData(ProfileActionKind.ApplyLocalTransmitAudio, "join")]
        public void ALiveAudioStep_WhoseWorldChangedBeforeItsWrite_IsRefusedAtTheStep_AndTheSequenceAborts(ProfileActionKind step, string what)
        {
            // The recheck is at the STEP (Track G3, group 3): autosave and
            // capture ran without one until then.
            var r = new Rig();
            r.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            r.Port.Facts.Intent = ProfileGuestIntent.UseMyTransmitAudio;
            r.Port.LocalTxProfile = "Contest"; r.Port.LocalTxProfileExists = true;
            r.Port.RadioAutosave = true;
            r.Port.GuestRoster = RosterVerdict.OnlyUs;                 // the positive control's authority
            r.Port.BeforeLiveAudioStep = kind =>
            {
                if (kind != step) return;
                switch (what)
                {
                    case "hold": r.Port.Facts.HoldArmed = true; break;
                    case "join": r.Port.GuestRoster = RosterVerdict.OthersPresent; break;
                    case "intent": r.Port.Facts.Intent = ProfileGuestIntent.LeaveAlone; break;
                }
            };
            var station = Established(); station.Outcome = StationOutcome.PolicySkipped;
            station.Allocation.Stop = AllocationStop.TargetReached; station.Allocation.OwnSlicesAtEnd = 2;

            var result = r.Run(station);

            Assert.True(result.LiveAudioAborted);
            Assert.DoesNotContain(step, r.Port.LiveAudioRun);          // the step's write never happened
            Assert.Contains(r.Port.Log, l => l.StartsWith("live " + step + " refused", StringComparison.Ordinal));
            Assert.False(result.LiveAudioApplied);
            Assert.False(result.LiveAudioDeferred);
        }

        [Fact]
        public void AutosaveNotConfirmedOff_AbortsBeforeCapture()
        {
            var r = new Rig();
            r.Port.GuestRoster = RosterVerdict.OnlyUs;
            r.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            r.Port.Facts.Intent = ProfileGuestIntent.UseMyTransmitAudio;
            r.Port.LocalTxProfile = "Contest"; r.Port.LocalTxProfileExists = true;
            r.Port.RadioAutosave = true;
            r.Port.LiveAudioOutcomes[ProfileActionKind.TurnAutosaveOff] = ProfileActionOutcome.Failed;
            var station = Established(); station.Outcome = StationOutcome.PolicySkipped;
            station.Allocation.Stop = AllocationStop.TargetReached; station.Allocation.OwnSlicesAtEnd = 2;

            var result = r.Run(station);

            Assert.Equal(new[] { ProfileActionKind.TurnAutosaveOff }, r.Port.LiveAudioRun);
            Assert.True(result.LiveAudioAborted);
            Assert.Contains("abort-live-audio autosave=False", r.Port.Log);
        }

        [Fact]
        public void ACaptureThatExpires_AbortsBeforeApply_AndGivesAutosaveBack()
        {
            var r = new Rig();
            r.Port.GuestRoster = RosterVerdict.OnlyUs;
            r.Port.Facts.Ownership = RadioOwnership.SomeoneElses;
            r.Port.Facts.Intent = ProfileGuestIntent.UseMyTransmitAudio;
            r.Port.LocalTxProfile = "Contest"; r.Port.LocalTxProfileExists = true;
            r.Port.RadioAutosave = true;
            r.Port.LiveAudioOutcomes[ProfileActionKind.CaptureLiveTransmitAudio] = ProfileActionOutcome.Failed;
            var station = Established(); station.Outcome = StationOutcome.PolicySkipped;
            station.Allocation.Stop = AllocationStop.TargetReached; station.Allocation.OwnSlicesAtEnd = 2;

            var result = r.Run(station);

            Assert.Equal(new[] { ProfileActionKind.TurnAutosaveOff, ProfileActionKind.CaptureLiveTransmitAudio }, r.Port.LiveAudioRun);
            Assert.True(result.LiveAudioAborted);
            Assert.Contains("abort-live-audio autosave=True", r.Port.Log);
        }

        [Fact]
        public void TheAssessmentIsAlwaysTheLastCall()
        {
            foreach (var station in new[] { Established(), Unconfirmed() })
            {
                var r = new Rig();
                r.Run(station);
                Assert.StartsWith("conclude", r.Port.Log.Last());
                Assert.Equal(1, r.Port.Log.Count(l => l.StartsWith("conclude", StringComparison.Ordinal)));
            }
        }
    }
}
