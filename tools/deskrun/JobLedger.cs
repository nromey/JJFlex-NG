using System.Globalization;

namespace JJFlex.DeskRun;

/// <summary>
/// Round 4 (task #906): a handle on every process seen in the job, held until
/// the end of the run, so each one's error mode can be read back while it
/// lives and its exit code read after it ends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> On 2026-11-23 a test host crashed under deskrun and Windows put
/// a "dotnet.exe - Application Error" box on the operator's screen. The crash
/// itself is still a result: <c>dotnet test</c> reports an aborted run and
/// exits 1. But nothing in deskrun's own report said a process had crashed, or
/// with what. This says it, with the exception code, and makes sure a crash
/// can never end a run at 0 (<see cref="DeskRunRules.ExitJobProcessCrashed"/>).
/// </para>
/// <para>
/// <b>The error mode read back is evidence, not a watch.</b> It shows that the
/// error mode the launcher set on itself, and the one the job's
/// die-on-unhandled-exception limit forces, reached every process through
/// each hop (dotnet, then vstest, then testhost, then anything a test starts).
/// A reading that lacks <c>SEM_NOGPFAULTERRORBOX</c> is reported in capitals;
/// it does not by itself fail the run, because the crash-box watch is what
/// judges whether a box actually reached the screen.
/// </para>
/// <para>
/// <b>What it can miss.</b> A process is first seen when the job's process
/// list is sampled (every 250 ms or so). One that is born and dies between two
/// samples is never seen here, so its exit code is not in the report; it was
/// still in the job, so the job's limits applied to it all the same.
/// </para>
/// </remarks>
internal sealed class JobLedger : IDisposable
{
    private sealed class Entry
    {
        public required string Name;
        public required IntPtr Handle;
        public uint? ModeAnd;      // every reading ANDed: a flag missing once stays missing
        public uint? ModeOr;
        public int ModeReads;
        public int ModeReadsWithoutBoxesAway;  // readings that lacked SEM_NOGPFAULTERRORBOX or SEM_FAILCRITICALERRORS
        public uint? ModeLast;
        public int ModeReadFailures;
        public uint? ExitCode;
    }

    private readonly IntPtr _job;
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly List<string> _unopened = new();

    public JobLedger(IntPtr job) => _job = job;

