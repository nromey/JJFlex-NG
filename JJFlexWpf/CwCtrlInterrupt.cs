using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace JJFlexWpf
{
    /// <summary>
    /// Makes Ctrl silence CW notifications the way it already silences
    /// speech (#182).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The problem this closes:</b> Ctrl is the universal "stop talking"
    /// for every screen reader user, pressed reflexively without deciding to.
    /// Until this class, the reflex HALF-worked in this app: the reader
    /// silenced its speech and the CW kept keying. A trained response that
    /// produces a partial result is worse than no interrupt, because the
    /// operator has no model that explains it.
    /// </para>
    /// <para>
    /// <b>The mechanism is a low-level keyboard hook (WH_KEYBOARD_LL),
    /// modeled directly on the one <see cref="HelpLauncher"/> installs</b> —
    /// same P/Invoke shape, same Win32 error reporting, deliberately not a
    /// second approach. A low-level hook observes keys BEFORE application
    /// dispatch, so it reacts while the screen reader still receives Ctrl
    /// and still silences itself.
    /// </para>
    /// <para>
    /// <b>THIS HOOK NEVER SWALLOWS.</b> Every path returns
    /// <c>CallNextHookEx</c>; there is no code path that returns 1. A hook
    /// that consumed Ctrl would break speech interruption system-wide —
    /// far worse than the bug it fixes. The decision logic is factored into
    /// <see cref="Decide"/> precisely so a test can assert the invariant
    /// over the whole input space rather than trusting this comment.
    /// </para>
    /// <para>
    /// <b>Semantics mirror the reader exactly, so there is one model:</b>
    /// any physical Ctrl keydown — left or right, alone or opening a
    /// shortcut, whichever window has focus — cancels in-flight and pending
    /// CW immediately, mid-character included. The mid-character prohibition
    /// (#88) protects an operator who is READING; an operator who pressed
    /// "stop" is not reading and owns their own ears, and speech does not
    /// finish the word either. The cancel reaches the CW output only; a
    /// continuous earcon (ATU progress) on the shared alert mixer is
    /// untouched, exactly as with the repeat key's cancel.
    /// </para>
    /// <para>
    /// <b>Injected Ctrl counts on purpose.</b> Screen readers re-inject
    /// keys — JAWS synthesises down/up pairs — and filtering
    /// LLKHF_INJECTED would silently exempt exactly this app's audience.
    /// Auto-repeat while held fires once, on the down transition.
    /// </para>
    /// <para>
    /// <b>The hook lives on <see cref="KeyboardHookThread"/>, never the UI
    /// thread (#402).</b> Windows delivers a WH_KEYBOARD_LL callback via the
    /// pump of the thread that installed it, so a hook installed from the UI
    /// thread makes every keystroke ON THE MACHINE wait out
    /// LowLevelHooksTimeout whenever that thread is blocked — which a stuck
    /// connect did on 2026-08-29, three times, ~45 s each. Install here only
    /// hands the real installation to the dedicated pumped thread;
    /// #307 (global hotkeys), which this comment used to promise the shared
    /// wrapper to, gets that same host when it lands.
    /// </para>
    /// </remarks>
    public static class CwCtrlInterrupt
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;

        internal const uint VK_CONTROL = 0x11;   // generic — some injectors send it
        internal const uint VK_LCONTROL = 0xA2;
        internal const uint VK_RCONTROL = 0xA3;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelKeyboardProc? _hookProc; // keep delegate alive for the unmanaged callback
        private static IntPtr _hookHandle = IntPtr.Zero;

        private static Func<bool>? _cwActive;
        private static Action? _cancelCw;

        /// <summary>
        /// True while we believe a Ctrl key is held, so auto-repeat keydowns
        /// fire the cancel once per press rather than thirty times a second.
        /// Only the hook thread writes it.
        /// </summary>
        private static bool _ctrlDown;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        /// <summary>True once the install has been handed to the hook thread.</summary>
        private static bool _installPosted;

        /// <summary>
        /// Install the hook. Call once, from anywhere — the actual
        /// SetWindowsHookEx runs on <see cref="KeyboardHookThread"/>'s
        /// dedicated pump, never on the caller (#402), so a blocked UI
        /// thread can no longer stall keystrokes machine-wide.
        /// </summary>
        /// <param name="cwActive">
        /// Cheap busy check consulted on the hook thread — must be a volatile
        /// read, never real work. When false the press does nothing at all.
        /// </param>
        /// <param name="cancelCw">
        /// The cancel, run on the thread pool — never on the hook thread. A
        /// slow low-level hook lags every keystroke on the machine and gets
        /// silently removed by Windows.
        /// </param>
        public static void Install(Func<bool> cwActive, Action cancelCw)
        {
            _cwActive = cwActive ?? throw new ArgumentNullException(nameof(cwActive));
            _cancelCw = cancelCw ?? throw new ArgumentNullException(nameof(cancelCw));

            if (_installPosted)
                return;
            _installPosted = true;

            KeyboardHookThread.InstallHook(
                "CwCtrlInterrupt (Ctrl silences CW)",
                installOnHookThread: InstallOnHookThread,
                unhookOnHookThread: UnhookOnHookThread);
        }

        /// <summary>
        /// The real installation. Runs ONLY on the hook thread — Windows
        /// delivers the callback via the installing thread's pump, and that
        /// thread must be one that can never block.
        /// </summary>
        private static void InstallOnHookThread()
        {
            if (_hookHandle != IntPtr.Zero)
                return;

            try
            {
                _hookProc = HookCallback;
                using var process = Process.GetCurrentProcess();
                using var module = process.MainModule;
                _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
                    GetModuleHandle(module?.ModuleName), 0);

                if (_hookHandle == IntPtr.Zero)
                {
                    Trace.WriteLine(
                        $"CwCtrlInterrupt: SetWindowsHookEx failed, error {Marshal.GetLastWin32Error()} — " +
                        "Ctrl will not silence CW notifications (speech interruption is the reader's and is unaffected).");
                    _hookProc = null;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"CwCtrlInterrupt.Install: {ex.Message}");
                _hookProc = null;
            }
        }

        /// <summary>
        /// Teardown, run on the hook thread during shutdown. A global hook
        /// left installed while its thread has stopped pumping degrades
        /// every keystroke on the machine — the very failure #402 removes.
        /// </summary>
        private static void UnhookOnHookThread()
        {
            if (_hookHandle == IntPtr.Zero)
                return;
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            _hookProc = null;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                long msg = (long)wParam;
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                if (isDown || isUp)
                {
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    var d = Decide(data.vkCode, isDown, _ctrlDown, _cwActive?.Invoke() ?? false);
                    _ctrlDown = d.CtrlNowDown;
                    if (d.CancelCw)
                    {
                        // Off the hook thread before touching audio. The
                        // callback's whole job is to be over in microseconds.
                        var cancel = _cancelCw;
                        if (cancel != null) _ = Task.Run(cancel);
                    }
                    // d.SwallowKey is false by construction (see Decide) and
                    // is not even consulted: this hook has no swallow path.
                }
            }
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        /// <summary>What one key event means. See <see cref="Decide"/>.</summary>
        internal readonly struct Decision
        {
            public Decision(bool cancelCw, bool ctrlNowDown)
            {
                CancelCw = cancelCw;
                CtrlNowDown = ctrlNowDown;
            }

            /// <summary>Fire the CW cancel for this event.</summary>
            public bool CancelCw { get; }

            /// <summary>Held-state to carry to the next event.</summary>
            public bool CtrlNowDown { get; }

            /// <summary>
            /// ALWAYS false. The property exists so the never-swallows
            /// invariant is an assertable fact rather than a comment: this
            /// hook observes Ctrl, and the screen reader must keep receiving
            /// it and keep silencing its own speech.
            /// </summary>
            public bool SwallowKey => false;
        }

        /// <summary>
        /// The pure decision: given one low-level key event and the current
        /// held-state, should the CW cancel fire, and what is the new
        /// held-state. No Win32, no audio, no state — so a unit test can
        /// sweep the whole input space and pin the invariants: never swallow,
        /// fire once per press (not per auto-repeat), fire only while CW is
        /// audible, ignore every non-Ctrl key.
        /// </summary>
        internal static Decision Decide(uint vkCode, bool isKeyDown, bool ctrlAlreadyDown, bool cwActive)
        {
            bool isCtrl = vkCode == VK_CONTROL || vkCode == VK_LCONTROL || vkCode == VK_RCONTROL;
            if (!isCtrl)
                return new Decision(cancelCw: false, ctrlNowDown: ctrlAlreadyDown);

            if (!isKeyDown)
                return new Decision(cancelCw: false, ctrlNowDown: false);

            // Down transition only: a held Ctrl auto-repeats WM_KEYDOWN and
            // must not spam cancels. A press with nothing keying does
            // nothing — idempotent by inspection, but not even worth a
            // thread-pool hop.
            bool transition = !ctrlAlreadyDown;
            return new Decision(cancelCw: transition && cwActive, ctrlNowDown: true);
        }
    }
}
