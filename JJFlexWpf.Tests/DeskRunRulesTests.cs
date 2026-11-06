using System.Collections;
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
}
