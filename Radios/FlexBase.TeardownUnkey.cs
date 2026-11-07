using System;
using System.Diagnostics;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios;

public partial class FlexBase
{
    // ── Every teardown unkeys first (#596, #861, #600) ─────────────────────
    //
    // Codex's checks of the 2026-11-02 junk box batch found that a clean
    // disconnect, a radio switch, an exit and a power-off released only the
    // reflected-power cut's hold before tearing down: ordinary push to talk,
    // a lock, a check's key and a send could all still be keyed, and #861's
    // put-back, moved to run while the link still answers, could then switch
    // the radio's microphone input from PC back to MIC or BAL in the middle
    // of a transmission. So the clean disconnect now does this FIRST: stop
    // what this computer is feeding the transmitter, take down this
    // session's own carriers, let those writes reach the radio, and wait for
    // the radio to report receive. Only then is anything put back.
    //
    // ── Repair round 2: guards at the write ─────────────────────────────
    //
    // Codex's re-check found round one's guards remembered a decision
    // instead of checking at the write. Four rules now hold here:
    //
    // 1. The teardown's unkey checks OWNERSHIP AT THE WRITE. Its xmit 0 and
    //    tune-off re-read the radio's transmitting client and the command
    //    connection inside the queued closure, and send nothing when another
    //    client's carrier is up by then (#686: one operator never touches
    //    another's transmission). The general emergency unkeys are not
    //    changed; only the teardown's own unkey is scoped.
    // 2. Work ADMITTED before a drop or a teardown is revoked by generation:
    //    a key-on, a tune-on and queued CW cannot run against a reconnected
    //    radio or after the teardown's unkey.
    // 3. Every put-back of the transmit chain asks the radio's interlock AT
    //    ITS WRITE, not the wait's earlier answer: a foot switch, VOX or
    //    another client can key between the two. Receive must be POSITIVE
    //    (receive, ready, not ready); a stuck input, a fault, a timeout or an
    //    unknown state withholds.
    // 4. Withheld means kept, not lost: the restoration debt stays where the
    //    next connect can find it. Nothing here replays it; that is #590.
    //
    // ── Repair round 3: the mechanism, not the instance ─────────────────
    //
    // Codex's second re-check found each of round two's rules applied where
    // the named blocker was and broken one route over. So the rules are now
    // mechanisms that a route nobody named passes through by construction:
    //
    // 1. ONE question for every restoration write of the transmit chain,
    //    asked at the write: RestoreRefusedAtWrite. The teardown's profile
    //    loads and the live transmit-audio setters run under one
    //    restore-scoped QueuedWriteGate that asks it (RunRestoreStepAtTheWrite);
    //    the microphone put-back asks it; VOX's restore writes ask it inside
    //    the VOX pause owner, the one owner every VOX restore goes through.
    // 2. A debt is discharged only when its writes ran: a step's record, the
    //    live snapshot on disk and a VOX or microphone debt are kept unless
    //    every write they stand for went out.
    // 3. Receive means a report received (InterlockReports): FlexLib's cache
    //    starting at Ready, or keeping it through a state it does not know, is
    //    not the radio saying so.
    // 4. The check and the send are one step: key-ons and CW take
    //    _transmitWorkGate around the final check and the send, and revocation
    //    takes it too, so nothing admitted before a revocation can send after
    //    it returns.

    /// <summary>How long the teardown waits for its unkey writes to leave the command queue.</summary>
    internal static readonly TimeSpan TeardownUnkeyDrainBound = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long the teardown waits for the radio to report receive after its unkey.</summary>
    internal const int TeardownReceiveWaitMs = 2000;

    /// <summary>How long the teardown waits for a queued microphone put-back to run before the hang-up.</summary>
    internal static readonly TimeSpan TeardownPutBackDrainBound = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Set while a clean disconnect could not confirm the radio was receiving:
    /// what it still reported. Nothing in the transmit chain is then put back.
    /// Null otherwise.
    /// </summary>
    private volatile string _teardownStillOnAir;

    /// <summary>
    /// True from a clean disconnect's unkey until the teardown ends: the
    /// window in which a restoration must ask the interlock first
    /// (<see cref="TeardownRefusesRestore"/>).
    /// </summary>
    private volatile bool _teardownRestoring;

    /// <summary>
    /// The generation of transmit work this connection may still run (#596,
    /// repair round 2, N4). A key-on, a tune-on, a CW message or a CW
    /// character takes it when it is queued and is dropped, not run, when it
    /// has moved on. Advanced by a drop and by a teardown's unkey.
    /// </summary>
    private long _transmitWorkGeneration;

    /// <summary>
    /// This client's own <c>xmit 1</c> has gone out and no <c>xmit 0</c> after
    /// it: our key request is in flight or up. Set where the write is made.
    /// </summary>
    private volatile bool _ownKeyRequested;

    /// <summary>
    /// This client's own tune ON has gone out and no tune OFF after it (repair
    /// round 8, Codex's #596 B14): the tune equivalent of
    /// <see cref="_ownKeyRequested"/>, retired only when an OFF really went.
    /// </summary>
    private volatile bool _ownTuneRequested;

