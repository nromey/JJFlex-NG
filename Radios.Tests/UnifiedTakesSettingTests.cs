#nullable enable
using System;
using System.IO;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes, round two, part 4e: "Save each playback as a take" (DRAFT)
/// is a per-radio check setting, off by default, saved alone, and its box is
/// shown for the method, never hidden for the connection.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class UnifiedTakesSettingTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(UnifiedTakesSettingTests));
    public void Dispose() => _scope.Dispose();

    private static string NewSerial() =>
        "8640-" + Random.Shared.Next(1000, 9999) + "-" + Random.Shared.Next(1000, 9999) + "-" + Random.Shared.Next(1000, 9999);

    [Fact]
    public void It_is_off_until_turned_on_and_is_remembered_for_that_radio_only()
    {
        string mine = NewSerial(), other = NewSerial();
        Assert.False(RadioConfig.LoadForRadio(mine).AudioCheckSavePlaybackTakes);
        Assert.True(RadioConfig.RecordAudioCheckPref(mine, AudioCheckPref.SavePlaybackTakes,
            AudioCheckListenMethods.RecordPlayback, AudioCheckPowerModes.DummyLoad, 10, false, shownSavePlaybackTakes: true));
        Assert.True(RadioConfig.LoadForRadio(mine).AudioCheckSavePlaybackTakes);
        Assert.False(RadioConfig.LoadForRadio(other).AudioCheckSavePlaybackTakes);
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
