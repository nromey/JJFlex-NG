using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// #1163, narrowed: the manual tuner's OFF waits for the SWR to stop falling,
/// sleeping 100 ms at a time, and it used to do that on its caller's thread.
/// The caller can be FlexLib's read thread: an ATU status that ends the tune
/// clears a latch set in manual type. The settle now runs off that thread, and
/// the carrier still comes down after it.
/// </summary>
/// <remarks>
/// The branch stays: the ON half is driven by <c>TunePressTests</c>,
/// <c>CallsignGateTests</c>, <c>CheckTeardownAdmissionTests</c> and
/// <c>AtuTuneEndTests</c>. Runs the real FlexBase on the vendor radio fixture
/// and hands the settle a runner that only collects it, so the test can see
/// what the caller's thread did and did not do.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ManualTuneSettleTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ManualTuneSettleTests));
    public void Dispose() => _scope.Dispose();

    private static void RunQueue(RadioCase c)
    {
        while (c.F.Rig.q.Count > 0)
            if (c.F.Rig.q.Dequeue().Item is FlexBase.FunctionDel a) a();
    }

    private static int Sent(RadioCase c, string fragment) =>
        c.F.Vendor.Transport.Commands.Count(x => x.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Manual type, the rig hearing the radio's property changes, and a
    /// settle runner that only collects the work.</summary>
    private static List<Action> ManualWithCollectedSettles(RadioCase c)
    {
        typeof(FlexBase).GetField("_FlexTunerType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, FlexBase.FlexTunerTypes.manual);
        var radio = c.F.Vendor.Radio;
        var binding = new StationConnect.ObservationBindingFor(radio).Binding;
        var handler = typeof(FlexBase).GetMethod("radioPropertyChangedHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
        radio.PropertyChanged += (s, e) => handler.Invoke(c.F.Rig, new object[] { s!, e, binding });
        var settles = new List<Action>();
        c.F.Rig.ManualSettleRunner = settles.Add;
        return settles;
    }

    private static void TuneCarrierUp(RadioCase c)
    {
        c.F.Rig.FlexTunerOn = true;   // the manual tune press
        RunQueue(c);
        Assert.True(Sent(c, "transmit tune 1") > 0, "positive control: the press keyed the tune carrier");
        Assert.True(c.F.Rig.FlexTunerOn);
    }

    [Fact]
    public void An_ATU_status_that_ends_a_manual_latch_does_not_settle_on_the_radio_s_read_thread()
    {
        using var c = new RadioCase();
        var settles = ManualWithCollectedSettles(c);
        TuneCarrierUp(c);
        int offs = Sent(c, "transmit tune 0");

        // FlexLib's read thread delivers this; here it is the test's thread.
        c.F.Vendor.Status("atu status=TUNE_SUCCESSFUL");
        RunQueue(c);

        Assert.False(c.F.Rig.FlexTunerOn, "the latch clears at once");
        Assert.Single(settles);   // handed off, not run here
        Assert.Equal(offs, Sent(c, "transmit tune 0"));   // the carrier stays up while the SWR settles

        FlexBase.FlexAntTunerArg? reported = null;
        c.F.Rig.FlexAntTunerStartStop += e => reported = e;
        settles[0]();   // the settle, run where the runner would
        RunQueue(c);

        Assert.Equal(offs + 1, Sent(c, "transmit tune 0"));   // and then it comes down
        Assert.NotNull(reported);
        Assert.Equal(nameof(ATUTuneStatus.OK), reported!.Status);
    }

    [Fact]
    public void A_manual_stop_returns_without_sleeping_and_the_carrier_still_comes_down_after_the_settle()
    {
        using var c = new RadioCase();
        var settles = ManualWithCollectedSettles(c);
        TuneCarrierUp(c);
        int offs = Sent(c, "transmit tune 0");

        c.F.Rig.FlexTunerOn = false;   // a stop on the UI thread
        RunQueue(c);

        Assert.Single(settles);
        Assert.Equal(offs, Sent(c, "transmit tune 0"));
        settles[0]();
        RunQueue(c);
        Assert.Equal(offs + 1, Sent(c, "transmit tune 0"));
    }

    [Fact]
    public void A_settle_whose_report_throws_still_drops_the_carrier()
    {
        using var c = new RadioCase();
        var settles = ManualWithCollectedSettles(c);
        TuneCarrierUp(c);
        int offs = Sent(c, "transmit tune 0");
        c.F.Rig.FlexAntTunerStartStop += _ => throw new InvalidOperationException("a subscriber failed");

        c.F.Rig.FlexTunerOn = false;
        settles.Single()();
        RunQueue(c);

        Assert.Equal(offs + 1, Sent(c, "transmit tune 0"));
    }

    [Fact]
    public void The_automatic_type_s_stop_is_unchanged_and_settles_nothing()
    {
        using var c = new RadioCase();
        var settles = ManualWithCollectedSettles(c);
        typeof(FlexBase).GetField("_FlexTunerType", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(c.F.Rig, FlexBase.FlexTunerTypes.auto);
        c.F.Rig.FlexTunerOn = true;
        RunQueue(c);
        c.F.Rig.FlexTunerOn = false;
        RunQueue(c);
        Assert.Empty(settles);
    }
}
