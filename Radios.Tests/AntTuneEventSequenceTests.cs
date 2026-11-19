using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// Batch 14 repair round 3: what the antenna-tuner event carries, raised from
/// the radio's own status lines, as the 8600 bench traced them at
/// <c>5f7fc030</c> (steps 2c and 2d). The window's rule for the Ant Tune
/// button (<c>MainWindow.AntTuneButtonTextFor</c>) is tested in JJFlexWpf.Tests;
/// this side shows the two inputs it reads are true when the event is raised.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class AntTuneEventSequenceTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(AntTuneEventSequenceTests));
    public void Dispose() => _scope.Dispose();

    private static List<(string? Type, string Status, bool AtuTuning, FlexBase.FlexTunerTypes TypeAfter)> Wire(RadioCase c, FlexBase.FlexTunerTypes type)
    {
        typeof(FlexBase).GetField("_FlexTunerType", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(c.F.Rig, type);
        var radio = c.F.Vendor.Radio;
        var binding = new StationConnect.ObservationBindingFor(radio).Binding;
        var handler = typeof(FlexBase).GetMethod("radioPropertyChangedHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
        radio.PropertyChanged += (s, e) => handler.Invoke(c.F.Rig, new object[] { s!, e, binding });
        var seen = new List<(string?, string, bool, FlexBase.FlexTunerTypes)>();
        c.F.Rig.FlexAntTunerStartStop += a => seen.Add((a.Type, a.Status, c.F.Rig.ATUTuneInProgress, c.F.Rig.FlexTunerType));
        return seen;
    }

    /// <summary>
    /// Step 2d: an automatic cycle ends Bypass. The event says "auto",
    /// "Bypass", raised before the type is switched to manual, so a handler
    /// that reads the type later sees manual: the reason it must use the
    /// event's own type.
    /// </summary>
    [Fact]
    public void An_automatic_cycle_ending_Bypass_is_raised_as_automatic_before_the_type_turns_manual()
    {
        using var c = new RadioCase();
        var seen = Wire(c, FlexBase.FlexTunerTypes.auto);
        c.F.Vendor.Status("atu status=TUNE_IN_PROGRESS");
        c.F.Vendor.Status("atu status=TUNE_BYPASS");

        Assert.Contains(seen, x => x.Status == "InProgress" && x.Type == "auto" && x.AtuTuning);
        var end = seen.FindLast(x => x.Status == "Bypass");
        Assert.Equal("auto", end.Type);
        Assert.False(end.AtuTuning);
        Assert.Equal(FlexBase.FlexTunerTypes.manual, c.F.Rig.FlexTunerType);   // switched after the raise
    }

    /// <summary>
    /// Step 2c: in automatic type a plain carrier rises while the radio's ATU
    /// reads TUNE_MANUAL_BYPASS. FlexBase raises "InProgress" for it, and the
    /// radio's ATU is NOT tuning when it does: the second input the window's
    /// rule reads.
    /// </summary>
    [Fact]
    public void A_plain_carrier_in_automatic_type_raises_InProgress_with_the_ATU_not_tuning()
    {
        using var c = new RadioCase();
        var seen = Wire(c, FlexBase.FlexTunerTypes.auto);
        c.F.Vendor.Status("atu status=TUNE_MANUAL_BYPASS");
        seen.Clear();
        c.F.Vendor.Status("transmit tune=1");

        Assert.Contains(seen, x => x.Status == "InProgress");
        Assert.All(seen, x => Assert.False(x.AtuTuning, "the radio's ATU is not tuning under a plain carrier"));
    }
}
