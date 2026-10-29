using System;
using System.Collections.Generic;

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
    /// <b>Two kinds of "another key" it must not miss (Codex round 1).</b> A
    /// key already held when Control goes down (F1): every key seen going down
    /// and not yet seen coming up is remembered, so that chord is never bare.
    /// And a key this app's own low-level hook SWALLOWED, which the window
    /// never sees at all (B1): the system-wide JJ key passes modifiers and eats
    /// the letter, so Ctrl+Shift+J and then Ctrl+K reaches the window as
    /// Control alone. The hooks count what they swallow
    /// (<see cref="WithheldKeyCounter"/>); a press during which that count
    /// moved is a chord.
    /// </para>
    /// <para>
    /// A remembered key whose release is never seen would block the skip, not
    /// fire it: the safe direction. Losing focus (<see cref="Reset"/>) forgets
    /// them all.
    /// </para>
    /// <para>
    /// Pure, so a test can hold every chord without a window. The window feeds
    /// it every key it sees, before handling any of them.
    /// </para>
    /// </remarks>
    public sealed class BareControlPress
    {
        private readonly Func<long>? _withheldDowns;
        private readonly HashSet<int> _held = new();
        private bool _down;
        private bool _spoiled;
        private long _withheldAtDown;

        /// <param name="withheldDowns">How many key presses this app's hooks
        /// have swallowed so far (<see cref="WithheldKeyCounter.Downs"/>); null
        /// when nothing can swallow a key.</param>
        public BareControlPress(Func<long>? withheldDowns = null) => _withheldDowns = withheldDowns;

        /// <summary>A key went down.</summary>
        /// <param name="key">The key's identity, the same number on its release.</param>
        /// <param name="isControl">It is Left or Right Control.</param>
        /// <param name="isRepeat">It is a held key repeating.</param>
        /// <param name="otherModifierHeld">Shift, Alt or Windows is already held
        /// as this key goes down, so a Control pressed now joins a chord.</param>
        public void KeyDown(int key, bool isControl, bool isRepeat, bool otherModifierHeld = false)
        {
            if (isControl)
            {
                // A fresh press starts clean unless it joins a key or modifier
                // already held; a repeat, or the other Control key joining,
                // leaves the press as it was.
                if (!_down && !isRepeat)
                {
                    _down = true;
                    _spoiled = otherModifierHeld || _held.Count > 0 || !TryWithheld(out _withheldAtDown);
                }
                else if (otherModifierHeld) _spoiled = true;
                return;
            }
            // Any other key makes it a chord, whether Control is down yet or
            // not: Shift then Control is Shift+Control, never a bare Control.
            _held.Add(key);
            _spoiled = true;
        }

        /// <summary>A key came up. True exactly when it ends a bare Control press.</summary>
        /// <param name="key">The key's identity, as given to <see cref="KeyDown"/>.</param>
        /// <param name="isControl">It is Left or Right Control.</param>
        public bool KeyUp(int key, bool isControl)
        {
            if (!isControl)
            {
                _held.Remove(key);
                return false;
            }
            // A key the hooks swallowed while Control was held made it a chord.
            bool bare = _down && !_spoiled && TryWithheld(out long now) && now == _withheldAtDown;
            _down = false;
            _spoiled = false;
            return bare;
        }

        /// <summary>Forget any press in progress, and every held key: the window lost focus, so their releases may never be seen.</summary>
        public void Reset()
        {
            _down = false;
            _spoiled = false;
            _held.Clear();
        }

        /// <summary>The hooks' count. A count that cannot be read makes the press a chord, never bare.</summary>
        private bool TryWithheld(out long downs)
        {
            try { downs = _withheldDowns?.Invoke() ?? 0; return true; }
            catch { downs = 0; return false; }
        }
    }
}
