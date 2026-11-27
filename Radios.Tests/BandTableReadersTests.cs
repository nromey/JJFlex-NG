#nullable enable
using System.Linq;
using HamBands;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Repair round 1 of junk box batch 31 (#1282 and #1312): the corrected US band
/// table has other readers than the lockout. The logger's ADIF band, the Band
/// Plans browser's mode filter, and 2200 m and 630 m's emissions.
/// </summary>
/// <remarks>
/// Every figure is retyped here from its source: ADIF 3.1.8's Band enumeration
/// (adif.org.uk/318) and 47 CFR 97.305(c)(1) and (2); never read back from the
/// table under test.
/// </remarks>
public sealed class BandTableReadersTests
{
    // ---- ADIF's band, a pure function of frequency ----

    [Theory]
    // Every ADIF band at both edges (inclusive), from 3.1.8's enumeration.
    [InlineData(135_700UL, "2190m")] [InlineData(137_800UL, "2190m")]
    [InlineData(472_000UL, "630m")] [InlineData(479_000UL, "630m")]
    [InlineData(501_000UL, "560m")] [InlineData(504_000UL, "560m")]
    [InlineData(1_800_000UL, "160m")] [InlineData(2_000_000UL, "160m")]
    [InlineData(3_500_000UL, "80m")] [InlineData(4_000_000UL, "80m")]
    [InlineData(5_060_000UL, "60m")] [InlineData(5_450_000UL, "60m")]
    [InlineData(7_000_000UL, "40m")] [InlineData(7_300_000UL, "40m")]
    [InlineData(10_100_000UL, "30m")] [InlineData(10_150_000UL, "30m")]
    [InlineData(14_000_000UL, "20m")] [InlineData(14_350_000UL, "20m")]
    [InlineData(18_068_000UL, "17m")] [InlineData(18_168_000UL, "17m")]
    [InlineData(21_000_000UL, "15m")] [InlineData(21_450_000UL, "15m")]
    [InlineData(24_890_000UL, "12m")] [InlineData(24_990_000UL, "12m")]
    [InlineData(28_000_000UL, "10m")] [InlineData(29_700_000UL, "10m")]
    [InlineData(40_000_000UL, "8m")] [InlineData(45_000_000UL, "8m")]
    [InlineData(50_000_000UL, "6m")] [InlineData(54_000_000UL, "6m")]
    [InlineData(54_000_001UL, "5m")] [InlineData(69_900_000UL, "5m")]
    [InlineData(70_000_000UL, "4m")] [InlineData(71_000_000UL, "4m")]
    [InlineData(144_000_000UL, "2m")] [InlineData(148_000_000UL, "2m")]
    [InlineData(222_000_000UL, "1.25m")] [InlineData(225_000_000UL, "1.25m")]
    [InlineData(420_000_000UL, "70cm")] [InlineData(450_000_000UL, "70cm")]
    [InlineData(902_000_000UL, "33cm")] [InlineData(928_000_000UL, "33cm")]
    [InlineData(1_240_000_000UL, "23cm")] [InlineData(1_300_000_000UL, "23cm")]
    [InlineData(2_300_000_000UL, "13cm")] [InlineData(2_450_000_000UL, "13cm")]
    [InlineData(3_300_000_000UL, "9cm")] [InlineData(3_500_000_000UL, "9cm")]
    [InlineData(5_650_000_000UL, "6cm")] [InlineData(5_925_000_000UL, "6cm")]
    [InlineData(10_000_000_000UL, "3cm")] [InlineData(10_500_000_000UL, "3cm")]
    [InlineData(24_000_000_000UL, "1.25cm")] [InlineData(24_250_000_000UL, "1.25cm")]
    [InlineData(47_000_000_000UL, "6mm")] [InlineData(47_200_000_000UL, "6mm")]
    [InlineData(75_500_000_000UL, "4mm")] [InlineData(81_000_000_000UL, "4mm")]
    [InlineData(119_980_000_000UL, "2.5mm")] [InlineData(123_000_000_000UL, "2.5mm")]
    [InlineData(134_000_000_000UL, "2mm")] [InlineData(149_000_000_000UL, "2mm")]
    [InlineData(241_000_000_000UL, "1mm")] [InlineData(250_000_000_000UL, "1mm")]
    [InlineData(300_000_000_000UL, "submm")] [InlineData(7_500_000_000_000UL, "submm")]
    public void The_ADIF_band_is_ADIF_s_own_name_at_each_edge(ulong hz, string expected) =>
        Assert.Equal(expected, Bands.AdifBand(hz));

    [Theory]
    [InlineData(135_699UL)] [InlineData(137_801UL)]
    [InlineData(219_000_000UL)] [InlineData(219_500_000UL)] [InlineData(220_000_000UL)]   // ours (data only), not ADIF's
    [InlineData(221_999_999UL)]
    [InlineData(275_000_000_000UL)] [InlineData(290_000_000_000UL)] [InlineData(299_999_999_999UL)]   // ours, not ADIF's
    [InlineData(5_450_001UL)]                // above ADIF's 60 m
    [InlineData(54_000_000_500UL)]
    [InlineData(7_500_000_000_001UL)]
    [InlineData(0UL)]
    public void Where_ADIF_defines_no_band_the_ADIF_band_is_empty(ulong hz) =>
        Assert.Equal("", Bands.AdifBand(hz));

    [Fact]
    public void The_display_label_is_kept_and_is_not_the_ADIF_value_where_they_differ()
    {
        // Band memory and the readouts keep Part 97's names; only the logger asks ADIF.
        Assert.Equal("2200m", Bands.Query(136_500)!.Name);
        Assert.Equal("2190m", Bands.AdifBand(136_500));
        Assert.Equal("5cm", Bands.Query(5_760_000_000)!.Name);
        Assert.Equal("6cm", Bands.AdifBand(5_760_000_000));
        Assert.Equal("1.2cm", Bands.Query(24_048_000_000)!.Name);
        Assert.Equal("1.25cm", Bands.AdifBand(24_048_000_000));
        Assert.Equal("33cm", Bands.AdifBand(915_000_000));   // was 35cm, never an ADIF value
    }

    /// <summary>
    /// Repair round 2 changed this pin on purpose: round 1 held the export's own
    /// <c>AdifBand(FreqInt64(freqText))</c> line, which derived BAND from the
    /// RECEIVE frequency. The export and duplicate checking now share one band
    /// key (<see cref="Bands.ContactBandKey"/>, through <c>LogDupChecking.BandKey</c>);
    /// the logger still writes <see cref="Bands.AdifBand"/> of the transmit
    /// frequency. Their behaviour is <see cref="LogBandKeyTests"/>'s.
    /// </summary>
    [Fact]
    public void The_logger_the_export_and_duplicate_checking_ask_ADIF_and_nothing_else_does()
    {
        string root = FieldKeyMapScan.RepoRoot();
        string Read(string f) => System.IO.File.ReadAllText(System.IO.Path.Combine(root, f));
        Assert.Contains("HamBands.Bands.AdifBand(RigControl.TXFrequency)", Read("LogEntry.vb"));
        Assert.Contains("bandFld.Data = Bands.AdifBand(FreqInt64(val))", Read("LogSession.vb"));
        Assert.DoesNotContain("item.Name", Read("LogSession.vb"));
        Assert.Contains("value = LogDupChecking.BandKey(session)", Read("ExportForm.vb"));
        Assert.DoesNotContain("AdifBand", Read("ExportForm.vb"));
        Assert.Contains("HamBands.Bands.ContactBandKey(", Read("LogDupChecking.vb"));
        Assert.DoesNotContain("AdifBand", Read(System.IO.Path.Combine("Radios", "RadioStatusBuilder.cs")));
        Assert.DoesNotContain("AdifBand", Read(System.IO.Path.Combine("JJFlexWpf", "MainWindow.xaml.cs")));
    }

    // ---- repair round 2: one band key per contact ----

    [Theory]
    [InlineData("35cm", "33cm")]
    [InlineData("35CM", "33cm")]
    [InlineData("2200m", "2190m")]
    [InlineData("5cm", "6cm")]
    [InlineData("1.2cm", "1.25cm")]
    [InlineData("70cm", "70cm")]
    [InlineData("2M", "2M")]                       // an ADIF name, as logged
    [InlineData("above 275GHz", "above 275GHz")]   // spans no band and submm: no alias
    [InlineData("1.25m", "1.25m")]
    public void A_logged_band_is_its_alias_or_itself(string logged, string key) =>
        Assert.Equal(key, Bands.ContactBandKey(logged, 435_100_000, 145_900_000));

    [Fact]
    public void An_empty_logged_band_is_derived_from_transmit_then_receive()
    {
        Assert.Equal("70cm", Bands.ContactBandKey("", 435_100_000, 145_900_000));
        Assert.Equal("70cm", Bands.ContactBandKey(null!, 435_100_000, 145_900_000));
        Assert.Equal("2m", Bands.ContactBandKey("", 0, 145_900_000));          // transmit missing or zero
        Assert.Equal("", Bands.ContactBandKey("", 219_500_000, 145_900_000));   // never the receive band
        Assert.Equal("", Bands.ContactBandKey("", 0, 0));
        Assert.Equal("630m", Bands.ContactBandKey("  ", 475_000, 0));          // blank is empty
    }

    /// <summary>
    /// Every alias maps a label whose whole range lies inside exactly that ADIF
    /// band, and is not itself an ADIF name. Retyped ranges from the labels'
    /// history: 35cm 902 to 928 MHz (main), 2200m 135.7 to 137.8 kHz, 5cm 5650
    /// to 5925 MHz and 1.2cm 24.0 to 24.25 GHz (this branch only).
    /// </summary>
    [Theory]
    [InlineData("35cm", 902_000_000UL, 928_000_000UL)]
    [InlineData("2200m", 135_700UL, 137_800UL)]
    [InlineData("5cm", 5_650_000_000UL, 5_925_000_000UL)]
    [InlineData("1.2cm", 24_000_000_000UL, 24_250_000_000UL)]
    public void Each_alias_s_range_lies_in_its_one_ADIF_band(string label, ulong low, ulong high)
    {
        string alias = Bands.ContactBandKey(label, 0, 0);
        Assert.NotEqual(label, alias);
        Assert.Equal(alias, Bands.AdifBand(low));
        Assert.Equal(alias, Bands.AdifBand(high));
        Assert.Equal(alias, Bands.AdifBand((low + high) / 2));
        Assert.NotEqual(label, Bands.AdifBand(low));   // the label itself is not ADIF's name
    }

    // ---- the Band Plans browser's mode filter ----

    private static (ulong Low, ulong High)[] Found(Bands.BandNames band, Bands.Licenses cls, Bands.Modes filter) =>
        Bands.Query(band, cls, filter)?.Divisions?.Select(d => (d.Low, d.High)).ToArray() ?? System.Array.Empty<(ulong, ulong)>();

    private const Bands.Licenses Technician = Bands.Licenses.technition;
    private const Bands.Licenses General = Bands.Licenses.general;

    [Theory]
    [InlineData(Bands.Modes.PhoneCW)]
    [InlineData(Bands.Modes.USB)]
    [InlineData(Bands.Modes.SSB)]
    public void Phone_filters_find_the_Technician_s_10_metre_SSB_segment_and_not_the_CW_segment(Bands.Modes filter) =>
        Assert.Equal(new[] { (28_300_000UL, 28_500_000UL) }, Found(Bands.BandNames.m10, Technician, filter));

    [Fact]
    public void Each_filter_against_the_Technician_s_10_metres()
    {
        Assert.Equal(2, Found(Bands.BandNames.m10, Technician, Bands.Modes.CW).Length);       // CW everywhere
        Assert.Equal(2, Found(Bands.BandNames.m10, Technician, Bands.Modes.none).Length);
        Assert.Empty(Found(Bands.BandNames.m10, Technician, Bands.Modes.MCW));
        Assert.Empty(Found(Bands.BandNames.m10, Technician, Bands.Modes.Image));
        Assert.Equal(new[] { (28_000_000UL, 28_300_000UL) }, Found(Bands.BandNames.m10, Technician, Bands.Modes.RTTYData));
        Assert.Equal(new[] { (28_000_000UL, 28_300_000UL) }, Found(Bands.BandNames.m10, Technician, Bands.Modes.Data));
        Assert.Empty(Found(Bands.BandNames.m10, Technician, Bands.Modes.M60Data));
    }

    [Fact]
    public void Each_filter_against_2_metres()
    {
        var all = new[] { (144_000_000UL, 144_100_000UL), (144_100_000UL, 148_000_000UL) };
        var above = new[] { (144_100_000UL, 148_000_000UL) };
        Assert.Equal(all, Found(Bands.BandNames.m2, General, Bands.Modes.CW));
        Assert.Equal(all, Found(Bands.BandNames.m2, General, Bands.Modes.none));
        Assert.Equal(above, Found(Bands.BandNames.m2, General, Bands.Modes.PhoneCW));   // not the CW-only segment
        Assert.Equal(above, Found(Bands.BandNames.m2, General, Bands.Modes.USB));
        Assert.Equal(above, Found(Bands.BandNames.m2, General, Bands.Modes.SSB));
        Assert.Equal(above, Found(Bands.BandNames.m2, General, Bands.Modes.MCW));
        Assert.Equal(above, Found(Bands.BandNames.m2, General, Bands.Modes.Image));
        Assert.Equal(above, Found(Bands.BandNames.m2, General, Bands.Modes.RTTYData));
        Assert.Equal(above, Found(Bands.BandNames.m2, General, Bands.Modes.Data));
        Assert.Empty(Found(Bands.BandNames.m2, General, Bands.Modes.M60Data));
    }

    [Fact]
    public void Each_filter_against_60_metres()
    {
        foreach (var filter in new[] { Bands.Modes.none, Bands.Modes.CW, Bands.Modes.PhoneCW, Bands.Modes.USB, Bands.Modes.SSB,
                                       Bands.Modes.RTTYData, Bands.Modes.Data, Bands.Modes.M60Data })
            Assert.Equal(5, Found(Bands.BandNames.m60, General, filter).Length);
        Assert.Empty(Found(Bands.BandNames.m60, General, Bands.Modes.Image));
        Assert.Empty(Found(Bands.BandNames.m60, General, Bands.Modes.MCW));
        // The class-less query reads the same mapping.
        Assert.Equal(5, Bands.Query(Bands.BandNames.m60, Bands.Modes.USB)!.Divisions!.Length);
    }

    [Fact]
    public void Two_thousand_two_hundred_and_six_hundred_and_thirty_metres_take_CW_but_not_MCW()
    {
        foreach (var band in new[] { Bands.BandNames.m2190, Bands.BandNames.m560 })
        {
            Assert.Single(Found(band, General, Bands.Modes.CW));
            Assert.Single(Found(band, General, Bands.Modes.PhoneCW));
            Assert.Empty(Found(band, General, Bands.Modes.MCW));
            Assert.DoesNotContain(Bands.Modes.MCW, Bands.Query(band)!.Divisions![0].Mode);
        }
        Assert.Contains(Bands.Modes.MCW, Bands.Query(146_520_000)!.Divisions![1].Mode);   // MCW stays on VHF
        Assert.Single(Found(Bands.BandNames.m6, General, Bands.Modes.MCW));
    }

    // ---- the transmit judge's table is not the browser's, and did not move ----

    /// <summary>
    /// <c>TransmitBandLockout.Permits</c>, every mark against every emission kind,
    /// pinned as it stood at 429080ba. Repair round 1 changed only the 60 m
    /// divisions' mark (Modes.USB to Modes.PhoneCW), not this table: the
    /// browser's filter (<see cref="Bands.Allows"/>) is deliberately coarser,
    /// and making the two agree would loosen what the lockout admits.
    /// </summary>
    [Fact]
    public void The_lockout_s_permits_table_is_unchanged()
    {
        var K = typeof(TransmitBandLockout.EmissionKind);
        string Row(Bands.Modes m) => string.Join("", System.Enum.GetValues<TransmitBandLockout.EmissionKind>()
            .Select(k => TransmitBandLockout.Permits(new[] { m }, k) ? "1" : "0"));
        // Columns: Cw, SsbPhone, OtherPhone, DataOrImage, Rtty.
        Assert.Equal(new[] { "Cw", "SsbPhone", "OtherPhone", "DataOrImage", "Rtty" }, System.Enum.GetNames(K));
        Assert.Equal("10000", Row(Bands.Modes.none));
        Assert.Equal("10000", Row(Bands.Modes.CW));
        Assert.Equal("10000", Row(Bands.Modes.MCW));
        Assert.Equal("11100", Row(Bands.Modes.PhoneCW));
        Assert.Equal("11000", Row(Bands.Modes.USB));
        Assert.Equal("10010", Row(Bands.Modes.Image));
        Assert.Equal("10011", Row(Bands.Modes.RTTYData));
        Assert.Equal("10010", Row(Bands.Modes.M60Data));
        Assert.Equal("11000", Row(Bands.Modes.SSB));
        Assert.Equal("10010", Row(Bands.Modes.Data));
        foreach (var k in System.Enum.GetValues<TransmitBandLockout.EmissionKind>())
        {
            Assert.True(TransmitBandLockout.Permits(null, k));
            Assert.True(TransmitBandLockout.Permits(System.Array.Empty<Bands.Modes>(), k));
        }
        // The only difference repair round 1 made: 60 m is marked phone, not USB.
        Assert.Equal(new[] { Bands.Modes.PhoneCW, Bands.Modes.RTTYData, Bands.Modes.M60Data },
            Bands.Query(Bands.BandNames.m60)!.Divisions![0].Mode);
    }
}