    /// <summary>This connection has handed CW to the radio's CWX queue.</summary>
    private volatile bool _cwSentThisConnection;

    /// <summary>
    /// Serialises revocation with every admitted key-on and CW send (#596 N4,
    /// repair round 3): the final check and the write are one step, as the
    /// tune's are under <c>_tuneWriteGate</c>. Held across a FlexLib setter,
    /// which writes to the transport without taking its dispatch lock.
    /// </summary>
    private readonly object _transmitWorkGate = new();

    /// <summary>
    /// Admitted transmit work, at its write: run <paramref name="send"/> only
    /// if nothing has revoked <paramref name="generation"/> and the radio and
    /// its command connection are still the ones it was admitted on. The check
    /// and the send hold <see cref="_transmitWorkGate"/>, which
    /// <see cref="RevokeAdmittedTransmitWork"/> takes too, so once a revocation
    /// returns, no work admitted before it is part-way to the radio.
    /// </summary>
    private bool SendIfStillAdmitted(long generation, Radio owner, object connection, string what, Action send)
    {
        lock (_transmitWorkGate)
        {
            if (TransmitWorkRevoked(generation, what)) return false;
            // The identity check is made with the write (repair round 5): a
            // new connection waits for it, and work admitted with no radio is
            // never upgraded into authority over one attached later (Codex's
            // #596 re-check: the no-radio admission keyed a later radio).
            string why = WriteOnPinnedTarget(owner, connection, null, send);
            if (why == null) return true;
            Tracing.TraceLine("TransmitWork: " + what + " was dropped, not sent - " + why, TraceLevel.Warning);
            return false;
        }
    }

    /// <summary>
    /// Revoke every piece of transmit work already admitted to the command
    /// queue: key-ons, tune-ons, CW text and CW characters. They are dropped
    /// when the loop reaches them. Unkeys are never revoked.
    /// </summary>
    /// <param name="trace">Where the line goes: a fall passes its own
    /// session-bound trace, so the line is filed with the fall's session.</param>
    internal void RevokeAdmittedTransmitWork(string why, Action<string, TraceLevel> trace = null)
    {
        // Taken with the admitted sends' final check (repair round 3): a send
        // already past its check finishes before this returns, and none
        // starts after it.
        long now;
        lock (_transmitWorkGate) now = Interlocked.Increment(ref _transmitWorkGeneration);
        ForgetTuneWrites();
        string line = "TransmitWork: " + why + "; work admitted before this (generation " + (now - 1) + ") keys nothing";
        if (trace != null) trace(line, TraceLevel.Info);
        else Tracing.TraceLine(line, TraceLevel.Info);
    }

    /// <summary>True, with a trace, when work admitted under <paramref name="generation"/> has been revoked.</summary>
    private bool TransmitWorkRevoked(long generation, string what)
    {
        long now = Volatile.Read(ref _transmitWorkGeneration);
        if (generation == now) return false;
        Tracing.TraceLine("TransmitWork: " + what + " queued before a drop or a teardown (generation " + generation
            + ", now " + now + ") was dropped, not sent", TraceLevel.Warning);
        return true;
    }

    /// <summary>
    /// True only while the radio's own interlock POSITIVELY reports receive:
    /// receive, ready or not ready. A transmit state, a stuck input (something
    /// holding the transmitter down), a fault, a timeout and an unknown state
    /// are not receive. Unlike <see cref="RadioInterlockReportsTransmit"/>,
    /// which answers "keyed?" and reads an unknown state as no, this answers
    /// "safe to put the transmit chain back?" and reads it as no. Note FlexLib
    /// starts its cache at Ready, so a radio that has reported nothing reads
    /// as receive. With no radio, false.
    /// </summary>
    internal bool RadioInterlockConfirmsReceive => InterlockConfirmsReceiveOn(theRadio);

    /// <summary><see cref="RadioInterlockConfirmsReceive"/>, of a named radio (repair round 5: the write's own target).</summary>
    private static bool InterlockConfirmsReceiveOn(Radio radio)
    {
        if (radio == null) return false;
        // Repair round 3 (#861 R3): FlexLib's cache AND the radio's own last
        // report on this connection. The cache alone reads Ready before any
        // report, and keeps Ready through a state it does not recognise.
        return radio.InterlockState is InterlockState.Receive or InterlockState.Ready or InterlockState.NotReady
            && InterlockReports.ConfirmsReceive(radio);
    }

    /// <summary>
    /// Stands for "there was no command connection" when a write is pinned
    /// to one (repair round 4): never the current connection, so a write
    /// admitted with none is refused once one exists.
    /// </summary>
    internal static readonly object NoConnection = new object();

