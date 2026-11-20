#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using JJTrace;

namespace Radios.SmartLink
{
    /// <summary>How one SmartLink method attempt ended.</summary>
    public enum SmartLinkAttemptEnd
    {
        /// <summary>Still running.</summary>
        Pending = 0,
        /// <summary>Proven: the open completed and qualified UDP arrived from the radio (Track B's proof rule).</summary>
        Proven = 1,
        /// <summary>Dialled and handed to the open; not yet proven. The interim end until Track B's proof lands.</summary>
        Dialled = 2,
        /// <summary>No handle within the wait. Ends the SmartLink leg; never a method failure.</summary>
        NoHandle = 3,
        /// <summary>The broker answered with an empty handle. Ends the SmartLink leg.</summary>
        NullHandle = 4,
        /// <summary>FlexLib's <c>Radio.Connect</c> returned false (TCP or TLS stage). A method failure.</summary>
        ConnectFailed = 5,
        /// <summary>The open failed for a reason other than transport. Ends the SmartLink leg.</summary>
        OpenFailed = 6,
        /// <summary>The command connection was lost terminally before proof. A method failure.</summary>
        CommandConnectionLost = 7,
        /// <summary>No qualified UDP arrived within the proof window. A method failure.</summary>
        NoQualifiedUdp = 8,
        /// <summary>The operator cancelled. Never walked on.</summary>
        Cancelled = 9,
        /// <summary>Nothing was sent (for example the brokering session vanished). Ends the SmartLink leg.</summary>
        NotSent = 10,
    }

    /// <summary>
    /// One method attempt. FROZEN: Track B files these and Track C reads them.
    /// Add a field only by agreement; never repurpose one.
    /// </summary>
    /// <param name="Method">The method attempted.</param>
    /// <param name="TlsPortSent">The TCP port dialled for a ports method; -1 for a punch.</param>
    /// <param name="UdpPortSent">The UDP port registered to for a ports method; -1 for a punch.</param>
    /// <param name="PunchPortSent">The port sent to the broker in the connect request: the punch port for a punch, 0 for a ports method.</param>
    /// <param name="PunchPortSource">Where a punch port came from ("radioProfile" or "random"); "none" for a ports method.</param>
    /// <param name="HandleArrived">Whether the broker's handle arrived.</param>
    /// <param name="OpenSucceeded">The open's result; null until it is known.</param>
    /// <param name="QualifiedUdpAfter">Time from this attempt's VITA socket creation to the first qualified UDP (MIGRATION.md item 30); null if none arrived.</param>
    /// <param name="End">How it ended.</param>
    /// <param name="EndDetail">The terminal reason in plain words for the trace and the diagnostic; never spoken as-is.</param>
    /// <param name="Sequence">This attempt's place in its connect, from 1; 0 until <see cref="SmartLinkConnectRecord.WithAttempt"/> stamps it. With the connect's <see cref="SmartLinkConnectRecord.ConnectId"/> it is the attempt's identity (repair round 1).</param>
    public sealed record SmartLinkAttemptRecord(
        SmartLinkMethod Method,
        int TlsPortSent,
        int UdpPortSent,
        int PunchPortSent,
        string PunchPortSource,
        bool HandleArrived,
        bool? OpenSucceeded,
        TimeSpan? QualifiedUdpAfter,
        SmartLinkAttemptEnd End,
        string EndDetail,
        int Sequence)
    {
        /// <summary>A ports attempt about to be sent. Its sequence is stamped when it joins a record.</summary>
        public static SmartLinkAttemptRecord ForPorts(SmartLinkMethod method, AdvertisedPortPair pair) =>
            new(method, pair.TlsPort, pair.UdpPort, 0, "none", false, null, null, SmartLinkAttemptEnd.Pending, "", 0);

        /// <summary>A punch attempt about to be sent. Its sequence is stamped when it joins a record.</summary>
        public static SmartLinkAttemptRecord ForPunch(int punchPort, string source) =>
            new(SmartLinkMethod.HolePunch, -1, -1, punchPort, source, false, null, null, SmartLinkAttemptEnd.Pending, "", 0);

        public string Describe() =>
            $"#{Sequence} {Method} " + (Method == SmartLinkMethod.HolePunch
                ? $"punchPort={PunchPortSent}({PunchPortSource})"
                : $"tls={TlsPortSent} udp={UdpPortSent}")
            + $" handle={(HandleArrived ? "yes" : "no")} open={(OpenSucceeded is bool o ? (o ? "ok" : "failed") : "unknown")}"
            + $" qualifiedUdp={(QualifiedUdpAfter is TimeSpan q ? ((long)q.TotalMilliseconds) + "ms" : "none")} end={End}"
            + (EndDetail.Length > 0 ? $" ({EndDetail})" : "");
    }

