using System;
using System.IO;
using System.Linq;
using HamBands;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #1282: with the TX band lockout on, every band from 2 m up was refused for
/// every class, because the band table gave those bands no divisions. The
/// table now carries the US privileges of 47 CFR 97.301 as current on
/// 2026-11-20 (the Part 97 check of 2026-11-24), and the corrected 2200 m and
/// 630 m bands.
/// </summary>
/// <remarks>
/// <para>Judged through <see cref="TransmitBandLockout.Allows"/>, the
/// lockout's frequency arithmetic, so these tests are about the table's
/// EDGES. Which modes a division allows is #1310's to judge.</para>
/// <para>Every figure is retyped from Part 97 here, never read back from the
/// table, so a wrong table cannot agree with itself.</para>
/// </remarks>
public sealed class UsBandTableTests
{
    private const Bands.Licenses Novice = Bands.Licenses.novice;
    private const Bands.Licenses Technician = Bands.Licenses.technition;
    private const Bands.Licenses General = Bands.Licenses.general;
    private const Bands.Licenses Extra = Bands.Licenses.extra;

    /// <summary>An FM signal, 8 kHz either side of the carrier.</summary>
    private static bool Fm(ulong hz, Bands.Licenses license) => TransmitBandLockout.Allows(hz, "FM", -8000, 8000, license);

    /// <summary>A CW signal, 250 Hz either side.</summary>
    private static bool Cw(ulong hz, Bands.Licenses license) => TransmitBandLockout.Allows(hz, "CW", -250, 250, license);

    /// <summary>A data signal in DIGU, 3 kHz above the dial.</summary>
    private static bool Digu(ulong hz, Bands.Licenses license) => TransmitBandLockout.Allows(hz, "DIGU", 0, 3000, license);

    // ---- the brief's cases ----

    [Fact]
    public void A_Technician_may_transmit_on_146_52_and_223_5_megahertz()
    {
        Assert.True(Fm(146_520_000, Technician));
        Assert.True(Fm(223_500_000, Technician));
    }

    [Fact]
    public void A_Technician_may_send_data_on_219_5_megahertz_and_a_Novice_may_not()
    {
        // 97.301(a) gives Technician and up 219 to 220 MHz; 97.301(e) gives a
        // Novice nothing there.
        Assert.True(Digu(219_500_000, Technician));
        Assert.False(Digu(219_500_000, Novice));
    }

    [Fact]
    public void A_General_may_transmit_on_630_and_2200_metres()
    {
        Assert.True(Cw(475_000, General));
        Assert.True(Cw(136_500, General));
    }

    [Fact]
    public void Five_hundred_and_one_kilohertz_is_refused_regression_pin()
    {
        // Not an amateur allocation; the table used to call it "560m". Refused
        // before this change too (no divisions), so this pins, it does not prove.
        Assert.False(Cw(501_000, General));
        Assert.False(Cw(501_000, Extra));
    }

    // ---- the edges, band by band ----

    [Fact]
    public void The_2200_metre_band_is_135_7_to_137_8_kilohertz()
    {
        Assert.True(Cw(135_960, General));
        Assert.True(Cw(137_540, General));
        Assert.False(Cw(135_600, General));
        Assert.False(Cw(137_900, General));
        Assert.False(Cw(136_500, Technician));
    }

    [Fact]
    public void The_630_metre_band_is_472_to_479_kilohertz_for_General_and_up_only()
    {
        Assert.True(Cw(472_300, Extra));
        Assert.True(Cw(478_700, Extra));
        Assert.False(Cw(471_900, Extra));
        Assert.False(Cw(479_100, Extra));
        Assert.False(Cw(475_000, Technician));
        Assert.False(Cw(475_000, Novice));
    }

    [Theory]
    [InlineData(144_050_000UL)]
    [InlineData(147_990_000UL)]
    [InlineData(420_100_000UL)]
    [InlineData(449_900_000UL)]
    [InlineData(902_100_000UL)]
    [InlineData(927_900_000UL)]
    [InlineData(1_240_100_000UL)]
    [InlineData(1_299_900_000UL)]
    [InlineData(2_300_100_000UL)]
    [InlineData(2_309_900_000UL)]
    [InlineData(2_390_100_000UL)]
    [InlineData(2_449_900_000UL)]
    [InlineData(5_650_100_000UL)]
    [InlineData(10_000_100_000UL)]
    [InlineData(24_000_100_000UL)]
    [InlineData(47_000_100_000UL)]
    [InlineData(76_000_100_000UL)]
    [InlineData(80_999_900_000UL)]
    [InlineData(122_250_100_000UL)]
    [InlineData(122_999_900_000UL)]
    [InlineData(134_000_100_000UL)]
    [InlineData(140_999_900_000UL)]
    [InlineData(241_000_100_000UL)]
    [InlineData(300_000_000_000UL)]
    public void Technician_and_up_have_every_97_301_a_band(ulong hz)
    {
        Assert.True(Cw(hz, Technician));
        Assert.True(Cw(hz, General));
        Assert.True(Cw(hz, Bands.Licenses.advanced));
        Assert.True(Cw(hz, Extra));
    }

