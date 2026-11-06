using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #863, junk box repair round 5 (Codex's fourth re-check, N1 and N2). Every
/// falling transmit cue waits for the radio's own receive report (ruled
/// 2026-11-04). Round four scoped that report to its connection; it now also
/// belongs to the TRANSMISSION being ended (N1): a READY from before the key
/// does not confirm the release. And the tone's last check and its submission
/// are one step against a new key (N2): a key-down either voids an owed tone
/// before it is paid, or waits for it to be handed to the player.
/// </summary>
/// <remarks>
/// <b>Windowless.</b> Every test asserts
/// <see cref="NoiseReductionPerChannelTests.AssertWindowless"/>, that the run is
/// silenced and that settings are isolated. The controller is real, with speech
/// off; the radio is a vendored <see cref="Radio"/> on a fake transport that
/// writes nowhere (<see cref="ReportingRadio"/>). FlexLib's Mox is driven
/// through the vendor's setter and the app's production bridge
/// (<see cref="ReportingRadio.VendorMox"/>). Tones are observed where the
/// earcon is called; with the run silenced nothing renders.
/// </remarks>
public sealed class FallingToneRepairFiveTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly ReportingRadio _r = new();
    private readonly PttSafetyController _controller;

    public FallingToneRepairFiveTests()
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

    /// <summary>The controller's owed flag, read WITHOUT its lock, so a test holding a lock the payment needs can still watch it.</summary>
    private bool OwedUnlocked() => (bool)typeof(PttSafetyController).GetField("_edgeStopOwed", Any)!.GetValue(_controller)!;

    /// <summary>Whether the controller has commanded RF on (its <c>SetTx</c>), read without any lock.</summary>
    private bool KeyCommanded() => (bool)typeof(PttSafetyController).GetField("_rfCommandedOn", Any)!.GetValue(_controller)!;

    private object EdgeToneLock() => typeof(PttSafetyController).GetField("_edgeToneLock", Any)!.GetValue(_controller)!;

    /// <summary>A short press: the key and the unkey, both before any report from the radio about them.</summary>
    private void ShortPressWithNoReport()
    {
        _controller.PttDown();
        _r.VendorMox(true);
        _controller.PttUp();
        _r.VendorMox(false);
    }

    // ── N1: a receive report belongs to the transmission it confirms ──

    [Fact]
    public void A_READY_from_before_the_key_does_not_play_the_falling_chirp_for_that_key()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);                   // reported once, before the key
        ShortPressWithNoReport();
        Thread.Sleep(400);                                    // past the fallback
        Assert.Empty(Submitted());
        Assert.True(_controller.ChirpStopOwed);

        _r.Interlock(InterlockState.Ready);                   // the radio's word AFTER the release
        Assert.True(Within(2000, () => Submitted().Length == 1), "a receive report after the release did not pay the chirp");
        Assert.Equal(new[] { nameof(EarconPlayer.TxStopTone) }, Submitted());
    }

    [Fact]
    public void A_receive_left_from_the_last_transmission_does_not_play_the_next_short_press()
    {
        // The episode, not only the report: the last transmission's own
        // transmit report and its READY both came, and then a short press
        // whose reports have not. The old pair is before this key.
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _controller.PttDown();
        _r.VendorMox(true);
        _r.Interlock(InterlockState.Transmitting);
        _controller.PttUp();
        _r.VendorMox(false);
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 1), "control: the first press's chirp");

        ShortPressWithNoReport();
        Thread.Sleep(400);
        Assert.Single(Submitted());
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 2));
    }

    [Fact]
    public void A_short_press_whose_reports_come_late_plays_its_chirp_at_the_late_receive()
    {
        // SmartLink: the key and the unkey both leave before the radio's first
        // word about them. Its transmit report comes after the release, and
        // then its receive, which is what the chirp waits for.
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        ShortPressWithNoReport();
        _r.Interlock(InterlockState.Transmitting);            // late
        Thread.Sleep(300);
        Assert.Empty(Submitted());
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 1));
        Thread.Sleep(100);
        Assert.Single(Submitted());
    }

    [Fact]
    public void A_sends_three_notes_are_not_paid_by_a_READY_from_before_its_key()
    {
        // The other family: a send keyed (the setter takes the episode), its
        // carrier came and went with no report, and it owes its three notes.
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _r.Rig.Transmit = true;                                // the send's key, as its adapter asks
        _r.VendorMox(true);
        _r.VendorMox(false);
        _controller.OweElsewhereStopTone();
        Thread.Sleep(400);
        Assert.Empty(Submitted());
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 1));
        Assert.Equal(new[] { nameof(EarconPlayer.TxElsewhereStopTone) }, Submitted());
    }

    [Fact]
    public void A_hand_off_after_the_carriers_own_reports_came_and_went_still_plays_at_once()
    {
        // Codex's hand-off case, kept: the carrier's transmit report, then its
        // receive, both before the hand-off. That receive belongs to it.
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _r.Rig.Transmit = true;
        _r.VendorMox(true);
        _r.Interlock(InterlockState.Transmitting);
        _r.VendorMox(false);
        _r.Interlock(InterlockState.Ready);
        _controller.TakeOverElsewhereStopTone();
        Assert.Single(Submitted());                            // at once, on this thread

        // And its negative: the same hand-off with no report of that carrier
        // at all waits for one.
        _r.Rig.Transmit = true;
        _r.VendorMox(true);
        _r.VendorMox(false);
        _controller.TakeOverElsewhereStopTone();
        Thread.Sleep(300);
        Assert.Single(Submitted());
        Assert.True(_controller.ElsewhereStopOwed);
    }

    // ── N2: the last check and the submission are one step against a new key ──

    [Fact]
    public void A_new_key_waits_for_a_falling_tone_already_being_paid_and_never_lands_before_it()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _controller.PttDown();
        _r.VendorMox(true);
        _r.Interlock(InterlockState.Transmitting);
        _controller.PttUp();
        _r.VendorMox(false);
        Assert.True(_controller.ChirpStopOwed);

        var list = _controller.FallingTonesSubmitted!;
        int submittedWhenTheKeyLanded = -1;
        bool keyLandedWhilePaymentWasPaused;
        Thread key;
        bool keyAskedForWhilePaused;
        Monitor.Enter(list);                                  // pauses the payment at its submission
        try
        {
            _r.Interlock(InterlockState.Ready);               // the worker wakes and starts to pay
            Assert.True(Within(2000, () => !OwedUnlocked()), "the payment never began");
            key = new Thread(() =>
            {
                _controller.PttDown();                         // a quick new press, in the payment's gap
                submittedWhenTheKeyLanded = list.Count;
            }) { IsBackground = true };
            key.Start();
            keyLandedWhilePaymentWasPaused = key.Join(300);
            keyAskedForWhilePaused = KeyCommanded();
        }
        finally { Monitor.Exit(list); }
        Assert.True(key.Join(3000), "the new key never completed");

        Assert.False(keyLandedWhilePaymentWasPaused, "the new key went ahead of an owed falling tone already being paid");
        Assert.False(keyAskedForWhilePaused, "the new key was asked of the radio before the owed tone was handed over");
        Assert.Equal(1, submittedWhenTheKeyLanded);           // the falling tone was handed over first, then the key
        Assert.Single(Submitted());
    }

    // ── T2 (repair round 6): the world changes after the worker has
    //    ACKNOWLEDGED it reached the payment's final check, and before it ──

    /// <summary>A released push-to-talk carrier whose falling chirp is owed, its receive report not yet delivered.</summary>
    private void ChirpOwedAfterARelease()
    {
        _r.Interlock(InterlockState.Ready);
        _controller.PttDown();
        _r.VendorMox(true);
        _r.Interlock(InterlockState.Transmitting);
        _controller.PttUp();
        _r.VendorMox(false);
        Assert.True(_controller.ChirpStopOwed);
    }

    /// <summary>
    /// Deliver the radio's READY with the transport's dispatch gate held, so
    /// the payment, which takes that gate for its final check, waits there;
    /// return once the worker has signalled, from inside the payment, that it
    /// is about to take it. <paramref name="change"/> then runs, on this
    /// thread, AFTER the worker's last observation and BEFORE its final check.
    /// </summary>
    private void BetweenTheObservationAndTheFinalCheck(Action change)
    {
        using var entered = new ManualResetEventSlim();
        var hook = typeof(FlexBase).GetField("BeforeFallingCueFinalCheckForTests", Any);
        Assert.True(hook != null, "the payment's entry barrier is not where this test reaches it; the schedule below would be vacuous");
        hook!.SetValue(_r.Rig, (Action)(() => entered.Set()));
        object dispatch = _r.Transport.ConnectionSync;
        Monitor.Enter(dispatch);
        try
        {
            _r.Interlock(InterlockState.Ready);               // the wait sees receive and calls the payment
            Assert.True(entered.Wait(3000), "the worker never reached the payment's final check");
            change();
        }
        finally { Monitor.Exit(dispatch); }
        // Completion acknowledged (repair round 7, Codex's F1): the payment has
        // run its final check and settled the debt, paid or not, before any
        // assertion reads what it submitted.
        Assert.True(Within(3000, () => !OwedUnlocked()), "the payment never completed after the change");
    }

    [Fact]
    public void A_carrier_rising_between_the_receive_report_and_the_final_check_voids_the_tone()
    {
        AssertTheDeskIsSafe();
        ChirpOwedAfterARelease();
        BetweenTheObservationAndTheFinalCheck(() =>
        {
            _r.VendorMox(true);                                // a carrier rises that the controller did not key
            _r.Interlock(InterlockState.Transmitting);
        });
        Assert.Empty(Submitted());
    }

    [Fact]
    public void A_connection_going_between_the_receive_report_and_the_final_check_voids_the_tone()
    {
        AssertTheDeskIsSafe();
        ChirpOwedAfterARelease();
        BetweenTheObservationAndTheFinalCheck(() => _r.Transport.Fall());
        Assert.Empty(Submitted());
    }

    [Fact]
    public void The_same_pause_with_nothing_changed_still_pays_the_tone()
    {
        // The control for the two above: the barrier is reached and the
        // payment then goes ahead, so their silence is the final check's.
        AssertTheDeskIsSafe();
        ChirpOwedAfterARelease();
        BetweenTheObservationAndTheFinalCheck(() => { });
        Assert.Single(Submitted());
    }

    // ── S1 (Codex's fifth re-check): a send's key or a reported carrier
    //    AFTER the final check, while the tone is being handed over ──

    /// <summary>
    /// The vendor's parser, as it runs inside the transport's dispatch for a
    /// hardware or VOX carrier: its interlock and Mox caches, and the app's
    /// production bridge, after the app's own record (the watch, subscribed
    /// first). The fixture's transport has no vendor parser on it.
    /// </summary>
    private void ParseCarrierReportsInsideTheDispatch()
    {
        _r.Transport.DataReceived += report =>
        {
            if (report.Text?.Contains("source=HW") != true) return;
            typeof(Radio).GetProperty(nameof(Radio.InterlockState))!.GetSetMethod(nonPublic: true)!
                .Invoke(_r.Radio, new object[] { InterlockState.Transmitting });
            typeof(Radio).GetField("_mox", Any)!.SetValue(_r.Radio, true);
            typeof(FlexBase).GetMethod("noteRadioMox", Any)!.Invoke(_r.Rig, new object[] { true });
        };
    }

    /// <summary>
    /// Pause the payment at its submission (the test-only list, inside the
    /// payment), run <paramref name="producer"/> on another thread, and say
    /// whether it finished while the payment was paused and how many tones had
    /// been handed over when it did.
    /// </summary>
    private (bool FinishedWhilePaused, int SubmittedWhenItFinished) ProducerDuringTheSubmission(Action producer)
    {
        var list = _controller.FallingTonesSubmitted!;
        int submittedWhenItFinished = -1;
        bool finishedWhilePaused;
        Thread worker;
        Monitor.Enter(list);
        try
        {
            _r.Interlock(InterlockState.Ready);
            Assert.True(Within(2000, () => !OwedUnlocked()), "the payment never began");
            using var entered = new ManualResetEventSlim();
            worker = new Thread(() => { entered.Set(); producer(); submittedWhenItFinished = list.Count; }) { IsBackground = true };
            worker.Start();
            // Producer entry and its blocked state acknowledged (repair round 7,
            // Codex's F1): the producer really ran during the pause, and is
            // either waiting at the boundary or has finished, never unscheduled.
            Assert.True(entered.Wait(2000), "the producer never started");
            Assert.True(Within(2000, () => !worker.IsAlive || (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0),
                "the producer neither blocked nor finished");
            finishedWhilePaused = worker.Join(300);
        }
        finally { Monitor.Exit(list); }
        Assert.True(worker.Join(3000), "the producer never completed");
        return (finishedWhilePaused, submittedWhenItFinished);
    }

    [Fact]
    public void A_new_sends_key_after_the_final_check_never_lands_before_the_old_falling_tone()
    {
        AssertTheDeskIsSafe();
        ChirpOwedAfterARelease();
        var send = new FlexBaseVoiceMessageRig(_r.Rig);       // the ordinary send's own adapter
        var (finishedWhilePaused, submittedWhenItFinished) = ProducerDuringTheSubmission(() =>
        {
            send.Transmit = true;                              // the send asks for its key
            _r.VendorMox(true);                                // and it goes up
        });
        Assert.False(finishedWhilePaused, "a send keyed while an old falling tone was being handed over");
        Assert.Equal(1, submittedWhenItFinished);
        Assert.Single(Submitted());
    }

    [Fact]
    public void A_hardware_carrier_reported_after_the_final_check_never_lands_before_the_old_falling_tone()
    {
        AssertTheDeskIsSafe();
        ParseCarrierReportsInsideTheDispatch();
        ChirpOwedAfterARelease();
        var (finishedWhilePaused, submittedWhenItFinished) = ProducerDuringTheSubmission(() =>
            _r.Transport.Deliver("S0|interlock state=TRANSMITTING source=HW tx_client_handle=0x00000000"));
        Assert.False(finishedWhilePaused, "a reported carrier landed while an old falling tone was being handed over");
        Assert.Equal(1, submittedWhenItFinished);
        Assert.Single(Submitted());
    }

    [Fact]
    public void A_new_send_keyed_and_unkeyed_before_the_old_tone_is_paid_voids_it()
    {
        // Codex's variant: the old wait's marks never met the newer episode,
        // so once the optimistic cache fell again, the first carrier's
        // receive paid it, while the new send's carrier may still be up.
        AssertTheDeskIsSafe();
        ChirpOwedAfterARelease();
        var send = new FlexBaseVoiceMessageRig(_r.Rig);
        send.Transmit = true;
        _r.VendorMox(true);
        send.Transmit = false;
        _r.VendorMox(false);                                   // its reports have not come
        _r.Interlock(InterlockState.Ready);
        Thread.Sleep(400);
        Assert.Empty(Submitted());
        Assert.True(Within(2000, () => !_controller.ChirpStopOwed), "the voided debt was left waiting instead of given up");
    }

    // ── S2 (Codex's sixth re-check): a debt carries its transmission's origin ──

    /// <summary>A box-off VOX send's carrier, reported by the radio: its rise (with our Mox bridge), then its transmit report, then receive.</summary>
    private void VoxCarrierCameAndWent()
    {
        _r.Rig.TxTune = true;                               // an earlier transmission asked on this rig, so
        _r.Rig.TxTune = false;                              // the count is not zero when the carrier rises
        _r.Interlock(InterlockState.Ready);
        _controller.TonesOnVoxEdges();                      // the send's tones are armed for the radio's edges
        _r.VendorMox(true);                                 // the radio reports the rise, on its reader
        _r.Interlock(InterlockState.Transmitting);
        _r.VendorMox(false);
        _r.Interlock(InterlockState.Ready);                 // and its receive, before the UI hears of any of it
    }

    [Fact]
    public void A_deferred_VOX_notification_handled_after_a_tune_request_owes_no_falling_tone()
    {
        AssertTheDeskIsSafe();
        VoxCarrierCameAndWent();
        _r.Rig.TxTune = true;                               // the UI is busy asking for tune meanwhile
        _controller.NoteRadioTransmit(true);                // only now does the deferred "keyed" arrive
        Assert.Equal(1, _controller.ElsewhereStartTonesPlayed);
        Assert.True(Within(2000, () => !_controller.ElsewhereStopOwed), "the old debt was left waiting");
        Thread.Sleep(200);
        Assert.Empty(Submitted());                          // the old carrier's READY does not pay it
    }

    [Fact]
    public void The_same_deferred_VOX_notification_with_nothing_asked_since_pays_on_its_carriers_receive()
    {
        // The control: the debt is the old carrier's own, and its receive
        // report came after its transmit report, so it pays at once.
        AssertTheDeskIsSafe();
        VoxCarrierCameAndWent();
        _controller.NoteRadioTransmit(true);
        Assert.True(Within(2000, () => Submitted().Length == 1));
        Assert.Equal(new[] { nameof(EarconPlayer.TxElsewhereStopTone) }, Submitted());
    }

    // ── Follow-up: a superseded wait gives itself up (Codex's standing control) ──

    private int RadioSubscribers()
    {
        var field = typeof(Radio).BaseType!.GetField("PropertyChanged", Any);   // the vendor's ObservableObject
        Assert.True(field != null, "ObservableObject's PropertyChanged backing field moved; the count below would be vacuous");
        return ((PropertyChangedEventHandler?)field!.GetValue(_r.Radio))?.GetInvocationList().Length ?? 0;
    }

    [Fact]
    public void A_hundred_replaced_debts_under_a_held_carrier_leave_one_wait_and_none_once_ended()
    {
        AssertTheDeskIsSafe();
        int baseline = RadioSubscribers();
        _r.Interlock(InterlockState.Transmitting);
        _r.MoxCache(false);
        for (int i = 0; i < 100; i++) _controller.OweElsewhereStopTone();
        Assert.True(Within(3000, () => RadioSubscribers() == baseline + 1),
            "replaced waits did not give themselves up: " + (RadioSubscribers() - baseline) + " still waiting");
        _controller.EndEdgeTonesWithConnection();
        Assert.True(Within(3000, () => RadioSubscribers() == baseline), "the last wait outlived its debt");
        _r.Interlock(InterlockState.Ready);
        Thread.Sleep(150);
        Assert.Empty(Submitted());
    }
}
