using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// What committing a typed frequency owes the mode, and its arrival sentence
/// (#312). Pure static decision: NO WINDOW, no FreqOutHandlers instance, no
/// WPF object is constructed here, so this class is desk-safe.
/// </summary>
/// <remarks>
/// <b>What this cannot see:</b> that <c>CommitQuickType</c> hands the outcome
/// to <c>SixtyMeterModeDelivery</c>, which decides the true mode clause and is
/// tested past the speech gate in Radios.Tests (SixtyMeterModeDeliveryTests).
/// The arrival here deliberately carries no mode clause (repair round one).
/// Bench half: type 5.360 and Enter on the frequency field while on USB, and
/// listen for the mode.
/// </remarks>
public sealed class QuickTypeCommitTests
{
    [Fact]
    public void Typing_a_frequency_inside_the_60m_digital_segment_on_USB_sets_CW_and_says_so()
    {
        var o = FreqOutHandlers.QuickTypeCommitOutcome("US", 5_360_000, "USB", "5.360 megahertz");

        Assert.Equal("CW", o.Correction?.RequiredMode);
        Assert.Equal(", mode set to CW for digital segment", o.Correction!.Advisory);
        Assert.Equal("Frequency set to 5.360 megahertz", o.Arrival);
    }

    [Fact]
    public void Typing_a_60m_channel_on_CW_sets_the_channel_mode_as_the_band_jump_does()
    {
        var o = FreqOutHandlers.QuickTypeCommitOutcome("US", 5_332_000, "CW", "5.332 megahertz");

        Assert.Equal("USB", o.Correction?.RequiredMode);
        Assert.Equal(", mode set to USB for Channel 1", o.Correction!.Advisory);
        Assert.Equal("Frequency set to 5.332 megahertz", o.Arrival);
    }

    [Theory]
    [InlineData(5_360_000UL, "DIGU")]
    [InlineData(5_360_000UL, "CW")]
    [InlineData(5_332_000UL, "USB")]
    [InlineData(14_200_000UL, "USB")]
    [InlineData(7_030_000UL, "LSB")]
    public void Anything_already_permitted_or_off_60m_is_unchanged(ulong hz, string mode)
    {
        var o = FreqOutHandlers.QuickTypeCommitOutcome("US", hz, mode, "the frequency");

        Assert.Null(o.Correction);
        Assert.Equal("Frequency set to the frequency", o.Arrival);
    }

    [Fact]
    public void With_no_rig_there_is_no_mode_to_correct()
    {
        var o = FreqOutHandlers.QuickTypeCommitOutcome("US", 5_360_000, null, "5.360 megahertz");

        Assert.Null(o.Correction);
        Assert.Equal("Frequency set to 5.360 megahertz", o.Arrival);
    }
}
