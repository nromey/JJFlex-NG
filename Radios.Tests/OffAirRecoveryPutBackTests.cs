using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Radios.Listenback;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// The off-air take's recovery is the next connect's automatic put-back (#835,
/// fix round 1030 P2, ruled by Noel 2026-10-29 23:49, decision (a): "if the put
/// back is reliable"). Same reads, only a setting still at the take's value is
/// written, same confirmation, same sentence; the take's record is archived,
/// never deleted, and the connect no longer offers it for approval.
/// </summary>
/// <remarks>
/// This file and the commit that added it are removable together: if the bench
/// finds the put-back unreliable, reverting that one commit puts the
/// approve-first offer back.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class OffAirRecoveryPutBackTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(OffAirRecoveryPutBackTests));
    public void Dispose() => _scope.Dispose();

    private const string Lead = "When this radio was last disconnected, an audio check had not put its settings back. ";

    private static string Folder() => Path.Combine(RadioConfig.AppDataRoot, "offair-putback-" + Guid.NewGuid().ToString("N"));

    private static (ConnectBriefing Briefing, List<BriefingUtterance> Said, Func<int> Alarms) OwnBriefing()
    {
        var said = new List<BriefingUtterance>();
        int alarms = 0;
        return (new ConnectBriefing(u => { lock (said) said.Add(u); }, () => alarms++), said, () => alarms);
    }

    private static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
    }

    private static void Announce(FlexBase rig) =>
        typeof(FlexBase).GetMethod("AnnounceCheckRestoreReminder", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig, null);

    private static async Task PutBackFinishes(RadioCase c)
    {
        Task task = c.F.Rig.CheckPutBackTask;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { RunQueue(c); await Task.Delay(5); }
        await task.WaitAsync(TimeSpan.FromSeconds(1));
        RunQueue(c);
    }

    /// <summary>A reconnect to a radio still holding what an off-air take set: the transverter port, 1 watt, full duplex on.</summary>
    private static (RadioCase Case, CheckRestoreReminderStore Store) Reconnect(string serial, ConnectBriefing briefing)
    {
        var c = new RadioCase(serial);
        var store = new CheckRestoreReminderStore(Folder());
        c.F.Rig.CheckRestoreRemindersOverride = store;
        c.F.Rig.CheckRestoreBriefingOverride = briefing;
        c.F.Rig.SuppressSpeech = false;
        c.F.Rig.RestoreWriteWait = TimeSpan.FromSeconds(2);
        Field(c.Tx, "_txant", "XVT-A");
        Field(c.F.Vendor.Radio, "_rfPower", 1);
        Field(c.F.Vendor.Radio, "_fullDuplexEnabled", true);
        c.F.Vendor.Transport.Written.Clear();
        return (c, store);
    }

    /// <summary>
    /// An off-air take's record in StartListenbackAsync's own shape, written by
    /// an earlier connection: the station settings with what the take set, the
    /// operator's slice mute, and the listening slice, pan and stream it made.
    /// </summary>
    private static RadioRecoverySnapshot OffAirRecord(RadioCase c)
    {
        var r = c.F.Vendor.Radio;
        var s = new RadioRecoverySnapshot { Serial = r.Serial, ClientHandle = 0x99, ConnectionId = Guid.NewGuid(),
            TakeDirectory = "take-" + Guid.NewGuid().ToString("N"), GuiClientId = "persistent-gui", CreationEvidenceVersion = 1 };
        s.ResourceNotes.Add("preexisting-slice-indices=1");
        s.Settings.Add(new("full-duplex", "0", "radio set full_duplex_enabled=0", "radio", "full_duplex_enabled", RequestedValue: "1"));
        s.Settings.Add(new("tx-antenna", "ANT1", "slice set 1 txant=ANT1", "slice 1", "txant", "slice", 1, RequestedValue: "XVT-A"));
        s.Settings.Add(new("slice-mute-1", "0", "slice set 1 audio_mute=0", "slice 1", "audio_mute", "slice", 1, RequestedValue: "1"));
        s.Settings.Add(new("rf-power", "25", "transmit set rfpower=25", "transmit", "rfpower", RequestedValue: "1"));
        s.Settings.Add(new("monitor", "0", "transmit set mon=0", "transmit", "sb_monitor", RequestedValue: "0"));
        s.Settings.Add(new("created-pan", "removed", "display pan remove 0x40000000", "display pan 0x40000000", "", "pan", 0x40000000, RequestedValue: "created"));
        s.Settings.Add(new("created-slice", "removed", "slice remove 0", "slice 0", "", "slice", 0, RequestedValue: "created"));
        s.Settings.Add(new("pan-iq-channel", "0", "display pan set 0x40000000 daxiq_channel=0", "display pan 0x40000000", "daxiq_channel", "pan", 0x40000000, RequestedValue: "1"));
        s.Settings.Add(new("created-stream", "removed", "stream remove 0x20000000", "stream 0x20000000", "", "stream", 0x20000000, RequestedValue: "created"));
        c.Store.Save(s);
        return s;
    }

    private static string[] Archived(string serial) =>
        Directory.GetFiles(Path.Combine(RadioConfig.AppDataRoot, "listenback-recovery"),
            Path.GetFileName(new RadioRecoveryStore(Path.Combine(RadioConfig.AppDataRoot, "listenback-recovery")).PathFor(serial)) + ".put-back-*");

    [Fact]
    public async Task The_next_connect_puts_back_an_off_air_takes_settings_without_asking_and_says_so()
    {
        var (briefing, said, alarms) = OwnBriefing();
        var (c, store) = Reconnect("offair-serial", briefing);
        using var held = c;
        OffAirRecord(c);

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        var commands = c.F.Vendor.Transport.Commands;
        Assert.Contains("slice set 1 txant=ANT1", commands);
        Assert.Contains("transmit set rfpower=25", commands);
        Assert.Contains("radio set full_duplex_enabled=0", commands);
        // Each write is confirmed by its own reply; a ping proves nothing about acceptance (Codex round 1030, B3).
        Assert.DoesNotContain(commands, x => x.StartsWith("ping ", StringComparison.Ordinal));
        Assert.Equal(25, c.F.Vendor.Radio.RFPower);
        // What belonged to the take's connection is the reconnect's, as for a check.
        Assert.DoesNotContain(commands, x => x.Contains("audio_mute", StringComparison.Ordinal)
            || x.StartsWith("slice remove", StringComparison.Ordinal) || x.StartsWith("display pan", StringComparison.Ordinal)
            || x.StartsWith("stream remove", StringComparison.Ordinal));

        Assert.Single(said, u => u.Text == Lead + "Put back: the transmit antenna, transmit power and full duplex."
            && u.Subject == Radios.Speech.SpeechSubject.CheckSettingsNotRestored);
        Assert.Equal(1, alarms());

        // The take's record is archived, not deleted, and no longer offered.
        Assert.Null(c.F.Rig.PendingListenbackRecovery);
        Assert.Single(Archived("offair-serial"));
        Assert.False(store.Contains("offair-serial"));
    }

    [Fact]
    public async Task An_off_air_setting_something_else_has_changed_is_left_alone()
    {
        var (briefing, said, _) = OwnBriefing();
        var (c, store) = Reconnect("offair-left-serial", briefing);
        using var held = c;
        // The profile load set 50 watts; the antenna is already back.
        Field(c.F.Vendor.Radio, "_rfPower", 50);
        Field(c.Tx, "_txant", "ANT1");
        OffAirRecord(c);

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        var commands = c.F.Vendor.Transport.Commands;
        Assert.DoesNotContain(commands, x => x.StartsWith("transmit set rfpower", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, x => x.Contains("txant=", StringComparison.Ordinal));
        Assert.Contains("radio set full_duplex_enabled=0", commands);
        Assert.Equal(50, c.F.Vendor.Radio.RFPower);
        Assert.Single(said, u => u.Text == Lead + "Put back: full duplex. Already changed by something else, so left alone: transmit power.");
        Assert.Null(c.F.Rig.PendingListenbackRecovery);
    }

    [Fact]
    public async Task A_radio_transmitting_at_the_connect_gets_nothing_written_and_the_take_settings_wait_in_the_put_back()
    {
        var (briefing, said, _) = OwnBriefing();
        var (c, store) = Reconnect("offair-keyed-serial", briefing);
        using var held = c;
        OffAirRecord(c);
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c.F.Rig, new object[] { true });

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.StartsWith("transmit set", StringComparison.Ordinal)
            || x.StartsWith("radio set", StringComparison.Ordinal) || x.Contains("txant=", StringComparison.Ordinal));
        Assert.Single(said);
        // The take's settings are the put-back's now, kept for the next connect.
        Assert.Equal(3, store.PutBacksFor("offair-keyed-serial").Count);
        Assert.Null(c.F.Rig.PendingListenbackRecovery);
    }

    [Fact]
    public async Task A_silent_connect_puts_an_off_air_take_back_too()
    {
        var (briefing, said, _) = OwnBriefing();
        var (c, store) = Reconnect("offair-quiet-serial", briefing);
        using var held = c;
        c.F.Rig.SuppressSpeech = true;
        OffAirRecord(c);

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        Assert.Contains("transmit set rfpower=25", c.F.Vendor.Transport.Commands);
        Assert.Empty(said);
        Assert.Null(c.F.Rig.PendingListenbackRecovery);
        Assert.True(CheckPutBackOutcome.TryParse(store.ReasonFor("offair-quiet-serial"), out _));
    }

    [Fact]
    public void A_diagnostic_take_an_unreadable_record_and_an_old_one_stay_with_the_approve_first_offer()
    {
        var (briefing, said, _) = OwnBriefing();

        // The receive-only diagnostic take: no transmit antenna, so not the off-air take.
        var (diag, diagStore) = Reconnect("diag-serial", briefing);
        using (diag)
        {
            var s = new RadioRecoverySnapshot { Serial = "diag-serial", TakeDirectory = "diag-take", ConnectionId = Guid.NewGuid() };
            s.Settings.Add(new("full-duplex", "0", "radio set full_duplex_enabled=0", "radio", "full_duplex_enabled", RequestedValue: "1"));
            diag.Store.Save(s);
            Announce(diag.F.Rig);
            Assert.NotNull(diag.Store.Find("diag-serial"));
            Assert.Empty(diagStore.PutBacksFor("diag-serial"));
            Assert.Empty(diag.F.Vendor.Transport.Commands);
        }

        // A record that cannot be read: only the operator can acknowledge it.
        var (bad, badStore) = Reconnect("unreadable-serial", briefing);
        using (bad)
        {
            string path = bad.Store.PathFor("unreadable-serial");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            Announce(bad.F.Rig);
            Assert.NotNull(bad.Store.Find("unreadable-serial")?.UnreadableHash);
            Assert.Empty(badStore.PutBacksFor("unreadable-serial"));
        }

        // An off-air record from before takes recorded what they set: nothing
        // to prove a setting still holds the take's value, so it is not taken.
        var (old, oldStore) = Reconnect(null!, briefing);
        using (old)
        {
            var s = old.Snapshot(reconnect: true);
            old.F.Vendor.Transport.Written.Clear();
            Announce(old.F.Rig);
            Assert.NotNull(old.Store.Find(s.Serial));
            Assert.Empty(oldStore.PutBacksFor(s.Serial));
            Assert.Empty(old.F.Vendor.Transport.Commands);
        }
        Assert.Empty(said);
    }

    [Fact]
    public void The_take_record_maps_to_the_put_backs_settings()
    {
        var s = new RadioRecoverySnapshot { Serial = "map", TakeDirectory = "t" };
        s.Settings.Add(new("tx-antenna", "ANT2", "x", "slice 0", "txant", "slice", 0, RequestedValue: "XVTB"));
        s.Settings.Add(new("rf-power", "100", "x", "transmit", "rfpower", RequestedValue: "1"));
        s.Settings.Add(new("monitor", "1", "x", "transmit", "sb_monitor", RequestedValue: "0"));
        s.Settings.Add(new("full-duplex", "1", "x", "radio", "full_duplex_enabled", RequestedValue: "1"));   // unchanged
        s.Settings.Add(new("xvtr-drive", "-2", "x", "xvtr 3", "max_power", RequestedValue: "-10"));
        s.Settings.Add(new("slice-mute-0", "0", "x", "slice 0", "audio_mute", "slice", 0, RequestedValue: "1"));

        Assert.True(FlexBase.OffAirRecoveryGoesToPutBack(s));
        Assert.Equal(new[]
        {
            CheckPutBack.Antenna("ANT2", "XVTB"),
            CheckPutBack.Power(100, 1),
            CheckPutBack.MonitorOn(true, false),
            new CheckPutBack(CheckPutBack.XvtrDrive, "3", "-2", "-10"),
        }, FlexBase.OffAirPutBacks(s));
    }

    [Fact]
    public void The_connects_approve_first_offer_leaves_an_off_air_take_to_the_put_back()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        string prompt = File.ReadAllText(Path.Combine(dir!.FullName, "JJFlexWpf", "Dialogs", "ListenbackRecoveryPrompt.cs"));
        int offer = prompt.IndexOf("internal static async Task OfferOnConnectAsync", StringComparison.Ordinal);
        Assert.True(offer >= 0);
        string body = prompt[offer..];
        int skip = body.IndexOf("FlexBase.OffAirRecoveryGoesToPutBack(pending)", StringComparison.Ordinal);
        int review = body.IndexOf("await ReviewAsync(rig)", StringComparison.Ordinal);
        Assert.True(skip > 0 && skip < review, "the connect's offer still shows an off-air take's record");
    }
}
