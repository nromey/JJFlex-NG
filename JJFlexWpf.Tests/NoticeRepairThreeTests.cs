using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #863, junk box repair round 3 (Codex's second re-check, B1, and Noel's
/// 2026-11-04 ruling on VOX sends). The falling tri-tone owed to a carrier
/// this controller did not key for itself is paid on the radio's REAL
/// receive, read off the radio's interlock, not on FlexLib's Mox falling,
/// which happens the moment our own software key is withdrawn while a hand
/// mic or foot switch still holds the carrier. A box-off voice message
/// released to VOX gets the rising tri-tone on the radio's own key and the
/// falling one on its real receive, through the same owner.
/// </summary>
/// <remarks>
/// <b>Windowless.</b> Every test asserts
/// <see cref="NoiseReductionPerChannelTests.AssertWindowless"/>, that the run
/// is silenced and that settings are isolated. The controller is built with
/// chirps and speech off. The rig is an unconnected <see cref="FlexBase"/>
/// with a vendored <see cref="Radio"/> planted as its radio (no socket, no
/// thread of its own), so the interlock can be set the way the radio's
/// status would set it. Tones are counted by the controller; nothing renders.
/// </remarks>
public sealed class NoticeRepairThreeTests : IDisposable
{
    // Repair round 4: the falling tone needs the radio's own receive REPORT on
    // a live connection, so the radio now has a transport its reports travel
    // through (ReportingRadio), not only FlexLib's cache.
    private readonly ReportingRadio _r = new();
    private readonly PttSafetyController _controller;

    public NoticeRepairThreeTests()
    {
        _controller = new PttSafetyController(() => _r.Rig, () => true,
            new PttConfig { ChirpEnabled = false, SpeechEnabled = false });
    }

    public void Dispose()
    {
        if (_controller.IsTransmitting) _controller.EscapeUnlock();
        _controller.Dispose();
        _r.Dispose();
    }

    private static void AssertTheDeskIsSafe()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    /// <summary>Our cached Transmit, which follows FlexLib's Mox.</summary>
    private void MoxCache(bool on) => _r.MoxCache(on);

    /// <summary>The radio's interlock, as its status line sets it and as the app reads the line.</summary>
    private void Interlock(InterlockState state) => _r.Interlock(state);

