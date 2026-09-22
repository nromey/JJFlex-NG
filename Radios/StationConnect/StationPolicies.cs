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
        public IRosterAuthorityPolicy RosterAuthority = RosterAuthorityUnknownPolicy.Instance;
        public ILoadCompletionPolicy LoadCompletion = LoadCompletionUnconfirmedPolicy.Instance;
        public IInitialMaterializationPolicy InitialMaterialization = MaterializationUnknownPolicy.Instance;

        /// <summary>All three at their fail-closed defaults.</summary>
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
            "roster: " + RosterAuthority.Name + "; completion: " + LoadCompletion.Name
            + "; materialization: " + InitialMaterialization.Name;
    }
}
