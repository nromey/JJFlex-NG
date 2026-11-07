using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace JJFlex.DeskRun;

/// <summary>
/// The two permanent checks a run through deskrun carries while the child is
/// alive (task #906, Noel's ruling C9, conditions 1 and 2): no window from the
/// job on any desktop but the hidden one, and no sound from the job.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THESE ARE, HONESTLY (round 3): sampling tripwires on top of
/// structural facts, not exhaustive proofs.</b> The facts that actually keep
/// windows off the operator's screen are structural and hold between samples:
/// a thread's windows are created on that thread's own desktop; every process
/// in the job is born on the hidden desktop (<c>STARTUPINFO.lpDesktop</c>) and
/// inside the job (<c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>); and the job carries
/// <c>JOB_OBJECT_UILIMIT_DESKTOP</c>, so no job process can create a desktop
/// or switch the visible one. Sound is kept off structurally by the test
/// process's own <c>QuietRun</c> gate, which the guard requires. These watches
/// look, often, for the case where those facts did not hold, and end the run
/// when they see it.
/// </para>
/// <para>
/// <b>What a sample can miss, named rather than chased.</b> A job process can
/// still OPEN an existing desktop by name (<c>OpenDesktop</c>), put a thread on
/// it (<c>SetThreadDesktop</c>) and show a window there; one that lives for less
/// than a sample (about 100 ms) can come and go unseen, which Codex
/// demonstrated with a 5 ms window. A burst of sound shorter than a meter
/// sample, or from a session that opened and closed between two listings
/// (every 200 ms), can be missed too, as can sound from a process that joined
/// and left the job between two looks. The watches do not make those
/// impossible; they make anything longer than a sample fatal to the run.
/// </para>
/// <para>
/// <b>Every look must succeed (round 3, B4).</b> A watch that cannot measure
/// ends the run at <see cref="ExitCouldNotMeasure"/> (74), naming what failed:
/// the audio service, an endpoint or session listing, a peak meter, a desktop
/// listing or a desktop that exists and cannot be opened, a window enumeration,
/// the job's own process list, or the input desktop (the operator's screen)
/// when it cannot be opened. Round 2 printed "UNMEASURED" and returned the
/// child's success, and turned a failed meter read into zero, which reads
/// exactly like silence.
/// </para>
/// <para>
/// <b>Both watches finish one full pass before the child runs.</b>
/// <see cref="WaitForFirstPass"/> is called before <c>ResumeThread</c>; round 2
/// only started the threads, so the comment claiming the first look came first
/// was not true.
/// </para>
/// <para>
/// <b>Ownership is read when a window is seen.</b> A window or audio session
/// whose process is not in the pass's snapshot of the job is checked against
/// the job's own process list again at that moment, so a descendant born after
/// the snapshot is the job's for that same pass (round 3, B3).
/// </para>
/// <para>
/// <b>Windows.</b> Every 100 ms, every desktop in the window station is
/// enumerated (the input desktop, which is the operator's screen, labelled as
/// such) and every top-level window's owner is judged against the job. A job
/// window anywhere but the launcher's own desktop kills the job at once and the
/// run exits 72. The hidden desktop is enumerated by the SAME code, and the job
/// windows found there are counted and reported: the positive control that the
/// enumeration and the owner check can see a test window at all.
/// </para>
/// <para>
/// <b>Sound.</b> About every 6 ms every audio session on every active render
/// endpoint has its peak meter read; sessions are re-listed every 200 ms and
/// endpoints every 2 s. A session owned by a process that was ever in the job,
/// whose peak ever left zero, kills the job and the run exits 73. The watcher
/// only READS meters: it never opens an audio stream.
/// </para>
/// </remarks>
internal sealed class Watch : IDisposable
{
    public const int ExitWindowEscaped = DeskRunRules.ExitWindowEscaped;
    public const int ExitAudibleFromTheJob = DeskRunRules.ExitAudibleFromTheJob;
    public const int ExitCouldNotMeasure = DeskRunRules.ExitWatchCouldNotMeasure;

