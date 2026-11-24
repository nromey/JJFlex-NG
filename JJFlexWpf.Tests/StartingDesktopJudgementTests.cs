using JJFlexWpf.Tests.Infrastructure;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Task #906: when may the guard accept a desktop the test process was
/// already ON, rather than one it moved its thread to?
/// </summary>
/// <remarks>
/// <para>
/// Under a plain <c>dotnet test</c> the UI thread cannot change desktops at all
/// (ERROR_BUSY, 170: OLE has already given it a window), so the
/// <c>tools/deskrun</c> launcher starts the whole process on a hidden desktop
/// instead. The guard then has to recognise that, from facts Windows reports,
/// and nothing else. Every row of that judgement is asserted here, created
/// state first, and each refusal row is a way the screen could be visible.
/// </para>
/// <para>
/// <b>Windowless.</b> Pure functions, plus one read of the calling thread's
/// own desktop name, which creates no window and changes nothing.
/// </para>
/// </remarks>
public sealed class StartingDesktopJudgementTests
{
    private const string Hidden = "JJFlexDeskRun_1234_0123456789ab";

    // ── the created state ────────────────────────────────────────────────

    [Fact]
    public void AHiddenDesktopThatIsNotReceivingInputIsAccepted()
    {
        Assert.Equal(StartingDesktopJudgement.Accepted,
            PrivateDesktop.JudgeStartingDesktop("WinSta0", Hidden, "Default"));
    }

    [Theory]
    [InlineData("winsta0", "jjflexdeskrun_1234_0123456789ab", "default")]
    [InlineData("WINSTA0", "JJFLEXDESKRUN_1234_0123456789AB", "DEFAULT")]
    public void AcceptanceDoesNotDependOnLetterCase(string station, string thread, string input)
    {
        Assert.Equal(StartingDesktopJudgement.Accepted,
            PrivateDesktop.JudgeStartingDesktop(station, thread, input));
    }

    // ── refusals: desktops a person sees ─────────────────────────────────