    /// <summary>
    /// THE question every restoration of the transmit chain asks at its write
    /// (repair round 3, rule 1): the profile loads and the live transmit-audio
    /// setters through the restore gate, the microphone put-back, and VOX's
    /// restore writes. Null means the write may go; otherwise why not. No,
    /// when the radio or its command connection is not the one the write was
    /// for, when this computer is keying the radio, or when the radio does not
    /// positively report receive (a report received, rule 3).
    /// </summary>
    /// <remarks>
    /// <b>Always asked of a target</b> (repair round 5, Codex's #861 B1): the
    /// radio and command connection the write will use. A guard asked with no
    /// target checked whichever radio the rig held at that moment, which need
    /// not be the one written, so a missing target now refuses, and the
    /// receive question is put to the target radio itself.
    /// </remarks>
    internal string RestoreRefusedAtWrite(Radio expectedRadio, object expectedConnection)
    {
        if (theRadio == null) return "there is no radio";
        // The target, its connection, and that the connection is still up:
        // FlexLib keeps an ended connection as the current one, and a write
        // sent into it goes nowhere while its item counts as run (repair
        // round 4, Codex's F4). One wording with the pinned writes.
        string notTarget = PinnedTargetRefused(expectedRadio, expectedConnection);
        if (notTarget != null) return notTarget;
        if (_Transmit) return "this computer is keying the radio";
        return WhyNotReceiving(expectedRadio);
    }

    /// <summary>
    /// One teardown restoration step under ONE restore-scoped gate (repair
    /// round 3, rules 1 and 2): every write <paramref name="queueIt"/> queues,
    /// through any setter, asks <see cref="RestoreRefusedAtWrite"/> when it
    /// RUNS, pinned to the radio and connection the step began on. The step
    /// counts as done only when the queue has reached its writes and every one
    /// of them ran; a write refused, one that threw, or a queue that never got
    /// there leaves the step NOT done, and its debt is the caller's to keep.
    /// </summary>
    internal RestoreStep RunRestoreStepAtTheWrite(string what, Func<bool> queueIt, TimeSpan bound)
    {
        var radio = theRadio;
        object connection = radio?.CurrentCommandConnection;
        // Sticky: once one write of the step is refused, the rest of the step
        // is refused too, so a carrier that rises and falls inside a step
        // cannot leave it applied out of order.
        string refused = null;
        // Each write's question and its vendor write are one pinned step
        // (repair round 6, Codex's #861 N2): the profile loads, the live
        // transmit-audio setters and the equaliser's per-band sends all run
        // inside StepOnPinnedTarget, which a new command connection waits for,
        // as the direct mic and VOX writes do.
        // Receive is asked again AT each transport write, under the reader's
        // dispatch gate (round 6, Codex's #861 N3); a write refused there marks
        // its item withheld, so the step's debt is kept.
        var gate = StationConnect.QueuedWriteGate.Open(() => refused ??= RestoreRefusedAtWrite(radio, connection),
            step => StepOnPinnedTarget(radio, connection, step,
                () => RestoreRefusedAtWrite(radio, connection),
                why => StationConnect.QueuedWriteGate.WithholdRunning(why),
                // And at the bytes (round five, Codex's S1 residual, the class):
                // a carrier reported while the write waits for the gate refuses it.
                atTheBytes: () => RestoreRefusedAtWrite(radio, connection)));
        bool queued;
        try { queued = queueIt(); }
        catch (Exception ex)
        {
            Tracing.TraceLine("Restore step (" + what + "): threw while queueing: " + ex.Message, TraceLevel.Error);
            queued = false;
        }
        finally { gate.Dispose(); }
        bool reached = DrainCommandQueueAsync(bound).GetAwaiter().GetResult();
        RestoreStep outcome =
            gate.Refused > 0 ? RestoreStep.Withheld
            : !reached ? RestoreStep.NotReached
            : gate.Threw > 0 || !queued ? RestoreStep.Failed
            : RestoreStep.Ran;
        if (outcome != RestoreStep.Ran)
            Tracing.TraceLine("Restore step (" + what + "): " + outcome
                + (gate.FirstRefusal != null ? " - " + gate.FirstRefusal : "")
                + (gate.FirstThrow != null ? " - " + gate.FirstThrow : "")
                + "; its debt is kept, not discharged", TraceLevel.Warning);
        return outcome;
    }

    // ── A send's admission (repair round 4, Codex's #596 B5) ──

    /// <summary>The radio and command connection a piece of transmit work was admitted on.</summary>
    private sealed class TransmitAdmission
    {
        public TransmitAdmission(Radio radio, object connection) { Radio = radio; Connection = connection; }
        public Radio Radio { get; }
        public object Connection { get; }
    }

    /// <summary>
    /// The radio and command connection now, for a send to carry from its
    /// press (repair round 4, #596 B5). Its later key and unkey go out only
    /// while <see cref="TransmitAdmissionStands"/>: a send outlived by its
    /// connection must never unkey a carrier on the next one, which may be
    /// another client's.
    /// </summary>
    public object TransmitAdmissionNow()
    {
        var radio = theRadio;
        return new TransmitAdmission(radio, radio?.CurrentCommandConnection);
    }

