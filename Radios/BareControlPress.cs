namespace Radios
{
    /// <summary>
    /// Recognises a BARE Control: pressed and released with no other key in
    /// between (#835). What skips Check your audio's countdown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ruled by Noel 2026-10-29 01:05</b>, that Control during the count
    /// skips the rest of it and transmits at once, and with it that the
    /// trigger is a bare Control: Ctrl+Enter, which CANCELS the count, and
    /// Ctrl+Space both begin with Control down, so a skip on Control-down, or
    /// on any Control release, would turn a cancel into a transmission.
    /// </para>
    /// <para>
    /// So it fires on the RELEASE of Control, only when the press was seen
    /// and no other key went down while it was held. A Control repeating
    /// while held is still bare. A release whose press was never seen (it went
    /// down in another window) does not fire. Left and Right Control are both
    /// Control.
    /// </para>
    /// <para>
    /// Pure, so a test can hold every chord without a window. The window feeds
    /// it every key it sees, before handling any of them.
    /// </para>
    /// </remarks>
    public sealed class BareControlPress
    {
        private bool _down;
        private bool _spoiled;

        /// <summary>A key went down.</summary>
        /// <param name="isControl">It is Left or Right Control.</param>
        /// <param name="isRepeat">It is a held key repeating.</param>
        /// <param name="otherModifierHeld">Shift, Alt or Windows is already held
        /// as this key goes down, so a Control pressed now joins a chord.</param>
        public void KeyDown(bool isControl, bool isRepeat, bool otherModifierHeld = false)
        {
            if (isControl)
            {
                // A fresh press starts clean unless it joins a modifier already
                // held; a repeat, or the other Control key joining, leaves the
                // press as it was.
                if (!_down && !isRepeat) { _down = true; _spoiled = otherModifierHeld; }
                else if (otherModifierHeld) _spoiled = true;
                return;
            }
            // Any other key makes it a chord, whether Control is down yet or
            // not: Shift then Control is Shift+Control, never a bare Control.
            _spoiled = true;
        }

        /// <summary>A key came up. True exactly when it ends a bare Control press.</summary>
        /// <param name="isControl">It is Left or Right Control.</param>
        public bool KeyUp(bool isControl)
        {
            if (!isControl) return false;
            bool bare = _down && !_spoiled;
            _down = false;
            _spoiled = false;
            return bare;
        }

        /// <summary>Forget any press in progress: the window lost focus, so its release may never be seen.</summary>
        public void Reset()
        {
            _down = false;
            _spoiled = false;
        }
    }
}
