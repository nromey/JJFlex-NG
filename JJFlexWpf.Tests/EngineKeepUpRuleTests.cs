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

    /// <summary>
    /// Repair 4, after Codex's third re-check: a batch of buffers sharing one
    /// lateness is exactly that many buffers fed one at a time. Random batches,
    /// from none to several thousand, with gaps and restarts among them, on the
    /// default rule and on a small one that settles quickly; after each batch the
    /// returned change and every visible figure agree, and at the end both rules
    /// are fed the same single buffers and must answer alike, which they cannot
    /// if the batch left the ring's position or the recovery hold different.
    /// </summary>
    [Theory]
    [InlineData("default")]
    [InlineData("small")]
    [InlineData("long grace")]
    public void A_batch_of_buffers_is_exactly_that_many_buffers_one_at_a_time(string shape)
    {
        // "long grace" has a grace longer than the recovery hold, so a batch that
        // starts in the grace needs the grace in its bound as well as the window.
        EngineKeepUpRule Make() => shape switch
        {
            "small" => new EngineKeepUpRule(window: 10, fallAt: 4, recoverAt: 1, grace: 3, stuckWarmup: 7, recoverHold: 5),
            "long grace" => new EngineKeepUpRule(window: 10, fallAt: 4, recoverAt: 1, grace: 40, stuckWarmup: 7, recoverHold: 2),
            _ => new EngineKeepUpRule(),
        };
        var batch = Make();
        var one = Make();
        batch.Restart("nnr"); one.Restart("nnr");
        var seen = new System.Collections.Generic.HashSet<EngineKeepUpChange>();
        var random = new Random(4866);
        for (int step = 0; step < 400; step++)
        {
            int kind = random.Next(20);
            if (kind == 0) { batch.Gap(); one.Gap(); continue; }
            if (kind == 1) { batch.Restart("nnr"); one.Restart("nnr"); continue; }
            bool late = random.Next(3) == 0;
            int count = random.Next(4) switch { 0 => 0, 1 => random.Next(1, 5), 2 => random.Next(5, 200), _ => random.Next(200, 3000) };
            var expected = EngineKeepUpChange.None;
            for (int i = 0; i < count; i++) { var c = one.Observe(late); if (c != EngineKeepUpChange.None) expected = c; }
            Assert.Equal(expected, batch.Observe(late, count));
            Assert.Equal((one.Behind, one.LateInWindow, one.Filled), (batch.Behind, batch.LateInWindow, batch.Filled));
            seen.Add(expected);
        }
        // The sequence reached a fall and a recovery, or the comparison proved little.
        Assert.Contains(EngineKeepUpChange.FellBehind, seen);
        Assert.Contains(EngineKeepUpChange.Recovered, seen);
        for (int i = 0; i < 2000; i++)
        {
            bool late = random.Next(4) == 0;
            Assert.Equal(one.Observe(late), batch.Observe(late));
            Assert.Equal((one.Behind, one.LateInWindow), (batch.Behind, batch.LateInWindow));
        }
    }

    /// <summary>Repair 4: the batch form of a helper warm-up is that many warm-up buffers one at a time, an engine change included.</summary>
    [Fact]
    public void A_batch_of_warm_up_buffers_is_exactly_that_many_one_at_a_time()
    {
        var batch = new EngineKeepUpRule();
        var one = new EngineKeepUpRule();
        var random = new Random(1866);
        int falls = 0;
        for (int step = 0; step < 600; step++)
        {
            string id = random.Next(30) == 0 ? "nr2" : "nnr";
            bool late = random.Next(3) != 0;
            int count = random.Next(3) switch { 0 => 0, 1 => random.Next(1, 40), _ => random.Next(40, 700) };
            var expected = one.Warming(id, false);
            for (int i = 0; i < count; i++) { var c = one.Warming(id, late); if (c != EngineKeepUpChange.None) expected = c; }
            Assert.Equal(expected, batch.Warming(id, late, count));
            Assert.Equal((one.Behind, one.EngineId), (batch.Behind, batch.EngineId));
            if (expected == EngineKeepUpChange.FellBehind) falls++;
            if (random.Next(10) == 0) { one.Restart(id); batch.Restart(id); }
        }
        // The sequence fell more than once, or the comparison proved little; and the stuck mark exactly.
        Assert.True(falls > 1, $"only {falls} falls");
        var fresh = new EngineKeepUpRule();
        Assert.Equal(EngineKeepUpChange.FellBehind, fresh.Warming("nnr", true, EngineKeepUpRule.DefaultStuckWarmup));
        var under = new EngineKeepUpRule();
        Assert.Equal(EngineKeepUpChange.None, under.Warming("nnr", true, EngineKeepUpRule.DefaultStuckWarmup - 1));
        Assert.Equal(EngineKeepUpChange.FellBehind, under.Warming("nnr", true, 1));
    }

    /// <summary>
    /// Repair 4: the batch form is bounded in time on the audio thread. The
    /// largest count a meter can report is judged in a few hundred steps, not
    /// two billion, and still arrives at the recovery it describes.
    /// </summary>
    [Fact]
    public void The_largest_batch_is_judged_in_bounded_time_and_still_recovers()
    {
        var rule = new EngineKeepUpRule();
        rule.Restart("nnr");
        Assert.Equal(EngineKeepUpChange.FellBehind, rule.Observe(true, 200));
        var t = System.Diagnostics.Stopwatch.StartNew();
        var change = rule.Observe(false, int.MaxValue);
        t.Stop();
        Assert.Equal(EngineKeepUpChange.Recovered, change);
        Assert.False(rule.Behind);
        Assert.True(t.ElapsedMilliseconds < 500, $"int.MaxValue buffers took {t.ElapsedMilliseconds} ms");
        Assert.Equal(EngineKeepUpChange.None, rule.Observe(false, 0));
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
