using System;

namespace Radios.StationConnect
{
    /// <summary>
    /// Everything a completion policy may consider about one sent global
    /// load. Assembled by the coordinator from the evidence logs; the policy
    /// never reaches past it to a radio object.
    /// </summary>
    public sealed class LoadEvidence
    {
        /// <summary>The name the load asked for.</summary>
        public string WantedName = "";

        /// <summary>The attempt the load belongs to.</summary>
        public int AttemptGeneration;

        /// <summary>Profile-evidence sequence at the moment the command was sent.</summary>
        public long SentAtProfileSequence;

        /// <summary>Own-station sequence at the moment the command was sent.</summary>
        public long SentAtStationSequence;

        /// <summary>The own station as it was when the load was armed, so a
        /// slice that was already there cannot masquerade as restored.</summary>
        public StationSnapshot StationBefore;

        /// <summary>The own station now.</summary>
        public StationSnapshot StationNow;

        /// <summary>Profile evidence now.</summary>
        public ProfileEvidenceSnapshot ProfileNow;

        /// <summary>True when the radio's command reply was an error.</summary>
        public bool Rejected;
        public string RejectionText = "";

        /// <summary>True when the command reply came back non-error. In this
        /// FlexLib the global setter offers no reply to its caller, so this is
        /// false unless a reply-capable path was used. Acceptance only, never
        /// completion.</summary>
        public bool Acknowledged;

        /// <summary>Monotonic milliseconds since the send.</summary>
        public long ElapsedSinceSendMs;

        /// <summary>
        /// True when, at the moment the load was sent, FlexLib's own
        /// connect-time <c>profile global info</c> might still have been
        /// unanswered (<see cref="ProfileEvidenceSnapshot.FlexLibConnectAnswerMayBeOutstanding"/>).
        /// Taken at the send and never revised: a list arriving later may be
        /// that answer, and nothing in a list says which ask it answers.
        /// Noel ruled, 2026-10-20 08:15, that such a load never confirms on
        /// any list (#639, repair round two).
        /// </summary>
        public bool FlexLibConnectAnswerMayBeOutstandingAtSend;

        /// <summary>Convenience: a radio-reported global selection equal to the
        /// wanted name observed AFTER the send.</summary>
        public bool RadioReportedSelectionMatchesAfterSend =>
            ProfileNow?.GlobalSelection != null
            && ProfileNow.GlobalSelection.Provenance == ObservationProvenance.RadioReported
            && ProfileNow.GlobalSelection.Sequence > SentAtProfileSequence
            && string.Equals(ProfileNow.GlobalSelection.Name, WantedName, StringComparison.Ordinal);

        /// <summary>Convenience: a radio-origin end boundary observed after the send.</summary>
        public bool RadioEndBoundaryAfterSend =>
            ProfileNow != null && ProfileNow.RadioEndBoundaryAtSequence > SentAtProfileSequence;

        /// <summary>Convenience: ANY radio-reported global profile list observed
        /// after the send, including the answer to an inventory ask of ours
        /// that was still outstanding at the send. <b>Not completion
        /// evidence on its own</b> (#639, Codex B1): our bounded fresh read
        /// can give up on cached names and let the load go out with its ask
        /// unanswered, and the late answer then looks exactly like this. See
        /// <see cref="UnsolicitedGlobalListAfterSend"/>.</summary>
        public bool RadioReportedGlobalListAfterSend =>
            ProfileNow?.GlobalList != null
            && ProfileNow.GlobalList.Provenance == ObservationProvenance.RadioReported
            && ProfileNow.GlobalList.Sequence > SentAtProfileSequence;

        /// <summary>Convenience: a radio-reported global list observed after
        /// the send that was NOT taken as the answer to a recorded
        /// <c>profile global info</c>, FlexLib's connect-time ask or ours
        /// (<see cref="ProfileEvidenceSnapshot.UnsolicitedGlobalList"/>).
        /// The radio sends one of these, unasked, a few milliseconds after the
        /// last slice a global load restores (measured 2026-10-17 to
        /// 2026-10-20, #639).</summary>
        public bool UnsolicitedGlobalListAfterSend =>
            ProfileNow?.UnsolicitedGlobalList != null
            && ProfileNow.UnsolicitedGlobalList.Sequence > SentAtProfileSequence;

        /// <summary>Convenience: <see cref="UnsolicitedGlobalListAfterSend"/>,
        /// AND that list arrived after every own slice the load has produced so
        /// far (by <see cref="ObservationOrdinal"/>, which both logs stamp). A
        /// slice reported after the list re-opens the question: the measured
        /// order is slices, then the list after the LAST of them, so a list
        /// that precedes a slice was not the end of the load.</summary>
        public bool UnsolicitedGlobalListAfterNewestNewOwnSlice
        {
            get
            {
                if (!UnsolicitedGlobalListAfterSend || StationNow == null) return false;
                long listOrdinal = ProfileNow.UnsolicitedGlobalList.Ordinal;
                bool any = false;
                foreach (var slice in StationNow.NewSince(SentAtStationSequence, StationBefore))
                {
                    any = true;
                    if (slice.Ordinal > listOrdinal) return false;
                }
                return any;
            }
        }

