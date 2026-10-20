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

        /// <summary>Convenience: a RADIO-REPORTED global profile list observed
        /// AFTER the send. The radio re-reports its lists, unasked, a few
        /// milliseconds after the last slice a global load restores (measured
        /// 2026-10-17 to 2026-10-20, #639); a list reported before the send —
        /// FlexLib's connect-time ask, or our own fresh ask, both of which are
        /// answered before the load goes out — has a lower sequence and does
        /// not count.</summary>
        public bool RadioReportedGlobalListAfterSend =>
            ProfileNow?.GlobalList != null
            && ProfileNow.GlobalList.Provenance == ObservationProvenance.RadioReported
            && ProfileNow.GlobalList.Sequence > SentAtProfileSequence;

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
    /// load is Confirmed when the radio has RE-REPORTED ITS GLOBAL PROFILE
    /// LIST after the send AND at least one own slice has been reported ready
    /// after the send that was not present at the send. Rejection is
    /// rejection; anything short of both facts is Unconfirmed, and the
    /// coordinator keeps judging inside the station phase as evidence arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was measured, and where.</b> The 2026-10-17 bench (#499)
    /// established that the load WORKS: <c>JJRadioDefault</c> came back
    /// byte-identical across three loads. It did not find a distinct
    /// end-of-load message, and FlexLib surfaces none. What every archived
    /// trace of the bench 8600 shows instead — seven connects from 2026-10-17
    /// to 2026-10-20, a three-slice profile on 2026-10-19 06:00 and 06:35 and
    /// a one-slice profile on 2026-10-19 17:42 to 19:01 and 2026-10-20 04:44
    /// and 04:45 — is one order, every time: the load is sent; 260 to 480 ms
    /// later the restored slices are reported ready; 2 to 5 ms after the LAST
    /// of them the radio sends <c>profile global list=</c> again, together
    /// with the transmit and microphone lists, unasked. Three slices: load
    /// 11101, slices 11554, 11559, 11561, list 11566. One slice: load 13083,
    /// slice 13342, list 13347.
    /// </para>
    /// <para>
    /// <b>Why the list report is the radio's own.</b> FlexLib asks for the
    /// lists once, at connect (<c>GetProfileLists</c> in its connect
    /// sequence, before the subscriptions), and the coordinator's fresh ask
    /// for the global inventory is ANSWERED before the load is sent, because
    /// the coordinator waits for that answer and only then dispatches. During
    /// the coordinator's wait nothing of ours asks for the global list, so a
    /// radio-reported list with a sequence after the send came from the radio
    /// on its own account. The radio's <c>profile global current=</c> is not
    /// usable here: FlexLib raises it only on a CHANGE, and on an owner's
    /// reconnect the wanted profile is usually already current.
    /// </para>
    /// <para>
    /// <b>What this is not.</b> It is a measured regularity of this radio's
    /// behaviour on this firmware, not a documented end-of-load message. If a
    /// radio ever re-lists its profiles BEFORE the last restored slice is
    /// ready, this policy confirms early: the result's slice count is low,
    /// the layout recorded at "station established" is partial until the
    /// next place flush rewrites it, and the post-station writes — which do
    /// not depend on the slice count — run while slices are still arriving.
    /// Nothing is written to a slice on that account. If a restored slice is
    /// ever traced AFTER the list re-report, this boundary is early and the
    /// policy must change.
    /// </para>
    /// <para>
    /// <b>What stays fail-closed, and the bench that would open each.</b> A
    /// saved profile with ZERO slices never confirms (the list arrives, no
    /// slice does) and ends Unconfirmed at the phase deadline — save an empty
    /// station, connect, and read whether the list is still re-reported; if
    /// it is, a zero-slice rule can be written on that trace. A load whose
    /// restored slices REUSE the indices of slices present at the send is not
    /// counted by <see cref="StationSnapshot.NewSince"/> and ends Unconfirmed
    /// at the deadline — the operator-requested load after company left, with
    /// client-local slices already on the radio, is the case; the fix is in
    /// the station tracker's identity rule, not here. The 6300, and a
    /// multi-slice profile over SmartLink, are unobserved: the bench is one
    /// connect with the trace read for the order of <c>sliceAdded</c> and
    /// <c>ProfileGlobalList</c> after <c>global load ... SENT</c>.
    /// </para>
    /// <para>
    /// <b>Where it is judged.</b> Only <c>StationCoordinator.LoadExistingRoute</c>
    /// asks a completion policy, and every global load the application sends
    /// goes through it: the connect (operation 1), the post-import re-entry,
    /// and the operator-requested load. The radio's response to
    /// <c>profile global load</c> is the same command on all three, so one
    /// policy judges one kind of evidence; the unobserved variables are the
    /// radio and the transport, named above, not the path.
    /// </para>
    /// </remarks>
    public sealed class LoadCompletionByGlobalListReportPolicy : ILoadCompletionPolicy
    {
        public static readonly LoadCompletionByGlobalListReportPolicy Instance = new LoadCompletionByGlobalListReportPolicy();
        public string Name => "the radio re-reported its global profile list after the load, and an own slice materialized";
        public LoadCompletion Judge(LoadEvidence e)
        {
            if (e == null) return LoadCompletion.Unconfirmed;
            if (e.Rejected) return LoadCompletion.Rejected;
            // The list alone is not a station: a zero-slice profile and a load
            // whose slices never materialized both look like this.
            if (!e.RadioReportedGlobalListAfterSend) return LoadCompletion.Unconfirmed;
            // A slice alone may be the first of several: the list is what the
            // radio was measured to send after the LAST one.
            if (e.NewOwnSlicesSinceSend == 0) return LoadCompletion.Unconfirmed;
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
        /// few milliseconds after the last slice a load restores, and that
        /// plus a materialized own slice is what
        /// <see cref="LoadCompletionByGlobalListReportPolicy"/> requires. The
        /// measurement, its limits and the benches that would widen it are on
        /// that class. Until then this was
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
