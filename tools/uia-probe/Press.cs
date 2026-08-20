using System.Text.Json.Serialization;

namespace JJFlex.UiaProbe;

/// <summary>
/// The result of one press. This shape IS the Tier 3 seam — Track C's radio
/// observer reads it off stdout — so treat the property names as a contract and
/// add rather than rename.
/// </summary>
internal sealed class PressResult
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion => 1;
    [JsonPropertyName("chord")] public string Chord { get; set; } = "";
    [JsonPropertyName("keyDisplay")] public string KeyDisplay { get; set; } = "";
    [JsonPropertyName("context")] public string Context { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("derivation")] public string Derivation { get; set; } = "";
    [JsonPropertyName("risk")] public string Risk { get; set; } = "";

    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("window")] public string Window { get; set; } = "";
    [JsonPropertyName("windowHandle")] public string WindowHandle { get; set; } = "";
    [JsonPropertyName("foregrounded")] public bool Foregrounded { get; set; }

    [JsonPropertyName("sentAtUtc")] public string SentAtUtc { get; set; } = "";
    [JsonPropertyName("settledAtUtc")] public string SettledAtUtc { get; set; } = "";
    [JsonPropertyName("settleMs")] public int SettleMs { get; set; }
    /// <summary>True when the app went quiet on its own; false when it was still
    /// churning when the maximum wait expired.</summary>
    [JsonPropertyName("quiesced")] public bool Quiesced { get; set; }

    /// <summary>What the key dispatcher logged, in order. Info level, always on.</summary>
    [JsonPropertyName("routed")] public List<string> Routed { get; set; } = new();
    /// <summary>True when the dispatcher explicitly said no command was found —
    /// the chord arrived and nothing was listening. The dead-binding signature.</summary>
    [JsonPropertyName("dispatcherFoundNothing")] public bool DispatcherFoundNothing { get; set; }

    [JsonPropertyName("spoke")] public List<string> Spoke { get; set; } = new();
    [JsonPropertyName("uiChanges")] public List<string> UiChanges { get; set; } = new();
    [JsonPropertyName("traceChannelAvailable")] public bool TraceChannelAvailable { get; set; }

    /// <summary>
    /// "handled" — the app did something observable.
    /// "unhandled" — the chord ARRIVED and the dispatcher found no command for
    ///     it. Distinct from silent, and much more damning.
    /// "silent" — pressed cleanly and nothing changed anywhere we can see.
    /// "not-sent" — never reached the app; see <see cref="Error"/>.
    /// "skipped" — deliberately not pressed; see <see cref="Error"/>.
    /// </summary>
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = "";
    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonPropertyName("focusBefore")] public string FocusBefore { get; set; } = "";
    [JsonPropertyName("focusAfter")] public string FocusAfter { get; set; } = "";
}

/// <summary>
/// A vouch from whoever can actually see the radio, that keying it right now is
/// within the operator's power ceiling.
///
/// <para>Raised by Track G, 2026-08-20: <c>FlexBase.setupFromScratch()</c> sets
/// <c>RFPower = 100</c> unconditionally. It only runs when no saved global
/// profile is found, so it does not fire on the current bench radio — but a
/// harness that keys a radio which has been reset, or one it has never seen
/// before, can find itself at full power with nothing having asked for it.</para>
///
/// <para>The correction that matters: a ceiling you SET is a wish, and a
/// ceiling you READ BACK immediately before keying is a ceiling. This probe
/// deliberately has no radio connection, so it cannot do the reading itself —
/// which is exactly why the vouch is a file. The tool that can see the radio
/// writes it; the tool that presses the key demands it and checks it is fresh.
/// Neither half can wave the other through.</para>
/// </summary>
internal sealed class TransmitClearance
{
    [JsonPropertyName("issuedUtc")] public string IssuedUtc { get; set; } = "";
    [JsonPropertyName("ceilingWatts")] public double CeilingWatts { get; set; }
    /// <summary>Power read back FROM THE RADIO, not the value that was sent to it.</summary>
    [JsonPropertyName("measuredWatts")] public double MeasuredWatts { get; set; }
    [JsonPropertyName("radio")] public string Radio { get; set; } = "";
    [JsonPropertyName("validForMs")] public int ValidForMs { get; set; } = 10000;

