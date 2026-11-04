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

    /// <summary>This connection has handed CW to the radio's CWX queue.</summary>
    private volatile bool _cwSentThisConnection;

    /// <summary>
    /// Revoke every piece of transmit work already admitted to the command
    /// queue: key-ons, tune-ons, CW text and CW characters. They are dropped
    /// when the loop reaches them. Unkeys are never revoked.
    /// </summary>
    /// <param name="trace">Where the line goes: a fall passes its own
    /// session-bound trace, so the line is filed with the fall's session.</param>
    internal void RevokeAdmittedTransmitWork(string why, Action<string, TraceLevel> trace = null)
    {
        long now = Interlocked.Increment(ref _transmitWorkGeneration);
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
    internal bool RadioInterlockConfirmsReceive
    {
        get
        {
            var radio = theRadio;
            if (radio == null) return false;
            return radio.InterlockState is InterlockState.Receive or InterlockState.Ready or InterlockState.NotReady;
        }
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
        uint txClient = radio.TXClientHandle;
        if (txClient != 0 && txClient != clientHandle)
        {
            Tracing.TraceLine("Teardown unkey (" + what + "): not sent; another client (0x" + txClient.ToString("X8")
                + ") is the one transmitting now, and its carrier is not ours to unkey", TraceLevel.Warning);
            return false;
        }
        return true;
    }

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
        q.Enqueue((FunctionDel)(() =>
        {
            if (!OurCarrierToUnkey(owner, connection, why)) return;
            owner.Mox = false;
            _ownKeyRequested = false;
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
        _teardownStillOnAir = null;
        _teardownRestoring = true;
        var radio = theRadio;
        if (radio == null) return true;
        try
        {
            RevokeAdmittedTransmitWork("teardown (" + why + ")");
            TransmitKillSwitch.DisarmSources(this);
            bool mayBeKeyed = _Transmit || radio.Mox || radio.TXTune || _ownKeyRequested
                              || !RadioInterlockConfirmsReceive;
            if (!mayBeKeyed)
            {
                Tracing.TraceLine("Teardown (" + why + "): nothing transmitting; nothing to unkey before putting back",
                    TraceLevel.Info);
                return true;
            }
            Tracing.TraceLine("Teardown (" + why + "): the radio may be transmitting (interlock " + InterlockNow()
                + "); unkeying this session's own carriers, each checked again as it goes out", TraceLevel.Warning);
            UnkeyOwnCarrier("teardown (" + why + ")");
            EnqueueTuneWrite(false, "teardown tune", null, ownCarrierOnly: true);
            if (_cwSentThisConnection)
            {
                // The radio's own CW queue holds only what this client sent,
                // but it keys the shared transmitter, so it is cleared only
                // when the carrier is not someone else's.
                Radio owner = radio;
                object connection = radio.CurrentCommandConnection;
                q.Enqueue((FunctionDel)(() =>
                {
                    if (!OurCarrierToUnkey(owner, connection, "the CW queue")) return;
                    cwx?.ClearBuffer();
                }), "CWX clear (own carrier only)");
            }
            bool drained = DrainCommandQueueAsync(drainBound).GetAwaiter().GetResult();
            if (!drained)
                Tracing.TraceLine("Teardown (" + why + "): the unkey had not left the command queue after "
                    + (int)drainBound.TotalMilliseconds + " ms", TraceLevel.Warning);
            bool receive = await(() => RadioInterlockConfirmsReceive, receiveWaitMs);
            if (receive)
            {
                Tracing.TraceLine("Teardown (" + why + "): the radio reports receive; putting back may go ahead,"
                    + " and each put-back asks again as it writes", TraceLevel.Info);
                return true;
            }
            _teardownStillOnAir = "the radio's interlock still reported " + InterlockNow() + " " + receiveWaitMs
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
    /// During a teardown: true, with a trace, when <paramref name="what"/> must
    /// not be put back now, because the teardown could not confirm receive or
    /// the radio does not positively report receive at this moment. The debt
    /// is kept where the next connect finds it. False outside a teardown.
    /// </summary>
    internal bool TeardownRefusesRestore(string what)
    {
        if (!_teardownRestoring) return false;
        string onAir = WhyNotReceiving();
        if (onAir == null) return false;
        Tracing.TraceLine("Teardown: NOT putting back " + what + " - " + onAir
            + ". It is kept for the next connect rather than written under a carrier.", TraceLevel.Warning);
        return true;
    }

    /// <summary>Why the transmit chain may not be written now, or null when the radio reports receive.</summary>
    private string WhyNotReceiving()
    {
        string onAir = _teardownStillOnAir;
        if (onAir != null) return onAir;
        if (RadioInterlockConfirmsReceive) return null;
        return "the radio's interlock reports " + InterlockNow() + " at the moment of the write";
    }

    /// <summary>
    /// At the microphone put-back's WRITE (#861 B1, repair round 2): true,
    /// with a trace, when the radio is not positively receiving right now,
    /// teardown or not. Switching the input from PC to the radio's own
    /// microphone under a live carrier puts the room on the air. The saved
    /// value is not written and is not reported as put back; the caller keeps
    /// it (<see cref="KeepMicPutBackOwed"/>).
    /// </summary>
    private bool MicPutBackWithheld(string when, string saved)
    {
        string onAir = WhyNotReceiving();
        if (onAir == null) return false;
        Tracing.TraceLine("PutBackSavedMicInput (" + when + "): NOT writing '" + saved + "' - " + onAir
            + ". The mic input is not written, and the value is kept as still owed.", TraceLevel.Warning);
        return true;
    }

    /// <summary>
    /// A withheld put-back's value stays owed: back where the next stop or
    /// the next run's start looks for it, unless a newer value is already
    /// there. Not replayed from here.
    /// </summary>
    private void KeepMicPutBackOwed(MicInputSwap swap)
    {
        if (swap == null) return;
        lock (_micSwitchLock)
        {
            if (_micInputSwap == null) _micInputSwap = swap;
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
        _teardownStillOnAir = null;
        _teardownRestoring = false;
        _ownKeyRequested = false;
        _cwSentThisConnection = false;
    }
}