        /// <summary>Convenience: own slices reported after the send whose
        /// identity was not present before it.</summary>
        public int NewOwnSlicesSinceSend =>
            StationNow == null ? 0 : System.Linq.Enumerable.Count(StationNow.NewSince(SentAtStationSequence, StationBefore));
    }

    /// <summary>What a completion policy concludes about a sent load.</summary>
    public enum LoadCompletion
    {
        /// <summary>The evidence the bench established as the end of this load
        /// has been observed, together with the materialized own station.</summary>
        Confirmed,

        /// <summary>The radio refused the command. Terminal; nothing was restored.</summary>
        Rejected,

        /// <summary>Not proven yet, but a later observation may prove it. The
        /// coordinator keeps waiting inside the phase bound.</summary>
        Unconfirmed,

        /// <summary>Not provable by this policy on this transport, so waiting
        /// cannot help. The coordinator stops at once with an Unconfirmed
        /// outcome instead of spending the bound on an answer that cannot
        /// arrive. Only <see cref="LoadCompletionUnconfirmedPolicy"/> returns
        /// it, and that was the production default until 2026-10-20 (#639).</summary>
        NotProvable,
    }

    /// <summary>
    /// <b>Bench question B's plug.</b> Given the evidence for a sent global
    /// load, has the load finished reconstructing the station? No quiet timer
    /// is ever promoted to proof here or anywhere else. The production default
    /// is <see cref="LoadCompletionByGlobalListReportPolicy"/>, on the measured
    /// behaviour of the radio after a load (#639, 2026-10-20); if a radio ever
    /// turns out to emit a distinct end-of-load message, the handler that
    /// recognises it feeds <see cref="ProfileEvidenceLog.RadioEndBoundary"/>
    /// and <see cref="LoadCompletionByRadioEndBoundaryPolicy"/> is the shape
    /// that requires it.
    /// </summary>
    public interface ILoadCompletionPolicy
    {
        string Name { get; }
        LoadCompletion Judge(LoadEvidence evidence);
    }

    /// <summary>
    /// The RETIRED placeholder: nothing confirms a load, rejection is still
    /// rejection. The production default from the coordinator's landing
    /// (2026-09-21) until 2026-10-20, when #639 replaced it with
    /// <see cref="LoadCompletionByGlobalListReportPolicy"/>. Kept for the tests
    /// that need a sent load to stay outstanding (the post-import barrier, the
    /// new-connection reset), and as the record of what the trace line
    /// "unconfirmed until bench B" meant. <b>Not a production policy.</b>
    /// </summary>
    /// <remarks>
    /// What it cost while it shipped, so nobody reinstates it as "safer": on
    /// every owner connect it ended the station phase the instant the load
    /// was sent, reported <c>established=False</c> for a restore the trace
    /// showed working, skipped transmit, microphone, owner initialisation and
    /// live audio, and said "Station restore not confirmed" to an operator
    /// whose station had just come back exactly as saved (Noel, 2026-10-19
    /// 06:57; #639). Bench B was answered on 2026-10-17 and this code never
    /// heard — the shape <c>memory/feedback_a_closed_blocker_does_not_tell_its_entry.md</c>
    /// describes, between the register and the code.
    /// </remarks>
    public sealed class LoadCompletionUnconfirmedPolicy : ILoadCompletionPolicy
    {
        public static readonly LoadCompletionUnconfirmedPolicy Instance = new LoadCompletionUnconfirmedPolicy();
        public string Name => "unconfirmed until bench B";
        public LoadCompletion Judge(LoadEvidence evidence)
        {
            if (evidence != null && evidence.Rejected) return LoadCompletion.Rejected;
            return LoadCompletion.NotProvable;
        }
    }

