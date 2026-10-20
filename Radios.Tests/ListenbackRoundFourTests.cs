using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Radios.Listenback;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Flex.Smoothlake.Vita;
using Radios.Tests.StationConnect;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ListenbackRoundFourTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ListenbackRoundFourTests));
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public ListenbackRoundFourTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    public void Dispose() => _scope.Dispose();
    private static float[] Noise(int pairs) { var r = new Random(51); return Enumerable.Range(0, pairs * 2).Select(_ => r.Next(-30, 31) * 16f / 32768).ToArray(); }
    private static IqPacketHeader BenchFirst => new(0x20000000, 1, 739, 1, 2, 1791215884, 707120340872, 20155542157131);
    private static IqPacketHeader BenchSecond => new(0x20000000, 2, 739, 1, 2, 1792448056, 425537007583, 20155542202873);
    private static IqPacketHeader Next(IqPacketHeader h, int pairs) => h with {
        Count = (byte)((h.Count + 1) % 16), Fraction = h.Fraction + (ulong)Math.Round(pairs * 1e12 / h.Rate),
        ArrivalTicks = h.ArrivalTicks + (long)Math.Round(pairs * (double)Stopwatch.Frequency / h.Rate) };
    private string DirectoryFor(string name) { var p = Path.Combine(RadioConfig.AppDataRoot, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }

    [Fact]
    public void Exact_bench_stale_leading_packet_is_excluded_only_after_the_following_clock_is_confirmed()
    {
        string path = DirectoryFor("stale");
        using var w = new IqTakeWriter(path, 24000, new Dictionary<string,string>()) { Active = true, EchoedRate = 24000 };
        w.Accept(new(BenchFirst, Noise(176), 0, 0));
        w.Accept(new(BenchSecond, Noise(176), 0, 0));
        w.Accept(new(Next(BenchSecond,176), Noise(8192), 0, 0));
        w.Finish("operator-stop");
        Assert.Null(w.Failure);
        Assert.Equal(8368, w.PairsWritten);
        var take = TakeReader.Inspect(path);
        Assert.Equal("clean", take.State);
        Assert.Contains(take.Fields, x => x.Key == "startup-discard" && x.Value.Contains("1791215884"));
        Assert.Contains("1792448056", take.Get("first-packet"));
    }

    [Fact]
    public void A_clock_jump_after_established_capture_is_still_a_failure()
    {
        using var w = new IqTakeWriter(DirectoryFor("jump"),24000,new Dictionary<string,string>()) { Active = true, EchoedRate = 24000 };
        w.Accept(new(BenchFirst,Noise(176),0,0));
        w.Accept(new(Next(BenchFirst,176),Noise(8192),0,0));
        w.Accept(new(BenchSecond,Noise(176),0,0));
        Assert.Equal("timestamp-discontinuity",w.Failure);
    }

    [Fact]
    public async Task Restore_requires_both_success_reply_and_status_and_continues_other_items()
    {
        using var c = new RadioCase(); var s = c.Snapshot();
        s.TakeDirectory = DirectoryFor("restore-refused"); c.Store.Save(s);
        Field(c.F.Vendor.Radio,"_fullDuplexEnabled",true);
        c.F.Vendor.Transport.CommandWritten = command => {
            if (command == "radio set full_duplex_enabled=0") {
                c.F.Vendor.Status("radio full_duplex_enabled=0");
                c.F.Vendor.Reply(c.F.Vendor.LastSequence,0x50000001,"refused");
            } else c.Respond(command);
        };
        Assert.False(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(s,true)));
        Assert.NotNull(c.F.Rig.PendingListenbackRecovery);
        var trace = string.Join("\n",Directory.GetFiles(s.TakeDirectory,"restore-*.txt").Select(File.ReadAllText));
        Assert.Contains("full-duplex",trace); Assert.Contains("50000001",trace); Assert.Contains("verdict",trace);
    }

    [Fact]
    public async Task A_surviving_stream_prevents_pan_deletion_but_not_full_duplex_restoration()
    {
        using var c = new RadioCase(); var s = c.Snapshot(); s.TakeDirectory = DirectoryFor("restore-stream"); c.Store.Save(s);
        Field(c.F.Vendor.Radio,"_fullDuplexEnabled",true);
        c.F.Vendor.Status($"stream 0x20000000 type=dax_iq client_handle=0x{c.F.Vendor.Radio.ClientHandle:X} daxiq_channel=1 pan=0x40000000 daxiq_rate=24000 active=1");
        c.F.Vendor.Transport.CommandWritten = command => {
            if (command == "stream remove 0x20000000") c.F.Vendor.Reply(c.F.Vendor.LastSequence,0x50000001,"refused");
            else c.Respond(command);
        };
        Assert.False(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(s,true)));
        Assert.DoesNotContain("display pan remove 0x40000000",c.F.Vendor.Transport.Commands);
        Assert.Contains("radio set full_duplex_enabled=0",c.F.Vendor.Transport.Commands);
    }
    [Theory]
    [InlineData("FLEX-8600")]
    [InlineData("FLEX-6300")]
    [InlineData("UNKNOWN")]
    public async Task Full_duplex_is_unknown_until_reported_and_waits_for_the_set_reply(string model)
    {
        using var c = new RadioCase(); var rig = c.F.Rig; var radio = c.F.Vendor.Radio;
        Property(radio,"Model",model);
        rig.WireStationHandlers(radio);
        Assert.Null(rig.FullDuplexAvailable);
        Assert.Equal("audio.full_duplex.not_reported",await rig.ToggleFullDuplexAsync());
        c.F.Vendor.Status("radio full_duplex_enabled=0");
        Assert.True(rig.FullDuplexAvailable); Assert.False(rig.FullDuplexEnabled);
        c.F.Vendor.Transport.CommandWritten = command => { }; // hold the set reply
        var toggle = rig.ToggleFullDuplexAsync();
        for (int i = 0; i < 50 && c.F.Vendor.Transport.Commands.Count == 0; ++i)
        {
            while(rig.q.Count > 0) if(rig.q.Dequeue().Item is FlexBase.FunctionDel action) action();
            await Task.Delay(5);
        }
        Assert.False(toggle.IsCompleted); Assert.False(rig.FullDuplexEnabled);
        Assert.Contains("radio set full_duplex_enabled=1",c.F.Vendor.Transport.Commands);
        c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
        Assert.Equal("audio.full_duplex.on",await toggle);
        Assert.True(rig.FullDuplexEnabled);
        Assert.DoesNotContain("sub radio all", c.F.Vendor.Transport.Commands);
        c.F.Vendor.Transport.CommandWritten = c.Respond;
        Assert.Equal("audio.full_duplex.off",await c.Pump(rig.ToggleFullDuplexAsync()));
        Assert.False(rig.FullDuplexEnabled);
    }

    [Fact]
    public async Task Full_duplex_refusal_and_missing_reply_do_not_claim_a_new_state()
    {
        using var c = new RadioCase(); c.F.Rig.WireStationHandlers(c.F.Vendor.Radio);
        c.F.Vendor.Status("radio full_duplex_enabled=0");
        c.F.Rig.ListenbackConfirmationWait = TimeSpan.FromMilliseconds(60);
        c.F.Vendor.Transport.CommandWritten = command => c.F.Vendor.Reply(c.F.Vendor.LastSequence,1,"refused");
        Assert.Equal("audio.full_duplex.refused",await c.Pump(c.F.Rig.ToggleFullDuplexAsync()));
        Assert.False(c.F.Rig.FullDuplexEnabled);
        c.F.Vendor.Transport.CommandWritten = command => { }; // no set reply
        Assert.Equal("audio.full_duplex.unconfirmed",await c.Pump(c.F.Rig.ToggleFullDuplexAsync()));
        Assert.False(c.F.Rig.FullDuplexEnabled);
    }

    [Fact]
    public void Exact_bench_four_pair_gap_is_recorded_as_a_startup_signature()
    {
        var first = new IqPacketHeader(0x20000000,0,739,1,2,1792448344,891792219912,20158426792920);
        var second = new IqPacketHeader(0x20000000,1,739,1,2,1792448344,897292215842,20158426867579);
        using var w = new IqTakeWriter(DirectoryFor("four-pair-gap"),24000,new Dictionary<string,string>()) { Active = true, EchoedRate = 24000 };
        w.Accept(new(first,Noise(128),0,0)); w.Accept(new(second,Noise(8192),0,0));
        w.Finish("operator-stop");
        Assert.Equal(4,w.FillPairs);
        var take = TakeReader.Inspect(w.DirectoryPath);
        Assert.Equal("clean", take.State);
        Assert.Equal("detected", take.Get("startup-signature"));
    }

    [Fact]
    public void Inspection_reports_absence_and_does_not_infer_ownership_from_presence()
    {
        using var c = new RadioCase(); var s = c.Snapshot();
        c.F.Vendor.Status("slice 0 in_use=0");
        string text = ListenbackInspectionSummary.Describe(s,x => c.F.Rig.ListenbackResourcePresent(s,x));
        Assert.Contains("slice 0 is already absent",text);
        Assert.Contains("pan 0x40000000 with this ID is still present",text);
        Assert.Contains("ownership can be confirmed",text);
        Assert.Equal("0.9999",ListenbackResultText.Ratio("0.9999197764618374"));
        string result = Lexicon.Get("audio.iq.diagnostic_result",("seconds","19.3"),("antenna","ANT1"),("gain","16"));
        Assert.Contains("19.3",result); Assert.Contains("ANT1",result); Assert.Contains("16",result);
    }

    [Fact]
    public void Creation_trace_distinguishes_missing_fields_from_mismatched_reports()
    {
        var report = ListenbackCreationEvidence.Read("pan",0x40000001,new[] {
            "S0|display pan 0x40000001 center=21.195 rxant=ANT1" },out string problem,out string[] lines);
        Assert.Null(report); Assert.Equal("missing-client_handle",problem); Assert.Single(lines);
        report = ListenbackCreationEvidence.Read("pan",0x40000001,lines.Append("S0|display pan 0x40000001 client_handle=0x123"),out problem,out lines);
        Assert.NotNull(report); Assert.Equal("",problem); Assert.Equal(21.195,report.Frequency); Assert.Equal(2,lines.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_start_failure_records_evidence_and_restores_in_dependency_order(bool confirmationTimeout)
    {
        using var c = new RadioCase(); var radio = c.F.Vendor.Radio;
        c.F.Rig.ListenbackConfirmationWait = TimeSpan.FromMilliseconds(80);
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create"))
            {
                c.Ears = c.Add(0,21.195);
                c.F.Vendor.Status("slice 0 in_use=1 pan=0x40000000 RF_frequency=21.195 mode=USB rxant=ANT1");
                // Deliberately missing ownership on slice status, while vendor
                // object exists: a missing proof is not permission to guess.
                c.F.Vendor.Status($"display pan 0x40000000 client_handle=0x{radio.ClientHandle:X} center=21.195 rxant=ANT1");
                Field(c.F.Rig,"_RXVFO",1); Field(c.F.Rig,"_TXVFO",1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence,0,"0x40000000,0x42000000");
            }
            else if(command.StartsWith("stream create type=dax_iq"))
                c.F.Vendor.Status($"stream 0x20000000 type=dax_iq client_handle=0x{radio.ClientHandle:X} daxiq_channel=1 pan=0x0 daxiq_rate=48000 active=1 payload_endian=little");
            else if (confirmationTimeout && command == "sub slice all")
            {
                c.F.Vendor.Status("slice 0 in_use=1 pan=0x40000000 RF_frequency=21.195 mode=USB rxant=ANT1");
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            }
            else
            {
                c.Respond(command);
                if(command == "display pan set 0x40000000 daxiq_channel=1")
                {
                    c.F.Vendor.Status($"stream 0x20000000 type=dax_iq client_handle=0x{radio.ClientHandle:X} daxiq_channel=1 pan=0x40000000 daxiq_rate=24000 active=1 payload_endian=little");
                    var stream = radio.DAXIQStreamList.Single();
                    var packet = new VitaIFDataPacket { StreamId = stream.StreamID, TimestampInt = 100,
                        payload = confirmationTimeout ? Noise(8192) : new[] { float.NaN,0f } };
                    packet.ClassId.PacketClassCode = 0x02e3;
                    var dispatch = typeof(RXAudioStream).GetMethod("AddRXData",BindingFlags.NonPublic|BindingFlags.Instance,null,new[] { typeof(VitaIFDataPacket) },null)!;
                    dispatch.Invoke(stream,new object[] { packet });
                    if(confirmationTimeout)
                    {
                        packet.Header.packet_count = 1; packet.TimestampFrac = 341333333333;
                        dispatch.Invoke(stream,new object[] { packet });
                    }
                }
            }
        };
        string takesRoot = Path.Combine(RadioConfig.AppDataRoot,"Recordings","takes");
        var beforeTakes = Directory.Exists(takesRoot) ? Directory.GetDirectories(takesRoot) : Array.Empty<string>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Pump(c.F.Rig.StartListenbackAsync(new ListenbackOptions(SeparatePan:true,FullDuplex:true),default)));
        Assert.Equal(confirmationTimeout ? "listening-state-not-confirmed" : "non-finite-payload",error.Message);
        Assert.False(radio.FullDuplexEnabled); Assert.Null(c.F.Rig.PendingListenbackRecovery);
        var commands = c.F.Vendor.Transport.Commands.ToList();
        int streamRemove = commands.IndexOf("stream remove 0x20000000");
        int unbind = commands.IndexOf("display pan set 0x40000000 daxiq_channel=0");
        int sliceRemove = commands.IndexOf("slice remove 0");
        int panRemove = commands.IndexOf("display pan remove 0x40000000");
        int restoreFdx = commands.IndexOf("radio set full_duplex_enabled=0");
        Assert.True(streamRemove >= 0 && unbind > streamRemove && sliceRemove > unbind && panRemove > sliceRemove && restoreFdx > panRemove);
        Assert.Single(commands.Where(c => c == "stream remove 0x20000000"));
        Assert.Single(commands.Where(c => c == "display pan remove 0x40000000"));
        for(int i=0;i<commands.Count;i++) _output.WriteLine($"{i+1}. {commands[i]}");
        string directory = Directory.GetDirectories(takesRoot).Except(beforeTakes).Single();
        string restore = File.ReadAllText(Directory.GetFiles(directory,"restore-*.txt").Single());
        Assert.Contains("full-duplex",restore); Assert.Contains("Sequence",restore); Assert.Contains("00000000",restore);
        Assert.Contains("reply-confirmed",restore);
        if(confirmationTimeout)
        {
            string header = File.ReadAllText(Path.Combine(directory,"take.txt"));
            Assert.Contains("listener-confirmation-timeout",header); Assert.Contains("missing-client_handle",header);
            Assert.Contains("SliceLines",header); Assert.Contains("PanLines",header);
        }
    }

    [Fact]
    public void Receive_only_analysis_is_not_an_alignment_failure()
    {
        string directory = DirectoryFor("receive-only");
        var facts = new Dictionary<string,string> { ["mode"]="USB",["tx-filter-low"]="100",["tx-filter-high"]="3000",
            ["pan-centre-mhz"]="14.2",["tx-frequency-mhz"]="14.2",["tone-engaged"]="False" };
        using(var w = new IqTakeWriter(directory,24000,facts) { Active=true,EchoedRate=24000 })
        {
            w.Accept(new(BenchSecond,Noise(176),0,0)); w.Accept(new(Next(BenchSecond,176),Noise(8192),0,0)); w.Finish("operator-stop");
        }
        SsbTakeRenderer.Render(directory);
        var analysis = TakeAnalysis.Run(directory);
        Assert.Equal("not-keyed",analysis.Alignment.Reason);
        Assert.False(analysis.Difference.Comparable);
        Assert.Contains("aligned=not-applicable",File.ReadAllText(Path.Combine(directory,"align.txt")));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_suspect_leading_clock_needs_a_contiguous_third_packet(bool third)
    {
        using var w = new IqTakeWriter(DirectoryFor("unproven-head"),24000,new Dictionary<string,string>()) { Active=true,EchoedRate=24000 };
        w.Accept(new(BenchFirst,Noise(176),0,0)); w.Accept(new(BenchSecond,Noise(176),0,0));
        if(third) w.Accept(new(Next(BenchSecond,352),Noise(8192),0,0));
        w.Finish("operator-stop");
        Assert.Equal("timestamp-discontinuity",w.Failure);
        Assert.DoesNotContain(TakeHeader.Read(Path.Combine(w.DirectoryPath,"take.txt")),x => x.Key == "startup-discard");
    }

    [Fact]
    public async Task A_queued_full_duplex_toggle_cannot_follow_a_replaced_radio()
    {
        using var c = new RadioCase(); using var replacement = new RadioCase();
        c.F.Rig.WireStationHandlers(c.F.Vendor.Radio);
        c.F.Vendor.Status("radio full_duplex_enabled=0");
        var toggle = c.F.Rig.ToggleFullDuplexAsync();
        Field(c.F.Rig,"theRadio",replacement.F.Vendor.Radio);
        Assert.Equal("audio.full_duplex.unconfirmed",await c.Pump(toggle));
        Assert.DoesNotContain(c.F.Vendor.Transport.Commands,x=>x.StartsWith("radio set full_duplex"));
        Assert.DoesNotContain(replacement.F.Vendor.Transport.Commands,x=>x.StartsWith("radio set full_duplex"));
        Assert.Null(c.F.Rig.FullDuplexAvailable);
        Field(c.F.Rig,"theRadio",c.F.Vendor.Radio);
    }

}
