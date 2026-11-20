using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// The receive slot's running tally (#1229), driven with a fake clock: a
/// thousand ticks a second, so a buffer of 10 ms audio is 10 ticks of audio
/// and a minute is 60,000 ticks. Pure: no pipeline, no thread, no window.
/// </summary>
public sealed class SlotTallyTests
{
    private const long Tps = 1000;
    private readonly ITestOutputHelper _out;

    public SlotTallyTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    private static List<SlotTallyReport> Take(SlotTally t)
    {
        var list = new List<SlotTallyReport>();
        while (t.TryTake(out var r)) list.Add(r);
        return list;
    }

    /// <summary>One in-process buffer every 10 ms of clock, taking <paramref name="workMs"/> of it.</summary>
    private static long Feed(SlotTally t, string id, long now, int buffers, double workMs, bool late = false)
    {
        for (int i = 0; i < buffers; i++, now += 10)
            t.Buffer(id, false, now, 1, late ? 1 : 0, false, workMs, 10);
        return now;
    }

    [Fact]
    public void A_minute_of_running_writes_one_line_with_its_counts_and_the_next_period_starts_at_once()
    {
        var t = new SlotTally(DenoiseFamily.Neural, Tps);
        long now = Feed(t, "rnnoise", 1, 6000, 2.0);
        Assert.Empty(Take(t));                   // 59.99 s: not yet
        now = Feed(t, "rnnoise", now, 1, 2.0);   // the buffer at 60 s closes the minute
        var r = Assert.Single(Take(t));
        _out.WriteLine(r.Describe());
        Assert.Equal(SlotTallyReason.Minute, r.Reason);
        Assert.Equal("rnnoise", r.EngineId);
        Assert.Equal(6001, r.Buffers);
        Assert.Equal(6001, r.Judged);
        Assert.Equal(0, r.Late);
        Assert.Equal(2.0, r.AverageMsPer10, 6);
        Assert.Equal(2.0, r.WorstSecondMsPer10, 6);
        Assert.Equal(60, r.Seconds);
        Assert.Equal(60.0, r.PeriodSeconds, 3);
        Assert.Contains("for the last minute", r.Describe());
        // The next minute is counted from here.
        Feed(t, "rnnoise", now, 6000, 2.0);
        Assert.Single(Take(t));
    }

    [Fact]
    public void The_worst_second_is_kept_apart_from_the_average()
    {
        var t = new SlotTally(DenoiseFamily.Neural, Tps);
        long now = Feed(t, "deepfilternet3", 1, 100, 2.0);
        now = Feed(t, "deepfilternet3", now, 100, 8.0, late: false);
        now = Feed(t, "deepfilternet3", now, 100, 12.0, late: true);
        t.FeedEnded(now);
        var reports = Take(t);
        var r = reports.First(x => x.Reason == SlotTallyReason.FeedEnded);
        _out.WriteLine(r.Describe());
        Assert.Equal(300, r.Buffers);
        Assert.Equal(100, r.Late);
        Assert.Equal(22.0 / 3, r.AverageMsPer10, 6);
        Assert.Equal(12.0, r.WorstSecondMsPer10, 6);
        Assert.Equal(3, r.Seconds);
    }

    [Fact]
    public void A_helper_period_counts_frames_judged_late_frames_missed_buffers_and_dips()
    {
        var t = new SlotTally(DenoiseFamily.Neural, Tps);
        long now = 1;
        for (int i = 0; i < 200; i++, now += 10)
        {
            bool slow = i >= 150;
            bool missed = i % 10 == 0;
            // Two frames of new work on every other buffer, none on the rest.
            int frames = i % 2 == 0 ? 2 : 0;
            t.Buffer("nnr", true, now, frames, slow ? frames : 0, missed, frames * (slow ? 12.0 : 4.8), frames * 10.0, dipBegan: i == 50);
        }
        t.FeedEnded(now);
        var r = Take(t).First(x => x.Reason == SlotTallyReason.FeedEnded);
        _out.WriteLine(r.Describe());
        Assert.True(r.Helper);
        Assert.Equal(200, r.Buffers);
        Assert.Equal(200, r.Judged);
        Assert.Equal(50, r.Late);
        Assert.Equal(20, r.Missed);
        Assert.Equal(1, r.DipsBegan);
        Assert.Contains("helper frames judged by the warning's rule", r.Describe());
        Assert.Contains("1 delivery dip(s) began", r.Describe());
    }

    [Fact]
    public void A_slot_that_processes_nothing_for_a_second_closes_its_period_at_the_moment_it_stopped()
    {
        var t = new SlotTally(DenoiseFamily.Spectral, Tps);
        long now = Feed(t, "nr4", 1, 300, 1.0);
        long stoppedAt = now;
        // A short pause is not a stop.
        for (int i = 0; i < 50; i++, now += 10) Assert.False(t.Idle(now));
        now = Feed(t, "nr4", now, 10, 1.0);
        Assert.Empty(Take(t));
        stoppedAt = now;
        bool closed = false;
        for (int i = 0; i < 101 && !closed; i++, now += 10) closed = t.Idle(now);
        Assert.True(closed, "a second of nothing processed never closed the period");
        var r = Assert.Single(Take(t));
        _out.WriteLine(r.Describe());
        Assert.Equal(SlotTallyReason.Stopped, r.Reason);
        Assert.Equal(310, r.Buffers);
        Assert.Equal((stoppedAt - 1) / 1000.0, r.PeriodSeconds, 3);
        Assert.Equal("", t.EngineId);
    }

