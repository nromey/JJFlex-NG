using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #863, junk box repair round 4 (Codex's third re-check B2; ruled by Noel
/// 2026-11-04 22:24 and 22:26). EVERY falling transmit cue waits for the
/// radio's own receive report: the three notes on every route (push to talk's
/// own release, a send's own end, a takeover, a VOX send) AND the ordinary
/// falling chirp on the operator's own slice. A falling cue is never played
/// while the radio reports transmit, and a report that never comes plays
/// nothing (no timed tone).
/// </summary>
/// <remarks>
/// <b>Windowless.</b> Every test asserts
/// <see cref="NoiseReductionPerChannelTests.AssertWindowless"/>, that the run
/// is silenced and that settings are isolated. The controller is real, with
/// speech off; the radio is a vendored <see cref="Radio"/> whose interlock
/// reports travel through a fake transport (<see cref="ReportingRadio"/>), so
/// the tone waits on the same record production reads. Tones are observed at
/// their submission to the earcon player (the controller's
/// <c>FallingTonesSubmitted</c>, written where the earcon is called); with the
/// run silenced nothing renders.
/// </remarks>
public sealed class FallingToneRepairFourTests : IDisposable
{
    private readonly ReportingRadio _r = new();
    private readonly PttSafetyController _controller;

    public FallingToneRepairFourTests()
    {
        _controller = new PttSafetyController(() => _r.Rig, () => true,
            new PttConfig { ChirpEnabled = true, SpeechEnabled = false })
        {
            FallingTonesSubmitted = new(),
        };
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

    private string[] Submitted()
    {
        var submitted = _controller.FallingTonesSubmitted!;
        lock (submitted) return submitted.ToArray();
    }

    private static bool Within(int ms, Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(2);
        }
        return condition();
    }

    /// <summary>Push to talk keyed on the operator's own slice, the radio reporting the carrier.</summary>
    private void KeyedOnOwnSlice()
    {
        _r.Interlock(InterlockState.Ready);
        _r.MoxCache(false);
        _controller.PttDown();
        Assert.True(_controller.IsTransmitting);
        _r.Interlock(InterlockState.Transmitting);
        _r.MoxCache(true);
    }

    [Fact]
    public void The_ordinary_falling_chirp_waits_for_the_radios_receive_report_and_plays_once()
    {
        AssertTheDeskIsSafe();
        KeyedOnOwnSlice();
        _controller.PttUp();                                  // our release only asks the radio to unkey
        Assert.True(_controller.ChirpStopOwed);
        _r.MoxCache(false);                                   // FlexLib's Mox falls at our request
        _r.Interlock(InterlockState.UnkeyRequested);
        Thread.Sleep(300);                                    // past the fallback: nothing may play on a timer
        Assert.Empty(Submitted());

        _r.Interlock(InterlockState.Ready);                   // the radio's own word: receive
        Assert.True(Within(2000, () => Submitted().Length == 1), "the falling chirp never played at the receive report");
        Assert.Equal(new[] { nameof(EarconPlayer.TxStopTone) }, Submitted());
        Assert.Equal(1, _controller.ChirpStopTonesPlayed);

        _r.Interlock(InterlockState.Receive);                 // a repeated receive adds nothing
        Thread.Sleep(100);
        Assert.Single(Submitted());
    }

    [Fact]
    public void A_falling_tone_follows_the_receive_report_with_no_delay_of_our_own()
    {
        // The wait is woken by the report itself. Repair round 5 (Codex's T1):
        // with the ordinary 250 ms fallback, a waiter checking at 250 and 500
        // ms could meet a 200 ms bound after a report at 320 ms on the
        // fallback alone. So this rig's fallback is ten seconds: a tone inside
        // 200 ms of the report can only have been woken by it.
        // On a real connection FlexLib parses the line first and the app's
        // record is written just after; here FlexLib's cache moves first and
        // the line arrives later, so only the record's own notice can wake it.
        AssertTheDeskIsSafe();
        _r.ReceiveFallbackMs(10_000);
        KeyedOnOwnSlice();
        _controller.PttUp();
        _r.MoxCache(false);
        typeof(Radio).GetProperty(nameof(Radio.InterlockState))!.GetSetMethod(nonPublic: true)!
            .Invoke(_r.Radio, new object[] { InterlockState.Ready });
        Thread.Sleep(320);                                    // let the wait settle into its fallback sleep
        Assert.Empty(Submitted());                            // FlexLib's cache alone is not the radio's report
        var sw = Stopwatch.StartNew();
        _r.Transport.Deliver("S0|interlock state=READY");
        Assert.True(Within(2000, () => Submitted().Length == 1));
        Assert.True(sw.ElapsedMilliseconds < 200, "the tone came " + sw.ElapsedMilliseconds + " ms after the report: it waited on a timer");
    }

