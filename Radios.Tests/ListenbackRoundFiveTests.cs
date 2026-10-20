using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Flex.Smoothlake.Vita;
using Radios.Listenback;
using Radios.Tests.StationConnect;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ListenbackRoundFiveTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ListenbackRoundFiveTests));
    public void Dispose() => _scope.Dispose();

    [Fact]
    public async Task Full_duplex_toggle_reads_before_and_after_a_no_echo_write()
    {
        using var c = new RadioCase();
        var radio = c.F.Vendor.Radio;
        c.F.Rig.WireStationHandlers(radio);
        c.F.Vendor.Status("radio full_duplex_enabled=0");
        c.F.Rig.ListenbackConfirmationWait = TimeSpan.FromMilliseconds(100);
        bool actual = false;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("radio set full_duplex_enabled="))
                actual = command.EndsWith("=1", StringComparison.Ordinal);
            else if (command == "sub radio all")
                c.F.Vendor.Status("radio full_duplex_enabled=" + (actual ? "1" : "0"));
            c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
        };
        Assert.Equal("audio.full_duplex.on", await c.Pump(c.F.Rig.ToggleFullDuplexAsync()));
        Assert.True(actual); Assert.True(c.F.Rig.FullDuplexEnabled);
        Assert.Equal("audio.full_duplex.off", await c.Pump(c.F.Rig.ToggleFullDuplexAsync()));
        Assert.False(actual); Assert.False(c.F.Rig.FullDuplexEnabled);
        Assert.Equal(4, c.F.Vendor.Transport.Commands.Count(x => x == "sub radio all"));
    }

    [Fact]
    public async Task Restore_full_duplex_uses_a_new_readback_after_the_success_reply()
    {
        using var c = new RadioCase();
        var snapshot = c.Snapshot();
        snapshot.TakeDirectory = Path.Combine(RadioConfig.AppDataRoot, "round-five-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshot.TakeDirectory); c.Store.Save(snapshot);
        // The bench client still showed off while the radio was on.
        Field(c.F.Vendor.Radio, "_fullDuplexEnabled", false);
        c.F.Rig.ListenbackConfirmationWait = TimeSpan.FromMilliseconds(100);
        bool actual = true;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("radio set full_duplex_enabled="))
            {
                actual = command.EndsWith("=1", StringComparison.Ordinal);
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence); // no status echo
            }
            else if (command == "sub radio all")
            {
                c.F.Vendor.Status("radio full_duplex_enabled=" + (actual ? "1" : "0"));
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            }
            else c.Respond(command);
        };
        Assert.True(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(snapshot, true)));
        Assert.False(actual); Assert.False(c.F.Rig.FullDuplexEnabled);
        string trace = string.Join("\n", Directory.GetFiles(snapshot.TakeDirectory, "restore-*.txt").Select(File.ReadAllText));
        Assert.Contains("readback", trace);
        Assert.Contains("full-duplex", trace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_listener_tune_sends_raw_commands_and_records_a_refusal(bool refuseSliceTune)
    {
        using var c = new RadioCase(); var radio = c.F.Vendor.Radio;
        Field(c.Tx, "_freq", 21.195); // bench: TX is at 21.195, new listener starts at 21.275
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create", StringComparison.Ordinal))
            {
                c.Ears = c.Add(0, 21.275);
                c.F.Vendor.Status($"slice 0 in_use=1 client_handle=0x{radio.ClientHandle:X} pan=0x40000000 RF_frequency=21.275 mode=USB rxant=ANT1");
                c.F.Vendor.Status($"display pan 0x40000000 client_handle=0x{radio.ClientHandle:X} center=21.275 rxant=ANT1");
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else if (command == "slice tune 0 21.195000")
            {
                if (refuseSliceTune) c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000001, "refused");
                else
                {
                    c.F.Vendor.Status($"slice 0 in_use=1 client_handle=0x{radio.ClientHandle:X} pan=0x40000000 RF_frequency=21.195000 mode=USB rxant=ANT1");
                    c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
                }
            }
            else if (command.StartsWith("stream create type=dax_iq", StringComparison.Ordinal))
                c.F.Vendor.Status($"stream 0x20000000 type=dax_iq client_handle=0x{radio.ClientHandle:X} daxiq_channel=1 daxiq_rate=48000 active=1 pan=0x0 payload_endian=little");
            else
            {
                c.Respond(command);
                if (c.Ears != null && command == "display pan set 0x40000000 daxiq_channel=1")
                {
                    c.F.Vendor.Status($"stream 0x20000000 type=dax_iq client_handle=0x{radio.ClientHandle:X} daxiq_channel=1 daxiq_rate=24000 active=1 pan=0x40000000 payload_endian=little");
                    var stream = radio.DAXIQStreamList.Single();
                    var random = new Random(59);
                    var packet = new VitaIFDataPacket { StreamId = stream.StreamID, TimestampInt = 100,
                        payload = Enumerable.Range(0, 16384).Select(_ => random.Next(-30, 31) * 16f / 32768).ToArray() };
                    packet.ClassId.PacketClassCode = 0x02e3;
                    var dispatch = typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { typeof(VitaIFDataPacket) }, null)!;
                    dispatch.Invoke(stream, new object[] { packet });
                    packet.Header.packet_count = 1; packet.TimestampFrac = 341333333333;
                    dispatch.Invoke(stream, new object[] { packet });
                }
            }
        };
        string root = Path.Combine(RadioConfig.AppDataRoot, "Recordings", "takes");
        var before = Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>();
        if (refuseSliceTune)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => c.Pump(c.F.Rig.StartListenbackAsync(
                new ListenbackOptions(SeparatePan: true), default)));
            Assert.Equal("listener-command-refused", error.Message);
            Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.StartsWith("stream create", StringComparison.Ordinal));
        }
        else
        {
            var run = await c.Pump(c.F.Rig.StartListenbackAsync(new ListenbackOptions(SeparatePan: true), default));
            Assert.Equal(21.195, c.Ears.Freq, 6);
            Assert.Equal(21.195, c.Ears.Panadapter.CenterFreq, 6);
            await c.Pump(Stop(run));
            Assert.True(run.Restored);
        }
        Assert.Contains("slice tune 0 21.195000", c.F.Vendor.Transport.Commands);
        string dir = Directory.GetDirectories(root).Except(before).Single();
        string header = File.ReadAllText(Path.Combine(dir, "take.txt"));
        Assert.Contains("listener-command-reply", header);
        Assert.Contains(refuseSliceTune ? "50000001" : "pan-frequency", header);
        if (!refuseSliceTune) Assert.Contains("confirmed-created-resources", header);
    }

    private static async Task<bool> Stop(ListenbackRun run)
    { await run.StopAsync(); return run.Restored; }

    [Fact]
    public async Task Loopback_check_waits_for_full_duplex_readback_and_restores_it_without_an_echo()
    {
        using var c = new RadioCase();
        bool actual = false;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("radio set full_duplex_enabled=", StringComparison.Ordinal))
            {
                actual = command.EndsWith("=1", StringComparison.Ordinal);
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence); // bench: no setter echo
            }
            else if (command == "sub radio all")
            {
                c.F.Vendor.Status("radio full_duplex_enabled=" + (actual ? "1" : "0"));
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            }
            else if (command.StartsWith("display panafall create", StringComparison.Ordinal))
            {
                c.Ears = c.Add(0, 1.8);
                Field(c.F.Rig, "mySliceAdded", true);
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else c.Respond(command);
        };
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        Assert.True(actual);
        Assert.True(c.F.Rig.FullDuplexEnabled);
        Assert.Equal("", await c.Pump(c.F.Rig.EndLoopbackArrangementAsync()));
        Assert.False(actual);
        Assert.False(c.F.Rig.FullDuplexEnabled);
        Assert.Equal(3, c.F.Vendor.Transport.Commands.Count(x => x == "sub radio all"));
    }

    [Fact]
    public async Task Keyed_restore_settings_can_be_proved_by_fresh_readback_without_setter_echoes()
    {
        using var c = new RadioCase(); var snapshot = c.Snapshot();
        snapshot.Settings.Add(new("xvtr-drive", "-1", "xvtr set 0 max_power=-1", "xvtr 0", "max_power"));
        string antenna = "XVT-A"; int power = 1; bool monitor = true;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "slice set 1 txant=ANT1") { antenna = "ANT1"; c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence); }
            else if (command == "transmit set rfpower=20") { power = 20; c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence); }
            else if (command == "transmit set mon=0") { monitor = false; c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence); }
            else if (command == "sub slice all")
            {
                c.F.Vendor.Status($"slice 1 txant={antenna}");
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            }
            else if (command == "sub tx all")
            {
                c.F.Vendor.Status($"transmit rfpower={power} sb_monitor={(monitor ? 1 : 0)}");
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            }
            else if (command == "xvtr set 0 max_power=-1") c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            else if (command == "sub xvtr all")
            {
                c.F.Vendor.Status("xvtr 0 max_power=-1");
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            }
            else c.Respond(command);
        };
        Assert.True(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(snapshot, true)));
        Assert.Equal("ANT1", antenna); Assert.Equal(20, power); Assert.False(monitor);
        Assert.Contains("sub slice all", c.F.Vendor.Transport.Commands);
        Assert.Contains("sub tx all", c.F.Vendor.Transport.Commands);
        Assert.Contains("sub xvtr all", c.F.Vendor.Transport.Commands);
    }
}
