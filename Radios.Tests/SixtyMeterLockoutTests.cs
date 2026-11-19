using HamBands;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// #1171: with TX band lockout on, every 60 m frequency was refused for every
/// license class. The 60 m division in <c>HamBands/Bands.cs</c> read
/// <c>BandDivision(5300, 5500, ...)</c> while every other division is in
/// hertz, so <c>Bands.Query</c> found the band but no division covered a real
/// 60 m frequency.
/// </summary>
/// <remarks>
/// <see cref="Allowed"/> calls <see cref="TransmitBandLockout.Allows"/>, the
/// judgement <c>FreqOutHandlers.CanTransmitHere</c> itself calls since #1173,
/// with no mode, which is the arithmetic the lockout always used: the band by
/// the signal's low edge, then that band's divisions for the license, then the
/// whole signal inside one of them. Until #1173 this was a retyped copy that
/// could drift. The channel and segment rules are separate work (#1172,
/// #1161); SixtyMeterChannels is not touched.
/// </remarks>
public sealed class SixtyMeterLockoutTests
{
    /// <summary>USB with a 100 to 2,800 Hz filter, as a 60 m channel is worked.</summary>
    private static bool UsbAllowed(ulong dialHz, Bands.Licenses license) => Allowed(dialHz, 100, 2800, license);

    private static bool Allowed(ulong freq, int filterLow, int filterHigh, Bands.Licenses license) =>
        TransmitBandLockout.Allows(freq, null, filterLow, filterHigh, license);

    [Theory]
    [InlineData(Bands.Licenses.general)]
    [InlineData(Bands.Licenses.advanced)]
    [InlineData(Bands.Licenses.extra)]
    public void A_General_or_higher_on_a_60_metre_channel_may_transmit(Bands.Licenses license) =>
        Assert.True(UsbAllowed(5_332_000, license));

    [Theory]
    [InlineData(Bands.Licenses.technition)]
    [InlineData(Bands.Licenses.novice)]
    public void A_Technician_or_Novice_on_60_metres_is_refused(Bands.Licenses license) =>
        Assert.False(UsbAllowed(5_332_000, license));

    [Fact]
    public void The_60_metre_division_is_in_hertz_like_every_other()
    {
        var sixty = Bands.Query(Bands.BandNames.m60, Bands.Licenses.general);
        Assert.Equal(Bands.BandNames.m60, Bands.Query(5_332_000UL)!.Band);
        Assert.Contains(sixty.Divisions, d => d.Low == 5_300_000UL && d.High == 5_500_000UL);
    }

    [Fact]
    public void Forty_metres_is_unchanged()
    {
        // General phone at 7.200 LSB; Technician refused there, and on CW at 7.050.
        Assert.True(Allowed(7_200_000, -2800, -100, Bands.Licenses.general));
        Assert.False(Allowed(7_200_000, -2800, -100, Bands.Licenses.technition));
        Assert.True(Allowed(7_050_000, -300, 300, Bands.Licenses.technition));
    }
}
