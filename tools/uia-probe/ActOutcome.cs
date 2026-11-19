namespace JJFlex.UiaProbe;

/// <summary>
/// How an <c>act</c> ended, as a script needs to tell it (#1067): done, refused
/// at the safety gate, or nothing to act on. Kept free of UI Automation types
/// so the exit codes can be tested without a desktop.
/// </summary>
internal enum ActOutcome
{
    /// <summary>The control was found and the operation ran.</summary>
    Done,
    /// <summary>Refused before anything was found or touched (an unarmed or valueless <c>--op value</c>).</summary>
    Refused,
    /// <summary>No control matched, or the index was past the matches.</summary>
    NotFound,
}

/// <summary>The exit code for each way an <c>act</c> ends: the codes the help text already lists.</summary>
internal static class ActExit
{
    /// <summary>"refused at the safety gate", as for every other refusal.</summary>
    public const int Refused = 6;

    /// <summary>"target window not found (for value: no such control)".</summary>
    public const int NotFound = 4;

    public static int CodeFor(ActOutcome outcome) => outcome switch
    {
        ActOutcome.Refused => Refused,
        ActOutcome.NotFound => NotFound,
        _ => 0,
    };
}
