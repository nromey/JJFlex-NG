using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace JJFlex.DeskRun;

/// <summary>
/// The two permanent checks a run through deskrun carries while the child is
/// alive (task #906 round 2, Noel's ruling C9, conditions 1 and 2): no window
/// from the job on any desktop but the hidden one, and no sound from the job.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> Round 2 stopped the guard consulting the desk-free
/// declaration on the hidden-desktop route. Noel's condition for that was
/// "if it really works" and "if sound is not required": the windows must
/// really land on the hidden desktop with none reaching his screen, and the
/// run must really be silent. Those are measured here on every run, not
/// assumed from the guard's verdict, and both are reported at the end of every
/// run, including the clean ones.
/// </para>
/// <para>
/// <b>Windows.</b> Every 100 ms, every desktop in the window station is
/// enumerated (the input desktop, which is the operator's screen, labelled as
/// such) and every top-level window's owning process is compared against the
/// processes alive in the job at that moment. A job window anywhere but the
/// launcher's own desktop kills the job at once and the run exits 72. The
/// hidden desktop is enumerated by the SAME code, and the job windows found
/// there are counted and reported: that is the positive control that the
/// enumeration and the owner filter can see a test window at all.
/// </para>
/// <para>
/// <b>Sound.</b> About every 5 ms, every audio session on every active render
/// endpoint has its peak meter read; sessions are re-listed every 200 ms. Any
/// session owned by a process that was ever in the job, whose peak ever left
/// zero, kills the job and the run exits 73. A meter reports the peak of one
/// device period (about 10 ms) at a time, so sampling cannot see every period;
/// the achieved sampling rate is reported so a reader can judge the gap. The
/// watcher only READS meters: it never opens an audio stream.
/// </para>
/// </remarks>
internal sealed class Watch : IDisposable
{
    public const int ExitWindowEscaped = 72;
    public const int ExitAudibleFromTheJob = 73;

    private readonly IntPtr _job;
    private readonly string _hiddenName;
    private readonly ConcurrentDictionary<int, string> _everInJob = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _windows;
    private readonly Thread _audio;

    // ── window results ──
    private readonly object _gate = new();
    private int _windowSamples;
    private readonly Dictionary<IntPtr, string> _hiddenJobWindows = new();
    private readonly Dictionary<string, HashSet<IntPtr>> _otherDesktopWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<IntPtr>> _otherDesktopExplorer = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unreadableDesktops = new(StringComparer.OrdinalIgnoreCase);
    private string? _inputDesktopName;
    private string? _escapedOn;
    private int _inputUnreadableSamples;

    // ── audio results ──
    private int _audioSamples;
    private int _audioListings;
    private double _audioSeconds;
    private int _endpoints;
    private string? _audioFailure;
    private readonly Dictionary<string, SessionRecord> _sessions = new();

    /// <summary>Set once, by whichever watcher killed the job, with the exit code and the reason.</summary>
    public (int Code, string Reason)? Violation { get; private set; }

    private sealed class SessionRecord
    {
        public required string Endpoint;
        public required uint Pid;
        public required bool PidAmbiguous;
        public required bool SystemSounds;
        public string Process = "?";
        public bool InJob;
        public float MaxPeak;
        public bool EverActive;
    }

    public Watch(IntPtr job, string hiddenDesktopName)
    {
        _job = job;
        _hiddenName = hiddenDesktopName;
        _windows = new Thread(WindowLoop) { IsBackground = true, Name = "deskrun window watch" };
        _audio = new Thread(AudioLoop) { IsBackground = true, Name = "deskrun audio watch" };
        _audio.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        RefreshJob();
        _windows.Start();
        _audio.Start();
    }

    /// <summary>Stop sampling and wait for both watchers to finish their last pass.</summary>
    public void Stop()
    {
        _stop.Cancel();
        _windows.Join(TimeSpan.FromSeconds(10));
        _audio.Join(TimeSpan.FromSeconds(10));
    }

    public void Dispose() => _stop.Dispose();

    private HashSet<int> RefreshJob()
    {
        var now = new HashSet<int>(Native.ProcessIds(_job));
        foreach (int pid in now)
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
        return now;
    }

