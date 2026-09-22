using System;

namespace Radios.StationConnect
{
    /// <summary>
    /// The seven distinct results of station establishment (design section
    /// 1, closing paragraph). <c>GetProfileInfo</c>'s old Boolean could not
    /// carry these, and "false" fell through to a scratch setup that layered
    /// defaults over whatever the radio had done.
    /// </summary>
    public enum StationOutcome
    {
        /// <summary>No automatic global stewardship: the hold, an unanswered or
        /// leave-alone intent, a non-owner connection, a roster that could not
        /// authorise a write, or a planner refusal. Nothing shared was sent.
        /// A client-local station may still have been allocated; see
        /// <see cref="StationResult.Allocation"/>.</summary>
        PolicySkipped,

        /// <summary>The wanted global is already selected, our own station is
        /// present and initial materialization is known to have ended.
        /// Nothing sent.</summary>
        ExistingStationConfirmed,

        /// <summary>A global load was sent and the completion policy confirmed
        /// it from evidence. No top-up allocation follows.</summary>
        RestoredConfirmed,

        /// <summary>The wanted global was definitely absent from an owned,
        /// opted-in radio; no load was sent; a fresh client station was
        /// allocated under the bounded allocator; creation at clean disconnect
        /// is armed for the EFFECTIVE per-radio name.</summary>
        FreshStationConfirmed,

        /// <summary>A command was refused, a dispatch never ran, or allocation
        /// stopped on timeout or denial. What was obtained is kept; nothing
        /// station-dependent runs against it.</summary>
        Failed,

        /// <summary>The attempt was invalidated (disconnect, radio replaced,
        /// operator cancel) before a terminal result.</summary>
        Cancelled,

        /// <summary>Not proven either way. A load was sent and its completion
        /// could not be confirmed, or a station is present whose completeness
        /// cannot be established. Retain what is there; forbid default fill
        /// and dependent tx/mic writes; report; offer retry.</summary>
        Unconfirmed,
    }

    /// <summary>Which of the design's four global routes the decision took.</summary>
    public enum GlobalRoute
    {
        None,
        /// <summary>Route 1: existing, established station; AlreadyLoaded skip retained.</summary>
        ExistingStation,
        /// <summary>Route 2: existing wanted profile requiring a load.</summary>
        LoadExisting,
        /// <summary>Route 3: definitely missing wanted profile on a Mine, opted-in radio.</summary>
        MissingOwnedGlobal,
        /// <summary>Route 4: no wanted global, policy refusal, or a non-owner connection.</summary>
        Refused,
    }

    /// <summary>Why the bounded allocator stopped.</summary>
    public enum AllocationStop
    {
        NotAttempted,
        /// <summary>The route does not permit a fresh station (restored, existing, unconfirmed).</summary>
        RouteForbids,
        /// <summary>A global load is outstanding or unconfirmed.</summary>
        LoadOutstanding,
        /// <summary>Bench question D has not established the end of initial materialization.</summary>
        MaterializationUnknown,
        TargetReached,
        /// <summary>The radio reports no remaining capacity.</summary>
        CapacityExhausted,
        /// <summary>The one outstanding request produced no new own resource within its bound.</summary>
        Timeout,
        Cancelled,
        /// <summary>No target could be computed (capacity and legacy target both unknown or zero).</summary>
        NoTarget,
    }

    public sealed class AllocationResult
    {
        public int Target;
        public int Requests;
        public int Obtained;
        public int OwnSlicesAtEnd;
        public AllocationStop Stop = AllocationStop.NotAttempted;
        public string Note = "";

        public bool ReachedTargetOrCapacity =>
            Stop == AllocationStop.TargetReached || Stop == AllocationStop.CapacityExhausted;

        public override string ToString() =>
            Stop + " target=" + Target + " requests=" + Requests + " obtained=" + Obtained + " own=" + OwnSlicesAtEnd
            + (string.IsNullOrEmpty(Note) ? "" : " (" + Note + ")");
    }

    /// <summary>The whole result of one station-establishment run.</summary>
    public sealed class StationResult
    {
        public StationOutcome Outcome = StationOutcome.Unconfirmed;
        public GlobalRoute Route = GlobalRoute.None;
        public string Reason = "";
        public string WantedGlobal = "";
        public int AttemptGeneration;

        /// <summary>A global load command went out this run.</summary>
        public bool LoadSent;

        /// <summary>A load went out and its effect is not established. The
        /// command may still act; nothing may layer defaults over it.</summary>
        public bool LoadOutstanding;

        /// <summary>The disconnect-time create is armed for <see cref="PendingCreateName"/>.</summary>
        public bool CreationArmed;
        public string PendingCreateName = "";

        public RosterJudgement RosterAtDecision;
        public AllocationResult Allocation = new AllocationResult();
        public int OwnSlicesAtEnd;
        public string Policies = "";

        /// <summary>The three confirmed outcomes: a station whose shape is
        /// known. Downstream station-dependent writes may consult this.</summary>
        public bool StationConfirmed =>
            Outcome == StationOutcome.ExistingStationConfirmed
            || Outcome == StationOutcome.RestoredConfirmed
            || Outcome == StationOutcome.FreshStationConfirmed;