    /// <summary>
    /// True while <paramref name="admission"/> is still this radio on the same,
    /// still-connected command connection. False, traced with
    /// <paramref name="what"/>, otherwise: then that work's key or unkey is not
    /// this connection's to send. The teardown and the drop own the release
    /// of a carrier whose connection has gone.
    /// </summary>
    public bool TransmitAdmissionStands(object admission, string what)
    {
        string why;
        var radio = theRadio;
        if (admission is not TransmitAdmission a) why = "it carries no admission";
        // Admitted with no radio attached: nothing it does can reach a radio,
        // so it stands while there is still none, and never once one is.
        else if (a.Radio == null) { if (radio == null) return true; why = "a radio was attached after it was admitted"; }
        else if (radio == null) why = "there is no radio";
        else if (!ReferenceEquals(radio, a.Radio)) why = "the radio it was admitted on is no longer this one";
        else if (a.Connection == null || !ReferenceEquals(radio.CurrentCommandConnection, a.Connection))
            why = "the radio's command connection has changed since it was admitted";
        else if (radio.CurrentCommandConnection?.State != CommandConnectionState.Connected)
            why = "the command connection it was admitted on has ended";
        else return true;
        Tracing.TraceLine("TransmitAdmission: " + what + " NOT sent - " + why, TraceLevel.Warning);
        return false;
    }

    /// <summary>What became of one restoration step's writes.</summary>
    internal enum RestoreStep
    {
        /// <summary>Every write ran: the debt it stands for may be discharged.</summary>
        Ran,
        /// <summary>A write was refused at its run (a carrier, a changed connection).</summary>
        Withheld,
        /// <summary>The queue did not reach the writes in time.</summary>
        NotReached,
        /// <summary>A write threw, or the step could not be queued.</summary>
        Failed,
    }

    private string InterlockNow()
    {
        try { return theRadio?.InterlockState.ToString() ?? "no radio"; }
        catch { return "unreadable"; }
    }

    /// <summary>
    /// At an unkey's WRITE: may this client's teardown unkey go to the radio?
    /// No when the radio or its command connection is no longer the one the
    /// unkey was asked on, or when another client is the one transmitting
    /// (#686). Zero means no client is transmitting, and then an unkey can
    /// touch no one else's carrier.
    /// </summary>
    private bool OurCarrierToUnkey(Radio owner, object connection, string what)
    {
        var radio = theRadio;
        if (owner == null || !ReferenceEquals(radio, owner))
        {
            Tracing.TraceLine("Teardown unkey (" + what + "): not sent; the radio it was asked for is no longer this one",
                TraceLevel.Info);
            return false;
        }
        if (!ReferenceEquals(radio.CurrentCommandConnection, connection))
        {
            Tracing.TraceLine("Teardown unkey (" + what + "): not sent; the radio's command connection has changed since",
                TraceLevel.Info);
            return false;
        }
        string foreign = ForeignCarrier(radio);
        if (foreign != null)
        {
            Tracing.TraceLine("Teardown unkey (" + what + "): not sent; " + foreign, TraceLevel.Warning);
            return false;
        }
        return true;
    }

    /// <summary>OurCarrierToUnkey said no, and traced why itself.</summary>
    private const string NotOurs = "not ours (traced)";

    /// <summary>
    /// Unkey THIS client's carrier, and nothing else's (#596 N1, #861 B2;
    /// Codex's re-check). Queued like every transmit write, but the queued
    /// closure checks ownership when it actually runs
    /// (<see cref="OurCarrierToUnkey"/>), so a carrier that has passed to
    /// another client while the unkey waited is left alone. For the
    /// teardown's own unkey and the push-to-talk controller's release at
    /// teardown or a drop; the ordinary and emergency unkeys keep
    /// <see cref="Transmit"/>'s unconditional write.
    /// </summary>
    public void UnkeyOwnCarrier(string why)
    {
        CancelSlicePlayback();
        Radio owner = theRadio;
        object connection = owner?.CurrentCommandConnection;
        var offFor = LatestOwnKey;   // the key this unkey ends (round 12, F5)
        q.Enqueue((FunctionDel)(() =>
        {
            // Ownership checked with the write (repair round 5, rule 1).
            // Forced, and retired only when it really went (round 8, #596 B13).
            bool off = false;
            long offMark = 0;
            string refused = WriteOnPinnedTarget(owner, connection,
                () => OurCarrierToUnkey(owner, connection, why) ? null : NotOurs,
                () => off = WriteOwnOff(owner, r => r.SendMoxOff(), out offMark),
                () => ForeignCarrier(owner), unkeyOfOurs: true);
            if (refused == null && off) { _ownKeyRequested = false; NoteOwnOffWritten(offFor, offMark); return; }
            refused ??= "the transport did not take it";
            if (refused != NotOurs)
                Tracing.TraceLine("Teardown unkey (" + why + "): not sent; " + refused, TraceLevel.Info);
        }), "Mox (own carrier only)");
    }

