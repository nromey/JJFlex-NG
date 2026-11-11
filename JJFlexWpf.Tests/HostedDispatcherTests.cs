using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;
using Radios.Speech;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Two announcements waited on <c>Application.Current</c>, which is always null
/// in this WinForms-hosted app (#1035): the full duplex outcome never waited for
/// the return to Home, and the slice playback-finished sentence was spoken on
/// the radio's status thread. Both now take the UI thread's own dispatcher, on
/// the UI thread, before anything moves them off it.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> No window and no control: the dispatcher test runs on a
/// private STA thread that creates a dispatcher and nothing else, and on an MTA
/// thread, asserted; the rest read source on an MTA thread, asserted. What a
/// screen reader then hears is the harness's to confirm (NVDA's IO log).
/// </remarks>
public sealed class HostedDispatcherTests
{
    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    [Fact]
    public void The_UI_threads_own_dispatcher_is_found_where_Application_Current_finds_nothing()
    {
        AssertWindowless();
        // A thread with no dispatcher has none, and asking does not create one.
        Assert.Null(HostedDispatcher.OfThisThread());
        Assert.Null(Dispatcher.FromThread(Thread.CurrentThread));

        Exception? failure = null;
        Dispatcher? found = null, own = null;
        bool appWasNull = false;
        var thread = new Thread(() =>
        {
            try
            {
                own = Dispatcher.CurrentDispatcher;   // a UI thread has one
                appWasNull = System.Windows.Application.Current == null;
                found = HostedDispatcher.OfThisThread();
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The test thread did not finish.");
        if (failure != null) throw failure;
        // The condition the old code met: no WPF Application, so it found nothing.
        Assert.True(appWasNull, "this test process has a WPF Application, so it no longer models the app");
        Assert.NotNull(found);
        Assert.Same(own, found);
    }

    [Fact]
    public void The_full_duplex_outcome_takes_the_dispatcher_before_its_await_and_is_queued()
    {
        AssertWindowless();
        string src = Source("JJFlexWpf", "FullDuplexControl.cs");
        Assert.DoesNotMatch(new Regex(@"Application\.Current\?\.Dispatcher"), src);
        string body = Between(src, "internal static async Task ToggleAsync(", "\n    }");
        int taken = body.IndexOf("HostedDispatcher.OfThisThread()", StringComparison.Ordinal);
        int awaited = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(taken > 0 && taken < awaited, "the dispatcher is taken on the caller's thread, before the await");
        Assert.Contains("dispatcher.BeginInvoke(DispatcherPriority.ContextIdle", body, StringComparison.Ordinal);
        Assert.Equal(SpeechIntent.Queue, FullDuplexControl.OutcomeIntent);
        string deliver = Between(src, "private static void Deliver(", "\n    }");
        Assert.Contains("OutcomeIntent", deliver, StringComparison.Ordinal);
        Assert.Contains("SpeechSubject.FullDuplexOutcome", deliver, StringComparison.Ordinal);
    }

    [Fact]
    public void The_playback_finished_sentence_is_marshalled_with_the_dispatcher_taken_at_Observe()
    {
        AssertWindowless();
        string src = Source("JJFlexWpf", "RecorderControls.cs");
        string observe = Between(src, "public static void Observe(FlexBase? rig)", "\n    }");
        int taken = observe.IndexOf("var dispatcher = HostedDispatcher.OfThisThread();", StringComparison.Ordinal);
        int handler = observe.IndexOf("r.SlicePlaybackFinished += outcome =>", StringComparison.Ordinal);
        Assert.True(taken > 0 && taken < handler, "the dispatcher is taken when Observe runs, not inside the handler");
        Assert.DoesNotContain("Application.Current", observe.Substring(handler), StringComparison.Ordinal);
        Assert.Contains("dispatcher.BeginInvoke(new Action(() => Speak(outcome)))", observe, StringComparison.Ordinal);
    }

    /// <summary>
    /// The class, not the two instances: nothing else in JJFlexWpf waits on
    /// <c>Application.Current</c>'s dispatcher. Two other reads of it are not
    /// "the UI thread": a trace label in the Audio Devices window and the
    /// update window's Shutdown.
    /// </summary>
    [Fact]
    public void Nothing_else_in_JJFlexWpf_waits_on_Application_Current()
    {
        AssertWindowless();
        string root = Path.Combine(SolutionDir(), "JJFlexWpf");
        var hits = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"Application\.Current\?*\.Dispatcher"))
            .Select(f => Path.GetFileName(f))
            .ToList();
        Assert.Equal(new[] { "HostedDispatcher.cs" }, hits);
    }

    private static string Between(string text, string start, string end)
    {
        int at = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + start);
        int stop = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
        Assert.True(stop > at, "no end after: " + start);
        return text.Substring(at, stop - at);
    }

    private static string SolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { SolutionDir() }.Concat(parts).ToArray())).Replace("\r\n", "\n");
}
