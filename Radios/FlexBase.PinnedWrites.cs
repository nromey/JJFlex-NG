#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios;

public partial class FlexBase
{
    // ── Repair round 5: the check travels with the write ────────────────
    //
    // Codex's fourth re-check found every round-four identity check asked
    // BEFORE the write it guarded, never AT it: a send's release checked its
    // admission and then called the ordinary setter, whose queued OFF ran later
    // with no admission (#596 B7); the VOX owner compared its pin and then the
    // vendor wrote through whatever command connection was current when it
    // actually wrote (#596 B8); the microphone put-back decided under a lock
    // and returned an Action that wrote a captured radio nobody checked again
    // (#861 B1). So a write that belongs to one radio and one command
    // connection now makes its identity check and its vendor write as ONE
    // step, against the replacement of that connection:
    //
    // - WriteOnPinnedTarget checks the radio, the connection (and that it is
    //   still up), and the caller's own question, then writes, while it is
    //   counted as a pinned write in flight.
    // - The vendor raises "a new command connection begins" (the Connecting
    //   report) from the transport's BeginConnection, unconditionally, BEFORE
    //   the new attempt can carry a single write: Tcp and Tls both assign the
    //   attempt that Write uses only after BeginConnection returns
    //   (CommandConnection.cs, TcpCommandCommunication.BeginAttempt,
    //   TlsCommandCommunication.StartClient), and the radio's own handler
    //   forwards the Connecting report before its _ignoreConnectedEvents test
    //   (Radio._commandCommunication_ConnectionChanged). FlexBase hears it in
    //   onCommandConnectionChanged, and waits there for every pinned write in
    //   flight (AwaitPinnedWritesBeforeNewConnection).
    // - So a write that passed its check finishes on the old attempt (which
    //   has ended, so the vendor drops it) before the replacement can be
    //   written to, and one that checks after the report finds the connection
    //   changed and refuses. Rule 4: the guard is reached by the event it
    //   relies on, every time, by the vendor's own construction.
    //
    // Why a count and not a lock. A pinned write runs a vendor setter, and the
    // setter raises PropertyChanged synchronously, so our own handlers run
    // inside it (noteRadioMox, the transmit-change subscribers, and from
    // there TxFileStop, which can release a VOX lease and take the VOX owner's
    // gate). A VOX restore takes that gate BEFORE its write. A lock shared by
    // the two would let a send's release and a VOX restore deadlock each
    // other. A count excludes only the replacement, never another write, so
    // the writes cannot wait on each other at all. The replacement's wait is
    // bounded; a write still counted after the bound is past its vendor write
    // (only its handlers can be that slow), so going on is safe, and traced.

    /// <summary>Pinned writes between their identity check and the end of their vendor write.</summary>
    private int _pinnedWritesInFlight;

    /// <summary>How long a new command connection waits for pinned writes already past their check.</summary>
    internal static readonly TimeSpan PinnedWriteDrainBound = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Why a write pinned to <paramref name="radio"/> and
    /// <paramref name="connection"/> may not go now, or null when it may: the
    /// radio is still this one, the connection is still its current one, and
    /// it is still up. A write admitted with no radio, or with no connection,
    /// never goes: it must not be upgraded into authority over a radio that
    /// arrived later.
    /// </summary>
    internal string? PinnedTargetRefused(Radio? radio, object? connection)
    {
        if (radio == null) return "it was admitted with no radio";
        if (connection == null || ReferenceEquals(connection, NoConnection)) return "it was admitted with no command connection";
        if (!ReferenceEquals(theRadio, radio)) return "the radio it was for is no longer this one";
        var current = radio.CurrentCommandConnection;
        if (!ReferenceEquals(current, connection)) return "the radio's command connection has changed since";
        if (current?.State != CommandConnectionState.Connected) return "the radio's command connection has ended";
        return null;
    }

    /// <summary>
    /// The identity check, the caller's own question and the vendor write as
    /// one step against a connection replacement (repair round 5, rule 1).
    /// Null when <paramref name="write"/> ran; otherwise why it did not, and
    /// nothing was written. Exceptions from <paramref name="write"/> propagate.
    /// </summary>
    /// <param name="refused">Asked after the identity check, inside the same
    /// step: the write's own reason not to go (a carrier that is not ours, no
    /// receive report). Null means none.</param>
    internal string? WriteOnPinnedTarget(Radio? radio, object? connection, Func<string?>? refused, Action write) =>
        StepOnPinnedTarget(radio, connection, () =>
        {
            string? why = refused?.Invoke();
            if (why != null) return why;
            write();
            return null;
        });

