using System;
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
                result.Route = GlobalRoute.Refused;
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

        private StationResult LoadExistingRoute(StationResult result, StationDeadline phase, string name, StationPolicyFacts factsAtPlan)
        {
            // Arm BEFORE the send: whatever arrives during dispatch or before
            // the wait begins is retained by sequence.
            var stationBefore = _station.Snapshot();
            long profileSeqAtSend = _profiles.Sequence;
            long stationSeqAtSend = _station.Sequence;
            bool sent = false;
            string refusal = null;
            string rejection = null;
            long sentAtMs = 0;

            _port.Dispatch("stewardship global load '" + name + "'", () =>
            {
                // The recheck. Read on the UI side and assume the queued
                // command is still authorised is exactly the gap this closes.
                // The operation and the phase deadline are part of it: a
                // delegate that finally runs after either has ended is stale
                // work, not a late success.
                if (_op.IsEnded) { refusal = "the operation ended before the load was sent: " + _op.WhyNotLive; return; }
                if (phase.Passed(_clock)) { refusal = "the queued load ran after the station phase had ended"; return; }
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
                _port.SendGlobalLoad(name, why => { rejection = why ?? "rejected"; _attempt.Signal(); });
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

            if (refusal != null)
            {
                Trace("global load NOT sent: " + refusal);
                result.Route = GlobalRoute.Refused;
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
                    ElapsedSinceSendMs = _clock.NowMs - sentAtMs,
                };
                var completion = _policies.LoadCompletion.Judge(evidence);

                if (completion == LoadCompletion.Rejected)
                {
                    return Finish(result, StationOutcome.Failed, GlobalRoute.LoadExisting,
                        "the radio rejected the global load: " + evidence.RejectionText);
                }
                if (completion == LoadCompletion.Confirmed)
                {
                    // No top-up. A saved one-slice layout stays one slice.
                    result.Allocation.Stop = AllocationStop.RouteForbids;
                    result.Allocation.Note = "a restored profile is never topped up";
                    return Finish(result, StationOutcome.RestoredConfirmed, GlobalRoute.LoadExisting,
                        "the load's completion evidence was observed (policy: " + _policies.LoadCompletion.Name + ")");
                }
                if (_op.IsEnded)
                {
                    result.LoadOutstanding = true;
                    return Finish(result, StationOutcome.Cancelled, GlobalRoute.LoadExisting,
                        _op.WhyNotLive + "; the load was sent and its effect is unknown");
                }
                if (completion == LoadCompletion.NotProvable)
                {
                    result.LoadOutstanding = true;
                    result.Allocation.Stop = AllocationStop.LoadOutstanding;
                    return Finish(result, StationOutcome.Unconfirmed, GlobalRoute.LoadExisting,
                        "the load was sent; completion is not provable under policy '" + _policies.LoadCompletion.Name
                        + "'. Retaining whatever arrives; no default fill; no dependent writes");
                }

                // Invalidation after send: the command may still act. Stop
                // dependent work, keep the uncertainty, never roll back.
                var factsNow = _port.ReadPolicyFacts();
                var rosterNow = RosterGuard.ForAutomaticWrite(_roster.Snapshot(), _policies.RosterAuthority);
                if (factsNow.HoldArmed || rosterNow.Verdict == RosterVerdict.OthersPresent)
                {
                    result.LoadOutstanding = true;
                    result.Allocation.Stop = AllocationStop.LoadOutstanding;
                    return Finish(result, StationOutcome.Unconfirmed, GlobalRoute.LoadExisting,
                        "permission changed after the load was sent (" + (factsNow.HoldArmed ? "hold armed" : rosterNow.ToString())
                        + "); the command may still act, so nothing further is written");
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

            var allocation = Allocate(phase);
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
            return Finish(result, StationOutcome.PolicySkipped, result.Route, why + "; fresh allocation: " + allocation);
        }

        private AllocationResult Allocate(StationDeadline phase)
        {
            var alloc = new AllocationResult();
            int own = _station.Snapshot().OwnSliceCount;
            int capacity = _port.CapacityRemaining();
            int legacy = _port.LegacyFreshTarget();

            // The legacy target reconciled with CURRENT capacity. The latch is
            // a startup observation and is never the saved layout's size.
            int target;
            if (legacy > 0 && capacity >= 0) target = Math.Min(legacy, own + capacity);
            else if (legacy > 0) target = legacy;
            else if (capacity > 0) target = own + capacity;
            else target = own;
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

                // One request in flight. Arm before sending: the identity test
                // is "an own slice reported after this sequence whose index
                // was not present before", never a count.
                var before = _station.Snapshot();
                long armedSeq = _station.Sequence;
                bool requested = false;
                _port.Dispatch("request panafall " + (alloc.Requests + 1), () =>
                {
                    if (_op.IsEnded) return;
                    _port.RequestPanafall();
                    requested = true;
                    _attempt.Signal();
                });
                alloc.Requests++;

                var requestBound = phase.Clip(_clock, _deadlines.AllocationRequestMs);
                bool gotOne = false;
                while (true)
                {
                    var now = _station.Snapshot();
                    if (requested && now.NewSince(armedSeq, before).Any()) { gotOne = true; break; }
                    if (_op.IsEnded) break;
                    if (requestBound.Passed(_clock)) break;
                    _waiter.Wait(Math.Min(25, requestBound.RemainingMs(_clock)));
                }

                if (_op.IsEnded) { alloc.Stop = AllocationStop.Cancelled; break; }
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
