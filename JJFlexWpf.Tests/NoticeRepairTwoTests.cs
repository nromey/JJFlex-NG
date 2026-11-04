using System;
using System.IO;
using System.Reflection;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #863, junk box repair round 2 (Codex's re-check, R1 and R3). Windowless:
/// every test asserts <see cref="NoiseReductionPerChannelTests.AssertWindowless"/>
/// and that the run is silenced; the controller is built with chirps and
/// speech off, and the rig is an unconnected <see cref="FlexBase"/>.
/// </summary>
public sealed class NoticeRepairTwoTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
    private readonly PttSafetyController _controller;

    public NoticeRepairTwoTests()
    {
        _controller = new PttSafetyController(() => _rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
    }

    public void Dispose()
    {
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        _rig.Dispose();
    }

    private static void AssertTheDeskIsSafe()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    private void Transmitting(bool on) =>
        typeof(FlexBase).GetField("_Transmit", Any)!.SetValue(_rig, on);

    /// <summary>
    /// R1: with the box off, a send that starts a carrier says where first,
    /// whether it keys the radio itself (VOX off) or releases the file to VOX
    /// (VOX on). The old test asked "keys itself", which VOX made false, so
    /// the VOX send went straight out unannounced.
    /// </summary>
    [Fact]
    public void A_box_off_send_that_starts_a_carrier_says_where_whatever_VOX_is()
    {
        AssertTheDeskIsSafe();
        Transmitting(false);
        Assert.True(VoiceMessageSender.StartsACarrierWithTheBoxOff(_rig));
        Transmitting(true);   // control: joining a carrier already up starts nothing, and does not wait
        Assert.False(VoiceMessageSender.StartsACarrierWithTheBoxOff(_rig));

        // The door asks THAT, and nothing about VOX, before it decides to wait.
        string src = Source("JJFlexWpf/VoiceMessageSender.cs");
        int piece = src.IndexOf("private static void StartVoiceMessagePiece(", StringComparison.Ordinal);
        int end = src.IndexOf("internal static bool StartsACarrierWithTheBoxOff(", piece, StringComparison.Ordinal);
        string body = src.Substring(piece, end - piece);
        int ask = body.IndexOf("if (StartsACarrierWithTheBoxOff(rig))", StringComparison.Ordinal);
        int prep = body.IndexOf("TransmitSliceNotice.Prepare(rig, \"the send of \"", StringComparison.Ordinal);
        Assert.True(ask > 0 && prep > ask, "the box-off door does not prepare the notice for every carrier it starts");
        Assert.DoesNotContain("KeysItselfWithTheBoxOff", src);
        string predicate = src.Substring(end, src.IndexOf('}', src.IndexOf('}', end) + 1) - end);
        Assert.DoesNotContain("Vox", predicate);
    }

    /// <summary>R3: a takeover leaves the carrier up, so the file's end is not the falling tone.</summary>
    [Fact]
    public void A_taken_over_send_owes_its_falling_tone_to_the_real_unkey()
    {
        AssertTheDeskIsSafe();
        Assert.Equal(VoiceMessageSender.ElsewhereStopTone.OwedToTheRealUnkey,
            VoiceMessageSender.StopToneAtTheEnd(VoiceMessageOutcome.TakenOver));
        foreach (var ended in new VoiceMessageOutcome?[]
                 { VoiceMessageOutcome.Sent, VoiceMessageOutcome.Stopped, VoiceMessageOutcome.Interrupted, null })
            Assert.Equal(VoiceMessageSender.ElsewhereStopTone.PlayNow, VoiceMessageSender.StopToneAtTheEnd(ended));
    }

    [Fact]
    public void A_hardware_takeover_plays_the_falling_tone_when_the_radio_reports_receive()
    {
        AssertTheDeskIsSafe();
        Assert.False(_controller.IsTransmitting);
        _controller.NoteRadioTransmit(true);
        _controller.TakeOverElsewhereStopTone();          // a foot switch took the send over
        Assert.True(_controller.ElsewhereStopOwed);
        _controller.NoteRadioTransmit(true);              // still up: nothing yet
        Assert.True(_controller.ElsewhereStopOwed);
        _controller.NoteRadioTransmit(false);             // the real unkey
        Assert.False(_controller.ElsewhereStopOwed);
    }

    [Fact]
    public void A_push_to_talk_takeover_hands_the_falling_tone_to_the_controllers_own_unkey()
    {
        AssertTheDeskIsSafe();
        _controller.PttDown();
        Assert.True(_controller.IsTransmitting);
        _controller.TakeOverElsewhereStopTone();
        Assert.False(_controller.ElsewhereStopOwed);      // not owed to the radio's report
        Assert.True(_controller.KeyedElsewhere);          // its own key-up plays the three notes
        _controller.PttUp();
        Assert.False(_controller.KeyedElsewhere);
    }

    [Fact]
    public void The_send_hands_the_tone_over_rather_than_playing_it_at_a_takeover()
    {
        AssertTheDeskIsSafe();
        string src = Source("JJFlexWpf/VoiceMessageSender.cs");
        int settle = src.IndexOf("private static void Settle()", StringComparison.Ordinal);
        int decide = src.IndexOf("if (StopToneAtTheEnd(send?.Outcome) == ElsewhereStopTone.PlayNow)", settle, StringComparison.Ordinal);
        int hand = src.IndexOf(".TakeOverElsewhereStopTone();", decide, StringComparison.Ordinal);
        Assert.True(settle > 0 && decide > settle && hand > decide);
        // And a controller key-down takes over any owed tone and clears a split on the selected slice.
        string ptt = Source("JJFlexWpf/PttSafetyController.cs");
        Assert.Contains("_elsewhereStopOwed = false;", ptt);
        Assert.Contains("Radios.TransmitSliceNotice.KeyedWithoutNotice(_getRigControl());", ptt);
    }

    private static string Source(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }
}
