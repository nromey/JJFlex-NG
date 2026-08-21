using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows.Automation;

namespace JJFlex.UiaProbe;

/// <summary>
/// jjprobe — drive and observe a running JJ Flexible from outside the process.
/// See README.md in this directory for the contract; --help prints the usage.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        // Whatever happens below — a crash, a Ctrl+C, an unhandled exception in
        // a UIA callback — modifiers come back up. A stuck Alt on a blind
        // operator's desktop is silent and makes every later keystroke wrong.
        //
        // The capture guard rides the same hooks for the same reason (#173): a
        // detailed capture the sweep turned on and walked away from is the same
        // category of mess as a stuck Alt, slower and measured in gigabytes —
        // roughly 1 MB a minute, and the operator's ran about 75 minutes on
        // 2026-08-21 before anyone noticed. Restore runs first because it may
        // press the toggle chord, then modifiers come up behind it.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            CaptureGuard.RestoreIfArmed(null);
            Native.ReleaseAllModifiers();
        };
        Console.CancelKeyPress += (_, _) =>
        {
            CaptureGuard.RestoreIfArmed(null);
            Native.ReleaseAllModifiers();
        };

        // Reports quote what the app said, and the app speaks in prose with
        // em dashes and curly quotes in it. On the console's default code page
        // those turn into mojibake and a verbatim quotation stops being one.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
        catch (IOException) { /* redirected to something that will not take it */ }

        try
        {
            return Dispatch(args);
        }
        catch (Exception ex)
        {
            Native.ReleaseAllModifiers();
            Console.Error.WriteLine("jjprobe: " + ex.Message);
            return 1;
        }
        finally
        {
            Native.ReleaseAllModifiers();
        }
    }

    private static int Dispatch(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Usage();

        string command = args[0].ToLowerInvariant();
        var opt = new Args(args.Skip(1));

        return command switch
        {
            "windows" => CmdWindows(opt),
            "tree" => CmdTree(opt),
            "focus" => CmdFocus(opt),
            "watch" => CmdWatch(opt),
            "press" => Armed(CmdPress, opt),
            "act" => CmdAct(opt),
            "inventory" => CmdInventory(opt),
            "unbound" => CmdUnbound(opt),
            "expand" => CmdExpand(opt),
            "altcheck" => CmdAltCheck(opt),
            "trace" => CmdTrace(opt),
            "sweep" => opt.Flag("dry-run") ? CmdSweep(opt) : Armed(CmdSweep, opt),
            _ => Usage($"unknown command '{command}'"),
        };
    }

    /// <summary>
    /// Run a command that is allowed to inject synthetic input.
    ///
    /// <para>Deliberately explicit rather than ambient. Everything else in this
    /// tool observes; only these two type. Marking the boundary in one place
    /// means the claim "the read-only commands cannot take your keyboard" is
    /// checkable by reading four lines instead of auditing the whole program.
    /// One codicil: <see cref="CaptureGuard"/> may press the capture toggle at
    /// exit, and it can only have been armed from inside an armed sweep — it is
    /// that sweep's cleanup, not a third typist.</para>
    /// </summary>
    private static int Armed(Func<Args, int> command, Args a)
    {
        Native.InjectionArmed = true;
        try { return command(a); }
        finally { Native.ReleaseAllModifiers(); }
    }

    private static int Usage(string? error = null)
    {
        if (error != null) Console.Error.WriteLine("jjprobe: " + error);
        Console.Error.WriteLine("""
jjprobe — drive and observe a running JJ Flexible from outside the process.

  jjprobe windows   [--pid N | --process NAME] [--all]
  jjprobe tree      [--pid N] [--window SEL] [--depth N] [--max-nodes N] [--out FILE]
  jjprobe focus     [--pid N]
  jjprobe watch     [--pid N] --seconds N [--out FILE]
  jjprobe press     [--pid N] [--window SEL] --chord "Ctrl+J, Ctrl+A"
                    [--quiet-ms 400] [--max-settle-ms 2500] [--json] [--no-digest]
                    [--risk safe|mutates|transmits] [--transmit-clearance FILE]
  jjprobe act       [--pid N] --op invoke|toggle|select|expand|focus|value|listitems
                    (--id ID | --name NAME | --class CLASS) [--index N] [--value V]
  jjprobe inventory [--pid N | --appdir DIR] [--json]
  jjprobe unbound   [--pid N | --appdir DIR] [--json]
  jjprobe expand    [--pid N | --appdir DIR]        (offline: no app driving)
  jjprobe altcheck  --src DIR                        (offline: static source scan)
  jjprobe trace     [--pid N] [--appdir DIR] [--all]
  jjprobe sweep     [--pid N] [--window SEL] [--appdir DIR] [--context NAME]
                    [--risk safe,mutates,transmits] [--max N] [--no-capture] [--digest]
                    [--transmit-clearance FILE] [--exclude "Comma,Period"] [--dry-run]
                    [--out FILE] [--json]

Default process name is 'jjflexible'. --window takes a title substring, a class
substring, or an index from `jjprobe windows`.

SAFETY. `press` and `sweep` type on the real desktop: the target window is
brought to the foreground first, so whatever the operator was doing loses focus.
Only chords classified safe are pressed unless --risk says otherwise, and a
chord that keys the transmitter ALSO needs --transmit-clearance: a JSON file,
written by something that can actually read the radio's power back, carrying
issuedUtc, ceilingWatts, measuredWatts and validForMs. Stale or over-ceiling
clearances are refused. A ceiling you set is a wish; a ceiling you read back
immediately before keying is a ceiling.

Exit codes: 0 ok · 1 error · 2 usage · 3 pressed but never settled ·
4 target window not found · 5 could not foreground the window ·
6 refused at the safety gate.
""");
        return error == null ? 0 : 2;
    }

    // ───────────────────────────── commands ─────────────────────────────

    private static int CmdWindows(Args a)
    {
        int pid = ResolvePid(a);
        var windows = Targets.Windows(pid, visibleOnly: !a.Flag("all"));
        Console.WriteLine($"pid {pid}: {windows.Count} top-level window(s)");
        for (int i = 0; i < windows.Count; i++)
        {
            WindowInfo w = windows[i];
            Console.WriteLine($"  [{i}] {w.HwndHex} class={w.ClassName} visible={w.Visible} "
                + $"foreground={w.Foreground} title=\"{w.Title}\" uiaName=\"{w.UiaName}\"");
        }
        return 0;
    }

    private static int CmdTree(Args a)
    {
        int pid = ResolvePid(a);
        WindowInfo? window = a.Has("window") ? Targets.Resolve(pid, a.Str("window")) : null;
        if (a.Has("window") && window == null) { Console.Error.WriteLine("no window matched"); return 4; }

        string dump = Tree.Dump(pid, window, a.Int("depth", 25), a.Int("max-nodes", 2000), out _, out int failures);
        Write(a, dump);
        return failures > 0 ? 0 : 0;
    }

    private static int CmdFocus(Args a)
    {
        int pid = ResolvePid(a);
        Snapshot s = Observe.Capture(pid, TraceLog.FindLiveLog(), digest: false);
        Console.WriteLine($"foreground window: \"{s.ForegroundTitle}\"");
        Console.WriteLine($"focused in pid {pid}: {s.FocusControlType} name=\"{s.FocusName}\" "
            + $"id=\"{s.FocusAutomationId}\" class={s.FocusClassName}");
        if (s.FocusValue.Length > 0) Console.WriteLine($"value: \"{s.FocusValue}\"");
        if (s.FocusToggleState.Length > 0) Console.WriteLine($"toggle: {s.FocusToggleState}");
        return 0;
    }

    private static int CmdWatch(Args a)
    {
        int pid = ResolvePid(a);
        int seconds = a.Int("seconds", 10);
        var log = new List<string>();

        AutomationFocusChangedEventHandler handler = (src, _) =>
        {
            try
            {
                if (src is not AutomationElement el) return;
                var c = el.Current;
                string tag = c.ProcessId == pid ? "FOCUS-IN-APP" : $"focus-elsewhere(pid {c.ProcessId})";
                lock (log)
                {
                    log.Add($"{DateTime.Now:HH:mm:ss.fff} {tag} "
                        + c.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal)
                        + $" name=\"{c.Name}\" id=\"{c.AutomationId}\" class={c.ClassName}");
                }
            }
            catch (ElementNotAvailableException)
            {
                lock (log) log.Add($"{DateTime.Now:HH:mm:ss.fff} focus event but the element had already gone");
            }
        };

        Automation.AddAutomationFocusChangedEventHandler(handler);
        Thread.Sleep(seconds * 1000);
        Automation.RemoveAutomationFocusChangedEventHandler(handler);

        lock (log)
        {
            if (log.Count == 0) log.Add($"(no focus events at all in {seconds} s)");
            Write(a, string.Join(Environment.NewLine, log));
        }
        return 0;
    }

    private static int CmdPress(Args a)
    {
        int pid = ResolvePid(a);
        string chordText = a.Str("chord") ?? throw new ArgumentException("--chord is required");
        if (!Chord.TryParse(chordText, out Chord chord, out string error))
        {
            Console.Error.WriteLine("jjprobe: " + error);
            return 2;
        }

        WindowInfo? window = Targets.Resolve(pid, a.Str("window"));
        if (window == null) { Console.Error.WriteLine("no visible window for that process"); return 4; }

        RiskLevel risk = a.Has("risk") ? Risk.Parse(a.Str("risk") ?? "safe") : RiskLevel.Safe;

        // Attach to THIS process's log, never to whichever file happens to be
        // newest — with several tracks running the app that is somebody else's
        // session. See TraceLog.FindForApp.
        string? appDir = a.Str("appdir") ?? Inventory.AppDirOf(pid);
        TraceLog.TraceHeader? header = appDir != null ? TraceLog.FindForApp(appDir) : null;
        if (header == null)
            Console.Error.WriteLine("jjprobe: no trace file found for that process — routing and speech "
                + "cannot be observed. Run `jjprobe trace --all` to see what is on disk.");

        // Observe the live file, not the file the header happens to sit in —
        // on pre-CaptureState builds those differ for every post-boot session,
        // and watching the finished one reports every press as silent.
        string? observeLog = header != null
            ? TraceLog.LiveLogForInstance(header.Instance) ?? header.Path
            : null;

        PressResult r = Press.Send(pid, chord, window, observeLog,
            a.Int("quiet-ms", 400), a.Int("max-settle-ms", 2500), digest: !a.Flag("no-digest"),
            risk, LoadClearance(a));

        if (a.Flag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(r, Json));
        }
        else
        {
            Console.WriteLine($"pressed {r.Chord} on \"{r.Window}\" ({r.WindowHandle})");
            Console.WriteLine($"settled after {r.SettleMs} ms, quiesced={r.Quiesced}");
            Console.WriteLine($"focus before: {r.FocusBefore}");
            Console.WriteLine($"focus after:  {r.FocusAfter}");
            foreach (string s in r.Spoke) Console.WriteLine($"  said:  \"{s}\"");
            foreach (string c in r.Routed) Console.WriteLine($"  route: {c}");
            foreach (string c in r.UiChanges) Console.WriteLine($"  saw:   {c}");
            Console.WriteLine($"verdict: {r.Verdict}{(r.Error != null ? " — " + r.Error : "")}");
        }

        if (r.Verdict == "not-sent") return 5;
        if (r.Verdict == "skipped") return 6;
        return r.Quiesced ? 0 : 3;
    }

    private static int CmdAct(Args a)
    {
        int pid = ResolvePid(a);
        Console.WriteLine(Act.Perform(pid, a.Str("op") ?? "invoke", a.Str("id"), a.Str("name"),
            a.Str("class"), a.Int("index", 0), a.Str("value")));
        return 0;
    }

    private static int CmdInventory(Args a)
    {
        var rows = Inventory.Load(ResolveAppDir(a));
        if (a.Flag("json")) { Console.WriteLine(JsonSerializer.Serialize(rows, Json)); return 0; }

        Console.WriteLine($"{rows.Count} fixed-key inventory entries.");
        foreach (var g in rows.GroupBy(r => r.ContextLabel))
        {
            Console.WriteLine();
            Console.WriteLine(g.Key + ":");
            foreach (InventoryEntry e in g) Console.WriteLine($"  {e.KeyDisplay} — {e.Description}");
        }
        return 0;
    }

    private static int CmdUnbound(Args a)
    {
        var rows = Inventory.LoadUnbound(ResolveAppDir(a));
        if (a.Flag("json")) { Console.WriteLine(JsonSerializer.Serialize(rows, Json)); return 0; }

        Console.WriteLine($"{rows.Count} registry commands ship with no key.");
        Console.WriteLine();
        foreach (var g in rows.GroupBy(r => r.Reason).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{g.Key} ({g.Count()}):");
            foreach (UnboundEntry e in g) Console.WriteLine($"  {e.Command} — {e.Detail}");
            Console.WriteLine();
        }

        int unassigned = rows.Count(r => r.Reason == "Unassigned");
        Console.WriteLine(unassigned == 0
            ? "None are Unassigned: every unbound command states a reason, so none of them is an accident."
            : $"{unassigned} are Unassigned — those are the ones nobody decided about.");
        return 0;
    }

    private static int CmdExpand(Args a)
    {
        var rows = Inventory.Load(ResolveAppDir(a));
        int total = 0, residue = 0;
        var lines = new List<string>();

        foreach (InventoryEntry e in rows)
        {
            Expansion x = KeyDisplayExpander.Expand(e);
            if (x.Residue != null)
            {
                residue++;
                lines.Add($"UNEXPANDABLE  {e.ContextLabel}: \"{e.KeyDisplay}\" — {x.Residue}");
                continue;
            }
            foreach (ExpandedChord c in x.ReservedElsewhere ?? Array.Empty<ExpandedChord>())
                lines.Add($"EXCLUDED      {c.Chord.Display}  [{e.Context}] — inside \"{e.KeyDisplay}\" but "
                    + "carved out by the inventory; it belongs to another command's row");
            foreach (ExpandedChord c in x.Chords)
            {
                total++;
                RiskLevel risk = Risk.Classify(c.Chord.Display, e.Description);
                lines.Add($"{c.Chord.Display}  [{e.Context}] [{c.Derivation}] [{risk}] — {e.Description}");
            }
        }

        Write(a, string.Join(Environment.NewLine, lines)
            + Environment.NewLine + Environment.NewLine
            + $"{rows.Count} inventory rows expand to {total} pressable chords; "
            + $"{residue} rows could not be expanded.");
        return 0;
    }

    private static int CmdAltCheck(Args a)
    {
        string src = a.Str("src") ?? Directory.GetCurrentDirectory();
        Write(a, AltAudit.Run(Path.GetFullPath(src)));
        return 0;
    }

    /// <summary>
    /// Say which log the probe would read, and why — the gate on whether a
    /// sweep is worth anyone's time.
    ///
    /// <para>Exists because the first real smoke test attached to another
    /// track's session and reported "no routing, no speech" for a key it was
    /// not watching the right file for. A sweep run in that state produces
    /// hundreds of confident, worthless rows. Now the question is answerable in
    /// one command, before the operator gives up their keyboard.</para>
    /// </summary>
    private static int CmdTrace(Args a)
    {
        if (a.Flag("all"))
        {
            var all = TraceLog.AllHeaders()
                .OrderByDescending(h => h.StartedAt)
                .ToList();
            Console.WriteLine($"{all.Count} trace file(s) with a readable session header, newest session first:");
            foreach (var h in all)
            {
                string captureNote = h.Capture switch
                {
                    TraceLog.CaptureState.On => ", capture ON",
                    TraceLog.CaptureState.Off => ", capture off",
                    _ => "",
                };
                Console.WriteLine($"  {Path.GetFileName(h.Path)} — instance {h.Instance}, level {h.Level}{captureNote}, "
                    + $"started {h.StartedAt:yyyy-MM-dd HH:mm:ss}, built from {h.AppDir}");
            }
            return 0;
        }

        string appDir = ResolveAppDir(a);
        Console.WriteLine($"build under test: {appDir}");

        TraceLog.TraceHeader? found = TraceLog.FindForApp(appDir);
        if (found == null)
        {
            Console.WriteLine("NO TRACE FILE for that build. Routing and speech are both unobservable, so a "
                + "sweep would report every key as silent regardless of whether it works.");
            Console.WriteLine("Run `jjprobe trace --all` to see whose sessions are on disk.");
            return 1;
        }

        Console.WriteLine($"attached to: {found.Path}");
        Console.WriteLine($"  instance {found.Instance}, session started {found.StartedAt:yyyy-MM-dd HH:mm:ss}, "
            + $"trace level {found.Level}");

        // Read from the file the app is WRITING, which the header search cannot
        // always name: builds that predate CaptureState lines leave post-boot
        // sessions anonymous, so the header can sit in a finished file while
        // the live one grows next to it under the fixed instance name.
        string readFrom = TraceLog.LiveLogForInstance(found.Instance) ?? found.Path;
        if (!string.Equals(readFrom, found.Path, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"  live log: {readFrom} (the header above is in a finished file; "
                + "channels below are read from the live one)");

        // Capture state: the marker if this build writes one, the prose line
        // each session opens with if not, and honesty about which was used.
        string head = TraceLog.HeadLine(readFrom);
        (TraceLog.CaptureState legacyState, bool legacyVerbose) = TraceLog.LegacyStateFromHead(head);
        // A marker-era header describes the current SESSION even when rotation
        // has moved the live bytes to a fresh file, so it outranks head-line
        // inference whenever it exists at all.
        bool markerKnown = found.Capture != TraceLog.CaptureState.Unknown;
        TraceLog.CaptureState captureState = markerKnown ? found.Capture : legacyState;
        bool speechLive = captureState == TraceLog.CaptureState.On
            || (markerKnown ? TraceLog.LevelIsVerbose(found.Level) : legacyVerbose);

        Console.WriteLine($"  capture state:   " + (captureState, markerKnown) switch
        {
            (TraceLog.CaptureState.On, true) => "detailed capture RUNNING (the log's CaptureState line says so)",
            (TraceLog.CaptureState.On, false) => "detailed capture RUNNING (inferred from the session's opening "
                + "line — this build predates CaptureState lines)",
            (TraceLog.CaptureState.Off, true) => "no capture (the log's CaptureState line says so)",
            (TraceLog.CaptureState.Off, false) => "no capture (inferred from the session's opening line)",
            _ => "UNKNOWN — neither a CaptureState line nor a recognisable session opening",
        });

        // Session-scoped, not byte-scoped. The old read took the last 256 KB
        // and on 2026-08-21 answered "0" for a session whose DoCommand lines
        // sat 45 seconds in — the firehose had pushed them out of the window,
        // and the probe told the operator the opposite of the truth.
        var (sessionLines, scope) = TraceLog.SessionLines(readFrom);
        int routing = TraceLog.Routing(sessionLines).Count;

        Console.WriteLine($"  routing channel: {(routing > 0 ? "READABLE" : "no DoCommand or Leader lines yet")} "
            + $"({routing} {scope})");
        Console.WriteLine($"  speech channel:  {(speechLive ? "LIVE" : "NOT LIVE — needs a detailed capture (Ctrl+J, Ctrl+D)")}");
        Console.WriteLine(speechLive || routing > 0
            ? "Good enough to sweep."
            : "Not worth sweeping yet: neither channel would show anything.");
        return 0;
    }

    private static int CmdSweep(Args a)
    {
        // A dry run needs no running app when --appdir names a build, which is
        // the point: the plan can be reviewed and authorised before anything is
        // started, let alone pressed.
        bool dryRun = a.Flag("dry-run");
        var o = new SweepOptions
        {
            Pid = dryRun && a.Str("appdir") != null ? 0 : ResolvePid(a),
            WindowSelector = a.Str("window"),
            AppDir = a.Str("appdir"),
            ContextFilter = a.Str("context"),
            QuietMs = a.Int("quiet-ms", 400),
            MaxSettleMs = a.Int("max-settle-ms", 2500),
            BetweenKeysMs = a.Int("between-ms", 150),
            StartCapture = !a.Flag("no-capture"),
            Digest = a.Flag("digest"),
            MaxKeys = a.Int("max", int.MaxValue),
            Clearance = LoadClearance(a),
        };
        if (a.Has("exclude"))
            o.Exclude = (a.Str("exclude") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (a.Has("risk"))
            o.AllowedRisk = (a.Str("risk") ?? "safe")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Risk.Parse).ToHashSet();

        if (dryRun)
        {
            Write(a, Sweep.DryRun(o, a.Str("appdir") is string d ? Path.GetFullPath(d) : ResolveAppDir(a)));
            return 0;
        }

        SweepReport report = Sweep.Run(o);
        string text = a.Flag("json") ? Sweep.ToJson(report) : Sweep.ToText(report);
        Write(a, text);

        // Always leave the machine-readable copy beside the text one: the JSON
        // is what a later comparison run diffs against.
        if (a.Str("out") is string outPath && !a.Flag("json"))
            File.WriteAllText(Path.ChangeExtension(outPath, ".json"), Sweep.ToJson(report));

        return report.AbortedBecause != null ? 1 : 0;
    }

    // ───────────────────────────── plumbing ─────────────────────────────

    private static int ResolvePid(Args a)
    {
        if (a.Has("pid")) return a.Int("pid", 0);

        string name = a.Str("process") ?? Targets.DefaultProcessName;
        int[] pids = Targets.FindPids(name);
        if (pids.Length == 0)
            throw new InvalidOperationException(
                $"no process named '{name}' is running. Start JJ Flexible, or pass --pid.");
        if (pids.Length > 1)
            Console.Error.WriteLine($"jjprobe: {pids.Length} '{name}' processes; using pid {pids[0]}. "
                + "Pass --pid to choose.");
        return pids[0];
    }

    /// <summary>
    /// Read the transmit clearance, if one was offered. Deliberately a FILE
    /// rather than a flag: the probe cannot see the radio, so the only honest
    /// clearance is one written by something that can, and a file carries a
    /// timestamp a flag cannot.
    /// </summary>
    private static TransmitClearance? LoadClearance(Args a)
    {
        if (a.Str("transmit-clearance") is not string path) return null;
        if (!File.Exists(path))
            throw new FileNotFoundException($"transmit clearance file not found: {path}", path);
        return JsonSerializer.Deserialize<TransmitClearance>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"transmit clearance file {path} did not parse");
    }

    private static string ResolveAppDir(Args a)
    {
        if (a.Str("appdir") is string dir) return Path.GetFullPath(dir);
        int pid = ResolvePid(a);
        return Inventory.AppDirOf(pid)
            ?? throw new InvalidOperationException("could not read that process's directory — pass --appdir");
    }

    private static void Write(Args a, string text)
    {
        if (a.Str("out") is string path)
        {
            File.WriteAllText(path, text);
            Console.WriteLine($"wrote {path} ({text.Length} chars)");
        }
        else
        {
            Console.WriteLine(text);
        }
    }

    /// <summary>Minimal --name value / --flag parsing. No dependency, no surprises.</summary>
    private sealed class Args
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public Args(IEnumerable<string> args)
        {
            string? pending = null;
            foreach (string arg in args)
            {
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    if (pending != null) _values[pending] = null;
                    pending = arg[2..];
                    _values.TryAdd(pending, null);
                }
                else if (pending != null)
                {
                    _values[pending] = arg;
                    pending = null;
                }
            }
        }

        public bool Has(string name) => _values.ContainsKey(name);
        public bool Flag(string name) => _values.ContainsKey(name);
        public string? Str(string name) => _values.TryGetValue(name, out string? v) ? v : null;

        public int Int(string name, int fallback) =>
            _values.TryGetValue(name, out string? v) && v != null
            && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                ? n : fallback;
    }
}