    [Theory]
    [InlineData(2_350_000_000UL)]      // between the two 13 cm pieces
    [InlineData(3_400_000_000UL)]      // the old 9 cm band
    [InlineData(75_700_000_000UL)]     // below 4 mm's 76 GHz
    [InlineData(120_000_000_000UL)]    // the old 2.5 mm
    [InlineData(145_000_000_000UL)]    // the old 2 mm
    [InlineData(221_000_000UL)]        // between 220 and 222 MHz
    public void What_is_not_in_97_301_a_is_refused(ulong hz) => Assert.False(Cw(hz, Extra));

    [Fact]
    public void A_Novice_has_only_222_to_225_and_1270_to_1295_megahertz_above_30()
    {
        Assert.True(Fm(223_500_000, Novice));
        Assert.True(Fm(1_280_000_000, Novice));
        Assert.False(Fm(1_260_000_000, Novice));
        Assert.False(Fm(1_296_000_000, Novice));
        Assert.False(Fm(146_520_000, Novice));
        Assert.False(Fm(446_000_000, Novice));
        Assert.False(Cw(50_200_000, Novice));
        Assert.True(Cw(50_200_000, Technician), "positive control: 6 m for a Technician");
    }

    // ---- the traps ----

    [Fact]
    public void Every_band_sits_at_its_enum_value_and_has_a_name()
    {
        for (int i = 0; i < Bands.TheBands.Length; i++)
            Assert.Equal(i, (int)Bands.TheBands[i].Band);
        Assert.Equal((int)Bands.BandNames.NumBands, Bands.TheBands.Length);
        Assert.All(Bands.TheBands, b => Assert.False(string.IsNullOrEmpty(b.Name)));
    }

    [Fact]
    public void Nine_centimetres_keeps_its_slot_but_is_never_found_by_frequency()
    {
        Assert.Null(Bands.Query(3_400_000_000UL));
        var nine = Bands.Query(Bands.BandNames.m9);
        Assert.NotNull(nine);
        Assert.True(nine!.Retired);
        Assert.False(Bands.Query(Bands.BandNames.m2)!.Retired, "positive control: a live band is not retired");
    }

    [Fact]
    public void The_band_member_names_band_memory_saves_are_all_still_there()
    {
        // BandMemory saves a band by NAME: a name it no longer knows empties
        // the whole memory (and the next save writes the empty one over it).
        string[] saved =
        {
            "m2190", "m560", "m160", "m80", "m60", "m40", "m30", "m20", "m17", "m15", "m12", "m10",
            "m6", "m2", "m125", "m70", "m35", "m23", "m13", "m9", "m6c", "m3c", "m125c",
            "m6m", "m4m", "m25m", "m2m", "m1m",
        };
        string[] now = Enum.GetNames(typeof(Bands.BandNames));
        for (int i = 0; i < saved.Length; i++)
            Assert.Equal(saved[i], now[i]);
    }

    [Fact]
    public void An_old_band_memory_holding_the_retired_and_renamed_bands_still_loads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-bandtable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Test_Op_bandMemory.xml"),
                "<?xml version=\"1.0\"?>\r\n<BandMemory><Entries>"
                + "<BandModeEntry Band=\"m560\" Mode=\"CW\" Frequency=\"502000\" />"
                + "<BandModeEntry Band=\"m9\" Mode=\"USB\" Frequency=\"3400000000\" />"
                + "<BandModeEntry Band=\"m2190\" Mode=\"CW\" Frequency=\"136500\" />"
                + "<BandModeEntry Band=\"m20\" Mode=\"USB\" Frequency=\"14250000\" />"
                + "</Entries></BandMemory>\r\n");
            var memory = BandMemory.Load(dir, "Test_Op");
            Assert.Equal(4, memory.Entries.Count);
            Assert.Equal(14_250_000UL, memory.GetFrequency(Bands.BandNames.m20, "USB"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_60_metre_divisions_are_the_channels_and_the_segment()
    {
        // 97.303(h)(3): each channel is 2.8 kHz centred on its centre; the
        // segment is 5351.5 to 5366.5 kHz. The centres are the 60 m table's.
        var sixty = Bands.Query(Bands.BandNames.m60, General)!;
        var expected = SixtyMeterChannels.USChannels
            .Select(c => (c.CentreHz - 1400, c.CentreHz + 1400))
            .Append((5_351_500UL, 5_366_500UL))
            .OrderBy(e => e.Item1)
            .ToArray();
        var actual = sixty.Divisions.Select(d => (d.Low, d.High)).OrderBy(e => e.Low).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Sixty_metres_between_the_channels_is_refused_by_the_lockout()
    {
        // Used to pass: one division covered 5.300 to 5.500 MHz.
        Assert.False(Cw(5_340_000, Extra));
        Assert.False(Cw(5_450_000, General));
        Assert.True(Cw(5_332_000, General), "positive control: a channel's centre");
        Assert.True(Cw(5_357_000, General), "positive control: the segment");
    }
}