    /// <summary>
    /// The clean disconnect's first step: unkey this session, and confirm
    /// receive before anything is put back. True when the radio positively
    /// reports receive (or nothing was keyed); false when it does not after
    /// <paramref name="receiveWaitMs"/>, and then nothing in the transmit chain
    /// will be put back. Never throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only this session's own carriers</b>, checked at the write: the
    /// unkey and the tune-off are queued unconditionally whenever anything is
    /// or may be keyed, and each re-reads the radio's transmitting client as
    /// it goes out (<see cref="OurCarrierToUnkey"/>). FlexLib's Mox and TXTune
    /// read true for ANY client's transmission on a GUI client that is not
    /// bound to another, so they cannot say whose carrier it is.
    /// </para>
    /// <para>
    /// <b>Work already queued is revoked first</b>: a key-on our own push to
    /// talk queued a moment ago would otherwise key the radio after this
    /// unkey (Codex's re-check of #861, the pending-key schedule).
    /// </para>
    /// <para>
    /// <b>Sources before carriers</b>, as every kill route does
    /// (<see cref="TransmitKillSwitch.DisarmSources"/>).
    /// </para>
    /// </remarks>
    internal bool UnkeyBeforePuttingBack(string why, TimeSpan drainBound, int receiveWaitMs)
    {
        _teardownMicLeftOnPc = null;
        _teardownStillOnAir = null;
        _teardownRestoring = true;
        var radio = theRadio;
        if (radio == null) return true;
        try
        {
            RevokeAdmittedTransmitWork("teardown (" + why + ")");
            TransmitKillSwitch.DisarmSources(this);
            // CW this connection handed to the radio's own CW queue is pending
            // there whether or not the radio reports a carrier yet (#596 N4,
            // repair round 3): a message sent before its key report, or one
            // between break-in transmissions. So it is cleared whenever this
            // connection sent CW, keyed or not, through the same owned route.
            bool cwOwed = _cwSentThisConnection;
            if (cwOwed) EnqueueOwnCwClear(radio, why);
            bool mayBeKeyed = _Transmit || radio.Mox || radio.TXTune || _ownKeyRequested || _ownTuneRequested
                              || !RadioInterlockConfirmsReceive || OwnCarrierNotEnded(radio.CurrentCommandConnection) != null;
            if (!mayBeKeyed)
            {
                Tracing.TraceLine("Teardown (" + why + "): nothing transmitting; nothing to unkey before putting back",
                    TraceLevel.Info);
                if (cwOwed && !DrainCommandQueueAsync(drainBound).GetAwaiter().GetResult())
                    Tracing.TraceLine("Teardown (" + why + "): the CW queue's clear had not left the command queue after "
                        + (int)drainBound.TotalMilliseconds + " ms", TraceLevel.Warning);
                return true;
            }
            Tracing.TraceLine("Teardown (" + why + "): the radio may be transmitting (interlock " + InterlockNow()
                + "); unkeying this session's own carriers, each checked again as it goes out", TraceLevel.Warning);
            object connection = radio.CurrentCommandConnection;
            UnkeyOwnCarrier("teardown (" + why + ")");
            EnqueueTuneWrite(false, "teardown tune", null, ownCarrierOnly: true);
            bool drained = DrainCommandQueueAsync(drainBound).GetAwaiter().GetResult();
            if (!drained)
                Tracing.TraceLine("Teardown (" + why + "): the unkey had not left the command queue after "
                    + (int)drainBound.TotalMilliseconds + " ms", TraceLevel.Warning);
            // Our own off did not go out (repair round 10, Codex's #596 B16):
            // the forced off is tried once more before anything else.
            if (drained && OwnOffUnwritten(connection))
            {
                Tracing.TraceLine("Teardown (" + why + "): our own off was not written; trying it once more",
                    TraceLevel.Warning);
                UnkeyOwnCarrier("teardown (" + why + "), second attempt");
                EnqueueTuneWrite(false, "teardown tune", null, ownCarrierOnly: true);
                if (!DrainCommandQueueAsync(drainBound).GetAwaiter().GetResult())
                    Tracing.TraceLine("Teardown (" + why + "): the second unkey had not left the command queue after "
                        + (int)drainBound.TotalMilliseconds + " ms", TraceLevel.Warning);
            }
            // A carrier the radio's own push to talk holds (a foot switch, a hand
            // mic: source MIC, ACC or RCA) with our own off already written is
            // one this computer cannot end, so the receive wait is not made
            // (integration fix, 2026-11-06): it blocked the UI thread for its
            // whole length and could only end refused. Nothing is put back.
            string hardware = HardwarePttHolds(radio);
            if (hardware != null && !OwnOffUnwritten(connection))
            {
                _teardownStillOnAir = hardware;
                Tracing.TraceLine("Teardown (" + why + "): " + hardware + "; not waiting for receive, and nothing in the"
                    + " transmit chain will be put back", TraceLevel.Warning);
                return false;
            }
            // Receive, and our own carrier ended by the question its falling cue
            // asks: a receive reported after our own off really went out.
            bool receive = await(() => RadioInterlockConfirmsReceive && OwnCarrierNotEnded(connection) == null, receiveWaitMs);
            if (receive)
            {
                Tracing.TraceLine("Teardown (" + why + "): the radio reports receive; putting back may go ahead,"
                    + " and each put-back asks again as it writes", TraceLevel.Info);
                return true;
            }
            string own = OwnCarrierNotEnded(connection);
            _teardownStillOnAir = own != null
                ? own + ", " + receiveWaitMs + " ms into the teardown"
                : "the radio's interlock still reported " + InterlockNow() + " " + receiveWaitMs
                    + " ms into the teardown (a foot switch, VOX or another client may be keying it)";
            Tracing.TraceLine("Teardown (" + why + "): " + _teardownStillOnAir
                + "; nothing in the transmit chain will be put back, so the room microphone cannot be switched onto the air",
                TraceLevel.Warning);
            return false;
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("Teardown (" + why + "): unkeying threw: " + ex.Message, TraceLevel.Error);
            _teardownStillOnAir = "the unkey before the put-back could not be confirmed (" + ex.Message + ")";
            return false;
        }
    }

