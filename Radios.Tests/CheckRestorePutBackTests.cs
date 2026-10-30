using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// When a disconnect comes while the radio is still transmitting, the next
/// connect puts back each setting the audio check left that STILL holds the
/// check's value, leaves alone any that something else has changed, and says
/// what it did with the alarm (#835, fix round 1030, ruled by Noel 2026-10-29
/// 21:13: "I think so. We could still alarm for informational purposes.").
/// </summary>
/// <remarks>
/// The bench this answers (21:07): Loopback keyed on the hand mic, Radio,
/// Disconnect; the restores were withheld and abandoned with the radio, and at
/// the reconnect the alarm asked Noel to check his settings himself. What these
/// cannot see: whether a real 8600 echoes each write, and what the reconnect's
/// profile load has already done by the time the put-back reads. The trace
/// lines named in the report are for the harness at the radio.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class CheckRestorePutBackTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(CheckRestorePutBackTests));
    public void Dispose() => _scope.Dispose();

    private static string Folder() => Path.Combine(RadioConfig.AppDataRoot, "putback-" + Guid.NewGuid().ToString("N"));

    private static (ConnectBriefing Briefing, List<BriefingUtterance> Said, Func<int> Alarms) OwnBriefing()
    {
        var said = new List<BriefingUtterance>();
        int alarms = 0;
        return (new ConnectBriefing(u => { lock (said) said.Add(u); }, () => alarms++), said, () => alarms);
    }

    private static void Mox(FlexBase rig, bool on) =>
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig, new object[] { on });

    private static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
    }

    private static void Announce(FlexBase rig) =>
        typeof(FlexBase).GetMethod("AnnounceCheckRestoreReminder", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig, null);

    /// <summary>Run the command loop by hand until the put-back the connect started has finished.</summary>
    private static async Task PutBackFinishes(RadioCase c)
    {
        Task task = c.F.Rig.CheckPutBackTask;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { RunQueue(c); await Task.Delay(5); }
        await task.WaitAsync(TimeSpan.FromSeconds(1));
        RunQueue(c);
    }

    /// <summary>A connect to a radio still holding what the Loopback arrangement set.</summary>
    private static RadioCase RadioHoldingTheChecksValues(string serial, CheckRestoreReminderStore store, ConnectBriefing briefing)
    {
        var c = new RadioCase(serial);
        c.F.Rig.CheckRestoreRemindersOverride = store;
        c.F.Rig.CheckRestoreBriefingOverride = briefing;
        c.F.Rig.SuppressSpeech = false;
        c.F.Rig.RestoreWriteWait = TimeSpan.FromSeconds(2);
        Field(c.Tx, "_txant", "XVT-A");
        Field(c.F.Vendor.Radio, "_rfPower", 1);
        Field(c.F.Vendor.Radio, "_fullDuplexEnabled", true);
        c.F.Vendor.Transport.Written.Clear();
        return c;
    }

    /// <summary>What a Loopback withheld at a disconnect records: the 8600 bench's own values.</summary>
    private static readonly CheckPutBack[] LoopbackLeft =
    {
        CheckPutBack.Antenna("ANT1", "XVT-A"),
        CheckPutBack.Power(25, 1),
        CheckPutBack.Duplex(false, true),
    };

    // ── The record ───────────────────────────────────────────────────

    [Fact]
    public void The_record_keeps_each_setting_beside_the_reason_and_forgets_them_separately()
    {
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks("8600-serial", LoopbackLeft.Append(CheckPutBack.MonitorOn(false, false)));
        store.Add("8600-serial", FlexBase.CheckRestoreWithheld);

        // A setting the check did not really change has nothing to put back.
        Assert.Equal(LoopbackLeft, store.PutBacksFor("8600-serial"));
        Assert.Equal("withheld", store.ReasonFor("8600-serial"));

        // Saying the reminder forgets the reason and keeps the settings.
        store.RemoveReason("8600-serial");
        Assert.Null(store.ReasonFor("8600-serial"));
        Assert.True(store.Contains("8600-serial"));
        Assert.Equal(3, store.PutBacksFor("8600-serial").Count);

        // A later reason does not drop them either; each goes on its own.
        store.Add("8600-serial", FlexBase.CheckRestoreUnconfirmed);
        store.RemovePutBack("8600-serial", CheckPutBack.Power(0, 0));
        Assert.DoesNotContain(store.PutBacksFor("8600-serial"), p => p.Setting == CheckPutBack.RfPower);
        Assert.Equal(2, store.PutBacksFor("8600-serial").Count);
        Assert.Empty(store.PutBacksFor("another-radio"));

        // A second record of a setting keeps the operator's original.
        store.AddPutBacks("8600-serial", new[] { CheckPutBack.Antenna("XVT-A", "XVT-B") });
        Assert.Equal(CheckPutBack.Antenna("ANT1", "XVT-B"), store.PutBacksFor("8600-serial").Single(p => p.Setting == CheckPutBack.TxAntenna));
    }

    [Fact]
    public void A_record_written_before_this_round_still_reads_as_a_reminder()
    {
        string folder = Folder();
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "checkRestoreReminders.txt"), new[] { "old-serial withheld" });
        var store = new CheckRestoreReminderStore(folder);
        Assert.Equal("withheld", store.ReasonFor("old-serial"));
        Assert.Empty(store.PutBacksFor("old-serial"));
    }

    [Fact]
    public async Task A_Loopback_withheld_at_a_disconnect_records_what_it_set_and_what_it_found()
    {
        using var c = new RadioCase("loopback-serial");
        Field(c.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
        Field(c.F.Rig, "_XmitPower", 25);
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create", StringComparison.Ordinal))
            {
                c.Ears = c.Add(0, 1.8);
                Field(c.F.Rig, "mySliceAdded", true);
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else c.Respond(command);
        };
        var store = new CheckRestoreReminderStore(Folder());
        c.F.Rig.CheckRestoreRemindersOverride = store;
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        RunQueue(c);

        // The hand mic holds the radio keyed; the operator disconnects.
        Mox(c.F.Rig, true);
        var end = c.F.Rig.EndLoopbackArrangementWithOutcomeAsync();
        var settle = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < settle) { RunQueue(c); await Task.Delay(20); }
        Assert.False(end.IsCompleted);
        c.F.Rig.NoteCheckRestoreReminder(FlexBase.CheckRestoreWithheld);
        c.F.Rig.CheckRestorations.Dispose();
        await end.WaitAsync(TimeSpan.FromSeconds(3));

        var recorded = store.PutBacksFor("loopback-serial");
        Assert.Contains(CheckPutBack.Antenna("ANT1", "XVT-A"), recorded);
        Assert.Contains(CheckPutBack.Power(25, 1), recorded);
        Assert.Contains(CheckPutBack.Duplex(false, true), recorded);
        // The monitor was already off, so the check changed nothing there; the
        // mutes and the listening slice belong to the connection.
        Assert.DoesNotContain(recorded, p => p.Setting == CheckPutBack.Monitor);
        Assert.Equal(3, recorded.Count);
        Assert.Equal("withheld", store.ReasonFor("loopback-serial"));
    }

    [Fact]
    public async Task A_Loopback_that_put_everything_back_leaves_nothing_to_record()
    {
        // The positive control: the same arrangement, ended in receive.
        using var c = new RadioCase("clean-serial");
        Field(c.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
        Field(c.F.Rig, "_XmitPower", 25);
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("display panafall create", StringComparison.Ordinal))
            {
                c.Ears = c.Add(0, 1.8);
                Field(c.F.Rig, "mySliceAdded", true);
                Field(c.F.Rig, "_RXVFO", 1); Field(c.F.Rig, "_TXVFO", 1);
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0, "0x40000000,0x42000000");
            }
            else c.Respond(command);
        };
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        RunQueue(c);
        var outcome = await c.Pump(c.F.Rig.EndLoopbackArrangementWithOutcomeAsync());
        Assert.Empty(outcome.NotRestored);
        Assert.Empty(c.F.Rig.CheckPutBacksOwed());
    }

    // ── The next connect ─────────────────────────────────────────────

    [Fact]
    public async Task The_next_connect_puts_back_what_still_holds_the_checks_value_and_says_so_with_the_alarm()
    {
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks("putback-serial", LoopbackLeft);
        store.Add("putback-serial", FlexBase.CheckRestoreWithheld);
        var (briefing, said, alarms) = OwnBriefing();
        using var c = RadioHoldingTheChecksValues("putback-serial", store, briefing);

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        var commands = c.F.Vendor.Transport.Commands;
        Assert.Contains("slice set 1 txant=ANT1", commands);
        Assert.Contains("transmit set rfpower=25", commands);
        Assert.Contains("radio set full_duplex_enabled=0", commands);
        Assert.Contains(commands, x => x.StartsWith("ping ", StringComparison.Ordinal));
        Assert.Equal(25, c.F.Vendor.Radio.RFPower);

        string expected = "When this radio was last disconnected, an audio check had not put its settings back. "
            + "Put back: the transmit antenna, transmit power and full duplex.";
        Assert.Single(said, u => u.Text == expected && u.Subject == Radios.Speech.SpeechSubject.CheckSettingsNotRestored);
        Assert.Equal(1, alarms());
        Assert.False(store.Contains("putback-serial"), "everything was put back and said");
    }

    [Fact]
    public async Task A_setting_something_else_has_changed_is_left_alone_and_named()
    {
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks("left-serial", LoopbackLeft);
        store.Add("left-serial", FlexBase.CheckRestoreWithheld);
        var (briefing, said, _) = OwnBriefing();
        using var c = RadioHoldingTheChecksValues("left-serial", store, briefing);
        // Another client set 50 watts after the disconnect; the profile load put the antenna back.
        Field(c.F.Vendor.Radio, "_rfPower", 50);
        Field(c.Tx, "_txant", "ANT1");

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        var commands = c.F.Vendor.Transport.Commands;
        Assert.DoesNotContain(commands, x => x.StartsWith("transmit set rfpower", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, x => x.Contains("txant=", StringComparison.Ordinal));
        Assert.Contains("radio set full_duplex_enabled=0", commands);
        Assert.Equal(50, c.F.Vendor.Radio.RFPower);
        Assert.Single(said, u => u.Text == "When this radio was last disconnected, an audio check had not put its settings back. "
            + "Put back: full duplex. Already changed by something else, so left alone: transmit power.");
        Assert.False(store.Contains("left-serial"));
    }

    [Fact]
    public async Task Settings_the_reconnect_already_put_back_are_not_written_and_the_alarm_still_informs()
    {
        // The 21:11 bench: the profile load had put everything back already.
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks("already-serial", LoopbackLeft);
        store.Add("already-serial", FlexBase.CheckRestoreWithheld);
        var (briefing, said, alarms) = OwnBriefing();
        using var c = RadioHoldingTheChecksValues("already-serial", store, briefing);
        Field(c.Tx, "_txant", "ANT1");
        Field(c.F.Vendor.Radio, "_rfPower", 25);
        Field(c.F.Vendor.Radio, "_fullDuplexEnabled", false);

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        var commands = c.F.Vendor.Transport.Commands;
        Assert.DoesNotContain(commands, x => x.StartsWith("transmit set", StringComparison.Ordinal)
            || x.StartsWith("radio set", StringComparison.Ordinal) || x.Contains("txant=", StringComparison.Ordinal));
        Assert.Single(said, u => u.Text == "When this radio was last disconnected, an audio check had not put its settings back. They were already back.");
        Assert.Equal(1, alarms());
        Assert.False(store.Contains("already-serial"));
    }

    [Fact]
    public async Task A_radio_transmitting_at_the_connect_gets_nothing_put_back_and_the_reminder_as_before()
    {
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks("keyed-serial", LoopbackLeft);
        store.Add("keyed-serial", FlexBase.CheckRestoreWithheld);
        var (briefing, said, alarms) = OwnBriefing();
        using var c = RadioHoldingTheChecksValues("keyed-serial", store, briefing);
        Mox(c.F.Rig, true);

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        var commands = c.F.Vendor.Transport.Commands;
        Assert.DoesNotContain(commands, x => x.StartsWith("transmit set", StringComparison.Ordinal)
            || x.StartsWith("radio set", StringComparison.Ordinal) || x.Contains("txant=", StringComparison.Ordinal));
        Assert.Single(said, u => u.Text == Lexicon.Get("audio.check.teardown_withheld_reminder"));
        Assert.Equal(1, alarms());
        // The reminder was said; what to put back waits for the next connect.
        Assert.Null(store.ReasonFor("keyed-serial"));
        Assert.Equal(LoopbackLeft, store.PutBacksFor("keyed-serial"));

        // That next connect, in receive, puts them back.
        Mox(c.F.Rig, false);
        c.F.Vendor.Transport.Written.Clear();
        Announce(c.F.Rig);
        await PutBackFinishes(c);
        Assert.Contains("transmit set rfpower=25", c.F.Vendor.Transport.Commands);
        Assert.False(store.Contains("keyed-serial"));
    }

    [Fact]
    public async Task A_put_back_the_radio_never_answers_is_kept_for_the_next_connect()
    {
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks("silent-serial", new[] { CheckPutBack.Power(25, 1) });
        store.Add("silent-serial", FlexBase.CheckRestoreWithheld);
        var (briefing, said, _) = OwnBriefing();
        using var c = RadioHoldingTheChecksValues("silent-serial", store, briefing);
        c.F.Rig.RestoreWriteWait = TimeSpan.FromMilliseconds(400);
        // The radio takes the write but never answers the ping that confirms it.
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("ping ", StringComparison.Ordinal)) return;
            c.Respond(command);
        };

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        Assert.Contains("transmit set rfpower=25", c.F.Vendor.Transport.Commands);
        Assert.Single(said, u => u.Text == "When this radio was last disconnected, an audio check had not put its settings back. "
            + "Not confirmed, so check transmit power.");
        Assert.Equal(new[] { CheckPutBack.Power(25, 1) }, store.PutBacksFor("silent-serial"));
    }

    [Fact]
    public async Task A_setting_that_cannot_be_read_is_kept_and_a_transverter_band_that_is_gone_is_left_alone()
    {
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks("unread-serial", new[] { CheckPutBack.Antenna("ANT1", "XVT-A"), CheckPutBack.Drive(7, -2.0, -10.0) });
        store.Add("unread-serial", FlexBase.CheckRestoreWithheld);
        var (briefing, said, _) = OwnBriefing();
        using var c = RadioHoldingTheChecksValues("unread-serial", store, briefing);
        // No transmit slice and no selected slice yet: the antenna cannot be read.
        Field(c.Tx, "_isTransmitSlice", false);
        Field(c.Tx, "_active", false);

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.Contains("txant=", StringComparison.Ordinal)
            || x.StartsWith("xvtr set", StringComparison.Ordinal));
        Assert.Single(said, u => u.Text == "When this radio was last disconnected, an audio check had not put its settings back. "
            + "Already changed by something else, so left alone: transverter drive. Not confirmed, so check the transmit antenna.");
        Assert.Equal(new[] { CheckPutBack.Antenna("ANT1", "XVT-A") }, store.PutBacksFor("unread-serial"));
    }

    [Fact]
    public async Task A_connect_that_fails_before_its_briefing_is_spoken_says_the_put_back_at_the_next_one()
    {
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks("failed-serial", new[] { CheckPutBack.Power(25, 1) });
        store.Add("failed-serial", FlexBase.CheckRestoreWithheld);
        var (briefing, said, _) = OwnBriefing();
        using var c = RadioHoldingTheChecksValues("failed-serial", store, briefing);
        string sentence = "When this radio was last disconnected, an audio check had not put its settings back. Put back: transmit power.";

        // The connect collects the sentence, then fails before its settle.
        briefing.FlowBegan(); briefing.RadioChosen();
        Announce(c.F.Rig);
        await PutBackFinishes(c);
        briefing.RadioGone(); briefing.FlowEndedWithoutRadio();
        Assert.DoesNotContain(said, u => u.Text == sentence);
        Assert.Empty(store.PutBacksFor("failed-serial"));
        Assert.True(store.Contains("failed-serial"), "what was done is still owed to the operator");

        // The next connect writes nothing, and says what the last one did.
        c.F.Vendor.Transport.Written.Clear();
        briefing.FlowBegan(); briefing.RadioChosen();
        Announce(c.F.Rig);
        briefing.Settle("Connected.", null);
        Assert.Single(said, u => u.Text == sentence);
        Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.StartsWith("transmit set", StringComparison.Ordinal));
        Assert.False(store.Contains("failed-serial"));
    }

    // ── A connect with speech suppressed (fix round 1030 P2) ──────────
    //
    // Ruled by Noel 2026-10-29 23:52: "Easy, put back, even if silent. Why
    // not, you lose nothing though a profile restore could do it anyway ... I
    // see no negative with the extra insurance." Until then a silent connect
    // put nothing back and kept everything (the test this section replaces
    // pinned exactly that).

    [Fact]
    public async Task A_connect_that_cannot_speak_puts_back_says_nothing_and_the_next_connect_that_can_says_it()
    {
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks("quiet-serial", LoopbackLeft);
        store.Add("quiet-serial", FlexBase.CheckRestoreWithheld);
        var (briefing, said, alarms) = OwnBriefing();
        using var c = RadioHoldingTheChecksValues("quiet-serial", store, briefing);
        c.F.Rig.SuppressSpeech = true;

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        // The same writes a speaking connect makes, confirmed the same way.
        var commands = c.F.Vendor.Transport.Commands;
        Assert.Contains("slice set 1 txant=ANT1", commands);
        Assert.Contains("transmit set rfpower=25", commands);
        Assert.Contains("radio set full_duplex_enabled=0", commands);
        Assert.Contains(commands, x => x.StartsWith("ping ", StringComparison.Ordinal));
        Assert.Equal(25, c.F.Vendor.Radio.RFPower);
        // Nothing said, no alarm, and nothing left to put back; what it did is kept.
        Assert.Empty(said);
        Assert.Equal(0, alarms());
        Assert.Empty(store.PutBacksFor("quiet-serial"));
        Assert.True(CheckPutBackOutcome.TryParse(store.ReasonFor("quiet-serial"), out _), "the outcome waits to be said");

        // The next connect that can speak writes nothing and says what was done.
        c.F.Rig.SuppressSpeech = false;
        c.F.Vendor.Transport.Written.Clear();
        Announce(c.F.Rig);
        Assert.Single(said, u => u.Text == "When this radio was last disconnected, an audio check had not put its settings back. "
            + "Put back: the transmit antenna, transmit power and full duplex."
            && u.Subject == Radios.Speech.SpeechSubject.CheckSettingsNotRestored);
        Assert.Equal(1, alarms());
        Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.StartsWith("transmit set", StringComparison.Ordinal)
            || x.StartsWith("radio set", StringComparison.Ordinal) || x.Contains("txant=", StringComparison.Ordinal));
        Assert.False(store.Contains("quiet-serial"));
    }

    [Fact]
    public async Task A_silent_connect_while_transmitting_writes_nothing_and_keeps_everything()
    {
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks("quiet-keyed-serial", LoopbackLeft);
        store.Add("quiet-keyed-serial", FlexBase.CheckRestoreWithheld);
        var (briefing, said, alarms) = OwnBriefing();
        using var c = RadioHoldingTheChecksValues("quiet-keyed-serial", store, briefing);
        c.F.Rig.SuppressSpeech = true;
        Mox(c.F.Rig, true);

        Announce(c.F.Rig);
        await PutBackFinishes(c);

        Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.StartsWith("transmit set", StringComparison.Ordinal)
            || x.StartsWith("radio set", StringComparison.Ordinal) || x.Contains("txant=", StringComparison.Ordinal));
        Assert.Empty(said);
        Assert.Equal(0, alarms());
        Assert.Equal(LoopbackLeft, store.PutBacksFor("quiet-keyed-serial"));
        Assert.Equal("withheld", store.ReasonFor("quiet-keyed-serial"));
    }

    [Fact]
    public async Task What_a_silent_connect_did_is_said_together_with_what_the_next_one_does()
    {
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks("two-connect-serial", LoopbackLeft);
        store.Add("two-connect-serial", FlexBase.CheckRestoreWithheld);
        var (briefing, said, alarms) = OwnBriefing();
        using var c = RadioHoldingTheChecksValues("two-connect-serial", store, briefing);
        c.F.Rig.SuppressSpeech = true;
        c.F.Rig.RestoreWriteWait = TimeSpan.FromMilliseconds(400);
        // The silent connect's antenna write is never confirmed: no transmit
        // slice can be read, so it is kept and named as not confirmed.
        Field(c.Tx, "_isTransmitSlice", false);
        Field(c.Tx, "_active", false);

        Announce(c.F.Rig);
        await PutBackFinishes(c);
        Assert.Empty(said);
        Assert.Equal(new[] { CheckPutBack.Antenna("ANT1", "XVT-A") }, store.PutBacksFor("two-connect-serial"));

        // The next connect can speak and can read the antenna: it puts that back,
        // and says the earlier connect's work with its own, once.
        c.F.Rig.SuppressSpeech = false;
        Field(c.Tx, "_isTransmitSlice", true);
        Field(c.Tx, "_active", true);
        Announce(c.F.Rig);
        await PutBackFinishes(c);

        Assert.Single(said, u => u.Text == "When this radio was last disconnected, an audio check had not put its settings back. "
            + "Put back: the transmit antenna, transmit power and full duplex.");
        Assert.Equal(1, alarms());
        Assert.False(store.Contains("two-connect-serial"));
    }

    [Fact]
    public async Task Two_connections_to_one_radio_never_put_back_at_once()
    {
        var store = new CheckRestoreReminderStore(Folder());
        store.AddPutBacks("shared-serial", new[] { CheckPutBack.Power(25, 1) });
        store.Add("shared-serial", FlexBase.CheckRestoreWithheld);
        var (briefing, _, _) = OwnBriefing();
        using var first = RadioHoldingTheChecksValues("shared-serial", store, briefing);
        using var second = RadioHoldingTheChecksValues("shared-serial", store, briefing);
        second.F.Rig.SuppressSpeech = true;

        // The first connect's put-back is waiting for its command loop.
        Announce(first.F.Rig);
        Assert.False(first.F.Rig.CheckPutBackTask.IsCompleted);

        // A second connection to the same radio, a connection tester's say,
        // leaves it to the first and writes nothing.
        Announce(second.F.Rig);
        await second.F.Rig.CheckPutBackTask;
        RunQueue(second);
        Assert.DoesNotContain(second.F.Vendor.Transport.Commands, x => x.StartsWith("transmit set", StringComparison.Ordinal));

        await PutBackFinishes(first);
        Assert.Contains("transmit set rfpower=25", first.F.Vendor.Transport.Commands);
        Assert.Empty(store.PutBacksFor("shared-serial"));
    }

    [Fact]
    public void An_earlier_outcome_merges_with_a_later_one_by_what_the_later_one_examined()
    {
        var earlier = new CheckPutBackOutcome(new[] { CheckPutBack.RfPower }, new[] { CheckPutBack.Monitor },
            new[] { CheckPutBack.TxAntenna, CheckPutBack.FullDuplex });
        // The later connect examined the antenna and full duplex: it put the
        // antenna back and found full duplex already at its original.
        var now = new CheckPutBackOutcome(new[] { CheckPutBack.TxAntenna }, Array.Empty<string>(), Array.Empty<string>());
        var merged = CheckPutBackOutcome.Merge(earlier, now, new[] { CheckPutBack.TxAntenna, CheckPutBack.FullDuplex });
        Assert.Equal(new[] { CheckPutBack.RfPower, CheckPutBack.TxAntenna }, merged.PutBack);
        Assert.Equal(new[] { CheckPutBack.Monitor }, merged.LeftAlone);
        Assert.Empty(merged.NotPutBack);
    }

    [Fact]
    public void The_outcome_round_trips_through_the_reminder_file_and_says_each_list_only_when_it_has_one()
    {
        var outcome = new CheckPutBackOutcome(new[] { CheckPutBack.RfPower, CheckPutBack.TunePower },
            new[] { CheckPutBack.Monitor }, new[] { CheckPutBack.XvtrDrive });
        Assert.True(CheckPutBackOutcome.TryParse(outcome.Reason, out var read));
        Assert.Equal(outcome.PutBack, read.PutBack);
        Assert.Equal(outcome.LeftAlone, read.LeftAlone);
        Assert.Equal(outcome.NotPutBack, read.NotPutBack);
        Assert.Equal("When this radio was last disconnected, an audio check had not put its settings back. "
            + "Put back: transmit power and tune power. Already changed by something else, so left alone: the monitor. "
            + "Not confirmed, so check transverter drive.", read.Sentence());
        Assert.False(CheckPutBackOutcome.TryParse(FlexBase.CheckRestoreWithheld, out _));
        Assert.False(CheckPutBackOutcome.TryParse(FlexBase.CheckRestoreUnconfirmed, out _));
    }

    [Fact]
    public void The_checks_other_changes_say_what_they_put_back()
    {
        // The dummy load, the power cap and the monitor an ordinary check
        // changes are recorded the same way as Loopback's.
        string dialog = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Check.cs"));
        Assert.Contains("rig.DescribeCheckPutBack(entry, putBacks);", dialog);
        Assert.Contains("}, CheckPutBack.Power(rf, 0), CheckPutBack.Tune(tune, 0));", dialog);
        Assert.Contains("}, CheckPutBack.Power(saved, _lowPowerWatts));", dialog);
        Assert.Contains("CheckPutBack.MonitorOn(false, true));", dialog);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