    /// <summary>
    /// <b>The production default since 2026-10-20 (#639).</b> A sent global
    /// load is Confirmed when four things hold: FlexLib's own connect-time
    /// <c>profile global info</c> had been answered when the load was sent;
    /// at least one own slice has been reported ready after the send that
    /// was not present at the send; the radio has sent its global profile
    /// list ON ITS OWN ACCOUNT after the send — a list that is not taken as
    /// the answer to any recorded <c>profile global info</c>, FlexLib's or
    /// ours; and that list arrived AFTER the newest of those slices. A
    /// rejection is Rejected; anything short of all four is Unconfirmed, and
    /// the coordinator keeps judging inside the station phase as evidence
    /// arrives, so a later slice re-opens the wait for a later list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was measured, and where.</b> The 2026-10-17 bench (#499)
    /// established that the load WORKS: <c>JJRadioDefault</c> came back
    /// byte-identical across three loads. It did not find a distinct
    /// end-of-load message, and FlexLib surfaces none. What every archived
    /// trace of the bench 8600 shows instead — 21 loads in ten trace files
    /// from 2026-10-19 and 2026-10-20 (Codex's census, 2026-10-20): three
    /// three-slice loads on LAN, eighteen one-slice loads of which six were
    /// over SmartLink — is one order, every time: the load is sent; 259 to
    /// 689 ms later the restored slices are reported ready; 2 to 5 ms after
    /// the LAST of them the radio sends <c>profile global list=</c> again,
    /// together with the transmit and microphone lists, with nothing of ours
    /// having asked. Three slices: load 11101, slices 11554, 11559, 11561,
    /// list 11566. One slice: load 13083, slice 13342, list 13347. SmartLink:
    /// load 163634, slice 163910, list 163912. The radio's
    /// <c>profile global current=</c> is not usable here: FlexLib raises it
    /// only on a CHANGE, and on an owner's reconnect the wanted profile is
    /// usually already current.
    /// </para>
    /// <para>
    /// <b>Why the list's ORIGIN has to be established, not just its
    /// freshness (Codex B1, 2026-10-20).</b> The coordinator's fresh
    /// inventory read (<c>ReadRadioProfileList</c>) is bounded; when the
    /// radio is slow it gives up on the cached names with <c>Reported</c>
    /// still true, the planner proceeds, and the load goes out with our
    /// <c>profile global info</c> still unanswered. Its late answer then
    /// arrives as a radio-reported list with a sequence after the send and
    /// looks exactly like the post-load re-report. With the FIRST slice of a
    /// multi-slice restore that would have confirmed an unfinished load and
    /// started the post-station writes while the radio was still rebuilding.
    /// So <c>ReadRadioProfileList</c> now records every global ask in the
    /// evidence log before it goes out, the log takes the next radio-reported
    /// list as that ask's answer, and only a list with NO ask outstanding is
    /// <see cref="ProfileEvidenceSnapshot.UnsolicitedGlobalList"/>. An ask the
    /// radio never answers holds this closed for the attempt (the load's own
    /// re-report is consumed as the answer): fail-closed, not a timer.
    /// </para>
    /// <para>
    /// <b>FlexLib's own ask is accounted for too (repair round two).</b>
    /// <c>Radio.Connect</c> sends <c>profile global info</c> itself, from
    /// <c>GetProfileLists()</c>, and round one left it unrecorded on the
    /// premise that the radio answers it before the load's first slice
    /// because it was sent earlier. Codex (round-one re-verify) found nothing
    /// that proves that premise: the source shows received messages are
    /// handled in order, not that the radio emits an earlier query's answer
    /// before a later load's effects. So FlexBase now records FlexLib's ask
    /// immediately before every live vendor Connect
    /// (<c>NoteFlexLibConnectTimeProfileAsk</c>), the next radio-reported list
    /// is taken as its answer, and Noel ruled (2026-10-20 08:15) that if it
    /// may still be unanswered when the load is sent, the load never
    /// confirms on any list (<see cref="LoadEvidence.FlexLibConnectAnswerMayBeOutstandingAtSend"/>):
    /// it ends Unconfirmed at the phase bound. Every one of the 24 archived
    /// loads (2026-10-19 and 2026-10-20) had both answers, FlexLib's and
    /// ours, in before the send, so the measured path is unchanged.
    /// </para>
    /// <para>
    /// <b>Why the list must follow the newest slice.</b> The measured order is
    /// slices, then the radio's own list a few milliseconds after the LAST of
    /// them. Requiring the list to carry a higher
    /// <see cref="ObservationOrdinal"/> than every new own slice rejects any
    /// list that is not that one, including a late answer the accounting
    /// attributed to the wrong ask. The two logs have separate sequences,
    /// which is why the ordinal exists.
    /// </para>
    /// <para>
    /// <b>What this is not.</b> It is a measured regularity of one radio's
    /// behaviour, not a documented end-of-load message, and the policy has no
    /// model or transport input. A FLEX-6300, or a multi-slice profile over
    /// SmartLink, is UNMEASURED and NOT held closed by this code: if the same
    /// evidence arrives there, it confirms. Whether to gate those on a bench
    /// is an open question for Noel (ASK-639-measured-scope, 2026-10-20); a
    /// gate was not written here because the behaviour is the profile
    /// subsystem's, which the 6000 and 8000 series share, and because a
    /// per-model gate would cost every unmeasured model the whole phase bound
    /// and the unconfirmed sentence until somebody benches it. A firmware
    /// that re-lists after EACH restored slice would confirm this policy
    /// early; none is known. If a restored slice is ever traced AFTER the
    /// list re-report, this boundary is early and the policy must change.
    /// </para>
    /// <para>
    /// <b>What is Unconfirmed by construction, and what it costs.</b> A saved
    /// profile with ZERO slices never confirms (the list arrives, no slice
    /// does); a load whose restored slices all REUSE the indices of slices
    /// present at the send is not counted by
    /// <see cref="StationSnapshot.NewSince"/> (a mixed load confirms on its
    /// new index); a radio that never re-lists, or whose answer to our ask
    /// never comes, never confirms. Every one of those now spends what
    /// remains of the station phase's budget (<see cref="StationDeadlines.StationPhaseMs"/>,
    /// 20 s from the phase's start, not from the send) before ending
    /// Unconfirmed, where the retired placeholder ended in a millisecond.
    /// Same outcome, no post-station writes either way; the cost is the
    /// wait. FlexLib's connect-time ask still unanswered at the send is one
    /// more such case, by ruling. The benches: save an empty station and connect, for the
    /// zero-slice rule; the operator-requested load after company left with
    /// client-local slices on the radio, for index reuse (the fix belongs in
    /// the tracker's identity rule); one connect on a 6300, and one with a
    /// multi-slice profile over SmartLink, reading the trace for the order
    /// of <c>sliceAdded</c> and <c>ProfileGlobalList</c> after
    /// <c>global load ... SENT</c>.
    /// </para>
    /// <para>
    /// <b>Where it is judged.</b> Only <c>StationCoordinator.LoadExistingRoute</c>
    /// asks a completion policy, and every global load the application sends
    /// goes through it: the connect (operation 1), the post-import re-entry,
    /// and the operator-requested load.
    /// </para>
    /// </remarks>
    public sealed class LoadCompletionByGlobalListReportPolicy : ILoadCompletionPolicy
    {
        public static readonly LoadCompletionByGlobalListReportPolicy Instance = new LoadCompletionByGlobalListReportPolicy();
        public string Name => "the radio re-reported its global profile list, unasked, after the newest own slice the load restored";
        public LoadCompletion Judge(LoadEvidence e)
        {
            if (e == null) return LoadCompletion.Unconfirmed;
            if (e.Rejected) return LoadCompletion.Rejected;
            // FlexLib's connect-time answer may still have been on its way at
            // the send: any list after it may be that answer, so nothing after
            // it can confirm (Noel, 2026-10-20 08:15). Unconfirmed, not
            // NotProvable: the phase still waits out its bound, keeping the
            // slices that arrive, as ruled.
            if (e.FlexLibConnectAnswerMayBeOutstandingAtSend) return LoadCompletion.Unconfirmed;
            // A list taken as the answer to a recorded ask is inventory, not
            // the radio reporting the load; the list alone is not a station
            // either (a zero-slice profile and a load whose slices never
            // materialized both look like that).
            if (!e.UnsolicitedGlobalListAfterSend) return LoadCompletion.Unconfirmed;
            // A slice alone may be the first of several: the list is what the
            // radio was measured to send after the LAST one, so the list must
            // follow the newest slice, and a later slice re-opens the wait.
            if (e.NewOwnSlicesSinceSend == 0) return LoadCompletion.Unconfirmed;
            if (!e.UnsolicitedGlobalListAfterNewestNewOwnSlice) return LoadCompletion.Unconfirmed;
            return LoadCompletion.Confirmed;
        }
    }

