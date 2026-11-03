using System.Globalization;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// What F says (#642). Ruled by Noel 2026-10-18: a setting, "Active,
/// transmit, or both", Both speaking the transmit slice first, one sentence
/// when the two are the same slice.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class FrequencyKeyReadoutTests
{
    public FrequencyKeyReadoutTests() => Lexicon.Load(Lexicon.Partitions);

    private static string Mhz(ulong hz) => (hz / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>Selected A on 7.150, transmit B on 14.100: the case this exists for.</summary>
    private static readonly SliceRoles Apart =
        new SliceRoles("A", 7_150_000UL, hasTransmit: true, "B", 14_100_000UL, same: false);

    /// <summary>One slice, selected and transmit: the common case.</summary>
    private static readonly SliceRoles Together =
        new SliceRoles("A", 14_250_000UL, hasTransmit: true, "A", 14_250_000UL, same: true);

    [Theory]
    [InlineData(FrequencyKeySlices.Both)]
    [InlineData(FrequencyKeySlices.Transmit)]
    [InlineData(FrequencyKeySlices.Selected)]
    public void One_slice_is_one_sentence_whatever_the_setting(FrequencyKeySlices setting)
    {
        string said = FrequencyKeyReadout.Sentence(setting, Together, Mhz);
        Assert.Equal(Lexicon.Get("connect.home.frequency", ("freqText", "14.250"), ("slice", "A")), said);
        Assert.Equal(1, Count(said, "14.250"));
    }

    [Fact]
    public void Both_speaks_the_transmit_slice_first_and_names_each_role()
    {
        string said = FrequencyKeyReadout.Sentence(FrequencyKeySlices.Both, Apart, Mhz);
        int tx = said.IndexOf("14.100", System.StringComparison.Ordinal);
        int sel = said.IndexOf("7.150", System.StringComparison.Ordinal);
        Assert.True(tx >= 0 && sel > tx, "transmit first: " + said);
        Assert.Contains("slice B, transmit", said);
        Assert.Contains("slice A, selected", said);
    }

    [Fact]
    public void Transmit_reads_the_transmit_slice_and_says_so()
    {
        string said = FrequencyKeyReadout.Sentence(FrequencyKeySlices.Transmit, Apart, Mhz);
        Assert.Contains("14.100", said);
        Assert.DoesNotContain("7.150", said);
        Assert.Contains("slice B, transmit", said);
    }

    [Fact]
    public void Selected_reads_the_selected_slice_and_says_so()
    {
        string said = FrequencyKeyReadout.Sentence(FrequencyKeySlices.Selected, Apart, Mhz);
        Assert.Contains("7.150", said);
        Assert.DoesNotContain("14.100", said);
        Assert.Contains("slice A, selected", said);
    }

    [Fact]
    public void No_transmit_slice_reads_the_selected_slice_plainly()
    {
        var none = new SliceRoles("A", 7_150_000UL, hasTransmit: false, "", 0UL, same: false);
        Assert.False(none.TransmitsElsewhere);
        Assert.Equal(Lexicon.Get("connect.home.frequency", ("freqText", "7.150"), ("slice", "A")),
                     FrequencyKeyReadout.Sentence(FrequencyKeySlices.Transmit, none, Mhz));
    }

    [Fact]
    public void The_words_are_the_slice_status_words()
    {
        // RadioStatusBuilder.BuildFullSliceStatus names roles ", selected" and
        // ", transmit"; F must not grow a third vocabulary for them.
        string status = System.IO.File.ReadAllText(System.IO.Path.Combine(
            CaptureMeterSetTests.RepoRoot(), "Radios", "RadioStatusBuilder.cs"));
        Assert.Contains("if (isActive) sb.Append(\", selected\");", status);
        Assert.Contains("if (isTx) sb.Append(\", transmit\");", status);
        Assert.EndsWith(", transmit", Lexicon.Get("connect.home.frequency_transmit", ("freqText", "x"), ("slice", "B")));
        Assert.EndsWith(", selected", Lexicon.Get("connect.home.frequency_selected", ("freqText", "x"), ("slice", "A")));
    }

    [Fact]
    public void The_default_is_both()
    {
        Assert.Equal(FrequencyKeySlices.Both, default(FrequencyKeySlices));
        Assert.Equal(0, (int)FrequencyKeySlices.Both);
    }

    private static int Count(string text, string needle)
    {
        int n = 0;
        for (int at = text.IndexOf(needle, System.StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + 1, System.StringComparison.Ordinal)) n++;
        return n;
    }
}
