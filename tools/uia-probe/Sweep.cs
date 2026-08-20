using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JJFlex.UiaProbe;

internal sealed class SweepOptions
{
    public int Pid { get; set; }
    public string? WindowSelector { get; set; }
    public string? AppDir { get; set; }
    public HashSet<RiskLevel> AllowedRisk { get; set; } = new() { RiskLevel.Safe };
    public string? ContextFilter { get; set; }
    public int QuietMs { get; set; } = 400;
    public int MaxSettleMs { get; set; } = 2500;
    public int BetweenKeysMs { get; set; } = 150;
    public bool StartCapture { get; set; } = true;
    public string? OutPath { get; set; }
    /// <summary>
    /// Fingerprint the foreground window's automation tree before and after
    /// each press. OFF by default in a sweep, deliberately.
    ///
    /// <para>It costs up to 500 cross-process property reads twice per press,
    /// which on 199 chords is minutes of an operator's authorised run — and it
    /// buys almost nothing on the surface being swept, because the Home display
    /// publishes hardly anything to the automation tree in the first place. The
    /// change it would actually catch, a dialog appearing, is already caught by
    /// diffing the window list. Turn it on for dialog-heavy contexts.</para>
    /// </summary>
    public bool Digest { get; set; }
    public int MaxKeys { get; set; } = int.MaxValue;
    /// <summary>A fresh power read-back from whoever can see the radio. Without
    /// one, transmitting chords are refused even if --risk names them.</summary>
    public TransmitClearance? Clearance { get; set; }

    /// <summary>
    /// Chords never to press this run, by display name.
    ///
    /// <para>Risk levels are too coarse on their own. Releasing a slice and
    /// toggling noise reduction are both "mutates", but one of them costs the
    /// operator a rebuild of their whole slice layout and the other is a
    /// keypress to undo. Naming the individual chords is how a broad run stays
    /// acceptable, and <see cref="DefaultExclusions"/> is what a sensible one
    /// starts from.</para>
    /// </summary>
    public HashSet<string> Exclude { get; set; } = new(DefaultExclusions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Excluded by default because they destroy operator state rather than
    /// changing it: Comma releases the current slice, Shift+Comma releases every
    /// slice except the first, and Period creates slices that then have to be
    /// cleaned up. Pass --exclude "" to press them anyway.
    /// </summary>
    public static readonly string[] DefaultExclusions = { "Comma", "Shift+Comma", "Period" };
}

internal sealed class SweepReport
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion => 1;
    [JsonPropertyName("startedUtc")] public string StartedUtc { get; set; } = "";
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("appDir")] public string AppDir { get; set; } = "";
    [JsonPropertyName("traceLog")] public string? TraceLogPath { get; set; }
    [JsonPropertyName("speechChannelVerified")] public bool SpeechChannelVerified { get; set; }
    [JsonPropertyName("fieldMap")] public List<string> FieldMap { get; set; } = new();
    [JsonPropertyName("presses")] public List<PressResult> Presses { get; set; } = new();
    [JsonPropertyName("unexpandable")] public List<string> Unexpandable { get; set; } = new();
    [JsonPropertyName("skipped")] public List<string> Skipped { get; set; } = new();
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();
    [JsonPropertyName("abortedBecause")] public string? AbortedBecause { get; set; }
}

/// <summary>
/// Part two of the track: press every binding in KeyInventory for real and
/// assert something observable happened.
///
/// <para>Three things make this harder than "loop over a list and send keys",
/// and each is handled explicitly rather than assumed away.</para>
///
/// <para><b>Most of these keys are context-sensitive.</b> M mutes only while
/// the operator is on a Home field. Pressing it from the wrong place proves
/// nothing and would be written up as a dead key. So the sweep navigates to the
/// right field first and records where it went, and every result carries the
/// context it was measured in.</para>
///
/// <para><b>The Home fields are not separate automation elements.</b> They are
/// caret positions inside one custom-peer text box that deliberately publishes
/// no TextPattern and no ValuePattern, precisely so NVDA stays quiet and the app
/// can do its own speaking. Focus therefore never moves between fields, and no
/// amount of automation-tree inspection can tell you which field you are on.
/// The app's own speech is the only external signal, which is why the trace file
/// is a first-class observation channel here rather than a convenience — and why
/// the sweep learns the layout by walking it and listening.</para>
///
/// <para><b>Some of these keys transmit.</b> See <see cref="Risk"/>. The default
/// is safe-only, transmitting chords additionally need a fresh power read-back
/// from the radio side, and everything skipped is listed — because a silent
/// exclusion reads as coverage.</para>
/// </summary>
internal static class Sweep
{
    /// <summary>
    /// How far right the Home display can possibly go. The fields total well
    /// under this; the margin exists so a longer display in a future build does
    /// not silently truncate the map.
    /// </summary>
    private const int MaxRightsAcrossHome = 56;

