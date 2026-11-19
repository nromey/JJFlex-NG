using System;
using System.IO;
using HamBands;
using Xunit;
using static Radios.Tests.ListenbackRoundTwoTests;

namespace Radios.Tests;

/// <summary>
/// #1173: the TX band lockout judges the transmit slice, with that slice's own
/// filter signed per mode, not the receive frequency and the selected slice's
/// filter. On a split the two differ, so the lockout used to let a transmit
/// slice outside the licence key, and refuse one inside it.
/// </summary>
/// <remarks>
/// Tests <see cref="TransmitBandLockout"/>, the pure judgement
/// <c>FreqOutHandlers.CanTransmitHere</c> calls, and the FlexBase reading of
/// the transmit slice on the vendor radio fixture. Not refusals only: a split
/// whose transmit slice is in privileges now keys where it used to refuse, so
/// both directions are pinned.
/// </remarks>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class TransmitBandLockoutTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(TransmitBandLockoutTests));
    public void Dispose() => _scope.Dispose();

    // 40 m for a General: CW and data 7.025 to 7.125 MHz, phone 7.175 to 7.300 MHz.
    private const ulong InPhone = 7_200_000;    // an LSB signal here is in a General's phone segment
    private const ulong OutOfPhone = 7_150_000; // and here it is in the gap a General may not use

    private static TransmitSliceReading Lsb(ulong hz) => new(hz, "LSB", -2800, -100);

    // ---- the mode table ----

    [Theory]
    [InlineData("LSB", -2800, -100, 7_197_200, 7_199_900)]   // FlexLib's signed edges: as before
    [InlineData("LSB", 100, 2800, 7_197_200, 7_199_900)]     // written without the sign: still below
    [InlineData("DIGL", -3000, 0, 7_197_000, 7_200_000)]
    [InlineData("DIGL", 0, 3000, 7_197_000, 7_200_000)]
    [InlineData("USB", 100, 2800, 7_200_100, 7_202_800)]
    [InlineData("USB", -2800, -100, 7_200_100, 7_202_800)]   // written below zero: still above
    [InlineData("DIGU", 0, 3000, 7_200_000, 7_203_000)]
    [InlineData("CW", -300, 300, 7_199_700, 7_200_300)]
    [InlineData("AM", -5000, 5000, 7_195_000, 7_205_000)]
    [InlineData("FM", -8000, 8000, 7_192_000, 7_208_000)]
    [InlineData("RTTY", -285, 115, 7_199_715, 7_200_115)]    // the rest: carrier plus each edge, as before
    [InlineData("lsb", -2800, -100, 7_197_200, 7_199_900)]   // the mode's case does not matter
    public void Each_mode_puts_the_signal_on_its_own_side(string mode, int low, int high, long expectLow, long expectHigh) =>
        Assert.Equal((expectLow, expectHigh), TransmitBandLockout.SignalExtent(7_200_000, mode, low, high));

    [Fact]
    public void No_mode_is_the_arithmetic_the_lockout_always_used() =>
        Assert.Equal((7_197_200L, 7_199_900L), TransmitBandLockout.SignalExtent(7_200_000, null, -2800, -100));

    [Fact]
    public void The_extent_is_clamped_at_zero() =>
        Assert.Equal((0L, 0L), TransmitBandLockout.SignalExtent(0, "LSB", -2800, -100));

    [Fact]
    public void A_lower_sideband_just_above_a_segment_edge_is_refused_whichever_way_its_edges_are_written()
    {
        // 7.176 LSB puts the signal at 7.1732 to 7.1759 MHz: under a General's
        // 7.175 phone edge. Judged on the wrong side it would read 7.1761 to
        // 7.1788 and be allowed: the reason the radio's unsigned transmit
        // filter must never be used here.
        Assert.False(TransmitBandLockout.Allows(7_176_000, "LSB", -2800, -100, Bands.Licenses.general));
        Assert.False(TransmitBandLockout.Allows(7_176_000, "LSB", 100, 2800, Bands.Licenses.general));
        Assert.True(TransmitBandLockout.Allows(7_176_000, "USB", 100, 2800, Bands.Licenses.general),
            "positive control: the mirror image of that signal is allowed");
    }

    // ---- which slice is judged ----

    [Fact]
    public void A_split_whose_transmit_slice_is_outside_the_licence_is_refused()
    {
        // Receive in the phone segment, transmit in the gap: this used to key.
        bool allowed = TransmitBandLockout.Judge(Lsb(OutOfPhone), InPhone, -2800, -100, Bands.Licenses.general, out bool judgedSelected);
        Assert.False(allowed);
        Assert.False(judgedSelected);
        Assert.True(TransmitBandLockout.Allows(InPhone, null, -2800, -100, Bands.Licenses.general),
            "positive control: the receive slice alone is allowed, which is what used to be judged");
    }

    [Fact]
    public void A_split_whose_transmit_slice_is_inside_the_licence_keys()
    {
        // Receive in the gap, transmit in the phone segment: this used to refuse.
        bool allowed = TransmitBandLockout.Judge(Lsb(InPhone), OutOfPhone, -2800, -100, Bands.Licenses.general, out bool judgedSelected);
        Assert.True(allowed);
        Assert.False(judgedSelected);
        Assert.False(TransmitBandLockout.Allows(OutOfPhone, null, -2800, -100, Bands.Licenses.general),
            "positive control: the receive slice alone is refused, which is what used to be judged");
    }

    [Fact]
    public void The_transmit_slice_s_own_filter_is_judged_not_the_selected_slice_s()
    {
        // Transmit 7.176 LSB with its own 2.7 kHz filter reaches under the
        // edge; the selected slice's narrow filter would not.
        var transmit = new TransmitSliceReading(7_176_000, "LSB", -2800, -100);
        Assert.False(TransmitBandLockout.Judge(transmit, 7_176_000, -900, -100, Bands.Licenses.general, out _));
    }

    [Fact]
    public void With_no_transmit_slice_of_ours_the_selected_slice_is_judged_as_before()
    {
        Assert.True(TransmitBandLockout.Judge(null, InPhone, -2800, -100, Bands.Licenses.general, out bool judgedSelected));
        Assert.True(judgedSelected);
        Assert.False(TransmitBandLockout.Judge(null, OutOfPhone, -2800, -100, Bands.Licenses.general, out _));
    }

    [Fact]
    public void A_transmit_frequency_of_zero_is_never_a_refusal_it_judges_the_selected_slice()
    {
        var zero = new TransmitSliceReading(0, "LSB", -2800, -100);
        Assert.True(TransmitBandLockout.Judge(zero, InPhone, -2800, -100, Bands.Licenses.general, out bool judgedSelected));
        Assert.True(judgedSelected);
    }

    // ---- the reading, on the fake radio ----

    [Fact]
    public void The_reading_is_the_designated_transmit_slice_s_frequency_mode_and_own_filter()
    {
        using var c = new RadioCase();
        c.Add(0, 7.15);
        c.F.Vendor.Status("slice 1 tx=0");
        c.F.Vendor.Status("slice 0 mode=LSB filter_lo=-2700 filter_hi=-150 tx=1");
        c.F.Vendor.Status("slice 1 mode=USB filter_lo=100 filter_hi=2800");

        var reading = c.F.Rig.TransmitSliceSignal();

        Assert.NotNull(reading);
        Assert.Equal(7_150_000UL, reading!.Value.Hz);
        Assert.Equal("LSB", reading.Value.Mode);
        Assert.Equal(-2700, reading.Value.FilterLow);
        Assert.Equal(-150, reading.Value.FilterHigh);
    }

    [Fact]
    public void With_no_slice_of_ours_designated_for_transmit_there_is_no_reading()
    {
        using var c = new RadioCase();
        Assert.NotNull(c.F.Rig.TransmitSliceSignal());   // positive control: the fixture's slice transmits
        c.F.Vendor.Status("slice 1 tx=0");
        Assert.Null(c.F.Rig.TransmitSliceSignal());
    }

    // ---- the wiring ----

    [Fact]
    public void CanTransmitHere_judges_the_transmit_slice_through_the_one_judgement()
    {
        string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "FreqOutHandlers.cs")).Replace("\r\n", "\n");
        int start = src.IndexOf("public bool CanTransmitHere()", StringComparison.Ordinal);
        Assert.True(start > 0);
        int end = src.IndexOf("public string GetTxLockoutMessage()", start, StringComparison.Ordinal);
        string body = src.Substring(start, end - start);
        Assert.Contains("Rig.TransmitSliceSignal()", body, StringComparison.Ordinal);
        Assert.Contains("TransmitBandLockout.Judge(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Bands.Query", body, StringComparison.Ordinal);   // no second copy of the arithmetic
        Assert.DoesNotContain("TXFilterLow", body, StringComparison.Ordinal);   // never the radio's unsigned transmit filter
    }
}
