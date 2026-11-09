using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// No callsign, no transmit (#335) at the operator's doors: push to talk, the
/// lock, a check's own key, and every door that asks
/// <see cref="ZeroPowerDoors.RefuseTransmit"/> or
/// <see cref="ZeroPowerDoors.RefuseTune"/>. Ruled by Noel 2026-11-08: every
/// keying JJ Flexible starts is refused, the checks included, with one
/// sentence naming the missing callsign and where to enter it.
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread and an unconnected <see cref="FlexBase"/>
/// whose writes go nowhere. The operator's callsign is stood in for by
/// <see cref="ZeroPowerDoors.NoCallsignOverride"/>; the radio-side chokepoints
/// are walked against the vendor fake radio in
/// <c>Radios.Tests.CallsignGateTests</c>. Shares ZeroPowerDoors' process-wide
/// overrides with <see cref="ZeroPowerDoorsTests"/>, so the same collection.
/// </remarks>
[Collection("TuneDoors")]
public sealed class CallsignDoorsTests : IDisposable
{
    private const string NoCall = "No callsign entered, so nothing was transmitted. Enter yours in the Radio menu, under Operators.";
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
    private readonly PttSafetyController _controller;
    private readonly VerbosityLevel _verbosityWas;
    private bool _noCallsign = true;
    private long _now = 1_000_000;
    private readonly List<string> _answers = new();

    public CallsignDoorsTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        _verbosityWas = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        ZeroPowerDoors.Forget();
        ZeroPowerDoors.AtZeroOverride = (rig, tune) => false;   // power is fine: only the callsign decides
        ZeroPowerDoors.NoCallsignOverride = rig => ReferenceEquals(rig, _rig) && _noCallsign;
        ZeroPowerDoors.Clock = () => _now;
        ZeroPowerDoors.Answered = _answers.Add;
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
    }

    public void Dispose()
    {
        ZeroPowerDoors.AtZeroOverride = null;
        ZeroPowerDoors.NoCallsignOverride = null;
        ZeroPowerDoors.Answered = null;
        ZeroPowerDoors.Clock = () => Environment.TickCount64;
        ZeroPowerDoors.Forget();
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        ScreenReaderOutput.CurrentVerbosity = _verbosityWas;
        typeof(FlexBase).GetField("theRadio", Any)!.SetValue(_rig, null);
        try { _rig.Dispose(); } catch { }
    }

    private static List<string> Said(Action press)
    {
        string marker = "marker " + Guid.NewGuid().ToString("N");
        ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
        press();
        return ScreenReaderOutput.RecentMessages.TakeWhile(m => m != marker).Reverse().ToList();
    }

    [Fact]
    public void Push_to_talk_is_refused_aloud_and_keys_nothing()
    {
        Assert.Equal(new[] { NoCall }, Said(_controller.PttDown));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void Push_to_talk_keys_once_a_callsign_is_entered()
    {
        _noCallsign = false;             // the positive control: the same press on the same controller
        _controller.PttDown();
        Assert.Equal(PttSafetyController.PttState.PttHold, _controller.State);
    }

    [Fact]
    public void The_lock_is_refused_aloud_and_keys_nothing()
    {
        Assert.Equal(new[] { NoCall }, Said(_controller.ToggleLock));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    /// <summary>
    /// The difference from the zero-power rule: a check's own key is gated too.
    /// Before this, KeyForCheck went straight to Locked with no callsign.
    /// </summary>
    [Fact]
    public void A_checks_own_key_is_refused_too()
    {
        Assert.Equal(new[] { NoCall }, Said(_controller.KeyForCheck));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        _noCallsign = false;
        _controller.KeyForCheck();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
    }

    [Fact]
    public void An_unlock_a_release_and_Escape_are_never_refused()
    {
        _noCallsign = false;
        _controller.ToggleLock();
        _noCallsign = true;              // the operator cleared the callsign while keyed
        Assert.DoesNotContain(NoCall, Said(_controller.ToggleLock));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        _noCallsign = false;
        _controller.PttDown();
        _noCallsign = true;
        _controller.PttUp();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        _noCallsign = false;
        _controller.ToggleLock();
        _noCallsign = true;
        _controller.EscapeUnlock();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void Every_door_that_asks_about_power_asks_about_the_callsign_first()
    {
        Assert.True(ZeroPowerDoors.RefuseTransmit(_rig, "a send"));
        Assert.True(ZeroPowerDoors.RefuseTune(_rig, "the tune carrier"));
        Assert.Equal(new[] { "callsign: a send", "callsign: the tune carrier" }, _answers);
        _noCallsign = false;
        Assert.False(ZeroPowerDoors.RefuseTransmit(_rig, "a send"));
        Assert.False(ZeroPowerDoors.RefuseTune(_rig, "the tune carrier"));
    }

    /// <summary>
    /// Told the usual way, as the zero-power refusal is: the sentence at Terse,
    /// so the operator's own verbosity setting governs it.
    /// </summary>
    [Fact]
    public void The_refusal_is_told_the_usual_way_at_Terse()
    {
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        Assert.Equal(new[] { NoCall }, Said(_controller.PttDown));
    }

    [Fact]
    public void A_rig_with_no_callsign_reader_keys_as_it_always_has()
    {
        ZeroPowerDoors.NoCallsignOverride = null;   // the real read: this rig was handed no reader
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
    }
}
