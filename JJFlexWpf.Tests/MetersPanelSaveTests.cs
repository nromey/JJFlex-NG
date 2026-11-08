using System;
using System.IO;
using System.Text.RegularExpressions;
using JJFlexWpf.Controls;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The meters panel saves a meter the moment the operator changes it. Until
/// this was wired, <c>MetersPanel.SaveToConfig</c> had no caller, so a meter
/// added, deleted or edited in the panel reached disk only if a Settings OK or
/// a verbosity change happened to follow.
/// </summary>
/// <remarks>
/// <b>Windowless and silent.</b> MTA thread throughout; the save is run through
/// its static core with config objects and a temporary folder, and the wiring
/// is read from source. No panel is constructed.
/// </remarks>
public sealed class MetersPanelSaveTests : IDisposable
{
    // These run the save as it is after a connect has applied the saved
    // config. Before that, the save refuses (AudioConfigBeforeLoadTests).
    private readonly bool _wasLoaded = AudioOutputConfig.EngineHoldsSavedConfig;

    public MetersPanelSaveTests() => AudioOutputConfig.EngineHoldsSavedConfig = true;

    public void Dispose() => AudioOutputConfig.EngineHoldsSavedConfig = _wasLoaded;

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-meterssave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void With_a_radio_open_the_change_goes_into_the_live_config_and_is_saved()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir = TempDir();
        try
        {
            var live = new AudioOutputConfig();
            int saves = 0;

            bool saved = MetersPanel.PersistMeterChoices(
                () => live, () => saves++, dir, c => c.MeterSpeechIntervalSeconds = 7);

            Assert.True(saved);
            Assert.Equal(7, live.MeterSpeechIntervalSeconds);
            Assert.Equal(1, saves);
            // The live route does not also write the root file behind its back.
            Assert.False(File.Exists(Path.Combine(dir, "audioConfig.xml")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void With_no_radio_open_the_change_is_saved_into_the_root_file_and_the_rest_is_kept()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "audioConfig.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><AudioOutputConfig><CwSpeedWpm>31</CwSpeedWpm></AudioOutputConfig>");

            // The live source answers null with no radio powered on; the save
            // hook alone must not be enough to take the live route.
            int saves = 0;
            bool saved = MetersPanel.PersistMeterChoices(
                () => null, () => saves++, dir, c => c.MeterSpeechIntervalSeconds = 9);

            Assert.True(saved);
            Assert.Equal(0, saves);
            var reopened = AudioOutputConfig.Load(dir);
            Assert.Equal(9, reopened.MeterSpeechIntervalSeconds);
            Assert.Equal(31, reopened.CwSpeedWpm); // the rest of the root file as it was
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Before_any_connect_has_set_the_hooks_the_change_still_reaches_the_root_file()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir = TempDir();
        try
        {
            bool saved = MetersPanel.PersistMeterChoices(null, null, dir, c => c.MeterSpeechIntervalSeconds = 4);

            Assert.True(saved);
            Assert.Equal(4, AudioOutputConfig.Load(dir).MeterSpeechIntervalSeconds);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Every control in the panel that changes a meter, or one of the panel's
    /// own settings, saves. A handler added without the call is the defect
    /// this entry was filed for, one control at a time.
    /// </summary>
    [Theory]
    [InlineData("private void SourceCombo_SelectionChanged(")]
    [InlineData("private void VoiceCombo_SelectionChanged(")]
    [InlineData("private void ActivationCombo_SelectionChanged(")]
    [InlineData("private void PanSlider_ValueChanged(")]
    [InlineData("private void VolumeSlider_ValueChanged(")]
    [InlineData("private void PitchLowBox_LostFocus(")]
    [InlineData("private void PitchHighBox_LostFocus(")]
    [InlineData("private void SlotEnabledCheck_Changed(")]
    [InlineData("private void AddSlotButton_Click(")]
    [InlineData("private void DeleteSelectedSlot(")]
    [InlineData("private void SpeechIntervalBox_LostFocus(")]
    public void Each_change_handler_saves(string signature)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string body = MethodBody(PanelSource(), signature);
        Assert.Contains("PersistMeterChoices();", body);
    }

    [Fact]
    public void The_panels_own_three_switches_save_both_ways()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string body = MethodBody(PanelSource(), "private void WireGlobals(");
        Assert.Equal(6, Regex.Matches(body, @"PersistMeterChoices\(\);").Count);
    }

    [Fact]
    public void The_panel_save_leaves_the_meter_tones_switch_to_its_owners()
    {
        // Ctrl+M switches the tones on to open the panel; if the panel saved
        // that, every later launch would start with meter tones sounding.
        NoiseReductionPerChannelTests.AssertWindowless();
        string body = MethodBody(PanelSource(), "public void SaveToConfig(");
        Assert.Contains("config.Meters = MeterToneEngine.ExportDefinitions();", body); // positive control
        Assert.DoesNotContain("MeterTonesEnabled", body);
    }

    [Fact]
    public void The_live_route_is_offered_only_while_a_radio_is_powered_on()
    {
        // OpenParms and CurrentAudioConfig outlive a disconnect; saving that
        // older object whole would put back what Settings changed since.
        NoiseReductionPerChannelTests.AssertWindowless();
        string main = File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "MainWindow.xaml.cs"));
        int at = main.IndexOf("Controls.MetersPanel.AudioConfigSource =", StringComparison.Ordinal);
        Assert.True(at >= 0, "MainWindow sets the meters panel's live source");
        int end = main.IndexOf(';', at);
        Assert.Contains("_radioPowerOn", main.Substring(at, end - at));
        Assert.Contains("Controls.MetersPanel.AudioConfigSave =", main);
    }

    private static string PanelSource() =>
        File.ReadAllText(Path.Combine(Repo(), "JJFlexWpf", "Controls", "MetersPanel.xaml.cs"));

    private static string MethodBody(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature + " was not found");
        int open = source.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced braces after " + signature);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
