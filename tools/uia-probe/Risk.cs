namespace JJFlex.UiaProbe;

internal enum RiskLevel
{
    /// <summary>Reads, speaks, or moves focus. Pressing it changes nothing an
    /// operator would have to undo.</summary>
    Safe,
    /// <summary>Changes radio or application state that persists after the
    /// press: creates or releases a slice, moves the frequency, flips a DSP
    /// switch, rewrites a level. Recoverable, but the operator will notice.</summary>
    Mutates,
    /// <summary>Keys the transmitter, or arms something that will. NEVER pressed
    /// without an explicit opt-in, and never as a side effect of "test
    /// everything".</summary>
    Transmits,
}

/// <summary>
/// What pressing a key will cost.
///
/// <para>A sweep that presses every key in this application without thinking
/// about this would key the transmitter seven times over from the CW message
/// slots alone, release every slice but the first, and reset somebody's TX
/// audio chain. So classification is a precondition of the sweep, not a
/// refinement of it, and the default is to press only what is safe.</para>
///
/// <para>Classification reads the inventory's own Description first, because
/// that prose is maintained and a hardcoded chord list would rot. Explicit
/// overrides exist only where the description alone is not enough to tell.</para>
/// </summary>
internal static class Risk
{
    private static readonly Dictionary<string, RiskLevel> Overrides =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Push to talk and transmit lock: the two chords that key the rig
            // directly from Home.
            ["Ctrl+Space"] = RiskLevel.Transmits,
            ["Shift+Space"] = RiskLevel.Transmits,
            // Ctrl+J, G arms the TX test tone. Arming does not transmit, but it
            // silently replaces the microphone on the NEXT transmission, and an
            // operator who does not know it is armed will be heard as a tone.
            ["Ctrl+J, G"] = RiskLevel.Transmits,
            // Creating and releasing slices is exactly the state an operator
            // would have to put back by hand.
            ["Period"] = RiskLevel.Mutates,
            ["Comma"] = RiskLevel.Mutates,
            ["Shift+Comma"] = RiskLevel.Mutates,
            // Starts and stops writing a detailed diagnostic log. Harmless, but
            // the sweep itself uses it deliberately and should not trip it twice.
            ["Ctrl+J, Ctrl+D"] = RiskLevel.Mutates,
        };

    /// <summary>
    /// Checked FIRST, and it has to be. "Stop transmitting" contains the word
    /// transmit, so a naive keyword pass classifies the panic-stop key as a
    /// transmitting chord and then refuses to test it — which would leave the
    /// one key that exists to get an operator OUT of transmit as the one key
    /// the harness never presses.
    /// </summary>
    private static readonly string[] SafeWords =
    {
        "stop transmitting", "speak the", "speak this", "speak your", "speak log",
        "cancel leader mode", "list the leader key commands",
    };

    private static readonly string[] TransmitWords =
    {
        "transmit while held", "push to talk", "transmit lock", "send the cw message",
        "start the audio check", "test tone", "replaces your microphone",
    };

    private static readonly string[] MutateWords =
    {
        "create a new slice", "release", "toggle", "adjust", "set ", "tune", "cycle",
        "turn ", "mute", "unmute", "reset", "delete", "save", "load", "import", "export",
        "capture", "make this slice", "move transmit", "clear the transmit",
        "widen", "slide", "squeeze", "pull", "copy rit", "jump straight",
        "enter a frequency", "type a frequency", "round to", "step multiplier",
        "positive or negative", "next slice", "previous slice", "pan ",
    };

    private static readonly string[] DialogWords =
    {
        "opens the", "open the", "dialog", "picker", "enter a frequency",
        "type an exact value", "list the leader key commands", "speak the keys",
    };

    public static RiskLevel Classify(string chordDisplay, string description)
    {
        if (Overrides.TryGetValue(chordDisplay, out RiskLevel over)) return over;

        string d = description ?? "";
        if (SafeWords.Any(w => d.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return RiskLevel.Safe;
        if (TransmitWords.Any(w => d.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return RiskLevel.Transmits;
        if (MutateWords.Any(w => d.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return RiskLevel.Mutates;
        return RiskLevel.Safe;
    }

    /// <summary>
    /// True when pressing this is expected to put a window in front of the one
    /// under test. The sweep has to dismiss it before the next key, or every
    /// remaining result is measured against the wrong window.
    /// </summary>
    public static bool OpensSomething(string description) =>
        DialogWords.Any(w => (description ?? "").Contains(w, StringComparison.OrdinalIgnoreCase));

    public static RiskLevel Parse(string s) => s.ToLowerInvariant() switch
    {
        "safe" => RiskLevel.Safe,
        "mutates" or "mutate" => RiskLevel.Mutates,
        "transmits" or "transmit" => RiskLevel.Transmits,
        _ => throw new ArgumentException($"unknown risk level '{s}' (safe, mutates, transmits)", nameof(s)),
    };
}
