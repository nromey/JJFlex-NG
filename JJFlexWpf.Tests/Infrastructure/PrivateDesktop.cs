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

    public static DesktopIsolation MoveCurrentThread()
    {
        try
        {
            if (_desktop == IntPtr.Zero)
            {
                var name = "JJFlexTier1_" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
            }

            if (_desktop == IntPtr.Zero) return DesktopIsolation.CreateFailed;
            return SetThreadDesktop(_desktop) ? DesktopIsolation.Isolated : DesktopIsolation.SwitchFailed;
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