    private static bool Within(int ms, Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    [Fact]
    public void A_hardware_takeover_keeps_its_falling_tone_through_Mox_falling_and_plays_it_once_at_real_receive()
    {
        AssertTheDeskIsSafe();
        // A box send on another slice, taken over by a hand mic: our software
        // key is withdrawn, so Mox and our cache fall, while the interlock
        // still reports the hand mic's carrier.
        Interlock(InterlockState.Transmitting);
        MoxCache(false);
        _controller.TakeOverElsewhereStopTone();
        Assert.True(_controller.ElsewhereStopOwed);

        _controller.NoteRadioTransmit(false);           // FlexLib's Mox fell: not receive
        Thread.Sleep(150);
        Assert.Equal(0, _controller.ElsewhereStopTonesPlayed);
        Assert.True(_controller.ElsewhereStopOwed);

        Interlock(InterlockState.Ready);                // the hand mic let go: the radio really receives
        Assert.True(Within(2000, () => _controller.ElsewhereStopTonesPlayed == 1),
            "the falling tone did not play when the radio reported receive");
        Assert.False(_controller.ElsewhereStopOwed);

        _controller.NoteRadioTransmit(false);           // repeated receive adds nothing
        Thread.Sleep(100);
        Assert.Equal(1, _controller.ElsewhereStopTonesPlayed);
    }

    [Fact]
    public void A_hand_off_after_the_carrier_has_already_fallen_plays_at_once()
    {
        AssertTheDeskIsSafe();
        // Codex's second probe observation: a debt handed to an idle controller
        // with the radio already receiving waited for another notification.
        // Repair round 5 (Codex's #863 N1): "already fallen" now means the
        // carrier's own reports came and went, transmit then receive, so the
        // receive belongs to that carrier. A READY with no carrier report at
        // all is not evidence (FallingToneRepairFiveTests).
        Interlock(InterlockState.Ready);
        Interlock(InterlockState.Transmitting);
        Interlock(InterlockState.Ready);
        MoxCache(false);
        _controller.TakeOverElsewhereStopTone();
        Assert.Equal(1, _controller.ElsewhereStopTonesPlayed);
        Assert.False(_controller.ElsewhereStopOwed);
    }

    [Fact]
    public void A_key_of_the_controllers_own_takes_an_owed_falling_tone_over()
    {
        AssertTheDeskIsSafe();
        Interlock(InterlockState.Transmitting);
        MoxCache(false);
        _controller.TakeOverElsewhereStopTone();
        Assert.True(_controller.ElsewhereStopOwed);
        _controller.PttDown();                          // its own key-up tone answers now
        Assert.False(_controller.ElsewhereStopOwed);
        _controller.PttUp();
        Interlock(InterlockState.Ready);
        Thread.Sleep(150);
        Assert.Equal(0, _controller.ElsewhereStopTonesPlayed);
    }

    [Fact]
    public void A_VOX_send_gets_its_rising_tone_when_the_radio_keys_and_its_falling_one_at_real_receive()
    {
        AssertTheDeskIsSafe();
        Interlock(InterlockState.Ready);
        MoxCache(false);
        _controller.TonesOnVoxEdges();                  // the file is released to VOX
        Assert.True(_controller.ElsewhereStartOwed);
        Assert.Equal(0, _controller.ElsewhereStartTonesPlayed);

        Interlock(InterlockState.Transmitting);         // VOX keys the radio
        MoxCache(true);
        _controller.NoteRadioTransmit(true);
        Assert.Equal(1, _controller.ElsewhereStartTonesPlayed);
        Assert.True(_controller.ElsewhereStopOwed);
        Thread.Sleep(100);
        Assert.Equal(0, _controller.ElsewhereStopTonesPlayed);

        Interlock(InterlockState.Ready);                // VOX lets go
        MoxCache(false);
        _controller.NoteRadioTransmit(false);
        Assert.True(Within(2000, () => _controller.ElsewhereStopTonesPlayed == 1));
        _controller.NoteRadioTransmit(true);            // a later key owes nothing more
        Assert.Equal(1, _controller.ElsewhereStartTonesPlayed);
    }

    [Fact]
    public void A_VOX_send_that_never_keyed_owes_no_tone_after_it_ends()
    {
        AssertTheDeskIsSafe();
        Interlock(InterlockState.Ready);
        MoxCache(false);
        _controller.TonesOnVoxEdges();
        _controller.EndVoxEdgeTones();                  // the send settled; VOX never keyed
        Interlock(InterlockState.Transmitting);
        MoxCache(true);
        _controller.NoteRadioTransmit(true);            // someone else's key later
        Assert.Equal(0, _controller.ElsewhereStartTonesPlayed);
        Assert.False(_controller.ElsewhereStopOwed);
    }

    [Fact]
    public void A_VOX_send_released_onto_a_radio_already_keyed_plays_its_rising_tone_at_once()
    {
        AssertTheDeskIsSafe();
        Interlock(InterlockState.Transmitting);
        MoxCache(true);
        _controller.TonesOnVoxEdges();
        Assert.Equal(1, _controller.ElsewhereStartTonesPlayed);
        Assert.True(_controller.ElsewhereStopOwed);
    }

    [Fact]
    public void A_drop_ends_an_owed_falling_tone_without_playing_it()
    {
        AssertTheDeskIsSafe();
        Interlock(InterlockState.Transmitting);
        MoxCache(false);
        _controller.TakeOverElsewhereStopTone();
        _controller.DropWithConnection("the connection fell");
        Assert.False(_controller.ElsewhereStopOwed);
        Interlock(InterlockState.Ready);
        Thread.Sleep(150);
        Assert.Equal(0, _controller.ElsewhereStopTonesPlayed);
    }
}
