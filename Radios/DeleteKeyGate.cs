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
        /// The gap for this machine: one and a half times its own repeat delay,
        /// within <see cref="PttHoldFilter"/>'s bounds, exactly as that filter
        /// sizes its first gap, so the longest pause a synthesising reader
        /// leaves inside a hold is covered.
        /// </summary>
        public static int GapForRepeatDelay(int repeatDelayMs) =>
            Math.Min(Math.Max(repeatDelayMs * 3 / 2, PttHoldFilter.DefaultDeferMs), PttHoldFilter.MaxFirstGapDeferMs);

        /// <summary>
        /// Delete went down. True when this is a press to act on; false for a
        /// held key (repeat, or a synthesised pair) or while a removal lands.
        /// Every Delete down is reported here, acted on or not.
        /// </summary>
        public bool Down(bool isRepeat, long nowMs)
        {
            if (_holding)
            {
                bool letGo = (_lastUpMs != long.MinValue && nowMs - _lastUpMs > ReleaseGapMs)
                             || nowMs - _lastDownMs > ReleaseGapMs;
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
            var gate = new DeleteKeyGate(DeleteKeyGate.GapForRepeatDelay(
                KeyRepeatTiming.DelayMsFromSetting(System.Windows.Forms.SystemInformation.KeyboardDelay)));
            list.KeyDown += (_, e) =>
            {
                if (e.KeyCode != System.Windows.Forms.Keys.Delete || e.Modifiers != System.Windows.Forms.Keys.None) return;
                // WinForms does not mark repeats; the gate's timing covers them.
                bool press = gate.Down(isRepeat: false, Environment.TickCount64);
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
        }
    }
}
