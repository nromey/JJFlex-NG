using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// An audio check's OWN restores, the ones a teardown waits for, read as done
/// only on each write's own reply (#835, round 1030 repairs, track RP, point 3,
/// after Codex's B3 on the put-back).
/// </summary>
/// <remarks>
/// <para>
/// Until this, a restore queued ordinary setters, FlexLib sent each command and
/// discarded the radio's reply, and the restore counted as done as soon as the
/// queue had run it. The teardown then asked the radio to answer one ping
/// (<c>ConfirmWritesReachedRadioAsync</c>), and that answer proves the radio
/// PROCESSED the writes, not that it TOOK them. So a restore the radio
/// rejected read as restored, the teardown said completed, and nothing was
/// recorded for the next connect's put-back.
/// </para>
/// <para>
/// Now, while a restore runs, the writes its setters queue go out through the
/// reply-bearing path; a rejection or no answer makes the restore Failed, the
/// teardown unconfirmed, and the setting owed. The run-time transmit refusal
/// (our Mox or the radio's interlock) and its Retry are unchanged.
/// </para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class CheckTeardownReplyTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(CheckTeardownReplyTests));
    public void Dispose() => _scope.Dispose();

    private static RadioCase Radio(string serial)
    {
        var c = new RadioCase(serial);
        c.F.Rig.RestoreWriteWait = TimeSpan.FromMilliseconds(600);
        Field(c.F.Vendor.Radio, "_rfPower", 1);
        Field(c.F.Vendor.Radio, "_tunePower", 0);
        Field(c.F.Vendor.Radio, "_txMonitor", true);
        Field(c.F.Vendor.Radio, "_micInput", "MIC");
        c.F.Vendor.Transport.Written.Clear();
        return c;
    }

    /// <summary>The radio rejects commands starting <paramref name="prefix"/>, with no status, and answers the rest, pings included.</summary>
    private static void Rejects(RadioCase c, string prefix) =>
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith(prefix, StringComparison.Ordinal))
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000016, "refused");
            else c.Respond(command);
        };

    /// <summary>The radio never answers commands starting <paramref name="prefix"/>, and answers the rest.</summary>
    private static void NeverAnswers(RadioCase c, string prefix) =>
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith(prefix, StringComparison.Ordinal)) return;
            c.Respond(command);
        };

    private static Action Restore(FlexBase rig, string what) => what switch
    {
        "power" => () => { using (rig.AppInitiatedSettingChanges()) rig.XmitPower = 80; },
        "tune" => () => { using (rig.AppInitiatedSettingChanges()) rig.TunePower = 40; },
        "monitor" => () => rig.Monitor = FlexBase.OffOnValues.off,
        "mic" => () => { using (rig.AppInitiatedSettingChanges()) rig.MicSource = "PC"; },
        _ => throw new ArgumentOutOfRangeException(nameof(what)),
    };

    private static string Command(string what) => what switch
    {
        "power" => "transmit set rfpower=80",
        "tune" => "transmit set tunepower=40",
        "monitor" => "transmit set mon=0",
        "mic" => "mic input PC",
        _ => throw new ArgumentOutOfRangeException(nameof(what)),
    };

    private static bool Wrote(RadioCase c, string command) => c.F.Vendor.Transport.Commands.Contains(command);

    // ── One restore ────────────────────────────────────────────────────

    [Theory]
    [InlineData("power")]
    [InlineData("tune")]
    [InlineData("monitor")]
    [InlineData("mic")]
    public async Task A_restore_write_the_radio_rejects_is_not_restored(string what)
    {
        using var c = Radio("reject-" + what);
        string command = Command(what);
        Rejects(c, command);

        var outcome = await c.Pump(c.F.Rig.RestoreWritesAtReceiveAsync(what, Restore(c.F.Rig, what)));

        Assert.True(Wrote(c, command), "the write must have gone out for its rejection to mean anything");
        Assert.Equal(RestoreOutcome.Failed, outcome);
    }

    [Theory]
    [InlineData("power")]
    [InlineData("monitor")]
    public async Task A_restore_write_the_radio_never_answers_is_not_restored(string what)
    {
        using var c = Radio("silent-" + what);
        string command = Command(what);
        NeverAnswers(c, command);

        var outcome = await c.Pump(c.F.Rig.RestoreWritesAtReceiveAsync(what, Restore(c.F.Rig, what)));

        Assert.True(Wrote(c, command));
        Assert.Equal(RestoreOutcome.Failed, outcome);
    }

    [Theory]
    [InlineData("power")]
    [InlineData("tune")]
    [InlineData("monitor")]
    [InlineData("mic")]
    public async Task Control_a_restore_write_the_radio_accepts_is_restored(string what)
    {
        using var c = Radio("accept-" + what);

        var outcome = await c.Pump(c.F.Rig.RestoreWritesAtReceiveAsync(what, Restore(c.F.Rig, what)));

        Assert.True(Wrote(c, Command(what)));
        Assert.Equal(RestoreOutcome.Done, outcome);
    }

    [Fact]
    public async Task The_dummy_loads_release_waits_for_both_powers_replies()
    {
        // The Audio Check's dummy-load restore releases the mode, whose setter
        // writes transmit AND tune power; a rejected tune power is a failed release.
        using var c = Radio("dummy-load");
        var rig = c.F.Rig;
        Field(rig, "_dummyLoadMode", true);
        Field(rig, "_savedRFPower", 80);
        Field(rig, "_savedTunePower", 40);
        Rejects(c, "transmit set tunepower");

        var outcome = await c.Pump(rig.RestoreWritesAtReceiveAsync("dummy load", () => rig.DummyLoadMode = false));

        Assert.True(Wrote(c, "transmit set rfpower=80"));
        Assert.True(Wrote(c, "transmit set tunepower=40"));
        Assert.Equal(RestoreOutcome.Failed, outcome);
    }

    [Fact]
    public async Task The_transmit_refusal_at_the_writes_run_is_still_a_Retry_and_sends_nothing()
    {
        using var c = Radio("retry-serial");
        var rig = c.F.Rig;

        var restore = rig.RestoreWritesAtReceiveAsync("power", Restore(rig, "power"));
        c.F.Vendor.Status("interlock state=TRANSMITTING source=RCA");
        var outcome = await c.Pump(restore);

        Assert.Equal(RestoreOutcome.Retry, outcome);
        Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.StartsWith("transmit set rfpower", StringComparison.Ordinal));
    }

    // ── Through the teardown to the next connect's record ──────────────

    [Fact]
    public async Task A_teardown_whose_restore_the_radio_rejects_is_unconfirmed_and_the_setting_is_owed()
    {
        using var c = Radio("teardown-serial");
        var rig = c.F.Rig;
        var store = new CheckRestoreReminderStore(Path.Combine(RadioConfig.AppDataRoot, "teardown-" + Guid.NewGuid().ToString("N")));
        rig.CheckRestoreRemindersOverride = store;
        Rejects(c, "transmit set rfpower");

        // What the check's power cap registers (CheckYourAudioDialog's Remember).
        var entry = rig.CheckRestorations.Register("audio check: power",
            () => rig.RestoreWritesAtReceiveAsync("audio check: power", Restore(rig, "power")), held: true);
        rig.DescribeCheckPutBack(entry, CheckPutBack.Power(80, 1));

        rig.BeginCheckTeardown();
        AudioCheckTeardownResult result;
        try { result = await c.Pump(rig.SettleChecksForTeardownAsync(TimeSpan.FromSeconds(5))); }
        finally { rig.EndCheckTeardown(); }

        Assert.True(Wrote(c, "transmit set rfpower=80"));
        Assert.True(c.F.Vendor.Transport.Commands.Any(x => x.StartsWith("ping ", StringComparison.Ordinal))
            || result.Stage == "faulted", "either the ping ran or the teardown stopped before it");
        Assert.False(result.Completed, "a rejected restore read as done");
        Assert.NotEqual("still-transmitting", result.Stage);

        // The dialog's unconfirmed path, as at a disconnect.
        rig.NoteCheckRestoreReminder(FlexBase.CheckRestoreUnconfirmed);
        Assert.Equal(new[] { CheckPutBack.Power(80, 1) }, store.PutBacksFor(c.F.Vendor.Radio.Serial));
        Assert.Equal(FlexBase.CheckRestoreUnconfirmed, store.ReasonFor(c.F.Vendor.Radio.Serial));
    }

    [Fact]
    public async Task Control_a_teardown_whose_restore_the_radio_accepts_completes_and_owes_nothing()
    {
        using var c = Radio("teardown-ok-serial");
        var rig = c.F.Rig;
        var entry = rig.CheckRestorations.Register("audio check: power",
            () => rig.RestoreWritesAtReceiveAsync("audio check: power", Restore(rig, "power")), held: true);
        rig.DescribeCheckPutBack(entry, CheckPutBack.Power(80, 1));

        rig.BeginCheckTeardown();
        AudioCheckTeardownResult result;
        try { result = await c.Pump(rig.SettleChecksForTeardownAsync(TimeSpan.FromSeconds(5))); }
        finally { rig.EndCheckTeardown(); }

        Assert.True(result.Completed, "stage " + result.Stage);
        Assert.Empty(rig.CheckPutBacksOwed());
    }

    // ── The Loopback arrangement's own restores ────────────────────────

    private static async Task<LoopbackEndOutcome> LoopbackEndedWith(RadioCase c, Action<string> respond)
    {
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
        while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
        c.F.Vendor.Transport.Written.Clear();
        c.F.Vendor.Transport.CommandWritten = respond;
        return await c.Pump(c.F.Rig.EndLoopbackArrangementWithOutcomeAsync());
    }

    [Fact]
    public async Task A_Loopback_transmit_antenna_the_radio_rejects_is_named_not_restored_and_owed()
    {
        using var c = new RadioCase("lb-antenna-serial");
        c.F.Rig.RestoreWriteWait = TimeSpan.FromMilliseconds(600);
        var outcome = await LoopbackEndedWith(c, command =>
        {
            if (command.Contains(" txant=ANT1", StringComparison.Ordinal))
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000016, "refused");
            else c.Respond(command);
        });

        Assert.Contains(LoopbackEndOutcome.Antenna, outcome.NotRestored);
        Assert.Contains(c.F.Rig.CheckPutBacksOwed(), p => p.Setting == CheckPutBack.TxAntenna);
    }

    [Fact]
    public async Task A_Loopback_power_the_radio_rejects_is_named_not_restored_and_owed()
    {
        using var c = new RadioCase("lb-power-serial");
        c.F.Rig.RestoreWriteWait = TimeSpan.FromMilliseconds(600);
        var outcome = await LoopbackEndedWith(c, command =>
        {
            if (command.StartsWith("transmit set rfpower=25", StringComparison.Ordinal))
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000016, "refused");
            else c.Respond(command);
        });

        Assert.Contains(LoopbackEndOutcome.Power, outcome.NotRestored);
        Assert.Contains(c.F.Rig.CheckPutBacksOwed(), p => p.Setting == CheckPutBack.RfPower);
    }

    [Fact]
    public async Task Control_a_Loopback_the_radio_accepts_restores_everything()
    {
        using var c = new RadioCase("lb-ok-serial");
        c.F.Rig.RestoreWriteWait = TimeSpan.FromMilliseconds(600);
        var outcome = await LoopbackEndedWith(c, c.Respond);

        Assert.Empty(outcome.NotRestored);
        Assert.Contains(LoopbackEndOutcome.Antenna, outcome.Restored);
        Assert.Contains(LoopbackEndOutcome.Power, outcome.Restored);
        Assert.Empty(c.F.Rig.CheckPutBacksOwed());
    }

    // ── Codex round 2 (2026-10-30), two findings in this area ──────────

    /// <summary>The radio acknowledges every command and echoes no status at all, as it does for the monitor.</summary>
    private static void AcknowledgesWithoutEcho(RadioCase c) =>
        c.F.Vendor.Transport.CommandWritten = command => c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);

    [Fact]
    public async Task Round2_a_dummy_load_release_refused_at_its_run_sends_the_powers_when_retried()
    {
        // Codex's schedule: DummyLoadMode = false flips the flag, its two power
        // writes are queued, the radio keys before the loop reaches them, both
        // are refused (Retry); the radio unkeys and the restore runs again.
        using var c = Radio("dummy-retry");
        var rig = c.F.Rig;
        Field(rig, "_dummyLoadMode", true);
        Field(rig, "_savedRFPower", 80);
        Field(rig, "_savedTunePower", 40);
        Field(c.F.Vendor.Radio, "_rfPower", 0);
        Field(c.F.Vendor.Radio, "_tunePower", 0);
        c.F.Vendor.Transport.Written.Clear();

        var first = rig.RestoreWritesAtReceiveAsync("dummy load", () => rig.DummyLoadMode = false);
        Assert.True(rig.q.Count > 0, "the release's writes were never queued, so the schedule proves nothing");
        c.F.Vendor.Status("interlock state=TRANSMITTING source=RCA");
        Assert.Equal(RestoreOutcome.Retry, await c.Pump(first));
        Assert.Empty(c.F.Vendor.Transport.Commands);

        c.F.Vendor.Status("interlock state=READY source=NONE");
        var second = await c.Pump(rig.RestoreWritesAtReceiveAsync("dummy load", () => rig.DummyLoadMode = false));

        Assert.Equal(RestoreOutcome.Done, second);
        Assert.Contains("transmit set rfpower=80", c.F.Vendor.Transport.Commands);
        Assert.Contains("transmit set tunepower=40", c.F.Vendor.Transport.Commands);
        Assert.Equal(80, c.F.Vendor.Radio.RFPower);
        Assert.Equal(40, c.F.Vendor.Radio.TunePower);
    }

    [Fact]
    public async Task Round2_control_a_dummy_load_release_that_went_out_is_not_sent_again()
    {
        using var c = Radio("dummy-once");
        var rig = c.F.Rig;
        Field(rig, "_dummyLoadMode", true);
        Field(rig, "_savedRFPower", 80);
        Field(rig, "_savedTunePower", 40);
        Assert.Equal(RestoreOutcome.Done, await c.Pump(rig.RestoreWritesAtReceiveAsync("dummy load", () => rig.DummyLoadMode = false)));
        c.F.Vendor.Transport.Written.Clear();

        Assert.Equal(RestoreOutcome.Done, await c.Pump(rig.RestoreWritesAtReceiveAsync("dummy load", () => rig.DummyLoadMode = false)));
        Assert.Empty(c.F.Vendor.Transport.Commands);
    }

    [Fact]
    public async Task Round2_an_acknowledged_monitor_restore_with_no_echo_leaves_FlexLibs_cache_true_and_the_next_send_keeps_it_on()
    {
        // Codex's schedule: the radio holds the monitor off; the restore turns it
        // ON, the radio acknowledges and sends no status. Then a send holds the
        // monitor and lets go: it must find the monitor already on and leave it.
        using var c = Radio("monitor-cache");
        var rig = c.F.Rig;
        Field(rig, "_IsConnected", true);
        Field(c.F.Vendor.Radio, "_txMonitor", false);
        bool held = false;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("transmit set mon=", StringComparison.Ordinal))
            {
                held = command.EndsWith("1", StringComparison.Ordinal);
                c.F.Vendor.ReplyOk(c.F.Vendor.LastSequence);
            }
            else c.Respond(command);
        };

        var restored = await c.Pump(rig.RestoreWritesAtReceiveAsync("monitor", () => rig.Monitor = FlexBase.OffOnValues.on));
        Assert.Equal(RestoreOutcome.Done, restored);
        Assert.True(held);
        Assert.True(c.F.Vendor.Radio.TXMonitor, "FlexLib still caches the monitor off though the radio took ON");

        c.F.Vendor.Transport.Written.Clear();
        var hold = rig.HoldMonitorForSend("after the restore");
        while (rig.q.Count > 0) if (rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
        hold.Dispose();
        while (rig.q.Count > 0) if (rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();

        Assert.True(held, "the send turned off the monitor the operator had on");
        Assert.DoesNotContain("transmit set mon=0", c.F.Vendor.Transport.Commands);
    }

    [Theory]
    [InlineData("power")]
    [InlineData("tune")]
    [InlineData("monitor")]
    [InlineData("mic")]
    [InlineData("antenna")]
    public async Task Round2_every_raw_restore_write_leaves_FlexLibs_cache_true_when_the_radio_accepts_without_an_echo(string what)
    {
        using var c = Radio("cache-" + what);
        var rig = c.F.Rig;
        Field(c.Tx, "_txant", "XVT-A");
        AcknowledgesWithoutEcho(c);
        Action restore = what == "antenna" ? () => rig.TXAntennaName = "ANT1" : Restore(rig, what);

        Assert.Equal(RestoreOutcome.Done, await c.Pump(rig.RestoreWritesAtReceiveAsync(what, restore)));

        var r = c.F.Vendor.Radio;
        switch (what)
        {
            case "power": Assert.Equal(80, r.RFPower); break;
            case "tune": Assert.Equal(40, r.TunePower); break;
            case "monitor": Assert.False(r.TXMonitor); break;
            case "mic": Assert.Equal("PC", r.MicInput); break;
            case "antenna": Assert.Equal("ANT1", c.Tx.TXAnt); break;
        }
    }

    [Theory]
    [InlineData("monitor")]
    [InlineData("power")]
    [InlineData("antenna")]
    public async Task Round2_the_put_backs_raw_writes_also_leave_the_cache_true_when_accepted_without_an_echo(string what)
    {
        string serial = "putback-cache-" + what;
        var store = new CheckRestoreReminderStore(Path.Combine(RadioConfig.AppDataRoot, "pb-cache-" + Guid.NewGuid().ToString("N")));
        var item = what switch
        {
            "monitor" => CheckPutBack.MonitorOn(true, false),
            "power" => CheckPutBack.Power(80, 1),
            _ => CheckPutBack.Antenna("ANT1", "XVT-A"),
        };
        store.AddPutBacks(serial, new[] { item });
        store.Add(serial, FlexBase.CheckRestoreWithheld);
        using var c = Radio(serial);
        var rig = c.F.Rig;
        rig.CheckRestoreRemindersOverride = store;
        rig.CheckRestoreBriefingOverride = new ConnectBriefing(_ => { }, () => { });
        rig.RestoreWriteWait = TimeSpan.FromSeconds(2);
        Field(c.F.Vendor.Radio, "_txMonitor", false);
        Field(c.Tx, "_txant", "XVT-A");
        AcknowledgesWithoutEcho(c);

        typeof(FlexBase).GetMethod("AnnounceCheckRestoreReminder", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig, null);
        await c.Pump(rig.CheckPutBackTask.ContinueWith(_ => true));

        Assert.Empty(store.PutBacksFor(serial));
        var r = c.F.Vendor.Radio;
        switch (what)
        {
            case "monitor": Assert.True(r.TXMonitor); break;
            case "power": Assert.Equal(80, r.RFPower); break;
            case "antenna": Assert.Equal("ANT1", c.Tx.TXAnt); break;
        }
    }

    // ── Codex round 2 restarted (2026-10-30): R2-B1 and R2-B2 ──────────
    //
    // R2-B1: the Loopback ending's full-duplex and slice-mute restores (and its
    // listening-slice removal) passed the owner's receive question and then sent
    // from their queued action without asking again, so a key landing between
    // the two got the write while the radio's interlock said TRANSMITTING.
    // R2-B2: an antenna restore with no transmit slice to apply it to sent
    // nothing and read as Done, so the debt was dropped.

    /// <summary>
    /// Codex's schedule: a real Loopback start and ending on the in-memory
    /// transport. The command loop is run here by hand; once <paramref name="marker"/>
    /// has gone out (or at once, when it is null), the first queued item named <paramref name="boundaryName"/>
    /// is the restore's own send. When <paramref name="inject"/>, the radio's
    /// interlock reports TRANSMITTING just before that item runs and READY just
    /// after it. Returns the ending and, for every <paramref name="target"/>
    /// command at the wire, whether the interlock said Transmitting then.
    /// </summary>
    private static async Task<(LoopbackEndOutcome Outcome, System.Collections.Generic.List<bool> AtWire, bool Reached)> LoopbackKeyedAtTheRestoresRun(
        RadioCase c, string marker, string target, string boundaryName, bool inject)
    {
        var atWire = new System.Collections.Generic.List<bool>();
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
        while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
        c.F.Vendor.Transport.Written.Clear();
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command == target)
                lock (atWire) atWire.Add(c.F.Vendor.Radio.InterlockState == Flex.Smoothlake.FlexLib.InterlockState.Transmitting);
            c.Respond(command);
        };

        var end = c.F.Rig.EndLoopbackArrangementWithOutcomeAsync();
        bool reached = false;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!end.IsCompleted && DateTime.UtcNow < deadline)
        {
            while (c.F.Rig.q.Count > 0)
            {
                var item = c.F.Rig.q.Dequeue();
                bool boundary = !reached && item.Name == boundaryName &&
                    (marker == null || c.F.Vendor.Transport.Commands.Contains(marker)) && !c.F.Vendor.Transport.Commands.Contains(target);
                if (boundary)
                {
                    reached = true;
                    if (inject) c.F.Vendor.Status("interlock state=TRANSMITTING source=RCA");
                }
                if (item.Item is FlexBase.FunctionDel run) run();
                if (boundary && inject) c.F.Vendor.Status("interlock state=READY source=NONE");
            }
            await Task.Delay(5);
        }
        return (await end.WaitAsync(TimeSpan.FromSeconds(1)), atWire, reached);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task R2B1_the_Loopback_full_duplex_restore_waits_out_a_key_that_lands_at_its_run(bool inject)
    {
        using var c = new RadioCase("r2b1-duplex-" + inject);
        c.F.Rig.RestoreWriteWait = TimeSpan.FromMilliseconds(800);
        var (outcome, atWire, reached) = await LoopbackKeyedAtTheRestoresRun(c,
            "transmit set mon=0", "radio set full_duplex_enabled=0", "Listenback", inject);

        Assert.True(reached, "the full duplex restore's queued send was never found, so the schedule proves nothing");
        Assert.DoesNotContain(true, atWire);
        Assert.NotEmpty(atWire);
        Assert.Contains(LoopbackEndOutcome.Duplex, outcome.Restored);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task R2B1_the_Loopback_slice_mute_restore_waits_out_a_key_that_lands_at_its_run(bool inject)
    {
        using var c = new RadioCase("r2b1-mute-" + inject);
        c.F.Rig.RestoreWriteWait = TimeSpan.FromMilliseconds(800);
        var (outcome, atWire, reached) = await LoopbackKeyedAtTheRestoresRun(c,
            "slice remove 0", "slice set 1 audio_mute=0", "Listenback", inject);

        Assert.True(reached, "the mute restore's queued send was never found, so the schedule proves nothing");
        Assert.DoesNotContain(true, atWire);
        Assert.NotEmpty(atWire);
        Assert.Contains(LoopbackEndOutcome.Mutes, outcome.Restored);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task R2B1_the_Loopback_listening_slice_removal_waits_out_a_key_that_lands_at_its_run(bool inject)
    {
        // Codex asked for the removal to be audited too; it was unguarded in source.
        using var c = new RadioCase("r2b1-removal-" + inject);
        c.F.Rig.RestoreWriteWait = TimeSpan.FromMilliseconds(800);
        // The removal is the first Loopback step that writes; it needs no marker.
        var (_, atWire, reached) = await LoopbackKeyedAtTheRestoresRun(c,
            null, "slice remove 0", "LoopbackEarsRemove", inject);
        Assert.True(reached, "the removal's queued action was never found");
        Assert.DoesNotContain(true, atWire);
        Assert.NotEmpty(atWire);
    }

    [Fact]
    public async Task R2B2_an_antenna_restore_with_no_transmit_slice_is_not_restored()
    {
        using var c = Radio("r2b2-no-slice");
        var rig = c.F.Rig;
        // Codex's helper schedule: no transmit or active slice at all.
        lock (((System.Collections.Generic.List<Flex.Smoothlake.FlexLib.Slice>)typeof(FlexBase)
            .GetField("mySlices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig)!))
            ((System.Collections.Generic.List<Flex.Smoothlake.FlexLib.Slice>)typeof(FlexBase)
                .GetField("mySlices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig)!).Clear();
        c.F.Vendor.Radio.SliceList.Clear();

        var outcome = await c.Pump(rig.RestoreWritesAtReceiveAsync("antenna", () => rig.TXAntennaName = "ANT2"));

        Assert.Empty(c.F.Vendor.Transport.Commands);
        Assert.Equal(RestoreOutcome.Failed, outcome);
    }

    [Fact]
    public async Task R2B2_control_an_antenna_restore_with_its_slice_is_sent_and_restored()
    {
        using var c = Radio("r2b2-slice");
        var outcome = await c.Pump(c.F.Rig.RestoreWritesAtReceiveAsync("antenna", () => c.F.Rig.TXAntennaName = "ANT2"));
        Assert.Contains("slice set 1 txant=ANT2", c.F.Vendor.Transport.Commands);
        Assert.Equal(RestoreOutcome.Done, outcome);
    }

    [Fact]
    public async Task R2B2_a_Loopback_whose_transmit_slice_went_names_the_antenna_not_restored_and_owes_it()
    {
        // Codex's full schedule: while the listening slice's removal completes,
        // the radio reports the transmit slice gone on the same connection.
        using var c = new RadioCase("r2b2-loopback");
        c.F.Rig.RestoreWriteWait = TimeSpan.FromMilliseconds(600);
        bool removed = false;
        var mySlices = (System.Collections.Generic.List<Flex.Smoothlake.FlexLib.Slice>)typeof(FlexBase)
            .GetField("mySlices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.F.Rig)!;
        var outcome = await LoopbackEndedWith(c, command =>
        {
            c.Respond(command);
            if (command.StartsWith("slice remove ", StringComparison.Ordinal))
            {
                c.F.Vendor.Status("slice 1 in_use=0");
                lock (mySlices) mySlices.Clear();
                c.F.Vendor.Radio.SliceList.Clear();
                removed = true;
            }
        });

        Assert.True(removed, "the transmit slice never went, so the schedule proves nothing");
        Assert.DoesNotContain(c.F.Vendor.Transport.Commands, x => x.Contains("txant=", StringComparison.Ordinal));
        Assert.Contains(LoopbackEndOutcome.Antenna, outcome.NotRestored);
        Assert.DoesNotContain(LoopbackEndOutcome.Antenna, outcome.Restored);
        Assert.Contains(c.F.Rig.CheckPutBacksOwed(), p => p.Setting == CheckPutBack.TxAntenna);
    }
}