    /// <summary>
    /// <see cref="WriteOnPinnedTarget"/> for a caller whose own question and
    /// write are one function (the VOX owner's restore): the identity check,
    /// then <paramref name="step"/>, as one step against a connection
    /// replacement. Returns the identity refusal, or what the step returned.
    /// </summary>
    internal string? StepOnPinnedTarget(Radio? radio, object? connection, Func<string?> step)
    {
        Interlocked.Increment(ref _pinnedWritesInFlight);
        try
        {
            return PinnedTargetRefused(radio, connection) ?? step();
        }
        finally { Interlocked.Decrement(ref _pinnedWritesInFlight); }
    }

    /// <summary>
    /// A new command connection is beginning on this rig's radio (the vendor's
    /// Connecting report, raised inside the transport's BeginConnection,
    /// before the new attempt can carry a write). Work admitted on the old
    /// connection is revoked, and every pinned write already past its check
    /// finishes first. Bounded; traced when the bound is reached.
    /// </summary>
    private void AwaitPinnedWritesBeforeNewConnection()
    {
        // A key-on, a tune-on or CW queued for the old connection keys nothing
        // on this one (the tune-on had no identity of its own).
        RevokeAdmittedTransmitWork("a new command connection began on this radio");
        // Dekker's order: the vendor has already made the new connection the
        // current one; a full fence, then the count. A write counted before
        // this is waited for; one counted after it sees the new connection.
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _pinnedWritesInFlight) == 0) return;
        var clock = Stopwatch.StartNew();
        var spin = new SpinWait();
        while (Volatile.Read(ref _pinnedWritesInFlight) != 0)
        {
            if (clock.Elapsed > PinnedWriteDrainBound)
            {
                Tracing.TraceLine("PinnedWrites: a write checked on the old command connection was still running "
                    + (int)PinnedWriteDrainBound.TotalMilliseconds + " ms into the new one; it is past its vendor write by now, so the connection goes on",
                    TraceLevel.Warning);
                return;
            }
            spin.SpinOnce();
        }
        Tracing.TraceLine("PinnedWrites: the new command connection waited " + clock.ElapsedMilliseconds
            + " ms for a write checked on the old one to finish there", TraceLevel.Info);
    }

    /// <summary>
    /// Why a release should leave the carrier alone at its write, or null: the
    /// radio reports another client transmitting (#686). Zero, or our own
    /// handle, means the carrier is ours or nobody's.
    /// </summary>
    private string? ForeignCarrier(Radio radio)
    {
        uint txClient = radio.TXClientHandle;
        if (txClient != 0 && txClient != clientHandle)
            return "another client (0x" + txClient.ToString("X8") + ") is the one transmitting now, and its carrier is not ours to unkey";
        return null;
    }

    /// <summary>
    /// A send's own key or release, carrying the admission it took at its
    /// press all the way to the write (repair round 5, Codex's #596 B7). The
    /// key-on is admitted work as every key-on is (revoked by a drop, a
    /// teardown or a new connection), pinned to the ADMISSION's radio and
    /// connection, not to whatever is current when it is queued. The release
    /// is checked when it RUNS: the same radio, the same still-live command
    /// connection, and no other client's carrier up. It is never revoked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the emergency kill.</b> <see cref="TransmitKillSwitch.Request"/>
    /// still drops the carrier through <see cref="TransmitKillSwitch.DropCarrier"/>,
    /// which writes <see cref="Transmit"/> false unconditionally, and so does
    /// every ordinary unkey. Only a SEND's release, which belongs to the
    /// connection it was admitted on, is pinned.
    /// </para>
    /// </remarks>
    public void TransmitAdmitted(object admission, bool value, string what)
    {
        if (!TransmitAdmissionStands(admission, what)) return;
        var a = (TransmitAdmission)admission;
        if (value)
        {
            EnqueueKeyOn(a.Radio, a.Connection, what);
            return;
        }
        CancelSlicePlayback();
        Radio? owner = a.Radio;
        object? connection = a.Connection;
        q.Enqueue((FunctionDel)(() =>
        {
            string? why = WriteOnPinnedTarget(owner, connection, () => ForeignCarrier(owner!), () =>
            {
                owner!.Mox = false;
                _ownKeyRequested = false;
            });
            if (why != null)
                Tracing.TraceLine("TransmitAdmission: " + what + " NOT sent at its write - " + why, TraceLevel.Warning);
        }), "Mox (a send's own release)");
    }
}
