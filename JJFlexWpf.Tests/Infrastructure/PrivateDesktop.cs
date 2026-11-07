using System.Runtime.InteropServices;

namespace JJFlexWpf.Tests.Infrastructure;

public enum DesktopIsolation
{
    NotAttempted,

    /// <summary>The UI thread was MOVED onto a private desktop this process created.</summary>
    Isolated,

    CreateFailed,
    SwitchFailed,

    /// <summary>
    /// The whole process STARTED on a hidden desktop (task #906, the
    /// <c>tools/deskrun</c> launcher), and the thread's desktop, the input
    /// desktop and the window station were each read from Windows and judged
    /// acceptable by <see cref="PrivateDesktop.JudgeStartingDesktop"/>.
    /// <see cref="DeskGuard.Decide"/> treats it exactly like
    /// <see cref="Isolated"/>; it is a separate value only so the report can
    /// say which route isolated the run.
    /// </summary>
    StartedOnPrivateDesktop,
}

/// <summary>
/// What <see cref="PrivateDesktop.JudgeStartingDesktop"/> concluded about the
/// desktop a thread was already on.
/// </summary>
public enum StartingDesktopJudgement
{
    /// <summary>A named desktop that is not shared and not receiving input, on WinSta0.</summary>
    Accepted,

    /// <summary>The process is not on WinSta0, the interactive console session.</summary>
    NotTheConsoleWindowStation,

    /// <summary>The thread's desktop name could not be read.</summary>
    ThreadDesktopUnreadable,

    /// <summary>Default, Winlogon or Screen-saver: desktops that a person sees.</summary>
    SharedDesktop,

    /// <summary>The input desktop could not be opened or named, so nothing is assumed.</summary>
    InputDesktopUnreadable,

    /// <summary>The thread's desktop IS the one receiving input: someone switched to it.</summary>
    IsTheInputDesktop,
}

