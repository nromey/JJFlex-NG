namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// The invariants, numbered as in the Sprint 33 Track A brief. Numbers are part
/// of the contract: findings are grouped by them and the report is read by
/// number, so do not renumber, only append.
/// </summary>
public enum Invariant
{
    /// <summary>1. Every focusable control exposes a non-empty automation Name.</summary>
    FocusableHasName = 1,

    /// <summary>2. Nothing focusable is missing from the automation tree, and no peer throws while being walked.</summary>
    AutomationSubtreeComplete = 2,

    /// <summary>3. Every control that declares help text actually has some.</summary>
    HelpTextNotEmpty = 3,

    /// <summary>4. Focus cycles are conserved - N moves produce N focus changes.</summary>
    FocusConserved = 4,

    /// <summary>5. No duplicate automation ids within one window.</summary>
    UniqueAutomationIds = 5,

    /// <summary>6. Every actionable control is reachable from the keyboard, not merely present and named.</summary>
    KeyboardReachable = 6,

    /// <summary>7. No surface is wired to a delegate that nothing ever assigns.</summary>
    NoDeadDelegateSurface = 7,
}

/// <summary>One defect, with enough identity to find the control again.</summary>
public sealed record Finding(Invariant Invariant, string Dialog, string Control, string Detail)
{
    public override string ToString() => $"[{(int)Invariant}] {Dialog}: {Control} - {Detail}";
}

/// <summary>Everything learned about one dialog in one pass.</summary>
public sealed class DialogReport
{
    public required string Dialog { get; init; }
    public string? SkipReason { get; init; }
    public RealizationStrategy Strategy { get; init; }
    public bool LoadedFired { get; init; }
    public int PeerCount { get; init; }
    public int FocusableCount { get; init; }
    public int TabStopCount { get; init; }
    public string? FocusWalkDiagnostic { get; init; }
    public List<Finding> Findings { get; } = new();

    public bool Skipped => SkipReason != null;
}
