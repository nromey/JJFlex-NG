using System;
using System.Linq;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// #773, the frequency half: a tracking notch can be moved (ruled 2026-10-19,
/// fine and coarse steps like tuning, and a typed frequency).
/// </summary>
/// <remarks>
/// FlexLib's <c>TNF.Frequency</c> setter sets its own value before it sends,
/// so reading the notch back proves nothing. These read the command that went
/// out, on the vendor radio fixture, after the rig's queue has run.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class TrackingNotchMoveTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(TrackingNotchMoveTests));
    public void Dispose() => _scope.Dispose();

    private static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0)
            if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
    }

    private static string[] Moves(RadioCase c) =>
        c.F.Vendor.Transport.Commands.Where(x => x.Contains("tnf set 1 freq=", StringComparison.Ordinal)).ToArray();

    private static RadioCase OneNotch()
    {
        var c = new RadioCase();
        c.F.Vendor.Status("tnf 1 freq=14.200000");
        Assert.Equal(1, c.F.Rig.TNFCount());
        Assert.Equal(14_200_000UL, c.F.Rig.TNFFrequencyHz(0));
        return c;
    }

    [Fact]
    public void A_move_sends_the_new_frequency_to_the_radio()
    {
        using var c = OneNotch();
        Assert.Equal(14_200_100UL, c.F.Rig.SetTNFFrequencyHz(0, 14_200_100));
        RunQueue(c);
        Assert.Equal(new[] { "tnf set 1 freq=14.200100" }, Moves(c).Select(m => m.Substring(m.IndexOf("tnf", StringComparison.Ordinal))));
    }

    [Fact]
    public void The_same_frequency_again_sends_nothing()
    {
        using var c = OneNotch();
        c.F.Rig.SetTNFFrequencyHz(0, 14_200_100);
        RunQueue(c);
        Assert.Single(Moves(c));   // positive control: the first move went out

        c.F.Rig.SetTNFFrequencyHz(0, 14_200_100);
        RunQueue(c);
        Assert.Single(Moves(c));
    }

    [Theory]
    [InlineData(10UL, FlexBase.TNFFrequencyMinHz, "freq=0.030000")]
    [InlineData(146_520_000UL, FlexBase.TNFFrequencyMaxHz, "freq=54.000000")]
    public void A_frequency_outside_the_receive_coverage_is_clamped(ulong asked, ulong expected, string sent)
    {
        using var c = OneNotch();
        Assert.Equal(expected, c.F.Rig.SetTNFFrequencyHz(0, asked));
        RunQueue(c);
        Assert.Contains(Moves(c), m => m.EndsWith(sent, StringComparison.Ordinal));
    }

    [Fact]
    public void A_notch_that_is_gone_is_not_moved()
    {
        using var c = OneNotch();
        Assert.Equal(0UL, c.F.Rig.SetTNFFrequencyHz(1, 14_200_100));
        RunQueue(c);
        Assert.Empty(Moves(c));
    }
}
