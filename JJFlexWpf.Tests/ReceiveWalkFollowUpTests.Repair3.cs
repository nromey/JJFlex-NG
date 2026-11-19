using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track N2 repair 3: Codex's second re-check of 183c921c, F2. A move
/// to another slice and back inside a memory recall's lifetime restored the
/// slice number, so the recall's marks matched again and the old recall's ask
/// came back. The slice moves are now counted (<see cref="FlexBase.RXSliceChoices"/>,
/// a mark of their own) and a recall found not owning the radio is retired for
/// good. On repair 1's status bench (real FlexBase over vendor objects, no
/// socket, isolated settings, globals restored); windowless on the MTA test
/// thread, asserted by the class constructor.
/// </summary>
public sealed partial class ReceiveWalkFollowUpTests
{
    /// <summary>
    /// Codex's probe (for-claude/2026-11-19-codex-n2-recheck-2-results/
    /// ReceiveWalkFollowUpTests.CodexSliceRoundTrip.cs), both variants: with no
    /// check while away, and with a check that refuses while away. Its controls
    /// are unchanged: the ask is there before the round trip, and the round trip
    /// leaves the frequency requests alone. The one change is that its marks
    /// carry the new fifth mark, the slice moves; run unchanged at 42cdc410,
    /// both variants failed at Assert.Null(revived), as in Codex's run.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Codex_recall_is_retired_after_a_slice_round_trip_even_if_checked_while_away(bool checkWhileAway)
    {
        using var bench = NewStatusBench(out var rig);
        Put(rig, "_RXFrequency", 14_100_000UL);
        Put(rig, "_RXMode", "USB");
        RecallMarks MarksNow() => new(rig, rig.RXSliceIdentity, 4, rig.RXFrequencyRequests, rig.RXSliceChoices);
        int slice = rig.RXVFO;
        int requests = rig.RXFrequencyRequests;
        var recall = new PendingRecall(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), 1000, MarksNow());
        Assert.NotNull(recall.AskIfStillOwned(MarksNow, () => 1000));
        rig.RXVFO = 1;
        if (checkWhileAway) Assert.Null(recall.AskIfStillOwned(MarksNow, () => 1001));
        rig.RXVFO = slice;
        Assert.Equal(requests, rig.RXFrequencyRequests);
        var revived = recall.AskIfStillOwned(MarksNow, () => 1002);
        var landing = RadioStatusBuilder.CaptureSpokenStatus(rig, revived);
        Assert.Null(revived);
        Assert.Equal(RadioStatusBuilder.BuildSpokenStatus(rig), landing.Text);
    }

    [Fact]
    public void Every_receive_slice_move_is_counted_and_a_write_of_the_same_slice_is_not()
    {
        using var bench = NewStatusBench(out var rig);
        int start = rig.RXSliceChoices;
        int slice = rig.RXVFO;
        rig.RXVFO = slice;                       // no move
        Assert.Equal(start, rig.RXSliceChoices);
        rig.RXVFO = 1;
        Assert.Equal(start + 1, rig.RXSliceChoices);
        rig.RXVFO = slice;                       // back: a second move, not an undo
        Assert.Equal(start + 2, rig.RXSliceChoices);
        Assert.Equal(slice, rig.RXVFO);
    }

    [Fact]
    public void A_recall_with_no_slice_move_is_still_the_live_request_after_the_count_exists()
    {
        // The no-intervention control for the round trip: the same recall, the
        // same bench, nothing moved, so the ask is there at every check.
        using var bench = NewStatusBench(out var rig);
        Put(rig, "_RXFrequency", 14_100_000UL);
        Put(rig, "_RXMode", "USB");
        RecallMarks MarksNow() => new(rig, rig.RXSliceIdentity, 4, rig.RXFrequencyRequests, rig.RXSliceChoices);
        var recall = new PendingRecall(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), 1000, MarksNow());
        Assert.NotNull(recall.AskIfStillOwned(MarksNow, () => 1000));
        Assert.NotNull(recall.AskIfStillOwned(MarksNow, () => 1001));
        var ask = recall.AskIfStillOwned(MarksNow, () => 1002);
        Assert.NotNull(ask);
        Assert.Contains("7.030", RadioStatusBuilder.CaptureSpokenStatus(rig, ask).Text);
    }

    [Fact]
    public void The_slice_jump_moves_the_slice_through_the_counted_setter()
    {
        // JumpToSlice is the JJ key's slice jump; it must reach the setter that
        // counts, not the cache field behind it.
        string keys = Source("JJFlexWpf", "KeyCommands.cs");
        string jump = Body(keys, "private void JumpToSlice(int sliceIndex)", "EarconPlayer.ConfirmTone();");
        Assert.Contains("rig.RXVFO = vfo;", jump);
        Assert.DoesNotContain("_RXVFO", jump);
    }
}
