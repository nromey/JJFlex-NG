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
/// </summary>
internal static class TraceLog
{
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JJFlexRadio");

    /// <summary>
    /// The trace file the running app is writing. Files are named
    /// JJFlexRadioTrace-yyyyMMdd-HHmmss.txt and a long session rotates into
    /// -1, -2 parts, so newest-by-write-time is the only reliable answer.
    /// </summary>
    public static string? FindCurrent()
    {
        try
        {
            var dir = new DirectoryInfo(AppDataDir);
            if (!dir.Exists) return null;
            return dir.GetFiles("JJFlexRadioTrace-*.txt")
                      .OrderByDescending(f => f.LastWriteTimeUtc)
                      .FirstOrDefault()?.FullName;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static long Length(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
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
