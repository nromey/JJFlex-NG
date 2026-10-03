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
        /// cannot help. The default while bench question B is open. The
        /// coordinator stops at once with an Unconfirmed outcome instead of
        /// spending the bound on an answer that cannot arrive.</summary>
        NotProvable,
    }

    /// <summary>
    /// <b>Bench question B's plug.</b> Given the evidence for a sent global
    /// load, has the load finished reconstructing the station? The default
    /// returns <see cref="LoadCompletion.Unconfirmed"/> for everything but an
    /// explicit rejection. No quiet timer is ever promoted to proof here or
    /// anywhere else; if the bench finds a radio-origin end boundary, the
    /// handler that recognises it feeds <see cref="ProfileEvidenceLog.RadioEndBoundary"/>
    /// and a policy that requires it replaces the default.
    /// </summary>
    public interface ILoadCompletionPolicy
    {
        string Name { get; }
        LoadCompletion Judge(LoadEvidence evidence);
    }

    /// <summary>The default: nothing confirms a load. Rejection is still rejection.</summary>
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
    /// A completion policy that requires a radio-origin end boundary observed
    /// after the send AND at least one own slice reported after the send (or
    /// the boundary explicitly supporting zero). <b>Not the default</b>: it is
    /// the shape the design describes for the step-6 barrier, provided so the
    /// bench result can be plugged in and so tests have a positive control.
    /// Whether such a boundary exists is what bench B determines.
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
    /// The three pluggable policies the bench supplies, held together so the
    /// connect path reads one object. <see cref="Current"/> is what production
    /// uses; the orchestrator sets it once the bench answers, and a test
    /// passes its own instance to the coordinator.
    /// </summary>
    public sealed class StationPolicies
    {
        /// <summary>Roster authority is RULED (Noel, 2026-09-22): live
        /// membership from the radio's own status, with discovery-only
        /// removals treated as present. Of the other THREE, LoadCompletion and
        /// GuestSharedWriteAuthority are still fail-closed;
        /// InitialMaterialization is NOT, as of 2026-10-03 — bench D answered
        /// it. This sentence said "the other two" when there were three of
        /// them, so count the fields rather than trusting the number.</summary>
        public IRosterAuthorityPolicy RosterAuthority = RosterAuthorityByLiveMembershipPolicy.Instance;
        public ILoadCompletionPolicy LoadCompletion = LoadCompletionUnconfirmedPolicy.Instance;

        /// <summary>
        /// BENCH D IS ANSWERED, 2026-10-02, so this is no longer the fail-closed
        /// placeholder. Nine consecutive connects produced NINE distinct client
        /// GUIDs and nine distinct handles: the radio never saw our client id
        /// twice. So a GUI client the radio has just minted genuinely has no
        /// persisted station to deliver, which is exactly the reasoning
        /// <see cref="MaterializationEndsAtOwnHandlePolicy"/> was written on,
        /// and its premise holds unconditionally rather than situationally.
        ///
        /// CONDITIONAL ON NOBODY MAKING THE CLIENT ID STABLE. That is the one
        /// thing that would invalidate this: a persisted or derived client id
        /// would let the radio recognise us and deliver a station we would then
        /// allocate over. If a future change makes the id stable, this default
        /// has to come back to Unknown and bench D has to be re-run.
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

        /// <summary>The production defaults, as of 2026-10-03: the ruled owner
        /// roster authority; materialization ending at our own handle (bench D,
        /// answered 2026-10-02); load completion and the GUEST shared-write
        /// authority still fail-closed. Materialization was fail-closed until
        /// 2026-10-03, and that default was the whole of the zero-slices
        /// report — see the note on the field.</summary>
        public static StationPolicies Defaults() => new StationPolicies();

        private static StationPolicies _current = Defaults();

        /// <summary>
        /// The policies production connects with. Defaults refuse every
        /// automatic shared write and every fresh allocation; that is the
        /// working, safer app the design describes while the bench is open.
        /// </summary>
        public static StationPolicies Current
        {
            get => _current;
            set => _current = value ?? Defaults();
        }

        public string Describe() =>
            "roster: " + RosterAuthority.Name + "; guest shared write: " + GuestSharedWriteAuthority.Name
            + "; completion: " + LoadCompletion.Name + "; materialization: " + InitialMaterialization.Name;
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
