using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Radios.StationConnect
{
    /// <summary>What actually happened to one planned step.</summary>
    public enum ProfileActionOutcome
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

    /// <summary>How the post-station phase ended.</summary>
    public enum PostStationOutcome
    {
        /// <summary>Every permitted step ran and every sent selection was confirmed.</summary>
        Completed,
        /// <summary>No established station: nothing dependent was run; the
        /// assessment was read-only.</summary>
        NotEstablished,
        /// <summary>A step was refused or failed; nothing after it ran.</summary>
        Stopped,
        /// <summary>A sent selection was not confirmed within its bound, or
        /// the phase ended; nothing after it ran and the repair is withheld.</summary>
        Unconfirmed,
        Cancelled,
    }

    /// <summary>The result of one post-station run: what was read, planned,
    /// sent, confirmed, and what the assessment may do.</summary>
    public sealed class PostStationResult
    {
        public PostStationOutcome Outcome = PostStationOutcome.Unconfirmed;
        public string Reason = "";

        /// <summary>The situation as read for this run: connection facts,
        /// plus each type's state as it was read at ITS turn.</summary>
        public ProfileSituation Situation;

        /// <summary>The plans of every type phase, merged for the
        /// announcement. Never used to decide anything after the fact.</summary>
        public ProfilePlan Plan = new ProfilePlan();

        /// <summary>Types whose selection was sent and confirmed by a
        /// radio-reported selection after the send.</summary>
        public List<ProfileTypes> ConfirmedSelections = new List<ProfileTypes>();

        public bool OwnerInitialisationRan;
        public bool LiveAudioApplied;
        public bool LiveAudioDeferred;
        public bool LiveAudioAborted;

        /// <summary>The repair the final assessment may make: only when every
        /// automatic write was permitted and nothing ended uncertain.</summary>
        public bool MayRepairMicrophone;

        public override string ToString() =>
            Outcome + (string.IsNullOrEmpty(Reason) ? "" : " — " + Reason)
            + " confirmed=[" + string.Join(",", ConfirmedSelections) + "]"
            + (OwnerInitialisationRan ? " owner-init" : "")
            + (LiveAudioApplied ? " live-audio-applied" : LiveAudioDeferred ? " live-audio-deferred" : LiveAudioAborted ? " live-audio-aborted" : "")
            + (MayRepairMicrophone ? " may-repair" : " no-repair");
    }

    /// <summary>
    /// What the post-station orchestration needs from the radio side, and
    /// nothing else. The production implementation is a thin translation
    /// onto FlexLib inside FlexBase; a test's fake holds a radio's profile
    /// state and can change it in response to a send, which is how the
    /// tx-changes-mic dependency is exercised without a radio.
    /// </summary>
    public interface IPostStationPort
    {
        StationPolicyFacts ReadPolicyFacts();

        /// <summary>The verdict the OWNER'S automatic shared write must obtain.</summary>
        RosterJudgement RosterForAutomaticWrite();

        /// <summary>The verdict a GUEST'S shared write (the live-audio route)
        /// must obtain: a separate authority, Unknown in production until a
        /// bench establishes it (see <see cref="StationPolicies.GuestSharedWriteAuthority"/>).</summary>
        RosterJudgement RosterForGuestSharedWrite();

        /// <summary>The connection-level facts of a situation with NO type
        /// states: connected, hold, ownership, intent, reported autosave, the
        /// local transmit-audio choice and stranded snapshot, station present.</summary>
        ProfileSituation ReadBaseSituation();

        /// <summary>ONE fresh, bounded, radio-origin read of one type.</summary>
        ProfileTypeState ReadType(ProfileTypes type, int timeoutMs);

        /// <summary>
        /// Dispatch a selection. The delegate MUST call
        /// <paramref name="refusalAtSend"/> immediately before writing and
        /// refuse on a non-null answer; it adds its own radio-side checks.
        /// Returns Refused, Queued (the loop has not run it inside the bound)
        /// or Sent. The radio's reply to the load command is delivered to
        /// <paramref name="onReply"/> when it arrives; the production port
        /// sends through FlexLib's reply-bearing path, never the setter, so
        /// the reply is real and the vendor cache is not pre-assigned (see
        /// <see cref="CommandReply"/>).
        /// </summary>
        ProfileActionOutcome SendSelection(ProfileAction action, Func<string> refusalAtSend, Action<CommandReply> onReply);

        /// <summary>The latest RADIO-REPORTED selection for a type, or null.</summary>
        SelectionObservation LatestReportedSelection(ProfileTypes type);

        /// <summary>The profile-evidence sequence now, for "after the send".</summary>
        long ProfileSequence { get; }

        /// <summary>The owner's generic initialisation: TNF, MicInput, VOX,
        /// CW break-in, TX1, the keyer restore. Called only under the full
        /// gate; the delegate MUST call <paramref name="refusalAtWrite"/>
        /// immediately before writing and refuse on a non-null answer.
        /// Returns Confirmed (written), Refused, or Queued.</summary>
        ProfileActionOutcome RunOwnerInitialization(Func<string> refusalAtWrite);

        /// <summary>One live-audio step (autosave off, capture, apply). The
        /// delegate MUST call <paramref name="refusalAtSend"/> immediately
        /// before the step's write and refuse on a non-null answer (Track
        /// G3: until then autosave and capture ran without a dispatched
        /// recheck).</summary>
        ProfileActionOutcome RunLiveAudioAction(ProfileAction action, Func<string> refusalAtSend);

        /// <summary>The live-audio sequence aborted on a safety step.</summary>
        void AbortLiveAudio(bool autosaveWasTurnedOff);

        /// <summary>Session records for what was actually done.</summary>
        void RecordSession(IEnumerable<ProfileSessionRecord> records);

        /// <summary>The LAST call of the phase: say what happened, then the
        /// silent-microphone assessment, repairing only when permitted and
        /// only after any deferred work has completed or ended uncertain.</summary>
        void Conclude(PostStationResult result);

        void Trace(string line, bool warn = false);
    }

    /// <summary>
    /// Steps 8 to 10 of the station-first design, in their real order:
    /// owner initialisation under the full gate; then transmit — read fresh,
    /// plan, send, CONFIRM; then microphone, read fresh only after the
    /// transmit effect is confirmed; then the live transmit-audio path; then
    /// the assessment, last. Stops on Refused, Failed or Unconfirmed and runs
    /// nothing after the stop.
    /// </summary>
    /// <remarks>
    /// <para>Until Track G2, RunPostStationPhase read both types together,
    /// built one plan, reported every selection as Sent with no
    /// applied-state boundary, kept looping past a refusal, ran the owner's
    /// MicInput/VOX/CWBreakIn/TX1/keyer writes after the whole phase and its
    /// assessment (even after an Unconfirmed station), and its only test was
    /// a source-order pin (review section 1 step 8; section 6). This is the
    /// algorithm behind a port narrow enough for a fake to drive with the
    /// fake clock, so the ordering is a behavioural test.</para>
    /// <para>A selection is CONFIRMED by two independent things: the radio's
    /// REPLY to the load command (code 0), and a radio-reported selection
    /// for that type equal to the wanted name — either reported after the
    /// send, or already the radio's last report before it (the equal-value
    /// case, where the vendor sends no second status). A local echo of our
    /// setter is neither; the production port does not use the setter. A
    /// non-zero reply is a rejection and stops the phase. Whether the report
    /// also proves the profile's VALUES were applied is bench question C;
    /// until it answers, this is the strongest evidence available and it is
    /// named as a selection confirmation, not an applied-state one.</para>
    /// </remarks>
    public sealed class PostStationOrchestrator
    {
        private readonly IPostStationPort _port;
        private readonly IStationClock _clock;
        private readonly IStationWaiter _waiter;
        private readonly StationDeadlines _deadlines;

        public PostStationOrchestrator(IPostStationPort port, IStationClock clock, IStationWaiter waiter, StationDeadlines deadlines)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
            _clock = clock ?? MonotonicStationClock.Instance;
            _waiter = waiter ?? new NoWaiter();
            _deadlines = deadlines ?? StationDeadlines.Default();
        }

        public PostStationResult Run(StationResult station, StationOperation operation)
        {
            if (station == null) throw new ArgumentNullException(nameof(station));
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            var result = new PostStationResult();
            var phase = StationDeadline.In(_clock, _deadlines.PostStationPhaseMs);
            var factsAtPlan = _port.ReadPolicyFacts();
            var rosterAuto = _port.RosterForAutomaticWrite();
            var rosterGuest = _port.RosterForGuestSharedWrite();
            var baseSituation = _port.ReadBaseSituation() ?? new ProfileSituation { Connected = false };
            result.Situation = baseSituation;
            baseSituation.StationPresent = station.OwnSlicesAtEnd > 0 || baseSituation.StationPresent;
            baseSituation.OnlyStation = rosterAuto.Verdict == RosterVerdict.OnlyUs;
            baseSituation.OnlyStationUnknown = rosterAuto.Verdict == RosterVerdict.Unknown;

            _port.Trace("post-station phase — station " + station.Outcome + ", established=" + station.StationEstablished
                + ", facts " + factsAtPlan + ", roster(owner) " + rosterAuto + ", roster(guest) " + rosterGuest);

            if (!station.StationEstablished)
            {
                result.Outcome = PostStationOutcome.NotEstablished;
                result.Reason = "no established station (" + station.Outcome + "); transmit, microphone, owner initialisation and live audio are NOT run; the assessment is read-only";
                _port.Trace(result.Reason, warn: true);
                // The situation still carries what the radio holds for the
                // types, read fresh, so the announcement can name a stranded
                // restore point; nothing is planned or sent from it.
                foreach (var type in ProfileStewardship.TransmitAudioTypes)
                {
                    var st = _port.ReadType(type, ReadBudget(phase));
                    if (st != null) baseSituation.Types.Add(st);
                }
                result.Plan = ProfileStewardship.PlanConnectRuled(baseSituation, ProfileStewardship.TransmitAudioTypes);
                result.Plan.Actions.Clear();
                result.MayRepairMicrophone = false;
                _port.Conclude(result);
                return result;
            }

            // The gates. The OWNER'S automatic shared writes: connected, hold
            // off, Mine, opted in, and the owner's roster authority saying
            // only us. The GUEST'S live-audio route: the strict roster test
            // under the guest's SEPARATE authority (Track G3; Track G2 had
            // applied the owner's ruled relaxation to this route too), plus
            // the intent and the hold.
            bool mayWriteShared = ProfileStewardship.MayWriteSharedStateAutomatically(baseSituation)
                                  && rosterAuto.Verdict == RosterVerdict.OnlyUs;
            bool liveAudioRoute = baseSituation.Intent == ProfileGuestIntent.UseMyTransmitAudio
                                  && baseSituation.Connected && !baseSituation.ChangeNothingArmed
                                  && rosterGuest.Verdict == RosterVerdict.OnlyUs;

            // ── owner initialisation, before the final profile choices, with
            //    the recheck at its write ──
            if (mayWriteShared)
            {
                var init = _port.RunOwnerInitialization(() => OwnerInitRefusalAtWrite(operation, phase, factsAtPlan));
                if (init == ProfileActionOutcome.Confirmed)
                {
                    result.OwnerInitialisationRan = true;
                }
                else
                {
                    // A permission that changed before the first shared write
                    // has changed for every one after it.
                    result.Outcome = init == ProfileActionOutcome.Refused ? PostStationOutcome.Stopped : PostStationOutcome.Unconfirmed;
                    result.Reason = "owner initialisation was " + init + " at its write; nothing after it runs";
                    _port.Trace(result.Reason, warn: true);
                    result.MayRepairMicrophone = false;
                    _port.Conclude(result);
                    return result;
                }
            }
            else
            {
                _port.Trace("owner initialisation (TNF, MicInput, VOX, CW break-in, TX1, keyer) NOT run — "
                    + (baseSituation.ChangeNothingArmed ? "the hold is armed"
                        : baseSituation.Ownership != RadioOwnership.Mine ? "not the declared owner"
                        : baseSituation.Intent != ProfileGuestIntent.LoadMineAndPutBack ? "intent is " + baseSituation.Intent
                        : "roster: " + rosterAuto));
            }

            // ── transmit, then microphone: each read fresh after the previous confirmed effect ──
            var liveAudioActions = new List<ProfileAction>();
            bool stopped = false;
            foreach (var type in ProfileStewardship.TransmitAudioTypes)
            {
                if (operation.IsEnded)
                {
                    result.Outcome = PostStationOutcome.Cancelled;
                    result.Reason = "operation ended before the " + ProfileStewardship.Label(type) + " decision: " + operation.WhyNotLive;
                    stopped = true;
                    break;
                }
                if (phase.Passed(_clock))
                {
                    result.Outcome = PostStationOutcome.Unconfirmed;
                    result.Reason = "the post-station phase ended before the " + ProfileStewardship.Label(type) + " decision";
                    stopped = true;
                    break;
                }

                var state = _port.ReadType(type, ReadBudget(phase));
                var situation = CloneBase(baseSituation);
                if (state != null) situation.Types.Add(state);
                if (state != null) baseSituation.Types.Add(state);

                var plan = ProfileStewardship.PlanConnectRuled(situation, new[] { type });
                Merge(result.Plan, plan);

                foreach (var action in plan.Actions)
                {
                    if (action.Kind != ProfileActionKind.LoadOurs)
                    {
                        liveAudioActions.Add(action);
                        continue;
                    }
                    if (!mayWriteShared)
                    {
                        _port.Trace("NOT sending " + action + " — automatic shared writes are not authorised on this connection");
                        continue;
                    }

                    long seqBefore = _port.ProfileSequence;
                    var reportedBefore = _port.LatestReportedSelection(type);
                    CommandReply reply = null;
                    var send = _port.SendSelection(action, () => RefusalAtSend(operation, phase, factsAtPlan, type, action.ProfileName),
                        r => { if (r != null) { Volatile.Write(ref reply, r); operation.Signal(); } });
                    if (send != ProfileActionOutcome.Sent)
                    {
                        result.Outcome = send == ProfileActionOutcome.Refused || send == ProfileActionOutcome.Failed
                            ? PostStationOutcome.Stopped : PostStationOutcome.Unconfirmed;
                        result.Reason = ProfileStewardship.Label(type) + " selection '" + action.ProfileName + "' was " + send + "; nothing after it runs";
                        _port.Trace(result.Reason, warn: true);
                        stopped = true;
                        break;
                    }

                    var confirmation = WaitForSelectionConfirmed(type, action.ProfileName, seqBefore, reportedBefore, () => Volatile.Read(ref reply), phase, operation);
                    if (confirmation != null)
                    {
                        result.Outcome = operation.IsEnded ? PostStationOutcome.Cancelled
                            : confirmation.StartsWith("rejected", StringComparison.Ordinal) ? PostStationOutcome.Stopped
                            : PostStationOutcome.Unconfirmed;
                        result.Reason = ProfileStewardship.Label(type) + " selection '" + action.ProfileName
                            + "' was sent but " + confirmation + "; nothing after it runs and the assessment does not repair";
                        _port.Trace(result.Reason, warn: true);
                        stopped = true;
                        break;
                    }
                    result.ConfirmedSelections.Add(type);
                }
                if (stopped) break;
            }

            // ── the live transmit-audio path (design step 9's tx/mic half) ──
            var records = new List<ProfileSessionRecord>();
            if (!stopped && liveAudioActions.Count > 0)
            {
                if (!liveAudioRoute)
                {
                    foreach (var a in liveAudioActions)
                        _port.Trace("NOT running " + a + " — the live transmit-audio route is not open on this connection (guest roster authority: " + rosterGuest + ")");
                }
                else
                {
                    bool autosaveOff = false;
                    foreach (var action in liveAudioActions)
                    {
                        if (operation.IsEnded || phase.Passed(_clock)) { result.LiveAudioAborted = true; break; }
                        var outcome = _port.RunLiveAudioAction(action, () => LiveAudioRefusalAtSend(operation, phase, factsAtPlan));
                        switch (action.Kind)
                        {
                            case ProfileActionKind.TurnAutosaveOff:
                                if (outcome == ProfileActionOutcome.Confirmed) autosaveOff = true;
                                else
                                {
                                    _port.Trace("the radio did not CONFIRM autosave off from its own status, so the operator's transmit audio "
                                        + "was NOT applied — a live change under autosave could land in the owner's profile.", warn: true);
                                    result.LiveAudioAborted = true;
                                }
                                break;
                            case ProfileActionKind.CaptureLiveTransmitAudio:
                                if (outcome != ProfileActionOutcome.Confirmed)
                                {
                                    _port.Trace("could not capture the radio's live transmit audio within its bound, so nothing was applied "
                                        + "— expiry prevents application rather than accepting defaults.", warn: true);
                                    result.LiveAudioAborted = true;
                                }
                                break;
                            case ProfileActionKind.ApplyLocalTransmitAudio:
                                if (outcome == ProfileActionOutcome.Confirmed) result.LiveAudioApplied = true;
                                else if (outcome == ProfileActionOutcome.Deferred) result.LiveAudioDeferred = true;
                                else result.LiveAudioAborted = true;
                                break;
                        }
                        if (result.LiveAudioAborted) break;
                    }
                    if (result.LiveAudioAborted)
                    {
                        _port.AbortLiveAudio(autosaveOff);
                    }
                    else if (result.LiveAudioApplied)
                    {
                        // A CONFIRMED apply owes a put-back. A deferred one
                        // records itself when its own delegates have run.
                        records.AddRange(result.Plan.Record.Where(r => r.LiveTransmitAudio));
                    }
                }
            }

            // Records from what was CONFIRMED, never from membership in a plan.
            foreach (var rec in result.Plan.Record.Where(r => !r.LiveTransmitAudio))
            {
                if (result.ConfirmedSelections.Contains(rec.ProfileType)) records.Add(rec);
            }
            if (records.Count > 0) _port.RecordSession(records);

            if (!stopped)
            {
                result.Outcome = PostStationOutcome.Completed;
                result.Reason = "every permitted step ran and every sent selection was confirmed";
            }
            result.MayRepairMicrophone = mayWriteShared && result.Outcome == PostStationOutcome.Completed;
            _port.Conclude(result);
            return result;
        }

        /// <summary>The recheck inside a dispatched live-audio step (autosave
        /// off, capture, apply): the operation, the phase, and the route's
        /// own conditions read again — connected, no hold, the intent still
        /// transmit audio, and the strict roster.</summary>
        private string LiveAudioRefusalAtSend(StationOperation operation, StationDeadline phase, StationPolicyFacts factsAtPlan)
        {
            if (operation.IsEnded) return "operation ended: " + operation.WhyNotLive;
            if (phase.Passed(_clock)) return "the queued live-audio step ran after the post-station phase had ended";
            var now = _port.ReadPolicyFacts();
            if (!now.Connected) return "not connected";
            if (now.HoldArmed) return "the change-nothing hold is armed";
            if (now.Intent != ProfileGuestIntent.UseMyTransmitAudio) return "the intent for this radio is no longer transmit audio (" + now.Intent + ")";
            if (!string.Equals(now.Serial, factsAtPlan.Serial, StringComparison.Ordinal)) return "a different radio is connected";
            var roster = _port.RosterForGuestSharedWrite();
            if (roster.Verdict != RosterVerdict.OnlyUs) return "guest roster authority at dispatch: " + roster;
            return null;
        }

        /// <summary>The recheck inside the owner initialisation's dispatched
        /// write: the operation, the phase, the facts at planning, the
        /// stewardship refusal, and the owner's roster authority.</summary>
        private string OwnerInitRefusalAtWrite(StationOperation operation, StationDeadline phase, StationPolicyFacts factsAtPlan)
        {
            if (operation.IsEnded) return "operation ended: " + operation.WhyNotLive;
            if (phase.Passed(_clock)) return "the queued owner initialisation ran after the post-station phase had ended";
            var now = _port.ReadPolicyFacts();
            if (!now.SameAutomaticPermissionAs(factsAtPlan)) return "policy changed before the write (" + now + ")";
            string refusal = StationCoordinator.AutomaticStewardshipRefusal(now);
            if (refusal != null) return refusal;
            var roster = _port.RosterForAutomaticWrite();
            if (roster.Verdict != RosterVerdict.OnlyUs) return "roster at the write: " + roster;
            return null;
        }

        /// <summary>The recheck inside the dispatched selection: the
        /// operation, the phase, the facts at planning, the stewardship
        /// refusal, the strict roster verdict, and — for THIS type — the
        /// wanted name still being the one planned and no unsaved work
        /// reported since (Track G3; the restore-point check is the
        /// adapter's, against the radio's list at the write).</summary>
        private string RefusalAtSend(StationOperation operation, StationDeadline phase, StationPolicyFacts factsAtPlan, ProfileTypes type, string plannedName)
        {
            if (operation.IsEnded) return "operation ended: " + operation.WhyNotLive;
            if (phase.Passed(_clock)) return "the queued selection ran after the post-station phase had ended";
            var now = _port.ReadPolicyFacts();
            if (!now.SameAutomaticPermissionAs(factsAtPlan)) return "policy changed before dispatch (" + now + ")";
            if (!now.SameWantedFor(type, factsAtPlan) || !string.Equals(now.WantedFor(type), plannedName ?? "", StringComparison.Ordinal))
                return "the wanted " + ProfileStewardship.Label(type) + " profile changed before dispatch (now '" + now.WantedFor(type) + "', planned '" + plannedName + "')";
            if (now.UnsavedFor(type)) return "the radio reports unsaved " + ProfileStewardship.Label(type) + " work; loading ours would discard it";
            string refusal = StationCoordinator.AutomaticStewardshipRefusal(now);
            if (refusal != null) return refusal;
            var roster = _port.RosterForAutomaticWrite();
            if (roster.Verdict != RosterVerdict.OnlyUs) return "roster at dispatch: " + roster;
            return null;
        }

        /// <summary>
        /// Null when confirmed; else why not. Confirmation needs the radio's
        /// acknowledgment of the command AND a radio-reported selection equal
        /// to the name — reported after the send, or already the last report
        /// before it (equal value: the vendor sends nothing further). A
        /// rejection returns at once with the radio's text.
        /// </summary>
        private string WaitForSelectionConfirmed(
            ProfileTypes type, string name, long seqBefore, SelectionObservation reportedBefore,
            Func<CommandReply> reply, StationDeadline phase, StationOperation operation)
        {
            var bound = phase.Clip(_clock, _deadlines.TxMicEffectMs);
            bool alreadyReported = reportedBefore != null && reportedBefore.Provenance == ObservationProvenance.RadioReported
                                   && string.Equals(reportedBefore.Name, name, StringComparison.Ordinal);
            while (true)
            {
                var r = reply();
                if (r != null && !r.Acknowledged) return "rejected by the radio: " + r;
                var obs = _port.LatestReportedSelection(type);
                bool reportedAfter = obs != null && obs.Provenance == ObservationProvenance.RadioReported
                    && obs.Sequence > seqBefore && string.Equals(obs.Name, name, StringComparison.Ordinal);
                if (r != null && r.Acknowledged && (reportedAfter || alreadyReported)) return null;
                if (operation.IsEnded) return "the operation ended: " + operation.WhyNotLive;
                if (bound.Passed(_clock))
                {
                    if (r == null) return "the radio did not acknowledge the load within " + _deadlines.TxMicEffectMs + " ms";
                    return "the radio acknowledged the load but did not report it selected within " + _deadlines.TxMicEffectMs + " ms";
                }
                _waiter.Wait(Math.Min(25, bound.RemainingMs(_clock)));
            }
        }

        private int ReadBudget(StationDeadline phase) =>
            Math.Min(_deadlines.ProfileReadMs, phase.RemainingMs(_clock));

        private static ProfileSituation CloneBase(ProfileSituation b) => new ProfileSituation
        {
            Connected = b.Connected,
            Ownership = b.Ownership,
            Intent = b.Intent,
            ChangeNothingArmed = b.ChangeNothingArmed,
            OnlyStation = b.OnlyStation,
            OnlyStationUnknown = b.OnlyStationUnknown,
            RadioAutosave = b.RadioAutosave,
            LocalTransmitAudioProfile = b.LocalTransmitAudioProfile,
            LocalTransmitAudioProfileExists = b.LocalTransmitAudioProfileExists,
            StrandedLiveTransmitAudioSnapshot = b.StrandedLiveTransmitAudioSnapshot,
            StationPresent = b.StationPresent,
        };

        private static void Merge(ProfilePlan into, ProfilePlan from)
        {
            into.Actions.AddRange(from.Actions);
            into.Skips.AddRange(from.Skips);
            into.Record.AddRange(from.Record);
            foreach (var p in from.StrandedRestorePoints)
                if (!into.StrandedRestorePoints.Contains(p)) into.StrandedRestorePoints.Add(p);
            into.AskWhoseRadioThisIs |= from.AskWhoseRadioThisIs;
            if (from.Suggestion != ProfileGuestIntent.NotAnswered) into.Suggestion = from.Suggestion;
        }

        private sealed class NoWaiter : IStationWaiter
        {
            public void Wait(int maxMs) { }
        }
    }
}