    /// <summary>Take a handle on a process just seen in the job's list.</summary>
    public void Observe(int pid, string name)
    {
        if (_entries.ContainsKey(pid)) return;
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.SYNCHRONIZE, false, pid);
        if (h == IntPtr.Zero)
        {
            _unopened.Add(name + " " + pid.ToString(CultureInfo.InvariantCulture));
            return;
        }
        // The id was read from the job's list a moment ago; make sure the
        // process behind this handle is still that one, not a recycled id.
        if (!Native.IsProcessInJob(h, _job, out bool inJob) || !inJob)
        {
            Native.CloseHandle(h);
            return;
        }
        _entries[pid] = new Entry { Name = name, Handle = h };
        Sample(pid, _entries[pid]);
    }

    /// <summary>Read the error mode of every live process, and the exit code of every one that has ended.</summary>
    public void Sample()
    {
        foreach (var (pid, e) in _entries) Sample(pid, e);
    }

    private static void Sample(int pid, Entry e)
    {
        if (e.ExitCode != null) return;
        if (Native.WaitForSingleObject(e.Handle, 0) == Native.WAIT_OBJECT_0)
        {
            if (Native.GetExitCodeProcess(e.Handle, out uint code)) e.ExitCode = code;
            return;
        }
        uint? mode = Native.ErrorModeOf(e.Handle);
        if (mode == null)
        {
            e.ModeReadFailures++;
            return;
        }
        e.ModeReads++;
        e.ModeLast = mode.Value;
        if (!DeskRunRules.ErrorModeKeepsBoxesAway(mode.Value)) e.ModeReadsWithoutBoxesAway++;
        e.ModeAnd = (e.ModeAnd ?? uint.MaxValue) & mode.Value;
        e.ModeOr = (e.ModeOr ?? 0) | mode.Value;
    }

    /// <summary>Every process that exited with an exception code: name, id, code.</summary>
    public IReadOnlyList<(string Name, int Pid, uint Code)> Crashed()
        => _entries.Where(kv => kv.Value.ExitCode is { } c && DeskRunRules.IsCrashExitCode(c))
                   .Select(kv => (kv.Value.Name, kv.Key, kv.Value.ExitCode!.Value))
                   .ToList();

    public IEnumerable<string> Report()
    {
        Sample();
        var lines = new List<string>();

        var modes = _entries.Select(kv =>
        {
            var e = kv.Value;
            string reading = e.ModeAnd == null
                ? "NOT READ (" + e.ModeReadFailures + " failed read(s); it may have ended first)"
                : "0x" + e.ModeAnd.Value.ToString("X4", CultureInfo.InvariantCulture)
                  + (e.ModeOr != e.ModeAnd ? " to 0x" + e.ModeOr!.Value.ToString("X4", CultureInfo.InvariantCulture) : "")
                  + " (" + e.ModeReads + " read(s)"
                  + (e.ModeReadsWithoutBoxesAway == 0
                      ? ")"
                      : ", " + e.ModeReadsWithoutBoxesAway + " of them WITHOUT BOTH SEM_NOGPFAULTERRORBOX AND SEM_FAILCRITICALERRORS, "
                        + "last 0x" + e.ModeLast!.Value.ToString("X4", CultureInfo.InvariantCulture) + ")");
            return e.Name + " " + kv.Key.ToString(CultureInfo.InvariantCulture) + " " + reading;
        }).ToList();
        bool allHeld = _entries.Values.All(e => e.ModeAnd is { } m && DeskRunRules.ErrorModeKeepsBoxesAway(m));
        lines.Add("crash prevention: error mode read back from each job process: "
                  + (modes.Count == 0 ? "none seen" : string.Join("; ", modes)) + ". "
                  + (allHeld
                      ? "Every reading kept the fault box and the critical-error box away."
                      : "NOT EVERY READING KEPT THE BOXES AWAY (see above): a process in the job changed its own error mode, "
                        + "or was started with a default one. The job's die-on-unhandled-exception limit does not depend on "
                        + "it, and the crash-box watch is what judges whether a box appeared."));
        if (_unopened.Count > 0)
            lines.Add("crash prevention: could not open " + string.Join(", ", _unopened)
                      + " to read its error mode or exit code; it may have ended first.");

        var exits = _entries.Select(kv => kv.Value.Name + " " + kv.Key.ToString(CultureInfo.InvariantCulture) + " "
                                          + (kv.Value.ExitCode is { } c
                                              ? (DeskRunRules.IsCrashExitCode(c) ? "CRASHED, " + DeskRunRules.DescribeExitCode(c)
                                                 : DeskRunRules.IsErrorStatusExit(c) ? "ended with error status " + DeskRunRules.DescribeExitCode(c)
                                                 : c.ToString(CultureInfo.InvariantCulture))
                                              : "still running when read"));
        lines.Add("exit codes of the job's processes: " + (_entries.Count == 0 ? "none seen" : string.Join("; ", exits)) + ".");

        var crashed = Crashed();
        if (crashed.Count > 0)
            lines.Add("A PROCESS IN THE JOB CRASHED: "
                      + string.Join("; ", crashed.Select(c => c.Name + " " + c.Pid.ToString(CultureInfo.InvariantCulture)
                                                              + " exited with " + DeskRunRules.DescribeExitCode(c.Code)))
                      + ". The run does not end at 0.");
        return lines;
    }

    public void Dispose()
    {
        foreach (var e in _entries.Values) Native.CloseHandle(e.Handle);
        _entries.Clear();
    }
}
