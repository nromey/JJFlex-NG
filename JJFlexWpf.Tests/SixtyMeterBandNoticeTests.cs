using System;
using System.IO;
using HamBands;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Classic tuning said "Entering 60 meter band" on steps made inside 60 m.
/// </summary>
/// <remarks>
/// <para><b>What the harness saw, and why.</b> Alt+Up jumped to 40 m (the band
/// jump remembers 40 m), Alt+Shift+Down then stopped on a 60 m channel (which
/// tuned without telling the band notices), and the next Control+Up inside 60 m
/// compared 60 m against the remembered 40 m and said "Entering 60 meter band".
/// The frequency readout, an interrupt, then cut it and the speech arbiter
/// replayed it 600 ms later, which is why it was heard twice, around the
/// frequency. A step now judges from the frequency it started on.</para>
/// <para>Pure decisions and the source only: no window is built, and every
/// test asserts it runs on an MTA thread, where WPF cannot build one.</para>
/// </remarks>
public sealed class SixtyMeterBandNoticeTests
{
    private static void AssertWindowless() => NoiseReductionPerChannelTests.AssertWindowless();

    [Fact]
    public void A_step_between_60_meter_channels_is_not_a_band_change_even_with_another_band_remembered()
    {
        AssertWindowless();
        // The harness's own numbers: channel 3 (5.3585 MHz), Control+Up by 5 kHz,
        // with 40 m remembered from the band jump before the channel stop.
        var before = FreqOutHandlers.BandBefore(5_358_500, Bands.BandNames.m40);
        var after = Bands.Query(5_363_500UL)?.Band;
        Assert.Equal(Bands.BandNames.m60, before);
        Assert.Equal(Bands.BandNames.m60, after);
        Assert.False(FreqOutHandlers.SaysBandEntry(before, after));
    }

    [Fact]
    public void Leaving_the_channels_for_the_digital_segment_and_back_stays_inside_60_meters()
    {
        AssertWindowless();
        // Channel 1, the digital segment and channel 5: one band for Bands.Query.
        foreach (var (from, to) in new (ulong, ulong)[]
                 { (5_332_000, 5_337_000), (5_348_000, 5_353_000), (5_405_000, 5_410_000), (5_356_500, 5_351_500) })
        {
            Assert.False(FreqOutHandlers.SaysBandEntry(
                FreqOutHandlers.BandBefore(from, Bands.BandNames.m17), Bands.Query(to)?.Band));
        }
    }

    [Fact]
    public void A_real_band_change_still_says_it_once()
    {
        AssertWindowless();
        // Positive control: from 40 m to 60 m is a band change, whether the
        // starting frequency is known or only remembered.
        Assert.True(FreqOutHandlers.SaysBandEntry(
            FreqOutHandlers.BandBefore(7_150_000, null), Bands.Query(5_363_500UL)?.Band));
        Assert.True(FreqOutHandlers.SaysBandEntry(
            FreqOutHandlers.BandBefore(null, Bands.BandNames.m40), Bands.Query(5_363_500UL)?.Band));
    }

    [Fact]
    public void Leaving_a_band_for_no_band_says_nothing_as_before()
    {
        AssertWindowless();
        Assert.False(FreqOutHandlers.SaysBandEntry(Bands.BandNames.m40, null));
        Assert.False(FreqOutHandlers.SaysBandEntry(null, Bands.BandNames.m60));
    }

    [Fact]
    public void Without_a_starting_frequency_the_remembered_band_is_used()
    {
        AssertWindowless();
        Assert.Equal(Bands.BandNames.m20, FreqOutHandlers.BandBefore(null, Bands.BandNames.m20));
        Assert.Null(FreqOutHandlers.BandBefore(null, null));
    }

    [Fact]
    public void A_tuning_step_hands_its_starting_frequency_to_the_band_notice()
    {
        AssertWindowless();
        string text = Source("JJFlexWpf", "FreqOutHandlers.cs");
        int tune = text.IndexOf("private void TuneFreq(ulong delta)", StringComparison.Ordinal);
        Assert.True(tune > 0);
        int end = text.IndexOf("\n    }", tune, StringComparison.Ordinal);
        string body = text.Substring(tune, end - tune);
        Assert.Contains("ulong current = GetRXFrequency();", body, StringComparison.Ordinal);
        Assert.Contains("CheckBandBoundary(newFreq, fromHz: current);", body, StringComparison.Ordinal);

        int check = text.IndexOf("public void CheckBandBoundary(ulong newFreq, ulong? fromHz = null)", StringComparison.Ordinal);
        Assert.True(check > 0);
        string checkBody = text.Substring(check, 1200);
        Assert.Contains("BandBefore(fromHz, _lastBand)", checkBody, StringComparison.Ordinal);
        Assert.Contains("SaysBandEntry(_lastBand, newBand)", checkBody, StringComparison.Ordinal);
    }

    [Fact]
    public void A_60_meter_channel_stop_moves_the_band_memory_with_it()
    {
        AssertWindowless();
        string text = Source("JJFlexWpf", "MainWindow.xaml.cs");
        int nav = text.IndexOf("public void SixtyMeterChannelNavigate(int direction)", StringComparison.Ordinal);
        Assert.True(nav > 0);
        string body = text.Substring(nav, 2500);
        int tune = body.IndexOf("RigControl.Frequency = stop.FrequencyHz;", StringComparison.Ordinal);
        int note = body.IndexOf("_freqOutHandlers?.InitializeBandTracking(stop.FrequencyHz);", StringComparison.Ordinal);
        Assert.True(tune > 0 && note > tune, "a 60 m channel stop tunes without telling the band notices");
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string[] all = new string[parts.Length + 1];
        all[0] = dir!.FullName;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return File.ReadAllText(Path.Combine(all));
    }
}
