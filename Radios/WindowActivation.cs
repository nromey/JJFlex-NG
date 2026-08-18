using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Bring one of our windows genuinely to the foreground, including in the
    /// cases where Windows refuses to let us.
    ///
    /// **Why a plain SetForegroundWindow is not enough.** Windows only honours
    /// SetForegroundWindow from a process that already owns the foreground
    /// window (or was explicitly handed the right by one that did). Every other
    /// caller gets a silent refusal: the function returns false, the taskbar
    /// button flashes, and the window sits behind whatever the operator was
    /// looking at. There is no error, no exception, nothing to notice.
    ///
    /// That refusal is invisible to a sighted user — they see the flashing
    /// button and click it. It is NOT invisible to a screen reader user, who
    /// gets no announcement at all and has to go hunting for a window they were
    /// never told had opened. Reported 2026-08-18 launching from PowerShell:
    /// the console keeps the foreground, so the connect dialog opens unfocused
    /// and unannounced.
    ///
    /// The console is only the easiest way to reproduce it. The same refusal
    /// applies whenever something other than the shell starts or raises us —
    /// the updater relaunching the app, a scheduled task, another application
    /// invoking us.
    ///
    /// **The workaround** is to briefly attach our input queue to the current
    /// foreground thread's. While attached, Windows treats the two threads as
    /// one input context, so the foreground restriction no longer applies and
    /// SetForegroundWindow is honoured. We detach immediately afterwards.
    ///
    /// We deliberately do NOT change the system-wide foreground lock timeout
    /// (SPI_SETFOREGROUNDLOCKTIMEOUT). That is a machine setting owned by the
    /// operator, not by us, and altering it would affect every application on
    /// the box to fix a problem in one.
    /// </summary>
    public static class WindowActivation
    {
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        /// <summary>
        /// Make <paramref name="hWnd"/> the foreground window if it is not
        /// already ours to command.
        ///
        /// Does NOTHING when this process already owns the foreground — that is
        /// the ordinary case, where the operator opened a dialog themselves and
        /// Windows will focus it correctly without help. Forcing foreground
        /// unconditionally would let any dialog yank focus away at a moment the
        /// operator did not choose, which is a worse defect than the one being
        /// fixed.
        ///
        /// Never throws: failing to raise a window must not take down the
        /// window that was trying to raise itself.
        /// </summary>
        public static void EnsureForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;

            try
            {
                if (AlreadyOurs()) return;

                if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

                var foreground = GetForegroundWindow();
                uint ourThread = GetCurrentThreadId();
                uint theirThread = foreground == IntPtr.Zero
                    ? 0
                    : GetWindowThreadProcessId(foreground, out _);

                bool attached = theirThread != 0
                                && theirThread != ourThread
                                && AttachThreadInput(ourThread, theirThread, true);
                try
                {
                    BringWindowToTop(hWnd);
                    if (!SetForegroundWindow(hWnd))
                    {
                        Tracing.TraceLine(
                            "WindowActivation: SetForegroundWindow refused even while "
                            + "attached — the window will flash in the taskbar instead.",
                            TraceLevel.Warning);
                    }
                }
                finally
                {
                    if (attached) AttachThreadInput(ourThread, theirThread, false);
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"WindowActivation: EnsureForeground failed: {ex.Message}",
                    TraceLevel.Warning);
            }
        }

        /// <summary>
        /// True when the foreground window already belongs to this process, so
        /// there is nothing to take and nothing to take it from.
        /// </summary>
        private static bool AlreadyOurs()
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            GetWindowThreadProcessId(foreground, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }
    }
}
