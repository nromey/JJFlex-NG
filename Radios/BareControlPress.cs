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
    /// and no other key went down while it was held, or was already held when
    /// it went down (Codex round 1, F1). A Control repeating while held is
    /// still bare. A release whose press was never seen does not fire. Left
    /// and Right Control are both Control. A remembered key whose release is
    /// never seen blocks the skip rather than firing it; <see cref="Reset"/>
    /// forgets everything.
    /// </para>
    /// <para>
    /// <b>It must be fed EVERY key edge, in order.</b> That is why, while
    /// this app's system-wide hook is installed, it is fed on the hook thread
    /// by <see cref="BareControlGestures"/>, which sees the keys the hook
    /// swallows as well as the ones it passes (Codex rounds 1 and 2, B1).
    /// A window sees neither the swallowed keys nor, across threads, the order
    /// they came in, so the window feeds one of these only when no hook is
    /// installed and nothing is swallowed.
    /// </para>
    /// </remarks>
    public sealed class BareControlPress
    {
        private readonly HashSet<int> _held = new();
        private bool _down;
        private bool _spoiled;

        /// <summary>True from a fresh Control press until its release.</summary>
        public bool ControlDown => _down;

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
                    _spoiled = otherModifierHeld || _held.Count > 0;
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
            bool bare = _down && !_spoiled;
            _down = false;
            _spoiled = false;
            return bare;
        }

        /// <summary>Forget any press in progress, and every held key: their releases may never be seen.</summary>
        public void Reset()
        {
            _down = false;
            _spoiled = false;
            _held.Clear();
        }
    }

    /// <summary>One bare Control press, as the system-wide hook saw it.</summary>
    /// <param name="Gesture">A number that rises with every bare press, so one press is acted on once.</param>
    /// <param name="DownTick">When Control went down, on <c>Environment.TickCount64</c>.</param>
    /// <param name="UpTick">When it came up, on the same clock.</param>
    /// <param name="DownWindow">The foreground window when Control went down, as a handle; 0 when unknown.</param>
    /// <param name="UpWindow">The foreground window when it came up.</param>
    public readonly record struct BareControlRelease(long Gesture, long DownTick, long UpTick,
        long DownWindow = 0, long UpWindow = 0);

    /// <summary>
    /// Bare Control decided where every key edge is seen in order: inside this
    /// app's system-wide low-level hook, on its thread, for the keys it passes
    /// AND the keys it swallows (#835, Codex round 2, B1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why here and not in the window.</b> The hook swallows a JJ chord's
    /// letter and passes its Control, so a window sees Ctrl+Shift+J and then
    /// Ctrl+K as Control alone. Round 1 counted swallowed keys and compared
    /// the count at the window's Control press and release; Codex showed that
    /// fails, because the hook runs on its own thread and can count the
    /// swallowed K before the window has even handled the Control press, so
    /// both reads see the same count. Only the hook sees the edges in the
    /// order they happened, so the decision is made there and handed to the
    /// window as a verdict.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> A Control gesture this does not see as bare never
    /// skips: if the hook has faulted, or is not running, there is no verdict,
    /// and the window's own recogniser is used only when no hook is installed
    /// at all (and so nothing is being swallowed).
    /// </para>
    /// </remarks>
    public sealed class BareControlGestures
    {
        private const uint VkControl = 0x11, VkLControl = 0xA2, VkRControl = 0xA3;

        private readonly BareControlPress _press = new();
        private long _downTick;
        private long _downWindow;
        private long _gesture;

        /// <summary>Whether a virtual key is a Control key.</summary>
        public static bool IsControlVirtualKey(uint vk) => vk == VkControl || vk == VkLControl || vk == VkRControl;

        /// <summary>
        /// One key edge, in the order the hook saw it, passed or swallowed.
        /// Returns the verdict when this edge ends a bare Control press.
        /// </summary>
        /// <param name="vk">The virtual key.</param>
        /// <param name="isDown">A press, or a release.</param>
        /// <param name="tick">When, on <c>Environment.TickCount64</c>.</param>
        /// <param name="foreground">The foreground window at this edge, so the
        /// verdict says where Control went down and where it came up (Codex
        /// round 3, B2).</param>
        public BareControlRelease? Observe(uint vk, bool isDown, long tick, long foreground = 0)
        {
            bool control = IsControlVirtualKey(vk);
            if (isDown)
            {
                if (control && !_press.ControlDown) { _downTick = tick; _downWindow = foreground; }
                // A low-level hook reports a held key's repeats as further
                // downs; the recogniser already treats a second Control down
                // as the same press and a repeated other key as still held.
                _press.KeyDown((int)vk, control, isRepeat: false);
                return null;
            }
            if (!_press.KeyUp((int)vk, control)) return null;
            return new BareControlRelease(++_gesture, _downTick, tick, _downWindow, foreground);
        }

        /// <summary>Forget every edge in flight.</summary>
        public void Reset() => _press.Reset();
    }

    /// <summary>
    /// Check your audio's side of the skip (#835): whether a bare Control the
    /// hook reported skips THIS count, and whether the window's own
    /// recogniser may be trusted. Pure, so the pairing is held by tests.
    /// </summary>
    public sealed class CountdownSkipGate
    {
        private long _lastGesture;

        /// <summary>
        /// A verdict from the system-wide hook. True, once per gesture, when
        /// the WHOLE gesture belongs to this window and this count (Codex round
        /// 3, B2): the hook is installed; this window is active now, and has
        /// been without a break since before Control went down (an activation
        /// is a fresh start, so a press made elsewhere, or before this window
        /// left and came back, is not ours); the hook saw this window in front
        /// at the press AND at the release; and a count is running that began
        /// before the press. Every "before" is STRICT (round 3, F1): the clock
        /// ticks every 10 to 16 ms, so equal stamps do not say which came first,
        /// and an unprovable order never skips.
        /// </summary>
        /// <param name="verdict">The hook's report of a bare Control.</param>
        /// <param name="hookInstalled">The hook is installed now.</param>
        /// <param name="windowActive">This window is the active one now.</param>
        /// <param name="ownWindow">This window's handle, as the hook reports windows.</param>
        /// <param name="activeSinceTick">When this window last became active; <c>long.MaxValue</c> while it is not.</param>
        /// <param name="countRunning">A count is running now.</param>
        /// <param name="countStartedTick">When that count began.</param>
        public bool HookVerdictSkips(BareControlRelease verdict, bool hookInstalled, bool windowActive,
            long ownWindow, long activeSinceTick, bool countRunning, long countStartedTick)
        {
            if (verdict.Gesture <= _lastGesture) return false;
            _lastGesture = verdict.Gesture;
            return hookInstalled
                && windowActive
                && ownWindow != 0
                && verdict.DownWindow == ownWindow
                && verdict.UpWindow == ownWindow
                && activeSinceTick < verdict.DownTick
                && countRunning
                && countStartedTick < verdict.DownTick;
        }

        /// <summary>
        /// The window's own recogniser decides only when no hook had EVER been
        /// installed by Control's press AND by its release: then nothing can
        /// have been swallowed between them. "Is a hook installed now" at the
        /// two ends is not enough (Codex round 3, F2): a hook can install,
        /// swallow and unhook between two delayed samples. With a hook, ever,
        /// only its verdict counts.
        /// </summary>
        public static bool WindowMayDecide(bool hookInstalledAtDown, bool hookInstalledAtUp) =>
            !hookInstalledAtDown && !hookInstalledAtUp;
    }
}
