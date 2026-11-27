using System;
using System.IO;
using System.Linq;
using HamBands;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #1312, items 1 and 2: the sub-band notice names the operator's own class,
/// not the division's first listed one, and every band in the corrected table
/// has a spoken name, so a notice never says "Entering m2190 band". The Band
/// Plans dialog's lists and result use the same words.
/// </summary>
/// <remarks>Pure decisions and the source only: no window is built, and every
/// test asserts it runs on an MTA thread, where WPF cannot build one.</remarks>
public sealed class BandWordsTests
{
    public BandWordsTests() => Lexicon.Load(Lexicon.Partitions);

    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    private static string Label(Bands.Licenses cls, string modeKey) =>
        Lexicon.Get("settings.band.subband_label", ("license", FreqOutHandlers.FormatLicense(cls)), ("mode", Lexicon.Get(modeKey)));

    [Fact]
    public void A_Technician_at_3_525_hears_Technician_not_Novice()
    {
        AssertWindowless();
        Assert.Equal(Label(Bands.Licenses.technition, "settings.band.mode_cw"),
            FreqOutHandlers.SubBandLabel(3_530_000, Bands.BandNames.m80, Bands.Licenses.technition));
        Assert.Equal(Label(Bands.Licenses.novice, "settings.band.mode_cw"),
            FreqOutHandlers.SubBandLabel(3_530_000, Bands.BandNames.m80, Bands.Licenses.novice));
    }

    [Fact]
    public void An_Extra_at_7_200_hears_Extra_not_Advanced()
    {
        AssertWindowless();
        string extra = FreqOutHandlers.SubBandLabel(7_200_000, Bands.BandNames.m40, Bands.Licenses.extra)!;
        Assert.Equal(Label(Bands.Licenses.extra, "settings.band.mode_phone_cw"), extra);
        Assert.DoesNotContain(FreqOutHandlers.FormatLicense(Bands.Licenses.advanced), extra, StringComparison.Ordinal);
        Assert.Null(FreqOutHandlers.SubBandLabel(7_150_000, Bands.BandNames.m40, Bands.Licenses.general));
    }

    [Fact]
    public void Every_band_has_a_spoken_name_and_none_is_its_enum_name()
    {
        AssertWindowless();
        foreach (Bands.BandNames band in Enum.GetValues(typeof(Bands.BandNames)))
        {
            if (band == Bands.BandNames.NumBands) continue;
            string name = FreqOutHandlers.BandDisplayName(band);
            Assert.False(string.IsNullOrWhiteSpace(name), band + " has no spoken name");
            Assert.NotEqual(band.ToString(), name);
        }
        Assert.Equal(Lexicon.Get("settings.band.name_2200m"), FreqOutHandlers.BandDisplayName(Bands.BandNames.m2190));
        Assert.Equal(Lexicon.Get("settings.band.name_630m"), FreqOutHandlers.BandDisplayName(Bands.BandNames.m560));
    }

    [Fact]
    public void Class_names_are_never_the_enum_s_own()
    {
        AssertWindowless();
        Assert.Equal(Lexicon.Get("settings.band.license_technician"), FreqOutHandlers.FormatLicense(Bands.Licenses.technition));
        foreach (Bands.Modes m in Enum.GetValues(typeof(Bands.Modes)))
        {
            if (m == Bands.Modes.none) continue;
            Assert.False(string.IsNullOrEmpty(FreqOutHandlers.FormatMode(m)), m + " has no words");
        }
    }

    [Fact]
    public void Band_Plans_maps_a_list_position_to_a_listed_band_and_names_classes_and_modes_in_words()
    {
        AssertWindowless();
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "NativeMenuBar.cs")).Replace("\r\n", "\n");
        int start = src.IndexOf("private void ShowBandPlansDialog()", StringComparison.Ordinal);
        int end = src.IndexOf("private void ShowFeatureAvailability()", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        string body = src.Substring(start, end - start);
        Assert.DoesNotContain("(Bands.BandNames)bandIdx", body, StringComparison.Ordinal);
        Assert.Contains("var band = listedBands[bandIdx];", body, StringComparison.Ordinal);
        Assert.Contains("Where(b => !b.Retired)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Enum.GetNames(", body, StringComparison.Ordinal);
        Assert.Contains("LicenseConfig.ClassLabel(l)", body, StringComparison.Ordinal);
        Assert.Contains("d.License.Select(FreqOutHandlers.FormatLicense)", body, StringComparison.Ordinal);
        Assert.Contains("d.Mode.Select(FreqOutHandlers.FormatMode)", body, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
