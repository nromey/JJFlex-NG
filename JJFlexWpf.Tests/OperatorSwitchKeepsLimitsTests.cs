using System;
using System.Reflection;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// An operator switch never changes the limits of a transmission already under
/// way (#1072 repair, Codex's B1). The new operator's settings are held while
/// the controller is anywhere but Idle and take over when the transmission
/// ends, whichever way it ends; when nothing is keyed they apply at once.
/// The probe that found it: lock as A with a 180-second timeout, switch to B
/// with 600 at 175 seconds, and the cut at 180 never came.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> No window: the real controller and an unconnected
/// <see cref="FlexBase"/> only, on an MTA thread (asserted first in every
/// test). The rig's transmit setter enqueues onto a command queue that is not
/// running. <see cref="QuietRun"/> has render off; chirps and speech are off.
/// The timeout ladder's DispatcherTimer never pumps here, so its tick is
/// invoked directly and the lock's clock is moved back by reflection.
/// </remarks>
public sealed class OperatorSwitchKeepsLimitsTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;

    public OperatorSwitchKeepsLimitsTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(() => _rig, () => true, Operator("A", 180, alcSeconds: 60, cut: true));
    }

    public void Dispose()
    {
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        _rig.Dispose();
    }

    private static void Guard()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
    }

    private static PttConfig Operator(string name, int timeout, int alcSeconds, bool cut) => new PttConfig
    {
        TimeoutSeconds = timeout,
        AlcAutoReleaseSeconds = alcSeconds,
        CutTransmitOnReflectedAlarm = cut,
        ChirpEnabled = false,
        SpeechEnabled = false,
    };

    // ---- plumbing, by name: a missing production member is a red test ----

    private T Read<T>(string member)
    {
        var p = typeof(PttSafetyController).GetProperty(member, Any);
        if (p != null) return (T)p.GetValue(_controller)!;
        var f = typeof(PttSafetyController).GetField(member, Any);
        Assert.True(f != null, "production member PttSafetyController." + member + " is missing");
        return (T)f!.GetValue(_controller)!;
    }

    private double EffectiveTimeout => Read<double>("EffectiveTimeoutSeconds");
    private PttConfig InUse => Read<PttConfig>("_config");

    /// <summary>Move the lock's clock so it reads <paramref name="seconds"/> elapsed.</summary>
    private void LockedFor(double seconds)
    {
        var clock = Read<object>("_lockClock");
        var start = clock.GetType().GetField("_startMs", Any);
        Assert.True(start != null, "LockTimeoutClock._startMs is missing");
        start!.SetValue(clock, Environment.TickCount64 - (long)(seconds * 1000));
    }

    private void Tick()
    {
        var m = typeof(PttSafetyController).GetMethod("WarningTimerTick", Any);
        Assert.True(m != null, "production entry PttSafetyController.WarningTimerTick is missing");
        m!.Invoke(_controller, new object?[] { null, EventArgs.Empty });
    }

    private void Lock()
    {
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
    }

    // ---- the probe Codex ran, as a test ----

    [Fact]
    public void A_switch_at_175_seconds_to_a_longer_timeout_still_cuts_at_the_old_180()
    {
        Guard();
        Lock();
        LockedFor(175);
        Tick();
        Assert.Equal(PttSafetyController.PttState.OhCrap, _controller.State);

        var b = Operator("B", 600, alcSeconds: 0, cut: false);
        _controller.UpdateConfig(b);

        // Still keyed under A's limits, every one of them.
        Assert.Equal(PttSafetyController.PttState.OhCrap, _controller.State);
        Assert.Equal(180, EffectiveTimeout);
        Assert.Equal(60, InUse.AlcAutoReleaseSeconds);
        Assert.True(TransmitKillSwitch.CutOnReflectedAlarm?.Invoke() ?? false,
            "the reflected-power cutoff the transmission was keyed under went with the switch");

        LockedFor(185);
        Tick();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        // The cut ended it, and B's settings are in use from then on.
        Assert.Same(b, InUse);
        Assert.Equal(600, EffectiveTimeout);
        Assert.False(TransmitKillSwitch.CutOnReflectedAlarm?.Invoke() ?? true);
    }

    [Fact]
    public void A_switch_to_a_shorter_timeout_does_not_cut_a_transmission_already_past_it()
    {
        Guard();
        Lock();
        LockedFor(100);
        _controller.UpdateConfig(Operator("B", 60, alcSeconds: 60, cut: true));
        Tick();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        Assert.Equal(180, EffectiveTimeout);
    }

    [Fact]
    public void With_nothing_keyed_the_new_settings_apply_at_once()
    {
        Guard();
        var b = Operator("B", 600, alcSeconds: 0, cut: false);
        _controller.UpdateConfig(b);
        Assert.Same(b, InUse);
        Assert.Null(Read<PttConfig?>("_heldConfig"));
        Assert.Equal(600, EffectiveTimeout);
    }

    [Fact]
    public void A_second_switch_before_the_end_replaces_the_held_settings()
    {
        Guard();
        Lock();
        _controller.UpdateConfig(Operator("B", 600, alcSeconds: 0, cut: false));
        var c = Operator("C", 300, alcSeconds: 30, cut: true);
        _controller.UpdateConfig(c);
        Assert.Equal(180, EffectiveTimeout);

        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.Same(c, InUse);
    }

    public static TheoryData<string> Ends => new() { "unlock", "escape", "kill", "hold released", "connection fell", "teardown" };

    [Theory]
    [MemberData(nameof(Ends))]
    public void However_the_transmission_ends_the_held_settings_take_over(string end)
    {
        Guard();
        if (end == "hold released") _controller.PttDown();
        else Lock();
        Assert.NotEqual(PttSafetyController.PttState.Idle, _controller.State);

        var b = Operator("B", 600, alcSeconds: 0, cut: false);
        _controller.UpdateConfig(b);
        Assert.NotSame(b, InUse);

        switch (end)
        {
            case "unlock": _controller.ToggleLock(); break;
            case "escape": _controller.EscapeUnlock(); break;
            case "kill": _controller.KillTransmitNow("the operator switch test"); break;
            case "hold released": _controller.PttUp(); break;
            case "connection fell": _controller.DropWithConnection("the connection fell in the operator switch test"); break;
            case "teardown": _controller.ReleaseForTeardown(); break;
        }

        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.Same(b, InUse);
        Assert.Null(Read<PttConfig?>("_heldConfig"));
    }
}
