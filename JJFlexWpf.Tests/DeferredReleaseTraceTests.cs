using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using JJFlexWpf.Tests.Infrastructure;
using JJTrace;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The trace line for a deferred push to talk release records the hold
/// filter's DECISION that the key was let go, never an unkey (#704, repair
/// one). The filter goes on judging the key after the app has ended the
/// hold itself (a reference recording reaching its end leaves the
/// controller idle), and a hold can become a lock while a release is
/// pending; in both, the release changes nothing on the air, and a line
/// saying "unkeyed" would be a cause nobody established.
/// </summary>
/// <remarks>
/// <para><b>Created state, through production.</b> The pure filter tests
/// cannot see this: the controller's state is the host's. Here the hold is
/// keyed through MainWindow's own system-wide edge
/// (<c>HandleSystemWidePttEdge</c>), the reference's end is MainWindow's own
/// <c>HandleReferenceEndUnkey</c>, the hold filter and the controller are the
/// real ones, and the deferral running out is the production timer's own Tick,
/// invoked, as <see cref="DialogTransmitKeysTests"/> does.</para>
/// <para><b>Desk safety.</b> No window: MainWindow is
/// <see cref="RuntimeHelpers.GetUninitializedObject"/>, so no constructor runs,
/// no XAML loads and no HWND exists, and every test asserts the thread is MTA,
/// where WPF refuses to build any element. The controller has chirps and speech
/// off, <see cref="QuietRun"/> has render off, and the rig is an unconnected
/// <see cref="FlexBase"/> whose transmit writes go nowhere.</para>
/// </remarks>
public sealed class DeferredReleaseTraceTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly PttHoldFilter _filter = new();
    private readonly MainWindow _main;

    public DeferredReleaseTraceTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        _main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        Put("_pttController", _controller);
        Put("_radioPowerOn", true);
        Put("_pttHoldFilter", _filter);
    }

    public void Dispose()
    {
        DialogTransmitKeys.HoldWindowLost("test teardown");
        ((System.Windows.Threading.DispatcherTimer?)typeof(MainWindow).GetField("_pttDeferTimer", Any)?.GetValue(_main))?.Stop();
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        _rig.Dispose();
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

    private static void AssertTheDeskIsSafe()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    private static PttSafetyController.PttState Hold => PttSafetyController.PttState.PttHold;
    private static PttSafetyController.PttState Idle => PttSafetyController.PttState.Idle;
    private static PttSafetyController.PttState Locked => PttSafetyController.PttState.Locked;

    private void Edge(bool down) => Call("HandleSystemWidePttEdge", down);

    private void SyntheticPair()
    {
        Edge(true);
        Edge(false);
    }

    /// <summary>A held Ctrl+Space as JAWS delivers it: keyed, its pairs absorbed, a release pending.</summary>
    private void JawsHoldKeyed()
    {
        Edge(true);
        Assert.Equal(Hold, _controller.State);
        Edge(false);                                   // implausibly fast: a synthetic up
        Assert.True(_filter.SynthesisDetected, "positive control: the filter saw the synthetic signature");
        SyntheticPair();
        Assert.Equal(Hold, _controller.State);
        Assert.True(_filter.ReleasePending, "positive control: a release is pending");
    }

    private sealed class CapturingListener : TraceListener
    {
        public readonly List<string> Lines = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { lock (Lines) Lines.Add(message ?? ""); }
    }

    /// <summary>The production deferral timer runs out (its Tick, invoked), with the trace captured.</summary>
    private List<string> DeferralRunsOutTraced()
    {
        var timer = (System.Windows.Threading.DispatcherTimer?)Get("_pttDeferTimer");
        Assert.True(timer != null, "no deferral timer was armed");
        timer!.Stop();
        var tick = (EventHandler?)typeof(System.Windows.Threading.DispatcherTimer).GetField("Tick", Any)?.GetValue(timer);
        Assert.True(tick != null, "the production timer has no Tick handler");

        var listener = new CapturingListener();
        bool onBefore = Tracing.On;
        TraceLevel levelBefore = Tracing.TheSwitch.Level;
        Trace.Listeners.Add(listener);
        try
        {
            Tracing.On = true;
            Tracing.TheSwitch.Level = TraceLevel.Info;
            tick!(timer, EventArgs.Empty);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            Tracing.TheSwitch.Level = levelBefore;
            Tracing.On = onBefore;
        }
        lock (listener.Lines) return listener.Lines.Where(l => l.Contains("PTT:", StringComparison.Ordinal)).ToList();
    }

    private static void AssertTheReleaseLineIsTheDecisionOnly(List<string> ptt)
    {
        // Positive control: the host traced its line for this release.
        string line = Assert.Single(ptt, l => l.Contains("deferred key-up", StringComparison.Ordinal));
        Assert.Contains("PTT: a deferred key-up was resolved as the key let go, after 0 probe extension(s)", line, StringComparison.Ordinal);
        Assert.Contains("not the transmitter's state", line, StringComparison.Ordinal);
        // The mutation: the line as it was ("a deferred release unkeyed ...").
        Assert.DoesNotContain(ptt, l => l.Contains("unkeyed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_release_after_the_app_ended_the_hold_is_traced_as_the_key_let_go_not_as_an_unkey()
    {
        AssertTheDeskIsSafe();
        JawsHoldKeyed();

        // The reference recording reaches its end: the app ends the hold
        // itself, and the filter goes on judging the key.
        Assert.True((bool)Call("HandleReferenceEndUnkey")!);
        Assert.Equal(Idle, _controller.State);
        Assert.True((bool)Get("_pttHoldEndedByApp")!, "positive control: the app-ended hold is latched");

        // The operator still holds the key for a while (the reader's pairs),
        // then lets go: the last up is deferred, and nothing claims it.
        for (int i = 0; i < 3; i++) SyntheticPair();
        Assert.True(_filter.ReleasePending, "positive control: the operator's release is deferred");

        var ptt = DeferralRunsOutTraced();

        Assert.Equal(Idle, _controller.State);         // nothing was keyed to unkey
        Assert.False((bool)Get("_pttHoldEndedByApp")!, "the real release ends the latch");
        Assert.NotNull(_filter.LastDeferredRelease);
        AssertTheReleaseLineIsTheDecisionOnly(ptt);
    }

    [Fact]
    public void A_release_pending_when_the_hold_became_a_lock_is_traced_as_the_key_let_go_and_the_lock_stays()
    {
        AssertTheDeskIsSafe();
        JawsHoldKeyed();

        // The lock latched while the hold's release was pending.
        _controller.ToggleLock();
        Assert.Equal(Locked, _controller.State);

        var ptt = DeferralRunsOutTraced();

        Assert.Equal(Locked, _controller.State);       // the release changed nothing on the air
        Assert.True(_controller.IsTransmitting);
        AssertTheReleaseLineIsTheDecisionOnly(ptt);
    }
}
