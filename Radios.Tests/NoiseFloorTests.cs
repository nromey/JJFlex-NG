using System;
using Radios;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Sprint 49 Track S (#1019): the one conversion between the stored wet fraction
/// and the noise floor an operator sees, and the one source of every default.
/// </summary>
public sealed class NoiseFloorTests
{
    private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

    [Fact]
    public void Every_shown_step_round_trips_to_the_exact_stored_bits()
    {
        for (int floor = NoiseFloor.Min; floor <= NoiseFloor.Max; floor += NoiseFloor.Step)
        {
            float stored = NoiseFloor.ToStrength(floor);
            Assert.Equal(floor, NoiseFloor.FromStrength(stored));
            Assert.Equal(Bits(stored), Bits(NoiseFloor.ToStrength(NoiseFloor.FromStrength(stored))));
            // The way the Strength lever built a stored value before the flip:
            // the float nearest percent / 100. The flip must store the same bits.
            float before = (float)((100 - floor) / 100.0);
            Assert.Equal(Bits(before), Bits(stored));
        }
    }

    [Theory]
    [InlineData(0.8f, 20)]
    [InlineData(0.65f, 35)]
    [InlineData(0.7f, 30)]
    [InlineData(1f, 0)]
    [InlineData(0.9f, 10)]
    [InlineData(0f, 100)]
    public void The_stored_values_that_exist_today_show_their_floor_and_write_back_bit_for_bit(float stored, int floor)
    {
        Assert.Equal(floor, NoiseFloor.FromStrength(stored));
        Assert.Equal(Bits(stored), Bits(NoiseFloor.ToStrength(floor)));
    }

    [Fact]
    public void Out_of_range_and_non_finite_values_clamp_rather_than_throw()
    {
        Assert.Equal(0, NoiseFloor.FromStrength(1.5f));
        Assert.Equal(100, NoiseFloor.FromStrength(-0.2f));
        Assert.Equal(0, NoiseFloor.FromStrength(float.NaN));
        Assert.Equal(Bits(0f), Bits(NoiseFloor.ToStrength(250)));
        Assert.Equal(Bits(1f), Bits(NoiseFloor.ToStrength(-5)));
        Assert.Equal(Bits(1f), Bits(NoiseFloor.ToStrength(double.NaN)));
    }

    [Theory]
    [InlineData("rnnoise", DenoiseDirection.Receive, 10)]
    [InlineData("rnnoise", DenoiseDirection.Transmit, 20)]
    [InlineData("nr2", DenoiseDirection.Receive, 0)]
    [InlineData("nr4", DenoiseDirection.Receive, 0)]
    [InlineData("nnr", DenoiseDirection.Receive, 0)]
    [InlineData("deepfilternet3", DenoiseDirection.Receive, 0)]
    [InlineData("spectral_capture", DenoiseDirection.Receive, 0)]
    public void The_defaults_are_the_ruled_ones(string engine, DenoiseDirection direction, int floor)
    {
        // Ruled S3, Noel 2026-11-09 15:41: receive RNNoise 10, transmit RNNoise
        // 20 until he has listened on transmit, every other engine 0.
        Assert.Equal(floor, NoiseFloor.DefaultFloor(engine, direction));
        Assert.Equal(Bits(NoiseFloor.ToStrength(floor)), Bits(NoiseFloor.DefaultStrength(engine, direction)));
    }

    [Fact]
    public void The_transmit_profile_starts_from_the_one_source()
    {
        // TxConditioningSettings lives in Radios, which is why the one source does.
        Assert.Equal(Bits(NoiseFloor.DefaultStrength("rnnoise", DenoiseDirection.Transmit)), Bits(new TxConditioningSettings().NrStrength));
        Assert.Equal(Bits(0.8f), Bits(new TxConditioningSettings().NrStrength)); // transmit unchanged: floor 20
    }
}
