using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using System.Diagnostics;
using Radios.StationConnect;

namespace Radios
{
    // ══════════════════════════════════════════════════════════════════════
    // Station-first connect (Sprint 45 Track G; #563, #574, #577, #578, #579,
    // #587, #588, #590).
    //
    // This file is the seam between FlexLib and the Radios.StationConnect
    // layer: the observation feeds the handlers call, the production
    // IStationPort, the attempt lifecycle, the phased connect that replaced
    // GetProfileInfo's body, and the deferred disconnect-time create. Every
    // RULE lives in Radios.StationConnect or ProfileStewardship, where a test
    // reaches it without a radio; everything here is wire.
    //
    // THE RULING THAT SHAPES IT (Noel, 2026-09-21, #590): only the declared
    // owner's connection restores a station. Anyone else connecting writes
    // nothing shared, regardless of the roster. A guest gets client-local
    // receive resources and touches no shared state. The one guest path that
    // remains is UseMyTransmitAudio, which is a different mechanism.
    // ══════════════════════════════════════════════════════════════════════
    public partial class FlexBase
    {
        // ------------------------------------------------------------------
        // Observation infrastructure, one set per FlexBase, reset per attempt
        // ------------------------------------------------------------------

        private readonly IStationClock _stationClock = MonotonicStationClock.Instance;
        private RosterTracker _rosterTracker;
        private StationTracker _stationTracker;
        private ProfileEvidenceLog _profileEvidence;
        private ConnectionAttempt _stationAttempt;
        private readonly object _stationAttemptLock = new object();

        /// <summary>
        /// The binding the CURRENT radio object's handlers were wired with.
        /// Every feed below stamps its observation with the binding's
        /// generation, captured at wiring, never with whatever attempt is
        /// current when the callback runs. A previous radio object keeps its
        /// own binding on its own, dead attempt; discovery may go on raising
        /// events on it forever and every tracker rejects them.
        /// </summary>
        private ObservationBinding _stationBinding;

        /// <summary>The result of the last station establishment on this
        /// connection, or null before one has run. Read by the post-station
        /// phase, the deferred create and the connect briefing.</summary>
        public StationResult LastStationResult { get; private set; }

        /// <summary>The armed disconnect-time create (#578), or null.</summary>
        private PendingGlobalCreation _pendingGlobalCreation;

        private RosterTracker RosterTracker => _rosterTracker ??= new RosterTracker(_stationClock);
        private StationTracker StationTracker => _stationTracker ??= new StationTracker(_stationClock);
        private ProfileEvidenceLog ProfileEvidence => _profileEvidence ??= new ProfileEvidenceLog(_stationClock);

        /// <summary>The current attempt, or a cancelled placeholder when no
        /// connection has begun. Never null.</summary>
        internal ConnectionAttempt StationAttempt
        {
            get
            {
                lock (_stationAttemptLock)
                {
                    if (_stationAttempt == null)
                    {
                        _stationAttempt = new ConnectionAttempt("");
                        _stationAttempt.Cancel("no connection has begun");
                    }
                    return _stationAttempt;
                }
            }
        }

        private int AttemptGen => StationAttempt.Generation;

        /// <summary>
        /// The binding for <paramref name="radio"/>: the current one when it
        /// is for that object, else a fresh one on the current attempt. Used
        /// at handler wiring; the wired closures hold the binding.
        /// </summary>
        internal ObservationBinding BindingFor(Radio radio)
        {
            var b = _stationBinding;
            if (b != null && b.IsFor(radio)) return b;
            b = new ObservationBinding(radio, StationAttempt);
            _stationBinding = b;
            return b;
        }

        /// <summary>
        /// Begin a connection attempt: mint the generation every observation
        /// of this connection is stamped with, and empty the trackers so the
        /// previous connection's roster, slices and profile facts cannot
        /// complete a wait on this one. Called BEFORE the FlexLib handlers are
        /// wired and before Connect(), so observation is subscribed before any
        /// command is sent (design step 1).
        /// </summary>
        /// <remarks>
        /// The handlers are wired ONCE per radio object (RetryConnect wires
        /// nothing). So the binding is rebound when the attempt is for the
        /// same radio object, and replaced when it is for a different one —
        /// leaving the old object's handlers bound to the old attempt, whose
        /// generation the trackers reject. That is the whole of the
        /// generation-isolation guarantee: by radio object, never by the
        /// moment a callback happens to run.
        /// </remarks>
        internal void BeginStationAttempt(Radio radio, string why)
        {
            string serial = radio?.Serial ?? "";
            ConnectionAttempt previous;
            ConnectionAttempt fresh = new ConnectionAttempt(serial);
            lock (_stationAttemptLock)
            {
                previous = _stationAttempt;
                _stationAttempt = fresh;
            }
            previous?.Cancel("superseded by " + fresh + " (" + why + ")");

            var binding = _stationBinding;
            if (binding != null && binding.IsFor(radio)) binding.Rebind(fresh);
            else _stationBinding = new ObservationBinding(radio, fresh);

            RosterTracker.Reset(fresh.Generation);
            StationTracker.Reset(fresh.Generation);
            ProfileEvidence.Reset(fresh.Generation);
            _pendingGlobalCreation = null;
            LastStationResult = null;

            // Anything the trackers publish wakes whoever is waiting.
            RosterTracker.Changed -= StationEvidenceChanged;
            StationTracker.Changed -= StationEvidenceChanged;
            ProfileEvidence.Changed -= StationEvidenceChanged;
            RosterTracker.Changed += StationEvidenceChanged;
            StationTracker.Changed += StationEvidenceChanged;
            ProfileEvidence.Changed += StationEvidenceChanged;

            Tracing.TraceLine("StationConnect: " + fresh + " begins (" + why + "); observation subscribed before any command",
                TraceLevel.Info);
        }

