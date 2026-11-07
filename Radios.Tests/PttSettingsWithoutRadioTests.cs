using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The 8600 bench of 2026-11-06, Fail 3 (#496): with no radio connected,
/// Settings edits a throwaway PttConfig, saves nothing, and still said
/// "Settings saved." The PTT tab's settings live per operator beside a
/// radio's configuration, so with no radio they have no home: the commit must
/// say so plainly rather than claim a save.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class PttSettingsWithoutRadioTests
{
    public PttSettingsWithoutRadioTests() => Lexicon.Load(Lexicon.Partitions);

    private static readonly MethodInfo? NotKept =
        typeof(PttConfig).GetMethod("ChangesNotKept", BindingFlags.Public | BindingFlags.Static);

    private static bool Dropped(string atOpen, PttConfig now, bool hasHome)
    {
        Assert.True(NotKept != null, "nothing tells a PTT change made with no radio from one that was saved");
        return (bool)NotKept!.Invoke(null, new object[] { atOpen, now, hasHome })!;
    }

    private static string Snapshot(PttConfig c) =>
        (string)typeof(PttConfig).GetMethod("Snapshot")!.Invoke(c, null)!;

    [Fact]
    public void A_change_with_no_radio_is_reported_as_not_kept()
    {
        var ptt = new PttConfig();
        string atOpen = Snapshot(ptt);
        ptt.AnnounceTransmitSlice = !ptt.AnnounceTransmitSlice;   // the bench's own switch (#496)
        Assert.True(Dropped(atOpen, ptt, hasHome: false));
    }

    [Fact]
    public void No_change_or_a_radio_connected_is_not()
    {
        var ptt = new PttConfig();
        string atOpen = Snapshot(ptt);
        Assert.False(Dropped(atOpen, ptt, hasHome: false));        // untouched: nothing was lost
        ptt.AnnounceTransmitSlice = !ptt.AnnounceTransmitSlice;
        Assert.False(Dropped(atOpen, ptt, hasHome: true));         // connected: it is saved
    }

    [Fact]
    public void The_dialog_never_says_saved_over_a_dropped_change()
    {
        string root = CaptureMeterSetTests.RepoRoot();
        string dialog = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));
        string menu = File.ReadAllText(Path.Combine(root, "JJFlexWpf", "NativeMenuBar.cs"));
        Assert.Contains("PttConfig.ChangesNotKept(_pttAtOpen, _pttConfig, PttSettingsHaveAHome)", dialog);
        const string key = "settings.commit.ptt_not_saved";
        Assert.Contains("pttDropped ? \"" + key + "\"", dialog);
        Assert.Contains("PttSettingsHaveAHome = _window.CurrentPttConfig != null", menu);
        string sentence = Lexicon.Get(key);
        Assert.DoesNotContain("settings.commit", sentence);         // the key resolves
        Assert.Contains("not saved", sentence);
    }
}
