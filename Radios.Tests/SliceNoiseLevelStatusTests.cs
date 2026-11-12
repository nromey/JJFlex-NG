using System;
using System.Collections.Generic;
using Flex.Smoothlake.FlexLib;
using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Sprint 49 Track E follow-up, item A (#1012, ruled S5 and S12): the carried
/// FlexLib patch in <c>Slice.StatusUpdate</c> (MIGRATION.md item 21). The radio
/// reports Legacy NR's, Legacy Auto-Notch's and Spectral NR's levels as
/// <c>nrl_level</c>, <c>anfl_level</c> and <c>nrs_level</c>; stock FlexLib
/// dropped all three, so the app spoke a held intent ("Legacy NR, level 1")
/// while the radio ran 100. A real vendored Radio and Slice, the real status
/// parse, a fake transport that records every command: a status line must
/// update the level, raise its property, send NOTHING back (the public
/// setters send <c>slice set</c>), and be what FlexBase reads.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class SliceNoiseLevelStatusTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(SliceNoiseLevelStatusTests));
    public void Dispose() => _scope.Dispose();

    [Theory]
    [InlineData("nrl_level", nameof(Slice.NRL_Level))]
    [InlineData("anfl_level", nameof(Slice.ANFL_Level))]
    [InlineData("nrs_level", nameof(Slice.NRSLevel))]
    public void A_status_line_sets_the_level_raises_its_property_and_sends_nothing(string key, string property)
    {
        using var c = new ListenbackRoundTwoTests.RadioCase();
        var slice = c.Tx;
        var raised = new List<string?>();
        slice.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        int before = c.F.Vendor.Transport.Commands.Count;

        c.F.Vendor.Status($"slice {slice.Index} {key}=100");

        Assert.Equal(100, Level(slice, property));
        Assert.Contains(property, raised);
        Assert.Equal(before, c.F.Vendor.Transport.Commands.Count);

        // An equal value raises nothing more; a new one does, and still sends nothing.
        raised.Clear();
        c.F.Vendor.Status($"slice {slice.Index} {key}=100");
        Assert.DoesNotContain(property, raised);
        c.F.Vendor.Status($"slice {slice.Index} {key}=37");
        Assert.Equal(37, Level(slice, property));
        Assert.Contains(property, raised);
        Assert.Equal(before, c.F.Vendor.Transport.Commands.Count);

        // Out of FlexLib's 0 to 100 range, or not a number: ignored, as the neighbouring nr_level case does.
        c.F.Vendor.Status($"slice {slice.Index} {key}=101");
        c.F.Vendor.Status($"slice {slice.Index} {key}=loud");
        Assert.Equal(37, Level(slice, property));
        Assert.Equal(before, c.F.Vendor.Transport.Commands.Count);
    }

    [Fact]
    public void FlexBase_reads_the_reported_levels_off_the_active_slice()
    {
        // What every noise surface reads (the catalog's Read delegates are
        // these three FlexBase properties), so what Space reports.
        using var c = new ListenbackRoundTwoTests.RadioCase();
        Assert.Same(c.Tx, c.F.Vendor.Radio.ActiveSlice);
        Assert.Equal(0, c.F.Rig.NoiseReductionLegacyLevel);

        c.F.Vendor.Status($"slice {c.Tx.Index} nrl_level=100 anfl_level=40 nrs_level=25");

        Assert.Equal(100, c.F.Rig.NoiseReductionLegacyLevel);
        Assert.Equal(40, c.F.Rig.AutoNotchLegacyLevel);
        Assert.Equal(25, c.F.Rig.SpectralNoiseReductionLevel);
    }

    [Fact]
    public void NR_Filters_level_is_still_not_parsed_because_the_radio_does_not_report_it()
    {
        // Positive control for the scope of the ruling: only the three cases.
        // A FLEX-8600 answers nrf_level "Bad field"; a stray one stays unparsed.
        using var c = new ListenbackRoundTwoTests.RadioCase();
        c.F.Vendor.Status($"slice {c.Tx.Index} nrf_level=60");
        Assert.Equal(0, c.Tx.NRFLevel);
    }

    private static int Level(Slice slice, string property) => property switch
    {
        nameof(Slice.NRL_Level) => slice.NRL_Level,
        nameof(Slice.ANFL_Level) => slice.ANFL_Level,
        nameof(Slice.NRSLevel) => slice.NRSLevel,
        _ => throw new ArgumentOutOfRangeException(nameof(property)),
    };
}