        private void StationEvidenceChanged() => StationAttempt.Signal();

        /// <summary>Invalidate the current attempt. Idempotent.</summary>
        private void CancelStationAttempt(string why)
        {
            ConnectionAttempt current;
            lock (_stationAttemptLock) current = _stationAttempt;
            if (current != null && current.IsLive)
            {
                current.Cancel(why);
                Tracing.TraceLine("StationConnect: " + current, TraceLevel.Info);
            }
        }

        // ------------------------------------------------------------------
        // Feeds from the FlexLib handlers (called from the receive thread)
        // ------------------------------------------------------------------

        private static RosterEntry RosterEntryFrom(GUIClient c) =>
            new RosterEntry(c.ClientHandle, c.ClientID, c.IsThisClient, c.Station, c.Program);

        // Every feed takes the BINDING its handler was wired with and stamps
        // the observation with that binding's generation. None of them reads
        // the current attempt: a callback from a radio object we have left is
        // stamped with the attempt it was wired for, and rejected.

        private void ObserveClientAdded(ObservationBinding binding, GUIClient client, bool isMine)
        {
            int gen = binding.Generation;
            RosterTracker.ClientAdded(RosterEntryFrom(client), gen);
            if (isMine) RosterTracker.OwnHandleEstablished(client.ClientHandle, gen);
        }

        private void ObserveClientUpdated(ObservationBinding binding, GUIClient client) =>
            RosterTracker.ClientUpdated(RosterEntryFrom(client), binding.Generation);

        private void ObserveClientRemoved(ObservationBinding binding, GUIClient client) =>
            RosterTracker.ClientRemoved(client.ClientHandle, binding.Generation);

        private void ObserveOwnSliceAdded(ObservationBinding binding, Slice slc) =>
            StationTracker.OwnSliceAdded(slc.Index, slc.Letter, slc.ClientHandle, slc.PanadapterStreamID, binding.Generation);

        private void ObserveOwnSliceRemoved(ObservationBinding binding, Slice slc) =>
            StationTracker.OwnSliceRemoved(slc.Index, binding.Generation);

        private void ObserveOwnPanadapterAdded(ObservationBinding binding, Panadapter pan) =>
            StationTracker.OwnPanadapterAdded(pan.StreamID, binding.Generation);

        private void ObserveOwnPanadapterRemoved(ObservationBinding binding, Panadapter pan) =>
            StationTracker.OwnPanadapterRemoved(pan.StreamID, binding.Generation);

        /// <summary>
        /// Set on the thread that is inside one of OUR profile writes, so a
        /// PropertyChanged raised synchronously by the setter reads as a local
        /// echo and not as the radio answering. FlexLib's receive thread never
        /// carries it, so anything arriving there is radio-reported.
        /// </summary>
        [ThreadStatic] private static int _ownProfileWriteDepth;

        private static ObservationProvenance ProvenanceNow() =>
            _ownProfileWriteDepth > 0 ? ObservationProvenance.LocalEcho : ObservationProvenance.RadioReported;

        private sealed class OwnProfileWriteScope : IDisposable
        {
            public OwnProfileWriteScope() { _ownProfileWriteDepth++; }
            public void Dispose() { _ownProfileWriteDepth--; }
        }

        private static IDisposable OwnProfileWrite() => new OwnProfileWriteScope();

        /// <summary>The radio property names that make up the transmit chain
        /// the live-audio snapshot captures. A radio-reported change to any
        /// of them advances the chain generation.</summary>
        private static readonly HashSet<string> TxChainProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "MicLevel", "MicBoost", "MicBias", "MicInput", "CompanderOn", "CompanderLevel",
            "SpeechProcessorEnable", "SpeechProcessorLevel", "TXFilterLow", "TXFilterHigh",
            "TXMonitor", "SBMonitorGain", "SBMonitorPan", "TXEqEnabled",
        };

        /// <summary>Called from the radio property handler for the properties
        /// the station phase observes. Returns quickly; never blocks.</summary>
        private void ObserveRadioProfileProperty(ObservationBinding binding, Radio r, string propertyName)
        {
            if (!binding.IsFor(r)) return; // a radio object this binding was not wired on
            int gen = binding.Generation;
            var provenance = ProvenanceNow();
            switch (propertyName)
            {
                case "ProfileGlobalList":
                    ProfileEvidence.GlobalListObserved(r.ProfileGlobalList?.ToList(), provenance, gen);
                    break;
                case "ProfileGlobalSelection":
                    ProfileEvidence.GlobalSelectionObserved(r.ProfileGlobalSelection, provenance, gen);
                    break;
                case "ProfileAutoSave":
                    ProfileEvidence.AutosaveObserved(r.ProfileAutoSave, provenance, gen);
                    break;
                case "PersistenceLoaded":
                    if (r.PersistenceLoaded) ProfileEvidence.PersistenceLoadedObserved(gen);
                    break;
                default:
                    if (provenance == ObservationProvenance.RadioReported && TxChainProperties.Contains(propertyName))
                        ProfileEvidence.TxChainReported(gen);
                    break;
            }
        }

