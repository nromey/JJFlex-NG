using System;
using System.Runtime.InteropServices;
using System.Threading;
using JJTrace;
using TraceLevel = System.Diagnostics.TraceLevel;

namespace Radios
{
    /// <summary>
    /// Force a window to the foreground past Windows' foreground lock.
    /// Ported from Civ VI Access's WindowFocusManager (2026-08-06, at Noel's
    /// suggestion — "for that application it works every time"): Windows
    /// refuses SetForegroundWindow from a thread that doesn't own the current
    /// foreground unless the caller briefly attaches its input state to the
    /// foreground thread's. Attach, force, detach, VERIFY — and retry,
    /// because the first attempt can lose a race with whoever grabbed
    /// foreground a moment ago (the Connecting form, in the sign-in case).
    ///
    /// Call from the thread that owns the target window (its message pump),
    /// e.g. from the form's Shown handler.
    /// </summary>
    public static class WindowFocusForcer
    {
        private const int SW_RESTORE = 9;

        // ===================================================================
        // The armistice flag (round 26, 2026-08-06). The ConnectingForm has
        // its own 200ms focus-reclaim timer, built in an EARLIER round of
        // this same battle when the Auth0 browser window was considered the
        // thief. Two reclaim loops fighting produced "sm connec sm connec sm
        // connec ... connecting" live. Windows that legitimately own the
        // user's attention (sign-in forms) register here; everything with a
        // focus-reclaim habit checks the flag and stands down while any are
        // open. Counter, not bool, so overlapping windows can't clear each
        // other's claim.
        // ===================================================================

        private static int _signInWindowsOpen;

        /// <summary>True while any sign-in window is open — focus-reclaim
        /// timers elsewhere (ConnectingForm) must yield while this is set.</summary>
        public static bool SignInWindowOpen =>
            System.Threading.Volatile.Read(ref _signInWindowsOpen) > 0;

        public static void PushSignInWindow() =>
            System.Threading.Interlocked.Increment(ref _signInWindowsOpen);

        public static void PopSignInWindow() =>
            System.Threading.Interlocked.Decrement(ref _signInWindowsOpen);

        /// <summary>
        /// Returns true when the window verifiably holds the foreground.
        /// False after all attempts means the caller should tell the user
        /// where the window is instead of pretending focus moved.
        /// </summary>
        public static bool ForceForeground(IntPtr hWnd, int attempts = 6, int delayMs = 100)
        {
            if (hWnd == IntPtr.Zero) return false;

            for (int i = 0; i < attempts; i++)
            {
                if (GetForegroundWindow() == hWnd) return true;

                if (IsIconic(hWnd))
                {
                    ShowWindow(hWnd, SW_RESTORE);
                }

                var foreground = GetForegroundWindow();
                uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
                uint currentThread = GetCurrentThreadId();

                bool attached = foregroundThread != 0
                    && foregroundThread != currentThread
                    && AttachThreadInput(currentThread, foregroundThread, true);

                SetForegroundWindow(hWnd);
                BringWindowToTop(hWnd);

                if (attached)
                {
                    AttachThreadInput(currentThread, foregroundThread, false);
                }

                if (GetForegroundWindow() == hWnd)
                {
                    Tracing.TraceLine($"WindowFocusForcer: foreground taken on attempt {i + 1}", TraceLevel.Info);
                    return true;
                }

                Thread.Sleep(delayMs);
            }

            bool finalCheck = GetForegroundWindow() == hWnd;
            if (!finalCheck)
            {
                Tracing.TraceLine("WindowFocusForcer: could not take foreground after all attempts", TraceLevel.Warning);
            }
            return finalCheck;
        }

        /// <summary>
        /// Guard a just-forced window against LATE thieves — found live
        /// 2026-08-06: the sign-in dialog verifiably took foreground, then the
        /// Connecting form appeared ~half a second later and squashed it. The
        /// initial force "thinks it's been successful because it has" (Noel).
        /// For the grace window, any foreground steal by a window of OUR OWN
        /// process gets reclaimed; a steal by another application means the
        /// user chose to leave, and the watchdog stands down immediately —
        /// reclaiming against the user is hostile. Call from the form's own
        /// thread (Shown handler); the timer lives on that thread's pump and
        /// dies with the form.
        /// </summary>
        public static void KeepForegroundWhileVisible(System.Windows.Forms.Form form, int graceMs = 6000)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            long deadline = Environment.TickCount64 + graceMs;
            timer.Tick += (_, _) =>
            {
                if (form.IsDisposed || !form.Visible || Environment.TickCount64 > deadline)
                {
                    timer.Stop();
                    timer.Dispose();
                    return;
                }

                var fg = GetForegroundWindow();
                if (fg == form.Handle) return;

                GetWindowThreadProcessId(fg, out uint thiefPid);
                if (thiefPid == (uint)Environment.ProcessId)
                {
                    Tracing.TraceLine(
                        "WindowFocusForcer: own-process window stole foreground - reclaiming",
                        TraceLevel.Info);
                    ForceForeground(form.Handle, attempts: 2, delayMs: 50);
                }
                else
                {
                    timer.Stop();
                    timer.Dispose();
                }
            };
            timer.Start();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
