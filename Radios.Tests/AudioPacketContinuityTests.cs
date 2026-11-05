#nullable enable
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The continuity model a playback take's capture uses (round three), as
/// arithmetic: real-shaped packets in, placed packets and their gaps out. The
/// packets are shaped as <see cref="UnifiedTakesCaptureTests"/> shapes them:
/// whole UTC seconds plus a fractional half that is not time, the 4-bit
/// counter, and an arrival time the test sets.
/// </summary>
public sealed class AudioPacketContinuityTests
{
    private sealed class Run
    {
        public readonly AudioPacketContinuity<int> Model = new(Stopwatch.Frequency);
        public readonly List<PlacedPacket<int>> Placed = new();
        public readonly List<PacketOutcome> Outcomes = new();

        public void Offer(int index, double lateMs = 0) =>
            Outcomes.Add(Model.Offer(index, UnifiedTakesCaptureTests.RadioKey(index), UnifiedTakesCaptureTests.Sequence(index),
                UnifiedTakesCaptureTests.Arrival(index, lateMs), Placed));

        public void OfferAt(int index, long arrival) =>
            Outcomes.Add(Model.Offer(index, UnifiedTakesCaptureTests.RadioKey(index), UnifiedTakesCaptureTests.Sequence(index),
                arrival, Placed));

        public void Range(int from, int to) { for (int i = from; i < to; i++) Offer(i); }

        public PlacedPacket<int> GapAt(int index) => Placed.Single(p => p.Item == index);
        public long TotalMissing => Placed.Sum(p => p.MissingBefore);
    }

    [Fact]
    public void A_stream_with_nothing_missing_places_every_packet_with_no_gap_across_second_boundaries()
    {
        var run = new Run();
        run.Range(0, 400);
        run.Model.Flush(run.Placed);
        Assert.Equal(400, run.Placed.Count);
        Assert.Equal(0, run.TotalMissing);
        Assert.Equal(0, run.Model.Witness().UnaccountedShortSeconds);
        Assert.Equal(100, run.Model.Witness().PeakPerSecond);
    }

    [Fact]
    public void A_loss_under_sixteen_is_the_counters_exactly()
    {
        var run = new Run();
        run.Range(0, 10);
        run.Range(15, 30);
        Assert.Equal(5, run.GapAt(15).MissingBefore);
        Assert.Equal("counter", run.GapAt(15).Basis);
        Assert.Equal(5, run.TotalMissing);
    }

    [Fact]
    public void A_loss_of_exactly_sixteen_is_found_by_arrival_once_the_next_packet_comes_at_the_radios_pace()
    {
        var run = new Run();
        run.Range(0, 20);
        run.Offer(36);
        Assert.DoesNotContain(run.Placed, p => p.Item == 36); // held: a stall and a loss look alike at this packet
        run.Offer(37);
        Assert.Equal(16, run.GapAt(36).MissingBefore);
        Assert.Equal("counter-and-arrival-estimate", run.GapAt(36).Basis);
    }

    [Fact]
    public void A_stall_that_drains_in_a_burst_is_not_a_loss()
    {
        var run = new Run();
        run.Range(0, 30);
        for (int i = 30; i < 50; i++) run.OfferAt(i, UnifiedTakesCaptureTests.Arrival(49) + (i - 30) * 100);
        run.Range(50, 60);
        Assert.Equal(0, run.TotalMissing);
        Assert.Equal(1, run.Model.StallsSeen);
        Assert.Equal(60, run.Placed.Count);
    }

    [Fact]
    public void A_stall_that_also_lost_packets_keeps_the_counters_reading()
    {
        // FlexLib clearing its queue under a stall: 20 lost (residue 4), the rest in a burst.
        var run = new Run();
        run.Range(0, 30);
        for (int i = 50; i < 60; i++) run.OfferAt(i, UnifiedTakesCaptureTests.Arrival(59) + (i - 50) * 100);
        run.Range(60, 70);
        Assert.Equal(4, run.GapAt(50).MissingBefore);
        Assert.Equal("counter", run.GapAt(50).Basis);
    }

    [Fact]
    public void Whole_seconds_skipped_bound_the_count_and_arrival_settles_it()
    {
        // 250 lost: the key skips two seconds, the counter's residue is 10.
        var run = new Run();
        run.Range(0, 10);
        run.Range(260, 270);
        Assert.Equal(250, run.GapAt(260).MissingBefore);
        Assert.Equal("counter-and-arrival-estimate", run.GapAt(260).Basis);
    }

    [Fact]
    public void Whole_seconds_skipped_under_a_stall_give_at_least_the_skipped_seconds()
    {
        var run = new Run();
        run.Range(0, 10);
        for (int i = 260; i < 270; i++) run.OfferAt(i, UnifiedTakesCaptureTests.Arrival(269) + (i - 260) * 100);
        Assert.Equal(106, run.GapAt(260).MissingBefore);    // one whole second (100) plus the residue's next value
        Assert.Equal("counter-and-utc-second", run.GapAt(260).Basis);
    }

