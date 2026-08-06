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
    internal static class WindowFocusForcer
    {
        private const int SW_RESTORE = 9;

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
