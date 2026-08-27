using JJFlexWpf.Tests.Infrastructure;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The whole decision table of the guard that decides whether this tier may
/// build a window, exercised as a pure function.
/// </summary>
/// <remarks>
/// <para>
/// <b>Task #233.</b> <see cref="DeskGuard.Decide"/> used to ask one question —
/// is the UI thread on a desktop nobody is looking at — and answer "allowed" on
/// the strength of it. <see cref="PrivateDesktop"/> does nothing whatever about
/// sound, so a run could pass the guard and then play earcons and speech at
/// whoever was sitting there. Noel, from the operator's chair during a Tier 1
/// run: "it doesn't bring up menu windows, it does ding whenever it does the
/// tests, you just can't see them."
/// </para>
/// <para>
/// A guard that half-works is more dangerous than none, because it is trusted.
/// So audio suppression is a CONDITION of being allowed, not a line in the
/// report afterwards, and the whole table is asserted rather than the one row
/// somebody happened to think of.
/// </para>
/// <para>
/// <b>These need no dispatcher, no window and no desktop</b>, exactly like
/// <see cref="QuietRunTests"/>, and for the same reason: the run in which the
/// guard refuses is the run somebody is investigating.
/// </para>
/// </remarks>
public sealed class DeskGuardDecisionTests
{
    [Fact]
    public void IsolatedAndSilentIsTheOnlyWayToBeAllowedWithoutAHumanSayingSo()
    {
        Assert.Equal(
            DeskGuard.Verdict.AllowedIsolated,
            DeskGuard.Decide(isolationRequested: true,
                             isolation: DesktopIsolation.Isolated,
                             deskDeclaredFree: false,
                             audioSuppressed: true,
                             settingsIsolated: true));
    }

    [Fact]
    public void IsolatedButAudibleIsRefused()
    {
        // The row that did not exist. Before task #233 this returned
        // AllowedIsolated and the operator heard the run.
        var verdict = DeskGuard.Decide(isolationRequested: true,
                                       isolation: DesktopIsolation.Isolated,
                                       deskDeclaredFree: false,
                                       audioSuppressed: false,
                                       settingsIsolated: true);

        Assert.Equal(DeskGuard.Verdict.RefusedAudioNotSuppressed, verdict);
        Assert.False(DeskGuard.IsAllowed(verdict));
    }

    [Theory]
    [InlineData(DesktopIsolation.NotAttempted)]
    [InlineData(DesktopIsolation.CreateFailed)]
    [InlineData(DesktopIsolation.SwitchFailed)]
    public void AFailedIsolationIsRefusedEvenWhenTheRunIsSilent(DesktopIsolation isolation)
    {
        // And it reports the DESKTOP failure, not the audio one. Both would be
        // true; the desktop is the one that has to be fixed before the other
        // question even arises, and a message naming the wrong half sends the
        // reader after the wrong fault.
        Assert.Equal(
            DeskGuard.Verdict.RefusedIsolationFailed,
            DeskGuard.Decide(isolationRequested: true,
                             isolation: isolation,
                             deskDeclaredFree: false,
                             audioSuppressed: true,
                             settingsIsolated: true));
    }

    [Fact]
    public void IsolationSwitchedOffIsRefusedHoweverSilentTheRun()
    {
        Assert.Equal(
            DeskGuard.Verdict.RefusedIsolationDisabled,
            DeskGuard.Decide(isolationRequested: false,
                             isolation: DesktopIsolation.NotAttempted,
                             deskDeclaredFree: false,
                             audioSuppressed: true,
                             settingsIsolated: true));
    }

    [Theory]
    [InlineData(true, DesktopIsolation.Isolated, true)]
    [InlineData(true, DesktopIsolation.CreateFailed, false)]
    [InlineData(false, DesktopIsolation.NotAttempted, false)]
    public void AHumanWhoDeclaredTheDeskFreeWinsOutright(
        bool requested, DesktopIsolation isolation, bool silent)
    {
        // Someone who has stepped away has said the strongest thing available,
        // and "the machine is mine to use" is not a statement about one sense.
        // A run that refuses after being told it may proceed is a run nobody
        // can use.
        var verdict = DeskGuard.Decide(requested, isolation,
                                       deskDeclaredFree: true,
                                       audioSuppressed: silent,
                                       settingsIsolated: true);

        Assert.Equal(DeskGuard.Verdict.AllowedDeskDeclaredFree, verdict);
        Assert.True(DeskGuard.IsAllowed(verdict));
    }

