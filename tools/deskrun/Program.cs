using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace JJFlex.DeskRun;

/// <summary>
/// deskrun: run one test project's <c>dotnet test</c> on a desktop nobody can
/// see (task #906).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> <c>JJFlexWpf.Tests</c> builds real windows only when
/// its UI thread is on a private desktop. Under a plain <c>dotnet test</c> the
/// thread cannot get there: <c>SetThreadDesktop</c> fails with ERROR_BUSY (170)
/// because the CLR's OleInitialize has already given the thread a hidden
/// window. So the guard refuses every window-building class, correctly, and
/// those classes never ran on an agent's desk.
/// </para>
/// <para>
/// A desktop can be chosen for a whole process at birth, through
/// <c>STARTUPINFO.lpDesktop</c>, and every process it starts inherits it. That
/// is what this does: create a desktop, start <c>dotnet test</c> on it, relay
/// the output, and tear everything down afterwards. The guard in the test
/// process then reads, from Windows, that its thread is already on a desktop
/// which is not the one receiving input, and only then accepts.
/// </para>
/// <para>
/// <b>Nothing may be left running on a desktop nobody can see.</b> The child
/// goes into a job object before it runs a single instruction, so testhost (a
/// grandchild) is inside it too; the job kills everything when its handle
/// closes, including if this launcher is itself killed. Every process seen in
/// the job is recorded and checked gone at the end, and the desktop is checked
/// gone after its handle is closed.
/// </para>
/// </remarks>
internal static class Program
{
    private const int ExitUsage = 64;
    private const int ExitSetupFailed = 70;
    private const int ExitLeftSomethingBehind = 71;
    private const int ExitTimedOut = 124;

    private static volatile bool _cancelled;

    private static int Main(string[] args)
    {
        var parsed = DeskRunRules.Parse(args, File.Exists, out string error);
        if (parsed == null)
        {
            Say(error);
            Console.Error.WriteLine(Usage);
            return ExitUsage;
        }

        string? station = Native.ObjectName(Native.GetProcessWindowStation());
        if (!string.Equals(station, "WinSta0", StringComparison.OrdinalIgnoreCase))
        {
            Say("refused: this process is on window station '" + (station ?? "(unreadable)")
                + "', not WinSta0, the interactive console session. Nothing was started.");
            return ExitSetupFailed;
        }

        string? dotnet = FindDotnet();
        if (dotnet == null)
        {
            Say("refused: dotnet.exe was not found on PATH. Nothing was started.");
            return ExitSetupFailed;
        }

        IntPtr job = CreateKillOnCloseJob(out int jobError);
        if (job == IntPtr.Zero)
        {
            Say("refused: could not create the job object that guarantees cleanup (Windows error "
                + jobError + "). Nothing was started.");
            return ExitSetupFailed;
        }

        string name = DeskRunRules.NewDesktopName(Environment.ProcessId, Guid.NewGuid());
        string qualified = station + "\\" + name;

        IntPtr existing = Native.OpenDesktop(name, 0, false, Native.DESKTOP_READOBJECTS);
        if (existing != IntPtr.Zero)
        {
            Native.CloseDesktop(existing);
            Native.CloseHandle(job);
            Say("refused: a desktop named '" + name + "' already exists, and CreateDesktop would "
                + "silently open it rather than make a new one. Nothing was started.");
            return ExitSetupFailed;
        }

        IntPtr desktop = Native.CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, Native.GENERIC_ALL, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
        {
            int e = Marshal.GetLastWin32Error();
            Native.CloseHandle(job);
            Say("refused: CreateDesktop failed for '" + qualified + "' (Windows error " + e
                + "). Nothing was started.");
            return ExitSetupFailed;
        }

        Say("created hidden desktop " + qualified);

        var parentEnv = Environment.GetEnvironmentVariables();
        if (DeskRunRules.CarriesDeskFree(parentEnv))
            Say("removed " + DeskRunRules.DeskFreeVariable + " from the child's environment: "
                + "a run through deskrun is allowed by isolation or not at all.");
        var childEnv = DeskRunRules.ChildEnvironment(parentEnv, qualified);

        var dotnetArgs = DeskRunRules.DotnetArguments(parsed);
        string commandLine = DeskRunRules.CommandLine(dotnet, dotnetArgs);
        Say("starting: " + commandLine);
        Say("timeout: " + parsed.TimeoutSeconds + " s");

        int result = Run(job, desktop, name, qualified, commandLine, childEnv, parsed.TimeoutSeconds,
                         out bool finished);
        if (!finished)
        {
            // Nothing ran (or it was killed before running): still close what
            // was opened, so the failure paths leave no desktop behind either.
            Native.TerminateJobObject(job, 1);
            Native.CloseHandle(job);
            Native.CloseDesktop(desktop);
        }
        return result;
    }

