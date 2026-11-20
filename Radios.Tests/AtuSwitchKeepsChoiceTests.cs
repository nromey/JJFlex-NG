using System;
using System.Linq;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// The ATU switch keeps the operator's choice (#1188, ruled by Noel
/// 2026-11-19, J8): a tune that ends in bypass, and a remembered bypass result
/// at connect, no longer turn automatic off. Off means only that the operator
/// bypassed the tuner, which the radio reports as a manual bypass.
/// </summary>
/// <remarks>
/// Runs the real FlexBase on the vendor radio fixture, hearing the radio's
/// property changes as <see cref="ManualTuneSettleTests"/> does, and drives it
/// with the radio's own status lines. A FAILED tune, where Jim's code bypassed
/// the tuner itself and turned automatic off, followed in batch 17 (ruled by
/// Noel 2026-11-19 20:35, J1): it too leaves the switch On, and no bypass
/// command goes to the radio (the orchestrator's decision, matching SmartSDR).
/// This class pinned the old Fail behaviour as correct until then. Not built:
/// a manual bypass that arrives after the ATU is enabled has no case in the
/// tune status switch, so in that order the type stays automatic, as before
/// (a separate finding, #1220's; TuneTypeFlipTests pins it).
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class AtuSwitchKeepsChoiceTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(AtuSwitchKeepsChoiceTests));
    public void Dispose() => _scope.Dispose();

    private static RadioCase Hearing()
    {
        var c = new RadioCase();
        var radio = c.F.Vendor.Radio;
        var binding = new StationConnect.ObservationBindingFor(radio).Binding;
        var handler = typeof(FlexBase).GetMethod("radioPropertyChangedHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
        radio.PropertyChanged += (s, e) => handler.Invoke(c.F.Rig, new object[] { s!, e, binding });
        return c;
    }

    private static bool SwitchOn(RadioCase c) => FlexBase.AtuSwitchReadsOn(c.F.Rig.FlexTunerType);

    [Fact]
    public void A_tune_that_ends_in_bypass_leaves_the_switch_on()
    {
        using var c = Hearing();
        c.F.Vendor.Status("atu atu_enabled=1");
        Assert.Equal(FlexBase.FlexTunerTypes.auto, c.F.Rig.FlexTunerType);   // positive control: the ATU is enabled and automatic

        c.F.Vendor.Status("atu status=TUNE_IN_PROGRESS");
        c.F.Vendor.Status("atu status=TUNE_BYPASS");

        Assert.Equal(FlexBase.FlexTunerTypes.auto, c.F.Rig.FlexTunerType);
        Assert.True(SwitchOn(c));
    }

    [Fact]
    public void A_remembered_bypass_result_at_connect_leaves_the_switch_on_when_the_ATU_is_enabled_first()
    {
        using var c = Hearing();
        c.F.Vendor.Status("atu atu_enabled=1");
        c.F.Vendor.Status("atu status=TUNE_BYPASS");   // the first tune status heard
        Assert.True(SwitchOn(c));
    }

    [Fact]
    public void A_remembered_bypass_result_at_connect_leaves_the_switch_on_when_the_status_comes_first()
    {
        using var c = Hearing();
        c.F.Vendor.Status("atu status=TUNE_BYPASS");
        c.F.Vendor.Status("atu atu_enabled=1");
        Assert.True(SwitchOn(c));
    }

    /// <summary>Positive control: the operator's own bypass, remembered by the radio, still reads Off.</summary>
    [Fact]
    public void A_remembered_manual_bypass_at_connect_reads_off()
    {
        using var c = Hearing();
        c.F.Vendor.Status("atu status=TUNE_MANUAL_BYPASS");
        c.F.Vendor.Status("atu atu_enabled=1");
        Assert.NotEqual(FlexBase.FlexTunerTypes.auto, c.F.Rig.FlexTunerType);
        Assert.False(SwitchOn(c));
    }

    private static int Sent(RadioCase c, string fragment) =>
        c.F.Vendor.Transport.Commands.Count(x => x.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A failed automatic tune leaves the switch On, clears the latch, and
    /// sends the radio no bypass. Until batch 17 this test was
    /// <c>A_failed_tune_still_turns_automatic_off</c> and pinned the opposite.
    /// </summary>
    [Fact]
    public void A_failed_tune_leaves_the_switch_on_and_sends_no_bypass()
    {
        using var c = Hearing();
        c.F.Vendor.Status("atu atu_enabled=1");
        Assert.True(SwitchOn(c));   // positive control: enabled and automatic
        c.F.Vendor.Status("atu status=TUNE_IN_PROGRESS");
        c.F.Vendor.Status("atu status=TUNE_FAIL");

        Assert.Equal(FlexBase.FlexTunerTypes.auto, c.F.Rig.FlexTunerType);
        Assert.True(SwitchOn(c));
        Assert.False(c.F.Rig.FlexTunerOn, "a failed tune still ends the tune");
        Assert.Equal(0, Sent(c, "atu bypass"));
    }

    /// <summary>
    /// The next connect, after a failure: a new rig hears the radio's
    /// remembered TUNE_FAIL, in either order with the ATU enabled, and the
    /// switch reads On.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_remembered_failed_tune_at_connect_reads_on(bool statusFirst)
    {
        using var c = Hearing();
        if (statusFirst)
        {
            c.F.Vendor.Status("atu status=TUNE_FAIL");
            c.F.Vendor.Status("atu atu_enabled=1");
        }
        else
        {
            c.F.Vendor.Status("atu atu_enabled=1");
            c.F.Vendor.Status("atu status=TUNE_FAIL");
        }
        Assert.Equal(FlexBase.FlexTunerTypes.auto, c.F.Rig.FlexTunerType);
        Assert.True(SwitchOn(c));
        Assert.Equal(0, Sent(c, "atu bypass"));
    }
}
