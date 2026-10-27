#nullable enable
using System;

namespace Radios
{
    /// <summary>Which way a send goes, decided once at the press (#835).</summary>
    public enum SendRoute
    {
        /// <summary>
        /// The VOX send box: key the radio, send the whole recording with VOX held
        /// off, unkey, hand back to VOX
        /// (<see cref="VoiceMessageSend.BeginKeyed"/>).
        /// </summary>
        Box,

        /// <summary>
        /// The radio is already transmitting and the box does not take it: the
        /// recording joins that transmission and never touches the key. Push to
        /// talk, the lock, a hardware PTT line, or (box unticked) VOX.
        /// </summary>
        JoinTransmission,

        /// <summary>
        /// Everything as it was before the box: Play listens on the speakers,
        /// and a voice message keys or lets VOX key it as #151 ruled.
        /// </summary>
        AsBefore,
    }

    /// <summary>
    /// The VOX send box (#835, Noel 2026-10-27 14:54): one operator preference, off by
    /// default and saved with the operator's audio settings rather than per
    /// radio, and the one rule that decides what it does to a send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> A VOX operator is never keyed unless talking, so
    /// without it Play can never reach the air; and letting VOX key a recording
    /// drops the transmission in the recording's pauses and clips its first
    /// word. With the box ticked and VOX on, ANY send — a voice message, the
    /// radio's slice recording, or a computer recording — keys the radio
    /// itself.
    /// </para>
    /// <para>
    /// <b>The rule, all of it in <see cref="Choose"/>.</b> The box applies only
    /// when ticked AND VOX is on for a voice mode, and never to a transmission
    /// the operator is holding — push to talk, the lock, or a hardware PTT
    /// line (#151: PTT stays with the operator). A radio held up only by VOX
    /// counts as unkeyed for the box (15:28). Unticked, every existing rule
    /// stands unchanged (14:54, 15:25).
    /// </para>
    /// <para>
    /// The decision is pure so a test can hold every combination; the doors
    /// read the live state and ask it once per press. Nothing here keys
    /// anything.
    /// </para>
    /// </remarks>
    public static class VoxSendBox
    {
        private static volatile bool _ticked;

        /// <summary>
        /// The operator's preference. Off by default. Set at startup from the
        /// saved audio settings and by every door that toggles it.
        /// </summary>
        public static bool Ticked
        {
            get => _ticked;
            set => _ticked = value;
        }

        /// <summary>
        /// Which way a send goes. Read the inputs live at the press.
        /// </summary>
        /// <param name="ticked">The box (<see cref="Ticked"/>).</param>
        /// <param name="voiceVoxOn">
        /// VOX is on for the transmit slice's voice mode: <c>SimpleVOXEnable</c>,
        /// read live (<c>FlexBase.CurrentVoxSettingIfOn</c>). CW break-in is not
        /// VOX for the box: its Home control collapses in CW with gain and delay.
        /// </param>
        /// <param name="transmitting">The radio reports transmitting, from any source.</param>
        /// <param name="operatorHolds">
        /// The operator is keying the radio: push to talk, the lock, a hardware
        /// PTT line, or the tune carrier. False when the radio is up only on VOX.
        /// </param>
        public static SendRoute Choose(bool ticked, bool voiceVoxOn, bool transmitting, bool operatorHolds)
        {
            if (operatorHolds) return SendRoute.JoinTransmission;
            if (ticked && voiceVoxOn) return SendRoute.Box;
            return transmitting ? SendRoute.JoinTransmission : SendRoute.AsBefore;
        }

        /// <summary>
        /// How long a box send waits for a radio held up only by VOX to drop
        /// once VOX has been switched off for the send, before concluding
        /// something else holds it and joining the transmission instead. VOX's
        /// own hang time tops out at <see cref="FlexBase.VoxDelayMax"/>; half a
        /// second more covers the status echo.
        /// </summary>
        public static readonly TimeSpan VoxTailDropWait = TimeSpan.FromMilliseconds(FlexBase.VoxDelayMax + 500);
    }
}