        // ------------------------------------------------------------------
        // The live roster verdict (#577), replacing the cached Boolean
        // ------------------------------------------------------------------

        /// <summary>
        /// What the current roster shows about company on this radio,
        /// recomputed from the live snapshot on every read. Membership and
        /// identity, never count alone; Unknown when our own handle is not
        /// established or not in the roster.
        /// </summary>
        public RosterVerdict OtherOperatorPresence => RosterGuard.Evaluate(RosterTracker.Snapshot()).Verdict;

        /// <summary>The verdict with its reason, for callers that speak it.</summary>
        internal RosterJudgement RosterJudgementNow() => RosterGuard.Evaluate(RosterTracker.Snapshot());

        /// <summary>The verdict an AUTOMATIC shared write must obtain: the live
        /// roster plus the bench-supplied authority policy.</summary>
        internal RosterJudgement RosterJudgementForAutomaticWrite() =>
            RosterGuard.ForAutomaticWrite(RosterTracker.Snapshot(), StationPolicies.Current.RosterAuthority);

        // ------------------------------------------------------------------
        // The production port
        // ------------------------------------------------------------------

        private sealed class FlexStationPort : IStationPort
        {
            private readonly FlexBase _rig;
            public FlexStationPort(FlexBase rig) { _rig = rig; }

            public StationPolicyFacts ReadPolicyFacts() => _rig.ReadStationPolicyFacts();

            public ProfileSituation ReadGlobalSituation(int timeoutMs)
            {
                if (_rig.theRadio == null) return null;
                return _rig.ReadProfileSituation(
                    _rig.WantedProfilesForThisRadio(), freshAsk: true,
                    freshTypes: new[] { ProfileTypes.global }, timeoutMs: timeoutMs);
            }

            public int CapacityRemaining() => _rig.theRadio?.PanadaptersRemaining ?? -1;

            public int LegacyFreshTarget() => _rig.initialFreeSlices;

            public void Dispatch(string name, Action work) => _rig.DispatchStationWork(name, work);

            public void SendGlobalLoad(string name, Action<string> onRejected)
            {
                var radio = _rig.theRadio;
                if (radio == null) { onRejected?.Invoke("no radio"); return; }
                // The FlexLib setter sends for every non-empty name, equal or
                // not, and raises its own notification synchronously — which
                // the provenance scope marks as a local echo. It offers no
                // reply to its caller, so a rejection cannot be observed here.
                using (OwnProfileWrite())
                {
                    radio.ProfileGlobalSelection = name;
                }
            }

            public void RequestPanafall() => _rig.theRadio?.RequestPanafall();

            public void Trace(string line, bool isError = false) =>
                Tracing.TraceLine(line, isError ? TraceLevel.Error : TraceLevel.Info);
        }

        private StationPolicyFacts ReadStationPolicyFacts()
        {
            var radio = theRadio;
            var serial = radio?.Serial ?? "";
            var facts = new StationPolicyFacts
            {
                Connected = radio != null && IsConnected,
                HoldArmed = ChangeNothingActive,
                Serial = serial,
            };
            if (!string.IsNullOrEmpty(serial))
            {
                facts.Ownership = RadioConfig.OwnershipOf(serial);
                facts.Intent = RadioConfig.ProfileIntentOf(serial);
                var wanted = WantedProfilesForThisRadio();
                facts.WantedGlobal = wanted.TryGetValue(ProfileTypes.global, out var g) ? (g ?? "") : "";
            }
            return facts;
        }

        /// <summary>
        /// Run command-path work without ever waiting on a queue only the
        /// current thread can drain (design section 4). Before the command
        /// loop is up it runs inline, as the startup queue always did; on the
        /// command thread itself (the post-import entry) it runs inline; from
        /// any other thread while the loop runs, it is queued.
        /// </summary>
        private void DispatchStationWork(string name, Action work)
        {
            if (work == null) return;
            if (q == null || !q.MainLoop || Thread.CurrentThread == mainThread)
            {
                Tracing.TraceLine("StationConnect: dispatch inline — " + name, TraceLevel.Info);
                work();
                return;
            }
            q.Enqueue((FunctionDel)(() => work()), name);
        }