    [Theory]
    [InlineData("Default")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    [InlineData("Winlogon")]
    [InlineData("WINLOGON")]
    [InlineData("Screen-saver")]
    [InlineData("screen-saver")]
    public void TheSharedDesktopsAreRefusedWhateverIsReceivingInput(string thread)
    {
        // With input somewhere else entirely, so the shared-name rule is what
        // refuses, not the comparison.
        Assert.Equal(StartingDesktopJudgement.SharedDesktop,
            PrivateDesktop.JudgeStartingDesktop("WinSta0", thread, Hidden));
    }

    [Theory]
    [InlineData("Default", "Default")]
    [InlineData("default", "DEFAULT")]
    public void DefaultIsRefusedWhenItIsAlsoTheInputDesktop(string thread, string input)
    {
        // The ordinary case of a plain dotnet test on the operator's desk.
        Assert.NotEqual(StartingDesktopJudgement.Accepted,
            PrivateDesktop.JudgeStartingDesktop("WinSta0", thread, input));
    }

    // ── refusals: someone switched to it ─────────────────────────────────

    [Theory]
    [InlineData(Hidden, Hidden)]
    [InlineData(Hidden, "JJFLEXDESKRUN_1234_0123456789AB")]
    public void APrivateNameIsRefusedWhenItIsTheInputDesktop(string thread, string input)
    {
        // An unfamiliar name is hidden only while nothing has switched to it.
        Assert.Equal(StartingDesktopJudgement.IsTheInputDesktop,
            PrivateDesktop.JudgeStartingDesktop("WinSta0", thread, input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnreadableInputDesktopIsRefusedNotAssumed(string? input)
    {
        Assert.Equal(StartingDesktopJudgement.InputDesktopUnreadable,
            PrivateDesktop.JudgeStartingDesktop("WinSta0", Hidden, input));
    }

    // ── refusals: not the console session, or nothing read ───────────────

    [Theory]
    [InlineData("Service-0x0-3e7$")]
    [InlineData("WinSta1")]
    [InlineData("")]
    [InlineData(null)]
    public void AnyWindowStationOtherThanWinSta0IsRefused(string? station)
    {
        Assert.Equal(StartingDesktopJudgement.NotTheConsoleWindowStation,
            PrivateDesktop.JudgeStartingDesktop(station, Hidden, "Default"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnreadableThreadDesktopIsRefused(string? thread)
    {
        Assert.Equal(StartingDesktopJudgement.ThreadDesktopUnreadable,
            PrivateDesktop.JudgeStartingDesktop("WinSta0", thread, "Default"));
    }

    // ── the names come from Windows ──────────────────────────────────────

    [Fact]
    public void TheReaderTakesTheDesktopFromWindowsNotFromTheLaunchersAnnouncement()
    {
        // The launcher announces its desktop in JJFLEX_DESKRUN_DESKTOP for the
        // log reader. A guard that believed it would accept a forged value on
        // the operator's own desk. Forge it, read, and the forged name must not
        // come back.
        string? before = Environment.GetEnvironmentVariable("JJFLEX_DESKRUN_DESKTOP");
        const string forged = "WinSta0\\JJFlexDeskRun_forged_000000000000";
        try
        {
            Environment.SetEnvironmentVariable("JJFLEX_DESKRUN_DESKTOP", forged);
            var reading = PrivateDesktop.ReadCurrentThread();

            Assert.False(string.IsNullOrEmpty(reading.ThreadDesktop),
                "The calling thread's desktop name could not be read from Windows.");
            Assert.DoesNotContain("forged", reading.ThreadDesktop!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("forged", reading.WindowStation ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JJFLEX_DESKRUN_DESKTOP", before);
        }
    }

    [Fact]
    public void TheReaderAndTheJudgementAgreeAboutThisVeryProcess()
    {
        // Whatever desk this runs on, the judgement of a live reading must be
        // consistent with it: accepted ONLY when the thread is on a desktop
        // that is not shared and not the input desktop. On the operator's desk
        // under a plain dotnet test this reads Default and refuses.
        var r = PrivateDesktop.ReadCurrentThread();
        var j = PrivateDesktop.JudgeStartingDesktop(r.WindowStation, r.ThreadDesktop, r.InputDesktop);

        if (j == StartingDesktopJudgement.Accepted)
        {
            Assert.NotEqual("Default", r.ThreadDesktop, StringComparer.OrdinalIgnoreCase);
            Assert.NotNull(r.InputDesktop);
            Assert.NotEqual(r.InputDesktop, r.ThreadDesktop, StringComparer.OrdinalIgnoreCase);
        }
        else if (string.Equals(r.ThreadDesktop, "Default", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(StartingDesktopJudgement.SharedDesktop, j);
        }
    }

    // ── the verdict line names the route ─────────────────────────────────

    [Fact]
    public void TheTwoIsolatedRoutesAreDescribedDifferentlyAndNameTheDesktop()
    {
        var hiddenReading = new DesktopReading("WinSta0", Hidden, "Default", 0);
        var defaultReading = new DesktopReading("WinSta0", "Default", "Default", 0);

        string started = PrivateDesktop.DescribeRoute(
            DesktopIsolation.StartedOnPrivateDesktop, hiddenReading,
            StartingDesktopJudgement.Accepted, "WinSta0\\" + Hidden, 0);
        string moved = PrivateDesktop.DescribeRoute(
            DesktopIsolation.Isolated, defaultReading,
            StartingDesktopJudgement.SharedDesktop, "JJFlexTier1_1_abcdef01", 0);

        Assert.NotEqual(started, moved);
        Assert.Contains("started on hidden desktop", started, StringComparison.Ordinal);
        Assert.Contains(Hidden, started, StringComparison.Ordinal);
        Assert.Contains("moved the UI thread", moved, StringComparison.Ordinal);
        Assert.Contains("JJFlexTier1_1_abcdef01", moved, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedRouteSaysWhyTheStartingDesktopWasNotAccepted()
    {
        var reading = new DesktopReading("WinSta0", "Default", "Default", 0);
        string line = PrivateDesktop.DescribeRoute(
            DesktopIsolation.SwitchFailed, reading, StartingDesktopJudgement.SharedDesktop, null, 170);

        Assert.Contains("route: none", line, StringComparison.Ordinal);
        Assert.Contains("SharedDesktop", line, StringComparison.Ordinal);
        Assert.Contains("170", line, StringComparison.Ordinal);
    }
}
