namespace JJFlex.UiaProbe;

/// <summary>What one self-check run found, for the console and for the sweep that asked.</summary>
internal sealed class SelfCheckResult
{
    public SelfCheckVerdict Verdict { get; init; }
    public string Chord { get; init; } = "";
    public string Message { get; init; } = "";
    public PressResult? Press { get; init; }
    public bool Passed => Verdict == SelfCheckVerdict.Passed;
}

/// <summary>
/// Run the injection self-check against a live app: confirm the key is
/// unbound in the build under test, press it once through the same path every
/// measured press takes (<see cref="Press.Send"/>), and judge with
/// <see cref="InjectionSelfCheck"/>. The decisions live there, with no Win32,
/// so they are tested; this file only gathers the facts.
/// </summary>
internal static class SelfCheck
{
    public static SelfCheckResult Run(int pid, WindowInfo window, string appDir, string? traceLogPath, string chordText)
    {
        if (!JJFlex.UiaProbe.Chord.TryParse(chordText, out Chord chord, out string error))
            throw new ArgumentException(error);

        string expected = chord.Steps.Count == 1
            ? InjectionSelfCheck.ExpectedRoutingLine(InjectionSelfCheck.WinFormsKeyData(chord.Steps[0].Vk,
                (chord.Steps[0].Mods & Mods.Ctrl) != 0, (chord.Steps[0].Mods & Mods.Shift) != 0,
                (chord.Steps[0].Mods & Mods.Alt) != 0))
            : "";

        SelfCheckVerdict? refusal = InjectionSelfCheck.RefuseBeforePress(chord.Steps.Count, IsBound(chord, appDir));
        if (refusal is SelfCheckVerdict refused)
        {
            return new SelfCheckResult
            {
                Verdict = refused, Chord = chord.Display,
                Message = InjectionSelfCheck.Describe(refused, chord.Display, expected),
            };
        }

        PressResult r = Press.Send(pid, chord, window, traceLogPath, quietMs: 400, maxSettleMs: 2500, digest: false);
        bool sent = r.Verdict is not ("not-sent" or "skipped");
        bool stillInFront = Native.GetForegroundWindow() == window.Hwnd;
        SelfCheckVerdict verdict = InjectionSelfCheck.Judge(r.Foregrounded, sent, stillInFront,
            traceLogPath != null, r.Routed, expected);
        return new SelfCheckResult
        {
            Verdict = verdict, Chord = chord.Display, Press = r,
            Message = InjectionSelfCheck.Describe(verdict, chord.Display, expected)
                + (r.Error is { Length: > 0 } e && verdict != SelfCheckVerdict.Passed ? " (" + e + ")" : ""),
        };
    }

    /// <summary>
    /// Is the chord bound in this build? Null when the inventory cannot say:
    /// it could not be read, or a row it could not expand names the same key.
    /// </summary>
    private static bool? IsBound(Chord chord, string appDir)
    {
        List<InventoryEntry> rows;
        try { rows = Inventory.Load(appDir); }
        catch (Exception) { return null; }

        string keyName = chord.Steps.Count == 1 ? KeyName(chord.Steps[0].Display) : chord.Display;
        bool unknown = false;
        foreach (InventoryEntry e in rows)
        {
            Expansion x = KeyDisplayExpander.Expand(e);
            if (x.Chords.Any(c => string.Equals(c.Chord.Display, chord.Display, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (x.Residue != null && e.KeyDisplay.Contains(keyName, StringComparison.OrdinalIgnoreCase))
                unknown = true;
        }
        return unknown ? null : false;
    }

    /// <summary>The key without its modifiers: "F12" from "Ctrl+Shift+F12".</summary>
    private static string KeyName(string stepDisplay)
    {
        int plus = stepDisplay.LastIndexOf('+');
        return plus >= 0 && plus < stepDisplay.Length - 1 ? stepDisplay[(plus + 1)..] : stepDisplay;
    }
}
