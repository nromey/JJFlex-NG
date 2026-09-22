using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios.StationConnect
{
    /// <summary>One GUI client as the radio (or discovery) reported it.</summary>
    public sealed class RosterEntry
    {
        public RosterEntry(uint handle, string clientId, bool isThisClient, string station, string program)
        {
            Handle = handle;
            ClientId = clientId ?? "";
            IsThisClient = isThisClient;
            Station = station ?? "";
            Program = program ?? "";
        }

        public uint Handle { get; }
        public string ClientId { get; }
        public bool IsThisClient { get; }
        public string Station { get; }
        public string Program { get; }

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
            + (string.IsNullOrEmpty(Station) ? "" : " " + Station);
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
            RosterChangeKind lastChange, RosterEntry lastChangedEntry, bool removalUnconfirmed)
        {
            Entries = entries ?? Array.Empty<RosterEntry>();
            OwnHandle = ownHandle;
            Generation = generation;
            ObservedAtMs = observedAtMs;
            AttemptGeneration = attemptGeneration;
            LastChange = lastChange;
            LastChangedEntry = lastChangedEntry;
            RemovalUnconfirmed = removalUnconfirmed;
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
        /// True when the most recent change was the removal of another
        /// client's record and nothing since has confirmed the roster from the
        /// radio's own status. Discovery can delete a still-live record
        /// (design, bench question A), so a removal is ambiguous until the
        /// bench says otherwise. It clears at the next add or update.
        /// </summary>
        public bool RemovalUnconfirmed { get; }

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
        private bool _removalUnconfirmed;

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
                _removalUnconfirmed = false;
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
                _entries[entry.Handle] = entry;
                _generation++;
                _lastChange = RosterChangeKind.Added;
                _lastChangedEntry = entry;
                // A fresh identity-bearing record is the radio's own status
                // speaking; that settles any earlier ambiguous removal.
                if (entry.IdentityBearing) _removalUnconfirmed = false;
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
                if (entry.IdentityBearing) _removalUnconfirmed = false;
            }
            Changed?.Invoke();
        }

        public void ClientRemoved(uint handle, int attemptGeneration)
        {
            lock (_lock)
            {
                if (attemptGeneration != _attemptGeneration) return;
                _entries.TryGetValue(handle, out var removed);
                _entries.Remove(handle);
                _generation++;
                _lastChange = RosterChangeKind.Removed;
                _lastChangedEntry = removed;
                // Removing OUR record is loss of own evidence, handled by the
                // guard (own handle not in roster). Removing ANOTHER handle is
                // the ambiguous case: TCP status or a discovery packet that
                // simply did not list them this time.
                if (handle != _ownHandle) _removalUnconfirmed = true;
            }
            Changed?.Invoke();
        }

        public RosterSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new RosterSnapshot(
                    _entries.Values.OrderBy(e => e.Handle).ToList(),
                    _ownHandle, _generation, _clock.NowMs, _attemptGeneration,
                    _lastChange, _lastChangedEntry, _removalUnconfirmed);
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
                return new RosterJudgement(RosterVerdict.OthersPresent,
                    others.Count + " other client handle(s) present: " + string.Join(", ", others.Select(o => o.ToString())),
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

            if (s.RemovalUnconfirmed)
            {
                return new RosterJudgement(RosterVerdict.Unknown,
                    "another client's record was removed and nothing since has confirmed the roster from the radio's own status",
                    s.Generation, mayChange: true);
            }

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