    /// <summary>
    /// A completion policy that requires a radio-origin end boundary observed
    /// after the send AND at least one own slice reported after the send (or
    /// the boundary explicitly supporting zero). <b>Not the default</b>: it is
    /// the shape the design describes for the step-6 barrier, kept so tests
    /// have a positive control with an explicit boundary, and so that a radio
    /// which does emit a distinct end-of-load message (none is known; bench B
    /// found the list re-report instead, see
    /// <see cref="LoadCompletionByGlobalListReportPolicy"/>) can be plugged in
    /// through <see cref="ProfileEvidenceLog.RadioEndBoundary"/>.
    /// </summary>
    public sealed class LoadCompletionByRadioEndBoundaryPolicy : ILoadCompletionPolicy
    {
        public static readonly LoadCompletionByRadioEndBoundaryPolicy Instance = new LoadCompletionByRadioEndBoundaryPolicy();
        public string Name => "radio end boundary after send, plus materialized own station";
        public LoadCompletion Judge(LoadEvidence e)
        {
            if (e == null) return LoadCompletion.Unconfirmed;
            if (e.Rejected) return LoadCompletion.Rejected;
            if (!e.RadioEndBoundaryAfterSend) return LoadCompletion.Unconfirmed;
            // A boundary alone is not the station: the design requires the
            // client-owned materialized state as well. A zero-slice profile
            // can complete only when the boundary itself supports zero, which
            // is expressed by a token the bench names; none is defined yet.
            if (e.NewOwnSlicesSinceSend == 0 && !string.Equals(e.ProfileNow?.RadioEndBoundaryToken, "zero-slices", StringComparison.Ordinal))
                return LoadCompletion.Unconfirmed;
            return LoadCompletion.Confirmed;
        }
    }

