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
        Assert.Equal("Today at 3:30 PM, original QSO recording, 12 minutes 8 seconds",
            RecordedQsoWords.Row(Qso(new DateTime(2026, 10, 24, 15, 30, 12)), Now));
        Assert.Equal("Yesterday at 8:15 PM, original QSO recording, 45 seconds",
            RecordedQsoWords.Row(Qso(new DateTime(2026, 10, 23, 20, 15, 0), 45), Now));
        Assert.Equal("Thursday, October 22 at 9:05 AM, original QSO recording, 12 minutes 8 seconds",
            RecordedQsoWords.Row(Qso(new DateTime(2026, 10, 22, 9, 5, 0)), Now));
        Assert.Equal("Friday, December 26, 2025 at 9:05 AM, original QSO recording, 12 minutes 8 seconds",
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
        Assert.Equal("Today at 3:30 PM, original QSO recording, length unknown",
            RecordedQsoWords.Row(Qso(new DateTime(2026, 10, 24, 15, 30, 0), seconds: null), Now));
        Assert.Equal("Today at 3:30 PM, original QSO recording, still recording",
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
        Assert.Equal(new[] { 0.1, 0.5, 1, 5, 10, 30, 60 }, RecordedQsoBrowser.Steps);
        Assert.Equal(new[] { "0.1 seconds", "0.5 seconds", "1 second", "5 seconds", "10 seconds", "30 seconds", "1 minute" },
            RecordedQsoBrowser.Steps.Select(RecordedQsoWords.Step));
    }

    [Fact]
    public void Positions_marks_and_selections_read_to_the_tenth_and_a_tenth_step_lands_on_a_tenth()
    {
        // Noel, 2026-10-25: "I'd speak more than just a second resolution",
        // and a 0.1 and 0.5 second step. The bench read "from 1 second to 2
        // seconds, under a second" for marks a second apart.
        Assert.Equal("0.8 seconds", RecordedQsoWords.Precise(0.8));
        Assert.Equal("1 second", RecordedQsoWords.Precise(1.04));
        Assert.Equal("1.3 seconds", RecordedQsoWords.Precise(1.25));
        Assert.Equal("1 minute", RecordedQsoWords.Precise(59.97));
        Assert.Equal("2 minutes 5.3 seconds", RecordedQsoWords.Precise(125.3));
        Assert.Equal("the start", RecordedQsoWords.Position(0.04, 5));
        Assert.Equal("the end", RecordedQsoWords.Position(4.96, 5));
        Assert.Equal("4.9 seconds", RecordedQsoWords.Position(4.9, 5));

        var b = new RecordedQsoBrowser();
        b.Select(Qso(Now, 5));
        b.StepSeconds = 0.1;
        for (int i = 0; i < 13; i++) b.Step(1);
        Assert.Equal("You are at 1.4 seconds of 5 seconds.", b.Step(1));
        b.SetStart(1.4);
        var end = b.SetEnd(2.3);
        Assert.Equal("Trim end set to 2.3 seconds. The selection runs from 1.4 seconds to 2.3 seconds, 0.9 seconds.", end.Sentence);
        // A row's own length stays in whole seconds.
        Assert.Equal("Today at 5:00 PM, original QSO recording, 5 seconds", RecordedQsoWords.Row(Qso(Now.AddHours(-1), 5.04), Now));
    }

    [Fact]
    public void Shift_brackets_clear_one_mark_and_Backspace_clears_both()
    {
        var b = new RecordedQsoBrowser();
        b.Select(Qso(Now, 600));
        Assert.False(b.ClearStart().Changed);
        Assert.Equal("There is no trim start mark to clear.", b.ClearStart().Sentence);
        Assert.Equal("There is no trim end mark to clear.", b.ClearEnd().Sentence);
        b.SetStart(65);
        b.SetEnd(180);
        var start = b.ClearStart();
        Assert.True(start.Changed);
        Assert.Equal("Trim start mark cleared. The selection runs from the start to 3 minutes, 3 minutes.", start.Sentence);
        var endCleared = b.ClearEnd();
        Assert.Equal("Trim end mark cleared. The whole recording is selected. It is 10 minutes long.", endCleared.Sentence);
        b.SetStart(65);
        Assert.Equal("Trim marks cleared.", b.ClearSelection());
        Assert.True(b.IsWhole);
    }

    [Fact]
    public void The_step_size_is_remembered_only_as_one_the_combo_offers()
    {
        Assert.Equal(0.5, RecordedQsoBrowser.ValidStep(0.5));
        Assert.Equal(60, RecordedQsoBrowser.ValidStep(60));
        Assert.Equal(RecordedQsoBrowser.DefaultStep, RecordedQsoBrowser.ValidStep(7));
        Assert.Equal(RecordedQsoBrowser.DefaultStep, RecordedQsoBrowser.ValidStep(double.NaN));
    }

    [Fact]
    public void A_file_Save_As_wrote_says_its_type_so_two_trims_can_be_told_apart()
    {
        var opus = Qso(new DateTime(2026, 10, 24, 15, 30, 12), 0.6, name: "QSO-2026-10-24-153012-trim") with
            { Path = @"C:\rec\QSO-2026-10-24-153012-trim.opus" };
        // Retest 2026-10-25: when it was saved, what as, and how long; the
        // name Save As made up is left off, as a recording's own is.
        Assert.Equal("Today at 3:30 PM, saved as Opus, under a second", RecordedQsoWords.Row(opus, Now));
        var mp3Whole = Qso(new DateTime(2026, 10, 24, 15, 30, 12), 728) with { Path = @"C:\rec\QSO-2026-10-24-153012.mp3" };
        Assert.Equal("Today at 3:30 PM, saved as MP3, 12 minutes 8 seconds", RecordedQsoWords.Row(mp3Whole, Now));
        var flacTrim = Qso(new DateTime(2026, 10, 24, 15, 30, 12), 0.6, name: "QSO-2026-10-24-153012-trim-2");
        Assert.Equal("Today at 3:30 PM, saved as FLAC, under a second", RecordedQsoWords.Row(flacTrim, Now));
        var named = Qso(new DateTime(2026, 10, 24, 15, 30, 12), 0.6, name: "Contest pileup") with
            { Path = @"C:\rec\Contest pileup.opus" };
        Assert.Equal("Contest pileup, saved as Opus today at 3:30 PM, under a second", RecordedQsoWords.Row(named, Now));
        Assert.True(RecordedQsoCatalog.IsSaveAsName("QSO-2026-10-24-153012-trim"));
        Assert.True(RecordedQsoCatalog.IsSaveAsName("QSO-2026-10-24-153012-trim-12"));
        Assert.False(RecordedQsoCatalog.IsSaveAsName("QSO-2026-10-24-153012"));
        Assert.False(RecordedQsoCatalog.IsSaveAsName("Contest pileup-trim"));
        Assert.False(RecordedQsoCatalog.IsSaveAsName("QSO-2026-10-24-153012-trimmed"));
        Assert.True(RecordedQsoCatalog.IsSaveAsName("QSO-2026-10-24-153012-copy"));
        Assert.True(RecordedQsoCatalog.IsSaveAsName("QSO-2026-10-24-153012-copy-2"));
        Assert.True(RecordedQsoCatalog.IsSaveAsName("QSO-2026-10-24-153012-copy2"));
        Assert.True(RecordedQsoCatalog.IsSaveAsName("QSO-2026-10-24-153012-trim-copy"));
        Assert.False(RecordedQsoCatalog.IsSaveAsName("QSO-2026-10-24-153012-2"));
        Assert.False(opus.IsFlac);
        Assert.True(Qso(Now).IsFlac);
    }

    [Fact]
    public void Saved_names_the_month_folder_when_it_is_the_recordings_own_and_the_path_otherwise()
    {
        string source = @"C:\Users\op\AppData\Roaming\JJFlexRadio\Recorded QSOs\2026\10\QSO-2026-10-25-102646.flac";
        Assert.Equal("Saved QSO-2026-10-25-102646-trim.opus in the October 2026 folder. It is 0.6 seconds long.",
            RecordedQsosDialog.SavedSentence("QSO-2026-10-25-102646-trim.opus",
                @"C:\Users\op\AppData\Roaming\JJFlexRadio\Recorded QSOs\2026\10", source, 0.6));
        Assert.Equal(@"Saved x.mp3 in D:\Clips. It is 0.6 seconds long.",
            RecordedQsosDialog.SavedSentence("x.mp3", @"D:\Clips", source, 0.6));
        Assert.Null(RecordedQsosDialog.MonthFolder(@"C:\Other\2026\10", source));
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
        Assert.Equal("Trim marks cleared.", b.ClearSelection());
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
    public void The_dialog_opens_on_a_choice_that_still_exists_otherwise_on_the_newest()
    {
        var rows = new List<RecordedQso> { Qso(Now.AddHours(-1)), Qso(Now.AddHours(-2)), Qso(Now.AddHours(-3)) };
        Assert.Equal(1, RecordedQsoBrowser.Opening(rows, rows[1].Path.ToUpperInvariant()));
        Assert.Equal(0, RecordedQsoBrowser.Opening(rows, null));
        Assert.Equal(0, RecordedQsoBrowser.Opening(rows, @"C:\rec\gone.flac"));
        Assert.Equal(-1, RecordedQsoBrowser.Opening(new List<RecordedQso>(), null));
    }

    [Fact]
    public void A_recording_still_being_made_has_a_sentence_for_its_selection_not_length_unknown_long()
    {
        // The bench heard "It is length unknown long." on the still-recording row.
        var b = new RecordedQsoBrowser();
        b.Select(Qso(Now, seconds: null));
        Assert.Equal("The whole recording is selected. Its length is not known yet.", b.Selection());
        Assert.DoesNotContain("unknown long", Lexicon.Get("audio.qsos.recording_selection"));
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
            "audio.qsos.no_position", "audio.qsos.playing_unknown",
            "audio.qsos.nothing_selected", "audio.qsos.still_recording", "audio.qsos.play_refused_transmitting",
            "audio.qsos.vox_unconfirmed", "audio.qsos.trim_start_refused",
            "audio.qsos.trim_end_refused", "audio.qsos.deleted_last", "audio.qsos.refused_missing",
            "audio.qsos.refused_no_bin", "audio.qsos.refused_bin_off", "audio.qsos.refused_too_large",
            "audio.qsos.refused_failed", "audio.qsos.prune_preview_none", "audio.qsos.pruned_one",
            "audio.qsos.saveas_saving", "audio.qsos.saveas_no_name", "audio.qsos.saveas_bad_name",
            "audio.qsos.saveas_same_file", "audio.qsos.saveas_no_folder", "audio.qsos.saveas_mp3_unavailable",
            "audio.qsos.saveas_m4a_unavailable", "audio.qsos.being_saved",
            "audio.qsos.stopped_to_start", "audio.qsos.play_head_start", "audio.qsos.whole_length_unknown",
            "audio.qsos.recording_selection", "audio.qsos.no_start_mark", "audio.qsos.no_end_mark",
            "audio.qsos.flac_only", "audio.qsos.list_help", "audio.qsos.step_help", "audio.qsos.saveas_type_help",
            "audio.qsos.play_help", "audio.qsos.stop_help", "audio.qsos.position_help", "audio.qsos.set_start_help",
            "audio.qsos.set_end_help", "audio.qsos.selection_help", "audio.qsos.clear_selection_help",
            "audio.qsos.save_as_help", "audio.qsos.delete_help", "audio.qsos.prune_help", "audio.qsos.close_help",
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
            + "Wednesday, September 30 at 9:00 AM, original QSO recording, 12 minutes 8 seconds is in use, so it stays where it is.", text);
        Assert.Equal("No recordings match, so nothing will be moved.",
            RecordedQsoPruneDialog.Describe(new RecordedQsoPrunePlan(Array.Empty<RecordedQso>(), Array.Empty<RecordedQso>(), 0), Now));
    }

    [Theory]
    [InlineData(100, 20)]
    [InlineData(30, 15)]
    [InlineData(5, 2)]
    [InlineData(1, 1)]
    public void Prune_never_opens_set_to_move_every_recording_on_a_short_list(int total, int expected)
    {
        Assert.Equal(expected, RecordedQsoPruneDialog.OpeningCount(total));
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
        Assert.Equal("QSO-2026-10-24-153012-copy.flac", RecordedQsoSaveAs.SuggestName(source, true, flac, taken.Contains));
        Assert.Equal("QSO-2026-10-24-153012-trim.flac", RecordedQsoSaveAs.SuggestName(source, false, flac, taken.Contains));
        taken.Add(@"C:\rec\2026\10\QSO-2026-10-24-153012-trim.flac");
        Assert.Equal("QSO-2026-10-24-153012-trim-copy.flac", RecordedQsoSaveAs.SuggestName(source, false, flac, taken.Contains));
        Assert.Equal("QSO-2026-10-24-153012.mp3",
            RecordedQsoSaveAs.SuggestName(source, true, RecordedQsoSaveAs.For(RecordedQsoExport.Format.Mp3), taken.Contains));
    }

    // Bench 2026-10-25: the window's name was said twice, once by NVDA and
    // once by the base dialog. This window opts out of the base copy; every
    // other dialog keeps it until it is checked the same way. No window is
    // built: the property reads no state, so an uninitialized object answers.
    [Fact]
    public void Recorded_QSOs_leaves_its_title_to_the_screen_reader_and_other_dialogs_still_speak_theirs()
    {
        var qsos = (JJFlexDialog)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(RecordedQsosDialog));
        var workshop = (JJFlexDialog)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(AudioWorkshopDialog));

        Assert.False(qsos.SpeaksOwnTitle);
        Assert.True(workshop.SpeaksOwnTitle);
    }

    [Fact]
    public void Recorded_QSOs_has_a_description_of_its_own_so_NVDA_does_not_read_the_text_lines()
    {
        Assert.Equal("Radio audio captured by the computer", Lexicon.Get("audio.qsos.description"));
    }

    // Retest 2026-10-25: Save As to MP3 offered "-2.mp3", because the first
    // suggestion was made for FLAC, which clashes with the source, and a type
    // change only swapped the extension. While the name is still the one
    // suggested, a type change suggests again; an edited name keeps its words.
    [Fact]
    public void Changing_the_Save_As_type_suggests_again_until_the_name_is_edited()
    {
        string source = @"C:\rec\2026\10\QSO-2026-10-25-172707.flac";
        var flac = RecordedQsoSaveAs.For(RecordedQsoExport.Format.Flac);
        var mp3 = RecordedQsoSaveAs.For(RecordedQsoExport.Format.Mp3);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source };
        string first = RecordedQsoSaveAs.SuggestName(source, true, flac, taken.Contains);
        Assert.Equal("QSO-2026-10-25-172707-copy.flac", first);

        Assert.Equal("QSO-2026-10-25-172707.mp3",
            RecordedQsoSaveAs.NameForType(first, first, source, true, mp3, taken.Contains));
        Assert.Equal("pileup.mp3",
            RecordedQsoSaveAs.NameForType("pileup.flac", first, source, true, mp3, taken.Contains));
        Assert.Equal("QSO-2026-10-25-172707-copy.mp3",
            RecordedQsoSaveAs.NameForType(first, null, source, true, mp3, taken.Contains));
        // A second whole FLAC copy: "-copy" is taken, so "-copy2", never "-2".
        taken.Add(@"C:\rec\2026\10\QSO-2026-10-25-172707-copy.flac");
        Assert.Equal("QSO-2026-10-25-172707-copy2.flac", RecordedQsoSaveAs.SuggestName(source, true, flac, taken.Contains));
        // A free name stays plain: an MP3 of a FLAC recording clashes with nothing.
        Assert.Equal("QSO-2026-10-25-172707.mp3", RecordedQsoSaveAs.SuggestName(source, true, mp3, taken.Contains));
        // A clashing MP3 is a copy too.
        taken.Add(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "QSO-2026-10-25-172707.mp3"));
        Assert.Equal("QSO-2026-10-25-172707-copy.mp3", RecordedQsoSaveAs.SuggestName(source, true, mp3, taken.Contains));
    }

    // #821 at the bench (2026-10-25): the connect played "two opens and a
    // close before it even connected". The radio list and the searching
    // window opt out; every other dialog keeps its tones.
    [Fact]
    public void The_connect_windows_play_no_dialog_tones_and_other_dialogs_still_do()
    {
        JJFlexDialog Bare(System.Type t) =>
            (JJFlexDialog)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
        Assert.False(Bare(typeof(RigSelectorDialog)).PlaysOpenCloseTones);
        Assert.False(Bare(typeof(DiscoveringRadiosWindow)).PlaysOpenCloseTones);
        Assert.True(Bare(typeof(RecordedQsosDialog)).PlaysOpenCloseTones);
        Assert.True(Bare(typeof(AudioWorkshopDialog)).PlaysOpenCloseTones);
    }
}
