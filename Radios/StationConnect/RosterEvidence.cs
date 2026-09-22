using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.StationConnect
{
    /// <summary>One GUI client as the radio (or discovery) reported it.</summary>
    public sealed class RosterEntry
    {
        public RosterEntry(uint handle, string clientId, bool isThisClient, string station, string program)
            : this(handle, clientId, isThisClient, station, program, reportedGoneByDiscovery: false)
        {
        }

        private RosterEntry(uint handle, string clientId, bool isThisClient, string station, string program, bool reportedGoneByDiscovery)
        {
            Handle = handle;
            ClientId = clientId ?? "";
            IsThisClient = isThisClient;
            Station = station ?? "";
            Program = program ?? "";
            ReportedGoneByDiscovery = reportedGoneByDiscovery;
        }

        public uint Handle { get; }
        public string ClientId { get; }
        public bool IsThisClient { get; }
        public string Station { get; }
        public string Program { get; }

        /// <summary>
        /// True when the only thing that has said this client left is a
        /// discovery packet that did not list it. Discovery can omit a
        /// still-live client (design, bench question A), so the record is
        /// KEPT and the client is treated as present — ruled 2026-09-22,
        /// "ambiguous removals treated as present" — until the radio's own
        /// status either lists it again (the flag clears) or reports it
        /// disconnected (the record goes).
        /// </summary>
        public bool ReportedGoneByDiscovery { get; }

        internal RosterEntry WithReportedGoneByDiscovery(bool value) =>
            new RosterEntry(Handle, ClientId, IsThisClient, Station, Program, value);

        /// <summary>
        /// True when the record carries identity facts: FlexLib stamped it as
        /// ours, or it holds a client_id, which only the radio's own TCP status
        /// supplies. A record without either was rebuilt from a discovery
        /// packet. <b>It is still a handle</b> — the naming-collision
        /// workaround in guiClientAdded ignores such records for one narrow
        /// purpose, and the design is explicit that this is not proof another
        /// operator is absent (section 1 step 3).
        /// </summary>
        public bool IdentityBearing => IsThisClient || !string.IsNullOrEmpty(ClientId);

        public override string ToString() =>
            "0x" + Handle.ToString("X") + (IsThisClient ? " (ours)" : "")
            + (IdentityBearing ? "" : " (no client_id)")
            + (string.IsNullOrEmpty(Station) ? "" : " " + Station)
            + (ReportedGoneByDiscovery ? " (discovery says gone; treated as present)" : "");
    }

    /// <summary>
    /// Where a removal came from. FlexLib raises the same event for both,
    /// and they mean different things: the radio's own TCP status saying
    /// "client disconnected" is the radio speaking; a discovery packet that
    /// did not list the client is a UDP broadcast that can lag or omit.
    /// </summary>
    public enum RosterRemovalOrigin
    {
        /// <summary>The radio's own status stream reported the client
        /// disconnected. The record is removed.</summary>
        RadioStatus,

        /// <summary>A discovery packet did not list the client. The record is
        /// kept and marked; the client is treated as present.</summary>
        Discovery,
    }

    /// <summary>What the last roster observation was, for the authority policy.</summary>
    public enum RosterChangeKind
    {
        None,
        OwnHandleEstablished,
        Added,
        Updated,
        Removed,
    }

    /// <summary>
    /// An immutable copy of the roster as last observed, with the facts a
    /// policy needs to judge its authority. Published by
    /// <see cref="RosterTracker.Snapshot"/>; never mutated.
    /// </summary>
    public sealed class RosterSnapshot
    {
        public const uint NoHandle = 0xffffffff;

        public RosterSnapshot(
            IReadOnlyList<RosterEntry> entries, uint ownHandle, long generation,
            long observedAtMs, int attemptGeneration,
            RosterChangeKind lastChange, RosterEntry lastChangedEntry)
        {
            Entries = entries ?? Array.Empty<RosterEntry>();
            OwnHandle = ownHandle;
            Generation = generation;
            ObservedAtMs = observedAtMs;
            AttemptGeneration = attemptGeneration;
            LastChange = lastChange;
            LastChangedEntry = lastChangedEntry;
        }

        public IReadOnlyList<RosterEntry> Entries { get; }

        /// <summary>Our client handle once the radio has told us; <see cref="NoHandle"/> until then.</summary>
        public uint OwnHandle { get; }

        public bool OwnHandleKnown => OwnHandle != NoHandle;

        /// <summary>Increments on every roster observation. A permission
        /// computed at one generation is stale at the next.</summary>
        public long Generation { get; }

        public long ObservedAtMs { get; }

        public int AttemptGeneration { get; }

        public RosterChangeKind LastChange { get; }

        public RosterEntry LastChangedEntry { get; }

        /// <summary>
        /// True when at least one OTHER client's record has been reported gone
        /// by discovery alone and the radio's own status has not yet said
        /// either way. Such a client is treated as PRESENT (ruled
        /// 2026-09-22); this flag exists so a policy or a sentence can say
        /// that the presence is discovery-ambiguous rather than reported.
        /// Until Track G2 this was a tracker-wide Boolean set by ANY other
        /// removal and cleared by ANY identity-bearing add or update — so a
        /// clean TCP leave never cleared it, and an unrelated update of our
        /// own record did. Both were wrong (review section 3A).
        /// </summary>
        public bool RemovalUnconfirmed =>
            Entries.Any(e => e.ReportedGoneByDiscovery && (!OwnHandleKnown || e.Handle != OwnHandle));

        public RosterEntry Own => OwnHandleKnown ? Entries.FirstOrDefault(e => e.Handle == OwnHandle) : null;

        public IEnumerable<RosterEntry> Others => Entries.Where(e => !OwnHandleKnown || e.Handle != OwnHandle);

        public override string ToString() =>
            "roster gen " + Generation + " own=" + (OwnHandleKnown ? "0x" + OwnHandle.ToString("X") : "?")
            + " [" + string.Join(", ", Entries.Select(e => e.ToString())) + "]"
            + (RemovalUnconfirmed ? " removal-unconfirmed" : "");
    }

    /// <summary>
    /// The live roster, maintained from the production client handlers and
    /// recomputed on every add, update, remove and own-handle establishment.
    /// Replaces the once-at-own-add <c>OnlyStation</c> snapshot (#577).
    /// </summary>
    /// <remarks>
    /// Lock scope is one dictionary write; snapshots are copies. Callbacks
    /// from FlexLib's receive thread publish and return — they never wait.
    /// FlexLib raises the discovery-driven removal while holding its own
    /// roster lock, which is one more reason nothing here may block.
    /// </remarks>
    public sealed class RosterTracker
    {
        private readonly object _lock = new object();
        private readonly Dictionary<uint, RosterEntry> _entries = new Dictionary<uint, RosterEntry>();
        private readonly IStationClock _clock;
        private uint _ownHandle = RosterSnapshot.NoHandle;
        private long _generation;
        private int _attemptGeneration;
        private RosterChangeKind _lastChange = RosterChangeKind.None;
        private RosterEntry _lastChangedEntry;

        public RosterTracker(IStationClock clock)
        {
            _clock = clock ?? MonotonicStationClock.Instance;
        }

        /// <summary>Raised after every publish. Wake the coordinator; do no work.</summary>
        public event Action Changed;

        /// <summary>Start a new attempt: forget the previous connection's roster
        /// entirely. A stale entry from the last connection is exactly the
        /// ghost #402 caught.</summary>
        public void Reset(int attemptGeneration)
        {
            lock (_lock)
            {
                _entries.Clear();
                _ownHandle = RosterSnapshot.NoHandle;
                _attemptGeneration = attemptGeneration;
                _generation++;
                _lastChange = RosterChangeKind.None;
                _lastChangedEntry = null;
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// Import the roster the vendor object ALREADY holds when the
        /// handlers are wired. Discovery fills FlexLib's client list before
        /// we connect, and no add event fires for a record that is already
        /// there — so without this, a client present at attachment was
        /// invisible until it happened to change (review section 3A, "the
        /// already-present-at-attachment case"). Entries come from discovery,
        /// so they carry no client_id and are not identity-bearing; the
        /// radio's own status updates them once we are connected.
        /// </summary>
        public void Seed(IEnumerable<RosterEntry> entries, int attemptGeneration)
        {
            if (entries == null) return;
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                foreach (var e in entries)
                {
                    if (e == null || _entries.ContainsKey(e.Handle)) continue;
                    _entries[e.Handle] = e;
                    _generation++;
                    _lastChange = RosterChangeKind.Added;
                    _lastChangedEntry = e;
                }
            }
            Changed?.Invoke();
        }

        public void OwnHandleEstablished(uint handle, int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _ownHandle = handle;
                _generation++;
                _lastChange = RosterChangeKind.OwnHandleEstablished;
                _entries.TryGetValue(handle, out _lastChangedEntry);
            }
            Changed?.Invoke();
        }

        public void ClientAdded(RosterEntry entry, int attemptGeneration)
        {
            if (entry == null) return;
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                // An add or update of THIS handle is that client present
                // again, whatever discovery said meanwhile. It says nothing
                // about any other handle.
                _entries[entry.Handle] = entry;
                _generation++;
                _lastChange = RosterChangeKind.Added;
                _lastChangedEntry = entry;
            }
            Changed?.Invoke();
        }

        public void ClientUpdated(RosterEntry entry, int attemptGeneration)
        {
            if (entry == null) return;
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _entries[entry.Handle] = entry;
                _generation++;
                _lastChange = RosterChangeKind.Updated;
                _lastChangedEntry = entry;
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// A removal, with WHERE it came from. The radio's own status
        /// removing a client is the client gone. Discovery not listing a
        /// client is ambiguous: the record stays, marked, and the client is
        /// treated as present until the radio's own status speaks for that
        /// handle (ruled 2026-09-22). Provenance is preserved here rather
        /// than reconstructed later, because a Boolean that both origins set
        /// cannot be un-mixed by any policy (review section 3A).
        /// </summary>
        public void ClientRemoved(uint handle, int attemptGeneration, RosterRemovalOrigin origin)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _entries.TryGetValue(handle, out var existing);
                _generation++;
                _lastChange = RosterChangeKind.Removed;
                _lastChangedEntry = existing;
                if (origin == RosterRemovalOrigin.RadioStatus)
                {
                    _entries.Remove(handle);
                }
                else if (existing != null)
                {
                    _entries[handle] = existing.WithReportedGoneByDiscovery(true);
                }
            }
            Changed?.Invoke();
        }

        /// <summary>A removal of unstated origin is treated as the ambiguous
        /// kind: kept and marked. Callers that know the origin say so.</summary>
        public void ClientRemoved(uint handle, int attemptGeneration) =>
            ClientRemoved(handle, attemptGeneration, RosterRemovalOrigin.Discovery);

        public RosterSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new RosterSnapshot(
                    _entries.Values.OrderBy(e => e.Handle).ToList(),
                    _ownHandle, _generation, _clock.NowMs, _attemptGeneration,
                    _lastChange, _lastChangedEntry);
            }
        }

        public long Generation { get { lock (_lock) return _generation; } }
    }

    /// <summary>What the roster says about company on the radio.</summary>
    public enum RosterVerdict
    {
        /// <summary>Our handle is present and no other handle is. For an
        /// automatic shared write this additionally required the authority
        /// policy's agreement.</summary>
        OnlyUs,

        /// <summary>At least one handle that is not ours is present. Membership
        /// alone decides this; identity is not required to refuse.</summary>
        OthersPresent,

        /// <summary>Cannot tell. Own handle unknown or absent, an unconfirmed
        /// removal, or the roster's authority not established. Refuses every
        /// automatic shared write and is named as uncertainty, never as
        /// "somebody is connected".</summary>
        Unknown,
    }

    /// <summary>A verdict with the sentence that explains it, for the trace.</summary>
    public sealed class RosterJudgement
    {
        public RosterJudgement(RosterVerdict verdict, string reason, long generation, bool mayChange = false)
        {
            Verdict = verdict;
            Reason = reason ?? "";
            Generation = generation;
            MayChange = mayChange;
        }
        public RosterVerdict Verdict { get; }
        public string Reason { get; }
        public long Generation { get; }

        /// <summary>True when an Unknown verdict could be settled by a later
        /// roster observation, so a bounded wait is worth spending. False
        /// when it is Unknown by policy and waiting cannot change it.</summary>
        public bool MayChange { get; }
        public override string ToString() => Verdict + " (" + Reason + ")";
    }

    /// <summary>Whether a roster snapshot may be trusted as complete and current.</summary>
    public enum RosterAuthority
    {
        /// <summary>Bench question A has established that this shape of snapshot
        /// is a complete, current enumeration of live clients.</summary>
        Authoritative,

        /// <summary>Not yet, but a later roster observation may settle it
        /// (for example our own record has not received its client_id). The
        /// coordinator keeps waiting inside the roster bound.</summary>
        Pending,

        /// <summary>Not established, and no further observation on this
        /// transport will establish it. The default until the bench says
        /// otherwise. The coordinator refuses at once rather than waiting out
        /// a bound for an answer that cannot change.</summary>
        Unknown,
    }

    /// <summary>
    /// <b>Bench question A's plug.</b> Given a roster snapshot, is it an
    /// authoritative account of who is on the radio? The default answer is
    /// <see cref="RosterAuthority.Unknown"/>, and Unknown refuses the owner's
    /// automatic global load. The bench teaches this policy what to match on;
    /// nothing else in the connect path changes when it does.
    /// </summary>
    public interface IRosterAuthorityPolicy
    {
        string Name { get; }
        RosterAuthority Judge(RosterSnapshot snapshot);
    }

    /// <summary>
    /// The default: no roster snapshot is authoritative. Until a receive-only
    /// bench on a real radio shows what a complete roster boundary looks like
    /// (TCP status enumeration, discovery snapshot, or neither), a recomputed
    /// Count == 1 is not evidence that nobody else is there.
    /// </summary>
    public sealed class RosterAuthorityUnknownPolicy : IRosterAuthorityPolicy
    {
        public static readonly RosterAuthorityUnknownPolicy Instance = new RosterAuthorityUnknownPolicy();
        public string Name => "unknown until bench A";
        public RosterAuthority Judge(RosterSnapshot snapshot) => RosterAuthority.Unknown;
    }

    /// <summary>
    /// A policy that accepts a roster as authoritative once our own handle is
    /// identity-bearing and no removal is pending. <b>Not the default.</b> It
    /// exists so the bench result can be plugged in without writing a class,
    /// and so tests have a positive control that proves the guard passes
    /// when the policy says yes. Whether this is the RIGHT contract is
    /// exactly what bench A decides.
    /// </summary>
    public sealed class RosterAuthorityFromRadioStatusPolicy : IRosterAuthorityPolicy
    {
        public static readonly RosterAuthorityFromRadioStatusPolicy Instance = new RosterAuthorityFromRadioStatusPolicy();
        public string Name => "radio status: own record identity-bearing, no unconfirmed removal";
        public RosterAuthority Judge(RosterSnapshot snapshot)
        {
            if (snapshot == null) return RosterAuthority.Pending;
            var own = snapshot.Own;
            if (own == null || !own.IdentityBearing) return RosterAuthority.Pending;
            if (snapshot.RemovalUnconfirmed) return RosterAuthority.Pending;
            return RosterAuthority.Authoritative;
        }
    }

    /// <summary>
    /// <b>The ruled authority (Noel, 2026-09-22): live membership from the
    /// radio's own status.</b> The roster is authoritative once OUR record is
    /// identity-bearing — the radio's TCP status has enumerated us with a
    /// client_id, which it does for every connected client — and a client
    /// that discovery alone says has gone is treated as present by the
    /// tracker, so this policy never has to reason about removals. Pending
    /// until our record carries identity; never Unknown.
    /// </summary>
    /// <remarks>
    /// <para><b>The limited guarantee, named.</b> This trusts that the
    /// radio's status stream enumerates every connected client and reports
    /// every departure. The 2026-09-21 SmartLink trace showed both for the
    /// observed join and leave (client events 973 ms and 487 ms ahead of
    /// discovery); it did not prove completeness for every firmware or
    /// transport. It is the ruling's answer, not a bench-proven contract, and
    /// the tracker's discovery-as-present rule is what makes it conservative
    /// where the two sources disagree.</para>
    /// </remarks>
    public sealed class RosterAuthorityByLiveMembershipPolicy : IRosterAuthorityPolicy
    {
        public static readonly RosterAuthorityByLiveMembershipPolicy Instance = new RosterAuthorityByLiveMembershipPolicy();
        public string Name => "live membership from the radio's own status; discovery-only removals count as present (ruled 2026-09-22)";
        public RosterAuthority Judge(RosterSnapshot snapshot)
        {
            if (snapshot == null) return RosterAuthority.Pending;
            var own = snapshot.Own;
            if (own == null || !own.IdentityBearing) return RosterAuthority.Pending;
            return RosterAuthority.Authoritative;
        }
    }

    /// <summary>
    /// The live roster guard (#577): membership AND identity, never count
    /// alone, recomputed from the current snapshot every time it is asked.
    /// </summary>
    public static class RosterGuard
    {
        /// <summary>
        /// What the roster shows right now, for an operator-initiated verb
        /// that will tell the operator the answer. Does not consult the
        /// authority policy: the operator is pressing a button and is told
        /// what the roster shows, including that it cannot tell.
        /// </summary>
        public static RosterJudgement Evaluate(RosterSnapshot s)
        {
            if (s == null) return new RosterJudgement(RosterVerdict.Unknown, "no roster observed", 0, mayChange: true);
            if (!s.OwnHandleKnown)
                return new RosterJudgement(RosterVerdict.Unknown, "our own client handle is not established yet", s.Generation, mayChange: true);
            if (s.Own == null)
                return new RosterJudgement(RosterVerdict.Unknown, "the roster does not contain our own handle", s.Generation, mayChange: true);

            var others = s.Others.ToList();
            if (others.Count > 0)
            {
                // A client discovery says has gone counts as present: the
                // radio's own status has not said so, and discovery can omit
                // a live client (ruled 2026-09-22).
                bool anyAmbiguous = others.Any(o => o.ReportedGoneByDiscovery);
                return new RosterJudgement(RosterVerdict.OthersPresent,
                    others.Count + " other client handle(s) present: " + string.Join(", ", others.Select(o => o.ToString()))
                    + (anyAmbiguous ? " — a discovery-only disappearance is treated as still present until the radio's own status confirms" : ""),
                    s.Generation);
            }
            return new RosterJudgement(RosterVerdict.OnlyUs, "our handle is the only one in the roster", s.Generation);
        }

        /// <summary>
        /// The verdict an AUTOMATIC shared write must obtain. Everything
        /// <see cref="Evaluate"/> requires, plus: no unconfirmed removal, and
        /// the authority policy agreeing that this snapshot is complete.
        /// Unknown here is a refusal.
        /// </summary>
        public static RosterJudgement ForAutomaticWrite(RosterSnapshot s, IRosterAuthorityPolicy policy)
        {
            var basic = Evaluate(s);
            if (basic.Verdict != RosterVerdict.OnlyUs) return basic;

            // A discovery-only disappearance never reaches here as OnlyUs:
            // the entry is retained and Evaluate reports it present. There is
            // no separate "removal unconfirmed" refusal any more because the
            // ambiguity is carried on the entry, not on the tracker.
            var p = policy ?? RosterAuthorityUnknownPolicy.Instance;
            var authority = p.Judge(s);
            if (authority == RosterAuthority.Pending)
            {
                return new RosterJudgement(RosterVerdict.Unknown,
                    "the roster shows only us, but its authority is not established yet (policy: " + p.Name + ")",
                    s.Generation, mayChange: true);
            }
            if (authority != RosterAuthority.Authoritative)
            {
                return new RosterJudgement(RosterVerdict.Unknown,
                    "the roster shows only us, but its authority is not established (policy: " + p.Name + ")",
                    s.Generation);
            }
            return new RosterJudgement(RosterVerdict.OnlyUs,
                "our handle is the only one in an authoritative roster (policy: " + p.Name + ")",
                s.Generation);
        }
    }
}
