using System;
using System.Collections.Generic;
using System.Linq;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// Round 8 of #456 added the Workshop's Record slice and Play recording
/// buttons. Since Tape Deck track 2 (#787) they are doors to the ONE recorder
/// surface, so these tests drive that surface the way the buttons do: the
/// active slice normally, the Loopback Check's listening slice while one runs.
/// The guarantees are the round's own and are unchanged — reply-tracked
/// commands, Play refused while transmitting, a rejected stop keeps state for
/// a retry. The wording moved into the lexicon and names the slice by letter.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ListenbackRoundEightTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ListenbackRoundEightTests));
    public void Dispose() => _scope.Dispose();

    [Fact]
    public async System.Threading.Tasks.Task Active_slice_record_and_play_use_reply_tracked_commands_and_refuse_transmit()
    {
        using var c = new RadioCase();
        var recording = await c.Pump(c.F.Rig.ToggleSliceRecordAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(recording.Success, recording.Message);
        Assert.Equal(Lexicon.Get("audio.recorder.recording", ("letter", "B")), recording.Message);
        Assert.Contains("slice set 1 record=1", c.F.Vendor.Transport.Commands);
        Assert.True(c.F.Rig.IsSliceRecording(SliceRecorderTarget.ActiveSlice));
        Assert.True((await c.Pump(c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.Contains("slice set 1 record=0", c.F.Vendor.Transport.Commands);
        Assert.False(c.F.Rig.IsSliceRecording(SliceRecorderTarget.ActiveSlice));

        Field(c.F.Rig, "_Transmit", true);
        c.F.Vendor.Status("slice 1 play=0");
        var refused = await c.Pump(c.F.Rig.ToggleSlicePlayAsync(SliceRecorderTarget.ActiveSlice));
        Assert.False(refused.Success);
        Assert.Equal(Lexicon.Get("audio.recorder.play_refused_transmitting", ("letter", "B")), refused.Message);
        Assert.DoesNotContain("slice set 1 play=1", c.F.Vendor.Transport.Commands);
        Field(c.F.Rig, "_Transmit", false);
        Assert.True(c.F.Rig.SliceHasRecording(SliceRecorderTarget.ActiveSlice));
        var playing = await c.Pump(c.F.Rig.ToggleSlicePlayAsync(SliceRecorderTarget.ActiveSlice));
        Assert.True(playing.Success, playing.Message);
        Assert.Contains("slice set 1 play=1", c.F.Vendor.Transport.Commands);
        c.F.Vendor.Status("slice 1 play=0"); // the radio finished its buffer on its own
        Assert.False(c.F.Rig.IsSlicePlaying(SliceRecorderTarget.ActiveSlice));
        Assert.True((await c.Pump(c.F.Rig.ToggleSlicePlayAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.Equal(2, c.F.Vendor.Transport.Commands.Count(x => x == "slice set 1 play=1"));
        Assert.True(await c.Pump(c.F.Rig.StopSliceRecorderAsync(SliceRecorderTarget.ActiveSlice)));
        Assert.Contains("slice set 1 play=0", c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async System.Threading.Tasks.Task Loopback_control_targets_the_owned_ears_not_the_active_slice()
    {
        using var c = new RadioCase();
        var beforeSlices = c.F.Vendor.Radio.SliceList.ToArray();
        var beforePans = c.F.Vendor.Radio.PanadapterList.ToArray();
        var resources = new LoopbackSliceResources(beforeSlices, beforePans);
        c.Ears = c.Add(0, 14.2);
        Assert.True(resources.Capture(c.F.Vendor.Radio.SliceList, c.F.Vendor.Radio.PanadapterList));
        Field(c.F.Rig, "_lbResources", resources);
        Field(c.F.Rig, "_loopbackArranged", true);
        var result = await c.Pump(c.F.Rig.ToggleSliceRecordAsync(SliceRecorderTarget.LoopbackEars));
        Assert.True(result.Success, result.Message);
        Assert.Equal(Lexicon.Get("audio.recorder.recording_loopback", ("letter", "A")), result.Message);
        Assert.Contains("slice set 0 record=1", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("slice set 1 record=1", c.F.Vendor.Transport.Commands);
        Assert.True(await c.Pump(c.F.Rig.StopSliceRecorderAsync(SliceRecorderTarget.LoopbackEars)));
        Assert.Contains("slice set 0 record=0", c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async System.Threading.Tasks.Task Rejected_slice_stop_retains_ownership_for_a_reply_tracked_retry()
    {
        using var c = new RadioCase();
        bool rejectStop = true;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "slice set 1 record=0" && rejectStop)
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000001, "busy");
            else c.Respond(command);
        };
        Assert.True((await c.Pump(c.F.Rig.ToggleSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        var refused = await c.Pump(c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.ActiveSlice));
        Assert.False(refused.Success);
        Assert.Equal(Lexicon.Get("audio.recorder.record_stop_unconfirmed", ("letter", "B")), refused.Message);
        Assert.True(c.F.Rig.IsSliceRecording(SliceRecorderTarget.ActiveSlice));
        rejectStop = false;
        Assert.True((await c.Pump(c.F.Rig.StopSliceRecordAsync(SliceRecorderTarget.ActiveSlice))).Success);
        Assert.False(c.F.Rig.IsSliceRecording(SliceRecorderTarget.ActiveSlice));
        Assert.Equal(2, c.F.Vendor.Transport.Commands.Count(x => x == "slice set 1 record=0"));
    }
}