    /// <summary>
    /// Why the carrier is held by the radio's own push to talk, or null: its
    /// interlock reports transmit and the source is a hardware input (MIC, ACC
    /// or RCA), on a report this connection received. Reads only.
    /// </summary>
    private static string HardwarePttHolds(Radio radio)
    {
        if (radio == null) return null;
        var last = InterlockReports.LastOn(radio.CurrentCommandConnection as CommandConnection);
        if (last == null || last.IsReceive) return null;
        if (radio.InterlockState is not (InterlockState.Transmitting or InterlockState.PTTRequested)) return null;
        var source = radio.PTTSource;
        if (source is not (PTTSource.Mic or PTTSource.ACC or PTTSource.RCA)) return null;
        return "the radio's own push to talk (" + source + ") is holding the carrier, which this computer cannot end";
    }

    /// <summary>
    /// Clear the radio's own CW queue of what this connection sent. It holds
    /// only this client's text, but it keys the shared transmitter, so it is
    /// cleared only when the carrier is not someone else's, checked as the
    /// write goes out (<see cref="OurCarrierToUnkey"/>).
    /// </summary>
    private void EnqueueOwnCwClear(Radio radio, string why)
    {
        Radio owner = radio;
        object connection = radio.CurrentCommandConnection;
        q.Enqueue((FunctionDel)(() =>
        {
            string refused = WriteOnPinnedTarget(owner, connection,
                () => OurCarrierToUnkey(owner, connection, "the CW queue") ? null : NotOurs, () => cwx?.ClearBuffer(),
                () => ForeignCarrier(owner), unkeyOfOurs: true);
            if (refused != null && refused != NotOurs)
                Tracing.TraceLine("Teardown unkey (the CW queue): not sent; " + refused, TraceLevel.Info);
        }), "CWX clear (own carrier only)");
    }

    /// <summary>
    /// During a teardown: true, with a trace, when <paramref name="what"/> must
    /// not be put back now, because the teardown could not confirm receive or
    /// the radio does not positively report receive at this moment. The debt
    /// is kept where the next connect finds it. False outside a teardown.
    /// </summary>
    internal bool TeardownRefusesRestore(string what)
    {
        if (!_teardownRestoring) return false;
        string onAir = WhyNotReceiving(theRadio);
        if (onAir == null) return false;
        Tracing.TraceLine("Teardown: NOT putting back " + what + " - " + onAir
            + ". It is kept for the next connect rather than written under a carrier.", TraceLevel.Warning);
        return true;
    }

    /// <summary>Why the transmit chain of <paramref name="target"/> may not be written now, or null when it reports receive.</summary>
    private string WhyNotReceiving(Radio target)
    {
        string onAir = _teardownStillOnAir;
        if (onAir != null) return onAir;
        // A carrier of our own that has not ended, by the question its falling
        // cue asks (repair round 10, Codex's #596 B16): a receive report from
        // before our off was written does not count.
        string own = OwnCarrierNotEnded(target?.CurrentCommandConnection);
        if (own != null) return own;
        if (InterlockConfirmsReceiveOn(target)) return null;
        string state;
        try { state = target?.InterlockState.ToString() ?? "no radio"; }
        catch { state = "unreadable"; }
        return "the radio's interlock reports " + state + " at the moment of the write";
    }

    /// <summary>
    /// At the microphone put-back's WRITE (#861 B1, repair round 2): true,
    /// with a trace, when the radio is not positively receiving right now,
    /// teardown or not. Switching the input from PC to the radio's own
    /// microphone under a live carrier puts the room on the air. The saved
    /// value is not written and is not reported as put back; the caller keeps
    /// it owed or abandons it (<see cref="KeepOwedOrAbandon"/>, round 9).
    /// </summary>
    private bool MicPutBackWithheld(string when, string saved, Radio radio, object connection)
    {
        // The family's one question (repair round 3, rule 1), asked of the
        // radio and connection the write will use (repair round 5, Codex's B1).
        string onAir = RestoreRefusedAtWrite(radio, connection);
        if (onAir == null) return false;
        // The line names the place; the caller's next line says whether the
        // value is kept owed on its own run and connection or abandoned (round
        // 9). Nothing replays it on another connection (that is #590). The line
        // stays; since 2026-11-06 a withhold during a deliberate disconnect is
        // also SPOKEN, once the hang-up is done (J3, below).
        Tracing.TraceLine("MicPutBackWithheld: radio " + (radio?.Serial ?? "(none)") + ", PutBackSavedMicInput ("
            + when + "): NOT writing '" + saved + "' - " + onAir + ". The mic input is not written.", TraceLevel.Warning);
        // Withheld because the radio was still transmitting, during a deliberate
        // disconnect: remembered for the sentence at its end. A refusal because
        // the radio or its connection is no longer the one written to is not
        // "while transmitting", and is not remembered.
        if (_teardownRestoring && radio != null && PinnedTargetRefused(radio, connection) == null)
            RememberMicLeftOnPc(radio, saved);
        return true;
    }