    public bool IsValid(out string reason)
    {
        if (!DateTime.TryParse(IssuedUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime issued))
        {
            reason = $"issuedUtc '{IssuedUtc}' is not a timestamp";
            return false;
        }

        double ageMs = (DateTime.UtcNow - issued).TotalMilliseconds;
        if (ageMs < 0) { reason = "clearance is dated in the future"; return false; }
        if (ageMs > ValidForMs)
        {
            reason = $"clearance is {ageMs:F0} ms old and only valid for {ValidForMs} ms — "
                   + "read the power back again";
            return false;
        }
        if (CeilingWatts <= 0) { reason = "no ceiling set"; return false; }
        if (MeasuredWatts > CeilingWatts)
        {
            reason = $"radio reports {MeasuredWatts:F0} W, ceiling is {CeilingWatts:F0} W";
            return false;
        }

        reason = $"{MeasuredWatts:F0} W measured against a {CeilingWatts:F0} W ceiling"
               + $"{(Radio.Length > 0 ? $" on {Radio}" : "")}, {ageMs:F0} ms ago";
        return true;
    }
}

/// <summary>
/// Press a chord for real and report what the outside world saw.
///
/// <para>The safety rules here are not decoration. This tool holds down
/// modifier keys on a live desktop belonging to an operator who cannot see the
/// screen, in an application whose keys can key a transmitter. So: modifiers are
/// released in a finally block on every path; the target window must genuinely
/// reach the foreground before anything is sent, and a refusal aborts rather
/// than firing the keystroke into whatever is focused instead; and a chord
/// classified as transmitting needs a fresh, radio-read power clearance rather
/// than a command-line flag.</para>
/// </summary>
internal static class Press
{
    /// <summary>Milliseconds a key is held down. Long enough for WPF to see it,
    /// short enough not to trigger auto-repeat.</summary>
    private const int HoldMs = 30;

    /// <summary>Gap between the steps of a sequence such as Ctrl+J then V.
    /// The leader layer arms on key-up, so the follow-on key must not race it.</summary>
    private const int BetweenStepsMs = 90;

    public static PressResult Send(
        int pid,
        Chord chord,
        WindowInfo window,
        string? traceLogPath,
        int quietMs,
        int maxSettleMs,
        bool digest,
        RiskLevel risk = RiskLevel.Safe,
        TransmitClearance? clearance = null,
        ActivityWatcher? watcher = null)
    {
        var result = new PressResult
        {
            Chord = chord.Display,
            Pid = pid,
            Risk = risk.ToString(),
            Window = string.IsNullOrEmpty(window.Title) ? window.ClassName : window.Title,
            WindowHandle = window.HwndHex,
            TraceChannelAvailable = traceLogPath != null,
        };

        // ── The transmit gate. Checked here, at the only place a keystroke can
        //    actually leave, so no caller can route around it.
        if (risk == RiskLevel.Transmits)
        {
            if (clearance == null)
            {
                result.Verdict = "skipped";
                result.Error = "this chord keys the transmitter and no power clearance was supplied. "
                             + "Pass --transmit-clearance with a file written by whatever can read the "
                             + "radio's power back; a command-line flag is not evidence about the radio.";
                return result;
            }
            if (!clearance.IsValid(out string why))
            {
                result.Verdict = "skipped";
                result.Error = "transmit clearance refused: " + why;
                return result;
            }
            result.Error = "transmit cleared: " + why;
        }

        // Start from a clean modifier state. A leftover Ctrl from an aborted
        // previous press turns the next plain key into a chord and produces a
        // completely fictitious result.
        Native.ReleaseAllModifiers();

        long traceOffset = traceLogPath != null ? TraceLog.Length(traceLogPath) : 0;
        Snapshot before = Observe.Capture(pid, traceLogPath, digest);
        result.FocusBefore = Summarise(before);

        if (!Native.Force(window.Hwnd))
        {
            result.Verdict = "not-sent";
            result.Error = "could not bring the target window to the foreground; "
                         + "the keystroke was NOT sent, because it would have gone to whatever is focused instead";
            return result;
        }
        result.Foregrounded = true;

        result.SentAtUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            for (int i = 0; i < chord.Steps.Count; i++)
            {
                if (i > 0) Thread.Sleep(BetweenStepsMs);
                SendStep(chord.Steps[i]);
            }
        }
        finally
        {
            // Unconditional. If anything above threw mid-chord, a modifier is
            // still down right now and the operator's next keystroke is wrong.
            Native.ReleaseAllModifiers();
        }

