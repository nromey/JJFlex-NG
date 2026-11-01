#nullable enable
using System;

namespace Radios
{
    /// <summary>
    /// Whether a chord that can key the radio is a new press or more of the one
    /// already down (Ctrl+Enter in Check your audio, #859). A check starts only
    /// on a new press, so a held key cannot turn a press the field refused or
    /// changed into a transmission at a number nobody accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The repeat flag is not enough, because a held chord does not arrive the
    /// same way everywhere.</b> Measured 2026-10-22 under JAWS, held Shift+Space
    /// and Ctrl+Space came as one key-down, repeat key-downs and one key-up, the
    /// same as NVDA. Measured 2026-08-24 under JAWS, held Up came as down/up
    /// PAIRS, each one looking like a fresh press, the first at the keyboard's
    /// repeat delay. The pairs are probably for keys JAWS has a script for.
    /// <b>Nobody has measured Ctrl+Enter</b>, and Enter is the kind of key a
    /// reader scripts, so both shapes are covered here.
    /// </para>
    /// <para>
    /// So a press counts as the same press when it is flagged a repeat, or when
    /// it comes inside <see cref="SameChordWindowMs"/> of the last one with
    /// Control never released. After a refusal the window holds even if Control
    /// was seen going up, in case a reader's pairs carry the modifier too. The
    /// operator has just been told something, so nothing real is lost.
    /// </para>
    /// <para>
    /// The window is a bound, not a measurement: Windows' slowest keyboard delay
    /// is one second, so a first pair cannot come later than this.
    /// </para>
    /// </remarks>
    public sealed class FreshPressGate
    {
        /// <summary>Above Windows' slowest keyboard repeat delay, 1000 ms.</summary>
        public const int SameChordWindowMs = 1100;

        private long _lastMs = long.MinValue / 2;
        private bool _controlReleased = true;
        private bool _refused;

        /// <summary>The modifier went up.</summary>
        public void NoteControlUp() => _controlReleased = true;

        /// <summary>
        /// Should this key-down go on? A held press never does, whatever the
        /// press would have done: it neither starts nor stops anything. A new
        /// press goes on if <paramref name="settle"/> agrees: it finishes
        /// anything pending in the field, and false means the operator has been
        /// told something and this press ends here.
        /// </summary>
        /// <param name="nowMs">A monotonic clock.</param>
        /// <param name="isRepeat">The key event's repeat flag.</param>
        /// <param name="settle">Finishes a typed entry. True when the number stands as typed, or there was none.</param>
        public bool GoesOn(long nowMs, bool isRepeat, Func<bool> settle)
        {
            bool inWindow = nowMs - _lastMs < SameChordWindowMs;
            bool samePress = isRepeat || (inWindow && (!_controlReleased || _refused));
            _lastMs = nowMs;
            _controlReleased = false;
            if (samePress) return false;
            _refused = !settle();
            return !_refused;
        }
    }
}
