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
    /// <summary>
    /// The origin each pre-round-3 row describes: the hidden route reads as a
    /// hidden desktop, and every other row is a person at a real desk, which
    /// Windows reports positively as WinSta0\Default receiving input. Round 3
    /// (task #906) made the origin a fact of its own; these rows keep their
    /// meaning by stating it.
    /// </summary>
    private static LaunchOrigin OriginOf(DesktopIsolation isolation)
        => isolation == DesktopIsolation.StartedOnPrivateDesktop
            ? LaunchOrigin.HiddenDesktop
            : LaunchOrigin.InteractiveDesktop;

    [Fact]
    public void IsolatedAndSilentIsTheOnlyWayToBeAllowedWithoutAHumanSayingSo()
    {
        Assert.Equal(
            DeskGuard.Verdict.AllowedIsolated,
            DeskGuard.Decide(isolationRequested: true,
                             isolation: DesktopIsolation.Isolated,
                             origin: LaunchOrigin.InteractiveDesktop,
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
                                       origin: LaunchOrigin.InteractiveDesktop,
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
                             origin: OriginOf(isolation),
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
                             origin: LaunchOrigin.InteractiveDesktop,
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
        var verdict = DeskGuard.Decide(requested, isolation, OriginOf(isolation),
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
                                       origin: LaunchOrigin.InteractiveDesktop,
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
        var verdict = DeskGuard.Decide(requested, isolation, OriginOf(isolation),
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
                             origin: LaunchOrigin.HiddenDesktop,
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
                             origin: LaunchOrigin.HiddenDesktop,
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
                             origin: LaunchOrigin.HiddenDesktop,
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
                             origin: LaunchOrigin.HiddenDesktop,
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
                                       origin: LaunchOrigin.HiddenDesktop,
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
                             origin: LaunchOrigin.HiddenDesktop,
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
                             origin: LaunchOrigin.HiddenDesktop,
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
                             origin: LaunchOrigin.HiddenDesktop,
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
        // A person at a real desk, POSITIVELY read as such (round 3): the
        // declaration still wins outright, sound included.
        Assert.Equal(
            DeskGuard.Verdict.AllowedDeskDeclaredFree,
            DeskGuard.Decide(isolationRequested: requested,
                             isolation: isolation,
                             origin: OriginOf(isolation),
                             deskDeclaredFree: true,
                             audioSuppressed: silent,
                             settingsIsolated: true));
    }

    [Fact]
    public void OnlyAPositiveReadingOfTheHumansDeskConsultsTheDeclarationIncludingAnyValueAddedLater()
    {
        // By enumeration of BOTH facts, so a new DesktopIsolation or
        // LaunchOrigin value cannot silently join the route that consults the
        // declaration. Round 2's version of this row enumerated isolation only,
        // and asserted the waiver on every value but the hidden one: exactly
        // the shape that let a failed read fall back to the waiver (Codex,
        // round 2). The waiver now needs the positive human reading.
        foreach (DesktopIsolation isolation in Enum.GetValues(typeof(DesktopIsolation)))
        foreach (LaunchOrigin origin in Enum.GetValues(typeof(LaunchOrigin)))
        foreach (bool requested in new[] { true, false })
        {
            var verdict = DeskGuard.Decide(isolationRequested: requested,
                                           isolation: isolation,
                                           origin: origin,
                                           deskDeclaredFree: true,
                                           audioSuppressed: false,
                                           settingsIsolated: true);

            bool waived = origin == LaunchOrigin.InteractiveDesktop
                          && isolation != DesktopIsolation.StartedOnPrivateDesktop;
            Assert.True(waived == (verdict == DeskGuard.Verdict.AllowedDeskDeclaredFree),
                        "isolation " + isolation + ", origin " + origin + ", requested " + requested
                        + ": " + verdict);
            if (!waived) Assert.False(DeskGuard.IsAllowed(verdict), isolation + "/" + origin + ": " + verdict);
        }
    }

    // ── Task #906 round 3: the declaration is consulted only on a POSITIVE
    //    reading of the person's own desk ─────────────────────────────────
    //
    // Created state first: each starting reading Windows could report, through
    // the real PrivateDesktop.JudgeLaunchOrigin, crossed with the declaration
    // on and with EVERY isolation result the attempt could fall back to. The
    // waiver may appear only on the first row. Codex's round-2 fault is the
    // fourth: deskrun's hidden desktop with the input desktop unreadable,
    // which used to fall back to SwitchFailed or NotAttempted and get the
    // waiver back. No row sets JJFLEX_TIER1_DESK_FREE.

    private const string HiddenName = "JJFlexDeskRun_1234_0123456789ab";

    public static IEnumerable<object?[]> StartingReadings() => new[]
    {
        // station, thread desktop, input desktop, positively the human desk?
        new object?[] { "WinSta0", "Default", "Default", true },
        new object?[] { "winsta0", "default", "DEFAULT", true },
        new object?[] { "WinSta0", HiddenName, "Default", false },     // hidden, accepted
        new object?[] { "WinSta0", HiddenName, null, false },          // Codex's fault: input unreadable on the hidden desktop
        new object?[] { "WinSta0", "Default", null, false },           // input unreadable on Default (locked workstation)
        new object?[] { "WinSta0", "Default", "Winlogon", false },     // declared free, then locked: input readable as Winlogon
        new object?[] { "WinSta0", "Default", "Screen-saver", false },
        new object?[] { "WinSta0", "Default", HiddenName, false },     // somebody switched input elsewhere
        new object?[] { "WinSta0", null, "Default", false },           // thread desktop unreadable
        new object?[] { "WinSta0", "", "Default", false },
        new object?[] { "Service-0x0-3e7$", "Default", "Default", false }, // another window station
        new object?[] { null, "Default", "Default", false },           // station unreadable
        new object?[] { "WinSta0", "Winlogon", "Winlogon", false },
        new object?[] { "WinSta0", "Screen-saver", "Screen-saver", false },
        new object?[] { "WinSta0", HiddenName, HiddenName, false },    // a hidden desktop somebody switched to
    };

    [Theory]
    [MemberData(nameof(StartingReadings))]
    public void TheDeclarationIsConsultedOnlyOnAPositiveReadingOfTheHumansDesk(
        string? station, string? thread, string? input, bool humanDesk)
    {
        LaunchOrigin origin = PrivateDesktop.JudgeLaunchOrigin(station, thread, input);
        Assert.Equal(humanDesk, origin == LaunchOrigin.InteractiveDesktop);

        foreach (DesktopIsolation isolation in Enum.GetValues(typeof(DesktopIsolation)))
        foreach (bool requested in new[] { true, false })
        foreach (bool silent in new[] { true, false })
        {
            // The isolation result is whatever the attempt fell back to; it is
            // crossed in full because round 2's failure was precisely a
            // fallback value carrying the waiver.
            var verdict = DeskGuard.Decide(isolationRequested: requested,
                                           isolation: isolation,
                                           origin: origin,
                                           deskDeclaredFree: true,
                                           audioSuppressed: silent,
                                           settingsIsolated: true);

            bool expectWaiver = humanDesk && isolation != DesktopIsolation.StartedOnPrivateDesktop;
            Assert.True(expectWaiver == (verdict == DeskGuard.Verdict.AllowedDeskDeclaredFree),
                        "reading " + (station ?? "null") + "\\" + (thread ?? "null") + ", input " + (input ?? "null")
                        + ", isolation " + isolation + ", requested " + requested + ", silent " + silent
                        + ": " + verdict);

            // Off the human desk the run is allowed only the way an undeclared
            // run would be: isolated AND silent.
            if (!humanDesk)
                Assert.Equal(DeskGuard.Decide(requested, isolation, origin, deskDeclaredFree: false,
                                              audioSuppressed: silent, settingsIsolated: true),
                             verdict);
        }
    }

    [Theory]
    [InlineData(DesktopIsolation.SwitchFailed, true)]
    [InlineData(DesktopIsolation.NotAttempted, false)]
    [InlineData(DesktopIsolation.Isolated, true)]
    [InlineData(DesktopIsolation.CreateFailed, true)]
    public void CodexsRoundTwoFaultNoLongerReopensTheWaiver(DesktopIsolation fellBackTo, bool requested)
    {
        // Round 2, verbatim: on deskrun's hidden desktop the input desktop could
        // not be read, the attempt fell back, and Decide(requested, fellBackTo,
        // declaration: true, audio: false, settings: true) returned
        // AllowedDeskDeclaredFree. The origin of that reading is Unknown.
        LaunchOrigin origin = PrivateDesktop.JudgeLaunchOrigin("WinSta0", "JJFlexDeskRun_11744_5aba01fb7927", null);
        Assert.Equal(LaunchOrigin.Unknown, origin);

        var verdict = DeskGuard.Decide(requested, fellBackTo, origin, deskDeclaredFree: true,
                                       audioSuppressed: false, settingsIsolated: true);

        Assert.NotEqual(DeskGuard.Verdict.AllowedDeskDeclaredFree, verdict);
        Assert.False(DeskGuard.IsAllowed(verdict));
    }

    [Fact]
    public void ADeclaredDeskThatIsThenLockedIsRefusedAndTheTextSaysWhy()
    {
        // The accepted cost of failing closed: the person stepped away, said
        // so, and locked the workstation. The input desktop is then Winlogon.
        LaunchOrigin origin = PrivateDesktop.JudgeLaunchOrigin("WinSta0", "Default", "Winlogon");
        var verdict = DeskGuard.Decide(true, DesktopIsolation.SwitchFailed, origin, deskDeclaredFree: true,
                                       audioSuppressed: true, settingsIsolated: true);

        Assert.Equal(DeskGuard.Verdict.RefusedIsolationFailed, verdict);
        string text = DeskGuard.Explain(verdict, 170, DesktopIsolation.SwitchFailed, origin);
        Assert.Contains("not consulted", text, StringComparison.Ordinal);
        Assert.Contains("locked", text, StringComparison.Ordinal);
        Assert.DoesNotContain("=1 for this run", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOriginNobodySetFailsClosed()
    {
        // The zero value is Unknown, so a default(LaunchOrigin) consults nothing.
        Assert.Equal(LaunchOrigin.Unknown, default(LaunchOrigin));
        Assert.False(DeskGuard.DeclarationConsulted(DesktopIsolation.NotAttempted, default));
    }

    [Fact]
    public void OnTheHiddenRouteTheRefusalDoesNotOfferTheDeclarationAsTheWayOut()
    {
        // The text must not tell somebody to set a variable this route ignores.
        string hidden = DeskGuard.Explain(DeskGuard.Verdict.RefusedAudioNotSuppressed, 0,
                                          DesktopIsolation.StartedOnPrivateDesktop, LaunchOrigin.HiddenDesktop);
        Assert.Contains("does NOT lift this", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("=1 for this run", hidden, StringComparison.Ordinal);

        string disabled = DeskGuard.Explain(DeskGuard.Verdict.RefusedIsolationDisabled, 0,
                                            DesktopIsolation.StartedOnPrivateDesktop, LaunchOrigin.HiddenDesktop);
        Assert.Contains("not consulted", disabled, StringComparison.Ordinal);
        Assert.DoesNotContain("=1 for this run", disabled, StringComparison.Ordinal);

        // Off the hidden route, the declaration is still offered.
        Assert.Contains("=1 for this run",
                        DeskGuard.Explain(DeskGuard.Verdict.RefusedAudioNotSuppressed, 0,
                                          DesktopIsolation.Isolated, LaunchOrigin.InteractiveDesktop),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedIsolationNamesTheStepThatFailed()
    {
        // Measured: under a plain dotnet test the desktop IS created and moving
        // the thread fails with 170. The text used to say "could not be created".
        string moved = DeskGuard.Explain(DeskGuard.Verdict.RefusedIsolationFailed, 170,
                                         DesktopIsolation.SwitchFailed, LaunchOrigin.InteractiveDesktop);
        Assert.Contains("MOVING the test thread", moved, StringComparison.Ordinal);
        Assert.DoesNotContain("Creating that desktop FAILED", moved, StringComparison.Ordinal);

        string created = DeskGuard.Explain(DeskGuard.Verdict.RefusedIsolationFailed, 5,
                                           DesktopIsolation.CreateFailed, LaunchOrigin.InteractiveDesktop);
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
                                           origin: OriginOf(isolation),
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
            foreach (DesktopIsolation isolation in Enum.GetValues(typeof(DesktopIsolation)))
            foreach (LaunchOrigin origin in Enum.GetValues(typeof(LaunchOrigin)))
            {
                string explanation = DeskGuard.Explain(verdict, 170, isolation, origin);

                Assert.False(string.IsNullOrWhiteSpace(explanation),
                    verdict + " refuses the run and says nothing about why.");
                Assert.Contains(DeskGuard.DeskFreeVariable, explanation, System.StringComparison.Ordinal);

                // Round 3: the text offers the declaration exactly where Decide
                // would read it, never where it would not.
                bool offered = explanation.Contains("=1 for this run", System.StringComparison.Ordinal);
                Assert.True(!offered || DeskGuard.DeclarationConsulted(isolation, origin),
                    verdict + " offers " + DeskGuard.DeskFreeVariable + " on " + isolation + "/" + origin
                    + ", where the guard does not read it.");
            }
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
