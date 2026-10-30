using System.Linq;
using System.IO;
using System.Xml.Serialization;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #855 step 4: the alert channel's output buffer as an operator setting.
/// Noel ruled yes on 2026-10-30, with the default chosen by measurement: the
/// countdown measurement that night found the alert channel's 100 ms by 2
/// buffers to be most of a 110 to 177 ms lag, and 30 ms by 3 to measure well.
/// </summary>
/// <remarks>
/// <b>Windowless and silent.</b> MTA thread throughout; the alert channel is
/// never opened (the player is not initialised), so setting the buffer only
/// records it, which is all these tests read.
/// </remarks>
public sealed class AlertBufferSettingTests
{
    private static AudioOutputConfig RoundTrip(AudioOutputConfig c)
    {
        var ser = new XmlSerializer(typeof(AudioOutputConfig));
        var sw = new StringWriter();
        ser.Serialize(sw, c);
        return (AudioOutputConfig)ser.Deserialize(new StringReader(sw.ToString()))!;
    }

    [Fact]
    public void The_default_is_the_measured_thirty_by_three_and_an_older_file_gets_it()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var fresh = new AudioOutputConfig();
        Assert.Equal(30, fresh.AlertBufferMs);
        Assert.Equal(3, fresh.AlertBufferCount);

        var older = (AudioOutputConfig)new XmlSerializer(typeof(AudioOutputConfig))
            .Deserialize(new StringReader("<AudioOutputConfig />"))!;
        Assert.Equal(30, older.AlertBufferMs);
        Assert.Equal(3, older.AlertBufferCount);
    }

    [Fact]
    public void The_buffer_is_saved_and_loaded_with_the_audio_settings()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var back = RoundTrip(new AudioOutputConfig { AlertBufferMs = 100, AlertBufferCount = 2 });
        Assert.Equal(100, back.AlertBufferMs);
        Assert.Equal(2, back.AlertBufferCount);
    }

    [Theory]
    [InlineData(30, 3, 30, 3)]      // the default stands
    [InlineData(20, 3, 20, 3)]      // the shortest measured
    [InlineData(100, 2, 100, 2)]    // the old setting
    [InlineData(5, 3, 20, 3)]       // under the floor: nothing below 20 ms was ever measured
    [InlineData(0, 0, 20, 3)]       // nonsense from a hand-edited file
    [InlineData(20, 2, 20, 3)]      // 40 ms in all is too little queued: a third buffer is added
    [InlineData(500, 9, 200, 4)]    // over the ceiling on both
    [InlineData(-30, -3, 20, 3)]
    public void A_buffer_that_could_stall_the_device_is_clamped_to_one_that_cannot(int ms, int count, int wantMs, int wantCount)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var (gotMs, gotCount) = EarconPlayer.ClampAlertBuffer(ms, count);
        Assert.Equal(wantMs, gotMs);
        Assert.Equal(wantCount, gotCount);
        Assert.True(gotMs * gotCount >= EarconPlayer.AlertBufferMinTotalMs);
    }

    [Fact]
    public void Setting_the_alert_buffer_clamps_it_and_capturing_the_settings_reads_it_back()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var before = EarconPlayer.AlertBuffer;
        try
        {
            EarconPlayer.SetAlertBuffer(50, 3);
            Assert.Equal((50, 3), EarconPlayer.AlertBuffer);
            EarconPlayer.SetAlertBuffer(1, 1);
            Assert.Equal((20, 3), EarconPlayer.AlertBuffer);

            var c = new AudioOutputConfig();
            EarconPlayer.SetAlertBuffer(100, 2);
            c.CaptureFromEngine();
            Assert.Equal(100, c.AlertBufferMs);
            Assert.Equal(2, c.AlertBufferCount);
        }
        finally
        {
            EarconPlayer.SetAlertBuffer(before.ms, before.count);
        }
    }

    [Fact]
    public void Every_offered_choice_is_one_the_clamp_keeps_and_names_its_whole_queue()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var presets = JJFlexWpf.Dialogs.SettingsDialog.AlertBufferPresets;
        Assert.Equal(4, presets.Length);
        Assert.Contains(presets, p => p.ms == EarconPlayer.DefaultAlertBufferMs && p.count == EarconPlayer.DefaultAlertBufferCount);
        Assert.Contains(presets, p => p.ms == 100 && p.count == 2);   // the old setting stays reachable
        foreach (var (ms, count, key) in presets)
        {
            Assert.Equal((ms, count), EarconPlayer.ClampAlertBuffer(ms, count));
            string label = Radios.Lexicon.Get(key, ("ms", ms * count));
            Assert.DoesNotContain("audio.", label);
            Assert.Contains((ms * count).ToString(System.Globalization.CultureInfo.InvariantCulture), label);
        }
        Assert.True(presets.Select(p => p.ms * p.count).SequenceEqual(presets.Select(p => p.ms * p.count).OrderBy(t => t)),
            "shortest first");
    }

    [Fact]
    public void The_meter_channel_keeps_its_own_buffer()
    {
        // Only the alert channel was measured and ruled. Meter tones are
        // continuous and nobody times them against speech.
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.Equal((100, 2), EarconPlayer.MeterBuffer);
    }
}
