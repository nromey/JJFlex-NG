using System.Runtime.InteropServices;

namespace JJFlex.DeskRun;

/// <summary>The Win32 calls the launcher needs: desktops, a job object, CreateProcess.</summary>
internal static class Native
{
    // ─────────────────────────── desktops ───────────────────────────

    internal const uint GENERIC_ALL = 0x10000000;
    internal const uint DESKTOP_READOBJECTS = 0x0001;
    internal const int UOI_NAME = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateDesktop(
        string lpszDesktop, IntPtr lpszDevice, IntPtr pDevmode, uint dwFlags, uint dwDesiredAccess, IntPtr lpsa);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(
        IntPtr hObj, int nIndex, [Out] char[]? pvInfo, uint nLength, out uint lpnLengthNeeded);

    /// <summary>The name of a window station or desktop, or null when it cannot be read.</summary>
    internal static string? ObjectName(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        GetUserObjectInformation(handle, UOI_NAME, null, 0, out uint needed);
        if (needed == 0) return null;
        var buffer = new char[needed / 2 + 1];
        if (!GetUserObjectInformation(handle, UOI_NAME, buffer, (uint)(buffer.Length * 2), out _))
            return null;
        int len = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, len < 0 ? buffer.Length : len);
    }

    // ─────────────────────────── processes ───────────────────────────

    internal const uint CREATE_SUSPENDED = 0x00000004;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint CREATE_NO_WINDOW = 0x08000000;
    internal const int STARTF_USESTDHANDLES = 0x00000100;
    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint WAIT_TIMEOUT = 0x102;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    internal const int PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x0002000D;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcess(
        string? lpApplicationName, char[] lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool IsProcessInJob(IntPtr processHandle, IntPtr jobHandle, out bool result);

    /// <summary>
    /// A process-creation attribute list holding the job the child is born
    /// into and the only handles it may inherit. The memory behind both values
    /// must outlive CreateProcess, so this owns it.
    /// </summary>
    internal sealed class AttributeList : IDisposable
    {
        public IntPtr Pointer { get; private set; }
        private IntPtr _jobs;
        private IntPtr _handles;

        private AttributeList() { }

        public static AttributeList? Create(IntPtr job, IntPtr[] inheritable, out string error)
        {
            error = "";
            var list = new AttributeList();
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            list.Pointer = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list.Pointer, 2, 0, ref size))
            {
                error = "could not initialise the process attribute list (Windows error "
                        + Marshal.GetLastWin32Error() + ").";
                Marshal.FreeHGlobal(list.Pointer);
                list.Pointer = IntPtr.Zero;
                return null;
            }

            list._jobs = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(list._jobs, job);
            if (!UpdateProcThreadAttribute(list.Pointer, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, list._jobs,
                                           IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                error = "could not ask Windows to create the child inside the cleanup job "
                        + "(PROC_THREAD_ATTRIBUTE_JOB_LIST, Windows error " + Marshal.GetLastWin32Error() + ").";
                list.Dispose();
                return null;
            }

            list._handles = Marshal.AllocHGlobal(IntPtr.Size * inheritable.Length);
            for (int i = 0; i < inheritable.Length; i++)
                Marshal.WriteIntPtr(list._handles, i * IntPtr.Size, inheritable[i]);
            if (!UpdateProcThreadAttribute(list.Pointer, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, list._handles,
                                           IntPtr.Size * inheritable.Length, IntPtr.Zero, IntPtr.Zero))
            {
                error = "could not limit the child's inherited handles (PROC_THREAD_ATTRIBUTE_HANDLE_LIST, "
                        + "Windows error " + Marshal.GetLastWin32Error() + ").";
                list.Dispose();
                return null;
            }

            return list;
        }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(Pointer);
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
            }
            if (_jobs != IntPtr.Zero) { Marshal.FreeHGlobal(_jobs); _jobs = IntPtr.Zero; }
            if (_handles != IntPtr.Zero) { Marshal.FreeHGlobal(_handles); _handles = IntPtr.Zero; }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    // ─────────────────────────── job object ───────────────────────────

    internal const int JobObjectBasicAccountingInformation = 1;
    internal const int JobObjectBasicProcessIdList = 3;
    internal const int JobObjectExtendedLimitInformation = 9;
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetInformationJobObject(
        IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool QueryInformationJobObject(
        IntPtr hJob, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int cbInfo, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool QueryInformationJobObject(
        IntPtr hJob, int infoClass, IntPtr info, int cbInfo, IntPtr returnLength);

    /// <summary>How many processes are alive in the job right now, or -1 when it cannot be read.</summary>
    internal static int ActiveProcesses(IntPtr job)
    {
        if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out var info,
                                       Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero))
            return -1;
        return (int)info.ActiveProcesses;
    }

    /// <summary>The ids of the processes alive in the job right now.</summary>
    internal static List<int> ProcessIds(IntPtr job)
    {
        const int capacity = 1024;
        int size = 8 + IntPtr.Size * capacity;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, JobObjectBasicProcessIdList, buffer, size, IntPtr.Zero))
                return new List<int>();
            int count = Marshal.ReadInt32(buffer, 4);
            var ids = new List<int>(count);
            for (int i = 0; i < count; i++)
                ids.Add((int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size));
            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
