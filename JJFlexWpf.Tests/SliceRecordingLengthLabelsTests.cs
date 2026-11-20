using System;
using System.Globalization;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The slice recording's length on Home's recorder strip, its Play button and
/// the Slice menu reads the way Play takes and the overwrite warning read it:
/// tenths below ten seconds, and a length above zero never as zero. Until the
/// repair these labels floored it, so a 0.1 second recording read "Play 0
/// second recording" on Home while Play takes said "0.1".
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread, an unconnected <see cref="FlexBase"/>,
/// and the radio's reported length stood in for by
/// <see cref="RecorderControls.SecondsOverride"/>, for this class's own rig
/// only. No window is built and nothing is spoken; the labels are strings.
/// </remarks>
public sealed class SliceRecordingLengthLabelsTests : IDisposable
{
    private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
    private readonly CultureInfo _was = CultureInfo.CurrentCulture;
    private double? _seconds;

    public SliceRecordingLengthLabelsTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        Lexicon.Load(Lexicon.Partitions);
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        RecorderControls.SecondsOverride = (rig, target) =>
            ReferenceEquals(rig, _rig) ? _seconds : rig.SliceRecordingSeconds(target);
    }

    public void Dispose()
    {
        RecorderControls.SecondsOverride = null;
        CultureInfo.CurrentCulture = _was;
        try { _rig.Dispose(); } catch { }
    }

    private static string Seconds(string n) => Lexicon.Get("audio.recorder.seconds", ("seconds", n));

    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(0.04, "0.1")]   // above zero never reads as zero
    [InlineData(4.9, "4.9")]    // floored, this read 4
    [InlineData(9.94, "9.9")]
    [InlineData(30.0, "30")]
    public void Home_the_menu_and_the_Play_button_say_the_length_in_tenths_below_ten_seconds(double seconds, string spoken)
    {
        _seconds = seconds;
        var target = SliceRecorderTarget.ActiveSlice;

        Assert.Equal(Seconds(spoken), RecorderControls.RecordingLength(_rig, target));
        Assert.Equal(Lexicon.Get("audio.recorder.play_strip", ("seconds", spoken)),
            RecorderControls.SliceStripPlayLabel(_rig, target));
        Assert.Equal(Lexicon.Get("audio.recorder.play_length", ("length", Seconds(spoken))),
            RecorderControls.SlicePlayLabel(_rig, target));
        Assert.Equal(Lexicon.Get("audio.recorder.length_recorded", ("length", Seconds(spoken))),
            RecorderControls.RecordingSummary(_rig, target));
    }

    /// <summary>The bug's own example: no label says zero for a recording that exists.</summary>
    [Fact]
    public void A_tenth_of_a_second_recording_never_reads_as_zero_on_Home()
    {
        _seconds = 0.1;
        var target = SliceRecorderTarget.ActiveSlice;
        Assert.NotEqual(Lexicon.Get("audio.recorder.play_strip", ("seconds", "0")),
            RecorderControls.SliceStripPlayLabel(_rig, target));
        Assert.NotEqual(Seconds("0"), RecorderControls.RecordingLength(_rig, target));
    }

    /// <summary>
    /// Positive control: with no recording reported, the labels fall back to
    /// their plain forms, so the cases above are reading the seam and not a
    /// fixed string.
    /// </summary>
    [Fact]
    public void With_no_length_reported_the_labels_carry_no_number()
    {
        _seconds = null;
        var target = SliceRecorderTarget.ActiveSlice;
        Assert.Null(RecorderControls.RecordingLength(_rig, target));
        Assert.Equal(Lexicon.Get("audio.recorder.play_button"), RecorderControls.SliceStripPlayLabel(_rig, target));
        Assert.Equal(Lexicon.Get("audio.recorder.play_button"), RecorderControls.SlicePlayLabel(_rig, target));
    }
}
