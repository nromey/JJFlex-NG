using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.Listenback;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ListenbackRoundTwoTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ListenbackRoundTwoTests));
    public void Dispose() => _scope.Dispose();

    internal static void Field(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    internal static void Property(object target, string name, object value) =>
        target.GetType().GetProperty(name)!.GetSetMethod(true)!.Invoke(target, new[] { value });

    internal sealed class RadioCase : IDisposable
    {
        internal readonly RigOnVendorRadio F;
        internal readonly RadioRecoveryStore Store = new(Path.Combine(RadioConfig.AppDataRoot, "listenback-recovery"));
        internal Slice Tx, Ears;
        internal RadioCase(string? serial = null)
        {
            F = new(serial ?? "round-two-" + Guid.NewGuid().ToString("N"));
            Field(F.Vendor.Radio, "_clientHandle", RigOnVendorRadio.OurHandle);
            Field(F.Vendor.Radio, "_guiClientID", "persistent-gui");
            Property(F.Vendor.Radio, "Model", "FLEX-8600");
            F.Rig.q.MainLoop = true;
            Tx = Add(1, 14.2);
            Field(Tx, "_active", true); Field(Tx, "_isTransmitSlice", true);
            Field(F.Rig, "_RXVFO", 0); Field(F.Rig, "_TXVFO", 0);
            F.Vendor.Transport.CommandWritten = Respond;
        }
        internal Slice Add(int index, double frequency)
        {
            var s = F.AddOwnSlice(index, frequency, "USB");
            var pan = (Panadapter)Activator.CreateInstance(typeof(Panadapter), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { F.Vendor.Radio }, null)!;
            Property(pan, "StreamID", (uint)(0x40000000 + index));
            Property(pan, "ClientHandle", RigOnVendorRadio.OurHandle);
            Property(s, "Panadapter", pan);
            Field(s, "_tx_ant_list", new[] { "ANT1", "XVT-A", "XVT-B" });
            Field(s, "_txant", "ANT1");
            F.Vendor.Radio.SliceList.Add(s); F.Vendor.Radio.PanadapterList.Add(pan);
            var own = (List<Slice>)typeof(FlexBase).GetField("mySlices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(F.Rig)!;
            own.Sort((a, b) => a.Index.CompareTo(b.Index));
            ((List<Panadapter>)typeof(FlexBase).GetField("myPanAdapters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(F.Rig)!).Add(pan);
            return s;
        }
        internal void Respond(string command)
        {
            if (command == "sub radio all") F.Vendor.Status("radio full_duplex_enabled=" + (F.Vendor.Radio.FullDuplexEnabled ? "1" : "0"));
            if (command == "sub tx all") F.Vendor.Status($"transmit rfpower={F.Vendor.Radio.RFPower} sb_monitor={(F.Vendor.Radio.TXMonitor ? 1 : 0)}");
            if (command == "sub slice all")
                foreach (var slice in F.Vendor.Radio.SliceList)
                    F.Vendor.Status($"slice {slice.Index} client_handle=0x{slice.ClientHandle:X} pan=0x{slice.Panadapter?.StreamID:X} txant={slice.TXAnt} RF_frequency={slice.Freq.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} mode={slice.DemodMode} rxant={slice.RXAnt}");
            if (command == "sub pan all")
                foreach (var pan in F.Vendor.Radio.PanadapterList)
                    F.Vendor.Status($"display pan 0x{pan.StreamID:X} client_handle=0x{pan.ClientHandle:X} center={pan.CenterFreq.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} rxant={pan.RXAnt}");
            if (command.StartsWith("radio set ")) F.Vendor.Status("radio " + command[10..]);
            if (command.StartsWith("transmit set ")) F.Vendor.Status("transmit " + command[13..].Replace("mon=", "sb_monitor="));
            if (command.StartsWith("slice set ")) F.Vendor.Status("slice " + command[10..]);
            if (command.StartsWith("display pan set ")) F.Vendor.Status("display pan " + command[16..]);
            if (command.StartsWith("slice remove "))
            {
                int id = int.Parse(command[13..]);
                F.Vendor.Status($"slice {id} in_use=0");
                Field(F.Rig, "mySliceRemoved", true);
            }
            if (command.StartsWith("display pan remove ")) F.Vendor.Status("display pan " + command[19..] + " removed");
            if (command.StartsWith("stream remove ")) F.Vendor.Status("stream " + command[14..] + " removed");
            F.Vendor.ReplyOk(F.Vendor.LastSequence);
        }
        internal RadioRecoverySnapshot Snapshot(bool reconnect = false)
        {
            // Exact off-air setting shape from StartListenbackAsync, including
            // the unconditional pan-iq-channel entry the old test omitted.
            Ears = Add(0, 14.2);
            Field(F.Rig, "_RXVFO", 1); Field(F.Rig, "_TXVFO", 1);
            Field(Tx, "_txant", "XVT-A");
            Field(Ears.Panadapter, "_daxIQChannel", 1);
            var r = F.Vendor.Radio;
            var s = new RadioRecoverySnapshot { Serial = r.Serial, ClientHandle = r.ClientHandle,
                ConnectionId = reconnect ? Guid.NewGuid() : r.CurrentCommandConnection.Id, TakeDirectory = "take" };
            s.ResourceNotes.Add("preexisting-slice-indices=1"); s.ResourceNotes.Add("preexisting-pan-ids=" + Tx.Panadapter.StreamID);
            s.Settings.Add(new("full-duplex", "0", "radio set full_duplex_enabled=0", "radio", "full_duplex_enabled"));
            s.Settings.Add(new("tx-antenna", "ANT1", "slice set 1 txant=ANT1", "slice 1", "txant", "slice", 1));
            s.Settings.Add(new("rf-power", "20", "transmit set rfpower=20", "transmit", "rfpower"));
            s.Settings.Add(new("monitor", "0", "transmit set mon=0", "transmit", "sb_monitor"));
            s.Settings.Add(new("created-pan", "removed", "display pan remove 0x40000000", "display pan 0x40000000", "", "pan", 0x40000000));
            s.Settings.Add(new("created-slice", "removed", "slice remove 0", "slice 0", "", "slice", 0));
            s.Settings.Add(new("pan-iq-channel", "0", "display pan set 0x40000000 daxiq_channel=0", "display pan 0x40000000", "daxiq_channel", "pan", 0x40000000));
            s.Settings.Add(new("created-stream", "removed", "stream remove 0x20000000", "stream 0x20000000", "", "stream", 0x20000000));
            Store.Save(s); F.Vendor.Transport.Written.Clear();
            return s;
        }
        internal async Task<T> Pump<T>(Task<T> task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                while (F.Rig.q.Count > 0) if (F.Rig.q.Dequeue().Item is FlexBase.FunctionDel action) action();
                await Task.Delay(5);
            }
            return await task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        public void Dispose() => F.Dispose();
    }

    [Fact]
    public async Task Operator_restore_of_the_real_shape_on_the_same_connection_clears_the_record()
    {
        using var c = new RadioCase(); var s = c.Snapshot();
        Assert.True(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(c.F.Rig.PendingListenbackRecovery, true)));
        Assert.Null(c.Store.Find(s.Serial));
        Assert.Contains("slice set 1 txant=ANT1", c.F.Vendor.Transport.Commands);
        Assert.Contains("slice remove 0", c.F.Vendor.Transport.Commands);
    }
    [Fact]
    public async Task Reconnect_restore_of_the_real_shape_can_confirm_already_removed_resources()
    {
        using var c = new RadioCase(); var s = c.Snapshot(true);
        c.F.Vendor.Status("slice 0 in_use=0"); c.F.Vendor.Status("display pan 0x40000000 removed");
        Field(c.Tx, "_txant", "ANT1");
        Assert.True(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(s, true)));
        Assert.Null(c.Store.Find(s.Serial));
        Assert.DoesNotContain("slice remove 0", c.F.Vendor.Transport.Commands);
    }
    [Fact]
    public async Task Close_restore_waits_for_the_radio_unkey_round_trip()
    {
        using var c = new RadioCase(); var s = c.Snapshot();
        Field(c.F.Rig, "_Transmit", true);
        var restore = c.F.Rig.RestoreListenbackSnapshotAsync(s, true);
        await Task.Delay(50);
        Assert.False(restore.IsCompleted);
        Assert.Empty(c.F.Vendor.Transport.Commands);
        Field(c.F.Rig, "_Transmit", false);
        Assert.True(await c.Pump(restore));
        Assert.Null(c.Store.Find(s.Serial));
    }
    [Fact]
    public async Task Closing_a_run_drains_capture_then_waits_for_reported_unkey_and_restores()
    {
        using var c = new RadioCase(); var s = c.Snapshot();
        string directory = Path.Combine(RadioConfig.AppDataRoot, "close-run");
        await using var capture = new IqRadioCapture(c.F.Vendor.Radio, c.Ears.Panadapter, 1, 24000,
            directory, new Dictionary<string, string>(), action => { action(); return Task.CompletedTask; }, manageRadioResources: false);
        c.F.Vendor.Status($"stream 0x20000000 type=dax_iq client_handle=0x{RigOnVendorRadio.OurHandle:X} daxiq_channel=1 daxiq_rate=24000 active=1");
        bool detached = false;
        var run = new ListenbackRun(capture, () => c.F.Rig.RestoreListenbackSnapshotAsync(s, true), () => detached = true);
        Field(c.F.Rig, "_Transmit", true);
        var stop = run.StopAsync("dialog-closed");
        await capture.Ended.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(30);
        Assert.True(detached); Assert.False(stop.IsCompleted); Assert.False(run.Restored);
        Assert.Empty(c.F.Vendor.Transport.Commands);
        Field(c.F.Rig, "_Transmit", false);
        async Task<bool> Finished() { await stop; return run.Restored; }
        Assert.True(await c.Pump(Finished()));
        Assert.Null(c.Store.Find(s.Serial));
    }
    [Fact]
    public async Task Stalled_queue_reports_timeout_and_expired_work_cannot_execute_later()
    {
        using var c = new RadioCase(); bool executed = false;
        c.F.Rig.ListenbackQueueWait = TimeSpan.FromMilliseconds(40);
        var queued = (Task)typeof(FlexBase).GetMethod("IqQueue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(c.F.Rig, new object[] { (Action)(() => executed = true) })!;
        var error = await Assert.ThrowsAsync<TimeoutException>(() => queued);
        Assert.Equal("command-queue-stalled", error.Message);
        while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel action) action();
        Assert.False(executed); Assert.Empty(c.F.Vendor.Transport.Commands);
    }
    [Fact]
    public async Task One_scu_remote_off_air_is_refused_before_preflight_or_mutation()
    {
        using var c = new RadioCase();
        Property(c.F.Vendor.Radio, "Model", "FLEX-6300");
        Property(c.F.Vendor.Radio, "IsWan", true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.F.Rig.StartListenbackAsync(new ListenbackOptions(OffAir: true), default));
        Assert.Equal("one-scu-not-confirmed", error.Message);
        Assert.Empty(c.F.Vendor.Transport.Commands); Assert.Equal(0, c.F.Rig.q.Count);
        Assert.Null(c.F.Rig.PendingListenbackRecovery);
    }
    [Fact]
    public async Task Hardware_hold_times_out_without_writes_then_retry_can_restore()
    {
        using var c = new RadioCase(); var s = c.Snapshot();
        c.F.Rig.ListenbackUnkeyWait = TimeSpan.FromMilliseconds(60);
        Field(c.F.Rig, "_Transmit", true);
        Assert.False(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(s, true)));
        Assert.NotNull(c.Store.Find(s.Serial)); Assert.Empty(c.F.Vendor.Transport.Commands);
        Field(c.F.Rig, "_Transmit", false);
        Assert.True(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(s, true)));
    }
    [Fact]
    public async Task Active_listener_returns_to_the_prior_slice_and_does_not_lock_future_takes()
    {
        using var c = new RadioCase(); var s = c.Snapshot();
        s.PriorActiveSlice = 1; c.Store.Save(s);
        Field(c.Tx, "_active", false); Field(c.Ears, "_active", true); Field(c.F.Rig, "_RXVFO", 0);
        Assert.True(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(s, true)));
        Assert.Null(c.Store.Find(s.Serial));
        var commands = c.F.Vendor.Transport.Commands;
        Assert.True(commands.IndexOf("slice set 1 active=1") < commands.IndexOf("slice remove 0"));
        Assert.DoesNotContain("slice remove 1", commands);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconnect_matches_recorded_resources_and_refuses_a_changed_replacement(bool replacement)
    {
        using var original = new RadioCase(); var s = original.Snapshot();
        s.GuiClientId = "persistent-gui";
        foreach (var slice in new[] { original.Tx, original.Ears })
            s.Resources.Add(new("slice", (uint)slice.Index, slice.ClientHandle, slice.Panadapter.StreamID, slice.Freq, slice.DemodMode, slice.RXAnt));
        s.Resources.Add(new("pan", original.Ears.Panadapter.StreamID, original.Ears.ClientHandle, original.Ears.Panadapter.StreamID,
            original.Ears.Panadapter.CenterFreq, "", original.Ears.Panadapter.RXAnt));
        original.Store.Save(s);
        Property(original.F.Vendor.Radio, "Connected", false);
        using var c = new RadioCase(s.Serial);
        c.Ears = c.Add(0, 14.2);
        Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
        Field(c.Tx, "_txant", "XVT-A"); Field(c.Ears.Panadapter, "_daxIQChannel", 1);
        const uint newHandle = RigOnVendorRadio.OurHandle + 7;
        Field(c.F.Vendor.Radio, "_clientHandle", newHandle); Field(c.F.Rig, "clientHandle", newHandle);
        foreach (var slice in new[] { c.Tx, c.Ears })
        { Property(slice, "ClientHandle", newHandle); Property(slice.Panadapter, "ClientHandle", newHandle); }
        Assert.NotSame(original.Ears, c.Ears);
        Assert.NotEqual(s.ConnectionId, c.F.Vendor.Radio.CurrentCommandConnection.Id);
        s = c.F.Rig.PendingListenbackRecovery; // disk discovery, as the connect offer uses
        if (replacement) VendorRadioFixture.SetFrequencyCache(c.Ears, 7.1);
        Assert.Equal(!replacement, await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(s, true)));
        if (replacement)
        {
            Assert.DoesNotContain("slice remove 0", c.F.Vendor.Transport.Commands);
            Assert.DoesNotContain("display pan remove 0x40000000", c.F.Vendor.Transport.Commands);
            Assert.NotNull(c.Store.Find(s.Serial));
        }
        else Assert.Null(c.Store.Find(s.Serial));
    }
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"Format\":1,\"Serial\":\"another-radio\",\"TakeDirectory\":\"take\"}")]
    public async Task Unreadable_record_is_preserved_only_after_explicit_inspection_and_never_executes_commands(string text)
    {
        using var c = new RadioCase();
        string path = c.Store.PathFor(c.F.Vendor.Radio.Serial);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text);
        var offered = c.F.Rig.PendingListenbackRecovery;
        Assert.NotNull(offered.UnreadableHash);
        Assert.False(await c.F.Rig.RestoreListenbackSnapshotAsync(offered, true));
        Assert.False(c.F.Rig.AcknowledgeUnreadableListenbackRecovery(offered, false));
        Assert.Empty(c.F.Vendor.Transport.Commands);
        Assert.True(c.F.Rig.AcknowledgeUnreadableListenbackRecovery(offered, true));
        Assert.Null(c.F.Rig.PendingListenbackRecovery);
        string archived = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".unreadable-*").Single();
        Assert.Equal(text, File.ReadAllText(archived));
    }
    [Fact]
    public void An_old_unreadable_offer_cannot_archive_a_repaired_or_newer_record()
    {
        using var c = new RadioCase(); string path = c.Store.PathFor(c.F.Vendor.Radio.Serial);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{");
        var offered = c.F.Rig.PendingListenbackRecovery;
        c.Snapshot();
        Assert.False(c.F.Rig.AcknowledgeUnreadableListenbackRecovery(offered, true));
        Assert.NotNull(c.F.Rig.PendingListenbackRecovery);
    }
    [Fact]
    public void Removing_only_the_listener_still_releases_its_owned_pan()
    {
        using var c = new RadioCase(); var ears = c.Add(0, 14.2);
        var resources = new LoopbackSliceResources(new[] { c.Tx }, new[] { c.Tx.Panadapter });
        Assert.True(resources.Capture(c.F.Vendor.Radio.SliceList, c.F.Vendor.Radio.PanadapterList));
        c.F.Vendor.Status("slice 0 in_use=0");
        c.F.Vendor.Transport.Written.Clear();
        Assert.True(resources.Release(c.F.Vendor.Radio.SliceList, c.F.Vendor.Radio.PanadapterList, c.Tx, c.Tx));
        Assert.Contains("display pan remove 0x40000000", c.F.Vendor.Transport.Commands, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("slice remove 0", c.F.Vendor.Transport.Commands);
    }
    [Fact]
    public void Receive_time_room_audio_is_excluded_without_collapsing_the_reference_clock()
    {
        var sent = Enumerable.Repeat(.99f, 1200).ToArray(); Array.Fill(sent, .1f, 400, 400);
        var keyed = TakeAudio.KeepOnlySpans(sent, new[] { new TakeSpan(400, 800) });
        Assert.Equal(sent.Length, keyed.Length);
        Assert.All(keyed[..400], value => Assert.Equal(0, value));
        Assert.All(keyed[400..800], value => Assert.Equal(.1f, value));
        Assert.All(keyed[800..], value => Assert.Equal(0, value));
    }
    [Fact]
    public async Task Sparse_loopback_integration_identifies_its_reply_pan_and_reserves_queued_removal()
    {
        using var c = new RadioCase(); var d = c.Add(3, 7.1);
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create"))
            {
                c.Ears = c.Add(0, 1.8); c.Add(2, 3.6); // simultaneous unrelated own creation
                Field(c.F.Rig, "mySliceAdded", true);
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else c.Respond(command);
        };
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
        Assert.Equal(14.2, c.Ears.Freq); Assert.Equal(7.1, d.Freq);
        var end = c.F.Rig.EndLoopbackArrangementAsync();
        while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
        await c.Pump(end);
        Assert.True(c.F.Rig.NewSlice()); // pending removal must count before the queue runs
        // Do not execute the new creation request; it only tests the capacity gate.
        while (c.F.Rig.q.Count > 0)
        {
            var next = c.F.Rig.q.Dequeue();
            if (next.Item is FlexBase.FunctionDel a && !c.F.Vendor.Transport.Commands.Contains("slice remove 0")) a();
        }
        Assert.Contains("slice remove 0", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("slice remove 3", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("slice remove 2", c.F.Vendor.Transport.Commands);
    }
}
