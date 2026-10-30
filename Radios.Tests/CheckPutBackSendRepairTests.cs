using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Radios.Listenback;
using Radios.Tests.StationConnect;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// The put-back's own sends, repaired after Codex's round-1030 check of Check
/// your audio (#835, 2026-10-30, findings B1, B2 and B3). Each test replays
/// one of Codex's compiled schedules: the connect's put-back has decided to
/// write and queued the write, and THEN something happens before the command
/// loop reaches it.
/// </summary>
/// <remarks>
/// <para>
/// <b>B1.</b> The put-back compared the live value before it queued the write,
/// and never again, so another client's newer choice in between was
/// overwritten (Codex: RF 80 written over a newer 5). The queued send now asks
/// again at its run: same radio, same connection, and the setting still at the
/// check's value. Anything else writes nothing.
/// </para>
/// <para>
/// <b>B2.</b> Full duplex had no transmit question at its run at all, and the
/// other writes asked only our cached Mox, which can say receive while the
/// radio's interlock still says transmitting (<c>FlexBase.CheckRestore.cs</c>
/// documents the disagreement). Every send now asks both.
/// </para>
/// <para>
/// <b>B3.</b> One answered ping retired every written setting, but a ping
/// proves the radio PROCESSED earlier commands, not that it ACCEPTED them, and
/// FlexLib's optimistic cache made the getter echo the request
/// (memory: project_ping_proves_order_not_acceptance). A setting now leaves
/// the record only on its own command's success reply.
/// </para>
/// <para>
/// The off-air take's recovery and the silent connect share this put-back,
/// so each has a case here too.
/// </para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class CheckPutBackSendRepairTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(CheckPutBackSendRepairTests));
    public void Dispose() => _scope.Dispose();

    private const string Lead = "When this radio was last disconnected, an audio check had not restored its settings. ";

    private static string Folder() => Path.Combine(RadioConfig.AppDataRoot, "putback-repair-" + Guid.NewGuid().ToString("N"));

    private static (ConnectBriefing Briefing, List<BriefingUtterance> Said) OwnBriefing()
    {
        var said = new List<BriefingUtterance>();
        return (new ConnectBriefing(u => { lock (said) said.Add(u); }, () => { }), said);
    }

    private static void Mox(FlexBase rig, bool on) =>
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig, new object[] { on });

    private static void Announce(FlexBase rig) =>
        typeof(FlexBase).GetMethod("AnnounceCheckRestoreReminder", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig, null);

    private static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
    }

    private static async Task WaitUntilQueued(RadioCase c, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (c.F.Rig.q.Count == 0 && DateTime.UtcNow < until) await Task.Delay(1);
        Assert.True(c.F.Rig.q.Count > 0, what + " was never queued, so the schedule below would prove nothing");
    }

    /// <summary>
    /// Codex's schedule: let the connect's first drain run, wait until the
    /// put-back has queued its write, then do <paramref name="between"/>
    /// before the command loop reaches that write.
    /// </summary>
    private static async Task BetweenTheDecisionAndTheSend(RadioCase c, Action between)
    {
        await WaitUntilQueued(c, "the connect's first drain");
        if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel drain) drain();
        // The positive control that the boundary is reachable: the write is queued and has not run.
        await WaitUntilQueued(c, "the put-back's write");
        between();
    }

    private static async Task PutBackFinishes(RadioCase c)
    {
        Task task = c.F.Rig.CheckPutBackTask;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { RunQueue(c); await Task.Delay(5); }
        await task.WaitAsync(TimeSpan.FromSeconds(1));
        RunQueue(c);
    }

    private static RadioCase Connect(string serial, CheckRestoreReminderStore store, ConnectBriefing briefing, bool silent = false)
    {
        var c = new RadioCase(serial);
        c.F.Rig.CheckRestoreRemindersOverride = store;
        c.F.Rig.CheckRestoreBriefingOverride = briefing;
        c.F.Rig.SuppressSpeech = silent;
        c.F.Rig.RestoreWriteWait = TimeSpan.FromSeconds(2);
        // The radio still holds the check's values: 1 watt, full duplex on.
        Field(c.F.Vendor.Radio, "_rfPower", 1);
        Field(c.F.Vendor.Radio, "_fullDuplexEnabled", true);
        c.F.Vendor.Transport.Written.Clear();
        return c;
    }

    private static CheckRestoreReminderStore Owing(string serial, params CheckPutBack[] items)
    {
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks(serial, items);
        store.Add(serial, FlexBase.CheckRestoreWithheld);
        return store;
    }

    private static bool Wrote(RadioCase c, string prefix) =>
        c.F.Vendor.Transport.Commands.Any(x => x.StartsWith(prefix, StringComparison.Ordinal));

    // ── B1: a newer value is never overwritten ─────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task B1_a_value_another_client_sets_after_the_decision_is_left_alone_not_overwritten(bool silent)
    {
        string serial = "b1-serial-" + silent;
        var store = Owing(serial, CheckPutBack.Power(80, 1));
        var (briefing, said) = OwnBriefing();
        using var c = Connect(serial, store, briefing, silent);

        Announce(c.F.Rig);
        // Another client chooses 5 watts after the put-back read 1 and queued its write.
        await BetweenTheDecisionAndTheSend(c, () => Field(c.F.Vendor.Radio, "_rfPower", 5));
        await PutBackFinishes(c);

        Assert.False(Wrote(c, "transmit set rfpower"), "the put-back raised the power over a newer choice");
        Assert.Equal(5, c.F.Vendor.Radio.RFPower);
        Assert.Empty(store.PutBacksFor(serial));
        if (silent)
        {
            Assert.Empty(said);
            Assert.True(CheckPutBackOutcome.TryParse(store.ReasonFor(serial), out var kept), "a silent connect keeps what it did");
            Assert.Equal(new[] { CheckPutBack.RfPower }, kept.LeftAlone);
            Assert.Empty(kept.PutBack);
        }
        else
            Assert.Single(said, u => u.Text == Lead + "Already changed by something else, so left alone: transmit power.");
    }

    [Fact]
    public async Task B1_a_write_queued_for_one_radio_never_reaches_another()
    {
        const string serial = "b1-radio-serial";
        var store = Owing(serial, CheckPutBack.Power(80, 1));
        var (briefing, _) = OwnBriefing();
        using var c = Connect(serial, store, briefing);
        var other = new VendorRadioFixture("another-radio");
        other.Transport.CommandWritten = command => other.ReplyOk(other.LastSequence);

        Announce(c.F.Rig);
        // The connection goes, and this FlexBase is now on another radio.
        await BetweenTheDecisionAndTheSend(c, () => Field(c.F.Rig, "theRadio", other.Radio));
        await PutBackFinishes(c);

        Assert.DoesNotContain(other.Transport.Commands, x => x.StartsWith("transmit set", StringComparison.Ordinal));
        Assert.False(Wrote(c, "transmit set rfpower"));
        Assert.Equal(new[] { CheckPutBack.Power(80, 1) }, store.PutBacksFor(serial));
    }

    // ── B2: nothing is written while the radio transmits ───────────────

    [Fact]
    public async Task B2_the_radios_interlock_saying_transmitting_at_the_writes_run_refuses_it()
    {
        const string serial = "b2-rf-serial";
        var store = Owing(serial, CheckPutBack.Power(80, 1));
        var (briefing, said) = OwnBriefing();
        using var c = Connect(serial, store, briefing);

        Announce(c.F.Rig);
        // Our cached Mox still says receive; the radio says it is transmitting.
        await BetweenTheDecisionAndTheSend(c, () => c.F.Vendor.Status("interlock state=TRANSMITTING source=RCA"));
        await PutBackFinishes(c);

        Assert.False(c.F.Rig.Transmit);
        Assert.False(Wrote(c, "transmit set rfpower"), "the put-back wrote while the radio's interlock said transmitting");
        Assert.Equal(1, c.F.Vendor.Radio.RFPower);
        Assert.Equal(new[] { CheckPutBack.Power(80, 1) }, store.PutBacksFor(serial));
        Assert.Single(said, u => u.Text == Lead + "Not confirmed, so check transmit power.");
    }

    [Fact]
    public async Task B2_control_our_own_Mox_at_the_writes_run_refuses_it()
    {
        // Codex's positive control: this half held before the repair.
        const string serial = "b2-mox-serial";
        var store = Owing(serial, CheckPutBack.Power(80, 1));
        var (briefing, _) = OwnBriefing();
        using var c = Connect(serial, store, briefing);

        Announce(c.F.Rig);
        await BetweenTheDecisionAndTheSend(c, () => Mox(c.F.Rig, true));
        await PutBackFinishes(c);

        Assert.False(Wrote(c, "transmit set rfpower"));
        Assert.Equal(new[] { CheckPutBack.Power(80, 1) }, store.PutBacksFor(serial));
    }

    [Theory]
    [InlineData("mox")]
    [InlineData("interlock")]
    public async Task B2_full_duplex_is_not_written_while_the_radio_transmits(string how)
    {
        string serial = "b2-duplex-" + how;
        var store = Owing(serial, CheckPutBack.Duplex(false, true));
        var (briefing, said) = OwnBriefing();
        using var c = Connect(serial, store, briefing);

        Announce(c.F.Rig);
        await BetweenTheDecisionAndTheSend(c, () =>
        {
            if (how == "mox") Mox(c.F.Rig, true);
            else c.F.Vendor.Status("interlock state=TRANSMITTING source=RCA");
        });
        await PutBackFinishes(c);

        Assert.False(Wrote(c, "radio set full_duplex_enabled"), "full duplex was written while the radio transmitted (" + how + ")");
        Assert.Equal(new[] { CheckPutBack.Duplex(false, true) }, store.PutBacksFor(serial));
        Assert.Single(said, u => u.Text == Lead + "Not confirmed, so check full duplex.");
    }

    // ── B3: only the setting's own reply retires it ────────────────────

    [Fact]
    public async Task B3_a_setting_the_radio_rejects_is_kept_and_said_as_not_confirmed_though_the_ping_is_answered()
    {
        const string serial = "b3-reject-serial";
        var store = Owing(serial, CheckPutBack.Power(80, 1));
        var (briefing, said) = OwnBriefing();
        using var c = Connect(serial, store, briefing);
        // Codex's input: the radio rejects the power write, sends no status
        // for it, and answers everything else, a ping included.
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("transmit set rfpower", StringComparison.Ordinal))
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000000, "refused");
            else c.Respond(command);
        };

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        Assert.True(Wrote(c, "transmit set rfpower=80"), "the write must have gone out for its rejection to mean anything");
        Assert.Equal(new[] { CheckPutBack.Power(80, 1) }, store.PutBacksFor(serial));
        Assert.Single(said, u => u.Text == Lead + "Not confirmed, so check transmit power.");
    }

    [Fact]
    public async Task B3_a_setting_whose_write_is_never_answered_is_kept_though_a_ping_is_answered()
    {
        const string serial = "b3-silent-serial";
        var store = Owing(serial, CheckPutBack.Power(80, 1));
        var (briefing, said) = OwnBriefing();
        using var c = Connect(serial, store, briefing);
        c.F.Rig.RestoreWriteWait = TimeSpan.FromMilliseconds(400);
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("transmit set rfpower", StringComparison.Ordinal)) return;
            c.Respond(command);
        };

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        Assert.True(Wrote(c, "transmit set rfpower=80"));
        Assert.Equal(new[] { CheckPutBack.Power(80, 1) }, store.PutBacksFor(serial));
        Assert.Single(said, u => u.Text == Lead + "Not confirmed, so check transmit power.");
    }

    [Fact]
    public async Task B3_control_a_setting_the_radio_accepts_is_put_back_and_retired()
    {
        const string serial = "b3-accept-serial";
        var store = Owing(serial, CheckPutBack.Power(80, 1), CheckPutBack.Duplex(false, true));
        var (briefing, said) = OwnBriefing();
        using var c = Connect(serial, store, briefing);

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        Assert.True(Wrote(c, "transmit set rfpower=80"));
        Assert.True(Wrote(c, "radio set full_duplex_enabled=0"));
        Assert.Equal(80, c.F.Vendor.Radio.RFPower);
        Assert.Empty(store.PutBacksFor(serial));
        Assert.Single(said, u => u.Text == Lead + "Restored: transmit power and full duplex.");
    }

    // ── The off-air take's recovery inherits all three ─────────────────

    [Fact]
    public async Task The_off_air_takes_recovery_inherits_the_transmit_question_at_the_writes_run()
    {
        const string serial = "offair-repair-serial";
        var store = new CheckRestoreReminderStore(Folder());
        var (briefing, _) = OwnBriefing();
        using var c = Connect(serial, store, briefing);
        Field(c.Tx, "_txant", "ANT1");
        var r = c.F.Vendor.Radio;
        var take = new RadioRecoverySnapshot { Serial = r.Serial, ClientHandle = 0x99, ConnectionId = Guid.NewGuid(),
            TakeDirectory = "take-" + Guid.NewGuid().ToString("N"), GuiClientId = "persistent-gui", CreationEvidenceVersion = 1 };
        take.Settings.Add(new("tx-antenna", "ANT1", "slice set 1 txant=ANT1", "slice 1", "txant", "slice", 1, RequestedValue: "ANT1"));
        take.Settings.Add(new("rf-power", "25", "transmit set rfpower=25", "transmit", "rfpower", RequestedValue: "1"));
        c.Store.Save(take);

        Announce(c.F.Rig);
        await BetweenTheDecisionAndTheSend(c, () => c.F.Vendor.Status("interlock state=TRANSMITTING source=RCA"));
        await PutBackFinishes(c);

        Assert.False(Wrote(c, "transmit set rfpower"), "the off-air take's recovery wrote while the radio transmitted");
        Assert.Contains(store.PutBacksFor(serial), p => p.Setting == CheckPutBack.RfPower && p.Original == "25" && p.CheckSet == "1");
    }

    [Fact]
    public async Task The_off_air_takes_recovery_keeps_a_setting_the_radio_rejects()
    {
        const string serial = "offair-reject-serial";
        var store = new CheckRestoreReminderStore(Folder());
        var (briefing, _) = OwnBriefing();
        using var c = Connect(serial, store, briefing);
        Field(c.Tx, "_txant", "ANT1");
        var r = c.F.Vendor.Radio;
        var take = new RadioRecoverySnapshot { Serial = r.Serial, ClientHandle = 0x99, ConnectionId = Guid.NewGuid(),
            TakeDirectory = "take-" + Guid.NewGuid().ToString("N"), GuiClientId = "persistent-gui", CreationEvidenceVersion = 1 };
        take.Settings.Add(new("tx-antenna", "ANT1", "slice set 1 txant=ANT1", "slice 1", "txant", "slice", 1, RequestedValue: "ANT1"));
        take.Settings.Add(new("rf-power", "25", "transmit set rfpower=25", "transmit", "rfpower", RequestedValue: "1"));
        c.Store.Save(take);
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("transmit set rfpower", StringComparison.Ordinal))
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000000, "refused");
            else c.Respond(command);
        };

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        Assert.True(Wrote(c, "transmit set rfpower=25"));
        Assert.Contains(store.PutBacksFor(serial), p => p.Setting == CheckPutBack.RfPower && p.Original == "25" && p.CheckSet == "1");
    }

    // ── The same question for the check's own restores ────────────────
    //
    // Not one of Codex's findings: the same class. The check's own restores
    // (a teardown, a check ending) queue ordinary setters under
    // RestoreWritesAtReceiveAsync, whose run-time question asked only our
    // cached Mox. The owner (CheckRestorations) waits on Mox OR the radio's
    // interlock before it starts an entry, so the two disagreed exactly where
    // FlexBase.CheckRestore.cs says they can: a hardware key between the
    // owner's question and the queued write's run.

    [Fact]
    public async Task A_checks_own_queued_restore_meets_the_radios_interlock_at_its_run_and_waits()
    {
        using var c = new RadioCase("own-restore-serial");
        Field(c.F.Vendor.Radio, "_rfPower", 1);
        c.F.Vendor.Transport.Written.Clear();
        var rig = c.F.Rig;

        var restore = rig.RestoreWritesAtReceiveAsync("transmit power", () =>
        {
            using (rig.AppInitiatedSettingChanges()) rig.XmitPower = 80;
        });
        // The write is queued; before the loop reaches it the radio keys on its own.
        c.F.Vendor.Status("interlock state=TRANSMITTING source=RCA");
        var outcome = await c.Pump(restore);

        Assert.False(rig.Transmit);
        Assert.Equal(RestoreOutcome.Retry, outcome);
        Assert.False(Wrote(c, "transmit set rfpower"), "a check's restore wrote while the radio's interlock said transmitting");
    }

    [Fact]
    public async Task Control_a_checks_own_queued_restore_in_receive_goes_out()
    {
        using var c = new RadioCase("own-restore-rx-serial");
        Field(c.F.Vendor.Radio, "_rfPower", 1);
        c.F.Vendor.Transport.Written.Clear();
        var rig = c.F.Rig;

        var outcome = await c.Pump(rig.RestoreWritesAtReceiveAsync("transmit power", () =>
        {
            using (rig.AppInitiatedSettingChanges()) rig.XmitPower = 80;
        }));

        Assert.Equal(RestoreOutcome.Done, outcome);
        Assert.True(Wrote(c, "transmit set rfpower=80"));
    }
}
