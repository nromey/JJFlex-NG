using System.Collections;
using System.IO;
using JJFlex.DeskRun;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Task #906: the hidden-desktop launcher's rules, from the very source file
/// <c>tools/deskrun</c> compiles (linked into this project).
/// </summary>
/// <remarks>
/// Windowless: string and dictionary work only. Nothing here starts a process
/// or sets an environment variable; the desk-free declaration appears only as
/// a key in a dictionary handed to a pure function, the same way
/// <see cref="DeskGuardDecisionTests"/> hands <c>deskDeclaredFree: true</c> to
/// <c>DeskGuard.Decide</c>.
/// </remarks>
public sealed class DeskRunRulesTests
{
    private const string Project = "JJFlexWpf.Tests/JJFlexWpf.Tests.csproj";

    private static DeskRunRules.Parsed? Parse(params string[] args)
        => DeskRunRules.Parse(args, _ => true, out _);

    // ── the child's environment ──────────────────────────────────────────

    [Theory]
    [InlineData("JJFLEX_TIER1_DESK_FREE")]
    [InlineData("jjflex_tier1_desk_free")]
    [InlineData("Jjflex_Tier1_Desk_Free")]
    public void TheChildNeverInheritsTheDeskFreeDeclarationInAnySpelling(string spelling)
    {
        IDictionary parent = new Hashtable(StringComparer.Ordinal)
        {
            ["PATH"] = @"C:\Windows",
            [spelling] = "1",
        };

        var child = DeskRunRules.ChildEnvironment(parent, "WinSta0\\X");

        Assert.DoesNotContain(child.Keys,
            k => string.Equals(k, DeskRunRules.DeskFreeVariable, StringComparison.OrdinalIgnoreCase));
        Assert.True(DeskRunRules.CarriesDeskFree(parent));
        Assert.DoesNotContain(DeskRunRules.DeskFreeVariable,
            DeskRunRules.EnvironmentBlock(child), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheChildKeepsEverythingElseAndIsToldNothingThatAllowsARun()
    {
        IDictionary parent = new Hashtable(StringComparer.Ordinal)
        {
            ["PATH"] = @"C:\Windows",
            ["JJFLEX_TIER1_PRIVATE_DESKTOP"] = "0",
        };

        var child = DeskRunRules.ChildEnvironment(parent, "WinSta0\\X");

        Assert.Equal(@"C:\Windows", child["PATH"]);
        // Passed through untouched: if somebody switched isolation off, the run
        // refuses with RefusedIsolationDisabled rather than the launcher
        // quietly turning it back on.
        Assert.Equal("0", child["JJFLEX_TIER1_PRIVATE_DESKTOP"]);
        Assert.Equal("WinSta0\\X", child[DeskRunRules.AnnouncedDesktopVariable]);
        Assert.Equal("1", child["MSBUILDDISABLENODEREUSE"]);
        Assert.False(DeskRunRules.CarriesDeskFree(parent));
    }

    [Fact]
    public void TheEnvironmentBlockIsSortedAndNulSeparated()
    {
        var env = DeskRunRules.ChildEnvironment(
            new Hashtable { ["b"] = "2", ["A"] = "1" }, "WinSta0\\X");
        string block = DeskRunRules.EnvironmentBlock(env);

        var entries = block.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(entries.OrderBy(e => e.Split('=')[0], StringComparer.OrdinalIgnoreCase), entries);
        Assert.EndsWith("\0", block, StringComparison.Ordinal);
    }

    // ── the arguments ────────────────────────────────────────────────────

    [Fact]
    public void ARunNamesExactlyOneProjectAndGetsNoBuild()
    {
        var p = Parse(Project, "-c", "Debug", "--filter", "FullyQualifiedName~X");

        Assert.NotNull(p);
        Assert.Equal(DeskRunRules.DefaultTimeoutSeconds, p!.TimeoutSeconds);
        Assert.Equal(new[] { "test", Project, "--no-build", "-c", "Debug", "--filter", "FullyQualifiedName~X" },
                     DeskRunRules.DotnetArguments(p));
    }

    [Fact]
    public void NoProjectIsRefused()
    {
        // A bare dotnet test builds and runs every test project in the solution.
        Assert.Null(DeskRunRules.Parse(Array.Empty<string>(), _ => true, out string e1));
        Assert.Contains("project", e1, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Parse("--timeout", "30"));
        Assert.Null(Parse("--filter", "X"));
    }

    [Theory]
    [InlineData("JJFlexRadio.sln")]
    [InlineData("JJFlexRadio.slnx")]
    public void ASolutionIsRefusedInEitherPosition(string solution)
    {
        Assert.Null(Parse(solution));
        Assert.Null(Parse(Project, solution));
        Assert.Null(Parse(Project, "Radios.Tests/Radios.Tests.csproj"));
    }

    [Fact]
    public void AMissingProjectIsRefused()
    {
        Assert.Null(DeskRunRules.Parse(new[] { Project }, _ => false, out string e));
        Assert.Contains("does not exist", e, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("ten")]
    public void ATimeoutMustBeAPositiveNumberOfSeconds(string value)
    {
        Assert.Null(Parse("--timeout", value, Project));
    }

    [Fact]
    public void TheTimeoutIsSettable()
    {
        Assert.Equal(45, Parse("--timeout", "45", Project)!.TimeoutSeconds);
    }

    [Fact]
    public void NoBuildIsNotDoubled()
    {
        var p = Parse(Project, "--no-build");
        Assert.Single(DeskRunRules.DotnetArguments(p!), a => a == "--no-build");
    }

    // ── repair round 1, B1: the ALLOWLIST ────────────────────────────────
    //
    // Codex showed VSTest installs environment variables in testhost AFTER the
    // launcher builds dotnet's environment: from --environment, from a
    // runsettings file, from inline settings after --, and from properties
    // that pick a settings file. Every such route is refused here, in every
    // spelling, and only the options a window-test run needs pass.

    [Theory]
    // --environment / -e, both forms, any case
    [InlineData("--environment", "CANARY=1")]
    [InlineData("-e", "CANARY=1")]
    [InlineData("--environment=CANARY=1")]
    [InlineData("--environment:CANARY=1")]
    [InlineData("--ENVIRONMENT", "CANARY=1")]
    [InlineData("--Environment=CANARY=1")]
    [InlineData("-E", "CANARY=1")]
    // a runsettings file
    [InlineData("--settings", "x.runsettings")]
    [InlineData("-s", "x.runsettings")]
    [InlineData("--settings=x.runsettings")]
    [InlineData("--settings:x.runsettings")]
    [InlineData("--SETTINGS", "x.runsettings")]
    [InlineData("-S", "x.runsettings")]
    // inline run settings after a bare --
    [InlineData("--", "RunConfiguration.EnvironmentVariables.CANARY=1")]
    [InlineData("--")]
    // properties that choose settings, in every switch spelling and case
    [InlineData("-p:RunSettingsFilePath=x.runsettings")]
    [InlineData("/p:RunSettingsFilePath=x.runsettings")]
    [InlineData("-p:VSTestSetting=x.runsettings")]
    [InlineData("-property:VSTestSetting=x.runsettings")]
    [InlineData("--property:VSTestCLIRunSettings=RunConfiguration.X=1")]
    [InlineData("/property:VSTestCollect=x")]
    [InlineData("-p:vstestsetting=x.runsettings")]
    [InlineData("-P:RunSettingsFilePath=x.runsettings")]
    [InlineData("-p", "RunSettingsFilePath=x.runsettings")]
    // a second property riding along with Platform
    [InlineData("-p:Platform=x64;VSTestSetting=x.runsettings")]
    [InlineData("-p:Platform=x64,VSTestSetting=x.runsettings")]
    [InlineData("-p:Platform=x64 -p:VSTestSetting=x")]
    // other properties, even harmless-looking ones
    [InlineData("-p:Configuration=Debug")]
    // response files, collectors, adapters, diagnostics
    [InlineData("@more.rsp")]
    [InlineData("--collect", "XPlat Code Coverage")]
    [InlineData("--test-adapter-path", ".")]
    [InlineData("--diag", "log.txt")]
    // loggers other than console and trx, and smuggling in a filter
    [InlineData("--logger", "custom;x=1")]
    [InlineData("--logger", "console;verbosity=detailed;x=$(Y)")]
    [InlineData("--filter", "Name~A;VSTestSetting=x")]
    [InlineData("--filter", "Name~A,B")]
    [InlineData("--results-directory", "out;VSTestSetting=x")]
    // round 2, route (a): a value that dotnet would read as an option. The
    // first filter keeps the run narrow; the second "filter" is --environment
    // to dotnet (Codex reproduced it through the real launcher).
    [InlineData("--filter", "Name~A", "--filter", "--environment=CANARY=1")]
    [InlineData("--filter", "--environment=CANARY=1")]
    [InlineData("--filter", "-e")]
    [InlineData("--filter", "/environment:CANARY=1")]
    [InlineData("--filter=--environment=CANARY=1")]
    [InlineData("--results-directory", "--environment=CANARY=1")]
    [InlineData("--logger", "-e")]
    [InlineData("-c", "--settings")]
    [InlineData("--blame-hang-timeout", "-s")]
    [InlineData("-v", "-e")]
    // the test-only barrier is a LAUNCHER option, unreachable after the project
    [InlineData("--lifecycle-barrier", "after-create")]
    // and anything unknown
    [InlineData("--blame-crash")]
    [InlineData("--whatever")]
    public void EveryRouteToTesthostsEnvironmentOrSettingsIsRefused(params string[] route)
    {
        var args = new List<string> { Project, "-c", "Debug" };
        args.AddRange(route);

        var parsed = DeskRunRules.Parse(args, _ => true, out string error);

        Assert.Null(parsed);
        Assert.Contains("refused argument", error, StringComparison.Ordinal);
        Assert.Contains(route[0], error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-c", "Debug")]
    [InlineData("--configuration", "Release")]
    [InlineData("--configuration=Debug")]
    [InlineData("--configuration:Debug")]
    [InlineData("-p:Platform=x64")]
    [InlineData("/p:Platform=x86")]
    [InlineData("--property:Platform=AnyCPU")]
    [InlineData("-property:platform=x64")]
    [InlineData("--filter", "FullyQualifiedName~HarnessTests|FullyQualifiedName~DialogEarlyExitTests")]
    [InlineData("--filter", "FullyQualifiedName~HarnessTests.Every_dialog")]
    [InlineData("--filter=Name!=X&(Category=A)")]
    [InlineData("--logger", "console;verbosity=detailed")]
    [InlineData("--logger", "trx;LogFileName=out.trx")]
    [InlineData("-l", "console")]
    [InlineData("--results-directory", @"C:\temp\results here")]
    [InlineData("--blame-hang")]
    [InlineData("--blame-hang-timeout", "60s")]
    [InlineData("--blame-hang-timeout", "5m")]
    [InlineData("--no-build")]
    [InlineData("--no-restore")]
    [InlineData("--list-tests")]
    [InlineData("--nologo")]
    [InlineData("--disable-build-servers")]
    [InlineData("-v", "minimal")]
    [InlineData("--verbosity", "detailed")]
    public void EachAllowedOptionPasses(params string[] option)
    {
        var args = new List<string> { Project };
        args.AddRange(option);

        var parsed = DeskRunRules.Parse(args, _ => true, out string error);

        Assert.True(parsed != null, error);
        Assert.Equal(option, parsed!.PassThrough);
    }

    [Fact]
    public void TheLifecycleBarrierIsALauncherOptionAndNeverForwarded()
    {
        var p = Parse("--lifecycle-barrier", "after-resume", Project, "-c", "Debug");

        Assert.NotNull(p);
        Assert.Equal("after-resume", p!.LifecycleBarrier);
        Assert.DoesNotContain(DeskRunRules.DotnetArguments(p), a => a.Contains("barrier", StringComparison.Ordinal));
        Assert.Null(Parse("--lifecycle-barrier", "somewhere-else", Project));
        Assert.Null(Parse(Project)!.LifecycleBarrier);
    }

    [Theory]
    [InlineData("VSTEST_HOST_DEBUG")]
    [InlineData("vstest_runner_debug")]
    [InlineData("VSTestSetting")]
    [InlineData("VSTestCLIRunSettings")]
    [InlineData("RunSettingsFilePath")]
    [InlineData("runsettingsfilepath")]
    [InlineData("CustomBeforeMicrosoftCommonTargets")]
    [InlineData("CustomAfterMicrosoftCommonProps")]
    [InlineData("DirectoryBuildPropsPath")]
    [InlineData("ImportDirectoryBuildTargets")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("DOTNET_ADDITIONAL_DEPS")]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("CORECLR_PROFILER_PATH_64")]
    [InlineData("COR_PROFILER")]
    public void VariablesThatChooseSettingsOrInjectCodeAreNotPassedToTheChild(string name)
    {
        // MSBuild reads every environment variable as a property, so a variable
        // named like a VSTest property selects settings as surely as -p: does.
        IDictionary parent = new Hashtable(StringComparer.Ordinal)
        {
            ["PATH"] = @"C:\Windows",
            [name] = "x",
        };

        var child = DeskRunRules.ChildEnvironment(parent, "WinSta0\\X", out var removed);

        Assert.DoesNotContain(child.Keys, k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(name, removed);
        Assert.Equal(@"C:\Windows", child["PATH"]);
    }

    // ── the project's own build files ────────────────────────────────────

    private const string Clean = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>";

    private static Func<string, string?> Tree(params (string Path, string Text)[] files)
    {
        var map = files.ToDictionary(f => Path.GetFullPath(f.Path), f => f.Text, StringComparer.OrdinalIgnoreCase);
        return p => map.TryGetValue(Path.GetFullPath(p), out var t) ? t : null;
    }

    [Fact]
    public void ACleanProjectTreeIsAccepted()
    {
        Assert.Null(DeskRunRules.ProjectSettingsProblem(@"C:\r\t\T.csproj",
            Tree((@"C:\r\t\T.csproj", Clean), (@"C:\r\Directory.Build.props", Clean))));
    }

    [Theory]
    [InlineData(@"C:\r\t\T.csproj", "<Project><PropertyGroup><RunSettingsFilePath>x</RunSettingsFilePath></PropertyGroup></Project>")]
    [InlineData(@"C:\r\t\T.csproj", "<Project><PropertyGroup><VSTestSetting>x</VSTestSetting></PropertyGroup></Project>")]
    [InlineData(@"C:\r\t\T.csproj", "<Project><Import Project=\"other.props\" /></Project>")]
    [InlineData(@"C:\r\Directory.Build.props", "<Project><PropertyGroup><vstestcollect>x</vstestcollect></PropertyGroup></Project>")]
    [InlineData(@"C:\Directory.Build.targets", "<Project><PropertyGroup><X>a.runsettings</X></PropertyGroup></Project>")]
    [InlineData(@"C:\r\Directory.Build.props", "<Project><Import Project=\"$(Up)\" /></Project>")]
    [InlineData(@"C:\r\t\Directory.Build.rsp", "-p:Anything=1")]
    [InlineData(@"C:\Directory.Build.rsp", "")]
    public void ABuildFileThatCanChooseSettingsIsRefused(string path, string text)
    {
        var files = new List<(string, string)> { (@"C:\r\t\T.csproj", Clean) };
        files.RemoveAll(f => string.Equals(f.Item1, path, StringComparison.OrdinalIgnoreCase));
        files.Add((path, text));

        Assert.NotNull(DeskRunRules.ProjectSettingsProblem(@"C:\r\t\T.csproj", Tree(files.ToArray())));
    }

    [Fact]
    public void ThisRepositorysWindowTestProjectPassesTheScanToday()
    {
        // Positive control on the real tree: if this goes red, someone added a
        // settings route to the build, and deskrun will refuse until it is read.
        string root = JJFlexWpf.Tests.Infrastructure.RepoPaths.RequireRoot();
        string project = Path.Combine(root, "JJFlexWpf.Tests", "JJFlexWpf.Tests.csproj");

        Assert.Null(DeskRunRules.ProjectSettingsProblem(project, p => File.Exists(p) ? File.ReadAllText(p) : null));
    }

    // ── quoting ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\Program Files\dotnet\", "\"C:\\Program Files\\dotnet\\\\\"")]
    [InlineData("FullyQualifiedName~A|FullyQualifiedName~B", "FullyQualifiedName~A|FullyQualifiedName~B")]
    public void ArgumentsAreQuotedSoTheChildReadsThemBack(string raw, string quoted)
    {
        Assert.Equal(quoted, DeskRunRules.Quote(raw));
    }

    [Fact]
    public void TheDesktopNameIsUniquePerRun()
    {
        string a = DeskRunRules.NewDesktopName(42, Guid.NewGuid());
        string b = DeskRunRules.NewDesktopName(42, Guid.NewGuid());

        Assert.NotEqual(a, b);
        Assert.StartsWith("JJFlexDeskRun_42_", a, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", a, StringComparison.Ordinal);
    }

    // ── round 2: the watchers' judgements (Noel's conditions 1 and 2) ─────

    private const string Hidden = "JJFlexDeskRun_42_abcdef012345";

    [Theory]
    [InlineData("Default")]
    [InlineData("default")]
    [InlineData("Winlogon")]
    [InlineData("Screen-saver")]
    [InlineData("SomebodyElsesHiddenDesktop")]
    [InlineData("JJFlexDeskRun_42_abcdef012346")]
    [InlineData(null)]
    public void AJobWindowOnAnyDesktopButTheHiddenOneHasEscaped(string? desktop)
    {
        Assert.Equal(DeskRunRules.WindowPlace.Escaped, DeskRunRules.JudgeWindow(desktop, Hidden, ownedByJob: true));
    }

    [Theory]
    [InlineData(Hidden)]
    [InlineData("jjflexdeskrun_42_ABCDEF012345")]
    public void AJobWindowOnTheHiddenDesktopIsWhereItBelongs(string desktop)
    {
        Assert.Equal(DeskRunRules.WindowPlace.OnTheHiddenDesktop,
                     DeskRunRules.JudgeWindow(desktop, Hidden, ownedByJob: true));
    }

    [Theory]
    [InlineData("Default")]
    [InlineData(Hidden)]
    [InlineData(null)]
    public void AWindowNotOwnedByTheJobIsNotJudged(string? desktop)
    {
        Assert.Equal(DeskRunRules.WindowPlace.NotTheJobs, DeskRunRules.JudgeWindow(desktop, Hidden, ownedByJob: false));
    }

    [Theory]
    [InlineData(true, 0f, false)]       // digital silence from the job passes
    [InlineData(true, 1e-7f, true)]     // anything above zero fails, however small
    [InlineData(true, 0.5f, true)]
    [InlineData(false, 1f, false)]      // somebody else's sound is not the job's
    public void OnlyANonZeroPeakFromTheJobCountsAsSound(bool ownedByJob, float peak, bool audible)
    {
        Assert.Equal(audible, DeskRunRules.AudibleFromTheJob(ownedByJob, peak));
    }

    // ── round 3, B4: every check fails closed ────────────────────────────
    //
    // Codex showed round 2's audio watch catching a COM failure, printing
    // "UNMEASURED" and returning the child's success, and turning a failed
    // meter read into a zero, which reads exactly like silence. Every look
    // that cannot be made now ends the run at 74. These are the pure halves;
    // the live halves run against the real launcher with --inject-fault.

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(124)]
    public void AWatchThatCouldNotMeasureFailsTheRunWhateverTheChildSaid(int childExit)
    {
        Assert.Equal(74, DeskRunRules.RunExitCode(74, cleanupComplete: true, timedOut: false, childExit));
        Assert.Equal(74, DeskRunRules.RunExitCode(74, cleanupComplete: false, timedOut: true, childExit));
        Assert.Equal(DeskRunRules.ExitWatchCouldNotMeasure, 74);
    }

    [Theory]
    [InlineData(72)]
    [InlineData(73)]
    [InlineData(74)]
    public void AWatchResultOutranksCleanupTimeoutAndTheChild(int violation)
    {
        Assert.Equal(violation, DeskRunRules.RunExitCode(violation, cleanupComplete: false, timedOut: true, childExit: 0));
    }

    // Codex's final #906 check: GetExitCodeProcess's result was ignored, and
    // its out value is 0 on failure, so a failed read reported a failing test
    // run as a pass. An unread or still-running child is unmeasured: 74.
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void AnExitCodeThatCouldNotBeReadIsNeverTakenAsTheResult(uint outValue)
    {
        Assert.Equal(74, DeskRunRules.ChildExitFromRead(readSucceeded: false, outValue));
        Assert.Equal(74, DeskRunRules.RunExitCode(null, cleanupComplete: true, timedOut: false,
                                                 DeskRunRules.ChildExitFromRead(false, outValue)));
    }

    [Fact]
    public void AChildStillRunningWhenReadHasNoResult()
        => Assert.Equal(74, DeskRunRules.ChildExitFromRead(readSucceeded: true, DeskRunRules.StillActive));

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, 1)]
    [InlineData(0xC0000005u, unchecked((int)0xC0000005u))]
    public void AnExitCodeThatWasReadIsTheChildsResult(uint code, int expected)
        => Assert.Equal(expected, DeskRunRules.ChildExitFromRead(readSucceeded: true, code));

    [Fact]
    public void WithoutAWatchResultTheRankingIsCleanupThenTimeoutThenTheChild()
    {
        Assert.Equal(71, DeskRunRules.RunExitCode(null, cleanupComplete: false, timedOut: true, childExit: 0));
        Assert.Equal(124, DeskRunRules.RunExitCode(null, cleanupComplete: true, timedOut: true, childExit: 0));
        Assert.Equal(0, DeskRunRules.RunExitCode(null, cleanupComplete: true, timedOut: false, childExit: 0));
        Assert.Equal(1, DeskRunRules.RunExitCode(null, cleanupComplete: true, timedOut: false, childExit: 1));
    }

    [Theory]
    [InlineData(0, 0f, 0f)]
    [InlineData(0, 0.25f, 0.25f)]
    [InlineData(0, 1f, 1f)]
    public void APeakThatWasReadIsAMeasurement(int hr, float value, float expected)
    {
        Assert.Equal(expected, DeskRunRules.PeakReading(hr, value));
    }

    [Theory]
    [InlineData(unchecked((int)0x80004005), 0f)]   // E_FAIL
    [InlineData(unchecked((int)0x88890004), 0f)]   // AUDCLNT_E_DEVICE_INVALIDATED
    [InlineData(unchecked((int)0x80070005), 0f)]   // E_ACCESSDENIED
    [InlineData(0, float.NaN)]
    [InlineData(0, -0.1f)]
    [InlineData(0, 1.5f)]
    public void AFailedOrImpossiblePeakIsNotAZero(int hr, float value)
    {
        // Round 2 turned a failed GetPeakValue into peak 0: unmeasured read as silent.
        Assert.Null(DeskRunRules.PeakReading(hr, value));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(unchecked((int)0x80004005), "failed (HRESULT 0x80004005)")]
    [InlineData(1, "failed (HRESULT 0x00000001)")]
    public void OnlySOkIsASuccessfulCall(int hr, string? fragment)
    {
        string? failed = DeskRunRules.CallFailed("IMMDeviceEnumerator::EnumAudioEndpoints", hr);
        if (fragment == null) Assert.Null(failed);
        else Assert.Contains(fragment, failed!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, (int)DeskRunRules.SessionOwner.SingleProcess)]
    [InlineData(DeskRunRules.AudclntSNoSingleProcess, (int)DeskRunRules.SessionOwner.SharedAcrossProcesses)]
    [InlineData(unchecked((int)0x80004005), (int)DeskRunRules.SessionOwner.Failed)]
    [InlineData(unchecked((int)0x88890004), (int)DeskRunRules.SessionOwner.Failed)]
    public void ASessionWhoseOwnerCannotBeReadIsAFailureNotSomebodyElses(int hr, int expected)
    {
        // Round 2 lumped a failed GetProcessId in with "ambiguous" and never
        // attributed it, so a job session whose owner could not be read passed.
        Assert.Equal((DeskRunRules.SessionOwner)expected, DeskRunRules.JudgeSessionOwner(hr));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(unchecked((int)0x80004005), null)]
    public void IsSystemSoundsSessionHasThreeAnswersNotTwo(int hr, bool? expected)
    {
        Assert.Equal(expected, DeskRunRules.JudgeSystemSounds(hr));
    }

    [Theory]
    [InlineData(true, 0, (int)DeskRunRules.DesktopOpen.Opened)]
    [InlineData(false, 2, (int)DeskRunRules.DesktopOpen.Gone)]           // measured: a missing name gives error 2
    [InlineData(false, 5, (int)DeskRunRules.DesktopOpen.CouldNotOpen)]   // exists, access denied: unmeasured
    [InlineData(false, 0, (int)DeskRunRules.DesktopOpen.CouldNotOpen)]
    [InlineData(false, 6, (int)DeskRunRules.DesktopOpen.CouldNotOpen)]
    public void OnlyAMissingDesktopCountsAsHoldingNoWindows(bool opened, int error, int expected)
    {
        Assert.Equal((DeskRunRules.DesktopOpen)expected, DeskRunRules.JudgeDesktopOpen(opened, error));
    }

    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(false, 0, false)]   // measured: an EMPTY desktop returns FALSE with error 0
    [InlineData(false, 6, true)]    // measured: a bad handle returns FALSE with error 6
    [InlineData(false, 5, true)]
    public void AWindowEnumerationFailsOnlyWithAnError(bool returned, int error, bool failed)
    {
        Assert.Equal(failed, DeskRunRules.WindowEnumerationFailed(returned, error));
    }

    // ── round 3, B3: ownership is read when the window is seen ───────────

    [Fact]
    public void AProcessInTheSnapshotIsTheJobsWithoutAnotherRead()
    {
        bool read = false;
        Assert.True(DeskRunRules.OwnedByJob(7, new HashSet<int> { 7 }, () => { read = true; return null; }));
        Assert.False(read);
    }

    [Fact]
    public void ADescendantBornAfterTheSnapshotIsStillTheJobs()
    {
        // Codex's ownership gap: round 2 judged every window of a pass against
        // the snapshot taken before it, so a process born during the pass read
        // as not the job's.
        Assert.True(DeskRunRules.OwnedByJob(9, new HashSet<int> { 7 }, () => new[] { 7, 9 }));
    }

    [Fact]
    public void AProcessInNeitherIsNotTheJobs()
    {
        Assert.False(DeskRunRules.OwnedByJob(11, new HashSet<int> { 7 }, () => new[] { 7, 9 }));
        Assert.False(DeskRunRules.OwnedByJob(0, new HashSet<int> { 0 }, () => new[] { 0 }));
    }

    [Fact]
    public void AJobListThatCannotBeReadIsUnmeasuredNotEmpty()
    {
        // Round 2's process list returned EMPTY on failure, which reads as
        // "nothing is the job's" and lets any job window pass.
        Assert.Null(DeskRunRules.OwnedByJob(9, new HashSet<int> { 7 }, () => null));
    }

    // ── round 3, B3: the job's UI limits ─────────────────────────────────

    [Fact]
    public void EveryJobForbidsCreatingOrSwitchingDesktops()
    {
        Assert.Equal(0x40u, DeskRunRules.UiLimitDesktop);
        Assert.Contains(DeskRunRules.JobUiLimits, l => l.Flag == DeskRunRules.UiLimitDesktop);
        Assert.NotEqual(0u, DeskRunRules.JobUiLimitMask & DeskRunRules.UiLimitDesktop);
    }

    [Fact]
    public void TheUiLimitsAreDistinctNamedFlags()
    {
        var flags = DeskRunRules.JobUiLimits.Select(l => l.Flag).ToList();
        Assert.Equal(flags.Count, flags.Distinct().Count());
        Assert.All(flags, f => Assert.True(f != 0 && (f & (f - 1)) == 0, "0x" + f.ToString("X") + " is not one flag"));
        Assert.All(DeskRunRules.JobUiLimits, l => Assert.StartsWith("JOB_OBJECT_UILIMIT_", l.Name, StringComparison.Ordinal));
    }

    // ── round 3: the fault seam is a launcher option only ────────────────

    [Theory]
    [InlineData("audio-init")]
    [InlineData("audio-endpoints")]
    [InlineData("audio-sessions")]
    [InlineData("audio-peak")]
    [InlineData("audio-peak-late")]
    [InlineData("window-enum")]
    [InlineData("job-list")]
    [InlineData("stale-snapshot")]
    public void AnInjectedFaultIsALauncherOptionAndNeverForwarded(string fault)
    {
        var p = Parse("--inject-fault", fault, Project, "-c", "Debug");

        Assert.NotNull(p);
        Assert.Equal(fault, p!.InjectFault);
        Assert.DoesNotContain(DeskRunRules.DotnetArguments(p), a => a.Contains("fault", StringComparison.Ordinal));
        Assert.DoesNotContain(DeskRunRules.DotnetArguments(p), a => a == fault);
    }

    [Fact]
    public void TheFaultSeamIsUnreachableAfterTheProjectAndInertByDefault()
    {
        Assert.Null(Parse(Project)!.InjectFault);
        Assert.Null(Parse(Project, "--inject-fault", "audio-init"));          // after the project: not on the allowlist
        Assert.Null(Parse(Project, "--filter", "--inject-fault"));            // nor as a value
        Assert.Null(Parse("--inject-fault", "something-else", Project));      // only the named faults
        Assert.Null(Parse("--inject-fault"));
    }
}
