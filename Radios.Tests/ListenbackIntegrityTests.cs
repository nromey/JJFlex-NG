using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Flex.Smoothlake.Vita;
using Radios.Listenback;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

public sealed class ListenbackIntegrityTests
{
    [Fact]
    public void Vendor_IF_event_is_borrowed_instance_data_and_Opus_does_not_raise_it()
    {
        var radio = new VendorRadioFixture("IF-event").Radio;
        var stream = new RXAudioStream(radio);
        var other = new RXAudioStream(radio);
        int packets = 0, ordinary = 0, opus = 0;
        var packet = new VitaIFDataPacket { payload = new[] { 0.25f, -0.5f } };
        stream.IFPacketReady += (sender, data) => { Assert.Same(stream, sender); Assert.Same(packet, data); packets++; };
        other.IFPacketReady += (_, _) => throw new Exception("cross-instance event");
        stream.DataReady += (_, data) => { Assert.Same(packet.payload, data); ordinary++; };
        stream.OpusPacketReceived += () => opus++;
        typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(VitaIFDataPacket) }, null)!.Invoke(stream, new object[] { packet });
        typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(VitaOpusDataPacket) }, null)!.Invoke(stream, new object[] { new VitaOpusDataPacket { TimestampInt = 1, payload = new byte[] { 1 } } });
        Assert.Equal(1, packets); Assert.Equal(1, ordinary); Assert.Equal(1, opus);
    }

    [Fact]
    public void Tail_overrun_is_filled_and_length_and_quantization_failures_are_refused()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jjflex-integrity-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var writer = new IqTakeWriter(directory, 24000, new Dictionary<string, string>()) { Active = true, EchoedRate = 24000 })
            {
                var random = new Random(10);
                var samples = Enumerable.Range(0, 16384).Select(_ => random.Next(-30, 31) * 16f / 32768).ToArray();
                writer.Accept(new(new(1, 0, 0x02e3, 1, 2, 10, 0, 0), samples, 0, 0));
                writer.Accept(new(new(1, 1, 0x02e3, 1, 2, 10, 341333333333, 3413333), new float[352], 0, 0));
                Assert.True(writer.IsReady);
                writer.Finish("operator-stop", 2, 352);
            }
            var take = TakeReader.Inspect(directory);
            Assert.Equal("incomplete", take.State);
            Assert.Equal("352", take.Get("fill-pairs"));
            Assert.Equal((8192 + 176 + 352) * 8, new FileInfo(Path.Combine(directory, "rf.iq")).Length);
            string headerPath = Path.Combine(directory, "take.txt");
            var lines = File.ReadAllLines(headerPath).Where(s => !s.StartsWith("sha256=") && !s.StartsWith("closed=")).ToArray();
            File.WriteAllLines(headerPath, lines);
            using (var file = new BinaryWriter(File.OpenWrite(Path.Combine(directory, "rf.iq")))) file.Write(3f);
            Assert.Equal("quantization", TakeReader.Inspect(directory).Reason);
            File.WriteAllLines(headerPath, lines.Select(s => s.StartsWith("pairs-written=") ? "pairs-written=1" : s));
            Assert.Equal("length", TakeReader.Inspect(directory).Reason);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