    /// <summary>Inventory contexts that live on the JJ Flexible Home surface,
    /// mapped to the label the app speaks on arrival.</summary>
    private static readonly Dictionary<string, string> HomeFieldLabels = new(StringComparer.Ordinal)
    {
        ["Slice"] = "slice",
        ["SliceOps"] = "slice operations",
        ["Freq.Classic"] = "frequency",
        ["Freq.Modern"] = "frequency",
        ["SMeter"] = "s meter",
        ["Squelch"] = "squelch",
        ["SquelchLevel"] = "squelch level",
        ["Split"] = "split",
        ["VOX"] = "vox",
        ["TXSlice"] = "transmit slice",
        ["Offset"] = "offset",
        ["RIT"] = "rit",
        ["XIT"] = "xit",
        ["Mute"] = "mute",
        ["Volume"] = "volume",
    };

    /// <summary>Contexts that work from anywhere on Home — no field seek needed.</summary>
    private static readonly HashSet<string> HomeWideContexts =
        new(StringComparer.Ordinal) { "Home", "HomeNav", "Leader", "VolumeMode", "Filter", "PTT" };

    /// <summary>Contexts that live in a dialog or another surface this sweep
    /// does not open. Reported honestly rather than pressed into Home and
    /// written up as dead.</summary>
    private static readonly Dictionary<string, string> ElsewhereContexts = new(StringComparer.Ordinal)
    {
        ["AudioWorkshop"] = "inside the Audio Workshop dialog",
        ["Categories"] = "inside Settings or the Audio Workshop",
        ["ValueField"] = "inside a Home field group (expander)",
        ["LoggingPane"] = "inside the logging radio pane",
        ["CWMessages"] = "in the CW message slots, which key the transmitter",
    };

    /// <summary>
    /// Everything the sweep WOULD press, without touching the keyboard.
    ///
    /// <para>This exists for the handshake. Driving the live UI needs the
    /// operator's explicit permission each time, and "may I press some keys for
    /// ten minutes" is a much worse question than a list of exactly which
    /// chords, in which order, and which ones are being left alone and why. It
    /// also means a mistake in the risk classification gets caught by reading
    /// rather than by an operator hearing their slices being released.</para>
    ///
    /// <para>What it cannot tell you is which Home fields are reachable — that
    /// is only knowable by walking the display and listening, which needs the
    /// running app. Those rows are listed as conditional.</para>
    /// </summary>
    public static string DryRun(SweepOptions o, string appDir)
    {
        var entries = Inventory.Load(appDir);
        var sb = new StringBuilder();
        var willPress = new List<string>();
        var wontPress = new List<string>();
        var conditional = new List<string>();

        foreach (InventoryEntry entry in entries)
        {
            if (o.ContextFilter != null
                && !entry.Context.Contains(o.ContextFilter, StringComparison.OrdinalIgnoreCase)) continue;

            Expansion expansion = KeyDisplayExpander.Expand(entry.KeyDisplay);
            if (expansion.Residue != null)
            {
                wontPress.Add($"{entry.ContextLabel} \"{entry.KeyDisplay}\" — {expansion.Residue}");
                continue;
            }
            if (ElsewhereContexts.TryGetValue(entry.Context, out string? where))
            {
                wontPress.Add($"{entry.ContextLabel} \"{entry.KeyDisplay}\" — lives {where}");
                continue;
            }

            foreach (ExpandedChord ec in expansion.Chords)
            {
                string label = $"{ec.Chord.Display} ({entry.Context}) — {entry.Description}";
                if (o.Exclude.Contains(ec.Chord.Display))
                {
                    wontPress.Add(label + " — on the exclusion list: destroys operator state");
                    continue;
                }
                RiskLevel risk = Risk.Classify(ec.Chord.Display, entry.Description);
                if (!o.AllowedRisk.Contains(risk))
                {
                    wontPress.Add(label + $" — classified {risk}, not allowed this run");
                    continue;
                }
                if (HomeWideContexts.Contains(entry.Context)) willPress.Add(label);
                else conditional.Add(label);
            }
        }

        sb.AppendLine("Sweep dry run — nothing was pressed.");
        sb.AppendLine();
        sb.AppendLine($"Allowed risk: {string.Join(", ", o.AllowedRisk.Select(r => r.ToString()))}.");
        sb.AppendLine($"Excluded by name: {(o.Exclude.Count == 0 ? "nothing" : string.Join(", ", o.Exclude))}.");
        sb.AppendLine();
        sb.AppendLine($"Would press {willPress.Count + conditional.Count} chords: "
            + $"{willPress.Count} that work from anywhere on Home, and {conditional.Count} that first need the "
            + "sweep to find their field by walking the display and listening — if a field is never heard, its "
            + "keys are reported as unreachable rather than pressed.");
        sb.AppendLine($"Would NOT press {wontPress.Count}.");
        sb.AppendLine();

        sb.AppendLine("Would press, from anywhere on Home:");
        foreach (string s in willPress) sb.AppendLine($"- {s}");
        sb.AppendLine();
        sb.AppendLine("Would press, once the field is found:");
        foreach (string s in conditional) sb.AppendLine($"- {s}");
        sb.AppendLine();
        sb.AppendLine("Would not press:");
        foreach (string s in wontPress) sb.AppendLine($"- {s}");
        return sb.ToString();
    }