    private void Kill(int code, string reason)
    {
        lock (_gate)
        {
            if (Violation != null) return;
            Violation = (code, reason);
        }
        Native.TerminateJobObject(_job, (uint)code);
        Say(reason + " The whole job was killed at once.");
    }

    // ───────────────────────────── windows ─────────────────────────────

    private void WindowLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { SampleWindows(); }
            catch (Exception ex) { Say("window watch sample failed: " + ex.GetType().Name + ": " + ex.Message); }
            if (_stop.Token.WaitHandle.WaitOne(100)) break;
        }
        // One last look after the child has gone, before the desktop goes.
        try { SampleWindows(); } catch (Exception) { }
    }

    private void SampleWindows()
    {
        var jobNow = RefreshJob();
        IntPtr station = Native.GetProcessWindowStation();

        string? inputName = null;
        IntPtr input = OpenInputDesktop(0, false, DesktopRead);
        if (input != IntPtr.Zero)
        {
            inputName = Native.ObjectName(input);
            Native.CloseDesktop(input);
        }

        var names = new List<string>();
        EnumDesktops(station, (n, _) => { names.Add(n); return true; }, IntPtr.Zero);
        if (inputName != null && !names.Contains(inputName, StringComparer.OrdinalIgnoreCase))
            names.Add(inputName);

        lock (_gate)
        {
            _windowSamples++;
            if (inputName == null) _inputUnreadableSamples++;
            else _inputDesktopName = inputName;
        }

        foreach (string desktopName in names)
        {
            IntPtr desk = Native.OpenDesktop(desktopName, 0, false, DesktopRead);
            if (desk == IntPtr.Zero)
            {
                lock (_gate) _unreadableDesktops.Add(desktopName);
                continue;
            }

            try
            {
                var found = new List<IntPtr>();
                EnumDesktopWindows(desk, (h, _) => { found.Add(h); return true; }, IntPtr.Zero);

                foreach (IntPtr hwnd in found)
                {
                    GetWindowThreadProcessId(hwnd, out uint pidU);
                    int pid = (int)pidU;
                    bool ownedByJob = jobNow.Contains(pid);
                    var place = DeskRunRules.JudgeWindow(desktopName, _hiddenName, ownedByJob);

                    if (place == DeskRunRules.WindowPlace.Escaped)
                    {
                        bool isInput = inputName != null
                                       && string.Equals(desktopName, inputName, StringComparison.OrdinalIgnoreCase);
                        lock (_gate) _escapedOn ??= desktopName;
                        Kill(ExitWindowEscaped,
                             (isInput ? "A WINDOW REACHED THE OPERATOR'S DESKTOP" : "A WINDOW REACHED A DESKTOP DESKRUN DID NOT CREATE")
                             + ": desktop '" + desktopName + "'" + (isInput ? " (the input desktop)" : "")
                             + ", " + DescribeWindow(hwnd, pid) + ".");
                        return;
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
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var meters = new List<(string Key, IAudioMeterInformation Meter, IAudioSessionControl2 Control)>();
            var managers = new List<(string Endpoint, IAudioSessionManager2 Manager)>();
            var lastEndpoints = TimeSpan.FromDays(-1);
            var lastListing = TimeSpan.FromDays(-1);

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
                    RefreshJob();
                    meters = ListSessions(managers);
                    lastListing = clock.Elapsed;
                    lock (_gate) _audioListings++;
                }

                foreach (var (key, meter, control) in meters)
                {
                    float peak = 0;
                    if (meter.GetPeakValue(out float p) == 0) peak = p;
                    bool active = control.GetState(out int state) == 0 && state == 1;

                    SessionRecord rec;
                    lock (_gate)
                    {
                        rec = _sessions[key];
                        if (peak > rec.MaxPeak) rec.MaxPeak = peak;
                        if (active) rec.EverActive = true;
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
                if (stopping) break;
                Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            lock (_gate) _audioFailure = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            if (fineTimer) timeEndPeriod(1);
            lock (_gate) _audioSeconds = clock.Elapsed.TotalSeconds;
        }
    }

    private static List<(string, IAudioSessionManager2)> Endpoints(IMMDeviceEnumerator enumerator)
    {
        var list = new List<(string, IAudioSessionManager2)>();
        if (enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out var devices) != 0)
            return list;
        devices.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            if (devices.Item(i, out var device) != 0) continue;
            device.GetId(out string id);
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out object o) != 0) continue;
            list.Add((id, (IAudioSessionManager2)o));
        }
        return list;
    }

    private List<(string, IAudioMeterInformation, IAudioSessionControl2)> ListSessions(
        List<(string Endpoint, IAudioSessionManager2 Manager)> managers)
    {
        var meters = new List<(string, IAudioMeterInformation, IAudioSessionControl2)>();
        foreach (var (endpoint, manager) in managers)
        {
            if (manager.GetSessionEnumerator(out var sessions) != 0) continue;
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out object s) != 0) continue;
                var control = (IAudioSessionControl2)s;
                var meter = (IAudioMeterInformation)s;

                int pidResult = control.GetProcessId(out uint pid);
                bool ambiguous = pidResult == AUDCLNT_S_NO_SINGLE_PROCESS || pidResult != 0;
                bool system = control.IsSystemSoundsSession() == 0;
                control.GetSessionInstanceIdentifier(out string instance);
                string key = endpoint + "|" + instance;

                lock (_gate)
                {
                    if (!_sessions.TryGetValue(key, out var rec))
                    {
                        rec = new SessionRecord
                        {
                            Endpoint = endpoint,
                            Pid = pid,
                            PidAmbiguous = ambiguous,
                            SystemSounds = system,
                        };
                        _sessions[key] = rec;
                    }
                    // A session is the job's if its process was EVER in the
                    // job: a session outlives the process that opened it.
                    if (!ambiguous && pid != 0 && _everInJob.ContainsKey((int)pid))
                    {
                        rec.InJob = true;
                        rec.Process = _everInJob[(int)pid];
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

    private const int AUDCLNT_S_NO_SINGLE_PROCESS = 0x0889000D;

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
            lines.Add("window watch: " + _windowSamples + " sample(s). On the hidden desktop " + _hiddenName + ": "
                      + _hiddenJobWindows.Count + " distinct top-level window(s) owned by the job"
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
            if (_unreadableDesktops.Count > 0)
                lines.Add("window watch: could not open desktop(s) " + string.Join(", ", _unreadableDesktops)
                          + " to look (a job process could not have reached one without the same access).");
            if (_inputUnreadableSamples > 0)
                lines.Add("window watch: the input desktop could not be opened in " + _inputUnreadableSamples
                          + " sample(s) (the workstation may have been locked).");

            if (_audioFailure != null)
            {
                lines.Add("audio watch: FAILED, so this run's silence is UNMEASURED: " + _audioFailure);
            }
            else
            {
                double rate = _audioSeconds > 0 ? _audioSamples / _audioSeconds : 0;
                lines.Add("audio watch: " + _endpoints + " active render endpoint(s), " + _audioListings
                          + " session listing(s), " + _audioSamples + " meter pass(es) in "
                          + _audioSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                          + " s (about " + rate.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                          + " per second).");

                var job = _sessions.Values.Where(s => s.InJob).ToList();
                lines.Add("audio watch: sessions owned by the job: "
                          + (job.Count == 0
                              ? "none"
                              : string.Join("; ", job.Select(s => s.Process + " " + s.Pid + " max peak "
                                                                  + Peak(s.MaxPeak)
                                                                  + (s.EverActive ? ", was active" : ", never active")))) + ".");

                var system = _sessions.Values.Where(s => s.SystemSounds).ToList();
                if (system.Count > 0)
                    lines.Add("audio watch: system sounds session(s) during the run (not attributable to a process): max peak "
                              + Peak(system.Max(s => s.MaxPeak)) + ".");

                var others = _sessions.Values.Where(s => !s.InJob && !s.SystemSounds).ToList();
                lines.Add("audio watch: " + others.Count + " other session(s), loudest max peak "
                          + (others.Count == 0 ? "n/a" : Peak(others.Max(s => s.MaxPeak))
                                                         + " (" + others.OrderByDescending(s => s.MaxPeak).First().Process + ")")
                          + " (the meter reading a non-zero value here shows it can).");
            }

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
