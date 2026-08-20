using JJFlexWpf.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// One test per invariant per dialog. A failure names one invariant in one
/// dialog and lists the controls that broke it, which is what makes the output
/// triageable one item at a time.
/// </summary>
public sealed class DialogInvariantTests
{
    private readonly ITestOutputHelper _output;

    public DialogInvariantTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Dialogs => Sweep.DialogNames();

    [Theory]
    [MemberData(nameof(Dialogs))]
    public void Invariant_1_every_focusable_control_has_a_name(string dialog)
        => Assert(dialog, Invariant.FocusableHasName);

    [Theory]
    [MemberData(nameof(Dialogs))]
    public void Invariant_2_the_automation_tree_matches_what_is_focusable(string dialog)
        => Assert(dialog, Invariant.AutomationSubtreeComplete);

    [Theory]
    [MemberData(nameof(Dialogs))]
    public void Invariant_3_declared_help_text_is_not_empty(string dialog)
        => Assert(dialog, Invariant.HelpTextNotEmpty);

    [Theory]
    [MemberData(nameof(Dialogs))]
    public void Invariant_4_focus_cycles_are_conserved(string dialog)
        => Assert(dialog, Invariant.FocusConserved);

    [Theory]
    [MemberData(nameof(Dialogs))]
    public void Invariant_5_automation_ids_are_unique_within_a_window(string dialog)
        => Assert(dialog, Invariant.UniqueAutomationIds);

    [Theory]
    [MemberData(nameof(Dialogs))]
    public void Invariant_6_every_actionable_control_is_reachable_from_the_keyboard(string dialog)
        => Assert(dialog, Invariant.KeyboardReachable);

    private void Assert(string dialog, Invariant invariant)
    {
        var report = Sweep.ReportFor(dialog);

        if (report.Skipped)
        {
            // A skip is recorded, never silent: it is written to the test output
            // here and listed with its reason in the generated inventory. It is
            // not a failure, because "this dialog needs a live radio" is a
            // coverage fact, not a defect.
            _output.WriteLine($"SKIPPED - {dialog} was not inspected: {report.SkipReason}");
            return;
        }

        if (!report.LoadedFired && invariant == Invariant.AutomationSubtreeComplete)
        {
            _output.WriteLine($"{dialog}: Loaded never fired, so emptiness findings were suppressed for this dialog.");
        }

        var findings = report.Findings.Where(f => f.Invariant == invariant).ToList();
        _output.WriteLine(
            $"{dialog}: {report.PeerCount} automation peers, {report.FocusableCount} focusable controls, " +
            $"{report.TabStopCount} tab stops, Loaded {(report.LoadedFired ? "fired" : "did NOT fire")}.");
        if (report.FocusWalkDiagnostic != null) _output.WriteLine("Focus walk: " + report.FocusWalkDiagnostic);

        if (findings.Count == 0) return;

        var message = new System.Text.StringBuilder();
        message.AppendLine($"{dialog} breaks invariant {(int)invariant} ({invariant}) in {findings.Count} place(s):");
        foreach (var finding in findings)
            message.AppendLine($"  - {finding.Control}: {finding.Detail}");

        Xunit.Assert.Fail(message.ToString());
    }
}
