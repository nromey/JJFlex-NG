using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Radios.Listenback;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// While a teardown is putting an Audio Check back, nothing app-initiated keys
/// the radio or starts a resource, whatever door it comes through (#835, fix
/// round 4, Codex round-4 blocker 1).
/// </summary>
/// <remarks>
/// <para>
/// Round 3 refused at the PTT controller's two key methods. Voice messages
/// (which set Transmit directly, or start the file for VOX), tune, CW and the
/// Workshop's IQ bench starts never pass through them, and keyed or changed
/// the radio during the teardown's pumped wait. The refusal now sits at the
/// chokepoints in FlexBase that every such route ends at, one test per route,
/// each with a positive control once the teardown has finished.
/// </para>
/// <para>
/// Runs the real FlexBase against the listen-back fake radio and reads the
/// commands that actually go out. Unkey routes stay open, also tested.
/// </para>
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class CheckTeardownAdmissionTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(CheckTeardownAdmissionTests));
    public void Dispose() => _scope.Dispose();

    private static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0)
        {
            var item = c.F.Rig.q.Dequeue().Item;
            if (item is FlexBase.FunctionDel a) a();
        }
    }

    private static int Sent(RadioCase c, string fragment) =>
        c.F.Vendor.Transport.Commands.Count(x => x.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Transmit_true_is_refused_during_a_teardown_and_unkey_is_not()
    {
        using var c = new RadioCase();
        c.F.Rig.BeginCheckTeardown();
        c.F.Rig.Transmit = true;
        RunQueue(c);
        Assert.Equal(0, Sent(c, "xmit 1"));
        // Unkey is never refused: its write is queued (FlexLib then sends
        // nothing for a radio already receiving, so the queue is what shows it).
        Assert.Equal(0, c.F.Rig.q.Count);
        c.F.Rig.Transmit = false;
        Assert.Equal(1, c.F.Rig.q.Count);
        RunQueue(c);
        c.F.Rig.EndCheckTeardown();
        c.F.Rig.Transmit = true;
        RunQueue(c);
        Assert.Equal(1, Sent(c, "xmit 1"));
    }

    [Fact]
    public void A_key_queued_before_the_teardown_does_not_go_out_during_it()
    {
        using var c = new RadioCase();
        c.F.Rig.Transmit = true;              // queued, not yet run
        c.F.Rig.BeginCheckTeardown();
        RunQueue(c);                          // runs during the teardown
        Assert.Equal(0, Sent(c, "xmit 1"));
        c.F.Rig.EndCheckTeardown();
    }

    [Fact]
    public void Tune_is_refused_during_a_teardown()
    {
        using var c = new RadioCase();
        int before = Sent(c, "tune");
        c.F.Rig.BeginCheckTeardown();
        c.F.Rig.TxTune = true;
        RunQueue(c);
        Assert.Equal(before, Sent(c, "tune"));
        c.F.Rig.TxTune = false;               // turning tune off is never refused
        RunQueue(c);
        c.F.Rig.EndCheckTeardown();
        int afterOff = Sent(c, "tune");
        c.F.Rig.TxTune = true;
        RunQueue(c);
        Assert.True(Sent(c, "tune") > afterOff);
    }

    [Fact]
    public void CW_is_refused_during_a_teardown()
    {
        using var c = new RadioCase();
        c.F.Rig.BeginCheckTeardown();
        Assert.False(c.F.Rig.SendCW("TEST"));
        Assert.False(c.F.Rig.SendCW('E'));
        c.F.Rig.EndCheckTeardown();
        Assert.True(c.F.Rig.SendCW("TEST"));
    }

    [Fact]
    public void A_recording_is_not_started_toward_the_transmitter_during_a_teardown()
    {
        using var c = new RadioCase();
        c.F.Rig.TxFilePlayer.Load(new float[48000], 48000, "message");
        c.F.Rig.BeginCheckTeardown();
        c.F.Rig.TxFileStart();
        Assert.False(c.F.Rig.TxFilePlaying);
        c.F.Rig.TxFileStartForVox();
        Assert.False(c.F.Rig.TxFilePlaying);
        c.F.Rig.EndCheckTeardown();
        c.F.Rig.TxFileStart();
        Assert.True(c.F.Rig.TxFilePlaying);
        c.F.Rig.TxFileStop();
    }

    [Fact]
    public async Task An_IQ_take_is_not_started_during_a_teardown()
    {
        using var c = new RadioCase();
        c.F.Rig.BeginCheckTeardown();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.F.Rig.StartListenbackAsync(new ListenbackOptions(), default));
        Assert.Equal("teardown-in-progress", refused.Message);
        c.F.Rig.EndCheckTeardown();
    }
}
