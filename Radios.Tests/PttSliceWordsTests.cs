using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// "Transmitting" and "Receiving" name their slice when the operator has asked
/// for it (#496, ruled 2026-09-01: "'transmitting on slice a' 'receiving on
/// slice b' ... bingo done"). All three strings, every time when on.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class PttSliceWordsTests
{
    public PttSliceWordsTests() => Lexicon.Load(Lexicon.Partitions);

    private static readonly SliceRoles Apart =
        new SliceRoles("B", 7_150_000UL, hasTransmit: true, "A", 14_100_000UL, same: false);
    private static readonly SliceRoles Together =
        new SliceRoles("A", 14_250_000UL, hasTransmit: true, "A", 14_250_000UL, same: true);

    [Fact]
    public void On_each_announcement_names_its_slice_even_when_the_slices_agree()
    {
        Assert.Equal("Transmitting on slice A", PttSliceWords.Transmitting(Apart, nameSlice: true));
        Assert.Equal("Transmitting on slice A, locked", PttSliceWords.Locked(Apart, nameSlice: true));
        Assert.Equal("Receiving on slice B", PttSliceWords.Receiving(Apart, nameSlice: true));

        // Never only when they differ: silence must not be ambiguous.
        Assert.Equal("Transmitting on slice A", PttSliceWords.Transmitting(Together, nameSlice: true));
        Assert.Equal("Receiving on slice A", PttSliceWords.Receiving(Together, nameSlice: true));
    }

    [Fact]
    public void Off_the_shipped_words_are_unchanged()
    {
        Assert.Equal(Lexicon.Get("audio.ptt.announce_transmitting"), PttSliceWords.Transmitting(Apart, nameSlice: false));
        Assert.Equal(Lexicon.Get("audio.ptt.announce_locked"), PttSliceWords.Locked(Apart, nameSlice: false));
        Assert.Equal(Lexicon.Get("audio.ptt.announce_receiving"), PttSliceWords.Receiving(Apart, nameSlice: false));
        Assert.Equal("Transmitting", Lexicon.Get("audio.ptt.announce_transmitting"));
    }

    [Fact]
    public void No_slice_to_name_says_the_shipped_words_rather_than_an_empty_letter()
    {
        var none = default(SliceRoles);
        Assert.Equal(Lexicon.Get("audio.ptt.announce_transmitting"), PttSliceWords.Transmitting(none, nameSlice: true));
        Assert.Equal(Lexicon.Get("audio.ptt.announce_receiving"), PttSliceWords.Receiving(none, nameSlice: true));
        Assert.Equal(Lexicon.Get("audio.ptt.announce_locked"), PttSliceWords.Locked(none, nameSlice: true));
    }

    [Fact]
    public void The_setting_is_on_by_default()
    {
        Assert.True(new PttConfig().AnnounceTransmitSlice);
    }

    [Fact]
    public void The_controller_says_no_announcement_without_asking_the_setting()
    {
        // Every speak site of the three keys goes through PttSliceWords with
        // the operator's switch; a bare Lexicon lookup of one of them would be
        // the one announcement that silently omits the slice.
        string text = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "JJFlexWpf", "PttSafetyController.cs"));
        foreach (string key in new[] { "audio.ptt.announce_transmitting", "audio.ptt.announce_receiving", "audio.ptt.announce_locked" })
            Assert.DoesNotContain("\"" + key + "\"", text);
        Assert.Contains("Radios.PttSliceWords.Transmitting(CurrentSliceRoles(), _config.AnnounceTransmitSlice)", text);
        Assert.Contains("Radios.PttSliceWords.Receiving(CurrentSliceRoles(), _config.AnnounceTransmitSlice)", text);
        Assert.Contains("Radios.PttSliceWords.Locked(CurrentSliceRoles(), _config.AnnounceTransmitSlice)", text);
        Assert.Equal(1, Regex.Matches(text, @"SpeakKeyDownFirstLine\(TransmittingWords\(\)\)").Count);
        Assert.Equal(3, Regex.Matches(text, @"GoIdle\(ReceivingWords\(\)\)").Count);
        Assert.Equal(2, Regex.Matches(text, @"LockedWords\(\)").Count - 1);   // two speak sites, plus the definition
    }
}
