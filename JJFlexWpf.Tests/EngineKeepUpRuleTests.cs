using System;
using System.Linq;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The keep-up rule on its own (Sprint 49 Track L, #866): made-up sequences of
/// buffers, including the warm-up and a switch, with each negative beside a
/// positive control.
/// </summary>
public sealed class EngineKeepUpRuleTests
{
    public EngineKeepUpRuleTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static EngineKeepUpChange[] Feed(EngineKeepUpRule rule, int count, Func<int, bool> late) =>
        Enumerable.Range(0, count).Select(i => rule.Observe(late(i))).ToArray();

    [Fact]
    public void An_engine_late_in_every_buffer_falls_behind_once_after_the_grace_and_twenty_late_buffers()
    {
        var rule = new EngineKeepUpRule();
        rule.Restart("slow");
        var changes = Feed(rule, 300, _ => true);
        int fall = Array.IndexOf(changes, EngineKeepUpChange.FellBehind);
        Assert.Equal(EngineKeepUpRule.DefaultGrace + EngineKeepUpRule.DefaultFallAt - 1, fall);
        Assert.Single(changes, c => c == EngineKeepUpChange.FellBehind);
        Assert.DoesNotContain(EngineKeepUpChange.KeepingUp, changes);
        Assert.True(rule.Behind);
        Assert.Equal("slow", rule.EngineId);
    }

    [Fact]
    public void Scheduler_noise_of_one_late_buffer_a_second_never_falls_behind_and_says_it_keeps_up_once()
    {
        var rule = new EngineKeepUpRule();
        rule.Restart("fast");
        var changes = Feed(rule, 2000, i => i % 100 == 37);
        Assert.DoesNotContain(EngineKeepUpChange.FellBehind, changes);
        Assert.Single(changes, c => c == EngineKeepUpChange.KeepingUp);
        Assert.Equal(EngineKeepUpRule.DefaultGrace + EngineKeepUpRule.DefaultWindow - 1, Array.IndexOf(changes, EngineKeepUpChange.KeepingUp));
    }

    [Fact]
    public void Lateness_inside_the_grace_after_a_start_is_start_up_and_is_not_counted()
    {
        // Negative: the first 50 buffers late, then on time.
        var rule = new EngineKeepUpRule();
        rule.Restart("helper");
        var changes = Feed(rule, 400, i => i < EngineKeepUpRule.DefaultGrace);
        Assert.DoesNotContain(EngineKeepUpChange.FellBehind, changes);
        // Positive control: the same lateness one grace later is counted.
        var control = new EngineKeepUpRule();
        control.Restart("helper");
        var controlChanges = Feed(control, 400, i => i >= EngineKeepUpRule.DefaultGrace && i < 2 * EngineKeepUpRule.DefaultGrace);
        Assert.Contains(EngineKeepUpChange.FellBehind, controlChanges);
    }

    [Fact]
    public void A_gap_or_a_restart_forgets_the_window_and_begins_the_grace_again()
    {
        var rule = new EngineKeepUpRule();
        rule.Restart("e");
        Feed(rule, EngineKeepUpRule.DefaultGrace, _ => false);
        Assert.DoesNotContain(EngineKeepUpChange.FellBehind, Feed(rule, 19, _ => true));
        rule.Gap();
        Assert.Equal(0, rule.LateInWindow);
        // Nineteen more late buffers would have made twenty; after the gap they fall in the grace.
        Assert.DoesNotContain(EngineKeepUpChange.FellBehind, Feed(rule, 19, _ => true));
        Assert.Equal(0, rule.LateInWindow);

        // A switch to another engine is a restart: the old engine's window is gone.
        rule.Restart("other");
        Assert.Equal("other", rule.EngineId);
        Assert.False(rule.Behind);
        Assert.Equal(0, rule.Filled);
    }

    [Fact]
    public void Recovery_needs_the_window_clean_for_the_hold_as_well_so_an_edge_engine_does_not_flap()
    {
        var rule = new EngineKeepUpRule();
        rule.Restart("edge");
        Feed(rule, EngineKeepUpRule.DefaultGrace, _ => false);
        Assert.Contains(EngineKeepUpChange.FellBehind, Feed(rule, 100, _ => true));

        // Clean for under the hold, then late again: never recovered, and not a second fall either.
        var flap = Feed(rule, EngineKeepUpRule.DefaultWindow + 300, _ => false).Concat(Feed(rule, 100, _ => true)).ToArray();
        Assert.DoesNotContain(EngineKeepUpChange.Recovered, flap);
        Assert.DoesNotContain(EngineKeepUpChange.FellBehind, flap);
        Assert.True(rule.Behind);

        // Clean for the whole window and the hold: recovered, once.
        var back = Feed(rule, EngineKeepUpRule.DefaultWindow + EngineKeepUpRule.DefaultRecoverHold + 50, _ => false);
        Assert.Single(back, c => c == EngineKeepUpChange.Recovered);
        // The window was all late: it is clean once all but RecoverAt have slid
        // out, and it must then stay clean for the hold, one buffer more.
        int clean = EngineKeepUpRule.DefaultWindow - EngineKeepUpRule.DefaultRecoverAt - 1;
        Assert.Equal(clean + EngineKeepUpRule.DefaultRecoverHold, Array.IndexOf(back, EngineKeepUpChange.Recovered));
        Assert.False(rule.Behind);
        // And it can fall again.
        Assert.Contains(EngineKeepUpChange.FellBehind, Feed(rule, 100, _ => true));
    }

    [Fact]
    public void A_helper_warm_up_that_never_ends_falls_behind_once_and_one_that_ends_does_not()
    {
        var rule = new EngineKeepUpRule();
        var changes = Enumerable.Range(0, 400).Select(_ => rule.Warming("nnr")).ToArray();
        Assert.Equal(EngineKeepUpRule.DefaultStuckWarmup - 1, Array.IndexOf(changes, EngineKeepUpChange.FellBehind));
        Assert.Single(changes, c => c == EngineKeepUpChange.FellBehind);
        Assert.Equal("nnr", rule.EngineId);

        // A warm-up that ends (the engine becomes the running one) is a restart: nothing said.
        var ok = new EngineKeepUpRule();
        var short_ = Enumerable.Range(0, 30).Select(_ => ok.Warming("nnr")).ToArray();
        ok.Restart("nnr");
        Assert.All(short_, c => Assert.Equal(EngineKeepUpChange.None, c));
        Assert.False(ok.Behind);

        // A different engine warming starts its own count.
        var swap = new EngineKeepUpRule();
        for (int i = 0; i < EngineKeepUpRule.DefaultStuckWarmup - 1; i++) swap.Warming("nnr");
        Assert.Equal(EngineKeepUpChange.None, swap.Warming("nr2"));
        Assert.Equal("nr2", swap.EngineId);
    }

    [Fact]
    public void A_helper_warm_up_counts_only_the_buffers_its_processing_was_over_the_audio()
    {
        // Repair 1, B1: a warm-up that never ends because answers are delayed,
        // with the helper's processing fast, is not the warning's business.
        var fast = new EngineKeepUpRule();
        var none = Enumerable.Range(0, 1000).Select(_ => fast.Warming("nnr", late: false)).ToArray();
        Assert.All(none, c => Assert.Equal(EngineKeepUpChange.None, c));
        Assert.False(fast.Behind);

        // Positive control: processing over the audio in every buffer falls behind at the stuck mark.
        var slow = new EngineKeepUpRule();
        var changes = Enumerable.Range(0, 400).Select(_ => slow.Warming("nnr", late: true)).ToArray();
        Assert.Equal(EngineKeepUpRule.DefaultStuckWarmup - 1, Array.IndexOf(changes, EngineKeepUpChange.FellBehind));

        // Only the late buffers count: half of them late takes twice as long.
        var half = new EngineKeepUpRule();
        var mixed = Enumerable.Range(0, 1000).Select(i => half.Warming("nnr", late: i % 2 == 0)).ToArray();
        Assert.Equal(2 * EngineKeepUpRule.DefaultStuckWarmup - 2, Array.IndexOf(mixed, EngineKeepUpChange.FellBehind));
    }

    [Fact]
    public void Every_threshold_is_checked_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineKeepUpRule(window: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineKeepUpRule(window: 10, fallAt: 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineKeepUpRule(fallAt: 5, recoverAt: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineKeepUpRule(grace: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineKeepUpRule(stuckWarmup: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineKeepUpRule(recoverHold: -1));
    }
}
