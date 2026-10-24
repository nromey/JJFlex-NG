using System;
using System.Collections.Generic;
using System.Linq;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// What the recordings dialog says and decides, without its window (#787,
/// Tape Deck track 4): the rows, positions and trim marks read as the reader
/// will speak them, the step size, the trim refusals, the frames Save As is
/// handed, the row the dialog opens on, and Save As's name-and-type rules.
/// </summary>
/// <remarks>
/// Windowless, proved: an MTA thread, where WPF cannot construct an element,
/// plus the harness's silenced output and throwaway settings root. Nothing
/// here constructs a dialog; the dialog is a thin shell over these.
/// What these cannot see: focus, the order a screen reader reads in, and
/// whether a key reaches the window at all. Those are bench steps.
/// </remarks>
public sealed class RecordedQsoBrowserTests
{
    public RecordedQsoBrowserTests()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
    }

    private static readonly DateTime Now = new(2026, 10, 24, 18, 0, 0);

    private static RecordedQso Qso(DateTime at, double? seconds = 728, string? name = null, int rate = 24000) =>
        new(@"C:\rec\" + (name ?? at.ToString("'QSO-'yyyy-MM-dd-HHmmss")) + ".flac",
            name ?? at.ToString("'QSO-'yyyy-MM-dd-HHmmss"), at, name == null, seconds, 1_000_000, rate, 1,
            seconds is double s ? (long)(s * rate) : 0);

    [Fact]
    public void A_row_says_the_date_once_and_the_time_as_a_time()
    {
        Assert.Equal("Today at 3:30 PM, 12 minutes 8 seconds",
            RecordedQsoWords.Row(Qso(new DateTime(2026, 10, 24, 15, 30, 12)), Now));
        Assert.Equal("Yesterday at 8:15 PM, 45 seconds",
            RecordedQsoWords.Row(Qso(new DateTime(2026, 10, 23, 20, 15, 0), 45), Now));
        Assert.Equal("Thursday, October 22 at 9:05 AM, 12 minutes 8 seconds",
            RecordedQsoWords.Row(Qso(new DateTime(2026, 10, 22, 9, 5, 0)), Now));
        Assert.Equal("Friday, December 26, 2025 at 9:05 AM, 12 minutes 8 seconds",
            RecordedQsoWords.Row(Qso(new DateTime(2025, 12, 26, 9, 5, 0)), Now));
        foreach (var row in new[] { RecordedQsoWords.Row(Qso(new DateTime(2026, 10, 24, 15, 30, 12)), Now) })
        {
            Assert.DoesNotContain("153012", row);
            Assert.DoesNotContain("FLAC", row, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("QSO-", row);
        }
    }

    [Fact]
    public void A_renamed_file_leads_with_its_name_and_an_unfinished_one_says_so()
    {
        Assert.Equal("Contest pileup, recorded today at 3:30 PM, 12 minutes 8 seconds",
            RecordedQsoWords.Row(Qso(new DateTime(2026, 10, 24, 15, 30, 0), name: "Contest pileup"), Now));
        Assert.Equal("Today at 3:30 PM, length unknown",
            RecordedQsoWords.Row(Qso(new DateTime(2026, 10, 24, 15, 30, 0), seconds: null), Now));
        Assert.Equal("Today at 3:30 PM, still recording",
            RecordedQsoWords.Row(Qso(new DateTime(2026, 10, 24, 15, 30, 0), seconds: null), Now, stillRecording: true));
    }

    [Fact]
    public void Stepping_says_where_it_landed_in_a_whole_sentence_and_stops_at_each_end()
    {
        var b = new RecordedQsoBrowser();
        b.Select(Qso(Now.AddHours(-1), 70));
        Assert.Equal(5, b.StepSeconds);
        Assert.Equal("You are at the start of the recording.", b.Step(-1));
        Assert.Equal("You are at 5 seconds of 1 minute 10 seconds.", b.Step(1));
        b.StepSeconds = 60;
        Assert.Equal("You are at 1 minute 5 seconds of 1 minute 10 seconds.", b.Step(1));
        Assert.Equal("You are at the end of the recording.", b.Step(1));
        Assert.Equal(70, b.Cursor);
        Assert.Equal("You are at the start of the recording.", b.Edge(toEnd: false));
        Assert.Equal(new[] { 1, 5, 10, 30, 60 }, RecordedQsoBrowser.Steps);
        Assert.Equal(new[] { "1 second", "5 seconds", "10 seconds", "30 seconds", "1 minute" },
            RecordedQsoBrowser.Steps.Select(RecordedQsoWords.Step));
    }

    [Fact]
    public void A_recording_whose_length_is_unknown_cannot_be_stepped_or_trimmed()
    {
        var b = new RecordedQsoBrowser();
        b.Select(Qso(Now, seconds: null));
        Assert.False(b.CanMove);
        string refusal = b.Step(1);
        Assert.Equal("This recording does not say how long it is, so you cannot move through it or trim it.", refusal);
        Assert.False(b.SetStart(3).Changed);
        Assert.Equal(0, b.Cursor);
    }

    [Fact]
    public void Trim_marks_refuse_a_start_after_the_end_and_an_end_before_the_start_and_change_nothing()
    {
        var b = new RecordedQsoBrowser();
        b.Select(Qso(Now, 600));
        Assert.True(b.IsWhole);
        Assert.Equal("The whole recording is selected. It is 10 minutes long.", b.Selection());

        var start = b.SetStart(65);
        Assert.True(start.Changed);
        Assert.Equal("Trim start set to 1 minute 5 seconds. The selection runs from 1 minute 5 seconds to the end, 8 minutes 55 seconds.",
            start.Sentence);
        var end = b.SetEnd(180);
        Assert.True(end.Changed);
        Assert.Equal("Trim end set to 3 minutes. The selection runs from 1 minute 5 seconds to 3 minutes, 1 minute 55 seconds.",
            end.Sentence);

        var badEnd = b.SetEnd(30);
        Assert.False(badEnd.Changed);
        Assert.Equal("The end must be after the start, so the trim marks did not change.", badEnd.Sentence);
        var badStart = b.SetStart(200);
        Assert.False(badStart.Changed);
        Assert.Equal("The start must be before the end, so the trim marks did not change.", badStart.Sentence);
        Assert.Equal(65, b.TrimStart);
        Assert.Equal(180, b.TrimEnd);

        Assert.Equal((65L * 24000, (long?)(180L * 24000)), b.Frames());
        Assert.Equal("The whole recording is selected. It is 10 minutes long.", b.UseWhole());
        Assert.Equal((0L, (long?)null), b.Frames());
    }

    [Fact]
    public void Choosing_another_recording_starts_its_position_and_marks_again()
    {
        var b = new RecordedQsoBrowser();
        var first = Qso(Now.AddHours(-2), 600);
        b.Select(first);
        b.Step(1);
        b.SetStart(5);
        b.Select(Qso(Now.AddHours(-1), 300));
        Assert.Equal(0, b.Cursor);
        Assert.True(b.IsWhole);
        // The same recording again (a refresh) keeps where you were.
        b.Step(1);
        b.Select(b.Current);
        Assert.Equal(5, b.Cursor);
    }

    [Fact]
    public void The_dialog_opens_on_a_choice_that_still_exists_otherwise_on_the_newest_with_the_box_ticked()
    {
        var rows = new List<RecordedQso> { Qso(Now.AddHours(-1)), Qso(Now.AddHours(-2)), Qso(Now.AddHours(-3)) };
        Assert.Equal((1, false), RecordedQsoBrowser.Opening(rows, rows[1].Path.ToUpperInvariant()));
        Assert.Equal((0, true), RecordedQsoBrowser.Opening(rows, null));
        Assert.Equal((0, true), RecordedQsoBrowser.Opening(rows, @"C:\rec\gone.flac"));
        Assert.Equal((-1, true), RecordedQsoBrowser.Opening(new List<RecordedQso>(), null));
    }

    [Fact]
    public void Sizes_and_every_new_sentence_read_as_words_and_end_as_sentences()
    {
        Assert.Equal("800 kilobytes", RecordedQsoWords.Size(800 * 1024));
        Assert.Equal("3.5 megabytes", RecordedQsoWords.Size((long)(3.5 * 1024 * 1024)));
        Assert.Equal("34 megabytes", RecordedQsoWords.Size(34L * 1024 * 1024));
        Assert.Equal("1.2 gigabytes", RecordedQsoWords.Size((long)(1.2 * 1024 * 1024 * 1024)));

        // Noel cut a fragment ("Not while transmitting") on 2026-10-21. Every
        // sentence the window speaks ends as a sentence and starts with a capital.
        var spoken = new[]
        {
            "audio.qsos.empty", "audio.qsos.unreadable_one", "audio.qsos.at_start", "audio.qsos.at_end",
            "audio.qsos.pause_first", "audio.qsos.no_position", "audio.qsos.playing_unknown",
            "audio.qsos.nothing_selected", "audio.qsos.still_recording", "audio.qsos.play_refused_transmitting",
            "audio.qsos.vox_unconfirmed", "audio.qsos.choice_follows", "audio.qsos.trim_start_refused",
            "audio.qsos.trim_end_refused", "audio.qsos.deleted_last", "audio.qsos.refused_missing",
            "audio.qsos.refused_no_bin", "audio.qsos.refused_bin_off", "audio.qsos.refused_too_large",
            "audio.qsos.refused_failed", "audio.qsos.prune_preview_none", "audio.qsos.pruned_one",
            "audio.qsos.saveas_saving", "audio.qsos.saveas_no_name", "audio.qsos.saveas_bad_name",
            "audio.qsos.saveas_same_file", "audio.qsos.saveas_no_folder", "audio.qsos.saveas_mp3_unavailable",
            "audio.qsos.saveas_m4a_unavailable",
        };
        foreach (string key in spoken)
        {
            string text = Lexicon.Get(key);
            Assert.False(string.IsNullOrWhiteSpace(text), key);
            Assert.True(char.IsUpper(text[0]), key + ": " + text);
            Assert.EndsWith(".", text);
        }
        Assert.Contains("Media Feature Pack", Lexicon.Get("audio.qsos.saveas_mp3_unavailable"));
        Assert.DoesNotContain("Media Foundation", Lexicon.Get("audio.qsos.saveas_mp3_unavailable"));
    }

    [Fact]
    public void Each_Recycle_Bin_refusal_says_the_file_was_not_deleted()
    {
        foreach (var refusal in Enum.GetValues<RecycleRefusal>().Where(r => r is not (RecycleRefusal.None or RecycleRefusal.Missing)))
            Assert.Contains("not deleted", RecordedQsosDialog.RefusalSentence(refusal));
    }

    [Fact]
    public void The_prune_preview_names_the_count_the_space_the_dates_and_what_stays()
    {
        var a = Qso(new DateTime(2026, 10, 1, 9, 0, 0));
        var b = Qso(new DateTime(2026, 10, 3, 9, 0, 0));
        var held = Qso(new DateTime(2026, 9, 30, 9, 0, 0));
        string text = RecordedQsoPruneDialog.Describe(new RecordedQsoPrunePlan(new[] { a, b }, new[] { held }, 2 * 1024 * 1024), Now);
        Assert.Equal("This moves 2 recordings, using 2 megabytes, to the Recycle Bin. The oldest is from Thursday, October 1 at 9:00 AM "
            + "and the newest from Saturday, October 3 at 9:00 AM. Nothing has been moved yet. "
            + "Wednesday, September 30 at 9:00 AM, 12 minutes 8 seconds is in use, so it stays where it is.", text);
        Assert.Equal("No recordings match, so nothing will be moved.",
            RecordedQsoPruneDialog.Describe(new RecordedQsoPrunePlan(Array.Empty<RecordedQso>(), Array.Empty<RecordedQso>(), 0), Now));
    }

    [Fact]
    public void Save_As_keeps_the_name_and_the_type_in_step_both_ways()
    {
        var flac = RecordedQsoSaveAs.For(RecordedQsoExport.Format.Flac);
        var mp3 = RecordedQsoSaveAs.For(RecordedQsoExport.Format.Mp3);
        Assert.Equal("QSO-2026-10-24-153012.mp3", RecordedQsoSaveAs.WithExtension("QSO-2026-10-24-153012.flac", mp3));
        Assert.Equal("notes.txt.flac", RecordedQsoSaveAs.WithExtension("notes.txt", flac));
        Assert.Equal(RecordedQsoExport.Format.Opus, RecordedQsoSaveAs.KindOfName("pileup.OPUS")!.Format);
        Assert.Equal(RecordedQsoExport.Format.M4a, RecordedQsoSaveAs.KindOfName("pileup.m4a")!.Format);
        Assert.Null(RecordedQsoSaveAs.KindOfName("pileup.ogg"));
        Assert.Equal(64, mp3.DefaultBitrate);
        Assert.Equal(32, RecordedQsoSaveAs.For(RecordedQsoExport.Format.Opus).DefaultBitrate);
        Assert.False(RecordedQsoSaveAs.For(RecordedQsoExport.Format.M4a).HasChannels);
        Assert.Empty(flac.Bitrates);
    }

    [Fact]
    public void Save_As_suggests_a_new_file_in_the_recordings_own_folder_never_the_source()
    {
        string source = @"C:\rec\2026\10\QSO-2026-10-24-153012.flac";
        var flac = RecordedQsoSaveAs.For(RecordedQsoExport.Format.Flac);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source };
        Assert.Equal("QSO-2026-10-24-153012-2.flac", RecordedQsoSaveAs.SuggestName(source, true, flac, taken.Contains));
        Assert.Equal("QSO-2026-10-24-153012-trim.flac", RecordedQsoSaveAs.SuggestName(source, false, flac, taken.Contains));
        taken.Add(@"C:\rec\2026\10\QSO-2026-10-24-153012-trim.flac");
        Assert.Equal("QSO-2026-10-24-153012-trim-2.flac", RecordedQsoSaveAs.SuggestName(source, false, flac, taken.Contains));
        Assert.Equal("QSO-2026-10-24-153012.mp3",
            RecordedQsoSaveAs.SuggestName(source, true, RecordedQsoSaveAs.For(RecordedQsoExport.Format.Mp3), taken.Contains));
    }
}
