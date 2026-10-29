using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using Radios.Listenback;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// A take's restores leave FlexLib's cache true, so a send right after a take
/// reads the monitor as it really is (#835, fix round 2026-10-29, item 1).
/// </summary>
/// <remarks>
/// <para>
/// The bench, 2026-10-29 01:48: Noel turned the monitor ON in Home, made an
/// off-air take (its setup <c>transmit set mon=0</c>, its restore
/// <c>transmit set mon=1</c>, both raw, the restore confirmed by the radio's
/// reply alone), then a box send. FlexLib's cache still said off, so
/// <c>HoldMonitorForSend</c> "found" the monitor off, turned it on, and at the
/// end turned it OFF: "tx monitor is off".
/// </para>
/// <para>
/// These fakes model the radio that never echoes a raw change to the client
/// that made it. What they cannot see: which settings a real radio echoes and
/// which it does not, and whether a later status line from the radio agrees.
/// </para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class TakeRestoreCacheTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(TakeRestoreCacheTests));
    public void Dispose() => _scope.Dispose();

    /// <summary>
    /// The real shape of an off-air take's record, with the monitor ON before
    /// it (Value 1, set to 0 by the arrangement), every restore answered by a
    /// reply and NO status echo, and FlexLib's cache still holding what the
    /// arrangement set.
    /// </summary>
    private static RadioRecoverySnapshot StaleTakeOver(RadioCase c, out string directory)
    {
        var s = c.Snapshot();
        directory = Path.Combine(RadioConfig.AppDataRoot, "restore-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        s.TakeDirectory = directory;
        int i = s.Settings.FindIndex(x => x.Name == "monitor");
        s.Settings[i] = new("monitor", "1", "transmit set mon=1", "transmit", "sb_monitor", RequestedValue: "0");
        int p = s.Settings.FindIndex(x => x.Name == "rf-power");
        s.Settings[p] = new("rf-power", "100", "transmit set rfpower=100", "transmit", "rfpower", RequestedValue: "1");
        int a = s.Settings.FindIndex(x => x.Name == "tx-antenna");
        s.Settings[a] = s.Settings[a] with { RequestedValue = "XVT-A" };
        c.Store.Save(s);
        var radio = c.F.Vendor.Radio;
        // What the arrangement's raw setup left in FlexLib's cache.
        Field(radio, "_txMonitor", false);
        Field(radio, "_rfPower", 1);
        Field(radio, "_fullDuplexEnabled", true);
        // The radio answers every restore and echoes none of them.
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("transmit set ", StringComparison.Ordinal) ||
                command.StartsWith("slice set 1 txant=", StringComparison.Ordinal) ||
                command.StartsWith("radio set ", StringComparison.Ordinal))
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            else c.Respond(command);
        };
        return s;
    }

    [Fact]
    public async Task A_restore_the_radio_never_echoed_leaves_FlexLibs_cache_at_the_restored_value()
    {
        using var c = new RadioCase();
        var s = StaleTakeOver(c, out string directory);
        var radio = c.F.Vendor.Radio;
        Assert.True(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(s, true)));
        Assert.Contains("transmit set mon=1", c.F.Vendor.Transport.Commands);
        // No second command: the cache took the reply-proven value, nothing re-sent it.
        Assert.Single(c.F.Vendor.Transport.Commands, x => x == "transmit set mon=1");
        Assert.True(radio.TXMonitor);
        Assert.Equal(100, radio.RFPower);
        Assert.Equal("ANT1", c.Tx.TXAnt);
        Assert.False(radio.FullDuplexEnabled);
        string trace = string.Join("\n", Directory.GetFiles(directory, "restore-*.txt").Select(File.ReadAllText));
        Assert.Contains("cache-updated", trace);
    }

    [Fact]
    public async Task A_send_right_after_the_take_leaves_a_monitor_that_was_on_switched_on()
    {
        using var c = new RadioCase();
        var s = StaleTakeOver(c, out _);
        var radio = c.F.Vendor.Radio;
        Assert.True(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(s, true)));
        c.F.Vendor.Transport.Written.Clear();
        Field(c.F.Rig, "_IsConnected", true);
        c.F.Vendor.Transport.CommandWritten = c.Respond;

        var hold = c.F.Rig.HoldMonitorForSend("a send right after a take");
        Drain(c);
        hold.Dispose();
        Drain(c);

        Assert.DoesNotContain("transmit set mon=0", c.F.Vendor.Transport.Commands);
        Assert.True(radio.TXMonitor);
    }

    [Fact]
    public async Task An_echo_that_did_arrive_is_left_as_the_radio_said_it()
    {
        using var c = new RadioCase();
        var s = StaleTakeOver(c, out string directory);
        var radio = c.F.Vendor.Radio;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command == "transmit set mon=1") c.F.Vendor.Status("transmit sb_monitor=1");
            c.Respond(command);
        };
        Assert.True(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(s, true)));
        Assert.True(radio.TXMonitor);
        string trace = string.Join("\n", Directory.GetFiles(directory, "restore-*.txt").Select(File.ReadAllText));
        Assert.Contains("cache-already-true", trace);
    }

    [Fact]
    public void A_cache_someone_else_wrote_while_the_restore_was_out_is_left_alone()
    {
        using var c = new RadioCase();
        var radio = c.F.Vendor.Radio;
        var monitor = new SavedRadioSetting("monitor", "1", "transmit set mon=1", "transmit", "sb_monitor", RequestedValue: "0");
        Field(radio, "_txMonitor", false);
        string before = FlexLibRestoreCache.Read(radio, monitor);
        Assert.Equal("0", before);
        // The operator's own choice lands between the send and the reply.
        Field(radio, "_rfPower", 50);
        var power = new SavedRadioSetting("rf-power", "100", "transmit set rfpower=100", "transmit", "rfpower");
        Assert.Equal("cache-changed-meanwhile; left alone", FlexLibRestoreCache.Accept(radio, power, "1"));
        Assert.Equal(50, radio.RFPower);
        Assert.Equal("cache-updated", FlexLibRestoreCache.Accept(radio, monitor, before));
        Assert.True(radio.TXMonitor);
    }

    [Fact]
    public void The_private_FlexLib_parsers_this_relies_on_are_still_where_it_reaches_them()
    {
        // If a FlexLib upgrade renames ParseTransmitStatus or ParseRadioStatus,
        // the take's restores leave the cache stale again with only a trace
        // word to say so; this goes red the same day.
        Assert.True(FlexLibRestoreCache.ParsersFound);
    }

    private static void Drain(RadioCase c)
    {
        for (int pass = 0; pass < 20 && c.F.Rig.q.Count > 0; pass++)
            while (c.F.Rig.q.Count > 0)
                if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel action) action();
    }
}