        /// <summary>Blocks the coordinator between checks; woken by any
        /// observation or by cancellation.</summary>
        private sealed class EventStationWaiter : IStationWaiter, IDisposable
        {
            private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);
            private readonly ConnectionAttempt _attempt;
            public EventStationWaiter(ConnectionAttempt attempt)
            {
                _attempt = attempt;
                _attempt.Wake += OnWake;
            }
            private void OnWake() => _wake.Set();
            public void Wait(int maxMs)
            {
                if (maxMs <= 0) return;
                _wake.Wait(maxMs);
                _wake.Reset();
            }
            public void Dispose()
            {
                _attempt.Wake -= OnWake;
                _wake.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // The connect: station first, then read, then decide
        // ------------------------------------------------------------------

        /// <summary>True once the session records were initialised for this attempt.</summary>
        private int _stewardshipSessionAttempt = -1;
        private bool _stewardshipPreAnswered;

        /// <summary>
        /// Initialise the stewardship session ONCE per connection attempt:
        /// clear the records, run the #495 pre-answer migration. Later phases
        /// call the planner without resetting anything. Calling the old
        /// whole-connect routine twice cleared records and could plan a
        /// second global load; this is the smaller correct seam.
        /// </summary>
        private void InitializeStewardshipSession()
        {
            int gen = AttemptGen;
            if (_stewardshipSessionAttempt == gen) return;
            _stewardshipSessionAttempt = gen;

            lock (_profileRecordLock) _profileSessionRecord.Clear();
            StrandedProfileRestorePoints = Array.Empty<ProfileTypes>();
            _autosaveTurnedOffThisSession = false;
            _liveTxSnapshot = null;
            _liveTxSnapshotChainGeneration = -1;
            _liveTxSnapshotAttempt = -1;
            _pendingLiveTxApplyPreset = null;

            _stewardshipPreAnswered = MigrateProfileIntentForKnownRadio();
        }

        /// <summary>
        /// The station-first connect. Replaces the body of GetProfileInfo:
        /// establish the station under the coordinator, then read transmit and
        /// microphone facts FRESH and decide them, then the live transmit-audio
        /// path, then the silent-microphone assessment last. Returns the
        /// honest outcome; nothing here ever calls setupFromScratch.
        /// </summary>
        internal StationResult EstablishStationOnConnect(bool postImport)
        {
            Tracing.TraceLine("EstablishStationOnConnect:" + postImport, TraceLevel.Info);
            var attempt = StationAttempt;

            // One OPERATION per entry. The connect is operation 1; the
            // post-import re-entry is a later one on the same connection, so
            // anything the earlier operation queued refuses when it runs, and
            // the earlier operation's result travels along as "previous" so a
            // load it sent and never confirmed is still a barrier here. The
            // connection-level records and obligations are untouched.
            var previous = postImport ? LastStationResult : null;
            var operation = attempt.BeginOperation(postImport ? "post-import station re-establishment" : "station establishment on connect");

            // The hold announcement path: GuardSkips traces the skip once, and
            // the coordinator refuses on the same fact. The trace is the
            // operator-facing record that a protection was active.
            GuardSkips("default profile selection on connect (global, tx, mic)");

            if (!postImport) InitializeStewardshipSession();
            else Tracing.TraceLine("EstablishStationOnConnect: post-import entry as " + operation
                + " — records and obligations retained; previous result: " + (previous?.ToString() ?? "none"), TraceLevel.Info);

            // Capture the RX/TX slices by identity before anything can add
            // slices ahead of them (QB Track J).
            Slice oldRXSlice = VFOToSlice(RXVFO);
            Slice oldTXSlice = VFOToSlice(TXVFO);

            StationResult result;
            using (var waiter = new EventStationWaiter(attempt))
            {
                var coordinator = new StationCoordinator(
                    new FlexStationPort(this), RosterTracker, StationTracker, ProfileEvidence,
                    StationPolicies.Current, _stationClock, operation, StationDeadlines.Default(), waiter, previous);
                result = coordinator.Run();
            }
            LastStationResult = result;

            if (result.CreationArmed && !string.IsNullOrEmpty(result.PendingCreateName))
            {
                _pendingGlobalCreation = new PendingGlobalCreation(
                    result.PendingCreateName, theRadio?.Serial ?? "", attempt.Generation,
                    "the wanted global was absent from the radio's reported inventory at connect");
                Tracing.TraceLine("StationConnect: armed " + _pendingGlobalCreation
                    + " — created at clean disconnect only if every step-11 condition still holds", TraceLevel.Info);
            }

            // Restore RX/TX by identity after any allocation.
            if (oldRXSlice != null)
            {
                _RXVFO = SliceToVFO(oldRXSlice);
                oldRXSlice.Active = true;
            }
            if (oldTXSlice != null)
            {
                _TXVFO = SliceToVFO(oldTXSlice);
                oldTXSlice.IsTransmitSlice = true;
            }

            if (theRadio != null && operation.IsLive)
            {
                RunPostStationPhase(result, operation);
            }

            Tracing.TraceLine(
                "GetProfileInfo:radio profile autosave="
                + (ProfileEvidence.Snapshot().RadioReportedAutosave?.ToString() ?? "not reported")
                + ", global selection=" + (theRadio?.ProfileGlobalSelection ?? "none")
                + ", station outcome=" + result.Outcome, TraceLevel.Info);

            if (theRadio != null) _TotalNumSlices = theRadio.SliceList.Count;

            if (postImport && theRadio != null)
            {
                bool ok = result.StationEstablished;
                Tracing.TraceLine("flex import operation complete:" + ok + " (" + result.Outcome + ")", TraceLevel.Info);
                PCAudio = wasPCAudio;
                if (theRadio.ActiveSlice != null)
                {
                    FilterObj.RXFreqChange(theRadio.ActiveSlice);
                }
                raisePowerEvent(true);
                try { if (System.IO.Directory.Exists(importDir)) System.IO.Directory.Delete(importDir, true); }
                catch (Exception ex) { Tracing.TraceLine("post-import cleanup: " + ex.Message, TraceLevel.Warning); }
                string msg = ok ? importedMsg : importFailMsg;
                System.Windows.Forms.MessageBox.Show(msg, statusHdr, System.Windows.Forms.MessageBoxButtons.OK);
            }
            return result;
        }

        /// <summary>
        /// Steps 8 to 10: after station establishment, discard every pre-load
        /// transmit and microphone reading, read them fresh, plan and execute
        /// only what the ruling permits, run the live transmit-audio path for
        /// UseMyTransmitAudio, then the silent-microphone assessment last.
        /// </summary>
        private void RunPostStationPhase(StationResult station, StationOperation operation)
        {
            var attempt = operation.Attempt;
            var phase = StationDeadline.In(_stationClock, StationDeadlines.Default().PostStationPhaseMs);
            var facts = ReadStationPolicyFacts();
            var rosterAuto = RosterJudgementForAutomaticWrite();
            var rosterLive = RosterJudgementNow();

            Tracing.TraceLine("StationConnect: post-station phase — station " + station.Outcome
                + ", established=" + station.StationEstablished + ", facts " + facts
                + ", roster(auto) " + rosterAuto + ", roster(live) " + rosterLive, TraceLevel.Info);

            // Fresh transmit and microphone facts. The global type comes from
            // the session's held evidence, not a second ask: no second global
            // decision is made here.
            int readMs = Math.Min(StationDeadlines.Default().ProfileReadMs, phase.RemainingMs(_stationClock));
            var situation = ReadProfileSituation(
                WantedProfilesForThisRadio(), freshAsk: true,
                freshTypes: ProfileStewardship.TransmitAudioTypes, timeoutMs: readMs);
            if (situation == null || !operation.IsLive) return;
            situation.StationPresent = StationTracker.Snapshot().StationPresent;

            bool mayWriteShared = ProfileStewardship.MayWriteSharedStateAutomatically(situation)
                                  && station.StationEstablished
                                  && rosterAuto.Verdict == RosterVerdict.OnlyUs;
            bool liveAudioRoute = situation.Intent == ProfileGuestIntent.UseMyTransmitAudio
                                  && !situation.ChangeNothingArmed
                                  && station.StationEstablished
                                  && rosterLive.Verdict == RosterVerdict.OnlyUs;

            // The planner's OnlyStation input is the verdict the ROUTE needs:
            // the live membership verdict for the preserved guest live-audio
            // path (the brief keeps that path unchanged), the authority-policy
            // verdict for owner writes. Unknown is passed as Unknown so the
            // planner's refusal names uncertainty, not a person.
            var routeVerdict = situation.Intent == ProfileGuestIntent.UseMyTransmitAudio ? rosterLive : rosterAuto;
            situation.OnlyStation = routeVerdict.Verdict == RosterVerdict.OnlyUs;
            situation.OnlyStationUnknown = routeVerdict.Verdict == RosterVerdict.Unknown;

            var plan = ProfileStewardship.PlanConnectRuled(situation, ProfileStewardship.TransmitAudioTypes);
            StrandedProfileRestorePoints = ProfileStewardship.StrandedRestorePoints(situation).ToArray();

            foreach (var skip in plan.Skips)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: left the " + ProfileStewardship.Label(skip.ProfileType)
                    + " " + (skip.ProfileType == ProfileTypes.none ? "" : "profile ")
                    + "alone — " + skip.Reason
                    + (string.IsNullOrEmpty(skip.ProfileName) ? "" : " (would have used '" + skip.ProfileName + "')"),
                    TraceLevel.Info);
            }

