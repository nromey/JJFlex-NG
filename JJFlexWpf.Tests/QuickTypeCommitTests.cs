using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// What the two FreqOutHandlers ways onto 60 m — typed entry and the band
/// jump — owe the mode, and every sentence each can assemble for it (#312).
/// Pure static decisions: NO WINDOW, no FreqOutHandlers instance, no WPF
/// object is constructed here, so this class is desk-safe.
/// </summary>
/// <remarks>
/// <para>The third way, the channel rotation, is asserted in Radios.Tests
/// (SixtyMeterModeDeliveryTests), which also drives a real FlexBase's
/// RequestMode and reads the sentence past the speech gate. Here each path's
/// own arrival goes through the same <see cref="SixtyMeterModeDelivery.Compose"/>
/// for each outcome, so all three paths' wording is pinned.</para>
/// <para><b>Ruled 2026-10-20 (round two):</b> sent says "switching" and
/// nothing more; a refusal speaks about the MODE only. Round one's sentences
/// ("..., mode set to CW for digital segment", "..., and nothing was changed
/// because you are transmitting") fail these assertions.</para>
/// <para><b>What this cannot see:</b> that <c>CommitQuickType</c> and
/// <c>BandJump</c> hand these to <c>SixtyMeterModeDelivery.Deliver</c>; that is
/// read in the source. Bench half: type 5.360 and Enter on the frequency field
/// while on USB, jump to 60 meters on CW, and listen.</para>
/// </remarks>
public sealed class QuickTypeCommitTests
{
    private static string Say(string arrival, SixtyMeterChannels.ModeCorrection c, ModeRequestOutcome o)
        => SixtyMeterModeDelivery.Compose(arrival, c, o);

    // ---------------- typed entry ----------------

    [Fact]
    public void Typing_a_frequency_inside_the_60m_digital_segment_on_USB_asks_for_CW()
    {
        var o = FreqOutHandlers.QuickTypeCommitOutcome("US", 5_360_000, "USB", "5.360 megahertz");

        Assert.Equal("CW", o.Correction?.RequiredMode);
        Assert.Equal("Frequency set to 5.360 megahertz", o.Arrival);
        Assert.Equal("Frequency set to 5.360 megahertz, digital segment needs CW, switching",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.Sent));
        Assert.Equal("Frequency set to 5.360 megahertz, digital segment needs CW, mode not changed while transmitting",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.RefusedTransmitting));
        Assert.Equal("Frequency set to 5.360 megahertz, digital segment needs CW, mode not changed",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.RefusedNoSlice));
        Assert.Equal("Frequency set to 5.360 megahertz, digital segment needs CW, mode not changed",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.RefusedNotRunning));
    }

    [Fact]
    public void Typing_a_60m_channel_on_CW_asks_for_the_channel_mode_as_the_band_jump_does()
    {
        var o = FreqOutHandlers.QuickTypeCommitOutcome("US", 5_332_000, "CW", "5.332 megahertz");

        Assert.Equal("USB", o.Correction?.RequiredMode);
        Assert.Equal("Frequency set to 5.332 megahertz, Channel 1 needs USB, switching",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.Sent));
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

    // ---------------- band jump ----------------

    [Fact]
    public void A_band_jump_to_a_60m_channel_on_CW_asks_for_USB_and_says_only_what_is_known()
    {
        var o = FreqOutHandlers.BandJumpOutcome("60 meter", "5.332 megahertz", true, "US", 5_332_000, "CW");

        Assert.Equal("USB", o.Correction?.RequiredMode);
        Assert.Equal("60 meter band, 5.332 megahertz", o.Arrival);
        Assert.Equal("60 meter band, 5.332 megahertz, Channel 1 needs USB, switching",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.Sent));
        Assert.Equal("60 meter band, 5.332 megahertz, Channel 1 needs USB, mode not changed while transmitting",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.RefusedTransmitting));
        Assert.Equal("60 meter band, 5.332 megahertz, Channel 1 needs USB, mode not changed",
            Say(o.Arrival, o.Correction!, ModeRequestOutcome.RefusedNoSlice));
    }

    [Fact]
    public void A_band_jump_that_needs_no_correction_is_the_old_sentence()
    {
        var onChannel = FreqOutHandlers.BandJumpOutcome("60 meter", "5.332 megahertz", true, "US", 5_332_000, "USB");
        Assert.Null(onChannel.Correction);
        Assert.Equal("60 meter band, 5.332 megahertz", onChannel.Arrival);

        var elsewhere = FreqOutHandlers.BandJumpOutcome("20 meter", "14.200 megahertz", false, "US", 14_200_000, "LSB");
        Assert.Null(elsewhere.Correction);
        Assert.Equal("20 meter band, 14.200 megahertz", elsewhere.Arrival);
    }
}
