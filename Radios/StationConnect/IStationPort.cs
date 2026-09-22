using System;

namespace Radios.StationConnect
{
    /// <summary>The cheap, re-readable local facts every decision samples.
    /// Read at entry and again inside every queued shared-write delegate.</summary>
    public sealed class StationPolicyFacts
    {
        public bool Connected;
        public bool HoldArmed;
        public RadioOwnership Ownership = RadioOwnership.Unset;
        public ProfileGuestIntent Intent = ProfileGuestIntent.NotAnswered;
        /// <summary>The EFFECTIVE wanted global for THIS radio: the per-radio
        /// choice, else the operator default. Never <c>GetDefaultProfiles()[0]</c>
        /// alone (#578).</summary>
        public string WantedGlobal = "";
        public string Serial = "";

        public bool SameAutomaticPermissionAs(StationPolicyFacts other)
        {
            if (other == null) return false;
            return Connected == other.Connected
                && HoldArmed == other.HoldArmed
                && Ownership == other.Ownership
                && Intent == other.Intent
                && string.Equals(WantedGlobal, other.WantedGlobal, StringComparison.Ordinal)
                && string.Equals(Serial, other.Serial, StringComparison.Ordinal);
        }

        public override string ToString() =>
            (Connected ? "connected" : "not connected") + (HoldArmed ? ", hold armed" : "")
            + ", " + Ownership + ", " + Intent + ", wanted '" + WantedGlobal + "'";
    }

    /// <summary>
    /// What the coordinator needs from the radio side, and nothing else. The
    /// production implementation is a thin translation onto FlexLib inside
    /// FlexBase; tests supply one that records what was sent and lets the
    /// test hold a dispatched delegate before releasing it.
    /// </summary>
    /// <remarks>
    /// The RULES live in the coordinator, the guard and the trackers, which
    /// are production code in both cases. This interface carries facts in and
    /// commands out; it never decides anything.
    /// </remarks>
    public interface IStationPort
    {
        StationPolicyFacts ReadPolicyFacts();

        /// <summary>A bounded, radio-origin read of the GLOBAL profile type
        /// only, shaped for the planner. OnlyStation and StationPresent on
        /// the returned situation are overwritten by the coordinator from its
        /// own evidence; the port need not set them.</summary>
        ProfileSituation ReadGlobalSituation(int timeoutMs);

        /// <summary>PanadaptersRemaining as the radio last reported it, or -1.</summary>
        int CapacityRemaining();

        /// <summary>The legacy fresh-session target (the first PanadaptersRemaining
        /// latched at startup), or -1. A startup observation, not a saved layout.</summary>
        int LegacyFreshTarget();

        /// <summary>
        /// Run <paramref name="work"/> on the command path. Before the command
        /// loop is up this executes inline; on the command thread itself it
        /// executes inline (never wait on a queue only this thread drains);
        /// otherwise it is queued. A test port may hold the work and release
        /// it later, which is how the dispatch-race tests inject a change
        /// between planning and sending.
        /// </summary>
        void Dispatch(string name, Action work);

        /// <summary>Send the global load. Called only inside dispatched work,
        /// after the recheck. <paramref name="onRejected"/> is invoked with the
        /// radio's error text if a rejection can be observed; the FlexLib
        /// setter path cannot observe one and never calls it.</summary>
        void SendGlobalLoad(string name, Action<string> onRejected);

        /// <summary>Ask for one new panadapter-and-slice. Inside dispatched work only.</summary>
        void RequestPanafall();

        /// <summary>A client-local allocation is about to begin: the port may
        /// capture the operator's current receive and transmit slice OBJECTS,
        /// because the allocation can insert slices ahead of them.</summary>
        void BeginClientLocalAllocation();

        /// <summary>The allocation ended. The port restores the captured
        /// selections only if those objects are still members of the current
        /// list (see <see cref="SliceIdentityRestore"/>), and never after a
        /// cancelled allocation. Never called around a restore.</summary>
        void EndClientLocalAllocation(AllocationResult allocation);

        /// <summary>A FRESH, bounded <c>profile global info</c>: returns the
        /// radio-reported inventory observation that answered it, or the
        /// latest one held when nothing answered inside the bound (which the
        /// caller distinguishes by sequence and provenance). Inside dispatched
        /// work only.</summary>
        InventoryObservation RequestGlobalInventory(int timeoutMs);

        /// <summary>Send the global save. Inside dispatched work only, after
        /// the decision.</summary>
        void SaveGlobalProfile(string name);

        void Trace(string line, bool isError = false);
    }

    /// <summary>
    /// How the coordinator blocks between checks. Production waits on an
    /// event the trackers and the attempt signal; a test's waiter injects
    /// events and advances the fake clock instead, so no test sleeps.
    /// </summary>
    public interface IStationWaiter
    {
        /// <summary>Block for at most <paramref name="maxMs"/> or until woken.</summary>
        void Wait(int maxMs);
    }
}