    private static int Run(IntPtr job, IntPtr desktop, string name, string qualified, string commandLine,
                           SortedDictionary<string, string> childEnv, int timeoutSeconds,
                           out bool finished)
    {
        finished = false;
        var seen = new Dictionary<int, (string Name, DateTime Start)>();
        bool timedOut = false;
        int childExit = -1;
        Task relayOut = Task.CompletedTask, relayErr = Task.CompletedTask;
        var pi = default(Native.PROCESS_INFORMATION);
        IntPtr envBlock = IntPtr.Zero;

        using var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cancelled = true;
        };

        try
        {
            envBlock = Marshal.StringToHGlobalUni(DeskRunRules.EnvironmentBlock(childEnv));

            var si = new Native.STARTUPINFO
            {
                cb = Marshal.SizeOf<Native.STARTUPINFO>(),
                lpDesktop = qualified,
                dwFlags = Native.STARTF_USESTDHANDLES,
                hStdInput = stdin.ClientSafePipeHandle.DangerousGetHandle(),
                hStdOutput = stdout.ClientSafePipeHandle.DangerousGetHandle(),
                hStdError = stderr.ClientSafePipeHandle.DangerousGetHandle(),
            };

            char[] cmd = (commandLine + "\0").ToCharArray();
            bool created = Native.CreateProcess(
                null, cmd, IntPtr.Zero, IntPtr.Zero, true,
                Native.CREATE_SUSPENDED | Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_NO_WINDOW,
                envBlock, Environment.CurrentDirectory, ref si, out pi);
            int createError = Marshal.GetLastWin32Error();

            // The child holds its own copies now. Ours must go, or the pipes
            // never report end-of-file and the relay waits for ever.
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            stdin.DisposeLocalCopyOfClientHandle();
            stdin.Dispose(); // the child reads end-of-file at once

            if (!created)
            {
                Say("refused: CreateProcess failed (Windows error " + createError + ").");
                return ExitSetupFailed;
            }

            if (!Native.AssignProcessToJobObject(job, pi.hProcess))
            {
                int e = Marshal.GetLastWin32Error();
                Native.TerminateProcess(pi.hProcess, 1);
                Native.WaitForSingleObject(pi.hProcess, 10_000);
                Say("refused: could not put the child in the cleanup job (Windows error " + e
                    + "), so it was killed before it ran.");
                return ExitSetupFailed;
            }

            Native.ResumeThread(pi.hThread);
            Say("child process " + pi.dwProcessId + " started on " + qualified);

            relayOut = Relay(stdout, Console.Out);
            relayErr = Relay(stderr, Console.Error);

            var clock = Stopwatch.StartNew();
            while (true)
            {
                Record(job, seen);
                if (Native.WaitForSingleObject(pi.hProcess, 250) == Native.WAIT_OBJECT_0) break;

                if (_cancelled)
                {
                    Say("cancelled: killing the whole process tree.");
                    Native.TerminateJobObject(job, 1);
                    break;
                }
                if (clock.Elapsed.TotalSeconds >= timeoutSeconds)
                {
                    timedOut = true;
                    Say("TIMED OUT after " + timeoutSeconds + " s: killing the whole process tree.");
                    Native.TerminateJobObject(job, (uint)ExitTimedOut);
                    break;
                }
            }

            Native.WaitForSingleObject(pi.hProcess, 30_000);
            Native.GetExitCodeProcess(pi.hProcess, out uint code);
            childExit = unchecked((int)code);

            // The child is gone; anything it left behind is still in the job.
            // Give it a moment to finish on its own, then kill what remains.
            var grace = Stopwatch.StartNew();
            while (Native.ActiveProcesses(job) > 0 && grace.Elapsed < TimeSpan.FromSeconds(15))
            {
                Record(job, seen);
                Thread.Sleep(200);
            }

            int left = Native.ActiveProcesses(job);
            if (left != 0)
            {
                Say(left + " process(es) were still running in the job after the child exited: "
                    + Names(Native.ProcessIds(job), seen) + ". Killing them.");
                Native.TerminateJobObject(job, 1);
                var kill = Stopwatch.StartNew();
                while (Native.ActiveProcesses(job) != 0 && kill.Elapsed < TimeSpan.FromSeconds(15))
                    Thread.Sleep(100);
            }

            Task.WaitAll(new[] { relayOut, relayErr }, TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
            if (pi.hThread != IntPtr.Zero) Native.CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) Native.CloseHandle(pi.hProcess);
        }

