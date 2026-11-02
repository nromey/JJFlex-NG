using System;
using System.Linq;
using Radios.Tests.StationConnect;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// A tune write's life in the command queue (2026-11-02). Codex showed two ways
/// Check your audio's Escape closed ahead of a carrier: the request's 1.5 s
/// answer window expired while the ON still sat in the queue, and an OFF
/// cleared the request at once while the ON ahead of it still ran. These drive
/// the REAL setter and the REAL queue, draining it by hand where the gap is.
/// </summary>
/// <remarks>
/// What these cannot see: a radio. The vendor fixture records the command
/// FlexLib writes; whether the radio keys on it is the bench's question.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class TuneWriteLifetimeTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(TuneWriteLifetimeTests));
    public void Dispose() => _scope.Dispose();

    private static void Drain(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0)
            if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel action) action();
    }

    private static int Sent(RadioCase c, string command) =>
        c.F.Vendor.Transport.Commands.Count(x => x == command);

    [Fact]
    public void A_queued_on_counts_for_as_long_as_it_sits_in_the_queue()
    {
        using var c = new RadioCase();
        c.F.Rig.TuneFactClock = () => 0;
        c.F.Rig.TxTune = true;
        Assert.False(c.F.Rig.TxTune);              // the getter has not moved: nothing has run
        Assert.True(c.F.Rig.TuneAskedOrUp);
        c.F.Rig.TuneFactClock = () => 60_000;      // well past the answer window
        Assert.True(c.F.Rig.TuneAskedOrUp);        // blocker 1: no timeout stands in for the queue
        Drain(c);
        Assert.Equal(1, Sent(c, "transmit tune 1"));
        Assert.True(c.F.Rig.TuneAskedOrUp);        // now FlexLib's own flag
    }

    [Fact]
    public void An_off_revokes_an_on_that_has_not_run_and_it_is_never_sent()
    {
        // Blocker 2: Escape, then a prompt second Escape, with the ON still queued.
        using var c = new RadioCase();
        c.F.Rig.TxTune = true;
        c.F.Rig.TxTune = false;
        Assert.False(c.F.Rig.TuneAskedOrUp);       // the ON can no longer run, so the window may close
        Drain(c);
        Assert.Equal(0, Sent(c, "transmit tune 1"));
        Assert.False(c.F.Rig.TxTune);
        Assert.False(c.F.Rig.TuneAskedOrUp);
    }

    [Fact]
    public void An_off_behind_an_on_that_already_ran_keeps_counting_until_it_runs()
    {
        using var c = new RadioCase();
        c.F.Rig.TxTune = true;
        Drain(c);
        Assert.Equal(1, Sent(c, "transmit tune 1"));
        c.F.Rig.TxTune = false;
        Assert.True(c.F.Rig.TuneAskedOrUp);        // the carrier is up until the off is sent
        Drain(c);
        Assert.Equal(1, Sent(c, "transmit tune 0"));
        Assert.False(c.F.Rig.TuneAskedOrUp);
    }

    [Fact]
    public void Only_the_latest_on_runs_and_an_off_always_does()
    {
        // The positive control: revocation drops a superseded ON, not every ON.
        using var c = new RadioCase();
        c.F.Rig.TxTune = true;
        c.F.Rig.TxTune = false;
        c.F.Rig.TxTune = true;
        Assert.True(c.F.Rig.TuneAskedOrUp);
        Drain(c);
        Assert.Equal(1, Sent(c, "transmit tune 1"));
        Assert.True(c.F.Rig.TxTune);
    }
}
