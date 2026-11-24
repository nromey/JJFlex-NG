using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using JJFlexWpf.Tests.Infrastructure;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// Task #1286: WebView2 inside deskrun's job. One window hosting one WebView2
/// control and nothing else, so the job's process report is WebView2's alone.
///
/// <para><b>What was measured, 2026-11-24.</b> Through deskrun the browser
/// process starts and reports itself ready, then every renderer it launches
/// fails at once (<c>RenderProcessExited</c>, reason <c>LaunchFailed</c>,
/// exit code 0x12) and the page never renders (navigation fails with
/// <c>ConnectionAborted</c>). Lifting each of the job's UI limits in turn,
/// except the desktop limit, changed nothing. The desktop limit was not
/// lifted: it is what stops a job process making the hidden desktop the
/// visible one. So inside the job a WebView2 surface is a browser that never
/// rendered, which is why the sweep runs with no WebView2 runtime at all
/// (<see cref="NoWebView2Runtime"/>): a dialog hosting WebView2 is inspected on
/// its own no-runtime path instead (task #1286 repair 1).</para>
///
/// <para>This test keeps that choice honest. Through deskrun it asserts the
/// page does NOT render; the day it does, the test fails and says the reason
/// for hiding the runtime from the sweep is no longer true. On any other
/// route it asserts the page renders.</para>
///
/// <para>The browser's user-data folder is under this run's throwaway
/// settings tree, never the operator's own WebView2 folder.</para>
///
/// <para><b>BUILDS A REAL WINDOW AND STARTS A BROWSER.</b> Run only through
/// <c>tools/deskrun</c>, one class per run; never in the windowless filter.
/// The page is a few words of local text: nothing plays and nothing is
/// fetched.</para>
/// </summary>
public sealed class WebView2InJobTests
{
    private readonly ITestOutputHelper _output;

    public WebView2InJobTests(ITestOutputHelper output) => _output = output;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public void WebView2_renders_nothing_inside_deskruns_job_which_is_why_the_sweep_runs_without_its_runtime()
    {
        UiThread.EnsureStarted();
        _output.WriteLine(UiThread.VerdictLine);

        var outcome = UiThread.RunWithTimeout(Probe, Budget + TimeSpan.FromSeconds(30));
        foreach (var line in outcome.Log) _output.WriteLine(line);

        // The positive control for "it never rendered": the browser itself did start.
        Assert.True(outcome.EnvironmentCreated, "The WebView2 environment was not created: " + outcome.Summary);
        Assert.True(outcome.CoreReady, "WebView2's browser did not become ready: " + outcome.Summary);

        if (UiThread.Isolation == DesktopIsolation.StartedOnPrivateDesktop)
        {
            Assert.False(outcome.NavigationSucceeded,
                "A page rendered inside deskrun's job. The sweep hides the WebView2 runtime (NoWebView2Runtime) " +
                "because it did not (task #1286); that reason is no longer true, so the sweep could inspect the rendered page.");
            Assert.NotNull(outcome.NavigationResult);   // it failed, and said so; it did not merely run out of time
        }
        else
        {
            Assert.True(outcome.NavigationSucceeded, "The page did not render: " + outcome.Summary);
        }
    }

    private sealed record Outcome(bool EnvironmentCreated, bool CoreReady, bool? NavigationResult,
                                  int ProcessFailures, string Summary, List<string> Log)
    {
        public bool NavigationSucceeded => NavigationResult == true;
    }

    /// <summary>Runs on the UI thread; pumps its own frame until the page renders, fails, or the budget runs out.</summary>
    private static Outcome Probe()
    {
        var log = new List<string>();
        var clock = Stopwatch.StartNew();
        var envCreated = false;
        var coreReady = false;
        bool? navigated = null;
        var failures = 0;
        var browserPid = 0;
        string? failureText = null;

        var userData = Path.Combine(TestSettingsRoot.Directory, "WebView2");
        log.Add("User-data folder: " + userData);

        var web = new WebView2();
        var window = new Window { Content = web, Width = 400, Height = 300, Title = "WebView2 in job probe" };
        using (RealizedDialog.Realize(window, Sweep.Strategy))
        {
            var frame = new DispatcherFrame();
            async void Start()
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, userData, null);
                    envCreated = true;
                    log.Add($"{clock.ElapsedMilliseconds} ms: environment created, runtime {env.BrowserVersionString}.");
                    await web.EnsureCoreWebView2Async(env);
                    coreReady = true;
                    browserPid = (int)web.CoreWebView2.BrowserProcessId;
                    log.Add($"{clock.ElapsedMilliseconds} ms: browser ready, browser process {browserPid}.");
                    web.CoreWebView2.ProcessFailed += (_, e) =>
                    {
                        failures++;
                        log.Add($"{clock.ElapsedMilliseconds} ms: process failed: {e.ProcessFailedKind}, reason {e.Reason}, " +
                                $"exit code 0x{e.ExitCode:X8}.");
                    };
                    web.NavigationCompleted += (_, e) =>
                    {
                        navigated = e.IsSuccess;
                        log.Add($"{clock.ElapsedMilliseconds} ms: navigation completed, success {e.IsSuccess}, status {e.WebErrorStatus}.");
                        frame.Continue = false;
                    };
                    web.CoreWebView2.NavigateToString("<!DOCTYPE html><p>probe</p>");
                }
                catch (Exception ex)
                {
                    failureText = TreeWalk.Describe(ex);
                    log.Add($"{clock.ElapsedMilliseconds} ms: threw {failureText}");
                    frame.Continue = false;
                }
            }

            var timer = new DispatcherTimer { Interval = Budget };
            timer.Tick += (_, _) =>
            {
                log.Add($"{clock.ElapsedMilliseconds} ms: budget of {Budget.TotalSeconds:0} s ran out.");
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();

            try { web.Dispose(); } catch (Exception ex) { log.Add("Dispose threw " + TreeWalk.Describe(ex)); }
        }

        // The browser process does not exit within ten seconds of the control
        // closing, even with this thread pumping (measured 2026-11-24); deskrun
        // ends it with the rest of the job, so the test does not wait for it.
        if (browserPid != 0) log.Add($"{clock.ElapsedMilliseconds} ms: closed; browser process {browserPid} is left to deskrun's clean-up.");

        var summary = failureText
                      ?? (navigated == null ? "no navigation result within the budget" : "navigation success " + navigated);
        return new Outcome(envCreated, coreReady, navigated, failures, summary, log);
    }
}
