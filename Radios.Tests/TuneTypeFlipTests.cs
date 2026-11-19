using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// #1141, as rescoped by batch 14's pick: a tune carrier keyed in manual type
/// in the first moment after connecting, then the radio's ATU reports arrive.
/// In one of the two orders they can come in, the type turns automatic under
/// the live carrier, so the next tune press must not be read by tuner type.
/// </summary>
/// <remarks>
/// The FlexBase half, on the vendor radio fixture with the status lines a
/// radio sends: what the type becomes in each order, and that the carrier is
/// still ours to stop either way. The press half (MainWindow.StartATUTuneCycle
/// stops our carrier first, whatever the type) is in JJFlexWpf.Tests'
/// TuneKeyCarrierTests, and its order is pinned from source here.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class TuneTypeFlipTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(TuneTypeFlipTests));
    public void Dispose() => _scope.Dispose();

    private static void Wired(RadioCase c)
    {
        var radio = c.F.Vendor.Radio;
        var binding = new StationConnect.ObservationBindingFor(radio).Binding;
        var handler = typeof(FlexBase).GetMethod("radioPropertyChangedHandler", BindingFlags.Instance | BindingFlags.NonPublic)!;
        radio.PropertyChanged += (s, e) => handler.Invoke(c.F.Rig, new object[] { s!, e, binding });
    }

    private static RadioCase CarrierKeyedInManual()
    {
        var c = new RadioCase();
        Wired(c);
        Assert.Equal(FlexBase.FlexTunerTypes.manual, c.F.Rig.FlexTunerType);
        c.F.Rig.TxTune = true;   // what a manual-type tune press asks for
        Assert.True(c.F.Rig.TuneAskedOrUp, "positive control: the carrier is asked for");
        Assert.False(c.F.Rig.FlexTunerOn, "a tune carrier sets no tuner latch");
        return c;
    }

    [Fact]
    public void Enabled_first_turns_the_type_automatic_under_the_carrier_and_the_carrier_is_still_ours()
    {
        using var c = CarrierKeyedInManual();
        c.F.Vendor.Status("atu atu_enabled=1");
        Assert.Equal(FlexBase.FlexTunerTypes.auto, c.F.Rig.FlexTunerType);
        c.F.Vendor.Status("atu status=TUNE_MANUAL_BYPASS");

        // The flip that makes a type-based press start an ATU cycle on top of the carrier.
        Assert.Equal(TunePressOutcome.AtuTune,
            TunePress.For(c.F.Rig.MyCaps.HasCap(RigCaps.Caps.ATGet), c.F.Rig.FlexTunerType));
        Assert.True(c.F.Rig.TuneAskedOrUp, "the carrier is still ours, so the press must stop it");
        Assert.False(c.F.Rig.FlexTunerOn);
    }

    [Fact]
    public void Bypass_first_keeps_the_type_manual_and_the_carrier_is_still_ours()
    {
        using var c = CarrierKeyedInManual();
        c.F.Vendor.Status("atu status=TUNE_MANUAL_BYPASS");
        c.F.Vendor.Status("atu atu_enabled=1");

        Assert.Equal(FlexBase.FlexTunerTypes.manual, c.F.Rig.FlexTunerType);
        Assert.Equal(TunePressOutcome.TuneCarrier,
            TunePress.For(c.F.Rig.MyCaps.HasCap(RigCaps.Caps.ATGet), c.F.Rig.FlexTunerType));
        Assert.True(c.F.Rig.TuneAskedOrUp);
        Assert.False(c.F.Rig.FlexTunerOn);
    }

    [Fact]
    public void The_press_stops_our_carrier_before_it_reads_the_tuner_type()
    {
        string main = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "MainWindow.xaml.cs")).Replace("\r\n", "\n");
        int at = main.IndexOf("public void StartATUTuneCycle()", StringComparison.Ordinal);
        string body = main.Substring(at, main.IndexOf("\n    }\n", at, StringComparison.Ordinal) - at);
        int ours = body.IndexOf("if (RigControl.TuneAskedOrUp)", StringComparison.Ordinal);
        int type = body.IndexOf("Radios.TunePress.For(", StringComparison.Ordinal);
        int noTuner = body.IndexOf("if (outcome == Radios.TunePressOutcome.NoTuner)", StringComparison.Ordinal);
        Assert.True(ours > 0 && ours < type && type < noTuner, "our carrier's stop comes before the type and every refusal");
        string branch = body.Substring(ours, type - ours);
        Assert.Contains("TuneKeyCarrierPress();", branch);
        Assert.Contains("return;", branch);
        Assert.DoesNotContain("FlexTunerOn", branch);
    }
}
