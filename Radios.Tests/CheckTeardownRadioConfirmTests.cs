using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// A teardown that ends a KEYED check reports "completed" only on the radio's
/// own word (#835, fix round 2026-10-28): the restore waits for the radio's
/// interlock to report the unkey, not for our own cached Mox, and the teardown
/// waits for the radio to answer after the restore writes, not merely for our
/// command queue to have handed them to the socket.
/// </summary>
/// <remarks>
/// Runs the real <see cref="FlexBase"/>, its restore owner and its settle
/// against the vendored FlexLib on a fake transport, and reads the commands
/// that actually go out. What it cannot see: a real radio's timing, and
/// whether a real 8600 answers a ping only after the writes before it (the
/// command protocol is in-order, which is what this relies on).
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class CheckTeardownRadioConfirmTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(CheckTeardownRadioConfirmTests));
    public void Dispose() => _scope.Dispose();

    private static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0)
            if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
    }

    private static int IndexOf(RadioCase c, string fragment, int from = 0)
    {
        var commands = c.F.Vendor.Transport.Commands.ToList();
        for (int i = from; i < commands.Count; i++)
            if (commands[i].Contains(fragment, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static async Task Pump(RadioCase c, Func<bool> until, TimeSpan bound)
    {
        var deadline = DateTime.UtcNow + bound;
        while (!until() && DateTime.UtcNow < deadline) { RunQueue(c); await Task.Delay(20); }
    }

    /// <summary>The mic test's own shape: a held power restore, released by the teardown.</summary>
    private static CheckRestorations.Entry RememberPower(RadioCase c, int watts) =>
        c.F.Rig.CheckRestorations.Register("audio check: power",
            () => c.F.Rig.RestoreWritesAtReceiveAsync("audio check: power", () => c.F.Rig.XmitPower = watts), held: true);

    [Fact]
    public async Task A_keyed_check_torn_down_puts_power_back_only_after_the_radio_reports_its_unkey_and_answers()
    {
        using var c = new RadioCase();
        // The check is keyed: the radio's interlock says so. Our own unkey has
        // gone out, so FlexLib's cached Mox, and therefore Transmit, already
        // say receive. That optimistic belief is exactly what released the
        // restore before.
        c.F.Vendor.Status("interlock state=TRANSMITTING source=SW");
        Assert.True(c.F.Rig.RadioInterlockReportsTransmit);
        Assert.False(c.F.Rig.Transmit);

        RememberPower(c, 77);
        c.F.Rig.BeginCheckTeardown();
        var settle = c.F.Rig.SettleChecksForTeardownAsync(TimeSpan.FromSeconds(5));
        var until = DateTime.UtcNow.AddMilliseconds(700);
        while (DateTime.UtcNow < until) { RunQueue(c); await Task.Delay(20); }
        Assert.False(settle.IsCompleted, "the teardown waits while the radio still reports keyed");
        Assert.Equal(-1, IndexOf(c, "rfpower=77"));

        // The radio reports its unkey.
        c.F.Vendor.Status("interlock state=UNKEY_REQUESTED");
        c.F.Vendor.Status("interlock state=READY");
        await Pump(c, () => settle.IsCompleted, TimeSpan.FromSeconds(6));
        var result = await settle;
        Assert.True(result.Completed, "stage " + result.Stage);

        int write = IndexOf(c, "rfpower=77");
        Assert.True(write >= 0, "the operator's power went back");
        Assert.True(IndexOf(c, "ping", write + 1) > write, "the radio was asked to answer AFTER the restore write");
        c.F.Rig.EndCheckTeardown();
    }

    [Fact]
    public async Task A_radio_that_never_answers_after_the_writes_is_not_called_completed()
    {
        using var c = new RadioCase();
        var answer = c.F.Vendor.Transport.CommandWritten;
        // The radio takes the writes but its answer never comes: the
        // connection is going, or the command channel has stalled.
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("ping", StringComparison.Ordinal)) return;
            answer!(command);
        };
        RememberPower(c, 66);
        c.F.Rig.BeginCheckTeardown();
        var settle = c.F.Rig.SettleChecksForTeardownAsync(TimeSpan.FromMilliseconds(1500));
        await Pump(c, () => settle.IsCompleted, TimeSpan.FromSeconds(4));
        var result = await settle;
        Assert.Equal(new AudioCheckTeardownResult(false, "radio"), result);
        // Positive control: the write itself DID go out; only the confirmation is missing.
        Assert.True(IndexOf(c, "rfpower=66") >= 0);
        c.F.Rig.EndCheckTeardown();
    }

    [Fact]
    public async Task The_confirmation_comes_after_the_drain_and_a_refusal_is_named()
    {
        var order = new System.Collections.Generic.List<string>();
        var ok = await AudioCheckTeardown.RunAsync(() => Task.FromResult(true), () => false,
            _ => { order.Add("drain"); return Task.FromResult(true); }, TimeSpan.FromSeconds(2),
            _ => { order.Add("confirm"); return Task.FromResult(true); });
        Assert.True(ok.Completed);
        Assert.Equal(new[] { "drain", "confirm" }, order);

        var refused = await AudioCheckTeardown.RunAsync(() => Task.FromResult(true), () => false,
            _ => Task.FromResult(true), TimeSpan.FromSeconds(2), _ => Task.FromResult(false));
        Assert.Equal(new AudioCheckTeardownResult(false, "radio"), refused);

        var threw = await AudioCheckTeardown.RunAsync(() => Task.FromResult(true), () => false,
            _ => Task.FromResult(true), TimeSpan.FromSeconds(2), _ => throw new InvalidOperationException());
        Assert.Equal("radio", threw.Stage);
    }

    [Fact]
    public async Task The_confirmation_still_goes_out_and_completes_while_a_trace_topic_is_held()
    {
        // Codex round 1, follow-up 3: FlexLib's SendCommand, with any telnet
        // trace topic selected, takes the command's first word with
        // Substring(0, IndexOf(' ')) and throws on a one-word command. A status
        // tap (an IQ take, Track B's send-piece PTT watch) holds topics, so a
        // bare "ping" sent during one failed before it reached the socket.
        using var c = new RadioCase();
        using var tap = new Radios.Listenback.RadioStatusTap(c.F.Vendor.Radio);
        Assert.NotEmpty(c.F.Vendor.Radio.SelectedTelnetTraceTopics);   // the condition under test is real

        RememberPower(c, 55);
        c.F.Rig.BeginCheckTeardown();
        var settle = c.F.Rig.SettleChecksForTeardownAsync(TimeSpan.FromSeconds(4));
        await Pump(c, () => settle.IsCompleted, TimeSpan.FromSeconds(6));
        var result = await settle;
        Assert.True(result.Completed, "stage " + result.Stage);
        int write = IndexOf(c, "rfpower=55");
        int ping = IndexOf(c, "ping ", write + 1);
        Assert.True(write >= 0 && ping > write, "the confirmation went out after the write, as a command FlexLib can trace");
        c.F.Rig.EndCheckTeardown();
    }

    [Fact]
    public async Task With_a_trace_topic_held_an_unanswered_confirmation_is_still_named_not_completed()
    {
        using var c = new RadioCase();
        using var tap = new Radios.Listenback.RadioStatusTap(c.F.Vendor.Radio);
        var answer = c.F.Vendor.Transport.CommandWritten;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.StartsWith("ping", StringComparison.Ordinal)) return;
            answer!(command);
        };
        RememberPower(c, 44);
        c.F.Rig.BeginCheckTeardown();
        var settle = c.F.Rig.SettleChecksForTeardownAsync(TimeSpan.FromMilliseconds(1500));
        await Pump(c, () => settle.IsCompleted, TimeSpan.FromSeconds(4));
        Assert.Equal(new AudioCheckTeardownResult(false, "radio"), await settle);
        Assert.True(IndexOf(c, "ping ") >= 0, "the confirmation was asked for, and simply not answered");
        c.F.Rig.EndCheckTeardown();
    }

    [Fact]
    public async Task The_confirmation_completes_while_the_send_pieces_interlock_topic_is_held()
    {
        // The exact shape Track B's send-piece PTT-source watch leaves behind:
        // the "interlock" topic selected on the radio for as long as a send runs.
        using var c = new RadioCase();
        c.F.Vendor.Radio.AddTelnetTraceTopic("interlock");
        try
        {
            RememberPower(c, 33);
            c.F.Rig.BeginCheckTeardown();
            var settle = c.F.Rig.SettleChecksForTeardownAsync(TimeSpan.FromSeconds(4));
            await Pump(c, () => settle.IsCompleted, TimeSpan.FromSeconds(6));
            var result = await settle;
            Assert.True(result.Completed, "stage " + result.Stage);
            c.F.Rig.EndCheckTeardown();
        }
        finally { c.F.Vendor.Radio.RemoveTelnetTraceTopic("interlock"); }
    }
}
