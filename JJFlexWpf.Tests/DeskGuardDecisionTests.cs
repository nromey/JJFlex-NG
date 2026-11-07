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

    // ── Task #906: a process that STARTED on a hidden desktop ──────────────
    //
    // The launcher (tools/deskrun) starts the whole test process on a desktop
    // nobody can see, and the guard reports that as StartedOnPrivateDesktop.
    // It must be treated EXACTLY like Isolated: allowed only with sound
    // suppressed, and never past the settings refusal, which comes first and
    // which nothing waives. The rows above are unchanged; these sit beside them.

    [Fact]
    public void StartedOnAHiddenDesktopAndSilentIsAllowedAsIsolated()
    {
        Assert.Equal(
            DeskGuard.Verdict.AllowedIsolated,
            DeskGuard.Decide(isolationRequested: true,
                             isolation: DesktopIsolation.StartedOnPrivateDesktop,
                             deskDeclaredFree: false,
                             audioSuppressed: true,
                             settingsIsolated: true));
    }

    [Fact]
    public void StartedOnAHiddenDesktopButAudibleIsRefused()
    {
        // The launcher hides the windows. It does nothing about sound, so a run
        // through it is still refused unless QuietRun read back silence.
        Assert.Equal(
            DeskGuard.Verdict.RefusedAudioNotSuppressed,
            DeskGuard.Decide(isolationRequested: true,
                             isolation: DesktopIsolation.StartedOnPrivateDesktop,
                             deskDeclaredFree: false,
                             audioSuppressed: false,
                             settingsIsolated: true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void StartedOnAHiddenDesktopNeverGetsPastTheSettingsRefusal(bool deskFree, bool silent)
    {
        Assert.Equal(
            DeskGuard.Verdict.RefusedSettingsNotIsolated,
            DeskGuard.Decide(isolationRequested: true,
                             isolation: DesktopIsolation.StartedOnPrivateDesktop,
                             deskDeclaredFree: deskFree,
                             audioSuppressed: silent,
                             settingsIsolated: false));
    }

    [Fact]
    public void StartedOnAHiddenDesktopDoesNotOverrideIsolationSwitchedOff()
    {
        // JJFLEX_TIER1_PRIVATE_DESKTOP=0 still means what it said. The guard
        // never even reads the starting desktop on that path, but the decision
        // must not depend on that.
        Assert.Equal(
            DeskGuard.Verdict.RefusedIsolationDisabled,
            DeskGuard.Decide(isolationRequested: false,
                             isolation: DesktopIsolation.StartedOnPrivateDesktop,
                             deskDeclaredFree: false,
                             audioSuppressed: true,
                             settingsIsolated: true));
    }

    // ── Task #906 round 2 (Noel's ruling C9): the declaration is NOT consulted
    //    on the hidden-desktop route ─────────────────────────────────────────
    //
    // Created state first. On StartedOnPrivateDesktop the windows are hidden
    // whatever the declaration says, so all it could waive is SOUND, which is
    // what that route must still prove. Every other route keeps the declaration
    // exactly as it was: a person's word at a real desk. These rows never set
    // JJFLEX_TIER1_DESK_FREE; the decision is a pure function and its rows are
    // the proof.

    [Fact]
    public void DeclaredFreeOnTheHiddenRouteButAudibleIsRefused()
    {
        // THE row the ruling exists for. Before round 2 this returned
        // AllowedDeskDeclaredFree, so any route that put the variable back
        // after deskrun removed it (Codex found three) waived the sound check.
        var verdict = DeskGuard.Decide(isolationRequested: true,
                                       isolation: DesktopIsolation.StartedOnPrivateDesktop,
                                       deskDeclaredFree: true,
                                       audioSuppressed: false,
                                       settingsIsolated: true);

        Assert.Equal(DeskGuard.Verdict.RefusedAudioNotSuppressed, verdict);
        Assert.False(DeskGuard.IsAllowed(verdict));
    }

    [Fact]
    public void DeclaredFreeOnTheHiddenRouteWithSettingsInScopeIsRefusedOnSettings()
    {
        Assert.Equal(
            DeskGuard.Verdict.RefusedSettingsNotIsolated,
            DeskGuard.Decide(isolationRequested: true,
                             isolation: DesktopIsolation.StartedOnPrivateDesktop,
                             deskDeclaredFree: true,
                             audioSuppressed: true,
                             settingsIsolated: false));
    }

    [Fact]
    public void DeclaredFreeOnTheHiddenRouteAndSilentIsAllowedAsIsolatedNotAsWaived()
    {
        // Allowed, but on the grounds of isolation and silence, so the report
        // says so. "Somebody waived it" would be false here: nothing was waived.
        Assert.Equal(
            DeskGuard.Verdict.AllowedIsolated,
            DeskGuard.Decide(isolationRequested: true,
                             isolation: DesktopIsolation.StartedOnPrivateDesktop,
                             deskDeclaredFree: true,
                             audioSuppressed: true,
                             settingsIsolated: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeclaredFreeOnTheHiddenRouteWithIsolationSwitchedOffIsRefused(bool silent)
    {
        // UiThread reads the starting desktop even when isolation is switched
        // off, so JJFLEX_TIER1_PRIVATE_DESKTOP=0 plus the declaration cannot
        // put the waiver back on this route.
        Assert.Equal(
            DeskGuard.Verdict.RefusedIsolationDisabled,
            DeskGuard.Decide(isolationRequested: false,
                             isolation: DesktopIsolation.StartedOnPrivateDesktop,
                             deskDeclaredFree: true,
                             audioSuppressed: silent,
                             settingsIsolated: true));
    }

    [Theory]
    [InlineData(DesktopIsolation.NotAttempted, true, true)]
    [InlineData(DesktopIsolation.NotAttempted, true, false)]
    [InlineData(DesktopIsolation.NotAttempted, false, true)]
    [InlineData(DesktopIsolation.NotAttempted, false, false)]
    [InlineData(DesktopIsolation.Isolated, true, true)]
    [InlineData(DesktopIsolation.Isolated, true, false)]
    [InlineData(DesktopIsolation.Isolated, false, false)]
    [InlineData(DesktopIsolation.CreateFailed, true, true)]
    [InlineData(DesktopIsolation.CreateFailed, true, false)]
    [InlineData(DesktopIsolation.CreateFailed, false, false)]
    [InlineData(DesktopIsolation.SwitchFailed, true, true)]
    [InlineData(DesktopIsolation.SwitchFailed, true, false)]
    [InlineData(DesktopIsolation.SwitchFailed, false, false)]
    public void DeclaredFreeOnEveryOtherRouteBehavesExactlyAsBefore(
        DesktopIsolation isolation, bool requested, bool silent)
    {
        // A person at a real desk: the declaration still wins outright, sound
        // included. Round 2 does not touch this.
        Assert.Equal(
            DeskGuard.Verdict.AllowedDeskDeclaredFree,
            DeskGuard.Decide(isolationRequested: requested,
                             isolation: isolation,
                             deskDeclaredFree: true,
                             audioSuppressed: silent,
                             settingsIsolated: true));
    }

    [Fact]
    public void OnlyTheHiddenRouteIgnoresTheDeclarationIncludingAnyValueAddedLater()
    {
        // By enumeration, so a new DesktopIsolation value cannot silently join
        // or leave the route that ignores the declaration.
        foreach (DesktopIsolation isolation in Enum.GetValues(typeof(DesktopIsolation)))
        {
            var verdict = DeskGuard.Decide(isolationRequested: true,
                                           isolation: isolation,
                                           deskDeclaredFree: true,
                                           audioSuppressed: false,
                                           settingsIsolated: true);

            Assert.Equal(isolation == DesktopIsolation.StartedOnPrivateDesktop
                             ? DeskGuard.Verdict.RefusedAudioNotSuppressed
                             : DeskGuard.Verdict.AllowedDeskDeclaredFree,
                         verdict);
        }
    }

    [Fact]
    public void OnTheHiddenRouteTheRefusalDoesNotOfferTheDeclarationAsTheWayOut()
    {
        // The text must not tell somebody to set a variable this route ignores.
        string hidden = DeskGuard.Explain(DeskGuard.Verdict.RefusedAudioNotSuppressed, 0,
                                          DesktopIsolation.StartedOnPrivateDesktop);
        Assert.Contains("does NOT lift this", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("=1 for this run", hidden, StringComparison.Ordinal);

        string disabled = DeskGuard.Explain(DeskGuard.Verdict.RefusedIsolationDisabled, 0,
                                            DesktopIsolation.StartedOnPrivateDesktop);
        Assert.Contains("not consulted", disabled, StringComparison.Ordinal);
        Assert.DoesNotContain("=1 for this run", disabled, StringComparison.Ordinal);

        // Off the hidden route, the declaration is still offered.
        Assert.Contains("=1 for this run",
                        DeskGuard.Explain(DeskGuard.Verdict.RefusedAudioNotSuppressed, 0,
                                          DesktopIsolation.Isolated),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedIsolationNamesTheStepThatFailed()
    {
        // Measured: under a plain dotnet test the desktop IS created and moving
        // the thread fails with 170. The text used to say "could not be created".
        string moved = DeskGuard.Explain(DeskGuard.Verdict.RefusedIsolationFailed, 170,
                                         DesktopIsolation.SwitchFailed);
        Assert.Contains("MOVING the test thread", moved, StringComparison.Ordinal);
        Assert.DoesNotContain("Creating that desktop FAILED", moved, StringComparison.Ordinal);

        string created = DeskGuard.Explain(DeskGuard.Verdict.RefusedIsolationFailed, 5,
                                           DesktopIsolation.CreateFailed);
        Assert.Contains("Creating that desktop FAILED", created, StringComparison.Ordinal);

        Assert.DoesNotContain("could not be created",
                              DeskGuard.Describe(DeskGuard.Verdict.RefusedIsolationFailed),
                              StringComparison.Ordinal);
    }

    [Fact]
    public void EveryIsolationValueOtherThanTheTwoIsolatedRoutesRefuses()
    {
        // Including any value somebody adds later: the guard is fail-closed by
        // enumeration, not by remembering to extend a list.
        foreach (DesktopIsolation isolation in Enum.GetValues(typeof(DesktopIsolation)))
        {
            var verdict = DeskGuard.Decide(isolationRequested: true,
                                           isolation: isolation,
                                           deskDeclaredFree: false,
                                           audioSuppressed: true,
                                           settingsIsolated: true);

            bool isolatedRoute = isolation is DesktopIsolation.Isolated
                                 or DesktopIsolation.StartedOnPrivateDesktop;
            Assert.Equal(isolatedRoute ? DeskGuard.Verdict.AllowedIsolated
                                       : DeskGuard.Verdict.RefusedIsolationFailed,
                         verdict);
        }
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
