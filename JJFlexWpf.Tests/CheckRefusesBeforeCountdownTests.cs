using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Check your audio refuses BEFORE its countdown, with the reason, when it
/// would key with no callsign (#335, J1) or while another station holds the
/// transmitter (#910), and says nothing more behind the reason (#1023 item 2).
/// </summary>
/// <remarks>
/// <para><b>Desk safety.</b> No window: the dialog's start path is read from
/// source, and the controller's refusal runs on an unconnected
/// <see cref="FlexBase"/> on an MTA thread (asserted first), with its
/// "another station?" question replaced, as
/// <see cref="AnotherStationTransmittingTests"/> does. Nothing keys.</para>
/// <para>The realized check (a raw second client keyed into the dummy load,
/// then Start: one sentence, no countdown) is the harness's.</para>
/// </remarks>
public sealed class CheckRefusesBeforeCountdownTests : IDisposable
{
    private readonly FlexBase _rig;
    private readonly PttSafetyController _controller;
    private readonly VerbosityLevel _verbosityBefore;
    private readonly List<string> _said = new();
    private bool _another;

    public CheckRefusesBeforeCountdownTests()
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
        _controller.AnotherStationRefusalSaid = _said.Add;
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

    // ---- the controller's refusal, under the check's own door names ----

    [Fact]
    public void The_check_doors_refuse_once_each_and_key_nothing()
    {
        Guard();
        _another = true;
        Assert.True(_controller.RefuseWhileAnotherStationTransmits("Check your audio's start"));
        Assert.True(_controller.RefuseWhileAnotherStationTransmits("Check your audio's start"));   // a repeat: refused, not said
        Assert.True(_controller.RefuseWhileAnotherStationTransmits("Check your audio, after its count"));
        Assert.Equal(new[] { "Check your audio's start", "Check your audio, after its count" }, _said);
        Assert.False(_controller.IsTransmitting);
    }

    [Fact]
    public void With_nobody_else_transmitting_nothing_is_refused()
    {
        Guard();
        _another = false;
        Assert.False(_controller.RefuseWhileAnotherStationTransmits("Check your audio's start"));
        Assert.Empty(_said);
    }

    // ---- where the dialog asks ----

    [Fact]
    public void Both_refusals_come_before_any_question_and_before_the_countdown()
    {
        Guard();
        string body = Method(Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"), "public void ToggleAudioCheck(");
        int computerMic = body.IndexOf("ToggleComputerMicCheck();", StringComparison.Ordinal);
        int transmitting = body.IndexOf("if (ptt.IsTransmitting)", StringComparison.Ordinal);
        int callsign = body.IndexOf("if (ZeroPowerDoors.RefuseNoCallsign(_rig, \"Check your audio's start\")) return;", StringComparison.Ordinal);
        int another = body.IndexOf("if (ptt.RefuseWhileAnotherStationTransmits(\"Check your audio's start\")) return;", StringComparison.Ordinal);
        Assert.True(computerMic > 0 && transmitting > computerMic, "the computer microphone leaves first and keys nothing");
        Assert.True(callsign > transmitting && another > callsign, "after the already-transmitting check, callsign first");
        foreach (string later in new[] { "TuneDoors.Up(_rig)", "NeedsOverwriteQuestion(", "AdmitTakeBeforeCountdown(",
                                         "StartOffAirAsync(", "StartLoopbackCheckAsync(", "StartAudioCheckSessionAsync(" })
            Assert.True(body.IndexOf(later, StringComparison.Ordinal) > another, later + " comes after both refusals");
        // Not the operator-press door: checks are exempt from the 0 W refusal by ruling.
        Assert.DoesNotContain("RefuseTransmitPress", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_computer_microphone_is_never_refused_for_the_radio()
    {
        Guard();
        string src = Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.ComputerMic.cs");
        Assert.DoesNotContain("RefuseNoCallsign", src, StringComparison.Ordinal);
        Assert.DoesNotContain("RefuseWhileAnotherStationTransmits", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_station_is_asked_again_after_the_count_before_the_recorder_arms()
    {
        Guard();
        string body = Method(Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"),
            "public async Task<bool> StartAsync(AudioCheckStartGate.Lease start)");
        int count = body.IndexOf("CountdownLetsKeyAsync()", StringComparison.Ordinal);
        int asked = body.IndexOf("_ptt.RefuseWhileAnotherStationTransmits(\"Check your audio, after its count\")", StringComparison.Ordinal);
        int armed = body.IndexOf("StartSliceRecordAsync(", StringComparison.Ordinal);
        int keyed = body.IndexOf("_ptt.KeyForCheck();", StringComparison.Ordinal);
        Assert.True(count > 0 && asked > count && armed > asked && keyed > asked);
    }

    [Fact]
    public void A_refusal_already_said_is_not_followed_by_could_not_start()
    {
        Guard();
        string body = Method(Source("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"),
            "public async Task<bool> StartAsync(AudioCheckStartGate.Lease start)");
        int branch = body.IndexOf("if (!_ptt.IsTransmitting)", body.IndexOf("_ptt.KeyForCheck();", StringComparison.Ordinal), StringComparison.Ordinal);
        int couldNot = body.IndexOf("\"audio.check.could_not_start\"", branch, StringComparison.Ordinal);
        string before = body.Substring(branch, couldNot - branch);
        Assert.Contains("if (ZeroPowerDoors.NoCallsign(rig)) { }", before, StringComparison.Ordinal);
        Assert.Contains("else if (_ptt.RefuseWhileAnotherStationTransmits(\"Check your audio, at its key\")) { }", before, StringComparison.Ordinal);
        Assert.Matches(@"else\s*ScreenReaderOutput\.Speak\(Lexicon\.Get\($", before);
    }

    private static string Method(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature + " has moved");
        int open = src.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src.Substring(at, i - at + 1);
        }
        throw new Xunit.Sdk.XunitException(signature + " does not close");
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }
}
