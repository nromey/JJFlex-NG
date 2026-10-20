using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Radios.Tests.StationConnect;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ListenbackRoundSixTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ListenbackRoundSixTests));
    public void Dispose() => _scope.Dispose();

    [Fact]
    public async Task Control_confirms_both_directions_from_reply_zero_without_a_same_connection_radio_status()
    {
        using var c = new RadioCase();
        c.F.Rig.WireStationHandlers(c.F.Vendor.Radio);
        c.F.Vendor.Status("radio full_duplex_enabled=0");
        c.F.Rig.ListenbackConfirmationWait = TimeSpan.FromMilliseconds(100);
        bool actual = false;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("radio set full_duplex_enabled=", StringComparison.Ordinal))
                actual = command.EndsWith("=1", StringComparison.Ordinal);
            c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence); // repeated sub radio all would return no status
        };
        Assert.Equal("audio.full_duplex.on", await c.Pump(c.F.Rig.ToggleFullDuplexAsync()));
        Assert.True(actual); Assert.True(c.F.Rig.FullDuplexEnabled);
        Assert.Equal("audio.full_duplex.off", await c.Pump(c.F.Rig.ToggleFullDuplexAsync()));
        Assert.False(actual); Assert.False(c.F.Rig.FullDuplexEnabled);
        Assert.DoesNotContain("sub radio all", c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task Shipped_loopback_check_starts_and_restores_on_reply_zero_without_radio_echo()
    {
        using var c = new RadioCase();
        c.F.Rig.ListenbackConfirmationWait = TimeSpan.FromMilliseconds(100);
        bool actual = false;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("radio set full_duplex_enabled=", StringComparison.Ordinal))
                actual = command.EndsWith("=1", StringComparison.Ordinal);
            else if (command.StartsWith("display panafall create", StringComparison.Ordinal))
            {
                c.Ears = c.Add(0, 1.8);
                Field(c.F.Rig, "mySliceAdded", true);
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
            }
            else if (!command.StartsWith("sub radio all", StringComparison.Ordinal)) c.Respond(command);
            c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
        };
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        Assert.True(actual); Assert.True(c.F.Rig.FullDuplexEnabled);
        Assert.Equal("", await c.Pump(c.F.Rig.EndLoopbackArrangementAsync()));
        Assert.False(actual); Assert.False(c.F.Rig.FullDuplexEnabled);
        Assert.DoesNotContain("sub radio all", c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task Lan_check_follows_the_control_result_and_updates_only_on_a_disagreement()
    {
        using var c = new RadioCase();
        var radio = c.F.Vendor.Radio;
        Property(radio, "IP", IPAddress.Loopback); // query is injected: no socket is opened
        c.F.Rig.WireStationHandlers(radio);
        c.F.Vendor.Status("radio full_duplex_enabled=0");
        c.F.Vendor.Transport.CommandWritten = _ => c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
        int queries = 0;
        bool actual = false;
        c.F.Rig.FullDuplexLanQuery = (_, _) => { queries++; return Task.FromResult<bool?>(actual); };

        Assert.Equal("audio.full_duplex.on", await c.Pump(c.F.Rig.ToggleFullDuplexAsync()));
        Assert.True(c.F.Rig.FullDuplexEnabled);
        Assert.Equal(0, queries); // the UI has not spoken the success result yet
        c.F.Rig.BeginFullDuplexLanCheck();
        await c.F.Rig.FullDuplexLanCheckTask;
        Assert.Equal(1, queries);
        Assert.False(c.F.Rig.FullDuplexEnabled); // disagreement corrects the displayed getter
        c.F.Rig.BeginFullDuplexLanCheck();
        Assert.Equal(1, queries); // no duplicate check for this decision

        actual = true;
        Assert.Equal("audio.full_duplex.on", await c.Pump(c.F.Rig.ToggleFullDuplexAsync()));
        c.F.Rig.BeginFullDuplexLanCheck();
        await c.F.Rig.FullDuplexLanCheckTask;
        Assert.Equal(2, queries);
        Assert.True(c.F.Rig.FullDuplexEnabled); // agreement leaves the accepted state alone
    }

    [Fact]
    public async Task SmartLink_skips_the_separate_lan_check()
    {
        using var c = new RadioCase();
        var radio = c.F.Vendor.Radio;
        Property(radio, "IP", IPAddress.Loopback);
        Property(radio, "IsWan", true);
        c.F.Rig.WireStationHandlers(radio);
        c.F.Vendor.Status("radio full_duplex_enabled=0");
        c.F.Vendor.Transport.CommandWritten = _ => c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
        int queries = 0;
        c.F.Rig.FullDuplexLanQuery = (_, _) => { queries++; return Task.FromResult<bool?>(false); };
        Assert.Equal("audio.full_duplex.on", await c.Pump(c.F.Rig.ToggleFullDuplexAsync()));
        c.F.Rig.BeginFullDuplexLanCheck();
        await c.F.Rig.FullDuplexLanCheckTask;
        Assert.Equal(0, queries);
        Assert.True(c.F.Rig.FullDuplexEnabled);
    }

    [Fact]
    public void Lan_probe_accepts_only_a_radio_status_with_a_binary_full_duplex_value()
    {
        Assert.True(FullDuplexLanProbe.Parse("S0|radio full_duplex_enabled=1 nickname=bench"));
        Assert.False(FullDuplexLanProbe.Parse("S0|radio full_duplex_enabled=0"));
        Assert.Null(FullDuplexLanProbe.Parse("R1|0|"));
        Assert.Null(FullDuplexLanProbe.Parse("S0|transmit full_duplex_enabled=1"));
        Assert.Null(FullDuplexLanProbe.Parse("S0|radio full_duplex_enabled=unknown"));
    }
}
