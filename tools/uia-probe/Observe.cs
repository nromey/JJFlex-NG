using System.IO;
using System.Text;
using System.Windows.Automation;

namespace JJFlex.UiaProbe;

/// <summary>What the outside world can see of the app at one instant.</summary>
internal sealed class Snapshot
{
    public required string CapturedAt { get; init; }
    public required List<string> Windows { get; init; }
    public required string ForegroundTitle { get; init; }
    public string FocusControlType { get; init; } = "";
    public string FocusName { get; init; } = "";
    public string FocusAutomationId { get; init; } = "";
    public string FocusClassName { get; init; } = "";
    public string FocusValue { get; init; } = "";
    public string FocusToggleState { get; init; } = "";
    public int TreeNodeCount { get; init; }
    public string TreeDigest { get; init; } = "";
    public long SpeechLogLength { get; init; }

    public string FocusIdentity =>
        $"{FocusControlType}|{FocusName}|{FocusAutomationId}|{FocusClassName}";
}

/// <summary>
/// The observation layer: everything the probe can honestly claim to have seen
/// happen as a result of a keystroke.
///
/// <para>There are four channels, and they are NOT equally useful for this
/// application. Focus moves, window open/close and tree changes cover ordinary
/// desktop software, but most of JJ Flexible's keys change nothing visible —
/// they SPEAK. Pressing M mutes the slice and says "muted"; there is no visual
/// state anywhere in the automation tree that says so. A harness limited to the
/// UIA channels would report the working half of this key map as dead.</para>
///
/// <para>So the fourth channel is the app's own trace file, where
/// <c>ScreenReaderOutput</c> logs every utterance as
/// <c>ScreenReaderOutput: Spoke '...'</c> at Verbose level. That makes speech
/// observable from outside the process without changing a line of app code. It
/// costs one precondition: the app must be running a detailed capture, because
/// the default trace level is Info and Verbose lines are dropped. The sweep
/// turns that on with Ctrl+J, Ctrl+D and verifies it took — which doubles as
/// the first key the sweep proves.</para>
/// </summary>
internal static class Observe
{
    // ─────────────────────────── snapshots ───────────────────────────

