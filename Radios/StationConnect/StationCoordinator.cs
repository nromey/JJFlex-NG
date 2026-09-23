using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.StationConnect
{
    /// <summary>
    /// Station establishment, steps 1 to 7 of the station-first design: hold
    /// before intent, ownership gate, live roster, one bounded global read,
    /// the four-route global decision with its recheck inside the dispatched
    /// delegate, Sent-not-Loaded with a pluggable completion policy, and the
    /// bounded fresh-station allocator. Returns one of seven outcomes.
    /// </summary>
    /// <remarks>
    /// <para><b>What this is not.</b> Not a second profile store, not an
    /// ownership policy, not GUI identity. The per-type rules stay in
    /// <see cref="ProfileStewardship"/>; this is the phased caller around
    /// them, with its facts and waits made explicit.</para>
    /// <para><b>Waiting.</b> Every wait is on the monotonic clock, clipped to
    /// its phase, and ends on evidence, cancellation, invalidation or the
    /// deadline. A deadline that passes is an Unconfirmed or Failed result,
    /// never a success. Policies may say <c>Pending</c> (keep waiting) or
    /// <c>Unknown</c> / <c>NotProvable</c> (stop now: waiting cannot help), so
    /// the fail-closed defaults cost a connect nothing but the refusal.</para>
    /// <para><b>Threading.</b> Runs on the caller's thread, which in production
    /// is the command thread before its dequeue loop (or the same thread
    /// inside the loop, for the post-import entry). Trackers are fed from
    /// FlexLib's receive thread and publish copies; nothing here holds a lock
    /// across a wait.</para>
    /// </remarks>
    public sealed class StationCoordinator
    {
        private readonly IStationPort _port;
        private readonly RosterTracker _roster;
        private readonly StationTracker _station;
        private readonly ProfileEvidenceLog _profiles;
        private readonly StationPolicies _policies;
        private readonly IStationClock _clock;
        private readonly ConnectionAttempt _attempt;
        private readonly StationOperation _op;
        private readonly StationResult _previous;
        private readonly StationDeadlines _deadlines;
        private readonly IStationWaiter _waiter;
        private long _ownHandleSeenAtMs = -1;

        /// <param name="operation">The operation this run belongs to. Every
        /// command and wait checks it is still live; a later operation on the
        /// same connection ends it, and a delegate it queued then refuses.</param>
        /// <param name="previousOnThisConnection">The result of the previous
        /// operation on the SAME connection, or null. A load it sent whose
        /// completion was never established is still outstanding: this run
        /// sends no second load and allocates nothing over it.</param>
        public StationCoordinator(
            IStationPort port, RosterTracker roster, StationTracker station, ProfileEvidenceLog profiles,
            StationPolicies policies, IStationClock clock, StationOperation operation,
            StationDeadlines deadlines, IStationWaiter waiter, StationResult previousOnThisConnection = null)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
            _roster = roster ?? throw new ArgumentNullException(nameof(roster));
            _station = station ?? throw new ArgumentNullException(nameof(station));
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _policies = policies ?? StationPolicies.Defaults();
            _clock = clock ?? MonotonicStationClock.Instance;
            _op = operation ?? throw new ArgumentNullException(nameof(operation));
            _attempt = _op.Attempt;
            _previous = previousOnThisConnection;
            _deadlines = deadlines ?? StationDeadlines.Default();
            _waiter = waiter ?? new NoWaiter();
        }

        /// <summary>The result of the last <see cref="Run"/>, for callers that
        /// read it after the fact.</summary>
        public StationResult Result { get; private set; }

        // ------------------------------------------------------------------
        // The run
        // ------------------------------------------------------------------

        public StationResult Run()
        {
            var result = new StationResult
            {
                AttemptGeneration = _attempt.Generation,
                OperationGeneration = _op.Generation,
                Policies = _policies.Describe(),
            };
            Result = result;
            var phase = StationDeadline.In(_clock, _deadlines.StationPhaseMs);

            Trace("station phase begins for " + _op + " with policies: " + _policies.Describe());

            // ── Step 2: hold before intent, ownership before anything shared ──
            var facts = _port.ReadPolicyFacts();
            result.WantedGlobal = facts.WantedGlobal ?? "";
            if (_op.IsEnded) return Finish(result, StationOutcome.Cancelled, GlobalRoute.None, _op.WhyNotLive);
            if (!facts.Connected) return Finish(result, StationOutcome.Failed, GlobalRoute.None, "not connected at entry");

            // A load sent by an earlier operation on THIS connection whose
            // effect was never established is still acting, for all anybody
            // can tell. Re-entering (the post-import path) must not send a
            // second load over it or fill around it. The barrier persists
            // until the connection ends; a new connection starts clean.
            if (_previous != null && _previous.LoadOutstanding && _previous.AttemptGeneration == _attempt.Generation)
            {
                result.LoadOutstanding = true;
                result.Allocation.Stop = AllocationStop.LoadOutstanding;
                result.Allocation.Note = "a load from operation " + _previous.OperationGeneration + " is outstanding";
                return Finish(result, StationOutcome.Unconfirmed, GlobalRoute.LoadExisting,
                    "a global load sent earlier on this connection (operation " + _previous.OperationGeneration
                    + ") is still outstanding; no second load, no allocation, nothing layered over it");
            }

            string skip = AutomaticStewardshipRefusal(facts);
            if (skip != null)
            {
                result.Route = GlobalRoute.Refused;
                return FreshStationRoute(result, phase, StationOutcome.PolicySkipped, skip);
            }

            // ── Step 3: the live roster, for the OWNER'S automatic load ──
            var rosterJudgement = WaitForRoster(phase.Clip(_clock, _deadlines.RosterSettleMs));
            result.RosterAtDecision = rosterJudgement;
            if (_op.IsEnded) return Finish(result, StationOutcome.Cancelled, GlobalRoute.None, _op.WhyNotLive);
            if (rosterJudgement.Verdict != RosterVerdict.OnlyUs)
            {
                // Case 2 of the 2026-09-22 ruling: the owner, with someone
                // else on. The profile is NOT loaded over them. Free slices
                // are allocated client-locally (under D) and the owner's saved
                // frequencies are placed on them, per-client, touching
                // nothing of the other operator's. Case 3 (offer the full
                // load when they leave) is the adapter's, from this flag.
                result.Route = GlobalRoute.Refused;
                result.OwnerRefusedForCompany = rosterJudgement.Verdict == RosterVerdict.OthersPresent;
                return FreshStationRoute(result, phase, StationOutcome.PolicySkipped,
                    "the roster does not authorise an automatic shared write: " + rosterJudgement);
            }

            // ── Step 4: the facts needed to choose the global route ──
            var situation = _port.ReadGlobalSituation(phase.Clip(_clock, _deadlines.ProfileReadMs).RemainingMs(_clock));
            if (_op.IsEnded) return Finish(result, StationOutcome.Cancelled, GlobalRoute.None, _op.WhyNotLive);
            if (situation == null)
            {
                result.Route = GlobalRoute.Refused;
                return FreshStationRoute(result, phase, StationOutcome.PolicySkipped, "the global profile situation could not be read");
            }
            var stationNow = _station.Snapshot();
            situation.OnlyStation = true;                       // established above, this generation
            situation.StationPresent = stationNow.StationPresent; // #563's predicate, fed from evidence

            // ── Step 5: the global-only decision, existing policy order, under the ruling ──
            var plan = ProfileStewardship.PlanConnectRuled(situation, new[] { ProfileTypes.global });
            var load = plan.Actions.FirstOrDefault(a =>
                a.Kind == ProfileActionKind.LoadOurs && a.ProfileType == ProfileTypes.global);

            if (plan.Skipped(ProfileTypes.global, ProfileSkipReason.AlreadyLoaded))
            {
                result.Route = GlobalRoute.ExistingStation;
                var boundary = _policies.InitialMaterialization.Judge(MaterializationEvidenceNow());
                if (boundary == MaterializationBoundary.Pending)
                {
                    boundary = WaitForMaterialization(phase);
                }
                if (boundary == MaterializationBoundary.Ended)
                {
                    return Finish(result, StationOutcome.ExistingStationConfirmed, GlobalRoute.ExistingStation,
                        "the wanted global is selected, our station is present and initial materialization has ended");
                }
                if (_op.IsEnded) return Finish(result, StationOutcome.Cancelled, GlobalRoute.ExistingStation, _op.WhyNotLive);
                return Finish(result, StationOutcome.Unconfirmed, GlobalRoute.ExistingStation,
                    "the wanted global is selected and " + stationNow.OwnSliceCount
                    + " own slice(s) are present, but the end of initial materialization is not established (policy: "
                    + _policies.InitialMaterialization.Name + ")");
            }

            if (load != null && !load.MayCreate)
            {
                result.Route = GlobalRoute.LoadExisting;
                return LoadExistingRoute(result, phase, load.ProfileName, facts);
            }

            if (load != null && load.MayCreate)
            {
                // Route 3, #578: definitely missing on a Mine, opted-in radio.
                // No load for an absent name; no restoration claim; remember
                // the EFFECTIVE per-radio name; fresh station under the
                // bounded allocator; creation armed only on a confirmed one.
                result.Route = GlobalRoute.MissingOwnedGlobal;
                result.PendingCreateName = load.ProfileName;
                Trace("the wanted global '" + load.ProfileName + "' is absent from the radio's reported inventory; "
                      + "sending no load for it and claiming no restoration (#578)");
                return FreshStationRoute(result, phase, StationOutcome.FreshStationConfirmed,
                    "missing owned global '" + load.ProfileName + "'");
            }

            var firstSkip = plan.Skips.FirstOrDefault(s => s.ProfileType == ProfileTypes.global);
            result.Route = GlobalRoute.Refused;
            return FreshStationRoute(result, phase, StationOutcome.PolicySkipped,
                "the planner sent no global load: " + (firstSkip?.Reason.ToString() ?? "no action planned"));
        }

        /// <summary>Step 2's exits, and the #590 ruling, as one sentence or null.</summary>
        internal static string AutomaticStewardshipRefusal(StationPolicyFacts f)
        {
            if (f.HoldArmed) return "the change-nothing hold is armed";
            if (f.Intent == ProfileGuestIntent.NotAnswered) return "the profile question for this radio is not answered";
            if (f.Intent == ProfileGuestIntent.LeaveAlone) return "the operator said to leave this radio's profiles alone";
            if (f.Intent == ProfileGuestIntent.UseMyTransmitAudio) return "the intent is transmit audio only; no global stewardship";
            if (f.Ownership != RadioOwnership.Mine)
                return "this connection is not the declared owner's; only the owner's connection restores a station (ruled 2026-09-21, #590)";
            if (string.IsNullOrWhiteSpace(f.WantedGlobal)) return "no global profile is wanted for this radio";
            return null;
        }

        // ------------------------------------------------------------------
        // Step 3: roster
        // ------------------------------------------------------------------

        private RosterJudgement WaitForRoster(StationDeadline bound)
        {
            while (true)
            {
                var snapshot = _roster.Snapshot();
                if (snapshot.OwnHandleKnown && _ownHandleSeenAtMs < 0) _ownHandleSeenAtMs = _clock.NowMs;
                var judgement = RosterGuard.ForAutomaticWrite(snapshot, _policies.RosterAuthority);
                if (judgement.Verdict != RosterVerdict.Unknown) return judgement;
                if (!judgement.MayChange) return judgement;
                if (_op.IsEnded) return judgement;
                if (bound.Passed(_clock))
                {
                    return new RosterJudgement(RosterVerdict.Unknown,
                        "roster still unknown at the bound: " + judgement.Reason, judgement.Generation);
                }
                _waiter.Wait(Math.Min(25, bound.RemainingMs(_clock)));
            }
        }

        // ------------------------------------------------------------------
        // Step 6: an existing wanted profile requiring a load
        // ------------------------------------------------------------------

        /// <param name="allocatorFallback">True on the connect, where a load
        /// refused at dispatch falls back to the client-local fresh-station
        /// route. False for the operator's explicit request (Track G3): that
        /// operation promised a load or nothing, so a refusal finishes it.</param>
        private StationResult LoadExistingRoute(StationResult result, StationDeadline phase, string name, StationPolicyFacts factsAtPlan, bool allocatorFallback = true)
        {
            // Arm BEFORE the send: whatever arrives during dispatch or before
            // the wait begins is retained by sequence.
            var stationBefore = _station.Snapshot();
            long profileSeqAtSend = _profiles.Sequence;
            long stationSeqAtSend = _station.Sequence;
            bool sent = false;
            string refusal = null;
            string rejection = null;
            bool acknowledged = false;
            long sentAtMs = 0;
            var window = new ActionWindow("global load '" + name + "'");

            _port.Dispatch("stewardship global load '" + name + "'", () =>
            {
                // The recheck. Read on the UI side and assume the queued
                // command is still authorised is exactly the gap this closes.
                // The operation, the phase deadline and this action's own
                // window are part of it: a delegate that finally runs after
                // any of them has ended is stale work, not a late success.
                if (_op.IsEnded) { refusal = "the operation ended before the load was sent: " + _op.WhyNotLive; return; }
                if (phase.Passed(_clock)) { refusal = "the queued load ran after the station phase had ended"; return; }
                if (!window.IsOpen) { refusal = window.Refusal; return; }
                var now = _port.ReadPolicyFacts();
                if (!now.SameAutomaticPermissionAs(factsAtPlan) || AutomaticStewardshipRefusal(now) != null)
                {
                    refusal = "policy changed between planning and dispatch (" + now + ")";
                    return;
                }
                var roster = RosterGuard.ForAutomaticWrite(_roster.Snapshot(), _policies.RosterAuthority);
                if (roster.Verdict != RosterVerdict.OnlyUs)
                {
                    refusal = "roster at dispatch: " + roster;
                    return;
                }
                var inventory = _profiles.Snapshot().GlobalList;
                if (inventory == null || inventory.Provenance != ObservationProvenance.RadioReported || !inventory.Contains(name))
                {
                    refusal = "the wanted global '" + name + "' is not in the radio's reported inventory at dispatch";
                    return;
                }
                // An earlier build's restore point appearing since planning
                // means what is loaded right now is that session's profile,
                // not the owner's state. The planner refuses on it; so does
                // the send.
                if (inventory.Contains(ProfileRestorePoints.NameFor(ProfileTypes.global)))
                {
                    refusal = "a global restore point appeared in the inventory after planning; leaving it for the offered restore";
                    return;
                }

                profileSeqAtSend = _profiles.Sequence;
                stationSeqAtSend = _station.Sequence;
                stationBefore = _station.Snapshot();
                // The radio's reply is acceptance or rejection of the
                // COMMAND. Acceptance is never completion: the load rebuilds
                // the station afterwards, and only the completion policy
                // (bench B) may call that finished.
                _port.SendGlobalLoad(name, reply =>
                {
                    if (reply == null) return;
                    if (reply.Acknowledged) acknowledged = true;
                    else rejection = string.IsNullOrEmpty(reply.Text) ? reply.ToString() : reply.Text;
                    _attempt.Signal();
                });
                sentAtMs = _clock.NowMs;
                sent = true;
                _attempt.Signal();
            });

            // Wait for the delegate itself to have run — a paused queue in a
            // test, or a queued item on the command loop.
            while (!sent && refusal == null && !_op.IsEnded && !phase.Passed(_clock))
            {
                _waiter.Wait(Math.Min(25, phase.RemainingMs(_clock)));
            }
            // Whatever the outcome, this action's result is about to be
            // returned; a delegate still queued must not send after it.
            window.Close(sent ? "sent" : refusal ?? (_op.IsEnded ? "operation ended" : "station phase ended"));

            if (refusal != null)
            {
                Trace("global load NOT sent: " + refusal);
                result.Route = GlobalRoute.Refused;
                if (!allocatorFallback)
                {
                    result.Allocation.Stop = AllocationStop.RouteForbids;
                    result.Allocation.Note = "an explicit requested load allocates nothing; refused-and-finished";
                    return Finish(result, StationOutcome.PolicySkipped, GlobalRoute.Refused, "load refused at dispatch: " + refusal + "; no allocator fallback");
                }
                return FreshStationRoute(result, phase, StationOutcome.PolicySkipped, "load refused at dispatch: " + refusal);
            }
            if (!sent)
            {
                if (_op.IsEnded) return Finish(result, StationOutcome.Cancelled, GlobalRoute.LoadExisting, _op.WhyNotLive);
                return Finish(result, StationOutcome.Failed, GlobalRoute.LoadExisting, "the queued load never ran within the station phase");
            }

            result.LoadSent = true;
            Trace("global load '" + name + "' SENT (not loaded). Waiting for completion evidence under policy: " + _policies.LoadCompletion.Name);

            // ── Sent, not Loaded ──
            while (true)
            {
                var evidence = new LoadEvidence
                {
                    WantedName = name,
                    AttemptGeneration = _attempt.Generation,
                    SentAtProfileSequence = profileSeqAtSend,
                    SentAtStationSequence = stationSeqAtSend,
                    StationBefore = stationBefore,
                    StationNow = _station.Snapshot(),
                    ProfileNow = _profiles.Snapshot(),
                    Rejected = rejection != null,
                    RejectionText = rejection ?? "",
                    Acknowledged = acknowledged,
                    ElapsedSinceSendMs = _clock.NowMs - sentAtMs,
                };
                var completion = _policies.LoadCompletion.Judge(evidence);

                if (completion == LoadCompletion.Rejected)
                {
                    return Finish(result, StationOutcome.Failed, GlobalRoute.LoadExisting,
                        "the radio rejected the global load: " + evidence.RejectionText);
                }

                // Cancellation and permission BEFORE success is accepted.
                // Completion evidence and a cancellation, or a join, can land
                // in the same delivery; a confirmed station on a cancelled or
                // no-longer-permitted operation is not a confirmed station.
                // Once a sent load is invalidated the uncertainty is kept:
                // the command may still act, so nothing rolls back and
                // nothing further is written (review step 6).
                if (_op.IsEnded)
                {
                    result.LoadOutstanding = true;
                    return Finish(result, StationOutcome.Cancelled, GlobalRoute.LoadExisting,
                        _op.WhyNotLive + "; the load was sent and its effect is unknown");
                }
                string invalidation = InvalidationSinceSend(factsAtPlan);
                if (invalidation != null)
                {
                    result.LoadOutstanding = true;
                    result.Allocation.Stop = AllocationStop.LoadOutstanding;
                    return Finish(result, StationOutcome.Unconfirmed, GlobalRoute.LoadExisting,
                        "permission changed after the load was sent (" + invalidation
                        + "); the command may still act, so nothing further is written");
                }

                if (completion == LoadCompletion.Confirmed)
                {
                    // No top-up. A saved one-slice layout stays one slice.
                    result.Allocation.Stop = AllocationStop.RouteForbids;
                    result.Allocation.Note = "a restored profile is never topped up";
                    return Finish(result, StationOutcome.RestoredConfirmed, GlobalRoute.LoadExisting,
                        "the load's completion evidence was observed (policy: " + _policies.LoadCompletion.Name + ")");
                }
                if (completion == LoadCompletion.NotProvable)
                {
                    result.LoadOutstanding = true;
                    result.Allocation.Stop = AllocationStop.LoadOutstanding;
                    return Finish(result, StationOutcome.Unconfirmed, GlobalRoute.LoadExisting,
                        "the load was sent; completion is not provable under policy '" + _policies.LoadCompletion.Name
                        + "'. Retaining whatever arrives; no default fill; no dependent writes");
                }
                if (phase.Passed(_clock))
                {
                    result.LoadOutstanding = true;
                    result.Allocation.Stop = AllocationStop.LoadOutstanding;
                    return Finish(result, StationOutcome.Unconfirmed, GlobalRoute.LoadExisting,
                        "the station phase ended without completion evidence; the load may still be in progress. "
                        + "No second load, no disconnect-to-retry, no default fill");
                }
                _waiter.Wait(Math.Min(25, phase.RemainingMs(_clock)));
            }
        }

        /// <summary>
        /// Everything that withdraws permission for a load already sent, as
        /// one sentence or null: the hold; a changed intent, ownership,
        /// wanted name or connection; and a roster that is no longer only us
        /// — OthersPresent AND Unknown, because an Unknown roster cannot
        /// authorise the dependent writes either.
        /// </summary>
        private string InvalidationSinceSend(StationPolicyFacts factsAtPlan)
        {
            var factsNow = _port.ReadPolicyFacts();
            if (factsNow.HoldArmed) return "hold armed";
            if (!factsNow.SameAutomaticPermissionAs(factsAtPlan)) return "policy facts changed: " + factsNow;
            string refusal = AutomaticStewardshipRefusal(factsNow);
            if (refusal != null) return refusal;
            var rosterNow = RosterGuard.ForAutomaticWrite(_roster.Snapshot(), _policies.RosterAuthority);
            if (rosterNow.Verdict != RosterVerdict.OnlyUs) return rosterNow.ToString();
            return null;
        }

        // ------------------------------------------------------------------
        // Step 7: the protected fresh-station allocation
        // ------------------------------------------------------------------

        /// <summary>
        /// Routes 3 and 4 end here. Allocation runs only when initial
        /// materialization is known to have ended and no load is outstanding.
        /// A completed allocation earns FreshStationConfirmed on the
        /// missing-owned route and PolicySkipped (with the allocation
        /// recorded) on the refused route. <paramref name="outcomeIfEstablished"/>
        /// names the caller's intent for the trace and is checked against the route.
        /// </summary>
        private StationResult FreshStationRoute(StationResult result, StationDeadline phase, StationOutcome outcomeIfEstablished, string why)
        {
            if (outcomeIfEstablished == StationOutcome.FreshStationConfirmed && result.Route != GlobalRoute.MissingOwnedGlobal)
                throw new InvalidOperationException("FreshStationConfirmed is only earned on the missing-owned route");
            if (_op.IsEnded) return Finish(result, StationOutcome.Cancelled, result.Route, _op.WhyNotLive);

            var boundary = _policies.InitialMaterialization.Judge(MaterializationEvidenceNow());
            if (boundary == MaterializationBoundary.Pending) boundary = WaitForMaterialization(phase);
            if (_op.IsEnded) return Finish(result, StationOutcome.Cancelled, result.Route, _op.WhyNotLive);

            if (boundary != MaterializationBoundary.Ended)
            {
                result.Allocation.Stop = AllocationStop.MaterializationUnknown;
                result.Allocation.Note = "policy: " + _policies.InitialMaterialization.Name;
                if (result.OwnerRefusedForCompany)
                {
                    result.Placement.Stop = PlacementStop.NoSlices;
                    result.Placement.Note = "no free slice was allocated (materialization unknown), so nothing to place on";
                }
                if (result.Route == GlobalRoute.MissingOwnedGlobal)
                {
                    // An ambiguous station must not arm a save under the wanted name.
                    result.PendingCreateName = "";
                    return Finish(result, StationOutcome.Unconfirmed, result.Route,
                        why + "; no fresh allocation because the end of initial materialization is not established, so no creation is armed");
                }
                return Finish(result, StationOutcome.PolicySkipped, result.Route,
                    why + "; no fresh allocation because the end of initial materialization is not established");
            }

            // The refused and no-wanted routes preserve a layout that is
            // already there. A completed initial boundary says the radio has
            // finished delivering; it does not turn spare capacity into a
            // request for more slices over what it delivered (review step
            // 7). Only a route with NO own slices allocates client-locally.
            int ownNow = _station.Snapshot().OwnSliceCount;
            if (result.Route != GlobalRoute.MissingOwnedGlobal && ownNow > 0)
            {
                result.Allocation.Stop = AllocationStop.ExistingLayoutPreserved;
                result.Allocation.OwnSlicesAtEnd = ownNow;
                result.Allocation.Target = ownNow;
                result.Allocation.Note = "own slices already present on a route that does not restore; kept as they are";
                return Finish(result, StationOutcome.PolicySkipped, result.Route,
                    why + "; " + ownNow + " own slice(s) already present, preserved; nothing requested");
            }

            // Case 2 (the owner with company): the allocation is BOUND to the
            // remembered layout, read BEFORE anything is requested — two
            // remembered slices on four free slots ask for two, never four
            // (Track G3; re-review section 5). No remembered layout, no
            // request: there is nothing to put on a slice, and padding is
            // what the ruling forbids. Track G2 allocated to the legacy
            // target first and read the layout afterwards.
            StationLayout companyLayout = null;
            int? layoutBound = null;
            if (result.OwnerRefusedForCompany)
            {
                companyLayout = _port.ReadOwnerSavedLayout();
                if (companyLayout == null || companyLayout.IsEmpty)
                {
                    result.Allocation.Stop = AllocationStop.NoTarget;
                    result.Allocation.OwnSlicesAtEnd = ownNow;
                    result.Allocation.Note = "no remembered layout for this radio on this machine; nothing requested (never pad)";
                    result.Placement.Stop = PlacementStop.NoLayoutKnown;
                    result.Placement.Note = "this machine holds no station layout for this radio";
                    return Finish(result, StationOutcome.PolicySkipped, result.Route,
                        why + "; no remembered layout, so no slice was requested and nothing was placed");
                }
                layoutBound = companyLayout.Slices.Count;
            }

            // The operator's current receive and transmit slices are captured
            // around THIS allocation only, because it can insert slices ahead
            // of them; the port restores by identity when it ends. A restored
            // profile is never bracketed this way: nothing pre-restore is
            // replayed over a restored layout.
            long seqBeforeAllocation = _station.Sequence;
            _port.BeginClientLocalAllocation();
            var allocation = Allocate(phase, layoutBound);
            _port.EndClientLocalAllocation(allocation);
            result.Allocation = allocation;
            if (allocation.Stop == AllocationStop.Cancelled)
                return Finish(result, StationOutcome.Cancelled, result.Route, _op.WhyNotLive);

            if (result.Route == GlobalRoute.MissingOwnedGlobal)
            {
                if (allocation.ReachedTargetOrCapacity && allocation.OwnSlicesAtEnd > 0)
                {
                    result.CreationArmed = true;
                    return Finish(result, StationOutcome.FreshStationConfirmed, result.Route,
                        why + "; fresh station established (" + allocation + "); creation of '" + result.PendingCreateName + "' armed for clean disconnect");
                }
                result.PendingCreateName = "";
                return Finish(result, StationOutcome.Failed, result.Route,
                    why + "; fresh allocation stopped (" + allocation + "); partial station retained, no creation armed");
            }

            // The refused route: the global outcome is PolicySkipped whatever
            // the client-local allocation did, and the allocation record says
            // what it did. A timeout that left nothing at all is a Failed
            // connect, because there is no station to operate.
            if (allocation.Stop == AllocationStop.Timeout && allocation.OwnSlicesAtEnd == 0)
            {
                return Finish(result, StationOutcome.Failed, result.Route,
                    why + "; fresh allocation produced nothing: " + allocation);
            }

            // Case 2: the owner's saved frequencies on the free slices just
            // obtained. Small, per-client, confirmable actions where the big
            // unconfirmable one is refused.
            if (result.OwnerRefusedForCompany && allocation.Obtained > 0)
            {
                result.Placement = PlaceOwnerFrequencies(phase, seqBeforeAllocation, allocation, companyLayout);
            }
            else if (result.OwnerRefusedForCompany)
            {
                result.Placement.Stop = PlacementStop.NoSlices;
                result.Placement.Note = "no free slice was obtained to place a frequency on";
            }
            return Finish(result, StationOutcome.PolicySkipped, result.Route,
                why + "; fresh allocation: " + allocation
                + (result.OwnerRefusedForCompany ? "; frequency placement: " + result.Placement : ""));
        }

        // ------------------------------------------------------------------
        // Case 2 of the 2026-09-22 ruling: the owner's frequencies on free slices
        // ------------------------------------------------------------------

        /// <summary>
        /// Put the owner's saved frequencies and modes on the slices the
        /// allocation just obtained, one at a time, each CONFIRMED by the
        /// radio's own reply to the tune command within a bound. Stops on
        /// the first unconfirmed or rejected placement. The layout comes
        /// from this machine's record of the owner's last station; see
        /// <see cref="StationLayout"/>.
        /// </summary>
        /// <remarks>
        /// Until Track G3 the confirmation was a radio-reported tune through
        /// PropertyChanged. FlexLib's setter assigns its cache first and the
        /// vendor skips the status that repeats the cached value, so a
        /// correctly tuned slice reported NOTHING and the placement timed
        /// out on success (Track G2 re-review, section 5). The reply is the
        /// independent acknowledgment; a radio-reported frequency after the
        /// send, when the value changed, is corroboration and is traced.
        /// </remarks>
        private PlacementResult PlaceOwnerFrequencies(StationDeadline phase, long seqBeforeAllocation, AllocationResult allocation, StationLayout layout)
        {
            var placement = new PlacementResult();
            if (layout == null || layout.IsEmpty)
            {
                placement.Stop = PlacementStop.NoLayoutKnown;
                placement.Note = "this machine holds no station layout for this radio";
                Trace("frequency placement: " + placement);
                return placement;
            }
            // The slices the allocation obtained, in index order: the new
            // ones, never anything that was already there.
            var obtained = _station.Snapshot().Slices.Where(x => x.Sequence > seqBeforeAllocation).OrderBy(x => x.Sequence).ToList();
            placement.Wanted = Math.Min(layout.Slices.Count, obtained.Count);
            if (placement.Wanted == 0)
            {
                placement.Stop = PlacementStop.NoSlices;
                return placement;
            }

            for (int i = 0; i < placement.Wanted; i++)
            {
                var entry = layout.Slices[i];
                var slice = obtained[i];
                if (_op.IsEnded) { placement.Stop = PlacementStop.Cancelled; break; }
                if (phase.Passed(_clock)) { placement.Stop = PlacementStop.Unconfirmed; placement.Note = "station phase ended"; break; }

                long armedSeq = -1;
                bool sent = false;
                string refusal = null;
                int expectedReplies = string.IsNullOrEmpty(entry.Mode) ? 1 : 2;
                var replies = new List<CommandReply>();
                var window = new ActionWindow("placement of " + entry + " on slice " + slice.Index);
                _port.Dispatch("place " + entry + " on slice " + slice.Index, () =>
                {
                    if (_op.IsEnded) { refusal = "operation ended"; return; }
                    if (phase.Passed(_clock)) { refusal = "phase ended before the placement was sent"; return; }
                    if (!window.IsOpen) { refusal = window.Refusal; return; }
                    // The current facts, all of them, at the moment of the
                    // write: the hold, and the ownership and intent that
                    // made this the owner's connection in the first place.
                    var factsNow = _port.ReadPolicyFacts();
                    if (factsNow.HoldArmed) { refusal = "the hold was armed"; return; }
                    if (!factsNow.Connected) { refusal = "not connected"; return; }
                    if (factsNow.Ownership != RadioOwnership.Mine) { refusal = "the radio is no longer declared ours (" + factsNow.Ownership + ")"; return; }
                    if (factsNow.Intent == ProfileGuestIntent.LeaveAlone || factsNow.Intent == ProfileGuestIntent.NotAnswered)
                    { refusal = "the intent for this radio is now " + factsNow.Intent; return; }
                    if (!_station.Snapshot().HasSlice(slice.Index)) { refusal = "slice " + slice.Index + " is no longer ours"; return; }
                    armedSeq = _station.Sequence;
                    refusal = _port.SetSliceFrequencyAndMode(slice.Index, entry.FreqHz, entry.Mode, reply =>
                    {
                        if (reply == null) return;
                        lock (replies) replies.Add(reply);
                        _attempt.Signal();
                    });
                    if (refusal != null) return;
                    sent = true;
                    _attempt.Signal();
                });
                var bound = phase.Clip(_clock, _deadlines.FrequencyPlacementMs);
                while (!sent && refusal == null && !_op.IsEnded && !bound.Passed(_clock))
                {
                    _waiter.Wait(Math.Min(25, bound.RemainingMs(_clock)));
                }
                // This placement's bound is its send cutoff, not the phase's.
                window.Close(sent ? "sent" : refusal ?? (_op.IsEnded ? "operation ended" : "the placement's " + _deadlines.FrequencyPlacementMs + " ms bound ended"));
                if (refusal != null) { placement.Stop = PlacementStop.Refused; placement.Note = refusal; break; }
                if (!sent) { placement.Stop = _op.IsEnded ? PlacementStop.Cancelled : PlacementStop.Unconfirmed; placement.Note = "the placement was never dispatched"; break; }
                placement.Sent++;

                // Confirmed by the radio's reply to EVERY command sent for
                // this slice; rejected by any non-zero reply; unconfirmed
                // when a reply does not arrive inside the bound.
                bool confirmed = false;
                CommandReply rejected = null;
                while (true)
                {
                    List<CommandReply> got;
                    lock (replies) got = replies.ToList();
                    rejected = got.FirstOrDefault(r => !r.Acknowledged);
                    if (rejected != null) break;
                    if (got.Count >= expectedReplies) { confirmed = true; break; }
                    if (_op.IsEnded || bound.Passed(_clock)) break;
                    _waiter.Wait(Math.Min(25, bound.RemainingMs(_clock)));
                }
                if (rejected != null)
                {
                    placement.Stop = PlacementStop.Rejected;
                    placement.Note = "the radio rejected the tune of slice " + slice.Index + ": " + rejected;
                    break;
                }
                if (!confirmed)
                {
                    placement.Stop = _op.IsEnded ? PlacementStop.Cancelled : PlacementStop.Unconfirmed;
                    placement.Note = "the radio did not acknowledge the tune of slice " + slice.Index + " to " + entry.FreqHz
                        + " Hz within " + _deadlines.FrequencyPlacementMs + " ms";
                    break;
                }
                placement.Placed++;
                // Corroboration only: a changed frequency is also reported by
                // the radio's status; an unchanged one is not, by the vendor's
                // equal-value skip, and that absence means nothing.
                Trace("slice " + slice.Index + " acknowledged " + entry
                    + (_station.Snapshot().TunedSince(armedSeq, slice.Index, entry.FreqHz)
                        ? "; the radio also reported the frequency" : "; no frequency report (equal-value status is suppressed by FlexLib)"));
            }
            if (placement.Stop == PlacementStop.NotAttempted) placement.Stop = PlacementStop.Completed;
            Trace("frequency placement: " + placement);
            return placement;
        }

        // ------------------------------------------------------------------
        // Case 3 of the 2026-09-22 ruling: the operator asked for the load
        // ------------------------------------------------------------------

        /// <summary>
        /// The full global load at the OPERATOR'S request, after the other
        /// operator has left and a dialog was answered yes. Never automatic.
        /// The same recheck inside the dispatched delegate as the connect's
        /// load (hold, ownership, intent, strict roster, inventory), the same
        /// completion judgement, and never a top-up. It does not take the
        /// existing-station shortcut: an explicit request is a request.
        /// </summary>
        public StationResult RunOperatorRequestedLoad()
        {
            var result = new StationResult
            {
                AttemptGeneration = _attempt.Generation,
                OperationGeneration = _op.Generation,
                Policies = _policies.Describe(),
            };
            Result = result;
            var phase = StationDeadline.In(_clock, _deadlines.StationPhaseMs);
            Trace("operator-requested global load begins for " + _op);

            var facts = _port.ReadPolicyFacts();
            result.WantedGlobal = facts.WantedGlobal ?? "";
            if (_op.IsEnded) return Finish(result, StationOutcome.Cancelled, GlobalRoute.None, _op.WhyNotLive);
            if (!facts.Connected) return Finish(result, StationOutcome.Failed, GlobalRoute.None, "not connected");
            string skip = AutomaticStewardshipRefusal(facts);
            if (skip != null) return Finish(result, StationOutcome.PolicySkipped, GlobalRoute.Refused, skip);
            if (_previous != null && _previous.LoadOutstanding && _previous.AttemptGeneration == _attempt.Generation)
            {
                result.LoadOutstanding = true;
                return Finish(result, StationOutcome.Unconfirmed, GlobalRoute.LoadExisting,
                    "a global load sent earlier on this connection is still outstanding; no second load");
            }
            var roster = RosterGuard.ForAutomaticWrite(_roster.Snapshot(), _policies.RosterAuthority);
            result.RosterAtDecision = roster;
            if (roster.Verdict != RosterVerdict.OnlyUs)
            {
                return Finish(result, StationOutcome.PolicySkipped, GlobalRoute.Refused, "roster: " + roster);
            }
            var inventory = _profiles.Snapshot().GlobalList;
            if (inventory == null || inventory.Provenance != ObservationProvenance.RadioReported || !inventory.Contains(facts.WantedGlobal))
            {
                return Finish(result, StationOutcome.PolicySkipped, GlobalRoute.Refused,
                    "the wanted global '" + facts.WantedGlobal + "' is not in the radio's reported inventory");
            }
            result.Route = GlobalRoute.LoadExisting;
            return LoadExistingRoute(result, phase, facts.WantedGlobal, facts, allocatorFallback: false);
        }

        /// <param name="layoutBound">When set, the number of slices the
        /// remembered layout has: the target is that, clipped to current
        /// capacity, and the legacy startup latch is not consulted at all.</param>
        private AllocationResult Allocate(StationDeadline phase, int? layoutBound = null)
        {
            var alloc = new AllocationResult();
            int own = _station.Snapshot().OwnSliceCount;
            int capacity = _port.CapacityRemaining();
            int legacy = _port.LegacyFreshTarget();
            // A hold already armed at planning does not stop the client-local
            // receive allocation (the refused route's ruled resource); a hold
            // ARMED SINCE planning is a change the delegate must see (Track
            // G2 re-review, step 7).
            bool holdAtPlan = _port.ReadPolicyFacts().HoldArmed;

            int target;
            if (layoutBound.HasValue)
            {
                // Bound to the remembered layout, never padded to capacity.
                int wanted = own + Math.Max(0, layoutBound.Value);
                target = capacity >= 0 ? Math.Min(wanted, own + capacity) : wanted;
            }
            else
            {
                // The legacy target reconciled with CURRENT capacity. The
                // latch is a startup observation and is never the saved
                // layout's size.
                if (legacy > 0 && capacity >= 0) target = Math.Min(legacy, own + capacity);
                else if (legacy > 0) target = legacy;
                else if (capacity > 0) target = own + capacity;
                else target = own;
            }
            alloc.Target = target;

            if (target <= own)
            {
                alloc.OwnSlicesAtEnd = own;
                alloc.Stop = target == 0 && own == 0 ? AllocationStop.NoTarget : AllocationStop.TargetReached;
                alloc.Note = target == 0 && own == 0 ? "no capacity and no legacy target" : "already at target";
                return alloc;
            }

            while (true)
            {
                own = _station.Snapshot().OwnSliceCount;
                if (own >= target) { alloc.Stop = AllocationStop.TargetReached; break; }
                if (_op.IsEnded) { alloc.Stop = AllocationStop.Cancelled; break; }
                if (phase.Passed(_clock)) { alloc.Stop = AllocationStop.Timeout; alloc.Note = "station phase ended"; break; }
                capacity = _port.CapacityRemaining();
                if (capacity == 0) { alloc.Stop = AllocationStop.CapacityExhausted; break; }

                // One request in flight. The observation is armed INSIDE the
                // dispatched delegate, immediately before the send — not at
                // queue time. A slice reported between queueing and the actual
                // request predates the request and cannot be attributed to
                // it (review step 7). The delegate also rechecks the operation,
                // the phase deadline and the capacity at the moment it runs.
                StationSnapshot before = null;
                long armedSeq = -1;
                bool requested = false;
                string refusal = null;
                var window = new ActionWindow("panafall request " + (alloc.Requests + 1));
                _port.Dispatch("request panafall " + (alloc.Requests + 1), () =>
                {
                    if (_op.IsEnded) { refusal = "operation ended: " + _op.WhyNotLive; return; }
                    if (phase.Passed(_clock)) { refusal = "the queued request ran after the station phase had ended"; return; }
                    if (!window.IsOpen) { refusal = window.Refusal; return; }
                    if (!holdAtPlan && _port.ReadPolicyFacts().HoldArmed) { refusal = "the hold was armed after this allocation was planned"; return; }
                    if (_port.CapacityRemaining() == 0) { refusal = "no capacity remained when the request was about to be sent"; return; }
                    before = _station.Snapshot();
                    armedSeq = _station.Sequence;
                    _port.RequestPanafall();
                    requested = true;
                    _attempt.Signal();
                });
                alloc.Requests++;

                var requestBound = phase.Clip(_clock, _deadlines.AllocationRequestMs);
                bool gotOne = false;
                while (true)
                {
                    if (refusal != null) break;
                    var now = _station.Snapshot();
                    if (requested && now.NewSince(armedSeq, before).Any()) { gotOne = true; break; }
                    if (_op.IsEnded) break;
                    if (requestBound.Passed(_clock)) break;
                    _waiter.Wait(Math.Min(25, requestBound.RemainingMs(_clock)));
                }
                // The request's own bound is its send cutoff (review step 7):
                // a delegate the loop releases after this must not send.
                window.Close(requested ? "sent" : refusal ?? (_op.IsEnded ? "operation ended" : "the request's " + _deadlines.AllocationRequestMs + " ms bound ended"));

                if (_op.IsEnded) { alloc.Stop = AllocationStop.Cancelled; break; }
                if (refusal != null)
                {
                    // Never actually sent: the count of requests the radio saw
                    // goes back down, and the stop says why.
                    alloc.Requests--;
                    alloc.Stop = refusal.Contains("capacity") ? AllocationStop.CapacityExhausted : AllocationStop.Timeout;
                    alloc.Note = "request not sent: " + refusal;
                    break;
                }
                if (!requested)
                {
                    // Still queued when the bound ended: not sent, and now it
                    // cannot be. Count it as never made.
                    alloc.Requests--;
                    alloc.Stop = AllocationStop.Timeout;
                    alloc.Note = "the request was never dispatched inside its " + _deadlines.AllocationRequestMs + " ms bound; a late run refuses";
                    break;
                }
                if (!gotOne)
                {
                    // Timeout or denial: stop. There is no loop retrying a
                    // request that never changed the count (#588).
                    alloc.Stop = AllocationStop.Timeout;
                    alloc.Note = requested
                        ? "no new own slice within " + _deadlines.AllocationRequestMs + " ms of the request"
                        : "the request was never dispatched";
                    break;
                }
                alloc.Obtained++;
            }

            alloc.OwnSlicesAtEnd = _station.Snapshot().OwnSliceCount;
            return alloc;
        }

        // ------------------------------------------------------------------
        // Materialization (bench D)
        // ------------------------------------------------------------------

        private MaterializationEvidence MaterializationEvidenceNow()
        {
            var roster = _roster.Snapshot();
            var station = _station.Snapshot();
            if (roster.OwnHandleKnown && _ownHandleSeenAtMs < 0) _ownHandleSeenAtMs = _clock.NowMs;
            return new MaterializationEvidence
            {
                Roster = roster,
                Station = station,
                Profiles = _profiles.Snapshot(),
                ElapsedSinceOwnHandleMs = _ownHandleSeenAtMs < 0 ? -1 : _clock.NowMs - _ownHandleSeenAtMs,
                ElapsedSinceLastStationObservationMs = station.LastObservedAtMs == 0 ? -1 : _clock.NowMs - station.LastObservedAtMs,
            };
        }

        private MaterializationBoundary WaitForMaterialization(StationDeadline phase)
        {
            while (true)
            {
                var b = _policies.InitialMaterialization.Judge(MaterializationEvidenceNow());
                if (b != MaterializationBoundary.Pending) return b;
                if (_op.IsEnded || phase.Passed(_clock)) return MaterializationBoundary.Unknown;
                _waiter.Wait(Math.Min(25, phase.RemainingMs(_clock)));
            }
        }

        // ------------------------------------------------------------------

        private StationResult Finish(StationResult r, StationOutcome outcome, GlobalRoute route, string reason)
        {
            r.Outcome = outcome;
            if (route != GlobalRoute.None) r.Route = route;
            r.Reason = reason ?? "";
            r.OwnSlicesAtEnd = _station.Snapshot().OwnSliceCount;
            if (r.Allocation.Stop == AllocationStop.NotAttempted && !r.StationConfirmed && outcome != StationOutcome.PolicySkipped)
            {
                r.Allocation.Stop = AllocationStop.RouteForbids;
            }
            Trace("station phase ends: " + r, error: outcome == StationOutcome.Failed);
            return r;
        }

        private void Trace(string line, bool error = false) => _port.Trace("StationConnect: " + line, isError: error);

        private sealed class NoWaiter : IStationWaiter
        {
            public void Wait(int maxMs) { }
        }
    }
}
