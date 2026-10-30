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
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;
using static Radios.Tests.OffAirTakeRestoreTransmitTests;

namespace Radios.Tests;

/// <summary>
/// The off-air take's restore and the radio's restore owner, on a real take
/// (#835, round 1030 repair TD). A restore the radio's transmit held back is
/// run again by the owner once the radio receives; a take the radio confirmed
/// restored leaves the owner as done; what a disconnect leaves owed is the
/// take's own record, and only that is said.
/// </summary>
/// <remarks>
/// These use the repair's own surfaces (<c>FlexBase.TrackListenbackRunRestore</c>,
/// <c>ListenbackRunStoppedByItsWindow</c>, <c>CheckRestoreStillOwed</c>), so
/// they were made red by breaking each guarantee in the product, run, and put
/// back; the report lists the mutations. The bench's own shape, on what existed
/// at <c>72c727fe</c>, is <see cref="OffAirTakeRestoreTransmitTests"/>.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class OffAirTakeRestoreOwnerTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(OffAirTakeRestoreOwnerTests));
    public void Dispose() => _scope.Dispose();

    /// <summary>
    /// Start a real off-air take on the in-memory radio, as
    /// <c>ListenbackRoundThreeTests</c> does: a separate listening pan on the
    /// other transverter input, the transmit antenna on the transverter port,
    /// 1 watt, full duplex on.
    /// </summary>
    private static async Task<ListenbackRun> StartOffAir(RadioCase c)
    {
        var radio = c.F.Vendor.Radio;
        Field(c.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
        c.F.Vendor.Status("transmit rfpower=20 sb_monitor=0");
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create", StringComparison.Ordinal))
            {
                c.Ears = c.Add(0, 1.8);
                Field(c.Ears.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
                c.F.Vendor.Status($"slice 0 in_use=1 client_handle=0x{radio.ClientHandle:X} pan=0x40000000 RF_frequency=1.8 mode=USB rxant=ANT1");
                c.F.Vendor.Status($"display pan 0x40000000 client_handle=0x{radio.ClientHandle:X} center=1.8 rxant=ANT1");
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else if (command.StartsWith("stream create type=dax_iq", StringComparison.Ordinal))
            {
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
                c.F.Vendor.Status($"slice 0 RF_frequency={c.Tx.Freq.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} mode={c.Tx.DemodMode} rxant=XVT-B");
                c.F.Vendor.Status($"stream 0x20000000 type=dax_iq client_handle=0x{radio.ClientHandle:X} daxiq_channel=1 daxiq_rate=24000 active=1 pan=0x{c.Ears.Panadapter.StreamID:X} payload_endian=little");
            }
            else if (command.StartsWith("display pan rfgain_info 0x", StringComparison.Ordinal))
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0,32,8");
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
                    var add = typeof(RXAudioStream).GetMethod("AddRXData", BindingFlags.Instance | BindingFlags.NonPublic, null,
                        new[] { typeof(VitaIFDataPacket) }, null)!;
                    add.Invoke(stream, new object[] { packet });
                    packet.Header.packet_count = 1;
                    packet.TimestampFrac += (ulong)Math.Round(8192 * 1e12 / 24000);
                    add.Invoke(stream, new object[] { packet });
                }
            }
        };
        var run = await c.Pump(c.F.Rig.StartListenbackAsync(new ListenbackOptions(SeparatePan: true,
            ReceiveAntenna: "XVT-B", OffAir: true), CancellationToken.None));
        Assert.Equal("XVT-A", c.Tx.TXAnt); // positive control: the take really arranged the station
        Assert.True(c.F.Vendor.Radio.FullDuplexEnabled);
        c.F.Rig.ListenbackUnkeyWait = TimeSpan.FromMilliseconds(200);
        c.F.Vendor.Transport.Written.Clear();
        return run;
    }

    private static async Task<bool> Stopped(ListenbackRun run, string reason)
    { await run.StopAsync(reason); return run.Restored; }

    private static async Task<AudioCheckTeardownResult> Teardown(RadioCase c)
    {
        c.F.Rig.BeginCheckTeardown();
        try { return await c.Pump(c.F.Rig.SettleChecksForTeardownAsync(TimeSpan.FromMilliseconds(600))); }
        finally { c.F.Rig.EndCheckTeardown(); }
    }

    private static CheckRestoreReminderStore OwnStore(RadioCase c, ConnectBriefing briefing)
    {
        var store = new CheckRestoreReminderStore(Path.Combine(RadioConfig.AppDataRoot, "td-owner-" + Guid.NewGuid().ToString("N")));
        c.F.Rig.CheckRestoreRemindersOverride = store;
        c.F.Rig.CheckRestoreBriefingOverride = briefing;
        c.F.Rig.SuppressSpeech = false;
        return store;
    }

    // ── D1 in a live connection: the owner puts it back at receive ──────

    [Fact]
    public async Task A_take_stopped_under_the_hand_mic_is_put_back_by_the_owner_once_the_radio_receives()
    {
        using var c = new RadioCase("owner-retry-serial");
        var run = await StartOffAir(c);
        bool saidLater = false;
        var entry = c.F.Rig.TrackListenbackRunRestore(run, "off-air take", () => saidLater = true);

        HandMicKeys(c);
        Assert.False(await c.Pump(Stopped(run, "operator-stop")));
        Assert.True(run.RestoreHeldByTransmit);
        Assert.False(WroteTransmitSetting(c), "written under the hand mic's key: " + string.Join(", ", c.F.Vendor.Transport.Commands));

        // The window lets the owner have it; the owner asks, finds the key, and waits.
        c.F.Rig.ListenbackRunStoppedByItsWindow(entry, run);
        await Task.Delay(300);
        RunQueue(c);
        Assert.False(entry.Finished);
        Assert.False(WroteTransmitSetting(c), "the owner wrote while the hand mic keyed the radio");
        Assert.NotNull(c.F.Rig.PendingListenbackRecovery);

        HandMicReleases(c);
        Assert.True(await c.Pump(entry.Task), "the owner did not put the held take back once the radio received");

        Assert.True(run.Restored);
        Assert.True(saidLater, "the window was not told the take is now restored");
        Assert.Contains("slice set 1 txant=ANT1", c.F.Vendor.Transport.Commands);
        Assert.Contains("radio set full_duplex_enabled=0", c.F.Vendor.Transport.Commands);
        Assert.Equal("ANT1", c.Tx.TXAnt);
        Assert.Null(c.F.Rig.PendingListenbackRecovery);
    }

    [Fact]
    public async Task Control_a_take_stopped_while_receiving_restores_at_once_and_is_not_announced_again()
    {
        using var c = new RadioCase("owner-plain-serial");
        var run = await StartOffAir(c);
        bool saidLater = false;
        var entry = c.F.Rig.TrackListenbackRunRestore(run, "off-air take", () => saidLater = true);

        Assert.True(await c.Pump(Stopped(run, "operator-stop")));
        Assert.False(run.RestoreHeldByTransmit);
        c.F.Rig.ListenbackRunStoppedByItsWindow(entry, run);

        Assert.True(entry.Finished);
        Assert.True(await entry.Task);
        Assert.False(saidLater);
        Assert.Equal("ANT1", c.Tx.TXAnt);
    }

    // ── D2, the instance: a take the radio confirmed restored owes nothing ─

    [Fact]
    public async Task A_take_already_restored_leaves_the_owner_done_though_the_hand_mic_keys_before_the_teardown()
    {
        using var c = new RadioCase("restored-then-keyed");
        var run = await StartOffAir(c);
        var entry = c.F.Rig.TrackListenbackRunRestore(run, "off-air take");
        Assert.True(await c.Pump(Stopped(run, "operator-stop")));

        HandMicKeys(c); // the key lands before the owner reached the entry
        c.F.Rig.ListenbackRunStoppedByItsWindow(entry, run);
        var result = await Teardown(c);

        Assert.True(result.Completed, "the teardown read a take the radio had restored as owed: " + result.Stage);
    }

    [Fact]
    public async Task Even_withheld_a_take_already_restored_is_not_said_as_settings_not_put_back()
    {
        // The class beneath the instance: whatever leaves the take's entry
        // withheld, the words come from what is owed, and nothing is.
        using var c = new RadioCase("withheld-restored");
        var (briefing, said, alarms) = OwnBriefing();
        var store = OwnStore(c, briefing);
        var run = await StartOffAir(c);
        var entry = c.F.Rig.TrackListenbackRunRestore(run, "off-air take");
        Assert.True(await c.Pump(Stopped(run, "operator-stop")));

        HandMicKeys(c);
        c.F.Rig.CheckRestorations.Release(entry); // as before this repair: through the pump
        var result = await Teardown(c);
        Assert.Equal("still-transmitting", result.Stage); // positive control: the bench's stage

        Assert.False(c.F.Rig.CheckRestoreStillOwed());
        c.F.Rig.NoteCheckRestoreReminder(FlexBase.CheckRestoreWithheld);
        Assert.Null(store.ReasonFor(c.F.Vendor.Radio.Serial));
        HandMicReleases(c);
        Announce(c.F.Rig);
        Assert.Empty(said);
        Assert.Equal(0, alarms());
    }

    // ── Held through the disconnect: the take's record is what is owed ──

    [Fact]
    public async Task A_take_held_through_the_disconnect_is_owed_and_the_next_connect_says_what_it_put_back()
    {
        string serial = "owner-held-disconnect";
        var (briefing, said, _) = OwnBriefing();
        CheckRestoreReminderStore store;
        using (var first = new RadioCase(serial))
        {
            store = OwnStore(first, briefing);
            var run = await StartOffAir(first);
            var entry = first.F.Rig.TrackListenbackRunRestore(run, "off-air take");
            HandMicKeys(first);
            await first.Pump(Stopped(run, "connection-ending"));
            first.F.Rig.ListenbackRunStoppedByItsWindow(entry, run);
            var result = await Teardown(first);

            Assert.Equal("still-transmitting", result.Stage);
            Assert.False(WroteTransmitSetting(first), "written under the hand mic's key: " + string.Join(", ", first.F.Vendor.Transport.Commands));
            Assert.True(first.F.Rig.CheckRestoreStillOwed(), "the take's own record is owed, and the check said nothing was");
            first.F.Rig.NoteCheckRestoreReminder(FlexBase.CheckRestoreWithheld);
            Assert.NotNull(first.F.Rig.PendingListenbackRecovery);
        } // the connection ends with the hand mic still keyed

        using var c = new RadioCase(serial);
        c.F.Rig.CheckRestoreRemindersOverride = store;
        c.F.Rig.CheckRestoreBriefingOverride = briefing;
        c.F.Rig.SuppressSpeech = false;
        c.F.Rig.RestoreWriteWait = TimeSpan.FromSeconds(2);
        Field(c.Tx, "_txant", "XVT-A");
        Field(c.F.Vendor.Radio, "_rfPower", 1);
        Field(c.F.Vendor.Radio, "_fullDuplexEnabled", true);
        c.F.Vendor.Transport.Written.Clear();

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        Assert.Contains("slice set 1 txant=ANT1", c.F.Vendor.Transport.Commands);
        Assert.Contains("radio set full_duplex_enabled=0", c.F.Vendor.Transport.Commands);
        Assert.Single(said, u => u.Text.StartsWith(Lead + "Restored: the transmit antenna", StringComparison.Ordinal));
        Assert.DoesNotContain(said, u => u.Text == Lexicon.Get("audio.check.teardown_withheld_reminder"));
    }

    // ── The window's wiring ─────────────────────────────────────────────

    [Fact]
    public void The_check_window_gives_the_take_to_the_radios_owner_and_decides_its_words_from_what_is_owed()
    {
        string offAir = Read("JJFlexWpf/Dialogs/CheckYourAudioDialog.OffAir.cs");
        Assert.Contains("rig.TrackListenbackRunRestore(run, \"off-air take\"", offAir);
        Assert.Contains("runRig?.ListenbackRunStoppedByItsWindow(_iqRunRestore, run);", offAir);
        Assert.DoesNotContain("runRig?.CheckRestorations.Release(_iqRunRestore);", offAir);
        string dialog = Read("JJFlexWpf/Dialogs/CheckYourAudioDialog.cs");
        int owed = dialog.IndexOf("if (!result.Completed && !rig.CheckRestoreStillOwed())", StringComparison.Ordinal);
        int note = dialog.IndexOf("rig.NoteCheckRestoreReminder(withheld ? FlexBase.CheckRestoreWithheld : FlexBase.CheckRestoreUnconfirmed);", StringComparison.Ordinal);
        Assert.True(owed > 0 && note > owed, "the disconnect's words must be chosen after asking what is owed");
    }

    private static string Read(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }
}