/// <summary>
/// The three names <see cref="PrivateDesktop.JudgeStartingDesktop"/> needs,
/// each read from Windows. A null name means it could not be read.
/// </summary>
public sealed record DesktopReading(string? WindowStation, string? ThreadDesktop, string? InputDesktop, int InputError);

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
    private static string? _createdName;

    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr hObj, int nIndex, [Out] char[]? pvInfo, uint nLength, out uint lpnLengthNeeded);

    /// <summary>The name of a window station or desktop, or null when it cannot be read.</summary>
    private static string? ObjectName(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        GetUserObjectInformation(handle, UoiName, null, 0, out uint needed);
        if (needed == 0) return null;
        var buffer = new char[needed / 2 + 1];
        if (!GetUserObjectInformation(handle, UoiName, buffer, (uint)(buffer.Length * 2), out _))
            return null;
        int len = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, len < 0 ? buffer.Length : len);
    }

    /// <summary>
    /// The desktop a window's thread is on, read from Windows. For the
    /// created-state test, which must show the window it built was NOT on the
    /// operator's desktop.
    /// </summary>
    public static string? DesktopOfWindow(IntPtr hwnd)
    {
        uint thread = GetWindowThreadProcessId(hwnd, out _);
        return thread == 0 ? null : ObjectName(GetThreadDesktop(thread));
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

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

    /// <summary>What Windows said about the desktop the thread started on, at the last attempt.</summary>
    public static DesktopReading? LastReading { get; private set; }

    /// <summary>The judgement of <see cref="LastReading"/>.</summary>
    public static StartingDesktopJudgement? LastJudgement { get; private set; }

    /// <summary>The desktop the thread ended up isolated on, by either route, or null.</summary>
    public static string? IsolatedDesktopName { get; private set; }

    /// <summary>
    /// Desktops a person sees: the ordinary desktop, the secure one the lock
    /// screen and UAC use, and the screen saver's. Never acceptable as "hidden"
    /// however they were reached.
    /// </summary>
    private static readonly string[] SharedDesktops = { "Default", "Winlogon", "Screen-saver" };

    /// <summary>
    /// Is the desktop this thread is ALREADY on one that nobody can see?
    /// Pure, so every row can be tested without a desktop (task #906).
    /// </summary>
    /// <param name="windowStation">The process's window station name, read from Windows.</param>
    /// <param name="threadDesktop">The thread's desktop name, read from Windows.</param>
    /// <param name="inputDesktop">
    /// The name of the desktop receiving input, read from Windows, or null when
    /// it could not be opened (as when the workstation is locked).
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Fail closed, in every row.</b> Anything unread is a refusal, never an
    /// assumption: an unreadable input desktop in particular refuses rather than
    /// guessing that it is "probably Default", because the one case where it
    /// cannot be read is the case where the screen is in a state nobody planned
    /// for.
    /// </para>
    /// <para>
    /// <b>Why the input-desktop comparison and not just the name.</b> A desktop
    /// with an unfamiliar name is hidden only while nothing has switched to it.
    /// <c>SwitchDesktop</c> can make any desktop the visible one, and then its
    /// windows are in front of the operator whatever it is called.
    /// </para>
    /// <para>
    /// <b>The names come from Windows, never from an environment variable or an
    /// argument.</b> The launcher announces its desktop's name in
    /// <c>JJFLEX_DESKRUN_DESKTOP</c> for the log reader's benefit; nothing here
    /// reads it, and a test forges it to prove that.
    /// </para>
    /// </remarks>
    public static StartingDesktopJudgement JudgeStartingDesktop(
        string? windowStation, string? threadDesktop, string? inputDesktop)
    {
        if (!string.Equals(windowStation, "WinSta0", StringComparison.OrdinalIgnoreCase))
            return StartingDesktopJudgement.NotTheConsoleWindowStation;

        if (string.IsNullOrWhiteSpace(threadDesktop))
            return StartingDesktopJudgement.ThreadDesktopUnreadable;

        foreach (var shared in SharedDesktops)
            if (string.Equals(threadDesktop, shared, StringComparison.OrdinalIgnoreCase))
                return StartingDesktopJudgement.SharedDesktop;

        if (string.IsNullOrWhiteSpace(inputDesktop))
            return StartingDesktopJudgement.InputDesktopUnreadable;

        if (string.Equals(threadDesktop, inputDesktop, StringComparison.OrdinalIgnoreCase))
            return StartingDesktopJudgement.IsTheInputDesktop;

        return StartingDesktopJudgement.Accepted;
    }

    /// <summary>
    /// Read, from Windows, the three names <see cref="JudgeStartingDesktop"/>
    /// needs for the CALLING thread. Creates no window and changes nothing.
    /// </summary>
    public static DesktopReading ReadCurrentThread()
    {
        string? station = null, thread = null, input = null;
        int inputError = 0;
        try
        {
            station = ObjectName(GetProcessWindowStation());
            thread = ObjectName(GetThreadDesktop(GetCurrentThreadId()));

            IntPtr inputDesktop = OpenInputDesktop(0, false, DesktopReadObjects);
            if (inputDesktop == IntPtr.Zero)
            {
                inputError = Marshal.GetLastWin32Error();
            }
            else
            {
                try { input = ObjectName(inputDesktop); }
                finally { CloseDesktop(inputDesktop); }
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        return new DesktopReading(station, thread, input, inputError);
    }

    /// <summary>
    /// One line for the verdict: WHICH route isolated the run, and on what
    /// desktop, or why neither did. "Moved the thread" and "started on a hidden
    /// desktop" must not look the same afterwards. Pure.
    /// </summary>
    public static string DescribeRoute(DesktopIsolation isolation, DesktopReading? reading,
                                       StartingDesktopJudgement? judgement, string? isolatedOn, int lastError)
    {
        string started = reading == null
            ? "the starting desktop was not read"
            : "started on '" + (reading.WindowStation ?? "?") + "\\" + (reading.ThreadDesktop ?? "?")
              + "', input desktop '" + (reading.InputDesktop ?? "unreadable, error " + reading.InputError) + "'";

        return isolation switch
        {
            DesktopIsolation.StartedOnPrivateDesktop =>
                "route: the process started on hidden desktop '" + isolatedOn + "' (" + started + ")",
            DesktopIsolation.Isolated =>
                "route: moved the UI thread onto private desktop '" + isolatedOn + "' (" + started
                + ", not accepted: " + judgement + ")",
            DesktopIsolation.SwitchFailed =>
                "route: none; " + started + ", not accepted: " + judgement
                + "; moving the thread failed, Windows error " + lastError,
            DesktopIsolation.CreateFailed =>
                "route: none; " + started + ", not accepted: " + judgement
                + "; creating a private desktop failed, Windows error " + lastError,
            _ => "route: none; isolation not attempted",
        };
    }

    /// <summary>
    /// Only the first half of <see cref="MoveCurrentThread"/>: read where the
    /// calling thread already is and report
    /// <see cref="DesktopIsolation.StartedOnPrivateDesktop"/> when it is an
    /// accepted hidden desktop, otherwise <see cref="DesktopIsolation.NotAttempted"/>.
    /// Creates nothing, moves nothing.
    /// </summary>
    /// <remarks>
    /// <b>Task #906 round 2.</b> Used when isolation was NOT requested
    /// (<c>JJFLEX_TIER1_PRIVATE_DESKTOP=0</c>, or test code switching
    /// <see cref="UiThread.RequestPrivateDesktop"/> off). The hidden-desktop
    /// route is a fact about where the process started, not about what was
    /// asked for, and <see cref="DeskGuard.Decide"/> keys "do not consult the
    /// declaration" on it. If this were only read when isolation was asked
    /// for, switching isolation off and setting the declaration would put the
    /// waiver back on the very route the ruling took it off.
    /// </remarks>
    public static DesktopIsolation ReadStartingRoute()
    {
        try
        {
            var reading = ReadCurrentThread();
            LastReading = reading;
            LastJudgement = JudgeStartingDesktop(reading.WindowStation, reading.ThreadDesktop, reading.InputDesktop);
            if (LastJudgement != StartingDesktopJudgement.Accepted) return DesktopIsolation.NotAttempted;
            IsolatedDesktopName = reading.WindowStation + "\\" + reading.ThreadDesktop;
            return DesktopIsolation.StartedOnPrivateDesktop;
        }
        catch (DllNotFoundException) { return DesktopIsolation.NotAttempted; }
        catch (EntryPointNotFoundException) { return DesktopIsolation.NotAttempted; }
    }

    public static DesktopIsolation MoveCurrentThread()
    {
        try
        {
            // TASK #906: FIRST, ask where the thread already is. Under the
            // tools/deskrun launcher the whole process was born on a hidden
            // desktop, which is the only way a test host's STA thread can be
            // there at all (see the ERROR_BUSY note below). Every fact is read
            // from Windows; anything else falls through to the old path, which
            // under "dotnet test" still fails and still refuses.
            var reading = ReadCurrentThread();
            LastReading = reading;
            LastJudgement = JudgeStartingDesktop(reading.WindowStation, reading.ThreadDesktop, reading.InputDesktop);
            if (LastJudgement == StartingDesktopJudgement.Accepted)
            {
                IsolatedDesktopName = reading.WindowStation + "\\" + reading.ThreadDesktop;
                return DesktopIsolation.StartedOnPrivateDesktop;
            }

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
                else _createdName = name;
            }

            if (_desktop == IntPtr.Zero) return DesktopIsolation.CreateFailed;
            if (SetThreadDesktop(_desktop))
            {
                IsolatedDesktopName = _createdName;
                return DesktopIsolation.Isolated;
            }

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