    [Fact]
    public void A_period_shorter_than_a_second_of_audio_says_so_rather_than_claiming_a_whole_second()
    {
        var t = new SlotTally(DenoiseFamily.Neural, Tps);
        long now = Feed(t, "rnnoise", 1, 20, 3.0);
        t.FeedEnded(now);
        var r = Take(t).First(x => x.Reason == SlotTallyReason.FeedEnded);
        _out.WriteLine(r.Describe());
        Assert.Equal(0, r.Seconds);
        Assert.True(r.WorstSecondMsPer10 < 0, "a part-second was reported as a whole-second worst");
        Assert.Equal(3.0, r.PartSecondMsPer10, 6);
        Assert.Contains("less than a second of audio", r.Describe());
        Assert.DoesNotContain("whole second", r.Describe());
    }

    [Fact]
    public void A_dip_that_begins_with_the_buffer_that_closes_a_minute_belongs_to_that_minute()
    {
        // Codex's probe, with the real delivery rule in the pipeline's order:
        // the rule's fall is known before the buffer goes to the tally, and the
        // buffer is what closes the minute. Until repair 1 the dip landed in
        // the next period, a zero-buffer one if PC audio went off next.
        var t = new SlotTally(DenoiseFamily.Neural, Tps, periodTicks: 100);
        var delivery = new EngineKeepUpRule(window: 2, fallAt: 1, recoverAt: 0, grace: 0);
        delivery.Observe(false);
        t.Buffer("nnr", true, 1, 1, 0, false, 4, 10, dipBegan: false);
        var change = delivery.Observe(true);
        Assert.Equal(EngineKeepUpChange.FellBehind, change);
        t.Buffer("nnr", true, 101, 1, 0, true, 4, 10, dipBegan: change == EngineKeepUpChange.FellBehind);
        t.FeedEnded(102);
        var reports = Take(t);
        foreach (var r in reports) _out.WriteLine(r.Describe());
        var minute = reports.Single(r => r.Reason == SlotTallyReason.Minute);
        Assert.Equal(2, minute.Buffers);
        Assert.Equal(1, minute.Missed);
        Assert.Equal(1, minute.DipsBegan);
        Assert.DoesNotContain(reports, r => r.Reason == SlotTallyReason.FeedEnded && r.DipsBegan != 0);
        Assert.Equal(1, reports.Single(r => r.Reason == SlotTallyReason.Session).DipsBegan);
    }

    [Fact]
    public void A_dip_with_a_change_of_engine_belongs_to_the_new_engines_period()
    {
        var t = new SlotTally(DenoiseFamily.Neural, Tps);
        long now = Feed(t, "rnnoise", 1, 20, 1.0);
        t.Buffer("nnr", true, now, 1, 0, true, 4, 10, dipBegan: true);
        t.FeedEnded(now + 10);
        var reports = Take(t);
        Assert.Equal(0, reports.Single(r => r.Reason == SlotTallyReason.Changed).DipsBegan);
        Assert.Equal(1, reports.Single(r => r.Reason == SlotTallyReason.FeedEnded).DipsBegan);
    }

    [Fact]
    public void A_helper_batch_longer_than_a_second_completes_every_second_it_covers_at_its_own_cost()
    {
        // Codex's probe: 0.99 s at 2 ms, then one batch of 200 frames (2 s) at
        // 12 ms. That is 2.99 s: the first second is 0.99 s at 2 and 0.01 s at
        // 12, the second is all 12, and 0.99 s at 12 is left over. Until repair 1
        // it reported one whole second, worst 8.69.
        var t = new SlotTally(DenoiseFamily.Neural, Tps);
        for (int i = 0; i < 99; i++) t.Buffer("nnr", true, i + 1, 1, 0, false, 2, 10);
        t.Buffer("nnr", true, 100, 200, 200, false, 2400, 2000);
        t.FeedEnded(101);
        var r = Take(t).First(x => x.Reason == SlotTallyReason.FeedEnded);
        _out.WriteLine(r.Describe());
        Assert.Equal(2, r.Seconds);
        Assert.Equal(12.0, r.WorstSecondMsPer10, 6);
        Assert.Equal(12.0, r.PartSecondMsPer10, 6);
        Assert.Equal((99 * 2.0 + 2400) / 2990 * 10, r.AverageMsPer10, 6);
    }