        /// <summary>A client station exists and was established by a route that
        /// permits acting on it: a confirmed outcome, or a policy-skipped
        /// connection whose bounded fresh allocation reached its target or
        /// the radio's capacity with at least one slice.</summary>
        public bool StationEstablished =>
            StationConfirmed
            || (Outcome == StationOutcome.PolicySkipped
                && Allocation.ReachedTargetOrCapacity && Allocation.OwnSlicesAtEnd > 0);

        /// <summary>The old Boolean's honest replacement: never true for
        /// Failed, Cancelled or Unconfirmed.</summary>
        public bool PermitsScratchSetup => false;

        public override string ToString() =>
            Outcome + " via " + Route + (string.IsNullOrEmpty(Reason) ? "" : " — " + Reason)
            + (LoadSent ? " [load sent" + (LoadOutstanding ? ", outstanding]" : "]") : "")
            + (CreationArmed ? " [create '" + PendingCreateName + "' at disconnect]" : "")
            + " own=" + OwnSlicesAtEnd + " alloc=" + Allocation;
    }

    /// <summary>
    /// The remembered intent to create a global that was definitely missing
    /// (#578, design step 11). Bound to the attempt and the effective
    /// per-radio name; expires with either.
    /// </summary>
    public sealed class PendingGlobalCreation
    {
        public PendingGlobalCreation(string name, string serial, int attemptGeneration, string because)
        {
            Name = name ?? "";
            Serial = serial ?? "";
            AttemptGeneration = attemptGeneration;
            Because = because ?? "";
        }
        public string Name { get; }
        public string Serial { get; }
        public int AttemptGeneration { get; }
        public string Because { get; }
        public override string ToString() => "create '" + Name + "' on " + Serial + " (attempt " + AttemptGeneration + ")";
    }

    /// <summary>What the disconnect-time create decided.</summary>
    public sealed class CreationDecision
    {
        public bool Create;
        public string Reason = "";
        public static CreationDecision Yes(string why) => new CreationDecision { Create = true, Reason = why };
        public static CreationDecision No(string why) => new CreationDecision { Create = false, Reason = why };
    }

    /// <summary>The facts the disconnect-time create revalidates.</summary>
    public sealed class CreationFacts
    {
        public PendingGlobalCreation Pending;
        public ConnectionAttempt Attempt;
        public bool Connected;
        public bool HoldArmed;
        public RadioOwnership Ownership;
        public ProfileGuestIntent Intent;
        public string WantedGlobalNow = "";
        public string Serial = "";
        public RosterJudgement Roster;
        /// <summary>The latest RADIO-REPORTED inventory, or null when none was ever reported.</summary>
        public InventoryObservation Inventory;
        public StationOutcome StationOutcome;
        public bool LoadOutstanding;
    }

    /// <summary>Design step 11, as a pure rule. Every condition is named so a
    /// refusal in the trace says which one.</summary>
    public static class DeferredGlobalCreation
    {
        public static CreationDecision Decide(CreationFacts f)
        {
            if (f == null || f.Pending == null) return CreationDecision.No("nothing pending");
            if (f.Attempt == null || !f.Attempt.Owns(f.Pending.AttemptGeneration))
                return CreationDecision.No("the pending create belongs to a different connection attempt");
            if (!f.Connected) return CreationDecision.No("not connected");
            if (f.HoldArmed) return CreationDecision.No("the change-nothing hold is armed");
            if (f.Ownership != RadioOwnership.Mine) return CreationDecision.No("the radio is not declared ours");
            if (f.Intent != ProfileGuestIntent.LoadMineAndPutBack) return CreationDecision.No("the radio is not opted in");
            if (!string.Equals(f.Serial, f.Pending.Serial, StringComparison.Ordinal))
                return CreationDecision.No("a different radio is connected");
            if (!string.Equals(f.WantedGlobalNow, f.Pending.Name, StringComparison.Ordinal))
                return CreationDecision.No("the wanted global name changed since it was armed");
            if (f.Roster == null || f.Roster.Verdict != RosterVerdict.OnlyUs)
                return CreationDecision.No("roster: " + (f.Roster?.ToString() ?? "unknown"));
            if (f.StationOutcome != StationOutcome.FreshStationConfirmed)
                return CreationDecision.No("station establishment did not end as a confirmed fresh station (" + f.StationOutcome + ")");
            if (f.LoadOutstanding) return CreationDecision.No("a global load is outstanding");
            if (f.Inventory == null || f.Inventory.Provenance != ObservationProvenance.RadioReported)
                return CreationDecision.No("the radio never reported its global inventory, so absence is not established");
            if (f.Inventory.Contains(f.Pending.Name))
                return CreationDecision.No("a profile of that name now exists; never overwrite an intervening profile");
            return CreationDecision.Yes("still missing, same radio and name, owned and opted in, only us, station confirmed");
        }
    }
}
