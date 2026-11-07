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
}