    [Fact]
    public void A_held_packet_the_take_closes_on_keeps_the_counters_reading_and_says_it_was_unresolved()
    {
        var run = new Run();
        run.Range(0, 20);
        run.Offer(36);
        run.Model.Flush(run.Placed);
        Assert.Equal(0, run.GapAt(36).MissingBefore);
        Assert.Equal(21, run.Placed.Count);
        run.Offer(37);
        Assert.Equal(PacketOutcome.Taken, run.Outcomes.Last());
    }

    [Fact]
    public void A_repeat_or_a_late_packet_is_dropped_and_a_long_step_back_is_a_discontinuity()
    {
        var run = new Run();
        run.Range(300, 310);
        run.Offer(309, 3);
        run.Offer(305, 4);
        Assert.Equal(new[] { PacketOutcome.Late, PacketOutcome.Late }, run.Outcomes.Skip(10));
        Assert.Equal(2, run.Model.LatePackets);
        run.Offer(0);   // three seconds back
        Assert.Equal(PacketOutcome.Discontinuity, run.Outcomes.Last());
        Assert.Equal("clock-stepped-back", run.Model.DiscontinuityReason);
    }

    [Fact]
    public void Five_whole_seconds_missing_is_a_discontinuity_and_four_is_a_gap()
    {
        var gap = new Run();
        gap.Range(0, 5);
        gap.Range(463, 470);           // slot 41 to 500: the key moves from second 0 to second 5
        Assert.Equal(PacketOutcome.Taken, gap.Outcomes[5]);
        Assert.True(gap.GapAt(463).MissingBefore >= 400);
        var jump = new Run();
        jump.Range(0, 5);
        jump.Offer(564);               // slot 601: second 6, five whole seconds missing
        Assert.Equal(PacketOutcome.Discontinuity, jump.Outcomes.Last());
        Assert.Equal("clock-discontinuity", jump.Model.DiscontinuityReason);
    }

    [Fact]
    public void A_loss_nothing_could_place_is_counted_as_a_short_second()
    {
        var run = new Run();
        run.Range(0, 200);
        for (int i = 232; i < 242; i++) run.OfferAt(i, UnifiedTakesCaptureTests.Arrival(241) + (i - 232) * 100);
        run.Range(242, 450);
        Assert.Equal(0, run.TotalMissing);
        var witness = run.Model.Witness();
        Assert.Equal(100, witness.PeakPerSecond);
        Assert.Equal(1, witness.UnaccountedShortSeconds);
    }

    [Fact]
    public void A_short_second_with_a_gap_placed_in_it_is_already_accounted_for()
    {
        var run = new Run();
        run.Range(0, 200);
        run.Range(205, 450);
        Assert.Equal(5, run.TotalMissing);
        Assert.Equal(0, run.Model.Witness().UnaccountedShortSeconds);
    }

    // ── Part 5d: real per-second jitter is not a loss; a hidden 16 still is ──

    private static Run Keys(double[] keys, int dropFrom = -1, int dropCount = 0, bool stallAfterDrop = false)
    {
        var run = new Run();
        int next = dropFrom + dropCount;
        for (int i = 0; i < keys.Length; i++)
        {
            if (i >= dropFrom && i < next) continue;
            long arrival = stallAfterDrop && i >= next && i < next + 10
                ? UnifiedTakesCaptureTests.Arrival(next + 9) + (i - next) * 100
                : UnifiedTakesCaptureTests.Arrival(i);
            run.Outcomes.Add(run.Model.Offer(i, keys[i], UnifiedTakesCaptureTests.Sequence(i), arrival, run.Placed));
        }
        run.Model.Flush(run.Placed);
        return run;
    }

    [Fact]
    public void Seconds_holding_99_100_or_101_packets_of_a_whole_stream_are_not_a_loss()
    {
        var run = Keys(UnifiedTakesCaptureTests.KeysForSeconds(UnifiedTakesCaptureTests.LanSeconds));
        Assert.Equal(0, run.TotalMissing);
        var witness = run.Model.Witness();
        Assert.Equal(101, witness.PeakPerSecond);
        Assert.Equal(9, witness.CompleteSeconds);
        Assert.Equal(0, witness.UnaccountedShortSeconds);
    }

    [Fact]
    public void Seconds_alternating_100_and_101_packets_are_not_a_loss()
    {
        var run = Keys(UnifiedTakesCaptureTests.KeysForSeconds(50, 100, 101, 100, 101, 100, 101, 30));
        Assert.Equal(0, run.Model.Witness().UnaccountedShortSeconds);
    }

    [Fact]
    public void A_hidden_loss_of_exactly_sixteen_split_across_a_second_boundary_still_counts_on_jittery_seconds()
    {
        // LAN-shaped seconds; 16 packets lost under a stall (so the counter reads
        // contiguous and nothing is placed), eight each side of the boundary
        // between the fourth and fifth seconds, each of which jittered up by one.
        int[] seconds = { 63, 100, 99, 100, 101, 101, 100, 99, 41 };
        double[] keys = UnifiedTakesCaptureTests.KeysForSeconds(seconds);
        int boundary = 63 + 100 + 99 + 100 + 101;
        var run = Keys(keys, dropFrom: boundary - 8, dropCount: 16, stallAfterDrop: true);
        Assert.Equal(0, run.TotalMissing);
        Assert.True(run.Model.Witness().UnaccountedShortSeconds > 0, "a hidden loss of 16 went uncounted");
    }
}
