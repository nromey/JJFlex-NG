using System;
using System.IO;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// The half of the station-first connect that needs a FlexLib Radio to
    /// run — the handler feeds, the phase order in EstablishStationOnConnect,
    /// the deferred apply's revalidation, the readback-confirmed create — is
    /// pinned as SOURCE, in the ChangeNothingGuardTests shape and for the same
    /// reason: nothing else fails when one of these stops, and the radio it
    /// protects may belong to somebody else. Each pin names the design step
    /// it holds. A positive control first.
    /// </summary>
    public sealed class StationFirstWiringTests
    {
        private const string FlexBase = "Radios/FlexBase.cs";
        private const string FlexBaseStation = "Radios/FlexBase.StationConnect.cs";
        private const string Coordinator = "Radios/StationConnect/StationCoordinator.cs";

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Read(string relative) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        private static int IndexOf(string text, string needle)
        {
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(at >= 0, "expected to find '" + needle + "'");
            return at;
        }

        [Fact]
        public void TheReaderDiscriminates()
        {
            var text = Read(FlexBaseStation);
            Assert.Contains("EstablishStationOnConnect", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ThisStringIsNotInTheFileAnywhere", text, StringComparison.Ordinal);
        }

        // ── the handlers FEED the trackers (design step 1, section 3) ──

        [Theory]
        [InlineData("private void guiClientAdded(GUIClient client, ObservationBinding binding)", "ObserveClientAdded(binding, client, isMine);")]
        [InlineData("private void guiClientUpdated(GUIClient client, ObservationBinding binding)", "ObserveClientUpdated(binding, client);")]
        [InlineData("private void guiClientRemoved(GUIClient client, ObservationBinding binding)", "ObserveClientRemoved(binding, client);")]
        [InlineData("private void sliceAdded(Slice slc, ObservationBinding binding)", "ObserveOwnSliceAdded(binding, slc);")]
        [InlineData("private void sliceRemoved(Slice slc, ObservationBinding binding)", "ObserveOwnSliceRemoved(binding, slc);")]
        [InlineData("private void panadapterAdded(Panadapter pan, Waterfall fall, ObservationBinding binding)", "ObserveOwnPanadapterAdded(binding, pan);")]
        [InlineData("private void panAdapterRemoved(Panadapter pan, ObservationBinding binding)", "ObserveOwnPanadapterRemoved(binding, pan);")]
        [InlineData("private void radioPropertyChangedHandler(object sender, PropertyChangedEventArgs e, ObservationBinding binding)", "ObserveRadioProfileProperty(binding, r, e.PropertyName);")]
        public void TheProductionHandlerFeedsTheObservation(string signature, string feed)
        {
            var text = Read(FlexBase);
            int at = IndexOf(text, signature);
            Assert.Contains(feed, text.Substring(at, Math.Min(6000, text.Length - at)), StringComparison.Ordinal);
        }

        [Fact]
        public void TheOwnSliceFeedIsInsideTheOwnershipFilter()
        {
            // sliceAdded's myClient(slc.ClientHandle) branch is the production
            // ownership rule; the feed must sit inside it, so other clients'
            // slices never become our station.
            var text = Read(FlexBase);
            int sig = IndexOf(text, "private void sliceAdded(Slice slc, ObservationBinding binding)");
            int filter = text.IndexOf("if (myClient(slc.ClientHandle))", sig, StringComparison.Ordinal);
            int feed = text.IndexOf("ObserveOwnSliceAdded(binding, slc);", sig, StringComparison.Ordinal);
            int notMine = text.IndexOf("else Tracing.TraceLine(\"sliceAdded:not mine", sig, StringComparison.Ordinal);
            Assert.True(filter > 0 && feed > filter && notMine > feed,
                "ObserveOwnSliceAdded must be inside the myClient branch of sliceAdded");
        }

        [Fact]
        public void TheOwnSliceFieldFeedIsSubscribedPerNotification_AndTheFixtureSubscribesTheSameWay()
        {
            // The handler's lambda hands the PROPERTY NAME to the feed, so a
            // mode notification cannot record a frequency (Track G3). The
            // vendor fixture (RigOnVendorRadio.AddOwnSlice) subscribes the
            // same line without running sliceAdded's timer; this pins both.
            var text = Read(FlexBase);
            int sig = IndexOf(text, "private void sliceAdded(Slice slc, ObservationBinding binding)");
            string body = text.Substring(sig, 1200);
            Assert.Contains("ObserveOwnSliceReported(binding, (Slice)s2, e2.PropertyName);", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ObserveOwnSliceTuned(", body, StringComparison.Ordinal);

            var fixture = Read("Radios.Tests/StationConnect/VendorRadioFixture.cs");
            Assert.Contains("ObserveOwnSliceReportedMethod.Invoke(Rig, new object[] { Binding, (Slice)s2, e2.PropertyName });", fixture, StringComparison.Ordinal);
        }

        [Fact]
        public void TheOnceAtOwnAddSnapshotIsGone_AndOnlyStationIsLive()
        {
            var text = Read(FlexBase);
            Assert.DoesNotContain("OnlyStation = (theRadio.GuiClients.Count == 1);", text, StringComparison.Ordinal);
            Assert.Contains("public bool OnlyStation => OtherOperatorPresence == RosterVerdict.OnlyUs;", text, StringComparison.Ordinal);
        }

        [Fact]
        public void TheInventoryMembershipCompletionIsGone()
        {
            // #579: ProfileGlobalList.Contains(desired) no longer stands in for
            // station completion anywhere.
            var text = Read(FlexBase);
            Assert.DoesNotContain("globalProfileLoaded = r.ProfileGlobalList.Contains(globalProfileDesired);", text, StringComparison.Ordinal);
            Assert.DoesNotContain("await(() => globalProfileLoaded, 20000)", text, StringComparison.Ordinal);
        }

        [Fact]
        public void TheAttemptBeginsBeforeTheHandlersAreWired_AndBeforeConnect()
        {
            var text = Read(FlexBase);
            int begin = IndexOf(text, "BeginStationAttempt(theRadio, \"Connect\");");
            int wired = IndexOf(text, "WireStationHandlers(theRadio);");
            int connect = text.IndexOf("rv = theRadio.Connect();", begin, StringComparison.Ordinal);
            Assert.True(begin < wired && wired < connect,
                "observation must be subscribed, through the attempt's binding, before any command is sent");
            // The retry mints a new attempt through the same entry, which
            // rewires (BeginStationAttempt does it for a same-object retry).
            int retry = IndexOf(text, "BeginStationAttempt(theRadio, \"RetryConnect\");");
            Assert.True(retry > 0);
        }

        [Fact]
        public void EveryStationHandlerIsWiredThroughTheBinding_AndNoFeedReadsTheCurrentAttempt()
        {
            // The generation-isolation guarantee lives in the wiring: each
            // closure holds the IMMUTABLE binding minted for this attempt,
            // drops its callback whole when that binding is no longer
            // current, and is unwired on the next attempt (Track G3). A feed
            // that read AttemptGen would stamp a stale object's callback with
            // the current attempt — the review's step-1 defect.
            var station = Read(FlexBaseStation);
            int wiring = IndexOf(station, "internal void WireStationHandlers(Radio radio)");
            string wiringBody = station.Substring(wiring, 4000);
            foreach (var wire in new[]
            {
                "if (IsCurrentBinding(binding)) radioPropertyChangedHandler(s, e, binding);",
                "if (IsCurrentBinding(binding)) guiClientAdded(c, binding);",
                "if (IsCurrentBinding(binding)) guiClientUpdated(c, binding);",
                "if (IsCurrentBinding(binding)) guiClientRemoved(c, binding);",
                "if (IsCurrentBinding(binding)) sliceAdded(slc, binding);",
                "if (IsCurrentBinding(binding)) sliceRemoved(slc, binding);",
                "if (IsCurrentBinding(binding)) panadapterAdded(pan, fall, binding);",
                "if (IsCurrentBinding(binding)) panAdapterRemoved(pan, binding);",
                "previous.Unwire();",
            })
            {
                Assert.Contains(wire, wiringBody, StringComparison.Ordinal);
            }
            Assert.Null(typeof(Radios.StationConnect.ObservationBinding).GetMethod("Rebind"));
            var flex = Read(FlexBase);
            Assert.DoesNotContain("theRadio.GUIClientAdded += ", flex, StringComparison.Ordinal);
            int feeds = IndexOf(station, "private void ObserveClientAdded(ObservationBinding binding");
            int end = IndexOf(station, "// The live roster verdict (#577)");
            string feedRegion = station.Substring(feeds, end - feeds);
            Assert.DoesNotContain("AttemptGen", feedRegion, StringComparison.Ordinal);
            Assert.Contains("binding.Generation", feedRegion, StringComparison.Ordinal);
        }

        [Fact]
        public void TheLeaveOffer_IsCheckedAtEveryResultPublication_AndIsMadeOnceByCompareAndSwap()
        {
            // Track G2 re-review, section 5: a leave during the run with no
            // evidence after it was missed, and the once flag was a plain
            // check-then-set across two threads.
            var text = Read(FlexBaseStation);
            foreach (var site in new[] { "internal StationResult EstablishStationOnConnect(bool postImport)", "public StationResult LoadOwnerGlobalProfileOnRequest(OwnerLoadOffer offer)" })
            {
                int method = IndexOf(text, site);
                int publish = text.IndexOf("LastStationResult = result;", method, StringComparison.Ordinal);
                int consider = text.IndexOf("ConsiderOwnerProfileLoadOffer();", method, StringComparison.Ordinal);
                Assert.True(publish > method && consider > publish && consider - publish < 400,
                    site + " must consider the offer immediately after publishing its result");
            }
            int offer = IndexOf(text, "private void ConsiderOwnerProfileLoadOffer()");
            string body = text.Substring(offer, 2000);
            Assert.Contains("Interlocked.CompareExchange(ref _ownerLoadOfferedForAttempt, gen, already)", body, StringComparison.Ordinal);
            Assert.Contains("OwnerProfileLoadOffered?.Invoke(offer);", body, StringComparison.Ordinal);
        }

        [Fact]
        public void ThePostImportEntryBeginsItsOwnOperation_AndCarriesThePreviousResult()
        {
            var text = Read(FlexBaseStation);
            int method = IndexOf(text, "internal StationResult EstablishStationOnConnect(bool postImport)");
            string body = text.Substring(method, Math.Min(3000, text.Length - method));
            Assert.Contains("var previous = postImport ? LastStationResult : null;", body, StringComparison.Ordinal);
            Assert.Contains("attempt.BeginOperation(postImport ?", body, StringComparison.Ordinal);
            Assert.Contains("StationDeadlines.Default(), waiter, previous);", body, StringComparison.Ordinal);
        }

        // ── the order of phases (design steps 4 to 8; mutation check three) ──

        [Fact]
        public void TransmitAndMicrophoneAreReadAfterTheStationPhase_NotBefore()
        {
            // The order INSIDE the post-station phase (transmit confirmed,
            // then microphone read fresh) is a behavioural test now:
            // PostStationOrchestratorTests. This pins only that the phase
            // follows the coordinator's run and reads through the port's
            // one-type read, never the all-types situation.
            var text = Read(FlexBaseStation);
            int method = IndexOf(text, "internal StationResult EstablishStationOnConnect(bool postImport)");
            int run = text.IndexOf("result = coordinator.Run();", method, StringComparison.Ordinal);
            int post = text.IndexOf("RunPostStationPhase(result, operation);", method, StringComparison.Ordinal);
            Assert.True(run > 0 && post > run,
                "the post-station phase (fresh tx/mic reads) must follow the coordinator's run, never precede it");

            int port = IndexOf(text, "private sealed class FlexPostStationPort : IPostStationPort");
            string portBody = text.Substring(port, Math.Min(2500, text.Length - port));
            Assert.Contains("_rig.ReadProfileTypeState(type, _rig.WantedProfilesForThisRadio(), freshAsk: true, timeoutMs: timeoutMs)", portBody, StringComparison.Ordinal);
            Assert.DoesNotContain("ReadProfileSituation(", portBody, StringComparison.Ordinal);
        }

        [Fact]
        public void TheGlobalDecisionReadsOnlyTheGlobalType()
        {
            var text = Read(FlexBaseStation);
            int port = IndexOf(text, "public ProfileSituation ReadGlobalSituation(int timeoutMs)");
            Assert.Contains("freshTypes: new[] { ProfileTypes.global }",
                text.Substring(port, Math.Min(800, text.Length - port)), StringComparison.Ordinal);
        }

        [Fact]
        public void TheSilentMicAssessmentRunsLast_AndRepairsOnlyUnderTheSharedWriteAuthority()
        {
            // "Last" is behavioural now (PostStationOrchestratorTests,
            // TheAssessmentIsAlwaysTheLastCall); this pins the adapter's
            // Conclude: announce, then assess with the orchestrator's
            // MayRepairMicrophone, deferred behind the live-audio apply when
            // one is pending.
            var text = Read(FlexBaseStation);
            int conclude = IndexOf(text, "private void ConcludePostStationPhase(PostStationResult result)");
            string body = text.Substring(conclude, Math.Min(1600, text.Length - conclude));
            int announce = body.IndexOf("AnnounceConnectStewardship(", StringComparison.Ordinal);
            int deferred = body.IndexOf("_pendingAssessmentMayRepair = result.MayRepairMicrophone;", StringComparison.Ordinal);
            int check = body.IndexOf("CheckMicProfileForSilentTx(mayRepair: result.MayRepairMicrophone);", StringComparison.Ordinal);
            Assert.True(announce > 0 && deferred > announce && check > deferred, "conclude must announce, then defer or assess");

            var flex = Read(FlexBase);
            Assert.Contains("private void CheckMicProfileForSilentTx(bool mayRepair)", flex, StringComparison.Ordinal);
            Assert.Contains("&& !ChangeNothingActive && mayRepair", flex, StringComparison.Ordinal);
            // The deferred assessment runs from the continuation queued behind
            // the apply's setters, never before them.
            int apply = IndexOf(flex, "internal void ApplyDeferredGuestTransmitAudio()");
            string applyBody = flex.Substring(apply, Math.Min(6000, flex.Length - apply));
            int continuation = applyBody.IndexOf("\"live transmit audio continuation\"", StringComparison.Ordinal);
            int assess = applyBody.IndexOf("RunPendingSilentMicAssessment(\"after the deferred apply's setters\")", StringComparison.Ordinal);
            Assert.True(assess > 0 && continuation > assess, "the assessment must be inside the continuation queued behind the setters");
        }

        // ── the recheck lives INSIDE the dispatched delegate (mutation check two) ──

        [Fact]
        public void TheCoordinatorRechecksRosterAndPolicyInsideTheDispatchedDelegate()
        {
            var text = Read(Coordinator);
            int dispatch = IndexOf(text, "_port.Dispatch(\"stewardship global load '\" + name + \"'\", () =>");
            int send = text.IndexOf("_port.SendGlobalLoad(name,", dispatch, StringComparison.Ordinal);
            string inside = text.Substring(dispatch, send - dispatch);
            Assert.Contains("RosterGuard.ForAutomaticWrite(_roster.Snapshot(), _policies.RosterAuthority)", inside, StringComparison.Ordinal);
            Assert.Contains("_port.ReadPolicyFacts()", inside, StringComparison.Ordinal);
            Assert.Contains("inventory.Contains(name)", inside, StringComparison.Ordinal);
        }

        [Fact]
        public void TheTransmitAndMicrophoneSelectionsRecheckInsideTheirDelegate()
        {
            // The recheck's CONTENT (operation, phase, facts, stewardship,
            // strict roster) is the orchestrator's RefusalAtSend, exercised by
            // the held-dispatch tests in PostStationOrchestratorTests. This
            // pins that the production delegate calls it before writing.
            var text = Read(FlexBaseStation);
            int method = IndexOf(text, "private ProfileActionOutcome DispatchSelectionChecked(");
            int dispatch = text.IndexOf("DispatchStationWork(", method, StringComparison.Ordinal);
            int select = text.IndexOf("SendRadioCommandWithReply(radio, ProfileLoadCommand(action.ProfileType, action.ProfileName), onReply);", method, StringComparison.Ordinal);
            Assert.True(select > dispatch, "the selection must go through the reply-bearing send, never the FlexLib setter (Track G3)");
            string inside = text.Substring(dispatch, select - dispatch);
            Assert.Contains("refusal = refusalAtSend?.Invoke();", inside, StringComparison.Ordinal);
            Assert.Contains("if (refusal != null) return;", inside, StringComparison.Ordinal);
            Assert.DoesNotContain("radio.ProfileTXSelection = ", inside, StringComparison.Ordinal);
            Assert.DoesNotContain("radio.ProfileMICSelection = ", inside, StringComparison.Ordinal);

            var orchestrator = Read("Radios/StationConnect/PostStationOrchestrator.cs");
            int refusal = IndexOf(orchestrator, "private string RefusalAtSend(");
            string body = orchestrator.Substring(refusal, 1200);
            Assert.Contains("operation.IsEnded", body, StringComparison.Ordinal);
            Assert.Contains("phase.Passed(_clock)", body, StringComparison.Ordinal);
            Assert.Contains("_port.RosterForAutomaticWrite()", body, StringComparison.Ordinal);
        }

        // ── mainThreadProc: no scratch setup, no wait-on-self (design section 4; #582) ──

        [Fact]
        public void MainThreadProcNeverReachesSetupFromScratch()
        {
            var text = Read(FlexBase);
            int main = IndexOf(text, "private void mainThreadProc()");
            int end = text.IndexOf("public class cfg7620", main, StringComparison.Ordinal);
            string body = text.Substring(main, end - main);
            Assert.DoesNotContain("setupFromScratch();", body, StringComparison.Ordinal);
            Assert.Contains("var station = EstablishStationOnConnect(false);", body, StringComparison.Ordinal);
        }

        [Fact]
        public void TheDeferredGuestApplyIsQueueWork_NotABlockingCallBeforeTheDequeueLoop()
        {
            var text = Read(FlexBase);
            int main = IndexOf(text, "private void mainThreadProc()");
            int end = text.IndexOf("public class cfg7620", main, StringComparison.Ordinal);
            string body = text.Substring(main, end - main);
            Assert.Contains("q.Enqueue((FunctionDel)ApplyDeferredGuestTransmitAudio, \"deferred guest transmit audio\");", body, StringComparison.Ordinal);
            Assert.DoesNotContain("                ApplyDeferredGuestTransmitAudio();", body, StringComparison.Ordinal);
        }

        [Fact]
        public void DispatchRunsInlineOnTheCommandThread_SoThePostImportEntryCannotWaitOnItself()
        {
            var text = Read(FlexBaseStation);
            int method = IndexOf(text, "private void DispatchStationWork(string name, Action work)");
            Assert.Contains("Thread.CurrentThread == mainThread", text.Substring(method, 600), StringComparison.Ordinal);
        }

        // ── the guest live-audio path (design step 9; group 7) ──

        [Fact]
        public void TheDeferredApplyRevalidatesAtTheDelegate_AndInsideEverySetter_UnderTheStrictRoster()
        {
            // The rule's CONTENT is DeferredLiveAudioGate, tested condition by
            // condition in LiveTransmitAudioGateTests; the queue's honouring
            // of the ambient gate is tested there too. This pins the wiring:
            // the payload applied is the one held at deferral, the gate is
            // consulted before the apply and is the ambient gate around it,
            // and the roster it reads is the strict one.
            var text = Read(FlexBase);
            int method = IndexOf(text, "internal void ApplyDeferredGuestTransmitAudio()");
            int apply = text.IndexOf("ApplyLocalTransmitAudioPayloadNow(payload, pending)", method, StringComparison.Ordinal);
            Assert.True(apply > method, "the deferred apply must apply the HELD payload");
            string before = text.Substring(method, apply - method);
            Assert.Contains("DeferredLiveAudioRefusal(pending, payload)", before, StringComparison.Ordinal);
            Assert.Contains("QueuedWriteGate.Open(() => DeferredLiveAudioRefusal(pending, payload))", before, StringComparison.Ordinal);
            Assert.DoesNotContain("FindLocalTransmitAudioProfile(pending)", text.Substring(method, 6000), StringComparison.Ordinal);

            int refusal = IndexOf(text, "private string DeferredLiveAudioRefusal(string presetName, AudioChainPreset payload)");
            string body = text.Substring(refusal, Math.Min(2500, text.Length - refusal));
            Assert.Contains("DeferredLiveAudioGate.Refusal(", body, StringComparison.Ordinal);
            // The GUEST'S authority, not the owner's ruled one (Track G3).
            Assert.Contains("StrictRoster = RosterJudgementForGuestSharedWrite().Verdict", body, StringComparison.Ordinal);
            Assert.DoesNotContain("StrictRoster = RosterJudgementForAutomaticWrite().Verdict", body, StringComparison.Ordinal);
            Assert.DoesNotContain("RosterJudgementNow()", body, StringComparison.Ordinal);
        }

        [Fact]
        public void TheCaptureIsDurableOnlyWhenTheSnapshotReadsBack_AndTheAbortKeepsAnUnconfirmedNotice()
        {
            var text = Read(FlexBase);
            int capture = IndexOf(text, "private bool CaptureLiveTransmitAudio()");
            string body = text.Substring(capture, Math.Min(3500, text.Length - capture));
            Assert.Contains("if (!SaveLiveTxSnapshot(serial, _liveTxSnapshot))", body, StringComparison.Ordinal);
            Assert.Contains("LiveTxSnapshotStore.Persist(path, snapshot)", text, StringComparison.Ordinal);

            int abort = IndexOf(text, "private void RestoreRadioAutosaveAfterAbort()");
            string abortBody = text.Substring(abort, 2200);
            int confirmed = abortBody.IndexOf("bool confirmed = SetRadioProfileAutosaveInternal(true,", StringComparison.Ordinal);
            int clear = abortBody.IndexOf("RadioConfig.RecordAutosaveTurnedOffByUs(serial, false);", StringComparison.Ordinal);
            int guard = abortBody.IndexOf("if (!confirmed)", StringComparison.Ordinal);
            Assert.True(confirmed > 0 && guard > confirmed && clear > guard, "the durable notice must clear only after a confirmed restore");
        }

        [Fact]
        public void AutosaveConfirmationRequiresRadioProvenanceAfterTheSend()
        {
            var text = Read(FlexBase);
            int method = IndexOf(text, "private bool SetRadioProfileAutosaveInternal(bool on, string why)");
            string body = text.Substring(method, Math.Min(4000, text.Length - method));
            Assert.Contains("Provenance == ObservationProvenance.RadioReported", body, StringComparison.Ordinal);
            Assert.Contains("Sequence > seq", body, StringComparison.Ordinal);
            Assert.DoesNotContain("await(() => theRadio == null || theRadio.ProfileAutoSave == on, 3000)", body, StringComparison.Ordinal);
        }

        [Fact]
        public void AnExpiredCaptureBoundFails_RatherThanCapturingDefaults()
        {
            var text = Read(FlexBase);
            int method = IndexOf(text, "private bool CaptureLiveTransmitAudio()");
            string body = text.Substring(method, Math.Min(3000, text.Length - method));
            Assert.Contains("NOT capturing", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Capturing anyway", body, StringComparison.Ordinal);
            Assert.Contains("_liveTxSnapshotChainGeneration = evidence.TxChainGeneration;", body, StringComparison.Ordinal);
        }

        [Fact]
        public void TheGenericTransmitChainWritesAreOwnerOnly_AndInsideTheEstablishedStationPhase()
        {
            // TNF, MicInput, VOX, CW break-in, TX1 AND the keyer restore are
            // RunOwnerInitialization, dispatched by the orchestrator only
            // under the full gate with the recheck at the write (Track G3).
            // Nothing in mainThreadProc, issue7620 or Connect writes them.
            var text = Read(FlexBase);
            int main = IndexOf(text, "private void mainThreadProc()");
            int end = text.IndexOf("public class cfg7620", main, StringComparison.Ordinal);
            string body = text.Substring(main, end - main);
            Assert.DoesNotContain("MicInput = \"mic\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TX1Enabled = true", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SimpleVOXEnable = false", body, StringComparison.Ordinal);
            Assert.DoesNotContain("i_BreakinDelay = cfgData.BreakinDelay;", text, StringComparison.Ordinal);
            Assert.DoesNotContain("OwnerKeyerRestorePermitted(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("theRadio.TNFEnabled = true", text, StringComparison.Ordinal);

            var station = Read(FlexBaseStation);
            int init = IndexOf(station, "private ProfileActionOutcome RunOwnerInitialization(StationOperation operation, Func<string> refusalAtWrite)");
            string initBody = station.Substring(init, 2500);
            int dispatch = initBody.IndexOf("DispatchStationWork(\"owner initialisation\"", StringComparison.Ordinal);
            int recheck = initBody.IndexOf("refusal = refusalAtWrite?.Invoke();", StringComparison.Ordinal);
            int tnf = initBody.IndexOf("r.TNFEnabled = true;", StringComparison.Ordinal);
            int keyer = initBody.IndexOf("ApplyKeyerRestoreToRadio(r);", StringComparison.Ordinal);
            Assert.True(dispatch > 0 && recheck > dispatch && tnf > recheck && keyer > tnf,
                "owner initialisation must dispatch, recheck, then write TNF and the keyer inside the delegate");
        }

        [Fact]
        public void TheShackSpeakerMuteWrites_GoThroughTheOwnerGate()
        {
            var text = Read(FlexBase);
            Assert.Contains("OwnerSharedWriteSkips(\"IsMuteLocalAudioWhenRemoteOn=false on local connect\")", text, StringComparison.Ordinal);
            Assert.Contains("OwnerSharedWriteSkips(\"IsMuteLocalAudioWhenRemoteOn=true on remote audio start\")", text, StringComparison.Ordinal);
            Assert.DoesNotContain("GuardSkips(\"IsMuteLocalAudioWhenRemoteOn", text, StringComparison.Ordinal);
            var station = Read(FlexBaseStation);
            int gate = IndexOf(station, "private bool OwnerSharedWriteSkips(string what)");
            Assert.Contains("OwnerSharedWriteGate.Refusal(ReadStationPolicyFacts(), RosterJudgementForAutomaticWrite())", station.Substring(gate, 600), StringComparison.Ordinal);
        }

        [Fact]
        public void RecordsComeFromWhatWasConfirmed_NotFromMembershipInThePlan()
        {
            // Behavioural in PostStationOrchestratorTests (the owner's
            // confirmed loads record nothing; a deferred live apply records
            // nothing until it runs). This pins the rule's home.
            var text = Read("Radios/StationConnect/PostStationOrchestrator.cs");
            Assert.Contains("if (result.ConfirmedSelections.Contains(rec.ProfileType)) records.Add(rec);", text, StringComparison.Ordinal);
            Assert.DoesNotContain("plan.Actions.Any(a => a.ProfileType == rec.ProfileType", text, StringComparison.Ordinal);
        }

        // ── the deferred create (design step 11; group 10's readback half) ──

        [Fact]
        public void TheDeferredCreateRunsThroughTheDecideInsideTheDelegateRun()
        {
            // The behaviour (decision inside the delegate, fresh inventory,
            // deadline, readback, uncertain retained) is DeferredCreationRunTests.
            // This pins that production reaches it under the teardown
            // operation and keeps an unconfirmed send as uncertain.
            var text = Read(FlexBaseStation);
            int method = IndexOf(text, "private bool CreatePendingGlobalAtDisconnect()");
            string body = text.Substring(method, Math.Min(3000, text.Length - method));
            Assert.Contains("new DeferredCreationRun(", body, StringComparison.Ordinal);
            Assert.Contains("attempt.BeginOperation(\"teardown: disconnect-time create\")", body, StringComparison.Ordinal);
            Assert.Contains("if (result.Outcome == CreationOutcome.Unconfirmed)", body, StringComparison.Ordinal);
            Assert.DoesNotContain("DeferredGlobalCreation.Decide(", body, StringComparison.Ordinal);

            var run = Read("Radios/StationConnect/DeferredCreationRun.cs");
            int dispatch = IndexOf(run, "_port.Dispatch(\"save new global '\"");
            int fresh = run.IndexOf("_port.RequestGlobalInventory(", dispatch, StringComparison.Ordinal);
            int decide = run.IndexOf("DeferredGlobalCreation.Decide(", dispatch, StringComparison.Ordinal);
            int save = run.IndexOf("_port.SaveGlobalProfile(pending.Name);", dispatch, StringComparison.Ordinal);
            Assert.True(fresh > dispatch && decide > fresh && save > decide, "fresh ask, then decide, then save, all inside the delegate");
        }

        [Fact]
        public void TheDeferredCreateRunsFromTheCleanDisconnectAndFromDispose()
        {
            var text = Read(FlexBase);
            int count = text.Split(new[] { "saveNewGlobalProfile();" }, StringSplitOptions.None).Length - 1;
            Assert.True(count >= 2, "expected calls from Disconnect and from Dispose, found " + count);
        }

        [Fact]
        public void TheOperatorDefaultLookupIsGoneFromTheCreatePath()
        {
            // #578: saveNewGlobalProfile searched GetDefaultProfiles() and
            // missed a per-radio-only name.
            var text = Read(FlexBase);
            int method = IndexOf(text, "private bool saveNewGlobalProfile()");
            string body = text.Substring(method, Math.Min(1500, text.Length - method));
            Assert.DoesNotContain("GetDefaultProfiles()", body, StringComparison.Ordinal);
            Assert.Contains("CreatePendingGlobalAtDisconnect()", body, StringComparison.Ordinal);
        }

        // ── the policies are read from one place the bench can set ──

        [Fact]
        public void ProductionReadsThePoliciesFromStationPoliciesCurrent()
        {
            var text = Read(FlexBaseStation);
            Assert.Contains("StationPolicies.Current, _stationClock, operation, StationDeadlines.Default(), waiter, previous", text, StringComparison.Ordinal);
            Assert.Contains("StationPolicies.Current.RosterAuthority", text, StringComparison.Ordinal);
        }
    }
}
