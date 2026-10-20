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
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    public async Task Start_records_the_confirmed_created_antenna_and_operator_restore_can_remove_it(bool offAir, bool separate, bool holdEcho, bool cancel)
    {
        using var c = new RadioCase(); var radio = c.F.Vendor.Radio;
        using var cancellation = new CancellationTokenSource();
        var packets = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!separate) c.Ears = c.Tx;
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
            else if (command.StartsWith("stream create type=dax_iq"))
            {
                // A separate status, after the pan setter and the old optimistic
                // snapshot. The pan setter itself never updates Slice.RXAnt.
                if (separate && !holdEcho) c.F.Vendor.Status("slice 0 RF_frequency=14.2 mode=USB rxant=XVT-B");
                c.F.Vendor.Status($"stream 0x20000000 type=dax_iq client_handle=0x{radio.ClientHandle:X} daxiq_channel=1 daxiq_rate=24000 active=1 pan=0x{c.Ears.Panadapter.StreamID:X} payload_endian=little");
            }
            else
            {
                c.Respond(command);
                if (c.Ears != null && command == $"display pan set 0x{c.Ears.Panadapter.StreamID:X} daxiq_channel=1")
                {
                    var stream = radio.DAXIQStreamList.Single();
                    var random = new Random(81);
                    var packet = new VitaIFDataPacket { StreamId = stream.StreamID, TimestampInt = 100,
                        payload = Enumerable.Range(0, 16384).Select(_ => random.Next(-30, 31) * 16f / 32768).ToArray() };
                    packet.ClassId.PacketClassCode = 0x02e3;
                    typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.Instance | BindingFlags.NonPublic, null,
                        new[] { typeof(VitaIFDataPacket) }, null)!.Invoke(stream, new object[] { packet });
                    packets.TrySetResult(true);
                }
            }
        };
        var start = c.F.Rig.StartListenbackAsync(new ListenbackOptions(SeparatePan: separate,
            ReceiveAntenna: separate ? "XVT-B" : null, OffAir: offAir), cancellation.Token);
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
            }
            // Legitimate tuning/mode/receive-antenna changes do not change
            // the identity of the operator's own transmit slice and pan.
            c.F.Vendor.Status("slice 1 RF_frequency=7.1 mode=LSB rxant=ANT2");
            c.F.Vendor.Status("display pan 0x40000001 center=7.1 rxant=ANT2");
            c.F.Vendor.Transport.Written.Clear();
            Assert.True(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(snapshot, true)));
            Assert.Null(c.F.Rig.PendingListenbackRecovery);
            Assert.DoesNotContain("slice remove 1", c.F.Vendor.Transport.Commands);
            if (separate)
            {
                Assert.Contains("slice remove 0", c.F.Vendor.Transport.Commands);
                Assert.Equal("XVT-B", snapshot.Resources.Single(r => r.Kind == "slice" && r.Id == 0).Antenna);
            }
            Assert.DoesNotContain(snapshot.Resources, r => r.Kind == "slice" && r.Id == 1);
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