            if (!station.StationEstablished)
            {
                Tracing.TraceLine("StationConnect: no established station (" + station.Outcome
                    + "); transmit and microphone actions are NOT run. Read-only assessment continues.",
                    TraceLevel.Warning);
                NoteStationOutcome(station);
                AnnounceConnectStewardship(situation, plan, _stewardshipPreAnswered, liveAudioApplied: false);
                CheckMicProfileForSilentTx(mayRepair: false);
                return;
            }

            bool liveAudioApplied = false;
            var executed = new List<ProfileAction>();
            bool abort = false;
            foreach (var action in plan.Actions)
            {
                if (abort || !operation.IsLive || phase.Passed(_stationClock)) break;

                bool isLoad = action.Kind == ProfileActionKind.LoadOurs;
                if (isLoad && !mayWriteShared)
                {
                    Tracing.TraceLine("ProfileStewardship: NOT sending " + action
                        + " — automatic shared writes are not authorised on this connection", TraceLevel.Info);
                    continue;
                }
                if (!isLoad && !liveAudioRoute)
                {
                    Tracing.TraceLine("ProfileStewardship: NOT running " + action
                        + " — the live transmit-audio route is not open on this connection", TraceLevel.Info);
                    continue;
                }

                var outcome = RunProfileActionChecked(action, operation, phase);
                switch (action.Kind)
                {
                    case ProfileActionKind.TurnAutosaveOff:
                        if (outcome == ProfileActionOutcome.Confirmed) _autosaveTurnedOffThisSession = true;
                        else
                        {
                            Tracing.TraceLine(
                                "ProfileStewardship: the radio did not CONFIRM autosave off from its own status, so "
                                + "the operator's transmit audio was NOT applied — a live change under autosave could "
                                + "land in the owner's profile.", TraceLevel.Error);
                            abort = true;
                        }
                        break;
                    case ProfileActionKind.CaptureLiveTransmitAudio:
                        if (outcome != ProfileActionOutcome.Confirmed)
                        {
                            Tracing.TraceLine(
                                "ProfileStewardship: could not capture the radio's live transmit audio within its "
                                + "bound, so nothing was applied — expiry prevents application rather than accepting defaults.",
                                TraceLevel.Error);
                            abort = true;
                        }
                        break;
                    case ProfileActionKind.ApplyLocalTransmitAudio:
                        if (outcome == ProfileActionOutcome.Confirmed) liveAudioApplied = true;
                        break;
                }
                if (outcome == ProfileActionOutcome.Sent || outcome == ProfileActionOutcome.Confirmed
                    || outcome == ProfileActionOutcome.Deferred)
                {
                    executed.Add(action);
                }
            }

