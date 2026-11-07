using System;
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
/// #863, junk box repair round 9 (Codex's eighth re-check, S4 and S5; the
/// coordinator's anchor). A falling cue for a transmission WE keyed is paid
/// only on a receive report numbered after our own OFF for it was really
/// written, and only if our ON for it was really written before that. If the
/// ON never went out, nothing was transmitted and no cue is owed. A carrier we
/// keyed keeps the identity of our request, whatever report about it comes
/// later. Carriers we did not key keep the report-anchored rule.
/// </summary>
/// <remarks>
/// <b>Windowless.</b> Every test asserts
/// <see cref="NoiseReductionPerChannelTests.AssertWindowless"/>, a silenced run
/// and isolated settings. The controller is real, with speech off; the radio is
/// a vendored <see cref="Radio"/> on a fake transport that writes nowhere but
/// reports each write's outcome (<see cref="ReportingRadio"/>). The rig's
/// command queue is run by hand, item by item, so a test decides when our ON
/// and OFF are really written. Tones are observed where the earcon is called.
/// </remarks>
public sealed class FallingToneRepairNineTests : IDisposable
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly ReportingRadio _r = new();
    private readonly PttSafetyController _controller;

    public FallingToneRepairNineTests()
    {
        _controller = new PttSafetyController(() => _r.Rig, () => true,
            new PttConfig { ChirpEnabled = true, SpeechEnabled = false })
        {
            FallingTonesSubmitted = new(),
        };
        object q = typeof(FlexBase).GetField("q", Any)!.GetValue(_r.Rig)!;
        q.GetType().GetProperty("MainLoop")!.SetValue(q, true);
        // FlexLib's Mox notification reaches the app's bridge, as the station wiring does.
        _r.Radio.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Mox")
                typeof(FlexBase).GetMethod("noteRadioMox", Any)!.Invoke(_r.Rig, new object[] { _r.Radio.Mox });
        };
    }

    public void Dispose()
    {
        object q = typeof(FlexBase).GetField("q", Any)!.GetValue(_r.Rig)!;
        q.GetType().GetProperty("MainLoop")!.SetValue(q, false);
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

    private int Queued()
    {
        object q = typeof(FlexBase).GetField("q", Any)!.GetValue(_r.Rig)!;
        return (int)q.GetType().GetProperty("Count")!.GetValue(q)!;
    }

    /// <summary>Run the next queued command, as the main loop runs it.</summary>
    private void RunNextQueued()
    {
        Assert.True(Queued() > 0, "nothing was queued to run");
        object q = typeof(FlexBase).GetField("q", Any)!.GetValue(_r.Rig)!;
        object item = q.GetType().GetMethod("Dequeue")!.Invoke(q, null)!;
        typeof(FlexBase).GetMethod("RunQueuedItem", Any)!.Invoke(_r.Rig, new[] { item });
    }

    private FlexBase.OwnKey Key => _r.Rig.LatestOwnKey!;

    private int KeyState() => (int)typeof(FlexBase.OwnKey).GetProperty("State", Any)!.GetValue(Key)!;

    /// <summary>A full press whose ON and OFF are written, with its own transmit report between.</summary>
    private void PressWrittenWithItsTransmitReport()
    {
        _controller.PttDown();
        RunNextQueued();                                     // our ON, written
        _r.Interlock(InterlockState.Transmitting);
        _controller.PttUp();
        RunNextQueued();                                     // our OFF, written
    }

    // ── S5: a receive before our key has even run pays nothing ──

    [Fact]
    public void A_previous_carriers_READY_arriving_while_the_next_press_is_still_queued_pays_nothing()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        PressWrittenWithItsTransmitReport();                 // the first carrier; its READY is in flight
        _controller.PttDown();                               // the second press: ON and OFF both queued
        _controller.PttUp();
        Assert.Equal(2, Queued());

        _r.Interlock(InterlockState.Ready);                  // the FIRST carrier's late READY
        Thread.Sleep(300);
        Assert.Empty(Submitted());                           // round 8 paid the second press here
        Assert.True(_controller.ChirpStopOwed);

        RunNextQueued();                                     // the second ON, written
        RunNextQueued();                                     // and its OFF
        Thread.Sleep(300);
        Assert.Empty(Submitted());                           // no report after our OFF yet

        _r.Interlock(InterlockState.Ready);                  // the radio's word after our OFF
        Assert.True(Within(2000, () => Submitted().Length == 1), "the receive after our written OFF did not pay");
        Assert.Equal(new[] { nameof(EarconPlayer.TxStopTone) }, Submitted());
    }

    [Fact]
    public void A_receive_between_our_written_ON_and_our_written_OFF_pays_nothing()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _controller.PttDown();
        RunNextQueued();                                     // ON written
        _controller.PttUp();                                 // OFF queued, not yet written
        _r.Interlock(InterlockState.Transmitting);
        _r.Interlock(InterlockState.Ready);                  // a receive before our OFF went out
        Thread.Sleep(300);
        Assert.Empty(Submitted());

        RunNextQueued();                                     // OFF written
        Thread.Sleep(200);
        Assert.Empty(Submitted());
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 1));
    }

    [Fact]
    public void A_short_press_whose_reports_both_come_after_its_written_OFF_pays_once_at_the_late_receive()
    {
        // The valid control Codex named: ON and OFF really written before any
        // report; the late TRANSMITTING pays nothing, the late READY pays once.
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _controller.PttDown();
        _controller.PttUp();
        RunNextQueued();
        RunNextQueued();
        Assert.Equal(3, KeyState());                         // OFF WRITTEN
        _r.Interlock(InterlockState.Transmitting);
        Thread.Sleep(300);
        Assert.Empty(Submitted());
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 1));
        Thread.Sleep(100);
        Assert.Single(Submitted());
    }

    // ── The ON never went out: nothing was transmitted, no cue ──

    [Fact]
    public void A_key_revoked_in_the_queue_never_went_out_and_owes_no_falling_cue()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _controller.PttDown();
        typeof(FlexBase).GetMethod("RevokeAdmittedTransmitWork", Any)!.Invoke(_r.Rig, new object?[] { "a drop (test)", null });   // the queued ON keys nothing
        _controller.PttUp();
        RunNextQueued();                                     // the revoked ON
        Assert.True(Key.NeverWent);
        RunNextQueued();                                     // the OFF
        Assert.True(Within(2000, () => !_controller.ChirpStopOwed), "a key that never went out still owed its chirp");
        _r.Interlock(InterlockState.Ready);
        Thread.Sleep(200);
        Assert.Empty(Submitted());
    }

    [Fact]
    public void A_key_whose_setter_skipped_it_never_went_out_and_owes_no_falling_cue()
    {
        // FlexLib's setter sends nothing when its cache already reads Mox on (a
        // carrier we did not key was reported first): our ON never went out.
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _controller.PttDown();
        _r.Interlock(InterlockState.Transmitting);          // the cache reads Mox before our ON runs
        RunNextQueued();
        Assert.True(Key.NeverWent);
        _controller.PttUp();
        RunNextQueued();
        Assert.True(Within(2000, () => !_controller.ChirpStopOwed));
        _r.Interlock(InterlockState.Ready);
        Thread.Sleep(200);
        Assert.Empty(Submitted());
    }

    [Fact]
    public void A_refused_ask_owes_no_falling_cue()
    {
        // No key was asked at all (the rig refused it before the queue): the
        // controller holds no key, so its release owes nothing.
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        typeof(PttSafetyController).GetField("_key", Any)!.SetValue(_controller, null);
        typeof(PttSafetyController).GetMethod("KeyUpTone", Any)!.Invoke(_controller, null);
        Assert.False(_controller.ChirpStopOwed);
        _r.Interlock(InterlockState.Ready);
        Thread.Sleep(150);
        Assert.Empty(Submitted());
    }

    // ── S4: a carrier we keyed keeps our request's identity ──

    private void SendKeyedAndReleased(FlexBaseVoiceMessageRig send)
    {
        send.Transmit = true;
        RunNextQueued();                                     // the send's ON, written
        _r.Interlock(InterlockState.Transmitting);           // already reported by the radio
        send.Transmit = false;
        RunNextQueued();                                     // its OFF, written; Mox now off
    }

    [Fact]
    public void A_delayed_unkey_report_after_a_newer_tune_keeps_the_old_sends_identity_and_its_tone_is_void()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        var send = new FlexBaseVoiceMessageRig(_r.Rig);
        SendKeyedAndReleased(send);
        long sendOrigin = Key.Origin;
        _r.Rig.TxTune = true;                                // a newer tune asked; its ON stays queued
        Assert.NotEqual(sendOrigin, _r.Rig.TransmissionsAsked);

        _r.Interlock(InterlockState.UnkeyRequested);         // the OLD carrier's delayed reports
        Assert.Equal(sendOrigin, _r.Rig.LastCarrierOrigin);   // round 8 relabelled it with the tune's count
        _r.Interlock(InterlockState.Ready);
        RunNextQueued();                                     // the tune ON
        _controller.OweElsewhereStopTone();                  // what the send's end hands over
        Assert.True(Within(2000, () => !_controller.ElsewhereStopOwed), "the old send's debt was left waiting");
        Thread.Sleep(200);
        Assert.Empty(Submitted());
    }

    [Fact]
    public void The_same_send_with_no_newer_request_pays_on_its_carriers_receive()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        var send = new FlexBaseVoiceMessageRig(_r.Rig);
        SendKeyedAndReleased(send);
        _r.Interlock(InterlockState.UnkeyRequested);
        _r.Interlock(InterlockState.Ready);
        _controller.OweElsewhereStopTone();
        Assert.True(Within(2000, () => Submitted().Length == 1));
        Assert.Equal(new[] { nameof(EarconPlayer.TxElsewhereStopTone) }, Submitted());
    }

    // ── Round 10, S6: a takeover continues the carrier our earlier key put up ──

    [Fact]
    public void A_push_to_talk_takeover_of_a_sends_carrier_keeps_the_transferred_falling_tone()
    {
        // Codex's S6: the controller's own ask wrote no ON (FlexLib's cache
        // already read on), was classed as never gone out, and dropped the
        // three notes the send handed over.
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        var send = new FlexBaseVoiceMessageRig(_r.Rig);
        send.Transmit = true;
        RunNextQueued();                                     // the send's ON, written
        _r.Interlock(InterlockState.Transmitting);
        _controller.PttDown();                               // the takeover asks again
        _controller.TakeOverElsewhereStopTone();             // what the send's Settle hands over
        RunNextQueued();                                     // the redundant ON writes nothing
        Assert.False(Key.NeverWent);                         // it continues the send's carrier
        _controller.PttUp();
        RunNextQueued();                                     // our OFF, written
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 1), "the transferred falling tone was lost");
        Assert.Equal(new[] { nameof(EarconPlayer.TxElsewhereStopTone) }, Submitted());
    }

    [Fact]
    public void A_takeover_released_before_its_redundant_ON_runs_still_pays_once()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        var send = new FlexBaseVoiceMessageRig(_r.Rig);
        send.Transmit = true;
        RunNextQueued();
        _r.Interlock(InterlockState.Transmitting);
        _controller.PttDown();
        _controller.TakeOverElsewhereStopTone();
        _controller.PttUp();                                 // released before either item runs
        RunNextQueued();
        RunNextQueued();
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 1));
    }

    [Fact]
    public void A_held_press_turned_into_the_lock_keeps_its_falling_chirp()
    {
        // The pre-existing sibling: the lock's own ask continued the held key's
        // carrier, but superseded its count and left it unmarked.
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _controller.PttDown();
        RunNextQueued();                                     // ON written
        _r.Interlock(InterlockState.Transmitting);
        _controller.ToggleLock();                            // held becomes locked
        RunNextQueued();                                     // the redundant ON
        Assert.False(Key.NeverWent);
        _controller.ToggleLock();                            // unlock
        RunNextQueued();                                     // OFF written
        _r.Interlock(InterlockState.Ready);
        Assert.True(Within(2000, () => Submitted().Length == 1), "the lock's release lost its chirp");
        Assert.Equal(new[] { nameof(EarconPlayer.TxStopTone) }, Submitted());
    }

    [Fact]
    public void A_redundant_ask_with_no_ON_of_ours_outstanding_still_never_went_out()
    {
        // The rule's other half: the cache read on for a carrier we did not key
        // (a hand mic), and no key of ours was outstanding, so ours never went out.
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        PressWrittenWithItsTransmitReport();
        _r.Interlock(InterlockState.Ready);                  // our first key is over
        Assert.True(Within(2000, () => Submitted().Length == 1));
        _r.Interlock(InterlockState.Transmitting);           // a hand mic
        _controller.PttDown();
        RunNextQueued();
        Assert.True(Key.NeverWent);
    }

    // ── Round 10, F2: a receive parsed between our OFF's write and its bookkeeping ──

    [Fact]
    public void A_receive_parsed_between_our_written_OFF_and_its_bookkeeping_pays_the_cue()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _controller.PttDown();
        RunNextQueued();
        _r.Interlock(InterlockState.Transmitting);
        _controller.PttUp();
        bool once = true;
        _r.Transport.AfterWrite = command =>
        {
            if (!once || !command.Contains("|xmit 0")) return;
            once = false;
            _r.Interlock(InterlockState.Ready);              // the radio's prompt answer
        };
        RunNextQueued();                                     // OFF written; READY parsed before it returns
        Assert.True(Within(2000, () => Submitted().Length == 1), "the radio's first answer after our off was dated before it");
        Thread.Sleep(100);
        Assert.Single(Submitted());
    }

    // ── Integration fix 2026-11-06 (4): the final check's gate is bounded ──

    [Fact]
    public void A_falling_cue_whose_final_check_cannot_have_the_report_gate_is_not_played()
    {
        // The cue's final check took the transport's dispatch gate with an
        // unbounded lock, possibly on the UI thread. Now it waits only as long
        // as every other app-side take does, and on a timeout plays nothing.
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        PressWrittenWithItsTransmitReport();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var paid = new ManualResetEventSlim();
        Thread? holder = null;
        _controller.AfterFallingTonePaymentForTests = () => paid.Set();
        typeof(FlexBase).GetField("BeforeFallingCueFinalCheckForTests", Any)!.SetValue(_r.Rig, (Action)(() =>
        {
            holder = new Thread(() =>
            {
                lock (_r.Transport.ConnectionSync) { held.Set(); release.Wait(3000); }
            }) { IsBackground = true };
            holder.Start();
            Assert.True(held.Wait(2000));
        }));
        try
        {
            _r.Interlock(InterlockState.Ready);              // the radio's word after our OFF
            Assert.True(paid.Wait(3000), "the payment never finished");
            Assert.Empty(Submitted());                       // not played: the gate was not had in time
            Assert.False(_controller.ChirpStopOwed);
        }
        finally
        {
            release.Set();
            holder?.Join(3000);
            typeof(FlexBase).GetField("BeforeFallingCueFinalCheckForTests", Any)!.SetValue(_r.Rig, null);
        }
    }

    // ── Integration fix 2 (Codex's B2): the controller's closing unkey is our own carrier's only ──

    [Fact]
    public void Closing_the_controller_never_unkeys_another_clients_carrier()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _controller.PttDown();
        RunNextQueued();                                     // our ON
        // Another client now holds the transmitter.
        typeof(Radio).GetField("_txClientHandle", Any)!.SetValue(_r.Radio, 0x0BADF00Du);
        _r.Interlock(InterlockState.Transmitting);
        int before;
        lock (_r.Transport.Written) before = _r.Transport.Written.Count;
        _controller.Dispose();                               // as an import's power-off still does
        while (Queued() > 0) RunNextQueued();
        lock (_r.Transport.Written)
            Assert.DoesNotContain(_r.Transport.Written.Skip(before), w => w.Contains("|xmit 0"));
    }

    [Fact]
    public void A_carrier_we_did_not_key_still_takes_its_origin_from_the_report()
    {
        // Our last key's receive came after its OFF, so it is over; a hand-mic
        // carrier reported after a newer tune request takes the count then.
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        PressWrittenWithItsTransmitReport();
        _r.Interlock(InterlockState.Ready);                  // our key is over
        Assert.True(Within(2000, () => Submitted().Length == 1));
        _r.Rig.TxTune = true;
        _r.Rig.TxTune = false;
        _r.Interlock(InterlockState.Transmitting);           // the hand mic
        Assert.Equal(_r.Rig.TransmissionsAsked, _r.Rig.LastCarrierOrigin);
    }
}
