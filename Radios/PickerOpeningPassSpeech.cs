using System;

namespace Radios
{
    /// <summary>
    /// The radio picker's opening-pass progress lines ("Local connection list
    /// loaded, still listening" and its nothing-found sibling), and whether
    /// they are still worth saying (#775).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both orderings.</b> Measured 2026-10-19: the loaded line landed
    /// 300 ms after the main window came up, over a connect to a radio already
    /// found and chosen. The first fix stopped the pass from SAYING a line once
    /// a connect had started, which covers a connect before the settle window
    /// ends. It did not cover the other order (Codex's check of #775, the
    /// blocker): the line queued first, behind the arrival speech, and then the
    /// connect's own interrupting "Trying ..." sentence made the speech arbiter
    /// rescue the unheard line and say it after the connect, which is the very
    /// thing #775 is about. A startup auto-connect gives that order with no
    /// fast keypress at all: discovery settles in about 200 ms, the auto-connect
    /// timer ticks at one second.
    /// </para>
    /// <para>
    /// So every line goes out with a condition the arbiter asks before any
    /// replay of an unheard copy (<see cref="StillWorthSaying"/>), and a connect
    /// starting turns it false. The arbiter's general rescue policy is not
    /// touched: this is the producer saying its own sentence has stopped being
    /// true, the same mechanism Home's return status uses.
    /// </para>
    /// <para>
    /// An idle picker, no connect, still says its loaded state: that is the
    /// line's whole job.
    /// </para>
    /// </remarks>
    public sealed class PickerOpeningPassSpeech
    {
        private volatile bool _connectStarted;
        private readonly Action<string, Func<bool?>> _speak;

        /// <param name="speak">
        /// Where a line goes, with its condition. Null is the real one:
        /// <c>ScreenReaderOutput.Speak</c> at Terse, queued, never
        /// interrupting, so it waits behind the arrival speech already going
        /// out.
        /// </param>
        public PickerOpeningPassSpeech(Action<string, Func<bool?>> speak = null)
        {
            _speak = speak ?? ((text, stillValid) =>
                ScreenReaderOutput.Speak(text, VerbosityLevel.Terse, false, stillValid: stillValid));
        }

        /// <summary>A connect has started from the picker. From now on no line is said, and none already queued is replayed.</summary>
        public void ConnectStarting() => _connectStarted = true;

        /// <summary>True once a connect has started.</summary>
        public bool ConnectStarted => _connectStarted;

        /// <summary>
        /// The condition handed with every line, asked by the arbiter before a
        /// replay: true while no connect has started.
        /// </summary>
        public bool? StillWorthSaying() => !_connectStarted;

        /// <summary>
        /// Say an opening-pass line, queued. Nothing at all once a connect has
        /// started. True when it was handed on.
        /// </summary>
        public bool Say(string text)
        {
            if (_connectStarted || string.IsNullOrEmpty(text)) return false;
            _speak(text, StillWorthSaying);
            return true;
        }
    }
}
