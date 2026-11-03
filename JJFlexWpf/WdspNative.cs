using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JJFlexWpf
{
    /// <summary>
    /// The Windows calls the WDSP helper's host needs, and nothing else: a job
    /// object that kills the helper when JJ Flexible's last handle to it
    /// closes, a suspended, windowless start, and a pipe only this user can
    /// open. Process.Start cannot do the first two (it leaves a moment in which
    /// the child runs outside the job), so this is CreateProcess directly.
    /// </summary>
    internal static class WdspNative
    {
        internal const uint CREATE_SUSPENDED = 0x00000004;
        internal const uint CREATE_NO_WINDOW = 0x08000000;
        internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        internal const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
        private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint PIPE_TYPE_BYTE = 0x00000000;
        private const uint PIPE_READMODE_BYTE = 0x00000000;
        private const uint PIPE_WAIT = 0x00000000;
        private const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008;
        private const int STARTF_USESHOWWINDOW = 0x00000001;
        private const short SW_HIDE = 0;

        /// <summary>The job's limit flags, both of them required: see <see cref="CreateKillOnCloseJob"/>.</summary>
        internal const uint JobLimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;

        /// <summary>The creation flags the helper is always started with.</summary>
        internal const uint StartFlags = CREATE_SUSPENDED | CREATE_NO_WINDOW;

        // Start failures that mean the file was quarantined or refused, so the
        // helper is "missing" rather than broken, and is never retried by
        // itself. Numbers read from winerror.h in Windows SDK 10.0.26100.0
        // (2026-11-02): the reputation codes are NOT contiguous, 4556 to 4559
        // and then 4580 to 4582, which is why each is listed by name.
        internal const int ERROR_FILE_NOT_FOUND = 2;
        internal const int ERROR_PATH_NOT_FOUND = 3;
        internal const int ERROR_ACCESS_DENIED = 5;
        internal const int ERROR_VIRUS_INFECTED = 225;
        internal const int ERROR_VIRUS_DELETED = 226;
        internal const int ERROR_ACCESS_DISABLED_BY_POLICY = 1260;
        internal const int ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION = 4551;
        internal const int ERROR_SYSTEM_INTEGRITY_REPUTATION_MALICIOUS = 4556;
        internal const int ERROR_SYSTEM_INTEGRITY_REPUTATION_PUA = 4557;
        internal const int ERROR_SYSTEM_INTEGRITY_REPUTATION_DANGEROUS_EXT = 4558;
        internal const int ERROR_SYSTEM_INTEGRITY_REPUTATION_OFFLINE = 4559;
        internal const int ERROR_SYSTEM_INTEGRITY_REPUTATION_UNFRIENDLY_FILE = 4580;
        internal const int ERROR_SYSTEM_INTEGRITY_REPUTATION_UNATTAINABLE = 4581;
        internal const int ERROR_SYSTEM_INTEGRITY_REPUTATION_EXPLICIT_DENY_FILE = 4582;

        internal static bool IsBlocked(int error) => error is ERROR_ACCESS_DENIED or ERROR_VIRUS_INFECTED or ERROR_VIRUS_DELETED
            or ERROR_ACCESS_DISABLED_BY_POLICY or ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION
            or ERROR_SYSTEM_INTEGRITY_REPUTATION_MALICIOUS or ERROR_SYSTEM_INTEGRITY_REPUTATION_PUA
            or ERROR_SYSTEM_INTEGRITY_REPUTATION_DANGEROUS_EXT or ERROR_SYSTEM_INTEGRITY_REPUTATION_OFFLINE
            or ERROR_SYSTEM_INTEGRITY_REPUTATION_UNFRIENDLY_FILE or ERROR_SYSTEM_INTEGRITY_REPUTATION_UNATTAINABLE
            or ERROR_SYSTEM_INTEGRITY_REPUTATION_EXPLICIT_DENY_FILE;

        internal static bool IsNotThere(int error) => error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOW
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle hJob, int infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, SafeProcessHandle hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool result);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(SafeProcessHandle hProcess, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafePipeHandle CreateNamedPipeW(string lpName, uint dwOpenMode, uint dwPipeMode,
            uint nMaxInstances, uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut, ref SECURITY_ATTRIBUTES sa);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
            out IntPtr securityDescriptor, out uint size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        /// <summary>
        /// A job that kills every process in it when its last handle closes
        /// (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>), and that forces
        /// <c>SEM_NOGPFAULTERRORBOX</c> on them
        /// (<c>JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION</c>), so a crashing
        /// helper never puts an error dialog on the operator's screen. Its handle
        /// is not inheritable (null security attributes), so it can never reach
        /// the helper: if the helper held one, closing ours would not be the last
        /// close.
        /// </summary>
        internal static SafeFileHandle CreateKillOnCloseJob()
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JobLimitFlags;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                int error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error, "SetInformationJobObject failed");
            }
            return job;
        }

        /// <summary>The result of <see cref="StartInJob"/>.</summary>
        internal sealed class Started
        {
            public required SafeProcessHandle Process { get; init; }
            public required SafeFileHandle Job { get; init; }
            public required int ProcessId { get; init; }
        }

        /// <summary>Why <see cref="StartInJob"/> could not start the helper.</summary>
        internal sealed class StartFailure : Exception
        {
            public int Error { get; }
            public bool Blocked => IsBlocked(Error);
            public bool NotThere => IsNotThere(Error);
            public StartFailure(string what, int error) : base($"{what} failed: {new Win32Exception(error).Message} (error {error})") => Error = error;
        }

        /// <summary>
        /// Start <paramref name="path"/> suspended and with no window, assign it to
        /// a new kill-on-close job, then let it run. If the job cannot take it, the
        /// suspended child is terminated before it ever runs and the start fails:
        /// running a helper we cannot guarantee to kill is worse than not running it.
        /// No handle is inherited; the pipe name travels on the command line.
        /// </summary>
        internal static Started StartInJob(string path, string commandLine)
        {
            var job = CreateKillOnCloseJob();
            var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>(), dwFlags = STARTF_USESHOWWINDOW, wShowWindow = SW_HIDE };
            var line = new StringBuilder(commandLine);
            if (!CreateProcessW(path, line, IntPtr.Zero, IntPtr.Zero, false, StartFlags, IntPtr.Zero, null, ref si, out var pi))
            {
                int error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new StartFailure("CreateProcess", error);
            }
            var process = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            try
            {
                if (!AssignProcessToJobObject(job, process))
                {
                    int error = Marshal.GetLastWin32Error();
                    TerminateProcess(process, 1);
                    throw new StartFailure("AssignProcessToJobObject", error);
                }
                if (ResumeThread(pi.hThread) == uint.MaxValue)
                {
                    int error = Marshal.GetLastWin32Error();
                    TerminateProcess(process, 1);
                    throw new StartFailure("ResumeThread", error);
                }
            }
            catch
            {
                CloseHandle(pi.hThread);
                process.Dispose();
                job.Dispose();
                throw;
            }
            CloseHandle(pi.hThread);
            return new Started { Process = process, Job = job, ProcessId = pi.dwProcessId };
        }

        internal static void Terminate(SafeProcessHandle process, uint code)
        {
            if (!process.IsClosed && !process.IsInvalid) TerminateProcess(process, code);
        }

        internal static uint? ExitCode(SafeProcessHandle process)
        {
            if (process.IsClosed || process.IsInvalid || !GetExitCodeProcess(process, out uint code)) return null;
            return code == 259 ? null : code; // 259 is STILL_ACTIVE
        }

        internal static bool InJob(SafeProcessHandle process, SafeFileHandle job) =>
            IsProcessInJob(process, job, out bool inJob) && inJob;

        /// <summary>A new pipe name: <c>\\.\pipe\jjflex-wdsp-&lt;pid&gt;-&lt;32 lower-case hex&gt;</c>, random every start.</summary>
        internal static string NewPipeName(int parentPid) =>
            @"\\.\pipe\jjflex-wdsp-" + parentPid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        /// <summary>
        /// The pipe server: duplex, byte type, byte read mode, one instance only
        /// and the first one (so an impostor that made the name first makes this
        /// fail), remote clients refused, and an ACL naming the current user
        /// alone. Overlapped, so the reader and writer threads never serialise
        /// on one synchronous handle.
        /// </summary>
        internal static NamedPipeServerStream CreatePipeServer(string pipePath)
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) throw new InvalidOperationException("The current user has no SID.");
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;" + sid + ")", 1, out IntPtr sd, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Building the pipe's ACL failed");
            try
            {
                var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd, bInheritHandle = 0 };
                var handle = CreateNamedPipeW(pipePath,
                    PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
                    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                    1, 65536, 65536, 0, ref sa);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateNamedPipe failed");
                return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
            }
            finally { LocalFree(sd); }
        }

        internal static int? ClientProcessId(NamedPipeServerStream pipe) =>
            GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) ? (int)pid : null;

        /// <summary>
        /// The whole command line: the quoted path, then <c>--pipe &lt;name&gt;</c>,
        /// then any extra arguments (tests only; production passes none).
        /// </summary>
        internal static string CommandLine(string path, string pipeName, System.Collections.Generic.IReadOnlyList<string>? extra)
        {
            var sb = new StringBuilder();
            sb.Append('"').Append(path).Append("\" --pipe ").Append(pipeName);
            if (extra != null)
                foreach (var a in extra)
                    sb.Append(' ').Append(a.Contains(' ') ? "\"" + a + "\"" : a);
            return sb.ToString();
        }
    }
}
