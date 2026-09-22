using System;

namespace Radios.StationConnect
{
    /// <summary>How the disconnect-time create ended.</summary>
    public enum CreationOutcome
    {
        NotAttempted,
        /// <summary>The decision inside the delegate said no. Nothing sent.</summary>
        Refused,
        /// <summary>The queued delegate never ran inside its bound; a late run
        /// refuses on the deadline. Nothing sent.</summary>
        NotSent,
        /// <summary>The save went out and the radio's inventory reported the
        /// name afterwards.</summary>
        Confirmed,
        /// <summary>The save went out and no radio-reported inventory named
        /// it within the bound. Retained as uncertain: not claimed saved, and
        /// not to be sent again.</summary>
        Unconfirmed,
    }

    public sealed class CreationResult
    {
        public CreationOutcome Outcome = CreationOutcome.NotAttempted;
        public string Reason = "";
        public bool SaveSent;
        public override string ToString() => Outcome + (string.IsNullOrEmpty(Reason) ? "" : " — " + Reason);
    }

    /// <summary>
    /// Design step 11's execution: the decision is made INSIDE the dispatched
    /// save delegate, against a FRESH inventory request, with a live
    /// operation and the phase deadline checked at that moment; a queue item
    /// that outlives its bound is invalidated; a save whose readback never
    /// arrives is retained as uncertain (review step 11). Until Track G2 the
    /// decision ran before DispatchStationWork against the last cached list,
    /// the delegate contained no recheck, and a timeout was a silent
    /// "unconfirmed" with the pending intent cleared.
    /// </summary>
    public sealed class DeferredCreationRun
    {
        private readonly IStationPort _port;
        private readonly ProfileEvidenceLog _profiles;
        private readonly RosterTracker _roster;
        private readonly StationPolicies _policies;
        private readonly IStationClock _clock;
        private readonly IStationWaiter _waiter;
        private readonly StationDeadlines _deadlines;

        public DeferredCreationRun(
            IStationPort port, ProfileEvidenceLog profiles, RosterTracker roster, StationPolicies policies,
            IStationClock clock, IStationWaiter waiter, StationDeadlines deadlines)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _roster = roster ?? throw new ArgumentNullException(nameof(roster));
            _policies = policies ?? StationPolicies.Defaults();
            _clock = clock ?? MonotonicStationClock.Instance;
            _waiter = waiter ?? new NoWaiter();
            _deadlines = deadlines ?? StationDeadlines.Default();
        }

        /// <param name="pending">The armed create.</param>
        /// <param name="operation">The teardown operation this runs under.</param>
        /// <param name="lastStation">The station result the connect ended
        /// with, for the outcome and outstanding-load conditions.</param>
        public CreationResult Run(PendingGlobalCreation pending, StationOperation operation, StationResult lastStation)
        {
            var result = new CreationResult();
            if (pending == null) { result.Reason = "nothing pending"; return result; }
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            // The whole create — the fresh inventory ask, the decision and
            // the save — has one bound. A delegate that runs after it refuses.
            var deadline = StationDeadline.In(_clock, _deadlines.ProfileReadMs + _deadlines.DisconnectCreateConfirmMs);
            bool sent = false;
            string refusal = null;
            long sentAtSeq = -1;

            _port.Dispatch("save new global '" + pending.Name + "'", () =>
            {
                if (operation.IsEnded) { refusal = "the operation ended before the save was sent: " + operation.WhyNotLive; return; }
                if (deadline.Passed(_clock)) { refusal = "the queued save ran after its bound had ended"; return; }

                // A FRESH inventory, asked for now, not the last cached list.
                var inventory = _port.RequestGlobalInventory(Math.Min(_deadlines.ProfileReadMs, deadline.RemainingMs(_clock)));
                if (operation.IsEnded) { refusal = "the operation ended while the inventory was being read"; return; }

                var facts = _port.ReadPolicyFacts();
                var decision = DeferredGlobalCreation.Decide(new CreationFacts
                {
                    Pending = pending,
                    Attempt = operation.Attempt,
                    Connected = facts.Connected,
                    HoldArmed = facts.HoldArmed,
                    Ownership = facts.Ownership,
                    Intent = facts.Intent,
                    WantedGlobalNow = facts.WantedGlobal,
                    Serial = facts.Serial,
                    Roster = RosterGuard.ForAutomaticWrite(_roster.Snapshot(), _policies.RosterAuthority),
                    Inventory = inventory,
                    StationOutcome = lastStation?.Outcome ?? StationOutcome.Unconfirmed,
                    LoadOutstanding = lastStation?.LoadOutstanding ?? true,
                });
                if (!decision.Create) { refusal = decision.Reason; return; }

                sentAtSeq = _profiles.Sequence;
                _port.SaveGlobalProfile(pending.Name);
                sent = true;
                operation.Signal();
            });

            while (!sent && refusal == null && !operation.IsEnded && !deadline.Passed(_clock))
            {
                _waiter.Wait(Math.Min(25, deadline.RemainingMs(_clock)));
            }

            if (refusal != null)
            {
                result.Outcome = CreationOutcome.Refused;
                result.Reason = refusal;
                _port.Trace("saveNewGlobalProfile: NOT creating " + pending + " — " + refusal, isError: false);
                return result;
            }
            if (!sent)
            {
                result.Outcome = CreationOutcome.NotSent;
                result.Reason = operation.IsEnded ? "the operation ended before the queued save ran" : "the queued save did not run within its bound; a late run refuses";
                _port.Trace("saveNewGlobalProfile: " + result.Reason, isError: true);
                return result;
            }

            result.SaveSent = true;
            var readback = StationDeadline.In(_clock, _deadlines.DisconnectCreateConfirmMs);
            while (true)
            {
                var inv = _profiles.Snapshot().GlobalList;
                if (inv != null && inv.Provenance == ObservationProvenance.RadioReported && inv.Sequence > sentAtSeq && inv.Contains(pending.Name))
                {
                    result.Outcome = CreationOutcome.Confirmed;
                    result.Reason = "the radio's inventory now lists '" + pending.Name + "'";
                    _port.Trace("saveNewGlobalProfile: " + result.Reason);
                    return result;
                }
                if (readback.Passed(_clock) || operation.Attempt.IsCancelled)
                {
                    result.Outcome = CreationOutcome.Unconfirmed;
                    result.Reason = "the save command went out but the radio did not report '" + pending.Name + "' within "
                        + _deadlines.DisconnectCreateConfirmMs + " ms — UNCONFIRMED, not claimed saved, not sent again";
                    _port.Trace("saveNewGlobalProfile: " + result.Reason, isError: true);
                    return result;
                }
                _waiter.Wait(Math.Min(25, readback.RemainingMs(_clock)));
            }
        }

        private sealed class NoWaiter : IStationWaiter
        {
            public void Wait(int maxMs) { }
        }
    }
}
