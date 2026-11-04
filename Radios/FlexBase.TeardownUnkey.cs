using System;
using System.Diagnostics;
using System.Threading;
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

    /// <summary>How long the teardown waits for its unkey writes to leave the command queue.</summary>
    internal static readonly TimeSpan TeardownUnkeyDrainBound = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long the teardown waits for the radio to report receive after its unkey.</summary>
    internal const int TeardownReceiveWaitMs = 2000;

    /// <summary>How long the teardown waits for a queued microphone put-back to run before the hang-up.</summary>
    internal static readonly TimeSpan TeardownPutBackDrainBound = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Set while a clean disconnect could not confirm the radio was receiving:
    /// what it still reported. The microphone input is then NOT put back
    /// (<see cref="TeardownWithholdsMicPutBack"/>), because switching it from
    /// PC to the radio's own input while something keeps the transmitter up
    /// (a foot switch, VOX, another client) could put the room microphone on
    /// the air. Null otherwise.
    /// </summary>
    private volatile string _teardownStillOnAir;

    /// <summary>
    /// The clean disconnect's first step: unkey this session, and confirm
    /// receive before anything is put back. True when the radio reports
    /// receive (or nothing was keyed); false when it still reports transmit
    /// after <paramref name="receiveWaitMs"/>, and then the microphone input
    /// will not be put back. Never throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only this session's own carriers.</b> The unkey is sent only when the
    /// radio's interlock names this client as the one transmitting (or names
    /// none yet, while our own request is in flight), so another operator's
    /// transmission on a shared radio is never unkeyed from here (#686). Their
    /// transmission still withholds the microphone put-back.
    /// </para>
    /// <para>
    /// <b>Sources before carriers</b>, as every kill route does
    /// (<see cref="TransmitKillSwitch.DisarmSources"/>): a voice file or test
    /// tone left engaged would replace the microphone, and a VOX send left
    /// flowing would simply key the radio again.
    /// </para>
    /// </remarks>
    internal bool UnkeyBeforePuttingBack(string why, TimeSpan drainBound, int receiveWaitMs)
    {
        _teardownStillOnAir = null;
        var radio = theRadio;
        if (radio == null) return true;
        try
        {
            // FlexLib's Mox and TXTune read true for ANY client's transmission
            // on a GUI client that is not bound to another (ShouldUpdateMoxOrTuneState),
            // so "ours" is decided by the interlock's transmitting client:
            // this session's handle, or none yet (our own request in flight).
            uint txClient = radio.TXClientHandle;
            bool ourTransmitter = txClient == 0 || txClient == clientHandle;
            bool oursKeyed = ourTransmitter && (_Transmit || radio.Mox);
            bool oursTuning = ourTransmitter && radio.TXTune;
            bool interlockKeyed = RadioInterlockReportsTransmit;
            TransmitKillSwitch.DisarmSources(this);
            if (!oursKeyed && !oursTuning && !interlockKeyed)
            {
                Tracing.TraceLine("Teardown (" + why + "): nothing transmitting; nothing to unkey before putting back",
                    TraceLevel.Info);
                return true;
            }
            if (oursKeyed)
            {
                Tracing.TraceLine("Teardown (" + why + "): this session is transmitting; unkeying before anything is put back",
                    TraceLevel.Warning);
                Transmit = false;
            }
            if (oursTuning)
            {
                Tracing.TraceLine("Teardown (" + why + "): this session's tune carrier is up; dropping it before anything is put back",
                    TraceLevel.Warning);
                TxTune = false;
            }
            bool drained = DrainCommandQueueAsync(drainBound).GetAwaiter().GetResult();
            if (!drained)
                Tracing.TraceLine("Teardown (" + why + "): the unkey had not left the command queue after "
                    + (int)drainBound.TotalMilliseconds + " ms", TraceLevel.Warning);
            bool receive = await(() => !RadioInterlockReportsTransmit, receiveWaitMs);
            if (receive)
            {
                Tracing.TraceLine("Teardown (" + why + "): the radio reports receive; putting back may go ahead",
                    TraceLevel.Info);
                return true;
            }
            string state;
            try { state = radio.InterlockState.ToString(); } catch { state = "transmit"; }
            _teardownStillOnAir = "the radio's interlock still reported " + state + " " + receiveWaitMs
                + " ms after this session unkeyed (a foot switch, VOX or another client may be keying it)";
            Tracing.TraceLine("Teardown (" + why + "): " + _teardownStillOnAir
                + "; the microphone input will NOT be put back, so the room microphone cannot be switched onto the air",
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
    /// True, with a trace saying so, when a clean disconnect could not confirm
    /// receive and the microphone input must not be put back. The saved value
    /// is not written and is not reported as put back: an honest "not put
    /// back" is the outcome.
    /// </summary>
    private bool TeardownWithholdsMicPutBack(string when, string saved)
    {
        string onAir = _teardownStillOnAir;
        if (onAir == null) return false;
        Tracing.TraceLine("PutBackSavedMicInput (" + when + "): NOT writing '" + saved + "' - " + onAir
            + ". The mic input is left on PC and is NOT put back.", TraceLevel.Warning);
        return true;
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
                Tracing.TraceLine("Teardown (" + why + "): the queued mic input put-back ran before the hang-up",
                    TraceLevel.Info);
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
    private void EndTeardownUnkey() => _teardownStillOnAir = null;
}
