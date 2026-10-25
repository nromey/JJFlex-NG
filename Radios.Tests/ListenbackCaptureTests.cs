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
    private static async Task Until(Func<bool> predicate)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(5, limit.Token);
    }
}
