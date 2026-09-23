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
        /// after the recheck. <paramref name="onReply"/> receives the radio's
        /// reply to the load command when it arrives (on the receive thread):
        /// an acknowledgment is ACCEPTANCE of the command, never completion
        /// of the load, which remains the completion policy's question; a
        /// non-zero code is a rejection. The production port sends through
        /// FlexLib's reply-bearing path, not the setter, so the reply is
        /// real and the vendor cache is not pre-assigned.</summary>
        void SendGlobalLoad(string name, Action<CommandReply> onReply);

        /// <summary>Ask for one new panadapter-and-slice. Inside dispatched work only.</summary>
        void RequestPanafall();

        /// <summary>The owner's station layout as THIS MACHINE last recorded
        /// it for the connected radio, or null when none is known. The radio
        /// cannot be asked for a saved profile's contents (see
        /// <see cref="StationLayout"/>).</summary>
        StationLayout ReadOwnerSavedLayout();

        /// <summary>
        /// Tune one of OUR slices: a per-client write. Inside dispatched work
        /// only. Sends one command per field (the mode when non-empty, then
        /// the frequency) through FlexLib's reply-bearing path and delivers
        /// each reply to <paramref name="onReply"/> on the receive thread.
        /// The acknowledgment is the confirmation: FlexLib's own setter
        /// discards the success reply and the vendor suppresses the
        /// equal-value status, so PropertyChanged carries no ordinary
        /// success (see <see cref="CommandReply"/>). Returns null when the
        /// commands went out, else why they did not (no radio, slice not
        /// ours, slice locked, transport down).
        /// </summary>
        string SetSliceFrequencyAndMode(int sliceIndex, long freqHz, string mode, Action<CommandReply> onReply);

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