    /// <summary>Whether the radio has finished delivering whatever station it
    /// supplies on its own after a connect (bench question D).</summary>
    public enum MaterializationBoundary
    {
        /// <summary>Initial materialization is known to have ended; a slice
        /// arriving now is a response to something, not persistence.</summary>
        Ended,

        /// <summary>Not yet, but a later observation may settle it. The
        /// coordinator keeps waiting inside the phase bound.</summary>
        Pending,

        /// <summary>Not established, and no further observation will establish
        /// it under this policy. A late persistence slice would be
        /// indistinguishable from a requested one, so no fresh allocation and
        /// no matching-name AlreadyLoaded conclusion. The default.</summary>
        Unknown,
    }

    /// <summary>Everything the materialization policy may consider.</summary>
    public sealed class MaterializationEvidence
    {
        public RosterSnapshot Roster;
        public StationSnapshot Station;
        public ProfileEvidenceSnapshot Profiles;
        /// <summary>Monotonic milliseconds since our own handle was established, or -1.</summary>
        public long ElapsedSinceOwnHandleMs = -1;
        /// <summary>Monotonic milliseconds since the last own-station observation, or -1 when none.</summary>
        public long ElapsedSinceLastStationObservationMs = -1;
    }

    /// <summary>
    /// <b>Bench question D's plug.</b> The fresh-station allocation route and
    /// the matching-name AlreadyLoaded decision both need the radio's own
    /// initial delivery to have ended. The default is Unknown, which disables
    /// both until the bench says what the boundary is (or that there is none,
    /// which is a design decision rather than a policy).
    /// </summary>
    public interface IInitialMaterializationPolicy
    {
        string Name { get; }
        MaterializationBoundary Judge(MaterializationEvidence evidence);
    }

    /// <summary>The default: the boundary is not established.</summary>
    public sealed class MaterializationUnknownPolicy : IInitialMaterializationPolicy
    {
        public static readonly MaterializationUnknownPolicy Instance = new MaterializationUnknownPolicy();
        public string Name => "unknown until bench D";
        public MaterializationBoundary Judge(MaterializationEvidence evidence) => MaterializationBoundary.Unknown;
    }

    /// <summary>
    /// A policy that treats own-handle establishment as the end of initial
    /// materialization, on the reasoning that a GUI client the radio has just
    /// minted has no persisted station to deliver. <b>Not the default.</b>
    /// Whether that reasoning holds is exactly what bench D measures
    /// (reconnect with the same client id versus a new one); this exists so
    /// the result can be plugged in and so tests have a positive control.
    /// </summary>
    public sealed class MaterializationEndsAtOwnHandlePolicy : IInitialMaterializationPolicy
    {
        public static readonly MaterializationEndsAtOwnHandlePolicy Instance = new MaterializationEndsAtOwnHandlePolicy();
        public string Name => "ends when our own handle is established";
        public MaterializationBoundary Judge(MaterializationEvidence e)
        {
            if (e?.Roster == null || !e.Roster.OwnHandleKnown) return MaterializationBoundary.Pending;
            return MaterializationBoundary.Ended;
        }
    }

