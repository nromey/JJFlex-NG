using System.Globalization;
using System.Runtime.InteropServices;

namespace JJFlex.DeskRun;

/// <summary>
/// Round 4 (task #906): a handle on every process seen in the job, held until
/// the end of the run, so each one's error mode can be read back while it
/// lives and its exit code read after it ends. Round 5: a REPORT, which
/// decides nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> On 2026-11-23 a test host crashed under deskrun and Windows put
/// a "dotnet.exe - Application Error" box on the operator's screen. The crash
/// itself is still a result: <c>dotnet test</c> reports an aborted run and
/// exits 1. But nothing in deskrun's own report said a process had ended
/// abnormally, or with what. This says it, with the code in words.
/// </para>
/// <para>
/// <b>Why it no longer changes the exit code (round 5).</b> Round 4 made an
/// exception-shaped exit seen here end an otherwise passing run at 75. Codex's
/// re-check found that promise could not be kept: a process born and gone
/// between two samples is never seen, so the same crash ended at 0 when it was
/// fast and at 75 when it was slow; and an ordinary exit with an
/// exception-shaped number read as a crash. The child's own exit code is what
/// fails a run whose child crashed. A grandchild that a passing test started,
/// waited on and checked is that test's business.
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
/// still in the job, so the job's limits applied to it all the same. Every read
/// that fails is reported as the failure it is (an exit code not read, a wait
/// that failed, a membership that could not be checked), never as a reading.
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
        public int? ExitReadError;  // it had ended, but GetExitCodeProcess failed (round 5, R3)
        public int? WaitError;      // waiting on its handle failed, so whether it had ended is unknown
    }

    private readonly IntPtr _job;
    private readonly string? _fault;
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly List<string> _unopened = new();

    /// <param name="job">The run's job.</param>
    /// <param name="fault">The launcher's <c>--inject-fault</c>; only <c>ledger-exit-read</c> is read here.</param>
    public JobLedger(IntPtr job, string? fault = null)
    {
        _job = job;
        _fault = fault;
    }

    /// <summary>Take a handle on a process just seen in the job's list.</summary>
    public void Observe(int pid, string name)
    {
        if (_entries.ContainsKey(pid)) return;
        string who = name + " " + pid.ToString(CultureInfo.InvariantCulture);
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.SYNCHRONIZE, false, pid);
        if (h == IntPtr.Zero)
        {
            _unopened.Add(who + " (could not open it: Windows error "
                          + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ")");
            return;
        }
        // The id was read from the job's list a moment ago; make sure the
        // process behind this handle is still that one, not a recycled id.
        if (!Native.IsProcessInJob(h, _job, out bool inJob))
        {
            _unopened.Add(who + " (could not check it was still the job's: Windows error "
                          + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ")");
            Native.CloseHandle(h);
            return;
        }
        if (!inJob)
        {
            // Measured, not failed: the id now belongs to a process outside
            // the job, so the one that was in it has already gone.
            Native.CloseHandle(h);
            return;
        }
        _entries[pid] = new Entry { Name = name, Handle = h };
        Sample(_entries[pid]);
    }

    /// <summary>Read the error mode of every live process, and the exit code of every one that has ended.</summary>
    public void Sample()
    {
        foreach (var e in _entries.Values) Sample(e);
    }

    private void Sample(Entry e)
    {
        if (e.ExitCode != null) return;
        uint wait = Native.WaitForSingleObject(e.Handle, 0);
        if (wait == Native.WAIT_OBJECT_0)
        {
            e.WaitError = null;
            // TEST-ONLY (round 5, R3): Codex's fault, a real failing call on a
            // null handle, so the failed-read branch is exercised live.
            IntPtr readFrom = _fault == "ledger-exit-read" ? IntPtr.Zero : e.Handle;
            if (Native.GetExitCodeProcess(readFrom, out uint code))
            {
                e.ExitCode = code;
                e.ExitReadError = null;
            }
            else
            {
                e.ExitReadError = Marshal.GetLastWin32Error();
            }
            return;
        }
        if (wait != Native.WAIT_TIMEOUT)
        {
            e.WaitError = Marshal.GetLastWin32Error();
            return;
        }
        e.WaitError = null;
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

    /// <summary>Every process seen that ended with an exception-shaped code: name, id, code. A report, not a verdict.</summary>
    public IReadOnlyList<(string Name, int Pid, uint Code)> EndedWithExceptionCode()
        => _entries.Where(kv => kv.Value.ExitCode is { } c && DeskRunRules.IsExceptionExitCode(c))
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
            lines.Add("seen in the job but not followed: " + string.Join("; ", _unopened)
                      + ". Its error mode and how it ended are unknown.");

        var exits = _entries.Select(kv => kv.Value.Name + " " + kv.Key.ToString(CultureInfo.InvariantCulture) + " "
                                          + DeskRunRules.DescribeLedgerExit(kv.Value.ExitCode, kv.Value.ExitReadError,
                                                                            kv.Value.WaitError));
        lines.Add(DeskRunRules.LedgerExitHeading + ": "
                  + (_entries.Count == 0 ? "none seen" : string.Join("; ", exits)) + ".");

        var exceptional = EndedWithExceptionCode();
        if (exceptional.Count > 0)
            lines.Add("ENDED WITH AN EXCEPTION CODE (seen by the sampler; an exit code that looks like an exception, "
                      + "not proof of a crash, and it does not change the run's exit code): "
                      + string.Join("; ", exceptional.Select(c => c.Name + " " + c.Pid.ToString(CultureInfo.InvariantCulture)
                                                                  + " ended with exception code "
                                                                  + DeskRunRules.DescribeExitCode(c.Code)))
                      + ".");
        int unread = _entries.Values.Count(e => e.ExitCode == null && (e.ExitReadError != null || e.WaitError != null));
        if (unread > 0)
            lines.Add("HOW " + unread + " SEEN PROCESS(ES) ENDED COULD NOT BE READ (see above).");
        return lines;
    }

    public void Dispose()
    {
        foreach (var e in _entries.Values) Native.CloseHandle(e.Handle);
        _entries.Clear();
    }
}
