#nullable enable
using System;
using System.Diagnostics;
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
    // ── Repair round 6: the gate is at the transport's own write ──────
    //
    // Codex's fifth re-check found the round-5 count could not hold: the
    // Connecting barrier's wait was bounded, so a writer paused past it at
    // Radio.SendCommand's pre-write point wrote onto the replacement (#596 B9,
    // #861 N1); and a report arriving between our last check and the vendor's
    // transport write (another client taking the carrier, a reported
    // transmit, a newer mic choice) was never seen (#596 B10, #861 N3). Ruled
    // by Noel 2026-11-05 23:49: the gate goes into FlexLib's command transport,
    // as a carried patch (MIGRATION.md item 21, CommandWritePin).
    //
    // - A pinned step pins this thread's command writes to the CommandConnection
    //   it checked. The transport admits each write only to the attempt of that
    //   connection, still Connected: a write cannot be retargeted, however long
    //   the writer was paused, because the adapter writes through the attempt
    //   it compared. No count, no wait, no timeout.
    // - The write's own predicate (whose carrier it is, receive reported, the
    //   mic input still what we expect) is asked AT that write, under the
    //   transport's ConnectionSync, the gate the reader dispatches every report
    //   under. A report already dispatched is seen; none is dispatched while it
    //   decides. The bytes go out after the gate is released, so no lock is held
    //   across I/O, and the reader waits at most for the predicate's reads.
    // - The predicate waits for that gate only briefly (CommandWritePin's
    //   SerializeBound); a write that cannot be confirmed is NOT written and the
    //   caller is told (fails closed). A writer that holds a lock the reader's
    //   handlers take (the VOX owner's) therefore cannot deadlock it.
    // - What no client-side gate reaches: a change at the radio not yet
    //   reported to this process when the decision is made.
    // - Unpinned writes are untouched: the emergency kill, and every ordinary
    //   command, pass no predicate that could refuse them.

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
    /// one step (repair rounds 5 and 6). Null when <paramref name="write"/> ran
    /// and the transport refused none of its writes; otherwise why not.
    /// Exceptions from <paramref name="write"/> propagate.
    /// </summary>
    /// <param name="refused">Asked before the write, after the identity check:
    /// the write's own reason not to go. Null means none.</param>
    /// <param name="atTheWrite">Asked again AT each transport write, under the
    /// transport's dispatch gate. It must only read state: no tracing, no I/O,
    /// no locks. Null means identity only.</param>
    /// <param name="unkeyOfOurs">An unkey of our own carrier (repair round 7,
    /// Codex's #596 B11): never refused by contention at the transport, only
    /// by a positive "not ours" or a connection that is not the pinned one.</param>
    internal string? WriteOnPinnedTarget(Radio? radio, object? connection, Func<string?>? refused, Action write,
        Func<string?>? atTheWrite = null, bool unkeyOfOurs = false) =>
        StepOnPinnedTarget(radio, connection, () =>
        {
            string? why = refused?.Invoke();
            if (why != null) return why;
            write();
            return null;
        }, atTheWrite, null, unkeyOfOurs);

    /// <summary>
    /// <see cref="WriteOnPinnedTarget"/> for a caller whose own question and
    /// write are one function: the identity check, then
    /// <paramref name="step"/> with every command it writes pinned at the
    /// transport to <paramref name="connection"/> and asked
    /// <paramref name="atTheWrite"/> there (CommandWritePin). Returns the
    /// identity refusal, what the step returned, or the transport's refusal.
    /// </summary>
    /// <param name="onRefused">Told, on the writing thread, the moment the
    /// transport refuses a write (a queued item marks itself withheld).</param>
    /// <param name="atTheBytes">Asked again under the transport's write gate,
    /// immediately before the bytes, with no other lock (MIGRATION.md item 25,
    /// Codex's #596 B4): a restore's "has the operator chosen since" question.
    /// An operator's choice is recorded before its own write is queued, and that
    /// write waits for this gate, so either the restore's bytes go first and the
    /// operator's follow them, or the restore sees the choice here and refuses.
    /// It must only read memory. Null for none.</param>
    internal string? StepOnPinnedTarget(Radio? radio, object? connection, Func<string?> step,
        Func<string?>? atTheWrite = null, Action<string>? onRefused = null, bool unkeyOfOurs = false,
        Func<string?>? atTheBytes = null)
    {
        string? notTarget = PinnedTargetRefused(radio, connection);
        if (notTarget != null) return notTarget;
        using var pin = CommandWritePin.Pin((CommandConnection)connection!, atTheWrite, onRefused, unkeyOfOurs, atTheBytes);
        var outer = t_stepPin;
        t_stepPin = pin;
        try
        {
            string? stepWhy = step();
            return stepWhy ?? pin.Refusal;
        }
        finally { t_stepPin = outer; }
    }

    /// <summary>
    /// Set <paramref name="radio"/>'s mic input to <paramref name="value"/>
    /// through FlexLib's setter, and give the write's echo registration its
    /// real outcome (round six, Codex's re-check E1): the radio echoes a mic
    /// write to its writer, and an expectation for bytes that never went out
    /// would take another station's real report as ours. The setter writes only
    /// when the value differs from its cache, so nothing is expected otherwise.
    /// The outcome is read from the pinned step this runs in, or from a pin of
    /// its own (never nested inside another, which would skip that one's question).
    /// </summary>
    private static void SetMicInputTracked(Radio? radio, string? value)
    {
        if (radio == null) return;
        if (value == null || string.Equals(radio.MicInput, value, StringComparison.Ordinal)) { radio.MicInput = value; return; }
        var connection = radio.CurrentCommandConnection as CommandConnection;
        var echo = MicReports.Expect(connection, value);
        bool sent;
        if (t_stepPin != null)
        {
            int before = PinnedWritesSoFar;
            radio.MicInput = value;
            sent = PinnedWritesSoFar > before;
        }
        else if (connection != null)
        {
            using var pin = CommandWritePin.Pin(connection);
            radio.MicInput = value;
            sent = pin.Written > 0 && pin.Refusal == null;
        }
        else { radio.MicInput = value; sent = false; }
        echo?.Outcome(sent);
    }

    /// <summary>The pin of the pinned step running on this thread, if any (repair round 9).</summary>
    [ThreadStatic] private static CommandWritePin? t_stepPin;

    /// <summary>Writes the current pinned step has really written so far (CommandWritePin.Written, MIGRATION.md item 22).</summary>
    private static int PinnedWritesSoFar => t_stepPin?.Written ?? 0;

    /// <summary>
    /// A new command connection is beginning on this rig's radio (the vendor's
    /// Connecting report, raised inside the transport's BeginConnection,
    /// before the new attempt can carry a write). Work admitted on the old
    /// connection is revoked. Nothing is waited for: the transport writes each
    /// pinned write only to the connection it was pinned to (round 6). Named
    /// AwaitPinnedWritesBeforeNewConnection until 2026-11-06, from round 5's
    /// wait, which round 6 removed.
    /// </summary>
    private void RevokeWorkBeforeNewConnection()
    {
        // A key-on, a tune-on or CW queued for the old connection keys nothing
        // on this one (the tune-on had no identity of its own).
        RevokeAdmittedTransmitWork("a new command connection began on this radio");
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
        if (value) t_keyAskedHere = null;
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
        var offFor = LatestOwnKey;   // the key this release ends (round 12, F5)
        q.Enqueue((FunctionDel)(() =>
        {
            // Whose carrier it is, asked before AND at the transport write,
            // under the reader's dispatch gate (repair round 6, Codex's B10).
            // An unkey of ours: contention never refuses it (round 7, B11).
            // The OFF is forced (repair round 8, MIGRATION.md item 22): it goes
            // out whatever FlexLib's cache reads, so a receive report that landed
            // while our ON was written cannot turn it into a no-op (Codex's #596
            // B13). Our key counts as released only when the OFF really went.
            bool off = false;
            long offMark = 0;
            string? why = WriteOnPinnedTarget(owner, connection, () => ForeignCarrier(owner!),
                () => off = WriteOwnOff(owner!, r => r.SendMoxOff(), out offMark),
                () => ForeignCarrier(owner!), unkeyOfOurs: true);
            if (why == null && off) { _ownKeyRequested = false; NoteOwnOffWritten(offFor, offMark); return; }
            Tracing.TraceLine("TransmitAdmission: " + what + " NOT sent at its write - " + (why ?? "the transport did not take it"),
                TraceLevel.Warning);
        }), "Mox (a send's own release)");
    }
}
