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
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Native.ReleaseAllModifiers();
        Console.CancelKeyPress += (_, _) => Native.ReleaseAllModifiers();

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
            "press" => CmdPress(opt),
            "act" => CmdAct(opt),
            "inventory" => CmdInventory(opt),
            "unbound" => CmdUnbound(opt),
            "expand" => CmdExpand(opt),
            "altcheck" => CmdAltCheck(opt),
            "sweep" => CmdSweep(opt),
            _ => Usage($"unknown command '{command}'"),
        };
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
  jjprobe act       [--pid N] --op invoke|toggle|select|expand|focus|value|listitems
                    (--id ID | --name NAME | --class CLASS) [--index N] [--value V]
  jjprobe inventory [--pid N | --appdir DIR] [--json]
  jjprobe unbound   [--pid N | --appdir DIR] [--json]
  jjprobe expand    [--pid N | --appdir DIR]        (offline: no app driving)
  jjprobe altcheck  --src DIR                        (offline: static source scan)
  jjprobe sweep     [--pid N] [--window SEL] [--appdir DIR] [--context NAME]
                    [--risk safe,mutates,transmits] [--max N] [--no-capture]
                    [--out FILE] [--json]

Default process name is 'jjflexible'. --window takes a title substring, a class
substring, or an index from `jjprobe windows`.

SAFETY. `press` and `sweep` type on the real desktop: the target window is
brought to the foreground first, so whatever the operator was doing loses focus.
`sweep` presses only chords classified safe unless --risk says otherwise, and
never presses a transmitting chord without being told to by name.

Exit codes: 0 ok · 1 error · 2 usage · 3 pressed but never settled ·
4 target window not found · 5 could not foreground the window.
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
        Snapshot s = Observe.Capture(pid, SpeechLog.FindCurrent(), digest: false);
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

        PressResult r = Press.Send(pid, chord, window, SpeechLog.FindCurrent(),
            a.Int("quiet-ms", 400), a.Int("max-settle-ms", 2500), digest: !a.Flag("no-digest"));

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
            foreach (string s in r.Spoke) Console.WriteLine($"  said: \"{s}\"");
            foreach (string c in r.UiChanges) Console.WriteLine($"  saw:  {c}");
            Console.WriteLine($"verdict: {r.Verdict}{(r.Error != null ? " — " + r.Error : "")}");
        }

        if (r.Verdict == "not-sent") return 5;
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
            Expansion x = KeyDisplayExpander.Expand(e.KeyDisplay);
            if (x.Residue != null)
            {
                residue++;
                lines.Add($"UNEXPANDABLE  {e.ContextLabel}: \"{e.KeyDisplay}\" — {x.Residue}");
                continue;
            }
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

    private static int CmdSweep(Args a)
    {
        var o = new SweepOptions
        {
            Pid = ResolvePid(a),
            WindowSelector = a.Str("window"),
            AppDir = a.Str("appdir"),
            ContextFilter = a.Str("context"),
            QuietMs = a.Int("quiet-ms", 400),
            MaxSettleMs = a.Int("max-settle-ms", 2500),
            BetweenKeysMs = a.Int("between-ms", 150),
            StartCapture = !a.Flag("no-capture"),
            Digest = !a.Flag("no-digest"),
            MaxKeys = a.Int("max", int.MaxValue),
        };
        if (a.Has("risk"))
            o.AllowedRisk = (a.Str("risk") ?? "safe")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Risk.Parse).ToHashSet();

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
