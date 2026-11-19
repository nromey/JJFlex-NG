using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track N, repair 1: the mode-status hold (#1124) as a class.
/// A newer operator request supersedes a pending hold (F1); a poll that finds
/// the current group again cancels it, as the event road does (F2); and the
/// hold ends a fixed time after the REQUEST, not after the report it holds.
/// The first two reproductions are Codex's (2026-11-18 check of 9ffe872a),
/// adopted as written.
/// </summary>
/// <remarks>Windowless: the noise controller over <see cref="NoiseFixture"/>'s fakes, on an MTA thread.</remarks>
public sealed class ModeBounceHoldTests
{
    public ModeBounceHoldTests() => NoiseReductionPerChannelTests.AssertWindowless();

    /// <summary>Voice remembers RNNoise on, CW remembers NNR (its starting point); back in voice.</summary>
    private static NoiseFixture Ready(Func<long> clock)
    {
        var f = new NoiseFixture(clock: clock);
        f.Noise.Attach("USB");
        f.Noise.Toggle("rnnoise", "setup");
        f.Noise.OnModeChanged("CW");
        f.Noise.OnModeChanged("USB");
        f.Spoken.Clear();
        return f;
    }

    [Fact]
    public void A_new_operator_request_supersedes_an_already_held_report()
    {
        long now = 1_000_000;
        using var f = Ready(() => now);
        f.Noise.NoteModeRequested();
        f.Noise.OnModeChanged("CW");
        now += 100;
        f.Noise.OnModeChanged("USB");                  // held: a contradiction of the CW answer
        now += 200;
        f.Noise.NoteModeRequested();                   // the operator now asks for USB
        f.Noise.OnModeChanged("USB");                  // its answer is taken at once
        Assert.Equal(NoiseModeGroup.Voice, f.Noise.CurrentGroup);
        Assert.Equal("rnnoise", f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.Equal(0, f.Clock.Pending);              // and the old hold is gone, not merely outrun
    }

    [Fact]
    public void A_poll_return_to_the_current_group_cancels_a_held_report()
    {
        long now = 1_000_000;
        using var f = Ready(() => now);
        f.Noise.NoteModeRequested();
        f.Noise.OnModeChanged("CW");
        now += 100;
        f.Radio.Mode = "USB";
        f.Noise.FollowReceiveMode("stale poll");
        now += 200;
        f.Radio.Mode = "CW";
        f.Noise.FollowReceiveMode("the active slice is CW again");
        f.Clock.RunAll();
        Assert.Equal(NoiseModeGroup.Cw, f.Noise.CurrentGroup);
        Assert.Equal("nnr", f.Pipeline.NeuralSlot.ChosenEngineId);
        Assert.DoesNotContain(f.Spoken.Said, s => s.Text.StartsWith("Back in a voice mode", StringComparison.Ordinal));
    }

    [Fact]
    public void A_real_quick_operator_round_trip_without_a_held_report_is_followed()
    {
        // Codex's control: passes before and after the repair.
        long now = 1_000_000;
        using var f = Ready(() => now);
        f.Noise.NoteModeRequested(); f.Noise.OnModeChanged("CW");
        now += 317;
        f.Noise.NoteModeRequested(); f.Noise.OnModeChanged("USB");
        Assert.Equal(NoiseModeGroup.Voice, f.Noise.CurrentGroup);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(606)]   // the earliest stale report on disk after a menu request (e4 trace 2026-11-12 02:01, +606 to +632 ms)
    [InlineData(1400)]
    public void The_hold_ends_a_fixed_time_after_the_request_whenever_the_held_report_arrived(int reportAfterRequestMs)
    {
        long now = 1_000_000;
        using var f = Ready(() => now);
        f.Noise.NoteModeRequested();
        long requested = now;
        f.Noise.OnModeChanged("CW");
        now = requested + reportAfterRequestMs;
        f.Noise.OnModeChanged("USB");
        Assert.Equal(1, f.Clock.Pending);
        Assert.Equal(TimeSpan.FromMilliseconds(NoiseController.ModeBounceWindowMs - reportAfterRequestMs), f.Clock.Delays[^1]);
    }

    [Fact]
    public void A_report_at_or_after_the_bound_is_entered_at_once()
    {
        long now = 1_000_000;
        using var f = Ready(() => now);
        f.Noise.NoteModeRequested();
        f.Noise.OnModeChanged("CW");
        now += NoiseController.ModeBounceWindowMs;
        f.Noise.OnModeChanged("USB");
        Assert.Equal(NoiseModeGroup.Voice, f.Noise.CurrentGroup);
        Assert.Equal(0, f.Clock.Pending);
    }

    [Fact]
    public void The_bound_covers_every_stale_return_measured_on_disk()
    {
        // The four stale bounces in the e4 harness trace (JJFlexRadioTrace-20261112-020102.txt,
        // bench-2026-11-11-e2): the radio's contradicting report came back to the
        // requested mode 781 to 866 ms after the menu request. The hold must still
        // be in force then, with margin; otherwise the false group sentence returns.
        Assert.True(NoiseController.ModeBounceWindowMs >= 866 + 500);
    }
}

/// <summary>
/// Repair 1, F3: a hold's timer callback carries the hold's identity. The real
/// <see cref="TimerNoiseScheduler"/> posts its action after disposing its timer,
/// so cancelling cannot retract an action already posted; the posted action must
/// release only its own hold. Codex's reproduction, adopted, plus the same across
/// a detach and attach.
/// </summary>
/// <remarks>Windowless: a real threading timer whose action is captured in a queue, never a dispatcher.</remarks>
public sealed class ModeBounceTimerTests
{
    public ModeBounceTimerTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static NoiseController Make(NoiseFixture f, TimerNoiseScheduler scheduler, Func<long> clock) =>
        new(f.Catalog, () => f.Radio, () => f.Pipeline, () => f.Config, () => { }, f.Spoken, f.Held, scheduler, f.Cost,
            clock: clock, transmitChain: () => null);

    [Fact]
    public void A_timer_action_already_posted_before_cancellation_cannot_release_a_later_hold()
    {
        long now = 1_000_000;
        using var f = new NoiseFixture();
        var posted = new ConcurrentQueue<Action>();
        var noise = Make(f, new TimerNoiseScheduler(a => posted.Enqueue(a)), () => now);
        try
        {
            noise.Attach("USB"); noise.Toggle("rnnoise", "setup");
            noise.OnModeChanged("CW"); noise.OnModeChanged("USB");
            noise.NoteModeRequested(); noise.OnModeChanged("CW");
            now += 100; noise.OnModeChanged("USB");
            Assert.True(SpinWait.SpinUntil(() => !posted.IsEmpty, 5000), "the real timer must post its action");
            now += 1000; noise.OnModeChanged("CW");          // the hold is cancelled, its action already posted
            noise.NoteModeRequested(); noise.OnModeChanged("USB");
            now += 100; noise.OnModeChanged("CW");           // a second hold
            Assert.Equal(NoiseModeGroup.Voice, noise.CurrentGroup);
            Assert.True(posted.TryDequeue(out var oldCallback));
            oldCallback!();
            Assert.Equal(NoiseModeGroup.Voice, noise.CurrentGroup);
        }
        finally { noise.Detach(); }
    }

    [Fact]
    public void A_timer_action_posted_before_a_detach_cannot_release_a_hold_taken_after_the_next_attach()
    {
        long now = 1_000_000;
        using var f = new NoiseFixture();
        var posted = new ConcurrentQueue<Action>();
        var noise = Make(f, new TimerNoiseScheduler(a => posted.Enqueue(a)), () => now);
        try
        {
            noise.Attach("USB"); noise.Toggle("rnnoise", "setup");
            noise.OnModeChanged("CW"); noise.OnModeChanged("USB");
            noise.NoteModeRequested(); noise.OnModeChanged("CW");
            now += 100; noise.OnModeChanged("USB");
            Assert.True(SpinWait.SpinUntil(() => !posted.IsEmpty, 5000), "the real timer must post its action");
            noise.Detach();
            noise.Attach("USB");
            now += 5000;
            noise.NoteModeRequested(); noise.OnModeChanged("CW");
            now += 100; noise.OnModeChanged("USB");          // a hold in the new connection
            Assert.Equal(NoiseModeGroup.Cw, noise.CurrentGroup);
            Assert.True(posted.TryDequeue(out var oldCallback));
            oldCallback!();
            Assert.Equal(NoiseModeGroup.Cw, noise.CurrentGroup);   // the old connection's action released nothing
        }
        finally { noise.Detach(); }
    }
}
