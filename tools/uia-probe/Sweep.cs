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
    public bool Digest { get; set; } = true;
    public int MaxKeys { get; set; } = int.MaxValue;
    /// <summary>A fresh power read-back from whoever can see the radio. Without
    /// one, transmitting chords are refused even if --risk names them.</summary>
    public TransmitClearance? Clearance { get; set; }
}

internal sealed class SweepReport
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion => 1;
    [JsonPropertyName("startedUtc")] public string StartedUtc { get; set; } = "";
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("appDir")] public string AppDir { get; set; } = "";
    [JsonPropertyName("speechLog")] public string? SpeechLog { get; set; }
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
/// nothing and would be reported as a dead key. So the sweep navigates to the
/// right field first and RECORDS where it actually landed, so every result
/// carries the evidence of the context it was measured in.</para>
///
/// <para><b>The Home fields are not separate automation elements.</b> They are
/// caret positions inside one custom-peer text box that deliberately publishes
/// no TextPattern and no ValuePattern, precisely so NVDA stays quiet and the
/// app can do its own speaking. Focus therefore never moves between fields, and
/// no amount of automation-tree inspection can tell you which field you are on.
/// The app's own speech is the only external signal — which is why the trace
/// file is a first-class observation channel here rather than a convenience.</para>
///
/// <para><b>Some of these keys transmit.</b> See <see cref="Risk"/>. The
/// default is safe-only and the skipped ones are listed, because a silent
/// exclusion reads as coverage.</para>
/// </summary>
internal static class Sweep
{
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
        ["CWMessages"] = "keys the transmitter — CW message slots",
    };

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

        string? speechLog = TraceLog.FindCurrent();
        report.SpeechLog = speechLog;
        if (speechLog == null)
            report.Notes.Add("No trace file found in %AppData%\\JJFlexRadio. Neither routing nor speech can be "
                + "observed, so every key will look silent whether it works or not. Treat this run as invalid.");

        // ── The ROUTING channel needs nothing turned on. DoCommand and Leader
        //    lines are written unconditionally at Info, and they are the strongest
        //    signal here because they separate "the chord never arrived" from
        //    "the chord arrived and nothing was listening".
        //
        //    The SPEECH channel is the one that needs a detailed capture: the
        //    Spoke lines are Verbose, and the default level is Info. It is not
        //    optional cover, though — the Home field keys never reach DoCommand
        //    at all, so speech is the ONLY evidence they exist.
        if (o.StartCapture && speechLog != null)
        {
            var probe = PressChord(o, window, speechLog, "Ctrl+J, Ctrl+D", "start detailed capture", "preflight");
            report.Presses.Add(probe);
            speechLog = TraceLog.FindCurrent() ?? speechLog;   // capture may open a new file
            report.SpeechLog = speechLog;

            // Proof by observation: read the log back and look for a Verbose-only
            // line. Asking whether the chord "did something" is not the same
            // question and would pass on any old side effect.
            var after = TraceLog.ReadSince(speechLog, Math.Max(0, TraceLog.Length(speechLog) - 65536));
            report.SpeechChannelVerified = TraceLog.LooksVerbose(after);
            report.Notes.Add(report.SpeechChannelVerified
                ? "Verbose lines are present, so the speech channel is live and Ctrl+J, Ctrl+D did its job."
                : "No Verbose lines after Ctrl+J, Ctrl+D. Either the chord is dead or a capture was already "
                + "running and this press stopped it. Home field keys below cannot be judged.");
        }
        else if (speechLog != null)
        {
            report.Notes.Add("Detailed capture not started (--no-capture). Routing is still observable, but "
                + "Home field keys speak and never reach the dispatcher, so they cannot be judged this run.");
        }

        // ── Learn the Home layout by walking it, rather than assuming it.
        report.FieldMap = MapHomeFields(o, window, speechLog);
        if (report.FieldMap.Count == 0)
            report.Notes.Add("Walking Home with Home then Right produced no spoken field labels. Either focus was "
                + "not on Home, or Left/Right navigation is not announcing — both are findings.");

        // ── The work.
        var entries = Inventory.Load(appDir);
        int pressed = 0;

        foreach (InventoryEntry entry in entries)
        {
            if (o.ContextFilter != null
                && !entry.Context.Contains(o.ContextFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            Expansion expansion = KeyDisplayExpander.Expand(entry.KeyDisplay);
            if (expansion.Residue != null)
            {
                report.Unexpandable.Add($"{entry.ContextLabel}: \"{entry.KeyDisplay}\" — {expansion.Residue}");
                continue;
            }

            if (ElsewhereContexts.TryGetValue(entry.Context, out string? where))
            {
                report.Skipped.Add($"{entry.ContextLabel} \"{entry.KeyDisplay}\" — lives {where}; "
                    + "this sweep does not open that surface");
                continue;
            }

            foreach (ExpandedChord ec in expansion.Chords)
            {
                if (pressed >= o.MaxKeys) { report.Notes.Add($"stopped at --max {o.MaxKeys}"); return report; }

                RiskLevel risk = Risk.Classify(ec.Chord.Display, entry.Description);
                if (!o.AllowedRisk.Contains(risk))
                {
                    report.Skipped.Add($"{ec.Chord.Display} ({entry.ContextLabel}) — classified {risk}, "
                        + "not in the allowed set for this run");
                    continue;
                }

                if (!SeekContext(o, window, speechLog, entry.Context, out string landedIn))
                {
                    report.Skipped.Add($"{ec.Chord.Display} ({entry.ContextLabel}) — could not reach that context; "
                        + $"ended up at {landedIn}");
                    continue;
                }

                PressResult r = Press.Send(o.Pid, ec.Chord, window, speechLog, o.QuietMs, o.MaxSettleMs,
                    o.Digest, risk, o.Clearance);
                r.KeyDisplay = entry.KeyDisplay;
                r.Context = entry.Context + (landedIn.Length > 0 ? $" (landed: {landedIn})" : "");
                r.Description = entry.Description;
                r.Derivation = ec.Derivation.ToString();
                report.Presses.Add(r);
                pressed++;

                Thread.Sleep(o.BetweenKeysMs);

                if (!RestoreBaseline(o, window, speechLog, out string stuck))
                {
                    report.AbortedBecause =
                        $"after pressing {ec.Chord.Display} the app was left showing '{stuck}' and Escape did not "
                        + "return it. Every result after this point would have been measured against the wrong "
                        + "window, so the sweep stopped instead of producing plausible nonsense.";
                    return report;
                }
            }
        }

        return report;
    }

    // ────────────────────────── navigation ──────────────────────────

    /// <summary>
    /// Walk Home from the first field to the last with the Right key, and
    /// collect the labels the app speaks along the way. This is both the map
    /// the sweep navigates with AND a test in its own right: silence here means
    /// Home navigation announces nothing.
    /// </summary>
    private static List<string> MapHomeFields(SweepOptions o, WindowInfo window, string? speechLog)
    {
        var labels = new List<string>();
        if (speechLog == null) return labels;

        PressChord(o, window, speechLog, "Home", "jump to the first Home field", "field map");

        for (int i = 0; i < 48; i++)
        {
            PressResult r = PressChord(o, window, speechLog, "Right", "move one character right", "field map");
            foreach (string said in r.Spoke)
            {
                string norm = said.Trim();
                if (norm.Length == 0) continue;
                if (!labels.Contains(norm, StringComparer.OrdinalIgnoreCase)) labels.Add(norm);
            }
        }
        PressChord(o, window, speechLog, "Home", "back to the first Home field", "field map");
        return labels;
    }

    /// <summary>
    /// Get to the context a key belongs to, and report where we actually
    /// arrived. Returning the landing spot rather than a bare bool matters:
    /// a result measured in the wrong place must be readable as such later.
    /// </summary>
    private static bool SeekContext(SweepOptions o, WindowInfo window, string? speechLog,
        string context, out string landedIn)
    {
        landedIn = "";
        if (HomeWideContexts.Contains(context))
        {
            PressChord(o, window, speechLog, "Home", "return to a known Home position", "seek");
            landedIn = "Home, first field";
            return true;
        }

        if (!HomeFieldLabels.TryGetValue(context, out string? wanted)) { landedIn = "(unknown context)"; return false; }
        if (speechLog == null) { landedIn = "(no speech channel to navigate by)"; return false; }

        PressChord(o, window, speechLog, "Home", "start the seek from the first field", "seek");

        for (int i = 0; i < 48; i++)
        {
            PressResult r = PressChord(o, window, speechLog, "Right", "seek right", "seek");
            foreach (string said in r.Spoke)
            {
                if (said.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    landedIn = said.Trim();
                    return true;
                }
            }
        }
        landedIn = $"never heard '{wanted}' in 48 presses of Right";
        return false;
    }

    /// <summary>
    /// Put the app back where it was. A key that opened a dialog leaves every
    /// subsequent keystroke going somewhere else, so this runs after each press
    /// and the sweep halts rather than continuing blind.
    /// </summary>
    private static bool RestoreBaseline(SweepOptions o, WindowInfo window, string? speechLog, out string stuck)
    {
        stuck = "";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == window.Hwnd) return true;

            Native.GetWindowThreadProcessId(fg, out uint fgPid);
            if (fgPid != (uint)o.Pid)
            {
                // Something outside the app took the foreground. Not ours to
                // dismiss with Escape — just take it back.
                if (Native.Force(window.Hwnd)) return true;
                stuck = $"another application's window ('{Native.Text(fg)}')";
                return false;
            }

            stuck = Native.Text(fg);
            PressChord(o, window, speechLog, "Escape", "dismiss whatever opened", "restore");
            Thread.Sleep(200);
        }
        return Native.GetForegroundWindow() == window.Hwnd;
    }

    private static PressResult PressChord(SweepOptions o, WindowInfo window, string? speechLog,
        string chordText, string description, string context)
    {
        if (!Chord.TryParse(chordText, out Chord chord, out string error))
            return new PressResult { Chord = chordText, Verdict = "not-sent", Error = error, Context = context };

        PressResult r = Press.Send(o.Pid, chord, window, speechLog, o.QuietMs, o.MaxSettleMs, digest: false);
        r.Description = description;
        r.Context = context;
        r.Derivation = Derivation.Exact.ToString();
        r.Risk = RiskLevel.Safe.ToString();
        return r;
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
        sb.AppendLine($"Trace log: {r.SpeechLog ?? "none found"}.");
        sb.AppendLine("Routing channel (DoCommand and Leader lines, Info level): "
            + $"{(r.SpeechLog != null ? "available" : "NOT AVAILABLE")}. "
            + $"Speech channel (Verbose): {(r.SpeechChannelVerified ? "live" : "NOT LIVE")}.");
        if (!r.SpeechChannelVerified)
            sb.AppendLine("Without the speech channel, the Home field keys cannot be judged: they never reach "
                + "the dispatcher, so an utterance is their only outward sign.");
        sb.AppendLine();

        var real = r.Presses.Where(p => p.Context is not ("seek" or "field map" or "restore" or "preflight")).ToList();
        int handled = real.Count(p => p.Verdict == "handled");
        int unhandled = real.Count(p => p.Verdict == "unhandled");
        int silent = real.Count(p => p.Verdict == "silent");
        int notSent = real.Count(p => p.Verdict == "not-sent");
        int refused = real.Count(p => p.Verdict == "skipped");

        sb.AppendLine($"Pressed {real.Count} chords. {handled} did something observable, "
            + $"{unhandled} arrived at the dispatcher and found no command, {silent} produced nothing at all, "
            + $"{notSent} never reached the app, {refused} were refused at the safety gate.");
        sb.AppendLine();

        if (r.FieldMap.Count > 0)
        {
            sb.AppendLine("Home fields heard while walking left to right:");
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
            sb.AppendLine("Produced no observable effect at all — no routing, no speech, no visible change. "
                + "Either genuinely dead, or the key belongs to a surface this run could not reach:");
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
