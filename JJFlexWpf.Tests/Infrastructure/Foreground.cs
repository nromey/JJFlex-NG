using System.Runtime.InteropServices;
using System.Text;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>Who currently owns the keyboard, from the operating system's point of view.</summary>
public sealed record ForegroundWindow(int ProcessId, string Title)
{
    public override string ToString() => $"pid {ProcessId}, \"{Title}\"";
}

/// <summary>
/// The foreground window of the INPUT desktop, the one the operator sees and
/// types into, or why it could not be read (task #908). <see cref="Read"/>
/// false means nothing was measured, and a check built on it must say so
/// rather than pass.
/// </summary>
public sealed record InputForeground(bool Read, string? Desktop, long Handle, int ProcessId, string Title, string? Failure)
{
    public override string ToString()
        => Read
            ? $"input desktop '{Desktop}': window 0x{Handle:X}, pid {(ProcessId == 0 ? "not read" : ProcessId.ToString())}, \"{Title}\""
            : "NOT READ: " + Failure;
}

/// <summary>
/// The operator-facing guarantee, measured rather than asserted: running this
/// suite must not change which window has the keyboard. Every run checks it.
/// </summary>
public static class Foreground
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, [Out] char[]? pvInfo, uint nLength, out uint lpnLengthNeeded);

    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    /// <summary>
    /// The foreground window as the CALLING thread's desktop sees it. On a
    /// hidden desktop that is always nothing (pid 0), which is why the check
    /// that nothing took the keyboard reads <see cref="OnInputDesktop"/>
    /// instead (task #908).
    /// </summary>
    public static ForegroundWindow Current()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            GetWindowThreadProcessId(hwnd, out var processId);
            var title = new StringBuilder(512);
            GetWindowTextW(hwnd, title, title.Capacity);
            return new ForegroundWindow(processId, title.ToString());
        }
        catch
        {
            return new ForegroundWindow(0, string.Empty);
        }
    }

    /// <summary>
    /// Reads the input desktop's foreground window from a short-lived thread
    /// that is first placed on the input desktop (task #908).
    ///
    /// <para><b>Why a thread of its own.</b> <c>GetForegroundWindow</c> answers
    /// for the calling thread's desktop, so on deskrun's hidden desktop it
    /// always returned nothing, and the check compared nothing with nothing. A
    /// thread can be put on another desktop with <c>SetThreadDesktop</c> only
    /// while it owns no window and no hook, so a fresh thread that does nothing
    /// else is used, and the shared UI thread is never moved. The thread
    /// creates no window, so it puts nothing on the operator's screen, and it
    /// never calls <c>SwitchDesktop</c>, so the visible desktop never
    /// changes.</para>
    ///
    /// <para>Every step that fails is returned as not read, with the Windows
    /// error, never as an empty reading.</para>
    /// </summary>
    public static InputForeground OnInputDesktop()
    {
        InputForeground? result = null;
        var desktop = IntPtr.Zero;
        var thread = new Thread(() =>
        {
            desktop = OpenInputDesktop(0, false, DesktopReadObjects);
            if (desktop == IntPtr.Zero)
            {
                result = new InputForeground(false, null, 0, 0, "", $"OpenInputDesktop failed, Windows error {Marshal.GetLastWin32Error()}.");
                return;
            }

            var name = NameOf(desktop);
            if (!SetThreadDesktop(desktop))
            {
                result = new InputForeground(false, name, 0, 0, "", $"SetThreadDesktop to the input desktop '{name}' failed, Windows error {Marshal.GetLastWin32Error()}.");
                return;
            }

            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                result = new InputForeground(false, name, 0, 0, "",
                    $"GetForegroundWindow on the input desktop '{name}' returned no window: either none has the keyboard at this moment or it could not be seen from here.");
                return;
            }

            // The pid can be unreadable while the window handle is not: deskrun's
            // job forbids using window handles owned by processes outside it, and
            // the operator's windows are all outside it. The handle value alone
            // still identifies the window.
            GetWindowThreadProcessId(hwnd, out var processId);
            var title = new StringBuilder(512);
            GetWindowTextW(hwnd, title, title.Capacity);
            result = new InputForeground(true, name, hwnd.ToInt64(), processId, title.ToString(), null);
        })
        {
            IsBackground = true,
            Name = "JJFlex input-desktop foreground read",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        var finished = thread.Join(TimeSpan.FromSeconds(10));

        // Closed only once the thread that used it has ended: CloseDesktop
        // refuses a desktop a thread of this process is still on.
        if (finished && desktop != IntPtr.Zero) CloseDesktop(desktop);

        if (!finished)
            return new InputForeground(false, null, 0, 0, "", "The reading thread did not finish within 10 seconds.");
        return result ?? new InputForeground(false, null, 0, 0, "", "The reading thread ended without a result.");
    }

    private static string? NameOf(IntPtr handle)
    {
        GetUserObjectInformation(handle, UoiName, null, 0, out var needed);
        if (needed == 0) return null;
        var buffer = new char[needed / 2 + 1];
        return GetUserObjectInformation(handle, UoiName, buffer, (uint)(buffer.Length * 2), out _)
            ? new string(buffer).TrimEnd('\0')
            : null;
    }
}
