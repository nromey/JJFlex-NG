using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

public sealed class IqListenbackTests
{
    private static IqPacketHeader Header(long pair, int count, int rate = 24000)
    {
        long seconds = pair / rate;
        ulong fraction = (ulong)Math.Round((pair % rate) * 1e12 / rate);
        return new(1, (byte)(count & 15), rate == 24000 ? (ushort)0x02e3 : (ushort)0x02e4,
            1, 2, (uint)(1000 + seconds), fraction, pair * Stopwatch.Frequency / rate);
    }
    private static float[] Noise(int count, int seed = 123) =>
        new Random(seed).GetItems(Enumerable.Range(-64, 129).Select(i => i * 16f / 32768).ToArray(), count);

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(35)]
    public void Timestamp_sizes_gaps_across_counter_wrap(int missing)
    {
        var ledger = new IqGapLedger();
        Assert.Equal(0, ledger.Observe(Header(0, 0), 176).FillPairs);
        var gap = ledger.Observe(Header((missing + 1) * 176, missing + 1), 176);
        Assert.True(gap.Exact);
        Assert.Equal(missing * 176, gap.FillPairs);
    }

    [Fact]
    public void Reordered_packet_does_not_move_clock_back_and_consecutive_gaps_work()
    {
        var ledger = new IqGapLedger();
        ledger.Observe(Header(0, 0), 176);
        Assert.Equal(176, ledger.Observe(Header(352, 2), 176).FillPairs);
        Assert.True(ledger.Observe(Header(176, 1), 176).Discard);
        Assert.Equal(352, ledger.Observe(Header(880, 5), 176).FillPairs);
        Assert.Equal(0, ledger.Observe(Header(1056, 6), 176).FillPairs);
    }

    [Fact]
    public void Missing_timestamps_are_explicit_estimates_including_sixteen_lost_packets()
    {
        var ledger = new IqGapLedger();
        ledger.Observe(Header(0, 0) with { IntegerKind = 0, FractionKind = 0 }, 176);
        var gap = ledger.Observe(Header(17 * 176, 17) with { IntegerKind = 0, FractionKind = 0 }, 176);
        Assert.False(gap.Exact);
        Assert.Equal(16 * 176, gap.FillPairs);
    }

    [Fact]
    public void Repeating_payload_is_refused_and_real_quiet_fixture_passes()
    {
        Assert.Equal("repeating-pattern", IqClassifier.Classify(
            Enumerable.Range(0, 16384).Select(i => (float)(i % 16)).ToArray()).Verdict);
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "iq-quiet-quarter.f32"));
        var raw = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, raw, 0, bytes.Length);
        var result = IqClassifier.Classify(raw);
        Assert.Equal("noise-like", result.Verdict);
        Assert.True(result.Correlation < .95);
        Assert.Equal(16, result.ObservedStep);
    }

    [Fact]
    public void Header_round_trips_arbitrary_values_and_partial_take_stays_partial()
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "take.txt");
        using (var writer = new TakeHeader(path))
        {
            writer.Write("nickname", "a=b\\c\r\nclosed=clean");
            writer.Write("serial", "123");
        }
        File.WriteAllBytes(Path.Combine(temp.Path, "rf.iq"), new byte[16]);
        Assert.Equal("a=b\\c\r\nclosed=clean", TakeHeader.Read(path).Single(x => x.Key == "nickname").Value);
        Assert.Equal("incomplete", TakeReader.Inspect(temp.Path).State);
    }

    [Fact]
    public void Copies_reused_payload_and_fans_out_the_same_samples_to_file_and_live_consumer()
    {
        using var temp = new TemporaryDirectory();
        using var writer = new IqTakeWriter(temp.Path, 24000, new Dictionary<string, string>())
            { EchoedRate = 24000, Active = true };
        using var feed = new IqFeed(24000);
        using var take = feed.Subscribe(writer.Accept);
        var received = new List<float>();
        using var live = feed.Subscribe(b => received.AddRange(b.Samples.ToArray()));
        var payload = Noise(16384);
        var expected = (float[])payload.Clone();
        Assert.True(feed.Offer(Header(0, 0), payload));
        Array.Fill(payload, 99f);
        feed.Dispose();
        writer.Finish("operator-stop", feed.DroppedPackets, feed.DroppedPairs, feed.SubscriberFailures);
        Assert.Equal(expected, received);
        var raw = new float[expected.Length];
        Buffer.BlockCopy(File.ReadAllBytes(Path.Combine(temp.Path, "rf.iq")), 0, raw, 0, raw.Length * 4);
        Assert.Equal(expected.Select(x => x * 32768f), raw);
        Assert.Equal("clean", TakeReader.Inspect(temp.Path).State);
        raw[0] = 12345;
        using (var file = File.OpenWrite(Path.Combine(temp.Path, "rf.iq"))) file.WriteByte(123);
        Assert.Equal("corrupt", TakeReader.Inspect(temp.Path).State);
        Assert.Equal("checksum", TakeReader.Inspect(temp.Path).Reason);
    }

    [Fact]
    public void Stalled_subscriber_causes_exact_overrun_accounting()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var feed = new IqFeed(24000, packetCapacity: 2);
        using var subscription = feed.Subscribe(_ => { entered.Set(); Assert.True(release.Wait(5000)); });
        try
        {
            Assert.True(feed.Offer(Header(0, 0), new float[352]));
            Assert.True(entered.Wait(5000));
            Assert.True(feed.Offer(Header(176, 1), new float[352]));
            Assert.True(feed.Offer(Header(352, 2), new float[352]));
            for (int i = 0; i < 7; ++i) Assert.False(feed.Offer(Header(528 + i * 176, i + 3), new float[352]));
            Assert.Equal(7, feed.DroppedPackets);
            Assert.Equal(7 * 176, feed.DroppedPairs);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void Wrong_class_before_echo_does_not_start_the_take_or_classifier()
    {
        using var temp = new TemporaryDirectory();
        using var writer = new IqTakeWriter(temp.Path, 24000, new Dictionary<string, string>())
            { Active = true, EchoedRate = 48000 };
        writer.Accept(new(Header(0, 0, 48000), Noise(16384), 0, 0));
        Assert.Equal(0, writer.PairsWritten);
        writer.EchoedRate = 24000;
        writer.Accept(new(Header(1000, 1), Noise(16384), 0, 0));
        writer.Accept(new(Header(9192, 2), Noise(352), 0, 0));
        Assert.True(writer.IsReady);
        Assert.Equal(8368, writer.PairsWritten);
        writer.Finish("operator-stop");
        var take = TakeReader.Inspect(temp.Path);
        Assert.Equal("24000", take.Get("rate-echoed"));
        Assert.Equal(2, take.Fields.Count(x => x.Key == "packet-class"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jjflex-iq-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
