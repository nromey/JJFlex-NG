using System;
using System.Collections.Generic;
using System.IO;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A held key says the another-station refusal once (#911, #1022 item 2).
/// </summary>
/// <remarks>
/// <para>The harness, in simulation: a JAWS-style hold of Control+Space while
/// another station transmitted gave nine refusals, nine tones and nine
/// sentences in under three seconds, each cutting the one before. Every repeat
/// must still be refused; only the tone and the sentence are gated.</para>
/// <para><b>Desk safety.</b> As <see cref="AnotherStationTransmittingTests"/>:
/// the controller and an unconnected <see cref="FlexBase"/> only, on an MTA
/// thread (asserted first), with the radio's "another station?" answer and the
/// press clock replaced. Chirps are off and <see cref="QuietRun"/> has render
/// off. No window.</para>
/// </remarks>
public sealed class AnotherStationHeldKeyTests : IDisposable
{
    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly VerbosityLevel _verbosityBefore;
    private readonly List<string> _said = new();
    private bool _another = true;
    private long _now = 1_000_000;

    public AnotherStationHeldKeyTests()
    {
        Lexicon.Load(Lexicon.Partitions);
        _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
        _controller = new PttSafetyController(
            () => _rig,
            () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = true });
        _controller.AnotherStationTransmitting = _ => _another;
        _controller.RefusalClock = () => _now;
        _controller.AnotherStationRefusalSaid = door => _said.Add(door);
    }

    public void Dispose()
    {
        _another = false;
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
        _controller.Dispose();
        _rig.Dispose();
    }

    private static void Guard()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
    }

    /// <summary>JAWS: one down, about 512 ms, then down and up pairs about 250 ms apart.</summary>
    private void JawsHold(Action down, Action up, int pairs)
    {
        Down(down);
        _now += 512;
        for (int i = 0; i < pairs; i++)
        {
            up(); _now += 5; Down(down); _now += 245;
        }
        up();
    }

    /// <summary>A press, and proof it was refused: every repeat, not only the first.</summary>
    private void Down(Action down)
    {
        down();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(_controller.IsTransmitting);
        Assert.Equal(0, _controller.Keyings);
    }

    [Fact]
    public void A_JAWS_held_push_to_talk_is_refused_every_time_and_said_once()
    {
        Guard();
        JawsHold(_controller.PttDown, _controller.PttUp, pairs: 8);
        Assert.Equal(new[] { "push to talk" }, _said);
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(_controller.IsTransmitting);
    }

    [Fact]
    public void An_NVDA_held_push_to_talk_is_said_once()
    {
        Guard();
        // NVDA: one down, OS repeats about 33 ms apart, one up.
        Down(_controller.PttDown);
        _now += 500;
        for (int i = 0; i < 60; i++) { Down(_controller.PttDown); _now += 33; }
        _controller.PttUp();
        Assert.Equal(new[] { "push to talk" }, _said);
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void A_held_lock_key_is_refused_every_time_and_said_once()
    {
        Guard();
        // The lock key has no up of its own; a JAWS hold is just presses.
        for (int i = 0; i < 9; i++) { Down(_controller.ToggleLock); _now += 250; }
        Assert.Equal(new[] { "the lock key" }, _said);
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.False(_controller.IsTransmitting);
    }

    [Fact]
    public void A_fresh_press_after_the_window_is_said_again()
    {
        Guard();
        _controller.PttDown(); _controller.PttUp();
        _now += FreshPressGate.SameChordWindowMs + 100;
        _controller.PttDown(); _controller.PttUp();
        Assert.Equal(new[] { "push to talk", "push to talk" }, _said);
    }

    [Fact]
    public void The_two_doors_keep_their_own_gates()
    {
        Guard();
        _controller.PttDown(); _controller.PttUp();
        _now += 100;
        _controller.ToggleLock();
        Assert.Equal(new[] { "push to talk", "the lock key" }, _said);
    }

    [Fact]
    public void Control_with_nobody_else_transmitting_the_key_goes_through_at_once()
    {
        Guard();
        _controller.PttDown(); _controller.PttUp();       // refused, said
        _another = false;
        _now += 100;                                        // inside the gate's window
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        _controller.ToggleLock();                           // the unlock is never refused
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
        Assert.Equal(new[] { "push to talk" }, _said);
    }

    [Fact]
    public void The_gate_is_its_own_and_never_decides_the_refusal()
    {
        Guard();
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "PttSafetyController.cs"));
        // Internal since the third 2026-11-09 junk box batch: Check your
        // audio's start path calls it with its own door names (#910).
        int at = src.IndexOf("internal bool RefuseWhileAnotherStationTransmits(string door)", StringComparison.Ordinal);
        Assert.True(at > 0);
        string body = src.Substring(at, src.IndexOf("\n        }", at, StringComparison.Ordinal) - at);
        Assert.Contains("if (!AnotherStationRefusalIsNew(door)) return true;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ZeroPowerDoors", body, StringComparison.Ordinal);
        Assert.DoesNotContain(".Decide(", src, StringComparison.Ordinal);
        Assert.Contains("EarconPlayer.Warning2Beep();", body, StringComparison.Ordinal);
        Assert.Contains("VerbosityLevel.Critical", body, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