    /// <summary>
    /// Initial materialization ends when our own handle is known AND the
    /// radio has answered the post-connect fence (#782). <b>Used for a
    /// RETURNING connect only</b> — one that presented a stored GUI client
    /// id, so the radio may be restoring a station of ours; a fresh mint
    /// keeps <see cref="MaterializationEndsAtOwnHandlePolicy"/>, which bench D
    /// measured. <see cref="StationPolicies.ForConnect"/> makes the choice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the own-handle boundary stopped being enough.</b>
    /// <see cref="MaterializationEndsAtOwnHandlePolicy"/> rests on "a client
    /// the radio has just minted has nothing persisted to deliver", which
    /// bench D measured true while every connect minted a new id. Once JJ
    /// Flexible presents a stored id, the radio can recognise the client and
    /// restore its panadapters and slices — and FlexLib subscribes
    /// <c>sub client all</c> BEFORE <c>sub pan all</c> and <c>sub slice all</c>,
    /// so our handle is known before a single restored slice can have been
    /// reported. A coordinator judging "ended" at the handle would read zero
    /// own slices and allocate, and the restored ones would then arrive on
    /// top: the duplication the own-handle policy's own note warned of.
    /// </para>
    /// <para>
    /// <b>The fence.</b> Right after the vendor's Connect returns, FlexBase
    /// sends one reply-bearing no-op (<c>ping</c>, the keep-alive's own
    /// command) on the same ordered command channel. The radio answers it
    /// after every earlier command on that channel has been handled, the
    /// subscriptions included, so the reply sits AFTER every object the
    /// radio REPORTED on its own. FlexBase records the acknowledged reply as
    /// evidence only once every own slice the vendor's list holds is also
    /// READY (FlexLib raises <c>SliceAdded</c> only when the slice's
    /// panadapter is ready, and the station tracker counts ready slices), so
    /// "reported but not yet ready" holds the boundary rather than passing
    /// it. A reply with a nonzero code is never evidence. Deterministic on
    /// LAN and WAN alike, and it costs one round trip. What it assumes, and
    /// the bench must show FIRST, on LAN and over SmartLink: that the radio
    /// reports a restored object while handling the subscription rather
    /// than from a later background step. If a restored slice is ever
    /// traced AFTER the fence reply, this boundary is early and must move.
    /// </para>
    /// <para>
    /// <b>Fail-closed, as before.</b> Pending until both facts are in; the
    /// phase deadline turns a fence that never answers into Unknown, which
    /// allocates nothing — the same safe refusal the design always had,
    /// now reachable only when the transport is genuinely not answering.
    /// </para>
    /// </remarks>
    public sealed class MaterializationEndsAtConnectFencePolicy : IInitialMaterializationPolicy
    {
        public static readonly MaterializationEndsAtConnectFencePolicy Instance = new MaterializationEndsAtConnectFencePolicy();
        public string Name => "ends at the post-connect fence reply, with our own handle established";
        public MaterializationBoundary Judge(MaterializationEvidence e)
        {
            if (e?.Roster == null || !e.Roster.OwnHandleKnown) return MaterializationBoundary.Pending;
            if (e.Profiles == null || e.Profiles.ConnectFenceAtSequence == 0) return MaterializationBoundary.Pending;
            return MaterializationBoundary.Ended;
        }
    }

    /// <summary>
    /// The three pluggable policies the bench supplies, held together so the
    /// connect path reads one object. <see cref="Current"/> is what production
    /// uses; the orchestrator sets it once the bench answers, and a test
    /// passes its own instance to the coordinator.
    /// </summary>
    public sealed class StationPolicies
    {
        /// <summary>Roster authority is RULED (Noel, 2026-09-22): live
        /// membership from the radio's own status, with discovery-only
        /// removals treated as present. Of the other THREE, only
        /// GuestSharedWriteAuthority is still fail-closed: InitialMaterialization
        /// was answered by bench D (2026-10-03) and LoadCompletion by bench B
        /// (2026-10-17, in the code 2026-10-20, #639). This sentence has
        /// miscounted twice, so count the fields rather than trusting the
        /// number.</summary>
        public IRosterAuthorityPolicy RosterAuthority = RosterAuthorityByLiveMembershipPolicy.Instance;

        /// <summary>
        /// BENCH B IS ANSWERED, 2026-10-17 (#499), and in the code since
        /// 2026-10-20 (#639): the radio re-reports its global profile list a
        /// few milliseconds after the last slice a load restores, and that —
        /// unasked by us, after the newest restored slice — plus a materialized
        /// own slice is what <see cref="LoadCompletionByGlobalListReportPolicy"/>
        /// requires. The measurement, its limits and the benches that would
        /// widen it are on that class. Until then this was
        /// <see cref="LoadCompletionUnconfirmedPolicy"/>, which ended every
        /// owner connect Unconfirmed the instant the load was sent and kept
        /// transmit, microphone, owner initialisation and live audio from
        /// running on the one path that was restoring the station correctly.
        /// </summary>
        public ILoadCompletionPolicy LoadCompletion = LoadCompletionByGlobalListReportPolicy.Instance;

