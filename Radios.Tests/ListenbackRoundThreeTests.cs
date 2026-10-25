using System;
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
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ListenbackRoundThreeTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ListenbackRoundThreeTests));
    public void Dispose() => _scope.Dispose();

    private static async Task<bool> Stop(ListenbackRun run)
    { await run.StopAsync("dialog-closed"); return run.Restored; }

    [Theory]
    [InlineData(false, true, false, false, false, false, "0,32,8", false)]
    [InlineData(true, true, false, false, false, false, "0,32,8", false)]
    [InlineData(true, true, false, false, true, false, "0,48,8", false)]
    [InlineData(true, true, false, false, true, true, "0,32,8", false)]
    [InlineData(true, true, false, false, false, false, null, false)]
    [InlineData(true, true, false, false, false, false, "0,32,8", true)]
    [InlineData(false, false, false, false, false, false, "0,32,8", false)]
    [InlineData(false, true, true, false, false, false, "0,32,8", false)]
    [InlineData(false, true, true, true, false, false, "0,32,8", false)]
    public async Task Start_records_the_confirmed_created_antenna_and_operator_restore_can_remove_it(bool offAir, bool separate, bool holdEcho, bool cancel, bool retainEars, bool hearSelf, string gainReply, bool backToBack)
    {
        using var c = new RadioCase(); var radio = c.F.Vendor.Radio;
        if (backToBack) c.F.Vendor.Status("transmit rfpower=20 sb_monitor=1");
        string priorTxAnt = c.Tx.TXAnt;
        int priorPower = radio.RFPower;
        bool priorMonitor = radio.TXMonitor, priorDuplex = radio.FullDuplexEnabled;
        bool restoringWithoutEcho = false;
        Slice alreadyMuted = null;
        if (offAir)
        {
            alreadyMuted = c.Add(2, 7.1);
            Field(alreadyMuted, "_mute", true);
        }
        using var cancellation = new CancellationTokenSource();
        var packets = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!separate) c.Ears = c.Tx;
        Field(c.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (restoringWithoutEcho && (command.StartsWith("transmit set ") ||
                command == "slice set 1 txant=ANT1" || command == "slice set 1 audio_mute=0"))
            {
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence); // accepted by the radio, no FlexLib status echo
                return;
            }
            if (command.StartsWith("display panafall create"))
            {
                c.Ears = c.Add(0, 1.8);
                Field(c.Ears.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
                c.F.Vendor.Status($"slice 0 in_use=1 client_handle=0x{radio.ClientHandle:X} pan=0x40000000 RF_frequency=1.8 mode={(retainEars ? "LSB" : "USB")} rxant=ANT1");
                c.F.Vendor.Status($"display pan 0x40000000 client_handle=0x{radio.ClientHandle:X} center=1.8 rxant=ANT1");
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else if (command.StartsWith("stream create type=dax_iq"))
            {
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
                // A separate status, after the pan setter and the old optimistic
                // snapshot. The pan setter itself never updates Slice.RXAnt.
                if (separate && !holdEcho) c.F.Vendor.Status($"slice 0 RF_frequency={c.Tx.Freq.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} mode={c.Tx.DemodMode} rxant=XVT-B");
                c.F.Vendor.Status($"stream 0x20000000 type=dax_iq client_handle=0x{radio.ClientHandle:X} daxiq_channel=1 daxiq_rate=24000 active=1 pan=0x{c.Ears.Panadapter.StreamID:X} payload_endian=little");
            }
            else if (command.StartsWith("display pan rfgain_info 0x"))
            {
                if (gainReply != null) c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, gainReply);
            }
            else
            {
                c.Respond(command);
                if (command == "slice tune 0 1.800000") c.F.Vendor.Status("slice 0 RF_frequency=1.800000");
                if (c.Ears != null && command == $"display pan set 0x{c.Ears.Panadapter.StreamID:X} daxiq_channel=1")
                {
                    var stream = radio.DAXIQStreamList.Single();
                    var random = new Random(81);
                    var packet = new VitaIFDataPacket { StreamId = stream.StreamID, TimestampInt = 100,
                        payload = Enumerable.Range(0, 16384).Select(_ => random.Next(-30, 31) * 16f / 32768).ToArray() };
                    packet.ClassId.PacketClassCode = 0x02e3;
                    typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.Instance | BindingFlags.NonPublic, null,
                        new[] { typeof(VitaIFDataPacket) }, null)!.Invoke(stream, new object[] { packet });
                    packet.Header.packet_count = 1;
                    packet.TimestampFrac += (ulong)Math.Round(8192 * 1e12 / 24000);
                    typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.Instance | BindingFlags.NonPublic, null,
                        new[] { typeof(VitaIFDataPacket) }, null)!.Invoke(stream, new object[] { packet });
                    packets.TrySetResult(true);
                }
            }
        };
        var start = c.F.Rig.StartListenbackAsync(new ListenbackOptions(SeparatePan: separate,
            ReceiveAntenna: separate ? "XVT-B" : null, OffAir: offAir,
            HearSelfDuringOffAir: hearSelf), cancellation.Token);
        var pumped = c.Pump(start);
        if (holdEcho)
        {
            await packets.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(40);
            Assert.False(start.IsCompleted);
            var pending = c.F.Rig.PendingListenbackRecovery;
            Assert.DoesNotContain(pending.Resources, r => r.Kind == "slice" || r.Kind == "pan");
            Assert.Equal("XVT-B", pending.ListenerRequest.ReceiveAntenna);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pumped);
                Assert.Null(c.F.Rig.PendingListenbackRecovery);
                return;
            }
            c.F.Vendor.Status("slice 0 RF_frequency=14.2 mode=USB rxant=XVT-B");
        }
        var run = await pumped;
        try
        {
            if (offAir)
            {
                int expectedGain = gainReply == "0,48,8" ? 48 : 32;
                var commands = c.F.Vendor.Transport.Commands.ToList();
                int antennaAt = commands.FindIndex(x => x == "slice set 0 rxant=XVT-B");
                int rangeAt = commands.FindLastIndex(x => x.StartsWith("display pan rfgain_info 0x"));
                Assert.True(antennaAt >= 0 && rangeAt > antennaAt);
                Assert.Contains($"slice set 0 rfgain={expectedGain}", commands);
                Assert.Equal(expectedGain.ToString(), c.F.Rig.PendingListenbackRecovery.Settings.Single(s =>
                    s.Name == "ears-rf-gain").RequestedValue);
                Assert.True(c.Tx.Mute);
                Assert.True(alreadyMuted.Mute);
                Assert.Equal(!hearSelf, c.F.Vendor.Transport.Commands.Contains("slice set 0 audio_mute=1"));
                Assert.Equal(!hearSelf, c.F.Rig.PendingListenbackRecovery.Settings.Any(s =>
                    s.Name == "ears-mute" && s.Value == "0" && s.RequestedValue == "1"));
                var purposes = (System.Collections.IDictionary)typeof(FlexBase)
                    .GetField("_diagnosticSlices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.F.Rig)!;
                Assert.Equal("OffAir", purposes[c.Ears]?.ToString());
            }
            Assert.False(c.F.Rig.AcknowledgeInspectedListenbackRecovery(c.F.Rig.PendingListenbackRecovery, true));
            c.F.Rig.ListenbackUnkeyWait = TimeSpan.FromMilliseconds(40);
            Field(c.F.Rig, "_Transmit", true);
            Assert.False(await c.Pump(Stop(run))); // retain the real production snapshot
            Field(c.F.Rig, "_Transmit", false);
            var snapshot = c.F.Rig.PendingListenbackRecovery;
            Assert.NotEmpty(snapshot.Resources);
            if (separate) Assert.Contains("receive antenna XVT-B", ListenbackInspectionSummary.Describe(snapshot));
            if (offAir)
            {
                Assert.Equal("XVT-A", snapshot.Settings.Single(s => s.Name == "tx-antenna").RequestedValue);
                Assert.Equal("1", snapshot.Settings.Single(s => s.Name == "rf-power").RequestedValue);
                Assert.Contains(snapshot.Settings, s => s.Name == "slice-mute-1" && s.Value == "0");
                Assert.Contains(snapshot.Settings, s => s.Name == "ears-frequency" && s.Value == "1.800000");
                Assert.Contains(snapshot.Settings, s => s.Name == "ears-mode" && s.Value == (retainEars ? "LSB" : "USB"));
                Assert.Contains(snapshot.Settings, s => s.Name == "ears-antenna" && s.Value == "ANT1");
                Assert.Contains(snapshot.Settings, s => s.Name == "ears-rf-gain" && s.Value == "0");
                Assert.Contains(snapshot.Settings, s => s.Name == "ears-pan-center" && s.Value == "1.800000");
            }
            // Legitimate tuning/mode/receive-antenna changes do not change
            // the identity of the operator's own transmit slice and pan.
            c.F.Vendor.Status("slice 1 RF_frequency=7.1 mode=LSB rxant=ANT2");
            c.F.Vendor.Status("display pan 0x40000001 center=7.1 rxant=ANT2");
            if (retainEars) Field(c.Ears, "_isTransmitSlice", true);
            c.F.Vendor.Transport.Written.Clear();
            restoringWithoutEcho = backToBack;
            Assert.Equal(!retainEars, await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(snapshot, true)));
            restoringWithoutEcho = false;
            Assert.Equal(retainEars, c.F.Rig.PendingListenbackRecovery != null);
            if (offAir)
            {
                if (!backToBack) Assert.False(c.Tx.Mute);
                Assert.True(alreadyMuted.Mute);
                Assert.Contains("slice set 1 audio_mute=0", c.F.Vendor.Transport.Commands);
                if (!backToBack)
                {
                    Assert.Equal(priorTxAnt, c.Tx.TXAnt);
                    Assert.Equal(priorPower, radio.RFPower);
                    Assert.Equal(priorMonitor, radio.TXMonitor);
                }
                Assert.Equal(priorDuplex, radio.FullDuplexEnabled);
                string restore = File.ReadAllText(Directory.GetFiles(run.DirectoryPath, "restore-*.txt")
                    .OrderByDescending(File.GetLastWriteTimeUtc).First());
                Assert.Contains(restore.Split('\n'), line => line.Contains("\"Phase\":\"reply\",\"Detail\":{\"Name\":\"slice-mute-1\"") &&
                    line.Contains("\"Code\":\"00000000\""));
                Assert.Contains("\"Phase\":\"verdict\",\"Detail\":{\"Name\":\"slice-mute-1\",\"Confirmed\":true", restore);
                if (retainEars)
                {
                    Assert.Equal(!hearSelf, c.F.Vendor.Transport.Commands.Contains("slice set 0 audio_mute=0"));
                    Assert.False(c.Ears.Mute);
                    Assert.Equal("ANT1", c.Ears.RXAnt);
                    Assert.Equal(0, c.Ears.RFGain);
                    Assert.Equal(1.8, c.Ears.Freq, 6);
                    Assert.Equal("LSB", c.Ears.DemodMode);
                    Assert.Equal(1.8, c.Ears.Panadapter.CenterFreq, 6);
                    Assert.Contains("slice set 0 rxant=ANT1", c.F.Vendor.Transport.Commands);
                    Assert.Contains("slice set 0 rfgain=0", c.F.Vendor.Transport.Commands);
                    Assert.Contains("slice set 0 mode=LSB", c.F.Vendor.Transport.Commands);
                    Assert.Contains("slice tune 0 1.800000", c.F.Vendor.Transport.Commands);
                    Assert.Contains("display pan set 0x40000000 center=1.800000", c.F.Vendor.Transport.Commands);
                    foreach (string name in new[] { "ears-antenna", "ears-rf-gain", "ears-mode", "ears-frequency", "ears-pan-center" })
                        Assert.Contains(restore.Split('\n'), line => line.Contains("\"Phase\":\"reply\",\"Detail\":{\"Name\":\"" + name + "\"") &&
                            line.Contains("\"Code\":\"00000000\""));
                    if (!hearSelf)
                        Assert.Contains(restore.Split('\n'), line => line.Contains("\"Phase\":\"reply\",\"Detail\":{\"Name\":\"ears-mute\"") &&
                            line.Contains("\"Code\":\"00000000\""));
                }
            }
            Assert.DoesNotContain("slice remove 1", c.F.Vendor.Transport.Commands);
            if (separate && !retainEars)
            {
                Assert.Contains("slice remove 0", c.F.Vendor.Transport.Commands);
                Assert.Equal("XVT-B", snapshot.Resources.Single(r => r.Kind == "slice" && r.Id == 0).Antenna);
            }
            if (!offAir) Assert.DoesNotContain(snapshot.Resources, r => r.Kind == "slice" && r.Id == 1);
            if (backToBack)
            {
                // The raw restore replies succeeded, but FlexLib still reports
                // the take's old setup values because no status echoed them.
                Assert.True(c.Tx.Mute);
                Assert.Equal("XVT-A", c.Tx.TXAnt);
                Assert.Equal(1, radio.RFPower);
                Assert.False(radio.TXMonitor);
                c.F.Vendor.Transport.Written.Clear();
                var next = await c.Pump(c.F.Rig.StartListenbackAsync(new ListenbackOptions(OffAir: true), default));
                try
                {
                    var prior = c.F.Rig.PendingListenbackRecovery;
                    Assert.Equal("ANT1", prior.Settings.Single(s => s.Name == "tx-antenna").Value);
                    Assert.Equal("20", prior.Settings.Single(s => s.Name == "rf-power").Value);
                    Assert.Equal("1", prior.Settings.Single(s => s.Name == "monitor").Value);
                    Assert.Contains(prior.Settings, s => s.Name == "slice-mute-1" && s.Value == "0");
                    var commands = c.F.Vendor.Transport.Commands;
                    Assert.Contains("slice set 1 txant=XVT-A", commands);
                    Assert.Contains("transmit set rfpower=1", commands);
                    Assert.Contains("transmit set mon=0", commands);
                    Assert.Contains("slice set 1 audio_mute=1", commands);
                }
                finally { await c.Pump(Stop(next)); }
            }
        }
        finally { Field(c.F.Rig, "_Transmit", false); await c.Pump(Stop(run)); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspection_clears_an_unprovable_record_only_after_acknowledgement_without_radio_commands(bool creationPending)
    {
        using var c = new RadioCase(); var snapshot = c.Snapshot(true);
        snapshot.GuiClientId = "previous-gui"; snapshot.ResourceCreationPending = creationPending;
        snapshot.Settings[1] = snapshot.Settings[1] with { RequestedValue = "XVT-A" };
        c.Store.Save(snapshot);
        Assert.False(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(c.F.Rig.PendingListenbackRecovery, true)));
        var offered = c.F.Rig.PendingListenbackRecovery;
        string path = c.Store.PathFor(snapshot.Serial); byte[] before = File.ReadAllBytes(path);
        c.F.Vendor.Transport.Written.Clear();
        Assert.False(c.F.Rig.AcknowledgeInspectedListenbackRecovery(offered, false));
        Assert.NotNull(c.F.Rig.PendingListenbackRecovery);
        string description = ListenbackInspectionSummary.Describe(offered);
        Assert.Contains("XVT-A", description); Assert.Contains("ANT1", description);
        Assert.Contains("slice 0", description); Assert.Contains("pan 0x40000000", description);
        Assert.Contains("Full duplex", description); Assert.Contains("power", description);
        if (creationPending) Assert.Contains("identity was not recorded", description);
        Assert.True(c.F.Rig.AcknowledgeInspectedListenbackRecovery(offered, true));
        Assert.Null(c.F.Rig.PendingListenbackRecovery); Assert.Empty(c.F.Vendor.Transport.Commands);
        string archived = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".acknowledged-*").Single();
        Assert.Equal(before, File.ReadAllBytes(archived));
        // The next take gets to its hardware gate; recovery no longer locks it.
        Property(c.F.Vendor.Radio, "Model", "FLEX-6300");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => c.F.Rig.StartListenbackAsync(new(OffAir: true), default));
        Assert.Equal("one-scu-not-confirmed", error.Message);
    }
    [Fact]
    public void Inspection_of_an_old_offer_cannot_clear_revised_settings_even_in_the_same_take()
    {
        using var c = new RadioCase(); c.Snapshot(); var offered = c.F.Rig.PendingListenbackRecovery;
        var changed = c.F.Rig.PendingListenbackRecovery;
        changed.Settings[1] = changed.Settings[1] with { Value = "ANT2" }; c.Store.Save(changed);
        Assert.False(c.F.Rig.AcknowledgeInspectedListenbackRecovery(offered, true));
        Assert.Equal("ANT2", c.F.Rig.PendingListenbackRecovery.Settings[1].Value);
    }
    [Theory]
    [InlineData("preparation cancelled")]
    [InlineData("preflight cancelled")]
    [InlineData("playback failed")]
    [InlineData("sent playback failed")]
    [InlineData("comparison failed")]
    public async Task Close_failure_keeps_its_actual_text_after_cleanup_and_never_invents_a_retained_record(string failure)
    {
        using var c = new RadioCase(); c.Snapshot();
        var pending = ListenbackCloseNotice.ForCurrentRecord(c.F.Rig.PendingListenbackRecovery, failure, true);
        Assert.Equal(Lexicon.Get("audio.iq.recovery_pending"), pending.Text);
        Assert.True(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(c.F.Rig.PendingListenbackRecovery, true)));
        var cleared = ListenbackCloseNotice.ForCurrentRecord(c.F.Rig.PendingListenbackRecovery, failure, true);
        Assert.Equal(failure, cleared.Text);
        var oldPending = ListenbackCloseNotice.ForCurrentRecord(null, pending.Text, true);
        Assert.Equal(Lexicon.Get("audio.iq.no_recovery"), oldPending.Text);
    }
    [Fact]
    public void Creation_evidence_merges_only_radio_status_and_requires_every_identity_field()
    {
        const string slice = "S0|slice 0 client_handle=0x123 pan=0x40000000 RF_frequency=14.2 mode=USB rxant=ANT1";
        Assert.Null(ListenbackCreationEvidence.Read("slice", 0, new[] { "R1|0|", "C1|slice set 0 rxant=XVT-B" }));
        var reported = ListenbackCreationEvidence.Read("slice", 0, new[] { slice, "S0|slice 0 rxant=XVT-B", "S0|slice 1 rxant=ANT2" });
        Assert.Equal("XVT-B", reported.Antenna); Assert.Equal(14.2, reported.Frequency);
        Assert.Null(ListenbackCreationEvidence.Read("slice", 0, new[] { slice, "S0|slice 0 in_use=0", "S0|slice 0 rxant=XVT-B" }));
    }
    [Fact]
    public void Close_and_recovery_entry_points_use_the_shared_delivery_and_review_paths()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "JJFlexRadio.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string workshop = File.ReadAllText(Path.Combine(root!.FullName, "JJFlexWpf/Dialogs/AudioWorkshopDialog.Listenback.cs"));
        string prompt = File.ReadAllText(Path.Combine(root.FullName, "JJFlexWpf/Dialogs/ListenbackRecoveryPrompt.cs"));
        Assert.Contains("if (failure) PostClosedIqStatus(text, true)", workshop);
        Assert.DoesNotContain("AnnounceClosedRecovery(false)", workshop);
        int stopped = workshop.IndexOf("await _iqRun.StopAsync(\"cancelled\")", StringComparison.Ordinal);
        int announced = workshop.IndexOf("AnnounceClosedRecovery(_iqRun.Restored)", stopped, StringComparison.Ordinal);
        int returned = workshop.IndexOf("_iqRun = null; return;", stopped, StringComparison.Ordinal);
        Assert.InRange(announced, stopped + 1, returned - 1);
        int post = workshop.IndexOf("Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle", StringComparison.Ordinal);
        Assert.True(workshop.IndexOf("ForCurrentRecord(readRecovery?.Invoke()", StringComparison.Ordinal) > post);
        Assert.Contains("await ListenbackRecoveryPrompt.ReviewAsync(_rig)", workshop);
        Assert.Contains("await ReviewAsync(rig)", prompt);
        Assert.Equal(1, prompt.Split("ListenbackRecoveryAcknowledgement.Offer(").Length - 1);
    }

    [Fact]
    public void Close_reads_the_take_record_even_after_disconnect_clears_the_radio_reference()
    {
        using var c = new RadioCase();
        var read = c.F.Rig.CaptureListenbackRecoveryReader();
        Assert.Null(read());
        c.Snapshot();
        Field(c.F.Rig, "theRadio", null!);
        Assert.Null(c.F.Rig.PendingListenbackRecovery);
        var notice = ListenbackCloseNotice.ForCurrentRecord(read(), Lexicon.Get("audio.iq.no_recovery"), true);
        Assert.Equal(Lexicon.Get("audio.iq.recovery_pending"), notice.Text);
    }

}