            if (abort)
            {
                if (_autosaveTurnedOffThisSession) RestoreRadioAutosaveAfterAbort();
                lock (_profileRecordLock) _profileSessionRecord.Clear();
                _pendingLiveTxApplyPreset = null;
            }
            else
            {
                // Record from what was actually SENT, not from membership in
                // plan.Actions. A sent-but-unconfirmed selection is still owed
                // a put-back: the command may have acted.
                lock (_profileRecordLock)
                {
                    foreach (var rec in plan.Record)
                    {
                        bool owed = rec.LiveTransmitAudio
                            ? _liveTxSnapshot != null && executed.Any(a => a.Kind == ProfileActionKind.ApplyLocalTransmitAudio)
                            : executed.Any(a => a.ProfileType == rec.ProfileType && a.Kind == ProfileActionKind.LoadOurs);
                        if (owed) _profileSessionRecord.Add(rec);
                    }
                }
            }

            NoteStationOutcome(station);
            AnnounceConnectStewardship(situation, plan, _stewardshipPreAnswered, liveAudioApplied);

            // Step 10, last: the assessment. Repair only under the same
            // authority as every other automatic shared write.
            CheckMicProfileForSilentTx(mayRepair: mayWriteShared);
        }

        /// <summary>Tell the operator, once, when the station could not be
        /// confirmed or failed — retained resources, no default fill, retry
        /// through the connection UI. Healthy outcomes stay quiet.</summary>
        private void NoteStationOutcome(StationResult station)
        {
            if (SuppressSpeech || station == null) return;
            string full = null;
            string brief = null;
            var slices = ("slices", station.OwnSlicesAtEnd.ToString());
            switch (station.Outcome)
            {
                case StationOutcome.Unconfirmed:
                    full = Lexicon.Get("settings.profile_station.unconfirmed", slices);
                    brief = Lexicon.Get("settings.profile_station.unconfirmed_brief");
                    break;
                case StationOutcome.Failed:
                    full = Lexicon.Get("settings.profile_station.failed", slices);
                    brief = Lexicon.Get("settings.profile_station.failed_brief");
                    break;
                case StationOutcome.PolicySkipped:
                    // An OWNER who expected a restore and did not get one is
                    // told why, in the roster's own terms — and uncertainty is
                    // named as uncertainty, never as a person. A radio the
                    // policy never considered (unanswered, guest, held) has
                    // its own sentences elsewhere and stays quiet here.
                    if (station.RosterAtDecision != null)
                    {
                        string why;
                        switch (station.RosterAtDecision.Verdict)
                        {
                            case RosterVerdict.OthersPresent:
                                why = Lexicon.Get("settings.profile_guest.why.another_operator"); break;
                            case RosterVerdict.Unknown:
                                why = Lexicon.Get("settings.profile_guest.why.roster_unknown"); break;
                            default:
                                why = Lexicon.Get("settings.profile_station.why.not_chosen"); break;
                        }
                        full = Lexicon.Get("settings.profile_station.not_restored", ("why", why), slices);
                        brief = Lexicon.Get("settings.profile_station.not_restored_brief");
                    }
                    break;
            }

            if (full != null)
            {
                ConnectBriefing.Current.Note(new ConnectFact(
                    ConnectFactKind.ProfileStewardship, full, brief,
                    VerbosityLevel.Terse, Speech.SpeechSubject.ProfileStationOutcome, alarm: false));
            }

            // No station at all, and the reason is the open bench question:
            // say so, once, or an operator with silence on every slice key
            // concludes the application is broken.
            if (!station.StationEstablished && station.OwnSlicesAtEnd == 0
                && station.Allocation.Stop == AllocationStop.MaterializationUnknown)
            {
                ConnectBriefing.Current.Note(new ConnectFact(
                    ConnectFactKind.ProfileStewardship,
                    Lexicon.Get("settings.profile_station.no_slices"),
                    Lexicon.Get("settings.profile_station.no_slices_brief"),
                    VerbosityLevel.Critical, Speech.SpeechSubject.ProfileStationSlices, alarm: false));
            }
        }

        // ------------------------------------------------------------------
        // Executing one action with the recheck inside the dispatched work
        // ------------------------------------------------------------------

        /// <summary>What actually happened to one planned step.</summary>
        internal enum ProfileActionOutcome
        {
            /// <summary>The recheck inside the dispatched delegate refused it.</summary>
            Refused,
            /// <summary>Queued to a loop that has not run it yet.</summary>
            Queued,
            /// <summary>The command went out; the effect is not independently observed.</summary>
            Sent,
            /// <summary>The effect was confirmed by radio-origin evidence or readback.</summary>
            Confirmed,
            /// <summary>Deferred to the command loop (the live-audio apply).</summary>
            Deferred,
            Failed,
        }

        /// <summary>
        /// Run a planned action with every permission re-read INSIDE the
        /// dispatched delegate, not before it. A guard around the caller
        /// misses queue delay and later callbacks (design section 4).
        /// </summary>
        private ProfileActionOutcome RunProfileActionChecked(ProfileAction action, StationOperation operation, StationDeadline phase)
        {
            var attempt = operation.Attempt;
            var radio = theRadio;
            if (radio == null || action == null) return ProfileActionOutcome.Failed;

            Tracing.TraceLine(
                "ProfileStewardship: " + action.Kind + " " + ProfileStewardship.Label(action.ProfileType)
                + " '" + action.ProfileName + "' — " + action.Because, TraceLevel.Info);

            switch (action.Kind)
            {
                case ProfileActionKind.LoadOurs:
                    return DispatchSelectionChecked(radio, action, operation, phase);

                case ProfileActionKind.TurnAutosaveOff:
                    return SetRadioProfileAutosaveGuest(false) ? ProfileActionOutcome.Confirmed : ProfileActionOutcome.Failed;

                case ProfileActionKind.CaptureLiveTransmitAudio:
                    return CaptureLiveTransmitAudio() ? ProfileActionOutcome.Confirmed : ProfileActionOutcome.Failed;

                case ProfileActionKind.ApplyLocalTransmitAudio:
                    if (q != null && !q.MainLoop)
                    {
                        _pendingLiveTxApplyPreset = action.ProfileName;
                        _pendingLiveTxApplyAttempt = attempt.Generation;
                        Tracing.TraceLine(
                            "ProfileStewardship: deferring the live transmit-audio apply until the command loop is running; "
                            + "permission and the captured chain generation are revalidated there.", TraceLevel.Info);
                        return ProfileActionOutcome.Deferred;
                    }
                    return ApplyLocalTransmitAudioNow(action.ProfileName) ? ProfileActionOutcome.Confirmed : ProfileActionOutcome.Failed;

                default:
                    return RunProfileAction(action) ? ProfileActionOutcome.Sent : ProfileActionOutcome.Failed;
            }
        }

        private ProfileActionOutcome DispatchSelectionChecked(Radio radio, ProfileAction action, StationOperation operation, StationDeadline phase)
        {
            var factsAtPlan = ReadStationPolicyFacts();
            bool sent = false;
            string refusal = null;
            DispatchStationWork("stewardship " + ProfileStewardship.Label(action.ProfileType) + " selection", () =>
            {
                if (operation.IsEnded) { refusal = "operation ended: " + operation.WhyNotLive; return; }
                if (phase.Passed(_stationClock)) { refusal = "the queued selection ran after the post-station phase had ended"; return; }
                var now = ReadStationPolicyFacts();
                if (!now.SameAutomaticPermissionAs(factsAtPlan) || now.HoldArmed
                    || now.Ownership != RadioOwnership.Mine || now.Intent != ProfileGuestIntent.LoadMineAndPutBack)
                {
                    refusal = "policy changed before dispatch (" + now + ")";
                    return;
                }
                var roster = RosterJudgementForAutomaticWrite();
                if (roster.Verdict != RosterVerdict.OnlyUs) { refusal = "roster at dispatch: " + roster; return; }

                using (OwnProfileWrite())
                {
                    switch (action.ProfileType)
                    {
                        case ProfileTypes.tx:
                            if (!radio.ProfileTXList.Contains(action.ProfileName))
                            {
                                if (!action.MayCreate) { refusal = "transmit profile absent and creation not permitted"; return; }
                                radio.CreateTXProfile(action.ProfileName);
                            }
                            radio.ProfileTXSelection = action.ProfileName;
                            break;
                        case ProfileTypes.mic:
                            if (!radio.ProfileMICList.Contains(action.ProfileName))
                            {
                                if (!action.MayCreate) { refusal = "microphone profile absent and creation not permitted"; return; }
                                radio.CreateMICProfile(action.ProfileName);
                            }
                            radio.ProfileMICSelection = action.ProfileName;
                            break;
                        default:
                            refusal = "the global type is decided by the station coordinator, never here";
                            return;
                    }
                }
                sent = true;
            });

            if (!sent && refusal == null)
            {
                // Queued to the command loop from another thread: report only
                // after the delegate really ran, within the phase.
                var bound = phase.Clip(_stationClock, 3000);
                await(() => sent || refusal != null, bound.RemainingMs(_stationClock));
            }
            if (refusal != null)
            {
                Tracing.TraceLine("ProfileStewardship: " + action + " NOT sent — " + refusal, TraceLevel.Warning);
                return ProfileActionOutcome.Refused;
            }
            return sent ? ProfileActionOutcome.Sent : ProfileActionOutcome.Queued;
        }

        // ------------------------------------------------------------------
        // Generic connect-time radio-persistent writes (design step 8)
        // ------------------------------------------------------------------

        /// <summary>
        /// The startup chain's radio-persistent writes — MicInput, VOX,
        /// CW break-in, TX1, the CW keyer restore — are the OWNER's to make.
        /// On any other connection they would overwrite the owner's chain
        /// outside anything the live-audio snapshot captures, so they are
        /// suppressed rather than merely moved (design step 8; ruling #590).
        /// True when the write is skipped, and it traces why.
        /// </summary>
        private bool OwnerOnlyWriteSkips(string what)
        {
            if (GuardSkips(what)) return true;
            var serial = theRadio?.Serial;
            var ownership = string.IsNullOrEmpty(serial) ? RadioOwnership.Unset : RadioConfig.OwnershipOf(serial);
            if (ownership == RadioOwnership.Mine) return false;
            Tracing.TraceLine("StationConnect: skipped '" + what + "' — this radio is not declared ours ("
                + ownership + "); a guest writes nothing shared (#590)", TraceLevel.Info);
            return true;
        }

        // ------------------------------------------------------------------
        // The deferred disconnect-time create (#578, design step 11)
        // ------------------------------------------------------------------

        /// <summary>
        /// Create the previously missing global, at clean disconnect, only if
        /// every step-11 condition still holds — revalidated here, not
        /// remembered from connect — and confirm the save by radio-reported
        /// inventory readback within a bound, else report "unconfirmed, not
        /// saved". Never overwrites an intervening profile of the name; never
        /// loads what it created.
        /// </summary>
        private bool CreatePendingGlobalAtDisconnect()
        {
            var pending = _pendingGlobalCreation;
            if (pending == null) return false;
            var radio = theRadio;
            if (radio == null)
            {
                Tracing.TraceLine("saveNewGlobalProfile: NOT creating " + pending + " — no radio", TraceLevel.Warning);
                _pendingGlobalCreation = null;
                return false;
            }
            var facts = ReadStationPolicyFacts();
            var decision = DeferredGlobalCreation.Decide(new CreationFacts
            {
                Pending = pending,
                Attempt = StationAttempt,
                Connected = facts.Connected,
                HoldArmed = facts.HoldArmed,
                Ownership = facts.Ownership,
                Intent = facts.Intent,
                WantedGlobalNow = facts.WantedGlobal,
                Serial = facts.Serial,
                Roster = RosterJudgementForAutomaticWrite(),
                Inventory = ProfileEvidence.Snapshot().GlobalList,
                StationOutcome = LastStationResult?.Outcome ?? StationOutcome.Unconfirmed,
                LoadOutstanding = LastStationResult?.LoadOutstanding ?? true,
            });

            if (!decision.Create)
            {
                Tracing.TraceLine("saveNewGlobalProfile: NOT creating " + pending + " — " + decision.Reason, TraceLevel.Warning);
                _pendingGlobalCreation = null;
                return false;
            }

            Tracing.TraceLine("saveNewGlobalProfile: creating " + pending + " — " + decision.Reason, TraceLevel.Info);
            long seq = ProfileEvidence.Sequence;
            bool commandOut = false;
            DispatchStationWork("save new global '" + pending.Name + "'", () =>
            {
                using (OwnProfileWrite()) radio.SaveGlobalProfile(pending.Name);
                commandOut = true;
            });
            int bound = StationDeadlines.Default().DisconnectCreateConfirmMs;
            bool confirmed = await(() =>
            {
                if (!commandOut) return false;
                var inv = ProfileEvidence.Snapshot().GlobalList;
                return inv != null && inv.Provenance == ObservationProvenance.RadioReported
                       && inv.Sequence > seq && inv.Contains(pending.Name);
            }, bound);

            if (confirmed)
            {
                Tracing.TraceLine("saveNewGlobalProfile: the radio's inventory now lists '" + pending.Name + "'", TraceLevel.Info);
            }
            else
            {
                Tracing.TraceLine("saveNewGlobalProfile: the save command " + (commandOut ? "went out" : "did not go out")
                    + " but the radio did not report '" + pending.Name + "' within " + bound
                    + " ms — UNCONFIRMED, not claimed saved", TraceLevel.Warning);
            }
            _pendingGlobalCreation = null;
            return confirmed;
        }
    }
}