        int elapsed;
        result.Quiesced = watcher != null
            ? watcher.WaitForQuiet(traceLogPath, quietMs, maxSettleMs, out elapsed)
            : Observe.WaitForSettle(pid, traceLogPath, quietMs, maxSettleMs, out elapsed);
        result.SettleMs = elapsed;
        result.SettledAtUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        Snapshot after = Observe.Capture(pid, traceLogPath, digest);
        result.FocusAfter = Summarise(after);
        result.UiChanges = Observe.Diff(before, after);

        if (traceLogPath != null)
        {
            List<string> newLines = TraceLog.ReadSince(traceLogPath, traceOffset);
            result.Spoke = TraceLog.Utterances(newLines);

            var routing = TraceLog.Routing(newLines);
            result.Routed = routing.Select(e => e.Line).ToList();
            result.DispatcherFoundNothing = routing.Any(e => e.Unhandled);
        }

        bool anythingHappened = result.Spoke.Count > 0 || result.UiChanges.Count > 0
                                || result.Routed.Any(l => !l.StartsWith("DoCommand:key not found", StringComparison.Ordinal));

        result.Verdict =
            result.DispatcherFoundNothing && !anythingHappened ? "unhandled"
            : anythingHappened ? "handled"
            : "silent";
        return result;
    }

    /// <summary>
    /// Send a chord with NO observation and no settle wait — just the
    /// keystrokes and a short fixed pause.
    ///
    /// <para>This is how the sweep gets back to a known position between
    /// measured presses. The Home fields are caret positions inside one text
    /// box, so reaching the Squelch field means Home then some number of
    /// Rights, and doing that with a fully observed, fully settled press each
    /// time would spend the operator's authorised run time walking the display
    /// instead of testing it. Repositioning is not the measurement; it is the
    /// cost of getting to it.</para>
    ///
    /// <para>Still releases modifiers in a finally, because the reason for that
    /// does not change when nobody is watching.</para>
    /// </summary>
    public static void SendQuiet(Chord chord, WindowInfo window, int pauseMs = 45)
    {
        if (Native.GetForegroundWindow() != window.Hwnd && !Native.Force(window.Hwnd)) return;
        try
        {
            for (int i = 0; i < chord.Steps.Count; i++)
            {
                if (i > 0) Thread.Sleep(BetweenStepsMs);
                SendStep(chord.Steps[i]);
            }
        }
        finally { Native.ReleaseAllModifiers(); }
        Thread.Sleep(pauseMs);
    }

    private static void SendStep(Step step)
    {
        var down = new List<ushort>();
        if ((step.Mods & Mods.Ctrl) != 0) down.Add(Native.VK_CONTROL);
        if ((step.Mods & Mods.Alt) != 0) down.Add(Native.VK_MENU);
        if ((step.Mods & Mods.Shift) != 0) down.Add(Native.VK_SHIFT);
        if ((step.Mods & Mods.Win) != 0) down.Add(Native.VK_LWIN);

        foreach (ushort vk in down) { Native.SendKeyEvent(vk, up: false); Thread.Sleep(8); }
        try
        {
            Native.SendKeyEvent(step.Vk, up: false);
            Thread.Sleep(HoldMs);
            Native.SendKeyEvent(step.Vk, up: true);
        }
        finally
        {
            // Reverse order, so Alt comes up before Ctrl and Windows does not
            // read a lone Alt press and open a menu bar.
            for (int i = down.Count - 1; i >= 0; i--)
            {
                Thread.Sleep(8);
                Native.SendKeyEvent(down[i], up: true);
            }
        }
    }

    private static string Summarise(Snapshot s) =>
        s.FocusName.Length > 0 || s.FocusControlType.Length > 0
            ? $"{s.FocusControlType} '{s.FocusName}'{(s.FocusAutomationId.Length > 0 ? $" id={s.FocusAutomationId}" : "")}"
            : "(nothing focused in this process)";
}
