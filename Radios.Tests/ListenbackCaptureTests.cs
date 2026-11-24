using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Flex.Smoothlake.Vita;
using Radios.Listenback;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

public sealed class ListenbackCaptureTests
{
    [Fact]
    public async Task Real_status_gates_rate_other_clients_are_ignored_and_concurrent_stop_drains_once()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-capture-" + Guid.NewGuid().ToString("N"));
        var vendor = new VendorRadioFixture("capture");
        var pan = (Panadapter)Activator.CreateInstance(typeof(Panadapter), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { vendor.Radio }, null)!;
        typeof(Panadapter).GetProperty(nameof(Panadapter.StreamID))!.GetSetMethod(true)!.Invoke(pan, new object[] { 0x4001u });
        vendor.Radio.PanadapterList.Add(pan);
        try
        {
            await using var capture = new IqRadioCapture(vendor.Radio, pan, 1, 24000, root, new Dictionary<string, string>(),
                action => { action(); return Task.CompletedTask; });
            var start = capture.StartAsync(CancellationToken.None);
            Assert.Contains("stream create type=dax_iq daxiq_channel=1", vendor.Transport.Commands);
            vendor.ReplyOk(vendor.LastSequence);
            vendor.Status("stream 0x10 type=dax_iq client_handle=0x123 daxiq_channel=1 daxiq_rate=48000 active=1 payload_endian=little");
            Assert.False(start.IsCompleted);
            vendor.Status("stream 0x11 type=dax_iq client_handle=0x0 daxiq_channel=1 daxiq_rate=48000 active=1 payload_endian=little");
            var stream = vendor.Radio.DAXIQStreamList.Single(s => s.StreamID == 0x11);
            await Until(() => vendor.Transport.Commands.Any(c => c == "stream set 0x11 daxiq_rate=24000"));
            stream.SampleRate = 24000; // optimistic local setter is NOT evidence
            Assert.Equal(48000, capture.Writer.EchoedRate);
            vendor.Status("stream 0x11 type=dax_iq daxiq_rate=24000"); // separate status retains active=1
            var random = new Random(81);
            var packet = new VitaIFDataPacket { StreamId = 0x11, TimestampInt = 100,
                payload = Enumerable.Range(0, 16384).Select(_ => random.Next(-30, 31) * 16f / 32768).ToArray() };
            packet.ClassId.PacketClassCode = 0x02e3;
            typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(VitaIFDataPacket) }, null)!.Invoke(stream, new object[] { packet });
            packet.Header.packet_count++;
            packet.TimestampFrac += (ulong)Math.Round(8192 * 1e12 / 24000);
            typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(VitaIFDataPacket) }, null)!.Invoke(stream, new object[] { packet });
            await start.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(capture.Writer.IsReady);
            await Task.WhenAll(capture.StopAsync("operator-stop"), capture.StopAsync("operator-stop"));
            Assert.Single(vendor.Transport.Commands.Where(c => c == "stream remove 0x11"));
            Assert.DoesNotContain("stream remove 0x10", vendor.Transport.Commands);
            Assert.Equal("clean", TakeReader.Inspect(root).State);
            var facts = TakeHeader.Read(Path.Combine(root, "take.txt"));
            Assert.Contains(facts, f => f.Key == "iq-routed-packets" && f.Value == "2");
            Assert.Contains(facts, f => f.Key == "iq-routed-payload-bytes" && f.Value == "131072");
            Assert.Contains(facts, f => f.Key == "iq-create-reply" && f.Value.Contains("Code"));
            Assert.DoesNotContain("stream", vendor.Radio.SelectedTelnetTraceTopics);
            // A replacement stream may reuse the numeric id. An old object
            // must not feed it, and removal must end just this new instance.
            string replacementPath = Path.Combine(root, "replacement");
            await using var replacement = new IqRadioCapture(vendor.Radio, pan, 1, 24000, replacementPath, new Dictionary<string, string>(),
                action => { action(); return Task.CompletedTask; });
            var restart = replacement.StartAsync(CancellationToken.None);
            vendor.ReplyOk(vendor.LastSequence);
            vendor.Status("stream 0x11 type=dax_iq client_handle=0x0 daxiq_channel=1 daxiq_rate=24000 active=1 payload_endian=little");
            var nextStream = vendor.Radio.DAXIQStreamList.Single(s => s.StreamID == 0x11);
            Assert.NotSame(stream, nextStream);
            var dispatch = typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(VitaIFDataPacket) }, null)!;
            dispatch.Invoke(stream, new object[] { packet });
            Assert.Equal(0, replacement.Writer.PairsWritten);
            dispatch.Invoke(nextStream, new object[] { packet });
            packet.Header.packet_count++;
            packet.TimestampFrac += (ulong)Math.Round(8192 * 1e12 / 24000);
            dispatch.Invoke(nextStream, new object[] { packet });
            await restart.WaitAsync(TimeSpan.FromSeconds(3));
            vendor.Status("stream 0x11 removed");
            Assert.Equal("stream-removed", await replacement.Ended.WaitAsync(TimeSpan.FromSeconds(3)));
            await replacement.StopAsync("stream-removed");
            Assert.Equal("incomplete", TakeReader.Inspect(replacementPath).State);
            vendor.Radio.DAXIQStreamList.Single(s => s.StreamID == 0x10).Close();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Iq_create_refusal_is_recorded_and_refused_before_stream_wait()
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-capture-refused-" + Guid.NewGuid().ToString("N"));
        var vendor = new VendorRadioFixture("capture-refused");
        var pan = (Panadapter)Activator.CreateInstance(typeof(Panadapter), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { vendor.Radio }, null)!;
        try
        {
            await using var capture = new IqRadioCapture(vendor.Radio, pan, 1, 24000, root,
                new Dictionary<string, string>(), action => { action(); return Task.CompletedTask; });
            var start = capture.StartAsync(CancellationToken.None);
            vendor.Reply(vendor.LastSequence, 0x50000001, "capacity refused");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await start.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("stream-create-refused", error.Message);
            await capture.StopAsync("start-failed");
            var facts = TakeHeader.Read(Path.Combine(root, "take.txt"));
            Assert.Contains(facts, f => f.Key == "iq-create-reply" && f.Value.Contains("capacity refused"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    /// <summary>
    /// The take's IQ guard (#870). The radio can hand a new pan back already
    /// bound to the take's OWN channel (it restores a client's saved pan, IQ
    /// channel included), and the take then refused its own binding with
    /// "pan-iq-in-use". Own channel: accepted, nothing sent to the pan. No
    /// channel: accepted, the take binds it. Any other channel: still refused,
    /// and the pan is never written.
    /// </summary>
    [Theory]
    [InlineData(0, true, true)]
    [InlineData(1, true, false)]
    [InlineData(2, false, false)]
    public async Task The_take_accepts_a_pan_already_bound_to_its_own_channel_and_refuses_any_other(
        int boundBefore, bool accepted, bool bindsThePan)
    {
        string root = Path.Combine(Path.GetTempPath(), "jjflex-capture-guard-" + Guid.NewGuid().ToString("N"));
        var vendor = new VendorRadioFixture("capture-guard");
        var pan = (Panadapter)Activator.CreateInstance(typeof(Panadapter), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { vendor.Radio }, null)!;
        typeof(Panadapter).GetProperty(nameof(Panadapter.StreamID))!.GetSetMethod(true)!.Invoke(pan, new object[] { 0x4001u });
        typeof(Panadapter).GetField("_daxIQChannel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pan, boundBefore);
        vendor.Radio.PanadapterList.Add(pan);
        try
        {
            await using var capture = new IqRadioCapture(vendor.Radio, pan, 1, 24000, root, new Dictionary<string, string>(),
                action => { action(); return Task.CompletedTask; });
            var start = capture.StartAsync(CancellationToken.None);
            vendor.ReplyOk(vendor.LastSequence);
            vendor.Status("stream 0x11 type=dax_iq client_handle=0x0 daxiq_channel=1 daxiq_rate=48000 active=1 payload_endian=little");
            // Positive control: the start reached the guard (the rate request
            // is sent on the same queued step, just before it).
            await Until(() => vendor.Transport.Commands.Any(c => c == "stream set 0x11 daxiq_rate=24000"));
            if (!accepted)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await start.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Equal("pan-iq-in-use", error.Message);
                Assert.Equal(boundBefore, pan.DAXIQChannel);
                Assert.DoesNotContain(vendor.Transport.Commands, c => c.StartsWith("display pan set 0x4001", StringComparison.Ordinal));
                await capture.StopAsync("start-failed");
                return;
            }
            var stream = vendor.Radio.DAXIQStreamList.Single(s => s.StreamID == 0x11);
            vendor.Status("stream 0x11 type=dax_iq daxiq_rate=24000");
            var random = new Random(870);
            var packet = new VitaIFDataPacket { StreamId = 0x11, TimestampInt = 100,
                payload = Enumerable.Range(0, 16384).Select(_ => random.Next(-30, 31) * 16f / 32768).ToArray() };
            packet.ClassId.PacketClassCode = 0x02e3;
            var dispatch = typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(VitaIFDataPacket) }, null)!;
            dispatch.Invoke(stream, new object[] { packet });
            packet.Header.packet_count++;
            packet.TimestampFrac += (ulong)Math.Round(8192 * 1e12 / 24000);
            dispatch.Invoke(stream, new object[] { packet });
            await start.WaitAsync(TimeSpan.FromSeconds(3));   // no pan-iq-in-use
            Assert.True(capture.Writer.IsReady);
            Assert.Equal(1, pan.DAXIQChannel);
            Assert.Equal(bindsThePan ? 1 : 0,
                vendor.Transport.Commands.Count(c => c == "display pan set 0x4001 daxiq_channel=1"));
            await capture.StopAsync("operator-stop");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(5, limit.Token);
    }
}