    private readonly IntPtr _job;
    private readonly string _hiddenName;
    private readonly string? _fault;
    private readonly ConcurrentDictionary<int, string> _everInJob = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _windows;
    private readonly Thread _audio;
    private readonly ManualResetEventSlim _windowsFirstPass = new(false);
    private readonly ManualResetEventSlim _audioFirstPass = new(false);

    // ── window results ──
    private readonly object _gate = new();
    private int _windowSamples;
    private int _ownershipRechecks;
    private int _ownershipRecheckHits;
    private readonly Dictionary<IntPtr, string> _hiddenJobWindows = new();
    private readonly Dictionary<string, HashSet<IntPtr>> _otherDesktopWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<IntPtr>> _otherDesktopExplorer = new(StringComparer.OrdinalIgnoreCase);
    private int _desktopsGoneBeforeOpen;
    private string? _inputDesktopName;
    private string? _escapedOn;

    // ── audio results ──
    private int _audioSamples;
    private int _audioListings;
    private double _audioSeconds;
    private int _endpoints;
    private readonly Dictionary<string, SessionRecord> _sessions = new();

    /// <summary>
    /// Set once, by whichever watcher killed the job, with the exit code and the
    /// reason: 72 a window escaped, 73 the job made sound, 74 a watch could not
    /// measure.
    /// </summary>
    public (int Code, string Reason)? Violation { get; private set; }

    private sealed class SessionRecord
    {
        public required string Endpoint;
        public required uint Pid;
        public required DeskRunRules.SessionOwner Owner;
        public required bool SystemSounds;
        public string Process = "?";
        public bool InJob;
        public float MaxPeak;
        public bool EverActive;
    }

    /// <summary>A look that could not be made. Caught once, turned into exit 74.</summary>
    private sealed class CouldNotMeasure : Exception
    {
        public CouldNotMeasure(string what) : base(what) { }
    }

    /// <param name="fault">
    /// Test-only (round 3): one of <see cref="DeskRunRules.InjectableFaults"/>,
    /// set only by the launcher's own <c>--inject-fault</c> option. Null in
    /// every ordinary run.
    /// </param>
    public Watch(IntPtr job, string hiddenDesktopName, string? fault)
    {
        _job = job;
        _hiddenName = hiddenDesktopName;
        _fault = fault;
        _windows = new Thread(WindowLoop) { IsBackground = true, Name = "deskrun window watch" };
        _audio = new Thread(AudioLoop) { IsBackground = true, Name = "deskrun audio watch" };
        _audio.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        _windows.Start();
        _audio.Start();
    }

    /// <summary>
    /// Block until BOTH watches have completed one full pass, so the child is
    /// resumed only after the first look (round 3, B3). False when either
    /// failed, saw a violation, or did not finish in time; the run is then
    /// already killed with the reason, and the child must not be resumed.
    /// </summary>
    public bool WaitForFirstPass(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        bool windows = _windowsFirstPass.Wait(timeout);
        var left = timeout - clock.Elapsed;
        bool audio = _audioFirstPass.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero);

