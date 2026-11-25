using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #901: a power-on raised while the radio is already on built a second
/// push-to-talk controller and left the first running, its timers acting
/// through <c>() =&gt; RigControl</c>. Now an idle live controller is disposed
/// before its replacement is built, and one with the key in use is kept (no
/// second is built, nothing unkeys). A key in use on a DIFFERENT rig is #915's
/// reconnect path and is left as it was.
/// </summary>
/// <remarks>
/// <para><b>Desk safety, as <c>KeyDropWithConnectionTests</c> does it.</b> No
/// window is constructed: the MainWindow is
/// <see cref="RuntimeHelpers.GetUninitializedObject"/>, only its PTT fields and
/// <c>RigControl</c> are set, and the method the power-on calls at its
/// controller assignment is invoked. Every test asserts an MTA thread, where
/// WPF refuses to build any element. The controllers have chirps and speech
/// off; the rigs are unconnected <see cref="FlexBase"/> objects whose transmit
/// writes enqueue onto a queue that is not running. Timers never pump.</para>
/// <para>Adapted from #915's measured scratch test (two unconnected rigs, the
/// real controller, its timers read by their private fields).</para>
/// <para><b>Not reached:</b> the whole of <c>PowerNowOn</c>, whose earlier
/// steps need the window's controls. Its call of
/// <c>PttControllerAtPowerOn</c> is pinned by source.</para>
/// </remarks>
public sealed class PowerOnPttControllerTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly FlexBase _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
    private readonly FlexBase _otherRig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
    private readonly PttSafetyController _old;
    private PttSafetyController? _built;
    private int _builds;
    private readonly MainWindow _main;
    private readonly VerbosityLevel _verbosityBefore;

    public PowerOnPttControllerTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;
        _main = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        _old = NewController();
        Put("_pttController", _old);
        Put("_pttControllerRig", _rig);
        _main.RigControl = _rig;
    }

    public void Dispose()
    {
        foreach (var c in new[] { _old, _built })
        {
            if (c == null) continue;
            try
            {
                if (c.IsTransmitting) c.EscapeUnlock();
                c.Dispose();
            }
            catch { }
        }
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        _rig.Dispose();
        _otherRig.Dispose();
    }

    private PttSafetyController NewController() =>
        new PttSafetyController(() => _main.RigControl, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });

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
        f.SetValue(_main, value);
    }

    private PttSafetyController PowerOn() => _main.PttControllerAtPowerOn(() =>
    {
        _builds++;
        _built = NewController();
        return _built;
    });

    private static bool Disposed(PttSafetyController c) =>
        (bool)typeof(PttSafetyController).GetField("_disposed", Any)!.GetValue(c)!;

    private static string[] RunningTimers(PttSafetyController c) =>
        new[] { "_warningTimer", "_beepTimer", "_hardKillTimer", "_alcTimer" }
            .Where(n => typeof(PttSafetyController).GetField(n, Any)!.GetValue(c) is DispatcherTimer { IsEnabled: true })
            .ToArray();

    [Theory]
    [InlineData(false, false, false, "Build")]
    [InlineData(false, true, true, "Build")]
    [InlineData(true, true, true, "DisposeThenBuild")]
    [InlineData(true, true, false, "DisposeThenBuild")]
    [InlineData(true, false, true, "KeepLive")]
    [InlineData(true, false, false, "LeaveForReconnectPath")]
    public void The_rule(bool haveLive, bool liveIdle, bool sameRig, string step)
    {
        AssertTheDeskIsSafe();
        Assert.Equal(step, MainWindow.DecidePowerOnPtt(haveLive, liveIdle, sameRig).ToString());
    }

    [Fact]
    public void An_idle_live_controller_is_disposed_and_only_the_new_one_remains()
    {
        AssertTheDeskIsSafe();
        Assert.True(MainWindow.PttControllerIsIdle(_old));

        var now = PowerOn();

        Assert.Equal(1, _builds);
        Assert.Same(_built, now);
        Assert.NotSame(_old, now);
        Assert.True(Disposed(_old), "the old controller was disposed");
        Assert.Empty(RunningTimers(_old));
        Assert.False(Disposed(now));
    }

    [Fact]
    public void A_live_controller_with_the_key_locked_is_kept_still_keyed_and_no_second_is_built()
    {
        AssertTheDeskIsSafe();
        _old.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _old.State);
        Assert.Contains("_hardKillTimer", RunningTimers(_old));   // positive control: its timers run
        Assert.False(MainWindow.PttControllerIsIdle(_old));

        var now = PowerOn();

        Assert.Same(_old, now);
        Assert.Equal(0, _builds);
        Assert.Equal(PttSafetyController.PttState.Locked, _old.State);
        Assert.False(Disposed(_old));
        Assert.Contains("_hardKillTimer", RunningTimers(_old));
    }

    [Fact]
    public void A_held_key_on_a_different_rig_is_left_to_the_reconnect_path_as_before()
    {
        AssertTheDeskIsSafe();
        _old.ToggleLock();
        _main.RigControl = _otherRig;

        var now = PowerOn();

        // Unchanged from before #901: a new controller is built and the old
        // one is neither disposed nor unkeyed here (#915 owns its teardown).
        Assert.Equal(1, _builds);
        Assert.Same(_built, now);
        Assert.False(Disposed(_old));
        Assert.Equal(PttSafetyController.PttState.Locked, _old.State);
    }

    [Fact]
    public void With_no_live_controller_one_is_built()
    {
        AssertTheDeskIsSafe();
        Put("_pttController", null);
        var now = PowerOn();
        Assert.Equal(1, _builds);
        Assert.Same(_built, now);
        Assert.False(Disposed(_old));
    }

    [Fact]
    public void The_power_on_assigns_its_controller_through_the_rule()
    {
        AssertTheDeskIsSafe();
        string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs"));
        int on = src.IndexOf("private void PowerNowOn()", StringComparison.Ordinal);
        Assert.True(on > 0);
        Assert.True(src.IndexOf("_pttController = PttControllerAtPowerOn(() => new PttSafetyController(", on, StringComparison.Ordinal) > on);
        Assert.Equal(1, Count(src, "new PttSafetyController("));
    }

    private static int Count(string s, string token)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(token, i, StringComparison.Ordinal)) >= 0) { n++; i += token.Length; }
        return n;
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "repo root not found");
        return dir!.FullName;
    }
}
