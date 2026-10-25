using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Radios.Listenback;
using Radios.Tests.StationConnect;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ListenbackRoundSevenTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ListenbackRoundSevenTests));
    public void Dispose() => _scope.Dispose();
    private static float[] Noise(int pairs)
    {
        var random = new Random(4567);
        return Enumerable.Range(0, pairs * 2).Select(_ => random.Next(-30, 31) * 16f / 32768).ToArray();
    }

    private static IqPacketHeader First => new(0x20000000, 0, 739, 1, 2,
        1792448344, 891792219912, 20158426792920);

    private static IqPacketHeader Next(IqPacketHeader previous, int elapsedPairs)
    {
        ulong ticks = previous.Fraction + (ulong)Math.Round(elapsedPairs * 1e12 / previous.Rate);
        return previous with
        {
            Count = (byte)((previous.Count + 1) % 16),
            Seconds = previous.Seconds + (uint)(ticks / 1_000_000_000_000UL),
            Fraction = ticks % 1_000_000_000_000UL,
            ArrivalTicks = previous.ArrivalTicks + (long)Math.Round(elapsedPairs * (double)Stopwatch.Frequency / previous.Rate)
        };
    }

    private static TakeInspection Capture(int firstPairs, int secondStepPairs, int secondPairs = 8192)
    {
        string directory = Path.Combine(Path.GetTempPath(), "jjflex-round7-" + Guid.NewGuid().ToString("N"));
        using var writer = new IqTakeWriter(directory, 24000, new Dictionary<string, string>())
        { Active = true, EchoedRate = 24000 };
        var first = First;
        writer.Accept(new(first, Noise(firstPairs), 0, 0));
        writer.Accept(new(Next(first, secondStepPairs), Noise(secondPairs), 0, 0));
        writer.Finish("operator-stop");
        return TakeReader.Inspect(directory);
    }

    [Theory]
    [InlineData(new[] { "ANT1", "XVT-A", "XVT-B" }, "XVT-A", "XVT-B")]
    [InlineData(new[] { "ANT1", "xvt-b", "XVT-A" }, "XVT-A", "xvt-b")]
    [InlineData(new[] { "xvt-a", "XVT-B" }, "XVT-A", "xvt-a")]
    [InlineData(new[] { "ANT1", "XVT-A" }, "XVT-A", null)]
    public void Shared_chooser_keeps_the_listenback_order_and_null_case(string[] receive, string transmit, string? expected)
    {
        Assert.Equal(expected, FlexBase.OtherTransverterReceiveInput(receive, transmit));
    }

    [Fact]
    public void Exact_four_pair_gap_at_the_first_128_pair_boundary_is_a_clean_startup_signature()
    {
        var take = Capture(128, 132);
        Assert.Equal("clean", take.State);
        Assert.Equal("1", take.Get("gaps"));
        Assert.Equal("4", take.Get("fill-pairs"));
        Assert.Equal("detected", take.Get("startup-signature"));
        Assert.Single(take.Fields.Where(f => f.Key == "startup-signature-gap"));
    }

    [Theory]
    [InlineData(128, 133)]
    [InlineData(176, 180)]
    public void A_different_gap_size_or_boundary_remains_incomplete(int firstPairs, int secondStepPairs)
    {
        var take = Capture(firstPairs, secondStepPairs);
        Assert.Equal("incomplete", take.State);
        Assert.Null(take.Get("startup-signature"));
    }

    [Fact]
    public void An_inexact_four_pair_fill_at_the_first_boundary_is_not_the_signature()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jjflex-round7-" + Guid.NewGuid().ToString("N"));
        using var writer = new IqTakeWriter(directory, 24000, new Dictionary<string, string>())
        { Active = true, EchoedRate = 24000 };
        var first = First;
        var second = Next(first, 132) with
        { Fraction = first.Fraction + (ulong)Math.Round(132.4 * 1e12 / first.Rate) };
        writer.Accept(new(first, Noise(128), 0, 0));
        writer.Accept(new(second, Noise(8192), 0, 0));
        writer.Finish("operator-stop");
        var take = TakeReader.Inspect(directory);
        Assert.Equal("incomplete", take.State);
        Assert.Equal("4", take.Get("fill-pairs"));
        Assert.Null(take.Get("startup-signature"));
    }

    [Fact]
    public void A_second_gap_after_the_startup_signature_still_makes_the_take_incomplete()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jjflex-round7-" + Guid.NewGuid().ToString("N"));
        using var writer = new IqTakeWriter(directory, 24000, new Dictionary<string, string>())
        { Active = true, EchoedRate = 24000 };
        var first = First;
        var second = Next(first, 132);
        writer.Accept(new(first, Noise(128), 0, 0));
        writer.Accept(new(second, Noise(8192), 0, 0));
        writer.Accept(new(Next(second, 8193), Noise(128), 0, 0));
        writer.Finish("operator-stop");
        var take = TakeReader.Inspect(directory);
        Assert.Equal("detected", take.Get("startup-signature"));
        Assert.Equal("2", take.Get("gaps"));
        Assert.Equal("incomplete", take.State);
    }

    [Fact]
    public async Task A_check_created_listener_does_not_mark_the_slice_change_as_operator_initiated()
    {
        using var c = new RadioCase();
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create", StringComparison.Ordinal))
            {
                c.Ears = c.Add(0, 1.8);
                Field(c.F.Rig, "mySliceAdded", true);
                Field(c.F.Rig, "_RXVFO", 1);
                Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else c.Respond(command);
            c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
        };
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        var changed = typeof(FlexBase).GetField("_operatorChangedSliceSet", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False((bool)changed.GetValue(c.F.Rig)!);

        Assert.True(c.F.Rig.NewSlice()); // positive control: the operator route still marks its own change
        Assert.True((bool)changed.GetValue(c.F.Rig)!);
    }

    [Fact]
    public async Task Loopback_uses_the_other_port_and_restores_owned_receive_mutes()
    {
        using var c = new RadioCase();
        Field(c.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
        var alreadyMuted = c.Add(2, 7.1);
        var audible = c.Add(3, 3.6);
        Field(alreadyMuted, "_mute", true);
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create", StringComparison.Ordinal))
            {
                c.Ears = c.Add(0, 1.8);
                Field(c.F.Rig, "mySliceAdded", true);
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else c.Respond(command);
        };
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        Assert.Equal("XVT-B", c.Ears.RXAnt);
        Assert.True(c.Tx.Mute);
        Assert.True(alreadyMuted.Mute);
        Assert.True(audible.Mute);
        Assert.Contains("slice set 0 rfgain=32", c.F.Vendor.Transport.Commands);
        Assert.Equal("", await c.Pump(c.F.Rig.EndLoopbackArrangementAsync()));
        Assert.False(c.Tx.Mute);
        Assert.True(alreadyMuted.Mute);
        Assert.False(audible.Mute);
    }

    [Fact]
    public async Task Loopback_refuses_before_any_radio_write_without_the_other_receive_port()
    {
        using var c = new RadioCase();
        Field(c.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A" });
        c.F.Vendor.Transport.Written.Clear();
        Assert.Contains("second transverter", c.F.Rig.LoopbackUnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        Assert.Empty(c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task Off_air_listenback_and_Loopback_refuse_the_same_missing_port()
    {
        using var c = new RadioCase();
        Field(c.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A" });
        Assert.False(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.F.Rig.StartListenbackAsync(new(OffAir: true), default));
        Assert.Equal("no-other-transverter-input", error.Message);
    }

    [Fact]
    public async Task Rejected_ears_gain_prevents_readiness_and_restores_the_muted_slice()
    {
        using var c = new RadioCase();
        var second = c.Add(2, 7.1);
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create", StringComparison.Ordinal))
            {
                c.Ears = c.Add(0, 1.8);
                Field(c.F.Rig, "mySliceAdded", true);
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else if (command == "slice set 0 rfgain=32")
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000001, "refused");
            else c.Respond(command);
        };
        Assert.False(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        Assert.False(c.F.Rig.LoopbackArranged);
        Assert.False(c.Tx.Mute);
        Assert.False(second.Mute);
        Assert.Contains("slice set 1 audio_mute=0", c.F.Vendor.Transport.Commands);
        Assert.Contains("slice set 2 audio_mute=0", c.F.Vendor.Transport.Commands);
        Assert.Contains("slice remove 0", c.F.Vendor.Transport.Commands);
    }
}
