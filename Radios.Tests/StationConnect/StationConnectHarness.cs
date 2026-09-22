using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>Virtual monotonic time. Advanced by hand; never sleeps.</summary>
    internal sealed class FakeStationClock : IStationClock
    {
        public long NowMs { get; private set; } = 1_000_000;
        public void Advance(int ms) => NowMs += Math.Max(0, ms);
    }

    /// <summary>
    /// The port a test drives. Records every command the coordinator sends,
    /// exposes the policy facts and the fake inventory the test sets, and can
    /// HOLD dispatched work so a test can change the world between planning
    /// and sending — the dispatch-race tests of design section 6, group 3.
    /// </summary>
    internal sealed class FakeStationPort : IStationPort
    {
        public StationPolicyFacts Facts = new StationPolicyFacts
        {
            Connected = true,
            HoldArmed = false,
            Ownership = RadioOwnership.Mine,
            Intent = ProfileGuestIntent.LoadMineAndPutBack,
            WantedGlobal = "K5NER",
            Serial = "1234-5678-9012-3456",
        };

        /// <summary>The radio's global inventory as the bounded read reports it.</summary>
        public List<string> InventoryNames = new List<string> { "Default", "K5NER" };
        public bool InventoryReported = true;
        /// <summary>The radio's current global selection; null = unreadable.</summary>
        public string Selection = "Default";

        public int Capacity = 4;
        public int LegacyTarget = 4;

        public bool HoldDispatch;
        public readonly List<(string name, Action work)> Held = new List<(string, Action)>();
        public readonly List<string> GlobalLoadsSent = new List<string>();
        public int PanafallRequests;
        public string RejectNextLoad;
        public readonly List<string> Trace = new List<string>();

        /// <summary>Called inside SendGlobalLoad, after the send is recorded,
        /// so a test can deliver evidence synchronously during dispatch.</summary>
        public Action<string> OnGlobalLoadSent;

        /// <summary>Called inside RequestPanafall, so a test can deliver (or
        /// withhold) the resulting slice.</summary>
        public Action OnPanafallRequested;

        public int ReadPolicyFactsCalls;
        public int GlobalSituationReads;

        public StationPolicyFacts ReadPolicyFacts()
        {
            ReadPolicyFactsCalls++;
            return new StationPolicyFacts
            {
                Connected = Facts.Connected,
                HoldArmed = Facts.HoldArmed,
                Ownership = Facts.Ownership,
                Intent = Facts.Intent,
                WantedGlobal = Facts.WantedGlobal,
                Serial = Facts.Serial,
            };
        }

        public ProfileSituation ReadGlobalSituation(int timeoutMs)
        {
            GlobalSituationReads++;
            var s = new ProfileSituation
            {
                Connected = Facts.Connected,
                ChangeNothingArmed = Facts.HoldArmed,
                Ownership = Facts.Ownership,
                Intent = Facts.Intent,
            };
            s.Types.Add(new ProfileTypeState
            {
                ProfileType = ProfileTypes.global,
                Reported = InventoryReported,
                Names = InventoryNames.ToList(),
                Selection = Selection,
                Wanted = Facts.WantedGlobal,
            });
            return s;
        }

        public int CapacityRemaining() => Capacity;
        public int LegacyFreshTarget() => LegacyTarget;

        public void Dispatch(string name, Action work)
        {
            if (HoldDispatch) Held.Add((name, work));
            else work();
        }

        /// <summary>Run everything held, in order, as the command loop would,
        /// and run anything dispatched afterwards inline: the loop is up now.</summary>
        public void ReleaseHeld()
        {
            HoldDispatch = false;
            var items = Held.ToList();
            Held.Clear();
            foreach (var (_, work) in items) work();
        }

        public void SendGlobalLoad(string name, Action<string> onRejected)
        {
            GlobalLoadsSent.Add(name);
            if (RejectNextLoad != null)
            {
                var why = RejectNextLoad;
                RejectNextLoad = null;
                onRejected?.Invoke(why);
            }
            OnGlobalLoadSent?.Invoke(name);
        }

        public void RequestPanafall()
        {
            PanafallRequests++;
            OnPanafallRequested?.Invoke();
        }

        /// <summary>What a FRESH inventory request answers with, when it
        /// answers; null means the radio does not answer inside the bound.</summary>
        public List<string> FreshInventoryNames;
        public int InventoryRequests;
        public readonly List<string> GlobalSavesSent = new List<string>();
        public Action<string> OnGlobalSaved;
        /// <summary>Set by the harness so the fake can feed the evidence log.</summary>
        public Action<IReadOnlyList<string>> DeliverFreshInventory;
        public Func<InventoryObservation> LatestInventory;

        public InventoryObservation RequestGlobalInventory(int timeoutMs)
        {
            InventoryRequests++;
            if (FreshInventoryNames != null) DeliverFreshInventory?.Invoke(FreshInventoryNames.ToList());
            return LatestInventory?.Invoke();
        }

        public void SaveGlobalProfile(string name)
        {
            GlobalSavesSent.Add(name);
            OnGlobalSaved?.Invoke(name);
        }

        void IStationPort.Trace(string line, bool isError) => Trace.Add((isError ? "ERROR " : "") + line);
    }

    /// <summary>
    /// The coordinator's waits, scripted. Each Wait runs the next step the
    /// test queued (deliver an event, change a fact, cancel); when the script
    /// is exhausted, time simply advances by the requested amount so every
    /// deadline expires deterministically. No test sleeps.
    /// </summary>
    internal sealed class ScriptedWaiter : IStationWaiter
    {
        private readonly FakeStationClock _clock;
        private readonly Queue<Action> _steps = new Queue<Action>();
        public int Waits;

        /// <summary>Runs on EVERY wait, before the scripted step. For "the
        /// world changes while we wait" tests.</summary>
        public Action OnEveryWait;

        public ScriptedWaiter(FakeStationClock clock) { _clock = clock; }

        public ScriptedWaiter Then(Action step) { _steps.Enqueue(step); return this; }

        public void Wait(int maxMs)
        {
            Waits++;
            OnEveryWait?.Invoke();
            if (_steps.Count > 0) _steps.Dequeue()();
            else _clock.Advance(Math.Max(1, maxMs));
        }
    }

    /// <summary>
    /// One connection attempt's worth of production trackers, the fake port,
    /// the fake clock and the coordinator, with helpers that speak in events.
    /// </summary>
    internal sealed class StationHarness
    {
        public const uint OurHandle = 0x1A2B3C4D;
        public const uint OtherHandle = 0x5E6F7A8B;

        public readonly FakeStationClock Clock = new FakeStationClock();
        public readonly FakeStationPort Port = new FakeStationPort();
        public readonly RosterTracker Roster;
        public readonly StationTracker Station;
        public readonly ProfileEvidenceLog Profiles;
        public readonly StationPolicies Policies;
        public readonly StationDeadlines Deadlines = StationDeadlines.Default();
        public ConnectionAttempt Attempt;
        public readonly ScriptedWaiter Waiter;

        private int _nextSliceIndex;

        /// <summary>
        /// The positive-control policies: the shapes the design describes for
        /// each contract, so the happy path can be shown to send exactly the
        /// intended command. NOT the production defaults; the defaults are
        /// tested separately for refusing.
        /// </summary>
        public static StationPolicies PositiveControlPolicies() => new StationPolicies
        {
            RosterAuthority = RosterAuthorityByLiveMembershipPolicy.Instance,
            LoadCompletion = LoadCompletionByRadioEndBoundaryPolicy.Instance,
            InitialMaterialization = MaterializationEndsAtOwnHandlePolicy.Instance,
        };

        public StationHarness(StationPolicies policies = null)
        {
            Policies = policies ?? PositiveControlPolicies();
            Roster = new RosterTracker(Clock);
            Station = new StationTracker(Clock);
            Profiles = new ProfileEvidenceLog(Clock);
            Waiter = new ScriptedWaiter(Clock);
            Port.DeliverFreshInventory = names => Profiles.GlobalListObserved(names, ObservationProvenance.RadioReported, Gen);
            Port.LatestInventory = () => Profiles.Snapshot().GlobalList;
            NewAttempt();
        }

        /// <summary>The disconnect-time create under a fresh teardown operation.</summary>
        public CreationResult RunCreation(PendingGlobalCreation pending, StationResult lastStation)
        {
            Operation = Attempt.BeginOperation("teardown");
            return new DeferredCreationRun(Port, Profiles, Roster, Policies, Clock, Waiter, Deadlines)
                .Run(pending, Operation, lastStation);
        }

        public int Gen => Attempt.Generation;

        public void NewAttempt()
        {
            Attempt?.Cancel("replaced by a new attempt");
            Attempt = new ConnectionAttempt(Port.Facts.Serial);
            Roster.Reset(Attempt.Generation);
            Station.Reset(Attempt.Generation);
            Profiles.Reset(Attempt.Generation);
            var attempt = Attempt;
            Roster.Changed += attempt.Signal;
            Station.Changed += attempt.Signal;
            Profiles.Changed += attempt.Signal;
        }

        /// <summary>The operation the most recent coordinator was built for.</summary>
        public StationOperation Operation { get; private set; }

        /// <summary>A coordinator on a NEW operation of the current attempt,
        /// carrying <paramref name="previous"/> forward as the previous
        /// operation's result on this connection.</summary>
        public StationCoordinator Coordinator(StationResult previous = null)
        {
            Operation = Attempt.BeginOperation("test run");
            return new StationCoordinator(
                Port, Roster, Station, Profiles, Policies, Clock, Operation, Deadlines, Waiter, previous);
        }

        public StationResult Run() => Coordinator().Run();

        // ── roster events, as the production handlers would feed them ──

        public void OurClientAdded(string clientId = "our-client-id")
        {
            Roster.ClientAdded(new RosterEntry(OurHandle, clientId, true, "K5NER", "JJFlex"), Gen);
            Roster.OwnHandleEstablished(OurHandle, Gen);
        }

        public void OtherClientAdded(uint handle = OtherHandle, string clientId = "other-client-id", string station = "W1AW")
            => Roster.ClientAdded(new RosterEntry(handle, clientId, false, station, "SmartSDR"), Gen);

        public void OtherClientUpdated(uint handle = OtherHandle, string clientId = "other-client-id", string station = "W1AW")
            => Roster.ClientUpdated(new RosterEntry(handle, clientId, false, station, "SmartSDR"), Gen);

        /// <summary>The radio's own status reports the client disconnected.</summary>
        public void ClientRemoved(uint handle) => Roster.ClientRemoved(handle, Gen, RosterRemovalOrigin.RadioStatus);

        /// <summary>A discovery packet did not list the client.</summary>
        public void ClientRemovedByDiscovery(uint handle) => Roster.ClientRemoved(handle, Gen, RosterRemovalOrigin.Discovery);

        /// <summary>A radio-reported global inventory.</summary>
        public void RadioReportsGlobalList(params string[] names) =>
            Profiles.GlobalListObserved(names, ObservationProvenance.RadioReported, Gen);

        public void RadioReportsGlobalSelection(string name) =>
            Profiles.GlobalSelectionObserved(name, ObservationProvenance.RadioReported, Gen);

        public void LocalEchoSelection(string name) =>
            Profiles.GlobalSelectionObserved(name, ObservationProvenance.LocalEcho, Gen);

        public void PersistenceLoaded() => Profiles.PersistenceLoadedObserved(Gen);

        public void RadioEndBoundary(string token = "profile-load-complete") => Profiles.RadioEndBoundary(token, Gen);

        /// <summary>An own slice FlexLib reported ready. Returns its index.</summary>
        public int OwnSliceArrives(uint pan = 0x40000001)
        {
            int index = _nextSliceIndex++;
            Station.OwnSliceAdded(index, ((char)('A' + index)).ToString(), OurHandle, pan, Gen);
            return index;
        }

        public void OwnSliceRemoved(int index) => Station.OwnSliceRemoved(index, Gen);

        /// <summary>The radio delivers one slice for each panafall request.</summary>
        public void RadioHonoursPanafallRequests()
        {
            Port.OnPanafallRequested = () =>
            {
                OwnSliceArrives();
                if (Port.Capacity > 0) Port.Capacity--;
            };
        }

        /// <summary>A healthy, owned, opted-in radio with our client alone on
        /// it, the wanted profile present, the name selected with NO station
        /// (the #563 reconnect), and an inventory the radio has reported.</summary>
        public void ArrangeOwnerReconnect()
        {
            OurClientAdded();
            RadioReportsGlobalList(Port.InventoryNames.ToArray());
            Port.Selection = Port.Facts.WantedGlobal;
        }

        /// <summary>Deliver the completion evidence the positive-control policy accepts.</summary>
        public void DeliverGenuineCompletion()
        {
            OwnSliceArrives();
            RadioEndBoundary();
        }

        // ── assertions the brief insists on: the exact action AND zero of each competitor ──

        public void AssertExactlyOneLoadAndNothingElse(string name)
        {
            Assert.Equal(new[] { name }, Port.GlobalLoadsSent);
            Assert.Equal(0, Port.PanafallRequests);
        }

        public void AssertNothingWasSent()
        {
            Assert.Empty(Port.GlobalLoadsSent);
            Assert.Equal(0, Port.PanafallRequests);
        }
    }
}
