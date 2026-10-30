#nullable enable
using System;

namespace Radios
{
    /// <summary>Where a send stands across its pauses (#835).</summary>
    public enum SendSessionState
    {
        /// <summary>A piece of the recording is going out, or getting ready to.</summary>
        Running,
        /// <summary>
        /// Paused at a held place. Nothing is keyed by this send, nothing is
        /// engaged, and nothing will key the radio until Play is pressed.
        /// </summary>
        Paused,
        /// <summary>Over, however it ended. Nothing will carry on.</summary>
        Ended,
    }

    /// <summary>What a press of the send's own door means right now.</summary>
    public enum SendPress
    {
        /// <summary>No send is running or paused: the door does what it always did.</summary>
        StartNew,
        /// <summary>Hold the place, unkey only what the send keyed, give the microphone back.</summary>
        Pause,
        /// <summary>Carry on from the held place.</summary>
        Resume,
        /// <summary>End the send.</summary>
        Stop,
    }

    /// <summary>How a paused send carries on, decided at the Play press.</summary>
    public enum ResumeWay
    {
        /// <summary>The VOX send box: pause VOX again and key, exactly as a fresh box send.</summary>
        Box,
        /// <summary>The operator is holding the transmitter: ride it, never touch the key.</summary>
        Join,
        /// <summary>A voice message as #151 sends one: key the radio, or with VOX on let VOX key it.</summary>
        AsVoiceMessage,
        /// <summary>
        /// A recording, with nobody keying and the box not applying: Play
        /// never keys the radio by itself (2026-10-21 10:57), so the send stays
        /// paused and the operator is told to key first.
        /// </summary>
        NeedsKey,
    }

    /// <summary>
    /// One send to the air across the pieces it goes out in (#835): the
    /// ten-minute pause, and Play pausing and carrying on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rulings</b> (Noel, 2026-10-27). 20:48: a send longer than 180
    /// seconds warns first; at 600 seconds, the US identification interval, it
    /// pauses so it can restart. 20:50: <i>"it's got to unkey so you can ID,
    /// play keys it and stop stops it."</i> 20:51: <i>"That's how you pause
    /// normally ... you stop play and pause the same way"</i>: the same Play
    /// pauses a send and carries it on; nothing send-specific.
    /// </para>
    /// <para>
    /// <b>One piece is one <see cref="VoiceMessageSend"/>.</b> A pause ends the
    /// piece with <see cref="VoiceMessageOutcome.Paused"/>, which unkeys only
    /// what the piece keyed and gives the microphone back, exactly as a stop
    /// does. Carrying on starts a NEW piece from the held place, through the
    /// same doors a fresh press uses, so a resumed box send pauses VOX and
    /// keys exactly as a fresh one does and is under the same cut. Nothing
    /// here keys anything, and nothing here reacts to the radio: while paused
    /// there is no piece, so push to talk, VOX or another client keying the
    /// radio meets nothing of ours, and releasing it resumes nothing.
    /// </para>
    /// <para>
    /// <b>The held place</b> is where the current piece started in the whole
    /// recording plus how far the piece got. Each piece is loaded from that
    /// place.
    /// </para>
    /// <para>
    /// <b>The ten minutes</b> are counted from the send's start or from its
    /// last identification pause, across the operator's own pauses (Noel,
    /// 2026-10-28 21:25, replacing the first round's "counted afresh after
    /// every pause"): <see cref="SentSinceIdSeconds"/>.
    /// </para>
    /// </remarks>
    public sealed class SendSession
    {
        /// <summary>A send that will go out in pieces of a recording this long.</summary>
        public SendSession(double totalSeconds)
        {
            TotalSeconds = Math.Max(0, totalSeconds);
        }

        /// <summary>
        /// A send that starts part-way into the recording: a recording paused
        /// on the speakers that a press now sends from where it was paused
        /// (#835; Noel, 2026-10-30 05:56). The first piece must have been
        /// loaded from <paramref name="startAtSeconds"/>.
        /// </summary>
        public SendSession(double totalSeconds, double startAtSeconds) : this(totalSeconds)
        {
            HeldAtSeconds = Math.Min(TotalSeconds, Math.Max(0, startAtSeconds));
        }

        /// <summary>The whole recording's length, in seconds.</summary>
        public double TotalSeconds { get; }

        /// <summary>
        /// Where the current piece started in the whole recording, or where
        /// the next one will start while paused.
        /// </summary>
        public double HeldAtSeconds { get; private set; }

        /// <summary>How much of the recording is left from <see cref="HeldAtSeconds"/>.</summary>
        public double RemainingSeconds => Math.Max(0, TotalSeconds - HeldAtSeconds);

        /// <summary>The piece going out, or null while paused or ended.</summary>
        public VoiceMessageSend? Piece { get; private set; }