    /// <summary>
    /// What one SmartLink connect to one radio decided and how each attempt
    /// ended. FROZEN on 2026-11-19 by the tiers work's Track A: Track B files
    /// it, Track C's diagnostic reads it. Immutable; every change returns a
    /// new record, and <see cref="SmartLinkConnectRecords"/> holds, per radio,
    /// the record of the newest connect and its newest attempt: by identity
    /// (<see cref="ConnectId"/>, then the attempt's
    /// <see cref="SmartLinkAttemptRecord.Sequence"/>), never by whichever
    /// write came last (repair round 1).
    /// </summary>
    /// <param name="Serial">The radio.</param>
    /// <param name="BrokeringAccount">The account whose session brokers this connect (#798), and whose tier is the ceiling.</param>
    /// <param name="Tier">That account's tier.</param>
    /// <param name="TierChosen">Whether an operator chose that tier.</param>
    /// <param name="PerRadioChoice">The radio's own connection choice.</param>
    /// <param name="ForcedPath">The operator pinned the SmartLink path (context menu).</param>
    /// <param name="ForcedMethod">The radio is set to "Hole punch always".</param>
    /// <param name="Report">What the radio advertised when the connect was planned.</param>
    /// <param name="MethodOrder">The policy's method order; empty when refused.</param>
    /// <param name="Refusal">Why nothing was sent, or None.</param>
    /// <param name="LowestAdmittingTier">For a tier refusal, the lowest tier that would admit what the radio offers.</param>
    /// <param name="PunchWithheldUntilGate">Tier 3 Automatic would have punched after ports, and the hardware gate has not passed.</param>
    /// <param name="Attempts">Each method attempt, in order.</param>
    /// <param name="MethodUsed">The method of the connection actually in use; null until one is.</param>
    /// <param name="StartedUtc">When the connect was planned.</param>
    /// <param name="ConnectId">This connect's identity: unique and increasing for this run of the app, given by <see cref="Begin"/>. A later connect always has a larger one.</param>
    public sealed record SmartLinkConnectRecord(
        string Serial,
        string BrokeringAccount,
        SmartLinkConnectionMode Tier,
        bool TierChosen,
        RadioConnectionPreference PerRadioChoice,
        bool ForcedPath,
        bool ForcedMethod,
        RadioPortReport Report,
        IReadOnlyList<SmartLinkMethod> MethodOrder,
        SmartLinkRefusal Refusal,
        SmartLinkConnectionMode? LowestAdmittingTier,
        bool PunchWithheldUntilGate,
        IReadOnlyList<SmartLinkAttemptRecord> Attempts,
        SmartLinkMethod? MethodUsed,
        DateTime StartedUtc,
        long ConnectId)
    {
        /// <summary>A new record from the policy's input and answer, with no attempts yet, and a new connect identity.</summary>
        public static SmartLinkConnectRecord Begin(string serial, string brokeringAccount,
            SmartLinkPolicyInput input, SmartLinkPolicyDecision decision, DateTime nowUtc) =>
            new(serial ?? "", brokeringAccount ?? "", input.Tier, input.TierChosen, input.PerRadioChoice,
                input.ForcedPath, input.ForcedMethod, input.Report,
                decision.Methods.ToArray(), decision.Refusal, decision.LowestAdmittingTier, decision.PunchWithheldUntilGate,
                Array.Empty<SmartLinkAttemptRecord>(), null, nowUtc, SmartLinkConnectRecords.NextConnectId());

        public bool IsRefused => Refusal != SmartLinkRefusal.None;

        /// <summary>The newest attempt, or null.</summary>
        public SmartLinkAttemptRecord? LastAttempt => Attempts.Count > 0 ? Attempts[Attempts.Count - 1] : null;

        /// <summary>The newest attempt's sequence, or 0 before any attempt.</summary>
        public int AttemptSequence => LastAttempt?.Sequence ?? 0;

        /// <summary>Append an attempt, stamping its sequence (1 for the first).</summary>
        public SmartLinkConnectRecord WithAttempt(SmartLinkAttemptRecord attempt) =>
            this with { Attempts = Attempts.Append(attempt with { Sequence = Attempts.Count + 1 }).ToArray() };

        /// <summary>Replace the newest attempt (it must exist).</summary>
        public SmartLinkConnectRecord WithLastAttempt(Func<SmartLinkAttemptRecord, SmartLinkAttemptRecord> change)
        {
            if (Attempts.Count == 0) throw new InvalidOperationException("no attempt to change");
            var list = Attempts.ToArray();
            list[^1] = change(list[^1]);
            return this with { Attempts = list };
        }

        public SmartLinkConnectRecord WithMethodUsed(SmartLinkMethod? method) => this with { MethodUsed = method };

        /// <summary>One line for the trace.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"connect#{ConnectId} serial={Serial} account={BrokeringAccount} tier={(int)Tier + 1}{(TierChosen ? " chosen" : " not chosen")}");
            sb.Append($" choice={PerRadioChoice} forcedPath={ForcedPath} forcedMethod={ForcedMethod} report=[{Report}]");
            sb.Append(IsRefused
                ? $" refused={Refusal}" + (LowestAdmittingTier is { } t ? $" lowestAdmittingTier={(int)t + 1}" : "")
                : $" order={string.Join(",", MethodOrder)}" + (PunchWithheldUntilGate ? " punchWithheldUntilGate" : ""));
            for (int i = 0; i < Attempts.Count; i++) sb.Append($" attempt{i + 1}=[{Attempts[i].Describe()}]");
            sb.Append($" used={(MethodUsed?.ToString() ?? "none")}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Per radio, the record of the newest connect at its newest attempt, for
    /// this run of the app. Kept by identity, not by arrival: a late write
    /// from an older connect, or from an older attempt of the same connect,
    /// is refused and traced, so it can never replace a newer one (repair
    /// round 1; the shape Track B's walk needs, where an abandoned attempt's
    /// code path can still finish after the next attempt has begun).
    /// </summary>
    public static class SmartLinkConnectRecords
    {
        private static readonly ConcurrentDictionary<string, SmartLinkConnectRecord> _bySerial =
            new(StringComparer.OrdinalIgnoreCase);

        private static long _lastConnectId;

        /// <summary>Raised after a record is stored, on the storing thread. Not raised for a refused write.</summary>
        public static event Action<SmartLinkConnectRecord>? Changed;

        /// <summary>A new connect identity, larger than every earlier one in this run.</summary>
        public static long NextConnectId() => Interlocked.Increment(ref _lastConnectId);

        /// <summary>
        /// Whether <paramref name="incoming"/> is older than <paramref name="stored"/>:
        /// an earlier connect, or the same connect at an earlier attempt. A
        /// write for the same attempt (its end, its open result) is not older.
        /// </summary>
        public static bool IsOlderThan(SmartLinkConnectRecord incoming, SmartLinkConnectRecord stored) =>
            incoming.ConnectId < stored.ConnectId
            || (incoming.ConnectId == stored.ConnectId && incoming.AttemptSequence < stored.AttemptSequence);

        /// <summary>
        /// Keep this record as the radio's, unless the one already kept is
        /// newer (<see cref="IsOlderThan"/>). Returns false, and traces, when
        /// the write is refused.
        /// </summary>
        public static bool Store(SmartLinkConnectRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Serial)) return false;
            SmartLinkConnectRecord? newer = null;
            _bySerial.AddOrUpdate(record.Serial, record, (_, existing) =>
            {
                if (IsOlderThan(record, existing)) { newer = existing; return existing; }
                newer = null;
                return record;
            });
            if (newer != null)
            {
                Tracing.TraceLine(
                    $"SmartLinkConnectRecords: refused a late write for {record.Serial} (connect#{record.ConnectId} attempt {record.AttemptSequence}); kept connect#{newer.ConnectId} attempt {newer.AttemptSequence}",
                    TraceLevel.Info);
                return false;
            }
            try { Changed?.Invoke(record); } catch (Exception) { /* a reader's fault is not the connect's */ }
            return true;
        }

        /// <summary>The newest record for this radio, or null when it has not been connected over SmartLink in this run.</summary>
        public static SmartLinkConnectRecord? For(string serial) =>
            !string.IsNullOrEmpty(serial) && _bySerial.TryGetValue(serial, out var r) ? r : null;
    }
}
