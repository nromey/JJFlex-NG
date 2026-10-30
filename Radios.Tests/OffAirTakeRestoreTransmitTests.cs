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
/// The off-air take's own restore asks the radio's interlock, not only our Mox,
/// and what it cannot write before the connection ends reaches the next
/// connect's put-back; the next connect says only what is owed (#835, round
/// 1030 repair TD, Noel's bench 2026-10-30 06:11 on the 8600).
/// </summary>
/// <remarks>
/// <para>
/// <b>D1.</b> An off-air take was running; Noel held the hand mic's PTT (the
/// radio reported PTT source RCA, interlock TRANSMITTING) and chose Disconnect,
/// holding the key throughout. The restore owner rightly withheld its entry for
/// the take, but the window's own stop ran the take's restore directly, and that
/// restore asked only our cached Mox, which a hand mic never sets. It sent
/// <c>transmit set mon=1</c>, <c>transmit set rfpower=25</c>,
/// <c>slice set 0 audio_mute=0</c>, <c>slice set 0 txant=ANT1</c> and
/// <c>radio set full_duplex_enabled=0</c> under RF.
/// </para>
/// <para>
/// <b>D2.</b> The radio confirmed every one of those, yet the teardown read
/// "still-transmitting" (the withheld entry), the reminder kept that reason with
/// nothing recorded as owed, and the next connect said the old sentence that
/// power and antenna had not been put back.
/// </para>
/// <para>
/// Every test in this file uses only what existed at <c>72c727fe</c>, so each was
/// run red there first. The repair's own surfaces are tested in
/// <c>OffAirTakeRestoreOwnerTests</c>.
/// </para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class OffAirTakeRestoreTransmitTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(OffAirTakeRestoreTransmitTests));
    public void Dispose() => _scope.Dispose();

    internal const string Lead = "When this radio was last disconnected, an audio check had not put its settings back. ";

    /// <summary>The commands a take's restore sends that change what the radio transmits, or how.</summary>
    internal static readonly string[] TransmitSettingWrites =
    {
        "transmit set", "radio set full_duplex_enabled", "slice set 1 txant", "xvtr set",
    };

    internal static bool WroteTransmitSetting(RadioCase c) =>
        c.F.Vendor.Transport.Commands.Any(x => TransmitSettingWrites.Any(p => x.StartsWith(p, StringComparison.Ordinal)));

    internal static void HandMicKeys(RadioCase c) => c.F.Vendor.Status("interlock state=TRANSMITTING source=RCA");
    internal static void HandMicReleases(RadioCase c) => c.F.Vendor.Status("interlock state=READY source=NONE");

    internal static (ConnectBriefing Briefing, List<BriefingUtterance> Said, Func<int> Alarms) OwnBriefing()
    {
        var said = new List<BriefingUtterance>();
        int alarms = 0;
        return (new ConnectBriefing(u => { lock (said) said.Add(u); }, () => alarms++), said, () => alarms);
    }

    internal static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
    }

    internal static void Announce(FlexBase rig) =>
        typeof(FlexBase).GetMethod("AnnounceCheckRestoreReminder", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig, null);

    internal static async Task PutBackFinishes(RadioCase c)
    {
        Task task = c.F.Rig.CheckPutBackTask;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { RunQueue(c); await Task.Delay(5); }
        await task.WaitAsync(TimeSpan.FromSeconds(1));
        RunQueue(c);
    }

    /// <summary>
    /// The real off-air shape (<see cref="RadioCase.Snapshot"/>) with what the
    /// take set on each station setting, as <c>StartListenbackAsync</c> records
    /// it, so the next connect's put-back can take it (fix round 1030 P2).
    /// </summary>
    internal static RadioRecoverySnapshot TakeRecord(RadioCase c)
    {
        var s = c.Snapshot();
        Field(c.F.Vendor.Radio, "_rfPower", 1);
        Field(c.F.Vendor.Radio, "_fullDuplexEnabled", true);
        Field(c.F.Vendor.Radio, "_txMonitor", false);
        for (int i = 0; i < s.Settings.Count; i++)
            s.Settings[i] = s.Settings[i].Name switch
            {
                "full-duplex" => s.Settings[i] with { RequestedValue = "1" },
                "tx-antenna" => s.Settings[i] with { RequestedValue = "XVT-A" },
                "rf-power" => s.Settings[i] with { RequestedValue = "1" },
                "monitor" => s.Settings[i] with { RequestedValue = "0" },
                _ => s.Settings[i],
            };
        c.Store.Save(s);
        c.F.Vendor.Transport.Written.Clear();
        return c.Store.Find(s.Serial);
    }

    // ── D1: the take's restore and the radio's interlock ────────────────

    [Fact]
    public async Task The_takes_restore_writes_nothing_while_the_radios_interlock_says_transmitting()
    {
        using var c = new RadioCase("held-serial");
        var s = TakeRecord(c);
        c.F.Rig.ListenbackUnkeyWait = TimeSpan.FromMilliseconds(300);
        HandMicKeys(c);
        Assert.False(c.F.Rig.Transmit, "positive control: a hand mic leaves our Mox false, as on the bench");
        Assert.True(c.F.Rig.RadioInterlockReportsTransmit, "positive control: the radio's interlock says transmitting");

        bool restored = await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(s, true));

        Assert.False(WroteTransmitSetting(c), "the take's restore wrote a transmit setting while the hand mic keyed the radio: "
            + string.Join(", ", c.F.Vendor.Transport.Commands));
        Assert.Empty(c.F.Vendor.Transport.Commands);
        Assert.False(restored);
        Assert.NotNull(c.Store.Find(s.Serial));

        // Control: once the radio receives, the same restore puts everything back.
        HandMicReleases(c);
        Assert.True(await c.Pump(c.F.Rig.RestoreListenbackSnapshotAsync(c.Store.Find(s.Serial), true)));
        Assert.Contains("slice set 1 txant=ANT1", c.F.Vendor.Transport.Commands);
        Assert.Contains("radio set full_duplex_enabled=0", c.F.Vendor.Transport.Commands);
        Assert.Null(c.Store.Find(s.Serial));
    }

    /// <summary>
    /// The key lands after the restore has asked about the next item but before
    /// the command loop runs that item's write: the write itself must ask.
    /// <paramref name="how"/> is the hand mic (the interlock only) or our own Mox.
    /// </summary>
    [Theory]
    [InlineData("interlock")]
    [InlineData("mox")]
    public async Task A_key_landing_before_a_queued_restore_write_runs_stops_that_write(string how)
    {
        using var c = new RadioCase("at-run-" + how);
        var s = TakeRecord(c);
        bool armed = false, injected = false;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            c.Respond(command);
            if (command == "transmit set mon=0") armed = true;
        };

        var restore = c.F.Rig.RestoreListenbackSnapshotAsync(s, true);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!restore.IsCompleted && DateTime.UtcNow < deadline)
        {
            while (c.F.Rig.q.Count > 0)
            {
                var item = c.F.Rig.q.Dequeue().Item;
                if (armed && !injected)
                {
                    // Between the item's own question and its write's run.
                    if (how == "mox") Field(c.F.Rig, "_Transmit", true); else HandMicKeys(c);
                    injected = true;
                }
                if (item is FlexBase.FunctionDel a) a();
            }
            await Task.Delay(5);
        }
        bool restored = await restore.WaitAsync(TimeSpan.FromSeconds(1));
        Field(c.F.Rig, "_Transmit", false);

        Assert.True(injected, "positive control: the monitor's write went out, so the boundary was reached");
        Assert.DoesNotContain("transmit set rfpower=20", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("slice set 1 txant=ANT1", c.F.Vendor.Transport.Commands);
        Assert.DoesNotContain("radio set full_duplex_enabled=0", c.F.Vendor.Transport.Commands);
        Assert.False(restored);
        Assert.NotNull(c.Store.Find(s.Serial));
    }

    // ── D1, the debt: what the radio's transmit held back reaches the put-back

    [Fact]
    public async Task A_take_held_by_the_hand_mic_through_a_disconnect_is_put_back_at_the_next_connect()
    {
        string serial = "held-through-disconnect";
        var (briefing, said, alarms) = OwnBriefing();
        var store = new CheckRestoreReminderStore(Path.Combine(RadioConfig.AppDataRoot, "td-" + Guid.NewGuid().ToString("N")));
        using (var first = new RadioCase(serial))
        {
            var s = TakeRecord(first);
            first.F.Rig.ListenbackUnkeyWait = TimeSpan.FromMilliseconds(300);
            HandMicKeys(first);
            await first.Pump(first.F.Rig.RestoreListenbackSnapshotAsync(s, true));
            Assert.False(WroteTransmitSetting(first), "the take's restore wrote under the hand mic's key: "
                + string.Join(", ", first.F.Vendor.Transport.Commands));
        } // the connection ends, still keyed

        using var c = new RadioCase(serial);
        c.F.Rig.CheckRestoreRemindersOverride = store;
        c.F.Rig.CheckRestoreBriefingOverride = briefing;
        c.F.Rig.SuppressSpeech = false;
        c.F.Rig.RestoreWriteWait = TimeSpan.FromSeconds(2);
        // The radio as the take left it: the transverter port, 1 watt, full duplex on.
        Field(c.Tx, "_txant", "XVT-A");
        Field(c.F.Vendor.Radio, "_rfPower", 1);
        Field(c.F.Vendor.Radio, "_fullDuplexEnabled", true);
        c.F.Vendor.Transport.Written.Clear();

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        var commands = c.F.Vendor.Transport.Commands;
        Assert.Contains("slice set 1 txant=ANT1", commands);
        Assert.Contains("transmit set rfpower=20", commands);
        Assert.Contains("radio set full_duplex_enabled=0", commands);
        Assert.Single(said, u => u.Text == Lead + "Put back: the transmit antenna, transmit power and full duplex.");
        Assert.Equal(1, alarms());
        Assert.Null(c.F.Rig.PendingListenbackRecovery);
    }

    // ── D2: the next connect says what is owed, not what the stage was ──

    private static async Task<AudioCheckTeardownResult> TeardownWhileKeyed(RadioCase c)
    {
        c.F.Rig.BeginCheckTeardown();
        try { return await c.Pump(c.F.Rig.SettleChecksForTeardownAsync(TimeSpan.FromMilliseconds(400))); }
        finally { c.F.Rig.EndCheckTeardown(); }
    }

    [Fact]
    public async Task A_withheld_restore_that_owes_nothing_leaves_nothing_said_at_the_next_connect()
    {
        // The bench's shape: the restore the owner withheld had no station
        // setting left to put back (there, the take had already put back
        // everything itself). It says so: described, with nothing in its list.
        using var c = new RadioCase("owes-nothing-serial");
        var store = new CheckRestoreReminderStore(Path.Combine(RadioConfig.AppDataRoot, "td-" + Guid.NewGuid().ToString("N")));
        var (briefing, said, alarms) = OwnBriefing();
        c.F.Rig.CheckRestoreRemindersOverride = store;
        c.F.Rig.CheckRestoreBriefingOverride = briefing;
        c.F.Rig.SuppressSpeech = false;
        var entry = c.F.Rig.CheckRestorations.Register("already put back", () => Task.FromResult(true), held: true);
        c.F.Rig.DescribeCheckPutBack(entry);
        HandMicKeys(c);

        var result = await TeardownWhileKeyed(c);
        Assert.Equal("still-transmitting", result.Stage); // positive control: the bench's stage
        c.F.Rig.NoteCheckRestoreReminder(FlexBase.CheckRestoreWithheld);
        Assert.Null(store.ReasonFor("owes-nothing-serial"));
        HandMicReleases(c);
        Announce(c.F.Rig);
        await PutBackFinishes(c);

        Assert.DoesNotContain(said, u => u.Text == Lexicon.Get("audio.check.teardown_withheld_reminder"));
        Assert.Empty(said);
        Assert.Equal(0, alarms());
    }

    [Fact]
    public async Task Control_a_withheld_restore_nobody_described_still_keeps_the_old_reminder()
    {
        using var c = new RadioCase("unnamed-serial");
        var store = new CheckRestoreReminderStore(Path.Combine(RadioConfig.AppDataRoot, "td-" + Guid.NewGuid().ToString("N")));
        var (briefing, said, _) = OwnBriefing();
        c.F.Rig.CheckRestoreRemindersOverride = store;
        c.F.Rig.CheckRestoreBriefingOverride = briefing;
        c.F.Rig.SuppressSpeech = false;
        c.F.Rig.CheckRestorations.Register("something unnamed", () => Task.FromResult(true), held: true);
        HandMicKeys(c);

        var result = await TeardownWhileKeyed(c);
        Assert.Equal("still-transmitting", result.Stage);
        c.F.Rig.NoteCheckRestoreReminder(FlexBase.CheckRestoreWithheld);
        HandMicReleases(c);
        Announce(c.F.Rig);

        Assert.Single(said, u => u.Text == Lexicon.Get("audio.check.teardown_withheld_reminder"));
    }
}
