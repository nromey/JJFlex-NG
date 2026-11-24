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

    // ---- #1194: XIT moves the transmitted frequency ----

    /// <summary>
    /// The fixture's transmit slice moved to slice 0 at <paramref name="mhz"/>
    /// in <paramref name="mode"/>, with XIT as given; returns the lockout's
    /// verdict for a General on that slice's reading.
    /// </summary>
    private static (bool Allowed, ulong Hz) JudgeWithXit(double mhz, string mode, int low, int high, bool xitOn, int xitHz)
    {
        using var c = new RadioCase();
        c.Add(0, mhz);
        c.F.Vendor.Status("slice 1 tx=0");
        c.F.Vendor.Status("slice 0 mode=" + mode + " filter_lo=" + low + " filter_hi=" + high
            + " tx=1 xit_on=" + (xitOn ? "1" : "0") + " xit_freq=" + xitHz);
        var reading = c.F.Rig.TransmitSliceSignal();
        Assert.NotNull(reading);
        bool allowed = TransmitBandLockout.Judge(reading, 0, 0, 0, Bands.Licenses.general, out bool judgedSelected);
        Assert.False(judgedSelected);
        return (allowed, reading!.Value.Hz);
    }

    [Fact]
    public void A_negative_XIT_that_carries_the_signal_under_a_licence_edge_is_refused()
    {
        // 7.178 LSB is 7.1752 to 7.1779 MHz, inside a General's phone; XIT
        // -2,000 Hz transmits at 7.176, whose signal reaches under 7.175.
        var on = JudgeWithXit(7.178, "LSB", -2800, -100, xitOn: true, xitHz: -2000);
        Assert.Equal(7_176_000UL, on.Hz);
        Assert.False(on.Allowed);
    }

    [Fact]
    public void The_same_offset_with_XIT_off_is_allowed()
    {
        var off = JudgeWithXit(7.178, "LSB", -2800, -100, xitOn: false, xitHz: -2000);
        Assert.Equal(7_178_000UL, off.Hz);
        Assert.True(off.Allowed);
    }

    [Fact]
    public void A_positive_XIT_that_carries_the_signal_over_a_licence_edge_is_refused()
    {
        // 7.297 USB is 7.2971 to 7.2998 MHz, inside; XIT +2,000 Hz puts its top over 7.300.
        Assert.True(JudgeWithXit(7.297, "USB", 100, 2800, xitOn: false, xitHz: 2000).Allowed);
        var on = JudgeWithXit(7.297, "USB", 100, 2800, xitOn: true, xitHz: 2000);
        Assert.Equal(7_299_000UL, on.Hz);
        Assert.False(on.Allowed);
    }

    [Fact]
    public void An_XIT_that_carries_the_signal_back_inside_the_licence_keys()
    {
        // 7.174 LSB is outside a General's phone; XIT +4,000 Hz transmits at 7.178, inside.
        Assert.False(JudgeWithXit(7.174, "LSB", -2800, -100, xitOn: false, xitHz: 4000).Allowed);
        Assert.True(JudgeWithXit(7.174, "LSB", -2800, -100, xitOn: true, xitHz: 4000).Allowed);
    }

    [Fact]
    public void The_XIT_offset_is_the_slice_s_XIT_frequency_only_when_XIT_is_on()
    {
        Assert.Equal(-2000L, FlexBase.XitOffsetHz(true, -2000));
        Assert.Equal(99_999L, FlexBase.XitOffsetHz(true, 99_999));
        Assert.Equal(0L, FlexBase.XitOffsetHz(false, -2000));
    }

    // ---- #1207: the slice's transmit offset moves the transmitted frequency ----

    /// <summary>
    /// The fixture's transmit slice moved to slice 0 at <paramref name="mhz"/>
    /// in <paramref name="mode"/>, with the slice's status as given (a
    /// <c>tx_offset_freq</c> and anything else); returns the lockout's verdict
    /// for a General on that slice's reading, and the frequency it judged.
    /// </summary>
    private static (bool Allowed, ulong Hz) JudgeWithStatus(double mhz, string mode, int low, int high, string status)
    {
        using var c = new RadioCase();
        c.Add(0, mhz);
        c.F.Vendor.Status("slice 1 tx=0");
        c.F.Vendor.Status("slice 0 mode=" + mode + " filter_lo=" + low + " filter_hi=" + high + " tx=1 " + status);
        var reading = c.F.Rig.TransmitSliceSignal();
        Assert.NotNull(reading);
        bool allowed = TransmitBandLockout.Judge(reading, 0, 0, 0, Bands.Licenses.general, out bool judgedSelected);
        Assert.False(judgedSelected);
        return (allowed, reading!.Value.Hz);
    }

    private static (bool Allowed, ulong Hz) JudgeFmWithOffset(double mhz, string txOffsetMhz) =>
        JudgeWithStatus(mhz, "FM", -8000, 8000, "tx_offset_freq=" + txOffsetMhz);

    [Fact]
    public void A_simplex_FM_slice_is_judged_at_its_own_frequency()
    {
        var simplex = JudgeFmWithOffset(29.6, "0.000000");
        Assert.Equal(29_600_000UL, simplex.Hz);
        Assert.True(simplex.Allowed);
    }

    [Fact]
    public void A_plus_transmit_offset_is_judged_above_the_slice()
    {
        var plus = JudgeFmWithOffset(29.52, "0.100000");
        Assert.Equal(29_620_000UL, plus.Hz);
        Assert.True(plus.Allowed);
    }

    [Fact]
    public void A_minus_transmit_offset_is_judged_below_the_slice()
    {
        var minus = JudgeFmWithOffset(29.62, "-0.100000");
        Assert.Equal(29_520_000UL, minus.Hz);
        Assert.True(minus.Allowed);
    }

    [Fact]
    public void A_plus_offset_that_carries_the_transmitter_over_the_band_edge_is_refused()
    {
        // 29.680 FM is 29.672 to 29.688 MHz, inside 10 m; +100 kHz transmits
        // at 29.780, above the band's 29.700 edge.
        Assert.True(JudgeFmWithOffset(29.68, "0.000000").Allowed, "positive control: simplex there is allowed");
        var over = JudgeFmWithOffset(29.68, "0.100000");
        Assert.Equal(29_780_000UL, over.Hz);
        Assert.False(over.Allowed);
    }

    [Fact]
    public void A_minus_offset_that_carries_the_transmitter_under_the_band_edge_is_refused()
    {
        // 28.050 FM is inside 10 m (the lockout judges frequency, not mode);
        // -100 kHz transmits at 27.950, under the band's 28.000 edge.
        Assert.True(JudgeFmWithOffset(28.05, "0.000000").Allowed, "positive control: simplex there is allowed");
        var under = JudgeFmWithOffset(28.05, "-0.100000");
        Assert.Equal(27_950_000UL, under.Hz);
        Assert.False(under.Allowed);
    }

    [Fact]
    public void An_offset_that_carries_the_transmitter_back_inside_the_band_keys()
    {
        // 29.750 is above 10 m and refused simplex; -100 kHz transmits at 29.650, inside.
        Assert.False(JudgeFmWithOffset(29.75, "0.000000").Allowed);
        var inside = JudgeFmWithOffset(29.75, "-0.100000");
        Assert.Equal(29_650_000UL, inside.Hz);
        Assert.True(inside.Allowed);
    }

    [Fact]
    public void A_transmit_offset_on_a_slice_that_is_not_FM_is_judged_too()
    {
        // Applied in every mode (FlexLib carries the field without a mode; the
        // bench measured FM only). 14.230 USB is inside a General's phone;
        // -10 kHz transmits at 14.220, under the 14.225 edge.
        Assert.True(JudgeWithStatus(14.23, "USB", 100, 2800, "tx_offset_freq=0.000000").Allowed,
            "positive control: with no offset the slice is allowed");
        var usb = JudgeWithStatus(14.23, "USB", 100, 2800, "tx_offset_freq=-0.010000");
        Assert.Equal(14_220_000UL, usb.Hz);
        Assert.False(usb.Allowed);
    }

    [Fact]
    public void The_repeater_offset_and_its_direction_move_nothing_on_their_own()
    {
        // Measured on an 8600: only tx_offset_freq moves the transmit frequency.
        var stored = JudgeWithStatus(29.68, "FM", -8000, 8000,
            "fm_repeater_offset_freq=0.100000 repeater_offset_dir=UP tx_offset_freq=0.000000");
        Assert.Equal(29_680_000UL, stored.Hz);
        Assert.True(stored.Allowed);
    }

    [Fact]
    public void XIT_and_a_transmit_offset_together_are_both_added()
    {
        // How the radio combines the two is not established; both are added.
        var both = JudgeWithStatus(14.25, "USB", 100, 2800, "xit_on=1 xit_freq=1000 tx_offset_freq=0.050000");
        Assert.Equal(14_301_000UL, both.Hz);
    }

    [Fact]
    public void An_offset_that_carries_the_transmitter_below_zero_is_refused_not_judged_on_the_selected_slice()
    {
        // -20 MHz on a 14.230 slice: no frequency the radio could key lawfully.
        // Judged at 1 Hz, outside every band; never a null reading, which
        // would hand the judgement to the selected slice.
        var below = JudgeWithStatus(14.23, "USB", 100, 2800, "tx_offset_freq=-20.000000");
        Assert.Equal(1UL, below.Hz);
        Assert.False(below.Allowed);
    }

    [Theory]
    [InlineData(0.05, 50_000L)]
    [InlineData(-0.05, -50_000L)]
    [InlineData(0.6, 600_000L)]
    [InlineData(-0.6, -600_000L)]
    [InlineData(5.0, 5_000_000L)]
    [InlineData(0.000001, 1L)]
    [InlineData(0.0, 0L)]
    public void The_transmit_offset_is_the_slice_s_signed_megahertz_in_hertz(double mhz, long hz) =>
        Assert.Equal(hz, FlexBase.TxOffsetHz(mhz));

    [Fact]
    public void A_transmit_offset_that_is_not_a_number_or_absurdly_large_cannot_wrap()
    {
        Assert.Equal(0L, FlexBase.TxOffsetHz(double.NaN));
        Assert.Equal(0L, FlexBase.TxOffsetHz(double.PositiveInfinity));
        Assert.Equal(1_000_000_000_000L, FlexBase.TxOffsetHz(1e12));
        Assert.Equal(-1_000_000_000_000L, FlexBase.TxOffsetHz(-1e12));
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
