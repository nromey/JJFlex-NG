#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Unified takes, round two, part 4f: Play takes' "Add a take to this test",
/// the earlier-session notes for two chosen takes, and the wording for two
/// takes the operator chose (all DRAFT).
/// </summary>
public sealed class UnifiedTakesChosenTests : IDisposable
{
    private readonly string _root = UnifiedTakeFixtures.NewRoot();
    public void Dispose() => UnifiedTakeFixtures.Remove(_root);

    [Fact]
    public void Two_chosen_takes_are_named_as_this_take_and_the_one_you_chose()
    {
        string chosen = UnifiedTakeFixtures.AudioTake(_root, seed: 3);
        string current = UnifiedTakeFixtures.AudioTake(_root, seed: 4, highGain: 2.5);
        var difference = TakeAnalysis.CompareTakes(chosen, current);
        string said = ListenbackResultText.AudioRoadSentence(difference, chosen: true);
        Assert.Contains("This take sounds brighter than the one you chose", said);
        Assert.DoesNotContain("your last take", said);
        Assert.DoesNotContain("Your new take", said);
        // The automatic comparison keeps its own words (positive control).
        Assert.Contains("Your new take sounds brighter than your last take", ListenbackResultText.AudioRoadSentence(difference));
    }

    [Fact]
    public void Same_tone_between_chosen_takes_is_said_the_same_way()
    {
        string chosen = UnifiedTakeFixtures.AudioTake(_root, seed: 3);
        string current = UnifiedTakeFixtures.AudioTake(_root, seed: 5);
        var difference = TakeAnalysis.CompareTakes(chosen, current);
        Assert.EndsWith("This take has the same tone as the one you chose.", ListenbackResultText.AudioRoadSentence(difference, chosen: true));
    }

    [Fact]
    public void Each_take_from_an_earlier_session_is_named_for_which_take_it_is()
    {
        string old = UnifiedTakeFixtures.AudioTake(_root, name: "20201010-201500-000-abcdef12");
        string fresh = UnifiedTakeFixtures.AudioTake(_root);
        var start = DateTime.UtcNow.AddMinutes(-5);
        var now = new DateTime(2020, 10, 11, 9, 0, 0, DateTimeKind.Local);
        string notes = TakeSession.EarlierSessionNotes(old, fresh, start, now);
        Assert.StartsWith("The take you chose is from an earlier session, yesterday at ", notes);
        Assert.DoesNotContain("This take is from", notes);
        Assert.StartsWith("This take is from an earlier session, yesterday at ", TakeSession.EarlierSessionNotes(fresh, old, start, now));
        Assert.Equal("", TakeSession.EarlierSessionNotes(fresh, fresh, start, now));
    }

    [Fact]
    public void An_explicit_resume_outlasts_the_next_connection_until_a_take_joins()
    {
        var series = new TakeSeriesContext();
        series.Connected("radio-a");
        var test = TakeSession.CreateTest(_root);
        series.Resume(test.Id);
        series.Connected("radio-b"); // a different radio after the resume: the choice stands
        Assert.Equal(test.Id, series.ActiveTestId);
        string folder = AudioTakeWriter.NewDirectory(_root);
        Directory.CreateDirectory(folder);
        var (joined, number) = series.Reserve(_root, folder);
        Assert.Equal(test.Id, joined.Id);
        Assert.Equal(1, number);
        series.Connected("radio-c"); // spent: an ordinary change of radio starts afresh again
        Assert.Null(series.ActiveTestId);
    }

    [Fact]
    public void Add_a_take_to_this_test_resumes_and_opens_the_check_and_never_starts_or_keys()
    {
        string source = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "JJFlexWpf", "Dialogs", "PlayTakesDialog.cs"));
        int at = source.IndexOf("private void AddToTest()", StringComparison.Ordinal);
        string body = source.Substring(at, source.IndexOf("private async Task SaveCopyAsync", at, StringComparison.Ordinal) - at);
        Assert.Contains("TakeSession.Series.Resume(row.TestId);", body);
        Assert.Contains("CheckYourAudioDialog.ShowOrFocus(rig);", body);
        foreach (string never in new[] { "ToggleAudioCheck", "ShowOrFocusAndStartCheck", "Transmit", "KeyForCheck", "Connect" })
            Assert.DoesNotContain(never, body);
        Assert.Contains("Shown(_addToTest, member);", source);
    }
}
