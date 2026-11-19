using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track N2 repair 2: Codex's re-check of e2b069d8, R1. A memory
/// recall is bound to the operator's action, and a tune between that action and
/// the deferred landing retires it even when the radio's reported state shows
/// no trace of the tune. On a real FlexBase over vendor objects (the status
/// bench of repair 1: no socket, isolated settings, globals restored);
/// windowless on the MTA test thread, asserted by the class constructor.
/// </summary>
public sealed partial class ReceiveWalkFollowUpTests
{
    [Fact]
    public void Every_receive_frequency_request_is_counted_and_a_radio_report_is_not()
    {
        using var bench = NewStatusBench(out var rig);
        int start = rig.RXFrequencyRequests;
        rig.RXFrequency = 14_200_000UL;
        Assert.Equal(start + 1, rig.RXFrequencyRequests);
        rig.Frequency = 14_250_000UL;
        Assert.Equal(start + 2, rig.RXFrequencyRequests);
        // The radio's own report writes the cache, not the setter: not a request.
        Put(rig, "_RXFrequency", 7_030_000UL);
        Assert.Equal(start + 2, rig.RXFrequencyRequests);
    }

    [Fact]
    public void A_tune_before_the_landing_retires_the_recall_even_when_the_radio_shows_no_trace_of_it()
    {
        // Codex's first R1 ordering, in its hardest form: recall 7.030 CW at
        // 14.100 USB; a tune writes 14.200 before the idle landing; then the
        // radio's report of the recall's own frequency overwrites the tune's
        // cached value. The reading at the landing (7.030 USB) is on the
        // recall's way, so only the count of requests can see the tune.
        using var bench = NewStatusBench(out var rig);
        Put(rig, "_RXFrequency", 14_100_000UL);
        Put(rig, "_RXMode", "USB");
        RecallMarks MarksNow() => new(rig, rig.RXSliceIdentity, 4, rig.RXFrequencyRequests, rig.RXSliceChoices);
        var recall = new PendingRecall(7_030_000, "CW", RadioStatusBuilder.BuildDetailedStatus(rig), 1000, MarksNow());

        // Positive control: nothing intervened, the landing names the recall.
        var untouched = recall.AskIfStillOwned(MarksNow, () => 1000);
        Assert.NotNull(untouched);
        Assert.Contains("7.030", RadioStatusBuilder.CaptureSpokenStatus(rig, untouched).Text);

        rig.RXFrequency = 14_200_000UL;               // the tune
        Put(rig, "_RXFrequency", 7_030_000UL);         // the recall's report overwrites it
        Assert.Null(recall.AskIfStillOwned(MarksNow, () => 1000));
        Assert.False(untouched!.StillPending(), "an ask taken before the tune stops being pending");
        var landing = RadioStatusBuilder.CaptureSpokenStatus(rig, recall.AskIfStillOwned(MarksNow, () => 1000));
        Assert.Equal(RadioStatusBuilder.BuildSpokenStatus(rig), landing.Text);
    }
}