        if (Violation != null) return false;
        if (!windows || !audio)
        {
            Fail("start-up", "the " + (!windows ? "window" : "audio") + " watch did not complete a first full pass within "
                             + timeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                             + " s, so the child was never resumed");
            return false;
        }
        return true;
    }

    /// <summary>Stop sampling and wait for both watchers to finish their last pass.</summary>
    public void Stop()
    {
        _stop.Cancel();
        bool windows = _windows.Join(TimeSpan.FromSeconds(10));
        bool audio = _audio.Join(TimeSpan.FromSeconds(10));
        if (!windows || !audio)
            Fail("shut-down", "the " + (!windows ? "window" : "audio")
                              + " watch did not finish its last pass, so the end of the run is unmeasured");
    }

    public void Dispose()
    {
        _stop.Dispose();
        _windowsFirstPass.Dispose();
        _audioFirstPass.Dispose();
    }

    /// <summary>
    /// The job's processes right now, or null when the list could not be read.
    /// Every id seen is remembered with its name, so a session can be
    /// attributed after its process has gone.
    /// </summary>
    private HashSet<int>? JobNow()
    {
        var ids = _fault == "job-list" ? null : Native.TryProcessIds(_job);
        if (ids == null) return null;
        foreach (int pid in ids)
        {
            if (_everInJob.ContainsKey(pid)) continue;
            string name = "?";
            try
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            _everInJob[pid] = name;
        }
        return new HashSet<int>(ids);
    }

    private void Kill(int code, string reason)
    {
        lock (_gate)
        {
            if (Violation != null) return;
            Violation = (code, reason);
        }
        bool killed = Native.TerminateJobObject(_job, (uint)code);
        Say(reason + (killed
            ? " The whole job was killed at once."
            : " TerminateJobObject failed (Windows error " + Marshal.GetLastWin32Error()
              + "); the job is killed when its handle closes at the end of the run."));
    }

    /// <summary>
    /// A watch could not measure: end the run at 74, naming what failed. The
    /// run is killed at once rather than allowed to continue unwatched.
    /// </summary>
    internal void Fail(string which, string what)
        => Kill(ExitCouldNotMeasure,
                "A WATCH COULD NOT MEASURE (" + which + "): " + what + ". The run is failed rather than "
                + "reported clean: an unmeasured window or sound is not an absent one.");

    // ───────────────────────────── windows ─────────────────────────────

    private void WindowLoop()
    {
        try
        {
            bool first = true;
            while (true)
            {
                // Read BEFORE the sample, so a stop requested during the wait
                // still gets one last look after the child has gone.
                bool stopping = _stop.IsCancellationRequested;
                if (!SampleWindows()) return;
                if (first)
                {
                    _windowsFirstPass.Set();
                    first = false;
                }
                if (stopping) return;
                _stop.Token.WaitHandle.WaitOne(100);
            }
        }
        catch (CouldNotMeasure ex)
        {
            Fail("window watch", ex.Message);
        }
        catch (Exception ex)
        {
            Fail("window watch", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            // Wakes WaitForFirstPass on failure too; Violation tells it why.
            _windowsFirstPass.Set();
        }
    }

    /// <summary>One full look at every desktop. False once a violation is recorded.</summary>
    private bool SampleWindows()
    {
        if (Violation != null) return false;

        var jobNow = JobNow() ?? throw new CouldNotMeasure("the job's own process list could not be read");
        // TEST-ONLY positive control (round 3, B3): start the pass from an
        // empty snapshot, as if every job process were born after it, so each
        // job window can only be attributed by the fresh read below.
        if (_fault == "stale-snapshot") jobNow = new HashSet<int>();
        var notTheJobs = new HashSet<int>();
        IntPtr station = Native.GetProcessWindowStation();

        IntPtr input = OpenInputDesktop(0, false, DesktopRead);
        if (input == IntPtr.Zero)
        {
            // The operator's screen is a desktop this user cannot open: the
            // workstation is locked, or a secure prompt is showing. It cannot
            // be looked at, so this run is unmeasured there. (The guard in the
            // test process refuses to start on a hidden desktop in the same
            // state, so this costs no run that could otherwise have started.)
            throw new CouldNotMeasure("the input desktop (the operator's screen) could not be opened, Windows error "
                                      + Marshal.GetLastWin32Error() + "; the workstation may be locked");
        }
        string? inputName;
        try { inputName = Native.ObjectName(input); }
        finally { Native.CloseDesktop(input); }
        if (inputName == null)
            throw new CouldNotMeasure("the input desktop's name could not be read");

        var names = new List<string>();
        bool listed = _fault != "window-enum" && EnumDesktops(station, (n, _) => { names.Add(n); return true; }, IntPtr.Zero);
        if (!listed)
            throw new CouldNotMeasure(_fault == "window-enum"
                ? "listing the window station's desktops failed (injected by --inject-fault window-enum)"
                : "listing the window station's desktops failed, Windows error " + Marshal.GetLastWin32Error());
        if (!names.Contains(_hiddenName, StringComparer.OrdinalIgnoreCase))
            throw new CouldNotMeasure("the desktop listing does not include deskrun's own hidden desktop '"
                                      + _hiddenName + "', so it cannot be trusted to include the others");
        if (!names.Contains(inputName, StringComparer.OrdinalIgnoreCase))
            names.Add(inputName);

        lock (_gate)
        {
            _windowSamples++;
            _inputDesktopName = inputName;
        }

        foreach (string desktopName in names)
        {
            IntPtr desk = Native.OpenDesktop(desktopName, 0, false, DesktopRead);
            int openError = desk == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            switch (DeskRunRules.JudgeDesktopOpen(desk != IntPtr.Zero, openError))
            {
                case DeskRunRules.DesktopOpen.Gone:
                    lock (_gate) _desktopsGoneBeforeOpen++;
                    continue;
                case DeskRunRules.DesktopOpen.CouldNotOpen:
                    throw new CouldNotMeasure("desktop '" + desktopName + "' exists and could not be opened to look, "
                                              + "Windows error " + openError);
            }

            try
            {
                var found = new List<IntPtr>();
                bool enumerated = EnumDesktopWindows(desk, (h, _) => { found.Add(h); return true; }, IntPtr.Zero);
                int enumError = enumerated ? 0 : Marshal.GetLastWin32Error();
                if (DeskRunRules.WindowEnumerationFailed(enumerated, enumError))
                    throw new CouldNotMeasure("enumerating the windows of desktop '" + desktopName
                                              + "' failed, Windows error " + enumError);

                foreach (IntPtr hwnd in found)
                {
                    GetWindowThreadProcessId(hwnd, out uint pidU);
                    int pid = (int)pidU;
                    if (pid == 0) continue; // destroyed since it was listed

                    bool? owned = notTheJobs.Contains(pid)
                        ? false
                        : DeskRunRules.OwnedByJob(pid, jobNow, () => FreshJob(jobNow));
                    if (owned == false) notTheJobs.Add(pid);
                    if (owned == null)
                        throw new CouldNotMeasure("the job's own process list could not be read to judge window 0x"
                                                  + hwnd.ToInt64().ToString("X"));
                    bool ownedByJob = owned.Value;
                    var place = DeskRunRules.JudgeWindow(desktopName, _hiddenName, ownedByJob);

                    if (place == DeskRunRules.WindowPlace.Escaped)
                    {
                        bool isInput = string.Equals(desktopName, inputName, StringComparison.OrdinalIgnoreCase);
                        lock (_gate) _escapedOn ??= desktopName;
                        Kill(ExitWindowEscaped,
                             (isInput ? "A WINDOW REACHED THE OPERATOR'S DESKTOP" : "A WINDOW REACHED A DESKTOP DESKRUN DID NOT CREATE")
                             + ": desktop '" + desktopName + "'" + (isInput ? " (the input desktop)" : "")
                             + ", " + DescribeWindow(hwnd, pid) + ".");
                        return false;
                    }

                    lock (_gate)
                    {
                        if (place == DeskRunRules.WindowPlace.OnTheHiddenDesktop)
                        {
                            if (!_hiddenJobWindows.ContainsKey(hwnd))
                                _hiddenJobWindows[hwnd] = DescribeWindow(hwnd, pid);
                        }
                        else if (!string.Equals(desktopName, _hiddenName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!_otherDesktopWindows.TryGetValue(desktopName, out var set))
                                _otherDesktopWindows[desktopName] = set = new HashSet<IntPtr>();
                            set.Add(hwnd);
                            if (ProcessNameOf(pid) == "explorer")
                            {
                                if (!_otherDesktopExplorer.TryGetValue(desktopName, out var ex))
                                    _otherDesktopExplorer[desktopName] = ex = new HashSet<IntPtr>();
                                ex.Add(hwnd);
                            }
                        }
                    }
                }
            }
            finally
            {
                Native.CloseDesktop(desk);
            }
        }

        return Violation == null;
    }

    /// <summary>
    /// The job's process list, read again NOW, for a window or session whose
    /// process was not in the pass's snapshot (round 3, B3). Any process found
    /// is added to the snapshot so its other windows are judged without
    /// another read. Null when the list cannot be read.
    /// </summary>
    /// <remarks>
    /// Read once per process id per pass, not once per window: a process that
    /// is not in the job when it is read cannot join it afterwards (only a NEW
    /// process can be born into the job, and it has a new id), so a second
    /// read for that process's next window in the same pass would say the
    /// same. Measured 2026-11-06: per window it was 18,316 reads in 5.5 s and
    /// the audio meter rate fell from about 150 to about 48 a second. The one
    /// case the cache cannot see is a process that exits during the pass and
    /// whose id is reused by a new job process within that same pass.
    /// </remarks>
    private IReadOnlyCollection<int>? FreshJob(HashSet<int> snapshot)
    {
        var fresh = JobNow();
        lock (_gate) _ownershipRechecks++;
        if (fresh == null) return null;
        foreach (int pid in fresh)
            if (snapshot.Add(pid))
                lock (_gate) _ownershipRecheckHits++;
        return fresh;
    }

    private readonly ConcurrentDictionary<int, string> _processNames = new();

    private string ProcessNameOf(int pid)
    {
        if (_everInJob.TryGetValue(pid, out var n)) return n;
        return _processNames.GetOrAdd(pid, p =>
        {
            try
            {
                using var proc = Process.GetProcessById(p);
                return proc.ProcessName;
            }
            catch (Exception) { return "?"; }
        });
    }

    private string DescribeWindow(IntPtr hwnd, int pid)
    {
        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        var title = new StringBuilder(256);
        InternalGetWindowText(hwnd, title, title.Capacity);
        return "window 0x" + hwnd.ToInt64().ToString("X") + " class '" + cls + "' title '" + title + "' "
               + (IsWindowVisible(hwnd) ? "visible" : "hidden") + ", process " + ProcessNameOf(pid) + " " + pid;
    }

    // ───────────────────────────── sound ─────────────────────────────

    private void AudioLoop()
    {
        var clock = Stopwatch.StartNew();
        // A 5 ms sleep is 15.6 ms without this. On Windows 11 the finer timer
        // applies to this process only, and it is given back when the run ends.
        bool fineTimer = timeBeginPeriod(1) == 0;
        try
        {
            if (_fault == "audio-init")
                throw new COMException("simulated audio service failure (injected by --inject-fault audio-init)",
                                       unchecked((int)0x80070426));
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var meters = new List<(string Key, IAudioMeterInformation Meter, IAudioSessionControl2 Control)>();
            var managers = new List<(string Endpoint, IAudioSessionManager2 Manager)>();
            var lastEndpoints = TimeSpan.FromDays(-1);
            var lastListing = TimeSpan.FromDays(-1);
            bool first = true;

            while (true)
            {
                bool stopping = _stop.IsCancellationRequested;

                if (clock.Elapsed - lastEndpoints > TimeSpan.FromSeconds(2))
                {
                    managers = Endpoints(enumerator);
                    lastEndpoints = clock.Elapsed;
                    lock (_gate) _endpoints = managers.Count;
                }

                if (clock.Elapsed - lastListing > TimeSpan.FromMilliseconds(200) || stopping)
                {
                    meters = ListSessions(managers);
                    lastListing = clock.Elapsed;
                    lock (_gate) _audioListings++;
                }

                foreach (var (key, meter, control) in meters)
                {
                    SessionRecord rec;
                    lock (_gate) rec = _sessions[key];

                    float raw = 0f;
                    bool injectPeak = _fault == "audio-peak" || (_fault == "audio-peak-late" && _audioSamples >= 150);
                    int hr = injectPeak ? unchecked((int)0x80004005) : meter.GetPeakValue(out raw);
                    float? peak = DeskRunRules.PeakReading(hr, hr == 0 ? raw : 0f);
                    if (peak == null)
                        throw new CouldNotMeasure("reading the peak meter of the session of process " + rec.Process + " "
                                                  + rec.Pid + " on " + rec.Endpoint + " failed (HRESULT 0x"
                                                  + hr.ToString("X8") + (hr == 0 ? ", value " + raw : "") + ")"
                                                  + (injectPeak ? ", injected by --inject-fault " + _fault : ""));

                    int stateHr = control.GetState(out int state);
                    string? stateFailed = DeskRunRules.CallFailed("IAudioSessionControl::GetState", stateHr);
                    if (stateFailed != null)
                        throw new CouldNotMeasure(stateFailed + " for the session of process " + rec.Process + " " + rec.Pid);

                    lock (_gate)
                    {
                        if (peak.Value > rec.MaxPeak) rec.MaxPeak = peak.Value;
                        if (state == 1) rec.EverActive = true;
                    }

                    if (DeskRunRules.AudibleFromTheJob(rec.InJob, rec.MaxPeak))
                    {
                        Kill(ExitAudibleFromTheJob,
                             "THE JOB MADE SOUND: process " + rec.Process + " " + rec.Pid + " rendered a peak of "
                             + rec.MaxPeak.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
                             + " on " + rec.Endpoint + ".");
                        return;
                    }
                }

                lock (_gate) _audioSamples++;
                if (first)
                {
                    _audioFirstPass.Set();
                    first = false;
                }
                if (stopping || Violation != null) break;
                Thread.Sleep(5);
            }
        }
        catch (CouldNotMeasure ex)
        {
            Fail("audio watch", ex.Message);
        }
        catch (Exception ex)
        {
            Fail("audio watch", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (fineTimer) timeEndPeriod(1);
            lock (_gate) _audioSeconds = clock.Elapsed.TotalSeconds;
            _audioFirstPass.Set();
        }
    }

    private static void Check(string call, int hresult)
    {
        string? failed = DeskRunRules.CallFailed(call, hresult);
        if (failed != null) throw new CouldNotMeasure(failed);
    }

    private List<(string, IAudioSessionManager2)> Endpoints(IMMDeviceEnumerator enumerator)
    {
        var list = new List<(string, IAudioSessionManager2)>();
        IMMDeviceCollection devices = null!;
        int hr = _fault == "audio-endpoints"
            ? unchecked((int)0x80004005)
            : enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out devices);
        Check("IMMDeviceEnumerator::EnumAudioEndpoints"
              + (_fault == "audio-endpoints" ? " (injected by --inject-fault audio-endpoints)" : ""), hr);
        Check("IMMDeviceCollection::GetCount", devices.GetCount(out uint count));
        for (uint i = 0; i < count; i++)
        {
            Check("IMMDeviceCollection::Item", devices.Item(i, out var device));
            Check("IMMDevice::GetId", device.GetId(out string id));
            var iid = typeof(IAudioSessionManager2).GUID;
            Check("IMMDevice::Activate(IAudioSessionManager2) for " + id,
                  device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out object o));
            list.Add((id, (IAudioSessionManager2)o));
        }
        return list;
    }

    private List<(string, IAudioMeterInformation, IAudioSessionControl2)> ListSessions(
        List<(string Endpoint, IAudioSessionManager2 Manager)> managers)
    {
        var snapshot = JobNow() ?? throw new CouldNotMeasure("the job's own process list could not be read");
        var notTheJobs = new HashSet<int>();
        var meters = new List<(string, IAudioMeterInformation, IAudioSessionControl2)>();
        foreach (var (endpoint, manager) in managers)
        {
            IAudioSessionEnumerator sessions = null!;
            int hr = _fault == "audio-sessions"
                ? unchecked((int)0x80004005)
                : manager.GetSessionEnumerator(out sessions);
            Check("IAudioSessionManager2::GetSessionEnumerator on " + endpoint
                  + (_fault == "audio-sessions" ? " (injected by --inject-fault audio-sessions)" : ""), hr);
            Check("IAudioSessionEnumerator::GetCount on " + endpoint, sessions.GetCount(out int count));
            for (int i = 0; i < count; i++)
            {
                Check("IAudioSessionEnumerator::GetSession on " + endpoint, sessions.GetSession(i, out object s));
                var control = (IAudioSessionControl2)s;
                var meter = (IAudioMeterInformation)s;

                var owner = DeskRunRules.JudgeSessionOwner(control.GetProcessId(out uint pid));
                if (owner == DeskRunRules.SessionOwner.Failed)
                    throw new CouldNotMeasure("IAudioSessionControl2::GetProcessId failed for a session on " + endpoint
                                              + ", so whether it is the job's is unmeasured");
                bool? system = DeskRunRules.JudgeSystemSounds(control.IsSystemSoundsSession());
                if (system == null)
                    throw new CouldNotMeasure("IAudioSessionControl2::IsSystemSoundsSession failed for a session on " + endpoint);
                Check("IAudioSessionControl2::GetSessionInstanceIdentifier on " + endpoint,
                      control.GetSessionInstanceIdentifier(out string instance));
                string key = endpoint + "|" + instance;

                // A session is the job's if its process was EVER in the job (a
                // session outlives the process that opened it), or is in the
                // job's own list read again now (round 3, B3).
                bool inJob = false;
                if (owner == DeskRunRules.SessionOwner.SingleProcess && pid != 0)
                {
                    if (_everInJob.ContainsKey((int)pid)) inJob = true;
                    else
                    {
                        bool? owned = notTheJobs.Contains((int)pid)
                            ? false
                            : DeskRunRules.OwnedByJob((int)pid, snapshot, () => FreshJob(snapshot));
                        if (owned == false) notTheJobs.Add((int)pid);
                        if (owned == null)
                            throw new CouldNotMeasure("the job's own process list could not be read to judge an audio session");
                        inJob = owned.Value;
                    }
                }

                lock (_gate)
                {
                    if (!_sessions.TryGetValue(key, out var rec))
                    {
                        rec = new SessionRecord
                        {
                            Endpoint = endpoint,
                            Pid = pid,
                            Owner = owner,
                            SystemSounds = system.Value,
                        };
                        _sessions[key] = rec;
                    }
                    if (inJob)
                    {
                        rec.InJob = true;
                        rec.Process = _everInJob.TryGetValue((int)pid, out var n) ? n : ProcessNameOf((int)pid);
                    }
                    else if (rec.Process == "?" && pid != 0)
                    {
                        rec.Process = ProcessNameOf((int)pid);
                    }
                }
                meters.Add((key, meter, control));
            }
        }
        return meters;
    }

    // ───────────────────────────── the report ─────────────────────────────

    /// <summary>The lines printed at the end of every run, clean or not.</summary>
    public IEnumerable<string> Report()
    {
        lock (_gate)
        {
            var lines = new List<string>();

            var classes = _hiddenJobWindows.Values
                .Select(d => ClassOf(d))
                .GroupBy(c => c, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key + " x" + g.Count());
            lines.Add("window watch: " + _windowSamples + " sample(s), a sampling tripwire (see the README). On the hidden desktop "
                      + _hiddenName + ": " + _hiddenJobWindows.Count + " distinct top-level window(s) owned by the job"
                      + (_hiddenJobWindows.Count == 0 ? "" : " (" + string.Join(", ", classes) + ")") + ".");
            var titled = _hiddenJobWindows.Values.Select(TitleOf).Where(t => t.Length > 0)
                                          .Distinct(StringComparer.Ordinal).Take(20).ToList();
            if (titled.Count > 0)
                lines.Add("window watch: titled job windows on the hidden desktop: '"
                          + string.Join("', '", titled) + "'.");

            foreach (var (desk, set) in _otherDesktopWindows.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                bool isInput = string.Equals(desk, _inputDesktopName, StringComparison.OrdinalIgnoreCase);
                int explorer = _otherDesktopExplorer.TryGetValue(desk, out var ex) ? ex.Count : 0;
                lines.Add("window watch: desktop '" + desk + "'" + (isInput ? " (the input desktop)" : "") + ": "
                          + set.Count + " distinct window(s) seen, " + explorer + " of them explorer's, "
                          + (string.Equals(desk, _escapedOn, StringComparison.OrdinalIgnoreCase) ? "AT LEAST ONE" : "0")
                          + " owned by the job.");
            }
            lines.Add("window watch: ownership re-read from the job's own process list " + _ownershipRechecks
                      + " time(s) for a window or session whose process was not in that pass's snapshot; "
                      + _ownershipRecheckHits + " process(es) found in the job that way.");
            if (_desktopsGoneBeforeOpen > 0)
                lines.Add("window watch: " + _desktopsGoneBeforeOpen + " listed desktop(s) no longer existed when "
                          + "opened (error 2), so held no windows.");

            double rate = _audioSeconds > 0 ? _audioSamples / _audioSeconds : 0;
            lines.Add("audio watch: " + _endpoints + " active render endpoint(s), " + _audioListings
                      + " session listing(s), " + _audioSamples + " meter pass(es) in "
                      + _audioSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                      + " s (about " + rate.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                      + " per second), a sampling tripwire (see the README).");

            var job = _sessions.Values.Where(s => s.InJob).ToList();
            lines.Add("audio watch: sessions owned by the job: "
                      + (job.Count == 0
                          ? "none"
                          : string.Join("; ", job.Select(s => s.Process + " " + s.Pid + " max peak "
                                                              + Peak(s.MaxPeak)
                                                              + (s.EverActive ? ", was active" : ", never active")))) + ".");

            var shared = _sessions.Values.Where(s => s.Owner == DeskRunRules.SessionOwner.SharedAcrossProcesses && !s.SystemSounds).ToList();
            if (shared.Count > 0)
                lines.Add("audio watch: " + shared.Count + " cross-process session(s), which cannot be attributed to one process: max peak "
                          + Peak(shared.Max(s => s.MaxPeak)) + ".");

            var system = _sessions.Values.Where(s => s.SystemSounds).ToList();
            if (system.Count > 0)
                lines.Add("audio watch: system sounds session(s) during the run (not attributable to a process): max peak "
                          + Peak(system.Max(s => s.MaxPeak)) + ".");

            var others = _sessions.Values.Where(s => !s.InJob && !s.SystemSounds
                                                     && s.Owner != DeskRunRules.SessionOwner.SharedAcrossProcesses).ToList();
            lines.Add("audio watch: " + others.Count + " other session(s), loudest max peak "
                      + (others.Count == 0 ? "n/a" : Peak(others.Max(s => s.MaxPeak))
                                                     + " (" + others.OrderByDescending(s => s.MaxPeak).First().Process + ")")
                      + " (the meter reading a non-zero value here shows it can).");
            // Each other process's loudest moment, because sound the job
            // CAUSES can be rendered by somebody else (a screen reader
            // speaking about a window, for one). That cannot be attributed
            // from here, but a reader can see whether it happened at all.
            if (others.Count > 0)
                lines.Add("audio watch: other sessions by process: "
                          + string.Join("; ", others.GroupBy(o => o.Process + " " + o.Pid)
                                                    .Select(g => g.Key + " max peak " + Peak(g.Max(x => x.MaxPeak))
                                                                 + (g.Any(x => x.EverActive) ? " (active)" : "")))
                          + ".");

            return lines;
        }
    }

    private static string Peak(float v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    private static string TitleOf(string description)
    {
        int a = description.IndexOf("' title '", StringComparison.Ordinal);
        if (a < 0) return "";
        a += 9;
        int b = description.LastIndexOf("' ", StringComparison.Ordinal);
        return b <= a ? "" : description.Substring(a, b - a);
    }

    private static string ClassOf(string description)
    {
        int a = description.IndexOf("class '", StringComparison.Ordinal);
        if (a < 0) return "?";
        a += 7;
        int b = description.IndexOf('\'', a);
        return b < 0 ? "?" : description.Substring(a, b - a);
    }

    private static void Say(string line)
    {
        lock (Console.Error) Console.Error.WriteLine("deskrun: " + line);
    }

    // ───────────────────────────── Win32 ─────────────────────────────

    private const uint DesktopRead = 0x0001 /* READOBJECTS */ | 0x0040 /* ENUMERATE */;

    private delegate bool EnumDesktopsProc([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDesktops(IntPtr hwinsta, EnumDesktopsProc proc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int InternalGetWindowText(IntPtr hWnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    // ───────────────────────────── Core Audio (read only) ─────────────────────────────

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out uint count);
        [PreserveSig] int GetChannelsPeakValues(uint count, [Out] float[] peaks);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
    }
}