    // ── J3: a withheld mic put-back at disconnect is spoken ─────────────
    //
    // RULED by Noel 2026-11-06 10:57 (night question J3), replacing the
    // 2026-11-04 "logged only": that ruling's premise, that the next connection
    // finishes the put-back, was removed by repair round 5, so a disconnect
    // while the radio is still transmitting leaves its mic input on PC for good.
    // His wording, 11:02, with "while transmitting" at 11:03 (a lock, VOX or a
    // hand mic as well as a held key). Carrying the put-back to the next
    // connection stays with #590.
    //
    // HOW IT REACHES HIM. Speech at a disconnect is often cut off, so it takes
    // the route the audio check's own disconnect sentence takes
    // (CheckYourAudioDialog's teardown, SpeechSubject.CheckSettingsNotRestored):
    // QUEUED, at Critical, under its own subject, and not gated by the rig's
    // SuppressSpeech, which a menu disconnect or a radio switch sets so that the
    // radio layer's own narration stays quiet. Queued and subject-keyed, the
    // arbiter holds it in its pending ledger, so our own later interrupts cannot
    // destroy it unsaid; Critical, so no verbosity drops it; and said AFTER the
    // hang-up, so the rig's own "disconnected" interrupt cannot cut it. What can
    // still lose it: the screen reader flushing its own queue on a window change
    // (a radio switch opens the picker; an exit closes everything). The
    // arbiter's outcome line in the trace says whether the reader finished it.

    /// <summary>What a deliberate disconnect left on the radio's mic input, for <see cref="SayMicLeftOnPcAfterTeardown"/>.</summary>
    private sealed class MicLeftBehind
    {
        public MicLeftBehind(Radio radio, string saved, string left, string cacheAtWithhold, bool carrier)
        {
            Radio = radio;
            Saved = saved;
            Left = left;
            CacheAtWithhold = cacheAtWithhold;
            Carrier = carrier;
        }
        /// <summary>
        /// A carrier was established when it was withheld: this computer keying,
        /// or the radio's interlock reporting PTT requested, transmitting or
        /// unkey requested. False for a fault or an unconfirmed state (Codex's R5).
        /// </summary>
        public bool Carrier { get; }
        public Radio Radio { get; }
        /// <summary>The input that was not put back.</summary>
        public string Saved { get; }
        /// <summary>The input the radio is left on: the PC this app set, by the radio's own name for it.</summary>
        public string Left { get; }
        /// <summary>FlexLib's reading when it was withheld: a different reading later means a newer choice.</summary>
        public string CacheAtWithhold { get; }
    }

    private MicLeftBehind _teardownMicLeftOnPc;

    /// <summary>The last sentence handed to speech for a mic input left on PC at a disconnect, or null. For tests.</summary>
    internal string LastMicLeftOnPcReport { get; private set; }

    /// <summary>How many such sentences have been handed to speech. For tests.</summary>
    internal int MicLeftOnPcReports { get; private set; }

    /// <summary>Test seam: the route the sentence took (intent, level, subject). Null in production.</summary>
    internal Action<Speech.SpeechIntent, VerbosityLevel, string> MicLeftOnPcRouteProbe { get; set; }

    private static string MicInputNow(Radio radio)
    {
        try { return radio?.MicInput ?? ""; }
        catch { return ""; }
    }

    /// <summary>A put-back of <paramref name="saved"/> was withheld during this teardown, the radio still transmitting.</summary>
    private void RememberMicLeftOnPc(Radio radio, string saved)
    {
        string cache = MicInputNow(radio);
        // The radio's own name for the input this app set, when its cache shows
        // it; otherwise (a switch-back's raw write the radio has not reported
        // yet) the input that write sent.
        string left = string.Equals(cache, "PC", StringComparison.OrdinalIgnoreCase) ? cache : "PC";
        // "While transmitting" only when a carrier is established (rulings round
        // two, Codex's R5): a fault (TX_FAULT) or a radio that never confirmed
        // receive still withholds the restore, but gets the neutral sentence.
        bool carrier;
        try
        {
            carrier = _Transmit || radio.InterlockState is InterlockState.PTTRequested
                or InterlockState.Transmitting or InterlockState.UnkeyRequested;
        }
        catch { carrier = false; }
        System.Threading.Volatile.Write(ref _teardownMicLeftOnPc, new MicLeftBehind(radio, saved ?? "", left, cache, carrier));
    }

    /// <summary>
    /// A later write of the put-back, or a newer mic choice, settled what the
    /// withhold left: nothing is left on PC by this app any more.
    /// </summary>
    private void MicLeftOnPcSettled(Radio radio)
    {
        var left = System.Threading.Volatile.Read(ref _teardownMicLeftOnPc);
        if (left != null && ReferenceEquals(left.Radio, radio))
            System.Threading.Interlocked.CompareExchange(ref _teardownMicLeftOnPc, null, left);
    }

