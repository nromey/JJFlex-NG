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

    [Fact]
    public void A_carrier_rising_between_the_receive_report_and_the_tone_voids_the_tone()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _controller.PttDown();
        _r.VendorMox(true);
        _r.Interlock(InterlockState.Transmitting);
        _controller.PttUp();
        _r.VendorMox(false);

        Monitor.Enter(EdgeToneLock());                         // the payment waits at its lock
        try
        {
            _r.Interlock(InterlockState.Ready);               // the wait sees receive and calls the payment
            Thread.Sleep(300);
            _r.VendorMox(true);                                // a carrier rises that the controller did not key
            _r.Interlock(InterlockState.Transmitting);
        }
        finally { Monitor.Exit(EdgeToneLock()); }
        Thread.Sleep(300);
        Assert.Empty(Submitted());
    }

    [Fact]
    public void A_connection_going_between_the_receive_report_and_the_tone_voids_the_tone()
    {
        AssertTheDeskIsSafe();
        _r.Interlock(InterlockState.Ready);
        _controller.PttDown();
        _r.VendorMox(true);
        _r.Interlock(InterlockState.Transmitting);
        _controller.PttUp();
        _r.VendorMox(false);

        Monitor.Enter(EdgeToneLock());
        try
        {
            _r.Interlock(InterlockState.Ready);
            Thread.Sleep(300);
            _r.Transport.Fall();
        }
        finally { Monitor.Exit(EdgeToneLock()); }
        Thread.Sleep(300);
        Assert.Empty(Submitted());
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
