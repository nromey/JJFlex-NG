#nullable enable
using System;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes, round two, part 4e: "Save each playback as a take" (DRAFT)
/// is a per-radio check setting, saved alone, and its box is shown for the
/// method, never hidden for the connection. Since #999 round 3 (Noel,
/// 2026-11-08: "Turn on everywhere") it is ON by default, for radios whose
/// config already saved it off too, once only.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class UnifiedTakesSettingTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(UnifiedTakesSettingTests));
    public void Dispose() => _scope.Dispose();

    private static string NewSerial() =>
        "8640-" + Random.Shared.Next(1000, 9999) + "-" + Random.Shared.Next(1000, 9999) + "-" + Random.Shared.Next(1000, 9999);

    private string ConfigPath(string serial) => _scope.PathTo("radios", serial, "config.xml");

    /// <summary>
    /// A config file as a build before #999 round 3 wrote it: every field, with
    /// this setting under its OLD element name. Made by serializing a whole
    /// config and putting the old element back, so the file carries everything
    /// an older build's save did, not a hand-picked few fields.
    /// </summary>
    private void WriteOlderBuildConfig(string serial, bool oldValue)
    {
        string text;
        using (var bytes = new MemoryStream())
        {
            new XmlSerializer(typeof(RadioConfig)).Serialize(bytes, new RadioConfig { RadioId = serial });
            text = Encoding.UTF8.GetString(bytes.ToArray());
        }
        string current = "<" + RadioConfig.SavePlaybackTakesElement + ">true</" + RadioConfig.SavePlaybackTakesElement + ">";
        Assert.Contains(current, text);
        string old = oldValue ? "true" : "false";
        text = text.Replace(current, "<AudioCheckSavePlaybackTakes>" + old + "</AudioCheckSavePlaybackTakes>", StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath(serial))!);
        File.WriteAllText(ConfigPath(serial), text);
    }

    [Fact]
    public void A_new_radio_reads_on()
    {
        string serial = NewSerial();
        Assert.False(File.Exists(ConfigPath(serial)));
        Assert.True(RadioConfig.LoadForRadio(serial).AudioCheckSavePlaybackTakes);
        Assert.True(new RadioConfig().AudioCheckSavePlaybackTakes);
    }

    [Fact]
    public void A_config_an_older_build_saved_off_reads_on_and_the_next_save_writes_it_on()
    {
        string serial = NewSerial();
        WriteOlderBuildConfig(serial, oldValue: false);
        Assert.Contains("<AudioCheckSavePlaybackTakes>false</AudioCheckSavePlaybackTakes>", File.ReadAllText(ConfigPath(serial)));

        var cfg = RadioConfig.LoadForRadio(serial);
        Assert.Null(cfg.ReadFailure); // the old element is skipped, never a read failure (a read failure refuses every save)
        Assert.True(cfg.AudioCheckSavePlaybackTakes);

        // Any later save writes the new element on, and drops the old one.
        Assert.True(RadioConfig.RecordAudioCheckPref(serial, AudioCheckPref.LowPowerWatts,
            AudioCheckListenMethods.Monitor, AudioCheckPowerModes.LowPower, 6, false));
        string written = File.ReadAllText(ConfigPath(serial));
        Assert.Contains("<" + RadioConfig.SavePlaybackTakesElement + ">true</" + RadioConfig.SavePlaybackTakesElement + ">", written);
        Assert.DoesNotContain("<AudioCheckSavePlaybackTakes>", written);
        var reread = RadioConfig.LoadForRadio(serial);
        Assert.True(reread.AudioCheckSavePlaybackTakes);
        Assert.Equal(6, reread.AudioCheckLowPowerWatts);
    }

    [Fact]
    public void Turned_off_after_it_turned_on_it_stays_off_and_is_remembered_for_that_radio_only()
    {
        string mine = NewSerial(), other = NewSerial();
        WriteOlderBuildConfig(mine, oldValue: false);
        Assert.True(RadioConfig.LoadForRadio(mine).AudioCheckSavePlaybackTakes);

        Assert.True(RadioConfig.RecordAudioCheckPref(mine, AudioCheckPref.SavePlaybackTakes,
            AudioCheckListenMethods.RecordPlayback, AudioCheckPowerModes.DummyLoad, 10, false, shownSavePlaybackTakes: false));
        Assert.False(RadioConfig.LoadForRadio(mine).AudioCheckSavePlaybackTakes);
        Assert.Contains("<" + RadioConfig.SavePlaybackTakesElement + ">false</" + RadioConfig.SavePlaybackTakesElement + ">",
            File.ReadAllText(ConfigPath(mine)));
        // Reloaded again, still off: the turn-on happens once only.
        Assert.False(RadioConfig.LoadForRadio(mine).AudioCheckSavePlaybackTakes);
        Assert.True(RadioConfig.LoadForRadio(other).AudioCheckSavePlaybackTakes);
    }

    [Fact]
    public void A_neighbouring_check_setting_save_does_not_turn_it_back_on()
    {
        string serial = NewSerial();
        Assert.True(RadioConfig.RecordAudioCheckPref(serial, AudioCheckPref.SavePlaybackTakes,
            AudioCheckListenMethods.RecordPlayback, AudioCheckPowerModes.DummyLoad, 10, false, shownSavePlaybackTakes: false));
        // The window shows the box ticked; only the edited preference is saved.
        Assert.True(RadioConfig.RecordAudioCheckPref(serial, AudioCheckPref.CompareWithLastTake,
            AudioCheckListenMethods.Loopback, AudioCheckPowerModes.DummyLoad, 10, true, shownSavePlaybackTakes: true));
        Assert.True(RadioConfig.RecordAudioCheckPref(serial, AudioCheckPref.ListenMethod,
            AudioCheckListenMethods.RecordPlayback, AudioCheckPowerModes.DummyLoad, 10, true, shownSavePlaybackTakes: true));
        Assert.True(RadioConfig.RecordWarnBeforeSliceRecordingOverwritten(serial, false));
        var cfg = RadioConfig.LoadForRadio(serial);
        Assert.False(cfg.AudioCheckSavePlaybackTakes);
        Assert.True(cfg.AudioCheckCompareWithLastTake);
    }

    [Fact]
    public void Saving_it_leaves_every_other_check_setting_as_the_file_holds_it()
    {
        string serial = NewSerial();
        Assert.True(RadioConfig.RecordAudioCheckPref(serial, AudioCheckPref.LowPowerWatts,
            AudioCheckListenMethods.Monitor, AudioCheckPowerModes.LowPower, 7, false));
        // The window shows other values than the file; only the box is saved.
        Assert.True(RadioConfig.RecordAudioCheckPref(serial, AudioCheckPref.SavePlaybackTakes,
            AudioCheckListenMethods.Loopback, AudioCheckPowerModes.DummyLoad, 33, true, shownSavePlaybackTakes: true));
        var cfg = RadioConfig.LoadForRadio(serial);
        Assert.True(cfg.AudioCheckSavePlaybackTakes);
        Assert.Equal(7, cfg.AudioCheckLowPowerWatts);
        Assert.False(cfg.AudioCheckCompareWithLastTake);
        Assert.False(cfg.AudioCheckListenMethodChosen);
        // And a neighbouring change never turns it off.
        Assert.True(RadioConfig.RecordAudioCheckPref(serial, AudioCheckPref.LowPowerWatts,
            AudioCheckListenMethods.Monitor, AudioCheckPowerModes.LowPower, 8, false));
        Assert.True(RadioConfig.LoadForRadio(serial).AudioCheckSavePlaybackTakes);
    }

    [Fact]
    public void The_box_is_shown_for_its_methods_and_never_for_the_connection()
    {
        string source = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.SaveTakes.cs"));
        Assert.Contains("_sections.Add(CheckContent, box);", source); // not AddRadio: never disabled with the radio
        int layout = source.IndexOf("private void ApplySavePlaybackTakesLayout(AudioCheckListenMethods method)", StringComparison.Ordinal);
        string body = source.Substring(layout, source.IndexOf("private void LoadSavePlaybackTakesPref()", layout, StringComparison.Ordinal) - layout);
        Assert.Contains("method is AudioCheckListenMethods.RecordPlayback or AudioCheckListenMethods.Loopback", body);
        Assert.DoesNotContain("_rig", body);
        Assert.DoesNotContain("RemoteRig", body);
        Assert.Contains("\"audio.takes.save_playback\"", source);
    }

    [Fact]
    public void The_setting_is_what_reaches_every_part_of_the_save_path()
    {
        // Until the box sets it, nothing saves: the one gate every part reads.
        string source = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "JJFlexWpf", "Dialogs", "CheckYourAudioDialog.SaveTakes.cs"));
        Assert.Contains("_savePlaybackTakes && method is AudioCheckListenMethods.RecordPlayback or AudioCheckListenMethods.Loopback", source);
        Assert.Contains("_savePlaybackTakes = box.IsChecked == true;", source);
        Assert.Contains("_savePlaybackTakes = _radioCfg?.AudioCheckSavePlaybackTakes == true;", source);
    }
}
