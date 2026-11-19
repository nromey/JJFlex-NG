using System;
using System.Linq;
using System.Reflection;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// Batch 14 repair 11 (Codex's B1 on db03106b): in automatic type a start
/// queues our key and the radio's "atu start"; a stop accepted before the
/// command worker ran them cleared the latch and queued our unkey, but the
/// old start still went out after it. Now a stop revokes the start's writes
/// not yet sent. The real q_t and the vendor transport, drained item by item
/// through RunQueuedItem; the doors (the Tune button, Ctrl+T, the menu's
/// Start tune) all start and stop through these two latch writes, which
/// JJFlexWpf.Tests' TuneKeyCarrierTests shows per door.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class AtuStartRevokeTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(AtuStartRevokeTests));
    public void Dispose() => _scope.Dispose();

    private static RadioCase Automatic()
    {
        var c = new RadioCase();
        typeof(FlexBase).GetField("_FlexTunerType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, FlexBase.FlexTunerTypes.auto);
        return c;
    }

    private static void RunOne(RadioCase c) => c.F.Rig.RunQueuedItem(c.F.Rig.q.Dequeue());

    private static void Drain(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0) RunOne(c);
    }

    private static int Sent(RadioCase c, string fragment) =>
        c.F.Vendor.Transport.Commands.Count(x => x.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>The last index of a command, or -1.</summary>
    private static int LastAt(RadioCase c, string fragment) =>
        c.F.Vendor.Transport.Commands.ToList().FindLastIndex(x => x.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void A_start_with_no_stop_sends_the_key_and_atu_start()
    {
        using var c = Automatic();
        c.F.Rig.FlexTunerOn = true;
        Assert.True(c.F.Rig.q.Count >= 2, "the start queued its writes");
        Drain(c);
        Assert.True(Sent(c, "atu start") >= 1, "positive control: the start sends atu start");
        Assert.True(Sent(c, "xmit 1") >= 1, "positive control: and our key");
    }

    [Fact]
    public void A_stop_before_the_worker_ran_sends_no_atu_start_and_no_key()
    {
        using var c = Automatic();
        c.F.Rig.FlexTunerOn = true;
        c.F.Rig.FlexTunerOn = false;   // the accepted stop, before the drain
        Drain(c);
        Assert.Equal(0, Sent(c, "atu start"));
        Assert.Equal(0, Sent(c, "xmit 1"));
    }

    [Fact]
    public void A_stop_between_the_two_queued_writes_sends_no_atu_start_after_it()
    {
        using var c = Automatic();
        c.F.Rig.FlexTunerOn = true;
        int queued = c.F.Rig.q.Count;
        RunOne(c);                     // the start's first write: our key-on is now queued behind "atu start"
        Assert.True(c.F.Rig.q.Count >= queued, "positive control: the first write queued our key-on");
        c.F.Rig.FlexTunerOn = false;   // the stop, between the two
        Drain(c);
        Assert.Equal(0, Sent(c, "atu start"));
        Assert.Equal(0, Sent(c, "xmit 1"));
    }

    [Fact]
    public void A_start_after_a_revoked_one_goes_out()
    {
        using var c = Automatic();
        c.F.Rig.FlexTunerOn = true;
        c.F.Rig.FlexTunerOn = false;
        Drain(c);
        c.F.Rig.FlexTunerOn = true;    // a new, deliberate start
        Drain(c);
        Assert.Equal(1, Sent(c, "atu start"));
    }
}
