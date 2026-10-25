using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

public sealed class ListenbackRoundTwelveTests
{
    private const int Rate = 24000;
    private const uint ReusedStreamId = 0x20000000;

    private static IqPacketHeader Packet(byte count, uint seconds, ulong fraction, long arrival) =>
        new(ReusedStreamId, count, 0x02e3, 1, 2, seconds, fraction, arrival);

    private static float[] VoiceLikeNoise(int pairs)
    {
        var random = new Random(51);
        return Enumerable.Range(0, pairs * 2).Select(_ => random.Next(-30, 31) * 16f / 32768).ToArray();
    }

    [Fact]
    public void Contiguous_startup_burst_keeps_its_first_packet()
    {
        string path = Path.Combine(Path.GetTempPath(), "jjflex-burst-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var writer = new IqTakeWriter(path, Rate, new Dictionary<string, string>())
                { Active = true, EchoedRate = Rate };
            long arrival = Stopwatch.GetTimestamp();
            uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            writer.Accept(new(Packet(0, now, 0, arrival), VoiceLikeNoise(8192), 0, 0));
            // Two ordinary contiguous radio packets can be dispatched in a
            // burst immediately after the stream request. Their radio-time
            // separation is a full packet despite near-zero arrival gap.
            writer.Accept(new(Packet(1, now, 341_333_333_333,
                arrival + Stopwatch.Frequency / 1000), VoiceLikeNoise(8192), 0, 0));
            Assert.True(writer.IsReady);
            Assert.Equal(16384, writer.PairsWritten);
            writer.Finish("operator-stop");
            Assert.Equal("clean", TakeReader.Inspect(path).State);
            Assert.DoesNotContain("startup-discard=", File.ReadAllText(Path.Combine(path, "take.txt")));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void Rapid_second_take_excludes_the_prior_stream_tail_and_keeps_its_own_clock()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-two-takes-" + Guid.NewGuid().ToString("N"));
        try
        {
            uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long ticks = Stopwatch.GetTimestamp();
            var prior = Packet(0, now - 20, 0, ticks - 20 * Stopwatch.Frequency);
            using (var first = new IqTakeWriter(Path.Combine(root, "first"), Rate,
                new Dictionary<string, string>()) { Active = true, EchoedRate = Rate })
            {
                first.Accept(new(prior, VoiceLikeNoise(8192), 0, 0));
                first.Accept(new(Packet(1, now - 20, 341_333_333_333,
                    prior.ArrivalTicks + Stopwatch.Frequency / 3), VoiceLikeNoise(176), 0, 0));
                first.Finish("operator-stop");
                Assert.Equal("clean", TakeReader.Inspect(first.DirectoryPath).State);
            }

            // The radio reused the stream ID. One old datagram arrived at the
            // new writer, then two packets from the new stream arrived together.
            using var second = new IqTakeWriter(Path.Combine(root, "second"), Rate,
                new Dictionary<string, string>()) { Active = true, EchoedRate = Rate };
            long arrival = Stopwatch.GetTimestamp();
            second.Accept(new(Packet(2, now - 20, 348_666_666_666, arrival),
                Enumerable.Repeat(0.8f, 352).ToArray(), 0, 0));
            var fresh = Packet(3, now + 1, 0, arrival + Stopwatch.Frequency / 100);
            second.Accept(new(fresh, VoiceLikeNoise(176), 0, 0));
            second.Accept(new(Packet(4, now + 1, 7_333_333_333,
                fresh.ArrivalTicks + Stopwatch.Frequency * 176 / Rate), VoiceLikeNoise(8192), 0, 0));
            second.Finish("operator-stop");

            Assert.Equal(8368, second.PairsWritten);
            var take = TakeReader.Inspect(second.DirectoryPath);
            Assert.Equal("clean", take.State);
            Assert.InRange(double.Parse(take.Get("pairs-per-arrival-span"),
                System.Globalization.CultureInfo.InvariantCulture), 0.99, 1.01);
            Assert.Equal("0", take.Get("fill-pairs"));
            Assert.Contains("startup-discard=", File.ReadAllText(Path.Combine(second.DirectoryPath, "take.txt")));
            using var samples = new BinaryReader(File.OpenRead(Path.Combine(second.DirectoryPath, "rf.iq")));
            Assert.InRange(samples.ReadSingle(), -1000f, 1000f);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
