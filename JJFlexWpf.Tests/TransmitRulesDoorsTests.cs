using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The transmit rules at the operator's doors (#1310): push to talk and the
/// lock refuse exactly when the TX band lockout's check says no, as they always
/// have, and now speak the REASON's sentence: the approved
/// <c>audio.ptt.blocked_by_license</c> for a frequency, a sentence of its own
/// for a mode. An unkey is never refused.
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread and an unconnected <see cref="FlexBase"/>
/// whose writes go nowhere, as <see cref="CallsignDoorsTests"/> does, in the
/// same collection because both set ZeroPowerDoors' process-wide overrides.
/// The reason is stood in for by the radio's own judge delegate
/// (<see cref="FlexBase.OpenParms.JudgeTransmitHere"/>), which is how the doors
/// ask it in the app.
/// </remarks>
[Collection("TuneDoors")]
public sealed class TransmitRulesDoorsTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    private TransmitVerdict _verdict = TransmitVerdict.Allowed;
    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly VerbosityLevel _verbosityWas;
    private readonly bool _callsignCheckWas = FlexBase.CallsignCheckOn;

    public TransmitRulesDoorsTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        FlexBase.CallsignCheckOn = false;   // only the transmit rules decide here
        _verbosityWas = ScreenReaderOutput.CurrentVerbosity;
        ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        ZeroPowerDoors.Forget();
        ZeroPowerDoors.AtZeroOverride = (rig, tune) => false;   // power is fine
        _rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests", JudgeTransmitHere = _ => _verdict });
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        // The check as MainWindow wires it: the judge's yes or no.
        _controller.CanTransmitHereCheck = () => _verdict.IsAllowed;
    }

    public void Dispose()
    {
        FlexBase.CallsignCheckOn = _callsignCheckWas;
        ZeroPowerDoors.AtZeroOverride = null;
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

    private static string Frequency => Lexicon.Get("audio.ptt.blocked_by_license");
    private static string Mode => Lexicon.Get("audio.ptt.blocked_by_license_mode");

    [Fact]
    public void A_frequency_refusal_at_push_to_talk_keeps_the_approved_sentence_and_keys_nothing()
    {
        _verdict = TransmitVerdict.LockoutFrequency;
        Assert.Equal(new[] { Frequency }, Said(_controller.PttDown));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void A_mode_refusal_at_push_to_talk_says_the_mode_and_keys_nothing()
    {
        _verdict = new TransmitVerdict(LockoutReason.Mode);
        Assert.Equal(new[] { Mode }, Said(_controller.PttDown));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void The_lock_says_the_reason_too()
    {
        _verdict = new TransmitVerdict(LockoutReason.Mode);
        Assert.Equal(new[] { Mode }, Said(_controller.ToggleLock));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void A_no_from_a_check_whose_reason_cannot_be_read_is_the_frequency_refusal_as_before()
    {
        // A yes-or-no check with no radio judge behind it: what every door said until #1310.
        var bare = new PttSafetyController(() => null, () => true, new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
        try
        {
            bare.CanTransmitHereCheck = () => false;
            Assert.Equal(TransmitVerdict.LockoutFrequency, bare.JudgeTransmitHere());
            Assert.Equal(Frequency, bare.TransmitRulesRefusal());
        }
        finally { bare.Dispose(); }
    }

    [Fact]
    public void Allowed_keys_and_no_check_wired_keys()
    {
        _controller.PttDown();
        Assert.Equal(PttSafetyController.PttState.PttHold, _controller.State);
        _controller.PttUp();
        _controller.CanTransmitHereCheck = null;
        _verdict = new TransmitVerdict(LockoutReason.Mode);   // the radio would say no, but nothing asks it
        Assert.Null(_controller.TransmitRulesRefusal());
    }

    [Fact]
    public void An_unkey_a_release_and_Escape_are_never_refused()
    {
        _controller.ToggleLock();
        Assert.Equal(PttSafetyController.PttState.Locked, _controller.State);
        _verdict = TransmitVerdict.LockoutFrequency;   // the frequency moved out while keyed
        Assert.DoesNotContain(Frequency, Said(_controller.ToggleLock));
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        _verdict = TransmitVerdict.Allowed;
        _controller.PttDown();
        _verdict = TransmitVerdict.LockoutFrequency;
        _controller.PttUp();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);

        _verdict = TransmitVerdict.Allowed;
        _controller.ToggleLock();
        _verdict = new TransmitVerdict(LockoutReason.Mode);
        _controller.EscapeUnlock();
        Assert.Equal(PttSafetyController.PttState.Idle, _controller.State);
    }

    [Fact]
    public void Every_voice_send_door_and_recorder_playback_ask_the_same_check()
    {
        string sender = Source("VoiceMessageSender.cs");
        Assert.Equal(3, Occurrences(sender, "ptt?.TransmitRulesRefusal() is { } rulesRefusal"));
        Assert.DoesNotContain("audio.ptt.blocked_by_license", sender, StringComparison.Ordinal);
        Assert.DoesNotContain("CanTransmitHereCheck", sender, StringComparison.Ordinal);

        string ptt = Source("PttSafetyController.cs");
        Assert.Contains("(CanTransmitHereCheck?.Invoke() ?? true);", ptt, StringComparison.Ordinal);   // recorder playback, unchanged
        Assert.Equal(2, Occurrences(ptt, "if (TransmitRulesRefusal() is { } rulesRefusal)"));
        // No door names the frequency sentence's key itself any more (built, so
        // the lexicon's key scan does not read this line as a key).
        Assert.DoesNotContain("\"" + string.Join(".", "audio", "ptt", "blocked_by_license") + "\"", ptt, StringComparison.Ordinal);
    }

    private static string Source(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "JJFlexWpf", file)).Replace("\r\n", "\n");
    }

    private static int Occurrences(string text, string what)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
        return n;
    }
}
