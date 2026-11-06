#nullable enable
using System;
using System.Diagnostics;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// One press of Delete, one removal (#774): the edge filter the WPF lists
    /// (<c>JJFlexWpf.DeleteKeyRemoves</c>) and the Jim-era WinForms lists
    /// (<see cref="WinFormsDeleteKey"/>) share, so a held key cannot remove item
    /// after item under either screen reader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both ways a held key arrives.</b> A reader that passes the hold
    /// through (NVDA) sends the first down, then Windows' repeat downs every
    /// ~30 ms from the repeat delay on, then one up. JAWS, on a key it scripts,
    /// sends down/up PAIRS about 250 ms apart, the first at the repeat delay,
    /// each one a "new" down with no repeat flag (measured 2026-08-24; see
    /// <see cref="PttHoldFilter"/>). Neither may press twice.
    /// </para>
    /// <para>
    /// <b>The rule.</b> A down that is not a repeat, with the key not already
    /// held, is a press. After a press the key counts as held until an up has
    /// been seen and then nothing for longer than <see cref="ReleaseGapMs"/>,
    /// or until no down at all has arrived for that long (an up that went to
    /// another window, say a confirmation, is not waited for forever). A
    /// release and re-press faster than the gap is swallowed and the operator
    /// presses again: the safe side of the ambiguity, for a key that removes.
    /// While <see cref="Busy"/>, the removal a press started is still
    /// landing, and no down presses.
    /// </para>
    /// <para>
    /// <b>Repair round 2 (Codex's re-check).</b> The gap covers the repeat
    /// PERIOD as well as the delay (<see cref="GapForKeyboard"/>): the slowest
    /// repeat speed repeats every 400 ms, and a WinForms list sees no repeat
    /// flag, so a gap sized from a 250 ms delay alone let a held key remove on
    /// four downs of five. A down with no up since the last down, inside
    /// <see cref="HeldWithoutUpMs"/>, is the same hold whatever its flag says.
    /// Another key on the list after Delete came up (<see cref="OtherInput"/>)
    /// is a new intent, so a deliberate press on the next item is not
    /// swallowed; Delete's own pairs never count as another key, so a JAWS
    /// hold stays one hold. The list losing focus while a hold is live
    /// (<see cref="Interrupted"/>, a question opening) and getting it back
    /// (<see cref="Resumed"/>) restarts the silence from the return, so the
    /// key still held when the question closes cannot act again.
    /// </para>
    /// <para>
    /// <b>Repair round 3 (Codex's second re-check, ruled by Noel
    /// 2026-11-04).</b> A CLICK is a new intent only once Delete has really
    /// been released (<see cref="Pointer"/>): an up inside a JAWS hold is
    /// synthetic, and a click, unlike another key, does not stop the held
    /// key's stream, so a click there left the next pair free to remove a
    /// second item. A Delete with a modifier, or typed into a text box,
    /// continues a hold and never ends one (<see cref="DeleteThatDoesNotPress"/>).
    /// </para>
    /// <para>
    /// <b>Repair round 4 (Codex's third re-check).</b> A MODIFIER on its own
    /// (Shift, Ctrl, Alt, the Windows key) is not a new intent: pressing one
    /// does not stop a held Delete's stream, so it never reaches
    /// <see cref="OtherInput"/>. Both adapters read the key's real identity
    /// first (WPF reports Alt+Delete as <c>Key.System</c> with the real key in
    /// <c>SystemKey</c>), so a modified Delete continues a hold on its down
    /// and counts as Delete's up on its up.
    /// </para>
    /// </remarks>
    public sealed class DeleteKeyGate
    {
        private bool _holding;
        private long _lastDownMs;
        private long _lastUpMs = long.MinValue;

        public DeleteKeyGate(int releaseGapMs)
        {
            ReleaseGapMs = releaseGapMs > 0 ? releaseGapMs : PttHoldFilter.DefaultDeferMs;
        }

        /// <summary>How long a held key can go quiet inside one hold.</summary>
        public int ReleaseGapMs { get; }

        /// <summary>True from a press until its removal has landed.</summary>
        public bool Busy { get; set; }

        /// <summary>
        /// About 250 ms between JAWS's synthesised pairs of a held key
        /// (measured 242-272 ms, 2026-08-24).
        /// </summary>
        public const int JawsPairSpacingMs = 250;

        /// <summary>
        /// The gap for this keyboard (repair round 2): one and a half times
        /// the LONGEST quiet a held key leaves, whichever of the repeat delay,
        /// the repeat period and JAWS's pair spacing that is, within
        /// <see cref="PttHoldFilter"/>'s bounds. Every supported setting fits:
        /// the longest is a 1000 ms delay, giving 1500.
        /// </summary>
        public static int GapForKeyboard(int repeatDelayMs, int repeatPeriodMs)
        {
            int longest = Math.Max(Math.Max(repeatDelayMs, repeatPeriodMs), JawsPairSpacingMs);
            return Math.Min(Math.Max(longest * 3 / 2, PttHoldFilter.DefaultDeferMs), PttHoldFilter.MaxFirstGapDeferMs);
        }

        /// <summary>
        /// A down with no up seen since the previous down, this soon after it,
        /// is the same hold whatever its repeat flag says: a key cannot go down
        /// twice without coming up unless it is repeating. Bounded, so an up
        /// lost to another window cannot hold the key forever.
        /// </summary>
        public const int HeldWithoutUpMs = PttHoldFilter.MaxFirstGapDeferMs;

        /// <summary>
        /// Delete went down. True when this is a press to act on; false for a
        /// held key (repeat, or a synthesised pair) or while a removal lands.
        /// Every Delete down is reported here, acted on or not.
        /// </summary>
        public bool Down(bool isRepeat, long nowMs)
        {
            if (_holding)
            {
                bool letGo = !StillHeld(nowMs);
                if (!letGo)
                {
                    _lastDownMs = nowMs;
                    _lastUpMs = long.MinValue;   // still held: wait for its next up
                    return false;
                }
                _holding = false;
            }
            _holding = true;
            _lastDownMs = nowMs;
            _lastUpMs = long.MinValue;
            // A repeat with no press seen (focus arrived on a held key) starts
            // a hold without pressing.
            if (isRepeat) return false;
            return !Busy;
        }

        /// <summary>Delete came up.</summary>
        public void Up(long nowMs)
        {
            if (_holding) _lastUpMs = nowMs;
        }

        /// <summary>
        /// Whether the last press's hold is still live at <paramref name="nowMs"/>:
        /// no up since the last down and that down recent
        /// (<see cref="HeldWithoutUpMs"/>), or quiet for no longer than the gap.
        /// </summary>
        private bool StillHeld(long nowMs)
        {
            if (!_holding) return false;
            if (_lastUpMs == long.MinValue && nowMs - _lastDownMs <= Math.Max(ReleaseGapMs, HeldWithoutUpMs))
                return true;
            return !((_lastUpMs != long.MinValue && nowMs - _lastUpMs > ReleaseGapMs)
                     || nowMs - _lastDownMs > ReleaseGapMs);
        }

        /// <summary>
        /// Another key, or a click, on the list. After Delete has come up that
        /// is a new intent (the operator moved to another item), so the hold
        /// is over and the next Delete is a press. While Delete is still down
        /// (no up seen) it changes nothing.
        /// </summary>
        public void OtherInput(long nowMs)
        {
            if (_holding && _lastUpMs != long.MinValue) _holding = false;
        }

        /// <summary>
        /// A mouse press on the list (repair round 3, ruled by Noel 2026-11-04:
        /// a click counts as a new intent only once Delete has really been
        /// released). Unlike another KEY, a click does not stop a held key's
        /// stream: JAWS goes on synthesising a down/up pair from the held
        /// Delete, and an up seen in that stream is not a release. So a click
        /// ends the hold only when the hold has already ended by silence
        /// (<see cref="StillHeld"/> false); otherwise the next pair is the
        /// same hold.
        /// </summary>
        public void Pointer(long nowMs)
        {
            if (_holding && !StillHeld(nowMs)) _holding = false;
        }

        /// <summary>
        /// A Delete down that does not qualify to press (a modifier held, or
        /// typed into a text box in the list). It is still the Delete key, so
        /// it never ends a hold the way another key does; it continues a live
        /// hold, or starts one without pressing (repair round 3).
        /// </summary>
        public void DeleteThatDoesNotPress(long nowMs) => Down(isRepeat: true, nowMs);

        /// <summary>
        /// The list lost keyboard focus (a question opened, or the operator
        /// moved away). A hold live now stays held, with its up still to come:
        /// the up may go to the question.
        /// </summary>
        public void Interrupted(long nowMs)
        {
            if (!StillHeld(nowMs)) { _holding = false; return; }
            _lastDownMs = nowMs;
            _lastUpMs = long.MinValue;
        }

        /// <summary>
        /// The list has keyboard focus again. A hold still live counts its
        /// silence from now, so the key still held as a question closes keys
        /// nothing; a fresh press after the gap presses.
        /// </summary>
        public void Resumed(long nowMs)
        {
            if (!_holding) return;
            _lastDownMs = Math.Max(_lastDownMs, nowMs);
            _lastUpMs = nowMs;
        }

        /// <summary>
        /// Where selection goes after a press, or -1 for "leave it": nothing
        /// was removed, or the list is now empty. Next item, else the
        /// previous one. Shared by the WPF and WinForms lists.
        /// </summary>
        public static int LandingIndex(int removedAt, int countBefore, int countAfter)
        {
            if (countAfter >= countBefore || countAfter <= 0) return -1;
            return Math.Min(Math.Max(removedAt, 0), countAfter - 1);
        }
    }

    /// <summary>
    /// Delete on a Jim-era WinForms list presses that list's own remove button,
    /// the same way <c>JJFlexWpf.DeleteKeyRemoves</c> does on a WPF list
    /// (#774). The operator and memory-group lists the product actually opens
    /// are these forms (<c>Lister</c>, <c>ManageGroups</c>), not the WPF
    /// replacements, which nothing constructs.
    /// </summary>
    /// <remarks>
    /// Delete is a second way to press the SAME button: <see cref="System.Windows.Forms.Button.PerformClick"/>
    /// runs its own Click, so any question it asks is still asked, and a
    /// disabled or hidden button is not pressed. Bare Delete only. One press,
    /// one removal (<see cref="DeleteKeyGate"/>). Focus lands on the next item,
    /// or the previous when the last went, and stays on the list.
    /// </remarks>
    public static class WinFormsDeleteKey
    {
        public static void Wire(System.Windows.Forms.ListBox list, System.Windows.Forms.Button button, string name)
        {
            if (list == null) throw new ArgumentNullException(nameof(list));
            if (button == null) throw new ArgumentNullException(nameof(button));
            // The repeat period as well as the delay (repair round 2): WinForms
            // marks no repeats, and the slowest repeat speed is slower than the
            // shortest delay.
            var gate = new DeleteKeyGate(DeleteKeyGate.GapForKeyboard(
                KeyRepeatTiming.DelayMsFromSetting(System.Windows.Forms.SystemInformation.KeyboardDelay),
                KeyRepeatTiming.RepeatPeriodMsFromSetting(System.Windows.Forms.SystemInformation.KeyboardSpeed)));
            list.KeyDown += (_, e) =>
            {
                bool bareDelete = e.KeyCode == System.Windows.Forms.Keys.Delete && e.Modifiers == System.Windows.Forms.Keys.None;
                bool press = DecideKey(gate, e.KeyCode, e.Modifiers, Environment.TickCount64);
                if (!bareDelete) return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (!press || list.SelectedIndex < 0 || !button.Enabled || !button.Visible) return;
                int at = list.SelectedIndex;
                int before = list.Items.Count;
                Tracing.TraceLine("WinFormsDeleteKey: Delete on " + name + " presses " + button.Name + " for item " + at,
                    TraceLevel.Info);
                gate.Busy = true;
                try
                {
                    button.PerformClick();
                    int landing = DeleteKeyGate.LandingIndex(at, before, list.Items.Count);
                    if (landing >= 0 && list.SelectedIndex < 0) list.SelectedIndex = landing;
                    if (list.CanFocus) list.Focus();
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine("WinFormsDeleteKey: the button threw: " + ex.Message, TraceLevel.Error);
                }
                finally
                {
                    gate.Busy = false;
                }
            };
            list.KeyUp += (_, e) =>
            {
                if (e.KeyCode == System.Windows.Forms.Keys.Delete) gate.Up(Environment.TickCount64);
            };
            // A click is a new intent only once Delete is really up (repair
            // round 3); focus leaving (a question opening) and coming back is a
            // hold's interruption (repair round 2).
            list.MouseDown += (_, _) => Clicked(gate, Environment.TickCount64);
            list.LostFocus += (_, _) => gate.Interrupted(Environment.TickCount64);
            list.GotFocus += (_, _) => gate.Resumed(Environment.TickCount64);
        }

        /// <summary>
        /// A mouse press on a WinForms list, decided: a new intent only once
        /// Delete has really been released (<see cref="DeleteKeyGate.Pointer"/>).
        /// </summary>
        internal static void Clicked(DeleteKeyGate gate, long nowMs) => gate.Pointer(nowMs);

        /// <summary>
        /// A modifier key on its own (repair round 4): Shift, Ctrl, Alt or the
        /// Windows key, either side, and the keys a screen reader takes for its
        /// own (Insert, Caps Lock) or that only toggle a lock. Pressing one does
        /// not stop a held key's stream and moves to no other item, so it is
        /// not a new intent.
        /// </summary>
        internal static bool IsModifierOnly(System.Windows.Forms.Keys keyCode)
        {
            switch (keyCode & System.Windows.Forms.Keys.KeyCode)
            {
                case System.Windows.Forms.Keys.ShiftKey:
                case System.Windows.Forms.Keys.LShiftKey:
                case System.Windows.Forms.Keys.RShiftKey:
                case System.Windows.Forms.Keys.ControlKey:
                case System.Windows.Forms.Keys.LControlKey:
                case System.Windows.Forms.Keys.RControlKey:
                case System.Windows.Forms.Keys.Menu:
                case System.Windows.Forms.Keys.LMenu:
                case System.Windows.Forms.Keys.RMenu:
                case System.Windows.Forms.Keys.LWin:
                case System.Windows.Forms.Keys.RWin:
                // The screen readers' own keys and the lock keys: not a move
                // to another item either.
                case System.Windows.Forms.Keys.Insert:
                case System.Windows.Forms.Keys.CapsLock:
                case System.Windows.Forms.Keys.NumLock:
                case System.Windows.Forms.Keys.Scroll:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// One key-down on a WinForms list, by its real key and modifiers, the
        /// way the list's own handler decides it: true when it is a press of
        /// Delete to act on. A modifier alone changes nothing (repair round 4);
        /// a Delete with a modifier continues a hold and never ends one (repair
        /// round 3); any other key is another input (<see cref="Decide"/>).
        /// </summary>
        internal static bool DecideKey(DeleteKeyGate gate, System.Windows.Forms.Keys keyCode,
            System.Windows.Forms.Keys modifiers, long nowMs)
        {
            if (IsModifierOnly(keyCode)) return false;
            bool isDelete = (keyCode & System.Windows.Forms.Keys.KeyCode) == System.Windows.Forms.Keys.Delete;
            if (isDelete && modifiers != System.Windows.Forms.Keys.None)
            {
                gate.DeleteThatDoesNotPress(nowMs);
                return false;
            }
            return Decide(gate, isDelete, nowMs);
        }

        /// <summary>
        /// One key-down on a WinForms list, decided: true when it is a press of
        /// Delete to act on. Any other key is another input to the gate (a new
        /// intent once Delete has come up). WinForms reports no repeat flag;
        /// the gate tracks a down with no up since the last one instead.
        /// Modifier keys never reach here (<see cref="DecideKey"/>).
        /// </summary>
        internal static bool Decide(DeleteKeyGate gate, bool bareDelete, long nowMs)
        {
            if (!bareDelete)
            {
                gate.OtherInput(nowMs);
                return false;
            }
            return gate.Down(isRepeat: false, nowMs);
        }
    }
}
