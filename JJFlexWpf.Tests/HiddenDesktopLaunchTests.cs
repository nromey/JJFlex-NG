using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using JJFlexWpf.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Task #906, the created state end to end: run through <c>tools/deskrun</c>,
/// the UI thread is isolated by the hidden-desktop route and builds and closes
/// one real window there.
/// </summary>
/// <remarks>
/// <para>
/// <b>BUILDS A REAL WINDOW. Not windowless, and never in the desk-safe
/// filter.</b> On the operator's desk under a plain <c>dotnet test</c> the
/// guard refuses before any dispatcher exists, and this test fails with the
/// refusal and the verdict line. That failure IS the second half of the
/// evidence: without the launcher, nothing changed.
/// </para>
/// <para>
/// Run it unseen with:
/// <c>deskrun JJFlexWpf.Tests/JJFlexWpf.Tests.csproj -c Debug -p:Platform=x64
/// --filter FullyQualifiedName~HiddenDesktopLaunchTests
/// --logger "console;verbosity=detailed"</c>
/// </para>
/// </remarks>
public sealed class HiddenDesktopLaunchTests
{
    private readonly ITestOutputHelper _output;

    public HiddenDesktopLaunchTests(ITestOutputHelper output) => _output = output;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [Fact]
    public void UnderTheLauncherTheUiThreadIsIsolatedOnTheHiddenDesktopAndBuildsOneWindow()
    {
        try
        {
            UiThread.EnsureStarted();
        }
        finally
        {
            // Written whether the guard allowed or refused: the verdict, and
            // the route, are the result.
            _output.WriteLine(UiThread.VerdictLine);
        }

        Assert.Equal(DeskGuard.Verdict.AllowedIsolated, UiThread.Guard);
        Assert.Equal(DesktopIsolation.StartedOnPrivateDesktop, UiThread.Isolation);
        Assert.False(DeskGuard.DeskDeclaredFree,
            "The desk-free declaration reached the test process; deskrun must remove it.");

        string hidden = PrivateDesktop.IsolatedDesktopName
                        ?? throw new InvalidOperationException("No isolated desktop name was recorded.");

        // The launcher's announcement, COMPARED and never trusted: it shows this
        // process's environment was built by DeskRunRules.ChildEnvironment, the
        // same function that strips the desk-free declaration. The guard above
        // reached its verdict without reading it.
        string? announced = Environment.GetEnvironmentVariable("JJFLEX_DESKRUN_DESKTOP");
        _output.WriteLine("launcher announced '" + announced + "'; Windows reports '" + hidden + "'");

        // Repair round 1: a process can be isolated on a hidden desktop deskrun
        // did not create (Codex's own sandbox starts every command on one, and
        // the guard rightly accepts it). That is not this test's subject, which
        // is the deskrun route, so say so plainly rather than failing on a bare
        // comparison with null.
        Assert.True(announced != null,
            "This process is isolated on hidden desktop '" + hidden + "', but deskrun did not create it: "
            + "there is no launcher announcement. The guard accepted that desktop correctly; this test "
            + "checks the deskrun route specifically, so run it through tools/deskrun.");
        Assert.Equal(hidden, announced, ignoreCase: true);

        var built = UiThread.RunWithTimeout(() =>
        {
            var window = new Window
            {
                Title = "deskrun created-state probe",
                Width = 240,
                Height = 120,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            window.Show();
            UiThread.Drain();

            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            string? desktop = PrivateDesktop.DesktopOfWindow(hwnd);
            bool existedBeforeClose = IsWindow(hwnd);

            window.Close();
            UiThread.Drain();

            return (Handle: hwnd, Desktop: desktop, Existed: existedBeforeClose, GoneAfter: !IsWindow(hwnd));
        }, TimeSpan.FromSeconds(30));

        _output.WriteLine($"window 0x{built.Handle.ToInt64():X} built on desktop '{built.Desktop}', "
                          + $"existed {built.Existed}, gone after Close {built.GoneAfter}");

        Assert.NotEqual(IntPtr.Zero, built.Handle);
        Assert.True(built.Existed);
        Assert.NotNull(built.Desktop);
        Assert.NotEqual("Default", built.Desktop, StringComparer.OrdinalIgnoreCase);
        Assert.EndsWith("\\" + built.Desktop, hidden, StringComparison.OrdinalIgnoreCase);
        Assert.True(built.GoneAfter, "The window still exists after Close.");
    }
}
