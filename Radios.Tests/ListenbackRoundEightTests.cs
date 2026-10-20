using System;
using System.Collections.Generic;
using System.Linq;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ListenbackRoundEightTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ListenbackRoundEightTests));
    public void Dispose() => _scope.Dispose();

    [Fact]
    public async System.Threading.Tasks.Task Active_slice_record_and_play_use_reply_tracked_commands_and_refuse_transmit()
    {
        using var c = new RadioCase();
        var recording = await c.Pump(c.F.Rig.ToggleWorkshopSliceRecordAsync(false));
        Assert.True(recording.Success, recording.Message);
        Assert.Contains("received audio on active slice 1", recording.Message);
        Assert.Contains("slice set 1 record=1", c.F.Vendor.Transport.Commands);
        Assert.True(c.F.Rig.WorkshopSliceRecording);
        Assert.True(await c.Pump(c.F.Rig.StopWorkshopSliceRecorderAsync()));
        Assert.Contains("slice set 1 record=0", c.F.Vendor.Transport.Commands);
        Assert.False(c.F.Rig.WorkshopSliceRecording);

        Field(c.F.Rig, "_Transmit", true);
        var refused = await c.Pump(c.F.Rig.ToggleWorkshopSlicePlayAsync(false));
        Assert.False(refused.Success);
        Assert.Contains("while transmitting", refused.Message);
        Assert.DoesNotContain("slice set 1 play=1", c.F.Vendor.Transport.Commands);
        Field(c.F.Rig, "_Transmit", false);
        c.F.Vendor.Status("slice 1 play=0");
        Assert.True(c.F.Rig.WorkshopSlicePlayEnabled(false));
        var playing = await c.Pump(c.F.Rig.ToggleWorkshopSlicePlayAsync(false));
        Assert.True(playing.Success, playing.Message);
        Assert.Contains("slice set 1 play=1", c.F.Vendor.Transport.Commands);
        c.F.Vendor.Status("slice 1 play=0"); // the radio finished its buffer on its own
        Assert.False(c.F.Rig.WorkshopSlicePlaying);
        Assert.True((await c.Pump(c.F.Rig.ToggleWorkshopSlicePlayAsync(false))).Success);
        Assert.Equal(2, c.F.Vendor.Transport.Commands.Count(x => x == "slice set 1 play=1"));
        Assert.True(await c.Pump(c.F.Rig.StopWorkshopSliceRecorderAsync()));
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
        var result = await c.Pump(c.F.Rig.ToggleWorkshopSliceRecordAsync(true));
        Assert.True(result.Success, result.Message);
        Assert.Contains("sent audio heard off the air on Loopback listening slice 0", result.Message);
        Assert.Contains("slice set 0 record=1", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("slice set 1 record=1", c.F.Vendor.Transport.Commands);
        Assert.True(await c.Pump(c.F.Rig.StopWorkshopSliceRecorderAsync()));
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
        Assert.True((await c.Pump(c.F.Rig.ToggleWorkshopSliceRecordAsync(false))).Success);
        Assert.False(await c.Pump(c.F.Rig.StopWorkshopSliceRecorderAsync()));
        Assert.True(c.F.Rig.WorkshopSliceRecording);
        rejectStop = false;
        Assert.True(await c.Pump(c.F.Rig.StopWorkshopSliceRecorderAsync()));
        Assert.False(c.F.Rig.WorkshopSliceRecording);
        Assert.Equal(2, c.F.Vendor.Transport.Commands.Count(x => x == "slice set 1 record=0"));
    }
}