    public static SweepReport Run(SweepOptions o)
    {
        var report = new SweepReport
        {
            StartedUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Pid = o.Pid,
        };

        string appDir = o.AppDir ?? Inventory.AppDirOf(o.Pid)
            ?? throw new InvalidOperationException(
                "could not work out the app directory for that process — pass --appdir");
        report.AppDir = appDir;

        WindowInfo window = Targets.Resolve(o.Pid, o.WindowSelector)
            ?? throw new InvalidOperationException("no visible window for that process");

        // A modal message box in front of the app makes every result below a
        // fiction: the keystrokes go into the box, the app looks dead, and the
        // run reads as a wall of dead keys. Track A lost ten minutes to exactly
        // this on 2026-08-20 without knowing what it was looking at.
        WindowInfo? preflightBox = Targets.FindMessageBox(o.Pid);
        if (preflightBox != null)
        {
            report.AbortedBecause =
                $"a modal message box titled '{preflightBox.Title}' is already up in front of the app. "
                + "Nothing was pressed: every keystroke would have gone into that box. Dismiss it and run "
                + "again. Note that at least one dialog raises one of these DURING CONSTRUCTION, so this can "
                + "appear without anyone having opened anything.";
            return report;
        }

        // Load the inventory BEFORE touching the keyboard. If reflecting over
        // the build under test is going to fail, it should fail while the run
        // has cost nothing — not forty keystrokes into an authorised window.
        var entries = Inventory.Load(appDir);

        // Attach to THIS build's log. Newest-by-mtime attached the first smoke
        // test to another track's session and reported nothing for a key it was
        // not watching the right file for; with several tracks running the app,
        // that is the normal condition rather than an edge case.
        TraceLog.TraceHeader? header = TraceLog.FindForApp(appDir);
        string? traceLog = header?.Path;
        report.TraceLogPath = traceLog;
        if (header == null)
        {
            report.AbortedBecause = $"no trace file belongs to the build at {appDir}. Neither routing nor "
                + "speech can be observed, so every key would be reported silent whether it works or not. "
                + "Nothing was pressed. Run `jjprobe trace --all` to see whose sessions are on disk.";
            return report;
        }
        report.Notes.Add($"Attached to {System.IO.Path.GetFileName(traceLog)} — instance {header.Instance}, "
            + $"trace level {header.Level}, session started {header.StartedAt:HH:mm:ss}, built from {header.AppDir}.");

        // One subscription for the whole sweep. Subscribing costs real time, and
        // doing it twice per press would spend more of the operator's authorised
        // run wiring up event handlers than pressing keys.
        using var watcher = new ActivityWatcher(o.Pid);
        if (!watcher.Subscribed)
            report.Notes.Add("Could not subscribe to UI Automation events; settling was judged from the trace "
                + "file alone. For this app that is the richer signal anyway, but window-only changes may have "
                + "been missed.");

        var ctx = new SweepContext(o, window, traceLog, watcher, report);

        // A key that never reaches the dispatcher and a key the dispatcher
        // rejects look identical without this channel, and telling them apart is
        // the entire point of the sweep.
        var priming = TraceLog.ReadSince(traceLog, Math.Max(0, TraceLog.Length(traceLog) - 262144));
        if (TraceLog.Routing(priming).Count == 0)
            report.Notes.Add("No DoCommand or Leader lines in the last 256 KB of that log yet. The routing "
                + "channel may still be fine — the app may simply not have had a key pressed at it — but if "
                + "results below are uniformly silent, suspect the channel before the key map.");

        // ── Get to Home deliberately rather than hoping focus is already there.
        //    F2 is the registry's ShowFreq, so this doubles as the first proof
        //    that registry dispatch is alive at all.
        PressResult toHome = ctx.Measured("F2", "focus the frequency field", "preflight");
        report.Presses.Add(toHome);
        if (toHome.Verdict is "silent" or "unhandled")
            report.Notes.Add("F2 did not visibly put focus on Home. Everything measured below may have been "
                + "pressed somewhere other than the Home display.");

        // ── The ROUTING channel needs nothing turned on: DoCommand and Leader
        //    lines are Info level and always written. The SPEECH channel is the
        //    one that needs a detailed capture, and it is not optional cover —
        //    the Home field keys never reach the dispatcher at all, so an
        //    utterance is the only evidence they exist.
        if (traceLog != null)
        {
            var recent = TraceLog.ReadSince(traceLog, Math.Max(0, TraceLog.Length(traceLog) - 65536));
            bool alreadyVerbose = TraceLog.LooksVerbose(recent);

            if (alreadyVerbose)
            {
                report.SpeechChannelVerified = true;
                report.Notes.Add("A detailed capture was already running, so the speech channel was live before "
                    + "the sweep started. Ctrl+J, Ctrl+D was NOT pressed — pressing it would have stopped the "
                    + "capture and taken the channel away.");
            }
            else if (o.StartCapture)
            {
                report.Presses.Add(ctx.Measured("Ctrl+J, Ctrl+D", "start detailed capture", "preflight"));
                traceLog = TraceLog.FindCurrent() ?? traceLog;   // capture may open a new file
                report.TraceLogPath = traceLog;
                ctx.TraceLogPath = traceLog;

                var after = TraceLog.ReadSince(traceLog, Math.Max(0, TraceLog.Length(traceLog) - 65536));
                report.SpeechChannelVerified = TraceLog.LooksVerbose(after);
                report.Notes.Add(report.SpeechChannelVerified
                    ? "Verbose lines appeared after Ctrl+J, Ctrl+D, so that chord works and the speech channel "
                      + "is live. Proven by reading the log back, not by the chord merely having done something."
                    : "No Verbose lines after Ctrl+J, Ctrl+D. The Home field keys below cannot be judged.");
            }
            else
            {
                report.Notes.Add("Detailed capture not started, because --no-capture was given. Routing is still "
                    + "observable, but the Home field keys speak and never reach the dispatcher, so they cannot "
                    + "be judged.");
            }
        }

        // ── Learn the Home layout by walking it, rather than assuming it.
        Dictionary<string, int> fieldPositions = ctx.MapHomeFields(report);
        if (fieldPositions.Count == 0)
            report.Notes.Add("Walking Home with Home then Right produced no spoken field labels. Either focus "
                + "was not on Home, or Left and Right navigation announces nothing — both are findings.");

        // ── The work, grouped by context so each field is sought once.
        int pressed = 0;

        foreach (var group in entries
                     .Where(e => o.ContextFilter == null
                                 || e.Context.Contains(o.ContextFilter, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(e => e.Context, StringComparer.Ordinal))
        {
            string context = group.Key;

            if (ElsewhereContexts.TryGetValue(context, out string? where))
            {
                foreach (InventoryEntry e in group)
                    report.Skipped.Add($"{e.ContextLabel} \"{e.KeyDisplay}\" — lives {where}; "
                        + "this sweep does not open that surface");
                continue;
            }

            bool homeWide = HomeWideContexts.Contains(context);
            int rights = 0;
            if (!homeWide && !fieldPositions.TryGetValue(context, out rights))
            {
                foreach (InventoryEntry e in group)
                    report.Skipped.Add($"{e.ContextLabel} \"{e.KeyDisplay}\" — the {context} field was never "
                        + "heard while walking Home, so there is no way to get to it and no honest way to "
                        + "judge its keys");
                continue;
            }

            foreach (InventoryEntry entry in group)
            {
                Expansion expansion = KeyDisplayExpander.Expand(entry.KeyDisplay);
                if (expansion.Residue != null)
                {
                    report.Unexpandable.Add($"{entry.ContextLabel}: \"{entry.KeyDisplay}\" — {expansion.Residue}");
                    continue;
                }

                foreach (ExpandedChord ec in expansion.Chords)
                {
                    if (pressed >= o.MaxKeys)
                    {
                        report.Notes.Add($"Stopped at the --max limit of {o.MaxKeys}; the rest of the inventory "
                            + "was not reached.");
                        return report;
                    }

                    if (o.Exclude.Contains(ec.Chord.Display))
                    {
                        report.Skipped.Add($"{ec.Chord.Display} ({entry.ContextLabel}) — on the exclusion list "
                            + "for this run: it destroys operator state rather than changing it");
                        continue;
                    }

                    RiskLevel risk = Risk.Classify(ec.Chord.Display, entry.Description);
                    if (!o.AllowedRisk.Contains(risk))
                    {
                        report.Skipped.Add($"{ec.Chord.Display} ({entry.ContextLabel}) — classified {risk}, "
                            + "not in the allowed set for this run");
                        continue;
                    }

                    // Reposition WITHOUT observing: getting there is the cost of
                    // the measurement, not the measurement.
                    ctx.RepositionHome(rights);

                    PressResult r = Press.Send(o.Pid, ec.Chord, window, ctx.TraceLogPath,
                        o.QuietMs, o.MaxSettleMs, o.Digest, risk, o.Clearance, watcher);
                    r.KeyDisplay = entry.KeyDisplay;
                    r.Context = homeWide ? context : $"{context}, {rights} rights from Home";
                    r.Description = entry.Description;
                    r.Derivation = ec.Derivation.ToString();
                    report.Presses.Add(r);
                    pressed++;

                    Thread.Sleep(o.BetweenKeysMs);

                    if (!ctx.RestoreBaseline(out string stuck))
                    {
                        report.AbortedBecause =
                            $"after pressing {ec.Chord.Display} the app was left showing '{stuck}' and Escape did "
                            + "not return it. Every result after this point would have been measured against the "
                            + "wrong window, so the sweep stopped instead of producing plausible nonsense.";
                        return report;
                    }
                }
            }
        }

        return report;
    }

    // ────────────────────────── navigation ──────────────────────────

    /// <summary>
    /// The per-run state the navigation needs: which window, which log, which
    /// watcher. Bundled so the helpers stop taking six arguments each.
    /// </summary>
    private sealed class SweepContext
    {
        private readonly SweepOptions _o;
        private readonly WindowInfo _window;
        private readonly ActivityWatcher _watcher;
        private readonly SweepReport _report;

        public string? TraceLogPath { get; set; }

        public SweepContext(SweepOptions o, WindowInfo window, string? traceLog,
            ActivityWatcher watcher, SweepReport report)
        {
            _o = o;
            _window = window;
            TraceLogPath = traceLog;
            _watcher = watcher;
            _report = report;
        }

        /// <summary>A fully observed press, for measurements and preflight.</summary>
        public PressResult Measured(string chordText, string description, string context)
        {
            if (!Chord.TryParse(chordText, out Chord chord, out string error))
                return new PressResult { Chord = chordText, Verdict = "not-sent", Error = error, Context = context };

            PressResult r = Press.Send(_o.Pid, chord, _window, TraceLogPath,
                _o.QuietMs, _o.MaxSettleMs, digest: false, RiskLevel.Safe, null, _watcher);
            r.Description = description;
            r.Context = context;
            r.Derivation = Derivation.Exact.ToString();
            return r;
        }

        /// <summary>
        /// Walk Home left to right and record which Right-press first produced
        /// each spoken field label.
        ///
        /// <para>This is the map the sweep navigates by AND a test in its own
        /// right. It is also the only way to navigate at all: the Home fields
        /// are caret positions inside a single custom-peer text box that
        /// publishes no TextPattern and no ValuePattern, so focus never moves
        /// between them and the automation tree cannot say which field the
        /// operator is on. The app's speech is the whole map.</para>
        /// </summary>
        public Dictionary<string, int> MapHomeFields(SweepReport report)
        {
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            var heard = new List<(int Rights, string Label)>();
            if (TraceLogPath == null) return positions;

            Measured("Home", "jump to the first Home field", "field map");

            for (int i = 1; i <= MaxRightsAcrossHome; i++)
            {
                PressResult r = Measured("Right", "move one character right", "field map");
                foreach (string said in r.Spoke)
                {
                    string label = said.Trim();
                    if (label.Length == 0) continue;
                    heard.Add((i, label));
                    if (!report.FieldMap.Contains(label, StringComparer.OrdinalIgnoreCase))
                        report.FieldMap.Add(label);
                }
            }

            // Assign the longest expected label first: "slice operations" and
            // "transmit slice" both contain "slice", and matching the short one
            // first would claim the wrong position for all three.
            foreach (var kv in HomeFieldLabels.OrderByDescending(k => k.Value.Length))
            {
                if (positions.ContainsKey(kv.Key)) continue;
                foreach ((int rightsAt, string label) in heard)
                {
                    if (!label.Contains(kv.Value, StringComparison.OrdinalIgnoreCase)) continue;
                    if (positions.ContainsValue(rightsAt)) continue;
                    positions[kv.Key] = rightsAt;
                    break;
                }
            }

            // Both tuning modes name the field "frequency" and only one is live
            // at a time. Share the position, and let the report say which
            // silences were expected rather than calling half of them dead.
            if (positions.TryGetValue("Freq.Classic", out int freq)) positions.TryAdd("Freq.Modern", freq);
            else if (positions.TryGetValue("Freq.Modern", out freq)) positions.TryAdd("Freq.Classic", freq);

            Measured("Home", "back to the first Home field", "field map");
            return positions;
        }

        /// <summary>
        /// Put the caret back on a known field, fast and unobserved.
        /// </summary>
        public void RepositionHome(int rights)
        {
            if (!Chord.TryParse("Home", out Chord home, out _)) return;
            if (!Chord.TryParse("Right", out Chord right, out _)) return;

            Press.SendQuiet(home, _window);
            for (int i = 0; i < rights; i++) Press.SendQuiet(right, _window);
        }

        /// <summary>
        /// Put the app back where it was. A key that opened something leaves
        /// every later keystroke going somewhere else, so this runs after each
        /// press and the sweep halts rather than continuing blind.
        /// </summary>
        public bool RestoreBaseline(out string stuck)
        {
            stuck = "";
            if (!Chord.TryParse("Escape", out Chord escape, out _)) return true;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == _window.Hwnd) return true;

                Native.GetWindowThreadProcessId(fg, out uint fgPid);
                if (fgPid != (uint)_o.Pid)
                {
                    // Something outside the app took the foreground. Not ours to
                    // dismiss with Escape — just take it back.
                    if (Native.Force(_window.Hwnd)) return true;
                    stuck = $"another application's window, '{Native.Text(fg)}'";
                    return false;
                }

                stuck = Native.Text(fg);
                bool isMessageBox = string.Equals(Native.Cls(fg), Targets.MessageBoxClass, StringComparison.Ordinal);
                _report.Notes.Add(isMessageBox
                    ? $"A MODAL MESSAGE BOX titled '{stuck}' appeared and was dismissed with Escape. A message "
                      + "box blocks the app's UI thread entirely, so anything measured around it is suspect."
                    : $"'{stuck}' opened and was dismissed with Escape.");
                Press.SendQuiet(escape, _window, pauseMs: 250);
            }
            return Native.GetForegroundWindow() == _window.Hwnd;
        }
    }

