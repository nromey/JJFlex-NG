using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// What an Audio Check changed is put back only while the radio reports
/// receive, by one owner that lives with the radio (#835, fix round 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard.</b> Ending the Loopback arrangement restored the transmit
/// antenna, the operator's power and the transverter drive without asking
/// whether the radio was transmitting. With a hardware PTT line holding the
/// radio keyed, closing the check window or disconnecting put full power on
/// the prior antenna while the line was still held.
/// </para>
/// <para>
/// These run the real <c>FlexBase</c> Loopback arrangement against the
/// listen-back fake radio and read the commands it actually wrote. What they
/// cannot see is a real radio's keying line.
/// </para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class CheckRestorationTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(CheckRestorationTests));
    public void Dispose() => _scope.Dispose();

    private static RadioCase Arranged()
    {
        var c = new RadioCase();
        Field(c.Tx.Panadapter, "_rx_antenna_list", new[] { "ANT1", "XVT-A", "XVT-B" });
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
        return c;
    }

    private static void Mox(FlexBase rig, bool on) =>
        typeof(FlexBase).GetMethod("noteRadioMox", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rig, new object[] { on });

    private static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0) if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
    }

    private static List<string> PowerOrAntenna(IEnumerable<string> commands) => commands
        .Where(x => x.Contains("rfpower", StringComparison.OrdinalIgnoreCase) || x.Contains("txant", StringComparison.OrdinalIgnoreCase)
                    || x.Contains("max_power", StringComparison.OrdinalIgnoreCase))
        .ToList();

    [Fact]
    public async Task Loopback_teardown_with_hardware_PTT_held_writes_no_power_or_antenna_until_receive()
    {
        using var c = Arranged();
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        RunQueue(c);
        int before = c.F.Vendor.Transport.Commands.Count;

        // The software side has let go; a hardware line holds the radio keyed.
        Mox(c.F.Rig, true);
        var end = c.F.Rig.EndLoopbackArrangementAsync();
        var settle = DateTime.UtcNow.AddSeconds(1.5);
        while (DateTime.UtcNow < settle) { RunQueue(c); await Task.Delay(20); }
        Assert.Empty(PowerOrAntenna(c.F.Vendor.Transport.Commands.Skip(before)));
        Assert.False(end.IsCompleted, "the restore waits; it does not claim to have finished");

        // Positive control: the radio reports receive, and the same restore
        // now writes power and the antenna.
        Mox(c.F.Rig, false);
        await c.Pump(end);
        RunQueue(c);
        Assert.NotEmpty(PowerOrAntenna(c.F.Vendor.Transport.Commands.Skip(before)));
    }

    [Fact]
    public async Task A_Loopback_restore_the_radio_refuses_makes_the_owner_unconfirmed()
    {
        // Codex round 4, blocker 2: a restore that FAILED without throwing (a
        // refused mute, an unconfirmed full duplex, a changed connection, a
        // recorder stop not confirmed) returned normally, and the owner then
        // reported every change put back.
        using var c = Arranged();
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        RunQueue(c);
        var answer = c.F.Vendor.Transport.CommandWritten;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            // The radio refuses every mute restore.
            if (command.StartsWith("slice set ", StringComparison.Ordinal) && command.EndsWith("audio_mute=0", StringComparison.Ordinal))
                c.F.Vendor.Reply(c.F.Vendor.LastSequence, 0x50000016, "refused");
            else answer!(command);
        };
        string trouble = await c.Pump(c.F.Rig.EndLoopbackArrangementAsync());
        Assert.Contains("mute restoration was not confirmed", trouble);
        Assert.False(await c.F.Rig.CheckRestorations.CompletionAsync().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task A_power_restore_admitted_at_receive_is_refused_at_its_write_when_the_radio_keyed_in_between()
    {
        // Codex round 4, follow-up 3: the owner asked the radio before the
        // power entry, the entry's setter QUEUED the write, and the write then
        // went out unasked. A hardware line keying in that gap got the
        // operator's power while keyed. The queued write must ask again when
        // it runs, and the restore must wait for receive and go again.
        using var c = Arranged();
        Assert.True(await c.Pump(c.F.Rig.StartLoopbackArrangementAsync()));
        RunQueue(c);
        bool antennaBack = false, keyed = false;
        var whileKeyed = new List<string>();
        var answer = c.F.Vendor.Transport.CommandWritten;
        c.F.Vendor.Transport.CommandWritten = command =>
        {
            if (command.Contains("txant", StringComparison.OrdinalIgnoreCase)) antennaBack = true;
            if (keyed) lock (whileKeyed) whileKeyed.Add(command);
            answer!(command);
        };
        int before = c.F.Vendor.Transport.Commands.Count;
        var end = c.F.Rig.EndLoopbackArrangementAsync();
        var deadline = DateTime.UtcNow.AddSeconds(4);
        DateTime? keyedAt = null;
        while (DateTime.UtcNow < deadline && !(keyedAt is DateTime k && DateTime.UtcNow - k > TimeSpan.FromSeconds(1.5)))
        {
            while (c.F.Rig.q.Count > 0)
            {
                var next = c.F.Rig.q.Dequeue();
                // The antenna is back, so the owner has admitted the power entry
                // and its write is the next ordinary item in the queue: the
                // hardware line keys the radio now, after the owner asked.
                if (antennaBack && !keyed && next.Name == "unnamed")
                {
                    Mox(c.F.Rig, true);
                    keyed = true;
                    keyedAt = DateTime.UtcNow;
                }
                if (next.Item is FlexBase.FunctionDel a) a();
            }
            await Task.Delay(20);
        }
        Assert.True(keyed, "the interleave happened: the radio keyed between the owner's question and the queued write");
        lock (whileKeyed) Assert.Empty(PowerOrAntenna(whileKeyed));
        Assert.False(end.IsCompleted, "the power restore waits for receive; it does not claim to have finished");

        // Positive control: receive, and the same restore now writes the power.
        Mox(c.F.Rig, false);
        keyed = false;
        await c.Pump(end);
        RunQueue(c);
        Assert.Contains(c.F.Vendor.Transport.Commands.Skip(before), x => x.Contains("rfpower", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_restore_that_reports_false_is_not_done()
    {
        using var owner = new CheckRestorations(() => false);
        var entry = owner.Register("full duplex", () => Task.FromResult(false));
        Assert.False(await entry.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(await owner.CompletionAsync());
    }

    // ── The owner on its own ────────────────────────────────────────────

    private static async Task<bool> Within(Func<bool> condition, TimeSpan time)
    {
        var deadline = DateTime.UtcNow + time;
        while (DateTime.UtcNow < deadline) { if (condition()) return true; await Task.Delay(10); }
        return condition();
    }

    [Fact]
    public async Task A_held_restore_waits_for_its_release_and_then_for_receive()
    {
        bool transmitting = false;
        int restored = 0;
        using var owner = new CheckRestorations(() => transmitting);
        var entry = owner.Register("power", () => restored++, held: true);
        await Task.Delay(150);
        Assert.Equal(0, restored);                    // the check has not unkeyed yet
        transmitting = true;
        owner.Release(entry);
        await Task.Delay(150);
        Assert.Equal(0, restored);                    // released, but the radio is transmitting
        Assert.True(owner.WithheldByTransmit);
        transmitting = false;
        // No receive event at all: the slow poll alone must find it.
        Assert.True(await Within(() => restored == 1, TimeSpan.FromSeconds(3)));
        Assert.True(await owner.CompletionAsync());
        Assert.False(owner.Pending);
    }

    [Fact]
    public async Task The_radio_is_asked_again_before_every_restore()
    {
        // A rekey between two restores (a hardware line pressed after the first
        // write) holds the second: each write is checked at its own run, not
        // trusted from the receive that started the first.
        bool transmitting = false;
        var ran = new List<string>();
        using var owner = new CheckRestorations(() => transmitting);
        owner.Register("dummy load", () => { ran.Add("dummy load"); transmitting = true; });
        owner.Register("power", () => ran.Add("power"));
        await Task.Delay(300);
        Assert.Equal(new[] { "dummy load" }, ran);
        transmitting = false;
        Assert.True(await Within(() => ran.Count == 2, TimeSpan.FromSeconds(3)));
        Assert.Equal(new[] { "dummy load", "power" }, ran);
    }

    [Fact]
    public async Task A_restore_that_throws_makes_completion_false_not_true()
    {
        using var owner = new CheckRestorations(() => false);
        owner.Register("full duplex", (Action)(() => throw new InvalidOperationException("no reply")));
        owner.Register("monitor", () => { });
        Assert.False(await owner.CompletionAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        owner.ClearFinished();
        Assert.True(await owner.CompletionAsync());   // once reported, it is forgotten
    }

    [Fact]
    public async Task A_check_that_ended_and_a_window_that_closed_are_still_waited_for_at_disconnect()
    {
        // Nothing holds a reference to the session or the window: only the
        // radio's owner knows a change is outstanding, and that is enough.
        using var c = new RadioCase();
        int restored = 0;
        Field(c.F.Rig, "_Transmit", true);
        c.F.Rig.CheckRestorations.Register("audio check: power", () => restored++, held: true);

        c.F.Rig.BeginCheckTeardown();
        Assert.True(c.F.Rig.TeardownInProgress);
        var settle = c.F.Rig.SettleChecksForTeardownAsync(TimeSpan.FromSeconds(5));
        // A second teardown joins the first.
        Assert.Same(settle, c.F.Rig.SettleChecksForTeardownAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(300);
        Assert.False(settle.IsCompleted);
        Assert.Equal(0, restored);                    // withheld while transmitting

        Mox(c.F.Rig, false);                          // the radio reports receive
        var result = await c.Pump(settle);
        Assert.True(result.Completed, result.Stage);
        Assert.Equal(1, restored);
        Assert.True(c.F.Rig.TeardownInProgress, "admission stays shut until the caller says it is done");
        c.F.Rig.EndCheckTeardown();
        Assert.False(c.F.Rig.TeardownInProgress);
    }

    [Fact]
    public async Task A_teardown_on_a_radio_that_keeps_transmitting_withholds_and_says_so()
    {
        using var c = new RadioCase();
        int restored = 0;
        Field(c.F.Rig, "_Transmit", true);
        c.F.Rig.CheckRestorations.Register("audio check: dummy load", () => restored++, held: true);
        c.F.Rig.BeginCheckTeardown();
        var result = await c.Pump(c.F.Rig.SettleChecksForTeardownAsync(TimeSpan.FromMilliseconds(600)));
        c.F.Rig.EndCheckTeardown();
        Assert.Equal(new AudioCheckTeardownResult(false, "still-transmitting"), result);
        Assert.Equal(0, restored);
    }
}
