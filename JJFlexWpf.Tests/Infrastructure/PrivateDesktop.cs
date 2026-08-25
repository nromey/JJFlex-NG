using System.Runtime.InteropServices;

namespace JJFlexWpf.Tests.Infrastructure;

public enum DesktopIsolation
{
    NotAttempted,
    Isolated,
    CreateFailed,
    SwitchFailed,
}

/// <summary>
/// Strategy 3 of the focus-avoidance ladder: move the UI thread onto a desktop
/// object that is not the interactive one, so anything it shows is not merely
/// off-screen but on a surface no screen reader and no foreground window ever
/// looks at.
///
/// <para>Real but blunt. A desktop switch is per-thread and must happen before
/// the thread owns any window, and anything that puts up a modal Win32 dialog
/// over there (a MessageBox, a crash dialog) hangs with nobody able to dismiss
/// it. Kept as a fallback rather than the default for that reason.</para>
/// </summary>
internal static class PrivateDesktop
{
    private const uint GenericAll = 0x10000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(
        string lpszDesktop, IntPtr lpszDevice, IntPtr pDevmode, uint dwFlags, uint dwDesiredAccess, IntPtr lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    private static IntPtr _desktop;

    /// <summary>Win32 error from the last failed attempt, for the report.</summary>
    public static int LastError { get; private set; }

    public static DesktopIsolation MoveCurrentThread()
    {
        try
        {
            if (_desktop == IntPtr.Zero)
            {
                // UNIQUE PER ATTEMPT, not per process. A desktop object lives
                // as long as a handle to it is open, so a test host that was
                // KILLED can leave one behind — and Windows recycles process
                // ids, so the next run picked the same name and CreateDesktop
                // returned ERROR_BUSY (170). That is what happened on
                // 2026-08-25: isolation failed, the failure was recorded to a
                // property nobody read, and the dialogs went to the operator's
                // screen instead. A Guid cannot collide with a corpse.
                var name = "JJFlexTier1_"
                    + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                _desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
                if (_desktop == IntPtr.Zero) LastError = Marshal.GetLastWin32Error();
            }

            if (_desktop == IntPtr.Zero) return DesktopIsolation.CreateFailed;
            if (SetThreadDesktop(_desktop)) return DesktopIsolation.Isolated;

            // Measured 2026-08-20: this fails with ERROR_BUSY (170) on a WPF UI
            // thread. SetThreadDesktop refuses once the thread owns any window,
            // and the CLR's OleInitialize on an STA thread creates the hidden
            // OLE message window before any of our code runs. A private desktop
            // is therefore unreachable from inside an STA thread; it would have
            // to be set for the whole process at launch, through
            // STARTUPINFO.lpDesktop, which is not ours to choose under
            // "dotnet test".
            LastError = Marshal.GetLastWin32Error();
            return DesktopIsolation.SwitchFailed;
        }
        catch (DllNotFoundException)
        {
            return DesktopIsolation.CreateFailed;
        }
        catch (EntryPointNotFoundException)
        {
            return DesktopIsolation.CreateFailed;
        }
    }
}