        finished = true;
        return Finish(job, desktop, name, qualified, seen, timedOut, childExit);
    }

    /// <summary>
    /// Prove the tree and the desktop are gone, say so, and pick the exit code.
    /// </summary>
    private static int Finish(IntPtr job, IntPtr desktop, string name, string qualified,
                              Dictionary<int, (string Name, DateTime Start)> seen,
                              bool timedOut, int childExit)
    {
        int active = Native.ActiveProcesses(job);

        // Independent of the job's own count: every process ever seen in the
        // job, checked by id AND start time so a recycled id cannot pass for
        // one of ours.
        var survivors = new List<string>();
        foreach (var (pid, info) in seen)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited && p.StartTime == info.Start)
                    survivors.Add(info.Name + " (" + pid + ")");
            }
            catch (ArgumentException) { /* gone */ }
            catch (InvalidOperationException) { /* gone */ }
            catch (System.ComponentModel.Win32Exception) { /* cannot open: treat by job count */ }
        }

        // Closing the job handle kills anything that is somehow still in it.
        Native.CloseHandle(job);
        bool closed = Native.CloseDesktop(desktop);

        IntPtr reopen = Native.OpenDesktop(name, 0, false, Native.DESKTOP_READOBJECTS);
        bool desktopGone = reopen == IntPtr.Zero;
        if (!desktopGone) Native.CloseDesktop(reopen);

        Say("process tree: " + seen.Count + " process(es) ran in the job ("
            + Names(seen.Keys, seen) + "); active after the run: " + active
            + "; still running by id and start time: "
            + (survivors.Count == 0 ? "none" : string.Join(", ", survivors)) + ".");
        Say("desktop " + qualified + ": handle closed " + (closed ? "yes" : "NO")
            + ", " + (desktopGone ? "no longer exists" : "STILL EXISTS") + ".");

        if (active != 0 || survivors.Count != 0 || !desktopGone)
        {
            Say("FAILED to clean up completely; see above.");
            return ExitLeftSomethingBehind;
        }

        if (timedOut) return ExitTimedOut;
        Say("child exit code " + childExit + ".");
        return childExit;
    }

    private static void Record(IntPtr job, Dictionary<int, (string Name, DateTime Start)> seen)
    {
        foreach (int pid in Native.ProcessIds(job))
        {
            if (seen.ContainsKey(pid)) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                seen[pid] = (p.ProcessName, p.StartTime);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { seen[pid] = ("(unreadable)", DateTime.MinValue); }
        }
    }

    private static string Names(IEnumerable<int> pids, Dictionary<int, (string Name, DateTime Start)> seen)
        => string.Join(", ", pids.Select(p => (seen.TryGetValue(p, out var i) ? i.Name : "?") + " " + p));

    private static async Task Relay(Stream from, TextWriter to)
    {
        using var reader = new StreamReader(from);
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            lock (to) to.WriteLine(line);
        }
    }

    private static IntPtr CreateKillOnCloseJob(out int error)
    {
        error = 0;
        IntPtr job = Native.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            error = Marshal.GetLastWin32Error();
            return IntPtr.Zero;
        }

        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info,
                                            Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            error = Marshal.GetLastWin32Error();
            Native.CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    private static string? FindDotnet()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                string candidate = Path.Combine(dir.Trim().Trim('"'), "dotnet.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static void Say(string line)
    {
        lock (Console.Error) Console.Error.WriteLine("deskrun: " + line);
    }

    private const string Usage = """
        usage: deskrun [--timeout SECONDS] [--] PROJECT [dotnet test arguments...]

        Runs 'dotnet test PROJECT --no-build ...' on a newly created desktop that
        nobody can see, relays its output, and returns its exit code. Build the
        project first.

        Exactly one project is required; a solution is refused. The child never
        receives JJFLEX_TIER1_DESK_FREE. After SECONDS (default 600) the whole
        process tree is killed. Exit codes: the child's own, or 64 usage,
        70 nothing started, 71 something was left behind, 124 timed out.
        """;
}
