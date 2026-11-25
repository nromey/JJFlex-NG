using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The startup reminder that no callsign is set closes into Home, and Home is
/// said once (#1306, repair round 4). Its turn's end replays Home's parked
/// arrival ("JJ Flexible Home, no radio connected, Modern tuning mode"); the
/// landing every dialog of ours posts on close then said "JJ Flexible Home, no
/// radio connected" behind it, about three seconds later, on five of five
/// reminder closes on the bench. The landing behind a replayed arrival now
/// leaves its where-you-are sentence out, and only that landing does.
/// </summary>
/// <remarks>
/// <para><b>Driven through the production route:</b> MainWindow's own
/// <c>ReplayParkedBringUp</c>, <c>EndCallsignReminderTurn</c> and
/// <c>RunReturnToAppLanding</c>, with no radio. The parked arrival is a stand-in
/// action, because what is under test is the landing behind it, not the
/// arrival's own sentence.</para>
/// <para><b>What is counted.</b> Every utterance the speech layer emits is
/// recorded by <see cref="OutputChannelRecorder"/> to a transcript of this test
/// alone, with render off, after the arbiter's salvage window.</para>
/// <para><b>Desk safety.</b> No window is constructed: MainWindow is
/// <see cref="RuntimeHelpers.GetUninitializedObject"/>, so it has no
/// presentation source and its shell handle is zero, which the landing's focus
/// repair refuses to bring forward. The thread is asserted MTA, where WPF
/// refuses to build any element, and render is off throughout.</para>
/// </remarks>
public sealed class ReplayedArrivalLandingTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly MainWindow _main;
    private readonly VerbosityLevel _verbosityBefore;
    private string? _transcript;

    public ReplayedArrivalLandingTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        _main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
    }

    public void Dispose()
    {
        OutputChannelRecorder.Configure(render: false, record: false);
        if (_transcript != null) try { File.Delete(_transcript); } catch { }
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
    }

    // ── plumbing, inside this class on purpose ──

    private static void AssertTheDeskIsSafe()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    private void Put(string field, object? value)
    {
        var f = typeof(MainWindow).GetField(field, Any);
        Assert.True(f != null, "production field MainWindow." + field + " is missing");
        f!.SetValue(_main, value);
    }

    private object? Get(string field)
    {
        var f = typeof(MainWindow).GetField(field, Any);
        Assert.True(f != null, "production field MainWindow." + field + " is missing");
        return f!.GetValue(_main);
    }

    private void Call(string method, params object?[] args)
    {
        var m = typeof(MainWindow).GetMethod(method, Any);
        Assert.True(m != null, "production entry MainWindow." + method + " is missing");
        m!.Invoke(_main, args);
    }

    private bool Marked => (bool)Get("_arrivalJustReplayed")!;

    /// <summary>Home's arrival parked behind the reminder, as SpeakWelcome parks it; true once it ran.</summary>
    private Func<bool> ParkArrival()
    {
        bool ran = false;
        Put("_pendingArrival", (Action)(() => ran = true));
        return () => ran;
    }

    /// <summary>The landing a closing dialog of ours posts, with no radio.</summary>
    private void Landing() => Call("RunReturnToAppLanding", new object?[] { null });

    private static string NoRadio => Lexicon.Get("connect.no_radio.plain", VerbosityLevel.Chatty);

    private void Listen()
    {
        _transcript = Path.Combine(Path.GetTempPath(), "jjflex-replayed-arrival-" + Guid.NewGuid().ToString("N") + ".jsonl");
        OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _transcript);
        Assert.False(OutputChannelRecorder.RenderEnabled, "Refusing: render is on");
    }

    /// <summary>How many times the no-radio sentence was said since <see cref="Listen"/>.</summary>
    private int NoRadioSaid()
    {
        Thread.Sleep(900);   // past SpeechArbiter's 600 ms salvage settle, so a replay is counted
        using var stream = new FileStream(_transcript!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Count(e => e.GetProperty("event").GetString() == "speech"
                        && !e.GetProperty("gated").GetBoolean() && !e.GetProperty("suppressed").GetBoolean()
                        && e.GetProperty("text").GetString() == NoRadio);
    }

    // ── the tests ──

    [Fact]
    public void The_landing_behind_a_replayed_arrival_does_not_say_Home_again_and_a_later_landing_does()
    {
        AssertTheDeskIsSafe();
        Assert.Equal("JJ Flexible Home, no radio connected", NoRadio);
        var arrived = ParkArrival();

        Call("ReplayParkedBringUp");
        Assert.True(arrived(), "positive control: the parked arrival was replayed");
        Assert.True(Marked, "a replayed arrival marks the next landing");

        Listen();
        Landing();                       // the reminder's own close
        Assert.Equal(0, NoRadioSaid());  // the mutation (no check) says it here: the bench's late second Home
        Assert.False(Marked, "the landing spent the mark");

        Listen();
        Landing();                       // a later, unrelated dialog close
        Assert.Equal(1, NoRadioSaid());
    }

    [Fact]
    public void A_replay_with_nothing_parked_marks_nothing_and_the_landing_still_says_where_you_are()
    {
        AssertTheDeskIsSafe();
        Assert.Null(Get("_pendingArrival"));

        Call("ReplayParkedBringUp");
        Assert.False(Marked, "nothing was replayed, so nothing is marked");

        Listen();
        Landing();
        Assert.Equal(1, NoRadioSaid());
    }

    [Fact]
    public void A_turn_that_showed_no_window_leaves_no_mark_for_a_later_close()
    {
        // The reminder found it was no longer needed: no window, no landing.
        AssertTheDeskIsSafe();
        var arrived = ParkArrival();

        Call("EndCallsignReminderTurn", false);
        Assert.True(arrived(), "positive control: the parked arrival was still replayed");
        Assert.False(Marked, "no landing is coming, so the mark is cleared at once");

        Listen();
        Landing();
        Assert.Equal(1, NoRadioSaid());
    }

    [Fact]
    public void A_turn_that_showed_the_reminder_leaves_the_mark_for_its_own_close()
    {
        AssertTheDeskIsSafe();
        ParkArrival();

        Call("EndCallsignReminderTurn", true);
        Assert.True(Marked, "the reminder's close posted a landing; the mark waits for it");
    }

    [Fact]
    public void Every_exit_spends_the_mark()
    {
        // Pinned from source: the stand-down exits need a foreground window of
        // ours or the connect quiet scope, which only a running app has.
        AssertTheDeskIsSafe();
        string main = Src("JJFlexWpf", "MainWindow.xaml.cs");

        string landing = Body(main, "private void RunReturnToAppLanding(string? rescueLead = null)");
        int standDown = landing.IndexOf("another window of ours holds the foreground - standing down", StringComparison.Ordinal);
        int standDownReturn = landing.IndexOf("return;", standDown, StringComparison.Ordinal);
        int spentAtStandDown = landing.IndexOf("TakeArrivalJustReplayed();", standDown, StringComparison.Ordinal);
        Assert.True(standDown > 0 && spentAtStandDown > standDown && spentAtStandDown < standDownReturn,
            "the foreground stand-down spends the mark before it returns");
        int taken = landing.IndexOf("bool arrivalJustReplayed = TakeArrivalJustReplayed();", StringComparison.Ordinal);
        int rig = landing.IndexOf("var rig = RigControl;", StringComparison.Ordinal);
        Assert.True(taken > standDownReturn && rig > taken, "taken before either branch is chosen");

        int callback = main.IndexOf("JJFlexDialog.FocusReturnCallback = () =>", StringComparison.Ordinal);
        int quiet = main.IndexOf("if (_connectQuiet.IsQuiet)", callback, StringComparison.Ordinal);
        int quietReturn = main.IndexOf("return;", quiet, StringComparison.Ordinal);
        int spentWhenQuiet = main.IndexOf("TakeArrivalJustReplayed();", quiet, StringComparison.Ordinal);
        Assert.True(callback > 0 && quiet > callback && spentWhenQuiet > quiet && spentWhenQuiet < quietReturn,
            "a close the quiet scope stands down spends the mark too");
    }

    // ── helpers ──

    private static string Src(params string[] parts)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return File.ReadAllText(Path.Combine(d!.FullName, Path.Combine(parts))).Replace("\r\n", "\n");
    }

    /// <summary>The text of one method, from its signature to the next member at the same indent.</summary>
    private static string Body(string src, string signature)
    {
        int start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        int open = src.IndexOf("\n    {", start, StringComparison.Ordinal);
        int close = src.IndexOf("\n    }\n", open, StringComparison.Ordinal);
        Assert.True(open > start && close > open, signature + " has no body");
        return src.Substring(start, close - start);
    }
}
