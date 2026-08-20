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

    [JsonPropertyName("spoke")] public List<string> Spoke { get; set; } = new();
    [JsonPropertyName("uiChanges")] public List<string> UiChanges { get; set; } = new();
    [JsonPropertyName("speechChannelAvailable")] public bool SpeechChannelAvailable { get; set; }

    /// <summary>
    /// "effect" — something observably happened.
    /// "silent" — pressed cleanly and nothing changed anywhere we can see.
    /// "not-sent" — never reached the app; see <see cref="Error"/>.
    /// "skipped" — deliberately not pressed, see <see cref="Error"/>.
    /// </summary>
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = "";
    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonPropertyName("focusBefore")] public string FocusBefore { get; set; } = "";
    [JsonPropertyName("focusAfter")] public string FocusAfter { get; set; } = "";
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
/// classified as transmitting is never pressed without an explicit opt-in.</para>
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
        string? speechLogPath,
        int quietMs,
        int maxSettleMs,
        bool digest)
    {
        var result = new PressResult
        {
            Chord = chord.Display,
            Pid = pid,
            Window = string.IsNullOrEmpty(window.Title) ? window.ClassName : window.Title,
            WindowHandle = window.HwndHex,
            SpeechChannelAvailable = speechLogPath != null,
        };

        // Start from a clean modifier state. A leftover Ctrl from an aborted
        // previous press turns the next plain key into a chord and produces a
        // completely fictitious result.
        Native.ReleaseAllModifiers();

        long speechOffset = speechLogPath != null ? SpeechLog.Length(speechLogPath) : 0;
        Snapshot before = Observe.Capture(pid, speechLogPath, digest);
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

        result.Quiesced = Observe.WaitForSettle(pid, speechLogPath, quietMs, maxSettleMs, out int elapsed);
        result.SettleMs = elapsed;
        result.SettledAtUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        Snapshot after = Observe.Capture(pid, speechLogPath, digest);
        result.FocusAfter = Summarise(after);
        result.UiChanges = Observe.Diff(before, after);

        if (speechLogPath != null)
            result.Spoke = SpeechLog.Utterances(SpeechLog.ReadSince(speechLogPath, speechOffset));

        result.Verdict = result.Spoke.Count > 0 || result.UiChanges.Count > 0 ? "effect" : "silent";
        return result;
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
