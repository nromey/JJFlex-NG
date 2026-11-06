#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using JJTrace;

namespace Radios
{
    /// <summary>How the silence after a reference recording's end came to an end.</summary>
    public enum ReferenceSilenceEnd
    {
        /// <summary>The radio reported receive on the connection the silence began on; the microphone is back.</summary>
        Received,
        /// <summary>The connection went (a disconnect, a new connection or another radio); the microphone is back.</summary>
        ConnectionGone,
        /// <summary>Something else let the player go first (a disarm, a window closing, the kill switch, a new load); nothing left to do.</summary>
        AlreadyReleased,
    }

    /// <summary>
    /// FlexBase, the silence after a reference recording's end (Unified takes,
    /// round four, part 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ruled by Noel 2026-11-05.</b> 01:37, a hardware push to talk still
    /// held when the reference ends: send silence until the operator lets go.
    /// 01:38, the moment the app takes to unkey after it: <i>"silence as same
    /// as hardware PTT"</i>. So from a reference's end until the radio is
    /// receiving, the transmitter carries silence and never the microphone,
    /// whoever holds the key. After a reference only: a recorded send and Play
    /// from computer start with <see cref="TxFileStart"/> and hand the
    /// microphone back at their end, as the 2026-10-27 send rule says.
    /// </para>
    /// <para>
    /// <b>The silence is the player's</b>
    /// (<see cref="JJPortaudio.TxFilePlayer.StartHoldingSilenceAtEnd"/>), and it
    /// starts by itself the moment the recording runs out, on the audio thread,
    /// so nothing here has to be on time for the microphone to stay off. <b>What
    /// this owns is its END</b>: <see cref="TxReferenceEndSilenceUntilReceive"/>
    /// lets the player go once the radio is really receiving, by
    /// <see cref="TxFileStop"/>, the reference's own disarm.
    /// </para>
    /// <para>
    /// <b>"Receiving" is the radio's word</b>, the same wait a send's monitor and
    /// VOX pause make after a takeover (<see cref="WhenRadioReallyReceives"/>):
    /// our cached transmit false AND the radio's interlock out of transmit. Our
    /// Mox cache alone falls the moment we ask for an unkey, which is why the
    /// Mox-fall disarm in <c>noteRadioMox</c> stands aside while the silence is
    /// held.
    /// </para>
    /// <para>
    /// <b>It cannot outlive the connection.</b> The wait is pinned to the radio
    /// and command connection it began on, and ends when either goes. Every
    /// other way out reaches <see cref="TxFileStop"/> directly: the window's
    /// disarm, the radio's teardown, a disconnect's VOX release and the hard
    /// kill. A window that closes, or switches to another radio, with the
    /// silence still held does NOT stop it (round five, 7b; Noel 2026-11-06
    /// 03:12, "silence is fine"): it asks for a wait of its own here and lets
    /// that wait end it. Then the wait, when it ends, finds nothing held and does
    /// nothing. A generation number keeps an old wait from ending a newer
    /// pass's silence.
    /// </para>
    /// <para>
    /// <b>The generation moves when a wait begins AND whenever the player is
    /// started or stopped through FlexBase</b> (round four repair, part 6e-3).
    /// Until then it moved only when a wait began, so a wait whose callback was
    /// held up after deciding (receive seen, or its connection gone, a
    /// reconnect on the same FlexBase included) and before it took the gate
    /// could resume after a disarm, a fresh arm and the newer pass's end, but
    /// before the window's poll had asked for that pass's own wait. It then
    /// read the newer pass's silence as its own and stopped it, with the radio
    /// transmitting the newer pass. A wait now belongs to the pass that was
    /// playing when it began; a new pass cannot begin without a start through
    /// FlexBase, so it can never be mistaken for the old one.
    /// </para>
    /// <para>
    /// <b>It cannot hold a transmitter keyed.</b> It replaces what the encoder is
    /// handed; nothing that keys or unkeys the radio reads it. What it can do,
    /// if every way out failed, is leave the microphone off the encoder on a
    /// live connection with the radio still reporting transmit. That is why
    /// there is no time limit here either, deliberately as in
    /// <see cref="WhenRadioReallyReceives"/>: a radio still transmitting is
    /// exactly when the microphone must stay off.
    /// </para>
    /// </remarks>
    public partial class FlexBase
    {
        private int _referenceSilenceGeneration;

        /// <summary>
        /// The player is being started or stopped: every wait already running
        /// belongs to a pass that is over. Called by the starts and
        /// <see cref="TxFileStop"/>; see the remarks.
        /// </summary>
        private void StaleReferenceSilenceWaits() => Interlocked.Increment(ref _referenceSilenceGeneration);

        /// <summary>
        /// True while a reference recording has run off its end and the
        /// transmitter is carrying silence in place of the microphone: from
        /// the end itself, through the player's final fade, until the silence
        /// is let go.
        /// </summary>
        /// <remarks>
        /// The player's <see cref="JJPortaudio.TxFilePlayer.HoldingEndSilence"/>,
        /// the one definition (round four repair, part 6e). Everything in
        /// FlexBase that decides whether a reference's end may give the
        /// microphone back asks it through this name: the Mox-fall guard in
        /// <c>noteRadioMox</c> and the receive wait below. The window's arm
        /// refuses on it, so a re-arm is refused in the fade as well.
        /// </remarks>
        public bool TxReferenceEndSilenceHeld => txFilePlayer.HoldingEndSilence;

        /// <summary>
        /// Give the microphone back once the radio is really receiving, after a
        /// reference recording's end; or at once, with the connection gone.
        /// Runs <paramref name="then"/> exactly once, on this thread when
        /// nothing had to be waited for and on the pool otherwise.
        /// </summary>
        public void TxReferenceEndSilenceUntilReceive(Action<ReferenceSilenceEnd> then)
        {
            int generation = Interlocked.Increment(ref _referenceSilenceGeneration);
            bool waited = false;
            WhenRadioReallyReceives(sameConnection =>
            {
                ReferenceSilenceEnd how;
                lock (_voxPauseGate)
                {
                    if (generation != Volatile.Read(ref _referenceSilenceGeneration) || !TxReferenceEndSilenceHeld)
                        how = ReferenceSilenceEnd.AlreadyReleased;
                    else
                    {
                        // The reference's own disarm: the player hands the
                        // microphone back, and the reference's VOX pause goes
                        // after the drain, now that the radio is receiving.
                        TxFileStop();
                        how = sameConnection ? ReferenceSilenceEnd.Received : ReferenceSilenceEnd.ConnectionGone;
                    }
                }
                if (waited || how != ReferenceSilenceEnd.Received)
                    Tracing.TraceLine("Reference end: " + (how switch
                    {
                        ReferenceSilenceEnd.Received => "the radio is receiving; the microphone is back",
                        ReferenceSilenceEnd.ConnectionGone => "the connection went; the microphone is back",
                        _ => "the silence had already been let go",
                    }), TraceLevel.Info);
                then(how);
            }, beforeWaiting: () =>
            {
                waited = true;
                Tracing.TraceLine("Reference end: silence in place of the microphone until the radio receives (Transmit "
                    + _Transmit + ", interlock " + theRadio?.InterlockState + ")", TraceLevel.Info);
            });
        }
    }
}
