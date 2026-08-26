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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    private static IntPtr _desktop;

    /// <summary>
    /// Give the desktop object back when the process ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Task #233.</b> <see cref="_desktop"/> is a static handle and
    /// <c>CloseDesktop</c> was never called, so every run that reached
    /// <see cref="CreateDesktop"/> leaked one desktop object for the life of the
    /// session. A desktop lives exactly as long as a handle to it is open and
    /// each one carries its own desktop heap, so the leak is real rather than
    /// bookkeeping — and the leading theory for the ERROR_BUSY that has dogged
    /// this code is heap exhaustion from precisely this.
    /// </para>
    /// <para>
    /// <b>This does not close that question and must not be reported as having
    /// closed it.</b> It stops a NORMALLY EXITING run from leaving one behind.
    /// A test host that is KILLED still leaks, which is inherent — the process
    /// is gone before any managed handler runs — and it is why the desktop name
    /// already carries a Guid rather than the process id: a corpse can hold the
    /// object, but it can no longer own the NAME the next run wants.
    /// </para>
    /// <para>
    /// Registered at type load rather than after a successful create, so it
    /// cannot be skipped by an early return on the failure paths.
    /// </para>
    /// </remarks>
    static PrivateDesktop()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Release();
    }

    /// <summary>
    /// Close the desktop object if one was created. Safe to call more than once
    /// and safe to call when none was created.
    /// </summary>
    public static void Release()
    {
        var handle = Interlocked.Exchange(ref _desktop, IntPtr.Zero);
        if (handle == IntPtr.Zero) return;
        try { CloseDesktop(handle); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

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
                // MEASURED 2026-08-26 on this machine, task #233: CreateDesktop
                // SUCCEEDS. Three consecutive calls with GENERIC_ALL and unique
                // names all returned handles, and CloseDesktop returned true for
                // each. The probe was a bare P/Invoke — no WPF, no dispatcher, no
                // dialog, no audio — so it could be run without a desk-free
                // window.
                //
                // That matters because the standing description of this bug says
                // "CreateDesktop returns ERROR_BUSY (170) on this machine, every
                // time", and it does not. The 170 comes from SetThreadDesktop
                // below, for the reason documented there, and the two are
                // different failures with different fixes. Anyone reading the
                // old description would tune this call and change nothing.
                //
                // It also means desktop-heap exhaustion is NOT the explanation:
                // creation works fine even though every prior run leaked its
                // handle. The leak was real and is closed in Release(); it was
                // just not the cause.
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