    [Fact]
    public void SettingsNotIsolatedIsRefusedEvenWhenEverythingElseIsPerfect()
    {
        var verdict = DeskGuard.Decide(isolationRequested: true,
                                       isolation: DesktopIsolation.Isolated,
                                       deskDeclaredFree: false,
                                       audioSuppressed: true,
                                       settingsIsolated: false);

        Assert.Equal(DeskGuard.Verdict.RefusedSettingsNotIsolated, verdict);
        Assert.False(DeskGuard.IsAllowed(verdict));
    }

    [Theory]
    [InlineData(true, DesktopIsolation.Isolated, true)]
    [InlineData(true, DesktopIsolation.CreateFailed, false)]
    [InlineData(false, DesktopIsolation.NotAttempted, false)]
    public void NoHumanDeclarationWaivesTheSettings(
        bool requested, DesktopIsolation isolation, bool silent)
    {
        // The one refusal JJFLEX_TIER1_DESK_FREE does not lift, and the reason
        // is that it is not the consent being asked for. That variable says the
        // SCREEN and the SPEAKERS are free — a person has stepped away from a
        // desk. Nobody reads it as permission to rewrite their key map, and the
        // damage would outlast the run rather than evaporating with it. Consent
        // to be disturbed and consent to be modified are different consents.
        var verdict = DeskGuard.Decide(requested, isolation,
                                       deskDeclaredFree: true,
                                       audioSuppressed: silent,
                                       settingsIsolated: false);

        Assert.Equal(DeskGuard.Verdict.RefusedSettingsNotIsolated, verdict);
        Assert.False(DeskGuard.IsAllowed(verdict));
    }

    [Fact]
    public void ThisRunIsNotPointedAtTheOperatorsSettings()
    {
        // The standing assertion behind the condition above, and the one that
        // would have caught the original state: this tier constructs every
        // dialog in the app, a dialog reads and rewrites configuration as it
        // builds itself, and nothing here bound a settings root at all.
        Assert.True(TestSettingsRoot.Isolated,
            "Tier 1 would read and write the operator's own settings folder. "
            + (TestSettingsRoot.Failure ?? "No reason was recorded."));

        string live = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "JJFlexRadio");

        Assert.NotEqual(live, TestSettingsRoot.Directory, System.StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith(TestSettingsRoot.Directory, Radios.RadioConfig.AppDataRoot,
            System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryRefusalExplainsItselfToSomebodyWhoDidNotExpectIt()
    {
        foreach (var verdict in new[]
                 {
                     DeskGuard.Verdict.RefusedIsolationFailed,
                     DeskGuard.Verdict.RefusedIsolationDisabled,
                     DeskGuard.Verdict.RefusedAudioNotSuppressed,
                     DeskGuard.Verdict.RefusedSettingsNotIsolated,
                 })
        {
            string explanation = DeskGuard.Explain(verdict, lastWin32Error: 170);

            Assert.False(string.IsNullOrWhiteSpace(explanation),
                verdict + " refuses the run and says nothing about why.");
            Assert.Contains(DeskGuard.DeskFreeVariable, explanation, System.StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnAllowedRunSaysOnWhatGroundsItWasAllowed()
    {
        // "It was properly isolated" and "somebody waived the check" must never
        // look the same afterwards — that absence is how a failing isolation
        // went unnoticed in the first place.
        string isolated = DeskGuard.Describe(DeskGuard.Verdict.AllowedIsolated);
        string waived = DeskGuard.Describe(DeskGuard.Verdict.AllowedDeskDeclaredFree);

        Assert.NotEqual(isolated, waived);
        Assert.Contains("audio suppressed", isolated, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VISIBLE", waived, System.StringComparison.Ordinal);

        // And the audio refusal must not read like the desktop one, or a
        // reader chases the wrong fault.
        Assert.NotEqual(
            DeskGuard.Describe(DeskGuard.Verdict.RefusedIsolationFailed),
            DeskGuard.Describe(DeskGuard.Verdict.RefusedAudioNotSuppressed));
    }
}