    public static Snapshot Capture(int pid, string? speechLogPath, bool digest = true)
    {
        var windows = Targets.Windows(pid);
        IntPtr fg = Native.GetForegroundWindow();

        string focusType = "", focusName = "", focusId = "", focusClass = "", focusValue = "", toggle = "";
        int nodes = 0;
        string treeDigest = "";

        try
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            if (focused != null && focused.Current.ProcessId == pid)
            {
                var cur = focused.Current;
                focusType = cur.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal);
                focusName = cur.Name ?? "";
                focusId = cur.AutomationId ?? "";
                focusClass = cur.ClassName ?? "";
                focusValue = TryValue(focused);
                toggle = TryToggle(focused);
            }
        }
        catch (ElementNotAvailableException) { focusName = "(focus element vanished)"; }
        catch (System.Runtime.InteropServices.COMException) { focusName = "(focus unreadable)"; }

        if (digest && fg != IntPtr.Zero)
        {
            (nodes, treeDigest) = Digest(fg);
        }

        return new Snapshot
        {
            CapturedAt = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Windows = windows.Where(w => w.Visible)
                             .Select(w => string.IsNullOrEmpty(w.Title) ? $"({w.ClassName})" : w.Title)
                             .OrderBy(t => t, StringComparer.Ordinal).ToList(),
            ForegroundTitle = Native.IsWindow(fg) ? Native.Text(fg) : "",
            FocusControlType = focusType,
            FocusName = focusName,
            FocusAutomationId = focusId,
            FocusClassName = focusClass,
            FocusValue = focusValue,
            FocusToggleState = toggle,
            TreeNodeCount = nodes,
            TreeDigest = treeDigest,
            SpeechLogLength = speechLogPath != null ? TraceLog.Length(speechLogPath) : 0,
        };
    }

    private static string TryValue(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object p))
                return ((ValuePattern)p).Current.Value ?? "";
        }
        catch (InvalidOperationException) { }
        catch (ElementNotAvailableException) { }
        return "";
    }

    private static string TryToggle(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out object p))
                return ((TogglePattern)p).Current.ToggleState.ToString();
        }
        catch (InvalidOperationException) { }
        catch (ElementNotAvailableException) { }
        return "";
    }

    /// <summary>
    /// A cheap, stable fingerprint of the foreground window's control view:
    /// enough to notice "something in the tree changed" without paying for a
    /// full dump between every keystroke. Capped, because a sweep that takes a
    /// second per key is a sweep nobody runs twice.
    /// </summary>
    private static (int nodes, string digest) Digest(IntPtr hwnd)
    {
        try
        {
            AutomationElement? root = AutomationElement.FromHandle(hwnd);
            if (root == null) return (0, "");

            ulong hash = 14695981039346656037UL;   // FNV-1a 64
            int count = 0;
            var stack = new Stack<AutomationElement>();
            stack.Push(root);

            while (stack.Count > 0 && count < 500)
            {
                AutomationElement e = stack.Pop();
                string line;
                try
                {
                    var c = e.Current;
                    line = $"{c.ControlType.ProgrammaticName}|{c.Name}|{c.AutomationId}|{c.IsOffscreen}";
                }
                catch (ElementNotAvailableException) { line = "(gone)"; }
                catch (System.Runtime.InteropServices.COMException) { line = "(unreadable)"; }

                foreach (char ch in line) { hash ^= ch; hash *= 1099511628211UL; }
                count++;

                try
                {
                    AutomationElement? child = TreeWalker.ControlViewWalker.GetFirstChild(e);
                    while (child != null)
                    {
                        stack.Push(child);
                        child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                    }
                }
                catch (ElementNotAvailableException) { }
                catch (System.Runtime.InteropServices.COMException) { }
            }
            return (count, hash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (ElementNotAvailableException) { return (0, ""); }
        catch (System.Runtime.InteropServices.COMException) { return (0, ""); }
    }

    // ─────────────────────────── what changed ───────────────────────────

    public static List<string> Diff(Snapshot before, Snapshot after)
    {
        var changes = new List<string>();

        var opened = after.Windows.Except(before.Windows, StringComparer.Ordinal).ToList();
        var closed = before.Windows.Except(after.Windows, StringComparer.Ordinal).ToList();
        foreach (string w in opened) changes.Add($"window opened: {w}");
        foreach (string w in closed) changes.Add($"window closed: {w}");

        if (!string.Equals(before.ForegroundTitle, after.ForegroundTitle, StringComparison.Ordinal))
            changes.Add($"foreground window: '{before.ForegroundTitle}' to '{after.ForegroundTitle}'");

        if (!string.Equals(before.FocusIdentity, after.FocusIdentity, StringComparison.Ordinal))
            changes.Add($"focus moved: '{Describe(before)}' to '{Describe(after)}'");

        if (!string.Equals(before.FocusValue, after.FocusValue, StringComparison.Ordinal))
            changes.Add($"focused value: '{before.FocusValue}' to '{after.FocusValue}'");

        if (!string.Equals(before.FocusToggleState, after.FocusToggleState, StringComparison.Ordinal))
            changes.Add($"toggle state: {before.FocusToggleState} to {after.FocusToggleState}");

        if (before.TreeDigest.Length > 0 && after.TreeDigest.Length > 0
            && !string.Equals(before.TreeDigest, after.TreeDigest, StringComparison.Ordinal))
            changes.Add($"automation tree changed ({before.TreeNodeCount} to {after.TreeNodeCount} nodes)");

        return changes;
    }

    private static string Describe(Snapshot s) =>
        s.FocusName.Length > 0 ? $"{s.FocusControlType} {s.FocusName}"
        : s.FocusControlType.Length > 0 ? s.FocusControlType
        : "(nothing focused in this process)";

    // ─────────────────────────── settling ───────────────────────────

    /// <summary>
    /// Block until the app stops reacting, then say so.
    ///
    /// <para>"Settled" means: no UIA event from the target process AND no new
    /// bytes in the speech log for <paramref name="quietMs"/> consecutive
    /// milliseconds. That definition is what makes this tool composable with a
    /// radio-side observer — Track C can only ask "did the radio do the thing?"
    /// after the app has finished doing it, and a fixed sleep either wastes the
    /// run or races it.</para>
    ///
    /// <para>Returns false when <paramref name="maxMs"/> ran out first. That is
    /// reported, never swallowed: a key that leaves the app churning is itself
    /// a finding.</para>
    /// </summary>
    public static bool WaitForSettle(int pid, string? speechLogPath, int quietMs, int maxMs, out int elapsedMs)
    {
        using var watcher = new ActivityWatcher(pid);
        return watcher.WaitForQuiet(speechLogPath, quietMs, maxMs, out elapsedMs);
    }
}

/// <summary>
/// Subscribes to the app's UI Automation events ONCE and reports when it goes
/// quiet.
///
/// <para>Separate from the wait itself because subscribing is expensive.
/// Adding and removing a global focus handler plus a subtree structure handler
/// costs real time per call, and a sweep that pressed 243 chords while
/// subscribing twice for each would spend more of the operator's run time
/// wiring up event handlers than pressing keys. One watcher lives for the whole
/// sweep; each press just reads the clock it keeps.</para>
/// </summary>
internal sealed class ActivityWatcher : IDisposable
{
    private readonly int _pid;
    private readonly AutomationFocusChangedEventHandler? _focus;
    private readonly StructureChangedEventHandler? _structure;
    private long _lastActivityTicks = Environment.TickCount64;
    private bool _disposed;

    public bool Subscribed { get; }

    public ActivityWatcher(int pid)
    {
        _pid = pid;

        _focus = (src, _) => Note(src);
        _structure = (src, _) => Note(src);

        try
        {
            Automation.AddAutomationFocusChangedEventHandler(_focus);
            Automation.AddStructureChangedEventHandler(
                AutomationElement.RootElement, TreeScope.Subtree, _structure);
            Subscribed = true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Fall back to the trace-file channel alone. Worth continuing:
            // for this app the log is the richer signal anyway.
            Subscribed = false;
        }
    }

    private void Note(object src)
    {
        try
        {
            if (src is AutomationElement el && el.Current.ProcessId != _pid) return;
        }
        catch (ElementNotAvailableException) { /* it went away, which is activity */ }
        catch (System.Runtime.InteropServices.COMException) { }
        Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);
    }

    public void Bump() => Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);

    /// <summary>
    /// Block until the app has been quiet for <paramref name="quietMs"/>, or
    /// give up at <paramref name="maxMs"/> and say so. Returning false is a
    /// finding, not an error: a key that leaves the app churning is worth
    /// knowing about.
    /// </summary>
    public bool WaitForQuiet(string? traceLogPath, int quietMs, int maxMs, out int elapsedMs)
    {
        long start = Environment.TickCount64;
        Bump();
        long lastLen = traceLogPath != null ? TraceLog.Length(traceLogPath) : 0;

        while (true)
        {
            Thread.Sleep(30);
            long now = Environment.TickCount64;

            if (traceLogPath != null)
            {
                long len = TraceLog.Length(traceLogPath);
                if (len != lastLen) { lastLen = len; Bump(); }
            }

            if (now - Volatile.Read(ref _lastActivityTicks) >= quietMs)
            {
                elapsedMs = (int)(now - start);
                return true;
            }
            if (now - start >= maxMs)
            {
                elapsedMs = (int)(now - start);
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!Subscribed) return;

        try { if (_focus != null) Automation.RemoveAutomationFocusChangedEventHandler(_focus); }
        catch (System.Runtime.InteropServices.COMException) { }
        try
        {
            if (_structure != null)
                Automation.RemoveStructureChangedEventHandler(AutomationElement.RootElement, _structure);
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}

/// <summary>
/// JJ Flexible's own trace file, read from outside. Two channels come out of
/// it and they answer different questions.
///
/// <para><b>Routing</b> — <c>DoCommand:</c> and <c>Leader:</c> lines, written
/// UNCONDITIONALLY at Info level, so no detailed capture is needed. Every
/// registry keystroke logs the key it resolved to, and a keystroke that reaches
/// the dispatcher and finds nothing logs <c>DoCommand:key not found:</c>. That
/// last line is the dead-binding signature in plain text: the 2026-08-13 Alt+L
/// failure would have written one on every press. This is the strongest signal
/// the probe has, because it separates "the chord never arrived" from "the
/// chord arrived and nothing was listening" — a distinction speech cannot make
/// and a human at the keyboard cannot hear.</para>
///
/// <para><b>Speech</b> — <c>ScreenReaderOutput: Spoke '...'</c>, at Verbose.
/// Needed because the Home field keys never reach DoCommand at all; they are
/// handled in FreqOutHandlers and their only outward effect is an utterance.
/// Verbose is NOT on by default: every trace file on this machine at Info
/// contains zero ScreenReaderOutput lines, so a detailed capture has to be
/// running for this channel to exist.</para>
///
/// <para><b>Whether that capture IS running is read, never inferred.</b> The
/// app stamps every log transition with a <c>CaptureState:</c> line (written in
/// globals.vb, TraceCaptureStateMarker — the two are a contract), and the last
/// such line in a file is the truth about that file. Inference was tried and it
/// destroyed evidence: on 2026-08-21 the sweep sniffed the last 64 KB for
/// utterances to decide whether a capture was on, found none because the
/// Verbose meter firehose had pushed all speech out of the window, and pressed
/// the toggle at a capture that was already running.</para>
/// </summary>
internal static class TraceLog
{
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JJFlexRadio");

    /// <summary>Whether a detailed capture is writing a file, as the file's own
    /// CaptureState line states it. Unknown means the build predates the line —
    /// judge by <see cref="LegacyStateFromHead"/> then, and say which evidence
    /// was used.</summary>
    internal enum CaptureState { Unknown, On, Off }

    /// <summary>What a trace file's session header says about who wrote it —
    /// and, when the build is new enough to write CaptureState lines, what
    /// state the log was last in.</summary>
    internal sealed record TraceHeader(string Path, int Instance, string AppDir, string Level,
        DateTime StartedAt, CaptureState Capture = CaptureState.Unknown);

    /// <summary>
    /// Find the trace file belonging to a SPECIFIC running app, by reading each
    /// candidate's own session header and matching the build directory.
    ///
    /// <para><b>Newest-by-write-time is wrong, and it was wrong in two ways.</b>
    /// Measured on 2026-08-20: three traces existed within half an hour, two
    /// written by Track G's build under jjflex-33g and one by this track's build
    /// under jjflex-33b. Picking the newest attached the probe to ANOTHER
    /// TRACK'S SESSION, and it then reported "no routing, no speech" for a key
    /// it was simply not watching the right log for. With several tracks
    /// launching the app on one machine that is not an edge case, it is the
    /// normal condition.
    /// </para>
    ///
    /// <para>Worse, the live file can look OLDER than a closed one: Windows does
    /// not reliably update a directory entry's LastWriteTime while a file is
    /// held open, so the log being actively written can sort below a session
    /// that finished twenty minutes earlier. That is not a bug sorting harder
    /// can fix.</para>
    ///
    /// <para>So match on the header the app writes about itself:
    /// <c>Boot Tracing on instance:N &lt;path to jjflexible.dll&gt; &lt;version&gt;
    /// &lt;date&gt; level=&lt;level&gt;</c>. It names the exact build, which is the
    /// question actually being asked, and it carries the trace level so a caller
    /// can tell whether speech will be visible before relying on it.</para>
    ///
    /// <para>Three filename shapes are searched, and two of them are corpses:
    /// only the fixed names JJFlexRadioTrace.txt / JJFlexRadio2Trace.txt are
    /// ever LIVE. The timestamped JJFlexRadioTrace-yyyyMMdd-HHmmss.txt files
    /// are the plain-text remains of FINISHED sessions (the app archives a
    /// session and renames its text aside; the stamp is that session's start,
    /// not a capture the operator is running) — got wrong on 2026-08-21, when
    /// a stamped corpse full of Verbose lines was read as a live capture.
    /// Corpses are still worth reading headers from, because their session
    /// start times lose to the live session's and their CaptureState seal says
    /// level=Off; what they must never be is attached to for observation.</para>
    /// </summary>
    public static TraceHeader? FindForApp(string appDir)
    {
        DateTime best = DateTime.MinValue;
        TraceHeader? chosen = null;
        string want = appDir.TrimEnd('\\', '/');

        foreach (TraceHeader h in AllHeaders())
        {
            if (!string.Equals(h.AppDir.TrimEnd('\\', '/'), want, StringComparison.OrdinalIgnoreCase))
                continue;
            // Latest SESSION START, not latest mtime — see the remarks above.
            if (h.StartedAt < best) continue;
            best = h.StartedAt;
            chosen = h;
        }
        return chosen;
    }

    /// <summary>Every trace file in the app data folder with a readable header.</summary>
    internal static List<TraceHeader> AllHeaders()
    {
        var found = new List<TraceHeader>();
        try
        {
            var dir = new DirectoryInfo(AppDataDir);
            if (!dir.Exists) return found;

            foreach (FileInfo f in dir.GetFiles("JJFlexRadio*Trace*.txt"))
            {
                TraceHeader? h = ReadHeader(f.FullName);
                if (h != null) found.Add(h);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return found;
    }

    private const string HeaderMarker = "Boot Tracing on instance:";
    private const string StateMarkerPrefix = "CaptureState: capture=";

    /// <summary>
    /// Read a file's LAST self-identification — that is the current state.
    ///
    /// <para>Two generations of line qualify. Every log transition since the
    /// 2026-08-21 fix writes a <c>CaptureState:</c> line carrying identity AND
    /// capture state; older builds wrote only the boot header, exactly once per
    /// launch, which is why their post-boot session files (captures, resumes)
    /// are anonymous and this method returns null for them. Whichever line
    /// appears LATER in the file wins — a mid-session detail change re-stamps,
    /// and the newest stamp is the truth.</para>
    ///
    /// <para>Reads the tail first and only falls back to the head, because these
    /// files get very large: a marathon session on 2026-08-07 left an 11.7 GB
    /// trace, and a probe that reads one whole is a probe nobody runs twice.</para>
    /// </summary>
    private static TraceHeader? ReadHeader(string path)
    {
        foreach (string chunk in ReadEnds(path))
        {
            int stateAt = chunk.LastIndexOf(StateMarkerPrefix, StringComparison.Ordinal);
            int bootAt = chunk.LastIndexOf(HeaderMarker, StringComparison.Ordinal);

            if (stateAt >= 0 && stateAt > bootAt)
            {
                TraceHeader? parsed = ParseStateMarker(path, LineFrom(chunk, stateAt));
                if (parsed != null) return parsed;
            }
            if (bootAt >= 0)
            {
                TraceHeader? parsed = ParseHeader(path, LineFrom(chunk, bootAt));
                if (parsed != null) return parsed;
            }
        }
        return null;
    }

    private static string LineFrom(string chunk, int at)
    {
        int lineEnd = chunk.IndexOf('\n', at);
        return lineEnd < 0 ? chunk[at..] : chunk[at..lineEnd];
    }

    private static IEnumerable<string> ReadEnds(string path)
    {
        const int Window = 512 * 1024;
        string? tail = null, head = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;

            // The tail window is read even when it overlaps the head: the last
            // CaptureState line in the file is the one that counts, and a file
            // between 64 KB and the window size used to fall through to a
            // head-only read that could hand back a stale earlier stamp.
            long tailStart = Math.Max(0, len - Window);
            fs.Seek(tailStart, SeekOrigin.Begin);
            var buf = new byte[(int)Math.Min(len, Window)];
            int n = fs.Read(buf, 0, buf.Length);
            tail = Encoding.UTF8.GetString(buf, 0, n);

            fs.Seek(0, SeekOrigin.Begin);
            var headBuf = new byte[(int)Math.Min(len, 64 * 1024)];
            int hn = fs.Read(headBuf, 0, headBuf.Length);
            head = Encoding.UTF8.GetString(headBuf, 0, hn);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (tail != null) yield return tail;
        if (head != null) yield return head;
    }

    /// <summary>
    /// Parse the app's CaptureState line. The format is the contract written by
    /// globals.vb's TraceCaptureStateMarker — change either side only in step
    /// with the other:
    /// <c>CaptureState: capture=on|off level=&lt;TraceLevel&gt; instance=&lt;N&gt;
    /// started=&lt;ISO 8601 UTC&gt; version=&lt;v&gt; app=&lt;path&gt; file=&lt;path&gt;</c>.
    /// Scalars come first; the two paths come last because paths contain
    /// spaces, so <c>app=</c> ends where the final <c>file=</c> begins. Returns
    /// null rather than guessing, for the same reason ParseHeader does.
    /// </summary>
    private static TraceHeader? ParseStateMarker(string path, string line)
    {
        int at = line.IndexOf(StateMarkerPrefix, StringComparison.Ordinal);
        if (at < 0) return null;
        string rest = line[(at + "CaptureState: ".Length)..].Trim();

        int appAt = rest.IndexOf(" app=", StringComparison.Ordinal);
        string scalars = appAt < 0 ? rest : rest[..appAt];

        string? Get(string key)
        {
            int k = scalars.IndexOf(key + "=", StringComparison.Ordinal);
            if (k < 0) return null;
            int start = k + key.Length + 1;
            int end = scalars.IndexOf(' ', start);
            return end < 0 ? scalars[start..] : scalars[start..end];
        }

        CaptureState capture = Get("capture") switch
        {
            "on" => CaptureState.On,
            "off" => CaptureState.Off,
            _ => CaptureState.Unknown,
        };
        if (capture == CaptureState.Unknown) return null;

        if (!int.TryParse(Get("instance"), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int instance)) return null;

        string level = Get("level") ?? "";

        if (appAt < 0) return null;
        int fileAt = rest.LastIndexOf(" file=", StringComparison.Ordinal);
        string appPath = (fileAt > appAt
            ? rest[(appAt + " app=".Length)..fileAt]
            : rest[(appAt + " app=".Length)..]).Trim();
        string appDir = System.IO.Path.GetDirectoryName(appPath) ?? "";
        if (appDir.Length == 0) return null;

        DateTime started = DateTime.MinValue;
        if (DateTime.TryParse(Get("started"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsedStart))
        {
            started = parsedStart.Kind == DateTimeKind.Unspecified ? parsedStart : parsedStart.ToLocalTime();
        }
        if (started == DateTime.MinValue)
        {
            try { started = File.GetLastWriteTime(path); } catch (IOException) { }
        }

        return new TraceHeader(path, instance, appDir, level, started, capture);
    }

    /// <summary>
    /// Pull instance, build directory, level and start time out of a header
    /// line. Returns null rather than guessing: a header this code cannot read
    /// means the format changed, and silently attaching to the wrong log is the
    /// exact failure being fixed here.
    /// </summary>
    private static TraceHeader? ParseHeader(string path, string line)
    {
        int at = line.IndexOf(HeaderMarker, StringComparison.Ordinal);
        if (at < 0) return null;
        string rest = line[(at + HeaderMarker.Length)..].Trim();

        int sp = rest.IndexOf(' ');
        if (sp < 0 || !int.TryParse(rest[..sp], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int instance)) return null;

        int dll = rest.IndexOf(".dll", StringComparison.OrdinalIgnoreCase);
        if (dll < 0) return null;
        string dllPath = rest[(sp + 1)..(dll + 4)].Trim();
        string appDir = System.IO.Path.GetDirectoryName(dllPath) ?? "";
        if (appDir.Length == 0) return null;

        string level = "";
        int lv = rest.IndexOf("level=", StringComparison.OrdinalIgnoreCase);
        if (lv >= 0) level = rest[(lv + "level=".Length)..].Trim();

        // The start time sits between the version and "level=".
        DateTime started = DateTime.MinValue;
        if (lv > dll + 4)
        {
            string middle = rest[(dll + 4)..lv].Trim();
            int firstSpace = middle.IndexOf(' ');
            if (firstSpace >= 0)
            {
                string stamp = middle[(firstSpace + 1)..].Trim();
                DateTime.TryParse(stamp, System.Globalization.CultureInfo.CurrentCulture,
                    System.Globalization.DateTimeStyles.None, out started);
            }
        }
        if (started == DateTime.MinValue)
        {
            try { started = File.GetLastWriteTime(path); } catch (IOException) { }
        }

        return new TraceHeader(path, instance, appDir, level, started);
    }

    /// <summary>
    /// The live log for a specific app instance. No searching and no sorting:
    /// the app writes instance 1 to JJFlexRadioTrace.txt and instance N to
    /// JJFlexRadioNTrace.txt, so the name is a pure function of the instance
    /// number, and the instance is in every header this class parses.
    /// </summary>
    public static string? LiveLogForInstance(int instance)
    {
        string name = instance > 1
            ? $"JJFlexRadio{instance}Trace.txt"
            : "JJFlexRadioTrace.txt";
        string path = Path.Combine(AppDataDir, name);
        try { return File.Exists(path) ? path : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Newest LIVE log — a file some app could still be writing. Only for
    /// commands with no process context; anything that knows its pid must go
    /// through <see cref="FindForApp"/> and <see cref="LiveLogForInstance"/>.
    ///
    /// <para>This replaced FindCurrent, which took the newest of EVERY trace
    /// file by write time. That glob included the stamped corpses of finished
    /// sessions, and a corpse can outrank the live file: Windows does not
    /// reliably update an open file's directory mtime, while a just-archived
    /// file's mtime is the moment it died. On 2026-08-21 that handed the sweep
    /// a dead capture full of Verbose lines seconds after the live capture was
    /// toggled, and the report claimed a speech channel it did not have. Only
    /// the fixed instance names can be live, so only they are considered.</para>
    /// </summary>
    public static string? FindLiveLog()
    {
        try
        {
            var dir = new DirectoryInfo(AppDataDir);
            if (!dir.Exists) return null;
            return dir.GetFiles("JJFlexRadio*Trace.txt")
                      .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                          f.Name, @"^JJFlexRadio\d*Trace\.txt$"))
                      .OrderByDescending(f => f.LastWriteTimeUtc)
                      .FirstOrDefault()?.FullName;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The first line of a file. For a trace log this is a session fingerprint:
    /// every session opens with either the boot header, "Detailed capture
    /// started ...", or "Diagnostic log resumed ...", each behind a
    /// ticks-since-launch prefix that differs between sessions — so "the head
    /// changed" is proof a new session replaced the old one under the same
    /// file name, which no mtime or length comparison can establish.
    /// </summary>
    public static string HeadLine(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[512];
            int n = fs.Read(buf, 0, buf.Length);
            string s = Encoding.UTF8.GetString(buf, 0, n);
            int nl = s.IndexOf('\n');
            return (nl < 0 ? s : s[..nl]).TrimEnd('\r');
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    /// <summary>
    /// Best-effort capture state for a build too old to write CaptureState
    /// lines, inferred from the prose line each session opens with. Weaker than
    /// the marker — it is inference, and any report that leans on it must say
    /// so — but the lines themselves are written unconditionally, so when the
    /// head names a transition it can be believed.
    /// </summary>
    public static (CaptureState State, bool Verbose) LegacyStateFromHead(string headLine)
    {
        if (headLine.Contains("Detailed capture started", StringComparison.Ordinal))
            return (CaptureState.On, true);   // a capture is always Verbose
        if (headLine.Contains("Diagnostic log resumed", StringComparison.Ordinal))
            return (CaptureState.Off, headLine.Contains("at Verbose", StringComparison.Ordinal));
        if (headLine.Contains(HeaderMarker, StringComparison.Ordinal))
            return (CaptureState.Off, headLine.Contains("level=Verbose", StringComparison.Ordinal));
        return (CaptureState.Unknown, false);
    }

    /// <summary>The level name at which speech becomes visible.</summary>
    public static bool LevelIsVerbose(string level) =>
        string.Equals(level, "Verbose", StringComparison.OrdinalIgnoreCase);

    public static long Length(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
    }

    // ── Session-scoped reading (task #170, fix B) ────────────────────────
    //
    // Byte-scoped tail windows are banned from this class's consumers now,
    // and the incident that banned them is worth keeping in full: on
    // 2026-08-21 `jjprobe trace` reported "routing channel: no DoCommand or
    // Leader lines yet (0 in the last 256 KB)" while a direct tail of the
    // SAME file returned four DoCommand lines. They sat 41–45 seconds into
    // the session; the log was already 562 KB; the firehose had pushed them
    // out of the window; and the probe concluded "Not worth sweeping yet" —
    // exactly backwards. A byte-scoped window is a time window whose duration
    // is set by the noisiest subsystem. Nobody chose 256 KB to mean "about
    // 40 seconds", but that is what it meant that morning.
    //
    // The replacement scope is the SESSION, which since the CaptureState work
    // is congruent with the FILE: every log transition (capture start/stop,
    // resume, settings change) archives the old session's file away and opens
    // a fresh one under the live name, so reading a live log from offset zero
    // IS reading from the start of the current session. Mid-session size
    // rotation can move early bytes into a part file, in which case offset
    // zero is still every session byte that remains under the live name.

    /// <summary>
    /// Lines of the current session, streamed lazily so a caller counting
    /// matches never holds a 50 MB session in memory. Whole session by
    /// default; a genuinely enormous file (the 2026-08-07 marathon left an
    /// 11.7 GB one) is capped by TIME, not bytes — the tick prefix every app
    /// line carries locates the tail window — and <c>Scope</c> says exactly
    /// which of the two a report is claiming.
    /// </summary>
    public static (IEnumerable<string> Lines, string Scope) SessionLines(string path)
    {
        // Generous on purpose: streaming 128 MB takes a moment and lies to
        // nobody. Only past this does the time cap start trimming, because a
        // multi-gigabyte linear scan makes the probe a tool nobody runs twice.
        const long wholeSessionCap = 128 * 1024 * 1024;
        TimeSpan window = TimeSpan.FromMinutes(30);

        long len = Length(path);
        if (len < 0) return (Enumerable.Empty<string>(), "this session (unreadable)");
        if (len <= wholeSessionCap) return (StreamSession(path, 0), "this session");

        long offset = SessionTailOffset(path, window);
        if (offset <= 0) return (StreamSession(path, 0), "this session");
        return (StreamSession(path, offset),
            $"in about the last {(int)window.TotalMinutes} minutes of a {len / (1024 * 1024)} MB session");
    }

    /// <summary>
    /// Stream a file's lines from <paramref name="fromOffset"/>, sharing the
    /// file so the live log can be read while the app holds it open — the
    /// same sharing every reader in this class uses, because File.ReadLines
    /// opens FileShare.Read and loses a race with the writer.
    /// </summary>
    public static IEnumerable<string> StreamSession(string path, long fromOffset)
    {
        FileStream? fs = null;
        StreamReader? reader = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fromOffset > 0 && fromOffset <= fs.Length) fs.Seek(fromOffset, SeekOrigin.Begin);
            reader = new StreamReader(fs, Encoding.UTF8);
        }
        catch (IOException) { reader?.Dispose(); fs?.Dispose(); yield break; }
        catch (UnauthorizedAccessException) { reader?.Dispose(); fs?.Dispose(); yield break; }

        using (reader)
        {
            while (true)
            {
                string? line = null;
                try { line = reader.ReadLine(); }
                catch (IOException) { }
                if (line == null) yield break;
                yield return line;
            }
        }
    }

    /// <summary>
    /// Byte offset where roughly the last <paramref name="window"/> of an
    /// enormous session begins, found by binary-searching the millisecond
    /// tick prefix the app stamps on every line. NOTE the prefix is
    /// milliseconds since the PROCESS started, not the session or the wall
    /// clock — a grep for HH:MM:SS matches nothing in these files, which is
    /// its own silent-absence trap and caught a session on 2026-08-21. Only
    /// differences between ticks mean anything, and differences are all this
    /// needs. Vendor lines (FlexLib's Debug.WriteLine output) carry no tick;
    /// the probe just reads past them to the next stamped line.
    /// </summary>
    internal static long SessionTailOffset(string path, TimeSpan window)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;

            long? lastTick = TickNear(fs, Math.Max(0, len - 64 * 1024), len, last: true);
            if (lastTick == null) return 0;
            long cutoff = lastTick.Value - (long)window.TotalMilliseconds;

            long? firstTick = TickNear(fs, 0, Math.Min(len, 64 * 1024), last: false);
            if (firstTick == null || firstTick.Value >= cutoff) return 0;

            long lo = 0, hi = len;
            while (hi - lo > 64 * 1024)
            {
                long mid = lo + (hi - lo) / 2;
                long? tick = TickNear(fs, mid, Math.Min(len, mid + 64 * 1024), last: false);
                if (tick == null) { lo = mid; continue; } // unstampable region: move forward
                if (tick.Value >= cutoff) hi = mid; else lo = mid;
            }
            return lo;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>First (or last) parseable tick prefix on a line boundary in
    /// [from, to) of an open stream, or null when the region has none.</summary>
    private static long? TickNear(FileStream fs, long from, long to, bool last)
    {
        int size = (int)Math.Min(to - from, 64 * 1024);
        if (size <= 0) return null;
        fs.Seek(from, SeekOrigin.Begin);
        var buf = new byte[size];
        int n = fs.Read(buf, 0, size);
        string chunk = Encoding.UTF8.GetString(buf, 0, n);

        long? found = null;
        int at = 0;
        if (from != 0)
        {
            // A mid-file probe almost always lands inside a line; the first
            // newline is where honest parsing can start.
            int firstNl = chunk.IndexOf('\n');
            if (firstNl < 0) return null;
            at = firstNl + 1;
        }
        while (at < chunk.Length)
        {
            int end = at;
            while (end < chunk.Length && chunk[end] >= '0' && chunk[end] <= '9') end++;
            if (end > at && end < chunk.Length && chunk[end] == ' '
                && long.TryParse(chunk[at..end], out long tick))
            {
                found = tick;
                if (!last) return found;
            }
            int next = chunk.IndexOf('\n', at);
            if (next < 0) break;
            at = next + 1;
        }
        return found;
    }

    /// <summary>Read everything appended since <paramref name="fromOffset"/>.</summary>
    public static List<string> ReadSince(string path, long fromOffset)
    {
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fromOffset > fs.Length) fromOffset = 0;   // rotated under us
            fs.Seek(fromOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null) lines.Add(line);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return lines;
    }

    private const string SpokeMarker = "ScreenReaderOutput: Spoke '";
    private const string OutputMarker = "ScreenReaderOutput: Output '";

    /// <summary>Pull the utterances out of raw trace lines.</summary>
    public static List<string> Utterances(IEnumerable<string> traceLines)
    {
        var said = new List<string>();
        foreach (string line in traceLines)
        {
            foreach (string marker in new[] { SpokeMarker, OutputMarker })
            {
                int at = line.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0) continue;
                int start = at + marker.Length;
                int end = line.LastIndexOf('\'');
                said.Add(end > start ? line[start..end] : line[start..]);
                break;
            }
        }
        return said;
    }

    /// <summary>
    /// Verbose lines prove the trace level is high enough for speech to appear.
    /// Without this check an entire sweep can report "no observable effect" for
    /// every key and be measuring nothing but its own misconfiguration.
    /// </summary>
    public static bool LooksVerbose(IEnumerable<string> traceLines) =>
        traceLines.Any(l => l.Contains("ScreenReaderOutput:", StringComparison.Ordinal));

    /// <summary>What the key dispatcher did with a keystroke.</summary>
    internal sealed record RoutingEvent(string Line, bool Unhandled);

    private static readonly string[] RoutingMarkers =
    {
        "DoCommand:", "Leader:", "DispatchFromDialogWindow:",
    };

    private static readonly string[] UnhandledMarkers =
    {
        "DoCommand:key not found:", "Leader:no command for ", "DoCommand:no rig setup",
    };

    /// <summary>
    /// Pull the routing decisions out of raw trace lines. Order is preserved:
    /// a chord that logs its key and then "key not found" tells a different
    /// story from one that logs nothing at all, and the sequence is the story.
    /// </summary>
    public static List<RoutingEvent> Routing(IEnumerable<string> traceLines)
    {
        var events = new List<RoutingEvent>();
        foreach (string line in traceLines)
        {
            int at = -1;
            foreach (string marker in RoutingMarkers)
            {
                at = line.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0) break;
            }
            if (at < 0) continue;

            string text = line[at..].Trim();
            bool unhandled = UnhandledMarkers.Any(m => text.StartsWith(m, StringComparison.Ordinal));
            events.Add(new RoutingEvent(text, unhandled));
        }
        return events;
    }
}