    /// <summary>
    /// The mic input put-back still owed when a deliberate disconnect stops PC
    /// audio (withheld earlier, under a carrier, and kept owed on this run and
    /// connection) is made NOW, before the hang-up, through the same pinned
    /// write as every put-back: paid if the radio reports receive, withheld
    /// (and so said at the end) if it is still transmitting. Without this, a
    /// debt owed while PC audio was already stopped waited for a receive report
    /// the disconnect then cut off, and was dropped in silence.
    /// </summary>
    internal void PayOwedMicPutBackForTeardown(string why)
    {
        bool owed;
        lock (_micSwitchLock) owed = _micInputSwap != null && !_micSessionOpen;
        if (!owed) return;
        Tracing.TraceLine("Teardown (" + why + "): a mic input put-back is still owed on this connection;"
            + " making it now, before the hang-up", TraceLevel.Info);
        PutBackSavedMicInput("teardown (" + why + "), a put-back still owed", sameRunOnly: true);
    }

    /// <summary>
    /// At the end of a deliberate disconnect, after the hang-up: if a mic input
    /// put-back was withheld during it because the radio was still
    /// transmitting, and nothing has put it back or chosen another input since,
    /// say so (J3, ruled by Noel 2026-11-06). Never throws.
    /// </summary>
    internal void SayMicLeftOnPcAfterTeardown(string why)
    {
        try
        {
            var left = System.Threading.Interlocked.Exchange(ref _teardownMicLeftOnPc, null);
            if (left == null) return;
            string now = MicInputNow(left.Radio);
            if (!string.Equals(now, left.CacheAtWithhold, StringComparison.OrdinalIgnoreCase))
            {
                Tracing.TraceLine("Teardown (" + why + "): the withheld mic input put-back of '" + left.Saved
                    + "' is not said: the mic input has read '" + now + "' since, not the '" + left.CacheAtWithhold
                    + "' it read then", TraceLevel.Info);
                return;
            }
            // DRAFT PROSE (J3): Noel's own wording, 2026-11-06 11:02 and 11:03.
            // DRAFT PROSE (R5): the neutral sentence when no carrier was established.
            string text = Lexicon.Get(left.Carrier ? "connect.disconnect.mic_left_on" : "connect.disconnect.mic_left_on_unconfirmed",
                ScreenReaderOutput.CurrentVerbosity, ("input", left.Left));
            LastMicLeftOnPcReport = text;
            MicLeftOnPcReports++;
            Tracing.TraceLine("MicPutBackWithheld at disconnect (" + why + "): radio " + (left.Radio?.Serial ?? "(none)")
                + " is left on '" + left.Left + "', not put back to '" + left.Saved + "'; said: " + text,
                TraceLevel.Warning);
            MicLeftOnPcRouteProbe?.Invoke(Speech.SpeechIntent.Queue, VerbosityLevel.Critical, Speech.SpeechSubject.MicInputLeftOnPc);
            ScreenReaderOutput.Speak(text, Speech.SpeechIntent.Queue, VerbosityLevel.Critical,
                subject: Speech.SpeechSubject.MicInputLeftOnPc);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("Teardown (" + why + "): saying the mic input was left on PC threw: " + ex.Message, TraceLevel.Error);
        }
    }

    /// <summary>
    /// After PC audio has stopped: a microphone put-back the switch-back left
    /// on the command queue (<see cref="PutBackAfterSwitchBack"/>) runs before
    /// the command loop is stopped and the link closed (#861, Codex's check,
    /// blocker 2). Joining the audio thread proves the put-back was queued,
    /// not that it ran. Bounded; traced either way. Never throws.
    /// </summary>
    internal void DrainQueuedMicPutBack(string why, TimeSpan bound)
    {
        try
        {
            bool owed;
            lock (_micSwitchLock) owed = _queuedPutBack != null;
            if (!owed) return;
            bool reached = DrainCommandQueueAsync(bound).GetAwaiter().GetResult();
            bool stillOwed;
            lock (_micSwitchLock) stillOwed = _queuedPutBack != null;
            if (reached && !stillOwed)
                Tracing.TraceLine("Teardown (" + why + "): the queued mic input put-back ran (or was withheld, as its"
                    + " own line says) before the hang-up", TraceLevel.Info);
            else
                Tracing.TraceLine("Teardown (" + why + "): the queued mic input put-back did NOT run within "
                    + (int)bound.TotalMilliseconds + " ms (" + (reached ? "the queue drained but it is still owed" : "the queue did not drain")
                    + "); the mic input is not put back", TraceLevel.Warning);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("Teardown (" + why + "): draining the queued mic put-back threw: " + ex.Message, TraceLevel.Error);
        }
    }

    /// <summary>The teardown is over: nothing withheld outlives it.</summary>
    private void EndTeardownUnkey()
    {
        _teardownMicLeftOnPc = null;
        _teardownStillOnAir = null;
        _teardownRestoring = false;
        _ownKeyRequested = false;
        _ownTuneRequested = false;
        _cwSentThisConnection = false;
    }
}
