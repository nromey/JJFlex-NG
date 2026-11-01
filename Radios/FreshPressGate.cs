#nullable enable
using System;

namespace Radios
{
    /// <summary>What a Ctrl+Enter key-down does.</summary>
    public enum PressAct
    {
        /// <summary>Nothing: it is more of a press already answered, or the field has said why not.</summary>
        Nothing,

        /// <summary>Something is live: stop or cancel it, now.</summary>
        Stop,

        /// <summary>Start a check.</summary>
        Start,
    }

    /// <summary>
    /// What a chord that can key the radio does (Ctrl+Enter in Check your audio,
    /// #859). <b>This gate decides starts only.</b> A key-down that is not a
    /// repeat, while something is live, always stops or cancels it, at once and
    /// whatever the time: the Control key held between two taps is an ordinary
    /// habit, and a stop that was swallowed leaves a radio keyed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The direction is deliberate.</b> A start is refused when in doubt; a
    /// stop never is. So under a reader that delivers a held key as down/up
    /// pairs, a held Ctrl+Enter starts a check and the first pair stops it.
    /// That is the safe direction, and the next pair starts nothing. Nobody has
    /// measured how this chord arrives.
    /// </para>
    /// <para>
    /// <b>The repeat flag is not enough for a start, because a held chord does
    /// not arrive the same way everywhere.</b> Measured 2026-10-22 under JAWS,
    /// held Shift+Space and Ctrl+Space came as one key-down, repeat key-downs
    /// and one key-up, the same as NVDA. Measured 2026-08-24 under JAWS, held Up
    /// came as down/up PAIRS, each one looking like a fresh press, the first at
    /// the keyboard's repeat delay. The pairs are probably for keys JAWS has a
    /// script for, and Enter is such a key.
    /// </para>
    /// <para>
    /// So a start needs a new press: not flagged a repeat, and not inside
    /// <see cref="SameChordWindowMs"/> of the last one with Control never
    /// released. After a refusal the window holds even if Control was seen going
    /// up, in case a reader's pairs carry the modifier too. The operator has
    /// just been told something, so nothing real is lost. The window is a bound,
    /// not a measurement: Windows' slowest keyboard delay is one second.
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

        /// <summary>What this key-down does.</summary>
        /// <param name="nowMs">A monotonic clock.</param>
        /// <param name="isRepeat">The key event's repeat flag. A repeat never does anything, stop or start.</param>
        /// <param name="live">A check is active, a start is pending, or a playback of this window is running.</param>
        /// <param name="settle">Finishes a typed entry. True when the number stands as typed, or there was none.</param>
        public PressAct Decide(long nowMs, bool isRepeat, bool live, Func<bool> settle)
        {
            bool inWindow = nowMs - _lastMs < SameChordWindowMs;
            bool samePress = isRepeat || (inWindow && (!_controlReleased || _refused));
            _lastMs = nowMs;
            _controlReleased = false;
            if (isRepeat) return PressAct.Nothing;
            if (live)
            {
                // Whatever the field says, the stop goes.
                settle();
                _refused = false;
                return PressAct.Stop;
            }
            if (samePress) return PressAct.Nothing;
            _refused = !settle();
            return _refused ? PressAct.Nothing : PressAct.Start;
        }
    }
}
