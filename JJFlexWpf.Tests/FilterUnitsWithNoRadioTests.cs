using System.IO;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #979 repair one: the switch for the word "hertz" on spoken filter widths is
/// a setting that needs no radio, so it is kept and applied with or without
/// one. It first lived in the push-to-talk settings, which exist only while a
/// radio is connected: with no radio Settings edited a throwaway, nothing saved
/// it, and the receipt then blamed push to talk. It now lives with the
/// operator's other speech preferences in the root audio settings, which every
/// Settings OK writes with a radio or without, and which decide it at connect.
/// </summary>
/// <remarks>
/// <b>Windowless and silent.</b> MTA thread throughout; only config objects,
/// files in a temporary folder, and the source text.
/// </remarks>
public sealed class FilterUnitsWithNoRadioTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-filterunits-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void It_starts_on_for_a_new_operator_and_for_a_settings_file_written_before_it_existed()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(new AudioOutputConfig().FilterWidthUnits, "the switch must start on (ruled 2026-11-11 11:58)");
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "audioConfig.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><AudioOutputConfig><CwSpeedWpm>27</CwSpeedWpm></AudioOutputConfig>");
            var existing = AudioOutputConfig.Load(dir);
            Assert.Equal(27, existing.CwSpeedWpm);   // positive control: the file was read, not defaulted
            Assert.True(existing.FilterWidthUnits);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Turned_off_before_the_first_connection_it_is_kept_through_Settings_and_a_restart()
    {
        // The path Settings OK takes with no radio: the dialog edits the root
        // audio settings and the menu persists the operator's list to root.
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir = TempDir();
        try
        {
            var chosen = AudioOutputConfig.Load(dir);
            Assert.True(chosen.FilterWidthUnits);
            chosen.FilterWidthUnits = false;
            AudioOutputConfig.PersistUserScope(chosen, dir);
            Assert.False(AudioOutputConfig.Load(dir).FilterWidthUnits);

            chosen.FilterWidthUnits = true;
            AudioOutputConfig.PersistUserScope(chosen, dir);
            Assert.True(AudioOutputConfig.Load(dir).FilterWidthUnits);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void At_connect_a_radios_older_copy_does_not_undo_it()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var root = new AudioOutputConfig { FilterWidthUnits = false };
        var perRadio = new AudioOutputConfig { FilterWidthUnits = true, CwSpeedWpm = 31 };
        perRadio.TakeOperatorChoicesFrom(root);
        Assert.False(perRadio.FilterWidthUnits);
        Assert.Equal(31, perRadio.CwSpeedWpm);   // nothing else is taken
    }

    [Fact]
    public void Settings_reads_and_writes_it_with_the_audio_settings_and_it_is_made_live_with_or_without_a_radio()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        // Not a push-to-talk setting: so a no-radio change to it can never be
        // reported as "Push-to-talk settings need a connected radio".
        Assert.Null(typeof(Radios.PttConfig).GetProperty("FilterWidthUnits"));

        string repo = Repo();
        string dialog = File.ReadAllText(Path.Combine(repo, "JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));
        Assert.Contains("FilterUnitsCheckbox.IsChecked = _audioConfig.FilterWidthUnits;", dialog);
        Assert.Contains("_audioConfig.FilterWidthUnits = FilterUnitsCheckbox.IsChecked != false;", dialog);

        string config = File.ReadAllText(Path.Combine(repo, "JJFlexWpf", "AudioOutputConfig.cs"));
        int apply = config.IndexOf("public void Apply()", System.StringComparison.Ordinal);
        int live = config.IndexOf("Radios.HertzSpeech.SpeakUnits = FilterWidthUnits;", System.StringComparison.Ordinal);
        Assert.True(apply > 0 && live > apply, "Apply does not make the switch live");
        Assert.Contains("rootConfig.FilterWidthUnits = chosen.FilterWidthUnits;", config);

        // At startup, before any connect, from root, as the other speech choices are.
        string main = File.ReadAllText(Path.Combine(repo, "JJFlexWpf", "MainWindow.xaml.cs"));
        Assert.Contains("Radios.HertzSpeech.SpeakUnits = userConfig.FilterWidthUnits;", main);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