    [Fact]
    public void A_batch_of_801_frames_spread_over_seconds_completes_eight_and_its_remainder_finishes_the_ninth()
    {
        // The size Track L's own pipeline test forwards in one batch.
        var t = new SlotTally(DenoiseFamily.Neural, Tps);
        t.Buffer("nnr", true, 1, 801, 0, false, 801 * 3.0, 8010);
        t.Buffer("nnr", true, 11, 99, 0, false, 99 * 5.0, 990);
        t.FeedEnded(12);
        var r = Take(t).First(x => x.Reason == SlotTallyReason.FeedEnded);
        _out.WriteLine(r.Describe());
        Assert.Equal(9, r.Seconds);
        // The ninth second is 0.01 s at 3 and 0.99 s at 5.
        Assert.Equal(0.01 * 3 + 0.99 * 5, r.WorstSecondMsPer10, 6);
        Assert.True(r.PartSecondMsPer10 < 0);
    }

    [Fact]
    public void A_session_never_takes_a_part_second_as_its_worst_whole_second()
    {
        // Codex's probe: a 10 ms period at 100 ms per 10 ms, then a whole second
        // at 2 ms. Until repair 1 the session said "worst second 100".
        var t = new SlotTally(DenoiseFamily.Neural, Tps);
        t.Buffer("rnnoise", false, 1, 1, 1, false, 100, 10);
        for (int i = 0; i < 100; i++) t.Buffer("other", false, 2 + i, 1, 0, false, 2, 10);
        t.FeedEnded(103);
        var reports = Take(t);
        foreach (var r in reports) _out.WriteLine(r.Describe());
        var first = reports.First(r => r.Reason == SlotTallyReason.Changed);
        Assert.Equal(0, first.Seconds);
        Assert.Equal(100.0, first.PartSecondMsPer10, 6);   // the period's own line still shows it, as a part-second
        var session = reports.Last();
        Assert.Equal(SlotTallyReason.Session, session.Reason);
        Assert.Equal(1, session.Seconds);
        Assert.Equal(2.0, session.WorstSecondMsPer10, 6);
        Assert.True(session.PartSecondMsPer10 < 0);
    }

    [Fact]
    public void A_change_of_engine_closes_the_old_engines_period_and_opens_the_new_ones()
    {
        var t = new SlotTally(DenoiseFamily.Neural, Tps);
        long now = Feed(t, "rnnoise", 1, 120, 1.0);
        now = Feed(t, "deepfilternet3", now, 30, 5.0);
        var r = Assert.Single(Take(t));
        Assert.Equal(SlotTallyReason.Changed, r.Reason);
        Assert.Equal("rnnoise", r.EngineId);
        Assert.Equal(120, r.Buffers);
        Assert.Equal("deepfilternet3", t.EngineId);
    }

    [Fact]
    public void PC_audio_off_closes_the_open_period_and_writes_the_session_total_over_every_period()
    {
        var t = new SlotTally(DenoiseFamily.Neural, Tps);
        long now = Feed(t, "rnnoise", 1, 6000, 1.0);          // a minute
        now = Feed(t, "rnnoise", now, 1000, 1.0);             // closes it, then 10 s more
        now = Feed(t, "deepfilternet3", now, 499, 6.0, late: false);
        t.Buffer("deepfilternet3", false, now, 1, 0, false, 6.0, 10, dipBegan: true);
        now += 10;
        t.FeedEnded(now);
        var reports = Take(t);
        foreach (var r in reports) _out.WriteLine(r.Describe());
        Assert.Equal(new[] { SlotTallyReason.Minute, SlotTallyReason.Changed, SlotTallyReason.FeedEnded, SlotTallyReason.Session },
            reports.Select(r => r.Reason).ToArray());
        var s = reports.Last();
        Assert.Equal(3, s.Periods);
        Assert.Equal(7500, s.Buffers);
        Assert.Equal(1, s.DipsBegan);
        Assert.Equal("deepfilternet3", s.EngineId);
        Assert.Equal((7000 * 1.0 + 500 * 6.0) / 7500, s.AverageMsPer10, 6);
        Assert.Equal(6.0, s.WorstSecondMsPer10, 6);
        Assert.Contains("session total at PC audio off, 3 period(s)", s.Describe());
        // A new session starts from nothing.
        t.FeedEnded(now + 10);
        var empty = Assert.Single(Take(t));
        Assert.Equal(SlotTallyReason.Session, empty.Reason);
        Assert.Equal(0, empty.Periods);
        Assert.Contains("ran no engine", empty.Describe());
    }

    [Fact]
    public void A_reader_that_falls_a_whole_ring_behind_loses_reports_counted_never_torn()
    {
        var t = new SlotTally(DenoiseFamily.Neural, Tps, periodTicks: 100);
        long now = 1;
        for (int i = 0; i < 40 * 11; i++, now += 10) t.Buffer("rnnoise", false, now, 1, 0, false, 1, 10);
        var first = Take(t);
        Assert.Equal(16, first.Count);
        Assert.All(first, r => Assert.Equal("rnnoise", r.EngineId));
        for (int i = 0; i < 20; i++, now += 10) t.Buffer("rnnoise", false, now, 1, 0, false, 1, 10);
        var next = Take(t);
        Assert.NotEmpty(next);
        Assert.True(next[0].Dropped > 0, "the lost reports were not counted");
        Assert.Contains("lost because the reader fell behind", next[0].Describe());
    }
}