        /// <summary>Where the send stands.</summary>
        public SendSessionState State { get; private set; } = SendSessionState.Running;

        /// <summary>True when the last pause was the ten-minute one rather than the operator's.</summary>
        public bool PausedForId { get; private set; }

        /// <summary>True when the last piece was keyed by this send, so its pause unkeyed the radio.</summary>
        public bool PausedPieceKeyed { get; private set; }

        /// <summary>How many pieces have gone out, the current one included.</summary>
        public int Pieces { get; private set; }

        /// <summary>
        /// How much of the recording has gone out since the send began or since
        /// it last paused for identification. It runs across the operator's OWN
        /// pauses and only the automatic ten-minute pause starts it again (Noel,
        /// 2026-10-28 21:25), so a quick pause of his cannot push the reminder
        /// out to nineteen minutes. Each piece is told it at its start
        /// (<see cref="VoiceMessageSend.CarryIdCount"/>).
        /// </summary>
        public double SentSinceIdSeconds { get; private set; }

        /// <summary>
        /// A piece has started, from <see cref="HeldAtSeconds"/>. The piece's
        /// recording must have been loaded from that place.
        /// </summary>
        public void Begin(VoiceMessageSend piece)
        {
            if (piece == null) throw new ArgumentNullException(nameof(piece));
            if (State == SendSessionState.Ended)
                throw new InvalidOperationException("an ended send cannot carry on");
            Piece = piece;
            State = SendSessionState.Running;
            Pieces++;
            piece.CarryIdCount(SentSinceIdSeconds);
        }

        /// <summary>
        /// The current piece has finished. A pause holds the place and the send
        /// waits for Play; anything else ends the send. Returns the new state.
        /// </summary>
        public SendSessionState Settle()
        {
            VoiceMessageSend? piece = Piece;
            if (piece == null || !piece.Finished) return State;
            Piece = null;
            if (piece.Outcome == VoiceMessageOutcome.Paused)
            {
                HeldAtSeconds = Math.Min(TotalSeconds, HeldAtSeconds + piece.PausedAtSeconds);
                PausedForId = piece.PausedForId;
                SentSinceIdSeconds = piece.PausedForId ? 0 : SentSinceIdSeconds + piece.PausedAtSeconds;
                PausedPieceKeyed = piece.WeKeyed;
                State = SendSessionState.Paused;
            }
            else
            {
                State = SendSessionState.Ended;
            }
            return State;
        }

        /// <summary>
        /// Paused before a piece keyed: the operator pressed Play while a box
        /// send was still waiting for VOX to confirm off. Nothing was sent, so
        /// the place is unchanged.
        /// </summary>
        public void PauseBeforeKeying()
        {
            if (State == SendSessionState.Ended) return;
            Piece = null;
            PausedForId = false;
            PausedPieceKeyed = false;
            State = SendSessionState.Paused;
        }

        /// <summary>
        /// End the send: Stop, Escape, another send's door, or the radio
        /// disconnecting. A running piece is stopped (microphone back, unkeyed
        /// if it keyed); a paused send touches nothing, because nothing of it
        /// is engaged or keyed. Idempotent.
        /// </summary>
        public void Stop()
        {
            VoiceMessageSend? piece = Piece;
            Piece = null;
            if (piece != null && !piece.Finished) piece.Stop();
            State = SendSessionState.Ended;
        }

        /// <summary>
        /// What a press of a send door means. <paramref name="sameDoor"/>: the
        /// press is the door that started this send (Play from computer for a
        /// computer recording, the same slot's key for a voice message).
        /// <paramref name="pressPauses"/>: that door pauses a running send
        /// rather than stopping it (Play does, and so does a voice message's own
        /// key: Noel, 2026-10-28 21:20).
        /// <paramref name="canHoldPlace"/>: the send can carry on from a place
        /// at all (the radio's slice playback cannot, and stops instead).
        /// </summary>
        public static SendPress Answer(SendSessionState? state, bool sameDoor, bool pressPauses, bool canHoldPlace)
        {
            switch (state)
            {
                case SendSessionState.Running:
                    return sameDoor && pressPauses && canHoldPlace ? SendPress.Pause : SendPress.Stop;
                case SendSessionState.Paused:
                    return sameDoor ? SendPress.Resume : SendPress.Stop;
                default:
                    return SendPress.StartNew;
            }
        }

        /// <summary>
        /// How a paused send carries on, from the route a fresh press would
        /// take now (<see cref="VoxSendBox.Choose"/>, read live at the press).
        /// </summary>
        public static ResumeWay ResumeAs(bool voiceMessage, SendRoute routeNow) => routeNow switch
        {
            SendRoute.Box => ResumeWay.Box,
            SendRoute.JoinTransmission => ResumeWay.Join,
            _ => voiceMessage ? ResumeWay.AsVoiceMessage : ResumeWay.NeedsKey,
        };
    }
}
