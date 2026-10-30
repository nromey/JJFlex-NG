using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Flex.Smoothlake.Vita;
using Radios.Listenback;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// The off-air take records the listening pan's centre the radio REPORTED
/// after we moved it, never FlexLib's cache from before the move (#835, fix
/// round 2026-10-28).
/// </summary>
/// <remarks>
/// <para>
/// On the bench the listening slice was created at a radio-chosen frequency
/// (21.275) and tuned to the transmit frequency (21.195). The radio reported
/// the pan's move 668 ms later; the take's header had already read
/// <c>pan.CenterFreq</c>, so it said 21.275 and the renderer demodulated 80 kHz
/// off: static. On 40 m the same race gave a detuned take. Re-rendering that
/// take's own IQ with the true centre brought its correlation with what was
/// sent from 0.05 to 0.72, the range of the takes Noel heard.
/// </para>
/// <para>
/// This fake withholds the pan's centre report until the IQ stream has
/// started, which is the late report in miniature: FlexLib's cache keeps the
/// creation frequency until then. What it cannot see: a real radio's timing.
/// </para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ListenbackPanCentreRaceTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ListenbackPanCentreRaceTests));
    public void Dispose() => _scope.Dispose();

    [Fact]
    public async Task A_pan_whose_move_is_reported_late_is_recorded_at_the_centre_the_radio_reported()
    {
        using var c = new RadioCase(); var radio = c.F.Vendor.Radio;
        var packets = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Field(c.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create"))
            {
                // The radio creates the listening slice and pan where IT likes.
                c.Ears = c.Add(0, 1.8);
                Field(c.Ears.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
                c.F.Vendor.Status($"slice 0 in_use=1 client_handle=0x{radio.ClientHandle:X} pan=0x40000000 RF_frequency=1.8 mode=USB rxant=ANT1");
                c.F.Vendor.Status($"display pan 0x40000000 client_handle=0x{radio.ClientHandle:X} center=1.8 rxant=ANT1");
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else if (command == "display pan set 0x40000000 center=14.200000")
            {
                // Accepted, and the pan's own report of the move comes LATE.
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            }
            else if (command.StartsWith("stream create type=dax_iq"))
            {
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
                c.F.Vendor.Status($"stream 0x20000000 type=dax_iq client_handle=0x{radio.ClientHandle:X} daxiq_channel=1 daxiq_rate=24000 active=1 pan=0x{c.Ears.Panadapter.StreamID:X} payload_endian=little");
            }
            else if (command.StartsWith("display pan rfgain_info 0x"))
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0,32,8");
            else
            {
                c.Respond(command);
                if (command == "slice tune 0 14.200000") c.F.Vendor.Status("slice 0 RF_frequency=14.200000 mode=USB rxant=XVT-B");
                if (c.Ears != null && command == $"display pan set 0x{c.Ears.Panadapter.StreamID:X} daxiq_channel=1")
                {
                    var stream = radio.DAXIQStreamList.Single();
                    var random = new Random(81);
                    var packet = new VitaIFDataPacket { StreamId = stream.StreamID, TimestampInt = 100,
                        payload = Enumerable.Range(0, 16384).Select(_ => random.Next(-30, 31) * 16f / 32768).ToArray() };
                    packet.ClassId.PacketClassCode = 0x02e3;
                    var add = typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.Instance | BindingFlags.NonPublic, null,
                        new[] { typeof(VitaIFDataPacket) }, null)!;
                    add.Invoke(stream, new object[] { packet });
                    packet.Header.packet_count = 1;
                    packet.TimestampFrac += (ulong)Math.Round(8192 * 1e12 / 24000);
                    add.Invoke(stream, new object[] { packet });
                    packets.TrySetResult(true);
                }
            }
        };

        var start = c.F.Rig.StartListenbackAsync(new ListenbackOptions(SeparatePan: true, ReceiveAntenna: "XVT-B"), default);
        var pumped = c.Pump(start);
        await packets.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(40);
        Assert.False(start.IsCompleted, "the start waits for the radio's report of the pan's move");
        Assert.Equal(1.8, c.Ears.Panadapter.CenterFreq, 6);   // FlexLib's cache: the creation frequency

        // The radio reports the move.
        c.F.Vendor.Status($"display pan 0x40000000 client_handle=0x{radio.ClientHandle:X} center=14.200000 rxant=XVT-B");
        var run = await pumped;
        try
        {
            // The take is still open for writing; read it the way a viewer would.
            string text;
            using (var stream = new FileStream(Path.Combine(run.DirectoryPath, "take.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
            var header = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r')).Where(line => line.Contains('='))
                .Select(line => new System.Collections.Generic.KeyValuePair<string, string>(line[..line.IndexOf('=')], line[(line.IndexOf('=') + 1)..]))
                .ToList();
            string Last(string key) => header.Last(p => p.Key == key).Value;
            Assert.Equal(14.2, double.Parse(Last("pan-centre-mhz"), System.Globalization.CultureInfo.InvariantCulture), 6);
            Assert.Equal(14.2, double.Parse(Last("tx-frequency-mhz"), System.Globalization.CultureInfo.InvariantCulture), 6);
            Assert.Equal("radio-report", Last("pan-centre-source"));
            // Never a stale centre anywhere in the header, not even overwritten later.
            Assert.DoesNotContain(header, p => p.Key == "pan-centre-mhz" && p.Value.StartsWith("1.8", StringComparison.Ordinal));
        }
        finally
        {
            c.F.Rig.ListenbackUnkeyWait = TimeSpan.FromMilliseconds(40);
            await c.Pump(StopAsync(run));
        }
    }

    [Fact]
    public async Task A_pan_that_never_reports_its_move_refuses_the_take()
    {
        using var c = new RadioCase(); var radio = c.F.Vendor.Radio;
        c.F.Rig.ListenbackConfirmationWait = TimeSpan.FromMilliseconds(300);
        Field(c.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create"))
            {
                c.Ears = c.Add(0, 1.8);
                Field(c.Ears.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
                c.F.Vendor.Status($"slice 0 in_use=1 client_handle=0x{radio.ClientHandle:X} pan=0x40000000 RF_frequency=1.8 mode=USB rxant=ANT1");
                c.F.Vendor.Status($"display pan 0x40000000 client_handle=0x{radio.ClientHandle:X} center=1.8 rxant=ANT1");
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else if (command == "display pan set 0x40000000 center=14.200000")
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            else if (command.StartsWith("stream create type=dax_iq"))
            {
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
                c.F.Vendor.Status($"stream 0x20000000 type=dax_iq client_handle=0x{radio.ClientHandle:X} daxiq_channel=1 daxiq_rate=24000 active=1 pan=0x{c.Ears.Panadapter.StreamID:X} payload_endian=little");
            }
            else
            {
                c.Respond(command);
                if (command == "slice tune 0 14.200000") c.F.Vendor.Status("slice 0 RF_frequency=14.200000 mode=USB rxant=XVT-B");
                if (c.Ears != null && command == $"display pan set 0x{c.Ears.Panadapter.StreamID:X} daxiq_channel=1")
                {
                    var stream = radio.DAXIQStreamList.Single();
                    var random = new Random(81);
                    var packet = new VitaIFDataPacket { StreamId = stream.StreamID, TimestampInt = 100,
                        payload = Enumerable.Range(0, 16384).Select(_ => random.Next(-30, 31) * 16f / 32768).ToArray() };
                    packet.ClassId.PacketClassCode = 0x02e3;
                    var add = typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.Instance | BindingFlags.NonPublic, null,
                        new[] { typeof(VitaIFDataPacket) }, null)!;
                    add.Invoke(stream, new object[] { packet });
                    packet.Header.packet_count = 1;
                    packet.TimestampFrac += (ulong)Math.Round(8192 * 1e12 / 24000);
                    add.Invoke(stream, new object[] { packet });
                }
            }
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Pump(c.F.Rig.StartListenbackAsync(new ListenbackOptions(SeparatePan: true, ReceiveAntenna: "XVT-B"), default)));
        // The one sentence the window speaks for this refusal names the cause.
        Assert.Equal("listening-state-not-confirmed", error.Message);
        Assert.Contains("did not confirm the listening slice and pan", Lexicon.Get("audio.iq.reason.listening-state-not-confirmed"));
    }

    private static async Task<bool> StopAsync(ListenbackRun run)
    { await run.StopAsync("dialog-closed"); return run.Restored; }
}
