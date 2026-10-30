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
}