        /// <summary>
        /// BENCH D IS ANSWERED, 2026-10-02, so this is no longer the fail-closed
        /// placeholder. Nine consecutive connects produced NINE distinct client
        /// GUIDs and nine distinct handles: the radio never saw our client id
        /// twice. So a GUI client the radio has just minted genuinely has no
        /// persisted station to deliver, which is exactly the reasoning
        /// <see cref="MaterializationEndsAtOwnHandlePolicy"/> was written on,
        /// and its premise holds unconditionally rather than situationally.
        ///
        /// THE CLIENT ID BECAME STABLE ON 2026-10-19 (#782), WHICH IS THE ONE
        /// THING THE PARAGRAPH BELOW SAID WOULD INVALIDATE THE OWN-HANDLE
        /// BOUNDARY. The answer is not a new default here: this field stays
        /// the own-handle policy for a connect that presented NO stored id
        /// (a fresh mint, the case bench D measured), and
        /// <see cref="ForConnect"/> swaps in
        /// <see cref="MaterializationEndsAtConnectFencePolicy"/> for a connect
        /// that presented a stored id and may be handed a restored station.
        /// A fresh connect therefore behaves exactly as it did on 2026-10-03,
        /// and cannot be left with zero slices by a fence that never answers.
        /// The note that follows is kept as the record of why the fence
        /// exists; "come back to Unknown" was the zero-slices regression and
        /// is not the answer.
        ///
        /// (Original note:) CONDITIONAL ON NOBODY MAKING THE CLIENT ID STABLE.
        /// That is the one thing that would invalidate this: a persisted or
        /// derived client id would let the radio recognise us and deliver a
        /// station we would then allocate over. If a future change makes the
        /// id stable, this default has to come back to Unknown and bench D has
        /// to be re-run.
        ///
        /// WHAT IT UNBLOCKS, measured on the bench 8600 2026-10-03: with the
        /// Unknown placeholder, an owner connecting with the
        /// UseMyTransmitAudio intent got ZERO slices. Stewardship is correctly
        /// refused for that intent, which routes to FreshStationRoute - the
        /// client-local allocation - and that path asks this policy first and
        /// stopped at AllocationStop.MaterializationUnknown. The trace said so
        /// in terms: "no fresh allocation because the end of initial
        /// materialization is not established". Noel has been reporting zero
        /// slices for weeks; this is the whole of it.
        ///
        /// NOTE WHAT IS *NOT* CHANGED HERE. <see cref="GuestSharedWriteAuthority"/>
        /// below stays Unknown. It is NOT merely waiting on a bench: its own
        /// note requires the legacy put-back executor to retain unresolved
        /// snapshots and autosave obligations first, and that work is not done.
        /// It also governs writing on a radio that is not ours. Bench A
        /// measured the roster on Noel's OWN radio, which is the owner case and
        /// not "what a complete roster looks like for a non-owner". Two
        /// placeholders were answered on 2026-10-02; only ONE of them was a
        /// one-line swap.
        /// </summary>
        public IInitialMaterializationPolicy InitialMaterialization = MaterializationEndsAtOwnHandlePolicy.Instance;

        /// <summary>
        /// The authority a GUEST'S shared write (the UseMyTransmitAudio
        /// route: autosave off, the live chain) must obtain. SEPARATE from
        /// <see cref="RosterAuthority"/> on purpose (Track G2 re-review,
        /// section 4): Noel's 2026-09-22 ruling accepted live membership as
        /// the OWNER'S defence in depth, and Track G2 applied that same
        /// relaxation to the guest route, which the ruling never covered.
        /// This stays Unknown — the route unreachable — until a bench
        /// establishes what a complete roster looks like for a non-owner,
        /// and until the legacy put-back executor can retain unresolved
        /// snapshots and autosave obligations (re-review, group 3). A test's
        /// positive control sets it; production does not.
        /// </summary>
        public IRosterAuthorityPolicy GuestSharedWriteAuthority = RosterAuthorityUnknownPolicy.Instance;

        /// <summary>The production defaults, as of 2026-10-20: the ruled owner
        /// roster authority; materialization ending at our own handle (bench D,
        /// answered 2026-10-02; a RETURNING connect gets the fence through
        /// <see cref="ForConnect"/>, #782); load completion on the radio's
        /// measured list re-report plus a materialized own slice (bench B,
        /// answered 2026-10-17, #639); the GUEST shared-write authority still
        /// fail-closed. Materialization was fail-closed until 2026-10-03, and
        /// that default was the whole of the zero-slices report; load
        /// completion was fail-closed until 2026-10-20, and that default was
        /// the whole of "Station restore not confirmed" on every owner connect
        /// — see the notes on the fields.</summary>
        public static StationPolicies Defaults() => new StationPolicies();

        private static StationPolicies _current = Defaults();

        /// <summary>
        /// The policies production connects with. One default is still
        /// fail-closed — the guest's shared-write authority — so the guest
        /// live-audio route stays refused; everything else now judges on
        /// radio-reported evidence.
        /// </summary>
        public static StationPolicies Current
        {
            get => _current;
            set => _current = value ?? Defaults();
        }

        public string Describe() =>
            "roster: " + RosterAuthority.Name + "; guest shared write: " + GuestSharedWriteAuthority.Name
            + "; completion: " + LoadCompletion.Name + "; materialization: " + InitialMaterialization.Name;

