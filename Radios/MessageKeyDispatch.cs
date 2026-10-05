#nullable enable
using System;

namespace Radios
{
    /// <summary>What a message key should do, given the slot and the radio's mode.</summary>
    public enum MessageKeyAction
    {
        /// <summary>Send the slot's CW text through the radio's keyer.</summary>
        SendCw,
        /// <summary>Send the slot's recording in place of the microphone.</summary>
        SendVoice,
        /// <summary>The radio is in CW and the slot has no CW text. Say so.</summary>
        NothingForCw,
        /// <summary>The radio is in a voice mode and the slot has no recording. Say so.</summary>
        NothingForVoice,
    }

    /// <summary>
    /// The mode branch behind every message key (Sprint 48 Track A, #151).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Noel's ruling, 2026-10-05: <i>"Heck, we could make it work for voice and
    /// CW, same keys."</i> <c>Ctrl+1</c> means "send my CQ" whatever mode the
    /// radio is in. The slot holds the message; the radio's mode decides
    /// whether that is keyed text or played audio. This class is that
    /// decision and nothing else, so the six combinations — text only, audio
    /// only, both, each in CW and in a voice mode — can be asserted without a
    /// radio, a window or a key table.
    /// </para>
    /// <para>
    /// <b>CW is the radio's own word for it.</b> The transmit gate in
    /// <c>FlexBase.remoteAudioProc</c> runs the PC transmit-audio stream only
    /// when the transmit slice's mode is not <c>CW</c>, and
    /// <see cref="FlexBase.TxTonePathTrouble"/> refuses the file path in any
    /// mode beginning with CW. So the branch asks the same question those
    /// two ask, and a slot never ends up with a payload the engine cannot
    /// carry: text goes to the keyer only in CW, a recording goes to the
    /// transmit stream only outside it.
    /// </para>
    /// <para>
    /// <b>A slot with nothing for the current mode says so.</b> Before this
    /// branch existed a message key in a voice mode queued CW text to a keyer
    /// that was not keying, and nothing was said. A key that does nothing is
    /// indistinguishable from a broken one to an operator who cannot see the
    /// radio, so both empty cases are named outcomes with sentences behind
    /// them rather than silence.
    /// </para>
    /// </remarks>
    public static class MessageKeyDispatch
    {
        /// <summary>
        /// True when <paramref name="mode"/> is a CW mode. Case-insensitive
        /// and prefix-based, matching <see cref="FlexBase.TxTonePathTrouble"/>.
        /// An empty or unknown mode is NOT CW: with no slice to ask, the
        /// voice path is taken and its own path check says what is missing.
        /// </summary>
        public static bool IsCwMode(string? mode) =>
            !string.IsNullOrEmpty(mode)
            && mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase);

        /// <summary>Decide what the key does for this slot in this mode.</summary>
        public static MessageKeyAction Plan(CWMessageItem item, string? mode)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (IsCwMode(mode))
                return item.HasText ? MessageKeyAction.SendCw : MessageKeyAction.NothingForCw;
            return item.HasAudio ? MessageKeyAction.SendVoice : MessageKeyAction.NothingForVoice;
        }
    }
}
