using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #1020: a held push to talk has the lock's limit. Ruled by Noel 2026-11-09
/// 20:27 and 2026-11-11 11:41: every held key warns 30, 15 and 5 seconds
/// before fifteen minutes from its FIRST press and is then ended; the lock's
/// own timeout is the lock's only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Driven through the production route:</b> the real
/// <see cref="PttSafetyController"/>, MainWindow's own edges
/// (<c>HandleSystemWidePttEdge</c>) and the real <see cref="PttHoldFilter"/>.
/// The held key's clock is the controller's injectable
/// <c>HoldClockMs</c>, so minute fifteen is reached without waiting for it;
/// the shipped 900 s is never shortened. A tick is the production timer's own
/// Tick handler, invoked, as <see cref="ReferenceEndHeldKeyTests"/> runs the
/// deferral timer: no dispatcher is pumped.
/// </para>
/// <para>
/// <b>Desk safety.</b> No window is constructed: MainWindow is
/// <see cref="RuntimeHelpers.GetUninitializedObject"/>, and every test asserts
/// the thread is MTA, where WPF refuses to build any element. Chirps and
/// push-to-talk speech are off, <see cref="QuietRun"/> has render off, and the
/// rigs are unconnected <see cref="FlexBase"/>s whose transmit writes go
/// nowhere. Every keying assertion lands on the controller's state and its RF
/// command (<c>_rfCommandedOn</c>).
/// </para>
/// </remarks>
public sealed class HeldKeyLimitTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly long KillMs = PttConfig.HardKillSeconds * 1000L;

    private readonly FlexBase _rig;
    private readonly FlexBase _otherRig;
    private FlexBase _source;
    private readonly PttSafetyController _controller;
    private readonly PttHoldFilter _filter = new();
    private readonly MainWindow _main;
    private readonly VerbosityLevel _verbosityBefore;
    private long _now = 1_000_000;

    public HeldKeyLimitTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _otherRig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _source = _rig;
        _controller = new PttSafetyController(() => _source, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        _controller.HoldClockMs = () => _now;

        _main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        Put("_pttController", _controller);
        Put("_radioPowerOn", true);
        Put("_pttHoldFilter", _filter);
        // As MainWindow wires it where it builds the controller (pinned below).
        _controller.HoldEndedByApp = why => Call("LatchAppEndedHold", why);
    }

    public void Dispose()
    {
        ((DispatcherTimer?)typeof(MainWindow).GetField("_pttDeferTimer", Any)?.GetValue(_main))?.Stop();
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        _rig.Dispose();
        _otherRig.Dispose();
    }

    // ── plumbing ──

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

    private object? Call(string method, params object[] args)
    {
        var m = typeof(MainWindow).GetMethod(method, Any);
        Assert.True(m != null, "production entry MainWindow." + method + " is missing");
        return m!.Invoke(_main, args);
    }

    private static T Field<T>(object owner, string name)
    {
        var f = owner.GetType().GetField(name, Any);
        Assert.True(f != null, "production field " + owner.GetType().Name + "." + name + " is missing");
        return (T)f!.GetValue(owner)!;
    }

    private bool RfOn => Field<bool>(_controller, "_rfCommandedOn");
    private bool Latched => (bool)Get("_pttHoldEndedByApp")!;

    private static PttSafetyController.PttState Hold => PttSafetyController.PttState.PttHold;
    private static PttSafetyController.PttState Idle => PttSafetyController.PttState.Idle;

    private DispatcherTimer? HoldTimer => Field<DispatcherTimer?>(_controller, "_holdTimer");

    /// <summary>The held key's timer ticks once (its own Tick handler, invoked).</summary>
    private static void Tick(DispatcherTimer timer)
    {
        var tick = (EventHandler?)typeof(DispatcherTimer).GetField("Tick", Any)?.GetValue(timer);
        Assert.True(tick != null, "the production timer has no Tick handler");
        tick!(timer, EventArgs.Empty);
    }

    /// <summary>The held key's clock moves to <paramref name="msFromFirst"/> after the first press, and its timer ticks.</summary>
    private void At(long firstPress, long msFromFirst)
    {
        _now = firstPress + msFromFirst;
        var t = HoldTimer;
        Assert.True(t != null, "no held key timer is running");
        Tick(t!);
    }

    private void Edge(bool down) => Call("HandleSystemWidePttEdge", down);

    private static void SyntheticPair(Action<bool> edge)
    {
        edge(true);
        edge(false);
    }

    private void DeferralRunsOut()
    {
        var timer = (DispatcherTimer?)Get("_pttDeferTimer");
        Assert.True(timer != null, "no deferral timer was armed");
        timer!.Stop();
        Tick(timer);
    }

    private string? _marker;

    /// <summary>How many times the sentence was said since <see cref="Mark"/>: the history is shared by every test.</summary>
    private int Said(string lexiconKey)
    {
        Assert.True(_marker != null, "Mark() first");
        var recent = ScreenReaderOutput.RecentMessages;   // newest first
        int at = recent.ToList().IndexOf(_marker!);
        Assert.True(at >= 0, "the marker fell out of the speech history");
        return recent.Take(at).Count(m => m == Lexicon.Get(lexiconKey));
    }

    private void Mark()
    {
        _marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(_marker, VerbosityLevel.Critical, true);
    }

    // ── the ladder ──

    [Fact]
    public void A_held_key_warns_thirty_fifteen_and_five_seconds_before_fifteen_minutes_then_is_ended()
    {
        AssertTheDeskIsSafe();
        Mark();
        long first = _now;
        _controller.PttDown();
        Assert.Equal(Hold, _controller.State);
        Assert.True(RfOn);

        At(first, KillMs - 31_000);
        Assert.Equal(Hold, _controller.HoldStage);   // nothing before thirty seconds out

        At(first, KillMs - 30_000);
        Assert.Equal(PttSafetyController.PttState.Warning1, _controller.HoldStage);
        Assert.Equal(1, Said("audio.ptt.hold_limit_approaching"));
        Assert.Equal(Hold, _controller.State);       // a stage of the hold, never a state of its own

        At(first, KillMs - 15_000);
        Assert.Equal(PttSafetyController.PttState.Warning2, _controller.HoldStage);
        Assert.Equal(1, Said("audio.ptt.hold_limit_soon"));

        At(first, KillMs - 5_000);
        Assert.Equal(PttSafetyController.PttState.OhCrap, _controller.HoldStage);
        Assert.Equal(1, Said("audio.ptt.hold_limit_ending"));

        At(first, KillMs - 1);
        Assert.Equal(Hold, _controller.State);
        At(first, KillMs);
        Assert.Equal(Idle, _controller.State);
        Assert.False(RfOn, "the hold was not unkeyed at its limit");
        Assert.Equal(1, Said("audio.ptt.hold_limit_released"));
    }

    [Fact]
    public void The_lock_timeout_does_not_apply_to_a_held_key()
    {
        AssertTheDeskIsSafe();
        long first = _now;
        _controller.PttDown();
        // The lock's default timeout is 180 s; a held key runs past it untouched.
        for (long s = 1; s <= 600; s++) At(first, s * 1000);
        Assert.Equal(Hold, _controller.State);
        Assert.Equal(Hold, _controller.HoldStage);
        Assert.True(RfOn);
    }

    // ── trap 1: a release in a warning still unkeys ──

    [Theory]
    [InlineData(30_000, PttSafetyController.PttState.Warning1)]
    [InlineData(15_000, PttSafetyController.PttState.Warning2)]
    [InlineData(5_000, PttSafetyController.PttState.OhCrap)]
    public void A_release_in_any_warning_unkeys(long beforeKill, PttSafetyController.PttState stage)
    {
        AssertTheDeskIsSafe();
        long first = _now;
        _controller.PttDown();
        At(first, KillMs - beforeKill);
        Assert.Equal(stage, _controller.HoldStage);

        _controller.PttUp();
        Assert.Equal(Idle, _controller.State);
        Assert.False(RfOn, "a release in " + stage + " left the transmitter keyed");
        Assert.False(HoldTimer?.IsEnabled == true, "the held key's limit outlived the hold");
    }

    // ── trap 2: a key still held after the limit keys nothing ──

    [Fact]
    public void A_jaws_hold_ended_at_its_limit_does_not_key_the_radio_again_until_really_let_go()
    {
        AssertTheDeskIsSafe();
        long first = _now;
        Edge(true);
        Assert.Equal(Hold, _controller.State);
        Edge(false);                                   // implausibly fast: a synthetic up
        Assert.True(_filter.SynthesisDetected, "positive control: the filter saw the synthetic signature");
        SyntheticPair(Edge);                           // absorbed: still one hold
        Assert.Equal(Hold, _controller.State);

        At(first, KillMs);
        Assert.Equal(Idle, _controller.State);

        // The key is still down: JAWS goes on sending pairs.
        for (int i = 0; i < 6; i++)
        {
            SyntheticPair(Edge);
            Assert.Equal(Idle, _controller.State);    // the mutation: no latch, and a pair keys the radio again
        }
        Assert.True(Latched, "the hold the limit ended was not latched");

        // Let go: the last up defers, and no re-down claims it.
        Assert.True(_filter.ReleasePending);
        DeferralRunsOut();
        Assert.False(Latched, "a real release ends the latch");

        // The control: a press after a real release keys as normal.
        Edge(true);
        Assert.Equal(Hold, _controller.State);
    }

    [Fact]
    public void An_nvda_hold_ended_at_its_limit_waits_for_the_real_release_and_a_new_press_keys()
    {
        AssertTheDeskIsSafe();
        long first = _now;
        Edge(true);
        At(first, KillMs);
        Assert.Equal(Idle, _controller.State);
        Assert.True(Latched);

        // NVDA passes a real hold: nothing more arrives until the operator lets go.
        Thread.Sleep(PttHoldFilter.ImplausibleReleaseMs + 30);
        Edge(false);
        Assert.False(_filter.SynthesisDetected);
        Assert.False(Latched, "a real release under NVDA ends the latch at once");
        Edge(true);
        Assert.Equal(Hold, _controller.State);
    }

    [Fact]
    public void MainWindow_latches_the_hold_its_controller_ends()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs"));
        Assert.Contains("_pttController.HoldEndedByApp = LatchAppEndedHold;", src);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── counted from the first press ──

    [Fact]
    public void A_release_and_press_inside_the_hold_filters_bridge_is_the_same_hold()
    {
        AssertTheDeskIsSafe();
        long first = _now;
        _controller.PttDown();
        At(first, KillMs - 30_000);                    // into the first warning
        _now = first + KillMs - 29_000;
        _controller.PttUp();                           // a pair the filter did not absorb
        _now += 250;
        _controller.PttDown();
        Assert.Equal(Hold, _controller.State);
        Assert.Equal(PttSafetyController.PttState.Warning1, _controller.HoldStage);

        At(first, KillMs);
        Assert.Equal(Idle, _controller.State);         // fifteen minutes from the FIRST press
        Assert.False(RfOn);
    }

    [Fact]
    public void A_press_after_the_bridge_is_a_new_hold_with_its_own_fifteen_minutes()
    {
        AssertTheDeskIsSafe();
        long first = _now;
        _controller.PttDown();
        _now = first + 10 * 60_000;
        _controller.PttUp();
        _now += PttHoldSpan.ContinuesWithinMs;
        long second = _now;
        _controller.PttDown();
        At(first, KillMs);
        Assert.Equal(Hold, _controller.State);
        Assert.Equal(Hold, _controller.HoldStage);
        At(second, KillMs);
        Assert.Equal(Idle, _controller.State);
    }

    // ── trap 3: the lock takes over with its own fresh limit, as today ──

    [Fact]
    public void A_hold_that_becomes_the_lock_at_minute_fourteen_gets_the_locks_fresh_fifteen()
    {
        AssertTheDeskIsSafe();
        long first = _now;
        _controller.PttDown();
        At(first, 14 * 60_000);
        var holdTimer = HoldTimer!;
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        Assert.False(holdTimer.IsEnabled, "the held key's limit outlived the hold");

        // The held key's limit no longer acts...
        _now = first + KillMs;
        Tick(holdTimer);
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        // ...and the lock's hard kill is fresh: its whole fifteen minutes from now.
        var hardKill = Field<DispatcherTimer?>(_controller, "_hardKillTimer");
        Assert.True(hardKill != null && hardKill.IsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(PttConfig.HardKillSeconds), hardKill!.Interval);
    }

    // ── one hold, one kill ──

    [Fact]
    public void A_fifteen_minute_system_wide_hold_is_unkeyed_once_with_one_sentence()
    {
        AssertTheDeskIsSafe();
        Mark();
        var watch = new SystemWidePttWatch();
        long first = _now;
        watch.NoteDown(first);
        Edge(true);
        Assert.Equal(Hold, _controller.State);

        int controllerEnds = 0, watchdogVerdicts = 0;
        bool wasOn = RfOn;
        for (long t = first; t <= first + 20 * 60_000; t += SystemWidePttWatch.PollMs)
        {
            _now = t;
            if ((t - first) % 1000 == 0 && HoldTimer is { } ht) Tick(ht);
            bool holding = _controller.State == Hold;
            var v = watch.Poll(t, physicallyDown: true, controllerHolding: holding);
            if (v != SystemWidePttWatch.Verdict.Quiet) watchdogVerdicts++;
            if (wasOn && !RfOn) controllerEnds++;
            wasOn = RfOn;
        }
        Assert.Equal(1, controllerEnds);
        Assert.Equal(0, watchdogVerdicts);
        Assert.Equal(1, Said("audio.ptt.hold_limit_released"));
    }

    // ── the hold's timer never acts on another connection ──

    [Fact]
    public void The_held_keys_limit_stops_at_teardown_and_never_acts_after_it()
    {
        AssertTheDeskIsSafe();
        Mark();
        long first = _now;
        _controller.PttDown();
        var holdTimer = HoldTimer!;
        Assert.True(holdTimer.IsEnabled);

        _controller.ReleaseRigForTeardown("test teardown");
        Assert.False(holdTimer.IsEnabled, "the held key's limit survived teardown");

        // Even if its tick still ran, it does nothing.
        _now = first + KillMs;
        Tick(holdTimer);
        Assert.Equal(0, Said("audio.ptt.hold_limit_released"));
        Assert.True(RfOn, "a stale limit unkeyed");
    }

    [Fact]
    public void A_replaced_controllers_held_key_limit_does_not_act_on_the_new_radio()
    {
        AssertTheDeskIsSafe();
        Mark();
        long first = _now;
        _controller.PttDown();
        var holdTimer = HoldTimer!;

        // A reconnect or a radio switch: the controller's rig source now gives the new radio.
        _source = _otherRig;
        _now = first + KillMs;
        Tick(holdTimer);
        Assert.Equal(Hold, _controller.State);
        Assert.True(RfOn, "the old hold's limit unkeyed the new radio");
        Assert.Equal(0, Said("audio.ptt.hold_limit_released"));
        Assert.False(holdTimer.IsEnabled);
    }
}