    // ────────────────────────── reporting ──────────────────────────

    /// <summary>
    /// The screen-reader-first report. Bullets and prose, never a table: this
    /// document exists to be read aloud, and a table read aloud is a wall of
    /// coordinates.
    /// </summary>
    public static string ToText(SweepReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Key press sweep");
        sb.AppendLine();
        sb.AppendLine($"Started {r.StartedUtc}, process {r.Pid}, build directory {r.AppDir}.");
        sb.AppendLine($"Trace log: {r.TraceLogPath ?? "none found"}.");
        sb.AppendLine("Routing channel, meaning the DoCommand and Leader lines at Info level: "
            + $"{(r.TraceLogPath != null ? "available" : "NOT AVAILABLE")}. "
            + $"Speech channel, which needs Verbose: {(r.SpeechChannelVerified ? "live" : "NOT LIVE")}.");
        if (!r.SpeechChannelVerified)
            sb.AppendLine("Without the speech channel the Home field keys cannot be judged: they never reach "
                + "the dispatcher, so an utterance is their only outward sign.");
        sb.AppendLine();

        var real = r.Presses
            .Where(p => p.Context is not ("field map" or "preflight"))
            .ToList();
        int handled = real.Count(p => p.Verdict == "handled");
        int unhandled = real.Count(p => p.Verdict == "unhandled");
        int silent = real.Count(p => p.Verdict == "silent");
        int notSent = real.Count(p => p.Verdict == "not-sent");
        int refused = real.Count(p => p.Verdict == "skipped");

        sb.AppendLine($"Pressed {real.Count} chords. {handled} did something observable, "
            + $"{unhandled} arrived at the dispatcher and found no command, {silent} produced nothing at all, "
            + $"{notSent} never reached the app, and {refused} were refused at the safety gate.");
        sb.AppendLine();

        if (r.FieldMap.Count > 0)
        {
            sb.AppendLine("Heard while walking Home from left to right:");
            foreach (string f in r.FieldMap) sb.AppendLine($"- {f}");
            sb.AppendLine();
        }

        if (unhandled > 0)
        {
            sb.AppendLine("ARRIVED AND NOTHING WAS LISTENING. This is the dead-binding signature: the keystroke "
                + "reached the dispatcher, which logged that it had no command for it.");
            foreach (PressResult p in real.Where(p => p.Verdict == "unhandled"))
                sb.AppendLine($"- {p.Chord} on {p.Context} — should have: {p.Description}. "
                    + $"Dispatcher said: {string.Join(" / ", p.Routed)}");
            sb.AppendLine();
        }

        if (silent > 0)
        {
            sb.AppendLine("Produced no observable effect at all: no routing, no speech, no visible change. "
                + "Either genuinely dead, or belonging to a surface this run could not reach:");
            foreach (PressResult p in real.Where(p => p.Verdict == "silent"))
                sb.AppendLine($"- {p.Chord} on {p.Context} — expected: {p.Description}");
            sb.AppendLine();
        }

        if (notSent > 0)
        {
            sb.AppendLine("Never reached the app:");
            foreach (PressResult p in real.Where(p => p.Verdict == "not-sent"))
                sb.AppendLine($"- {p.Chord} — {p.Error}");
            sb.AppendLine();
        }

        if (refused > 0)
        {
            sb.AppendLine("Refused at the safety gate:");
            foreach (PressResult p in real.Where(p => p.Verdict == "skipped"))
                sb.AppendLine($"- {p.Chord} — {p.Error}");
            sb.AppendLine();
        }

        sb.AppendLine("Did something observable:");
        foreach (PressResult p in real.Where(p => p.Verdict == "handled"))
        {
            string said = p.Spoke.Count > 0 ? "said \"" + string.Join("\" then \"", p.Spoke) + "\"" : "";
            string routed = p.Routed.Count > 0 ? "dispatcher: " + string.Join(" / ", p.Routed) : "";
            string seen = p.UiChanges.Count > 0 ? string.Join("; ", p.UiChanges) : "";
            string joined = string.Join("; ", new[] { said, routed, seen }.Where(s => s.Length > 0));
            sb.AppendLine($"- {p.Chord} on {p.Context} — {joined}");
        }
        sb.AppendLine();

        if (r.Unexpandable.Count > 0)
        {
            sb.AppendLine("Not pressed, because the inventory writes them as prose rather than as a chord:");
            foreach (string u in r.Unexpandable) sb.AppendLine($"- {u}");
            sb.AppendLine();
        }

        if (r.Skipped.Count > 0)
        {
            sb.AppendLine("Deliberately not pressed:");
            foreach (string s in r.Skipped) sb.AppendLine($"- {s}");
            sb.AppendLine();
        }

        if (r.Notes.Count > 0)
        {
            sb.AppendLine("Notes:");
            foreach (string n in r.Notes) sb.AppendLine($"- {n}");
            sb.AppendLine();
        }

        if (r.AbortedBecause != null)
        {
            sb.AppendLine("SWEEP STOPPED EARLY.");
            sb.AppendLine(r.AbortedBecause);
        }
        return sb.ToString();
    }

    public static string ToJson(SweepReport r) =>
        JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true });
}