    [Fact]
    public void A_hand_mic_still_holding_the_carrier_at_release_keeps_the_chirp_owed_until_the_radio_receives()
    {
        AssertTheDeskIsSafe();
        KeyedOnOwnSlice();
        _controller.PttUp();
        _r.MoxCache(false);                                   // our software key is withdrawn...
        _r.Interlock(InterlockState.Transmitting);            // ...and the hand mic still keys the radio
        Thread.Sleep(300);
        Assert.Empty(Submitted());
        Assert.True(_controller.ChirpStopOwed);
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 1));
    }

    [Fact]
    public void Our_own_release_on_another_slice_owes_the_three_notes_to_the_receive_report()
    {
        AssertTheDeskIsSafe();
        KeyedOnOwnSlice();
        _controller.TakeOverElsewhereStopTone();              // a push-to-talk takeover of a send elsewhere
        Assert.True(_controller.KeyedElsewhere);
        _controller.PttUp();
        _r.MoxCache(false);
        Thread.Sleep(150);
        Assert.Empty(Submitted());
        Assert.True(_controller.ElsewhereStopOwed);
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 1));
        Assert.Equal(new[] { nameof(EarconPlayer.TxElsewhereStopTone) }, Submitted());
    }

    [Fact]
    public void A_sends_own_end_owes_the_three_notes_to_the_receive_report_through_the_controller()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Transmitting);            // the send's carrier, still reported
        _r.MoxCache(false);
        _controller.OweElsewhereStopTone();                   // what Settle hands over at the file's end
        Thread.Sleep(150);
        Assert.Empty(Submitted());
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 1));
        Assert.Equal(new[] { nameof(EarconPlayer.TxElsewhereStopTone) }, Submitted());
    }

    [Fact]
    public void A_report_the_app_does_not_recognise_is_not_receive()
    {
        AssertTheDeskIsSafe();
        KeyedOnOwnSlice();
        _controller.PttUp();
        _r.MoxCache(false);
        _r.Interlock(InterlockState.Ready);                   // FlexLib's cache says Ready...
        Assert.True(Within(2000, () => Submitted().Length == 1));   // control: a real READY plays

        KeyedOnOwnSlice();
        _controller.PttUp();
        _r.MoxCache(false);
        typeof(Radio).GetProperty(nameof(Radio.InterlockState))!.GetSetMethod(nonPublic: true)!
            .Invoke(_r.Radio, new object[] { InterlockState.Ready });
        _r.InterlockUnrecognised("SOMETHING_NEW");            // ...but the radio's own last word is unknown
        Thread.Sleep(300);
        Assert.Single(Submitted());
        _r.Interlock(InterlockState.Receive);
        Assert.True(Within(2000, () => Submitted().Length == 2));
    }

    [Fact]
    public void No_receive_report_before_the_connection_ends_plays_nothing()
    {
        // The no-report design: silence plus a trace line, never a timed tone.
        AssertTheDeskIsSafe();
        KeyedOnOwnSlice();
        _controller.PttUp();
        _r.MoxCache(false);
        Assert.True(_controller.ChirpStopOwed);
        _r.Transport.Fall();                                  // the link ends with the radio still reporting transmit
        Assert.True(Within(2000, () => !_controller.ChirpStopOwed), "the debt outlived its connection");
        Thread.Sleep(100);
        Assert.Empty(Submitted());
    }

    [Fact]
    public void The_drop_ends_a_falling_tone_an_idle_controller_still_owes()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Transmitting);
        _r.MoxCache(false);
        _controller.OweElsewhereStopTone();
        Assert.True(_controller.ElsewhereStopOwed);
        _controller.EndEdgeTonesWithConnection();             // what the drop and the teardown now call, idle or not
        Assert.False(_controller.ElsewhereStopOwed);
        _r.Interlock(InterlockState.Ready);
        Thread.Sleep(150);
        Assert.Empty(Submitted());

        string main = Source("JJFlexWpf/MainWindow.xaml.cs");
        int body = main.IndexOf("private void ReleaseKeyForDroppedConnection(bool radioWasKeyed, bool deliberate)", StringComparison.Ordinal);
        int end = main.IndexOf("_pttController?.EndEdgeTonesWithConnection();", body, StringComparison.Ordinal);
        int transmitting = main.IndexOf("if (_pttController != null && _pttController.IsTransmitting)", body, StringComparison.Ordinal);
        Assert.True(body > 0 && end > body && end < transmitting, "an idle controller's owed tone is not ended at the drop");
    }

    [Fact]
    public void A_key_of_the_controllers_own_voids_an_owed_chirp_of_the_last_one()
    {
        AssertTheDeskIsSafe();
        KeyedOnOwnSlice();
        _controller.PttUp();
        _r.MoxCache(false);
        Assert.True(_controller.ChirpStopOwed);
        KeyedOnOwnSlice();                                    // keyed again before the radio ever said receive
        Assert.False(_controller.ChirpStopOwed);
        _controller.PttUp();
        _r.MoxCache(false);
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 1));
        Thread.Sleep(100);
        Assert.Single(Submitted());                           // one falling chirp, for the last release only
    }

    private static string Source(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }
}
