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
/// with the radio's own status lines. Built narrowly: a FAILED tune, where
/// Jim's code bypasses the tuner itself and turns automatic off, is left as it
/// was and pinned so here, pending its own question. Not built either: a
/// manual bypass that arrives after the ATU is enabled has no case in the tune
/// status switch, so in that order the type stays automatic, as before this
/// change (a separate finding).
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

    /// <summary>Left as it was on purpose: a failed tune still bypasses and turns automatic off.</summary>
    [Fact]
    public void A_failed_tune_still_turns_automatic_off()
    {
        using var c = Hearing();
        c.F.Vendor.Status("atu atu_enabled=1");
        Assert.True(SwitchOn(c));
        c.F.Vendor.Status("atu status=TUNE_IN_PROGRESS");
        c.F.Vendor.Status("atu status=TUNE_FAIL");
        Assert.False(SwitchOn(c));
    }
}