        /// <summary>A copy of these policies with another initial-materialization
        /// policy; the other three are shared, as they are immutable singletons.</summary>
        public StationPolicies WithInitialMaterialization(IInitialMaterializationPolicy policy) => new StationPolicies
        {
            RosterAuthority = RosterAuthority,
            LoadCompletion = LoadCompletion,
            InitialMaterialization = policy ?? InitialMaterialization,
            GuestSharedWriteAuthority = GuestSharedWriteAuthority,
        };

        /// <summary>
        /// The policies for ONE connect (#782). <see cref="Current"/> as it
        /// stands, except that a connect which presented a stored GUI client
        /// id — so the radio may be restoring a station of ours — judges
        /// initial materialization at the post-connect fence
        /// (<see cref="MaterializationEndsAtConnectFencePolicy"/>) instead of
        /// at our own handle. A fresh mint is exactly the case bench D
        /// measured and keeps <see cref="Current"/> untouched.
        /// </summary>
        public static StationPolicies ForConnect(bool presentedStoredClientId)
        {
            var current = Current;
            return presentedStoredClientId
                ? current.WithInitialMaterialization(MaterializationEndsAtConnectFencePolicy.Instance)
                : current;
        }
    }

    /// <summary>
    /// A NON-OWNER'S TNF write. RULED by Noel 2026-09-22 21:36 — <i>"if
    /// someone's connected by themselves, set it by all means when the
    /// profile loads ... make it simple, don't do it if you're a non-owner
    /// and the owner's connected"</i> — and REFINED a minute later at 21:37:
    /// <i>"A non-owner could set it if they're the only person on, but if the
    /// TNF is enabled, i.e. turned on by the owner, don't allow a change. If
    /// it's disabled, then the non-owner should be able to turn it on and set
    /// it temporarily. Connect will help with all this junk."</i>
    /// <para>
    /// The OWNER'S TNF write is not this gate's business and is unchanged: it
    /// is the first write of <c>RunOwnerInitialization</c>, checked at the
    /// write by the orchestrator's <c>OwnerInitRefusalAtWrite</c>.
    /// </para>
    /// <para>
    /// TWO LIMITS, both stated in the ruling, neither of them built here.
    /// First, "temporarily" needs a put-back on disconnect — the legacy
    /// put-back executor Track G3 left for a further track — so until that
    /// exists a non-owner's TNF is NOT undone when they leave. Second, the
    /// app cannot tell WHO turned TNF on; it sees only on or off, so "on
    /// means the owner set it" is an assumption, written down here as one.
    /// Noel: <i>"Connect carries who set what and replaces both."</i> Do not
    /// invent a who-set-it record.
    /// </para>
    /// </summary>
    public static class NonOwnerTnfGate
    {
        /// <summary>Null when the non-owner may turn TNF on; else why not.</summary>
        /// <param name="f">The facts at the moment of the write.</param>
        /// <param name="guestRoster">The GUEST'S roster authority, never the
        /// owner's ruled relaxation. A non-owner's station-global write is
        /// exactly the class the Track G2 re-review found had been widened
        /// past the ruling, so it takes the guest's own authority — which is
        /// Unknown in production until a bench establishes it, and therefore
        /// closes this path until then.</param>
        /// <param name="tnfAlreadyOn">What the radio reports TNF to be.</param>
        public static string Refusal(StationPolicyFacts f, RosterJudgement guestRoster, bool tnfAlreadyOn)
        {
            if (f == null) return "no facts";
            if (f.HoldArmed) return "the change-nothing hold is armed";
            if (!f.Connected) return "not connected";
            if (f.Ownership == RadioOwnership.Mine)
                return "this radio is declared ours; the owner's initialisation sets TNF, not this";
            if (guestRoster == null || guestRoster.Verdict != RosterVerdict.OnlyUs)
                return "not alone on this radio (guest roster authority: " + (guestRoster?.ToString() ?? "unknown") + ")";
            if (tnfAlreadyOn)
                return "TNF is already on; leave it — assume the owner turned it on, because the app cannot tell who did";
            return null;
        }
    }

    // Track G3's owner gate for station-global operating writes stood here.
    // Its only two callers were
    // the shack-speaker mute, and RULED by Noel 2026-09-22 21:33 — "mute the
    // shack speaker if you're going PC audio, unmute it if you're not using
    // it. If for some really weird reason you want to have the speaker
    // unmuted while you're PC audio connected, then cool. Why make it
    // complicated." — the mute is not a shared write and follows nothing but
    // PC audio. TNF and the keyer restore were never on this gate: they are
    // RunOwnerInitialization's, checked at the write by the orchestrator's
    // OwnerInitRefusalAtWrite, and unchanged.
}
