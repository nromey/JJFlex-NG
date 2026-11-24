using System.Reflection;
using JJFlexWpf.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Task #906 round 3: Codex's round-2 fault, reproduced through the REAL
/// reading code rather than a copy of it. On deskrun's hidden desktop the input
/// desktop failed to open, the starting desktop was therefore not accepted, the
/// attempt fell back to <see cref="DesktopIsolation.NotAttempted"/> or
/// <see cref="DesktopIsolation.SwitchFailed"/>, and the guard consulted the
/// desk-free declaration again. The launch origin is now its own fact, read
/// before anything is attempted, and a failed read leaves it
/// <see cref="LaunchOrigin.Unknown"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windowless.</b> These read desktop names and call
/// <see cref="PrivateDesktop.Isolate(bool, ReadFault)"/>. On the operator's desk
/// the requested path may create a private desktop OBJECT (no window, nothing
/// on any screen) and try to move a throwaway STA thread onto it, exactly what
/// every window class already does under a plain <c>dotnet test</c> before it
/// refuses; under deskrun the job forbids creating a desktop and the attempt
/// reports <see cref="DesktopIsolation.CreateFailed"/>. Nothing here builds a
/// window or sets an environment variable.
/// </para>
/// <para>
/// <b>Meaningful in both places.</b> Run on the operator's desk, the unfaulted
/// reading is the human desk and the fault is a locked workstation. Run through
/// <c>tools/deskrun</c>, the unfaulted reading is the hidden desktop and the
/// fault is exactly Codex's. The assertions hold in both, and the output says
/// which one ran.
/// </para>
/// </remarks>
public sealed class LaunchOriginTests
{
    private readonly ITestOutputHelper _output;

    public LaunchOriginTests(ITestOutputHelper output) => _output = output;

    private static T OnFreshStaThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = "LaunchOriginTests probe",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The probe thread did not finish.");
        if (failure != null) throw new InvalidOperationException("Probe thread threw.", failure);
        return result;
    }

    [Fact]
    public void TheUnfaultedReadingAndItsOriginAgree()
    {
        var reading = OnFreshStaThread(() => PrivateDesktop.ReadCurrentThread());
        var origin = PrivateDesktop.JudgeLaunchOrigin(reading.WindowStation, reading.ThreadDesktop, reading.InputDesktop);
        var judgement = PrivateDesktop.JudgeStartingDesktop(reading.WindowStation, reading.ThreadDesktop, reading.InputDesktop);
        _output.WriteLine("unfaulted reading: " + reading + " -> origin " + origin + ", judgement " + judgement);

        Assert.Equal(judgement == StartingDesktopJudgement.Accepted, origin == LaunchOrigin.HiddenDesktop);
        Assert.Equal(origin == LaunchOrigin.InteractiveDesktop,
                     string.Equals(reading.WindowStation, "WinSta0", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(reading.ThreadDesktop, "Default", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(reading.InputDesktop, "Default", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodexsFaultTheInputDesktopCannotBeReadAndTheWaiverDoesNotComeBack(bool requested)
    {
        var before = OnFreshStaThread(() => PrivateDesktop.Isolate(requested: false, ReadFault.None));
        var reading = OnFreshStaThread(() => PrivateDesktop.ReadCurrentThread(ReadFault.InputUnreadable));
        var faulted = OnFreshStaThread(() => PrivateDesktop.Isolate(requested, ReadFault.InputUnreadable));

        _output.WriteLine("unfaulted origin " + before.Origin + " (" + before.Isolation + "); faulted reading "
                          + reading + " -> " + faulted.Isolation + ", origin " + faulted.Origin
                          + (before.Origin == LaunchOrigin.HiddenDesktop
                              ? "  [this is Codex's case: a hidden desktop whose input read failed]"
                              : "  [run through deskrun for Codex's exact case]"));

        Assert.Null(reading.InputDesktop);
        Assert.Equal(5, reading.InputError);
        Assert.Equal(LaunchOrigin.Unknown, faulted.Origin);
        Assert.NotEqual(DesktopIsolation.StartedOnPrivateDesktop, faulted.Isolation);

        // What round 2 got wrong: whatever the attempt fell back to, the
        // declaration is not consulted, so the waiver cannot come back.
        foreach (bool silent in new[] { true, false })
        {
            var verdict = DeskGuard.Decide(requested, faulted.Isolation, faulted.Origin,
                                           deskDeclaredFree: true, audioSuppressed: silent,
                                           settingsIsolated: true);
            Assert.NotEqual(DeskGuard.Verdict.AllowedDeskDeclaredFree, verdict);
        }
        Assert.False(DeskGuard.IsAllowed(
            DeskGuard.Decide(requested, faulted.Isolation, faulted.Origin, deskDeclaredFree: true,
                             audioSuppressed: false, settingsIsolated: true)));
    }

    [Fact]
    public void AnUnreadableThreadDesktopIsAnUnknownOrigin()
    {
        var faulted = OnFreshStaThread(() => PrivateDesktop.Isolate(requested: false, ReadFault.ThreadUnreadable));
        _output.WriteLine("thread-unreadable fault -> " + faulted);

        Assert.Equal(LaunchOrigin.Unknown, faulted.Origin);
        Assert.Equal(DesktopIsolation.NotAttempted, faulted.Isolation);
        Assert.NotEqual(DeskGuard.Verdict.AllowedDeskDeclaredFree,
                        DeskGuard.Decide(false, faulted.Isolation, faulted.Origin, deskDeclaredFree: true,
                                         audioSuppressed: true, settingsIsolated: true));
    }

    [Fact]
    public void AFaultedReadRecordsNothingAVerdictLineWouldReport()
    {
        var lastReading = PrivateDesktop.LastReading;
        var lastOrigin = PrivateDesktop.LastOrigin;
        var lastJudgement = PrivateDesktop.LastJudgement;
        var isolatedOn = PrivateDesktop.IsolatedDesktopName;

        OnFreshStaThread(() => PrivateDesktop.Isolate(requested: false, ReadFault.InputUnreadable | ReadFault.ThreadUnreadable));

        Assert.Equal(lastReading, PrivateDesktop.LastReading);
        Assert.Equal(lastOrigin, PrivateDesktop.LastOrigin);
        Assert.Equal(lastJudgement, PrivateDesktop.LastJudgement);
        Assert.Equal(isolatedOn, PrivateDesktop.IsolatedDesktopName);
    }

    [Fact]
    public void TheFaultSeamIsReachableOnlyByArgument()
    {
        // Inert unless a test passes it: no public member takes a ReadFault,
        // the enum itself is not public, and the public entry points have no
        // fault parameter at all.
        Assert.False(typeof(ReadFault).IsPublic);

        foreach (var method in typeof(PrivateDesktop).GetMethods(BindingFlags.Static | BindingFlags.Public))
            Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(ReadFault));

        var isolate = typeof(PrivateDesktop).GetMethod(nameof(PrivateDesktop.Isolate),
                                                       BindingFlags.Static | BindingFlags.Public,
                                                       new[] { typeof(bool) });
        Assert.NotNull(isolate);
    }
}
